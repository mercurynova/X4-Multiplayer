#include "features/janitor/janitor_feature.h"

#include <algorithm>
#include <string>
#include <vector>

#include "features/avatars/avatar_hub.h"
#include "features/diag/diag_hub.h"
#include "features/ghosts/ghost_hub.h"
#include "game/game_api.h"
#include "game/main_thread.h"
#include "game/safe_remove.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
using NumFn = std::uint32_t (*)(const char* factionid);
using ListFn = std::uint32_t (*)(game::UniverseId* result, std::uint32_t resultlen, const char* factionid);
using NameFn = const char* (*)(game::UniverseId id);
using NumFactionsFn = std::uint32_t (*)(bool includehidden);
using FactionsFn = std::uint32_t (*)(const char** result, std::uint32_t resultlen, bool includehidden);

constexpr const char* kPendingKey = "janitor.pending";

struct Exports {
  NumFn num_ships = nullptr, num_stations = nullptr;
  ListFn ships = nullptr, stations = nullptr;
  NameFn name = nullptr;
  NumFactionsFn num_factions = nullptr;
  FactionsFn factions = nullptr;
  [[nodiscard]] bool complete() const { return num_ships && num_stations && ships && stations && name; }
};

Exports resolve(host::IPlatform& p) {
  Exports e;
  e.num_ships = reinterpret_cast<NumFn>(p.get_game_function("GetNumAllFactionShips"));
  e.num_stations = reinterpret_cast<NumFn>(p.get_game_function("GetNumAllFactionStations"));
  e.ships = reinterpret_cast<ListFn>(p.get_game_function("GetAllFactionShips"));
  e.stations = reinterpret_cast<ListFn>(p.get_game_function("GetAllFactionStations"));
  e.name = reinterpret_cast<NameFn>(p.get_game_function("GetComponentName"));
  e.num_factions = reinterpret_cast<NumFactionsFn>(p.get_game_function("GetNumAllFactions"));
  e.factions = reinterpret_cast<FactionsFn>(p.get_game_function("GetAllFactions"));
  return e;
}

// Close-out A item 6: GetNumAllFaction{Ships,Stations}("x4mp_team_N") wrote "Failed to retrieve faction with ID" to the game log (16 lines at
// every load) because the team factions are not defined yet. Only ask about factions the game lists (GetAllFactions, hidden ones included).
std::vector<std::string> existing_factions(const Exports& e) {
  std::vector<std::string> out;
  if (!e.num_factions || !e.factions) return out;
  const std::uint32_t n = e.num_factions(true);
  if (n == 0) return out;
  std::vector<const char*> ids(n, nullptr);
  const std::uint32_t got = e.factions(ids.data(), n, true);
  for (std::uint32_t i = 0; i < got && i < n; ++i) {
    if (ids[i]) out.emplace_back(ids[i]);
  }
  return out;
}

struct PassState {
  host::HostContext& ctx;
  const JanitorProtection& prot;
  bool do_remove = false;
  std::unordered_set<std::uint64_t>* acted = nullptr;  // ids already handed to safe_remove (or refused): never acted on twice
  std::uint32_t removed_this_frame = 0;
  std::uint32_t cap = 0;
  JanitorResult r;
};

void scan_list(const Exports& e, NumFn num, ListFn list, const std::string& faction, PassState& st) {
  JanitorResult& r = st.r;
  if (r.scanned >= JanitorFeature::kMaxObjects) return;
  const std::uint32_t n = num(faction.c_str());
  if (n == 0) return;
  std::vector<game::UniverseId> ids(n);
  const std::uint32_t got = list(ids.data(), n, faction.c_str());
  const bool team_owned = faction.starts_with("x4mp_team_");
  for (std::uint32_t i = 0; i < got && i < n && r.scanned < JanitorFeature::kMaxObjects; ++i) {
    const game::UniverseId id = ids[i];
    const char* nm = e.name(id);  // valid until the next call: inspect (copy) immediately
    const std::string name = nm ? nm : "";
    ++r.scanned;
    if (name.starts_with(kGhostNamePrefix)) ++r.marked;
    const auto verdict = janitor_decide(
        id, name, team_owned, [&st, id]() { return st.ctx.game.object_id_code(id).value_or(std::string{}); }, st.prot);
    switch (verdict) {
      case JanitorVerdict::NotOurs: break;
      case JanitorVerdict::KeepGhost: ++r.kept_ghost; break;
      case JanitorVerdict::KeepAvatar: ++r.kept_avatar; break;
      case JanitorVerdict::KeepOwn: ++r.kept_own; break;
      case JanitorVerdict::KeepGuarded: ++r.kept_guarded; break;
      case JanitorVerdict::Remove: {
        if (st.acted && st.acted->contains(id)) break;  // the game may keep listing a removed object for a frame
        ++r.leftovers;
        if (!st.do_remove || st.removed_this_frame >= st.cap) break;
        const auto res = game::safe_remove(id);
        if (st.acted) st.acted->insert(id);
        ++st.removed_this_frame;
        if (res == game::RemoveResult::Removed) {
          ++r.removed;
        } else {
          ++r.refused;
        }
        break;
      }
    }
  }
}

