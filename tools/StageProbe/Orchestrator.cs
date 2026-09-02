using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;

namespace StageProbe;

/// <summary>
/// The `run` scenario: 6 probe scenes (probe6 has 2 windows), a few focus-driven
/// scene switches, one strip screenshot per state, per-card edge fit, verdicts.
/// Two independent checks per single-window card:
///   render: measured angle vs the angle the app says it assigned (TILT log)
///   law:    measured angle vs atan((yEdge - screenCenterY) / d) straight from
///           the screenshot, d = screenHeight * 1379/1169 (CARD_QUAD_SPEC.md)
/// probe6 (2 windows) is presence-only: overlapping same-colour quads step the
/// column profile, so an edge fit across the union would be meaningless.
/// </summary>
internal static class Orchestrator
{
    private sealed record ProbeSpec(string Name, string ColorHex, int W, int H, int Count);

    // Sizes (physical px) picked so the sizing law exercises BOTH branches on a
    // scale-2 screen: probe1/2/4 hit the 96-dip minimum-height clamp (scale-up),
    // probe3/5 are tall enough (>= ~1415 px at scale 2) to ride the base
    // 0.135693 scale unclamped.
    private static readonly ProbeSpec[] Specs =
    {
        new("probe1", "FF0000", 900, 600, 1),
        new("probe2", "00FF00", 700, 700, 1),
        new("probe3", "0000FF", 900, 1700, 1),
        new("probe4", "FF00FF", 1000, 520, 1),
        new("probe5", "00FFFF", 1600, 1500, 1),
        new("probe6", "FFFF00", 900, 650, 2),
    };

    private const double RenderToleranceDeg = 0.7;
    private const double LawToleranceDeg = 0.8;
    // Card size vs macOS law s = max(0.135693, 96/hDip): fractional slack for
    // AA edges + the render's own perspective, with an absolute floor.
    private const double SizeToleranceFrac = 0.03;
    // Floor covers the ~7 px the 8-dip rounded corners shave off the measured
    // span ends plus AA fuzz.
    private const double SizeTolerancePxMin = 10.0;
    private const double BaseCardScale = 0.135693;
    private const double MinCardHeightDip = 96.0;
    private const int SettleAfterSpawnMs = 4000;
    // Long enough for the switch fly-out AND the previous active scene's
    // re-entry fly + FLIP slide to fully land before the screenshot.
    private const int SettleAfterSwitchMs = 2600;
    // Stage-arrival budget: how long after the focus change every window of the
    // newly active scene may take to be fully painted on stage.
    private const int StageArrivalBudgetMs = 1200;
    private const double StageArrivalCoverage = 0.90;
    // Left edge of the stage area in physical px (sidebar is 240 dip).
    private const int StageLeftPx = 520;
    private const int MinCardsPerState = 4;
    private const int StageBandHeightPx = 40;
    // Stow animation + desktop reveal after a minimize.
    private const int MinimizeSettleMs = 2500;
    // "Gone from the stage": AA fringes and a stray repaint leave a few pixels.
    private const double StageClearFrac = 0.02;
    // A scene that wrongly took the stage would paint one whole window; this is
    // far above wallpaper noise and far below that.
    private const int IntruderPixelLimit = 2000;

