# Stage Manager for Windows

A faithful recreation of macOS [Stage Manager](https://support.apple.com/en-us/HT213315) for Windows, built on [awaescher/StageManager](https://github.com/awaescher/StageManager). The goal is full feature parity with macOS. Currently in beta.

![Stage Manager](media/current_state.gif)

Groups windows by process into "scenes" shown on a sidebar. Switch scenes to focus on one group at a time while others are hidden. Drag windows between scenes to reorganize your workspace. Sidebar previews are live and show each window's current contents.

## Usage

Download and run the executable from the [Releases tab](https://github.com/BruhTheMomentum/StageManagerForWindows/releases/) or build from source:

```bash
git clone https://github.com/BruhTheMomentum/StageManagerForWindows.git
cd StageManager
dotnet run --project StageManager
```

### Requirements
 - Windows 10 version 2004 (build 19041) or newer
 - A GPU with Direct3D 11 support
 - [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download)

## Roadmap

The goal is a 1:1 match with macOS Stage Manager. Key remaining work:

- **Behaviour alignment** — match macOS scene switching logic, window grouping rules, and edge cases
- **Complete animations** — window shuffle effects, and remaining transition polish
- **Multi-monitor support** — independent stage managers per display
- **Visual polish** — adaptive sidebar positioning
- **Drag & drop refinement** — snap-to-scene indicators
- **Smarter window detection** — filter out popups and transient windows (e.g. Teams call toasts) that shouldn't create new scenes

## Acknowledgements

Built on [awaescher/StageManager](https://github.com/awaescher/StageManager). Window tracking code from [workspacer](https://github.com/workspacer/workspacer) by [Rick Button](https://github.com/rickbutton).
