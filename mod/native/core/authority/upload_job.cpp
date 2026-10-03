#include "core/authority/upload_job.h"

#include <algorithm>
#include <fstream>

#include "core/crypto/crypto.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::authority {

namespace {
namespace P = X4MP::Proto;
constexpr std::uint16_t T(P::MsgType t) { return static_cast<std::uint16_t>(t); }
constexpr std::uint32_t kMinChunk = 4096;
constexpr std::uint32_t kMaxChunk = 512u * 1024u;  // well under the 1 MiB frame cap
}  // namespace

const char* to_string(StoreResult r) noexcept {
  switch (r) {
    case StoreResult::Stored: return "Stored";
    case StoreResult::StoredNotCurrent: return "StoredNotCurrent";
    case StoreResult::HashMismatch: return "HashMismatch";
    case StoreResult::NotASave: return "NotASave";
    case StoreResult::TooLarge: return "TooLarge";
    case StoreResult::Aborted: return "Aborted";
  }
  return "?";
}

const char* to_string(UploadState s) noexcept {
  switch (s) {
    case UploadState::Idle: return "Idle";
    case UploadState::Running: return "Running";
    case UploadState::Completed: return "Completed";
    case UploadState::Failed: return "Failed";
    case UploadState::Cancelled: return "Cancelled";
  }
  return "?";
}

const char* to_string(UploadError e) noexcept {
  switch (e) {
    case UploadError::None: return "None";
    case UploadError::Timeout: return "Timeout";
    case UploadError::Io: return "Io";
    case UploadError::Protocol: return "Protocol";
    case UploadError::Refused: return "Refused";
  }
  return "?";
}

std::optional<UploadFile> describe_upload_file(TransferKind kind, const std::filesystem::path& path, std::string name) {
  std::error_code ec;
  const auto size = std::filesystem::file_size(path, ec);
  if (ec) return std::nullopt;
  const auto digest = crypto::sha256_file(path);
  if (!digest) return std::nullopt;
  UploadFile f;
  f.kind = kind;
  f.path = path;
  f.sha256.assign(digest->begin(), digest->end());
  f.size = size;
  f.name = std::move(name);
  return f;
}

// ---------------------------------------------------------------------------------------------------------------------------
// UploadJob
// ---------------------------------------------------------------------------------------------------------------------------

UploadJob::UploadJob(std::uint64_t generation, UploadSpec spec) : generation_(generation), spec_(std::move(spec)) {}

UploadJob::~UploadJob() {
  cancel();
  join();
}

bool UploadJob::start() {
  bool expected = false;
  if (!started_.compare_exchange_strong(expected, true)) return false;
  state_ = UploadState::Running;
  try {
    thread_ = std::thread([this] { run(); });
  } catch (...) {
    state_ = UploadState::Failed;
    return false;
  }
  return true;
}

void UploadJob::cancel() noexcept {
  cancelled_.store(true);
  const std::lock_guard lock(in_mutex_);  // pairs with the wait predicate: no lost wake-up
  in_cv_.notify_all();
}

void UploadJob::join() {
  if (thread_.joinable()) thread_.join();
}

bool UploadJob::finished() const noexcept {
  const auto s = state_.load();
  return s == UploadState::Completed || s == UploadState::Failed || s == UploadState::Cancelled;
}

UploadResult UploadJob::result() const {
  const std::lock_guard lock(result_mutex_);
  UploadResult r = result_;
  r.state = state_.load();
  return r;
}

UploadCounters UploadJob::counters() const noexcept {
  UploadCounters c;
  c.frames_fed = frames_fed_.load();
  c.foreign_frames_rejected = foreign_rejected_.load();
  c.frames_consumed = frames_consumed_.load();
  c.chunks_sent = chunks_sent_.load();
  c.bytes_sent = bytes_sent_.load();
  return c;
}

bool UploadJob::feed(std::uint64_t generation, std::uint16_t type, std::span<const std::uint8_t> payload) {
  if (type != T(P::MsgType::SaveUploadAccept) && type != T(P::MsgType::SaveChunkAck) && type != T(P::MsgType::SaveStored)) return false;
  if (generation != generation_) {
    // A frame of another connection must never reach this job.
    foreign_rejected_.fetch_add(1);
    return false;
  }
  if (cancelled_.load() || finished()) return false;
  {
    const std::lock_guard lock(in_mutex_);
    inbox_.push_back(InFrame{type, std::vector<std::uint8_t>(payload.begin(), payload.end())});
  }
  frames_fed_.fetch_add(1);
  in_cv_.notify_all();
  return true;
}

