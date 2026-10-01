#include "core/session/save_download.h"

#include <algorithm>
#include <system_error>
#include <utility>

#include "core/log/log.h"
#include "flatbuffers/flatbuffers.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::session {
namespace {

constexpr std::uint32_t kAckEveryChunks = 4;  // protocol.md 3.4: acked every 4 chunks, window 8

std::uint16_t T(X4MP::Proto::MsgType t) noexcept { return static_cast<std::uint16_t>(t); }

}  // namespace

const char* to_string(DownloadState state) noexcept {
  switch (state) {
    case DownloadState::Idle: return "Idle";
    case DownloadState::Requesting: return "Requesting";
    case DownloadState::Receiving: return "Receiving";
    case DownloadState::Completed: return "Completed";
    case DownloadState::Failed: return "Failed";
  }
  return "?";
}

const char* to_string(DownloadError error) noexcept {
  switch (error) {
    case DownloadError::None: return "None";
    case DownloadError::HashMismatch: return "HashMismatch";
    case DownloadError::SizeMismatch: return "SizeMismatch";
    case DownloadError::IoError: return "IoError";
    case DownloadError::ProtocolError: return "ProtocolError";
    case DownloadError::HashUnavailable: return "HashUnavailable";
  }
  return "?";
}

void SaveDownloader::fail_locked(DownloadError error, std::string detail) {
  if (part_.is_open()) part_.close();
  prog_.state = DownloadState::Failed;
  prog_.error = error;
  prog_.detail = std::move(detail);
  accepted_ = false;
  X4MP_LOGW("save: download failed ({}): {}", to_string(error), prog_.detail);
}

bool SaveDownloader::begin(const DownloadSpec& spec) {
  const std::lock_guard lock(mutex_);
  if (part_.is_open()) part_.close();
  spec_ = spec;
  prog_ = DownloadProgress{};
  prog_.kind = spec.kind;
  prog_.size = spec.size;
  next_offset_ = 0;
  download_id_ = 0;
  accepted_ = false;
  since_ack_ = 0;
  requested_epoch_ = 0;
  hasher_ = crypto::Sha256Hasher();

  if (spec.sha256.size() != crypto::kSha256Size || spec.size == 0 || spec.final_path.empty()) {
    fail_locked(DownloadError::ProtocolError, "bad download spec (need a 32-byte sha256, a size and a path)");
    return false;
  }
  part_path_ = spec.final_path;
  part_path_ += ".part";

  std::error_code ec;
  if (spec.final_path.has_parent_path()) std::filesystem::create_directories(spec.final_path.parent_path(), ec);
  ec.clear();
  std::uint64_t existing = 0;
  if (std::filesystem::exists(part_path_, ec)) {
    existing = static_cast<std::uint64_t>(std::filesystem::file_size(part_path_, ec));
    if (ec) existing = 0;
    if (existing > spec.size) {  // not ours (or a different file reusing the name): start over
      std::filesystem::remove(part_path_, ec);
      existing = 0;
    }
  }
  if (!hasher_.ok()) {
    fail_locked(DownloadError::HashUnavailable, "SHA-256 provider unavailable");
    return false;
  }
  if (existing > 0) {
    // Resume: hash what is already on disk, then continue appending.
    std::ifstream in(part_path_, std::ios::binary);
    std::vector<std::uint8_t> buf(1u << 20);
    std::uint64_t left = existing;
    while (in && left > 0) {
      in.read(reinterpret_cast<char*>(buf.data()), static_cast<std::streamsize>(std::min<std::uint64_t>(buf.size(), left)));
      const auto got = static_cast<std::uint64_t>(in.gcount());
      if (got == 0) break;
      hasher_.update(std::span<const std::uint8_t>(buf.data(), static_cast<std::size_t>(got)));
      left -= got;
    }
    if (left != 0) {
      fail_locked(DownloadError::IoError, "could not read the existing part file");
      return false;
    }
  }
  part_.open(part_path_, existing > 0 ? (std::ios::binary | std::ios::app) : (std::ios::binary | std::ios::trunc));
  if (!part_) {
    fail_locked(DownloadError::IoError, "could not open " + part_path_.string());
    return false;
  }
  next_offset_ = existing;
  prog_.bytes_done = existing;
  prog_.resumed_from = existing;
  prog_.state = DownloadState::Requesting;
  if (existing == spec.size) finalize_locked();  // a complete part file: verify without the network
  return prog_.state != DownloadState::Failed;
}

void SaveDownloader::finalize_locked() {
  part_.flush();
  const bool wrote_ok = static_cast<bool>(part_);
  part_.close();
  if (!wrote_ok) {
    fail_locked(DownloadError::IoError, "write error on the part file");
    return;
  }
  const auto digest = hasher_.finish();
  if (!digest) {
    fail_locked(DownloadError::HashUnavailable, "could not finish SHA-256");
    return;
  }
  if (!std::equal(digest->begin(), digest->end(), spec_.sha256.begin())) {
    std::error_code ec;
    std::filesystem::remove(part_path_, ec);
    fail_locked(DownloadError::HashMismatch, "SHA-256 of the downloaded file does not match; part file deleted");
    return;
  }
  std::error_code ec;
  std::filesystem::rename(part_path_, spec_.final_path, ec);  // replaces an existing target on Windows
  if (ec) {
    fail_locked(DownloadError::IoError, "rename failed: " + ec.message());
    return;
  }
  prog_.state = DownloadState::Completed;
  prog_.bytes_done = spec_.size;
  accepted_ = false;
  X4MP_LOGI("save: download verified and stored as {}", spec_.final_path.string());
}

