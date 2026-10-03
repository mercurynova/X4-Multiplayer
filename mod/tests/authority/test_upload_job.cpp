// M2-08: UploadJob / CheckpointUploader against an in-process fake server (no sockets): window, resume offsets, one job per
// connection generation, cancel + join on reconnect, no cross-connection reads. The live test against the real server is
// live_authority_upload.cpp.

#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <string>
#include <thread>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/authority/upload_job.h"
#include "core/crypto/crypto.h"
#include "gzip_fixture.h"
#include "message_ids_generated.h"
#include "session_generated.h"

using namespace x4mp::authority;
using namespace std::chrono_literals;
namespace P = X4MP::Proto;
namespace wire = x4mp::wire;

namespace {

constexpr std::uint16_t T(P::MsgType t) { return static_cast<std::uint16_t>(t); }
constexpr std::uint32_t kChunk = 4096;

std::vector<std::uint8_t> bytes_of(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

// What the real server's upload side does (UploadSession.cs), in memory: bytes already received survive a dropped connection
// and are reported as resume_offset when the same (checkpoint, kind) is begun again.
class FakeUploadServer {
 public:
  struct Stored {
    std::vector<std::uint8_t> data;
    bool complete = false;
  };

  // Behaviour knobs for hostile cases.
  bool never_ack = false;                     // swallow chunks without acks (the job parks on a full window)
  P::SaveStoreResult end_result = P::SaveStoreResult::Stored;
  bool answer_begin = true;
  bool stale_stored_before_accept = false;
  bool bulk_after_control = false;            // chunks are processed only after the Control frames sent with them
  std::uint32_t window = 8;

  std::map<std::pair<std::uint64_t, int>, Stored> store;  // (checkpoint lo, kind)
  std::uint64_t max_unacked = 0;
  std::uint64_t chunks = 0;
  std::uint32_t begins = 0;
  std::vector<std::uint64_t> begin_resume_offsets;

  // The uploader's frames in; the server's answers go to `reply` (which feeds them back to the uploader, tagged by the caller).
  template <class Reply>
  bool handle(wire::Lane, std::uint16_t type, std::span<const std::uint8_t> payload, Reply&& reply) {
    if (type == T(P::MsgType::SaveUploadBegin)) {
      const auto* b = flatbuffers::GetRoot<P::SaveUploadBegin>(payload.data());
      ++begins;
      cur_key_ = {b->checkpoint_id()->lo(), static_cast<int>(b->kind())};
      cur_size_ = b->size();
      cur_sha_.assign(b->sha256()->begin(), b->sha256()->end());
      const auto& st = store[cur_key_];
      upload_id_ = ++next_id_;
      last_ack_ = st.data.size();
      begin_resume_offsets.push_back(st.data.size());
      since_ack_ = 0;
      if (!answer_begin) return true;
      if (stale_stored_before_accept) {
        // The server reports the end of the upload the dropped connection left behind (an older upload id) to the new connection.
        flatbuffers::FlatBufferBuilder sfb(128);
        const P::Id128 scp(cur_key_.first, 0);
        sfb.Finish(P::CreateSaveStoredDirect(sfb, 77, &scp, static_cast<P::UploadKind>(cur_key_.second), P::SaveStoreResult::Aborted, "incomplete"));
        reply(T(P::MsgType::SaveStored), bytes_of(sfb));
      }
      flatbuffers::FlatBufferBuilder fbb(128);
      fbb.Finish(P::CreateSaveUploadAccept(fbb, upload_id_, kChunk, st.data.size(), static_cast<std::uint8_t>(window)));
      reply(T(P::MsgType::SaveUploadAccept), bytes_of(fbb));
    } else if (type == T(P::MsgType::SaveChunk)) {
      if (bulk_after_control) {
        deferred_.emplace_back(payload.begin(), payload.end());  // the Bulk lane is drained after Control frames (protocol.md 3.4)
      } else {
        on_chunk(payload, reply);
      }
    } else if (type == T(P::MsgType::SaveUploadEnd)) {
      auto& st = store[cur_key_];
      P::SaveStoreResult result = end_result;
      if (st.data.size() != cur_size_) result = P::SaveStoreResult::Aborted;
      else if (result == P::SaveStoreResult::Stored) {
        const auto d = x4mp::crypto::sha256(st.data);
        if (!d || std::vector<std::uint8_t>(d->begin(), d->end()) != cur_sha_) result = P::SaveStoreResult::HashMismatch;
      }
      st.complete = result == P::SaveStoreResult::Stored;
      flatbuffers::FlatBufferBuilder fbb(128);
      const P::Id128 cp(cur_key_.first, 0);
      fbb.Finish(P::CreateSaveStoredDirect(fbb, upload_id_, &cp, static_cast<P::UploadKind>(cur_key_.second), result, ""));
      reply(T(P::MsgType::SaveStored), bytes_of(fbb));
    }
    return true;
  }

  template <class Reply>
  void on_chunk(std::span<const std::uint8_t> payload, Reply&& reply) {
    const auto* c = flatbuffers::GetRoot<P::SaveChunk>(payload.data());
    auto& st = store[cur_key_];
    if (c->transfer_id() != upload_id_ || c->offset() != st.data.size()) return;  // out of order: ignored
    st.data.insert(st.data.end(), c->data()->begin(), c->data()->end());
    ++chunks;
    ++since_ack_;
    max_unacked = std::max<std::uint64_t>(max_unacked, st.data.size() - last_ack_);
    if (!never_ack && (since_ack_ >= 4 || st.data.size() == cur_size_)) {
      since_ack_ = 0;
      last_ack_ = st.data.size();
      flatbuffers::FlatBufferBuilder fbb(64);
      fbb.Finish(P::CreateSaveChunkAck(fbb, upload_id_, st.data.size()));
      reply(T(P::MsgType::SaveChunkAck), bytes_of(fbb));
    }
  }

  // Processes the chunks that were held back behind the Control frames of the same batch.
  template <class Reply>
  void flush_bulk(Reply&& reply) {
    auto held = std::move(deferred_);
    deferred_.clear();
    for (const auto& p : held) on_chunk(p, reply);
  }

 private:
  std::vector<std::vector<std::uint8_t>> deferred_;
  std::pair<std::uint64_t, int> cur_key_{};
  std::uint64_t cur_size_ = 0;
  std::vector<std::uint8_t> cur_sha_;
  std::uint32_t upload_id_ = 0;
  std::uint32_t next_id_ = 0;
  std::uint64_t last_ack_ = 0;
  std::uint32_t since_ack_ = 0;
};

struct Fixture {
  std::filesystem::path dir;
  UploadFile save, manifest;
  Fixture(std::uint64_t save_bytes, std::uint64_t seed) {
    dir = std::filesystem::temp_directory_path() / ("x4mp-upload-test-" + std::to_string(seed) + "-" + std::to_string(save_bytes));
    std::filesystem::create_directories(dir);
    const auto sp = (dir / "save.xml.gz").string();
    REQUIRE(x4mp::testing::write_stored_gzip(sp, save_bytes, seed));
    save = *describe_upload_file(x4mp::session::TransferKind::Save, sp, "save");
    const auto mp = dir / "manifest.x4mf";
    {
      std::ofstream out(mp, std::ios::binary | std::ios::trunc);
      const std::uint8_t m[] = {8, 0, 0, 0, 'X', '4', 'M', 'F', 0, 0, 0, 0};
      out.write(reinterpret_cast<const char*>(m), sizeof(m));
    }
    manifest = *describe_upload_file(x4mp::session::TransferKind::Manifest, mp, "manifest");
  }
  ~Fixture() {
    std::error_code ec;
    std::filesystem::remove_all(dir, ec);
  }
  UploadSpec spec(std::chrono::milliseconds timeout = 20s) const {
    UploadSpec s;
    s.checkpoint = {0xABCD, 0x1234};
    s.files = {save, manifest};
    s.step_timeout = timeout;
    return s;
  }
};

// Drives uploader + server like the game loop does: pump the uploader into the server; the server's answers are routed back to
// the uploader like frames received on the current connection.
struct Rig {
  CheckpointUploader up;
  FakeUploadServer server;
  std::uint64_t answered_generation = 0;

  std::size_t pump_once() {
    const auto reply = [&](std::uint16_t t, const std::vector<std::uint8_t>& p) { up.on_frame(t, p); };
    const auto n = up.pump([&](wire::Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) {
      return server.handle(lane, type, payload, reply);
    });
    server.flush_bulk(reply);
    return n;
  }
  template <class Pred>
  bool run_until(Pred&& pred, std::chrono::milliseconds timeout = 20s) {
    const auto end = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < end) {
      pump_once();
      if (pred()) return true;
      std::this_thread::yield();
    }
    return pred();
  }
};

}  // namespace

