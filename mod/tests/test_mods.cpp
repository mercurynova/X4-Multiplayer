// core/mods (M2-03 = M2-X2): content.xml parsing, class hints, id -> folder over install/user/Workshop roots, DLL and
// subst_*.cat detection, content hash (stable, order independent, cached), the frame-thread I/O guard, the extension
// provider (worker thread, 2 s snapshot wait) and the ClientHello that carries the result. Fixtures under
// tests/fixtures/mods/ are hand-written (no game or third-party files).

#include <chrono>
#include <filesystem>
#include <fstream>
#include <future>
#include <random>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/crypto/crypto.h"
#include "core/mods/mods.h"
#include "core/session/session.h"
#include "fake_server.h"
#include "x4mp/mod_policy_constants.h"

using namespace x4mp::mods;
namespace fs = std::filesystem;
using namespace std::chrono_literals;

namespace {

fs::path fixture_root() { return fs::path(X4MP_FIXTURE_DIR) / "mods"; }

ExtensionRoots fixture_roots() {
  ExtensionRoots r;
  r.install_extensions = fixture_root() / "install" / "extensions";
  r.user_game_dirs = {fixture_root() / "user" / "Egosoft" / "X4"};
  r.workshop_roots = {fixture_root() / "workshop" / "content" / "392160"};
  return r;
}

const ExtensionRecord& need(const ScanResult& scan, std::string_view id) {
  const auto* r = find_by_id(scan, id);
  REQUIRE(r != nullptr);
  return *r;
}

struct TempDir {
  fs::path path;
  TempDir() {
    std::random_device rd;
    path = fs::temp_directory_path() / ("x4mp_mods_test_" + std::to_string(rd()) + std::to_string(rd()));
    fs::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    fs::remove_all(path, ec);
  }
  TempDir(const TempDir&) = delete;
  TempDir& operator=(const TempDir&) = delete;
};

void write(const fs::path& file, const std::string& text) {
  fs::create_directories(file.parent_path());
  std::ofstream(file, std::ios::binary | std::ios::trunc) << text;
}

std::string hex(const std::vector<std::uint8_t>& b) { return x4mp::crypto::to_hex(b); }

std::vector<std::uint8_t> sha(const std::string& s) {
  const auto d = x4mp::crypto::sha256(std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(s.data()), s.size()));
  REQUIRE(d.has_value());
  return std::vector<std::uint8_t>(d->begin(), d->end());
}

const x4mp::session::ExtensionReport* report(const x4mp::session::ExtensionSnapshot& s, std::string_view id) {
  for (const auto& e : s.list) {
    if (e.id == id) return &e;
  }
  return nullptr;
}

const std::string kContent = R"(<content id="m" name="M" version="1" enabled="1"/>)";

// Every test that does I/O on the test thread must not leave the frame mark behind.
struct FrameMark {
  FrameMark() { mark_frame_thread(true); }
  ~FrameMark() { mark_frame_thread(false); }
};

}  // namespace

// ---- content.xml -------------------------------------------------------------------------------------------------
TEST_CASE("mods: content.xml parse reads id, name, version, enabled, save and dependencies", "[mods][parse]") {
  const auto c = parse_content_xml(R"(<?xml version="1.0" encoding="utf-8"?>
<!-- <content id="comment"/> must be ignored -->
<content id='abc' name="A &amp; B" version="123" save="1" enabled="0" author="x">
  <dependency id="x4native" version="900"/>
  <dependency id="opt_lib" optional="true"/>
  <dependency id="flag" optional='0'></dependency>
  <text language="44" name="x"/>
</content>)");
  REQUIRE(c.has_value());
  CHECK(c->id == "abc");
  CHECK(c->name == "A & B");
  CHECK(c->version == "123");
  CHECK(c->save);
  CHECK_FALSE(c->enabled);
  REQUIRE(c->dependencies.size() == 3);
  CHECK(c->dependencies[0].id == "x4native");
  CHECK_FALSE(c->dependencies[0].optional);
  CHECK(c->dependencies[1].optional);
  CHECK_FALSE(c->dependencies[2].optional);

  const auto d = parse_content_xml(R"(<content id="min"/>)");
  REQUIRE(d.has_value());
  CHECK(d->enabled);  // default
  CHECK_FALSE(d->save);
  CHECK(d->version.empty());
}

