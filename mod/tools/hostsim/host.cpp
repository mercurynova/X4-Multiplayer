#include "host.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <thread>

#include <nlohmann/json.hpp>
#include <x4_game_offsets.h>

#include "game/game_api.h"  // PosRotPod: the mod's SDK-free copy of UIPosRot must stay layout-identical

static_assert(sizeof(x4mp::game::PosRotPod) == sizeof(UIPosRot), "PosRotPod must match the SDK UIPosRot");
static_assert(offsetof(x4mp::game::PosRotPod, yaw) == offsetof(UIPosRot, yaw), "PosRotPod layout");
static_assert(offsetof(x4mp::game::PosRotPod, roll) == offsetof(UIPosRot, roll), "PosRotPod layout");

namespace hostsim {
namespace {
Host* g_host = nullptr;
std::thread::id g_main_thread;
}  // namespace

Host* current_host() { return g_host; }

// Static trampolines that reach the Host through g_host. A friend struct so they can touch private members.
struct Thunks {
  static int subscribe(const char* n, X4NativeEventCallback cb, void* ud, void*) {
    Host& h = *g_host;
    std::lock_guard lk(h.mu_);
    const int id = h.next_id_++;
    h.subs_.push_back({id, n ? n : "", cb, ud});
    return id;
  }
  static void unsubscribe(int id) {
    Host& h = *g_host;
    std::lock_guard lk(h.mu_);
    std::erase_if(h.subs_, [id](const Host::Sub& s) { return s.id == id; });
    h.hooks_.erase(id);
  }
  static void raise_event(const char* n, void* d) { g_host->fire(n ? n : "", d); }
  static int raise_lua_event(const char* n, const char* p) {
    Host& h = *g_host;
    if (std::this_thread::get_id() != g_main_thread) {
      h.note(std::string("VIOLATION raise_lua_event(") + (n ? n : "") + ") called off the main thread (X4Native requires the UI thread)");
      h.violation_count_++;
    }
    {
      std::lock_guard lk(h.mu_);
      h.lua_.push_back({n ? n : "", p ? p : ""});
      h.lua_history_.push_back({n ? n : "", p ? p : ""});
    }
    if (h.world.md_emulate) h.emulate_md(n ? n : "", p ? p : "");
    h.cv_.notify_all();
    return 0;
  }
  static int register_lua_bridge(const char* l, const char* c) {
    std::lock_guard lk(g_host->mu_);
    g_host->bridges_[l ? l : ""] = c ? c : (l ? l : "");
    return 0;
  }
  static void log(int level, const char* m) {
    static const char* const names[] = {"DEBUG", "INFO", "WARN", "ERROR"};
    Host& h = *g_host;
    const std::string line = std::string("[mod ") + names[std::clamp(level, 0, 3)] + "] " + (m ? m : "");
    {
      std::lock_guard lk(h.mu_);
      h.logs_.emplace_back(m ? m : "");
      std::printf("%s\n", line.c_str());
      std::fflush(stdout);
      if (h.log_file_) {
        std::fprintf(h.log_file_, "%s\n", line.c_str());
        std::fflush(h.log_file_);
      }
    }
    h.cv_.notify_all();
  }
  // The per-extension log function x4n::log::info() uses: fn(level, msg, api).
  static void ext_log(int level, const char* m, void*) { log(level, m); }
  static const char* game_version() { return g_host->game_version.c_str(); }
  static const char* x4native_version() { return g_host->x4native_version.c_str(); }
  static void* get_game_function(const char* name) {
    Host& h = *g_host;
    const auto it = h.game_fns_.find(name ? name : "");
    return it == h.game_fns_.end() ? nullptr : it->second;
  }
  static int hook_add(const char* fn, X4HookCallback, void*, void*) {
    Host& h = *g_host;
    std::lock_guard lk(h.mu_);
    const int id = h.next_id_++;
    h.hooks_[id] = fn ? fn : "";
    return id;
  }
  static void unhook(int id) {
    std::lock_guard lk(g_host->mu_);
    g_host->hooks_.erase(id);
  }
  static void* ensure_detour(const char*, void* d) { return d; }
  static void run_hooks(X4HookContext*) {}
  static void* resolve_internal(const char*) { return nullptr; }
  static int md_subscribe(std::uint32_t type_id, X4NativeEventCallback cb, void* ud, void*, const char* phase) {
    Host& h = *g_host;
    std::lock_guard lk(h.mu_);
    const int id = h.next_id_++;
    h.subs_.push_back({id, "md:" + std::to_string(type_id) + ":" + phase, cb, ud});
    return id;
  }
  static int md_before(std::uint32_t t, X4NativeEventCallback cb, void* ud, void* a) { return md_subscribe(t, cb, ud, a, "before"); }
  static int md_after(std::uint32_t t, X4NativeEventCallback cb, void* ud, void* a) { return md_subscribe(t, cb, ud, a, "after"); }