void SaveDownloader::send_request_locked(const SendFn& send) {
  flatbuffers::FlatBufferBuilder fbb(96);
  const auto sha = fbb.CreateVector(spec_.sha256.data(), spec_.sha256.size());
  fbb.Finish(X4MP::Proto::CreateSaveDownloadRequest(
      fbb, sha, spec_.kind == TransferKind::Save ? X4MP::Proto::UploadKind::Save : X4MP::Proto::UploadKind::Manifest,
      next_offset_));
  if (send(wire::Lane::Control, T(X4MP::Proto::MsgType::SaveDownloadRequest),
           std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()))) {
    ++prog_.requests_sent;
  }
}

void SaveDownloader::request(const SendFn& send, std::uint32_t epoch) {
  const std::lock_guard lock(mutex_);
  if (prog_.state != DownloadState::Requesting && prog_.state != DownloadState::Receiving) return;
  if (epoch != 0 && epoch == requested_epoch_) return;
  requested_epoch_ = epoch;
  // A new request supersedes whatever stream was in flight: forget the old download id, flush the part file so
  // the bytes on disk match next_offset_ (a later begin() after a process restart re-hashes exactly these).
  accepted_ = false;
  prog_.state = DownloadState::Requesting;
  part_.flush();
  since_ack_ = 0;
  send_request_locked(send);
}

bool SaveDownloader::on_frame(std::uint16_t type, std::span<const std::uint8_t> payload, const SendFn& send) {
  const bool is_accept = type == T(X4MP::Proto::MsgType::SaveDownloadAccept);
  const bool is_chunk = type == T(X4MP::Proto::MsgType::SaveChunk);
  if (!is_accept && !is_chunk) return false;
  const std::lock_guard lock(mutex_);
  if (prog_.state != DownloadState::Requesting && prog_.state != DownloadState::Receiving) return true;  // stale

  if (is_accept) {
    const auto* a = flatbuffers::GetRoot<X4MP::Proto::SaveDownloadAccept>(payload.data());
    if (a->size() != spec_.size) {
      fail_locked(DownloadError::SizeMismatch, "server announced " + std::to_string(a->size()) + " bytes, expected " +
                                                   std::to_string(spec_.size));
      return true;
    }
    download_id_ = a->download_id();
    accepted_ = true;
    since_ack_ = 0;
    prog_.state = DownloadState::Receiving;
    return true;
  }

  const auto* c = flatbuffers::GetRoot<X4MP::Proto::SaveChunk>(payload.data());
  if (!accepted_ || c->transfer_id() != download_id_ || c->data() == nullptr) return true;  // stale stream
  const std::uint64_t off = c->offset();
  const std::size_t len = c->data()->size();
  if (off < next_offset_) return true;  // duplicate
  if (off > next_offset_) {             // a gap: ask again from the contiguous offset
    X4MP_LOGW("save: chunk gap (got offset {}, expected {}): re-requesting", off, next_offset_);
    accepted_ = false;
    prog_.state = DownloadState::Requesting;
    send_request_locked(send);
    return true;
  }
  if (len == 0 || next_offset_ + len > spec_.size) {
    fail_locked(DownloadError::ProtocolError, "chunk beyond the announced size");
    return true;
  }
  part_.write(reinterpret_cast<const char*>(c->data()->data()), static_cast<std::streamsize>(len));
  if (!part_) {
    fail_locked(DownloadError::IoError, "write error on the part file");
    return true;
  }
  hasher_.update(std::span<const std::uint8_t>(c->data()->data(), len));
  next_offset_ += len;
  prog_.bytes_done = next_offset_;
  ++prog_.chunks_received;
  ++since_ack_;
  const bool last = next_offset_ == spec_.size;
  if (last || since_ack_ >= kAckEveryChunks) {
    flatbuffers::FlatBufferBuilder fbb(48);
    fbb.Finish(X4MP::Proto::CreateSaveChunkAck(fbb, download_id_, next_offset_));
    if (send(wire::Lane::Control, T(X4MP::Proto::MsgType::SaveChunkAck),
             std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()))) {
      ++prog_.acks_sent;
    }
    since_ack_ = 0;
  }
  if (last) finalize_locked();
  return true;
}

bool SaveDownloader::active() const {
  const std::lock_guard lock(mutex_);
  return prog_.state == DownloadState::Requesting || prog_.state == DownloadState::Receiving;
}

DownloadProgress SaveDownloader::progress() const {
  const std::lock_guard lock(mutex_);
  return prog_;
}

void SaveDownloader::cancel() {
  const std::lock_guard lock(mutex_);
  if (part_.is_open()) part_.close();
  if (prog_.state == DownloadState::Requesting || prog_.state == DownloadState::Receiving) {
    prog_.state = DownloadState::Idle;
  }
  accepted_ = false;
}

}  // namespace x4mp::session
