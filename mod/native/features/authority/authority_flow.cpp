#include "features/authority/authority_flow.h"

#include <algorithm>
#include <cstring>
#include <fstream>

#include <nlohmann/json.hpp>

#include "core/authority/checkpoint_messages.h"
#include "core/authority/entity_spawn.h"
#include "core/crypto/crypto.h"
#include "features/avatars/avatar_hub.h"
#include "features/chat/chat_json.h"
#include "features/selfship/selfship_hub.h"
#include "world_generated.h"
#include "features/janitor/janitor_feature.h"
#include "common_generated.h"
#include "message_ids_generated.h"

namespace x4mp::features::auth {

namespace {
using host::Cat;
using host::Level;
namespace fs = std::filesystem;
namespace P = X4MP::Proto;
using std::chrono::milliseconds;
using std::chrono::seconds;
constexpr std::uint16_t T(P::MsgType t) { return static_cast<std::uint16_t>(t); }

constexpr auto kCollectTimeout = seconds(20);
constexpr auto kShipGrace = milliseconds(1500);  // after the end marker: how long to wait for the ship record
constexpr auto kShipAnswerTimeout = seconds(5);  // MD answers within a frame or two; no answer = counted like "no ship"
constexpr int kAskBackoffMin = 20;               // frames: first re-ask when MD says "no ship" while the game says the player sits
constexpr int kAskBackoffMax = 600;
constexpr auto kSaveReplyTimeout = seconds(20);
constexpr auto kFileTimeout = seconds(180);
constexpr auto kFilePollEvery = milliseconds(250);
constexpr std::size_t kKeepCheckpointSaves = 2;
constexpr std::size_t kMaxInbox = 64;

std::string hex12(const std::vector<std::uint8_t>& v) {
  return crypto::to_hex(std::span<const std::uint8_t>(v).first(std::min<std::size_t>(6, v.size())));
}

std::string json_string(const std::string& text, const char* key) {
  const auto doc = nlohmann::json::parse(text, nullptr, false);
  if (!doc.is_object()) return {};
  const auto it = doc.find(key);
  return (it != doc.end() && it->is_string()) ? it->get<std::string>() : std::string{};
}
}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
void AuthInbox::push(std::string verb, std::string text) {
  const std::lock_guard lock(mutex);
  if (items.size() >= kMaxInbox) items.erase(items.begin());
  items.emplace_back(std::move(verb), std::move(text));
}
std::vector<std::pair<std::string, std::string>> AuthInbox::take() {
  const std::lock_guard lock(mutex);
  return std::exchange(items, {});
}

std::shared_ptr<AuthInbox> subscribe_authority_verbs(host::IPlatform& platform) {
  auto inbox = std::make_shared<AuthInbox>();
  for (const char* verb : {"auth_md", "auth_saved"}) {
    const std::string v = verb;
    std::shared_ptr<AuthInbox> in = inbox;
    (void)platform.subscribe_event(("x4mp." + v).c_str(), [in, v](std::string_view text) {
      if (text.size() <= 256 * 1024) in->push(v, std::string(text));
    });
  }
  return inbox;
}

// ---------------------------------------------------------------------------------------------------------------------
std::vector<std::uint8_t> AuthorityFlow::stored_loaded_sha(const session::IStash& stash) {
  if (const auto text = stash.get(kStashKey)) return AuthorityState::from_json(*text).loaded_sha;
  return {};
}
void AuthorityFlow::forget_loaded_sha(session::IStash& stash) {
  if (const auto text = stash.get(kStashKey)) {
    auto s = AuthorityState::from_json(*text);
    s.loaded_sha.clear();
    stash.put(kStashKey, s.to_json());
  }
}

AuthorityFlow::AuthorityFlow(host::HostContext& ctx, session::Session& session, session::IStash& stash, std::shared_ptr<AuthInbox> inbox)
    : session_(session), stash_(stash), inbox_(std::move(inbox)) {
  if (const auto text = stash_.get(kStashKey)) state_ = AuthorityState::from_json(*text);
  if (const auto dir = ctx.game.save_folder_path()) save_dir_ = fs::path(*dir);
  if (ctx.paths != nullptr && !ctx.paths->dir.empty()) {
    work_dir_ = ctx.paths->dir / "authority";
    ledger_ = ctx.paths->dir / "authority-saves.json";
  }
  // M3-11: the avatars take their net ids and string refs from this flow and send through its control lane.
  avatars::AuthorityServices svc;
  svc.connected = [this] { return uploader_.connected(); };
  svc.alloc_net_id = [this] { return allocate_net_id(); };
  svc.reserve_net_ids_above = [this](std::uint32_t id) { reserve_net_ids_above(id); };
  svc.string_ref = [this](avatars::StrKind kind, const std::string& value) {
    return string_ref(static_cast<std::uint8_t>(kind == avatars::StrKind::Faction ? P::StringKind::Faction : P::StringKind::Macro), value);
  };
  svc.send_control = [this](std::uint16_t type, std::vector<std::uint8_t> payload) { return send_control(type, payload); };
  avatars::avatar_hub().set_services(std::move(svc));
}

std::uint32_t AuthorityFlow::allocate_net_id() {
  const std::uint32_t id = state_.next_net_id++;
  persist();
  return id;
}

void AuthorityFlow::reserve_net_ids_above(std::uint32_t id) noexcept {
  if (state_.next_net_id <= id && id != 0xFFFFFFFEu) {
    state_.next_net_id = id + 1;
    persist();
  }
}

// The server's string table index of (kind, value): one already known, else appended (StringTableAdd with one entry). 0 = could not.
std::uint32_t AuthorityFlow::string_ref(std::uint8_t kind, const std::string& value) {
  if (value.empty()) return 0;
  for (const auto& k : known_strings_) {
    if (k.kind == kind && k.value == value) return k.index;
  }
  if (!uploader_.connected()) return 0;
  const std::uint32_t index = state_.string_count + 1;
  const std::vector<x4mp::authority::StringDesc> add = {{index, kind, value}};
  if (!send_control(T(P::MsgType::StringTableAdd), x4mp::authority::encode_string_table_add(add))) return 0;
  state_.string_count = index;
  known_strings_.push_back({kind, value, index});
  persist();
  return index;
}

AuthorityFlow::~AuthorityFlow() {
  avatars::avatar_hub().clear_services();
  if (prepared_.valid()) prepared_.wait();
}

void AuthorityFlow::persist() const { stash_.put(kStashKey, state_.to_json()); }

void AuthorityFlow::on_welcome(host::HostContext& ctx, bool resumed) {
  const auto generation = uploader_.new_connection(resumed);
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: connection generation {} ({})", generation, resumed ? "resumed" : "fresh join");
  if (!resumed) {
    step_ = Step::Idle;
    spawn_due_ = false;
    ship_wait_ = ship_pending_ = late_ship_ = false;
    sent_macros_.clear();
    known_strings_.clear();
    state_.spawned = false;
    state_.host_net_id = 0;
    state_.strings_sent = false;
    state_.string_count = 0;
    state_.next_net_id = 1;
    state_.checkpoints = 0;
    persist();
  }
  // M3-11: the avatars announce themselves again; ids already given to avatars stay reserved (a fresh join restarts the counter at 1).
  avatars::avatar_hub().on_welcome();
  reserve_net_ids_above(avatars::avatar_hub().max_net_id());
}

void AuthorityFlow::on_net_disconnected() { uploader_.connection_lost(); }

void AuthorityFlow::note_loaded_save(const std::vector<std::uint8_t>& sha) {
  if (sha.size() != 32) return;
  state_.loaded_sha = sha;
  persist();
}

bool AuthorityFlow::send_control(std::uint16_t type, const std::vector<std::uint8_t>& payload) {
  return session_.send(wire::Lane::Control, type, std::span<const std::uint8_t>(payload)) == net::SendResult::Ok;
}

bool AuthorityFlow::on_frame(host::HostContext& ctx, std::uint16_t type, std::span<const std::uint8_t> payload) {
  if (type == T(P::MsgType::RequestSave)) {
    const auto req = x4mp::authority::parse_request_save(payload);
    if (!req) return true;
    if (step_ != Step::Idle || uploader_.running() || uploader_.pending() || spawn_due_) {
      X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: RequestSave {} ignored, a checkpoint is already in progress", req->request_id);
      return true;
    }
    begin_request(ctx, req->request_id);
    return true;
  }
  return uploader_.on_frame(type, payload);
}

void AuthorityFlow::begin_request(host::HostContext& ctx, std::uint32_t request_id) {
  request_id_ = request_id;
  collected_.reset();
  end_seen_at_.reset();
  plan_ = GalaxyPlan{};
  step_ = Step::Collecting;
  step_since_ = Clock::now();
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: RequestSave {}: collecting galaxy metadata from MD", request_id);
  if (!ctx.platform.raise_lua("x4mp.auth_collect", R"({"v":1})")) {
    fail(ctx, "the Lua bridge is not available (x4mp.auth_collect could not be raised)");
  }
}

void AuthorityFlow::fail(host::HostContext& ctx, const std::string& why) {
  X4MP_CLOG(ctx.log, Cat::Save, Level::Error, "authority: checkpoint request {} failed: {}", request_id_, why);
  step_ = Step::Idle;
  watcher_.reset();
  if (prepared_.valid()) prepared_.wait();
  prepared_ = {};
}

void AuthorityFlow::drain_inbox(host::HostContext& ctx) {
  for (auto& [verb, text] : inbox_->take()) {
    if (verb == "auth_md") {
      if (step_ != Step::Collecting) {
        // the answer to a ship-only question (item 3): a P record, or N when MD has no ship either
        if (!ship_pending_ && !ship_wait_) continue;
        MdCollector answer;
        if (!answer.add(json_string(text, "data"))) continue;
        if (answer.ship()) {
          spawn_ship_ = answer.ship();
          ship_pending_ = false;
          ship_wait_ = false;
          late_ship_ = true;
          spawn_due_ = !state_.spawned;
          X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: MD now reports the player's ship ({}) after {} ask(s)", spawn_ship_->macro, ship_asks_);
        } else if (answer.no_ship_seen() && ship_pending_) {
          ship_pending_ = false;
          schedule_reask();
        }
        continue;
      }
      const std::string data = json_string(text, "data");
      if (!collected_.add(data)) {
        X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: unreadable galaxy message ignored ({} bytes)", data.size());
      } else if (collected_.end_seen() && !end_seen_at_) {
        end_seen_at_ = Clock::now();
      }
    } else if (verb == "auth_saved") {
      if (step_ == Step::WaitSaveReply) {
        on_save_reply(ctx, text);
      } else {
        X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: SaveGame reply with no save pending, ignored");
      }
    }
  }
}

void AuthorityFlow::step(host::HostContext& ctx) {
  drain_inbox(ctx);
  switch (step_) {
    case Step::Collecting: step_collecting(ctx); break;
    case Step::WaitSaveReply:
      if (Clock::now() - step_since_ > kSaveReplyTimeout) fail(ctx, "Lua did not answer the SaveGame request in 20 s");
      break;
    case Step::WaitFile: step_wait_file(ctx); break;
    case Step::Hashing: step_hashing(ctx); break;
    case Step::Uploading: step_uploading(ctx); break;
    case Step::Idle: break;
  }
  (void)uploader_.pump([this](wire::Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) {
    return session_.send(lane, type, payload) == net::SendResult::Ok;
  });
  if (spawn_due_) maybe_spawn(ctx);
  if (ship_wait_ && step_ == Step::Idle && !spawn_due_) step_ship_wait(ctx);
  step_world_clock(ctx);
}

// M3-14 (found by the two-DLL pair run): without a WorldUpdate the server never replicates anything (ReplicationModule: "no WorldUpdate yet: its game time is the
// reference every entry is stamped with"), so no client would see another player move. The M3 mod streams no NPC world (M4), so the authority only sends the
// clock: a WorldUpdate with no states, 20 Hz (the server's tick), on the Realtime lane like PlayerState. Starts once a checkpoint exists (the session runs).
void AuthorityFlow::step_world_clock(host::HostContext& ctx) {
  if (state_.checkpoints == 0 && stored_total_ == 0) return;
  const auto now = Clock::now();
  if (now < next_wu_) return;
  auto& hub = selfship::selfship_hub();
  std::int64_t server_now = 0;
  if (!hub.linked() || !hub.server_now(server_now)) return;
  const auto game_time = ctx.game.game_time();
  if (!game_time || !x4mp::authority::EntitySpawnBuilder::is_valid_game_time(*game_time)) return;
  next_wu_ = std::max(next_wu_ + std::chrono::milliseconds(50), now - std::chrono::milliseconds(50));  // 20 Hz, at most one extra send after a long frame
  wu_fbb_.Clear();
  const auto states = wu_fbb_.CreateVectorOfStructs<P::EntityState>(nullptr, 0);
  wu_fbb_.Finish(P::CreateWorldUpdate(wu_fbb_, ++wu_tick_, static_cast<std::uint64_t>(server_now), *game_time, states));
  if (hub.send_realtime(T(P::MsgType::WorldUpdate), std::span<const std::uint8_t>(wu_fbb_.GetBufferPointer(), wu_fbb_.GetSize()))) {
    ++wu_sent_;
    if (!wu_logged_) {
      wu_logged_ = true;
      X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: world clock: keepalive WorldUpdate at 20 Hz started (game_time {:.3f}); the server replicates player ships from now on", *game_time);
    }
  } else {
    ++wu_failed_;
  }
}

void AuthorityFlow::step_collecting(host::HostContext& ctx) {
  const auto now = Clock::now();
  if (collected_.complete() && (collected_.ship() || (end_seen_at_ && now - *end_seen_at_ > kShipGrace))) {
    plan_ = build_plan(collected_);
    if (plan_.sectors.empty()) return fail(ctx, "MD reported no sectors");
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: galaxy collected: {} sectors, {} links, {} strings, ship {}", plan_.sectors.size(),
              plan_.links.size(), plan_.strings.size(), collected_.ship() ? collected_.ship()->macro : std::string("(none)"));
    request_save(ctx);
  } else if (now - step_since_ > kCollectTimeout) {
    fail(ctx, "MD did not deliver the galaxy metadata in 20 s (is md/x4mp_galaxy.xml loaded?)");
  }
}

