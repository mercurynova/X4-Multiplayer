#pragma once
// features/avatars/avatar_hub: the hand-over points of the authority's avatars (M3-11), like chat_hub()/team_hub():
//
//   join feature (hook lines in join_feature.cpp, main thread):  on_welcome / on_frame_message / session_ended
//       -> decoded PlayerShip, PlayerState, EntityDespawn, RosterUpdate and ServerSettingsUpdate wait here until the AvatarsFeature drains them
//   authority flow (features/authority):  set_services(...) while the node is the authority: net ids, string-table refs and the Control-lane
//       send come from it (one counter, one string table: the flow owns them); the flow asks for the manifest records at SaveGame time.
//   avatars feature:  set_snapshot_fn(...) so the flow can read the avatars' current poses.
//
// Main thread only.

#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "features/avatars/avatar_director.h"
#include "features/avatars/avatar_plan.h"
#include "features/avatars/avatar_wire.h"

namespace x4mp::features::avatars {

struct AuthorityServices {
  std::function<bool()> connected;                                            // welcomed as the authority
  std::function<std::uint32_t()> alloc_net_id;
  std::function<void(std::uint32_t)> reserve_net_ids_above;                    // the counter must be > this
  std::function<std::uint32_t(StrKind, const std::string&)> string_ref;       // 0 = could not
  std::function<bool(std::uint16_t type, std::vector<std::uint8_t> payload)> send_control;
};

// M3-12: what a CLIENT node's takeover needs from the session (installed by the join feature while the node is in game and not the authority).
struct ClientLink {
  std::function<std::uint16_t()> player_id;                                   // the own player id (0 = unknown)
  std::function<bool(std::uint16_t type, std::vector<std::uint8_t> payload)> send_control;
};

// M3-13: what the janitor / the checkpoint check must never remove on the authority: the avatars. Both lists are copies taken on the frame thread.
struct AvatarProtect {
  std::vector<std::uint64_t> ids;        // local ids the binder bound (void after a load until it runs again)
  std::vector<std::string> idcodes;      // idcodes of every avatar record (they survive a load, the ids do not)
};

// M3-13: a client's takeover as the janitor sees it. `active` = the node is a client in game (the client link exists).
struct TakeoverStatus {
  bool active = false;
  bool done = false;
  std::uint64_t avatar_id = 0;  // the local copy the player sits in (or will)
  std::uint64_t host_id = 0;    // the vacated host-ship copy until it is removed
};

struct HubInputs {
  std::vector<AvatarInfo> avatar_spawns;  // M3-12: EntitySpawn avatars (client)
  std::vector<RosterIn> rosters;
  std::vector<PlayerShipReq> ships;
  std::vector<PlayerStateIn> states;
  std::vector<DespawnIn> despawns;
  std::vector<SettingsIn> settings;
  bool welcomed = false;
  // M3-22: the lineage events in order (a save this node loads, a stored checkpoint).
  struct LineageEvent {
    enum class Kind : std::uint8_t { LoadedSave, Checkpoint } kind = Kind::LoadedSave;
    std::string text;                  // save sha hex
    std::vector<std::string> idcodes;  // Checkpoint: the manifest's avatars
  };
  std::vector<LineageEvent> lineage;
  bool session_ended = false;
};

class AvatarHub {
 public:
  static constexpr std::size_t kMaxQueue = 256;

  // ---- join feature ----
  void on_welcome() { inputs_.welcomed = true; }
  // M3-22: this node is about to LOAD this save (sha256 hex): not called when the running universe is kept.
  void note_loaded_save(std::string sha_hex) { inputs_.lineage.push_back({HubInputs::LineageEvent::Kind::LoadedSave, std::move(sha_hex), {}}); }
  // M3-22: a checkpoint of the session was stored (authority): the avatars its manifest lists.
  void note_checkpoint(std::string sha_hex, std::vector<std::string> idcodes) {
    inputs_.lineage.push_back({HubInputs::LineageEvent::Kind::Checkpoint, std::move(sha_hex), std::move(idcodes)});
  }
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id);
  void session_ended();

  // ---- join feature, client role (M3-12) ----
  // Installed while this node is in game as a client; also holds the PlayerState stream back (selfship hub) until the takeover lifts it.
  void set_client_link(ClientLink link);
  void clear_client_link();
  [[nodiscard]] bool client_linked() const noexcept { return static_cast<bool>(client_.send_control); }
  [[nodiscard]] const ClientLink& client_link() const noexcept { return client_; }
  // The checkpoint manifest this node downloaded (x4mp_<sha12>.x4mf in the save folder); the takeover reads its avatar entries. "" = none.
  void set_manifest_file(std::string path) { manifest_file_ = std::move(path); manifest_dirty_ = true; }
  [[nodiscard]] const std::string& manifest_file() const noexcept { return manifest_file_; }
  [[nodiscard]] bool take_manifest_dirty() noexcept { return std::exchange(manifest_dirty_, false); }

  // ---- authority flow ----
  void set_services(AuthorityServices s) { services_ = std::move(s); have_services_ = true; }
  void clear_services() {
    services_ = {};
    have_services_ = false;
  }
  [[nodiscard]] bool authority() const noexcept { return have_services_; }
  [[nodiscard]] const AuthorityServices& services() const noexcept { return services_; }
  // The records (current poses) for the checkpoint manifest, taken at SaveGame time. Empty without an avatars feature.
  void set_snapshot_fn(std::function<std::vector<Record>()> fn) { snapshot_fn_ = std::move(fn); }
  [[nodiscard]] std::vector<Record> manifest_records() const { return snapshot_fn_ ? snapshot_fn_() : std::vector<Record>{}; }
  void set_max_net_id(std::uint32_t id) noexcept { max_net_id_ = id; }
  [[nodiscard]] std::uint32_t max_net_id() const noexcept { return max_net_id_; }

  // ---- janitor / checkpoint check (M3-13) ----
  void set_protect_fn(std::function<AvatarProtect()> fn) { protect_fn_ = std::move(fn); }
  [[nodiscard]] AvatarProtect protect() const { return protect_fn_ ? protect_fn_() : AvatarProtect{}; }
  void set_takeover_status(const TakeoverStatus& s) noexcept { takeover_ = s; }
  [[nodiscard]] const TakeoverStatus& takeover_status() const noexcept { return takeover_; }

  // ---- avatars feature ----
  [[nodiscard]] HubInputs take_inputs();

  void reset();

 private:
  HubInputs inputs_;
  ClientLink client_;
  std::string manifest_file_;
  bool manifest_dirty_ = false;
  AuthorityServices services_;
  bool have_services_ = false;
  std::function<std::vector<Record>()> snapshot_fn_;
  std::uint32_t max_net_id_ = 0;
  std::function<AvatarProtect()> protect_fn_;
  TakeoverStatus takeover_;
};

[[nodiscard]] AvatarHub& avatar_hub() noexcept;

}  // namespace x4mp::features::avatars
