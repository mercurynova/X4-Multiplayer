#include "features/join/join_feature.h"

#include <algorithm>
#include <array>
#include <filesystem>
#include <fstream>
#include <span>
#include <string>
#include <utility>

#include <nlohmann/json.hpp>

#include "core/crypto/crypto.h"
#include "core/version/version.h"
#include "features/join/join_messages.h"
#include "host/build_check.h"
#include "host/extension_roots.h"
#include "host/status_json.h"
#include "message_ids_generated.h"
#include "session_generated.h"
#include "x4mp/wire.h"

namespace x4mp::features {

namespace {
using host::Cat;
using host::Level;
namespace fs = std::filesystem;
using std::chrono::milliseconds;
using std::chrono::seconds;

constexpr auto kStatusMinInterval = milliseconds(500);   // x4mp.status at most 2 Hz
constexpr auto kStatusHeartbeat = milliseconds(2000);    // ping_ms refresh while nothing else changes
constexpr auto kSaveListTimeout = seconds(60);
constexpr auto kLoadFallbackAfter = seconds(15);         // loadSave raised but this DLL is still alive: ask Lua for LoadGame
constexpr const char* kStateKey = "state";               // stash "join.state"

constexpr const char* kVerbs[] = {"join", "disconnect", "ui_ready", "request_status", "extensions"};

std::string endpoint_text(const std::string& host, std::uint16_t port) {
  return (host.find(':') != std::string::npos ? "[" + host + "]" : host) + ":" + std::to_string(port);
}

std::string strip_save_ext(std::string name) {
  for (const char* ext : {".xml.gz", ".xml"}) {
    const std::string x = ext;
    if (name.size() > x.size() && name.compare(name.size() - x.size(), x.size(), x) == 0) {
      name.resize(name.size() - x.size());
      break;
    }
  }
  return name;
}

void wipe(std::string& s) {
  std::fill(s.begin(), s.end(), '\0');
  s.clear();
}
}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
// inbox: Lua verb handlers (any thread) -> on_frame
// ---------------------------------------------------------------------------------------------------------------------
struct JoinFeature::Inbox {
  std::mutex mutex;
  std::vector<std::pair<std::string, std::string>> items;
  bool closed = false;

  void push(std::string verb, std::string payload) {
    const std::lock_guard lock(mutex);
    if (closed || items.size() >= 64) return;
    items.emplace_back(std::move(verb), std::move(payload));
  }
  std::vector<std::pair<std::string, std::string>> take() {
    const std::lock_guard lock(mutex);
    std::vector<std::pair<std::string, std::string>> out;
    out.swap(items);
    return out;
  }
  void close() {
    const std::lock_guard lock(mutex);
    closed = true;
    for (auto& [verb, payload] : items) wipe(payload);
    items.clear();
  }
};

JoinFeature::JoinFeature() = default;
JoinFeature::~JoinFeature() = default;

// ---------------------------------------------------------------------------------------------------------------------
// lifecycle
// ---------------------------------------------------------------------------------------------------------------------
void JoinFeature::on_init(host::HostContext& ctx) {
  inbox_ = std::make_shared<Inbox>();
  stash_ = std::make_unique<join::PlatformStash>(ctx.platform);

  // Extension list for ClientHello (M2-03): the worker scans at start-menu time, the Lua list arrives with x4mp.extensions.
  mods::ProviderOptions po;
  const fs::path docs = (ctx.paths != nullptr && ctx.paths->portable) ? ctx.paths->dir : fs::path{};  // portable (hostsim/CI): never read Documents
  po.roots = host::make_extension_roots(host::x4_install_dir(), docs);
  if (ctx.paths != nullptr && !ctx.paths->dir.empty()) po.cache_file = ctx.paths->dir / "ext-hash-cache.json";
  extensions_ = std::make_unique<mods::ExtensionProvider>(std::move(po));
  extensions_->start();

  // Verbs: the handlers only copy the text; on_frame does the work.
  for (const char* verb : kVerbs) {
    const std::string event = std::string("x4mp.") + verb;
    std::shared_ptr<Inbox> inbox = inbox_;
    const std::string v = verb;
    if (!ctx.platform.on_lua_verb(event.c_str(), [inbox, v](std::string_view payload) { inbox->push(v, std::string(payload)); })) {
      X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "lua verb {} could not be registered", event);
    }
  }

