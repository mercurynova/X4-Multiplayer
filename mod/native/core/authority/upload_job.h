#pragma once
// core/authority: the authority's checkpoint upload (M2-08; protocol.md 6.3, docs/mod-design.md 6.2).
//
//   node  -> SaveUploadBegin{checkpoint, kind, size, sha256, name, ghosts_cleaned}   (Control)
//   server-> SaveUploadAccept{upload_id, chunk_size, resume_offset, window_chunks}   (Control)
//   node  -> SaveChunk{upload_id, offset, data} ...                                  (Bulk, at most 8 chunks unacked)
//   server-> SaveChunkAck{upload_id, next_offset} every 4 chunks                     (Control)
//   node  -> SaveUploadEnd{upload_id}; server -> SaveStored{result}                  (Control)
//   SaveUploadEnd is sent only after the server acked the FINAL offset: Control frames overtake Bulk ones in the net layer
//   (protocol.md 3.4), so an End queued behind the last chunks would reach the server first and end the upload "incomplete".
//   Normally two files per checkpoint: the save, then its manifest.
//
// THE RULE (learned in M1, mod-design 6.2): an upload job belongs to ONE connection generation. On a reconnect/resume the old
// job is cancelled and JOINED before the new one starts, and it can never read the new connection's frames or write to its
// socket. The FakeNode authority broke this: the old job, parked waiting for a SaveChunkAck, consumed the resumed job's
// SaveUploadAccept. Here that is structural, not a convention:
//   * every UploadJob has its OWN inbox and OWN outbox; there is no shared queue to steal from;
//   * frames are fed with the generation they arrived on; a job rejects (and counts) any other generation;
//   * the job never touches a socket or the Session: its Control/Bulk frames land in its outbox and the owner
//     (CheckpointUploader::pump, main thread) forwards the CURRENT generation's outbox only; a dead job's frames are discarded;
//   * CheckpointUploader cancels and joins the old job before it creates the next one.
//
// Threads: UploadJob runs its own worker thread (file reads and chunk encoding stay off the game thread). Its public members
// are thread-safe. CheckpointUploader is for the one main/session thread. No exceptions cross the API. core/ only, no X4 SDK.

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <thread>
#include <vector>

#include "core/session/session.h"
#include "x4mp/wire.h"

namespace x4mp::authority {

using session::Id128;
using session::TransferKind;

// Wire values of SaveStoreResult (session.fbs).
enum class StoreResult : std::uint8_t { Stored = 0, StoredNotCurrent, HashMismatch, NotASave, TooLarge, Aborted };
[[nodiscard]] const char* to_string(StoreResult r) noexcept;
[[nodiscard]] constexpr bool is_success(StoreResult r) noexcept { return r == StoreResult::Stored || r == StoreResult::StoredNotCurrent; }

inline constexpr std::uint32_t kMaxWindowChunks = 8;  // the 8-chunk window of protocol.md 6.3

struct UploadFile {
  TransferKind kind = TransferKind::Save;
  std::filesystem::path path;
  std::vector<std::uint8_t> sha256;  // 32 bytes
  std::uint64_t size = 0;
  std::string name;                  // display name
};
// Size + SHA-256 of a file on disk; nullopt if it cannot be read.
[[nodiscard]] std::optional<UploadFile> describe_upload_file(TransferKind kind, const std::filesystem::path& path, std::string name);

struct UploadSpec {
  Id128 checkpoint;                  // authority-generated, links the save and manifest uploads
  std::vector<UploadFile> files;     // uploaded in order
  bool ghosts_cleaned = true;
  std::chrono::milliseconds step_timeout{60000};  // how long to wait for the server's answer to one step
};

enum class UploadState : std::uint8_t { Idle, Running, Completed, Failed, Cancelled };
[[nodiscard]] const char* to_string(UploadState s) noexcept;
enum class UploadError : std::uint8_t { None, Timeout, Io, Protocol, Refused };
[[nodiscard]] const char* to_string(UploadError e) noexcept;

struct FileUploadResult {
  TransferKind kind = TransferKind::Save;
  bool answered = false;             // the server's SaveStored arrived
  StoreResult result = StoreResult::Aborted;
  std::string detail;
  std::uint64_t resume_offset = 0;   // what the server already had when this job began the file (> 0: a resumed upload)
  std::uint64_t bytes_sent = 0;      // chunk bytes this job sent for the file
};

struct UploadResult {
  UploadState state = UploadState::Idle;
  UploadError error = UploadError::None;
  std::string detail;
  std::vector<FileUploadResult> files;
};

// A frame the job wants on the wire. The owner forwards it; the job never writes to a connection itself.
struct OutFrame {
  wire::Lane lane = wire::Lane::Control;
  std::uint16_t type = 0;
  std::vector<std::uint8_t> payload;
  std::uint64_t generation = 0;
};

// Instrumentation (the live test asserts the "cross" counters are 0).
struct UploadCounters {
  std::uint64_t frames_fed = 0;                // frames accepted into this job's inbox
  std::uint64_t foreign_frames_rejected = 0;   // frames fed with another generation: a cross-connection read that was stopped
  std::uint64_t frames_consumed = 0;
  std::uint64_t chunks_sent = 0;               // chunks queued to the outbox
  std::uint64_t bytes_sent = 0;
};

class UploadJob {
 public:
  // `generation` identifies the connection this job belongs to (any nonzero value the owner keeps unique per connection).
  UploadJob(std::uint64_t generation, UploadSpec spec);
  ~UploadJob();  // cancel + join
  UploadJob(const UploadJob&) = delete;
  UploadJob& operator=(const UploadJob&) = delete;

