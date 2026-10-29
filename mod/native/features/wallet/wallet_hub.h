#pragma once
// features/wallet/wallet_hub: what this node knows about its own money (M5-22; docs/m5-plan.md W6, docs/mod-design.md 12.1-12.3).
//
// The game function table has AddPlayerMoney and no getter (mod/third_party/x4native/.../x4_game_func_list.inc:40), so native cannot
// read a balance at all. It arrives from Lua instead: ui/x4mp_money.lua polls GetPlayerMoney() every frame and pushes
// x4mp.money {"v":1,"value":<credits>} when the value changes. This hub is what native remembers of that stream: the last balance,
// when it was read, and what the last change was.
//
// The second half is the M5-22 part of the 12.2 reconciliation. Native CAN write money (AddPlayerMoney), and the change it writes comes
// back as an ordinary change on the next poll. note_apply() records what was asked for and observe() recognises the echo, so a write of
// ours is never mistaken for the player's own income (M5-23 builds the pending[] ledger on top of this).
//
// Pure logic: no SDK, no game calls, no IO. Main thread only (the feature drains the inbox in on_frame).

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string_view>

namespace x4mp::features::wallet {

// A money body is one small object. Anything bigger is not one of ours.
inline constexpr std::size_t kMaxMoneyBody = 256;

// What an observation meant. Bad bodies never get here: parse_money_value() refuses them first.
enum class Observation {
  Baseline,  // the first reading of a session: there is nothing to compare with
  Quiet,     // the same balance as the last reading
  Echo,      // exactly the change note_apply() asked the game for
  Change,    // a change this node did not cause: the player's trade, a refund, a repair bill
};

struct WalletStats {
  std::uint64_t polls = 0;     // readings that carried a number
  std::uint64_t bad = 0;       // bodies that were not a money reading
  std::uint64_t baseline = 0;  // first reading of a session
  std::uint64_t quiet = 0;     // unchanged readings
  std::uint64_t changes = 0;   // real changes (the player's, not our echo)
  std::uint64_t echoes = 0;    // changes that were exactly what note_apply() asked for
  std::uint64_t applies = 0;   // note_apply() calls
};

// The balance out of {"v":1,"value":N}: an object with an integral `value`, any key order, extra fields ignored. A float, a string, a
// missing field, a body that is not JSON or one that is too big is not a reading and returns nullopt (never 0: a fabricated zero looks
// exactly like a player going broke).
[[nodiscard]] std::optional<std::int64_t> parse_money_value(std::string_view text);

class WalletHub {
 public:
  // One poll's worth of truth. `now_us` is the frame clock, kept only so a later task can age the reading.
  Observation observe(std::int64_t local_money, std::uint64_t now_us);
  // Money this node asked the game to add (AddPlayerMoney) and has not seen come back yet. Accumulates: two writes before one poll are
  // recognised as their sum.
  void note_apply(std::int64_t delta);
  // A body that was not a money reading at all (the feature counts it and moves on).
  void note_bad() noexcept { ++stats_.bad; }

  [[nodiscard]] bool has_balance() const noexcept { return has_balance_; }
  [[nodiscard]] std::int64_t local_balance() const noexcept { return local_balance_; }
  [[nodiscard]] std::int64_t last_change() const noexcept { return last_change_; }
  [[nodiscard]] std::uint64_t observed_at_us() const noexcept { return observed_at_us_; }
  [[nodiscard]] std::int64_t expected_adjust() const noexcept { return expected_adjust_; }
  [[nodiscard]] const WalletStats& stats() const noexcept { return stats_; }

  void reset();

 private:
  bool has_balance_ = false;
  std::int64_t local_balance_ = 0;
  std::int64_t last_change_ = 0;
  std::uint64_t observed_at_us_ = 0;
  std::int64_t expected_adjust_ = 0;
  WalletStats stats_;
};

[[nodiscard]] WalletHub& wallet_hub();

}  // namespace x4mp::features::wallet