TEST_CASE("upload: a checkpoint (save + manifest) is stored and the window never exceeds 8 chunks", "[authority][upload]") {
  Fixture fx(300 * 1024, 1);
  Rig rig;
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec()));
  REQUIRE(rig.run_until([&] { return !rig.up.pending() && !rig.up.running(); }));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Completed);
  REQUIRE(r->files.size() == 2);
  CHECK(r->files[0].result == StoreResult::Stored);
  CHECK(r->files[1].result == StoreResult::Stored);
  CHECK(r->files[0].bytes_sent == fx.save.size);
  CHECK(rig.server.store[{0xABCD, 0}].data.size() == fx.save.size);
  CHECK(rig.server.store[{0xABCD, 0}].complete);
  CHECK(rig.server.store[{0xABCD, 1}].complete);
  CHECK(rig.server.max_unacked <= 8ull * kChunk);
  CHECK(rig.server.max_unacked >= 4ull * kChunk);  // several chunks go out before the first ack (the window is used)
  const auto st = rig.up.stats();
  CHECK(st.checkpoints_stored == 1);
  CHECK(st.cross_generation_reads == 0);
  CHECK(st.stale_writes_blocked == 0);
}

TEST_CASE("upload: a dropped connection resumes from the server's offset on the next generation", "[authority][upload][resume]") {
  Fixture fx(400 * 1024, 2);
  Rig rig;
  const auto gen1 = rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec()));
  // Let part of the save through, then drop the link.
  REQUIRE(rig.run_until([&] { return rig.server.store[{0xABCD, 0}].data.size() >= 100 * 1024; }));
  rig.up.connection_lost();
  CHECK_FALSE(rig.up.running());
  CHECK(rig.up.pending());
  const auto have = rig.server.store[{0xABCD, 0}].data.size();
  REQUIRE(have < fx.save.size);

  const auto gen2 = rig.up.new_connection(true);
  CHECK(gen2 == gen1 + 1);
  REQUIRE(rig.run_until([&] { return !rig.up.pending() && !rig.up.running(); }));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Completed);
  CHECK(r->files[0].resume_offset > 0);
  CHECK(r->files[0].resume_offset == have);  // exactly what the server held when the link dropped
  CHECK(r->files[0].bytes_sent == fx.save.size - r->files[0].resume_offset);
  CHECK(rig.server.store[{0xABCD, 0}].complete);
  const auto st = rig.up.stats();
  CHECK(st.resumed_jobs == 1);
  CHECK(st.jobs_cancelled == 1);
  CHECK(st.cross_generation_reads == 0);
  CHECK(st.stale_writes_blocked == 0);
}

