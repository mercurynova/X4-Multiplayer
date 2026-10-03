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
#include "features/authority/authority_flow.h"
#include "features/diag/diag_hub.h"
#include "features/join/join_messages.h"
#include "features/join/join_mods_json.h"
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

  auth_inbox_ = auth::subscribe_authority_verbs(ctx.platform);  // M2-09: x4mp.auth_md / x4mp.auth_saved (drained by the AuthorityFlow)

  // Verbs: the handlers only copy the text; on_frame does the work.
  for (const char* verb : kVerbs) {
    const std::string event = std::string("x4mp.") + verb;
    std::shared_ptr<Inbox> inbox = inbox_;
    const std::string v = verb;
    if (!ctx.platform.subscribe_event(event.c_str(), [inbox, v](std::string_view payload) { inbox->push(v, std::string(payload)); })) {
      X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "lua verb {} could not be registered", event);
    }
  }

  // ---- M2-07 begin: resume a session that survived an extension reload (save load or /reloadui) ----
  // Missing or corrupt stash falls back to a clean fresh join: no crash, one clear log line, the UI starts from the start menu.
  const auto intent = session::load_intent(*stash_);
  const auto state_text = stash_->get(kStateKey);
  if (intent) {
    std::optional<resume::State> state;
    if (state_text) state = resume::parse(*state_text);
    session::SessionIntent resume_intent = *intent;
    std::string why;
    if (!state_text) {
      why = "join.state is missing";
    } else if (!state) {
      why = "join.state is corrupt";
    } else if (!resume::is_resumable_stage(state->stage)) {
      why = "nothing to resume in stage '" + state->stage + "'";
    }
    if (!why.empty()) {
      // Clean fresh join: drop the saved stage and the resume token (a token the server cannot match would only be refused).
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Warn, "reload resume: {}; clean fresh join with the saved address", why);
      stash_->erase(kStateKey);
      resume_intent.resume_token = session::Id128{};
      state = resume::State{};
      state->stage = "joining";
    }
    {
      save_name_ = state->save_name;
      has_manifest_ = state->has_manifest;
      checkpoint_ = state->checkpoint;
      if (!state->save_sha.empty()) save_sha_ = state->save_sha;
      epoch_ = state->epoch;
      fingerprint_ = state->fingerprint;
      const std::string st = state->stage;
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "resuming the session after an extension reload (stage {}, epoch {:016x}, reload #{})", st,
                state->epoch, ctx.previous.present ? ctx.previous.reload_count : 0);
      start_session(ctx, join::JoinRequest{}, &resume_intent);
      if (session_) {
        resumed_incarnation_ = true;
        if (st == "ingame") {
          stage_ = Stage::InGame;
          undecided_ = *state;  // /reloadui or a save load? decided when the universe is ready (decide_after_reload)
        } else if (st == "loading") {
          stage_ = Stage::Loading;
        } else if (st == "preparing") {
          stage_ = Stage::Preparing;  // the verified file is already on disk: redo the pre-load steps
          prep_step_ = PrepStep::ReloadList;
        }  // joining / downloading: stay Joining; the server re-sends SessionSaveInfo while the node is syncing
      }
    }
  } else if (state_text) {
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Warn, "reload resume: join.state without a session intent; dropped (clean start)");
    stash_->erase(kStateKey);
  }
  // ---- M2-07 end ----
  force_status_ = true;
}

void JoinFeature::on_game_loaded(host::HostContext&) { game_loaded_flag_.store(true); }
void JoinFeature::on_universe_ready(host::HostContext&) { universe_ready_flag_.store(true); }

void JoinFeature::on_shutdown(host::HostContext& ctx) {
  if (inbox_) inbox_->close();
  diag_sender_ = false;
  diag_connected_ = false;
  diag_hub().set_log_sender({});
  diag_hub().set_connection(NodeRole::None, false);
  authority_.reset();  // M2-09: joins its upload worker before the session goes away
  if (session_) {
    // M2-07: a planned unload keeps the server slot for the resume grace. The stage is persisted first, then the net thread is joined
    // within a measured budget (session 2: ~5 ms). No game call here: the fingerprint is the last in-game sample.
    const char* stage = resume_stage_name();
    if (*stage != 0) persist_state(stage);
    const auto t0 = Clock::now();
    session_->unload_for_reload();
    const auto join_ms = std::chrono::duration_cast<milliseconds>(Clock::now() - t0).count();
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "extension shutdown: session unloaded for a resume (stage '{}', epoch {:016x}) join_ms={}", stage,
              epoch_, join_ms);
    if (join_ms > resume::kUnloadBudgetMs) {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Warn, "unload_for_reload took {} ms, over the {} ms shutdown budget", join_ms, resume::kUnloadBudgetMs);
    }
    session_.reset();
  }
  extensions_.reset();
}