std::size_t UploadJob::take_outbox(std::vector<OutFrame>& out) {
  const std::lock_guard lock(out_mutex_);
  const auto n = outbox_.size();
  for (auto& f : outbox_) out.push_back(std::move(f));
  outbox_.clear();
  return n;
}

void UploadJob::push_out(wire::Lane lane, std::uint16_t type, std::vector<std::uint8_t> payload) {
  const std::lock_guard lock(out_mutex_);
  outbox_.push_back(OutFrame{lane, type, std::move(payload), generation_});
}

void UploadJob::finish(UploadState state, UploadError error, std::string detail) {
  {
    const std::lock_guard lock(result_mutex_);
    result_.error = error;
    result_.detail = std::move(detail);
  }
  state_.store(state);
}

bool UploadJob::wait_frame(std::initializer_list<std::uint16_t> wanted, InFrame& out) {
  const auto deadline = std::chrono::steady_clock::now() + spec_.step_timeout;
  std::unique_lock lock(in_mutex_);
  for (;;) {
    while (!inbox_.empty()) {
      InFrame f = std::move(inbox_.front());
      inbox_.pop_front();
      frames_consumed_.fetch_add(1);
      if (std::find(wanted.begin(), wanted.end(), f.type) != wanted.end()) {
        out = std::move(f);
        return true;
      }
      // an ack nobody needs any more: skip
    }
    if (cancelled_.load()) return false;
    if (!in_cv_.wait_until(lock, deadline, [this] { return !inbox_.empty() || cancelled_.load(); })) {
      finish(UploadState::Failed, UploadError::Timeout, "no answer from the server within the step timeout");
      return false;
    }
    if (cancelled_.load()) return false;
  }
}

void UploadJob::run() noexcept {
  try {
    {
      const std::lock_guard lock(result_mutex_);
      result_.files.clear();
    }
    bool all_ok = true;
    for (const UploadFile& file : spec_.files) {
      FileUploadResult fr;
      fr.kind = file.kind;
      const bool ok = upload_file(file, fr);
      {
        const std::lock_guard lock(result_mutex_);
        result_.files.push_back(fr);
      }
      if (!ok) {
        if (cancelled_.load() && state_.load() == UploadState::Running) finish(UploadState::Cancelled, UploadError::None, "cancelled");
        else if (state_.load() == UploadState::Running) finish(UploadState::Failed, UploadError::Protocol, "upload failed");
        return;
      }
      if (!is_success(fr.result)) all_ok = false;
    }
    if (all_ok) finish(UploadState::Completed, UploadError::None, {});
    else finish(UploadState::Failed, UploadError::Refused, "the server did not store every file");
  } catch (...) {
    finish(UploadState::Failed, UploadError::Io, "exception in the upload worker");
  }
}

