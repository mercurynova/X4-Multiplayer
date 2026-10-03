#pragma once
// core/mods: the real extension list for ClientHello (M2-03 = M2-X2; docs/mod-management.md 1.1, 2, 3.1, 3.2, 8).
//
//   * Resolve extension id -> folder over the install `extensions\`, the user `Documents\Egosoft\X4\<id>\extensions\`
//     and Steam Workshop roots. Roots are PASSED IN (core stays platform-neutral); host/extension_roots.* builds them
//     on Windows.
//   * Parse content.xml (id, name, version, enabled default, save flag, dependencies), detect native DLLs
//     (x4native.json "library" or any *.dll) and subst_*.cat (base-game replacement), derive a class hint.
//   * Content hash: SHA-256 over the .cat INDEX files (HashKind::CatIndex) or, for loose mods, over sorted
//     (relative path, size, sha256) lines capped at 64 MB / 2000 files (HashKind::Files). Independent of directory
//     enumeration order. Cached on disk keyed by (folder path, total size, newest mtime, file count).
//   * ExtensionProvider implements session::IExtensionProvider. All file I/O runs on its worker thread; snapshot()
//     (main thread) only waits up to 2 s for the worker, then returns what it has (no hashes) -- never I/O.
//
// Threading: free functions here do file I/O and REFUSE to run on a thread that called mark_frame_thread(true): they
// count a violation (frame_thread_io_violations()) and return an empty result. The host marks its frame thread; tests
// assert the counter stays 0 and that the guard trips.

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>

#include "core/session/session.h"

namespace x4mp::mods {

// ---- frame-thread I/O guard ------------------------------------------------------------------------------------
void mark_frame_thread(bool is_frame_thread) noexcept;  // per calling thread
[[nodiscard]] bool is_frame_thread() noexcept;
[[nodiscard]] std::uint64_t frame_thread_io_violations() noexcept;
void reset_frame_thread_io_violations() noexcept;  // tests

// ---- values (wire numbers of ExtensionSource / ExtensionClass / HashKind) ----------------------------------------
enum class Source : std::uint8_t { Dlc = 0, Install = 1, User = 2, Workshop = 3 };
enum class ClassHint : std::uint8_t { Unknown = 0, Dlc = 1, Sim = 2, ClientOnly = 3 };
enum class HashKind : std::uint8_t { None = 0, CatIndex = 1, Files = 2 };

inline constexpr std::uintmax_t kHashByteCap = 64ull * 1024 * 1024;
inline constexpr std::size_t kHashFileCap = 2000;
inline constexpr std::chrono::milliseconds kSnapshotWait{2000};

// What the folders contain, at the top level (loose directories plus .cat index entries).
struct Shape {
  bool sim_content = false;      // md/, aiscripts/, libraries/, maps/, index/, assets/, ... (or save="1")
  bool ui_content = false;       // t/, ui/, textures/, sound/, ...
  bool unknown_content = false;  // top-level folders we do not recognise
  bool native_dll = false;
  bool subst_cat = false;
  bool cat_files = false;
};

struct ExtensionDependency {
  std::string id;
  bool optional = false;
};

// One extension folder as found on disk.
struct ExtensionRecord {
  std::filesystem::path folder;
  Source source = Source::Install;
  std::string id;  // content.xml id; empty only when content.xml is unusable (then `error` is set)
  std::string name;
  std::string version;
  bool enabled_default = true;
  bool save_dependent = false;
  std::uint64_t workshop_id = 0;
  std::vector<ExtensionDependency> dependencies;
  Shape shape;
  ClassHint class_hint = ClassHint::Unknown;
  bool has_native_dll = false;
  bool replaces_basegame = false;
  std::vector<std::uint8_t> content_hash;  // empty = not computed
  HashKind hash_kind = HashKind::None;
  std::string error;
  std::string warning;
};

// Where to look. Directories that do not exist are skipped silently.
struct ExtensionRoots {
  std::filesystem::path install_extensions;           // <X4>\extensions
  std::vector<std::filesystem::path> user_game_dirs;  // Documents\Egosoft\X4 (scans <id>\extensions and extensions)
  std::vector<std::filesystem::path> workshop_roots;  // ...\workshop\content\392160 (subfolder name = workshop id)
};

// ---- content.xml -------------------------------------------------------------------------------------------------
struct ContentXml {
  std::string id, name, version;
  bool enabled = true;
  bool save = false;
  std::vector<ExtensionDependency> dependencies;
};
// Tolerant scanner (no XML library): reads attributes of the <content> element and every <dependency>.
// nullopt if there is no <content id="..."> element.
[[nodiscard]] std::optional<ContentXml> parse_content_xml(std::string_view text);
// The version text the game itself shows (GetExtensionList, Lua) for a content.xml version: X4 stores versions as an integer in
// hundredths ("900" = 9.00, "105" = 1.05), the game reports "9.00". A value that is not all digits ("1.4", "2.0-beta") is kept as it
// is. Every version the DLL puts into a ClientHello goes through this, so the native scan and the Lua list always agree.
[[nodiscard]] std::string normalize_version(std::string_view version);

// ---- class hint / shape (pure, no I/O) ---------------------------------------------------------------------------
// Order of mod-management 2: Dlc prefix, shipped allowlist (-> ClientOnly), then the heuristic from `shape`.
[[nodiscard]] ClassHint classify(std::string_view id, bool save_dependent, const Shape& shape);
[[nodiscard]] const char* to_string(ClassHint) noexcept;
// Top-level folder of one cat entry / path ("md/foo.xml" -> "md"); empty for a root-level file.
[[nodiscard]] std::string top_level_folder(std::string_view path);
void add_top_level_folder(Shape& shape, std::string_view folder);

// ---- hash cache --------------------------------------------------------------------------------------------------
struct CacheKey {
  std::string folder;  // generic string of the folder path
  std::uint64_t total_size = 0;
  std::int64_t newest_mtime = 0;  // file_time_type ticks
  std::uint64_t file_count = 0;
  friend bool operator==(const CacheKey&, const CacheKey&) = default;
};
struct CacheEntry {
  CacheKey key;
  std::vector<std::uint8_t> hash;
  HashKind kind = HashKind::None;
};
class HashCache {
 public:
  void load(const std::filesystem::path& file);  // worker thread only; a missing/corrupt file = empty
  void save(const std::filesystem::path& file) const;
  [[nodiscard]] const CacheEntry* find(const CacheKey& key) const;
  void put(CacheEntry entry);
  [[nodiscard]] std::size_t size() const noexcept { return entries_.size(); }
  [[nodiscard]] bool dirty() const noexcept { return dirty_; }