void AuthorityFlow::request_save(host::HostContext& ctx) {
  std::array<std::uint8_t, 16> rnd{};
  if (!crypto::random_bytes(rnd)) return fail(ctx, "no random source for the checkpoint id");
  std::memcpy(&checkpoint_.lo, rnd.data(), 8);
  std::memcpy(&checkpoint_.hi, rnd.data() + 8, 8);
  if (checkpoint_.zero()) checkpoint_.lo = 1;
  save_name_ = checkpoint_save_name(checkpoint_.lo);
  if (save_dir_.empty()) return fail(ctx, "the game's save folder is not available");

  spawn_strings_fresh_ = false;
  if (!state_.strings_sent) {
    if (!send_control(T(P::MsgType::StringTableAdd), x4mp::authority::encode_string_table_add(plan_.strings))) return fail(ctx, "StringTableAdd not sent");
    state_.strings_sent = true;
    state_.string_count = static_cast<std::uint32_t>(plan_.strings.size());
    spawn_strings_fresh_ = true;
    sent_macros_.clear();
    known_strings_.clear();
    for (const auto& str : plan_.strings) {
      known_strings_.push_back({str.kind, str.value, str.index});
      if (str.kind == static_cast<std::uint8_t>(P::StringKind::Macro)) sent_macros_.emplace_back(str.value, str.index);
    }
    persist();
  }
  nlohmann::json j;
  j["v"] = 1;
  j["name"] = save_name_;
  j["request_id"] = request_id_;
  j["display"] = "X4MP checkpoint " + save_name_.substr(10, 8);
  // M3-13 save hygiene: the authority universe must hold no ghost / client-side leftovers when the game writes the checkpoint. The check removes
  // what it finds (never an avatar, never a guarded id); a leftover that cannot be removed flags the save ghosts_cleaned=false (the server stores
  // it but never makes it current, ADR-023).
  const auto hygiene = JanitorFeature::checkpoint_check(ctx);
  ghosts_cleaned_ = hygiene.clean;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: SaveGame requested as {} (request {})", save_name_, request_id_);
  step_ = Step::WaitSaveReply;
  step_since_ = Clock::now();
  if (!ctx.platform.raise_lua("x4mp.auth_save", j.dump())) fail(ctx, "x4mp.auth_save could not be raised");
}

