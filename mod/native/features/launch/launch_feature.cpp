#include "features/launch/launch_feature.h"

#include <chrono>
#include <string>

#include "features/join/join_requests.h"
#include "features/launch/launch_json.h"

namespace x4mp::features {

void LaunchFeature::on_init(host::HostContext& ctx) {
  using host::Cat;
  using host::Level;
  if (ctx.paths == nullptr || ctx.paths->launch_file.empty()) return;
  const auto now = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
  auto consumed = launch::consume_launch_file(ctx.paths->launch_file, now);
  if (!consumed.existed) return;

  if (!consumed.removed) {
    X4MP_CLOG(ctx.log, Cat::Sess, Level::Error, "launch.json could not be deleted ({}); its content was {}", ctx.paths->launch_file.string(),
              consumed.scrubbed ? "emptied" : "NOT emptied either");
  }
  switch (consumed.parse.status) {
    case launch::LaunchStatus::Ok: {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "launch.json consumed: {}", launch::describe(consumed.parse.request));
      if (ctx.previous.present) {
        // An extension reload (save load, /reloadui) is not a new launch: the resume in the join feature owns it.
        X4MP_CLOG(ctx.log, Cat::Sess, Level::Info, "launch.json ignored: this is a reload (#{}), the resume takes over", ctx.previous.reload_count);
      } else {
        join::submit_join_payload(launch::to_join_payload(consumed.parse.request));
      }
      break;
    }
    case launch::LaunchStatus::Expired:
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Warn, "launch.json expired ({}); ignored and deleted", launch::describe(consumed.parse.request));
      break;
    case launch::LaunchStatus::Invalid:
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Warn, "launch.json invalid ({}); ignored and deleted", consumed.parse.error);
      break;
  }
  launch::wipe(consumed.parse.request);
}

}  // namespace x4mp::features