// Uploads one file. Returns false on cancel/timeout/IO/protocol failure (the state is set); true once the server answered.
bool UploadJob::upload_file(const UploadFile& file, FileUploadResult& out) {
  std::vector<std::uint8_t> payload;
  {
    flatbuffers::FlatBufferBuilder fbb(512);
    const P::Id128 cp(spec_.checkpoint.lo, spec_.checkpoint.hi);
    const auto sha = fbb.CreateVector(file.sha256);
    const auto name = fbb.CreateString(file.name);
    fbb.Finish(P::CreateSaveUploadBegin(fbb, &cp, file.kind == TransferKind::Save ? P::UploadKind::Save : P::UploadKind::Manifest,
                                        file.size, sha, name, spec_.ghosts_cleaned));
    payload.assign(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
  }
  push_out(wire::Lane::Control, T(P::MsgType::SaveUploadBegin), std::move(payload));

  InFrame frame;
  for (;;) {
    if (!wait_frame({T(P::MsgType::SaveUploadAccept), T(P::MsgType::SaveStored)}, frame)) return false;
    if (frame.type == T(P::MsgType::SaveUploadAccept)) break;
    const auto* s = flatbuffers::GetRoot<P::SaveStored>(frame.payload.data());
    // A Begin is answered by SaveUploadAccept, or by a SaveStored with upload_id 0 (refused before an upload id was issued: too
    // large, ...). A SaveStored with an id belongs to some earlier upload of this checkpoint: the server reports the end of the
    // upload the dropped connection left behind to the authority's NEW connection, and it may arrive before our Accept. Not ours.
    if (s->upload_id() != 0) continue;
    out.answered = true;
    out.result = static_cast<StoreResult>(s->result());
    if (s->detail() != nullptr) out.detail = s->detail()->str();
    return true;
  }
  const auto* accept = flatbuffers::GetRoot<P::SaveUploadAccept>(frame.payload.data());
  const std::uint32_t upload_id = accept->upload_id();
  const std::uint32_t chunk = std::clamp(accept->chunk_size(), kMinChunk, kMaxChunk);
  const std::uint32_t window_chunks = std::clamp<std::uint32_t>(accept->window_chunks(), 1, kMaxWindowChunks);
  const std::uint64_t window = static_cast<std::uint64_t>(window_chunks) * chunk;
  std::uint64_t offset = accept->resume_offset();
  if (offset > file.size) {
    finish(UploadState::Failed, UploadError::Protocol, "the server's resume offset is beyond the file size");
    return false;
  }
  out.resume_offset = offset;
  std::uint64_t acked = offset;

  std::ifstream in(file.path, std::ios::binary);
  if (!in) {
    finish(UploadState::Failed, UploadError::Io, "cannot open " + file.path.string());
    return false;
  }
  in.seekg(static_cast<std::streamoff>(offset));
  std::vector<std::uint8_t> buf(chunk);
  flatbuffers::FlatBufferBuilder fbb(chunk + 256);

  const auto take_ack = [&](const InFrame& f) -> bool {  // true if it ended the file (the server gave up on us)
    if (f.type == T(P::MsgType::SaveChunkAck)) {
      const auto* a = flatbuffers::GetRoot<P::SaveChunkAck>(f.payload.data());
      if (a->transfer_id() == upload_id) acked = std::max<std::uint64_t>(acked, a->next_offset());
      return false;
    }
    const auto* s = flatbuffers::GetRoot<P::SaveStored>(f.payload.data());
    if (s->upload_id() != upload_id) return false;  // the end of an earlier upload (see above), not ours
    out.answered = true;
    out.result = static_cast<StoreResult>(s->result());
    if (s->detail() != nullptr) out.detail = s->detail()->str();
    return true;
  };

  while (offset < file.size) {
    while (offset - acked >= window) {
      if (!wait_frame({T(P::MsgType::SaveChunkAck), T(P::MsgType::SaveStored)}, frame)) return false;
      if (take_ack(frame)) return true;
    }
    if (cancelled_.load()) return false;
    const auto n = static_cast<std::size_t>(std::min<std::uint64_t>(chunk, file.size - offset));
    in.read(reinterpret_cast<char*>(buf.data()), static_cast<std::streamsize>(n));
    if (static_cast<std::size_t>(in.gcount()) != n) {
      finish(UploadState::Failed, UploadError::Io, "the file is shorter than announced: " + file.path.string());
      return false;
    }
    fbb.Clear();
    const auto data = fbb.CreateVector(buf.data(), n);
    fbb.Finish(P::CreateSaveChunk(fbb, upload_id, offset, data));
    push_out(wire::Lane::Bulk, T(P::MsgType::SaveChunk), std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()));
    offset += n;
    out.bytes_sent += n;
    bytes_sent_.fetch_add(n);
    chunks_sent_.fetch_add(1);
    // Take whatever acks have arrived without waiting.
    for (;;) {
      InFrame queued;
      {
        const std::lock_guard lock(in_mutex_);
        if (inbox_.empty()) break;
        queued = std::move(inbox_.front());
        inbox_.pop_front();
        frames_consumed_.fetch_add(1);
      }
      if (queued.type == T(P::MsgType::SaveChunkAck) || queued.type == T(P::MsgType::SaveStored)) {
        if (take_ack(queued)) return true;
      }
    }
  }

  // SaveUploadEnd travels on the Control lane, the chunks on the Bulk lane, and the net layer sends Bulk only when the Control path
  // is empty (protocol.md 3.4): the End could overtake the last chunks and the server would end the upload "incomplete" (seen
  // against the real server). So the End waits until the server has acknowledged every byte; it always acks the final offset.
  while (acked < file.size) {
    if (!wait_frame({T(P::MsgType::SaveChunkAck), T(P::MsgType::SaveStored)}, frame)) return false;
    if (take_ack(frame)) return true;
  }
  {
    flatbuffers::FlatBufferBuilder end_fbb(64);
    end_fbb.Finish(P::CreateSaveUploadEnd(end_fbb, upload_id));
    push_out(wire::Lane::Control, T(P::MsgType::SaveUploadEnd), std::vector<std::uint8_t>(end_fbb.GetBufferPointer(), end_fbb.GetBufferPointer() + end_fbb.GetSize()));
  }
  for (;;) {
    if (!wait_frame({T(P::MsgType::SaveStored)}, frame)) return false;
    const auto* s = flatbuffers::GetRoot<P::SaveStored>(frame.payload.data());
    if (s->upload_id() != upload_id) continue;
    out.answered = true;
    out.result = static_cast<StoreResult>(s->result());
    if (s->detail() != nullptr) out.detail = s->detail()->str();
    return true;
  }
}

