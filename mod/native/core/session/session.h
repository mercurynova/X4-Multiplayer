#pragma once
// core/session: the connection/session layer on top of core/net (M1-N3; docs/mod-design.md 2.6, protocol.md 4, 6.4, 6.6).
//
//   * Handshake. The Session installs a NetOptions::frame_hook. On the net thread it answers ServerHello with
//     ClientHello (HMAC-SHA256 proofs through BCrypt, byte-for-byte HandshakeAuth.cs), arms the heartbeat right
//     after (the server rejects anything but ClientHello before the handshake), and on Welcome decides what happens
//     to Control frames the main thread queued while the link was down: replay them in order after a RESUME,
//     drop and report them after a fresh join (NetOptions::gate_outbox_until_released, always on here).
//   * Resume. The resume token from Welcome is kept; every reconnect (the net layer redials by itself) presents it
//     with the player key. ResumeExpired makes the next attempt a fresh join.
//   * Planned unload. unload_for_reload() writes a SessionIntent to the injected IStash and says goodbye with
//     Disconnect(ClientReload): the server keeps the slot for the grace period and emits no leave/join. After
//     X4Native restarts the extension (save load), main.cpp (M2) builds a new Session from load_intent() and the
//     handshake resumes. The stash itself (x4n::stash) is behind IStash so core/ stays free of the SDK.
//   * Saves. SessionSaveInfo triggers an in-band download (save, then manifest) into SessionOptions::save_dir via
//     SaveDownloader (chunk reassembly, resume at an offset, SHA-256 verify); then SaveReady is sent.
//
// Threading: ALL Session members are for the one main/session thread. The frame hook runs on the net thread and
// touches only its own state plus the mutex-protected SaveDownloader and a few atomics. No exceptions cross this
// API. core/ includes no X4 SDK headers.

#include <array>
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "core/net/net.h"
#include "core/session/save_download.h"

namespace x4mp::session {

enum class State { Disconnected, Connecting, Handshaking, Joining, InSession, Reconnecting };

[[nodiscard]] const char* to_string(State state) noexcept;
[[nodiscard]] bool can_transition(State from, State to) noexcept;

// 128-bit opaque id (resume token, session id, checkpoint id). Zero = none / fresh join.
struct Id128 {
  std::uint64_t lo = 0;
  std::uint64_t hi = 0;
  [[nodiscard]] bool zero() const noexcept { return lo == 0 && hi == 0; }
  friend bool operator==(const Id128&, const Id128&) = default;
};

// ---- injected seams (no X4 SDK in core/) -----------------------------------------------------------------------
// The extension list the ClientHello carries (mod-management.md phase 1). In-game gathering is M2 (Lua
// x4mp.extensions verb + core/mods hashing); core only transports it. `hash` is SHA-256 of the sorted
// "id@version\n" lines of enabled Dlc/Sim extensions (empty = not computed), `entries` the same "id@version" list.
// `list` is the full ClientHello.extension_list (enabled and disabled; core/mods fills it, M2-X2); empty = legacy node.
// Enum-typed fields hold the wire values of X4MP.Proto.ExtensionSource / ExtensionClass / HashKind.
struct ExtensionDependencyReport {
  std::string id;
  bool optional = false;
};
struct ExtensionReport {
  std::string id;
  std::string name;
  std::string version;
  std::uint8_t source = 0;  // ExtensionSource: 0 Dlc, 1 Install, 2 User, 3 Workshop
  bool enabled = false;
  bool egosoft = false;
  std::uint64_t workshop_id = 0;
  std::vector<std::uint8_t> content_hash;  // empty = not computed
  std::uint8_t hash_kind = 0;              // HashKind: 0 None, 1 CatIndex, 2 Files
  bool has_native_dll = false;
  bool replaces_basegame = false;
  bool save_dependent = false;
  std::uint8_t class_hint = 0;  // ExtensionClass: 0 Unknown, 1 Dlc, 2 Sim, 3 ClientOnly
  std::string error;
  std::string warning;
  std::vector<ExtensionDependencyReport> dependencies;
};
struct ExtensionSnapshot {
  std::vector<std::uint8_t> hash;
  std::vector<std::string> entries;
  std::vector<ExtensionReport> list;
};
class IExtensionProvider {
 public:
  virtual ~IExtensionProvider() = default;
  // Called once per Session::start() on the caller's (main) thread. No file I/O: it may block up to a short bounded
  // time (core/mods: 2 s) waiting for a worker thread's result, never more.
  virtual ExtensionSnapshot snapshot() = 0;
};

// Process-lifetime key/value memory that survives an extension reload (x4n::stash in the mod). Must be thread-safe.
class IStash {
 public:
  virtual ~IStash() = default;
  virtual void put(std::string_view key, std::string_view value) = 0;
  [[nodiscard]] virtual std::optional<std::string> get(std::string_view key) const = 0;
  virtual void erase(std::string_view key) = 0;
};

// In-memory IStash for tests and the headless client.
class MemoryStash final : public IStash {
 public:
  void put(std::string_view key, std::string_view value) override;
  [[nodiscard]] std::optional<std::string> get(std::string_view key) const override;
  void erase(std::string_view key) override;

