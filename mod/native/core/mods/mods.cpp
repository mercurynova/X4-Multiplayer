#include "core/mods/mods.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <charconv>
#include <fstream>
#include <iterator>
#include <set>
#include <system_error>

#include <nlohmann/json.hpp>

#include "core/crypto/crypto.h"
#include "x4mp/mod_policy_constants.h"

namespace x4mp::mods {

namespace fs = std::filesystem;

// ---- frame-thread guard ----------------------------------------------------------------------------------------
namespace {

thread_local bool t_frame_thread = false;
std::atomic<std::uint64_t> g_violations{0};

// Every function that touches the disk starts with this. On the frame thread it refuses (and counts).
bool io_allowed() noexcept {
  if (t_frame_thread) {
    g_violations.fetch_add(1, std::memory_order_relaxed);
    return false;
  }
  return true;
}

}  // namespace

void mark_frame_thread(bool is_frame_thread) noexcept { t_frame_thread = is_frame_thread; }
bool is_frame_thread() noexcept { return t_frame_thread; }
std::uint64_t frame_thread_io_violations() noexcept { return g_violations.load(std::memory_order_relaxed); }
void reset_frame_thread_io_violations() noexcept { g_violations.store(0, std::memory_order_relaxed); }

// ---- small helpers -----------------------------------------------------------------------------------------------
namespace {

std::string to_lower(std::string_view s) {
  std::string r(s);
  for (auto& c : r) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  return r;
}

std::string hex(const std::vector<std::uint8_t>& bytes) {
  static constexpr char kDigits[] = "0123456789abcdef";
  std::string r;
  r.reserve(bytes.size() * 2);
  for (const auto b : bytes) {
    r.push_back(kDigits[b >> 4]);
    r.push_back(kDigits[b & 15]);
  }
  return r;
}

std::optional<std::vector<std::uint8_t>> unhex(std::string_view s) {
  if (s.size() % 2 != 0) return std::nullopt;
  std::vector<std::uint8_t> r;
  r.reserve(s.size() / 2);
  for (std::size_t i = 0; i < s.size(); i += 2) {
    unsigned v = 0;
    const auto [p, ec] = std::from_chars(s.data() + i, s.data() + i + 2, v, 16);
    if (ec != std::errc{} || p != s.data() + i + 2) return std::nullopt;
    r.push_back(static_cast<std::uint8_t>(v));
  }
  return r;
}

bool is_numeric(std::string_view s) { return !s.empty() && std::all_of(s.begin(), s.end(), [](char c) { return c >= '0' && c <= '9'; }); }

std::string utf8_generic(const fs::path& p) {
  const auto u = p.generic_u8string();
  return std::string(reinterpret_cast<const char*>(u.data()), u.size());
}

std::optional<std::string> read_text(const fs::path& file, std::uintmax_t max_bytes) {
  std::error_code ec;
  const auto size = fs::file_size(file, ec);
  if (ec || size > max_bytes) return std::nullopt;
  std::ifstream in(file, std::ios::binary);
  if (!in) return std::nullopt;
  std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
  return text;
}

struct FileEntry {
  std::string rel;  // lowercase, '/' separators
  fs::path abs;
  std::uintmax_t size = 0;
  std::int64_t mtime = 0;
};

// Recursive listing, sorted by `rel`: the order never depends on how the OS enumerates. Symlinked directories are not followed.
std::vector<FileEntry> walk(const fs::path& folder) {
  std::vector<FileEntry> out;
  std::error_code ec;
  fs::recursive_directory_iterator it(folder, fs::directory_options::skip_permission_denied, ec);
  const fs::recursive_directory_iterator end;
  for (; !ec && it != end; it.increment(ec)) {
    std::error_code e2;
    if (!it->is_regular_file(e2) || e2) continue;
    FileEntry f;
    f.abs = it->path();
    f.rel = to_lower(utf8_generic(fs::relative(f.abs, folder, e2)));
    f.size = it->file_size(e2);
    if (e2) f.size = 0;
    const auto t = it->last_write_time(e2);
    f.mtime = e2 ? 0 : static_cast<std::int64_t>(t.time_since_epoch().count());
    out.push_back(std::move(f));
  }
  std::sort(out.begin(), out.end(), [](const FileEntry& a, const FileEntry& b) { return a.rel < b.rel; });
  return out;
}

bool is_root_file(const FileEntry& f) { return f.rel.find('/') == std::string::npos; }
bool ends_with(const std::string& s, std::string_view suffix) { return s.size() >= suffix.size() && s.compare(s.size() - suffix.size(), suffix.size(), suffix) == 0; }

bool in_list(const auto& list, std::string_view id) { return std::find(list.begin(), list.end(), id) != list.end(); }

bool is_dlc_id(std::string_view id) { return id.starts_with(mod_policy::kDlcIdPrefix); }

}  // namespace

// ---- content.xml -------------------------------------------------------------------------------------------------
namespace {

std::string decode_entities(std::string_view s) {
  std::string r;
  r.reserve(s.size());
  for (std::size_t i = 0; i < s.size(); ++i) {
    if (s[i] == '&') {
      static constexpr std::array<std::pair<std::string_view, char>, 5> kEnt{
          {{"&amp;", '&'}, {"&lt;", '<'}, {"&gt;", '>'}, {"&quot;", '"'}, {"&apos;", '\''}}};
      bool done = false;
      for (const auto& [name, ch] : kEnt) {
        if (s.compare(i, name.size(), name) == 0) {
          r.push_back(ch);
          i += name.size() - 1;
          done = true;
          break;
        }
      }
      if (done) continue;
    }
    r.push_back(s[i]);
  }
  return r;
}

struct Tag {
  std::string name;
  std::vector<std::pair<std::string, std::string>> attrs;
  [[nodiscard]] const std::string* get(std::string_view key) const {
    for (const auto& [k, v] : attrs) {
      if (k == key) return &v;
    }
    return nullptr;
  }
};

bool truthy(const std::string* v, bool dflt) {
  if (v == nullptr) return dflt;
  const auto l = to_lower(*v);
  if (l == "1" || l == "true" || l == "yes") return true;
  if (l == "0" || l == "false" || l == "no") return false;
  return dflt;
}

bool space(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

// Reads the next start/empty-element tag from `text` at `pos` (skips comments, PIs, doctype, end tags).
bool next_tag(std::string_view text, std::size_t& pos, Tag& tag) {
  while (true) {
    const auto lt = text.find('<', pos);
    if (lt == std::string_view::npos) return false;
    if (text.compare(lt, 4, "<!--") == 0) {
      const auto e = text.find("-->", lt + 4);
      if (e == std::string_view::npos) return false;
      pos = e + 3;
      continue;
    }
    if (lt + 1 < text.size() && (text[lt + 1] == '?' || text[lt + 1] == '!' || text[lt + 1] == '/')) {
      const auto e = text.find('>', lt);
      if (e == std::string_view::npos) return false;
      pos = e + 1;
      continue;
    }
    std::size_t i = lt + 1;
    tag = Tag{};
    while (i < text.size() && !space(text[i]) && text[i] != '>' && text[i] != '/') tag.name.push_back(text[i++]);
    while (i < text.size()) {
      while (i < text.size() && (space(text[i]) || text[i] == '/')) ++i;
      if (i >= text.size()) return false;
      if (text[i] == '>') {
        pos = i + 1;
        return true;
      }
      std::string key;
      while (i < text.size() && !space(text[i]) && text[i] != '=' && text[i] != '>' && text[i] != '/') key.push_back(text[i++]);
      while (i < text.size() && space(text[i])) ++i;
      if (i < text.size() && text[i] == '=') {
        ++i;
        while (i < text.size() && space(text[i])) ++i;
        if (i < text.size() && (text[i] == '"' || text[i] == '\'')) {
          const char q = text[i++];
          const auto e = text.find(q, i);
          if (e == std::string_view::npos) return false;
          tag.attrs.emplace_back(key, decode_entities(text.substr(i, e - i)));
          i = e + 1;
        } else {
          return false;  // unquoted value: malformed
        }
      } else if (!key.empty()) {
        tag.attrs.emplace_back(key, std::string{});
      } else {
        ++i;
      }
    }
    return false;
  }
}

}  // namespace

std::optional<ContentXml> parse_content_xml(std::string_view text) {
  ContentXml c;
  bool have = false;
  std::size_t pos = 0;
  Tag tag;
  while (next_tag(text, pos, tag)) {
    if (tag.name == "content" && !have) {
      const auto* id = tag.get("id");
      if (id == nullptr || id->empty()) return std::nullopt;
      have = true;
      c.id = *id;
      if (const auto* v = tag.get("name")) c.name = *v;
      if (const auto* v = tag.get("version")) c.version = *v;
      c.enabled = truthy(tag.get("enabled"), true);
      c.save = truthy(tag.get("save"), false);
    } else if (tag.name == "dependency" && have) {
      if (const auto* id = tag.get("id"); id != nullptr && !id->empty()) {
        c.dependencies.push_back(ExtensionDependency{*id, truthy(tag.get("optional"), false)});
      }
    }
  }
  if (!have) return std::nullopt;
  return c;
}

std::string normalize_version(std::string_view version) {
  while (!version.empty() && space(version.front())) version.remove_prefix(1);
  while (!version.empty() && space(version.back())) version.remove_suffix(1);
  if (version.empty() || version.size() > 9) return std::string(version);
  for (const char ch : version) {
    if (ch < '0' || ch > '9') return std::string(version);
  }
  if (version.size() < 3) return std::string(version);  // too short to be hundredths ("1"): leave it alone
  std::string digits(version);
  while (digits.size() > 3 && digits.front() == '0') digits.erase(0, 1);
  digits.insert(digits.size() - 2, ".");
  return digits;
}

// ---- class hint --------------------------------------------------------------------------------------------------
std::string top_level_folder(std::string_view path) {
  while (!path.empty() && (path.front() == '/' || path.front() == '\\')) path.remove_prefix(1);
  const auto s = path.find_first_of("/\\");
  if (s == std::string_view::npos) return {};
  return to_lower(path.substr(0, s));
}

void add_top_level_folder(Shape& shape, std::string_view folder) {
  static constexpr std::array<std::string_view, 7> kSim{"md", "aiscripts", "libraries", "maps", "index", "assets", "extensions"};
  static constexpr std::array<std::string_view, 10> kUi{"t",     "ui",    "textures", "sound",  "sounds",
                                                        "music", "voice", "video",    "shaders", "cutscenes"};
  if (folder.empty() || folder == "native") return;
  const auto f = to_lower(folder);
  if (in_list(kSim, f)) {
    shape.sim_content = true;
  } else if (in_list(kUi, f)) {
    shape.ui_content = true;
  } else {
    shape.unknown_content = true;
  }
}

ClassHint classify(std::string_view id, bool save_dependent, const Shape& shape) {
  if (is_dlc_id(id)) return ClassHint::Dlc;
  if (in_list(mod_policy::kClientOnlyLibraryIds, id)) return ClassHint::ClientOnly;
  if (save_dependent || shape.sim_content || shape.native_dll) return ClassHint::Sim;
  if (shape.ui_content && !shape.unknown_content) return ClassHint::ClientOnly;
  return ClassHint::Unknown;
}

const char* to_string(ClassHint c) noexcept {
  switch (c) {
    case ClassHint::Unknown: return "Unknown";
    case ClassHint::Dlc: return "Dlc";
    case ClassHint::Sim: return "Sim";
    case ClassHint::ClientOnly: return "ClientOnly";
  }
  return "?";
}

// ---- cache -------------------------------------------------------------------------------------------------------
void HashCache::load(const fs::path& file) {
  entries_.clear();
  dirty_ = false;
  if (!io_allowed()) return;
  const auto text = read_text(file, 16u * 1024 * 1024);
  if (!text) return;
  const auto j = nlohmann::json::parse(*text, nullptr, false);
  if (!j.is_object() || j.value("version", 0) != 1 || !j.contains("entries") || !j["entries"].is_array()) return;
  for (const auto& e : j["entries"]) {
    if (!e.is_object()) continue;
    CacheEntry c;
    c.key.folder = e.value("folder", std::string{});
    c.key.total_size = e.value("size", std::uint64_t{0});
    c.key.newest_mtime = e.value("mtime", std::int64_t{0});
    c.key.file_count = e.value("count", std::uint64_t{0});
    const auto h = unhex(e.value("hash", std::string{}));
    const auto kind = e.value("kind", 0);
    if (c.key.folder.empty() || !h || (kind != 1 && kind != 2) || h->size() != crypto::kSha256Size) continue;
    c.hash = *h;
    c.kind = static_cast<HashKind>(kind);
    auto folder = c.key.folder;
    entries_.emplace(std::move(folder), std::move(c));
  }
}

void HashCache::save(const fs::path& file) const {
  if (!io_allowed() || !dirty_) return;
  std::vector<const CacheEntry*> sorted;
  for (const auto& [k, v] : entries_) sorted.push_back(&v);
  std::sort(sorted.begin(), sorted.end(), [](const CacheEntry* a, const CacheEntry* b) { return a->key.folder < b->key.folder; });
  nlohmann::json j;
  j["version"] = 1;
  auto arr = nlohmann::json::array();
  for (const auto* e : sorted) {
    arr.push_back({{"folder", e->key.folder},
                   {"size", e->key.total_size},
                   {"mtime", e->key.newest_mtime},
                   {"count", e->key.file_count},
                   {"hash", hex(e->hash)},
                   {"kind", static_cast<int>(e->kind)}});
  }
  j["entries"] = std::move(arr);
  std::error_code ec;
  if (file.has_parent_path()) fs::create_directories(file.parent_path(), ec);
  const auto tmp = fs::path(file).concat(".tmp");
  {
    std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
    if (!out) return;
    out << j.dump();
    if (!out) return;
  }
  fs::rename(tmp, file, ec);
  if (ec) fs::remove(tmp, ec);
}

const CacheEntry* HashCache::find(const CacheKey& key) const {
  const auto it = entries_.find(key.folder);
  if (it == entries_.end() || !(it->second.key == key)) return nullptr;
  return &it->second;
}

void HashCache::put(CacheEntry entry) {
  auto folder = entry.key.folder;
  entries_[std::move(folder)] = std::move(entry);
  dirty_ = true;
}

// ---- scanning ----------------------------------------------------------------------------------------------------
namespace {

// Parses the entries of one .cat index: "<path> <size> <timestamp> <md5>" per line; the path may hold spaces, so the
// last three tokens are peeled off from the right.
void scan_cat_index(const std::string& text, Shape& shape) {
  std::size_t pos = 0;
  while (pos < text.size()) {
    auto eol = text.find('\n', pos);
    if (eol == std::string::npos) eol = text.size();
    std::string_view line(text.data() + pos, eol - pos);
    pos = eol + 1;
    while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.remove_suffix(1);
    for (int i = 0; i < 3; ++i) {
      const auto sp = line.find_last_of(' ');
      if (sp == std::string_view::npos) {
        line = {};
        break;
      }
      line = line.substr(0, sp);
    }
    if (!line.empty()) add_top_level_folder(shape, top_level_folder(line));
  }
}

struct HashOutcome {
  std::vector<std::uint8_t> hash;
  HashKind kind = HashKind::None;
  bool computed = false;  // read file contents (not a cache hit)
};

HashOutcome content_hash(const std::vector<FileEntry>& files, const fs::path& folder, HashCache* cache) {
  CacheKey key;
  key.folder = utf8_generic(folder);
  key.file_count = files.size();
  for (const auto& f : files) {
    key.total_size += f.size;
    key.newest_mtime = std::max(key.newest_mtime, f.mtime);
  }
  if (cache != nullptr) {
    if (const auto* hit = cache->find(key)) return HashOutcome{hit->hash, hit->kind, false};
  }

  std::vector<const FileEntry*> set;
  for (const auto& f : files) {
    if (is_root_file(f) && ends_with(f.rel, ".cat")) set.push_back(&f);
  }
  HashKind kind = HashKind::CatIndex;
  if (set.empty()) {
    kind = HashKind::Files;
    for (const auto& f : files) set.push_back(&f);
  }
  std::uintmax_t bytes = 0;
  for (const auto* f : set) bytes += f->size;
  if (set.empty() || set.size() > kHashFileCap || bytes > kHashByteCap) return {};

  crypto::Sha256Hasher outer;
  for (const auto* f : set) {  // `files` is sorted by rel, so `set` is too
    const auto digest = crypto::sha256_file(f->abs);
    if (!digest) return {};
    const std::string line =
        f->rel + "\n" + std::to_string(f->size) + "\n" + hex(std::vector<std::uint8_t>(digest->begin(), digest->end())) + "\n";
    outer.update(std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(line.data()), line.size()));
  }
  const auto total = outer.finish();
  if (!total) return {};
  HashOutcome r;
  r.hash.assign(total->begin(), total->end());
  r.kind = kind;
  r.computed = true;
  if (cache != nullptr) cache->put(CacheEntry{key, r.hash, r.kind});
  return r;
}

}  // namespace

ExtensionRecord scan_folder(const fs::path& folder, Source source, HashCache* cache, ScanStats* stats) {
  ExtensionRecord r;
  r.folder = folder;
  r.source = source;
  if (!io_allowed()) {
    r.error = "scan refused on the frame thread";
    return r;
  }
  if (stats != nullptr) ++stats->folders;

  const auto content = read_text(folder / "content.xml", 1u * 1024 * 1024);
  if (!content) {
    r.error = "content.xml missing or unreadable";
    return r;
  }
  const auto parsed = parse_content_xml(*content);
  if (!parsed) {
    r.error = "content.xml has no <content id=...>";
    return r;
  }
  r.id = parsed->id;
  r.name = parsed->name;
  r.version = normalize_version(parsed->version);
  r.enabled_default = parsed->enabled;
  r.save_dependent = parsed->save;
  r.dependencies = parsed->dependencies;
  if (source == Source::Install && is_dlc_id(r.id)) r.source = Source::Dlc;

  const auto files = walk(folder);
  Shape& shape = r.shape;
  for (const auto& f : files) {
    const auto slash = f.rel.find('/');
    if (slash == std::string::npos) {
      if (ends_with(f.rel, ".cat")) {
        shape.cat_files = true;
        if (f.rel.starts_with("subst_")) shape.subst_cat = true;
        if (const auto text = read_text(f.abs, 32u * 1024 * 1024)) scan_cat_index(*text, shape);
      } else if (f.rel == "x4native.json") {
        if (const auto text = read_text(f.abs, 1u * 1024 * 1024); text && text->find("\"library\"") != std::string::npos) {
          shape.native_dll = true;
        }
      }
    } else {
      add_top_level_folder(shape, f.rel.substr(0, slash));
    }
    if (ends_with(f.rel, ".dll")) shape.native_dll = true;
  }
  r.has_native_dll = shape.native_dll;
  r.replaces_basegame = shape.subst_cat;
  r.class_hint = classify(r.id, r.save_dependent, shape);

  // Workshop: the folder name is the workshop id.
  if (source == Source::Workshop) {
    const auto name = folder.filename().string();
    if (is_numeric(name) && name.size() < 20) {
      std::from_chars(name.data(), name.data() + name.size(), r.workshop_id);
      if (r.id != std::string(mod_policy::kWorkshopIdPrefix) + name) r.warning = "workshop folder name does not match content.xml id";
    }
  }
  if (r.workshop_id == 0 && r.id.starts_with(mod_policy::kWorkshopIdPrefix)) {
    const auto digits = std::string_view(r.id).substr(mod_policy::kWorkshopIdPrefix.size());
    if (is_numeric(digits) && digits.size() < 20) std::from_chars(digits.data(), digits.data() + digits.size(), r.workshop_id);
  }

  if (!in_list(mod_policy::kHashExcludedIds, r.id)) {
    auto h = content_hash(files, folder, cache);
    r.content_hash = std::move(h.hash);
    r.hash_kind = h.kind;
    if (stats != nullptr) {
      if (h.kind != HashKind::None && h.computed) ++stats->hashed;
      if (h.kind != HashKind::None && !h.computed) ++stats->cache_hits;
    }
  }
  return r;
}

namespace {

std::vector<fs::path> subdirs(const fs::path& dir) {
  std::vector<fs::path> out;
  std::error_code ec;
  fs::directory_iterator it(dir, fs::directory_options::skip_permission_denied, ec);
  const fs::directory_iterator end;
  for (; !ec && it != end; it.increment(ec)) {
    std::error_code e2;
    if (it->is_directory(e2) && !e2) out.push_back(it->path());
  }
  std::sort(out.begin(), out.end());
  return out;
}

void add_root(ScanResult& result, const fs::path& dir, Source source, HashCache* cache) {
  for (const auto& d : subdirs(dir)) result.records.push_back(scan_folder(d, source, cache, &result.stats));
}

int source_rank(Source s) { return s == Source::Dlc ? 0 : s == Source::Install ? 1 : s == Source::User ? 2 : 3; }

}  // namespace

ScanResult scan_extensions(const ExtensionRoots& roots, HashCache* cache) {
  ScanResult result;
  if (!io_allowed()) return result;
  if (!roots.install_extensions.empty()) add_root(result, roots.install_extensions, Source::Install, cache);
  for (const auto& base : roots.user_game_dirs) {
    add_root(result, base / "extensions", Source::User, cache);
    for (const auto& profile : subdirs(base)) add_root(result, profile / "extensions", Source::User, cache);
  }
  for (const auto& w : roots.workshop_roots) add_root(result, w, Source::Workshop, cache);

  // Deterministic order: by id, then source rank, then folder. The first of an id is the winner.
  std::sort(result.records.begin(), result.records.end(), [](const ExtensionRecord& a, const ExtensionRecord& b) {
    if (a.id != b.id) return a.id < b.id;
    if (source_rank(a.source) != source_rank(b.source)) return source_rank(a.source) < source_rank(b.source);
    return a.folder.generic_string() < b.folder.generic_string();
  });
  for (std::size_t i = 1; i < result.records.size(); ++i) {
    auto& r = result.records[i];
    if (!r.id.empty() && r.id == result.records[i - 1].id) {
      if (!r.warning.empty()) r.warning += "; ";
      r.warning += "duplicate extension id, this copy is ignored";
    }
  }
  return result;
}

const ExtensionRecord* find_by_id(const ScanResult& scan, std::string_view id) {
  const auto it = std::lower_bound(scan.records.begin(), scan.records.end(), id,
                                   [](const ExtensionRecord& r, std::string_view v) { return std::string_view(r.id) < v; });
  if (it == scan.records.end() || it->id != id) return nullptr;
  return &*it;
}

// ---- snapshot ----------------------------------------------------------------------------------------------------
namespace {

session::ExtensionReport to_report(const ExtensionRecord& r, bool with_hashes) {
  session::ExtensionReport e;
  e.id = r.id;
  e.name = r.name;
  e.version = r.version;
  e.source = static_cast<std::uint8_t>(r.source);
  e.enabled = r.enabled_default;
  e.egosoft = r.source == Source::Dlc;
  e.workshop_id = r.workshop_id;
  if (with_hashes) {
    e.content_hash = r.content_hash;
    e.hash_kind = static_cast<std::uint8_t>(r.hash_kind);
  }
  e.has_native_dll = r.has_native_dll;
  e.replaces_basegame = r.replaces_basegame;
  e.save_dependent = r.save_dependent;
  e.class_hint = static_cast<std::uint8_t>(r.class_hint);
  e.error = r.error;
  e.warning = r.warning;
  for (const auto& d : r.dependencies) e.dependencies.push_back(session::ExtensionDependencyReport{d.id, d.optional});
  return e;
}

void join_text(std::string& into, const std::string& more) {
  if (more.empty()) return;
  if (!into.empty()) into += "; ";
  into += more;
}

}  // namespace

session::ExtensionSnapshot build_snapshot(const std::vector<ExtensionRecord>& scanned,
                                          const std::vector<ReportedExtension>* reported, bool with_hashes) {
  session::ExtensionSnapshot snap;
  ScanResult view;
  view.records = scanned;  // sorted by the caller (scan_extensions); re-sort defensively for find_by_id
  std::sort(view.records.begin(), view.records.end(), [](const ExtensionRecord& a, const ExtensionRecord& b) {
    if (a.id != b.id) return a.id < b.id;
    if (source_rank(a.source) != source_rank(b.source)) return source_rank(a.source) < source_rank(b.source);
    return a.folder.generic_string() < b.folder.generic_string();
  });

  if (reported != nullptr) {
    std::set<std::string> seen;
    for (const auto& lua : *reported) {
      if (lua.id.empty() || !seen.insert(lua.id).second) continue;
      if (snap.list.size() >= static_cast<std::size_t>(mod_policy::kMaxExtensionEntries)) break;
      const auto* rec = find_by_id(view, lua.id);
      session::ExtensionReport e;
      if (rec != nullptr) {
        e = to_report(*rec, with_hashes);
      } else {
        e.id = lua.id;
        e.source = static_cast<std::uint8_t>(is_dlc_id(lua.id) ? Source::Dlc
                                             : lua.id.starts_with(mod_policy::kWorkshopIdPrefix) ? Source::Workshop
                                                                                                  : Source::Install);
        e.class_hint = static_cast<std::uint8_t>(classify(lua.id, false, Shape{}));
        if (with_hashes) e.warning = "extension folder not found on disk";  // else: scan not finished, say nothing
      }
      if (!lua.name.empty()) e.name = lua.name;
      if (!lua.version.empty()) e.version = normalize_version(lua.version);
      e.enabled = lua.enabled;
      e.egosoft = lua.egosoft || e.egosoft;
      join_text(e.error, lua.error);
      join_text(e.warning, lua.warning);
      snap.list.push_back(std::move(e));
    }
  } else {
    for (const auto& r : view.records) {
      if (r.id.empty()) continue;
      if (!snap.list.empty() && snap.list.back().id == r.id) continue;  // duplicates: winner only
      if (snap.list.size() >= static_cast<std::size_t>(mod_policy::kMaxExtensionEntries)) break;
      snap.list.push_back(to_report(r, with_hashes));
    }
  }

  // Fast-path set hash. Withheld while the scan is not finished: the class hints it needs are not known yet, and an
  // empty hash makes the server fall back to the list.
  if (with_hashes) {
    std::vector<std::string> lines;
    for (const auto& e : snap.list) {
      const auto cls = static_cast<ClassHint>(e.class_hint);
      if (!e.enabled || cls == ClassHint::ClientOnly) continue;
      if (in_list(mod_policy::kClientOnlyLibraryIds, e.id) || in_list(mod_policy::kHashExcludedIds, e.id)) continue;
      lines.push_back(e.id + "@" + e.version);
    }
    std::sort(lines.begin(), lines.end());
    crypto::Sha256Hasher h;
    for (const auto& l : lines) {
      const std::string line = l + "\n";
      h.update(std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(line.data()), line.size()));
    }
    if (const auto d = h.finish()) snap.hash.assign(d->begin(), d->end());
    snap.entries = std::move(lines);
  }
  return snap;
}