TEST_CASE("upload: the old job, parked on a full window, never consumes the resumed job's SaveUploadAccept", "[authority][upload][resume]") {
  // The FakeNode bug: job 1 waits for a SaveChunkAck that never comes; the new connection's SaveUploadAccept must not land there.
  Fixture fx(512 * 1024, 3);
  Rig rig;
  rig.server.never_ack = true;
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec()));
  REQUIRE(rig.run_until([&] { return rig.server.chunks >= 8; }));  // window full: the job parks waiting for an ack that never comes
  CHECK(rig.up.running());
  (void)rig.run_until([] { return false; }, 100ms);  // give a runaway job time to show itself
  CHECK(rig.server.chunks == 8);                      // exactly the 8-chunk window went out, then it waits

  rig.up.connection_lost();
  rig.server.never_ack = false;
  rig.up.new_connection(true);  // cancels + joins before this returns, then the resumed job starts
  REQUIRE(rig.run_until([&] { return !rig.up.pending() && !rig.up.running(); }));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Completed);
  CHECK(r->files[0].resume_offset == 8ull * kChunk);
  const auto st = rig.up.stats();
  CHECK(st.cross_generation_reads == 0);
  CHECK(st.stale_writes_blocked == 0);
  CHECK(st.jobs_cancelled == 1);
}