void AuthorityFlow::on_save_reply(host::HostContext& ctx, const std::string& text) {
  const auto doc = nlohmann::json::parse(text, nullptr, false);
  if (!doc.is_object() || !doc.value("ok", false)) {
    return fail(ctx, "SaveGame failed in Lua: " + (doc.is_object() ? doc.value("error", std::string("unknown")) : std::string("bad reply")));
  }
  double gt = doc.value("game_time", 0.0);  // Lua GetCurrentGameTime() sampled right before SaveGame (== MD player.age)
  if (!x4mp::authority::EntitySpawnBuilder::is_valid_game_time(gt)) {
    const auto native = ctx.game.game_time();
    gt = native.value_or(0.0);
  }
  if (!x4mp::authority::EntitySpawnBuilder::is_valid_game_time(gt)) return fail(ctx, "no valid game time for SaveStarted");
  game_time_ = gt;
  manifest_avatars_ = avatars::avatar_hub().manifest_records();  // the avatars exactly as the game saves them now
  const auto started = x4mp::authority::encode_save_started(request_id_, checkpoint_, game_time_, state_.next_net_id);
  if (!started || !send_control(T(P::MsgType::SaveStarted), *started)) return fail(ctx, "SaveStarted not sent");
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: SaveStarted sent (game_time {:.3f}, next_net_id {}); waiting for {}.xml.gz to be complete", game_time_,
            state_.next_net_id, save_name_);
  watcher_.emplace(save_dir_ / (save_name_ + ".xml.gz"));
  next_poll_ = Clock::now();
  step_ = Step::WaitFile;
  step_since_ = Clock::now();
}