 private:
  mutable std::mutex mutex_;
  std::vector<std::pair<std::string, std::string>> items_;
};

// What survives an extension reload (mod-design 2.6 "session.intent"). The auth hashes are SHA256(password), not
// the passwords; the stash is in-process memory and dies with the game. (ghost.registry / netmap.epoch are M2.)
struct SessionIntent {
  bool connect = false;  // false = none
  std::string host;
  std::uint16_t port = 47780;
  std::string name;
  std::uint8_t roles = 0;
  Id128 resume_token;
  Id128 session_id;
  std::uint64_t last_journal_seq = 0;
  std::string auth_hash_hex;   // SHA256(session password), empty = none
  std::string admin_hash_hex;  // SHA256(admin password), empty = none
};
inline constexpr std::string_view kStashIntentKey = "session.intent";
[[nodiscard]] bool save_intent(IStash& stash, const SessionIntent& intent);
[[nodiscard]] std::optional<SessionIntent> load_intent(const IStash& stash);  // nullopt if absent/invalid/connect=false
void clear_intent(IStash& stash);

// ---- parsed protocol state ---------------------------------------------------------------------------------------
struct ServerInfo {
  std::uint16_t protocol_major = 0;
  std::uint16_t protocol_minor = 0;
  std::string server_version;
  std::string server_name;
  Id128 session_id;
  std::uint8_t auth = 0;  // AuthMethod: 0 None, 1 SessionPassword
  std::uint64_t server_caps = 0;
  std::uint8_t phase = 0;  // SessionPhase
  std::string required_game_build;
  std::vector<std::uint8_t> nonce;
};

struct WelcomeInfo {
  std::uint16_t player_id = 0;
  std::uint8_t granted_roles = 0;
  std::uint64_t negotiated_caps = 0;
  Id128 resume_token;
  bool resumed = false;
  std::uint32_t conn_id = 0;
  std::uint16_t udp_port = 0;
  std::uint64_t udp_token = 0;
  std::uint64_t server_time_us = 0;
  std::uint16_t heartbeat_interval_ms = 1000;
  std::uint16_t heartbeat_timeout_ms = 10000;
  std::uint16_t resume_grace_s = 60;
  std::string http_base_url;
  std::uint32_t max_ghosts = 4000;
  std::uint16_t team_id = 0;
  std::uint8_t team_role = 0;
  std::uint8_t faction_slot = 0;
};

struct SaveInfo {  // SessionSaveInfo
  Id128 checkpoint_id;
  std::vector<std::uint8_t> sha256;
  std::uint64_t size = 0;
  std::string display_name;
  std::string local_file_name;  // "x4mp_<sha12>.xml.gz"
  std::vector<std::uint8_t> manifest_sha256;
  std::uint64_t manifest_size = 0;
  std::string http_url;
};

// Parsers over buffers the net layer already ran through the FlatBuffers Verifier. nullopt on null/empty input.
[[nodiscard]] std::optional<ServerInfo> parse_server_hello(std::span<const std::uint8_t> payload);
[[nodiscard]] std::optional<WelcomeInfo> parse_welcome(std::span<const std::uint8_t> payload);
[[nodiscard]] std::optional<SaveInfo> parse_save_info(std::span<const std::uint8_t> payload);

// ---- Session -----------------------------------------------------------------------------------------------------
struct ClientIdentity {
  std::string mod_version = "0.1.0";
  std::string mod_build = "dev";
  std::string game_version = "9.00";
  std::string game_build = "900-611726";
  std::string x4native_version = "9.0.0";
  std::string platform = "win64";
};

struct SessionOptions {
  net::Endpoint endpoint;
  // Net layer knobs (clock, timeouts, retention caps). The Session overrides frame_hook and forces
  // gate_outbox_until_released = true.
  net::NetOptions net;
  ClientIdentity identity;
  std::string player_name;
  std::array<std::uint8_t, 32> player_key{};  // identity; generated once, stored in the mod config
  std::optional<std::string> password;        // session password (never stored; only SHA256 of it is kept)
  std::optional<std::string> admin_password;
  std::uint8_t requested_roles = 2;           // Role::Client
  std::uint64_t client_caps = 0;
  std::uint16_t preferred_team = 0;
  IExtensionProvider* extensions = nullptr;   // not owned; may be null (empty list)
  IStash* stash = nullptr;                    // not owned; may be null (no reload survival)
  std::vector<std::uint8_t> loaded_save_sha256;  // save currently loaded in this game; empty = main menu
  std::filesystem::path save_dir;             // downloads land here; empty = never auto-download
  bool auto_download = true;
  // Reconnect as the same node after an extension reload (from load_intent()). Supplies the resume token and the
  // auth hashes (so no plain password is needed).
  std::optional<SessionIntent> resume;
};

struct SessionEvent {
  enum class Kind : std::uint8_t {
    StateChanged,      // state = new state, prev = old
    ServerHello,       // server filled in session view
    Welcome,           // welcome (resumed tells fresh join vs resume); payload = the raw Welcome frame
    ServerDisconnect,  // the server sent Disconnect: code/text/expected (handshake rejection or later); payload = the raw Disconnect frame
    ControlReplayed,   // count Control frames were replayed after a resume
    ControlDropped,    // count queued Control frames were dropped because the join was fresh
    SaveInfo,          // the session save is announced
    SaveProgress,      // download progress
    SaveReady,         // save (+manifest) verified on disk; SaveReady sent to the server
    SaveFailed,        // download failed (text = reason)
    Frame,             // any other inbound frame: type/lane/payload
    NetDisconnected,   // the connection ended (text); the net layer redials unless halted
  };
  Kind kind = Kind::Frame;
  State state = State::Disconnected;
  State prev = State::Disconnected;
  std::uint16_t code = 0;
  std::uint32_t retry_after_ms = 0;
  std::uint64_t count = 0;
  std::string text;
  std::string expected;
  std::uint16_t type = 0;
  wire::Lane lane = wire::Lane::Control;
  std::vector<std::uint8_t> payload;
  DownloadProgress progress;
  SaveInfo save;
};

class Session {
 public:
  explicit Session(SessionOptions options);
  ~Session();  // stop()
  Session(const Session&) = delete;
  Session& operator=(const Session&) = delete;