TEST_CASE("upload: new_connection joins the old job before returning", "[authority][upload][resume]") {
  Fixture fx(256 * 1024, 4);
  Rig rig;
  rig.server.answer_begin = false;  // nobody answers: the job parks on SaveUploadAccept
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec(60s)));
  REQUIRE(rig.run_until([&] { return rig.server.begins >= 1; }));
  REQUIRE(rig.up.running());
  const auto t0 = std::chrono::steady_clock::now();
  rig.up.new_connection(true);
  // Cancel + join took no longer than a moment, not the 60 s step timeout, and the new job (same pending upload) is the only one running.
  CHECK(std::chrono::steady_clock::now() - t0 < 2s);
  CHECK(rig.up.stats().jobs_cancelled == 1);
  CHECK(rig.up.stats().jobs_started == 2);
  CHECK(rig.up.running());
  rig.up.connection_lost();
  CHECK_FALSE(rig.up.running());
}

TEST_CASE("upload: a job rejects frames of another generation and counts them", "[authority][upload][resume]") {
  Fixture fx(64 * 1024, 5);
  UploadJob job(7, fx.spec());
  REQUIRE(job.start());
  flatbuffers::FlatBufferBuilder fbb(64);
  fbb.Finish(P::CreateSaveUploadAccept(fbb, 1, kChunk, 0, 8));
  const auto accept = bytes_of(fbb);
  CHECK_FALSE(job.feed(8, T(P::MsgType::SaveUploadAccept), accept));  // another connection's frame
  CHECK_FALSE(job.feed(6, T(P::MsgType::SaveUploadAccept), accept));
  CHECK(job.counters().foreign_frames_rejected == 2);
  CHECK(job.counters().frames_fed == 0);
  CHECK(job.feed(7, T(P::MsgType::SaveUploadAccept), accept));
  CHECK(job.counters().frames_fed == 1);
  CHECK_FALSE(job.feed(7, T(P::MsgType::Ping), accept));  // not an upload message
  job.cancel();
  job.join();
  CHECK(job.state() == UploadState::Cancelled);
  // After the end nothing is accepted (and the frame is not counted as foreign).
  CHECK_FALSE(job.feed(7, T(P::MsgType::SaveUploadAccept), accept));
}

TEST_CASE("upload: frames of the old job's outbox are never forwarded to the new connection", "[authority][upload][resume]") {
  Fixture fx(200 * 1024, 6);
  CheckpointUploader up;
  up.new_connection(false);
  REQUIRE(up.start(fx.spec()));
  // The job queues SaveUploadBegin (and waits). The link drops before the owner pumped: whatever it queued dies with the old job.
  up.connection_lost();
  std::size_t forwarded = 0;
  up.new_connection(false);  // fresh join: pending dropped, no job
  forwarded += up.pump([&](wire::Lane, std::uint16_t, std::span<const std::uint8_t>) { return true; });
  CHECK(forwarded == 0);
  CHECK_FALSE(up.running());
  CHECK_FALSE(up.pending());
}

TEST_CASE("upload: a fresh join drops a pending upload, a resumed one restarts it", "[authority][upload]") {
  Fixture fx(64 * 1024, 7);
  CheckpointUploader up;
  up.new_connection(false);
  REQUIRE(up.start(fx.spec()));
  up.connection_lost();
  CHECK(up.pending());
  up.new_connection(false);
  CHECK_FALSE(up.pending());
  CHECK_FALSE(up.running());
  REQUIRE(up.start(fx.spec()));
  up.connection_lost();
  up.new_connection(true);
  CHECK(up.running());
  CHECK(up.stats().resumed_jobs == 1);
}