void AuthorityFlow::step_wait_file(host::HostContext& ctx) {
  const auto now = Clock::now();
  if (now - step_since_ > kFileTimeout) return fail(ctx, "the save file was not complete after 180 s");
  if (now < next_poll_) return;
  next_poll_ = now + kFilePollEvery;
  if (!watcher_->poll(now)) return;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: save file complete ({} bytes) after {} ms; hashing", watcher_->size(),
            std::chrono::duration_cast<milliseconds>(now - step_since_).count());
  const fs::path save_path = watcher_->file();
  const fs::path work = work_dir_.empty() ? fs::temp_directory_path() / "x4mp-authority" : work_dir_;
  const auto cp = checkpoint_;
  const auto gt = game_time_;
  const auto next_id = state_.next_net_id;
  auto strings = plan_.strings;
  const auto sectors = plan_.sectors;
  const std::string name = save_name_;
  // M3-11: the avatars become manifest entries; their macro / faction strings join the manifest's own table
  std::vector<x4mp::authority::ManifestAvatar> avs;
  {
    std::uint32_t top = 0;
    for (const auto& s : strings) top = std::max(top, s.index);
    const auto ref = [&](std::uint8_t kind, const std::string& value) -> std::uint32_t {
      for (const auto& s : strings) {
        if (s.kind == kind && s.value == value) return s.index;
      }
      strings.push_back({++top, kind, value});
      return top;
    };
    for (const auto& r : manifest_avatars_) {
      x4mp::authority::ManifestAvatar m;
      m.net_id = r.net_id;
      m.kind = avatars::ship_kind_of_macro(r.macro);
      m.macro_ref = ref(static_cast<std::uint8_t>(P::StringKind::Macro), r.macro);
      m.owner_ref = ref(static_cast<std::uint8_t>(P::StringKind::Faction), r.owner);
      m.owner_team = r.team;
      m.owner_player = r.player_id;
      for (const auto& sec : sectors) {
        if (sec.macro == r.sector_macro) m.sector = sec.index;
      }
      m.idcode = r.idcode;
      m.x = static_cast<float>(r.pose.x);
      m.y = static_cast<float>(r.pose.y);
      m.z = static_cast<float>(r.pose.z);
      m.controller_player = r.online ? r.player_id : 0;
      avs.push_back(std::move(m));
    }
    if (!avs.empty()) X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: manifest lists {} avatar(s)", avs.size());
  }
  prepared_ = std::async(std::launch::async, [=]() -> Prepared {
    Prepared out;
    auto save = x4mp::authority::describe_upload_file(session::TransferKind::Save, save_path, name);
    if (!save) {
      out.error = "cannot read the save file";
      return out;
    }
    const auto manifest = x4mp::authority::encode_manifest(cp, gt, next_id, strings, sectors, avs);
    if (!manifest) {
      out.error = "invalid game time for the manifest";
      return out;
    }
    std::error_code ec;
    fs::create_directories(work, ec);
    out.manifest_path = work / (name + ".x4mf");
    {
      std::ofstream f(out.manifest_path, std::ios::binary | std::ios::trunc);
      f.write(reinterpret_cast<const char*>(manifest->data()), static_cast<std::streamsize>(manifest->size()));
      if (!f) {
        out.error = "cannot write the manifest";
        return out;
      }
    }
    auto mf = x4mp::authority::describe_upload_file(session::TransferKind::Manifest, out.manifest_path, "manifest");
    if (!mf) {
      out.error = "cannot read the manifest back";
      return out;
    }
    out.save = std::move(*save);
    out.manifest = std::move(*mf);
    out.ok = true;
    return out;
  });
  step_ = Step::Hashing;
}