TEST_CASE("mods: content.xml without an id, or garbage, is rejected", "[mods][parse]") {
  CHECK_FALSE(parse_content_xml(R"(<content name="x" version="1">)").has_value());
  CHECK_FALSE(parse_content_xml("").has_value());
  CHECK_FALSE(parse_content_xml("not xml at all <<<").has_value());
  CHECK_FALSE(parse_content_xml(R"(<content id=unquoted/>)").has_value());
  CHECK_FALSE(parse_content_xml(R"(<other id="x"/>)").has_value());
}

// ---- class hint --------------------------------------------------------------------------------------------------
TEST_CASE("mods: class hint order is DLC prefix, allowlist, then the folder heuristic", "[mods][class]") {
  Shape none;
  Shape ui;
  ui.ui_content = true;
  Shape sim;
  sim.sim_content = true;
  Shape native;
  native.native_dll = true;
  Shape odd;
  odd.ui_content = true;
  odd.unknown_content = true;

  CHECK(classify("ego_dlc_anything", false, sim) == ClassHint::Dlc);
  CHECK(classify("ws_2042901274", false, sim) == ClassHint::ClientOnly);  // allowlist beats the md/ heuristic
  CHECK(classify("kuerteeUIExtensionsAndHUD", true, sim) == ClassHint::ClientOnly);
  CHECK(classify("m", false, ui) == ClassHint::ClientOnly);
  CHECK(classify("m", false, sim) == ClassHint::Sim);
  CHECK(classify("m", false, native) == ClassHint::Sim);
  CHECK(classify("m", true, ui) == ClassHint::Sim);  // save="1"
  CHECK(classify("m", false, none) == ClassHint::Unknown);
  CHECK(classify("m", false, odd) == ClassHint::Unknown);

  CHECK(top_level_folder("md/x/y.xml") == "md");
  CHECK(top_level_folder("/UI\\a.lua") == "ui");
  CHECK(top_level_folder("root.txt").empty());
  Shape s;
  add_top_level_folder(s, "libraries");
  add_top_level_folder(s, "t");
  add_top_level_folder(s, "native");  // neutral
  CHECK(s.sim_content);
  CHECK(s.ui_content);
  CHECK_FALSE(s.unknown_content);
}