  // M2-07: resume a session that survived an extension reload (save load or /reloadui).
  if (const auto intent = session::load_intent(*stash_)) {
    std::string stage;
    if (const auto text = stash_->get(kStateKey)) {
      const auto doc = nlohmann::json::parse(*text, nullptr, false);
      if (doc.is_object()) {
        stage = doc.value("stage", "");
        save_name_ = doc.value("name", "");
        has_manifest_ = doc.value("manifest", false);
        checkpoint_ = session::Id128{doc.value("cp_lo", std::uint64_t{0}), doc.value("cp_hi", std::uint64_t{0})};
        std::vector<std::uint8_t> sha;
        if (crypto::from_hex(doc.value("sha", ""), sha)) save_sha_ = std::move(sha);
      }
    }
    if (stage == "loading" || stage == "ingame") {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "resuming the session after an extension reload (stage {})", stage);
      start_session(ctx, join::JoinRequest{}, &*intent);
      if (session_) {
        stage_ = stage == "ingame" ? Stage::InGame : Stage::Loading;
        resumed_incarnation_ = true;
      }
    } else {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "stale session intent (stage '{}'): dropped", stage);
      session::clear_intent(*stash_);
      stash_->erase(kStateKey);
    }
  }
  force_status_ = true;
}

void JoinFeature::on_game_loaded(host::HostContext&) { game_loaded_flag_.store(true); }
void JoinFeature::on_universe_ready(host::HostContext&) { universe_ready_flag_.store(true); }

void JoinFeature::on_shutdown(host::HostContext& ctx) {
  if (inbox_) inbox_->close();
  if (session_) {
    // M2-07 refines this (shutdown budget, epoch rule): a planned unload keeps the server slot for the resume grace.
    persist_state(stage_ == Stage::InGame ? "ingame" : (stage_ == Stage::Loading ? "loading" : "other"));
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "extension shutdown: unloading the session for a resume");
    session_->unload_for_reload();
    session_.reset();
  }
  extensions_.reset();
}

// ---------------------------------------------------------------------------------------------------------------------
// frame
// ---------------------------------------------------------------------------------------------------------------------
void JoinFeature::on_frame(host::HostContext& ctx, const host::FrameInfo&) {
  mods::mark_frame_thread(true);  // core/mods refuses file I/O on this thread
  drain_inbox(ctx);

  if (game_loaded_flag_.exchange(false)) {
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "game loaded (stage {})", static_cast<int>(stage_));
  }
  if (universe_ready_flag_.exchange(false)) {
    if (stage_ == Stage::Loading) {
      universe_pending_ = true;
    } else {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "universe ready outside the load flow (stage {}): nothing to report", static_cast<int>(stage_));
    }
  }

  pump_session(ctx);
  publish_status(ctx, false);
}

void JoinFeature::drain_inbox(host::HostContext& ctx) {
  if (!inbox_) return;
  for (auto& [verb, payload] : inbox_->take()) {
    if (verb == "join") {
      on_join(ctx, payload);
    } else if (verb == "disconnect") {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "disconnect requested by the UI");
      stop_session(ctx, "disconnect");
      stage_ = Stage::Idle;
      reject_.clear();
      detail_.clear();
    } else if (verb == "ui_ready" || verb == "request_status") {
      force_status_ = true;
    } else if (verb == "extensions") {
      on_extensions(ctx, payload);
    }
    wipe(payload);
  }
}

void JoinFeature::on_extensions(host::HostContext& ctx, const std::string& payload) {
  const auto list = join::parse_extensions(payload);
  if (!list || !extensions_) {
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "x4mp.extensions: unreadable payload ({} bytes)", payload.size());
    return;
  }
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "extension list from Lua: {} entries", list->size());
  extensions_->set_reported(*list);
}

void JoinFeature::on_join(host::HostContext& ctx, const std::string& payload) {
  std::string error;
  auto request = join::parse_join(payload, &error);
  if (!request) {
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "x4mp.join refused: {}", error);  // never the payload (it holds the password)
    raise_lua(ctx, "x4mp.error", host::make_error_json("bad_join", error));
    return;
  }
  start_session(ctx, *request, nullptr);
  wipe(request->password);
  wipe(request->admin_password);
}

