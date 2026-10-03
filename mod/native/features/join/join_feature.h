#pragma once
// features/join: the client join flow (M2-06; docs/m2-plan.md 5.3, docs/mod-design.md section 7 "M2 bridge contract").
//
//   Lua x4mp.join -> core Session (net thread inside) -> SessionSaveInfo -> in-band download into GetSaveFolderPath() as
//   x4mp_<sha12>.xml.gz (verified before the rename) -> ReloadSaveList / wait / IsSaveValid -> vanilla Lua event `loadSave`
//   (raised from here, exactly what session 2 proved) -> X4 loads; the DLL is unloaded and re-initialised (stash survives) ->
//   resume -> on_universe_ready -> LoadStatus Matching, count-only ManifestReport, NodeReady{epoch} -> InGame.
//
// Threading: everything runs on the on_frame_update thread. Lua verb handlers (they run inside the Lua call) only copy their text
// into a mutex-protected inbox that on_frame drains; on_game_loaded / on_universe_ready only set atomic flags that on_frame acts
// on (the host may deliver them off the frame thread). The game is never paused by this feature (session 2: our Pause/Unpause
// fought the player's own Esc pause).
//
// Plug-in points for later tasks (search for "M2-07" / "M2-09" in join_feature.cpp):
//   M2-07 reload survival: on_shutdown() already unloads the Session into the stash (intent) and writes "join.state"; on_init()
//         resumes from it. M2-07 refines the epoch rule (new universe vs /reloadui) and the shutdown budget in those two places.
//   M2-09 authority: JoinRequest carries want_authority / admin_password (parsed, not yet forwarded); start_session() sets
//         requested_roles; handle_session_event() is the single switch an AuthorityDriver hooks into (Welcome, Frame,
//         NetDisconnected) and pump_session() is where it would step each frame.

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "core/mods/mods.h"
#include "core/session/session.h"
#include "features/authority/authority_flow.h"
#include "features/join/join_json.h"
#include "features/join/platform_stash.h"
#include "features/resume/resume_state.h"
#include "host/feature.h"

namespace x4mp::features {

class JoinFeature final : public host::IFeature {
 public:
  using Clock = std::chrono::steady_clock;

  JoinFeature();
  ~JoinFeature() override;

