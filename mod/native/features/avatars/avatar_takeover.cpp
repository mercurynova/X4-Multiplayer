#include "features/avatars/avatar_takeover.h"

#include <algorithm>
#include <charconv>
#include <cstdlib>
#include <format>
#include <sstream>

namespace x4mp::features::avatars {

namespace {
constexpr std::string_view kRecordHeader = "x4tk 1";

bool is_team_owner(std::string_view owner) { return owner.substr(0, kTeamFactionPrefix.size()) == kTeamFactionPrefix; }
bool is_avatar_name(std::string_view name) { return name.substr(0, kAvatarNamePrefix.size()) == kAvatarNamePrefix; }

std::string clean(std::string_view s) {
  std::string out;
  for (const char c : s) out += (c == '|' || c == ',' || c == ';' || c == '\n' || c == '\r') ? ' ' : c;
  return out;
}
std::vector<std::string_view> split(std::string_view s, char sep) {
  std::vector<std::string_view> out;
  std::size_t pos = 0;
  while (true) {
    const auto i = s.find(sep, pos);
    if (i == std::string_view::npos) {
      out.push_back(s.substr(pos));
      break;
    }
    out.push_back(s.substr(pos, i - pos));
    pos = i + 1;
  }
  return out;
}
template <class T>
bool to_uint(std::string_view s, T& out) {
  if (s.empty()) return false;
  const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
  return r.ec == std::errc{} && r.ptr == s.data() + s.size();
}
}  // namespace

// ---- record ---------------------------------------------------------------------------------------------------------------------
std::string takeover_record_to_text(const TakeoverRecord& r) {
  std::ostringstream o;
  o << kRecordHeader << '\n';
  o << static_cast<int>(r.phase) << '|' << r.player_id << '|' << r.net_id << '|' << clean(r.idcode) << '|' << r.avatar_id << '|' << r.host_id << '|' << clean(r.host_idcode) << '|';
  for (std::size_t i = 0; i < r.remove.size(); ++i) o << (i ? ";" : "") << r.remove[i].id << ',' << clean(r.remove[i].idcode);
  o << '\n';
  return o.str();
}

std::optional<TakeoverRecord> takeover_record_from_text(std::string_view text) {
  const auto nl = text.find('\n');
  if (nl == std::string_view::npos || text.substr(0, nl) != kRecordHeader) return std::nullopt;
  auto line = text.substr(nl + 1);
  while (!line.empty() && (line.back() == '\n' || line.back() == '\r')) line.remove_suffix(1);
  const auto f = split(line, '|');
  if (f.size() != 8) return std::nullopt;
  TakeoverRecord r;
  int phase = 0;
  if (!to_uint(f[0], phase) || phase < 0 || phase > 3) return std::nullopt;
  r.phase = static_cast<TakeoverRecord::Phase>(phase);
  if (!to_uint(f[1], r.player_id) || !to_uint(f[2], r.net_id) || !to_uint(f[4], r.avatar_id) || !to_uint(f[5], r.host_id)) return std::nullopt;
  r.idcode = std::string(f[3]);
  r.host_idcode = std::string(f[6]);
  if (!f[7].empty()) {
    for (const auto item : split(f[7], ';')) {
      const auto c = item.find(',');
      TakeoverRecord::Pending p;
      if (c == std::string_view::npos || !to_uint(item.substr(0, c), p.id)) return std::nullopt;
      p.idcode = std::string(item.substr(c + 1));
      r.remove.push_back(std::move(p));
    }
  }
  return r;
}

// ---- helpers ----------------------------------------------------------------------------------------------------------------------
std::optional<std::uint64_t> pick_avatar_copy(const AvatarInfo& g, std::string_view expected_owner, const std::vector<Candidate>& cands, std::uint64_t exclude) {
  const Candidate* best = nullptr;
  int best_tier = 99;
  int best_owner = 1;
  double best_d = 0;
  for (const auto& c : cands) {
    if (c.id == 0 || c.id == exclude || !is_team_owner(c.owner)) continue;
    const bool id_eq = !g.idcode.empty() && c.idcode == g.idcode;
    const bool name_eq = !g.name.empty() && c.name == g.name;
    int tier = 99;
    if (id_eq && name_eq) tier = 0;
    else if (id_eq) tier = 1;
    else if (name_eq) tier = 2;
    if (tier == 99) continue;
    const int owner_rank = (!expected_owner.empty() && c.owner == expected_owner) ? 0 : 1;
    const double d = distance_m(c.pose, g.pose);
    const bool better = !best || tier < best_tier || (tier == best_tier && (owner_rank < best_owner || (owner_rank == best_owner && (d < best_d || (d == best_d && c.id < best->id)))));
    if (better) {
      best = &c;
      best_tier = tier;
      best_owner = owner_rank;
      best_d = d;
    }
  }
  if (!best) return std::nullopt;
  return best->id;
}

double teleport_backoff_s(int tries) noexcept {
  static constexpr double kTable[] = {2, 2, 2, 3, 3, 4, 4, 5, 5, 5};
  if (tries < 1) return kTable[0];
  if (tries > AvatarTakeover::kMaxTeleportTries) return 20.0;
  return kTable[tries - 1];
}

const char* AvatarTakeover::stage_name(Stage s) noexcept {
  switch (s) {
    case Stage::Idle: return "idle";
    case Stage::Requesting: return "requesting";
    case Stage::Locating: return "locating";
    case Stage::Teleporting: return "teleporting";
    case Stage::Confirming: return "confirming";
    case Stage::Removing: return "removing";
    case Stage::Done: return "done";
  }
  return "?";
}

// ---- inputs -----------------------------------------------------------------------------------------------------------------------
void AvatarTakeover::on_spawn_avatars(const std::vector<AvatarInfo>& avatars) {
  for (const auto& a : avatars) {
    if (a.net_id == 0) continue;
    seen_[a.net_id] = a;
    if (grant_ && a.net_id == grant_->net_id && a.idcode != grant_->idcode && !a.idcode.empty() && stage_ <= Stage::Locating) grant_ = a;  // the authority refreshed it
  }
}

void AvatarTakeover::restart() {
  ++stats_.restarts;
  set_hint(0, false);
  stage_ = Stage::Idle;
  grant_.reset();
  avatar_id_ = host_id_ = 0;
  avatar_idcode_.clear();
  host_idcode_.clear();
  tries_ = frames_ok_ = 0;
  owner_set_ = false;
  todo_.clear();
  held_ = false;  // the feature (join hook) holds again with every new link
  // loaded_ stays: a record read at init is validated by idcode at the first ready step (a new universe fails that check and is dropped).
}

void AvatarTakeover::session_ended() {
  set_hint(0, false);
  stage_ = Stage::Idle;
  grant_.reset();
  seen_.clear();
  avatar_id_ = host_id_ = 0;
  tries_ = frames_ok_ = 0;
  owner_set_ = false;
  todo_.clear();
  held_ = false;
}

// ---- helpers on the state -----------------------------------------------------------------------------------------------------------
std::string AvatarTakeover::expected_owner() const { return grant_ ? env_.faction_of_team(grant_->owner_team) : std::string{}; }

bool AvatarTakeover::known_other_idcode(const std::string& idcode) const {
  if (idcode.empty()) return false;
  for (const auto& [id, a] : seen_) {
    if (a.owner_player != player_ && a.controller_player != player_ && a.idcode == idcode) return true;
  }
  for (const auto& a : manifest_) {
    if (a.owner_player != player_ && a.controller_player != player_ && a.idcode == idcode) return true;
  }
  return false;
}

void AvatarTakeover::set_hint(double now_s, bool show) {
  if (show) {
    env_.show_hint(true, std::string(kTakeoverHint));
    if (!hint_shown_) ++stats_.hints;
    hint_shown_ = true;
    next_hint_ = now_s + kHintRepeatS;
  } else if (hint_shown_) {
    env_.show_hint(false, {});
    hint_shown_ = false;
  }
}

void AvatarTakeover::persist(TakeoverRecord::Phase phase) {
  TakeoverRecord r;
  r.phase = phase;
  r.player_id = player_;
  r.net_id = grant_ ? grant_->net_id : 0;
  r.idcode = avatar_idcode_;
  r.avatar_id = avatar_id_;
  r.host_id = host_id_;
  r.host_idcode = host_idcode_;
  for (const auto& t : todo_) r.remove.push_back({t.id, t.idcode});
  env_.save_record(takeover_record_to_text(r));
}

// ---- the machine ------------------------------------------------------------------------------------------------------------------------
void AvatarTakeover::step(double now_s) {
  if (stage_ == Stage::Done) return;
  if (!env_.ready()) return;
  const auto player = env_.own_player_id();
  if (player == 0) return;
  if (diag_.takeover_off) {  // M3-23: no takeover at all; the player stays in the save's ship. Done at once (nothing waits for it forever).
    env_.hold_states(true);   // the own ship is the host's: it must never move the avatar
    env_.hold_ghosts(false);
    held_ = false;
    stage_ = Stage::Done;
    log(LogLevel::Warn, std::format("diag.takeover_off: no takeover; the player stays in the save's ship {} (no avatar, no removal)", env_.own_ship()));
    env_.probe("takeover_off: nothing done");
    return;
  }
  switch (stage_) {
    case Stage::Idle: {
      const auto ship = env_.own_ship();
      if (ship == 0) return;
      start(now_s, player, ship);
      break;
    }
    case Stage::Requesting: step_requesting(now_s, player); break;
    case Stage::Locating: step_locating(now_s); break;
    case Stage::Teleporting: step_teleporting(now_s); break;
    case Stage::Confirming: step_confirming(now_s); break;
    case Stage::Removing: step_removing(now_s); break;
    case Stage::Done: break;
  }
}

void AvatarTakeover::start(double now_s, std::uint16_t player, std::uint64_t ship) {
  player_ = player;
  started_ = now_s;
  if (!held_) {
    env_.hold_ghosts(true);
    env_.hold_states(true);  // until the avatar is taken over the own ship is the host's: it must not move the avatar (m3-plan 4.3 step 6)
    held_ = true;
  }
  last_sit_downs_ = env_.sit_downs();
  tries_ = 0;
  frames_ok_ = 0;
  owner_set_ = false;
  if (loaded_ && resume_from_record(now_s, player, ship)) return;
  loaded_.reset();
  host_id_ = ship;
  host_idcode_ = env_.idcode(ship);
  stage_ = Stage::Requesting;
  next_action_ = now_s;
  stage_since_ = now_s;
  log(LogLevel::Info, std::format("standing in ship {} ({}): requesting the avatar", ship, host_idcode_));
}

bool AvatarTakeover::resume_from_record(double now_s, std::uint16_t player, std::uint64_t ship) {
  const TakeoverRecord rec = *loaded_;
  loaded_.reset();
  if (rec.phase == TakeoverRecord::Phase::None || rec.player_id != player || rec.avatar_id == 0) return false;
  if (!env_.valid(rec.avatar_id) || env_.idcode(rec.avatar_id) != rec.idcode) return false;  // another universe: the local ids are void
  avatar_id_ = rec.avatar_id;
  avatar_idcode_ = rec.idcode;
  AvatarInfo g;
  g.net_id = rec.net_id;
  g.owner_player = player;
  g.controller_player = player;
  g.idcode = rec.idcode;
  grant_ = g;
  owner_set_ = true;
  if (rec.host_id != 0 && rec.host_id != avatar_id_ && env_.valid(rec.host_id) && env_.idcode(rec.host_id) == rec.host_idcode) {
    host_id_ = rec.host_id;
    host_idcode_ = rec.host_idcode;
  } else if (ship != avatar_id_) {
    host_id_ = ship;
    host_idcode_ = env_.idcode(ship);
  }
  const bool seated = env_.seated_ship() == avatar_id_;
  log(LogLevel::Info, std::format("record found (phase {}, avatar {} net_id {}): {}", static_cast<int>(rec.phase), avatar_id_, rec.net_id, seated ? "the player is in it" : "the player is not in it"));
  stage_since_ = now_s;
  if (rec.phase == TakeoverRecord::Phase::Done) {
    env_.set_own_net_id(rec.net_id);
    env_.hold_states(false);
    env_.hold_ghosts(false);
    held_ = false;
    stage_ = Stage::Done;
    return true;
  }
  if (rec.phase == TakeoverRecord::Phase::Seated) {
    env_.set_own_net_id(rec.net_id);
    env_.hold_states(false);
    held_ = false;
    for (const auto& p : rec.remove) {
      if (p.id != avatar_id_ && env_.valid(p.id) && env_.idcode(p.id) == p.idcode) todo_.push_back({p.id, p.idcode, 0});
    }
    stage_ = Stage::Removing;
    return true;
  }
  // Progress
  if (seated) {
    stage_ = Stage::Confirming;
    frames_ok_ = 0;
    return true;
  }
  stage_ = Stage::Teleporting;
  next_action_ = now_s;
  return true;
}

void AvatarTakeover::step_requesting(double now_s, std::uint16_t player) {
  for (const auto& [id, a] : seen_) {
    if (a.owner_player == player || a.controller_player == player) {
      grant_ = a;
      break;
    }
  }
  if (grant_) {
    ++stats_.grants;
    log(LogLevel::Info, std::format("avatar granted: net_id={} idcode={} name='{}' sector {} after {:.1f} s", grant_->net_id, grant_->idcode, grant_->name, grant_->sector, now_s - started_));
    stage_ = Stage::Locating;
    stage_since_ = now_s;
    next_action_ = now_s;
    return;
  }
  if (now_s < next_action_) return;
  if (env_.send_request(host_id_)) {
    ++stats_.requests;
    next_action_ = now_s + kRequestEveryS;
    if (stats_.requests > 1 && (stats_.requests - 1) % 3 == 0) log(LogLevel::Warn, std::format("no avatar yet after {:.0f} s ({} requests sent): is there an authority?", now_s - started_, stats_.requests));
  } else {
    next_action_ = now_s + 1.0;  // the link refused it (outbox full / not welcomed): try again soon
  }
}

void AvatarTakeover::step_locating(double now_s) {
  if (now_s < next_action_) return;
  if (!grant_) {
    stage_ = Stage::Requesting;
    return;
  }
  if (avatar_id_ == 0 || !env_.valid(avatar_id_)) {
    avatar_id_ = 0;
    owner_set_ = false;
    const auto cands = env_.team_candidates();
    if (!cands) {  // never spawn on an enumeration that is not there: a duplicate copy is worse than waiting
      next_action_ = now_s + kLocateRetryS;
      if (now_s >= next_log_) {
        next_log_ = now_s + kLocateGiveUpLogS;
        log(LogLevel::Warn, "the team ship list is not available: the local copy of the avatar cannot be searched yet");
      }
      return;
    }
    const auto pick = pick_avatar_copy(*grant_, expected_owner(), *cands, host_id_);
    if (pick) {
      avatar_id_ = *pick;
      ++stats_.bound;
      log(LogLevel::Info, std::format("the avatar is in the loaded save: local ship {} (bound by idcode/name)", avatar_id_));
    } else {
      const auto sector = env_.sector_id_of_index(grant_->sector);
      if (sector == 0) {
        next_action_ = now_s + kLocateRetryS;
        if (now_s >= next_log_) {
          next_log_ = now_s + kLocateGiveUpLogS;
          log(LogLevel::Warn, std::format("the avatar's sector index {} is not in the local sector map yet", grant_->sector));
        }
        return;
      }
      const std::string macro = resolve_starter(settings_, grant_->owner_team).macro;
      const auto id = env_.spawn(macro, sector, grant_->pose, "player");
      if (id == 0) {
        ++stats_.spawn_failed;
        ++tries_;
        next_action_ = now_s + teleport_backoff_s(tries_);
        log(LogLevel::Warn, std::format("spawning the local copy of the avatar failed (try {}, macro {})", tries_, macro));
        return;
      }
      avatar_id_ = id;
      owner_set_ = true;  // spawned as "player"
      ++stats_.spawned;
      log(LogLevel::Info, std::format("the avatar is not in the loaded save: spawned a local copy, ship {} ({})", id, macro));
    }
  }
  avatar_idcode_ = env_.idcode(avatar_id_);
  const bool was_owned = owner_set_;
  if (!owner_set_) {
    env_.set_owner(avatar_id_, "player");  // the team copy of the save becomes the player's own ship (keeps its loadout)
    owner_set_ = true;
  }
  env_.activate(avatar_id_, true);
  env_.probe(was_owned ? "takeover: avatar spawned" : "takeover: avatar bound from the save");
  stage_ = Stage::Teleporting;
  stage_since_ = now_s;
  next_action_ = now_s;
  tries_ = 0;
  persist(TakeoverRecord::Phase::Progress);
}

void AvatarTakeover::refused(double now_s, const std::string& reason) {
  ++tries_;
  ++stats_.refusals;
  if (tries_ <= 3 || tries_ % 5 == 0) log(LogLevel::Warn, std::format("teleport into ship {} refused (try {}): {}", avatar_id_, tries_, reason));
  if (tries_ >= kHintAfterRefusals) set_hint(now_s, true);
  next_action_ = now_s + teleport_backoff_s(tries_);
}

void AvatarTakeover::step_teleporting(double now_s) {
  const auto sit = env_.sit_downs();
  const bool sat = sit != last_sit_downs_;
  last_sit_downs_ = sit;
  if (hint_shown_ && now_s >= next_hint_) set_hint(now_s, true);  // the hint is repeated while the refusal lasts
  if (now_s < next_action_ && !(sat && tries_ > 0)) return;       // a sit-down is what the hint asks for: retry at once
  if (!env_.valid(avatar_id_)) {
    log(LogLevel::Warn, "the avatar copy is gone: locating it again");
    avatar_id_ = 0;
    stage_ = Stage::Locating;
    next_action_ = now_s;
    return;
  }
  const auto answer = env_.can_teleport(avatar_id_);
  if (!answer) {
    refused(now_s, "CanTeleportPlayerTo is not available");
    return;
  }
  if (*answer != "granted") {
    refused(now_s, answer->empty() ? std::string("no reason given") : *answer);
    return;
  }
  if (!env_.teleport(avatar_id_)) {
    refused(now_s, "TeleportPlayerTo returned false");
    return;
  }
  ++stats_.teleports;
  stage_ = Stage::Confirming;
  stage_since_ = now_s;
  frames_ok_ = 0;
  log(LogLevel::Info, std::format("teleported the player into ship {} (try {}); waiting for the guard", avatar_id_, tries_ + 1));
  env_.probe("takeover: teleported");
}

void AvatarTakeover::step_confirming(double now_s) {
  if (!env_.valid(avatar_id_)) {
    log(LogLevel::Warn, "the avatar copy vanished while confirming: locating it again");
    avatar_id_ = 0;
    stage_ = Stage::Locating;
    next_action_ = now_s;
    frames_ok_ = 0;
    return;
  }
  if (env_.seated_ship() == avatar_id_) {
    if (++frames_ok_ >= kGuardFrames) begin_removal(now_s);
    return;
  }
  if (frames_ok_ > 0) ++stats_.guard_resets;
  frames_ok_ = 0;
  if (now_s - stage_since_ > kConfirmTimeoutS) {
    stage_ = Stage::Teleporting;
    refused(now_s, "the player is not in the avatar 10 s after the teleport");
  }
}

void AvatarTakeover::begin_removal(double now_s) {
  set_hint(now_s, false);
  env_.show_hint(false, {});  // always, once: a hint shown by an earlier DLL instance (a /reloadui in between) must not stay on screen
  env_.set_own_net_id(grant_->net_id);  // PlayerState carries the avatar's id from now on
  env_.hold_states(false);
  held_ = false;
  todo_.clear();
  const auto add = [&](std::uint64_t id, const std::string& code) {
    if (id == 0 || id == avatar_id_) return;
    if (std::any_of(todo_.begin(), todo_.end(), [&](const Todo& t) { return t.id == id; })) return;
    todo_.push_back({id, code, 0});
  };
  if (host_id_ != 0 && host_id_ != avatar_id_ && env_.valid(host_id_)) {
    if (diag_.takeover_keep_original) log(LogLevel::Warn, std::format("diag.takeover_keep_original: the host ship copy {} is kept (not removed)", host_id_));
    else add(host_id_, env_.idcode(host_id_));
  }
  if (const auto cands = env_.team_candidates()) {
    for (const auto& c : *cands) {
      if (c.id == avatar_id_ || c.id == host_id_ || !is_team_owner(c.owner)) continue;
      const bool named = !manifest_loaded_ && is_avatar_name(c.name);  // the name is only the fallback for a missing manifest
      const bool listed = known_other_idcode(c.idcode);  // the EntitySpawns and the checkpoint manifest name it
      if (named || listed) add(c.id, c.idcode);
    }
  } else {
    log(LogLevel::Warn, "the team ship list is not available: only the host ship copy is removed");
  }
  persist(TakeoverRecord::Phase::Seated);
  stage_ = Stage::Removing;
  stage_since_ = now_s;
  log(LogLevel::Info,
      std::format("the guard confirmed ({} frames, {:.2f} s after the request started): player in ship {}, net_id {}; {} local copies to remove", frames_ok_, now_s - started_,
                  avatar_id_, grant_->net_id, todo_.size()));
  env_.probe("takeover: guard confirmed");
}

void AvatarTakeover::step_removing(double now_s) {
  if (todo_.empty()) {
    finish(now_s);
    return;
  }
  if (env_.seated_ship() != avatar_id_) {  // never remove anything while the player is not in the avatar
    if (now_s - stage_since_ > kRemoveWaitS) {
      log(LogLevel::Warn, std::format("the player is not in the avatar: {} local copies are left in place", todo_.size()));
      todo_.clear();
      finish(now_s);
    }
    return;
  }
  stage_since_ = now_s;
  int calls = 0;
  for (auto it = todo_.begin(); it != todo_.end() && calls < kRemovePerFrame;) {
    if (!env_.valid(it->id)) {
      it = todo_.erase(it);
      continue;
    }
    ++calls;
    if (env_.remove(it->id)) {
      ++stats_.removed;
      log(LogLevel::Info, std::format("removed the local copy {} ({})", it->id, it->idcode));
      it = todo_.erase(it);
    } else if (++it->tries >= kRemoveFrames) {
      ++stats_.remove_refused;
      log(LogLevel::Warn, std::format("SafeRemove refused the local copy {} {} times: left in place", it->id, it->tries));
      it = todo_.erase(it);
    } else {
      ++it;
    }
  }
}

void AvatarTakeover::finish(double now_s) {
  persist(TakeoverRecord::Phase::Done);
  env_.hold_ghosts(false);  // the copies are gone: ghosts (the host's included) may appear now
  stage_ = Stage::Done;
  log(LogLevel::Info, std::format("done in {:.2f} s: avatar ship {} net_id {}, {} local copies removed, {} refused, {} teleport refusals", now_s - started_, avatar_id_,
                                  grant_ ? grant_->net_id : 0, stats_.removed, stats_.remove_refused, stats_.refusals));
  env_.probe(diag_.takeover_keep_original ? "takeover: original removal skipped (diag)" : "takeover: original removed");
}

}  // namespace x4mp::features::avatars