  // Starts the net thread and the handshake. False if already started or the net thread cannot start.
  [[nodiscard]] bool start();

  // Main thread, once per frame: drains net events, advances the state machine and downloads, appends events.
  void poll(std::vector<SessionEvent>& out);

  // Queues a frame (Control frames queued while the link is down are replayed after a resume, dropped after a fresh
  // join; Realtime/Bulk are dropped). Same semantics as NetClient::send.
  net::SendResult send(wire::Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload);

  // The caller says the game is loaded and replication may start (M2: after NodeReady; headless: after SaveReady).
  void mark_in_session();

  // Normal leave: Disconnect(ClientQuit), intent cleared. Idempotent.
  void stop();
  // Planned extension unload (save load / reloadui): intent -> stash, Disconnect(ClientReload), net thread joined.
  void unload_for_reload();
  // Skip the redial backoff (or leave Halted) and try now.
  void reconnect_now();
  // Failure injection / tests (x4mp-headless --udp-block-after): drop all UDP datagrams in both directions.
  void set_udp_block(bool block);

  // Starts a download of the announced save into save_dir (what poll() does automatically with auto_download).
  [[nodiscard]] bool download_save(const SaveInfo& info);

  [[nodiscard]] State state() const noexcept { return state_; }
  [[nodiscard]] const ServerInfo& server() const noexcept { return server_; }
  [[nodiscard]] const WelcomeInfo& welcome() const noexcept { return welcome_; }
  [[nodiscard]] bool has_welcome() const noexcept { return has_welcome_; }
  [[nodiscard]] Id128 resume_token() const noexcept { return welcome_.resume_token; }
  [[nodiscard]] net::NetStatus net_status() const noexcept;
  [[nodiscard]] DownloadProgress save_progress() const;
  [[nodiscard]] bool save_ready() const noexcept { return save_ready_; }
  [[nodiscard]] SessionIntent make_intent() const;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
  // Main-thread view (the Impl owns the net-thread half).
  State state_ = State::Disconnected;
  ServerInfo server_;
  WelcomeInfo welcome_;
  bool has_welcome_ = false;
  bool save_ready_ = false;
};

}  // namespace x4mp::session