// ---------------------------------------------------------------------------------------------------------------------
// session
// ---------------------------------------------------------------------------------------------------------------------
bool JoinFeature::load_player_key(host::HostContext& ctx, std::array<std::uint8_t, 32>& key) {
  if (have_player_key_) {
    key = player_key_;
    return true;
  }
  const fs::path file = (ctx.paths != nullptr && !ctx.paths->dir.empty()) ? ctx.paths->dir / "player.key" : fs::path{};
  std::vector<std::uint8_t> bytes;
  if (!file.empty()) {
    std::ifstream in(file);
    std::string hex;
    if (in && std::getline(in, hex) && crypto::from_hex(hex, bytes) && bytes.size() == key.size()) {
      std::copy(bytes.begin(), bytes.end(), key.begin());
    } else {
      bytes.clear();
    }
  }
  if (bytes.empty()) {
    if (!crypto::random_bytes(key)) return false;
    if (!file.empty()) {
      std::error_code ec;
      fs::create_directories(file.parent_path(), ec);
      std::ofstream out(file, std::ios::trunc);
      if (out) out << crypto::to_hex(key) << "\n";  // the player's identity key: stays on this machine
    }
  }
  player_key_ = key;
  have_player_key_ = true;
  return true;
}

session::ClientIdentity JoinFeature::make_identity(host::HostContext& ctx) const {
  session::ClientIdentity id;
  id.mod_version = std::string(version::mod_version());
  id.mod_build = "x4mp";
  id.game_version = ctx.game.info().game_version;
  id.x4native_version = ctx.game.info().x4native_version;
  host::BuildInfo bi;
  bi.game_version = ctx.game.info().game_version;
  bi.version = ctx.game.game_version_struct();
  bi.build_suffix = ctx.game.build_version_suffix();
  bi.game_types_build = ctx.game.info().game_types_build;
  bi.x4native_version = ctx.game.info().x4native_version;
  const auto check = host::check_build(bi, version::game_build_pin());
  id.game_build = check.status == host::BuildStatus::Supported ? check.detected : std::string(version::game_build_pin());
  return id;
}

void JoinFeature::start_session(host::HostContext& ctx, const join::JoinRequest& request, const session::SessionIntent* resume) {
  stop_session(ctx, "new join");
  stage_ = Stage::Joining;
  reject_.clear();
  detail_.clear();
  last_net_error_.clear();
  roster_.clear();
  progress_ = 0.0f;
  save_.reset();
  if (resume == nullptr) {
    save_name_.clear();
    save_sha_.clear();
    has_manifest_ = false;
    resumed_incarnation_ = false;
  }
  fallback_sent_ = false;
  universe_pending_ = false;

  const auto fail = [&](const char* detail) {
    stage_ = Stage::Failed;
    detail_ = detail;
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Error, "cannot start the session: {}", detail);
  };

  session::SessionOptions o;
  if (resume != nullptr) {
    o.endpoint = net::Endpoint{resume->host, resume->port};
    o.player_name = resume->name;
    o.requested_roles = resume->roles != 0 ? resume->roles : std::uint8_t{2};
    o.resume = *resume;
  } else {
    o.endpoint = net::Endpoint{request.host, request.port};
    o.player_name = request.name;
    o.requested_roles = request.want_authority ? std::uint8_t{3} : std::uint8_t{2};  // M2-09: Authority | Client
    o.preferred_team = request.team;
    if (!request.password.empty()) {
      o.password = request.password;
      ctx.log.redactor().add_secret(request.password);
    }
    if (!request.admin_password.empty()) {
      o.admin_password = request.admin_password;
      ctx.log.redactor().add_secret(request.admin_password);
    }
  }
  server_text_ = endpoint_text(o.endpoint.host, o.endpoint.port);
  o.identity = make_identity(ctx);
  if (!load_player_key(ctx, o.player_key)) return fail("no random source for the player key");
  const auto save_dir = ctx.game.save_folder_path();
  if (!save_dir || save_dir->empty()) return fail("the game's save folder is not available");
  o.save_dir = fs::path(*save_dir);
  o.extensions = extensions_.get();
  o.stash = stash_.get();
  o.net.backoff_first_ms = 500;

  X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "joining {} (roles {}, {})", server_text_, static_cast<int>(o.requested_roles),
            resume != nullptr ? "resume" : "fresh");
  session_ = std::make_unique<session::Session>(std::move(o));
  if (!session_->start()) {
    session_.reset();
    return fail("the network thread could not start");
  }
}

