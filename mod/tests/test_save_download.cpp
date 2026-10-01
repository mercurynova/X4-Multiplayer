// core/session SaveDownloader (M1-N3): chunk reassembly, acks, resume at an offset from a part file, SHA-256 verify.
// Driven directly with scripted frames and a recording sender: no sockets.

#include <filesystem>
#include <fstream>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/crypto/crypto.h"
#include "core/session/save_download.h"
#include "fake_server.h"
#include "message_ids_generated.h"
#include "session_generated.h"

using namespace x4mp::session;
using fake::T;
using X4MP::Proto::MsgType;

namespace {

constexpr std::size_t kChunk = 256 * 1024;

struct SentFrame {
  std::uint16_t type;
  std::vector<std::uint8_t> payload;
};

struct Recorder {
  std::vector<SentFrame> sent;
  SaveDownloader::SendFn fn() {
    return [this](x4mp::wire::Lane, std::uint16_t type, std::span<const std::uint8_t> p) {
      sent.push_back(SentFrame{type, std::vector<std::uint8_t>(p.begin(), p.end())});
      return true;
    };
  }
  [[nodiscard]] std::vector<const X4MP::Proto::SaveChunkAck*> acks() const {
    std::vector<const X4MP::Proto::SaveChunkAck*> v;
    for (const auto& s : sent) {
      if (s.type == T(MsgType::SaveChunkAck)) v.push_back(flatbuffers::GetRoot<X4MP::Proto::SaveChunkAck>(s.payload.data()));
    }
    return v;
  }
  [[nodiscard]] std::vector<const X4MP::Proto::SaveDownloadRequest*> requests() const {
    std::vector<const X4MP::Proto::SaveDownloadRequest*> v;
    for (const auto& s : sent) {
      if (s.type == T(MsgType::SaveDownloadRequest)) {
        v.push_back(flatbuffers::GetRoot<X4MP::Proto::SaveDownloadRequest>(s.payload.data()));
      }
    }
    return v;
  }
};

struct TempDir {
  std::filesystem::path path;
  explicit TempDir(const char* tag) {
    path = std::filesystem::temp_directory_path() / (std::string("x4mp_test_") + tag);
    std::filesystem::remove_all(path);
    std::filesystem::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    std::filesystem::remove_all(path, ec);
  }
};

std::vector<std::uint8_t> make_data(std::size_t n) {
  std::vector<std::uint8_t> d(n);
  std::uint32_t x = 0x12345678u;
  for (auto& b : d) {
    x = x * 1664525u + 1013904223u;
    b = static_cast<std::uint8_t>(x >> 24);
  }
  return d;
}

DownloadSpec make_spec(const std::vector<std::uint8_t>& data, const std::filesystem::path& final_path) {
  const auto d = x4mp::crypto::sha256(data);
  REQUIRE(d);
  return DownloadSpec{TransferKind::Save, std::vector<std::uint8_t>(d->begin(), d->end()), data.size(), final_path};
}

// Feeds chunks [from, to) bytes of `data` (kChunk each) after an Accept with id `id`.
void serve(SaveDownloader& dl, Recorder& rec, const std::vector<std::uint8_t>& data, std::uint32_t id, std::size_t from,
           std::size_t to) {
  REQUIRE(dl.on_frame(T(MsgType::SaveDownloadAccept), std::span<const std::uint8_t>(fake::download_accept(id, data.size())).subspan(x4mp::wire::kFrameHeaderSize), rec.fn()));
  for (std::size_t off = from; off < to; off += kChunk) {
    const std::size_t n = std::min(kChunk, data.size() - off);
    const auto f = fake::chunk(id, off, std::span<const std::uint8_t>(data).subspan(off, n));
    REQUIRE(dl.on_frame(T(MsgType::SaveChunk), std::span<const std::uint8_t>(f).subspan(x4mp::wire::kFrameHeaderSize), rec.fn()));
  }
}

std::vector<std::uint8_t> read_all(const std::filesystem::path& p) {
  std::ifstream in(p, std::ios::binary);
  return std::vector<std::uint8_t>((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

}  // namespace

TEST_CASE("download: chunks reassemble, acks every 4 chunks and at the end, verified file appears", "[save]") {
  TempDir dir("dl_full");
  const auto data = make_data(kChunk * 9 + 123);  // 10 chunks, the last one partial
  const auto final_path = dir.path / "x4mp_test.xml.gz";
  SaveDownloader dl;
  Recorder rec;
  REQUIRE(dl.begin(make_spec(data, final_path)));
  CHECK(dl.progress().state == DownloadState::Requesting);
  dl.request(rec.fn());
  REQUIRE(rec.requests().size() == 1);
  CHECK(rec.requests()[0]->offset() == 0);
  CHECK(rec.requests()[0]->sha256()->size() == 32);

  serve(dl, rec, data, 77, 0, data.size());
  const auto p = dl.progress();
  CHECK(p.state == DownloadState::Completed);
  CHECK(p.bytes_done == data.size());
  CHECK(p.chunks_received == 10);
  CHECK(std::filesystem::exists(final_path));
  CHECK_FALSE(std::filesystem::exists(final_path.string() + ".part"));
  CHECK(read_all(final_path) == data);

  const auto acks = rec.acks();  // after chunk 4, 8 and the final chunk (10)
  REQUIRE(acks.size() == 3);
  CHECK(acks[0]->transfer_id() == 77);
  CHECK(acks[0]->next_offset() == 4 * kChunk);
  CHECK(acks[1]->next_offset() == 8 * kChunk);
  CHECK(acks[2]->next_offset() == data.size());
}

TEST_CASE("download: a SHA-256 mismatch is rejected, nothing is installed", "[save]") {
  TempDir dir("dl_badsha");
  const auto data = make_data(kChunk * 2 + 5);
  const auto final_path = dir.path / "x4mp_bad.xml.gz";
  auto spec = make_spec(data, final_path);
  spec.sha256[0] ^= 0xFF;  // the server announced a different hash than the bytes it then sends
  SaveDownloader dl;
  Recorder rec;
  REQUIRE(dl.begin(spec));
  dl.request(rec.fn());
  serve(dl, rec, data, 1, 0, data.size());
  const auto p = dl.progress();
  CHECK(p.state == DownloadState::Failed);
  CHECK(p.error == DownloadError::HashMismatch);
  CHECK_FALSE(std::filesystem::exists(final_path));
  CHECK_FALSE(std::filesystem::exists(final_path.string() + ".part"));  // a corrupt part must not be resumed
  CHECK_FALSE(dl.active());
}

TEST_CASE("download: resume after a cut at 50% re-requests at the part offset and sends under 60% again", "[save][resume]") {
  TempDir dir("dl_resume");
  const auto data = make_data(kChunk * 8);  // 2 MiB, 8 chunks
  const auto final_path = dir.path / "x4mp_resume.xml.gz";
  const auto spec = make_spec(data, final_path);

  std::size_t first_attempt_bytes = 0;
  {
    SaveDownloader dl;
    Recorder rec;
    REQUIRE(dl.begin(spec));
    dl.request(rec.fn());
    serve(dl, rec, data, 1, 0, data.size() / 2);  // the connection dies at 50%
    first_attempt_bytes = static_cast<std::size_t>(dl.progress().bytes_done);
    CHECK(first_attempt_bytes == data.size() / 2);
    CHECK(dl.progress().state == DownloadState::Receiving);
    dl.cancel();  // the process goes away; the part file stays
  }
  REQUIRE(std::filesystem::exists(final_path.string() + ".part"));
  CHECK(std::filesystem::file_size(final_path.string() + ".part") == data.size() / 2);

  SaveDownloader dl2;
  Recorder rec2;
  REQUIRE(dl2.begin(spec));
  CHECK(dl2.progress().resumed_from == data.size() / 2);
  dl2.request(rec2.fn());
  REQUIRE(rec2.requests().size() == 1);
  CHECK(rec2.requests()[0]->offset() == data.size() / 2);  // "send the request again with the current offset"
  serve(dl2, rec2, data, 2, data.size() / 2, data.size());
  CHECK(dl2.progress().state == DownloadState::Completed);
  CHECK(read_all(final_path) == data);
  const std::size_t second_attempt_bytes = data.size() - data.size() / 2;
  CHECK(second_attempt_bytes * 100 < data.size() * 60);  // under 60% of the file crossed the wire the second time
}

TEST_CASE("download: a complete part file verifies without any network", "[save][resume]") {
  TempDir dir("dl_partdone");
  const auto data = make_data(kChunk + 10);
  const auto final_path = dir.path / "x4mp_done.xml.gz";
  {
    std::ofstream out(final_path.string() + ".part", std::ios::binary);
    out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
  }
  SaveDownloader dl;
  REQUIRE(dl.begin(make_spec(data, final_path)));
  CHECK(dl.progress().state == DownloadState::Completed);
  CHECK(std::filesystem::exists(final_path));
}

TEST_CASE("download: gaps re-request, duplicates and stale ids are ignored, bad sizes fail", "[save]") {
  TempDir dir("dl_edge");
  const auto data = make_data(kChunk * 4);
  const auto final_path = dir.path / "x4mp_edge.xml.gz";
  SaveDownloader dl;
  Recorder rec;
  REQUIRE(dl.begin(make_spec(data, final_path)));
  dl.request(rec.fn(), 5);
  dl.request(rec.fn(), 5);  // same connection epoch: idempotent
  CHECK(rec.requests().size() == 1);

  const auto accept = fake::download_accept(9, data.size());
  REQUIRE(dl.on_frame(T(MsgType::SaveDownloadAccept), std::span<const std::uint8_t>(accept).subspan(8), rec.fn()));
  const auto c0 = fake::chunk(9, 0, std::span<const std::uint8_t>(data).subspan(0, kChunk));
  const auto c2 = fake::chunk(9, 2 * kChunk, std::span<const std::uint8_t>(data).subspan(2 * kChunk, kChunk));
  const auto stale = fake::chunk(8, kChunk, std::span<const std::uint8_t>(data).subspan(kChunk, kChunk));
  REQUIRE(dl.on_frame(T(MsgType::SaveChunk), std::span<const std::uint8_t>(c0).subspan(8), rec.fn()));
  REQUIRE(dl.on_frame(T(MsgType::SaveChunk), std::span<const std::uint8_t>(c0).subspan(8), rec.fn()));  // duplicate
  CHECK(dl.progress().bytes_done == kChunk);
  REQUIRE(dl.on_frame(T(MsgType::SaveChunk), std::span<const std::uint8_t>(stale).subspan(8), rec.fn()));  // other id
  CHECK(dl.progress().bytes_done == kChunk);
  REQUIRE(dl.on_frame(T(MsgType::SaveChunk), std::span<const std::uint8_t>(c2).subspan(8), rec.fn()));  // gap
  CHECK(dl.progress().bytes_done == kChunk);
  REQUIRE(rec.requests().size() == 2);
  CHECK(rec.requests()[1]->offset() == kChunk);  // asks again at the contiguous offset

  // Unrelated messages are not consumed.
  CHECK_FALSE(dl.on_frame(T(MsgType::Ping), std::span<const std::uint8_t>(accept).subspan(8), rec.fn()));

  // A server announcing another size fails the transfer.
  SaveDownloader dl2;
  REQUIRE(dl2.begin(make_spec(data, dir.path / "x4mp_other.xml.gz")));
  const auto wrong = fake::download_accept(1, data.size() + 1);
  REQUIRE(dl2.on_frame(T(MsgType::SaveDownloadAccept), std::span<const std::uint8_t>(wrong).subspan(8), rec.fn()));
  CHECK(dl2.progress().state == DownloadState::Failed);
  CHECK(dl2.progress().error == DownloadError::SizeMismatch);
}

TEST_CASE("download: bad specs are refused", "[save]") {
  SaveDownloader dl;
  CHECK_FALSE(dl.begin(DownloadSpec{}));
  CHECK(dl.progress().state == DownloadState::Failed);
}
