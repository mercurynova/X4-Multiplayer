#include "authority_driver.h"

#include <algorithm>
#include <cstdio>
#include <fstream>
#include <system_error>

#include "core/authority/entity_spawn.h"
#include "core/crypto/crypto.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::headless {

namespace {
namespace P = X4MP::Proto;
using authority::StoreResult;
constexpr std::uint16_t T(P::MsgType t) { return static_cast<std::uint16_t>(t); }
constexpr int kPhaseLoading = static_cast<int>(P::NodePhase::Loading);
constexpr int kPhaseMatching = static_cast<int>(P::NodePhase::Matching);

std::string hex12(const std::vector<std::uint8_t>& v) { return crypto::to_hex(std::span<const std::uint8_t>(v).first(std::min<std::size_t>(6, v.size()))); }
}  // namespace

std::vector<authority::SectorDesc> headless_sectors() {
  return {
      {1, "cluster_01_sector001_macro", "cluster_01_macro", "Headless One", 1, 0, 0, 0},
      {2, "cluster_02_sector001_macro", "cluster_02_macro", "Headless Two", 1, 10, 0, 0},
  };
}
std::vector<authority::LinkDesc> headless_links() { return {{1, 2}}; }
std::vector<authority::StringDesc> headless_strings() {
  return {{1, static_cast<std::uint8_t>(P::StringKind::Faction), "player"},
          {2, static_cast<std::uint8_t>(P::StringKind::Macro), "ship_arg_s_fighter_01_a_macro"}};
}

AuthorityDriver::AuthorityDriver(session::Session& session, AuthorityDriverOptions options)
    : session_(session), opt_(std::move(options)), started_(std::chrono::steady_clock::now()) {
  work_dir_ = opt_.work_dir;
  if (work_dir_.empty()) {
    std::error_code ec;
    work_dir_ = std::filesystem::temp_directory_path(ec) / "x4mp-headless-authority";
  }
  std::error_code ec;
  std::filesystem::create_directories(work_dir_, ec);
}

void AuthorityDriver::say(const std::string& line) const {
  if (opt_.log) opt_.log(line);
}

double AuthorityDriver::now_game_time() const {
  if (opt_.game_time) return opt_.game_time();
  return 60.0 + std::chrono::duration<double>(std::chrono::steady_clock::now() - started_).count();
}

void AuthorityDriver::fail(std::string why) {
  if (!failed_) {
    failed_ = true;
    failure_ = std::move(why);
    const auto st = uploader_.stats();
    const auto ns = session_.net_status();
    say("authority: FAILED " + failure_ + " [forwarded=" + std::to_string(st.frames_forwarded) + " send_failures=" + std::to_string(st.send_failures) +
        " net.frames_out=" + std::to_string(ns.frames_out) + " net.outbox_dropped=" + std::to_string(ns.outbox_dropped) + "]");
    if (const auto& r = uploader_.last_result()) {
      for (const auto& f : r->files) {
        say(std::string("authority:   file ") + (f.kind == session::TransferKind::Save ? "save" : "manifest") + " answered=" + (f.answered ? "yes" : "no") +
            " result=" + authority::to_string(f.result) + " detail='" + f.detail + "' resume_offset=" + std::to_string(f.resume_offset) +
            " bytes_sent=" + std::to_string(f.bytes_sent));
      }
    }
  }
}