// ---- scan over the fixture roots --------------------------------------------------------------------------------
TEST_CASE("mods: scan resolves ids over install, user and Workshop roots (cat mod, loose mod, Workshop id, DLL mod, missing folder)",
          "[mods][scan]") {
  const auto scan = scan_extensions(fixture_roots(), nullptr);

  SECTION("DLC: install root, ego_dlc_ prefix, cat index hash") {
    const auto& r = need(scan, "ego_dlc_fixture");
    CHECK(r.source == Source::Dlc);
    CHECK(r.class_hint == ClassHint::Dlc);
    CHECK(r.hash_kind == HashKind::CatIndex);
    CHECK(r.content_hash.size() == 32);
    CHECK(r.save_dependent);
    CHECK_FALSE(r.has_native_dll);
    CHECK(r.folder.filename() == "ego_dlc_fixture");
  }
  SECTION("cat mod with sim content and dependencies") {
    const auto& r = need(scan, "cat_sim");
    CHECK(r.source == Source::Install);
    CHECK(r.name == "Cat Sim Mod & Friends");
    CHECK(r.version == "102");
    CHECK(r.class_hint == ClassHint::Sim);
    CHECK(r.hash_kind == HashKind::CatIndex);
    CHECK(r.shape.cat_files);
    CHECK_FALSE(r.replaces_basegame);
    REQUIRE(r.dependencies.size() == 2);
    CHECK(r.dependencies[1].optional);
  }
  SECTION("loose UI mod hashes its files and is client-only") {
    const auto& r = need(scan, "loose_ui");
    CHECK(r.class_hint == ClassHint::ClientOnly);
    CHECK(r.hash_kind == HashKind::Files);
    CHECK(r.content_hash.size() == 32);
    CHECK_FALSE(r.shape.cat_files);
  }
  SECTION("native DLL mod: x4native.json library and *.dll") {
    const auto& r = need(scan, "dll_mod");
    CHECK(r.has_native_dll);
    CHECK(r.class_hint == ClassHint::Sim);
  }
  SECTION("x4mp / x4native ids are not content-hashed") {
    const auto& r = need(scan, "x4mp");
    CHECK(r.hash_kind == HashKind::None);
    CHECK(r.content_hash.empty());
  }
  SECTION("user roots: <profile>\\extensions and the direct extensions folder; subst_*.cat sets replaces_basegame") {
    const auto& s = need(scan, "subst_ui");
    CHECK(s.source == Source::User);
    CHECK(s.replaces_basegame);
    CHECK(s.class_hint == ClassHint::ClientOnly);  // UI-only substitution
    CHECK(s.hash_kind == HashKind::CatIndex);
    const auto& sv = need(scan, "saved_mod");
    CHECK(sv.source == Source::User);
    CHECK(sv.save_dependent);
    CHECK(sv.class_hint == ClassHint::Sim);  // save="1" wins over a text-only folder
    CHECK(need(scan, "direct_user").source == Source::User);
  }
  SECTION("Workshop: folder name is the workshop id; allowlisted id is client-only whatever it contains") {
    const auto& a = need(scan, "ws_2042901274");
    CHECK(a.source == Source::Workshop);
    CHECK(a.workshop_id == 2042901274ull);
    CHECK(a.class_hint == ClassHint::ClientOnly);
    CHECK(a.shape.sim_content);  // it ships md/, but the allowlist overrides
    CHECK(a.warning.empty());
    const auto& b = need(scan, "ws_999000111");
    CHECK(b.workshop_id == 999000111ull);
    CHECK(b.class_hint == ClassHint::Sim);
    CHECK(b.hash_kind == HashKind::Files);
  }
  SECTION("disabled by default") { CHECK_FALSE(need(scan, "disabled_sim").enabled_default); }
  SECTION("missing / broken content.xml are reported without a path in the message") {
    int errors = 0;
    for (const auto& r : scan.records) {
      if (r.id.empty()) {
        ++errors;
        CHECK_FALSE(r.error.empty());
        CHECK(r.error.find(':') == std::string::npos);
        CHECK(r.error.find('\\') == std::string::npos);
      }
    }
    CHECK(errors == 2);  // no_content, bad_content
  }
  SECTION("records are sorted by id") {
    for (std::size_t i = 1; i < scan.records.size(); ++i) CHECK(scan.records[i - 1].id <= scan.records[i].id);
    CHECK(find_by_id(scan, "does_not_exist") == nullptr);
  }
  SECTION("nonexistent roots are skipped") {
    ExtensionRoots r;
    r.install_extensions = fixture_root() / "nope";
    r.user_game_dirs = {fixture_root() / "nope2"};
    r.workshop_roots = {fixture_root() / "nope3"};
    CHECK(scan_extensions(r, nullptr).records.empty());
  }
}