 private:
  std::unordered_map<std::string, CacheEntry> entries_;  // by folder
  bool dirty_ = false;
};

// ---- scan --------------------------------------------------------------------------------------------------------
struct ScanStats {
  std::size_t folders = 0;
  std::size_t hashed = 0;  // computed from file contents
  std::size_t cache_hits = 0;
};
struct ScanResult {
  std::vector<ExtensionRecord> records;  // sorted by (id, folder): deterministic whatever the enumeration order
  ScanStats stats;
};

// Reads one folder (source is the root it was found under). Worker thread only.
[[nodiscard]] ExtensionRecord scan_folder(const std::filesystem::path& folder, Source source, HashCache* cache,
                                          ScanStats* stats = nullptr);
// All roots. `cache` may be null (no caching). Worker thread only.
[[nodiscard]] ScanResult scan_extensions(const ExtensionRoots& roots, HashCache* cache);
// id -> first record in priority order install, user, workshop (duplicates get a warning on the later ones).
[[nodiscard]] const ExtensionRecord* find_by_id(const ScanResult& scan, std::string_view id);

// ---- the provider ------------------------------------------------------------------------------------------------
// What the Lua list (x4mp.extensions, M2-X1) tells us: the game's own view of enabled/version/name.
struct ReportedExtension {
  std::string id, name, version;
  bool enabled = false;
  bool egosoft = false;
  std::string error, warning;
};

struct ProviderOptions {
  ExtensionRoots roots;
  std::filesystem::path cache_file;  // empty = no on-disk cache (x4mp\ext-hash-cache.json in the mod)
  std::chrono::milliseconds wait = kSnapshotWait;
  // Test seam: called on the worker before scanning (block it to exercise the snapshot timeout).
  std::function<void()> worker_gate;
};

// The extension-set hash (the fast path in ClientHello): SHA-256 of the sorted "id@version\n" lines of the enabled
// Dlc and Sim(+Unknown) extensions; allowlisted client-only libraries, ids in hashExcludedIds and ClientOnly ones are
// left out. `entries` receives the same "id@version" strings, `list` the full report. `reported` (Lua) overrides
// name/version/enabled; null = scan only (content.xml defaults). `with_hashes` false = no content hashes (timeout).
[[nodiscard]] session::ExtensionSnapshot build_snapshot(const std::vector<ExtensionRecord>& scanned,
                                                        const std::vector<ReportedExtension>* reported,
                                                        bool with_hashes);

class ExtensionProvider final : public session::IExtensionProvider {
 public:
  explicit ExtensionProvider(ProviderOptions options);
  ~ExtensionProvider() override;
  ExtensionProvider(const ExtensionProvider&) = delete;
  ExtensionProvider& operator=(const ExtensionProvider&) = delete;

  // Starts (or restarts, e.g. after /reloadui) the worker scan. Cheap, returns at once; safe from the frame thread.
  void start();
  // Lua list from x4mp.extensions. Any thread; takes effect at the next snapshot().
  void set_reported(std::vector<ReportedExtension> reported);

  // IExtensionProvider: main thread. Waits at most options.wait for the worker; after that the report goes out
  // without content hashes (hash_kind None). Does no file I/O.
  session::ExtensionSnapshot snapshot() override;

  [[nodiscard]] bool ready() const;              // worker finished
  [[nodiscard]] ScanResult scan_result() const;  // copy; empty until ready()

 private:
  void run(std::uint64_t generation);
  ProviderOptions opt_;
  mutable std::mutex mutex_;
  std::condition_variable cv_;
  std::vector<ReportedExtension> reported_;
  bool has_reported_ = false;
  ScanResult scan_;
  bool done_ = false;
  bool started_ = false;
  std::uint64_t generation_ = 0;
  std::thread worker_;
};

}  // namespace x4mp::mods
