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
#include <utility>
#include <vector>

#include "core/authority/upload_job.h"
#include "core/session/session.h"
#include "features/authority/authority_data.h"
#include "features/avatars/avatar_plan.h"
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

 private:
  struct Prepared {
    bool ok = false;
    std::string error;
    x4mp::authority::UploadFile save, manifest;
    std::filesystem::path manifest_path;
  };

  void drain_inbox(host::HostContext& ctx);
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
};

}  // namespace x4mp::features::auth