TEST_CASE("mods: a duplicate id across roots keeps the install copy and warns on the other", "[mods][scan]") {
  TempDir t;
  write(t.path / "install" / "a" / "content.xml", R"(<content id="dup" name="One" version="1"/>)");
  write(t.path / "ws" / "42" / "content.xml", R"(<content id="dup" name="Two" version="2"/>)");
  ExtensionRoots r;
  r.install_extensions = t.path / "install";
  r.workshop_roots = {t.path / "ws"};
  const auto scan = scan_extensions(r, nullptr);
  REQUIRE(scan.records.size() == 2);
  CHECK(need(scan, "dup").source == Source::Install);
  CHECK(scan.records[0].warning.empty());
  CHECK_FALSE(scan.records[1].warning.empty());
  CHECK(scan.records[1].warning.find("42") == std::string::npos);  // no path fragments
  const auto snap = build_snapshot(scan.records, nullptr, true);
  CHECK(snap.list.size() == 1);
}

// ---- hash --------------------------------------------------------------------------------------------------------
TEST_CASE("mods: loose-file hash follows the documented canonical form (known answer)", "[mods][hash]") {
  TempDir t;
  write(t.path / "m" / "content.xml", kContent);
  write(t.path / "m" / "libraries" / "wares.xml", "abc");
  const auto rec = scan_folder(t.path / "m", Source::Install, nullptr);
  REQUIRE(rec.hash_kind == HashKind::Files);
  // sorted by relative path: "content.xml" < "libraries/wares.xml"
  const std::string expected = "content.xml\n" + std::to_string(kContent.size()) + "\n" + hex(sha(kContent)) + "\n" +
                               "libraries/wares.xml\n3\n" + hex(sha("abc")) + "\n";
  CHECK(hex(rec.content_hash) == hex(sha(expected)));
}

TEST_CASE("mods: cat-index hash covers the .cat files only, not the .dat payload", "[mods][hash]") {
  TempDir t;
  write(t.path / "m" / "content.xml", kContent);
  write(t.path / "m" / "ext_01.cat", "md/a.xml 3 1 00\n");
  write(t.path / "m" / "ext_01.dat", "AAA");
  const auto a = scan_folder(t.path / "m", Source::Install, nullptr);
  REQUIRE(a.hash_kind == HashKind::CatIndex);
  write(t.path / "m" / "ext_01.dat", "BBB");  // payload changed, index not: documented behaviour of CatIndex
  CHECK(scan_folder(t.path / "m", Source::Install, nullptr).content_hash == a.content_hash);
  write(t.path / "m" / "ext_01.cat", "md/a.xml 3 2 00\n");
  CHECK(scan_folder(t.path / "m", Source::Install, nullptr).content_hash != a.content_hash);
}

TEST_CASE("mods: hash is stable across runs and independent of file creation / enumeration order", "[mods][hash]") {
  TempDir t;
  const std::vector<std::pair<std::string, std::string>> files{
      {"content.xml", kContent}, {"md/z.xml", "z"}, {"md/a.xml", "a"}, {"libraries/b.xml", "bb"}, {"t/0001-l044.xml", "tt"}, {"A.txt", "A"}};
  auto forward = files;
  auto backward = files;
  std::reverse(backward.begin(), backward.end());
  for (const auto& [n, c] : forward) write(t.path / "one" / n, c);
  for (const auto& [n, c] : backward) write(t.path / "two" / n, c);  // other creation order, other folder name

  const auto h1 = scan_folder(t.path / "one", Source::Install, nullptr);
  const auto h1b = scan_folder(t.path / "one", Source::Install, nullptr);
  const auto h2 = scan_folder(t.path / "two", Source::Install, nullptr);
  REQUIRE(h1.hash_kind == HashKind::Files);
  CHECK(h1.content_hash == h1b.content_hash);  // across runs
  CHECK(h1.content_hash == h2.content_hash);   // independent of order and of the folder location

  write(t.path / "one" / "md" / "a.xml", "changed");
  CHECK(scan_folder(t.path / "one", Source::Install, nullptr).content_hash != h1.content_hash);

  // The whole fixture scan, twice: identical per-extension hashes.
  const auto s1 = scan_extensions(fixture_roots(), nullptr);
  const auto s2 = scan_extensions(fixture_roots(), nullptr);
  REQUIRE(s1.records.size() == s2.records.size());
  for (std::size_t i = 0; i < s1.records.size(); ++i) {
    CHECK(s1.records[i].id == s2.records[i].id);
    CHECK(s1.records[i].content_hash == s2.records[i].content_hash);
  }
}

