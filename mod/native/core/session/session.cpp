#include "core/session/session.h"

#include <algorithm>
#include <chrono>
#include <deque>
#include <system_error>
#include <utility>

#include <nlohmann/json.hpp>

#include "control_generated.h"
#include "core/crypto/crypto.h"
#include "core/log/log.h"
#include "message_ids_generated.h"
#include "session_generated.h"
#include "x4mp/wire.h"

namespace x4mp::session {

// =====================================================================================================================
// state machine
// =====================================================================================================================
const char* to_string(State state) noexcept {
  switch (state) {
    case State::Disconnected: return "Disconnected";
    case State::Connecting: return "Connecting";
    case State::Handshaking: return "Handshaking";
    case State::Joining: return "Joining";
    case State::InSession: return "InSession";
    case State::Reconnecting: return "Reconnecting";
  }
  return "?";
}

bool can_transition(State from, State to) noexcept {
  if (to == State::Disconnected) return from != State::Disconnected;  // any live state can drop (or be halted)
  switch (from) {
    case State::Disconnected: return to == State::Connecting;
    case State::Connecting: return to == State::Handshaking;
    case State::Handshaking: return to == State::Joining || to == State::Reconnecting;
    case State::Joining: return to == State::InSession || to == State::Reconnecting;
    case State::InSession: return to == State::Reconnecting;
    case State::Reconnecting: return to == State::Handshaking;
  }
  return false;
}

// =====================================================================================================================
// stash
// =====================================================================================================================
void MemoryStash::put(std::string_view key, std::string_view value) {
  const std::lock_guard lock(mutex_);
  for (auto& [k, v] : items_) {
    if (k == key) {
      v = std::string(value);
      return;
    }
  }
  items_.emplace_back(std::string(key), std::string(value));
}

std::optional<std::string> MemoryStash::get(std::string_view key) const {
  const std::lock_guard lock(mutex_);
  for (const auto& [k, v] : items_) {
    if (k == key) return v;
  }
  return std::nullopt;
}

void MemoryStash::erase(std::string_view key) {
  const std::lock_guard lock(mutex_);
  std::erase_if(items_, [&](const auto& kv) { return kv.first == key; });
}

namespace {

std::string id_hex(const Id128& id) {
  std::uint8_t b[16];
  for (int i = 0; i < 8; ++i) {
    b[i] = static_cast<std::uint8_t>(id.lo >> (8 * i));
    b[8 + i] = static_cast<std::uint8_t>(id.hi >> (8 * i));
  }
  return crypto::to_hex(std::span<const std::uint8_t>(b, 16));
}

bool id_from_hex(std::string_view hex, Id128& out) {
  std::vector<std::uint8_t> b;
  if (!crypto::from_hex(hex, b) || b.size() != 16) return false;
  out = Id128{};
  for (int i = 0; i < 8; ++i) {
    out.lo |= static_cast<std::uint64_t>(b[static_cast<std::size_t>(i)]) << (8 * i);
    out.hi |= static_cast<std::uint64_t>(b[static_cast<std::size_t>(8 + i)]) << (8 * i);
  }
  return true;
}

}  // namespace

bool save_intent(IStash& stash, const SessionIntent& intent) {
  try {
    nlohmann::json j;
    j["connect"] = intent.connect;
    j["host"] = intent.host;
    j["port"] = intent.port;
    j["name"] = intent.name;
    j["roles"] = intent.roles;
    j["resume_token"] = id_hex(intent.resume_token);
    j["session_id"] = id_hex(intent.session_id);
    j["last_journal_seq"] = intent.last_journal_seq;
    j["auth_hash"] = intent.auth_hash_hex;
    j["admin_hash"] = intent.admin_hash_hex;
    stash.put(kStashIntentKey, j.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace));
    return true;
  } catch (...) {
    return false;
  }
}

