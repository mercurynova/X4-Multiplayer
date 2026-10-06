#include "features/diag/knowledge_feature.h"

#include <charconv>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/diag/diag_hub.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
constexpr std::size_t kInboxCap = 32;
constexpr std::size_t kMaxTextBytes = 2048;
constexpr std::size_t kMaxPending = 64;

std::vector<std::string_view> split(std::string_view s, char sep) {
  std::vector<std::string_view> out;
  std::size_t pos = 0;
  while (true) {
    const auto i = s.find(sep, pos);
    if (i == std::string_view::npos) {
      out.push_back(s.substr(pos));
      break;
    }
    out.push_back(s.substr(pos, i - pos));
    pos = i + 1;
  }
  return out;
}

bool to_u32(std::string_view s, std::uint32_t& out) {
  if (s.empty()) return false;
  const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
  return r.ec == std::errc{} && r.ptr == s.data() + s.size();
}

bool to_double(std::string_view s, double& out) {
  if (s.empty()) return false;
  const std::string tmp(s);
  char* end = nullptr;
  out = std::strtod(tmp.c_str(), &end);
  return end != tmp.c_str() && *end == '\0';
}
}  // namespace

std::optional<KnowledgeCounts> parse_knowledge(std::string_view data) {
  const auto f = split(data, ';');
  if (f.size() != 12 || f[0] != "K") return std::nullopt;
  KnowledgeCounts c;
  if (!to_u32(f[1], c.seq) || !to_double(f[2], c.age_s)) return std::nullopt;
  if (!to_u32(f[3], c.sectors_known) || !to_u32(f[4], c.sectors) || !to_u32(f[5], c.clusters_known) || !to_u32(f[6], c.clusters) ||
      !to_u32(f[7], c.stations_known) || !to_u32(f[8], c.stations) || !to_u32(f[9], c.gates_known) || !to_u32(f[10], c.gates)) {
    return std::nullopt;
  }
  if (!f[11].empty()) {
    for (const auto item : split(f[11], ',')) {
      const auto eq = item.find('=');
      if (eq == std::string_view::npos || eq == 0 || eq + 2 != item.size()) return std::nullopt;
      const char v = item[eq + 1];
      if (v != '0' && v != '1' && v != '-') return std::nullopt;
      c.samples.emplace_back(std::string(item.substr(0, eq)), v);
    }
  }
  return c;
}

std::string format_knowledge_line(const KnowledgeCounts& c, std::string_view tag) {
  std::string samples;
  for (const auto& [name, v] : c.samples) {
    if (!samples.empty()) samples += ',';
    samples += name;
    samples += '=';
    samples += v;
  }
  char age[32];
  std::snprintf(age, sizeof age, "%.1f", c.age_s);
  std::string out = "knowledge: sectors_known=" + std::to_string(c.sectors_known) + "/" + std::to_string(c.sectors) +
                    " stations_known=" + std::to_string(c.stations_known) + "/" + std::to_string(c.stations) +
                    " gates_known=" + std::to_string(c.gates_known) + "/" + std::to_string(c.gates) +
                    " clusters_known=" + std::to_string(c.clusters_known) + "/" + std::to_string(c.clusters) + " samples=[" + samples + "] at='" +
                    std::string(tag) + "' game_age=" + age + "s";
  return out;
}

// ---- M3-28 watch ---------------------------------------------------------------------------------------------------------------
std::optional<WatchTick> parse_watch(std::string_view data) {
  const auto parts = split(data, '|');
  const auto h = split(parts[0], ';');
  if (h.size() != 7 || h[0] != "W") return std::nullopt;
  WatchTick t;
  if (!to_u32(h[1], t.seq) || !to_double(h[2], t.age_s) || !to_u32(h[6], t.total)) return std::nullopt;
  t.player_sector = std::string(h[3]);
  t.player_ship = std::string(h[4]);
  t.undiscovered = std::string(h[5]);
  for (std::size_t i = 1; i < parts.size(); ++i) {
    if (parts[i].empty()) continue;
    const auto f = split(parts[i], ',');
    if (f.size() != 9) return std::nullopt;
    WatchShip s;
    s.idcode = std::string(f[0]);
    s.owner = std::string(f[1]);
    s.relation = std::string(f[2]);
    s.sector = std::string(f[8]);
    char* flags[] = {&s.radar, &s.live, &s.gravidar, &s.active, &s.known};
    for (int k = 0; k < 5; ++k) {
      const auto v = f[static_cast<std::size_t>(3 + k)];
      if (v.size() != 1 || (v[0] != '0' && v[0] != '1' && v[0] != '-')) return std::nullopt;
      *flags[k] = v[0];
    }
    t.ships.push_back(std::move(s));
  }
  return t;
}