// ---------------------------------------------------------------------------------------------------------------------
// frame
// ---------------------------------------------------------------------------------------------------------------------
void JoinFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  mods::mark_frame_thread(true);  // core/mods refuses file I/O on this thread
  drain_inbox(ctx);

  if (game_loaded_flag_.exchange(false)) {
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "game loaded (stage {})", static_cast<int>(stage_));
    // M2-09: a game load we did not cause (the player loaded another save) means the remembered "this game runs save X" is stale.
    if (stage_ != Stage::Loading && stash_) auth::AuthorityFlow::forget_loaded_sha(*stash_);
  }
  sample_fingerprint(ctx, info);  // M2-07
  if (universe_ready_flag_.exchange(false)) {
    if (stage_ == Stage::Loading) {
      universe_pending_ = true;
    } else if (stage_ == Stage::InGame && undecided_) {
      decide_after_reload(ctx);  // M2-07
    } else {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "universe ready outside the load flow (stage {}): nothing to report", static_cast<int>(stage_));
    }
  }

  pump_session(ctx);
  update_diag(ctx);
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
      mod_refusal_json_.clear();
      mod_policy_json_.clear();
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
  mod_refusal_json_.clear();
  mod_policy_json_.clear();
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
  authority_ready_step_ = 0;  // M2-09
  my_phase_ = -1;
  ready_pending_ = false;

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
  want_authority_ = (o.requested_roles & 1) != 0;  // M2-09
  if (want_authority_ && stash_) {
    // "this game already runs the session's start save": the server then sends no SessionSaveInfo (M2-02). Seam: the join payload may carry it.
    o.loaded_save_sha256 = (resume == nullptr && !request.loaded_save_sha256.empty()) ? request.loaded_save_sha256
                                                                                         : auth::AuthorityFlow::stored_loaded_sha(*stash_);
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
  authority_.reset();  // M2-09
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

// Tells the diag hub (save blocking, quicksave warning, self-test) what this node is, and installs the LogForward sender once the
// server granted the capability (1<<10). The sender sends on the Control lane from the frame thread; <= 40 lines/s.
void JoinFeature::update_diag(host::HostContext&) {
  auto& hub = diag_hub();
  const bool connected = welcomed();
  const bool authority = connected && (session_->welcome().granted_roles & 1) != 0;
  if (connected != diag_connected_) {
    diag_connected_ = connected;
    hub.set_connection(connected ? (authority ? NodeRole::Authority : NodeRole::Client) : NodeRole::None, connected);
  }
  const bool want_sender = connected && (session_->welcome().negotiated_caps & (std::uint64_t{1} << 10)) != 0;
  if (want_sender && !diag_sender_) {
    diag_sender_ = true;
    hub.set_log_sender([this](log::Level level, std::string_view text) {
      if (!session_ || !welcomed()) return false;
      const auto now = Clock::now();
      if (now - log_window_ >= std::chrono::seconds(1)) {
        log_window_ = now;
        log_in_window_ = 0;
      }
      if (++log_in_window_ > 40) return false;
      const auto us = static_cast<std::uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::system_clock::now().time_since_epoch()).count());
      send_control(join::msg_log_forward(), join::encode_log_forward(static_cast<int>(level), us, text));
      return true;
    });
  } else if (!want_sender && diag_sender_) {
    diag_sender_ = false;
    hub.set_log_sender({});
  }
}

void JoinFeature::send_control(std::uint16_t type, const std::vector<std::uint8_t>& payload) {
  if (session_) (void)session_->send(wire::Lane::Control, type, std::span<const std::uint8_t>(payload));
}

// M2-07: the persisted stage of the current stage_ ("" when there is nothing a resume could continue).
const char* JoinFeature::resume_stage_name() const {
  switch (stage_) {
    case Stage::Joining: return "joining";
    case Stage::Downloading: return "downloading";
    case Stage::Preparing: return "preparing";
    case Stage::Loading: return "loading";
    case Stage::InGame: return "ingame";
    default: return "";
  }
}

void JoinFeature::persist_state(const char* stage) const {
  if (!stash_) return;
  resume::State st;
  st.stage = stage;
  st.save_name = save_name_;
  st.save_sha = save_sha_;
  st.has_manifest = has_manifest_;
  st.checkpoint = checkpoint_;
  st.epoch = epoch_;
  st.fingerprint = fingerprint_;
  stash_->put(kStateKey, resume::to_json(st));
}