void AuthorityDriver::step() {
  std::vector<session::SessionEvent> events;
  session_.poll(events);
  for (const auto& ev : events) {
    if (opt_.on_event) opt_.on_event(ev);
    handle(ev);
  }
  advance_ready();
  (void)uploader_.pump([this](wire::Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) {
    return session_.send(lane, type, payload) == net::SendResult::Ok;
  });
  const auto st = uploader_.stats();
  while (counted_stored_ < st.checkpoints_stored) {
    ++counted_stored_;
    ++checkpoints_stored_;
    if (const auto& r = uploader_.last_result()) {
      std::string line = "authority: checkpoint stored:";
      for (const auto& f : r->files) {
        line += std::string(" ") + (f.kind == session::TransferKind::Save ? "save" : "manifest") + "=" + authority::to_string(f.result) +
                "(resumed_from=" + std::to_string(f.resume_offset) + ")";
      }
      say(line);
    }
  }
  if (const auto& r = uploader_.last_result(); r && r->state == authority::UploadState::Failed && !uploader_.pending() && !uploader_.running() &&
                                              checkpoints_stored_ == 0) {
    fail(std::string("upload failed: ") + authority::to_string(r->error) + " " + r->detail);
  }
  maybe_spawn();
}

void AuthorityDriver::track_roster(std::span<const std::uint8_t> payload) {
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!v.VerifyBuffer<P::RosterUpdate>(nullptr)) return;
  const auto* roster = flatbuffers::GetRoot<P::RosterUpdate>(payload.data());
  if (roster->players() == nullptr) return;
  for (const auto* p : *roster->players()) {
    if (p->player_id() == session_.welcome().player_id) phase_ = static_cast<int>(p->phase());
  }
}

void AuthorityDriver::handle(const session::SessionEvent& ev) {
  using K = session::SessionEvent::Kind;
  switch (ev.kind) {
    case K::Welcome: {
      const bool resumed = session_.welcome().resumed;
      const auto gen = uploader_.new_connection(resumed);
      say(std::string("authority: connection generation ") + std::to_string(gen) + (resumed ? " (resumed)" : " (fresh join)"));
      if (!resumed) {
        phase_ = -1;
        ready_step_ = 0;
        startup_sent_ = false;
      }
      break;
    }
    case K::NetDisconnected:
      uploader_.connection_lost();
      break;
    case K::Frame:
      if (ev.type == T(P::MsgType::RosterUpdate)) {
        track_roster(ev.payload);
      } else if (ev.type == T(P::MsgType::RequestSave)) {
        on_request_save(ev.payload);
      } else {
        (void)uploader_.on_frame(ev.type, ev.payload);
      }
      break;
    default: break;
  }
}

