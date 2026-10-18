#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//==============================================================================================
// CandleCloseConfirm — NinjaTrader 8 manual-arm / auto-fire strategy
//
// You decide the trade; the strategy decides the timing. Click ARM LONG or ARM SHORT while a
// candle is still forming. When that candle closes, the strategy checks a short rule set against
// the key-level line frozen at ARM time and either fires a market order through the ATM template
// selected in Chart Trader, or stands down. It cannot enter before the close: its decision logic
// only runs on bar-close events.
//
//----------------------------------------------------------------------------------------------
// RULES
//----------------------------------------------------------------------------------------------
//
// Let `level` = price of the key-level line at the moment ARM was clicked (frozen there).
// Let `range` = `high - low` of the evaluated (just-closed) bar.
// Parameters (editable in strategy properties): `WickMaxPct` default 33,
// `SpikeVetoPoints` (X) default 40. Setting either to 0 switches that check off.
//
// ARM LONG fires iff ALL of:
// 1. `range > 0` — a zero-range bar (high == low) is a veto (wick ratio undefined).
// 2. `close >= level` — "higher or equal."
// 3. Upper wick strictly below threshold: `(high - max(open, close)) / range < WickMaxPct/100`.
//    A wick of exactly the threshold vetoes. Skipped when WickMaxPct = 0.
// 4. No spike past the level: `high <= level + SpikeVetoPoints`.
//    Veto only when `high > level + X`; a high at exactly `level + X` passes.
//    Skipped when SpikeVetoPoints = 0.
//
// ARM SHORT fires iff ALL of (mirror):
// 1. `range > 0`.
// 2. `close <= level`.
// 3. Lower wick strictly below threshold: `(min(open, close) - low) / range < WickMaxPct/100`.
//    Skipped when WickMaxPct = 0.
// 4. `low >= level - SpikeVetoPoints`; veto only when `low < level - X`.
//    Skipped when SpikeVetoPoints = 0.
//
// One-shot rule: every armed cycle ends at the bar close it was armed for, fired or vetoed.
// There is no automatic re-arm. DISARM cancels an armed cycle, and the ARM buttons are usable
// again immediately — re-arming inside the same forming candle, in either direction, is allowed.
//
//----------------------------------------------------------------------------------------------
// SCOPE
//----------------------------------------------------------------------------------------------
// The strategy submits exactly one thing: a single AtmStrategyCreate market order. Quantity,
// stop, target and breakeven all live in the ATM template; this code never modifies or manages
// an order. Historical bars are fully inert — no arming, no evaluation, no ATM calls.
// Calculate.OnBarClose only: no intrabar decisions, ever.
//
//----------------------------------------------------------------------------------------------
// DESIGN NOTES
//----------------------------------------------------------------------------------------------
// - The ATM template is read from Chart Trader at the moment ARM is clicked (there is no
//   template parameter) and is frozen for the armed cycle together with the level. A missing or
//   unknown template is rejected at ARM, never at fire (REJECT_BAD_TEMPLATE).
// - ARM is interlocked: the strategy's own account must be flat on the chart instrument
//   (REJECT_NOT_FLAT), the key-level line must exist (REJECT_NO_LINE) and a usable ATM template
//   must be selected. Every ARM click, including a re-arm, runs the full set. Positions in other
//   accounts do not block ARM.
// - Closing a position by hand must not switch the strategy off: RealtimeErrorHandling is
//   IgnoreAllErrors, so NinjaTrader's automatic stop on order errors is disabled. Alerts still
//   show. The flat interlock re-reads the account at every ARM, so arming resumes on its own
//   once the account is flat again.
// - Wall-clock fire. Under Calculate.OnBarClose NinjaTrader raises the bar-close event only when
//   the first tick of the NEXT bar arrives, which can be a full second after the boundary. At
//   ARM a one-shot timer is scheduled for bar-end + EvalGraceMs; when it fires, the just-ended
//   bar is evaluated straight off the underlying Bars series. The wall clock is only trusted
//   after a sanity check made at ARM: the remaining wait must land inside the armed bar's own
//   window. In Market Replay — where bar times are historical dates while Core.Globals.Now keeps
//   running on the real clock — and under any clock/timezone misconfiguration, the check fails
//   and the timer stands down with an Output-window note; the cycle then runs purely
//   tick-driven. A stale timer callback re-checks due-time on the script thread before claiming
//   anything, so it can never evaluate a forming bar. The tick-driven path always stays live
//   and each armed cycle evaluates exactly once. EvalGraceMs = 0 switches the timer off.
// - Between a bar close and AtmStrategyCreate the code does no file or chart work: the ATM_FIRED
//   telemetry row is written right after the submission returns, and the level-line self-heal
//   runs after the evaluation rather than before it.
//
//----------------------------------------------------------------------------------------------
// COMPILE / VERSION NOTES — written against NinjaTrader 8.1.5.1, C# LangVersion 9.0
//----------------------------------------------------------------------------------------------
// 1. ATM entry point. The NT8 member on the Strategy base class is
//    `AtmStrategyCreate(OrderAction, OrderType, double limitPrice, double stopPrice,
//    TimeInForce, string orderId, string strategyTemplateName, string atmStrategyId,
//    Action<ErrorCode, string> callback)`. It needs no unmanaged mode (cf. NT8's own
//    @SampleAtmStrategy.cs).
// 2. Button hosting uses `UserControlCollection` (the documented NT8 way to put WPF controls on
//    a chart from a script). If a minor version rejects it, host the same StackPanel by walking
//    `Window.GetWindow(ChartControl.Parent)` instead — only AddChartUi/RemoveChartUi change.
// 3. All chart work (Draw calls, DrawObjects reads, ATM calls) is marshalled onto the NinjaScript
//    thread with `TriggerCustomEvent`. Button handlers themselves do nothing but post.
// 4. Level-line draggability: the line is drawn by the script with `IsLocked = false`. If your
//    build will not let you drag a script-drawn object, `FindLevelLine()` is the ONLY place the
//    line is located, so switch that one method to match a user-drawn HorizontalLine carrying
//    the same tag and nothing else moves.
// 5. `LogFilePath` is a FOLDER. The daily file `CandleCloseConfirm_yyyyMMdd.csv` is created
//    inside it. Numbers are written with InvariantCulture so a non-English locale cannot turn
//    decimal points into commas.
// 6. The ATM template is read via `Window.GetWindow(ChartControl.Parent) as Chart` ->
//    `.ChartTrader.AtmStrategy` -> `.Template`. Those are WPF objects owned by the chart window,
//    so the read happens ON THE UI THREAD inside the ARM click handler.
// 7. Nothing in here submits, modifies or manages an order other than the single
//    AtmStrategyCreate call. `IsExitOnSessionCloseStrategy` is forced false for that reason.
//
// Telemetry event codes (CSV + Output window): ARMED_LONG, ARMED_SHORT, MANUAL_DISARM,
// ATM_FIRED, VETO_ZERO_RANGE, VETO_CLOSE, VETO_WICK, VETO_SPIKE, REJECT_NOT_FLAT,
// REJECT_NO_LINE, REJECT_BAD_TEMPLATE, FIRE_FAILED.
//==============================================================================================