void AuthorityFlow::step_hashing(host::HostContext& ctx) {
  if (!prepared_.valid()) return fail(ctx, "internal: no hash job");
  if (prepared_.wait_for(std::chrono::seconds(0)) != std::future_status::ready) return;
  ready_ = prepared_.get();
  if (!ready_.ok) return fail(ctx, ready_.error);
  if (!send_control(T(P::MsgType::GalaxyMetadata), x4mp::authority::encode_galaxy_metadata(ready_.save.sha256, plan_.sectors, plan_.links))) {
    return fail(ctx, "GalaxyMetadata not sent");
  }
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: upload {} ({} bytes) + manifest ({} bytes)", hex12(ready_.save.sha256), ready_.save.size,
            ready_.manifest.size);
  x4mp::authority::UploadSpec spec;
  spec.checkpoint = checkpoint_;
  spec.files = {ready_.save, ready_.manifest};
  spec.ghosts_cleaned = ghosts_cleaned_;
  spec.step_timeout = seconds(60);
  if (!uploader_.start(std::move(spec))) return fail(ctx, "the upload job could not start (not connected?)");
  spawn_ship_ = collected_.ship();
  late_ship_ = false;
  ship_wait_ = false;  // this checkpoint decides again below (a newer collection supersedes an older ship question)
  ship_pending_ = false;
  spawn_plan_ = plan_;
  step_ = Step::Uploading;
}

