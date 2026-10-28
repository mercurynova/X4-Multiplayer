#pragma once
// features/authority: the in-game authority's checkpoint flow (M2-09; docs/m2-plan.md 5.3, docs/mod-design.md 6.2 and 7).
// Owned by the JoinFeature, which hands it the session events (marked "M2-09" there). Mirrors tools/headless/authority_driver.cpp
// (M2-08) but takes the save from X4 instead of a file on disk:
//
//   server RequestSave -> native raises Lua x4mp.auth_collect -> MD (md/x4mp_galaxy.xml) sends sectors + the player's ship through
//   Lua as verb x4mp.auth_md -> native raises Lua x4mp.auth_save {name} -> Lua calls SaveGame (inside X4MPSaves.allowSaves) and
//   answers x4mp.auth_saved {ok, game_time} -> SaveStarted(game_time) -> wait until <name>.xml.gz is complete (size stable, openable)
//   -> hash on a worker, empty-station manifest -> StringTableAdd (once), GalaxyMetadata, upload with CheckpointUploader (End only after
//   the final ack, stale SaveStored ignored) -> after both files are stored: ONE self-spawn EntitySpawn of the player's ship with the
//   same game_time. When MD had no player ship yet (the very first checkpoint ~2 s after the universe is ready, or the player stands in
//   the cockpit after a load: session 3), the spawn waits for the PILOT SEAT: the selfship feature (M3-09) reports the seat natively
//   (GetPlayerOccupiedShipID) and the flow asks MD for the ship ONCE per sit-down (x4mp.auth_collect {"ship_only":true}), or at once
//   when the player already sits while the checkpoint is stored. No timer, no retry limit: a player who stands for minutes still gets
//   the spawn when they sit down. MD answering "none" although the game says the player sits is asked again after a frame-counted
//   back-off (20 frames, doubling up to 600). The spawn carries the game time of that moment, never 0 -> old x4mp_ckpt_* saves beyond
//   the newest two are removed (only ones this mod made, listed in authority-saves.json).
//
// Threading: everything on the frame thread except the hash worker. Lua verbs only copy text into AuthInbox.

#include <chrono>
#include <cstdint>
#include <future>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <flatbuffers/flatbuffers.h>

#include "core/authority/upload_job.h"
#include "core/session/session.h"
#include "features/authority/authority_data.h"
#include "features/authority/capture_ingest.h"
#include "features/authority/capture_set.h"
#include "features/authority/entity_stream.h"
#include "features/authority/universe_index.h"
#include "features/avatars/avatar_plan.h"
#include "features/events/event_wire.h"
#include "host/feature.h"

namespace x4mp::features::auth {

struct AuthInbox {
  std::mutex mutex;
  std::vector<std::pair<std::string, std::string>> items;  // verb, text
  void push(std::string verb, std::string text);
  std::vector<std::pair<std::string, std::string>> take();
};
// Subscribes x4mp.auth_md and x4mp.auth_saved; the returned inbox outlives any flow.
[[nodiscard]] std::shared_ptr<AuthInbox> subscribe_authority_verbs(host::IPlatform& platform);

inline constexpr const char* kStashKey = "authority";  // PlatformStash adds its own prefix

class AuthorityFlow {
 public:
  using Clock = std::chrono::steady_clock;
  enum class Step : std::uint8_t { Idle, Collecting, WaitSaveReply, WaitFile, Hashing, Uploading };

  AuthorityFlow(host::HostContext& ctx, session::Session& session, session::IStash& stash, std::shared_ptr<AuthInbox> inbox);
  ~AuthorityFlow();
  AuthorityFlow(const AuthorityFlow&) = delete;
  AuthorityFlow& operator=(const AuthorityFlow&) = delete;

  void on_welcome(host::HostContext& ctx, bool resumed);
  void on_net_disconnected();
  // Routes a received frame: RequestSave starts a checkpoint, upload frames go to the uploader. True when consumed.
  bool on_frame(host::HostContext& ctx, std::uint16_t type, std::span<const std::uint8_t> payload);
  void step(host::HostContext& ctx);
  // The game now runs this save (a loaded session save): sent as ClientHello.loaded_save_sha256 by the next fresh join.
  void note_loaded_save(const std::vector<std::uint8_t>& sha);

  [[nodiscard]] Step current_step() const noexcept { return step_; }
  [[nodiscard]] const AuthorityState& state() const noexcept { return state_; }
  [[nodiscard]] bool waiting_for_ship() const noexcept { return ship_wait_; }
  // The player's seat state, every frame (from the selfship feature through its hub). `edges` counts sit-downs, stand-ups and ship changes:
  // a change resets the back-off so the next frame asks MD at once.
  void note_seat(bool seated, std::uint32_t edges) noexcept;
  [[nodiscard]] std::uint64_t checkpoints_stored() const noexcept { return stored_total_; }

