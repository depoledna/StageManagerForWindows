using AsyncAwaitBestPractices;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace StageManager
{
	public class SceneManager : IDisposable
	{
		private readonly Desktop _desktop;
		private List<Scene> _scenes;
		private readonly object _scenesLock = new object();
		private Scene _current;
		private bool _suspend = false;
		private Scene _lastScene; // remembers the scene that was active before desktop view
		private DateTime _lastDesktopToggle = DateTime.MinValue;
		private IWindow _lastFocusedWindow;
		private DateTime _lastFocusChange = DateTime.MinValue; // Track rapid focus changes

		/// <summary>
		/// When set, focus-triggered scene switches use this delegate instead of calling SwitchTo directly.
		/// MainWindow sets this to inject the transition animation.
		/// </summary>
		public Func<Scene, Task<bool>> AnimatedSwitch { get; set; }

		public event EventHandler<SceneChangedEventArgs> SceneChanged;
		public event EventHandler<CurrentSceneSelectionChangedEventArgs> CurrentSceneSelectionChanged;

		// Use full-transparency instead of minimising so hidden windows keep repainting and thumbnails stay live.
		private IWindowStrategy WindowStrategy { get; } = new OpacityWindowStrategy();

		public WindowsManager WindowsManager { get; }

		private const string TeamsProcessName1 = "ms-teams.exe";
		private const string TeamsProcessName2 = "teams.exe";
		private bool _disposed = false;
		private readonly bool _hideDesktopIcons;

		/// <summary>
		/// Determines whether the given window should stay visible across scenes and therefore must not
		/// participate in Stage Manager scene logic. Currently hard-codes an exception for the Microsoft
		/// Teams ‘Meeting compact’ floating pop-up.
		/// </summary>
		private bool IsPersistentWindow(IWindow window)
		{
			if (window == null)
				return false;

			// Quick process check – bail out early if it is definitely not Teams
			var exe = window.ProcessFileName ?? string.Empty;
			if (!string.Equals(exe, TeamsProcessName1, StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(exe, TeamsProcessName2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			// Identify the floating meeting pop-up through its title. The compact meeting view always contains
			// the words “Meeting” and “compact”. Adjust the checks here if Microsoft changes the wording.
			var title = window.Title ?? string.Empty;
			return title.IndexOf("Meeting", StringComparison.OrdinalIgnoreCase) >=0 &&
			 title.IndexOf("compact", StringComparison.OrdinalIgnoreCase) >=0;
		}

		public SceneManager(WindowsManager windowsManager, bool hideDesktopIcons = true)
		{
			WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
			_desktop = new Desktop();
			_hideDesktopIcons = hideDesktopIcons;

			// Ensure icons exist (shell-level) so the SysListView32 is available for alpha fading.
			// If previous session crashed, icons may have been toggled off — restore them.
			_desktop.EnsureIconsExist();

			if (_hideDesktopIcons)
				_desktop.HideIcons(animate: false);

			Log.Info("STARTUP", $"SceneManager constructor: hideDesktopIcons={_hideDesktopIcons}");
		}

		public async Task Start()
		{
			// Check if we're on the UI thread by verifying we have access to the dispatcher
			// This is more reliable than checking for thread ID1
			if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
				throw new NotSupportedException("Start has to be called on the main thread, otherwise events won't be fired.");

			Log.Info("STARTUP", "SceneManager starting");

			WindowsManager.WindowCreated += WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed += WindowsManager_WindowDestroyed;
			WindowsManager.UntrackedFocus += WindowsManager_UntrackedFocus;
			WindowsManager.DesktopShortClick += WindowsManager_DesktopShortClick;

			await WindowsManager.Start();

			Log.Info("STARTUP", "SceneManager started, WindowsManager active");
		}

		internal void Stop()
		{
			// Unsubscribe from all WindowsManager events to prevent memory leaks
			if (WindowsManager != null)
			{
				WindowsManager.WindowCreated -= WindowsManager_WindowCreated;
				WindowsManager.WindowUpdated -= WindowsManager_WindowUpdated;
				WindowsManager.WindowDestroyed -= WindowsManager_WindowDestroyed;
				WindowsManager.UntrackedFocus -= WindowsManager_UntrackedFocus;
				WindowsManager.DesktopShortClick -= WindowsManager_DesktopShortClick;
			}

			WindowsManager.Stop();

			// Determine which window should stay visible (e.g. the one that currently has focus)
			var exemptHandle = _lastFocusedWindow?.Handle ?? Win32.GetForegroundWindow();

			// Restore every known window (some might not be part of any scene anymore)
			foreach (var w in WindowsManager?.Windows ?? Array.Empty<IWindow>())
			{
				WindowStrategy.Show(w);
				if (w.Handle != exemptHandle)
				{
					w.ShowMinimized();
				}
			}

			// Original per-scene clean-up kept for completeness (will be mostly redundant)
			Scene[] scenesSnapshot;
			lock (_scenesLock)
				scenesSnapshot = _scenes?.ToArray() ?? Array.Empty<Scene>();
			foreach (var scene in scenesSnapshot)
			{
				foreach (var w in scene.Windows)
				{
					// Restore full opacity so windows become visible again
					WindowStrategy.Show(w);

					// Minimise every window except the one that should remain visible
					if (w.Handle != exemptHandle)
					{
						w.ShowMinimized();
					}
				}
			}

			// Restore icon visibility and remove layered style on shutdown
			if (_hideDesktopIcons)
				_desktop.RestoreIcons();
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
			if (_suspend)
			{
				Log.Window("EVENT", $"SUSPENDED, ignoring {type}", window);
				return;
			}

			if (type == WindowUpdateType.Foreground)
			{
				// Skip rapid focus changes to prevent scene switching loops
				if (IsRapidFocusChange())
				{
					Log.Window("FOCUS", "RAPID focus change, skipping", window);
					return;
				}

				Log.Window("FOCUS", "Foreground change", window);

				_lastFocusedWindow = window; // remember for scene restore
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
			// Some applications surface a previously hidden window with a simple ShowWindow
			// call that does NOT bring the window to the foreground. In that case the
			// window is visible but still carries WS_EX_TRANSPARENT from our hide logic
			// and is therefore not clickable. Treat a Show event as a signal that the
			// application wants to interact again and restore normal interactivity.
			else if (type == WindowUpdateType.Show)
			{
				Log.Window("EVENT", "Show", window);

				// Option2: Make Show event authoritative for current scene windows
				var scene = FindSceneForWindow(window);
				if (scene is not null && ReferenceEquals(scene, _current))
				{
					// If the window is minimized but just got shown, ensure it is restored
					if (window.IsMinimized)
					{
						Log.Window("EVENT", "Show: restoring minimized window in current scene", window);
						window.ShowNormal();
					}

					// Force clearing opacity/mouse-through regardless of skip checks
					WindowStrategy.Show(window);
				}
				else
				{
					// Restore normal interactivity for non-active scenes;
					// WindowStrategy.Show handles its own skip-checks internally.
					WindowStrategy.Show(window);
				}

				// Only switch scenes if this is actually a focus change, not just a show event
				// This prevents scene creation for minimized windows that shouldn't create scenes
				if (window.IsFocused)
				{
					Log.Window("EVENT", "Show + focused → switching scene", window);
					// Bring Stage Manager's focus model in sync by switching to the scene
					// containing this window. This guarantees proper stacking order and
					// icon visibility handling.
					SwitchToSceneByWindow(window).SafeFireAndForget();
				}
			}
		}

		private bool IsBlankDesktopClick(IntPtr handle)
		{
			// Determine window class
			var sb = new StringBuilder(256);
			Win32.GetClassName(handle, sb, sb.Capacity);
			var cls = sb.ToString();

			// Ignore taskbar / other common shells
			if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase))
				return false;

			// Helper local function to evaluate selection count on a SysListView32 window
			static bool IsListViewSelectionEmpty(IntPtr listView)
			{
				if (listView == IntPtr.Zero)
					return true;

				var sel = Win32.SendMessage(listView, Win32.LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero);
				return sel == IntPtr.Zero;
			}

			// Desktop background container windows (WorkerW/Progman)
			if (string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase))
			{
				// A click on these windows is only considered a blank desktop click when
				// no desktop icons are currently selected. Otherwise it is an icon click.

				// Find the SHELLDLL_DefView child hosting the desktop list view
				var shell = Desktop.FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null);
				// Within the DefView find the SysListView32 control that displays the icons
				var listView = shell != IntPtr.Zero ? Desktop.FindWindowEx(shell, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;

				return IsListViewSelectionEmpty(listView);
			}

			// Desktop icon view (list view) – ensure no icon is selected
			if (string.Equals(cls, "SysListView32", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
			{
				return IsListViewSelectionEmpty(handle);
			}

			return false;
		}

		private void WindowsManager_UntrackedFocus(object? sender, IntPtr e)
		{
			// Let dedicated mouse-click handler manage desktop toggling.
			if (IsBlankDesktopClick(e))
				return;

			// Potentially remember desktop view handle for future use
			if (!_desktop.HasDesktopView)
				_desktop.TrySetDesktopView(e);

			// No scene switching here – handled by DesktopShortClick or other logic.
		}

		private void WindowsManager_DesktopShortClick(object? sender, IntPtr handle)
		{
			if (_suspend)
				return;

			// Only treat clicks on truly blank desktop areas as a toggle trigger
			if (!IsBlankDesktopClick(handle))
			{
				Log.Info("DESKTOP", "Click on desktop icon, not blank area — ignoring", handle);
				return;
			}

			// Debounce additional toggles happening too quickly (double-click already filtered by WindowsManager)
			var now = DateTime.Now;
			if ((now - _lastDesktopToggle).TotalMilliseconds <100)
			{
				Log.Info("DESKTOP", "Debounced desktop toggle");
				return;
			}

			_lastDesktopToggle = now;

			if (_current is null)
			{
				Log.Action($"Desktop click → restore last scene '{_lastScene?.Title}'");
				if (_lastScene is object)
					SwitchTo(_lastScene).SafeFireAndForget();
			}
			else
			{
				Log.Action($"Desktop click → show desktop (hiding '{_current?.Title}')");
				SwitchTo(null).SafeFireAndForget();
			}
		}

		private void WindowsManager_WindowDestroyed(IWindow window)
		{
			Log.Window("EVENT", "WindowDestroyed", window);

			OpacityWindowStrategy.CleanupWindow(window.Handle);

			var scene = FindSceneForWindow(window);

			if (scene is not null)
			{
				scene.Remove(window);
				Log.Scene("Window removed from scene", scene, window);

				if (scene.Windows.Any())
				{
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));

					// If the removed window was focused, ensure another window from the same scene is shown shortly.
					if (ReferenceEquals(scene, _current))
					{
						Task.Run(async () =>
						{
							await Task.Delay(300);
							var first = scene.Windows.FirstOrDefault();
							if (first is object)
							{
								// Reveal and focus the first remaining window of the current scene
								WindowStrategy.Show(first);
								first.Focus();
							}
						});
					}
				}
				else
				{
					Log.Scene("Scene empty, removing", scene);
					lock (_scenesLock)
						_scenes.Remove(scene);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Removed));

					// If current scene became empty, switch to the first available scene after a short delay.
					if (ReferenceEquals(scene, _current))
					{
						Task.Run(async () =>
						{
							await Task.Delay(200);
							Scene fallback;
							lock (_scenesLock)
								fallback = _scenes.FirstOrDefault(s => s.Windows.Any());
							await SwitchTo(fallback).ConfigureAwait(false);
						});
					}
				}
			}
		}

		public Scene FindSceneForWindow(IWindow window) => FindSceneForWindow(window.Handle);

		public Scene FindSceneForWindow(IntPtr handle)
		{
			lock (_scenesLock)
				return _scenes?.FirstOrDefault(s => s.Windows.Any(w => w.Handle == handle));
		}

		private Scene FindSceneForProcess(string processName)
		{
			lock (_scenesLock)
				return _scenes?.FirstOrDefault(s => string.Equals(s.Key, processName, StringComparison.OrdinalIgnoreCase));
		}

		private async void WindowsManager_WindowCreated(IWindow window, bool firstCreate)
		{
			SwitchToSceneByNewWindow(window).SafeFireAndForget();
		}

		private async Task SwitchToSceneByWindow(IWindow window)
		{
			// Keep persistent windows (e.g. Teams meeting pop-ups) outside of scene logic.
			if (IsPersistentWindow(window))
			{
				Log.Window("SCENE", "Persistent window, skipping scene logic", window);
				return;
			}

			// Only create/switch scenes for windows that are actually focused, not just shown
			// This prevents scene creation for minimized windows that get Show events without focus
			if (!window.IsFocused)
			{
				Log.Window("SCENE", "Window not focused, skipping scene switch", window);
				return;
			}

			var scene = FindSceneForWindow(window);
			if (scene is null)
			{
				scene = new Scene(GetWindowGroupKey(window), window);
				lock (_scenesLock)
					_scenes.Add(scene);
				Log.Scene("Created new scene for window", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
			}
			else
			{
				Log.Scene("Switching to existing scene", scene, window);
			}

			if (AnimatedSwitch != null)
				await AnimatedSwitch(scene);
			else
				await SwitchTo(scene);
		}

		private async Task SwitchToSceneByNewWindow(IWindow window)
		{
			// Keep persistent windows (e.g. Teams meeting pop-ups) outside of scene logic.
			if (IsPersistentWindow(window))
			{
				Log.Window("SCENE", "New persistent window, skipping", window);
				return;
			}

			// Only create/switch scenes for windows that are actually focused, not just created
			// This prevents scene creation for new windows that don't have focus yet
			if (!window.IsFocused)
			{
				Log.Window("SCENE", "New window not focused, skipping", window);
				return;
			}

			// Use the group key (process id) consistently to guarantee a new process -> new scene
			var key = GetWindowGroupKey(window);
			var existentScene = FindSceneForProcess(key);
			var scene = existentScene ?? new Scene(key, window);

			if (existentScene is null)
			{
				lock (_scenesLock)
					_scenes.Add(scene);
				Log.Scene("New window → new scene created", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
			}
			else
			{
				scene.Add(window);
				Log.Scene("New window → added to existing scene", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			await SwitchTo(scene).ConfigureAwait(true);
		}

		/// <summary>
		/// Determines if a scene is switched back to shortly after it has been hidden.
		/// This can happen if an app activates one of it's windows after being hidde,
		/// like Microsoft Teams does if there's a small floating window for a current call.
		/// </summary>
		/// <param name="scene"></param>
		/// <returns></returns>
		/// <summary>
		/// Determines if focus changes are happening too rapidly to indicate system vs user interaction
		/// This helps prevent scene switching loops from automatic focus changes
		/// </summary>
		/// <returns></returns>
		private bool IsRapidFocusChange()
		{
			var now = DateTime.Now;
			if ((now - _lastFocusChange).TotalMilliseconds <100) // Less than100ms since last focus change
			{
				Log.Info("FOCUS", "Rapid focus change detected, filtering");
				_lastFocusChange = now;
				return true; // This is a rapid focus change
			}
			_lastFocusChange = now;
			return false;
		}

		public async Task<bool> SwitchTo(Scene? scene)
		{
			if (object.Equals(scene, _current))
			{
				Log.Info("SWITCH", $"Already on scene '{scene?.Title}', skipping");
				return false;
			}

			Log.Info("SWITCH", $"SwitchTo START: '{_current?.Title}' → '{scene?.Title ?? "(desktop)"}'");

			IWindow focusCandidate = null;

			try
			{
				_suspend = true;

				// Determine the window that currently has the keyboard focus (foreground).
				var foregroundHandle = Win32.GetForegroundWindow();

				// When switching to a scene, skip the foreground window (it gets focus handling separately).
				// When switching to desktop (scene=null), hide ALL windows including the foreground.
				var otherWindows = GetSceneableWindows()
					.Except(scene?.Windows ?? Array.Empty<IWindow>())
					.Where(w => scene is null || w.Handle != foregroundHandle)
					.ToArray();

				var prior = _current;
				_current = scene;

				Scene[] scenesSnapshot;
				lock (_scenesLock)
					scenesSnapshot = _scenes.ToArray();
				foreach (var s in scenesSnapshot)
				{
					s.IsSelected = s.Equals(scene);
				}

				Log.Info("SWITCH", $"Hiding {otherWindows.Length} windows");
				foreach (var o in otherWindows)
				{
					Log.Window("HIDE", "Hiding", o);
					WindowStrategy.Hide(o);
				}

				// Phase2: bring in target-scene windows.
				if (scene is object)
				{
					Log.Info("SWITCH", $"Showing {scene.Windows.Count()} windows in target scene");
					foreach (var w in scene.Windows)
					{
						// Option1: Restore-then-clear for any minimized window in the active scene
						if (w.IsMinimized)
						{
							Log.Window("SHOW", "Restoring minimized", w);
							w.ShowNormal();
						}

						Log.Window("SHOW", "Showing", w);
						// Always clear any previous opacity/click-through for active scene windows
						WindowStrategy.Show(w);
					}

					// Determine which window should get focus after restore – pick the last
					// focused window if it belongs to the scene and is not minimised, otherwise the first visible one.
					if (_lastFocusedWindow is object && scene.Windows.Contains(_lastFocusedWindow) && !_lastFocusedWindow.IsMinimized)
						focusCandidate = _lastFocusedWindow;
					else
						focusCandidate = scene.Windows.FirstOrDefault(w => !w.IsMinimized);

					Log.Window("SWITCH", "Focus candidate", focusCandidate ?? scene.Windows.FirstOrDefault());
				}

				CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current));

				if (scene is null)
				{
					_lastScene = prior;
					if (_hideDesktopIcons)
					{
						Log.Info("DESKTOP", "Showing desktop icons (switched to desktop view)");
						_desktop.ShowIcons();
					}
				}
				else
				{
					_lastScene = null;
					if (_hideDesktopIcons)
					{
						Log.Info("DESKTOP", "Hiding desktop icons (switched to scene)");
						_desktop.HideIcons();
					}
				}
			}
			finally
			{
				_suspend = false;

				// Apply focus once suspension lifted
				if (focusCandidate is object)
					focusCandidate.Focus();

				Log.Info("SWITCH", $"SwitchTo END: now on '{_current?.Title ?? "(desktop)"}'");
			}

			return true;
		}

		public Task MoveWindow(Scene sourceScene, IWindow window, Scene targetScene)
		{
			try
			{
				_suspend = true;

				if (sourceScene is null || sourceScene.Equals(targetScene))
					return Task.CompletedTask;

				Log.Window("MOVE", $"Moving from '{sourceScene.Title}' → '{targetScene.Title}'", window);

				sourceScene.Remove(window);
				targetScene.Add(window);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(targetScene, window, ChangeType.Updated));

				if (!sourceScene.Windows.Any())
				{
					Log.Scene("Source scene empty after move, removing", sourceScene);
					lock (_scenesLock)
						_scenes.Remove(sourceScene);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Removed));
				}

				if (targetScene.Equals(_current))
				{
					if (window.IsMinimized)
					{
						Log.Window("MOVE", "Restoring minimized window before showing", window);
						Win32Helper.SetAlpha(window.Handle, 0);
						window.ShowNormal();
					}
					Log.Window("MOVE", "Target is current scene, showing window", window);
					WindowStrategy.Show(window);
					window.Focus();
				}
				else
				{
					Log.Window("MOVE", "Target is not current scene, hiding window", window);
					WindowStrategy.Hide(window);

					// reset window position after move so that the window is back at the starting position on the new scene
					if (window is WindowsWindow w && w.PopLastLocation() is IWindowLocation l)
						Win32.SetWindowPos(window.Handle, IntPtr.Zero, l.X, l.Y,0,0, Win32.SetWindowPosFlags.IgnoreResize);
				}

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		public async Task MoveWindow(IntPtr handle, Scene targetScene)
		{
			var source = FindSceneForWindow(handle);

			if (source is null || source.Equals(targetScene))
				return;

			var window = source.Windows.First(w => w.Handle == handle);
			await MoveWindow(source, window, targetScene);
		}

		public async Task PopWindowFrom(Scene sourceScene)
		{
			if (sourceScene is null || _current is null || sourceScene.Equals(_current))
				return;

			var window = sourceScene.Windows.LastOrDefault();

			if (window is object)
			{
				Log.Window("DRAG", $"Pulling window from '{sourceScene.Title}' into '{_current.Title}'", window);
				await MoveWindow(sourceScene, window, _current).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Removes a window from its current scene and creates a new scene for it in the sidebar.
		/// The window is hidden (alpha→0). Returns the new scene, or null if the operation was skipped.
		/// </summary>
		public Scene SeparateWindowToNewScene(IWindow window)
		{
			var source = FindSceneForWindow(window);
			if (source == null || !source.Equals(_current))
			{
				Log.Window("DRAG", "SeparateWindow skipped: not in current scene", window);
				return null;
			}

			if (source.Windows.Count() <= 1)
			{
				Log.Window("DRAG", "SeparateWindow skipped: last window in scene", window);
				return null;
			}

			try
			{
				_suspend = true;

				Log.Window("DRAG", $"Separating from '{source.Title}' into new scene", window);

				source.Remove(window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, window, ChangeType.Updated));

				var newScene = new Scene(GetWindowGroupKey(window), window);
				lock (_scenesLock)
					_scenes.Add(newScene);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(newScene, window, ChangeType.Created));

				WindowStrategy.Hide(window);

				return newScene;
			}
			finally
			{
				_suspend = false;
			}
		}

		private IEnumerable<IWindow> GetSceneableWindows() => WindowsManager?.Windows?.Where(w => !IsPersistentWindow(w) && w.CanLayout && !string.IsNullOrEmpty(w.ProcessFileName) && !string.IsNullOrEmpty(w.Title));

		public IEnumerable<Scene> GetScenes()
		{
			lock (_scenesLock)
			{
				if (_scenes is null)
				{
					var restorePath = App.RestoreScenesPath;
					if (restorePath != null)
					{
						_scenes = RestoreScenesFromSnapshot(restorePath);
						UpdateService.CleanupStagingFolder();
					}

					if (_scenes is null)
					{
						_scenes = GetSceneableWindows()
							// Include all windows during initial startup (including minimized ones) for automatic scene population
							.Where(w => Win32.IsWindowVisible(w.Handle) || w.IsMinimized)
							.GroupBy(GetWindowGroupKey)
							.Select(group => new Scene(group.Key, group.ToArray()))
							.ToList();
					}

					Log.Info("STARTUP", $"Initial scenes: {_scenes.Count}");
					foreach (var scene in _scenes)
						Log.Scene("Initial scene", scene);
				}

				return _scenes.ToList();
			}
		}

		public SceneSnapshot.Snapshot CreateSnapshot()
		{
			Scene[] snap;
			Scene? currentScene;
			lock (_scenesLock)
			{
				snap = _scenes?.ToArray() ?? Array.Empty<Scene>();
				currentScene = _current;
			}

			var activeHandles = currentScene?.Windows.Select(w => (long)w.Handle).ToArray() ?? Array.Empty<long>();
			var sceneEntries = snap.Select(s => new SceneSnapshot.SceneEntry(
				s.Key,
				s.Windows.Select(w => (long)w.Handle).ToArray()
			)).ToArray();

			return new SceneSnapshot.Snapshot(activeHandles, sceneEntries);
		}

		private List<Scene>? RestoreScenesFromSnapshot(string path)
		{
			var snapshot = SceneSnapshot.Load(path);
			if (snapshot is null)
			{
				Log.Info("STARTUP", "Scene snapshot missing or corrupt, falling back to default grouping");
				return null;
			}

			var allWindows = GetSceneableWindows()
				.Where(w => Win32.IsWindowVisible(w.Handle) || w.IsMinimized)
				.ToDictionary(w => (long)w.Handle);

			var claimed = new HashSet<long>();
			var scenes = new List<Scene>();

			foreach (var entry in snapshot.Scenes)
			{
				var validWindows = entry.Handles
					.Where(h => Win32.IsWindow((IntPtr)h) && allWindows.ContainsKey(h))
					.Select(h => { claimed.Add(h); return allWindows[h]; })
					.ToArray();

				if (validWindows.Length > 0)
				{
					scenes.Add(new Scene(entry.Key, validWindows));
					Log.Info("STARTUP", $"Restored scene '{entry.Key}' with {validWindows.Length}/{entry.Handles.Length} windows");
				}
			}

			// Unclaimed windows get default PID grouping
			var unclaimed = allWindows
				.Where(kv => !claimed.Contains(kv.Key))
				.Select(kv => kv.Value);

			var defaultScenes = unclaimed
				.GroupBy(GetWindowGroupKey)
				.Select(g => new Scene(g.Key, g.ToArray()));
			scenes.AddRange(defaultScenes);

			Log.Info("STARTUP", $"Scene restore complete: {scenes.Count} scenes ({claimed.Count} windows restored)");
			return scenes.Count > 0 ? scenes : null;
		}

		public bool IsCurrentScene(Scene scene) => object.Equals(scene, _current);

		public bool IsDesktopView => _current is null;

		public IEnumerable<IWindow> GetCurrentWindows() => _current?.Windows ?? GetSceneableWindows();

		/// <summary>
		/// Instantly hides all windows in the current scene (alpha→0) without doing a full SwitchTo.
		/// Used by the transition animation so the outgoing placeholder covers the real window.
		/// </summary>
		public void HideCurrentSceneWindows()
		{
			if (_current == null)
			{
				Log.Info("SWITCH", "Pre-hide: no current scene, nothing to hide");
				return;
			}
			var count = _current.Windows.Count();
			Log.Info("SWITCH", $"Pre-hiding {count} windows in '{_current.Title}' for animation");
			foreach (var w in _current.Windows)
				WindowStrategy.Hide(w);
		}


		/// <summary>
		/// Restores minimized windows in a scene at alpha=0 so they have real screen positions
		/// but are invisible. Prevents the Windows taskbar restore animation on first switch.
		/// </summary>
		public void RestoreMinimizedInvisibly(Scene scene)
		{
			if (scene == null) return;
			foreach (var w in scene.Windows.Where(w => w.IsMinimized))
			{
				Win32Helper.SetAlpha(w.Handle, 0);
				Log.Window("SWITCH", "Silent restore (minimized→alpha=0)", w);
				w.ShowNormal();
			}
		}

		/// <summary>
		/// Shows desktop icons immediately (used when setting is disabled)
		/// </summary>
		public void ShowDesktopIcons()
		{
			_desktop.ShowIcons();
		}

		/// <summary>
		/// Hides desktop icons immediately (used when setting is enabled)
		/// </summary>
		public void HideDesktopIcons()
		{
			_desktop.HideIcons();
		}

		// Group windows by **process id** instead of the process name so that every
		// newly-launched program (i.e. a new process, even if it shares the same
		// executable name with another instance) gets its **own** scene.
		//
		// This fulfils the requirement that launching a new program should ALWAYS
		// create a separate scene.
		private string GetWindowGroupKey(IWindow window) => window.ProcessId.ToString();

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					// Already handled by Stop() method which should be called explicitly
					// But ensure cleanup in case Dispose is called directly
					Stop();
				}
				_disposed = true;
			}
		}
	}
}