JanitorProtection build_protection(host::HostContext&) {
  JanitorProtection p;
  p.is_ghost = [](std::uint64_t id) { return ghosts::ghost_hub().is_ghost_local(id); };
  p.is_guarded = [](std::uint64_t id) { return game::is_player_guarded(id); };
  const auto& hub = avatars::avatar_hub();
  const auto ap = hub.protect();
  p.avatar_ids.insert(ap.ids.begin(), ap.ids.end());
  p.avatar_idcodes.insert(ap.idcodes.begin(), ap.idcodes.end());
  const auto& ts = hub.takeover_status();
  if (ts.avatar_id != 0) p.own_ids.insert(ts.avatar_id);
  if (ts.host_id != 0) p.own_ids.insert(ts.host_id);
  // A client's team-owned objects are all leftovers; a node that is not (yet) a client in game could be the host of the save, so only the
  // name prefix decides there.
  p.remove_team_owned = ts.active && !hub.authority();
  return p;
}

JanitorResult run_pass(host::HostContext& ctx, const JanitorProtection& prot, bool do_remove, std::unordered_set<std::uint64_t>* acted) {
  JanitorResult none;
  if (!game::assert_main_thread("janitor")) return none;
  const Exports e = resolve(ctx.platform);
  if (!e.complete()) {
    none.exports_missing = true;
    return none;
  }
  PassState st{ctx, prot, do_remove, acted, 0, JanitorFeature::kMaxRemovePerFrame, {}};
  st.r.ran = true;
  std::vector<std::string> factions{"player"};
  const auto known = existing_factions(e);
  for (int i = 1; i <= 8; ++i) {
    const std::string team = "x4mp_team_" + std::to_string(i);
    if (std::find(known.begin(), known.end(), team) != known.end()) factions.push_back(team);
  }
  for (const auto& f : factions) {
    scan_list(e, e.num_ships, e.ships, f, st);
    scan_list(e, e.num_stations, e.stations, f, st);
    ++st.r.factions_queried;
  }
  return st.r;
}

void emit(host::HostContext& ctx, Level level, const std::string& text) {
  ctx.log.raw(Cat::Ghost, level, "janitor: " + text);
  diag_hub().forward_log(level, "[janitor] " + text);  // false when not connected: the local line is written
}
}  // namespace

void JanitorFeature::on_init(host::HostContext& ctx) {
  std::uint32_t size = 0;
  const void* p = ctx.platform.stash_get(kPendingKey, &size);
  carried_pending_ = p != nullptr && size == 1 && *static_cast<const char*>(p) == '1';
}

void JanitorFeature::on_shutdown(host::HostContext& ctx) {
  const char flag = pending_.load() ? '1' : '0';
  (void)ctx.platform.stash_set(kPendingKey, &flag, 1);
}

void JanitorFeature::on_universe_ready(host::HostContext& ctx) {
  acted_.clear();
  total_ = {};
  passes_ = 0;
  grace_s_ = 0.0;
  wait_logged_ = false;
  swept_ = false;
  if (ctx.gates.universe_ready_after_reload) {
    // /reloadui: the same universe. Ghosts and avatars come back from the stash; nothing is swept (a sweep that was still waiting is kept).
    pending_ = carried_pending_;
    ctx.log.raw(Cat::Ghost, Level::Info,
                pending_ ? "janitor: /reloadui, the sweep of this universe is still waiting (kept)" : "janitor: /reloadui, same universe: no sweep");
    return;
  }
  pending_ = true;
}

void JanitorFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  if (!pending_.load()) return;
  if (!info.universe_ready) return;
  const auto& hub = avatars::avatar_hub();
  const auto& ts = hub.takeover_status();
  bool go = false;
  const char* waiting_for = nullptr;
  if (ts.active) {  // a client in game: its takeover needs the save's avatar copy and the host copy until it is Done
    go = ts.done;
    waiting_for = "the takeover to finish";
  } else if (diag_hub().role() == NodeRole::Authority && diag_hub().connected()) {
    go = true;  // the authority's avatars are protected by their records, nothing else waits
  } else if (diag_hub().session_pending()) {
    grace_s_ = 0.0;
    waiting_for = "the session (connecting, loading the session save)";
  } else {
    grace_s_ += info.delta_s;
    go = grace_s_ >= kStandaloneGraceS;
    waiting_for = "the standalone grace period (no session)";
  }
  if (!go) {
    if (!wait_logged_ && waiting_for) {
      wait_logged_ = true;
      ctx.log.raw(Cat::Ghost, Level::Info, std::string("janitor: waiting for ") + waiting_for + " before sweeping '[MP] ' leftovers");
    }
    return;
  }

  const JanitorProtection prot = build_protection(ctx);
  const JanitorResult pass = run_pass(ctx, prot, /*do_remove=*/true, &acted_);
  last_ = pass;
  if (pass.exports_missing) {
    pending_ = false;
    swept_ = true;
    last_.exports_missing = true;
    emit(ctx, Level::Info, "skipped (the game exports needed to list objects are not available)");
    return;
  }
  if (!pass.ran) return;  // wrong thread: try again next frame
  ++passes_;
  if (passes_ == 1) {
    total_.ran = true;
    total_.scanned = pass.scanned;
    total_.marked = pass.marked;
    total_.factions_queried = pass.factions_queried;
    total_.kept_ghost = pass.kept_ghost;
    total_.kept_avatar = pass.kept_avatar;
    total_.kept_own = pass.kept_own;
    total_.kept_guarded = pass.kept_guarded;
  }
  total_.leftovers += pass.leftovers;
  total_.removed += pass.removed;
  total_.refused += pass.refused;
  if (pass.leftovers == 0 || passes_ >= kMaxPasses) {  // nothing new this pass: the sweep is over
    pending_ = false;
    swept_ = true;
    const bool bad = total_.removed > 0 || total_.refused > 0;
    emit(ctx, bad ? Level::Warn : Level::Info,
         "swept: " + std::to_string(total_.removed) + " leftover '[MP] ' object(s) removed, " + std::to_string(total_.refused) + " refused, among " +
             std::to_string(total_.scanned) + " scanned (" + std::to_string(total_.marked) + " carry the prefix); kept: ghosts " +
             std::to_string(total_.kept_ghost) + ", avatars " + std::to_string(total_.kept_avatar) + ", own copy " + std::to_string(total_.kept_own) +
             ", guarded " + std::to_string(total_.kept_guarded) + "; " + std::to_string(passes_) + " pass(es)");
  }
}

JanitorResult JanitorFeature::scan(host::HostContext& ctx) {
  const JanitorProtection prot = build_protection(ctx);
  return run_pass(ctx, prot, /*do_remove=*/false, nullptr);
}

CheckpointHygiene JanitorFeature::checkpoint_check(host::HostContext& ctx) {
  CheckpointHygiene h;
  JanitorProtection prot = build_protection(ctx);
  prot.remove_team_owned = false;  // the authority never removes a team-owned ship for lacking the name prefix
  h.ghost_registry = static_cast<std::uint32_t>(ghosts::ghost_hub().ghost_count());
  std::unordered_set<std::uint64_t> acted;
  JanitorResult total;
  for (std::uint32_t pass = 0; pass < kMaxPasses; ++pass) {
    const JanitorResult r = run_pass(ctx, prot, /*do_remove=*/true, &acted);
    if (!r.ran) {
      if (pass == 0) {
        h.ran = false;
        h.clean = h.ghost_registry == 0;
        const std::string why = r.exports_missing ? "the game exports needed to list objects are not available" : "not on the main thread";
        emit(ctx, Level::Warn, "checkpoint check: cannot verify the authority universe (" + why + ")");
        return h;
      }
      break;
    }
    if (pass == 0) {
      h.ran = true;
      h.avatars_kept = r.kept_avatar;
    }
    total.leftovers += r.leftovers;
    total.removed += r.removed;
    total.refused += r.refused;
    if (r.leftovers == 0) break;
  }
  h.stale_found = total.leftovers;
  h.removed = total.removed;
  h.remaining = total.refused;
  h.clean = h.remaining == 0 && h.ghost_registry == 0;
  const bool noteworthy = h.stale_found > 0 || h.ghost_registry > 0;
  emit(ctx, noteworthy ? (h.clean ? Level::Warn : Level::Error) : Level::Info,
       "checkpoint check: " + std::to_string(h.stale_found) + " stale '[MP] ' object(s) in the authority universe (" + std::to_string(h.removed) +
           " removed, " + std::to_string(h.remaining) + " could not be removed), ghost registry " + std::to_string(h.ghost_registry) + ", " +
           std::to_string(h.avatars_kept) + " avatar(s) kept: ghosts_cleaned=" + (h.clean ? "true" : "false"));
  return h;
}

}  // namespace x4mp::features