  // Services for the avatars (M3-11, registered with avatar_hub() while this flow lives): the ONE net id counter and the ONE string table of the
  // session. A new avatar takes its id here; its macro and faction strings are added to the server's table on first use.
  [[nodiscard]] std::uint32_t allocate_net_id();
  void reserve_net_ids_above(std::uint32_t id) noexcept;
  [[nodiscard]] std::uint32_t string_ref(std::uint8_t kind, const std::string& value);
  [[nodiscard]] bool connected() const noexcept { return uploader_.connected(); }
  [[nodiscard]] bool send_frame(std::uint16_t type, const std::vector<std::uint8_t>& payload) { return send_control(type, payload); }

  // Stash helpers shared with the join feature (the ClientHello of a fresh authority join).
  [[nodiscard]] static std::vector<std::uint8_t> stored_loaded_sha(const session::IStash& stash);
  static void forget_loaded_sha(session::IStash& stash);

  // M4-01/02: what the server asked us to capture, and the entities we know about. The streamer (M4-06..) reads both.
  [[nodiscard]] const CaptureSet& capture() const noexcept { return capture_; }
  [[nodiscard]] const UniverseIndex& index() const noexcept { return index_; }
  // The scan answer from our own MD (bridge verb x4mp.capture_scan): rows of one sector, or its terminating count. `sector_macro` is resolved
  // to the wire index through the self-ship galaxy map; an unknown macro is counted, never guessed.
  void on_scan_text(host::HostContext& ctx, std::string_view text);

 private:
  struct Prepared {
    bool ok = false;
    std::string error;
    x4mp::authority::UploadFile save, manifest;
    std::filesystem::path manifest_path;
  };

  void drain_inbox(host::HostContext& ctx);
  void on_capture_set(host::HostContext& ctx, std::span<const std::uint8_t> payload);
  // M5-07: an Intent forwarded by the server (a client's kill claim). Validates, applies and answers with exactly one IntentResult.
  void on_intent(host::HostContext& ctx, std::span<const std::uint8_t> payload);
  // M5-15/16: a trade report from a client. Applies the stock change to the authority's own copy of the station, answers with one
  // IntentResult, sends the station's EntityCargo and the TradeEvent, and moves the NPC side's money (M5-17).
  void on_trade_report(host::HostContext& ctx, const events::IntentView& intent, std::span<const std::uint8_t> payload);
  void apply_trade(host::HostContext& ctx, const events::IntentRequest& request, std::uint32_t station, std::uint32_t ship,
                   std::uint64_t component, const std::string& ware, std::int32_t amount, std::int32_t unit_price);
  void retry_trades(host::HostContext& ctx);
  void send_station_cargo(host::HostContext& ctx, std::uint32_t station, std::uint64_t component);
  void begin_request(host::HostContext& ctx, std::uint32_t request_id);
  void step_collecting(host::HostContext& ctx);
  void request_save(host::HostContext& ctx);
  void on_save_reply(host::HostContext& ctx, const std::string& text);
  void step_wait_file(host::HostContext& ctx);
  void step_hashing(host::HostContext& ctx);
  void step_uploading(host::HostContext& ctx);
  void on_checkpoint_stored(host::HostContext& ctx);
  void maybe_spawn(host::HostContext& ctx);
  void step_ship_wait(host::HostContext& ctx);
  void schedule_reask() noexcept;
  [[nodiscard]] std::uint32_t late_ship_macro_ref(host::HostContext& ctx, const std::string& macro);
  void fail(host::HostContext& ctx, const std::string& why);
  void persist() const;
  [[nodiscard]] bool send_control(std::uint16_t type, const std::vector<std::uint8_t>& payload);

  session::Session& session_;
  session::IStash& stash_;
  std::shared_ptr<AuthInbox> inbox_;
  x4mp::authority::CheckpointUploader uploader_;
  AuthorityState state_;
  std::filesystem::path save_dir_, work_dir_, ledger_;