void AuthorityFlow::step_uploading(host::HostContext& ctx) {
  const auto st = uploader_.stats();
  if (counted_stored_ < st.checkpoints_stored) {
    counted_stored_ = st.checkpoints_stored;
    on_checkpoint_stored(ctx);
    return;
  }
  if (const auto& r = uploader_.last_result();
      r && r->state == x4mp::authority::UploadState::Failed && !uploader_.pending() && !uploader_.running()) {
    fail(ctx, std::string("upload failed: ") + x4mp::authority::to_string(r->error) + " " + r->detail);
  }
}

void AuthorityFlow::on_checkpoint_stored(host::HostContext& ctx) {
  ++stored_total_;
  ++state_.checkpoints;
  state_.loaded_sha = ready_.save.sha256;  // this game is the source of that checkpoint
  {  // M3-22: the checkpoint joins the avatar lineage ledger: a later load of exactly this save keeps the avatars its manifest lists
    std::vector<std::string> idcodes;
    for (const auto& r : manifest_avatars_) {
      if (!r.idcode.empty()) idcodes.push_back(r.idcode);
    }
    avatars::avatar_hub().note_checkpoint(crypto::to_hex(std::span<const std::uint8_t>(ready_.save.sha256)), std::move(idcodes));
  }
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: checkpoint {} stored (save {}, request {})", save_name_, hex12(ready_.save.sha256), request_id_);
  std::error_code ec;
  fs::remove(ready_.manifest_path, ec);  // our own temporary manifest file
  if (!ledger_.empty()) {
    const auto removed = record_and_trim(ledger_, save_dir_, save_name_, kKeepCheckpointSaves);
    if (!removed.empty()) X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: removed {} old checkpoint save(s)", removed.size());
  }
  persist();
  step_ = Step::Idle;
  spawn_due_ = !state_.spawned;
}