// ---------------------------------------------------------------------------------------------------------------------------
// CheckpointUploader
// ---------------------------------------------------------------------------------------------------------------------------

CheckpointUploader::~CheckpointUploader() { retire_job(false); }

void CheckpointUploader::retire_job(bool count_cancel) {
  if (!job_) return;
  const bool was_running = !job_->finished();
  job_->cancel();
  job_->join();  // the old job has fully ended before anything else happens
  if (was_running && count_cancel) ++stats_.jobs_cancelled;
  const auto c = job_->counters();
  stats_.cross_generation_reads += c.foreign_frames_rejected;
  harvest();
  job_.reset();
}

void CheckpointUploader::harvest() const {
  if (!job_ || harvested_ || !job_->finished()) return;
  harvested_ = true;
  job_->join();
  auto r = job_->result();
  if (r.state == UploadState::Completed) {
    ++stats_.checkpoints_stored;
    pending_.reset();
  } else if (r.state == UploadState::Failed && r.error != UploadError::Timeout) {
    pending_.reset();  // refused / broken file: resuming would not help
  }
  last_result_ = std::move(r);
}

void CheckpointUploader::connection_lost() {
  connected_ = false;
  // Anything the job produced for the dead socket is discarded with it.
  retire_job(true);
}

std::uint64_t CheckpointUploader::new_connection(bool resumed) {
  retire_job(true);  // cancel + join BEFORE the next generation exists
  ++generation_;
  ++stats_.generations;
  connected_ = true;
  if (!resumed) pending_.reset();
  if (pending_) (void)start_job(true);
  return generation_;
}

bool CheckpointUploader::start_job(bool resumed) {
  if (!connected_ || !pending_) return false;
  job_ = std::make_unique<UploadJob>(generation_, *pending_);
  harvested_ = false;
  if (!job_->start()) {
    job_.reset();
    return false;
  }
  ++stats_.jobs_started;
  if (resumed) ++stats_.resumed_jobs;
  return true;
}

bool CheckpointUploader::start(UploadSpec spec) {
  if (!connected_ || spec.files.empty()) return false;
  retire_job(true);
  pending_ = std::move(spec);
  return start_job(false);
}

bool CheckpointUploader::on_frame(std::uint16_t type, std::span<const std::uint8_t> payload) {
  if (type != T(P::MsgType::SaveUploadAccept) && type != T(P::MsgType::SaveChunkAck) && type != T(P::MsgType::SaveStored)) return false;
  if (job_) (void)job_->feed(generation_, type, payload);
  return true;
}

std::size_t CheckpointUploader::pump(const SendFn& send) {
  std::size_t sent = 0;
  if (job_) {
    std::vector<OutFrame> out;
    job_->take_outbox(out);
    for (auto& f : out) {
      if (f.generation != generation_ || !connected_) {
        ++stats_.stale_writes_blocked;  // never forwarded to a socket it does not belong to
        continue;
      }
      if (send(f.lane, f.type, f.payload)) {
        ++stats_.frames_forwarded;
        ++sent;
      } else {
        ++stats_.send_failures;
      }
    }
    harvest();
  }
  return sent;
}

bool CheckpointUploader::running() const {
  harvest();
  return job_ && !job_->finished();
}

bool CheckpointUploader::pending() const {
  harvest();
  return pending_.has_value();
}

const std::optional<UploadResult>& CheckpointUploader::last_result() const {
  harvest();
  return last_result_;
}

UploaderStats CheckpointUploader::stats() const {
  harvest();
  UploaderStats s = stats_;
  if (job_) s.cross_generation_reads += job_->counters().foreign_frames_rejected;
  return s;
}

}  // namespace x4mp::authority