std::vector<std::string> format_watch_lines(const WatchTick& t, std::map<std::string, std::string>& last, bool full) {
  std::vector<std::string> out;
  char age[32];
  std::snprintf(age, sizeof age, "%.1f", t.age_s);
  out.push_back(std::string("knowledge-watch: age=") + age + "s psector=" + t.player_sector + " pship=" + t.player_ship + " undisc2km=" + t.undiscovered +
                " ships=" + std::to_string(t.ships.size()) + "/" + std::to_string(t.total));
  std::map<std::string, std::string> seen;
  for (const auto& s : t.ships) {
    std::string owner = s.owner;
    if (owner.rfind("x4mp_team_", 0) == 0) owner = "t" + owner.substr(10);
    const std::string state = std::string(s.idcode) + " own=" + owner + " rel=" + s.relation + " radar=" + s.radar + " live=" + s.live + " gravidar=" + s.gravidar +
                              " active=" + s.active + " known=" + s.known + " sec=" + s.sector;
    seen[s.idcode] = state;
    const auto it = last.find(s.idcode);
    if (full || it == last.end() || it->second != state) out.push_back("knowledge-watch:   " + state);
  }
  last = std::move(seen);  // a ship that left the list is logged again when it comes back
  return out;
}

void WatchSchedule::set(bool on, double now_s) {
  on_ = on;
  outstanding_ = false;
  started_ = now_s;
  next_ = now_s;
}

bool WatchSchedule::due(double now_s) {
  if (!on_) return false;
  if (now_s - started_ >= kWatchAutoOffS) {
    on_ = false;
    return false;
  }
  if (outstanding_ && now_s - asked_at_ < kWatchAnswerTimeoutS) return false;
  return now_s >= next_;
}

// ---- the hub ---------------------------------------------------------------------------------------------------------------------
void KnowledgeHub::request(const std::string& tag) {
  Sender s;
  {
    const std::lock_guard lock(m_);
    ++requested_;
    s = sender_;
    if (!s) {
      if (queued_.size() < 32) queued_.push_back(tag);
      return;
    }
  }
  s(tag);
}

void KnowledgeHub::clear_sender(const void* owner) {
  const std::lock_guard lock(m_);
  if (owner_ == owner) {
    sender_ = {};
    owner_ = nullptr;
  }
}

void KnowledgeHub::set_sender(const void* owner, Sender s) {
  const std::lock_guard lock(m_);
  sender_ = std::move(s);
  owner_ = owner;
}

std::vector<std::string> KnowledgeHub::take_queued() {
  const std::lock_guard lock(m_);
  return std::exchange(queued_, {});
}

std::uint64_t KnowledgeHub::requested() const {
  const std::lock_guard lock(m_);
  return requested_;
}

void KnowledgeHub::reset() {
  const std::lock_guard lock(m_);
  sender_ = {};
  owner_ = nullptr;
  queued_.clear();
  requested_ = 0;
}

KnowledgeHub& knowledge_hub() noexcept {
  static KnowledgeHub hub;
  return hub;
}

// ---- the feature -------------------------------------------------------------------------------------------------------------------
struct KnowledgeFeature::Inbox {
  std::mutex m;
  std::vector<std::pair<std::string, std::string>> q;  // verb, text
  void push(const std::string& verb, std::string_view text) {
    if (text.size() > kMaxTextBytes) return;
    const std::lock_guard lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(verb, std::string(text));
  }
  std::vector<std::pair<std::string, std::string>> take() {
    const std::lock_guard lock(m);
    return std::exchange(q, {});
  }
};

KnowledgeFeature::KnowledgeFeature() : inbox_(std::make_shared<Inbox>()) {}
KnowledgeFeature::~KnowledgeFeature() { knowledge_hub().clear_sender(this); }

void KnowledgeFeature::on_init(host::HostContext& ctx) {
  ctx_ = &ctx;
  for (const char* verb : {"knowledge_md", "knowledge_cmd"}) {
    auto inbox = inbox_;
    const std::string v = verb;
    (void)ctx.platform.subscribe_event(("x4mp." + v).c_str(), [inbox, v](std::string_view text) { inbox->push(v, text); });
  }
  knowledge_hub().set_sender(this, [this](const std::string& tag) {
    if (ctx_) ask(*ctx_, tag);
  });
}

void KnowledgeFeature::on_shutdown(host::HostContext&) {
  knowledge_hub().clear_sender(this);
  ctx_ = nullptr;
}