// M2-07: while in-game, remember the universe fingerprint about once a second (the shutdown must not call into the game).
void JoinFeature::sample_fingerprint(host::HostContext& ctx, const host::FrameInfo& info) {
  if (stage_ != Stage::InGame || undecided_ || !info.universe_ready || !info.game_time) return;
  if (fingerprint_.valid && ++fingerprint_frame_ < 60) return;
  fingerprint_frame_ = 0;
  fingerprint_.valid = true;
  fingerprint_.player_id = static_cast<std::uint64_t>(ctx.game.player_id());
  fingerprint_.game_time = *info.game_time;
}

// M2-07: a resumed in-game incarnation sees the universe become ready: same universe (/reloadui) or a new one (save load)?
void JoinFeature::decide_after_reload(host::HostContext& ctx) {
  resume::Fingerprint now;
  if (const auto t = ctx.game.game_time()) {
    now.valid = true;
    now.player_id = static_cast<std::uint64_t>(ctx.game.player_id());
    now.game_time = *t;
  }
  const resume::Decision d = resume::decide_universe(*undecided_, now);
  undecided_.reset();
  if (d.verdict == resume::Verdict::SameUniverse) {
    fingerprint_ = now;
    fingerprint_frame_ = 0;
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "reload resume: same universe (ui reload; {}): staying in-game, epoch {:016x} kept", d.reason, epoch_);
    return;
  }
  X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "reload resume: new universe (save load; {}): matching again", d.reason);
  fingerprint_ = resume::Fingerprint{};
  stage_ = Stage::Loading;
  universe_pending_ = true;
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

  // M2-09: the authority's checkpoint flow and its "nothing to load" ready path.
  if (authority_) authority_->step(ctx);
  step_authority_ready(ctx);
  finish_ready(ctx);

  switch (stage_) {
    case Stage::Preparing: step_preparing(ctx); break;
    case Stage::Loading: step_loading(ctx); break;
    default: break;
  }
  if (universe_pending_ && stage_ == Stage::Loading && welcomed()) complete_universe(ctx);
}