  static std::string key(const char* ns, const char* k) { return std::string(ns ? ns : "") + "/" + (k ? k : ""); }
  static int stash_set(const char* ns, const char* k, const void* d, std::uint32_t n) {
    std::lock_guard lk(g_host->mu_);
    g_host->stash_[key(ns, k)] = std::string(static_cast<const char*>(d), n);
    return 1;
  }
  static const void* stash_get(const char* ns, const char* k, std::uint32_t* n) {
    std::lock_guard lk(g_host->mu_);
    const auto it = g_host->stash_.find(key(ns, k));
    if (it == g_host->stash_.end()) return nullptr;
    if (n) *n = static_cast<std::uint32_t>(it->second.size());
    return it->second.data();
  }
  static int stash_remove(const char* ns, const char* k) {
    std::lock_guard lk(g_host->mu_);
    return static_cast<int>(g_host->stash_.erase(key(ns, k)));
  }
  static void stash_clear(const char* ns) {
    std::lock_guard lk(g_host->mu_);
    const std::string prefix = std::string(ns ? ns : "") + "/";
    std::erase_if(g_host->stash_, [&](const auto& kv) { return kv.first.rfind(prefix, 0) == 0; });
  }

  // Settings: the script's `set setting.<key> <value>` seeds them; set_* writes back and raises on_setting_changed.
  static const std::string* setting(const char* k) {
    const auto it = g_host->settings.find(k ? k : "");
    return it == g_host->settings.end() ? nullptr : &it->second;
  }
  static int get_setting_bool(const char* k, int fb, void*) {
    const auto* s = setting(k);
    if (!s) return fb;
    return (*s == "1" || *s == "true" || *s == "True") ? 1 : 0;
  }
  static double get_setting_number(const char* k, double fb, void*) {
    const auto* s = setting(k);
    return s ? std::atof(s->c_str()) : fb;
  }
  static const char* get_setting_string(const char* k, const char* fb, void*) {
    const auto* s = setting(k);
    return s ? s->c_str() : fb;
  }
  static void changed(const char* k, int type, int b, double d, const char* s) {
    X4NativeSettingChanged ev{g_host->ext_id_.c_str(), k, type, b, d, s};
    g_host->fire("on_setting_changed", &ev);
  }
  static void set_setting_bool(const char* k, int v, void*) {
    g_host->settings[k] = v ? "1" : "0";
    changed(k, X4N_SETTING_TOGGLE, v, 0, nullptr);
  }
  static void set_setting_number(const char* k, double v, void*) {
    g_host->settings[k] = std::to_string(v);
    changed(k, X4N_SETTING_SLIDER, 0, v, nullptr);
  }
  static void set_setting_string(const char* k, const char* v, void*) {
    g_host->settings[k] = v ? v : "";
    changed(k, X4N_SETTING_DROPDOWN, 0, 0, v);
  }
  static bool get_lua_property(const char*, X4nLuaKey, const char*, X4nLuaValueType, void*) { return false; }
  static bool get_lua_property_str(const char*, X4nLuaKey, const char*, char*, size_t) { return false; }