TEST_CASE("mods: a mod above the file cap gets no hash", "[mods][hash]") {
  TempDir t;
  write(t.path / "big" / "content.xml", kContent);
  for (std::size_t i = 0; i <= kHashFileCap; ++i) write(t.path / "big" / "f" / (std::to_string(i) + ".txt"), "x");
  const auto r = scan_folder(t.path / "big", Source::Install, nullptr);
  CHECK(r.hash_kind == HashKind::None);
  CHECK(r.content_hash.empty());
  CHECK(r.id == "m");  // everything else is still read
}

TEST_CASE("mods: hash cache is keyed by (path, size, mtime), survives a restart, and invalidates on change", "[mods][hash][cache]") {
  TempDir t;
  write(t.path / "ext" / "m" / "content.xml", kContent);
  write(t.path / "ext" / "m" / "ui" / "a.lua", "x");
  ExtensionRoots roots;
  roots.install_extensions = t.path / "ext";
  const auto cache_file = t.path / "cache" / "ext-hash-cache.json";

  HashCache c1;
  c1.load(cache_file);  // missing file: empty
  CHECK(c1.size() == 0);
  const auto first = scan_extensions(roots, &c1);
  CHECK(first.stats.hashed == 1);
  CHECK(first.stats.cache_hits == 0);
  c1.save(cache_file);
  REQUIRE(fs::exists(cache_file));

  HashCache c2;  // "next run"
  c2.load(cache_file);
  CHECK(c2.size() == 1);
  const auto second = scan_extensions(roots, &c2);
  CHECK(second.stats.hashed == 0);
  CHECK(second.stats.cache_hits == 1);
  CHECK(second.records[0].content_hash == first.records[0].content_hash);
  CHECK(second.records[0].hash_kind == HashKind::Files);

  write(t.path / "ext" / "m" / "ui" / "a.lua", "longer content");  // size (and mtime) change
  const auto third = scan_extensions(roots, &c2);
  CHECK(third.stats.hashed == 1);
  CHECK(third.records[0].content_hash != first.records[0].content_hash);

  write(cache_file, "{ this is not json");  // corrupt cache = empty cache, never a failure
  HashCache c3;
  c3.load(cache_file);
  CHECK(c3.size() == 0);
  write(cache_file, R"({"version":1,"entries":[{"folder":"x","size":1,"mtime":1,"count":1,"hash":"zz","kind":1},7]})");
  c3.load(cache_file);
  CHECK(c3.size() == 0);
}

// ---- frame-thread I/O guard -------------------------------------------------------------------------------------
TEST_CASE("mods: file I/O is refused on the frame thread (and counted)", "[mods][threads]") {
  reset_frame_thread_io_violations();
  {
    FrameMark frame;
    CHECK(is_frame_thread());
    CHECK(scan_extensions(fixture_roots(), nullptr).records.empty());
    CHECK(scan_folder(fixture_root() / "install" / "extensions" / "cat_sim", Source::Install, nullptr).id.empty());
    HashCache c;
    c.load(fixture_root() / "anything.json");
  }
  CHECK(frame_thread_io_violations() >= 3);
  CHECK_FALSE(is_frame_thread());
  reset_frame_thread_io_violations();
  CHECK_FALSE(scan_extensions(fixture_roots(), nullptr).records.empty());  // fine on any other thread
  CHECK(frame_thread_io_violations() == 0);
}

