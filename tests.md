## Overview

Design notes for a behaviour smoke test: drive a realistic workflow against a running
StageManager, watch `stagemanager.log`, and fail when something blows up. Parked — nothing
below is implemented yet.

Not a unit test suite and not a pixel test. The assertions are "did the app survive this
sequence" and "did the windows end up where they should", read from the log and from
`GetWindowRect`.

## What already exists

`tools/TiltProbe` is the harness to build on. It is the Windows twin of the macOS StageRun
probe, named after the first thing it measured. Untracked, so `tools/` has to be present
locally.

```
tools/run-tilt-test.ps1     builds app + probe, restarts StageManager, runs the scenario
tools/TiltProbe/
    Program.cs              verb dispatch: `window` | `run`
    ProbeWindow.cs          solid-colour borderless probe form, keeps WS_SYSMENU so
                            StageManager's CanLayout filter tracks it
    Orchestrator.cs         the `run` scenario: spawn 6 probes, switch, screenshot, verify
    StripCapture.cs         screenshot of the sidebar strip
    QuadFit.cs              sub-pixel edge fit of a probe's card in the strip
    TiltLog.cs              parses the app's [TILT] lines
    Native.cs               P/Invokes
```

`run` already spawns 6 probe scenes (probe6 has 2 windows), drives 6 scene switches, and
verifies card edge angles against both the app's own log and the macOS position law.
Report at `tools/TiltProbe/out/summary.json`, exit code propagated.

Reusable as-is: probe spawning, graceful `WM_CLOSE` cleanup, foreground save/restore, strip
capture, quad fitting, the JSON report shape.

Missing P/Invokes: `SendInput` (clicks and drags), `GetWindowRect` (position assertions),
`GetTopWindow` + `GetWindow` (z-order assertions).

## Why the log needs a curated pattern list

Severity is flat. Everything is `Log.Info`; only `Log.Fatal` is different, and it is the
only thing that survives a Release build (`Log.cs`). Nearly every failure path in this
codebase is caught and logged while the app carries on — `CaptureSession` swallows WinRT
teardown throws, `LiveCardHost.Release` swallows detach failures, `SceneTransitionAnimator`
catches per-frame throws. A level filter would see none of it.

**Hard fail — abort the run.** `App.xaml.cs` installs three handlers, all writing
`Log.Fatal("CRASH", …)` with the full exception: `DispatcherUnhandledException`,
`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`. Any `[CRASH]` line,
or the process vanishing, ends it.

**Soft fail — record, keep going.** `threw`, `FAILED`, `failed`, `timed out`, `aborting`,
`Gave up waiting for`, `BeginDraw failed`, `CreateWindowExW failed`.

**Invariants — counted after the run.** More valuable than any grep:

| invariant | what a violation means |
|---|---|
| `SwitchTo START` == `SwitchTo END` | a switch died mid-way, leaving `_suspend` true |
| `Starting animation` == `Animation completed` | animator wedged, `_isAnimating` stuck true |
| `Started capture` == `Disposed capture` at exit | session leak |
| `Move off-screen` each followed by `Instant show alpha→255` | window left stranded |
| no `BLOCKED:` | re-entrancy, or the harness drove it faster than the animation |
| no `SelectionChanged re-entered` | the tray rebuild raced itself |
| no `Gave up waiting for` | transition cards never became ready |
| no `Rescued window` at startup | **the previous run leaked** — see below |

`Rescued window` is the best signal in the list. `WindowsManager.RescueParkedWindow` logs it
when startup finds a window still parked past the virtual-screen edge, which can only happen
if an earlier run died without restoring it. No in-run check can catch that.

## Ignoring the user's own windows

The test creates its own windows and must ignore everything else on the desktop.

**Attribution is exact.** Each probe is a renamed copy of the probe exe, so
`probe1.exe`…`probe6.exe` each get their own scene (scenes group by process id). The log
carries identity on nearly every line:

```
[OPACITY] Move off-screen: 'probe3' Handle=0x3F0A12 Process='probe3.exe' …
[CAPSESS] Started capture for 0x3F0A12
```

`Log.Window` emits title + handle + process, `Log.Info(tag, msg, handle)` appends the handle,
`CAPSESS` embeds it in the message, `ANIM` and `TRANSITION` carry scene titles. Build the
probe handle set at spawn and drop every line resolving to a foreign one. Fail only on
probe-attributable lines plus global `[CRASH]`.