void AuthorityDriver::advance_ready() {
  if (!session_.has_welcome() || !uploader_.connected()) return;
  const auto now = std::chrono::steady_clock::now();
  const auto send_status = [&](P::NodePhase phase) {
    flatbuffers::FlatBufferBuilder fbb(64);
    fbb.Finish(P::CreateLoadStatusDirect(fbb, phase, 1.0f, 0, "", P::DisconnectCode::None));
    (void)session_.send(wire::Lane::Control, T(P::MsgType::LoadStatus), std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
  };
  switch (ready_step_) {
    case 0:
      send_status(P::NodePhase::Loading);
      ready_at_ = now;
      ready_step_ = 1;
      break;
    case 1:
      if (phase_ >= kPhaseLoading || now - ready_at_ > opt_.phase_wait) {
        send_status(P::NodePhase::Matching);
        ready_at_ = now;
        ready_step_ = 2;
      }
      break;
    case 2:
      if (phase_ >= kPhaseMatching || now - ready_at_ > opt_.phase_wait) {
        flatbuffers::FlatBufferBuilder fbb(64);
        const std::vector<std::uint8_t> none;
        fbb.Finish(P::CreateNodeReadyDirect(fbb, 1, &none));
        (void)session_.send(wire::Lane::Control, T(P::MsgType::NodeReady), std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
        session_.mark_in_session();
        say("authority: sent NodeReady, waiting for RequestSave");
        ready_step_ = 3;
      }
      break;
    default: break;
  }
}

void AuthorityDriver::on_request_save(std::span<const std::uint8_t> payload) {
  const auto req = authority::parse_request_save(payload);
  if (!req) return;
  ++request_saves_;
  if (uploader_.running() || uploader_.pending()) {
    say("authority: RequestSave ignored, an upload is already in progress");
    return;
  }
  const auto save = authority::describe_upload_file(session::TransferKind::Save, opt_.save_file, "x4mp headless save");
  if (!save) {
    fail("cannot read the save file " + opt_.save_file.string());
    return;
  }
  const double game_time = now_game_time();
  // Authority-generated checkpoint id (unique per request).
  std::array<std::uint8_t, 16> rnd{};
  if (!crypto::random_bytes(rnd)) {
    fail("no random source for the checkpoint id");
    return;
  }
  session::Id128 cp;
  std::memcpy(&cp.lo, rnd.data(), 8);
  std::memcpy(&cp.hi, rnd.data() + 8, 8);
  if (cp.zero()) cp.lo = 1;

  const auto manifest = authority::encode_manifest(cp, game_time, next_net_id_, headless_strings(), headless_sectors());
  if (!manifest) {
    fail("invalid game time for the manifest");
    return;
  }
  const auto manifest_path = work_dir_ / ("headless-" + crypto::to_hex(std::span<const std::uint8_t>(rnd).first(6)) + ".x4mf");
  {
    std::ofstream out(manifest_path, std::ios::binary | std::ios::trunc);
    out.write(reinterpret_cast<const char*>(manifest->data()), static_cast<std::streamsize>(manifest->size()));
    if (!out) {
      fail("cannot write the manifest " + manifest_path.string());
      return;
    }
  }
  const auto mf = authority::describe_upload_file(session::TransferKind::Manifest, manifest_path, "manifest");
  if (!mf) {
    fail("cannot read the manifest back");
    return;
  }

  const auto send_control = [&](P::MsgType t, const authority::Payload& p) {
    (void)session_.send(wire::Lane::Control, T(t), std::span<const std::uint8_t>(p));
  };
  if (!startup_sent_) {
    send_control(P::MsgType::StringTableAdd, authority::encode_string_table_add(headless_strings()));
    startup_sent_ = true;
  }
  send_control(P::MsgType::GalaxyMetadata, authority::encode_galaxy_metadata(save->sha256, headless_sectors(), headless_links()));
  const auto started = authority::encode_save_started(req->request_id, cp, game_time, next_net_id_);
  if (!started) {
    fail("invalid game time for SaveStarted");
    return;
  }
  send_control(P::MsgType::SaveStarted, *started);
  say("authority: RequestSave " + std::to_string(req->request_id) + " -> save " + hex12(save->sha256) + " (" + std::to_string(save->size) +
      " bytes), manifest " + std::to_string(mf->size) + " bytes, game_time " + std::to_string(game_time));

  authority::UploadSpec spec;
  spec.checkpoint = cp;
  spec.files = {*save, *mf};
  spec.ghosts_cleaned = true;
  spec.step_timeout = opt_.step_timeout;
  if (!uploader_.start(std::move(spec))) fail("the upload job could not start");
}

void AuthorityDriver::maybe_spawn() {
  if (spawn_sent_ || checkpoints_stored_ == 0 || failed_ || !uploader_.connected()) return;
  const double game_time = now_game_time();
  authority::EntitySpawnBuilder builder(game_time);
  authority::SpawnEntity self;
  self.net_id = next_net_id_;  // the first id after the checkpoint (SaveStarted.next_net_id)
  self.kind = authority::SpawnKind::ShipS;
  self.origin = authority::SpawnOrigin::AuthorityRuntime;
  self.name = "Headless authority ship";
  self.idcode = "HLA-001";
  self.sector = 1;
  (void)builder.add(std::move(self));
  const auto payload = builder.build();
  if (!payload) {
    fail(std::string("self-spawn refused: ") + authority::to_string(payload.error()));
    return;
  }
  (void)session_.send(wire::Lane::Control, T(P::MsgType::EntitySpawn), std::span<const std::uint8_t>(*payload));
  spawn_sent_ = true;
  spawn_game_time_ = game_time;
  say("authority: self-spawn sent net_id=" + std::to_string(next_net_id_) + " game_time=" + std::to_string(game_time));
}

}  // namespace x4mp::headless