// ---- provider ----------------------------------------------------------------------------------------------------
namespace {

std::vector<ReportedExtension> lua_list() {
  return {
      {"ego_dlc_fixture", "Fixture DLC", "9.00", true, true, "", ""},
      {"cat_sim", "Cat Sim", "1.02", true, false, "", ""},
      {"loose_ui", "Loose UI", "1.00", true, false, "", ""},
      {"dll_mod", "Native", "9.00", true, false, "", ""},
      {"x4mp", "X4MP", "0.1.0", true, false, "", ""},
      {"disabled_sim", "Disabled", "0.05", false, false, "", ""},
      {"ws_2042901274", "Lib", "1.50", true, false, "", ""},
      {"ws_999000111", "Workshop Sim", "4.10", true, false, "", ""},
      {"gone_mod", "Gone", "1", true, false, "", "missing dependency"},
  };
}

}  // namespace

TEST_CASE("mods: provider enriches the Lua list; the set hash covers enabled Dlc + Sim only", "[mods][provider]") {
  reset_frame_thread_io_violations();
  FrameMark frame;  // this thread plays the frame thread: any I/O here would be counted and fail below
  ProviderOptions o;
  o.roots = fixture_roots();
  ExtensionProvider p(o);
  p.set_reported(lua_list());
  p.start();
  const auto snap = p.snapshot();
  REQUIRE(p.ready());
  CHECK(frame_thread_io_violations() == 0);

  CHECK(snap.list.size() == 9);
  const auto* dlc = report(snap, "ego_dlc_fixture");
  REQUIRE(dlc != nullptr);
  CHECK(dlc->egosoft);
  CHECK(dlc->version == "9.00");  // Lua wins for what the player sees
  CHECK(dlc->class_hint == static_cast<std::uint8_t>(ClassHint::Dlc));
  CHECK(dlc->hash_kind == static_cast<std::uint8_t>(HashKind::CatIndex));
  CHECK(dlc->content_hash.size() == 32);
  const auto* dis = report(snap, "disabled_sim");
  REQUIRE(dis != nullptr);
  CHECK_FALSE(dis->enabled);
  const auto* dll = report(snap, "dll_mod");
  REQUIRE(dll != nullptr);
  CHECK(dll->has_native_dll);
  const auto* gone = report(snap, "gone_mod");
  REQUIRE(gone != nullptr);
  CHECK(gone->content_hash.empty());
  CHECK(gone->hash_kind == 0);
  CHECK(gone->warning.find("missing dependency") != std::string::npos);
  CHECK(gone->warning.find("not found") != std::string::npos);
  CHECK(report(snap, "cat_sim")->dependencies.size() == 2);

  // Expected lines: DLC + the enabled Sim ones. Not: loose_ui (ClientOnly), ws_2042901274 (allowlist), x4mp
  // (hashExcluded), disabled_sim (disabled). gone_mod is Unknown (= Sim) and enabled, so it counts.
  const std::vector<std::string> expected{"cat_sim@1.02", "dll_mod@9.00", "ego_dlc_fixture@9.00", "gone_mod@1", "ws_999000111@4.10"};
  CHECK(snap.entries == expected);
  std::string joined;
  for (const auto& l : expected) joined += l + "\n";
  CHECK(hex(snap.hash) == hex(sha(joined)));
}

TEST_CASE("mods: scan-only snapshot (no Lua list) uses content.xml defaults", "[mods][provider]") {
  ProviderOptions o;
  o.roots = fixture_roots();
  ExtensionProvider p(o);
  p.start();
  const auto snap = p.snapshot();
  REQUIRE(p.ready());
  CHECK(report(snap, "disabled_sim") != nullptr);
  CHECK_FALSE(report(snap, "disabled_sim")->enabled);
  CHECK(report(snap, "bad_content") == nullptr);  // unusable folders have no id and are not reported
  for (const auto& e : snap.entries) CHECK(e.find("x4mp@") == std::string::npos);
  CHECK(snap.hash.size() == 32);
}

