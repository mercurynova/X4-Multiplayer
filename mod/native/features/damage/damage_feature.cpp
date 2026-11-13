#include "features/damage/damage_feature.h"

#include <chrono>
#include <span>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/events/event_wire.h"
#include "features/events/events_hub.h"
#include "features/ghosts/ghost_hub.h"
#include "host/host_log.h"
#include "message_ids_generated.h"

namespace x4mp::features {

namespace {
using host::Cat;
using host::Level;
using Clock = std::chrono::steady_clock;
constexpr std::int64_t kReportEveryUs = 30'000'000;  // M15: the same 30 s line the other features print
constexpr double kMinReportedPercent = 0.05;          // below this a drop is rounding, not a hit
}  // namespace

void DamageFeature::on_init(host::HostContext& ctx) {
  hook_armed_ = ctx.platform.subscribe_damage([this](const host::IPlatform::MdDamage& hit) { on_game_hit(hit); });
  (void)ctx.platform.subscribe_event("x4mp.damage_state", [this](std::string_view text) { on_state_text(text); });
  if (hook_armed_) {
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "damage: the HullDamaged/ShieldDamaged hooks are armed (M15)");
  } else {
    ++hook_off_;
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn,
              "damage: the HullDamaged/ShieldDamaged hooks are not available; a hit on a ghost will not be reported");
  }
}

void DamageFeature::on_game_hit(const host::IPlatform::MdDamage& hit) {
  if (hit.component == 0) return;
  const std::lock_guard<std::mutex> lock(mutex_);
  if (hits_.size() >= 256) return;  // a flood cannot grow the heap; the next frame drains this
  hits_.push_back(hit.component);
}

void DamageFeature::on_state_text(std::string_view text) {
  if (text.empty()) return;
  const std::lock_guard<std::mutex> lock(mutex_);
  if (texts_.size() >= 16) return;
  texts_.emplace_back(text);
}

void DamageFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  (void)info;
  flush(ctx);

  const std::int64_t now_us =
      std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();

  std::vector<std::string> texts;
  {
    const std::lock_guard<std::mutex> lock(mutex_);
    texts.swap(texts_);
  }
  for (const std::string& text : texts) {
    nlohmann::json j = nlohmann::json::parse(text, nullptr, false);
    if (j.is_discarded() || !j.is_object()) continue;
    const auto rows = j.find("rows");
    if (rows == j.end() || !rows->is_array()) continue;
    ++states_;

    std::vector<events::HitEntryData> entries;
    {
      const std::lock_guard<std::mutex> lock(mutex_);
      for (const auto& row : *rows) {
        if (!row.is_array() || row.size() < 4) continue;
        const auto seq = row[1].is_number_unsigned() ? row[1].get<std::uint32_t>() : 0u;
        const auto found = in_flight_.find(seq);
        if (found == in_flight_.end()) continue;
        const std::uint64_t component = found->second;
        in_flight_.erase(found);

        const std::uint32_t net = ghosts::ghost_hub().net_id_of_local(component);
        if (net == 0) {
          ++ignored_;  // not a ghost of this node (our own ship, an avatar, a station)
          continue;
        }
        const damage::Percent reading{row[2].is_number() ? row[2].get<double>() : -1.0,
                                       row[3].is_number() ? row[3].get<double>() : -1.0};
        const auto was = known_.find(net);
        if (was == known_.end()) {
          known_[net] = reading;  // first sight: this hit's delta is unknowable, the next one is not
          ++baselines_;
          continue;
        }
        const damage::Percent delta = damage::percent_delta(was->second, reading);
        was->second = reading;
        if (delta.hull < kMinReportedPercent && delta.shield < kMinReportedPercent) continue;
        entries.push_back(events::HitEntryData{net, static_cast<float>(delta.hull), static_cast<float>(delta.shield)});
      }
      if (known_.size() > 4096) known_.clear();  // bounded memory; the next sighting just re-baselines
    }

    if (entries.empty()) continue;
    events::IntentRequest request;
    request.key_lo = ++next_key_;
    request.key_hi = events::events_hub().self_player_id();
    request.request_id = next_key_;
    request.player_id = static_cast<std::uint16_t>(request.key_hi);
    request.game_time = 0.0;
    const std::vector<std::uint8_t> bytes = events::encode_hit_report(request, entries);
    if (events::events_hub().send_control(static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent), std::span<const std::uint8_t>(bytes))) {
      ++sent_;
    }
  }

  if (now_us - last_probe_us_ >= kProbePeriodUs) {
    last_probe_us_ = now_us;
    send_probe(ctx, now_us);
  }

  if (now_us - last_log_us_ >= kReportEveryUs) {
    last_log_us_ = now_us;
    report(ctx);
  }
}

