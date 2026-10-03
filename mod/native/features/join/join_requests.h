#pragma once
// features/join/join_requests: how another feature starts a join exactly like the UI does (M2-12).
//
// The launch feature (launch.json) submits an x4mp.join JSON payload here; JoinFeature takes it on its next frame and runs the very
// same path as the Lua verb x4mp.join (join::parse_join -> start_session), but only while it is Idle (a resumed session wins).
// Thread-safe; process-wide (dies with the DLL, like the diag hub). The payload holds the password: it is wiped after use.

#include <string>
#include <vector>

namespace x4mp::features::join {

void submit_join_payload(std::string payload);
[[nodiscard]] std::vector<std::string> take_join_payloads();
void clear_join_payloads();  // wipes whatever is queued (shutdown, tests)

}  // namespace x4mp::features::join