TEST_CASE("mods: the set hash does not depend on the order of the Lua list", "[mods][provider]") {
  ProviderOptions o;
  o.roots = fixture_roots();
  ExtensionProvider p(o);
  p.start();
  auto fwd = lua_list();
  p.set_reported(fwd);
  const auto a = p.snapshot();
  std::reverse(fwd.begin(), fwd.end());
  p.set_reported(fwd);
  const auto b = p.snapshot();
  CHECK(a.hash == b.hash);
  CHECK(a.entries == b.entries);
}

TEST_CASE("mods: snapshot waits at most the configured time, then goes out without hashes; later snapshots have them", "[mods][provider][timeout]") {
  CHECK(kSnapshotWait == 2000ms);  // the documented bound (mod-management 8)

  std::promise<void> gate;
  auto released = gate.get_future().share();
  ProviderOptions o;
  o.roots = fixture_roots();
  o.wait = 60ms;
  o.worker_gate = [released] { released.wait(); };
  ExtensionProvider p(o);

  // Not started: returns at once, empty.
  const auto idle = p.snapshot();
  CHECK(idle.list.empty());
  CHECK(idle.hash.empty());

  p.set_reported(lua_list());
  p.start();
  const auto t0 = std::chrono::steady_clock::now();
  const auto snap = p.snapshot();  // the worker is held at the gate: must time out
  const auto waited = std::chrono::steady_clock::now() - t0;
  CHECK(waited >= 50ms);
  CHECK(waited < 1500ms);
  CHECK_FALSE(p.ready());
  CHECK(snap.hash.empty());  // not computed: the server falls back to the list
  CHECK(snap.entries.empty());
  REQUIRE(snap.list.size() == 9);  // the Lua list still goes out
  for (const auto& e : snap.list) {
    CHECK(e.content_hash.empty());
    CHECK(e.hash_kind == 0);
  }
  CHECK(report(snap, "gone_mod")->warning == "missing dependency");  // no false "not found" before the scan finished

  gate.set_value();
  const auto full = p.snapshot();  // done now (or about to be): waits for the worker
  CHECK(p.ready());
  CHECK(full.hash.size() == 32);
  CHECK(report(full, "cat_sim")->content_hash.size() == 32);
}

TEST_CASE("mods: the worker saves the on-disk cache and the next provider reuses it", "[mods][provider][cache]") {
  TempDir t;
  ProviderOptions o;
  o.roots = fixture_roots();
  o.cache_file = t.path / "x4mp" / "ext-hash-cache.json";
  {
    ExtensionProvider p(o);
    p.start();
    (void)p.snapshot();
    CHECK(p.scan_result().stats.hashed > 0);
    CHECK(p.scan_result().stats.cache_hits == 0);
  }
  REQUIRE(fs::exists(o.cache_file));
  ExtensionProvider p2(o);
  p2.start();
  (void)p2.snapshot();
  CHECK(p2.scan_result().stats.hashed == 0);
  CHECK(p2.scan_result().stats.cache_hits > 0);
}

TEST_CASE("mods: start() again after a finished scan rescans (reloadui)", "[mods][provider]") {
  TempDir t;
  write(t.path / "ext" / "a" / "content.xml", R"(<content id="a" version="1"/>)");
  write(t.path / "ext" / "a" / "md" / "x.xml", "1");
  ProviderOptions o;
  o.roots.install_extensions = t.path / "ext";
  ExtensionProvider p(o);
  p.start();
  const auto a = p.snapshot();
  write(t.path / "ext" / "b" / "content.xml", R"(<content id="b" version="1"/>)");
  write(t.path / "ext" / "b" / "md" / "x.xml", "1");
  p.start();
  const auto b = p.snapshot();
  CHECK(a.list.size() == 1);
  CHECK(b.list.size() == 2);
}