void JoinFeature::stop_session(host::HostContext& ctx, const char* why) {
  if (session_) {
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "leaving the session ({})", why);
    session_->stop();  // Disconnect(ClientQuit), intent cleared
    session_.reset();
  }
  if (stash_) {
    stash_->erase(kStateKey);
  }
  universe_pending_ = false;
}

bool JoinFeature::welcomed() const {
  return session_ && session_->has_welcome() &&
         (session_->state() == session::State::Joining || session_->state() == session::State::InSession);
}

void JoinFeature::send_control(std::uint16_t type, const std::vector<std::uint8_t>& payload) {
  if (session_) (void)session_->send(wire::Lane::Control, type, std::span<const std::uint8_t>(payload));
}

void JoinFeature::persist_state(const char* stage) const {
  if (!stash_) return;
  nlohmann::json j;
  j["stage"] = stage;
  j["name"] = save_name_;
  j["sha"] = crypto::to_hex(std::span<const std::uint8_t>(save_sha_));
  j["manifest"] = has_manifest_;
  j["cp_lo"] = checkpoint_.lo;
  j["cp_hi"] = checkpoint_.hi;
  stash_->put(kStateKey, j.dump());
}

void JoinFeature::pump_session(host::HostContext& ctx) {
  if (!session_) return;
  std::vector<session::SessionEvent> events;
  session_->poll(events);
  bool end_session = false;
  for (const auto& e : events) {
    handle_session_event(ctx, e);
    if (stage_ == Stage::Rejected) end_session = true;
  }
  if (end_session) {
    // A rejected attempt must not be redialled by the net layer: stop it here (the UI shows the reject text).
    const Stage keep = stage_;
    stop_session(ctx, "rejected");
    stage_ = keep;
    return;
  }

  switch (stage_) {
    case Stage::Preparing: step_preparing(ctx); break;
    case Stage::Loading: step_loading(ctx); break;
    default: break;
  }
  if (universe_pending_ && stage_ == Stage::Loading && welcomed()) complete_universe(ctx);
}

void JoinFeature::handle_session_event(host::HostContext& ctx, const session::SessionEvent& e) {
  using K = session::SessionEvent::Kind;
  switch (e.kind) {
    case K::StateChanged:
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "session {} -> {}", session::to_string(e.prev), session::to_string(e.state));
      break;
    case K::ServerHello:
      X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "ServerHello: server '{}' version {}", session_->server().server_name,
                session_->server().server_version);
      break;
    case K::Welcome:
      X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "Welcome: player_id={} roles={} resumed={}", session_->welcome().player_id,
                static_cast<int>(session_->welcome().granted_roles), session_->welcome().resumed);
      last_net_error_.clear();
      break;
    case K::ServerDisconnect: {
      X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "server Disconnect code={} message='{}' expected='{}'", e.code, e.text, e.expected);
      if (const auto token = join::reject_for_code(e.code)) {
        stage_ = Stage::Rejected;
        reject_ = std::string(*token);
        detail_ = e.text;
        if (!e.expected.empty()) detail_ += (detail_.empty() ? "" : " ") + std::string("(") + e.expected + ")";
      }
      break;
    }
    case K::NetDisconnected:
      last_net_error_ = e.text;
      break;
    case K::SaveInfo:
      X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "SessionSaveInfo: '{}' {} bytes, file {}, manifest {} bytes", e.save.display_name, e.save.size,
                e.save.local_file_name, e.save.manifest_size);
      if (stage_ == Stage::Joining) stage_ = Stage::Downloading;
      progress_ = 0.0f;
      break;
    case K::SaveProgress:
      progress_ = e.progress.fraction();
      break;
    case K::SaveFailed:
      X4MP_CLOG(ctx.log, Cat::Save, Level::Error, "save download failed: {}", e.text);
      stage_ = Stage::Failed;
      detail_ = e.text;
      break;
    case K::SaveReady:
      save_ = e.save;
      handle_save_ready(ctx);
      break;
    case K::Frame:
      if (e.type == static_cast<std::uint16_t>(X4MP::Proto::MsgType::RosterUpdate)) {
        flatbuffers::Verifier v(e.payload.data(), e.payload.size());
        if (v.VerifyBuffer<X4MP::Proto::RosterUpdate>(nullptr)) {
          const auto* roster = flatbuffers::GetRoot<X4MP::Proto::RosterUpdate>(e.payload.data());
          if (roster->full()) roster_.clear();
          if (roster->players() != nullptr) {
            for (const auto* p : *roster->players()) roster_.insert(p->player_id());
          }
          if (roster->removed() != nullptr) {
            for (const auto id : *roster->removed()) roster_.erase(id);
          }
        }
      }
      break;
    default: break;  // ControlReplayed / ControlDropped: nothing to do
  }
}