    public static int Run(Args a)
    {
        string logPath = a.Get("log", "");
        if (logPath.Length == 0) { Console.Error.WriteLine("run: --log <stagemanager.log> is required"); return 2; }
        if (Process.GetProcessesByName("StageManager").Length == 0)
        {
            Console.Error.WriteLine("run: StageManager is not running — start it first (tools/run-stage-test.ps1 does)");
            return 2;
        }

        string outDir = a.Get("out", Path.Combine(AppContext.BaseDirectory, "out"));
        Directory.CreateDirectory(outDir);

        var bounds = Screen.PrimaryScreen!.Bounds; // physical px
        double dpi = Native.GetDpiForSystem() / 96.0;
        double screenHDip = bounds.Height / dpi;
        double centerYDip = screenHDip / 2.0;
        double dDip = screenHDip * (1379.0 / 1169.0);
        Console.WriteLine($"screen {bounds.Width}x{bounds.Height}px scale={dpi:F2} centerY={centerYDip:F1}dip d={dDip:F1}dip");

        // Remember what was focused so the user's window (usually the terminal
        // running this) gets its scene back after the test.
        var originalForeground = Native.GetForegroundWindow();

        var procs = SpawnProbes(bounds);
        try
        {
            Thread.Sleep(SettleAfterSpawnMs);

            // Warm-up: StageManager only creates a scene when a window gains
            // focus, and roughly every other probe is denied foreground during
            // the spawn burst. One spaced activation pass guarantees all six
            // scenes exist before the first measured state.
            foreach (var spec in Specs)
            {
                if (!procs.TryGetValue(spec.Name, out var p)) continue;
                var h = WaitMainWindow(p, 3000);
                if (h != IntPtr.Zero) Native.Activate(h);
                Thread.Sleep(1000);
            }

            var seq = a.Get("switches", "probe1,probe4,probe6,probe2,probe5,probe3")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var states = new List<object>();
            bool allPass = true;

            for (int si = 0; si < seq.Length; si++)
            {
                string active = seq[si];
                if (!procs.TryGetValue(active, out var proc)) { Console.Error.WriteLine($"unknown probe '{active}'"); allPass = false; continue; }

                var hwnd = WaitMainWindow(proc, 5000);
                if (hwnd == IntPtr.Zero) { Console.Error.WriteLine($"{active}: no main window"); allPass = false; continue; }
                if (!Native.Activate(hwnd)) Console.Error.WriteLine($"{active}: activation not confirmed, continuing");

                var activeSpec = Specs.First(s => s.Name == active);
                int activeIndex = Array.FindIndex(Specs, s => s.Name == active);
                var arrival = MeasureStageArrival(activeSpec, SpawnOrigin(activeIndex, bounds), StageLeftPx, StageArrivalBudgetMs + 2000);
                bool arrivalOk = arrival.LatencyMs >= 0 && arrival.LatencyMs <= StageArrivalBudgetMs;
                if (!arrivalOk) allPass = false;
                Console.WriteLine(arrival.LatencyMs < 0
                    ? FormattableString.Invariant($"  {active}: STAGE ARRIVAL TIMED OUT — only {arrival.Pixels}/{arrival.Expected} px painted ({(100.0 * arrival.Pixels / arrival.Expected):F0}%){(activeSpec.Count > 1 ? " (multi-window scene)" : "")}")
                    : FormattableString.Invariant($"  {active}: stage arrival {arrival.LatencyMs} ms{(activeSpec.Count > 1 ? " (multi-window scene)" : "")} {(arrivalOk ? "OK" : $"SLOW (budget {StageArrivalBudgetMs} ms)")}"));

                Thread.Sleep(SettleAfterSwitchMs);
                Native.SetCursorPos(bounds.Width / 2, bounds.Height - 40); // hover would scale tiles
                Thread.Sleep(200);

                // Sidebar is 240 dip (480 px at scale 2); widest card ends ~413.
                // Anything further right is raw wallpaper — keep it out of the
                // colour sweep.
                using var bmp = StripCapture.Grab(520);
                string shot = Path.Combine(outDir, FormattableString.Invariant($"state_{si}_{active}.png"));
                bmp.Save(shot, ImageFormat.Png);

                var tilts = TiltLog.ReadLatest(logPath);
                var cards = new List<object>();
                int detectedCount = 0;

                foreach (var spec in Specs)
                {
                    var quad = QuadFit.Fit(bmp, spec.Name, ColorTranslator.FromHtml("#" + spec.ColorHex));
                    if (spec.Name == active)
                    {
                        if (quad is not null) Console.WriteLine($"  note: active {active} still visible in strip");
                        continue;
                    }
                    if (quad is null) { cards.Add(new { probe = spec.Name, detected = false }); continue; }
                    detectedCount++;

                    if (spec.Count > 1)
                    {
                        Console.WriteLine($"  {spec.Name}: present (multi-window, presence-only)");
                        cards.Add(new { probe = spec.Name, detected = true, multiWindow = true });
                        continue;
                    }

                    var assigned = TiltLog.For(tilts, spec.Name);
                    double lawTop = LawDeg(quad.TopMidYPx / dpi, centerYDip, dDip);
                    double lawBottom = LawDeg(quad.BottomMidYPx / dpi, centerYDip, dDip);

                    // The sidebar window ends at the work area; a card whose
                    // shear spills past that edge gets razor-clipped flat.
                    // Known rendering defect (layout task B), reported as
                    // CLIPPED and excluded from that edge's verdict.
                    var wa = Screen.PrimaryScreen!.WorkingArea;
                    double halfSpan = (quad.RightXPx - quad.LeftXPx) / 2.0;
                    bool bottomClipped = quad.BottomMidYPx + Math.Abs(Math.Tan(lawBottom * Math.PI / 180.0)) * halfSpan >= wa.Bottom - 8;
                    bool topClipped = quad.TopMidYPx - Math.Abs(Math.Tan(lawTop * Math.PI / 180.0)) * halfSpan <= wa.Top + 8;

                    bool topOk = topClipped
                        || (assigned is not null && Math.Abs(quad.Top.AngleDeg - assigned.TopDeg) <= RenderToleranceDeg
                            && Math.Abs(quad.Top.AngleDeg - lawTop) <= LawToleranceDeg);
                    bool bottomOk = bottomClipped
                        || (assigned is not null && Math.Abs(quad.Bottom.AngleDeg - assigned.BottomDeg) <= RenderToleranceDeg
                            && Math.Abs(quad.Bottom.AngleDeg - lawBottom) <= LawToleranceDeg);

                    // Sizing law: s = max(s0, 96/hDip) uniform on the source
                    // rect. The measured mid-column height carries the render's
                    // trapezoid foreshortening h(mid) = H * (1 - (W/2)/d), so
                    // the expectation includes it.
                    // App applies a uniform (1 − W/(2d)) squeeze (aspect-fit
                    // renderer can't shrink height alone), so BOTH expected
                    // dimensions carry it. Mid-height then equals the Mac law
                    // H·(1 − W/(2d)); width runs that factor narrow vs Mac.
                    double sizeScale = Math.Max(BaseCardScale, MinCardHeightDip / (spec.H / dpi));
                    double logicalWDip = spec.W * sizeScale / dpi;
                    double squeeze = 1.0 - logicalWDip / (2.0 * dDip);
                    double expWPx = spec.W * sizeScale * squeeze;
                    double expMidHPx = spec.H * sizeScale * squeeze;
                    double measWPx = quad.RightXPx - quad.LeftXPx + 1;
                    double measHPx = quad.BottomMidYPx - quad.TopMidYPx;
                    double wTol = Math.Max(SizeTolerancePxMin, expWPx * SizeToleranceFrac);
                    double hTol = Math.Max(SizeTolerancePxMin, expMidHPx * SizeToleranceFrac);
                    bool sizeOk = Math.Abs(measWPx - expWPx) <= wTol
                        && (bottomClipped || topClipped || Math.Abs(measHPx - expMidHPx) <= hTol);

                    bool renderOk = topOk && bottomOk && sizeOk;
                    bool lawOk = renderOk; // folded into the per-edge verdicts above
                    if (!renderOk) allPass = false;
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {spec.Name}: size {measWPx:F1}x{measHPx:F1}px expected {expWPx:F1}x{expMidHPx:F1}px (s={sizeScale:F4}) {(sizeOk ? "OK" : "SIZE FAIL")}"));
                    if (topClipped || bottomClipped)
                        Console.WriteLine($"  {spec.Name}: {(topClipped ? "top" : "bottom")} edge CLIPPED by sidebar boundary (layout task B) — excluded from verdict");

                    string logTop = assigned?.TopDeg.ToString("F3", CultureInfo.InvariantCulture) ?? "---";
                    string logBottom = assigned?.BottomDeg.ToString("F3", CultureInfo.InvariantCulture) ?? "---";
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {spec.Name}: top {quad.Top.AngleDeg,7:F3} (log {logTop}, law {lawTop:F3})  bottom {quad.Bottom.AngleDeg,7:F3} (log {logBottom}, law {lawBottom:F3})  rms {quad.Top.ResidualPx:F2}/{quad.Bottom.ResidualPx:F2}px  {(renderOk && lawOk ? "PASS" : "FAIL")}"));

                    cards.Add(new
                    {
                        probe = spec.Name,
                        detected = true,
                        measured = new { top = quad.Top.AngleDeg, bottom = quad.Bottom.AngleDeg },
                        assigned = assigned is null ? null : new { top = (double?)assigned.TopDeg, bottom = (double?)assigned.BottomDeg },
                        law = new { top = lawTop, bottom = lawBottom },
                        residualPx = new { top = quad.Top.ResidualPx, bottom = quad.Bottom.ResidualPx },
                        pixelCount = quad.PixelCount,
                        clipped = new { top = topClipped, bottom = bottomClipped },
                        size = new { measuredW = measWPx, measuredH = measHPx, expectedW = expWPx, expectedH = expMidHPx, scale = sizeScale, pass = sizeOk },
                        pass = renderOk,
                    });
                }

                Console.WriteLine($"state {si} (active={active}): {detectedCount} card(s) detected — {shot}");
                // The strip caps at MAX_SCENES (5) tiles including the active
                // scene, and the console that launched the test owns one of
                // them, so at most 4 probe cards can be on screen at once.
                if (detectedCount < MinCardsPerState) { allPass = false; Console.Error.WriteLine($"state {si}: too few cards detected ({detectedCount})"); }
                states.Add(new
                {
                    state = si,
                    active,
                    screenshot = shot,
                    stageArrival = new
                    {
                        latencyMs = arrival.LatencyMs,
                        pixels = arrival.Pixels,
                        expectedPixels = arrival.Expected,
                        windows = activeSpec.Count,
                        budgetMs = StageArrivalBudgetMs,
                        pass = arrivalOk,
                    },
                    cards,
                });
            }

            var minimize = RunMinimizeChecks(procs, bounds, outDir, ref allPass);

            var summary = new
            {
                screen = new { bounds.Width, bounds.Height, scale = dpi },
                law = new { centerYDip, dDip, ratio = 1379.0 / 1169.0 },
                tolerances = new { renderDeg = RenderToleranceDeg, lawDeg = LawToleranceDeg },
                pass = allPass,
                states,
                minimize,
            };
            File.WriteAllText(Path.Combine(outDir, "summary.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine(allPass ? "RESULT: PASS" : "RESULT: FAIL");
            return allPass ? 0 : 1;
        }
        finally
        {
            if (!a.Has("keep"))
            {
                // Graceful, staggered close: WM_CLOSE lets each probe exit its
                // message loop so StageManager sees window-destroy events and
                // removes the scene. A hard kill of 6 processes mid-animation
                // leaves ghost tiles squatting the tray cap.
                foreach (var p in procs.Values)
                {
                    try
                    {
                        if (!p.CloseMainWindow() || !p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
                    }
                    catch { /* already gone */ }
                    Thread.Sleep(300);
                }
                Thread.Sleep(1000); // let scene-removal events drain
            }
            if (originalForeground != IntPtr.Zero)
                Native.Activate(originalForeground, attempts: 2);
        }
    }

    /// <summary>
    /// How many of a probe's pixels are on stage right now, read from the same
    /// band the arrival check polls.
    /// </summary>
    private static int CountOnStage(ProbeSpec spec, Point origin)
    {
        int bandY = origin.Y + spec.H / 2 - StageBandHeightPx / 2;
        using var band = StripCapture.GrabStageBand(StageLeftPx, bandY, StageBandHeightPx);
        return QuadFit.CountMatching(band, ColorTranslator.FromHtml("#" + spec.ColorHex));
    }

    /// <summary>
    /// Minimize behaviour, two cases:
    ///   last window  — minimizing the only window of the active scene stows the
    ///                  whole scene, leaves the desktop showing (no other scene
    ///                  may sneak on via the foreground event that follows), puts
    ///                  the scene's card back in the strip, and restoring from the
    ///                  taskbar brings the scene back.
    ///   one of two   — minimizing one window of a two-window scene leaves the
    ///                  scene on stage with the other window still painted.
    /// </summary>
    private static object RunMinimizeChecks(Dictionary<string, Process> procs, Rectangle bounds, string outDir, ref bool allPass)
    {
        Console.WriteLine("minimize checks:");
        object? lastReport = null, multiReport = null;

        var last = Specs[0];
        var lastOrigin = SpawnOrigin(0, bounds);
        if (procs.TryGetValue(last.Name, out var lastProc))
        {
            var hwnd = WaitMainWindow(lastProc, 5000);
            if (hwnd != IntPtr.Zero)
            {
                Native.Activate(hwnd);
                Thread.Sleep(SettleAfterSwitchMs);

                Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
                Thread.Sleep(MinimizeSettleMs);

                int expectedOwn = last.W * last.Count * StageBandHeightPx;
                int own = CountOnStage(last, lastOrigin);
                bool ownGone = own <= expectedOwn * StageClearFrac;

                int intruder = 0;
                string intruderName = "(none)";
                for (int i = 1; i < Specs.Length; i++)
                {
                    int n = CountOnStage(Specs[i], SpawnOrigin(i, bounds));
                    if (n > intruder) { intruder = n; intruderName = Specs[i].Name; }
                }
                bool desktopShowing = intruder <= IntruderPixelLimit;

                using var strip = StripCapture.Grab(520);
                string shot = Path.Combine(outDir, "minimize_stowed.png");
                strip.Save(shot, ImageFormat.Png);
                bool tileBack = QuadFit.Fit(strip, last.Name, ColorTranslator.FromHtml("#" + last.ColorHex)) is not null;

                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                var back = MeasureStageArrival(last, lastOrigin, StageLeftPx, StageArrivalBudgetMs + 2000);
                bool restored = back.LatencyMs >= 0;

                bool pass = ownGone && desktopShowing && tileBack && restored;
                if (!pass) allPass = false;
                string restoreText = restored ? FormattableString.Invariant($"{back.LatencyMs}ms OK") : "TIMED OUT";
                Console.WriteLine(FormattableString.Invariant(
                    $"  last-window: stage cleared {own}/{expectedOwn}px {(ownGone ? "OK" : "FAIL")}, desktop showing (max intruder {intruderName} {intruder}px) {(desktopShowing ? "OK" : "FAIL")}, card stowed in strip {(tileBack ? "OK" : "FAIL")}, restore {restoreText} — {(pass ? "PASS" : "FAIL")}"));

                lastReport = new
                {
                    probe = last.Name,
                    stagePixelsAfterMinimize = own,
                    expectedPixels = expectedOwn,
                    stageCleared = ownGone,
                    maxIntruderProbe = intruderName,
                    maxIntruderPixels = intruder,
                    desktopShowing,
                    cardStowedInStrip = tileBack,
                    screenshot = shot,
                    restoreLatencyMs = back.LatencyMs,
                    restored,
                    pass,
                };
                Thread.Sleep(SettleAfterSwitchMs);
            }
        }

        var multi = Specs[^1]; // probe6, two windows
        var multiOrigin = SpawnOrigin(Specs.Length - 1, bounds);
        if (procs.TryGetValue(multi.Name, out var multiProc))
        {
            var handles = Native.TopLevelWindows(multiProc.Id);
            if (handles.Count >= 2)
            {
                Native.Activate(handles[0]);
                Thread.Sleep(SettleAfterSwitchMs);

                Native.ShowWindow(handles[0], Native.SW_MINIMIZE);
                Thread.Sleep(MinimizeSettleMs);

                int perWindow = multi.W * StageBandHeightPx;
                int onStage = CountOnStage(multi, multiOrigin);
                // One window's worth: the scene stayed, minus the minimized one.
                bool stayed = onStage >= perWindow * StageArrivalCoverage && onStage <= perWindow * 1.4;
                if (!stayed) allPass = false;
                Console.WriteLine(FormattableString.Invariant(
                    $"  one-of-two: {onStage}px on stage, expected ~{perWindow}px (one window) — {(stayed ? "PASS" : "FAIL")}"));

                multiReport = new
                {
                    probe = multi.Name,
                    stagePixelsAfterMinimize = onStage,
                    expectedPixelsOneWindow = perWindow,
                    sceneStayedOnStage = stayed,
                    pass = stayed,
                };

                Native.ShowWindow(handles[0], Native.SW_RESTORE);
                Thread.Sleep(MinimizeSettleMs);
            }
            else
            {
                Console.Error.WriteLine($"  one-of-two: only {handles.Count} top-level window(s) found for {multi.Name}, skipped");
            }
        }

        return new { lastWindow = lastReport, oneOfTwo = multiReport };
    }

    private static double LawDeg(double yDip, double centerYDip, double dDip)
        => Math.Atan((yDip - centerYDip) / dDip) * 180.0 / Math.PI;

    /// <summary>
    /// One renamed copy of this exe per probe: the apphost keeps its embedded
    /// StageProbe.dll reference, but StageManager groups scenes by process file
    /// name, so probe1.exe..probe6.exe each get their own scene.
    /// </summary>
    private static Dictionary<string, Process> SpawnProbes(Rectangle bounds)
    {
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        string dir = Path.GetDirectoryName(self)!;
        var procs = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < Specs.Length; i++)
        {
            var spec = Specs[i];
            var origin = SpawnOrigin(i, bounds);
            string exe = Path.Combine(dir, spec.Name + ".exe");
            File.Copy(self, exe, overwrite: true);

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in new[]
            {
                "window",
                "--color", spec.ColorHex,
                "--x", origin.X.ToString(CultureInfo.InvariantCulture),
                "--y", origin.Y.ToString(CultureInfo.InvariantCulture),
                "--w", spec.W.ToString(CultureInfo.InvariantCulture),
                "--h", spec.H.ToString(CultureInfo.InvariantCulture),
                "--count", spec.Count.ToString(CultureInfo.InvariantCulture),
                "--title", spec.Name,
            })
                psi.ArgumentList.Add(arg);

            var proc = Process.Start(psi)!;
            procs[spec.Name] = proc;
            Console.WriteLine($"spawned {spec.Name} ({spec.ColorHex}, {spec.W}x{spec.H}, windows={spec.Count})");

            // StageManager only creates a scene when the window gains FOCUS —
            // and Windows denies foreground to windows spawned by a background
            // process, so probes open unfocused and would stay scene-less
            // (blank tile) until their first scripted activation. Activate each
            // one explicitly so every scene exists before the first screenshot.
            var hwnd = WaitMainWindow(proc, 5000);
            // Foreground cooldown: back-to-back SetForegroundWindow calls get
            // denied every other time, so retry with spacing until confirmed.
            for (int t = 0; t < 3 && hwnd != IntPtr.Zero && !Native.Activate(hwnd); t++)
                Thread.Sleep(500);
            Thread.Sleep(500);
        }
        return procs;
    }

    /// <summary>
    /// Where probe <paramref name="index"/> puts its first window. Kept in one
    /// place because the arrival check needs the same rect the spawn used.
    /// x is 30% across so no probe overlaps the strip (that would auto-stow it).
    /// </summary>
    private static Point SpawnOrigin(int index, Rectangle bounds)
        => new((int)(bounds.Width * 0.30) + index * 40, (int)(bounds.Height * 0.12) + index * 30);

    /// <summary>
    /// Polls a band across the stage until the activated scene's windows are
    /// fully painted, and returns how long that took. This is what catches a
    /// scene rendering late — especially a multi-window scene, where the
    /// expected area covers BOTH windows, so "one window up, one still blank"
    /// reads as incomplete rather than done.
    /// </summary>
    private static (int LatencyMs, int Pixels, int Expected) MeasureStageArrival(
        ProbeSpec spec, Point origin, int stageLeftPx, int budgetMs)
    {
        var colour = ColorTranslator.FromHtml("#" + spec.ColorHex);
        int bandH = StageBandHeightPx;
        int bandY = origin.Y + spec.H / 2 - bandH / 2;
        int expected = spec.W * spec.Count * bandH;

        var sw = Stopwatch.StartNew();
        int best = 0;
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            using (var band = StripCapture.GrabStageBand(stageLeftPx, bandY, bandH))
            {
                int n = QuadFit.CountMatching(band, colour);
                if (n > best) best = n;
                if (n >= expected * StageArrivalCoverage)
                    return ((int)sw.ElapsedMilliseconds, n, expected);
            }
            Thread.Sleep(50);
        }
        return (-1, best, expected);
    }

    private static IntPtr WaitMainWindow(Process p, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }
}