void AuthorityFlow::maybe_spawn(host::HostContext& ctx) {
  if (state_.spawned) {
    spawn_due_ = false;
    return;
  }
  if (!spawn_ship_) {
    // Close-out A item 3: MD had no player ship when the checkpoint finished (the first one, ~2 s after the universe is ready:
    // player.occupiedship was still null; or the player stands in the cockpit after a load). Not a final answer: the spawn waits for the
    // pilot seat (M3-09). step_ship_wait asks MD once when the player sits (at once if they already do).
    spawn_due_ = false;
    if (!ship_wait_) {
      ship_wait_ = true;
      ship_pending_ = false;
      ship_asks_ = 0;
      ask_wait_frames_ = 0;
      ask_backoff_frames_ = 0;
      X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: no player ship was reported by MD with the checkpoint; the self-spawn waits for the pilot seat ({})",
                seated_ ? "the player sits: asking MD now" : "the player stands");
    }
    return;
  }
  if (!uploader_.connected()) return;  // sent when the link is back
  // The spawn happened "now": a late ship carries the game time of this moment (never 0); a ship of the checkpoint itself keeps the
  // checkpoint's game time.
  double spawn_time = game_time_;
  if (late_ship_) {
    const auto now_gt = ctx.game.game_time();
    if (now_gt && x4mp::authority::EntitySpawnBuilder::is_valid_game_time(*now_gt)) spawn_time = *now_gt;
  }
  if (!x4mp::authority::EntitySpawnBuilder::is_valid_game_time(spawn_time)) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Error, "authority: no valid game time for the self-spawn; not sent");
    spawn_due_ = false;
    return;
  }
  std::uint32_t macro_ref = spawn_plan_.ship_macro_ref;
  std::uint16_t ship_sector = spawn_plan_.ship_sector;
  if (!spawn_strings_fresh_ || late_ship_) {
    // the server's string table is the one sent with an earlier checkpoint: this plan's indices are not necessarily its indices
    macro_ref = late_ship_macro_ref(ctx, spawn_ship_->macro);
    ship_sector = 0;
    for (const auto& sec : spawn_plan_.sectors) {
      if (sec.macro == spawn_ship_->sector) ship_sector = sec.index;
    }
  }
  x4mp::authority::EntitySpawnBuilder builder(spawn_time);
  x4mp::authority::SpawnEntity self;
  self.net_id = state_.next_net_id;
  self.kind = spawn_kind_for_class(spawn_ship_->cls);
  // M3-14 (found by the two-DLL pair run): the host's ship is a PLAYER ship like every avatar. Without origin=PlayerShip and the controller the server
  // would not drive this entity from the host's PlayerState (the clients would see a frozen, unnamed ship, or none) and the roster would show
  // no ship for the host. The name is what the clients show as the ghost label.
  const std::uint16_t host_id = session_.welcome().player_id;
  std::string host_name;
  if (const auto* row = chat::chat_hub().roster().find(host_id)) host_name = row->name;
  self.origin = x4mp::authority::SpawnOrigin::PlayerShip;
  self.owner_player = host_id;
  self.controller_player = host_id;
  self.owner_team = session_.welcome().team_id;
  self.macro_ref = macro_ref;
  self.owner_ref = spawn_plan_.player_faction_ref;  // "player" is always the first string, index 1, in every table we send
  self.name = host_name.empty() ? spawn_ship_->name : "[MP] " + host_name;
  self.idcode = spawn_ship_->idcode;
  self.sector = ship_sector;
  (void)builder.add(std::move(self));
  const auto payload = builder.build();
  if (!payload) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Error, "authority: self-spawn refused: {}", x4mp::authority::to_string(payload.error()));
    spawn_due_ = false;
    return;
  }
  (void)send_control(T(P::MsgType::EntitySpawn), *payload);
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: self-spawn sent net_id={} game_time={:.3f}{}", state_.next_net_id, spawn_time,
            late_ship_ ? " (late: the ship was not known at the checkpoint)" : "");
  state_.spawned = true;
  state_.host_net_id = state_.next_net_id;  // the host's PlayerState carries it from now on (JoinFeature copies it to the selfship hub every frame)
  ++state_.next_net_id;
  spawn_due_ = false;
  ship_wait_ = false;
  persist();
}