  Step step_ = Step::Idle;
  Clock::time_point step_since_{};
  std::uint32_t request_id_ = 0;
  MdCollector collected_;
  std::optional<Clock::time_point> end_seen_at_;
  GalaxyPlan plan_;
  session::Id128 checkpoint_;
  std::string save_name_;
  double game_time_ = 0;
  std::optional<SaveFileWatcher> watcher_;
  Clock::time_point next_poll_{};
  std::future<Prepared> prepared_;
  Prepared ready_;
  std::uint64_t counted_stored_ = 0;
  std::uint64_t stored_total_ = 0;
  bool spawn_due_ = false;
  // The ship was unknown when the checkpoint finished: ask MD (x4mp.auth_collect {"ship_only":true}) when the player sits (seat edge).
  bool ship_wait_ = false;
  bool ship_pending_ = false;
  int ship_asks_ = 0;
  Clock::time_point ship_asked_{};
  bool seated_ = false;
  std::uint32_t seat_edges_ = 0;
  int ask_wait_frames_ = 0;     // frames until the next ask
  int ask_backoff_frames_ = 0;  // the back-off after an "MD has no ship" answer while the game says the player sits
  bool late_ship_ = false;            // spawn_ship_ came from a retry (not from the checkpoint's own collection)
  bool spawn_strings_fresh_ = false;  // the plan's string table was sent with THIS checkpoint (its refs are the server's)
  std::vector<std::pair<std::string, std::uint32_t>> sent_macros_;  // macro strings already in the server's table (memory only)
  struct KnownString {
    std::uint8_t kind = 0;
    std::string value;
    std::uint32_t index = 0;
  };
  std::vector<KnownString> known_strings_;  // every string (any kind) the server's table holds that this flow knows of (memory only)
  bool ghosts_cleaned_ = true;                        // M3-13: the pre-SaveGame hygiene check's verdict, sent as SaveUploadBegin.ghosts_cleaned
  std::vector<avatars::Record> manifest_avatars_;     // M3-11: the avatars as of SaveGame, for the manifest
  std::optional<ShipRec> spawn_ship_;
  GalaxyPlan spawn_plan_;
  // M3-14: the authority's clock reference. The server replicates player ships only after it has seen one WorldUpdate (its game time and capture time
  // are the reference every entity state is stamped with). M4-08: the same 20 Hz tick now carries the captured world through EntityStream, and an empty
  // WorldUpdate is still the keepalive when nothing moved.
  void step_world_clock(host::HostContext& ctx);
  Clock::time_point next_wu_{};
  bool wu_logged_ = false;

  // M4-01/02: the server's interest and the entities we know. `capture_stale_` counts resends (refused, not an error), `capture_bad_` broken
  // messages, `scan_no_sector_` scans of a sector macro the galaxy map does not know.
  CaptureSet capture_;
  UniverseIndex index_;
  // M4-06..11: the world streamer. Reads poses natively (the scan carries ids only), owns the per-entity dead-reckoning state and
  // sends EntitySpawn / WorldUpdate / EntityDespawn / EntityChange / EntityStatusBatch through this flow's lanes.
  EntityStream stream_;
  // M5-07: what the authority did with the kill claims it received, one counter per verdict, so the 30 s line can explain them.
  std::uint32_t intent_bad_ = 0;
  std::uint32_t intent_unsupported_ = 0;
  std::uint32_t intent_unknown_ = 0;
  std::uint32_t intent_out_of_interest_ = 0;
  std::uint32_t intent_player_ = 0;
  std::uint32_t intent_failed_ = 0;
  std::uint32_t kills_applied_ = 0;
  std::uint32_t players_died_ = 0;
  std::uint32_t capture_sets_ = 0, capture_stale_ = 0, capture_bad_ = 0;
  std::uint32_t scan_batches_ = 0, scan_bad_ = 0, scan_no_sector_ = 0;
  // M4-03: a sector whose scan MD has been asked for, and when. The entry is dropped when the closing count arrives, so a sector that MD
  // never answered is asked again after kScanRetryUs instead of waiting forever.
  static constexpr std::int64_t kScanRetryUs = 10'000'000;
  std::unordered_map<std::uint16_t, std::int64_t> scan_asked_;
  std::uint32_t scan_request_failed_ = 0;
  // M5-15: a trade report whose station this node cannot resolve yet. It is retried for kTradeRetryUs while the statics scan
  // catches up; after that the client gets one Rejected and the report is dropped (never applied to a guessed station).
  struct PendingTrade {
    events::IntentRequest request;
    std::uint32_t station = 0;
    std::uint32_t ship = 0;
    std::string ware;
    std::int32_t amount = 0;
    std::int32_t unit_price = 0;
    std::int64_t at_us = 0;
  };
  static constexpr std::int64_t kTradeRetryUs = 5'000'000;
  static constexpr std::size_t kMaxPendingTrades = 64;
  std::vector<PendingTrade> pending_trades_;
  // M5-16: a station whose stock just changed. The snapshot is read a frame or two after the shim ran, so a client that applies the
  // delta and then reads the same station sees the same number, not the pre-trade hold.
  struct SnapshotDue {
    std::uint32_t station = 0;
    std::uint64_t component = 0;
    std::int64_t at_us = 0;
  };
  static constexpr std::int64_t kSnapshotDelayUs = 30'000;
  std::vector<SnapshotDue> snapshot_due_;
  std::uint32_t trades_applied_ = 0;
  std::uint32_t trades_deferred_ = 0;
  std::uint32_t trades_dropped_ = 0;
  std::uint32_t trades_unknown_ = 0;
  std::uint32_t stock_ops_ = 0;
  std::uint32_t snapshots_sent_ = 0;
};

}  // namespace x4mp::features::auth
