# Windowing & Orchestration

> Freshness: 2026-08-05 · commit `c77517c` · covers `SceneManager.cs`, `Native/`, `Strategies/`, `Services/`, `Helpers/`
>
> Template equivalent: backend. Headless — no UI type is referenced from any file below.

## Win32 layer — `StageManager.Native`

Adapted from [workspacer](https://github.com/workspacer/workspacer) by Rick Button.

| File | Lines | Role |
|---|---|---|
| `WindowsManager.cs` | 559 | window registry + WinEvent hooks; the source of all window events |
| `WindowsWindow.cs` | 546 | `IWindow` over one HWND — title, class, process, state, move/resize |
| `PInvoke/Win32.Window.cs` | 235 | `SetWindowPos`, `ShowWindow`, `GetWindowPlacement`, DWM attributes |
| `PInvoke/Win32.Long.cs` | 132 | `WS`, `WS_EX` style flags as `long` |
| `PInvoke/Win32.WinEvent.cs` | 91 | `SetWinEventHook` + `EVENT_CONSTANTS` |
| `PInvoke/Win32.cs` | 64 | shared structs — `Rect`, `POINT`, `MONITORINFOEX` |
| `PInvoke/Win32Helper.cs` | 55 | style/visibility predicates |
| `WindowsDeferPosHandle.cs` | 80 | `BeginDeferWindowPos` batching, disposable |
| `VisualHelper.cs` | 86 | DPI + visual-tree geometry |
| `Interop/NativeMethods.cs` | 35 | thin extras |
| `FocusStealer.cs` | 14 | `AttachThreadInput` dance to force foreground |

### `WindowsManager`

```
event WindowCreateDelegate? WindowCreated      (IWindow, bool firstCreate)
event WindowDelegate?       WindowDestroyed    (IWindow)
event WindowUpdateDelegate? WindowUpdated      (IWindow, WindowUpdateType)
event EventHandler<IntPtr>? DesktopShortClick
IEnumerable<IWindow> Windows
Task Start()   void Stop()
IWindowsDeferPosHandle DeferWindowsPos(int count)
void SuppressNextDesktopClick()
```

`WindowUpdateType` distinguishes foreground, move-start, move-end, minimize, restore — `MainWindow` and
`DragDropManager` both filter on it rather than re-querying state.

Batch any multi-window reposition through `DeferWindowsPos`; individual `SetWindowPos` calls tear.

### `Native.Window` contracts

`IWindow` (59 lines) — `Handle`, `Title`, `Class`, `Location`, `Offset`, `ProcessId`,
`ProcessFileName`, `ProcessName`, `CanLayout`, `IsFocused`, `IsMinimized`, `IsMaximized`,
`IsMouseMoving`. Plus `IWindowLocation`, `IWindowsManager`, `IWindowsDeferPosHandle`, and the
`WindowState` / `WindowUpdateType` enums.

## Scene engine — `SceneManager.cs` (885 lines)

Groups windows into `Scene`s by process key and decides which scene is on stage.

```
event EventHandler<SceneChangedEventArgs>?                  SceneChanged
event EventHandler<CurrentSceneSelectionChangedEventArgs>?  CurrentSceneSelectionChanged

SceneManager(WindowsManager, bool hideDesktopIcons = true)
Task Start()                              void Stop()          void Dispose()

Scene? FindSceneForWindow(IWindow | IntPtr)
IEnumerable<Scene> GetScenes()            IEnumerable<IWindow> GetCurrentWindows()
bool IsCurrentScene(Scene?)

Task<bool> SwitchTo(Scene?)
Task MoveWindow(Scene source, IWindow, Scene target)
Task MoveWindow(IntPtr handle, Scene target)
Task PopWindowFrom(Scene source)
Scene? SeparateWindowToNewScene(IWindow)

void ParkWindow(IWindow)                  void RestoreWindow(IWindow)
void HideCurrentSceneWindows()            void RestoreMinimizedInvisibly(Scene)
void ShowDesktopIcons()                   void HideDesktopIcons()
SceneSnapshot.Snapshot CreateSnapshot()
```

`SwitchTo` is the hot path: park the outgoing scene's windows, restore the incoming ones, raise
`CurrentSceneSelectionChanged`. `MainWindow` animates around it, not inside it.

Grouping key is the process file name — every window of one process is one scene.

### Event filtering — four guards, all load-bearing

| Guard | Where | Purpose |
|---|---|---|
| `_suspend` | `:25`, set around `SwitchTo` and every mutation path | focus events fired *during* a switch would cascade into another switch; handlers early-return |
| `IsRapidFocusChange()` | `:449` | swallows foreground events <100 ms apart, blocking system-initiated focus loops (modal dialogs, Teams compact view) |
| `IsPersistentWindow()` | `:54`, filtered by `GetSceneableWindows` `:692` | keeps the Teams meeting compact view floating across all scenes instead of forming a scene |
| desktop blank-click | `:202-234` + `Helpers/DesktopShellClassifier` | a click on `WorkerW`/`Progman` counts as blank desktop only when the `SysListView32` child reports zero selected items via `LVM_GETSELECTEDCOUNT` — separates wallpaper click from icon click, drives scene ↔ desktop toggle |

`GetSceneableWindows` is the single filter: not persistent, `CanLayout`, non-empty `ProcessFileName`,
non-empty `Title`. Anything bypassing it will pick up shell and tool windows.

## Hide/show policy — `StageManager.Strategies`

`IWindowStrategy` — `Show(IWindow)` / `Hide(IWindow)`. Three implementations, chosen by `SceneManager`:

| Strategy | Lines | Mechanism | Trade-off |
|---|---|---|---|
| `OpacityWindowStrategy` | 190 | moves the window past the virtual-screen edge | DWM keeps compositing, so previews stay live |
| `ShowAndHideWindowStrategy` | 21 | `ShowWindow(SW_HIDE / SW_SHOW)` | cheap; kills capture and some apps mishandle it |
| `NormalizeAndMinimizeWindowStrategy` | 18 | minimize / restore | most compatible; visible taskbar animation |

`OpacityWindowStrategy` is the default and is a misnomer — it does **not** set alpha. Windows.Graphics.Capture
reads DWM-composited (post-alpha) output, so an alpha=0 window yields transparent frames. Off-screen parking
is the only hide that keeps frames flowing; hidden and minimized windows produce none.

Its extra statics matter to callers:

```
static bool TryGetOriginalPosition(IntPtr hWnd, out int x, out int y)
static void CleanupWindow(IntPtr hWnd)
```

`_originalPositions` holds the pre-hide on-screen point; `SceneTransitionAnimator` reads it to target a
window's *intended* rect rather than its parked one. A per-HWND `SemaphoreSlim` serialises Show/Hide so
concurrent calls can't race on position state; `CleanupWindow` acquires before disposing and must be
called on window destroy.

## Services — `StageManager.Services`

| File | Lines | Role |
|---|---|---|
| `UpdateService.cs` | 259 | GitHub releases check, download, apply-and-restart; `record UpdateInfo(TagName, Version, DownloadUrl, Size)` |
| `Desktop.cs` | 112 | desktop icon visibility — `GetDesktopIconsVisible`, `ShowIcons`, `HideIcons(animate)`, `RestoreIcons`, `EnsureIconsExist` |
| `ThemeManager.cs` | 60 | swaps `Themes/*.xaml`, listens for OS light/dark change |
| `SceneSnapshot.cs` | 59 | persist/restore scene layout; `record Snapshot(long[] ActiveSceneHandles, SceneEntry[] Scenes)` |
| `AutoStart.cs` | 34 | `HKCU\...\Run` registry entry |
| `Settings.cs` | 24 | `Get/SetHideDesktopIcons` |

`UpdateService` drives a `private enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }`
consumed by the tray menu in `MainWindow`.

## Helpers

| File | Role |
|---|---|
| `Helpers/DesktopShellClassifier.cs` | identifies `Progman` / `WorkerW` / shell windows that must never become scenes |
| `Helpers/OverlayCoordExtensions.cs` | `ToCanvas` — screen point → overlay canvas point |

## Shutdown contract

`WM_CLOSE` is input-synchronous, so any outgoing cross-apartment COM call made while dispatching it
fails with `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`. `MainWindow.OnClosing` therefore runs teardown **before**
WPF flips `IsVisible` across the tree:

```
_sceneTransitionAnimator?.Dispose()
_sidebarDragGhost?.Hide()
CompositionThumbnail.ShutdownAll()
```

Each is individually wrapped — a throw in one must not skip the others. `App.xaml.cs` deliberately does
not set `args.Handled` on unhandled exceptions, so anything escaping here kills the process mid-teardown
and leaks capture sessions that DWM keeps feeding.

## Known issues

- `SceneManager.cs` at 885 lines mixes grouping, switching, and strategy dispatch.
- `Scenes` mutations raise `CollectionChanged` synchronously and handlers re-enter; index-based
  `RemoveAt`/`Insert` needs a clamp. See `MainWindow.ApplyCurrentSceneSelection`.
- Multi-monitor is single-display only today — one sidebar, primary monitor.