std::optional<SessionIntent> load_intent(const IStash& stash) {
  try {
    const auto text = stash.get(kStashIntentKey);
    if (!text) return std::nullopt;
    const auto j = nlohmann::json::parse(*text, nullptr, false);
    if (!j.is_object()) return std::nullopt;
    SessionIntent in;
    in.connect = j.value("connect", false);
    if (!in.connect) return std::nullopt;
    in.host = j.value("host", std::string{});
    in.port = static_cast<std::uint16_t>(j.value("port", 47780));
    in.name = j.value("name", std::string{});
    in.roles = static_cast<std::uint8_t>(j.value("roles", 0));
    in.last_journal_seq = j.value("last_journal_seq", std::uint64_t{0});
    in.auth_hash_hex = j.value("auth_hash", std::string{});
    in.admin_hash_hex = j.value("admin_hash", std::string{});
    if (!id_from_hex(j.value("resume_token", std::string{}), in.resume_token)) return std::nullopt;
    if (!id_from_hex(j.value("session_id", std::string{}), in.session_id)) return std::nullopt;
    return in;
  } catch (...) {
    return std::nullopt;
  }
}

void clear_intent(IStash& stash) { stash.erase(kStashIntentKey); }

// =====================================================================================================================
// parsers
// =====================================================================================================================
namespace {

std::string str(const flatbuffers::String* s) { return s ? s->str() : std::string{}; }

std::vector<std::uint8_t> bytes(const flatbuffers::Vector<std::uint8_t>* v) {
  return v ? std::vector<std::uint8_t>(v->begin(), v->end()) : std::vector<std::uint8_t>{};
}

Id128 id(const X4MP::Proto::Id128* v) { return v ? Id128{v->lo(), v->hi()} : Id128{}; }

std::uint16_t T(X4MP::Proto::MsgType t) noexcept { return static_cast<std::uint16_t>(t); }

}  // namespace

std::optional<ServerInfo> parse_server_hello(std::span<const std::uint8_t> payload) {
  if (payload.size() < 8) return std::nullopt;
  const auto* h = flatbuffers::GetRoot<X4MP::Proto::ServerHello>(payload.data());
  ServerInfo s;
  s.protocol_major = h->protocol_major();
  s.protocol_minor = h->protocol_minor();
  s.server_version = str(h->server_version());
  s.server_name = str(h->server_name());
  s.session_id = id(h->session_id());
  s.auth = static_cast<std::uint8_t>(h->auth());
  s.server_caps = h->server_caps();
  s.phase = static_cast<std::uint8_t>(h->phase());
  s.required_game_build = str(h->required_game_build());
  s.nonce = bytes(h->nonce());
  return s;
}

std::optional<WelcomeInfo> parse_welcome(std::span<const std::uint8_t> payload) {
  if (payload.size() < 8) return std::nullopt;
  const auto* w = flatbuffers::GetRoot<X4MP::Proto::Welcome>(payload.data());
  WelcomeInfo o;
  o.player_id = w->player_id();
  o.granted_roles = static_cast<std::uint8_t>(w->granted_roles());
  o.negotiated_caps = w->negotiated_caps();
  o.resume_token = id(w->resume_token());
  o.resumed = w->resumed();
  o.conn_id = w->conn_id();
  o.udp_port = w->udp_port();
  o.udp_token = w->udp_token();
  o.server_time_us = w->server_time_us();
  o.heartbeat_interval_ms = w->heartbeat_interval_ms();
  o.heartbeat_timeout_ms = w->heartbeat_timeout_ms();
  o.resume_grace_s = w->resume_grace_s();
  o.http_base_url = str(w->http_base_url());
  o.max_ghosts = w->max_ghosts();
  o.team_id = w->team_id();
  o.team_role = static_cast<std::uint8_t>(w->team_role());
  o.faction_slot = w->faction_slot();
  return o;
}

std::optional<SaveInfo> parse_save_info(std::span<const std::uint8_t> payload) {
  if (payload.size() < 8) return std::nullopt;
  const auto* i = flatbuffers::GetRoot<X4MP::Proto::SessionSaveInfo>(payload.data());
  SaveInfo s;
  s.checkpoint_id = id(i->checkpoint_id());
  s.sha256 = bytes(i->sha256());
  s.size = i->size();
  s.display_name = str(i->display_name());
  s.local_file_name = str(i->local_file_name());
  s.manifest_sha256 = bytes(i->manifest_sha256());
  s.manifest_size = i->manifest_size();
  s.http_url = str(i->http_url());
  return s;
}

