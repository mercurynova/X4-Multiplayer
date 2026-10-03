#pragma once
// x4mp-headless --authority: the authority's checkpoint flow without X4 (M2-08). Test/diagnostic code, not shipped in x4mp.dll
// (M2-09 builds the in-game flow on core/authority; this mirrors what the FakeNode authority does so the C++ upload job can be
// exercised against the real server, including connections killed mid-upload).
//
// What it does, driven by Session events (single thread; call step() every ~10 ms):
//   * after every Welcome: CheckpointUploader::new_connection(resumed) (cancels + joins the previous job first);
//   * reports Loading, Matching, NodeReady (waiting for the server's RosterUpdate to confirm each phase, with a timeout);
//   * on RequestSave: StringTableAdd (once), GalaxyMetadata, SaveStarted (with game_time), then uploads the save file and an
//     empty-station manifest through CheckpointUploader;
//   * after both files are stored: ONE self-spawn EntitySpawn (the authority's own ship) with a nonzero game_time;
//   * on a lost connection: the upload job is cancelled; on the resumed connection it starts again from the server's offset.

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

#include "core/authority/checkpoint_messages.h"
#include "core/authority/upload_job.h"
#include "core/session/session.h"

namespace x4mp::headless {

struct AuthorityDriverOptions {
  std::filesystem::path save_file;  // uploaded verbatim as the checkpoint save (a gzip whose root element is <savegame>)
  std::filesystem::path work_dir;   // the manifest is written here (empty = a temp dir)
  std::chrono::milliseconds step_timeout{60000};
  std::chrono::milliseconds phase_wait{3000};  // how long to wait for the roster to confirm a node phase before sending on
  // The authority's game time. Default: 60 s + seconds since the driver was created (monotonic, never 0).
  std::function<double()> game_time;
  std::function<void(std::string_view)> log;                     // one line per notable step
  std::function<void(const session::SessionEvent&)> on_event;    // every session event, before the driver handles it
};

class AuthorityDriver {
 public:
  AuthorityDriver(session::Session& session, AuthorityDriverOptions options);

  // Polls the session, handles its events, forwards upload frames, advances the ready/spawn flow.
  void step();

  [[nodiscard]] bool checkpoint_stored() const noexcept { return checkpoints_stored_ > 0; }
  [[nodiscard]] bool spawn_sent() const noexcept { return spawn_sent_; }
  [[nodiscard]] bool done() const noexcept { return checkpoint_stored() && spawn_sent_; }
  [[nodiscard]] bool failed() const noexcept { return failed_; }
  [[nodiscard]] const std::string& failure() const noexcept { return failure_; }
  [[nodiscard]] double spawn_game_time() const noexcept { return spawn_game_time_; }
  [[nodiscard]] std::uint64_t request_saves() const noexcept { return request_saves_; }
  [[nodiscard]] const authority::CheckpointUploader& uploader() const noexcept { return uploader_; }
  [[nodiscard]] authority::UploaderStats upload_stats() const { return uploader_.stats(); }
  [[nodiscard]] const std::optional<authority::UploadResult>& last_upload() const noexcept { return uploader_.last_result(); }

 private:
  void handle(const session::SessionEvent& ev);
  void on_request_save(std::span<const std::uint8_t> payload);
  void advance_ready();
  void maybe_spawn();
  void fail(std::string why);
  void say(const std::string& line) const;
  [[nodiscard]] double now_game_time() const;
  void track_roster(std::span<const std::uint8_t> payload);

  session::Session& session_;
  AuthorityDriverOptions opt_;
  authority::CheckpointUploader uploader_;
  std::chrono::steady_clock::time_point started_;
  std::filesystem::path work_dir_;

  int phase_ = -1;  // NodePhase reported by the server for this node (-1: none yet)
  int ready_step_ = 0;
  std::chrono::steady_clock::time_point ready_at_;
  bool startup_sent_ = false;
  std::uint64_t checkpoints_stored_ = 0;
  std::uint64_t request_saves_ = 0;
  bool spawn_sent_ = false;
  double spawn_game_time_ = 0.0;
  bool failed_ = false;
  std::string failure_;
  std::uint32_t next_net_id_ = 1;
  std::uint64_t counted_stored_ = 0;
};

// Sectors/links/strings of the tiny galaxy the headless authority announces (what an empty save has to say to the server).
[[nodiscard]] std::vector<authority::SectorDesc> headless_sectors();
[[nodiscard]] std::vector<authority::LinkDesc> headless_links();
[[nodiscard]] std::vector<authority::StringDesc> headless_strings();

}  // namespace x4mp::headless