// The ship macro as a string index the server knows: reuse one already sent, else append one (StringTableAdd with a single entry).
std::uint32_t AuthorityFlow::late_ship_macro_ref(host::HostContext& ctx, const std::string& macro) {
  for (const auto& [value, index] : sent_macros_) {
    if (value == macro) return index;
  }
  const std::uint32_t index = state_.string_count + 1;
  const std::vector<x4mp::authority::StringDesc> add = {{index, static_cast<std::uint8_t>(P::StringKind::Macro), macro}};
  if (!send_control(T(P::MsgType::StringTableAdd), x4mp::authority::encode_string_table_add(add))) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: could not add the ship macro to the string table");
    return 0;
  }
  state_.string_count = index;
  sent_macros_.emplace_back(macro, index);
  persist();
  return index;
}

void AuthorityFlow::note_seat(bool seated, std::uint32_t edges) noexcept {
  if (edges != seat_edges_) {  // a sit-down, stand-up or ship change: ask at the next opportunity, no back-off
    seat_edges_ = edges;
    ask_wait_frames_ = 0;
    ask_backoff_frames_ = 0;
  }
  seated_ = seated;
}

void AuthorityFlow::schedule_reask() noexcept {
  ask_backoff_frames_ = ask_backoff_frames_ == 0 ? kAskBackoffMin : std::min(ask_backoff_frames_ * 2, kAskBackoffMax);
  ask_wait_frames_ = ask_backoff_frames_;
}

// Item 3 / M3-09: the checkpoint is stored but MD reported no player ship. The seat decides: while the player stands nothing is asked
// (the sit-down edge asks); while they sit MD is asked once, then again only after a frame-counted back-off if it still says "none".
void AuthorityFlow::step_ship_wait(host::HostContext& ctx) {
  if (state_.spawned) {
    ship_wait_ = false;
    return;
  }
  if (!seated_) return;
  const auto now = Clock::now();
  if (ship_pending_) {
    if (now - ship_asked_ <= kShipAnswerTimeout) return;
    ship_pending_ = false;  // no answer at all: count it as "no ship"
    schedule_reask();
  }
  if (ask_wait_frames_ > 0) {
    --ask_wait_frames_;
    return;
  }
  if (!uploader_.connected()) return;
  ++ship_asks_;
  ship_pending_ = true;
  ship_asked_ = now;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: the player sits in the pilot seat: asking MD for the ship (ask {})", ship_asks_);
  if (!ctx.platform.raise_lua("x4mp.auth_collect", R"({"v":1,"ship_only":true})")) {
    ship_pending_ = false;
    ship_wait_ = false;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: the ship question could not be raised (Lua bridge missing)");
  }
}

}  // namespace x4mp::features::auth