void JoinFeature::handle_save_ready(host::HostContext& ctx) {
  if (!save_) return;
  save_sha_ = save_->sha256;
  checkpoint_ = save_->checkpoint_id;
  has_manifest_ = save_->manifest_sha256.size() == crypto::kSha256Size && save_->manifest_size > 0;
  save_name_ = strip_save_ext(fs::path(save_->local_file_name).filename().string());
  if (save_name_.empty()) save_name_ = "x4mp_" + crypto::to_hex(std::span<const std::uint8_t>(save_sha_).first(std::min<std::size_t>(6, save_sha_.size())));
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "session save complete and verified as {}.xml.gz; preparing the load", save_name_);
  stage_ = Stage::Preparing;
  prep_step_ = PrepStep::ReloadList;
}

void JoinFeature::step_preparing(host::HostContext& ctx) {
  const auto now = Clock::now();
  if (prep_step_ == PrepStep::ReloadList) {
    const bool called = ctx.game.reload_save_list();
    prep_started_ = now;
    prep_step_ = PrepStep::WaitList;
    if (!called) {
      X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "ReloadSaveList is not available; loading by name without it");
      issue_load(ctx);
    }
    return;
  }
  const bool can_ask = ctx.game.fns().IsSaveListLoadingComplete != nullptr;
  if (can_ask && !ctx.game.save_list_loading_complete()) {
    if (now - prep_started_ > kSaveListTimeout) {
      X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "the save list did not finish loading in 60 s; loading by name anyway");
      issue_load(ctx);
    }
    return;
  }
  const bool valid = ctx.game.save_valid(save_name_);
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "save list ready; IsSaveValid({}) = {}", save_name_, valid);
  issue_load(ctx);
}

void JoinFeature::issue_load(host::HostContext& ctx) {
  persist_state("loading");  // intent first: the reload that the load causes must resume, not rejoin
  send_control(join::msg_load_status(), join::encode_load_status(join::JoinPhase::Loading, 0.0f));
  // Tell Lua (it keeps the start menu from being restored over the loading screen), then raise the vanilla event that
  // gameoptions.lua listens to: the path session 2 proved (the downloaded file is not in the Load list, loading by name works).
  raise_lua(ctx, "x4mp.load_save", host::make_load_save_json(save_name_, false));
  const int rc = ctx.platform.raise_lua("loadSave", save_name_);
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "raised the Lua event loadSave for {} (rc={})", save_name_, rc);
  stage_ = Stage::Loading;
  load_issued_ = Clock::now();
  fallback_sent_ = false;
}

void JoinFeature::step_loading(host::HostContext& ctx) {
  if (resumed_incarnation_ || fallback_sent_) return;
  if (Clock::now() - load_issued_ < kLoadFallbackAfter) return;
  // The vanilla event did not start a load (no reload within 15 s): let Lua call LoadGame(name) itself.
  fallback_sent_ = true;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "no reload 15 s after loadSave; asking Lua to call LoadGame for {}", save_name_);
  raise_lua(ctx, "x4mp.load_save", host::make_load_save_json(save_name_, true));
}