// M15: ask Lua for the percentages of the ghosts that were just hit. One raise per 250 ms, whatever the hit rate; a component
// already in flight is not asked again, so a burst cannot pile up answers.
void DamageFeature::send_probe(host::HostContext& ctx, std::int64_t now_us) {
  (void)now_us;
  std::vector<std::uint64_t> hits;
  {
    const std::lock_guard<std::mutex> lock(mutex_);
    hits.swap(hits_);
  }
  if (hits.empty()) return;

  std::unordered_map<std::uint64_t, bool> asked;
  nlohmann::json rows = nlohmann::json::array();
  {
    const std::lock_guard<std::mutex> lock(mutex_);
    for (const std::uint64_t component : hits) {
      if (component == 0 || asked.count(component) != 0) continue;
      asked[component] = true;
      const std::uint32_t seq = ++next_seq_;
      // A probe Lua never answers (the object left the world before the read) would otherwise stay here forever. Dropping the
      // whole table is safe: the only cost is that the next answer for those ships re-baselines instead of reporting a delta.
      if (in_flight_.size() >= 4096) in_flight_.clear();
      in_flight_[seq] = component;
      rows.push_back({std::to_string(component), seq});
      if (rows.size() >= 64) break;
    }
  }
  if (rows.empty()) return;

  nlohmann::json j;
  j["v"] = 1;
  j["rows"] = std::move(rows);
  if (!ctx.platform.raise_lua("x4mp.damage_probe", j.dump())) return;
  ++probes_;
}

void DamageFeature::on_shutdown(host::HostContext& ctx) {
  // Nothing is kept across a save: a queued line is one hit, and the authority re-reads the real hull after a reload.
  flush(ctx);
  const std::lock_guard<std::mutex> lock(mutex_);
  hits_.clear();
  texts_.clear();
  in_flight_.clear();
  known_.clear();
}

void DamageFeature::flush(host::HostContext& ctx) {
  std::vector<damage::DamageOp> ops = damage::damage_hub().take_ops(kPerFrame);
  if (ops.empty()) return;
  damage::DamageStats& stats = damage::damage_hub().stats();
  ++stats.frames;

  nlohmann::json j;
  j["v"] = 1;
  j["damage"] = nlohmann::json::array();
  for (const damage::DamageOp& op : ops) j["damage"].push_back({op.component, op.hull, op.shield});

  const std::string text = j.dump();
  if (!ctx.platform.raise_lua("x4mp.actions", text)) {
    ++stats.raises_failed;
    stats.ops_skipped += ops.size();
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "damage: actions shim refused {} damage line(s)", ops.size());
    return;
  }

  stats.ops_applied += ops.size();
}

void DamageFeature::report(host::HostContext& ctx) {
  const damage::DamageStats& s = damage::damage_hub().stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[damage] queued={} applied={} skipped={} dropped={} rejected={} frames={} shim refused={} | hook={} probes={} states={} "
            "sent={} ignored={} baselines={}",
            s.ops_queued, s.ops_applied, s.ops_skipped, s.ops_dropped, s.hits_rejected, s.frames, s.raises_failed,
            hook_armed_ ? "armed" : "OFF", probes_, states_, sent_, ignored_, baselines_);
}

}  // namespace x4mp::features