// ---- provider ----------------------------------------------------------------------------------------------------
ExtensionProvider::ExtensionProvider(ProviderOptions options) : opt_(std::move(options)) {}

ExtensionProvider::~ExtensionProvider() {
  if (worker_.joinable()) worker_.join();
}

void ExtensionProvider::start() {
  std::lock_guard lock(mutex_);
  if (started_ && !done_) return;  // a scan is running
  if (worker_.joinable()) worker_.join();  // finished: instant
  started_ = true;
  done_ = false;
  const auto gen = ++generation_;
  worker_ = std::thread([this, gen] { run(gen); });
}

void ExtensionProvider::run(std::uint64_t generation) {
  if (opt_.worker_gate) opt_.worker_gate();
  HashCache cache;
  if (!opt_.cache_file.empty()) cache.load(opt_.cache_file);
  ScanResult result = scan_extensions(opt_.roots, &cache);
  if (!opt_.cache_file.empty()) cache.save(opt_.cache_file);
  {
    std::lock_guard lock(mutex_);
    if (generation == generation_) {
      scan_ = std::move(result);
      done_ = true;
    }
  }
  cv_.notify_all();
}

void ExtensionProvider::set_reported(std::vector<ReportedExtension> reported) {
  std::lock_guard lock(mutex_);
  reported_ = std::move(reported);
  has_reported_ = true;
}

session::ExtensionSnapshot ExtensionProvider::snapshot() {
  std::unique_lock lock(mutex_);
  if (started_ && !done_) cv_.wait_for(lock, opt_.wait, [this] { return done_; });
  const auto* rep = has_reported_ ? &reported_ : nullptr;
  if (done_) return build_snapshot(scan_.records, rep, true);
  return build_snapshot({}, rep, false);
}

bool ExtensionProvider::ready() const {
  std::lock_guard lock(mutex_);
  return done_;
}

ScanResult ExtensionProvider::scan_result() const {
  std::lock_guard lock(mutex_);
  return done_ ? scan_ : ScanResult{};
}

}  // namespace x4mp::mods