`SIDEBAR`, `STARTUP` and `FILTER` lines are global state with no window to attribute to.

**Read from an offset.** Record the log length once the app has settled and the probes are
up; parse only past it. Removes all pre-existing noise without filtering.

**Count invariants per handle.** `Started capture` / `Disposed capture` and the
`Move off-screen` pairing are meaningless globally on a live desktop — one unrelated app
opening a window unbalances them. Scoped to probe handles they hold.

**The tray sorts itself out.** `MAX_SCENES = 5` and the tray shows the most recently
`Updated` scenes first (`MainWindow.xaml.cs`). Probes are spawned last, so they are the
newest: one goes on stage and leaves the tray, five fill every visible slot, and the user's
real scenes are pushed out. Nothing needs closing.

**What can still go wrong.** `SceneModel.Updated` is bumped on construction, on
`UpdateFromScene` (any window added, removed or **moved**) and on selection change. So a
background app that moves or opens a window jumps its scene to the front of the ordering and
evicts a probe from the tray. Two detectors:

- *Tile eviction* — before any step that clicks or drags a tile, run `QuadFit` for that
  probe's colour against the strip. Probes are flat colours; if the colour is absent, the
  tile is gone.
- *Foreground steal* — a notification or updater taking focus triggers a real switch. Any
  `SwitchTo START: … → 'X'` where X is not a probe and was not commanded by the harness.

Both mark the step **contaminated**, not failed. An environment problem reported as a product
failure is how a harness like this loses its credibility.

## Workflow

```
 0  clean start        delete log, launch app, wait for the startup sweep
                       ASSERT no "Rescued window"          ← previous run was clean
 1  spawn probes       6 scenes, probe6 has 2 windows
                       record log offset, probe handle/pid/colour set
                       ASSERT 6 scenes registered, probes hold the tray
                       record foreign scene count (report only, do not gate)
 2  switch by focus    probe1 → probe4 → probe2
                       the non-animated path, straight to SceneManager.SwitchTo
 3  switch by click    3 tray tiles, paced ~1s apart
                       the AnimatedSwitchTo path, which focus switching never reaches
 4  two-window scene   switch away from probe6, then back
                       ASSERT both rects unchanged, relative z-order unchanged
 5  drag tray→stage    press tile, clear the 10px threshold, interpolated moves past the
                       buffer, release at ~60% screen width
                       ASSERT window lands at the drop point
 6  drag stage→tray    the reverse, through the buffer into the strip
 7  rapid switching    6 switches at ~300ms, faster than the 300ms animation, to provoke
                       re-entrancy
                       "BLOCKED" is expected here; crashes and unbalanced START/END are not
 8  teardown           WM_CLOSE each probe, staggered
                       ASSERT scenes removed, capture counts balance
 9  app shutdown       WM_CLOSE StageManager
                       ASSERT no stranded windows, no CRASH during teardown
```

Steps 2 and 3 are deliberately separate. Focus-driven switching goes straight to
`SceneManager.SwitchTo` and never touches `AnimatedSwitchTo`, where the transition callbacks
live.

Step 7 is the one most likely to find something.

Whether `BLOCKED` is a failure depends on the step — expected in 7, a bug in 3 — so
expectations belong per step rather than in one global list.

## Shape

A third verb next to `window` and `run`:

```
tiltprobe smoke --log <stagemanager.log> [--out <dir>]
```

Reuses `SpawnProbes` and the staggered `WM_CLOSE` cleanup. Writes `summary.json` with a
per-step verdict and the offending log lines quoted, and propagates the exit code, matching
what `run` already does.

## Notes

`Log.cs` sets `Trace.AutoFlush = true`, so every DEBUG line is a synchronous flushed disk
write. Good for a log-watching harness — nothing is lost on a hard crash — but it is also the
performance problem in the open animation task. If logging is ever buffered, this harness
needs a flush on exit or it loses the tail.

`CLAUDE.md` says "No test project. Verify changes by building and running manually." True for
the solution, which contains only `StageManager`, but `tools/TiltProbe` already exists and is
simply untracked. Worth correcting when `tools/` is committed.

Renaming the harness (`tools/ProbeKit` or similar) would stop it reading as a scraping tool
once it carries behaviour scenarios. Cosmetic, and free while `tools/` is untracked.
