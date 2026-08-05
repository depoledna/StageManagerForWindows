# Data Models & State

> Freshness: 2026-08-05 · commit `c77517c` · covers `Model/`, plus persistence in `Services/`

No database, no ORM, no wire format. State is in-memory objects plus two small persistence sinks.

## Two parallel hierarchies

Domain objects live in `SceneManager` and know nothing of WPF. View models mirror them and implement
`INotifyPropertyChanged`. `MainWindow` is the only thing that maps one onto the other.

```
domain            view model            bound by
──────            ──────────            ────────
Scene       ───>  SceneModel            ObservableCollection<SceneModel> Scenes
  IWindow   ───>    WindowModel         SceneModel.Windows
```

## Domain — `StageManager.Model`

### `Scene` (70 lines)

```
Guid   Id       { get; }              = Guid.NewGuid()
string Key      { get; }              grouping key (process file name)
string Title    { get; private set; } derived from member windows
IEnumerable<IWindow> Windows
bool   IsSelected { get; set; }       raises SelectedChanged
void   Add(IWindow)   void Remove(IWindow)
event EventHandler? SelectedChanged
Scene(string key, params IWindow[] windows)
```

Identity is `Key`, not `Id` — two `Scene` instances for the same process are the same scene to the user.
`Id` exists so view models can track a scene across rebuilds.

### Event args

| Type | Payload |
|---|---|
| `SceneChangedEventArgs` | `ChangeType` enum + the affected `Scene` / `IWindow` |
| `CurrentSceneSelectionChangedEventArgs` | old and new current scene |

## View models

### `SceneModel` (292 lines)

```
Guid Id                      stable across UpdateFromScene
Scene Scene                  backing domain object
string Title                 => Scene?.Title ?? ""
bool IsVisible
bool IsHiddenButReserved     hidden by app filter, still occupies a row
Visibility Visibility        derived from the two above
DateTime Updated             { get; private set; }  UTC, drives ordering
double TiltTopDegrees        per-row edge angles, assigned by MainWindow.AssignRowTilts
double TiltBottomDegrees
ObservableCollection<WindowModel> Windows

static SceneModel FromScene(Scene)
void UpdateFromScene(Scene)          diffs Windows in place, then UpdatePreviewSizes()
void UpdatePreviewSizes()
```

`UpdateFromScene` **mutates in place** rather than rebuilding the collection — `ItemContainerGenerator`
breaks on duplicate items, and a rebuild would restart every capture session in the row.

### Card sizing law — `SceneModel.UpdatePreviewSizes`

Every card is its source window under one uniform scale. No per-scene normalisation, no fit-into-box.

```
s        = max(BaseCardScale, MinCardHeightDip / heightDip)
cardW    = widthDip  * s
cardH    = heightDip * s
dDip     = PrimaryScreenHeight * EdgePerspectiveDistanceRatio
squeeze  = 1 - cardW / (2 * dDip)
PreviewWidth  = cardW * squeeze
PreviewHeight = cardH * squeeze
```

| Constant | Value | Meaning |
|---|---|---|
| `BaseCardScale` | `0.135693` | uniform scale floor |
| `MinCardHeightDip` | `96.0` | short windows scale **up**; aspect always preserved |
| `EdgePerspectiveDistanceRatio` | `1379.0 / 1169.0` | perspective distance per screen height |

macOS anchors card perspective at the left edge; this renderer converges symmetrically and aspect-fits
the capture, so both dimensions take the same `squeeze`. That reproduces the mid-column and left-edge
heights exactly and leaves the widest cards up to ~7 % narrow.

Minimized windows report restored bounds via `GetWindowPlacement`, so sizing survives minimize.

All three constants were measured off macOS 26.5.2, not chosen. Changing one without re-measuring
breaks parity with the reference.

### `WindowModel` (124 lines)

```
IWindow Window               IntPtr Handle
string  Title                truncated to 20 chars, "..." suffix
ImageSource? Icon            cached; HBITMAP freed via DeleteObject
double PreviewWidth          set by SceneModel.UpdatePreviewSizes
double PreviewHeight
```

`Handle` is what XAML binds to `CompositionThumbnail.PreviewHandle`. `Icon` owns unmanaged GDI handles —
`IconToImageSource` / `ImageSourceFromBitmap` must delete the HBITMAP or the app leaks per icon refresh.

## Persistence

| Sink | Location | Contents |
|---|---|---|
| Scene snapshot | `%LOCALAPPDATA%\StageManager\updates\scene-snapshot.json` | `Snapshot(long[] ActiveSceneHandles, SceneEntry[] Scenes)`, `SceneEntry(string Key, long[] Handles)` |
| Settings | `HKCU\SOFTWARE\StageManager\Settings` | `HideDesktopIcons` (REG_DWORD) |
| Auto-start | `HKCU\...\CurrentVersion\Run` | app path, managed by `Services/AutoStart` |

`SceneSnapshot.Load` is **consume-once** — it deletes the file after a successful read, and returns null
on `IOException`, `JsonException` or `UnauthorizedAccessException`. The snapshot lives under `updates\`
because its purpose is carrying layout across an update-and-restart, not across every session.

`Settings.GetHideDesktopIcons` defaults to `true` and falls through on legacy REG_SZ values, matching
what old versions actually did.

Handles serialize as `long`, not `IntPtr` — they are only valid for the lifetime of the originating
session and are re-matched by `Key` on restore.

## Invariants

- `SceneModel.Id` is stable; `Scene` instances are not. Never key UI state on a `Scene` reference.
- `Windows` is mutated in place; never reassign the collection.
- `Updated` is UTC and drives `SyncVisibilityByUpdatedTimeStamp` ordering in `MainWindow`.
- `TiltTopDegrees` / `TiltBottomDegrees` are outputs of row position, written only by
  `MainWindow.AssignRowTiltsCore`, and read only by the `CompositionThumbnail` bindings.