TEST_CASE("upload: start refuses without a connection or without files", "[authority][upload]") {
  Fixture fx(8 * 1024, 8);
  CheckpointUploader up;
  CHECK_FALSE(up.start(fx.spec()));  // never connected
  up.new_connection(false);
  UploadSpec empty;
  CHECK_FALSE(up.start(empty));
  up.connection_lost();
  CHECK_FALSE(up.start(fx.spec()));
}

TEST_CASE("upload: a silent server ends the job with Timeout and the upload stays pending", "[authority][upload]") {
  Fixture fx(64 * 1024, 9);
  Rig rig;
  rig.server.answer_begin = false;
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec(150ms)));
  REQUIRE(rig.run_until([&] { return !rig.up.running(); }, 5s));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Failed);
  CHECK(r->error == UploadError::Timeout);
  CHECK(rig.up.pending());  // resumable on the next connection
}

TEST_CASE("upload: a refused save (HashMismatch) fails the job and is not retried", "[authority][upload]") {
  Fixture fx(64 * 1024, 10);
  Rig rig;
  rig.server.end_result = P::SaveStoreResult::HashMismatch;
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec()));
  REQUIRE(rig.run_until([&] { return !rig.up.running(); }));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Failed);
  CHECK(r->error == UploadError::Refused);
  CHECK(r->files[0].result == StoreResult::HashMismatch);
  CHECK_FALSE(rig.up.pending());
  CHECK(rig.up.stats().checkpoints_stored == 0);
}

TEST_CASE("upload: an already complete file resumes with an immediate SaveUploadEnd", "[authority][upload][resume]") {
  Fixture fx(64 * 1024, 11);
  Rig rig;
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec()));
  REQUIRE(rig.run_until([&] { return rig.server.store[{0xABCD, 0}].data.size() == fx.save.size; }));
  rig.up.connection_lost();  // cut right after the last chunk, before the SaveStored reached us
  rig.up.new_connection(true);
  REQUIRE(rig.run_until([&] { return !rig.up.pending() && !rig.up.running(); }));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Completed);
  CHECK(r->files[0].resume_offset == fx.save.size);
  CHECK(r->files[0].bytes_sent == 0);
}

TEST_CASE("upload: a SaveStored of an older upload id is not the answer to this Begin", "[authority][upload][resume]") {
  // Seen against the real server: after a drop, the end of the abandoned upload ("Aborted: incomplete") reaches the NEW connection.
  Fixture fx(64 * 1024, 13);
  Rig rig;
  rig.server.stale_stored_before_accept = true;
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec()));
  REQUIRE(rig.run_until([&] { return !rig.up.pending() && !rig.up.running(); }));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Completed);
  CHECK(r->files[0].result == StoreResult::Stored);
}

TEST_CASE("upload: SaveUploadEnd never overtakes the last chunks (Control lane vs Bulk lane)", "[authority][upload]") {
  // Seen against the real server: the net layer sends Bulk only when Control is empty, so an End queued right after the last chunk
  // reached the server first and ended the upload "incomplete". The job must wait for the ack of the final offset.
  Fixture fx(300 * 1024, 14);
  Rig rig;
  rig.server.bulk_after_control = true;
  rig.up.new_connection(false);
  REQUIRE(rig.up.start(fx.spec()));
  REQUIRE(rig.run_until([&] { return !rig.up.pending() && !rig.up.running(); }));
  const auto& r = rig.up.last_result();
  REQUIRE(r.has_value());
  CHECK(r->state == UploadState::Completed);
  CHECK(r->files[0].result == StoreResult::Stored);
  CHECK(r->files[1].result == StoreResult::Stored);
}

TEST_CASE("upload: parsing and describing helpers", "[authority][upload]") {
  Fixture fx(10 * 1024, 12);
  CHECK(fx.save.size > 10 * 1024);
  CHECK(fx.save.sha256.size() == 32);
  CHECK_FALSE(describe_upload_file(x4mp::session::TransferKind::Save, fx.dir / "missing", "x").has_value());
  CHECK(std::string(to_string(StoreResult::HashMismatch)) == "HashMismatch");
  CHECK(is_success(StoreResult::StoredNotCurrent));
  CHECK_FALSE(is_success(StoreResult::NotASave));
}
