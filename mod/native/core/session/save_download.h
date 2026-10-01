#pragma once
// core/session: in-band save download (protocol.md 6.4, server-design 3.3). One transfer at a time.
//
//   node  -> SaveDownloadRequest{sha256, kind, offset}            (Control)
//   server-> SaveDownloadAccept{download_id, size, chunk_size, window_chunks}   (Control)
//   server-> SaveChunk{transfer_id, offset, data} ...              (Bulk, windowed)
//   node  -> SaveChunkAck{transfer_id, next_offset} every 4 chunks and after the last   (Control)
//
// Bytes land in "<final>.part" and are hashed incrementally (BCrypt SHA-256), so the check at the end costs
// nothing and the net thread never stalls on a big file. When all `size` bytes are in, the digest is compared
// with the announced SHA-256: on a match the part file is renamed to the final name (the atomic step), on a
// mismatch it is deleted and the transfer reports HashMismatch (the caller then sends LoadStatus Failed /
// SaveChecksumMismatch and may start over). A part file is kept across interruptions: begin() re-hashes whatever
// prefix is on disk and the next request starts at that offset ("resume = send the request again with the
// current offset"). Out-of-order chunks are never written: a gap triggers a fresh request at the contiguous
// offset; duplicates are ignored.
//
// Thread safety: every public member locks an internal mutex, so begin()/progress() (main thread) and
// request()/on_frame() (net thread, from the Session frame hook) may interleave. The SendFn must not block
// (HookContext::send / NetClient::send are both non-blocking). No exceptions cross the API; core/ only.

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <mutex>
#include <span>
#include <string>
#include <vector>

#include "core/crypto/crypto.h"
#include "x4mp/wire.h"

namespace x4mp::session {

enum class TransferKind : std::uint8_t { Save = 0, Manifest = 1 };  // = UploadKind on the wire

enum class DownloadState : std::uint8_t {
  Idle,        // nothing armed
  Requesting,  // armed, request sent (or about to be), waiting for SaveDownloadAccept
  Receiving,   // accepted, chunks flowing
  Completed,   // verified and renamed into place
  Failed,      // see DownloadProgress::error
};
[[nodiscard]] const char* to_string(DownloadState state) noexcept;

enum class DownloadError : std::uint8_t {
  None,
  HashMismatch,    // all bytes arrived but SHA-256 differs (the part file was deleted)
  SizeMismatch,    // the server's Accept size differs from the announced size
  IoError,         // could not read/write/rename the part or final file
  ProtocolError,   // chunk beyond the announced size, Accept contradicting itself
  HashUnavailable, // BCrypt could not create a hash object
};
[[nodiscard]] const char* to_string(DownloadError error) noexcept;

struct DownloadSpec {
  TransferKind kind = TransferKind::Save;
  std::vector<std::uint8_t> sha256;  // 32 bytes: the expected digest (and the file's identity on the server)
  std::uint64_t size = 0;            // announced size (SessionSaveInfo); the server's Accept must agree
  std::filesystem::path final_path;  // e.g. <save dir>/x4mp_<sha12>.xml.gz; the part file is "<final>.part"
};

struct DownloadProgress {
  DownloadState state = DownloadState::Idle;
  TransferKind kind = TransferKind::Save;
  DownloadError error = DownloadError::None;
  std::string detail;
  std::uint64_t bytes_done = 0;
  std::uint64_t size = 0;
  std::uint64_t resumed_from = 0;     // bytes already on disk when begin() ran (0 = fresh)
  std::uint32_t requests_sent = 0;    // SaveDownloadRequest frames sent (re-requests after cuts count)
  std::uint32_t chunks_received = 0;
  std::uint32_t acks_sent = 0;
  [[nodiscard]] float fraction() const noexcept { return size == 0 ? 0.0f : static_cast<float>(bytes_done) / static_cast<float>(size); }
};

class SaveDownloader {
 public:
  using SendFn = std::function<bool(wire::Lane, std::uint16_t type, std::span<const std::uint8_t> payload)>;

  SaveDownloader() = default;
  SaveDownloader(const SaveDownloader&) = delete;
  SaveDownloader& operator=(const SaveDownloader&) = delete;

  // Arms a transfer, replacing any previous one. Picks up "<final>.part" if present. If the part file already
  // holds all `size` bytes it is verified at once (state Completed or Failed, no network needed). Returns false
  // (state Failed) on a bad spec / I/O error; the reason is in progress().detail.
  bool begin(const DownloadSpec& spec);

  // Sends SaveDownloadRequest at the current contiguous offset. Call after begin() once connected, and again on
  // every (re)connect while active(). No-op unless the state is Requesting or Receiving. `epoch` (the connection
  // counter, 0 = always send) makes it idempotent: a second call with the same non-zero epoch does nothing, so the
  // net-thread hook and the main thread cannot double-request on one connection.
  void request(const SendFn& send, std::uint32_t epoch = 0);

  // Feeds an inbound frame. Returns true if it was a download message (SaveDownloadAccept / SaveChunk) and so
  // consumed here; false for anything else.
  bool on_frame(std::uint16_t type, std::span<const std::uint8_t> payload, const SendFn& send);

  [[nodiscard]] bool active() const;  // Requesting or Receiving
  [[nodiscard]] DownloadProgress progress() const;
  // Drops the transfer; the part file stays (a later begin() resumes it).
  void cancel();

 private:
  void fail_locked(DownloadError error, std::string detail);
  void finalize_locked();
  void send_request_locked(const SendFn& send);
  bool open_part_locked(std::uint64_t existing);

  mutable std::mutex mutex_;
  DownloadSpec spec_;
  DownloadProgress prog_;
  std::filesystem::path part_path_;
  std::ofstream part_;
  crypto::Sha256Hasher hasher_;
  std::uint64_t next_offset_ = 0;
  std::uint32_t download_id_ = 0;
  bool accepted_ = false;
  std::uint32_t since_ack_ = 0;
  std::uint32_t requested_epoch_ = 0;
};

}  // namespace x4mp::session