// =====================================================================================================================
// Session::Impl
// =====================================================================================================================
struct Session::Impl {
  struct Report {
    bool replayed = false;
    std::uint64_t count = 0;
  };

  explicit Impl(SessionOptions o) : opt(std::move(o)) {}

  SessionOptions opt;
  net::NetClient net;
  SaveDownloader downloader;
  ExtensionSnapshot ext;

  // Net-thread-owned handshake state (initialised before the thread starts, then only the hook touches it).
  Id128 net_resume;
  std::uint64_t net_last_journal_seq = 0;
  std::optional<crypto::Sha256Digest> auth_hash;
  std::optional<crypto::Sha256Digest> admin_hash;

  // Net thread -> main.
  std::atomic<std::uint32_t> welcomed_epoch{0};
  std::mutex report_mutex;
  std::vector<Report> reports;

  // Main-thread state.
  bool started = false;
  bool was_in_session = false;
  std::deque<DownloadSpec> jobs;
  bool job_active = false;
  std::optional<SaveInfo> save_info;
  std::chrono::steady_clock::time_point last_progress_emit{};
  std::uint64_t last_progress_bytes = ~std::uint64_t{0};

  // ---------------------------------------------------------------------------------------------------------------
  // net thread
  // ---------------------------------------------------------------------------------------------------------------
  void on_server_hello(std::span<const std::uint8_t> payload, net::HookContext& ctx) {
    const auto sh = parse_server_hello(payload);
    if (!sh) return;
    const std::span<const std::uint8_t> nonce(sh->nonce);
    const std::span<const std::uint8_t> key(opt.player_key);

    std::vector<std::uint8_t> auth_proof;
    std::vector<std::uint8_t> admin_proof;
    if (sh->auth == static_cast<std::uint8_t>(X4MP::Proto::AuthMethod::SessionPassword) && auth_hash) {
      if (const auto p = crypto::compute_proof(std::span<const std::uint8_t>(*auth_hash), nonce, key)) {
        auth_proof.assign(p->begin(), p->end());
      }
    }
    if (admin_hash) {
      if (const auto p = crypto::compute_proof(std::span<const std::uint8_t>(*admin_hash), nonce, key)) {
        admin_proof.assign(p->begin(), p->end());
      }
    }

    flatbuffers::FlatBufferBuilder fbb(1024);
    std::vector<flatbuffers::Offset<flatbuffers::String>> ext_strings;
    ext_strings.reserve(ext.entries.size());
    for (const auto& e : ext.entries) ext_strings.push_back(fbb.CreateString(e));
    const std::vector<std::uint8_t> player_key(opt.player_key.begin(), opt.player_key.end());
    const X4MP::Proto::Id128 token(net_resume.lo, net_resume.hi);
    const std::vector<flatbuffers::Offset<X4MP::Proto::SaveRef>> cached;
    const auto& idn = opt.identity;
    fbb.Finish(X4MP::Proto::CreateClientHelloDirect(
        fbb, wire::kProtocolMajor, wire::kProtocolMinor, idn.mod_version.c_str(), idn.mod_build.c_str(),
        idn.game_version.c_str(), idn.game_build.c_str(), idn.x4native_version.c_str(), idn.platform.c_str(),
        &ext.hash, &ext_strings, &player_key, opt.player_name.c_str(),
        static_cast<X4MP::Proto::Role>(opt.requested_roles), opt.client_caps, &auth_proof, &admin_proof, &token,
        net_last_journal_seq, &opt.loaded_save_sha256, &cached, opt.preferred_team));
    (void)ctx.send(wire::Lane::Control, T(X4MP::Proto::MsgType::ClientHello),
                   std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
    // Pings may start only now: the server treats any frame but ClientHello as UnexpectedMessage before it.
    net.enable_heartbeat();
  }

  void on_welcome(std::span<const std::uint8_t> payload, net::HookContext& ctx) {
    const auto w = parse_welcome(payload);
    if (!w) return;
    net_resume = w->resume_token;
    // Reliable-Control retention: replay after a resume, drop after a fresh join. Nothing queued by the main
    // thread has reached the wire before this point (the gate), so ClientHello was the first frame.
    const std::size_t n = ctx.release_outbox(w->resumed);
    if (n > 0) {
      const std::lock_guard lock(report_mutex);
      reports.push_back(Report{w->resumed, n});
    }
    const std::uint32_t epoch = net.status().connections;
    welcomed_epoch.store(epoch);
    // A download in flight asks again at its contiguous offset (once per connection epoch).
    downloader.request(
        [&ctx](wire::Lane l, std::uint16_t t, std::span<const std::uint8_t> p) { return ctx.send(l, t, p); }, epoch);
  }

  void on_net_frame(std::uint16_t type, wire::Lane, std::span<const std::uint8_t> payload, net::HookContext& ctx) {
    using X4MP::Proto::MsgType;
    if (type == T(MsgType::ServerHello)) {
      on_server_hello(payload, ctx);
    } else if (type == T(MsgType::Welcome)) {
      on_welcome(payload, ctx);
    } else if (type == T(MsgType::Disconnect)) {
      const auto* d = flatbuffers::GetRoot<X4MP::Proto::Disconnect>(payload.data());
      if (static_cast<std::uint16_t>(d->code()) == net::kDisconnectResumeExpired) net_resume = Id128{};  // fresh join next
    } else if (type == T(MsgType::SaveDownloadAccept) || type == T(MsgType::SaveChunk)) {
      const bool consumed = downloader.on_frame(
          type, payload,
          [&ctx](wire::Lane l, std::uint16_t t, std::span<const std::uint8_t> p) { return ctx.send(l, t, p); });
      if (consumed) ctx.consume_frame();
    }
  }

  // ---------------------------------------------------------------------------------------------------------------
  // main thread helpers
  // ---------------------------------------------------------------------------------------------------------------
  [[nodiscard]] bool main_send(wire::Lane l, std::uint16_t t, std::span<const std::uint8_t> p) {
    return net.send(l, t, p) == net::SendResult::Ok;
  }

  void request_if_welcomed() {
    const net::NetStatus st = net.status();
    const std::uint32_t e = welcomed_epoch.load();
    if (st.state == net::ConnState::Connected && e != 0 && e == st.connections) {
      downloader.request(
          [this](wire::Lane l, std::uint16_t t, std::span<const std::uint8_t> p) { return main_send(l, t, p); }, e);
    }
  }

  static std::filesystem::path manifest_name(const std::vector<std::uint8_t>& sha) {
    return std::filesystem::path("x4mp_" + crypto::to_hex(std::span<const std::uint8_t>(sha).first(std::min<std::size_t>(sha.size(), 6))) + ".x4mf");
  }

  static bool file_matches(const std::filesystem::path& file, std::uint64_t size, const std::vector<std::uint8_t>& sha) {
    std::error_code ec;
    if (!std::filesystem::is_regular_file(file, ec)) return false;
    if (static_cast<std::uint64_t>(std::filesystem::file_size(file, ec)) != size || ec) return false;
    const auto d = crypto::sha256_file(file);
    return d && sha.size() == d->size() && std::equal(d->begin(), d->end(), sha.begin());
  }

  void send_save_ready(SessionEvent* ev_out_unused = nullptr) {
    (void)ev_out_unused;
    if (!save_info) return;
    flatbuffers::FlatBufferBuilder fbb(128);
    fbb.Finish(X4MP::Proto::CreateSaveReadyDirect(fbb, &save_info->sha256, &save_info->manifest_sha256));
    (void)net.send(wire::Lane::Control, T(X4MP::Proto::MsgType::SaveReady),
                   std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
  }

  void send_load_failed(std::uint16_t code, std::string_view detail) {
    flatbuffers::FlatBufferBuilder fbb(128);
    const std::string d(detail);
    fbb.Finish(X4MP::Proto::CreateLoadStatusDirect(fbb, X4MP::Proto::NodePhase::Failed, 0.0f, 0, d.c_str(),
                                                   static_cast<X4MP::Proto::DisconnectCode>(code)));
    (void)net.send(wire::Lane::Control, T(X4MP::Proto::MsgType::LoadStatus),
                   std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
  }
};

// =====================================================================================================================
// Session
// =====================================================================================================================
Session::Session(SessionOptions options) : impl_(std::make_unique<Impl>(std::move(options))) {}

Session::~Session() { stop(); }

bool Session::start() {
  if (impl_->started) return false;
  auto& o = impl_->opt;

  // Resume intent (after an extension reload) supplies the token and the auth hashes.
  if (o.resume && o.resume->connect) {
    impl_->net_resume = o.resume->resume_token;
    impl_->net_last_journal_seq = o.resume->last_journal_seq;
    welcome_.resume_token = o.resume->resume_token;
    server_.session_id = o.resume->session_id;
    const auto load_hash = [](const std::string& hex, std::optional<crypto::Sha256Digest>& out) {
      std::vector<std::uint8_t> b;
      if (crypto::from_hex(hex, b) && b.size() == crypto::kSha256Size) {
        crypto::Sha256Digest d{};
        std::copy(b.begin(), b.end(), d.begin());
        out = d;
      }
    };
    load_hash(o.resume->auth_hash_hex, impl_->auth_hash);
    load_hash(o.resume->admin_hash_hex, impl_->admin_hash);
    impl_->was_in_session = true;  // a resumed node goes back to InSession on a resumed Welcome
  }
  if (o.password && !impl_->auth_hash) impl_->auth_hash = crypto::hash_password(*o.password);
  if (o.admin_password && !impl_->admin_hash) impl_->admin_hash = crypto::hash_password(*o.admin_password);
  if (o.extensions != nullptr) impl_->ext = o.extensions->snapshot();

  net::NetOptions nopt = o.net;
  nopt.gate_outbox_until_released = true;
  Impl* impl = impl_.get();
  nopt.frame_hook = [impl](std::uint16_t type, wire::Lane lane, std::span<const std::uint8_t> payload,
                           net::HookContext& ctx) { impl->on_net_frame(type, lane, payload, ctx); };
  if (!impl_->net.start(o.endpoint, std::move(nopt))) return false;
  impl_->started = true;
  state_ = State::Connecting;
  return true;
}

net::SendResult Session::send(wire::Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) {
  return impl_->net.send(lane, type, payload);
}

net::NetStatus Session::net_status() const noexcept { return impl_->net.status(); }
DownloadProgress Session::save_progress() const { return impl_->downloader.progress(); }
void Session::reconnect_now() { impl_->net.reconnect_now(); }

void Session::mark_in_session() {
  if (state_ == State::Joining) state_ = State::InSession;
}

SessionIntent Session::make_intent() const {
  SessionIntent in;
  in.connect = true;
  in.host = impl_->opt.endpoint.host;
  in.port = impl_->opt.endpoint.port;
  in.name = impl_->opt.player_name;
  in.roles = impl_->opt.requested_roles;
  in.resume_token = welcome_.resume_token;
  in.session_id = server_.session_id;
  in.last_journal_seq = impl_->net_last_journal_seq;
  if (impl_->auth_hash) in.auth_hash_hex = crypto::to_hex(std::span<const std::uint8_t>(*impl_->auth_hash));
  if (impl_->admin_hash) in.admin_hash_hex = crypto::to_hex(std::span<const std::uint8_t>(*impl_->admin_hash));
  return in;
}

void Session::stop() {
  if (!impl_->started) return;
  if (impl_->opt.stash != nullptr) clear_intent(*impl_->opt.stash);
  impl_->net.set_goodbye_code(net::kDisconnectClientQuit);
  impl_->net.stop();
  impl_->downloader.cancel();
  impl_->started = false;
  state_ = State::Disconnected;
}

void Session::unload_for_reload() {
  if (!impl_->started) return;
  if (impl_->opt.stash != nullptr) (void)save_intent(*impl_->opt.stash, make_intent());
  impl_->net.set_goodbye_code(net::kDisconnectClientReload);
  impl_->net.stop();  // joins the net thread (best-effort Disconnect(ClientReload) on a live connection)
  impl_->downloader.cancel();
  impl_->started = false;
  state_ = State::Disconnected;
}

bool Session::download_save(const SaveInfo& info) {
  auto& im = *impl_;
  if (im.opt.save_dir.empty() || info.sha256.size() != crypto::kSha256Size || info.size == 0) return false;
  if (im.save_info && im.save_info->sha256 == info.sha256 && (im.job_active || save_ready_)) return true;  // already on it
  im.save_info = info;
  save_ready_ = false;
  im.jobs.clear();
  im.job_active = true;
  const std::string name = info.local_file_name.empty()
                               ? "x4mp_" + crypto::to_hex(std::span<const std::uint8_t>(info.sha256).first(6)) + ".xml.gz"
                               : std::filesystem::path(info.local_file_name).filename().string();  // no path components from the wire
  const auto save_path = im.opt.save_dir / name;
  if (!Impl::file_matches(save_path, info.size, info.sha256)) {
    im.jobs.push_back(DownloadSpec{TransferKind::Save, info.sha256, info.size, save_path});
  }
  if (info.manifest_sha256.size() == crypto::kSha256Size && info.manifest_size > 0) {
    const auto mpath = im.opt.save_dir / Impl::manifest_name(info.manifest_sha256);
    if (!Impl::file_matches(mpath, info.manifest_size, info.manifest_sha256)) {
      im.jobs.push_back(DownloadSpec{TransferKind::Manifest, info.manifest_sha256, info.manifest_size, mpath});
    }
  }
  return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// poll
// ---------------------------------------------------------------------------------------------------------------------
void Session::poll(std::vector<SessionEvent>& out) {
  auto& im = *impl_;
  if (!im.started) return;

  const auto transition = [&](State to) {
    if (state_ == to) return;
    if (!can_transition(state_, to)) {
      X4MP_LOGD("session: unusual transition {} -> {}", to_string(state_), to_string(to));
    }
    SessionEvent ev;
    ev.kind = SessionEvent::Kind::StateChanged;
    ev.prev = state_;
    ev.state = to;
    state_ = to;
    out.push_back(std::move(ev));
  };

  std::vector<net::InboundEvent> events;
  im.net.poll_inbox(events);
  for (auto& e : events) {
    using Kind = net::InboundEvent::Kind;
    if (e.kind == Kind::Connected) {
      transition(State::Handshaking);
      continue;
    }
    if (e.kind == Kind::Disconnected) {
      SessionEvent ev;
      ev.kind = SessionEvent::Kind::NetDisconnected;
      ev.text = e.text;
      ev.code = e.remote_code;
      out.push_back(std::move(ev));
      const bool halted = e.reason == net::DisconnectReason::ServerDisconnect && net::is_no_retry_code(e.remote_code);
      if (e.reason == net::DisconnectReason::Requested || halted) {
        transition(State::Disconnected);
      } else if (state_ == State::InSession) {
        im.was_in_session = true;
        transition(State::Reconnecting);
      } else if (state_ == State::Handshaking || state_ == State::Joining) {
        transition(State::Reconnecting);
      }
      continue;
    }
    // Frames.
    using X4MP::Proto::MsgType;
    const std::span<const std::uint8_t> payload(e.payload);
    if (e.type == T(MsgType::ServerHello)) {
      if (auto s = parse_server_hello(payload)) {
        server_ = std::move(*s);
        SessionEvent ev;
        ev.kind = SessionEvent::Kind::ServerHello;
        out.push_back(std::move(ev));
      }
    } else if (e.type == T(MsgType::Welcome)) {
      if (auto w = parse_welcome(payload)) {
        welcome_ = *w;
        has_welcome_ = true;
        transition(State::Joining);
        SessionEvent ev;
        ev.kind = SessionEvent::Kind::Welcome;
        out.push_back(std::move(ev));
        if (w->resumed && im.was_in_session) {
          transition(State::InSession);
        } else if (!w->resumed) {
          save_ready_ = false;  // a fresh join starts over: SessionSaveInfo follows
          im.save_info.reset();
          im.jobs.clear();
          im.job_active = false;
          im.downloader.cancel();
        }
        im.was_in_session = false;
      }
    } else if (e.type == T(MsgType::Disconnect)) {
      const auto* d = flatbuffers::GetRoot<X4MP::Proto::Disconnect>(payload.data());
      SessionEvent ev;
      ev.kind = SessionEvent::Kind::ServerDisconnect;
      ev.code = static_cast<std::uint16_t>(d->code());
      ev.text = str(d->message());
      ev.expected = str(d->expected());
      ev.retry_after_ms = d->retry_after_ms();
      if (ev.code == net::kDisconnectResumeExpired) welcome_.resume_token = Id128{};
      out.push_back(std::move(ev));
    } else if (e.type == T(MsgType::SessionSaveInfo)) {
      if (auto s = parse_save_info(payload)) {
        SessionEvent ev;
        ev.kind = SessionEvent::Kind::SaveInfo;
        ev.save = *s;
        out.push_back(std::move(ev));
        if (im.opt.auto_download) (void)download_save(*s);
      }
    } else {
      SessionEvent ev;
      ev.kind = SessionEvent::Kind::Frame;
      ev.type = e.type;
      ev.lane = e.lane;
      ev.payload = std::move(e.payload);
      out.push_back(std::move(ev));
    }
  }

  // Retention reports from the net thread (Welcome decided replay vs drop).
  {
    std::vector<Impl::Report> reps;
    {
      const std::lock_guard lock(im.report_mutex);
      reps.swap(im.reports);
    }
    for (const auto& r : reps) {
      SessionEvent ev;
      ev.kind = r.replayed ? SessionEvent::Kind::ControlReplayed : SessionEvent::Kind::ControlDropped;
      ev.count = r.count;
      out.push_back(std::move(ev));
    }
  }

  // ---- download orchestration ----
  if (im.job_active) {
    for (int guard = 0; guard < 4 && im.job_active; ++guard) {
      const DownloadProgress p = im.downloader.progress();
      const bool busy = p.state == DownloadState::Requesting || p.state == DownloadState::Receiving;
      if (busy) {
        im.request_if_welcomed();
        const auto now = std::chrono::steady_clock::now();
        if (p.bytes_done != im.last_progress_bytes &&
            now - im.last_progress_emit >= std::chrono::milliseconds(250)) {
          SessionEvent ev;
          ev.kind = SessionEvent::Kind::SaveProgress;
          ev.progress = p;
          out.push_back(std::move(ev));
          im.last_progress_bytes = p.bytes_done;
          im.last_progress_emit = now;
        }
        break;
      }
      if (p.state == DownloadState::Failed) {
        SessionEvent ev;
        ev.kind = SessionEvent::Kind::SaveFailed;
        ev.progress = p;
        ev.text = std::string(to_string(p.error)) + ": " + p.detail;
        out.push_back(std::move(ev));
        im.send_load_failed(p.error == DownloadError::HashMismatch ? std::uint16_t{50} : std::uint16_t{51}, ev.text);
        im.jobs.clear();
        im.job_active = false;
        break;
      }
      // Idle (nothing armed yet) or Completed: arm the next job, or finish.
      if (!im.jobs.empty()) {
        const DownloadSpec spec = im.jobs.front();
        im.jobs.pop_front();
        (void)im.downloader.begin(spec);  // failure shows as Failed on the next loop turn
        im.last_progress_bytes = ~std::uint64_t{0};
        continue;
      }
      im.job_active = false;
      save_ready_ = true;
      im.send_save_ready();
      SessionEvent ev;
      ev.kind = SessionEvent::Kind::SaveReady;
      ev.progress = p;
      if (im.save_info) ev.save = *im.save_info;
      out.push_back(std::move(ev));
    }
  }
}

}  // namespace x4mp::session
