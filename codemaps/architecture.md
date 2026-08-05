# Architecture

> Freshness: 2026-08-05 · commit `c77517c` · branch `0.8.3` · 65 C# files, ~9 990 lines

Single WPF executable, no server, no client/server split. One project: `StageManager/StageManager.csproj`
(`net10.0-windows10.0.19041.0`, `WinExe`, WPF + WinForms interop, nullable enabled).

## Layers

```
                        App.xaml.cs                     process entry, single-instance, theme
                             |
                        MainWindow                      sidebar shell, tray menu, input routing
                       /     |      \
          SceneManager   Animations   Controls          orchestration / motion / live previews
                |            |            |
       Native (Win32)   Composition   Composition       HWND control, DWM-free capture
                |            |            |
             user32       WinRT       Direct3D 11
```

| Layer | Namespace | Owns | Map |
|---|---|---|---|
| Shell | `StageManager` | `MainWindow`, `App`, `Log` | this file |
| Orchestration | `StageManager` , `.Strategies` , `.Services` | scene lifecycle, window hide/show policy, settings, updates | [windowing.md](windowing.md) |
| Windowing | `StageManager.Native.*` | HWND enumeration, WinEvent hooks, positioning | [windowing.md](windowing.md) |
| State | `StageManager.Model` | `Scene`, `SceneModel`, `WindowModel`, event args | [data.md](data.md) |
| Presentation | `StageManager.Controls` , `.Converters` , `.Animations` | tiles, overlays, transitions, drag ghosts | [rendering.md](rendering.md) |
| Capture | `StageManager.Composition.*` | frame capture, compositor visuals, D3D device | [rendering.md](rendering.md) |

Naming note: this app has no backend or frontend. `windowing.md` covers what the skill template calls
backend (headless orchestration + OS integration); `rendering.md` covers what it calls frontend.

## Namespace dependency graph

Arrows = `using` edges between internal namespaces. Acyclic.

```
MainWindow ──> Animations ──> Controls ──> Composition ──> Composition.Interop
     │              │             │              │
     │              └─> Model     └─> Native.PInvoke
     │              └─> Helpers
     ├──> SceneManager ──> Strategies ──> Native.Window
     │         └────────> Native.* , Services , Helpers , Model
     └──> Services , Native.Interop
```

Rules the graph enforces today:
- `Native.*` and `Model` never reference UI namespaces — both are reusable headless.
- `Composition` depends only on `Composition.Interop` and `Native.PInvoke`; it knows nothing of scenes.
- `Controls` is the only namespace that owns a `CaptureSession`; `Animations` borrows, never creates
  (except `LiveCardHost.TryCreateOwned`).
- `Log` reaches into `Model` for scene diagnostics — the one downward-facing exception.

## Runtime flow

1. `App.OnStartup` — single-instance mutex, `ThemeManager.ApplyTheme`, show `MainWindow`.
2. `MainWindow.OnContentRendered` — build `WindowsManager`, `SceneManager`, start SharpHook mouse hook,
   restore `SceneSnapshot`, warm the transition overlay.
3. `WindowsManager` WinEvent hooks raise `WindowCreated` / `WindowUpdated` / `WindowDestroyed`.
4. `SceneManager` groups windows by process key into `Scene`s, raises `SceneChanged` /
   `CurrentSceneSelectionChanged`.
5. `MainWindow` projects `Scene` → `SceneModel` into `ObservableCollection<SceneModel> Scenes`;
   XAML binds each `WindowModel.Handle` to a `CompositionThumbnail`.
6. Each tile opens a `CaptureSession` (`Windows.Graphics.Capture` → `Windows.UI.Composition`).
7. Switching or dragging hands the tile's live visual to `SceneTransitionAnimator` via `LiveCardHost`.

## External dependencies

| Package | Version | Used for |
|---|---|---|
| Vortice.Direct3D11 / Vortice.DXGI | 3.8.1 | D3D11 device behind the capture frame pool |
| SharpHook | 6.1.2 | global low-level mouse hook (edge trigger, drag tracking) |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | tray icon and menu |
| WpfScreenHelper | 2.1.1 | monitor/work-area geometry |
| ControlzEx | 7.0.1 | window chrome helpers |
| AsyncAwaitBestPractices | 9.0.0 | `SafeFireAndForget` |

WinRT surface (`Windows.Graphics.Capture`, `Windows.UI.Composition`, `Windows.Graphics.DirectX`) comes
from the `10.0.19041.0` target platform, not a package. That platform floor is what sets the app's
minimum OS.

## Diagnostics — `Log.cs`

Every method is `[Conditional("DEBUG")]`, so logging compiles away entirely in Release. Output goes
through a `TextWriterTraceListener` to `stagemanager.log` beside the executable
(`AppContext.BaseDirectory`). Tag strings (`CAPSESS`, `COMPTHUMB`, `ANIM`, `TILT`, `SHUTDOWN`) are how
sessions are read back; `MainWindow.xaml.cs:1055` emits a fixed-format geometry line that is parsed, so
its shape is an invariant.

## Build

- SDK pinned by `global.json` to `10.0.100-rc.2.25502.107`, `rollForward: latestPatch`.
- `SetVersionFromGitTag` target runs `git describe --tags --abbrev=0`, strips `v`, sets `Version`.
  A build with no tag reachable stays at `1.0.0`.
- `Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport=true` — WPF's `PenThreadWorker`
  throws on its own thread, which no handler can catch. App takes no pen input.
- Release workflow: `.github/workflows/dotnet-desktop.yml`, x64 + arm64.

## Known structural debt

- `MainWindow.xaml.cs` is 1 788 lines / ~70 members and mixes six concerns: scene projection, sidebar
  drag, visibility animation, icon overlay, window-mode switching, and the update UI. Largest single
  extraction candidates are the drag region (lines 678–980) and the update region (1 674–1 780).
- `SceneManager.cs` at 885 lines couples grouping, switching, and strategy dispatch.
