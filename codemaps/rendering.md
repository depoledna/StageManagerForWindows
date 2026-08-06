# Rendering

> Freshness: 2026-08-05 · commit `c77517c` · covers `Controls/`, `Animations/`, `Composition/`, `Converters/`, `Themes/`
>
> Template equivalent: frontend.

Everything the user sees. Two stacks meet here — WPF for layout and chrome, Windows.UI.Composition for
the live window previews. They are bridged by `CompositionHost`, an `HwndHost`.

## Capture stack — `StageManager.Composition`

| File | Lines | Role |
|---|---|---|
| `CaptureSession.cs` | 584 | one live window → one composition visual tree |
| `D3DDeviceHolder.cs` | 211 | shared D3D11 device + `IDirect3DDevice`, device-lost recovery |
| `CompositionHost.cs` | 130 | `HwndHost` child window with `WS_EX_NOREDIRECTIONBITMAP`; `Root` visual setter |
| `CompositorFactory.cs` | 65 | per-thread `Compositor` + `DesktopWindowTarget` |
| `DispatcherQueueHelper.cs` | 57 | `CreateDispatcherQueueController` so WPF's thread can host a compositor |
| `Interop/CompositionInterop.cs` | 73 | `ICompositorInterop`, `ICompositorDesktopInterop`, `ICompositionDrawingSurfaceInterop` |
| `Interop/Direct3DInterop.cs` | 75 | `CreateDirect3D11DeviceFromDXGIDevice`, `IDirect3DDxgiInterfaceAccess` |
| `Interop/GraphicsCaptureItemInterop.cs` | 69 | `IGraphicsCaptureItemInterop.CreateForWindow` |

### `CaptureSession` visual tree

Three layers, because Composition only foreshortens a **child** visual's 3D rotation — perspective must
sit alone on an ancestor.

```
_rootContainer  ContainerVisual   TransformMatrix = pure perspective, M34 = -1/depth
   └ _content   ContainerVisual   TransformMatrix = affine (shear, scale, translate)
      └ _sprite SpriteVisual      RotationAngle about Y, brush = capture surface
                                  GeometricClip = rounded rect
```

Public setters, all idempotent and dirty-checked by callers:

```
Start()  Pause()  Resume()  Dispose()
SetVisualSize(hwndPixels, basePixels)
SetTransformMatrix(Matrix4x4)      -> _content
SetSpriteRotationY(degrees)        -> _sprite
SetPerspective(depthPx)            -> _rootContainer
SetOpacity(float)  SetCornerRadius(radiusPx, sizePx)
RootVisual : Visual?    event TargetClosed
```

`Pause`/`Resume` take a per-HWND `SemaphoreSlim` with a **non-blocking** `Wait(0)`; contention defers to
the threadpool. This is deliberate — a blocking wait on the UI thread during `WM_CLOSE` deadlocks, and
disposing a WinRT session inside an input-synchronous message pump throws `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`
(`0x8001010D`), which is unhandleable. See `Controls/CompositionThumbnail.ShutdownAll`.

## Tiles — `StageManager.Controls`

| File | Lines | Role |
|---|---|---|
| `CompositionThumbnail.xaml.cs` | 640 | the sidebar tile; owns a `CaptureSession`, solves tile geometry |
| `IconOverlayManager.cs` | 458 | per-scene app icons and labels in layered windows |
| `IconOverlayWindow.xaml.cs` | 73 | one layered, click-through overlay window |
| `LayeredOverlayWindowBase.cs` | 45 | `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` base |

### `CompositionThumbnail` — geometry authority

Dependency properties: `PreviewHandle`, `CornerRadius`, `TopEdgeDegrees`, `BottomEdgeDegrees`,
`MirrorScale`, `MirrorOpacity`.

Constants:

| Name | Value | Meaning |
|---|---|---|
| `TrayTiltDegrees` | `2.0` | resting shear of a sidebar tile |
| `PerspectiveDepthPx` | `220.0` | vanishing distance; fixed for tile and flying card alike |
| `HoverHeadroom` | `0.30` | per-side HWND inflation (1.6× total) so the near edge isn't clipped |

`SolveEdgeAngles(topDeg, bottomDeg, baseHeightPx) -> (shearDeg, spriteRotationDeg)` is the **single
source of truth** for the trapezoid. Given the two requested edge slopes it splits them into a shear
component and a convergence component, then inverts `Q = H·tan(θ)/(2·depth)` for θ. Because θ is solved
against the live height, the look is size- and DPI-independent.

Both `ApplyTransform` (resting tile) and `LiveCardHost.SetEdgeShape` (flying card) call it. Nothing else
may compute a rotation angle.