void JoinFeature::handle_session_event(host::HostContext& ctx, const session::SessionEvent& e) {
  using K = session::SessionEvent::Kind;
  // M2-09 hook: RequestSave and the upload frames belong to the authority flow.
  if (e.kind == K::Frame && authority_ && authority_->on_frame(ctx, e.type, std::span<const std::uint8_t>(e.payload))) return;
  if (e.kind == K::NetDisconnected && authority_) authority_->on_net_disconnected();
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
      welcomed_at_ = Clock::now();
      mod_policy_json_ = join::mod_policy_json_from_welcome(std::span<const std::uint8_t>(e.payload));  // M2-X3
      if (!mod_policy_json_.empty()) raise_lua(ctx, "x4mp.mod_policy", mod_policy_json_);
      sync_authority(ctx);  // M2-09
      if (resumed_incarnation_ && !session_->welcome().resumed) {  // M2-07
        X4MP_CLOG(ctx.log, Cat::Sess, Level::Warn, "reload resume: the server did not resume the slot (fresh Welcome); joining from scratch");
      }
      break;
    case K::ServerDisconnect: {
      X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "server Disconnect code={} message='{}' expected='{}'", e.code, e.text, e.expected);
      if (const auto token = join::reject_for_code(e.code)) {
        stage_ = Stage::Rejected;
        reject_ = std::string(*token);
        detail_ = e.text;
        if (!e.expected.empty()) detail_ += (detail_.empty() ? "" : " ") + std::string("(") + e.expected + ")";
        // M2-X3: the grouped install/enable/disable/update lists go to Lua before the "rejected" status that names them.
        mod_refusal_json_ = join::mod_refusal_json(std::span<const std::uint8_t>(e.payload));
        if (!mod_refusal_json_.empty()) raise_lua(ctx, "x4mp.mod_refusal", mod_refusal_json_);
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
      if (stage_ == Stage::Loading || stage_ == Stage::InGame) break;  // M2-07: a resumed Welcome may re-announce the save; never load twice
      save_ = e.save;
      handle_save_ready(ctx);
      break;
    case K::Frame:
      if (e.type == static_cast<std::uint16_t>(X4MP::Proto::MsgType::ModPolicyChanged)) {  // M2-X3: the admin edited the mod list
        mod_policy_json_ = join::mod_policy_json_from_changed(std::span<const std::uint8_t>(e.payload));
        if (!mod_policy_json_.empty()) raise_lua(ctx, "x4mp.mod_policy", mod_policy_json_);
      }
      if (e.type == static_cast<std::uint16_t>(X4MP::Proto::MsgType::RosterUpdate)) {
        flatbuffers::Verifier v(e.payload.data(), e.payload.size());
        if (v.VerifyBuffer<X4MP::Proto::RosterUpdate>(nullptr)) {
          const auto* roster = flatbuffers::GetRoot<X4MP::Proto::RosterUpdate>(e.payload.data());
          if (roster->full()) roster_.clear();
          if (roster->players() != nullptr) {
            for (const auto* p : *roster->players()) {
              roster_.insert(p->player_id());
              if (session_ && p->player_id() == session_->welcome().player_id) my_phase_ = static_cast<int>(p->phase());
            }
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
  const int rc = ctx.platform.raise_lua("loadSave", save_name_) ? 0 : -1;
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

void JoinFeature::complete_universe(host::HostContext&) {
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
  ready_pending_ = true;  // NodeReady follows in finish_ready()
  matching_sent_at_ = Clock::now();
  pending_epoch_ = epoch;
}

void JoinFeature::finish_ready(host::HostContext& ctx) {
  if (!ready_pending_ || !session_) return;
  // The server only accepts NodeReady in Matching/CatchingUp/InGame and learns the phase from the LoadStatus asynchronously: a NodeReady
  // sent in the same frame as the Matching report can overtake it (PhaseDenied). Wait for the roster to show us in Matching (max 3 s).
  if (my_phase_ < 5 && Clock::now() - matching_sent_at_ < std::chrono::seconds(3)) return;
  ready_pending_ = false;
  const std::uint64_t epoch = pending_epoch_;
  // M2-09: remember which save this game runs (the authority's next fresh join tells the server in ClientHello).
  if (want_authority_ && stash_ && save_sha_.size() == 32) {
    if (authority_) {
      authority_->note_loaded_save(save_sha_);
    } else {
      auto st = auth::AuthorityState::from_json(stash_->get(auth::kStashKey).value_or(""));
      st.loaded_sha = save_sha_;
      stash_->put(auth::kStashKey, st.to_json());
    }
  }
  send_control(join::msg_node_ready(), join::encode_node_ready(epoch, save_sha_));
  session_->mark_in_session();
  stage_ = Stage::InGame;
  epoch_ = epoch;                         // M2-07
  fingerprint_ = resume::Fingerprint{};  // sampled again on the next frames
  persist_state("ingame");
  X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "universe ready: NodeReady sent (epoch {:016x}, manifest report {})", epoch,
            has_manifest_ ? "counts only" : "skipped");
}

// ---------------------------------------------------------------------------------------------------------------------
// M2-09: authority hooks
// ---------------------------------------------------------------------------------------------------------------------
// After every Welcome: the flow lives exactly while the server granted the authority role (bit 0).
void JoinFeature::sync_authority(host::HostContext& ctx) {
  const bool granted = session_ && (session_->welcome().granted_roles & 1) != 0;
  if (want_authority_ && !granted) {
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Warn, "the authority role was requested but not granted (roles {})", session_ ? static_cast<int>(session_->welcome().granted_roles) : 0);
  }
  if (!granted) {
    authority_.reset();
    return;
  }
  if (!authority_ && stash_) authority_ = std::make_unique<auth::AuthorityFlow>(ctx, *session_, *stash_, auth_inbox_);
  if (authority_) authority_->on_welcome(ctx, session_->welcome().resumed);
}

// An authority whose game is already loaded and that is NOT sent a SessionSaveInfo (no start save, or it already runs it) reports
// Loading, Matching and NodeReady itself, 2.5 s after the Welcome (the server sends the info right after it).
void JoinFeature::step_authority_ready(host::HostContext& ctx) {
  if (!authority_ || stage_ != Stage::Joining || ready_pending_ || !welcomed() || !ctx.gates.universe_ready) return;
  const auto now = Clock::now();
  if (authority_ready_step_ == 0) {
    if (now - welcomed_at_ < std::chrono::milliseconds(2500)) return;
    send_control(join::msg_load_status(), join::encode_load_status(join::JoinPhase::Loading, 1.0f));
    authority_ready_step_ = 1;
    authority_ready_at_ = now;
    return;
  }
  if (now - authority_ready_at_ < std::chrono::milliseconds(300)) return;
  save_sha_ = authority_->state().loaded_sha;
  X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "authority: no session save to load, reporting ready with the running game");
  universe_pending_ = false;
  complete_universe(ctx);
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
  const int rc = ctx.platform.raise_lua(event, payload) ? 0 : -1;
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
  if (answer) {  // M2-X3: a Lua state that just (re)loaded asks for the status; give it the mod topics too
    if (stage_ == Stage::Rejected && !mod_refusal_json_.empty()) raise_lua(ctx, "x4mp.mod_refusal", mod_refusal_json_);
    if (stage_ != Stage::Idle && stage_ != Stage::Rejected && !mod_policy_json_.empty()) raise_lua(ctx, "x4mp.mod_policy", mod_policy_json_);
  }
  force_status_ = false;
  last_status_ = status;
  last_status_at_ = now;
  raise_lua(ctx, "x4mp.status", status);
}

}  // namespace x4mp::features