  // ---- fake game functions (called by the mod on whatever thread it chooses; off-main-thread calls are recorded) ----
  static void check_thread(const char* what) {
    if (std::this_thread::get_id() != g_main_thread) {
      g_host->note(std::string("VIOLATION game function ") + what + " called off the main thread");
      g_host->violation_count_++;
    }
  }
  static double GetCurrentGameTime() {
    check_thread("GetCurrentGameTime");
    return g_host->game_time.load();
  }
  static const char* GetSaveFolderPath() {
    check_thread("GetSaveFolderPath");
    return g_host->save_dir_str_.c_str();
  }
  static bool IsSaveListLoadingComplete() {
    check_thread("IsSaveListLoadingComplete");
    return g_host->save_list_complete.load();
  }
  static bool IsSaveValid(const char*) {
    check_thread("IsSaveValid");
    return g_host->save_valid.load();
  }
  static bool IsGamePaused() {
    check_thread("IsGamePaused");
    return g_host->paused.load();
  }
  static void ReloadSaveList() {
    check_thread("ReloadSaveList");
    g_host->reload_save_list_calls++;
  }
  static GameVersion GetGameVersion() {
    check_thread("GetGameVersion");
    // "9.00" -> {9, 0}
    const std::string& v = g_host->game_version;
    return GameVersion{std::atoi(v.c_str()), v.find('.') == std::string::npos ? 0 : std::atoi(v.c_str() + v.find('.') + 1)};
  }
  static const char* GetBuildVersionSuffix() {
    check_thread("GetBuildVersionSuffix");
    return g_host->build_suffix.c_str();
  }
  static UniverseID GetPlayerID() {
    check_thread("GetPlayerID");
    return 1000042;
  }
  static void AddPlayerMoney(int64_t m) {
    check_thread("AddPlayerMoney");
    g_host->money_delta += m;
  }