  [[nodiscard]] std::string_view name() const noexcept override { return "join"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_universe_ready(host::HostContext& ctx) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  struct Inbox;
  enum class Stage : std::uint8_t {
    Idle,         // no session
    Joining,      // connecting / handshaking / waiting for SessionSaveInfo
    Downloading,  // SessionSaveInfo seen, bytes arriving
    Preparing,    // SaveReady: refreshing the save list before the load
    Loading,      // loadSave raised, waiting for the reload and on_universe_ready
    InGame,       // NodeReady sent
    Rejected,     // the server refused us (reject_ is set)
    Failed,       // local failure (download, save folder ...)
  };

  // ---- verbs ----
  void drain_inbox(host::HostContext& ctx);
  void on_join(host::HostContext& ctx, const std::string& payload);
  void on_extensions(host::HostContext& ctx, const std::string& payload);

  // ---- session ----
  void start_session(host::HostContext& ctx, const join::JoinRequest& request, const session::SessionIntent* resume);
  void stop_session(host::HostContext& ctx, const char* why);
  void pump_session(host::HostContext& ctx);
  void handle_session_event(host::HostContext& ctx, const session::SessionEvent& event);  // M2-09 hook point
  void handle_save_ready(host::HostContext& ctx);
  // ---- M2-09 hook points (authority) ----
  void sync_authority(host::HostContext& ctx);
  void step_authority_ready(host::HostContext& ctx);
  void step_preparing(host::HostContext& ctx);
  void issue_load(host::HostContext& ctx);
  void step_loading(host::HostContext& ctx);
  void complete_universe(host::HostContext& ctx);
  void finish_ready(host::HostContext& ctx);  // M2-09: NodeReady once the server confirmed Matching (NodeReady is phase-gated server-side)
  [[nodiscard]] bool welcomed() const;
  void update_diag(host::HostContext& ctx);
  void send_control(std::uint16_t type, const std::vector<std::uint8_t>& payload);
  void persist_state(const char* stage) const;
  [[nodiscard]] const char* resume_stage_name() const;  // M2-07: the persisted stage for the current stage_ ("" = nothing to resume)
  void sample_fingerprint(host::HostContext& ctx, const host::FrameInfo& info);  // M2-07
  void decide_after_reload(host::HostContext& ctx);  // M2-07: universe ready seen by a resumed in-game incarnation
  [[nodiscard]] session::ClientIdentity make_identity(host::HostContext& ctx) const;
  [[nodiscard]] bool load_player_key(host::HostContext& ctx, std::array<std::uint8_t, 32>& key);

  // ---- status ----
  void publish_status(host::HostContext& ctx, bool force);
  [[nodiscard]] std::string build_status() const;
  void raise_lua(host::HostContext& ctx, const char* event, const std::string& payload);

  std::shared_ptr<Inbox> inbox_;
  std::unique_ptr<join::PlatformStash> stash_;
  std::unique_ptr<mods::ExtensionProvider> extensions_;
  std::unique_ptr<session::Session> session_;
  // M2-09: the authority's checkpoint flow, alive while the server granted this node the authority role.
  std::shared_ptr<auth::AuthInbox> auth_inbox_;
  std::unique_ptr<auth::AuthorityFlow> authority_;
  bool want_authority_ = false;                       // the join asked for the authority role
  Clock::time_point welcomed_at_{};                    // the newest Welcome (authority: grace before "no save to load")
  int my_phase_ = -1;                                  // our NodePhase as the server last published it in RosterUpdate (-1 unknown)
  bool ready_pending_ = false;                         // Matching sent, NodeReady waits for the roster to confirm it
  Clock::time_point matching_sent_at_{};
  std::uint64_t pending_epoch_ = 0;
  int authority_ready_step_ = 0;                       // 0 waiting, 1 Loading sent (Matching + NodeReady follow)
  Clock::time_point authority_ready_at_{};

  Stage stage_ = Stage::Idle;
  bool resumed_incarnation_ = false;  // this DLL incarnation resumed a session after a reload
  std::string server_text_;           // host:port as shown in the UI
  std::string reject_;                // token for x4mp.status when Rejected
  std::string detail_;                // free text for x4mp.status
  std::string last_net_error_;
  float progress_ = 0.0f;
  std::set<std::uint16_t> roster_;
  std::array<std::uint8_t, 32> player_key_{};
  bool have_player_key_ = false;

  // The announced save and what the load needs of it (restored from the stash after a reload).
  std::optional<session::SaveInfo> save_;
  std::string save_name_;                       // x4mp_<sha12>, no extension
  std::vector<std::uint8_t> save_sha_;          // loaded_save_sha256 of NodeReady
  session::Id128 checkpoint_;                   // ManifestReport.checkpoint_id
  bool has_manifest_ = false;

  // preparing / loading
  enum class PrepStep : std::uint8_t { ReloadList, WaitList };
  PrepStep prep_step_ = PrepStep::ReloadList;
  Clock::time_point prep_started_{};
  Clock::time_point load_issued_{};
  bool fallback_sent_ = false;

  std::atomic<bool> game_loaded_flag_{false};
  std::atomic<bool> universe_ready_flag_{false};
  bool universe_pending_ = false;  // universe ready seen, waiting for a usable session to report on

  // M2-07 reload survival (features/resume/resume_state.h): epoch of the last NodeReady, last in-game fingerprint, and the
  // state of the previous incarnation while "new universe vs /reloadui" is still undecided.
  std::uint64_t epoch_ = 0;
  resume::Fingerprint fingerprint_;
  std::uint32_t fingerprint_frame_ = 0;
  std::optional<resume::State> undecided_;

  // status throttle (x4mp.status at most 2 Hz)
  std::string last_status_;
  Clock::time_point last_status_at_{};
  bool force_status_ = false;
  std::string mod_refusal_json_;  // M2-X3: x4mp.mod_refusal of the last ExtensionsMismatch (re-sent with every forced status)
  std::string mod_policy_json_;   // M2-X3: x4mp.mod_policy (session mod list) of the last Welcome / ModPolicyChanged

  // diag hub (M2-10): connection state + LogForward sender
  bool diag_connected_ = false;
  bool diag_stats_link_ = false;  // M2-12: the stats link is installed in the diag hub
  bool diag_sender_ = false;
  Clock::time_point log_window_{};
  int log_in_window_ = 0;
};

}  // namespace x4mp::features