void JoinFeature::complete_universe(host::HostContext& ctx) {
  universe_pending_ = false;
  std::array<std::uint8_t, 8> rnd{};
  std::uint64_t epoch = 0;
  if (crypto::random_bytes(rnd)) {
    for (const auto b : rnd) epoch = (epoch << 8) | b;
  }
  if (epoch == 0) epoch = 1;  // never 0 = "none"
  const auto t0 = Clock::now();
  send_control(join::msg_load_status(), join::encode_load_status(join::JoinPhase::Matching, 1.0f));
  if (has_manifest_) {
    // M2: count-only. Matching the manifest against the loaded universe is M4.
    const auto ms = static_cast<std::uint32_t>(std::chrono::duration_cast<milliseconds>(Clock::now() - t0).count());
    send_control(join::msg_manifest_report(), join::encode_manifest_report_counts(checkpoint_, 0, 0, ms));
  }
  send_control(join::msg_node_ready(), join::encode_node_ready(epoch, save_sha_));
  session_->mark_in_session();
  stage_ = Stage::InGame;
  persist_state("ingame");
  X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "universe ready: NodeReady sent (epoch {:016x}, manifest report {})", epoch,
            has_manifest_ ? "counts only" : "skipped");
}

// ---------------------------------------------------------------------------------------------------------------------
// status (native -> Lua)
// ---------------------------------------------------------------------------------------------------------------------
std::string JoinFeature::build_status() const {
  host::StatusFields f;
  std::string detail = detail_;
  std::string server = server_text_;
  std::string state = "disconnected";
  const session::State ss = session_ ? session_->state() : session::State::Disconnected;
  switch (stage_) {
    case Stage::Idle: break;
    case Stage::Rejected: state = "rejected"; break;
    case Stage::Failed: state = "error"; break;
    case Stage::Joining:
      if (ss == session::State::Connecting || ss == session::State::Reconnecting || ss == session::State::Disconnected) {
        state = "connecting";
        if (detail.empty()) detail = last_net_error_;
      } else if (ss == session::State::Handshaking) {
        state = "handshaking";
      } else {
        state = "checking_save";
      }
      break;
    case Stage::Downloading: state = "downloading"; f.progress = progress_; break;
    case Stage::Preparing:
    case Stage::Loading: state = "loading"; break;
    case Stage::InGame:
      state = ss == session::State::Reconnecting ? "connecting" : "ingame";
      if (ss == session::State::Reconnecting && detail.empty()) detail = "reconnecting";
      break;
  }
  f.state = state;
  f.detail = detail;
  f.reject = stage_ == Stage::Rejected ? std::string_view(reject_) : std::string_view();
  f.server = server;
  std::string role;
  if (session_ && session_->has_welcome()) {
    role = (session_->welcome().granted_roles & 1) != 0 ? "authority" : "client";
    f.role = role;
    if (session_->welcome().team_id != 0) f.team = session_->welcome().team_id;
  }
  if (!roster_.empty()) f.players = static_cast<int>(roster_.size());
  if (session_ && stage_ == Stage::InGame) {
    const auto rtt = session_->net_status().rtt_us;
    if (rtt > 0) f.ping_ms = static_cast<int>(rtt / 1000);
  }
  if (f.progress) f.progress = static_cast<double>(static_cast<int>(*f.progress * 100.0f)) / 100.0;  // 1 % steps: no chatter
  return host::make_status_json(f);
}

void JoinFeature::raise_lua(host::HostContext& ctx, const char* event, const std::string& payload) {
  const int rc = ctx.platform.raise_lua(event, payload);
  if (rc != 0) X4MP_CLOG(ctx.log, Cat::Ui, Level::Debug, "raise_lua({}) returned {}", event, rc);
}

void JoinFeature::publish_status(host::HostContext& ctx, bool force) {
  const std::string status = build_status();
  const auto now = Clock::now();
  // x4mp.status is capped at 2 Hz; only an explicit question from Lua (ui_ready / request_status) is answered at once.
  const bool answer = force || force_status_;
  if (!answer) {
    const bool changed = status != last_status_;
    const auto since = now - last_status_at_;
    if (!(changed && since >= kStatusMinInterval) && !(since >= kStatusHeartbeat && stage_ != Stage::Idle)) return;
  }
  force_status_ = false;
  last_status_ = status;
  last_status_at_ = now;
  raise_lua(ctx, "x4mp.status", status);
}

}  // namespace x4mp::features