  // ---- M3 fake universe exports (world.h). Signatures are the X4Native 9.0.0-611726 ones. ----
  static UniverseID GetPlayerOccupiedShipID() {
    check_thread("GetPlayerOccupiedShipID");
    return g_host->world.occupied();
  }
  static UniverseID GetPlayerControlledShipID() {
    check_thread("GetPlayerControlledShipID");
    return g_host->world.controlled();
  }
  static UniverseID GetPlayerObjectID() {
    check_thread("GetPlayerObjectID");
    return g_host->world.player_ship;
  }
  static UniverseID GetPlayerContainerID() {
    check_thread("GetPlayerContainerID");
    return g_host->world.container();
  }
  static UniverseID GetContextByClass(UniverseID id, const char* cls, bool include_self) {
    check_thread("GetContextByClass");
    return g_host->world.context(id, cls ? cls : "", include_self);
  }
  static bool IsValidComponent(UniverseID id) {
    check_thread("IsValidComponent");
    World& w = g_host->world;
    return id != 0 && (w.exists(id) || w.has_sector(id) || id == World::kPlayerEntity);
  }
  static UniverseID SpawnObjectAtPos2(const char* macro, UniverseID sector, UIPosRot pos, const char* owner) {
    check_thread("SpawnObjectAtPos2");
    return g_host->world.spawn(macro ? macro : "", sector, pos, owner ? owner : "");
  }
  static void ActivateObject(UniverseID id, bool active) {
    check_thread("ActivateObject");
    World& w = g_host->world;
    w.activate_calls++;
    if (Obj* o = w.find(id)) o->active = active;
  }
  static void SetObjectSectorPos(UniverseID id, UniverseID sector, UIPosRot pos) {
    check_thread("SetObjectSectorPos");
    World& w = g_host->world;
    w.set_pos_calls++;
    Obj* o = w.find(id);
    if (!o || !w.has_sector(sector)) return;  // the game ignores a bad target; scripts see it via expect-object
    o->sector = sector;
    o->pos = pos;
  }
  static UIPosRot GetObjectPositionInSector(UniverseID id) {
    check_thread("GetObjectPositionInSector");
    const Obj* o = g_host->world.find(id);
    return o ? o->pos : UIPosRot{};
  }
  static bool TeleportPlayerTo(UniverseID id, bool allow_controlling, bool, bool) {
    check_thread("TeleportPlayerTo");
    return g_host->world.teleport(id, allow_controlling);
  }
  static const char* CanTeleportPlayerTo(UniverseID id, bool, bool) {
    check_thread("CanTeleportPlayerTo");
    g_host->str_buf_ = g_host->world.can_teleport(id);
    return g_host->str_buf_.c_str();
  }
  static void SetComponentOwner(UniverseID id, const char* faction) {
    check_thread("SetComponentOwner");
    World& w = g_host->world;
    w.owner_calls++;
    if (Obj* o = w.find(id)) o->owner = faction ? faction : "";
  }
  static void SetObjectForcedRadarVisible(UniverseID id, bool v) {
    check_thread("SetObjectForcedRadarVisible");
    World& w = g_host->world;
    w.radar_calls++;
    if (Obj* o = w.find(id)) o->radar = v;
  }
  static bool IsSetaActive() {
    check_thread("IsSetaActive");
    return g_host->world.seta;
  }
  static const char* GetObjectIDCode(UniverseID id) {
    check_thread("GetObjectIDCode");
    const Obj* o = g_host->world.find(id);
    g_host->str_buf_ = o ? o->idcode : "";  // one shared buffer, like the game: callers must copy at once
    return g_host->str_buf_.c_str();
  }
  static const char* GetComponentName(UniverseID id) {
    check_thread("GetComponentName");
    const Obj* o = g_host->world.find(id);
    g_host->str_buf_ = o ? (o->name.empty() ? o->macro : o->name) : "";
    return g_host->str_buf_.c_str();
  }
  static bool IsComponentWrecked(UniverseID id) {
    check_thread("IsComponentWrecked");
    const Obj* o = g_host->world.find(id);
    return o && o->wrecked;
  }
  // Not in the SDK table (M3-11): resolved by name only. The factions the fake game lists: the NPC ones and x4mp_team_1..8.
  static std::uint32_t GetNumAllFactions(bool) { return static_cast<std::uint32_t>(faction_names().size()); }
  static std::uint32_t GetAllFactions(const char** out, std::uint32_t n, bool) {
    const auto& names = faction_names();
    std::uint32_t i = 0;
    for (; i < n && i < names.size(); ++i) out[i] = names[i].c_str();
    return i;
  }
  static std::uint32_t GetNumAllFactionShips(const char* faction) {
    check_thread("GetNumAllFactionShips");
    return static_cast<std::uint32_t>(g_host->world.ships_of(faction ? faction : "").size());
  }
  static std::uint32_t GetAllFactionShips(UniverseID* out, std::uint32_t n, const char* faction) {
    check_thread("GetAllFactionShips");
    const auto ids = g_host->world.ships_of(faction ? faction : "");
    std::uint32_t i = 0;
    for (; i < n && i < ids.size(); ++i) out[i] = ids[i];
    return i;
  }
  static const std::vector<std::string>& faction_names() { return g_host->world.factions; }  // `world factions a,b,c` edits it
  static bool IsPlayerOccupiedShipDocked() {
    check_thread("IsPlayerOccupiedShipDocked");
    return g_host->world.docked;
  }
  // M3-10: removal (the mod's only removal path is game::safe_remove, which resolves this export), the faction list and the ships of a faction.
  static void RemoveComponent(UniverseID id) {
    check_thread("RemoveComponent");
    g_host->world.remove(id);  // never the player ship or the station
  }
};

Host::Host(std::filesystem::path work_dir, std::string ext_id) : work_dir_(std::move(work_dir)), ext_id_(std::move(ext_id)) {
  g_host = this;
  g_main_thread = std::this_thread::get_id();
  std::filesystem::create_directories(work_dir_);
  save_dir = work_dir_ / "saves";
  std::filesystem::create_directories(save_dir);
  save_dir_str_ = save_dir.string() + "\\";
  ext_name_ = ext_id_;
  ext_path_ = (work_dir_ / "extension").string();
  std::filesystem::create_directories(ext_path_);
  // Portable mode: config and logs/x4mp.log stay in the extension folder, never in the user's Documents.
  { std::ofstream(work_dir_ / "extension" / "x4mp.portable").put(' '); }
  build_game();
  build_api();
}

Host::~Host() {
  if (log_file_) std::fclose(log_file_);
  if (g_host == this) g_host = nullptr;
}

void Host::build_game() {
  game_ = std::make_unique<X4GameFunctions>();
  std::memset(game_.get(), 0, sizeof(X4GameFunctions));
  game_->GetCurrentGameTime = &Thunks::GetCurrentGameTime;
  game_->GetSaveFolderPath = &Thunks::GetSaveFolderPath;
  game_->IsSaveListLoadingComplete = &Thunks::IsSaveListLoadingComplete;
  game_->IsSaveValid = &Thunks::IsSaveValid;
  game_->IsGamePaused = &Thunks::IsGamePaused;
  game_->ReloadSaveList = &Thunks::ReloadSaveList;
  game_->GetGameVersion = &Thunks::GetGameVersion;
  game_->GetBuildVersionSuffix = &Thunks::GetBuildVersionSuffix;
  game_->GetPlayerID = &Thunks::GetPlayerID;
  game_->AddPlayerMoney = &Thunks::AddPlayerMoney;
  game_->GetPlayerOccupiedShipID = &Thunks::GetPlayerOccupiedShipID;
  game_->GetPlayerControlledShipID = &Thunks::GetPlayerControlledShipID;
  game_->GetPlayerObjectID = &Thunks::GetPlayerObjectID;
  game_->GetPlayerContainerID = &Thunks::GetPlayerContainerID;
  game_->GetContextByClass = &Thunks::GetContextByClass;
  game_->IsValidComponent = &Thunks::IsValidComponent;
  game_->SpawnObjectAtPos2 = &Thunks::SpawnObjectAtPos2;
  game_->ActivateObject = &Thunks::ActivateObject;
  game_->SetObjectSectorPos = &Thunks::SetObjectSectorPos;
  game_->GetObjectPositionInSector = &Thunks::GetObjectPositionInSector;
  game_->TeleportPlayerTo = &Thunks::TeleportPlayerTo;
  game_->CanTeleportPlayerTo = &Thunks::CanTeleportPlayerTo;
  game_->SetComponentOwner = &Thunks::SetComponentOwner;
  game_->SetObjectForcedRadarVisible = &Thunks::SetObjectForcedRadarVisible;
  game_->IsSetaActive = &Thunks::IsSetaActive;
  game_->GetObjectIDCode = &Thunks::GetObjectIDCode;
  game_->GetComponentName = &Thunks::GetComponentName;
  game_->IsComponentWrecked = &Thunks::IsComponentWrecked;
  game_->IsPlayerOccupiedShipDocked = &Thunks::IsPlayerOccupiedShipDocked;
  game_->RemoveComponent = &Thunks::RemoveComponent;
#define X4HS_REG(name) game_fns_[#name] = reinterpret_cast<void*>(game_->name)
  X4HS_REG(GetCurrentGameTime);
  X4HS_REG(GetSaveFolderPath);
  X4HS_REG(IsSaveListLoadingComplete);
  X4HS_REG(IsSaveValid);
  X4HS_REG(IsGamePaused);
  X4HS_REG(ReloadSaveList);
  X4HS_REG(GetGameVersion);
  X4HS_REG(GetBuildVersionSuffix);
  X4HS_REG(GetPlayerID);
  X4HS_REG(AddPlayerMoney);
  X4HS_REG(GetPlayerOccupiedShipID);
  X4HS_REG(GetPlayerControlledShipID);
  X4HS_REG(GetPlayerObjectID);
  X4HS_REG(GetPlayerContainerID);
  X4HS_REG(GetContextByClass);
  X4HS_REG(IsValidComponent);
  X4HS_REG(SpawnObjectAtPos2);
  X4HS_REG(ActivateObject);
  X4HS_REG(SetObjectSectorPos);
  X4HS_REG(GetObjectPositionInSector);
  X4HS_REG(TeleportPlayerTo);
  X4HS_REG(CanTeleportPlayerTo);
  X4HS_REG(SetComponentOwner);
  X4HS_REG(SetObjectForcedRadarVisible);
  X4HS_REG(IsSetaActive);
  X4HS_REG(GetObjectIDCode);
  X4HS_REG(GetComponentName);
  X4HS_REG(IsComponentWrecked);
  X4HS_REG(IsPlayerOccupiedShipDocked);
  game_fns_["GetNumAllFactions"] = reinterpret_cast<void*>(&Thunks::GetNumAllFactions);
  game_fns_["GetAllFactions"] = reinterpret_cast<void*>(&Thunks::GetAllFactions);
  game_fns_["GetNumAllFactionShips"] = reinterpret_cast<void*>(&Thunks::GetNumAllFactionShips);
  game_fns_["GetAllFactionShips"] = reinterpret_cast<void*>(&Thunks::GetAllFactionShips);
  X4HS_REG(RemoveComponent);
#undef X4HS_REG
  offsets_ = std::make_unique<std::uint8_t[]>(sizeof(X4GameOffsets));
  std::memset(offsets_.get(), 0, sizeof(X4GameOffsets));
}

void Host::build_api() {
  api = X4NativeAPI{};
  api.api_version = X4NATIVE_API_VERSION;
  api.subscribe = &Thunks::subscribe;
  api.unsubscribe = &Thunks::unsubscribe;
  api.raise_event = &Thunks::raise_event;
  api.raise_lua_event = &Thunks::raise_lua_event;
  api.register_lua_bridge = &Thunks::register_lua_bridge;
  api.log = &Thunks::log;
  api.get_game_version = &Thunks::game_version;
  api.get_x4native_version = &Thunks::x4native_version;
  api.extension_path = ext_path_.c_str();
  api.game = game_.get();
  api.get_game_function = &Thunks::get_game_function;
  api.game_func_count = static_cast<int>(sizeof(X4GameFunctions) / sizeof(void*));
  api.exe_base = 0x140000000ull;
  api.game_types_build = game_types_build;
  api.hook_before = &Thunks::hook_add;
  api.hook_after = &Thunks::hook_add;
  api.unhook = &Thunks::unhook;
  api._ensure_detour = &Thunks::ensure_detour;
  api._run_before_hooks = &Thunks::run_hooks;
  api._run_after_hooks = &Thunks::run_hooks;
  api.resolve_internal = &Thunks::resolve_internal;
  api.md_subscribe_before = &Thunks::md_before;
  api.md_subscribe_after = &Thunks::md_after;
  api.stash_set = &Thunks::stash_set;
  api.stash_get = &Thunks::stash_get;
  api.stash_remove = &Thunks::stash_remove;
  api.stash_clear = &Thunks::stash_clear;
  api._ext_id = ext_id_.c_str();
  api._ext_display_name = ext_name_.c_str();
  api._ext_log_fn = reinterpret_cast<void*>(&Thunks::ext_log);
  api.offsets = offsets_.get();
  api.get_setting_bool = &Thunks::get_setting_bool;
  api.get_setting_number = &Thunks::get_setting_number;
  api.get_setting_string = &Thunks::get_setting_string;
  api.set_setting_bool = &Thunks::set_setting_bool;
  api.set_setting_number = &Thunks::set_setting_number;
  api.set_setting_string = &Thunks::set_setting_string;
  api.get_lua_property = &Thunks::get_lua_property;
  api.get_lua_property_str = &Thunks::get_lua_property_str;
}

void Host::emulate_md(const std::string& topic, const std::string& param) {
  if (topic == "x4mp.ghost_dress") {
    ++world.dress_events;
    const auto j = nlohmann::json::parse(param, nullptr, false);
    if (j.is_discarded() || !j.is_object() || !j.contains("id")) return;
    const auto id = static_cast<std::uint64_t>(std::strtoull(j["id"].get<std::string>().c_str(), nullptr, 10));
    if (Obj* o = world.find(id); o && id != world.player_ship) {  // MD refuses the player's ship
      if (j.contains("name")) o->name = j["name"].get<std::string>();
      if (j.contains("minhull")) o->min_hull = j["minhull"].get<int>();
      o->radar = true;
    }
  } else if (topic == "x4mp.ghost_velocity") {
    ++world.velocity_events;
    std::size_t pos = 0;
    while (pos < param.size()) {
      auto end = param.find(';', pos);
      if (end == std::string::npos) end = param.size();
      const std::string item = param.substr(pos, end - pos);
      pos = end + 1;
      // "<id>,<vx>,<vy>,<vz>"
      char* e = nullptr;
      const char* p = item.c_str();
      const unsigned long long id = std::strtoull(p, &e, 10);
      bool ok = e != p && *e == ',';
      double v[3] = {0, 0, 0};
      for (int k = 0; ok && k < 3; ++k) {
        p = e + 1;
        v[k] = std::strtod(p, &e);
        ok = e != p && (k == 2 ? *e == '\0' : *e == ',');
      }
      const double x = v[0], y = v[1], z = v[2];
      if (ok) {
        if (Obj* o = world.find(id); o && id != world.player_ship) {
          o->vx = x;
          o->vy = y;
          o->vz = z;
          ++o->velocity_hints;
        }
      }
    }
  } else if (topic == "x4mp.sector_map_collect") {  // M3-09 asks MD for the sector list: answer it (the ghosts use that map)
    for (const auto& data : world.md_sector_map) md_answers_.emplace_back("x4mp.sector_map", nlohmann::json{{"v", 1}, {"data", data}}.dump());
  } else if (topic == "x4mp.teams_apply") {  // M3-08: the MD team setup; answer with a report that matches the plan
    ++world.teams_applies;
    const auto j = nlohmann::json::parse(param, nullptr, false);
    if (j.is_discarded() || !j.is_object() || !j.contains("seq")) return;
    const auto seq = j["seq"].get<unsigned>();
    const std::size_t slots = j.contains("slots") && j["slots"].is_array() ? j["slots"].size() : 0;
    const std::size_t rel = j.contains("rel") && j["rel"].is_array() ? j["rel"].size() : 0;
    const std::string report = "R;" + std::to_string(seq) + ";" + std::to_string(slots) + ";0;0;" + std::to_string(slots) + ";" + std::to_string(rel);
    md_answers_.emplace_back("x4mp.teams_md", nlohmann::json{{"v", 1}, {"data", report}}.dump());
  }
}

void Host::deliver_md_answers() {
  if (md_answers_.empty()) return;
  std::vector<std::pair<std::string, std::string>> now;
  now.swap(md_answers_);
  for (auto& a : now) fire(a.first, a.second.data());
}

bool Host::fire(const std::string& name, void* data) {
  std::vector<Sub> copy;
  {
    std::lock_guard lk(mu_);
    for (const auto& s : subs_)
      if (s.name == name) copy.push_back(s);
  }
  bool ok = true;
  for (const auto& s : copy) {
    try {
      s.cb(name.c_str(), data, s.ud);
    } catch (const std::exception& e) {
      note("VIOLATION a subscriber of '" + name + "' threw: " + e.what());
      violation_count_++;
      ok = false;
    } catch (...) {
      note("VIOLATION a subscriber of '" + name + "' threw a non-standard exception");
      violation_count_++;
      ok = false;
    }
  }
  return ok;
}

int Host::subscriber_count(const std::string& name) {
  std::lock_guard lk(mu_);
  return static_cast<int>(std::count_if(subs_.begin(), subs_.end(), [&](const Sub& s) { return s.name == name; }));
}

bool Host::has_bridge(const std::string& lua_event, std::string* cpp_event) {
  std::lock_guard lk(mu_);
  const auto it = bridges_.find(lua_event);
  if (it == bridges_.end()) return false;
  if (cpp_event) *cpp_event = it->second;
  return true;
}

std::size_t Host::hook_count() {
  std::lock_guard lk(mu_);
  return hooks_.size();
}

void Host::clear_registrations() {
  std::lock_guard lk(mu_);
  subs_.clear();
  bridges_.clear();
  hooks_.clear();
}

bool Host::wait_lua(const std::string& topic, std::chrono::milliseconds timeout, const std::function<bool(const LuaEvent&)>& accept,
                    LuaEvent* out) {
  std::unique_lock lk(mu_);
  const auto find = [&] {
    for (auto it = lua_.begin(); it != lua_.end(); ++it) {
      if (it->topic == topic && (!accept || accept(*it))) {
        if (out) *out = *it;
        lua_.erase(it);
        return true;
      }
    }
    return false;
  };
  return cv_.wait_for(lk, timeout, find);
}

std::size_t Host::drop_lua(const std::string& topic) {
  std::lock_guard lk(mu_);
  const auto before = lua_.size();
  if (topic.empty()) lua_.clear();
  else std::erase_if(lua_, [&](const LuaEvent& e) { return e.topic == topic; });
  return before - lua_.size();
}

std::string Host::lua_summary(std::size_t max_items) {
  std::lock_guard lk(mu_);
  if (lua_.empty()) return "(no unconsumed Lua events)";
  std::string s;
  const std::size_t start = lua_.size() > max_items ? lua_.size() - max_items : 0;
  for (std::size_t i = start; i < lua_.size(); ++i) {
    std::string p = lua_[i].param.substr(0, 80);
    s += (s.empty() ? "" : ", ") + lua_[i].topic + "(" + p + ")";
  }
  return s;
}

std::size_t Host::lua_pending() {
  std::lock_guard lk(mu_);
  return lua_.size();
}

std::vector<LuaEvent> Host::lua_all() {
  std::lock_guard lk(mu_);
  return lua_history_;
}

bool Host::wait_log(const std::string& needle, std::chrono::milliseconds timeout, bool only_new) {
  std::unique_lock lk(mu_);
  std::size_t from = only_new ? log_cursor_ : 0;
  const auto scan = [&] {
    for (std::size_t i = from; i < logs_.size(); ++i) {
      if (logs_[i].find(needle) != std::string::npos) {
        log_cursor_ = i + 1;
        return true;
      }
    }
    from = logs_.size();
    return false;
  };
  return cv_.wait_for(lk, timeout, scan);
}

bool Host::log_contains(const std::string& needle) {
  std::lock_guard lk(mu_);
  return std::any_of(logs_.begin(), logs_.end(), [&](const std::string& l) { return l.find(needle) != std::string::npos; });
}

std::vector<std::string> Host::log_copy() {
  std::lock_guard lk(mu_);
  return logs_;
}

void Host::set_log_file(const std::filesystem::path& p) {
  std::lock_guard lk(mu_);
  if (log_file_) std::fclose(log_file_);
  log_file_ = nullptr;
  _wfopen_s(&log_file_, p.c_str(), L"wb");
}

void Host::note(const std::string& line) {
  std::lock_guard lk(mu_);
  std::printf("%s\n", line.c_str());
  std::fflush(stdout);
  if (log_file_) {
    std::fprintf(log_file_, "%s\n", line.c_str());
    std::fflush(log_file_);
  }
}

std::map<std::string, std::string> Host::stash_copy() {
  std::lock_guard lk(mu_);
  return stash_;
}

bool Host::stash_has(const std::string& key) {
  std::lock_guard lk(mu_);
  return stash_.count(key) != 0;
}

bool Host::stash_value(const std::string& key, std::string* out) {
  std::lock_guard lk(mu_);
  const auto it = stash_.find(key);
  if (it == stash_.end()) return false;
  if (out) *out = it->second;
  return true;
}

}  // namespace hostsim
