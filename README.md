# CandleCloseConfirm

**A NinjaTrader 8 strategy that enters only after the candle has closed — and only if the close passes your checks.**

You make the trade decision. You click **ARM LONG** or **ARM SHORT** while a candle is still forming. When that candle closes, the strategy verifies a short rule set against a key level you drew, then either fires a market order through your **ATM template** or stands down. It cannot enter early: its decision logic only runs on bar-close events.

It is a semi-automated, discretionary-assist tool. It does not look for setups, size positions, or manage exits.

---

## Why

Entering a few seconds before the candle closes is one of the most common discretionary leaks: the close you were waiting for never prints, and the wick you ignored becomes the loss. Knowing the rule does not stop the click. Taking the click away does.

With this tool the physical act changes from "buy now" to "arm, then wait". The strategy performs the close-confirmation step you would otherwise do by eye, at the exact bar close.

---

## How it works

1. Add the strategy to a chart (time-based bars in seconds or minutes). A **blue horizontal line** is drawn at the last close, and **ARM LONG / ARM SHORT / DISARM** buttons appear on the chart.
2. Drag the line to your key level.
3. While the candle is forming, click **ARM LONG** or **ARM SHORT**. At that instant the strategy **freezes** the line's price and the ATM template currently selected in Chart Trader.
4. At the bar close the strategy evaluates the rules below.
   - All pass → one market order is sent through the frozen ATM template (`ATM_FIRED`).
   - Any fails → nothing is sent, the reason is logged (`VETO_*`).
5. Either way the cycle ends. There is no automatic re-arm.

Quantity, stop, target and breakeven all live in your ATM template. The strategy never submits or manages any other order.

### The rules

`level` is the frozen line price, `range = high − low` of the bar that just closed.

| # | Check | Long | Short |
|---|---|---|---|
| 1 | Bar has range | `range > 0` | `range > 0` |
| 2 | Close vs level | `close >= level` | `close <= level` |
| 3 | Wick (skipped if *Wick max %* = 0) | `(high − max(open, close)) / range < Wick max %` | `(min(open, close) − low) / range < Wick max %` |
| 4 | Spike (skipped if *Spike veto points* = 0) | `high <= level + X` | `low >= level − X` |

Boundaries are deliberate: a wick of exactly *Wick max %* **vetoes**; a high exactly *X* points past the level **passes**. Checks run in the order above and the first failure is the one reported.

### Parameters

| Parameter | Default | Meaning |
|---|---|---|
| Wick max % | 33 | Veto when the rejection wick is this share of the bar range or larger. **0 turns the check off.** |
| Spike veto points (X) | 40 | Veto when the bar pushed more than X points beyond the level. **0 turns the check off.** |
| Eval grace (ms) | 200 | Wait this long after the wall-clock bar end, then evaluate without waiting for the next tick. 0 = wait for the next tick only. |
| Log folder | `Documents\NinjaTrader 8\CandleCloseConfirm` | Where the daily CSV is written. |
| Level line color / width | DodgerBlue / 2 | Cosmetics. |

### Safety interlocks

`ARM` is rejected, and the reason logged, when:

- the strategy's account already holds a position in the chart instrument (`REJECT_NOT_FLAT`);
- the key-level line was deleted — it is redrawn at the current close (`REJECT_NO_LINE`);
- Chart Trader has no usable ATM template selected (`REJECT_BAD_TEMPLATE`).

Every ARM click, including a re-arm after DISARM, runs the full set. Historical bars are fully inert.

---

## Install

1. Copy `src/CandleCloseConfirm.cs` to `Documents\NinjaTrader 8\bin\Custom\Strategies\`.
2. In NinjaTrader open **New → NinjaScript Editor** and press **F5** to compile.
3. Open a chart with **Chart Trader visible** and an **ATM strategy template** selected.
4. Right-click the chart → **Strategies** → add *CandleCloseConfirm*, choose the account, tick *Enabled*.

Written against NinjaTrader 8.1.5.1 (C# 9). The file header lists the NT8 API choices that differ between minor versions and the one-method change needed if your build will not let you drag a script-drawn line.

> **Test in Sim first.** In Market Replay the wall-clock timer stands down automatically (replay bar dates do not match the real clock) and evaluation falls back to the next-tick bar-close event, which is slightly later than on a live feed.

---

## Telemetry

Every event is printed to the NinjaScript Output window and appended to `CandleCloseConfirm_yyyyMMdd.csv`:

`timestamp, bar_time, event, detail, direction, level, open, high, low, close, wick_pct, spike_dist, template`

| Event | Meaning |
|---|---|
| `ARMED_LONG` / `ARMED_SHORT` | Cycle started; level and template frozen |
| `ATM_FIRED` | All checks passed; the ATM market order was submitted |
| `VETO_ZERO_RANGE` / `VETO_CLOSE` / `VETO_WICK` / `VETO_SPIKE` | The first failed check |
| `MANUAL_DISARM` | You cancelled an armed cycle |
| `REJECT_NOT_FLAT` / `REJECT_NO_LINE` / `REJECT_BAD_TEMPLATE` | ARM refused by an interlock |
| `FIRE_FAILED` | The ATM submission threw or returned an error |

Telemetry never delays an order: on a pass the order goes out first and the row is written after.

---

## Design notes

- `Calculate.OnBarClose` only — no intrabar decisions.
- Exactly one order call, `AtmStrategyCreate`, and nothing that modifies or cancels orders afterwards.
- `RealtimeErrorHandling = IgnoreAllErrors`, so closing a position by hand (or any order error) does not switch the strategy off.
- The bar is evaluated by a one-shot wall-clock timer at bar end + grace, because NinjaTrader raises the bar-close event only on the first tick of the *next* bar. The timer is sanity-checked at ARM and stands down on any clock mismatch; the tick-driven path always stays live and each cycle evaluates exactly once.

---

## License

[Apache-2.0](LICENSE).

---

## Disclaimer

For education and research. Futures trading involves substantial risk of loss and is not suitable for every investor. This software is provided as-is, without warranty, and is not financial advice. You are responsible for testing it in simulation and for every order it sends.