void KnowledgeFeature::ask(host::HostContext& ctx, const std::string& tag) {
  const std::uint32_t seq = ++seq_;
  if (pending_.size() >= kMaxPending) pending_.erase(pending_.begin());
  pending_[seq] = tag;
  nlohmann::json j;
  j["v"] = 1;
  j["seq"] = seq;
  if (!ctx.platform.raise_lua("x4mp.knowledge_ask", j.dump())) {
    pending_.erase(seq);
    ctx.log.raw(Cat::Md, Level::Warn, "knowledge: probe '" + tag + "' not sent (the Lua bridge is not available)");
  }
}

void KnowledgeFeature::ask_watch(host::HostContext& ctx) {
  nlohmann::json j;
  j["v"] = 1;
  j["seq"] = ++watch_seq_;
  j["max"] = kWatchMaxShips;
  watch_.asked(clock_s_);
  if (!ctx.platform.raise_lua("x4mp.knowledge_watch", j.dump())) {
    watch_.answered();
    ctx.log.raw(Cat::Md, Level::Warn, "knowledge-watch: probe not sent (the Lua bridge is not available)");
  }
}

void KnowledgeFeature::handle_command(host::HostContext& ctx, const std::string& text) {
  const auto j = nlohmann::json::parse(text, nullptr, false);
  std::string watch;
  if (j.is_object() && j.contains("watch") && j["watch"].is_string()) watch = j["watch"].get<std::string>();
  if (watch.empty()) {
    ask(ctx, "chat command /x4mp knowledge");
    return;
  }
  const bool want = watch == "off" ? false : (watch == "on" ? true : !watch_.on());
  watch_.set(want, clock_s_);
  watch_last_.clear();
  last_full_s_ = -1e9;
  const std::string line = want ? "knowledge-watch: ON (every 2 s; switches itself off after 60 min; type /x4mp knowledge watch again to stop)" : "knowledge-watch: OFF";
  ctx.log.raw(Cat::Md, Level::Info, line);
  diag_hub().forward_log(Level::Info, "[knowledge] " + line);
}

void KnowledgeFeature::handle_watch_answer(host::HostContext& ctx, const std::string& data) {
  watch_.answered();
  const auto tick = parse_watch(data);
  if (!tick) {
    ctx.log.raw(Cat::Md, Level::Warn, "knowledge-watch: unreadable MD answer (" + data.substr(0, 80) + ")");
    return;
  }
  if (!watch_.on()) return;  // switched off while the question was out
  const bool full = clock_s_ - last_full_s_ >= kWatchFullEveryS;
  if (full) last_full_s_ = clock_s_;
  for (const auto& line : format_watch_lines(*tick, watch_last_, full)) {
    ctx.log.raw(Cat::Md, Level::Info, line);
    diag_hub().forward_log(Level::Info, "[knowledge] " + line);
  }
}

void KnowledgeFeature::handle_answer(host::HostContext& ctx, const std::string& text) {
  const auto j = nlohmann::json::parse(text, nullptr, false);
  if (j.is_discarded() || !j.is_object() || !j.contains("data") || !j["data"].is_string()) {
    ctx.log.raw(Cat::Md, Level::Warn, "knowledge: unreadable answer from Lua");
    return;
  }
  const std::string data = j["data"].get<std::string>();
  if (data.rfind("W;", 0) == 0) {
    handle_watch_answer(ctx, data);
    return;
  }
  const auto counts = parse_knowledge(data);
  if (!counts) {
    ctx.log.raw(Cat::Md, Level::Warn, "knowledge: unreadable MD answer (" + data.substr(0, 80) + ")");
    return;
  }
  std::string tag = "?";
  if (const auto it = pending_.find(counts->seq); it != pending_.end()) {
    tag = it->second;
    pending_.erase(it);
  }
  ++answered_;
  const std::string line = format_knowledge_line(*counts, tag);
  ctx.log.raw(Cat::Md, Level::Info, line);
  diag_hub().forward_log(Level::Info, "[knowledge] " + line);  // false when not connected: the local line is written
}

void KnowledgeFeature::on_universe_ready(host::HostContext& ctx) {
  ctx_ = &ctx;
  ask(ctx, ctx.gates.universe_ready_after_reload ? "universe ready (after /reloadui)" : "universe ready");
}

void KnowledgeFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  ctx_ = &ctx;
  clock_s_ += info.delta_s;
  for (const auto& tag : knowledge_hub().take_queued()) ask(ctx, tag);  // asked before the sender was installed
  for (auto& [verb, text] : inbox_->take()) {
    if (verb == "knowledge_md") {
      handle_answer(ctx, text);
    } else if (verb == "knowledge_cmd") {
      handle_command(ctx, text);
    }
  }
  if (watch_.due(clock_s_)) ask_watch(ctx);
}

}  // namespace x4mp::features