// ---- ClientHello -------------------------------------------------------------------------------------------------
namespace {

x4mp::session::SessionOptions hello_options(const fake::Listener& server) {
  x4mp::session::SessionOptions o;
  o.endpoint = server.endpoint();
  o.player_name = "Tester";
  for (std::size_t i = 0; i < o.player_key.size(); ++i) o.player_key[i] = static_cast<std::uint8_t>(0xA0 + i);
  return o;
}

}  // namespace

TEST_CASE("mods: ClientHello carries extension_list, extensions_hash and entries from the provider", "[mods][session][handshake]") {
  fake::Listener server;
  ProviderOptions po;
  po.roots = fixture_roots();
  ExtensionProvider provider(po);
  provider.set_reported(lua_list());
  provider.start();

  auto opt = hello_options(server);
  opt.extensions = &provider;
  x4mp::session::Session s(opt);
  REQUIRE(s.start());
  auto conn = server.accept();
  REQUIRE(conn.valid());
  const auto hello = fake::do_server_hello(conn, false);
  REQUIRE(hello.has_value());
  const auto* h = hello->get();
  CHECK(h->extensions_hash()->size() == 32);
  CHECK(h->extensions()->size() == 5);
  REQUIRE(h->extension_list() != nullptr);
  REQUIRE(h->extension_list()->size() == 9);
  const X4MP::Proto::ExtensionInfo* cat = nullptr;
  const X4MP::Proto::ExtensionInfo* ws = nullptr;
  for (const auto* e : *h->extension_list()) {
    if (e->id()->str() == "cat_sim") cat = e;
    if (e->id()->str() == "ws_999000111") ws = e;
  }
  REQUIRE(cat != nullptr);
  CHECK(cat->name()->str() == "Cat Sim");
  CHECK(cat->version()->str() == "1.02");
  CHECK(cat->source() == X4MP::Proto::ExtensionSource::Install);
  CHECK(cat->enabled());
  CHECK(cat->hash_kind() == X4MP::Proto::HashKind::CatIndex);
  CHECK(cat->content_hash()->size() == 32);
  CHECK(cat->class_hint() == X4MP::Proto::ExtensionClass::Sim);
  REQUIRE(cat->dependencies()->size() == 2);
  CHECK(cat->dependencies()->Get(1)->id()->str() == "some_optional_lib");
  CHECK(cat->dependencies()->Get(1)->optional());
  REQUIRE(ws != nullptr);
  CHECK(ws->source() == X4MP::Proto::ExtensionSource::Workshop);
  CHECK(ws->workshop_id() == 999000111ull);
  s.stop();
}

TEST_CASE("mods: ClientHello is sent without hashes when enrichment is not finished within the wait", "[mods][session][handshake][timeout]") {
  fake::Listener server;
  std::promise<void> gate;
  auto released = gate.get_future().share();
  ProviderOptions po;
  po.roots = fixture_roots();
  po.wait = 80ms;
  po.worker_gate = [released] { released.wait(); };
  ExtensionProvider provider(po);
  provider.set_reported(lua_list());
  provider.start();

  auto opt = hello_options(server);
  opt.extensions = &provider;
  x4mp::session::Session s(opt);
  const auto t0 = std::chrono::steady_clock::now();
  REQUIRE(s.start());  // snapshot() runs here and times out
  CHECK(std::chrono::steady_clock::now() - t0 < 1500ms);
  auto conn = server.accept();
  REQUIRE(conn.valid());
  const auto hello = fake::do_server_hello(conn, false);
  REQUIRE(hello.has_value());
  const auto* h = hello->get();
  CHECK(h->extensions_hash()->size() == 0);
  REQUIRE(h->extension_list()->size() == 9);
  for (const auto* e : *h->extension_list()) {
    CHECK(e->content_hash()->size() == 0);
    CHECK(e->hash_kind() == X4MP::Proto::HashKind::None);
  }
  s.stop();
  gate.set_value();
}
