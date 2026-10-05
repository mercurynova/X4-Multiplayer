#pragma once
// features/diag/knowledge_feature: the knowledge probe of M3-23 (Finding 4: a client loses the player's map knowledge).
//
// Counts, in the running game, how many sectors / clusters / stations / gates are known to the player and the known state of three
// named sample sectors, and writes ONE log line per probe:
//
//   knowledge: sectors_known=N/M clusters_known=a/b stations_known=x/y gates_known=u/v samples=[01_001=1,07_001=0,14_001=-] at='<tag>' game_age=123.4s
//
// Who counts: the game, through MD (md/x4mp_diag.xml). `find_sector` / `find_station` / `find_gate` / `find_cluster` over the whole galaxy
// (`multiple`), once without and once with the documented `known="true"` search attribute (libraries/common.xsd, "Known to player?");
// the samples use the `.isknown` property (libraries/scriptproperties.xml). A sample is 1 known, 0 unknown, - not found.
//
// Wire (the same three-hop path as the other MD helpers):
//   native -> Lua  x4mp.knowledge_ask {"v":1,"seq":N}          -> AddUITriggeredEvent("X4MP_Diag", "probe", N)
//   MD -> Lua      x4mp.md_knowledge <string>                  -> forwarded unchanged
//   Lua -> native  x4mp.knowledge_md {"v":1,"data":"K;<seq>;<age>;<sk>;<st>;<ck>;<ct>;<stk>;<stt>;<gk>;<gt>;<name>=<0|1|->,..."}
//   Lua -> native  x4mp.knowledge_cmd {"v":1}                  the chat command "/x4mp knowledge"
//
// Who asks (knowledge_probe(tag), main thread): this feature at universe ready (it is the FIRST feature of the registry, so it runs before
// anything else of ours) and on the chat command; the janitor after its sweep, the ghost feature after the first ghost spawn, the client
// takeover after each stage. The answer comes back through MD a frame or so later, so the line carries the game age (MD player.age) of the
// moment the count was taken; compare it with the age of the neighbouring log lines.
//
// A diagnostic only: no game state is changed. Process-wide hub like the other features' hubs.

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "host/feature.h"

namespace x4mp::features {

// The decoded MD answer.
struct KnowledgeCounts {
  std::uint32_t seq = 0;
  double age_s = 0.0;
  std::uint32_t sectors_known = 0, sectors = 0;
  std::uint32_t clusters_known = 0, clusters = 0;
  std::uint32_t stations_known = 0, stations = 0;
  std::uint32_t gates_known = 0, gates = 0;
  std::vector<std::pair<std::string, char>> samples;  // name, '1' | '0' | '-'
};

// "K;seq;age;sk;st;ck;ct;stk;stt;gk;gt;a=1,b=0,c=-" -> counts. nullopt when it is not a K message or a field is unreadable.
[[nodiscard]] std::optional<KnowledgeCounts> parse_knowledge(std::string_view data);
// The full log line text, starting with "knowledge: ".
[[nodiscard]] std::string format_knowledge_line(const KnowledgeCounts& c, std::string_view tag);

class KnowledgeHub {
 public:
  using Sender = std::function<void(const std::string& tag)>;
  // Asks for one probe labelled `tag`. Main thread. Sent at once when the feature is up; before that it is queued and sent by the feature.
  void request(const std::string& tag);
  void set_sender(const void* owner, Sender s);  // installed by the feature at on_init
  void clear_sender(const void* owner);          // no-op when another owner installed one since (a reload creates the new feature before the old one dies)
  [[nodiscard]] std::vector<std::string> take_queued();
  [[nodiscard]] std::uint64_t requested() const;
  void reset();  // tests

 private:
  mutable std::mutex m_;
  Sender sender_;
  const void* owner_ = nullptr;
  std::vector<std::string> queued_;
  std::uint64_t requested_ = 0;
};

[[nodiscard]] KnowledgeHub& knowledge_hub() noexcept;
inline void knowledge_probe(const std::string& tag) { knowledge_hub().request(tag); }

class KnowledgeFeature final : public host::IFeature {
 public:
  KnowledgeFeature();
  ~KnowledgeFeature() override;
  [[nodiscard]] std::string_view name() const noexcept override { return "knowledge"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_universe_ready(host::HostContext& ctx) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  struct Inbox;
  void ask(host::HostContext& ctx, const std::string& tag);
  void handle_answer(host::HostContext& ctx, const std::string& text);

  std::shared_ptr<Inbox> inbox_;
  host::HostContext* ctx_ = nullptr;
  std::uint32_t seq_ = 0;
  std::map<std::uint32_t, std::string> pending_;  // seq -> tag
  std::uint32_t answered_ = 0;
};

}  // namespace x4mp::features
