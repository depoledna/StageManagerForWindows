## Overview

A recreation of macOS Stage Manager for Windows, built on
[awaescher/StageManager](https://github.com/awaescher/StageManager). Currently in beta; the goal is
feature parity with macOS.

Open windows are grouped by process into "scenes" listed on a sidebar. One scene is on stage at a time;
the rest are parked off-screen. Clicking a scene switches to it, and windows can be dragged between
scenes to reorganise the workspace. Sidebar tiles are live — each one is a real capture of the window,
tilted and scaled to match the macOS card look.

Single WPF executable, .NET 10, Windows 10 2004 or newer. No server, no database, no test project.

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

## Codemaps

Structure, dependencies and design decisions live in `codemaps/`, tracked in this repo. Read the
relevant map before changing code in that area, and update it in the same commit when a change moves
a boundary — new namespace, new dependency edge, changed public surface, changed geometry constant.

- [architecture.md](codemaps/architecture.md) — layers, namespace dependency graph, runtime flow, build, known debt
- [rendering.md](codemaps/rendering.md) — `Controls/`, `Animations/`, `Composition/`, `Converters/`, `Themes/`
- [windowing.md](codemaps/windowing.md) — `SceneManager.cs`, `Native/`, `Strategies/`, `Services/`, `Helpers/`
- [data.md](codemaps/data.md) — `Model/` and persistence

Each map carries a freshness line with the commit it was generated against. Regenerate with
`/update-codemaps`; the diff report lands in `.reports/codemap-diff.txt`, which is not tracked.


## CI/CD

Pipeline (`.github/workflows/dotnet-desktop.yml`) only triggers on `v*` tags. Regular pushes to main don't trigger builds. Includes CodeQL analysis and Gitleaks secret scanning.

## Target Framework

.NET 10.0 (WPF + WinForms enabled). SDK version pinned in `global.json` to `10.0.100-rc.2.25502.107`.