  [[nodiscard]] bool start();  // false if already started / the thread cannot be created
  void cancel() noexcept;      // asks the worker to stop and returns at once
  void join();                 // waits for the worker to end (idempotent)

  // Delivers a frame received on `generation`. Only SaveUploadAccept / SaveChunkAck / SaveStored are of interest; anything else is
  // ignored (false). A frame from another generation is rejected and counted.
  bool feed(std::uint64_t generation, std::uint16_t type, std::span<const std::uint8_t> payload);

  // Moves the frames the worker has produced since the last call to `out` (appends); returns how many.
  std::size_t take_outbox(std::vector<OutFrame>& out);

  [[nodiscard]] std::uint64_t generation() const noexcept { return generation_; }
  [[nodiscard]] const UploadSpec& spec() const noexcept { return spec_; }
  [[nodiscard]] UploadState state() const noexcept { return state_.load(); }
  [[nodiscard]] bool finished() const noexcept;  // Completed, Failed or Cancelled
  [[nodiscard]] UploadResult result() const;
  [[nodiscard]] UploadCounters counters() const noexcept;

 private:
  struct InFrame {
    std::uint16_t type = 0;
    std::vector<std::uint8_t> payload;
  };
  void run() noexcept;
  bool upload_file(const UploadFile& file, FileUploadResult& out);
  bool wait_frame(std::initializer_list<std::uint16_t> wanted, InFrame& out);  // false: cancelled or timed out (error set)
  void push_out(wire::Lane lane, std::uint16_t type, std::vector<std::uint8_t> payload);
  void finish(UploadState state, UploadError error, std::string detail);

  const std::uint64_t generation_;
  const UploadSpec spec_;
  std::thread thread_;
  std::atomic<bool> started_{false};
  std::atomic<bool> cancelled_{false};
  std::atomic<UploadState> state_{UploadState::Idle};

  std::mutex in_mutex_;
  std::condition_variable in_cv_;
  std::deque<InFrame> inbox_;

  std::mutex out_mutex_;
  std::vector<OutFrame> outbox_;

  mutable std::mutex result_mutex_;
  UploadResult result_;

  std::atomic<std::uint64_t> frames_fed_{0}, foreign_rejected_{0}, frames_consumed_{0}, chunks_sent_{0}, bytes_sent_{0};
};

// ---- the owner: one connection generation at a time -----------------------------------------------------------------

struct UploaderStats {
  std::uint64_t generations = 0;               // connections seen (new_connection calls)
  std::uint64_t jobs_started = 0;
  std::uint64_t jobs_cancelled = 0;            // cancelled by connection_lost / new_connection while still running
  std::uint64_t resumed_jobs = 0;              // jobs started again for a pending upload on a resumed connection
  std::uint64_t checkpoints_stored = 0;
  std::uint64_t cross_generation_reads = 0;    // frames a job refused because they belonged to another generation (must be 0)
  std::uint64_t stale_writes_blocked = 0;      // outbox frames of a non-current generation that were NOT forwarded (must be 0)
  std::uint64_t frames_forwarded = 0;
  std::uint64_t send_failures = 0;
};

class CheckpointUploader {
 public:
  using SendFn = std::function<bool(wire::Lane, std::uint16_t type, std::span<const std::uint8_t> payload)>;

  CheckpointUploader() = default;
  ~CheckpointUploader();  // cancel + join
  CheckpointUploader(const CheckpointUploader&) = delete;
  CheckpointUploader& operator=(const CheckpointUploader&) = delete;

  // The link went down: cancels and joins the current job and discards its outbox. The upload stays pending (resumable).
  void connection_lost();
  // A new connection was welcomed. Cancels + joins any job still around, advances the generation, and, if `resumed` and an
  // upload is pending, starts a new job for it (the server answers SaveUploadBegin with the offset it already has). A fresh join
  // drops the pending upload (the server asks again with RequestSave). Returns the new generation.
  std::uint64_t new_connection(bool resumed);

  // Starts uploading `spec` on the current generation (replaces a pending upload). False if there is no connection or the spec
  // has no files.
  bool start(UploadSpec spec);

  // Routes a received frame to the current job. True for SaveUploadAccept / SaveChunkAck / SaveStored (consumed or, with no job,
  // dropped).
  bool on_frame(std::uint16_t type, std::span<const std::uint8_t> payload);

  // Main thread, once per frame: forwards the current job's frames to `send`, notices a finished job. Returns frames sent.
  std::size_t pump(const SendFn& send);

  [[nodiscard]] bool connected() const noexcept { return connected_; }
  [[nodiscard]] std::uint64_t generation() const noexcept { return generation_; }
  // The accessors below notice a job that has just finished (they harvest its result), so a caller never sees "not running" with the
  // result still missing.
  [[nodiscard]] bool running() const;                  // a job is working right now
  [[nodiscard]] bool pending() const;                  // an upload is not yet fully stored
  [[nodiscard]] const std::optional<UploadResult>& last_result() const;  // newest finished job
  [[nodiscard]] UploaderStats stats() const;

 private:
  void retire_job(bool count_cancel);
  bool start_job(bool resumed);
  void harvest() const;  // takes the result of a finished job exactly once

  bool connected_ = false;
  std::uint64_t generation_ = 0;
  mutable std::unique_ptr<UploadJob> job_;
  mutable std::optional<UploadSpec> pending_;
  mutable std::optional<UploadResult> last_result_;
  mutable bool harvested_ = false;
  mutable UploaderStats stats_;
};

}  // namespace x4mp::authority