Static shutdown registry: `s_live` + `s_shuttingDown`, drained by `ShutdownAll()` from
`MainWindow.OnClosing`. `OnIsVisibleChanged`, `OnUnloaded` and `StartCaptureIfReady` all early-out once
the flag is set.

Visual lending: `BorrowRootVisual()` / `ReturnRootVisual()` let an animation take the tile's visual and
give it back. `Session` is exposed for the same purpose.

## Motion — `StageManager.Animations`

| File | Lines | Role |
|---|---|---|
| `DragDropManager.cs` | 285 | stage → sidebar drag; `BufferWidthLogical = 120.0` |
| `LiveCardHost.cs` | 226 | flying card backed by a real capture session |
| `SceneTransitionAnimator.cs` | 178 | drives the overlay animation, owns `TransitionOverlayWindow` |
| `DragGhostWindow.cs` | 141 | layered ghost window during a stage drag |
| `SidebarDragGhost.cs` | 100 | sidebar → stage ghost |
| `DebugZoneOverlay.cs` | 73 | visualises drag zones when enabled |
| `PlaceholderFactory.cs` | 48 | static fallback card |
| `BorderCard.cs` | 43 | `IFlyingCard` that draws a plain border, no capture |
| `Anim.cs` | 35 | `DoubleAnimation` factory — `To`, `From`, `Storyboard` (target/property pre-bound) |
| `IFlyingCard.cs` | 22 | `Update(baseRect, skewDegrees)`, `SetVisible`, `Release` |

`IFlyingCard` has three implementations: `LiveCardHost` (real frames), `BorderCard`, and the
`PlaceholderFactory` output. Callers never branch on which.

### `LiveCardHost`

Two flavours: **borrowed** (`TryBorrow` takes a tile's running session; `Release` returns the visual) and
**owned** (`TryCreateOwned` starts a fresh session for a window with no tile, e.g. the outgoing scene;
`Release` disposes it).

`Mount` states `SetPerspective(PerspectiveDepthPx)` once. `Update` then calls `SetEdgeShape`, which
re-solves the trapezoid against the card's **current** height every frame. Owned cards carry
`_restTop/_restBottomEdgeDegrees == 0` and take an early-out to a plain shear.

Why the re-solve exists: the card previously kept the rotation the tile solved for a ~200 px sprite while
growing to ~1 600 px. Q scales with H, so the perspective divide ran away — one vertical edge scaled
~1.85×, the other ~0.69×, a 2.7:1 trapezoid taller than the display.

## WPF surface

| File | Role |
|---|---|
| `MainWindow.xaml` | sidebar `ItemsControl`, `Grid` ItemsPanel, tray `NotifyIcon`, context menu |
| `Animations/TransitionOverlayWindow.xaml` | full-screen transparent canvas that hosts flying cards |
| `Controls/CompositionThumbnail.xaml` | grid + `CompositionHost` placement |
| `Controls/IconOverlayWindow.xaml` | icon/label layer |
| `Themes/{DarkColors,LightColors,TrayMenuTheme}.xaml` | resource dictionaries swapped by `ThemeManager` |
| `Converters/IndexToOffsetMarginConverter.cs` | per-row sidebar offset |
| `Converters/IndexToZIndexConverter.cs` | stacking order within a scene |

Tiles use `HorizontalAlignment="Left"` — WPF's default `Stretch` centres a fixed-width child, which
left tiles of differing widths ragged.

### Two window-level tricks

**Sidebar hit-test.** `CompositionThumbnail.xaml:12,23` set `Background="#01000000"`. The sidebar is a
layered top-level window, so it must register non-zero alpha at tile locations or `WindowFromPoint`
falls through to whatever is behind it. Alpha 1/255 is invisible and sufficient.

**Sidebar parking.** `MainWindow.ApplyWindowMode` (`:1329`) moves the sidebar to `Left = -Width` in
`WindowMode.OffScreen`. `Opacity = 0` alone would stop DWM compositing the tiles; parking keeps frames
flowing. Slide-in animates `Left` back to 0 on hover — the edge trigger is `e.Data.X <= 44` (`:1429`).
`WindowMode.Flyover` sets `Topmost`.

The transition overlay carries `WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT` so it stays out of Alt-Tab and
never intercepts clicks.

## Constraints

- Every capture visual belongs to the compositor of the thread that created it. Cross-thread mounting
  throws; `CompositorFactory` is per-thread for this reason.
- `HwndHost` children do not clip to WPF layout. Hence `HoverHeadroom` inflation instead of overflow.
- Device loss is recovered inside `D3DDeviceHolder`; sessions rebuild their frame pool on `Resume`.
- .NET 10 WPF removed `PlaneProjection` — noted at `Animations/BorderCard.cs:13` and
  `Animations/PlaceholderFactory.cs:24`. There is no framework route back to 3D tilt.
