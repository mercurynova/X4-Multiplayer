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
#include "features/join/join_json.h"
#include "features/join/platform_stash.h"
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
  void step_preparing(host::HostContext& ctx);
  void issue_load(host::HostContext& ctx);
  void step_loading(host::HostContext& ctx);
  void complete_universe(host::HostContext& ctx);
  [[nodiscard]] bool welcomed() const;
  void update_diag(host::HostContext& ctx);
  void send_control(std::uint16_t type, const std::vector<std::uint8_t>& payload);
  void persist_state(const char* stage) const;
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

  // status throttle (x4mp.status at most 2 Hz)
  std::string last_status_;
  Clock::time_point last_status_at_{};
  bool force_status_ = false;

  // diag hub (M2-10): connection state + LogForward sender
  bool diag_connected_ = false;
  bool diag_sender_ = false;
  Clock::time_point log_window_{};
  int log_in_window_ = 0;
};

}  // namespace x4mp::features
