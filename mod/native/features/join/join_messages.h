#pragma once
// features/join/join_messages: encoders for the node -> server messages of the join pipeline (protocol.md 6.4):
// LoadStatus, ManifestReport (count-only in M2) and NodeReady. SDK-free (core wire types only).

#include <cstdint>
#include <string_view>
#include <vector>

#include "core/session/session.h"

namespace x4mp::features::join {

using Payload = std::vector<std::uint8_t>;

// X4MP.Proto.NodePhase wire values used by the join flow.
enum class JoinPhase : std::uint8_t { SyncingSave = 2, Verifying = 3, Loading = 4, Matching = 5, CatchingUp = 6 };

[[nodiscard]] Payload encode_load_status(JoinPhase phase, float progress = 1.0f, std::string_view detail = {});

// Count-only stub (M2): the manifest is not matched against the loaded universe yet (M4), so only the checkpoint id and the
// duration are reported; total/matched stay 0.
[[nodiscard]] Payload encode_manifest_report_counts(const session::Id128& checkpoint, std::uint32_t total, std::uint32_t matched,
                                                    std::uint32_t duration_ms);

// M4-21: the real report (protocol/schema/session.fbs ManifestReport), built from the matching pass StaticsFeature ran. `sample` is
// the diagnostic net-id list (<= 256); the mismatch KEYS travel as LogForward lines, because the table has no field for them.
struct ManifestReportNumbers {
  std::uint32_t total = 0;                  // station rows the scans reported
  std::uint32_t matched = 0;
  std::uint32_t tiebreak_owner = 0;
  std::uint32_t tiebreak_idcode = 0;
  std::uint32_t unmatched = 0;
  std::uint32_t ambiguous = 0;
  std::uint32_t universe_id_equal = 0;
  std::uint32_t duration_ms = 0;
  std::vector<std::uint32_t> sample;
};
[[nodiscard]] Payload encode_manifest_report(const session::Id128& checkpoint, const ManifestReportNumbers& numbers);

[[nodiscard]] Payload encode_node_ready(std::uint64_t universe_epoch, const std::vector<std::uint8_t>& loaded_save_sha256);

// Wire message type numbers (X4MP.Proto.MsgType).
// One LogForward frame with a single line (level 0 Debug .. 3 Error, time_us = unix microseconds).
[[nodiscard]] Payload encode_log_forward(int level, std::uint64_t time_us, std::string_view text);
[[nodiscard]] std::uint16_t msg_log_forward() noexcept;

[[nodiscard]] std::uint16_t msg_load_status() noexcept;
[[nodiscard]] std::uint16_t msg_manifest_report() noexcept;
[[nodiscard]] std::uint16_t msg_node_ready() noexcept;

}  // namespace x4mp::features::join
