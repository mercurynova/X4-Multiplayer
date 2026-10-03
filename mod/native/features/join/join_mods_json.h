#pragma once
// features/join/join_mods_json: the mod-policy topics of the M2 bridge contract (docs/mod-design.md section 7, M2-X3). SDK-free.
//   x4mp.mod_refusal  {"v":1,"policy_version":N,"install":[ref],"enable":[ref],"disable":[ref],"update":[ref]}
//        ref = {"id","name","version","have_version","nexus_url","workshop_id":N,"notes"}   (empty strings / 0 omitted)
//   x4mp.mod_policy   {"v":1,"version":N,"source_mode":"authority|admin","unknown_default":"client_only|block|allow_all",
//                      "enforcement":"strict|warn","entries":[{"id","name","rule":"required|allowed|blocked","enabled":bool,
//                      "version_rule":"exact|at_least|any","version","nexus_url","workshop_id":N,"notes"}]}
// Strings are cut at 256 bytes (never inside a UTF-8 sequence), every list at 100 entries (an "<list>_more":N counts the rest).
// The URLs are passed through as the server sent them: the Lua side re-validates and derives the Workshop URL itself.

#include <cstdint>
#include <span>
#include <string>

namespace x4mp::features::join {

// `payload` = a Disconnect frame payload (already verified by the net layer). Empty string when it has no mod_violation.
[[nodiscard]] std::string mod_refusal_json(std::span<const std::uint8_t> disconnect_payload);

// A Welcome frame payload (settings.mod_policy) / a ModPolicyChanged frame payload. Empty string when there is no policy.
[[nodiscard]] std::string mod_policy_json_from_welcome(std::span<const std::uint8_t> welcome_payload);
[[nodiscard]] std::string mod_policy_json_from_changed(std::span<const std::uint8_t> changed_payload);

}  // namespace x4mp::features::join
