# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet run --project StageManager          # Run in Debug mode
dotnet build                               # Build only
dotnet publish StageManager/StageManager.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true  # Release build
```

No test projects exist. Verify changes by building and running manually.

## Architecture

macOS Stage Manager clone for Windows. Groups windows by process into "scenes", showing one scene at a time while hiding others by parking them off-screen.

```
MainWindow.xaml.cs          UI + sidebar + global mouse hooks (SharpHook)
    ↓ SwitchSceneCommand
SceneManager.cs             Orchestration: scene switching, window grouping, desktop toggle
    ↓ events
WindowsManager.cs           Window tracking via WinEventHook, mouse hooks, focus detection
    ↓
OpacityWindowStrategy.cs    Hides windows by moving them past the virtual-screen edge so
                            DWM keeps compositing them (live capture frames stay valid).
```

**Key flow**: Click sidebar scene → animation plays (SceneTransitionAnimator) → SceneManager.SwitchTo() hides other windows (off-screen park) and shows target windows (restore saved position) → sidebar updates via CurrentSceneSelectionChanged event.

## Folder Map

- `Animations/` — SceneTransitionAnimator, TransitionOverlayWindow, PlaceholderFactory, DragGhostWindow, DragDropManager
- `Composition/` — Windows.Graphics.Capture + WinUI Composition pipeline backing `CompositionThumbnail` (CaptureSession, CompositionHost, D3DDeviceHolder, CompositorFactory, DispatcherQueueHelper, Interop/)
- `Controls/` — CompositionThumbnail (sidebar live preview), IconOverlayManager, LayeredOverlayWindowBase
- `Model/` — `Scene` (core), `WindowModel` + `SceneModel` (INotifyPropertyChanged UI wrappers)
- `Native/` — `WindowsManager` (WinEventHook + LL mouse hook), `WindowsWindow` (`IWindow` impl), `PInvoke/` partial classes
- `Services/` — Settings, AutoStart, ThemeManager, SceneSnapshot, UpdateService, Desktop
- `Strategies/` — OpacityWindowStrategy (primary) + NormalizeAndMinimize, ShowAndHide alternates behind `IWindowStrategy`
- `Helpers/` — DesktopShellClassifier (WorkerW/Progman/SysListView32 class detection), OverlayCoordExtensions
- `Converters/` — sidebar layout converters (Index→Offset, Index→ZIndex)

## Key Design Decisions

- **OpacityWindowStrategy** over minimize: windows are moved off-screen (past the virtual screen edge) rather than minimized, so DWM keeps compositing them and `Windows.Graphics.Capture` continues delivering live frames to the sidebar. The `IWindowStrategy` interface allows swapping strategies. The previous alpha=0 trick was abandoned because WGC captures DWM-composited (post-alpha) output and would otherwise see transparent frames.
- **Saved-position restore** (`OpacityWindowStrategy._originalPositions`): pre-hide rect captured on `Hide`, replayed on `Show`. `TryGetOriginalPosition` exposes it read-only so the scene-transition animator can target the *intended* on-screen rect of an incoming window instead of its current parked location.
- **Per-hwnd lock** (`OpacityWindowStrategy.cs`): `ConcurrentDictionary<IntPtr, SemaphoreSlim> _windowLocks` serializes Show/Hide per window so concurrent calls don't race on position state. Disposed on window destroy via `CleanupWindow`.
- **Composition thumbnails** (`Controls/CompositionThumbnail`, `Composition/`): each sidebar tile owns a `CaptureSession` whose free-threaded `Direct3D11CaptureFramePool` blits into a `CompositionDrawingSurface` hosted by a per-tile `HwndHost` (`CompositionHost`). Single shared D3D11 device + WinRT projection (`D3DDeviceHolder` singleton). Sidebar pixel-alpha hit-test trick: the host containers carry `Background="#01000000"` so the layered top-level WPF window registers a non-zero alpha at thumbnail locations and `WindowFromPoint` lands on the sidebar rather than falling through to whatever is behind it.
- **[Conditional("DEBUG")]** on `Log` class: all logging compiles away in Release. Log output goes to `stagemanager.log` next to the exe via `TextWriterTraceListener`.
- **Scene grouping by process**: `Scene.Key` is the process filename. All windows from the same process belong to one scene.
- **Reentrancy protection** (`SceneManager.cs:25`): `_suspend` bool flag set around `SwitchTo` / scene-mutation paths so focus events fired during a switch don't cascade into another switch. Event handlers early-return when `_suspend` is true.
- **Rapid-focus throttle** (`SceneManager.cs:451`): `IsRapidFocusChange()` swallows foreground events <100ms apart to block system-initiated focus loops (e.g. modal dialogs, Teams compact view).
- **Persistent windows** (`SceneManager.cs:54`): `IsPersistentWindow` excludes Teams "Meeting compact view" pop-up from scene assignment so it floats across all scenes. `GetSceneableWindows` filters these out.
- **Desktop blank-click classification** (`SceneManager.cs:202-234`, `Helpers/DesktopShellClassifier.cs`): a click on WorkerW/Progman is "blank desktop" only when the SysListView32 child reports zero selected items via `LVM_GETSELECTEDCOUNT` — distinguishes wallpaper click from icon click. Used to toggle scene ↔ desktop view.
- **MainWindow off-screen parking** (`MainWindow.xaml.cs:1052,1459`): `WindowMode.OffScreen` parks the sidebar at `Left = -Width`. DWM still composites thumbnails (Opacity=0 alone wouldn't be enough), but the window is invisible to the user. Slide-in animates Left back to 0 on hover/hotkey.

## P/Invoke Organization

Win32 APIs are in `Native/PInvoke/` as partial classes on `Win32`:
- `Win32.cs` — constants, enums, core functions
- `Win32.Window.cs` — SetWindowPos, window positioning
- `Win32.Long.cs` — Get/SetWindowLong, extended styles (WS_EX)
- `Win32.WinEvent.cs` — SetWinEventHook, event constants

Composition / DXGI bridges live in `Composition/Interop/` (CompositionInterop, Direct3DInterop, GraphicsCaptureItemInterop).

## Animation System (WIP)

`Animations/SceneTransitionAnimator.cs` uses a separate transparent topmost WPF window (`TransitionOverlayWindow`) as an overlay. Placeholder rectangles animate from sidebar position to window position (incoming) and vice versa (outgoing). Duration: 300ms, PowerEase EaseOut.

The overlay has `WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT` so it doesn't appear in Alt-Tab or intercept clicks.

## CI/CD

Pipeline (`.github/workflows/dotnet-desktop.yml`) only triggers on `v*` tags. Regular pushes to main don't trigger builds. Includes CodeQL analysis and Gitleaks secret scanning.

## Target Framework

.NET 10.0 (WPF + WinForms enabled). SDK version pinned in `global.json` to `10.0.100-rc.2.25502.107`.