//This namespace holds Strategies in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Strategies
{
	public class CandleCloseConfirm : Strategy
	{
		#region Constants and state

		private const string LevelTag	= "CandleCloseConfirm_Level";
		private const string StatusTag	= "CandleCloseConfirm_Status";

		private enum ArmDirection { None, Long, Short }

		// Shared arm-state. Touched from the NinjaScript thread and (indirectly) from UI clicks,
		// so every read/write goes through stateLock.
		private readonly object		stateLock		= new object();
		private ArmDirection		armedDirection	= ArmDirection.None;
		private double				frozenLevel;
		private string				frozenAtmTemplate = string.Empty;
		private DateTime			expectedEvalTime = DateTime.MinValue;

		// Wall-clock evaluation timer: one-shot, scheduled at ARM for expectedEvalTime +
		// EvalGraceMs after the clock passes the sanity check in StartEvalTimer; the evaluation
		// itself always runs on the script thread.
		private System.Timers.Timer	evalTimer;

		// Ids of the last fire.
		private string				lastOrderId		= string.Empty;
		private string				lastAtmId		= string.Empty;

		// Status line content.
		private string				lastResult		= "none";
		private bool				telemetryWarning;

		// Last status text actually drawn; UpdateStatus skips the WPF redraw when unchanged.
		private string				lastStatusText;

		// ATM template name carried into the log rows (the one read at the last ARM click, or the
		// frozen one while an armed cycle is being evaluated).
		private string				atmTemplateForLog = string.Empty;

		// Chart UI.
		private System.Windows.Controls.StackPanel	armPanel;
		private System.Windows.Controls.Button		armLongButton;
		private System.Windows.Controls.Button		armShortButton;
		private System.Windows.Controls.Button		disarmButton;
		private bool								uiAdded;

		private SimpleFont			statusFont;
		private readonly object		logLock			= new object();

		// WriteCsv per-day cache (guarded by logLock): the log folder is resolved and created
		// once per day, not once per row. Cleared on date roll and after a failed write, so a
		// deleted folder is recreated on the next attempt.
		private string				csvFilePath;
		private string				csvFileDay;

		#endregion

		#region Parameters

		[NinjaScriptProperty]
		[Range(0.0, 100.0)]
		[Display(Name = "Wick max %", Description = "Wick veto threshold, as a percentage of the bar range. Wick ratio must be strictly below this to pass; exactly this value vetoes. 0 switches the wick check off.", Order = 1, GroupName = "Parameters")]
		public double WickMaxPct { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Spike veto points (X)", Description = "Veto when the bar spikes more than X points past the frozen level. Exactly X passes. 0 switches the spike check off.", Order = 2, GroupName = "Parameters")]
		public double SpikeVetoPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0, 5000)]
		[Display(Name = "Eval grace (ms)", Description = "Milliseconds after the armed bar's wall-clock close at which the bar is evaluated without waiting for the next tick. Must cover feed latency and PC clock error; 0 disables the timer and waits for the next tick. Stands down automatically in Market Replay or when the clock disagrees with bar times.", Order = 3, GroupName = "Parameters")]
		public int EvalGraceMs { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Log folder", Description = "Folder for the daily CSV telemetry file CandleCloseConfirm_yyyyMMdd.csv.", Order = 4, GroupName = "Parameters")]
		public string LogFilePath { get; set; }

		[XmlIgnore]
		[Display(Name = "Level line color", Description = "Color of the strategy-owned key-level line.", Order = 5, GroupName = "Parameters")]
		public Brush LevelLineColor { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Level line width", Description = "Stroke width of the strategy-owned key-level line.", Order = 6, GroupName = "Parameters")]
		public int LevelLineWidth { get; set; }

		[Browsable(false)]
		public string LevelLineColorSerializable
		{
			get { return Serialize.BrushToString(LevelLineColor); }
			set { LevelLineColor = Serialize.StringToBrush(value); }
		}

		// Chart label: the name only. NinjaTrader's default label appends every
		// [NinjaScriptProperty] value (log folder path included), which clutters the chart header.
		public override string DisplayName
		{
			get { return Name; }
		}

		#endregion

		#region Lifecycle

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name									= "CandleCloseConfirm";
				Description								= "Manual-arm / auto-fire strategy. ARM while a candle is forming; at the bar close the rules are verified and a market order is fired through the Chart Trader ATM template, or the strategy disarms.";
				Calculate								= Calculate.OnBarClose;
				IsInstantiatedOnEachOptimizationIteration = false;

				// This strategy never submits or manages an order of its own.
				IsExitOnSessionCloseStrategy			= false;

				// Closing a position by hand — or any other order error — must not switch the
				// strategy off. Alerts still surface; only NinjaTrader's automatic stop is off.
				RealtimeErrorHandling					= RealtimeErrorHandling.IgnoreAllErrors;

				WickMaxPct								= 33;
				SpikeVetoPoints							= 40;
				EvalGraceMs								= 200;
				LogFilePath								= DefaultLogFolder();
				LevelLineColor							= Brushes.DodgerBlue;
				LevelLineWidth							= 2;
			}
			else if (State == State.Realtime)
			{
				if (ChartControl == null)
				{
					// Chartless run: stay inert but say so.
					Print(Stamp() + " CandleCloseConfirm: no ChartControl — buttons and level line unavailable, strategy stays inert.");
				}
				else
				{
					ChartControl.Dispatcher.InvokeAsync(() => AddChartUi());
				}

				EnsureChartObjects();
				UpdateStatus();
			}
			else if (State == State.Terminated)
			{
				DisposeEvalTimer();

				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => RemoveChartUi());

				try
				{
					RemoveDrawObject(LevelTag);
					RemoveDrawObject(StatusTag);
				}
				catch (Exception ex)
				{
					Print(Stamp() + " CandleCloseConfirm: draw object cleanup failed: " + ex.Message);
				}
			}
		}

		#endregion

		#region Chart UI

		private void AddChartUi()
		{
			try
			{
				if (uiAdded || State >= State.Terminated)
					return;

				armPanel = new System.Windows.Controls.StackPanel
				{
					Orientation			= System.Windows.Controls.Orientation.Horizontal,
					HorizontalAlignment	= System.Windows.HorizontalAlignment.Left,
					VerticalAlignment	= System.Windows.VerticalAlignment.Top,
					Margin				= new System.Windows.Thickness(6, 6, 0, 0)
				};

				armLongButton	= CreateButton("ARM LONG",	Brushes.SeaGreen,	OnArmLongClick);
				armShortButton	= CreateButton("ARM SHORT",	Brushes.Firebrick,	OnArmShortClick);
				disarmButton	= CreateButton("DISARM",	Brushes.DimGray,	OnDisarmClick);

				armPanel.Children.Add(armLongButton);
				armPanel.Children.Add(armShortButton);
				armPanel.Children.Add(disarmButton);

				UserControlCollection.Add(armPanel);
				uiAdded = true;

				// We are on the UI thread here, so this is the safe moment to report what Chart
				// Trader currently has selected — a start-of-session check of the ATM template.
				string template = ReadChartTraderAtmTemplate();
				string detail;

				if (IsAtmTemplateValid(template, out detail))
					Print(Stamp() + " CandleCloseConfirm start: Chart Trader ATM template -> " + template);
				else
					Print(Stamp() + " CandleCloseConfirm start: no usable Chart Trader ATM template (" + detail + "). ARM stays rejected until one is selected.");
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: chart UI injection failed: " + ex.Message);
			}
		}

		private void RemoveChartUi()
		{
			try
			{
				if (armLongButton != null)	{ armLongButton.Click	-= OnArmLongClick;	armLongButton	= null; }
				if (armShortButton != null)	{ armShortButton.Click	-= OnArmShortClick;	armShortButton	= null; }
				if (disarmButton != null)	{ disarmButton.Click	-= OnDisarmClick;	disarmButton	= null; }

				if (armPanel != null)
				{
					armPanel.Children.Clear();
					if (UserControlCollection.Contains(armPanel))
						UserControlCollection.Remove(armPanel);
					armPanel = null;
				}

				uiAdded = false;
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: chart UI removal failed: " + ex.Message);
			}
		}

		private System.Windows.Controls.Button CreateButton(string caption, Brush background, System.Windows.RoutedEventHandler handler)
		{
			System.Windows.Controls.Button button = new System.Windows.Controls.Button
			{
				Content		= caption,
				MinWidth	= 94,
				Margin		= new System.Windows.Thickness(0, 0, 4, 0),
				Padding		= new System.Windows.Thickness(10, 4, 10, 4),
				FontWeight	= System.Windows.FontWeights.Bold,
				Foreground	= Brushes.White,
				Background	= background,
				BorderBrush	= Brushes.Black
			};
			button.Click += handler;
			return button;
		}

		// Handlers stay minimal and do no work on the UI thread: they post to the NinjaScript
		// thread, which is the only thread allowed to touch arm state, draw objects and ATM.
		// The ARM handlers read the Chart Trader ATM selection right here, on the UI thread that
		// owns those controls, and hand the name over with the click.
		private void OnArmLongClick(object sender, System.Windows.RoutedEventArgs e)
		{
			string template = ReadChartTraderAtmTemplate();
			PostToScriptThread(o => HandleArm(ArmDirection.Long, template));
		}

		private void OnArmShortClick(object sender, System.Windows.RoutedEventArgs e)
		{
			string template = ReadChartTraderAtmTemplate();
			PostToScriptThread(o => HandleArm(ArmDirection.Short, template));
		}

		private void OnDisarmClick(object sender, System.Windows.RoutedEventArgs e)
		{
			PostToScriptThread(o => HandleDisarm());
		}

		// Chart Trader's ATM selector is a WPF control owned by the chart window: UI THREAD ONLY.
		private string ReadChartTraderAtmTemplate()
		{
			try
			{
				if (ChartControl == null)
					return string.Empty;

				global::NinjaTrader.Gui.Chart.Chart chartWindow = System.Windows.Window.GetWindow(ChartControl.Parent) as global::NinjaTrader.Gui.Chart.Chart;

				if (chartWindow == null || chartWindow.ChartTrader == null)
					return string.Empty;

				global::NinjaTrader.NinjaScript.AtmStrategy atm = chartWindow.ChartTrader.AtmStrategy;

				if (atm == null || string.IsNullOrWhiteSpace(atm.Template))
					return string.Empty;

				return atm.Template.Trim();
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: could not read the Chart Trader ATM selection: " + ex.Message);
				return string.Empty;
			}
		}

		private void PostToScriptThread(Action<object> action)
		{
			try
			{
				TriggerCustomEvent(action, null);
			}
			catch (Exception ex)
			{
				// Deliberately not run inline: a click that cannot reach the script thread is a
				// visible failure, not a silent one.
				Print(Stamp() + " CandleCloseConfirm: click could not be dispatched to the script thread: " + ex.Message);
			}
		}

		#endregion

		#region ARM / DISARM

		private void HandleArm(ArmDirection direction, string atmTemplate)
		{
			try
			{
				if (State != State.Realtime)
					return;

				atmTemplate			= atmTemplate == null ? string.Empty : atmTemplate;
				atmTemplateForLog	= atmTemplate;

				string detail;

				// Interlocks: account flat, level line present, ATM template valid.
				if (!IsAccountFlat(out detail))
				{
					Reject("REJECT_NOT_FLAT", detail, direction);
					return;
				}

				HorizontalLine line = FindLevelLine();
				if (line == null || line.StartAnchor == null)
				{
					// Visible rejection AND the line is redrawn.
					Reject("REJECT_NO_LINE", "key-level line missing — redrawn at the current close", direction);
					EnsureChartObjects();
					return;
				}

				if (!IsAtmTemplateValid(atmTemplate, out detail))
				{
					Reject("REJECT_BAD_TEMPLATE", detail, direction);
					return;
				}

				double level		= line.StartAnchor.Price;
				DateTime evalTime	= ComputeExpectedEvalTime();

				lock (stateLock)
				{
					armedDirection		= direction;
					frozenLevel			= level;			// frozen here; dragging afterwards changes nothing.
					frozenAtmTemplate	= atmTemplate;		// the ATM selection freezes with the level.
					expectedEvalTime	= evalTime;
				}

				StartEvalTimer(evalTime);

				LogEvent(direction == ArmDirection.Long ? "ARMED_LONG" : "ARMED_SHORT",
					"level frozen at " + Num(level) + ", atm '" + atmTemplate + "', evaluates " + DescribeEvalTime(evalTime),
					direction, level, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

				UpdateStatus();
			}
			catch (Exception ex)
			{
				FailVisible("ARM failed: " + ex.Message, direction);
			}
		}

		private void HandleDisarm()
		{
			try
			{
				if (State != State.Realtime)
					return;

				ArmDirection was;
				double level;

				lock (stateLock)
				{
					was					= armedDirection;
					level				= frozenLevel;
					armedDirection		= ArmDirection.None;
					frozenLevel			= 0;
					frozenAtmTemplate	= string.Empty;
					expectedEvalTime	= DateTime.MinValue;
				}

				StopEvalTimer();

				lastResult = "MANUAL_DISARM";

				// The buttons stay live — re-arming inside the same forming candle is allowed.
				LogEvent("MANUAL_DISARM",
					was == ArmDirection.None ? "was idle; ARM available immediately" : "ARM available immediately",
					was, was == ArmDirection.None ? double.NaN : level,
					double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

				UpdateStatus();
			}
			catch (Exception ex)
			{
				FailVisible("DISARM failed: " + ex.Message, ArmDirection.None);
			}
		}

		private void Reject(string code, string detail, ArmDirection direction)
		{
			lastResult = code;
			LogEvent(code, detail, direction, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
			UpdateStatus();
		}

		// Never hold an armed state through an error, never act silently.
		private void FailVisible(string detail, ArmDirection direction)
		{
			lock (stateLock)
			{
				armedDirection		= ArmDirection.None;
				frozenLevel			= 0;
				frozenAtmTemplate	= string.Empty;
				expectedEvalTime	= DateTime.MinValue;
			}

			StopEvalTimer();

			lastResult = "FIRE_FAILED";

			try
			{
				LogEvent("FIRE_FAILED", detail, direction, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
				UpdateStatus();
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: " + detail + " (and reporting it failed: " + ex.Message + ")");
			}
		}

		#endregion

		#region Evaluation

		protected override void OnBarUpdate()
		{
			// Historical bars are fully inert: no arming, no evaluation, no drawing, no ATM.
			if (State != State.Realtime)
				return;

			ArmDirection direction;
			double level;
			string atmTemplate;

			lock (stateLock)
			{
				direction			= armedDirection;
				level				= frozenLevel;
				atmTemplate			= frozenAtmTemplate;

				// Every evaluation ends in disarm, fired or vetoed.
				armedDirection		= ArmDirection.None;
				frozenLevel			= 0;
				frozenAtmTemplate	= string.Empty;
				expectedEvalTime	= DateTime.MinValue;
			}

			if (direction == ArmDirection.None)
			{
				EnsureChartObjects();
				UpdateStatus();
				return;
			}

			// The tick-driven close claimed this cycle; the wall-clock timer stands down.
			StopEvalTimer();

			atmTemplateForLog = atmTemplate;

			try
			{
				EvaluateBar(direction, level, atmTemplate, Open[0], High[0], Low[0], Close[0], Time[0]);
			}
			catch (Exception ex)
			{
				FailVisible("evaluation failed: " + ex.Message, direction);
			}

			// Level-line self-heal after the evaluation, not before it: the level was frozen at ARM,
			// so the line cannot affect the decision, and the DrawObjects scan (plus any
			// redraw) stays off the close-to-order path.
			EnsureChartObjects();
		}

		// Rule math for one just-ended bar. Reached from OnBarUpdate at the tick-driven close, or
		// from the wall-clock timer with the same bar read off the underlying series — same values,
		// same checks, whichever event happens first.
		private void EvaluateBar(ArmDirection direction, double level, string atmTemplate, double open, double high, double low, double close, DateTime barTime)
		{
			double range	= high - low;

			// Raw double precision, no epsilon: the boundary semantics above are intentional.
			double threshold	= WickMaxPct / 100.0;
			double wick			= direction == ArmDirection.Long ? high - Math.Max(open, close) : Math.Min(open, close) - low;
			double wickRatio	= range > 0 ? wick / range : double.NaN;
			double wickPct		= range > 0 ? wickRatio * 100.0 : double.NaN;

			// Distance the bar pushed past the frozen level, on the side that matters.
			double spikeDist	= direction == ArmDirection.Long ? high - level : level - low;

			string code;
			string detail;

			// Check order and first-fail reporting: zero-range -> close -> wick -> spike.
			// The wick and spike checks are skipped when their parameter is 0 (check switched off).
			if (range <= 0)
			{
				code	= "VETO_ZERO_RANGE";
				detail	= "high == low (" + Num(high) + ")";
			}
			else if (direction == ArmDirection.Long ? close < level : close > level)
			{
				code	= "VETO_CLOSE";
				detail	= "close " + Num(close) + (direction == ArmDirection.Long ? " < level " : " > level ") + Num(level);
			}
			else if (WickMaxPct > 0 && wickRatio >= threshold)
			{
				code	= "VETO_WICK";
				detail	= Pct(wickPct) + " >= " + Num(WickMaxPct) + "%";
			}
			else if (SpikeVetoPoints > 0 && (direction == ArmDirection.Long ? high > level + SpikeVetoPoints : low < level - SpikeVetoPoints))
			{
				code	= "VETO_SPIKE";
				detail	= Num(spikeDist) + " > " + Num(SpikeVetoPoints) + " points past level";
			}
			else
			{
				code	= "ATM_FIRED";
				detail	= "all conditions passed";
			}

			// Telemetry must never delay a trading action, so on a pass the ATM submission
			// goes out before the Print/CSV work below. A synchronous submission failure is
			// reported after the ATM_FIRED row so the CSV keeps its row order.
			string fireError = null;

			if (code == "ATM_FIRED")
				fireError = Fire(direction, atmTemplate, level, open, high, low, close, wickPct, spikeDist, barTime);

			lastResult = code == "VETO_WICK"	? "VETO_WICK " + Pct(wickPct)
					   : code == "VETO_SPIKE"	? "VETO_SPIKE " + Num(spikeDist)
					   : code;

			// The log row always carries all computed values, whichever check failed first.
			LogEvent(code, detail, direction, level, open, high, low, close, wickPct, spikeDist, barTime);

			if (fireError != null)
				ReportFireFailure(direction, level, open, high, low, close, wickPct, spikeDist, barTime, fireError);

			UpdateStatus();
		}

		// ---- Wall-clock evaluation ----------------------------------------------------
		// NinjaTrader only raises the OnBarClose event when the first tick of the NEXT bar
		// arrives — on some feeds ~1 s after the boundary. At ARM a one-shot timer is scheduled
		// for expectedEvalTime + EvalGraceMs and the just-ended bar is then read straight off
		// Bars. The clock is sanity-checked at ARM (StartEvalTimer) and due-time is re-checked
		// before the claim (EvaluateFromTimer), so Market Replay or a bad clock stands the
		// timer down instead of firing into a forming bar. The tick-driven path stays live;
		// first claimant wins on the script thread.

		private void StartEvalTimer(DateTime evalTime)
		{
			if (EvalGraceMs <= 0 || evalTime == DateTime.MinValue)
				return;

			try
			{
				// The clock is only trusted when the remaining wait lands inside the armed
				// bar's own window. In Market Replay (historical bar dates against the real
				// clock — Core.Globals.Now is NOT playback-aware) or with a mis-set
				// clock/timezone this is wildly out of range; then the timer stands down and
				// the cycle runs tick-driven only.
				double waitMs	= (evalTime.AddMilliseconds(EvalGraceMs) - Core.Globals.Now).TotalMilliseconds;
				double windowMs	= (evalTime - Time[0]).TotalMilliseconds + EvalGraceMs + 5000;

				if (waitMs <= 0 || waitMs > windowMs)
				{
					Print(Stamp() + " CandleCloseConfirm: eval timer stands down (clock vs bar-time mismatch, wait would be " + Num(waitMs) + " ms — Market Replay or clock/timezone issue); tick-driven evaluation only for this cycle.");
					return;
				}

				if (evalTimer == null)
				{
					evalTimer			= new System.Timers.Timer();
					evalTimer.AutoReset	= false;
					evalTimer.Elapsed	+= OnEvalTimerElapsed;
				}

				evalTimer.Stop();
				evalTimer.Interval = waitMs;
				evalTimer.Start();
			}
			catch (Exception ex)
			{
				// Timer trouble degrades to the tick-driven path; it never blocks arming.
				Print(Stamp() + " CandleCloseConfirm: eval timer start failed (tick-driven evaluation still active): " + ex.Message);
			}
		}

		private void StopEvalTimer()
		{
			try
			{
				if (evalTimer != null)
					evalTimer.Stop();
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: eval timer stop failed: " + ex.Message);
			}
		}

		private void DisposeEvalTimer()
		{
			try
			{
				if (evalTimer != null)
				{
					evalTimer.Stop();
					evalTimer.Elapsed -= OnEvalTimerElapsed;
					evalTimer.Dispose();
					evalTimer = null;
				}
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: eval timer dispose failed: " + ex.Message);
			}
		}

		// Timer thread: nothing but a post; every check and all state work happen on the
		// script thread. AutoReset is false, so the timer stops itself after firing.
		private void OnEvalTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			try
			{
				TriggerCustomEvent(o => EvaluateFromTimer(), null);
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: eval timer tick failed: " + ex.Message);
			}
		}

		// Script thread. OnBarUpdate and this method run on the same thread, so exactly one of
		// them claims an armed cycle; the loser sees IDLE and leaves.
		private void EvaluateFromTimer()
		{
			try
			{
				if (State != State.Realtime)
					return;

				ArmDirection direction;
				double level;
				string atmTemplate;
				DateTime barTime;

				lock (stateLock)
				{
					direction	= armedDirection;
					level		= frozenLevel;
					atmTemplate	= frozenAtmTemplate;
					barTime		= expectedEvalTime;
				}

				if (direction == ArmDirection.None || barTime == DateTime.MinValue)
					return;

				// A stale callback (a disarm + re-arm racing a firing timer) must never touch
				// a bar that is still forming: re-check due-time on the clock that passed the
				// ARM-time sanity check. Not yet due means this post belongs to an older
				// cycle; the current cycle's own timer is still scheduled.
				if (Core.Globals.Now < barTime.AddMilliseconds(EvalGraceMs))
					return;

				// The just-ended bar sits at the tail of the series (NinjaTrader stamps time
				// bars with their end time as soon as they open). Not found means no tick ever
				// printed inside the armed bar — leave the cycle to the tick-driven path
				// rather than invent values.
				int index = -1;

				for (int i = Bars.Count - 1; i >= 0 && i >= Bars.Count - 3; i--)
				{
					if (Bars.GetTime(i) == barTime)
					{
						index = i;
						break;
					}
				}

				if (index < 0)
					return;

				lock (stateLock)
				{
					armedDirection		= ArmDirection.None;
					frozenLevel			= 0;
					frozenAtmTemplate	= string.Empty;
					expectedEvalTime	= DateTime.MinValue;
				}

				atmTemplateForLog = atmTemplate;

				try
				{
					EvaluateBar(direction, level, atmTemplate,
						Bars.GetOpen(index), Bars.GetHigh(index), Bars.GetLow(index), Bars.GetClose(index), barTime);
				}
				catch (Exception ex)
				{
					FailVisible("evaluation failed: " + ex.Message, direction);
				}

				EnsureChartObjects();
			}
			catch (Exception ex)
			{
				FailVisible("timer evaluation failed: " + ex.Message, ArmDirection.None);
			}
		}

		#endregion

		#region Fire

		// Submits the ATM order and nothing else — no Print/CSV work sits in front of the
		// submission (telemetry never delays a trading action). Returns null once the
		// submission is away, or the error text when AtmStrategyCreate throws synchronously;
		// the caller logs the ATM_FIRED row first, then the failure, keeping the CSV
		// row order. Async errors still arrive through the callback.
		private string Fire(ArmDirection direction, string atmTemplate, double level, double open, double high, double low, double close, double wickPct, double spikeDist, DateTime barTime)
		{
			string orderId	= GetAtmStrategyUniqueId();
			string atmId	= GetAtmStrategyUniqueId();

			lock (stateLock)
			{
				lastOrderId	= orderId;
				lastAtmId	= atmId;
			}

			OrderAction action = direction == ArmDirection.Long ? OrderAction.Buy : OrderAction.SellShort;

			try
			{
				AtmStrategyCreate(action, OrderType.Market, 0, 0, TimeInForce.Day, orderId, atmTemplate, atmId,
					(atmCallbackErrorCode, atmCallbackId) =>
					{
						// Only react to the callback for the ATM we just requested.
						if (atmCallbackId != atmId)
							return;

						if (atmCallbackErrorCode != ErrorCode.NoError)
							ReportFireFailure(direction, level, open, high, low, close, wickPct, spikeDist, barTime,
								"ATM create returned " + atmCallbackErrorCode + " (order id " + orderId + ", atm id " + atmId + ")");
					});

				return null;
			}
			catch (Exception ex)
			{
				// Never retry silently.
				return "AtmStrategyCreate threw: " + ex.Message;
			}
		}

		private void ReportFireFailure(ArmDirection direction, double level, double open, double high, double low, double close, double wickPct, double spikeDist, DateTime barTime, string detail)
		{
			lastResult = "FIRE_FAILED";

			// Output + CSV are safe from any thread; the status draw is posted to the script thread.
			LogEvent("FIRE_FAILED", detail, direction, level, open, high, low, close, wickPct, spikeDist, barTime);
			PostToScriptThread(o => UpdateStatus());
		}

		#endregion

		#region Interlocks

		// ATM-created positions do not register on the strategy's own Position, so the flat check
		// reads the account's positions. Only the strategy's own account counts: it is the
		// one AtmStrategyCreate submits on. The collection belongs to another thread:
		// snapshot under lock.
		private bool IsAccountFlat(out string detail)
		{
			detail = string.Empty;

			NinjaTrader.Cbi.Instrument instrument	= Instrument;
			NinjaTrader.Cbi.Account account			= Account;

			if (instrument == null)
			{
				detail = "chart instrument unavailable";
				return false;
			}

			if (account == null)
			{
				detail = "strategy account unavailable";
				return false;
			}

			string instrumentName = instrument.FullName;

			List<NinjaTrader.Cbi.Position> snapshot = new List<NinjaTrader.Cbi.Position>();

			lock (account.Positions)
			{
				foreach (NinjaTrader.Cbi.Position position in account.Positions)
					snapshot.Add(position);
			}

			foreach (NinjaTrader.Cbi.Position position in snapshot)
			{
				if (position == null || position.Instrument == null)
					continue;

				if (position.Instrument.FullName != instrumentName)
					continue;

				if (position.MarketPosition != MarketPosition.Flat && position.Quantity != 0)
				{
					detail = "account " + account.Name + " holds " + position.MarketPosition + " " + position.Quantity + " " + instrumentName;
					return false;
				}
			}

			return true;
		}

		// A bad or absent ATM template must fail at ARM, never at fire.
		private bool IsAtmTemplateValid(string name, out string detail)
		{
			detail = string.Empty;

			if (string.IsNullOrWhiteSpace(name))
			{
				detail = "no ATM template selected in Chart Trader — select one there first";
				return false;
			}

			try
			{
				string path = Path.Combine(Core.Globals.UserDataDir, "templates", "AtmStrategy", name + ".xml");

				if (!File.Exists(path))
				{
					detail = "ATM template not found: " + path;
					return false;
				}

				return true;
			}
			catch (Exception ex)
			{
				detail = "ATM template check failed for '" + name + "': " + ex.Message;
				return false;
			}
		}

		#endregion

		#region Level line and status

		// The ONLY place the key-level line is located. the fallback in the compile notes is a change to this method.
		private HorizontalLine FindLevelLine()
		{
			try
			{
				lock (DrawObjects)
				{
					foreach (DrawingTool drawingTool in DrawObjects)
					{
						HorizontalLine line = drawingTool as HorizontalLine;

						if (line != null && line.Tag == LevelTag)
							return line;
					}
				}
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: level line lookup failed: " + ex.Message);
			}

			return null;
		}

		private void EnsureChartObjects()
		{
			if (State != State.Realtime || CurrentBar < 0)
				return;

			try
			{
				// Only draw when it is missing — redrawing would snap a dragged line back.
				if (FindLevelLine() == null)
				{
					HorizontalLine line = Draw.HorizontalLine(this, LevelTag, Close[0], GetLevelBrush(),
						DashStyleHelper.Solid, LevelLineWidth);

					if (line != null)
						line.IsLocked = false;		// user-draggable
				}
			}
			catch (Exception ex)
			{
				Print(Stamp() + " CandleCloseConfirm: level line draw failed: " + ex.Message);
			}
		}

		private Brush GetLevelBrush()
		{
			try
			{
				Brush brush = LevelLineColor;

				if (brush == null)
					return Brushes.DodgerBlue;

				if (brush.IsFrozen)
					return brush;

				Brush clone = brush.Clone();
				clone.Freeze();
				return clone;
			}
			catch
			{
				return Brushes.DodgerBlue;
			}
		}

		private void UpdateStatus()
		{
			if (State != State.Realtime || CurrentBar < 0)
				return;

			try
			{
				ArmDirection direction;
				double level;
				DateTime evalTime;
				string atmTemplate;

				lock (stateLock)
				{
					direction	= armedDirection;
					level		= frozenLevel;
					evalTime	= expectedEvalTime;
					atmTemplate	= frozenAtmTemplate;
				}

				StringBuilder text = new StringBuilder("CandleCloseConfirm  ");

				if (direction == ArmDirection.None)
				{
					text.Append("IDLE");
					text.Append("  |  last: ").Append(lastResult);
					// Idle: no ATM name is shown, because the selection is only read at ARM and a
					// cached one could be stale by the time it is read.
					text.Append("  |  atm: read from Chart Trader at ARM");
				}
				else
				{
					text.Append("ARMED ").Append(direction == ArmDirection.Long ? "LONG" : "SHORT");
					text.Append("  |  level ").Append(Num(level));
					text.Append("  |  evaluates ").Append(DescribeEvalTime(evalTime));
					text.Append("  |  atm: ").Append(string.IsNullOrWhiteSpace(atmTemplate) ? "<none>" : atmTemplate);
				}

				if (telemetryWarning)
					text.Append("  |  LOG WARNING — see Output window");

				string status = text.ToString();

				// Idle bars rebuild the same text every bar; the WPF redraw only happens when
				// the text changed or the label was deleted off the chart.
				if (status == lastStatusText)
				{
					bool statusPresent;

					lock (DrawObjects)
					{
						statusPresent = DrawObjects[StatusTag] != null;
					}

					if (statusPresent)
						return;
				}

				if (statusFont == null)
					statusFont = new SimpleFont("Arial", 13);

				// Black text on a plain white plate: the state-coloured text was unreadable against
				// a light chart background. The state itself is named in the text, not the colour.
				Draw.TextFixed(this, StatusTag, status, TextPosition.BottomLeft,
					Brushes.Black, statusFont, Brushes.Transparent, Brushes.White, 70);

				// Cached only after a successful draw so a failed draw is retried on the next call.
				lastStatusText = status;
			}
			catch (Exception ex)
			{
				// Status is display, not the trading path: warn, never block.
				Print(Stamp() + " CandleCloseConfirm: status draw failed: " + ex.Message);
			}
		}

		// The status line always names which bar-close will be evaluated.
		private DateTime ComputeExpectedEvalTime()
		{
			try
			{
				if (CurrentBar < 0 || BarsPeriod == null || BarsPeriod.Value <= 0)
					return DateTime.MinValue;

				DateTime lastClose = Time[0];

				if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second)
					return lastClose.AddSeconds(BarsPeriod.Value);

				if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
					return lastClose.AddMinutes(BarsPeriod.Value);

				return DateTime.MinValue;
			}
			catch
			{
				return DateTime.MinValue;
			}
		}

		private static string DescribeEvalTime(DateTime evalTime)
		{
			return evalTime == DateTime.MinValue ? "the next bar close" : "bar close " + evalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
		}

		#endregion

		#region Telemetry

		private static string DefaultLogFolder()
		{
			try
			{
				return Path.Combine(Core.Globals.UserDataDir, "CandleCloseConfirm");
			}
			catch
			{
				return "CandleCloseConfirm";
			}
		}

		private void LogEvent(string code, string detail, ArmDirection direction, double level,
			double open, double high, double low, double close, double wickPct, double spikeDist,
			DateTime evalBarTime = default(DateTime))
		{
			string directionText	= direction == ArmDirection.Long ? "LONG" : direction == ArmDirection.Short ? "SHORT" : string.Empty;
			string barTime			= string.Empty;

			// Eval-path rows name the evaluated bar explicitly: the wall-clock timer can run before
			// NinjaTrader has closed the bar, when Time[0] still points at the previous one.
			try
			{
				if (evalBarTime != DateTime.MinValue)
					barTime = evalBarTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
				else if (CurrentBar >= 0)
					barTime = Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
			}
			catch
			{
				barTime = string.Empty;
			}

			// Output window, unconditionally.
			Print(Stamp() + " CandleCloseConfirm " + code
				+ (string.IsNullOrEmpty(directionText) ? string.Empty : " " + directionText)
				+ (string.IsNullOrEmpty(detail) ? string.Empty : " — " + detail));

			StringBuilder row = new StringBuilder();
			row.Append(Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))).Append(',');
			row.Append(Csv(barTime)).Append(',');
			row.Append(Csv(code)).Append(',');
			row.Append(Csv(detail)).Append(',');
			row.Append(Csv(directionText)).Append(',');
			row.Append(Csv(Num(level))).Append(',');
			row.Append(Csv(Num(open))).Append(',');
			row.Append(Csv(Num(high))).Append(',');
			row.Append(Csv(Num(low))).Append(',');
			row.Append(Csv(Num(close))).Append(',');
			row.Append(Csv(Num(wickPct))).Append(',');
			row.Append(Csv(Num(spikeDist))).Append(',');
			row.Append(Csv(atmTemplateForLog));

			WriteCsv(row.ToString());
		}

		private void WriteCsv(string row)
		{
			const string header = "timestamp,bar_time,event,detail,direction,level,open,high,low,close,wick_pct,spike_dist,template";

			try
			{
				lock (logLock)
				{
					string day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

					if (csvFilePath == null || day != csvFileDay)
					{
						string folder = LogFilePath;

						if (string.IsNullOrWhiteSpace(folder))
							folder = DefaultLogFolder();

						Directory.CreateDirectory(folder);

						csvFilePath	= Path.Combine(folder, "CandleCloseConfirm_" + day + ".csv");
						csvFileDay	= day;
					}

					// Deliberately re-checked every row: "header row if absent" must
					// hold even if the day's file is deleted mid-session.
					FileInfo info = new FileInfo(csvFilePath);
					bool needsHeader = !info.Exists || info.Length == 0;

					using (StreamWriter writer = new StreamWriter(csvFilePath, true, Encoding.UTF8))
					{
						if (needsHeader)
							writer.WriteLine(header);

						writer.WriteLine(row);
					}
				}

				telemetryWarning = false;
			}
			catch (Exception ex)
			{
				// Telemetry failure is warn-only and must never block a trading action.
				// The path cache is dropped so the next attempt re-resolves and recreates the
				// folder, exactly like a first write.
				lock (logLock)
				{
					csvFilePath	= null;
					csvFileDay	= null;
				}

				telemetryWarning = true;
				Print(Stamp() + " CandleCloseConfirm: TELEMETRY WRITE FAILED (trading actions continue): " + ex.Message);
			}
		}

		#endregion

		#region Formatting helpers

		private static string Stamp()
		{
			return DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
		}

		private static string Num(double value)
		{
			return double.IsNaN(value) || double.IsInfinity(value) ? string.Empty : value.ToString("0.########", CultureInfo.InvariantCulture);
		}

		private static string Pct(double value)
		{
			return double.IsNaN(value) || double.IsInfinity(value) ? "n/a" : value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
		}

		private static string Csv(string value)
		{
			if (string.IsNullOrEmpty(value))
				return string.Empty;

			if (value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
				return "\"" + value.Replace("\"", "\"\"") + "\"";

			return value;
		}

		#endregion
	}
}
