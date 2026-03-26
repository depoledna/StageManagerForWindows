using AsyncAwaitBestPractices;
using Microsoft.Xaml.Behaviors.Core;
using SharpHook;
using StageManager.Animations;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Interop;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StageManager
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
		private const int TIMERINTERVAL_MILLISECONDS = 500;
		private const int MAX_SCENES = 6;
		private const string APP_NAME = "StageManager";
		private IntPtr _thisHandle;
		private TaskPoolGlobalHook _hook;
		private WindowMode _mode;
		private double _lastWidth;
		private Timer _overlapCheckTimer;
		private Point _mouse = new Point(0, 0);
		private CancellationTokenSource _cancellationTokenSource;
		private SceneModel _removedCurrentScene;
		private SceneModel _mouseDownScene;
		private bool _hideDesktopIcons;
		private readonly SceneTransitionAnimator _sceneTransitionAnimator = new SceneTransitionAnimator();

		public event PropertyChangedEventHandler PropertyChanged;

		public bool EnableWindowDropToScene = false;
		public bool EnableWindowPullToScene = true;

		public bool HideDesktopIcons
		{
			get => _hideDesktopIcons;
			set
			{
				if (_hideDesktopIcons != value)
				{
					_hideDesktopIcons = value;
					Settings.SetHideDesktopIcons(value);
					RaisePropertyChanged(nameof(HideDesktopIcons));

					// Apply setting change immediately
					ApplyDesktopIconsSetting();
				}
			}
		}

		public MainWindow()
		{
			// Load initial setting BEFORE UI initialization
			_hideDesktopIcons = Settings.GetHideDesktopIcons();

			InitializeComponent();

			// Set DataContext AFTER setting is loaded
			DataContext = this;

			_overlapCheckTimer = new Timer(OverlapCheck, null, 2500, TIMERINTERVAL_MILLISECONDS);

			SwitchSceneCommand = new ActionCommand(async model =>
			{
				var sceneModel = (SceneModel)model;

				// Block entire command while a transition is in flight
				if (_sceneTransitionAnimator.IsAnimating)
				{
					Log.Info("TRANSITION", $"BLOCKED: click on '{sceneModel.Title}' while animation in progress");
					return;
				}

				// Bail out if already on this scene (avoids destructive setup with no matching SwitchTo)
				if (SceneManager != null && SceneManager.IsCurrentScene(sceneModel.Scene))
				{
					Log.Info("TRANSITION", $"Already on '{sceneModel.Title}', skipping");
					return;
				}

				var dpi = GetDpiScale();
				Log.Action($"Scene switch: '{_removedCurrentScene?.Title ?? "(none)"}' → '{sceneModel.Title}' | scenes={Scenes.Count} dpi={dpi.X:F2},{dpi.Y:F2}");

				var sidebarSlot = GetSceneThumbnailScreenBounds(sceneModel);
				var incomingTarget = GetSceneWindowBounds(sceneModel);
				Log.Info("TRANSITION", $"Bounds: sidebarSlot={sidebarSlot} incomingTarget={incomingTarget}");

				// Outgoing: current scene flies from its window position → the sidebar slot
				var outgoingModel = _removedCurrentScene;
				var outgoingSource = Rect.Empty;
				if (outgoingModel != null)
					outgoingSource = GetCurrentSceneWindowBounds();
				Log.Info("TRANSITION", $"Outgoing: model={outgoingModel != null} source={outgoingSource}");

				// Hide outgoing windows immediately (placeholder covers the transition)
				if (outgoingSource != Rect.Empty)
				{
					Log.Info("TRANSITION", "Pre-hiding current scene windows");
					SceneManager.HideCurrentSceneWindows();
				}

				// Collapse the clicked sidebar item so the placeholder is the only thing visible
				Log.Info("TRANSITION", $"Collapsing sidebar item '{sceneModel.Title}'");
				sceneModel.IsVisible = false;

				if (sidebarSlot != Rect.Empty && incomingTarget != Rect.Empty)
				{
					Log.Info("TRANSITION", "Starting animation");
					await _sceneTransitionAnimator.AnimateSceneTransitionAsync(
						sidebarSlot, incomingTarget, sceneModel,
						outgoingSource, sidebarSlot, outgoingModel);
					Log.Info("TRANSITION", "Animation completed");
				}
				else
				{
					Log.Info("TRANSITION", $"Skipping animation: sidebarSlot={sidebarSlot == Rect.Empty} incomingTarget={incomingTarget == Rect.Empty}");
				}

				Log.Info("TRANSITION", "Calling SwitchTo");
				await SceneManager!.SwitchTo(sceneModel.Scene);
				Log.Info("TRANSITION", $"SwitchTo completed, scenes={Scenes.Count}");
			});
		}

		protected override void OnInitialized(EventArgs e)
		{
			base.OnInitialized(e);

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			_lastWidth = Width;

			StartHook();	
		}

		protected override void OnClosed(EventArgs e)
		{
			// Cancel all background operations
			_cancellationTokenSource?.Cancel();
			_cancellationTokenSource?.Dispose();

			// Unsubscribe from SceneManager events before stopping to prevent memory leaks
			if (SceneManager != null)
			{
				SceneManager.SceneChanged -= SceneManager_SceneChanged;
				SceneManager.CurrentSceneSelectionChanged -= SceneManager_CurrentSceneSelectionChanged;
			}

			StopHook();

			// Dispose the overlap check timer to stop background operations
			_overlapCheckTimer?.Dispose();

			trayIcon.Dispose();

			// Dispose SceneManager properly
			SceneManager?.Dispose();

			// Clean up animation overlay
			_sceneTransitionAnimator?.Dispose();

			base.OnClosed(e);

			Environment.Exit(0);
		}

		protected override async void OnContentRendered(EventArgs e)
		{
			base.OnContentRendered(e);

			Log.Info("STARTUP", "MainWindow content rendered, initializing...");

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

			var windowsManager = new WindowsManager();
			SceneManager = new SceneManager(windowsManager, HideDesktopIcons);

			// Ensure SceneManager.Start() is called on the main thread
			if (Dispatcher.CheckAccess())
			{
				await SceneManager.Start();
			}
			else
			{
				await Dispatcher.InvokeAsync(async () => await SceneManager.Start());
			}

			SceneManager.SceneChanged += SceneManager_SceneChanged;
			SceneManager.CurrentSceneSelectionChanged += SceneManager_CurrentSceneSelectionChanged;

			AddInitialScenes();

			// Initialize cancellation token source for background operations
			_cancellationTokenSource = new CancellationTokenSource();

			// Schedule a late initialization pass to recalculate thumbnail sizes after all window information is available.
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(2000, _cancellationTokenSource.Token).ConfigureAwait(false);
					if (!_cancellationTokenSource.Token.IsCancellationRequested)
					{
						Dispatcher.Invoke(() =>
						{
							foreach (var scene in Scenes)
								scene.UpdatePreviewSizes();
						});
					}
				}
				catch (OperationCanceledException)
				{
					// Expected during shutdown, ignore
				}
			});

			var foreground = Win32.GetForegroundWindow();
			var foregroundScene = SceneManager.FindSceneForWindow(foreground);
			if (foregroundScene is object)
				await SceneManager.SwitchTo(foregroundScene).ConfigureAwait(true);
		}

		private void AddInitialScenes()
		{
			var initialScenes = SceneManager.GetScenes().ToArray();
			Log.Info("STARTUP", $"Adding {initialScenes.Length} initial scenes to sidebar");
			for (int i = 0; i < initialScenes.Length; i++)
			{
				var model = SceneModel.FromScene(initialScenes[i]);
				model.IsVisible = i <= MAX_SCENES; // i is zero based, so it should be i+1 but one scene gets selected (and removed from the sidebar) that makes i+0 again
				Scenes.Add(model);
				Log.Info("STARTUP", $"  Scene[{i}]: '{model.Title}' visible={model.IsVisible} windows={model.Windows.Count}");
			}
		}

		private void SceneManager_CurrentSceneSelectionChanged(object? sender, CurrentSceneSelectionChangedEventArgs args)
		{
			// Ensure we are on the UI/Dispatcher thread before mutating observable collections bound to UI
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.Invoke(() => SceneManager_CurrentSceneSelectionChanged(sender, args));
				return;
			}

			var currentModel = args.Current is null ? null : Scenes.FirstOrDefault(m => m.Id == args.Current.Id);
			Log.Info("SIDEBAR", $"SelectionChanged: current='{args.Current?.Title ?? "(null)"}' prior='{args.Prior?.Title ?? "(null)"}' removedCurrent='{_removedCurrentScene?.Title ?? "(null)"}' scenes={Scenes.Count}");

			if (currentModel is object)
			{
				var currentIndex = Scenes.IndexOf(currentModel);
				Log.Info("SIDEBAR", $"Removing '{currentModel.Title}' at index {currentIndex}, inserting '{_removedCurrentScene?.Title ?? "(null)"}'");
				Scenes.RemoveAt(currentIndex);

				if (_removedCurrentScene is object)
					Scenes.Insert(currentIndex, _removedCurrentScene);
			}
			else
			{
				Log.Info("SIDEBAR", $"Current not found in Scenes, appending '{_removedCurrentScene?.Title ?? "(null)"}'");
				if (_removedCurrentScene is object)
					Scenes.Add(_removedCurrentScene);
			}

			_removedCurrentScene = currentModel;
			Log.Info("SIDEBAR", $"State: _removedCurrentScene='{_removedCurrentScene?.Title ?? "(null)"}' scenes={Scenes.Count} visible={Scenes.Count(s => s.IsVisible)}");

			SyncVisibilityByUpdatedTimeStamp();
		}

		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
			var area = this.GetMonitorWorkSize();
			this.Left = 0;
			this.Top = 0;
			this.Height = area.Height;
		}

		private void SceneManager_SceneChanged(object sender, SceneChangedEventArgs e)
		{
			this.Dispatcher.Invoke(() =>
			{
				Log.Info("UI", $"SceneChanged: {e.Change} scene='{e.Scene?.Title}'");

				switch (e.Change)
				{
					case ChangeType.Created:
						Scenes.Add(SceneModel.FromScene(e.Scene));
						SyncVisibilityByUpdatedTimeStamp();
						break;
					case ChangeType.Updated:
						if (AllScenes.FirstOrDefault(s => s.Id == e.Scene.Id) is SceneModel toUpdate)
							toUpdate.UpdateFromScene(e.Scene);
						break;
					case ChangeType.Removed:
						if (AllScenes.FirstOrDefault(s => s.Id == e.Scene.Id) is SceneModel toRemove)
						{
							if (toRemove.Equals(_removedCurrentScene))
								_removedCurrentScene = null;
							else
								Scenes.Remove(toRemove);
						}
						SyncVisibilityByUpdatedTimeStamp();
						break;
				}
			});
		}

		private void OnMousePressed(object? sender, MouseHookEventArgs e)
		{
			// if it's allowed to drag windows into scenes, we cannot hide the scenes
			if (EnableWindowDropToScene)
				_overlapCheckTimer.Change(TimeSpan.Zero, TimeSpan.Zero);

			var foregroundWindow = Win32.GetForegroundWindow();
			if (foregroundWindow != _thisHandle)
				return;

			if (EnableWindowPullToScene)
			{
				var screenPoint = new Point(e.Data.X, e.Data.Y);
				this.Dispatcher.Invoke(() =>
				{
					_mouseDownScene = FindSceneByPoint(screenPoint);
				});
			}
		}

		private void OnMouseReleased(object? sender, MouseHookEventArgs e)
		{
			// if it's allowed to drag windows into scenes, we cannot hide the scenes
			if (EnableWindowDropToScene)
			{
				_overlapCheckTimer.Change(0, TIMERINTERVAL_MILLISECONDS);

				var foregroundWindow = Win32.GetForegroundWindow();

				if (foregroundWindow == _thisHandle)
					return;

				var screenPoint = new Point(e.Data.X, e.Data.Y);
				this.Dispatcher.Invoke(() =>
				{
					var sceneModel = FindSceneByPoint(screenPoint);

					if (sceneModel is object)
					{
						Log.Info("DRAG", $"Dropped window onto scene '{sceneModel.Title}'");
						SceneManager.MoveWindow(foregroundWindow, sceneModel.Scene).SafeFireAndForget();
					}
				});
			}

			if (EnableWindowPullToScene)
			{
				if (e.Data.X > _lastWidth && _mouseDownScene is object)
				{
					Log.Info("DRAG", $"Pulled window from scene '{_mouseDownScene.Title}' (mouseX={e.Data.X} > sidebarWidth={_lastWidth})");
					this.Dispatcher.Invoke(() =>
					{
						SceneManager.PopWindowFrom(_mouseDownScene.Scene).SafeFireAndForget();
					});
				}
			}
		}

		private SceneModel FindSceneByPoint(Point p)
		{
			var thisWindow = new WindowsWindow(_thisHandle);
			var pointOnWindow = new Point(p.X - thisWindow.Location.X, p.Y - thisWindow.Location.Y);

			var dpi = VisualTreeHelper.GetDpi(this);

			pointOnWindow.X /= dpi.DpiScaleX;
			pointOnWindow.Y /= dpi.DpiScaleY;

			SceneModel model = null;

			var element = VisualTreeHelper.HitTest(this, pointOnWindow)?.VisualHit;

			while (element is not null)
			{
				if (element is FrameworkElement { DataContext: SceneModel m })
				{
					model = m;
					break;
				}

				element = element.GetParentObject();
			}

			return model;
		}
		private void SyncVisibilityByUpdatedTimeStamp()
		{
			var scenes = Scenes.OrderByDescending(s => s.Updated).ToArray();
			for (int i = 0; i < scenes.Length; i++)
				scenes[i].IsVisible = i < MAX_SCENES;
		}

		public ObservableCollection<SceneModel> Scenes { get; } = new ObservableCollection<SceneModel>();

		public IEnumerable<SceneModel> AllScenes => Scenes.Union(new[] { _removedCurrentScene });

		public ICommand SwitchSceneCommand { get; }

		public SceneManager SceneManager { get; private set; }

		public IntPtr Handle => _thisHandle;

		public WindowMode Mode
		{
			get => _mode;
			set
			{
				if (value == _mode)
					return;

				Log.Info("MODE", $"Sidebar mode: {_mode} → {value}");

				_mode = value;

				this.Topmost = value == WindowMode.Flyover;

				ApplyWindowMode();
			}
		}

		private void ApplyWindowMode()
		{
			var newLeft = Mode == StageManager.WindowMode.OffScreen ? (-1 * Width) : 0.0;
			if (Left == newLeft)
				return;

			var isIncoming = newLeft > Left;
			var easingMode = isIncoming ? EasingMode.EaseOut : EasingMode.EaseIn;

			var animation = new DoubleAnimationUsingKeyFrames();
			animation.Duration = new Duration(TimeSpan.FromSeconds(0.5));
			var easingFunction = new PowerEase { EasingMode = easingMode };
			animation.KeyFrames.Add(new EasingDoubleKeyFrame(Left, KeyTime.FromPercent(0)));
			animation.KeyFrames.Add(new EasingDoubleKeyFrame(newLeft, KeyTime.FromPercent(1.0), easingFunction));

			BeginAnimation(LeftProperty, animation);
		}

		private void StartHook()
		{
			_hook = new TaskPoolGlobalHook();

			_hook.MousePressed += OnMousePressed;
			_hook.MouseReleased += OnMouseReleased;
			_hook.MouseMoved += _hook_MouseMoved;

			Task.Run(_hook.Run);
		}

		private void StopHook()
		{
			_hook.MousePressed -= OnMousePressed;
			_hook.MouseReleased -= OnMouseReleased;
			_hook.MouseMoved -= _hook_MouseMoved;

			try
			{
				_hook.Dispose();
			}
			catch (HookException)
			{
			}
		}

		private void _hook_MouseMoved(object? sender, MouseHookEventArgs e)
		{
			_mouse.X = e.Data.X;
			_mouse.Y = e.Data.Y;

			if (Mode == WindowMode.OffScreen && e.Data.X <= 44)
			{
				Dispatcher.Invoke(() => Mode = WindowMode.Flyover);
			}
		}

		private void OverlapCheck(object? _)
		{
			var currentWindows = SceneManager.GetCurrentWindows().ToArray(); // in case the enumeration changes
			UpdateModeByWindows(currentWindows);
		}

		private void UpdateModeByWindows(IEnumerable<IWindow> windows)
		{
			bool doesOverlap(IWindowLocation loc) => loc.State == Native.Window.WindowState.Maximized || (loc.State == Native.Window.WindowState.Normal && (loc.X * 2) < _lastWidth);

			var anyOverlappingWindows = windows.Any(w => doesOverlap(w.Location));

			var containsMouse = _mouse.X <= _lastWidth;
			var setMode = Mode == WindowMode.OnScreen && !containsMouse
							|| Mode == WindowMode.OffScreen
							|| (Mode == WindowMode.Flyover && !containsMouse);

			if (setMode)
			{
				Dispatcher.Invoke(() =>
				{
					Mode = anyOverlappingWindows ? WindowMode.OffScreen : WindowMode.OnScreen;
				});
			}
		}

		/// <summary>
		/// Gets the DPI scale factors for converting between physical and logical coordinates.
		/// </summary>
		private Point GetDpiScale()
		{
			var source = PresentationSource.FromVisual(this);
			if (source?.CompositionTarget != null)
				return new Point(source.CompositionTarget.TransformToDevice.M11, source.CompositionTarget.TransformToDevice.M22);
			return new Point(1.0, 1.0);
		}

		/// <summary>
		/// Returns the screen bounds of a scene's thumbnail in WPF logical (DPI-independent) units.
		/// </summary>
		private Rect GetSceneThumbnailScreenBounds(SceneModel sceneModel)
		{
			try
			{
				var container = scenesControl.ItemContainerGenerator.ContainerFromItem(sceneModel) as FrameworkElement;
				if (container == null)
					return Rect.Empty;

				var dpi = GetDpiScale();

				// Get screen coordinates (physical pixels) then convert to logical units
				var topLeft = container.TranslatePoint(new Point(0, 0), this);
				var bottomRight = container.TranslatePoint(new Point(container.ActualWidth, container.ActualHeight), this);

				var screenTopLeft = PointToScreen(topLeft);
				var screenBottomRight = PointToScreen(bottomRight);

				return new Rect(
					screenTopLeft.X / dpi.X,
					screenTopLeft.Y / dpi.Y,
					(screenBottomRight.X - screenTopLeft.X) / dpi.X,
					(screenBottomRight.Y - screenTopLeft.Y) / dpi.Y);
			}
			catch
			{
				return Rect.Empty;
			}
		}

		/// <summary>
		/// Returns the bounds of the scene's primary window in WPF logical (DPI-independent) units.
		/// Falls back to the monitor work area if all windows are minimized.
		/// </summary>
		private Rect GetSceneWindowBounds(SceneModel sceneModel)
		{
			try
			{
				var window = sceneModel.Scene.Windows.FirstOrDefault(w => !w.IsMinimized)
					?? sceneModel.Scene.Windows.FirstOrDefault();

				if (window == null)
					return GetWorkAreaBounds();

				var loc = window.Location;
				if (loc.Width <= 0 || loc.Height <= 0)
					return GetWorkAreaBounds();

				var dpi = GetDpiScale();

				// Location is in physical pixels — convert to WPF logical units
				return new Rect(
					loc.X / dpi.X,
					loc.Y / dpi.Y,
					loc.Width / dpi.X,
					loc.Height / dpi.Y);
			}
			catch
			{
				return GetWorkAreaBounds();
			}
		}

		/// <summary>
		/// Returns the bounds of the currently focused scene's primary window in WPF logical units.
		/// </summary>
		private Rect GetCurrentSceneWindowBounds()
		{
			try
			{
				var window = SceneManager.GetCurrentWindows().FirstOrDefault(w => !w.IsMinimized)
					?? SceneManager.GetCurrentWindows().FirstOrDefault();

				if (window == null)
					return Rect.Empty;

				var loc = window.Location;
				if (loc.Width <= 0 || loc.Height <= 0)
					return Rect.Empty;

				var dpi = GetDpiScale();

				return new Rect(
					loc.X / dpi.X,
					loc.Y / dpi.Y,
					loc.Width / dpi.X,
					loc.Height / dpi.Y);
			}
			catch
			{
				return Rect.Empty;
			}
		}

		/// <summary>
		/// Returns the monitor work area in WPF logical (DPI-independent) units. Used as fallback.
		/// </summary>
		private Rect GetWorkAreaBounds()
		{
			try
			{
				var hwndSource = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
				if (hwndSource == null)
					return Rect.Empty;

				var dpi = GetDpiScale();

				var monitor = NativeMethods.MonitorFromWindow(hwndSource.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
				if (monitor == IntPtr.Zero)
					return Rect.Empty;

				var info = new NativeMethods.MONITORINFOEX();
				info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
				if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
					return Rect.Empty;

				// Convert physical pixel work area to WPF logical units
				return new Rect(
					info.rcWork.Left / dpi.X,
					info.rcWork.Top / dpi.Y,
					info.rcWork.Width / dpi.X,
					info.rcWork.Height / dpi.Y);
			}
			catch
			{
				return Rect.Empty;
			}
		}

		private void NavigateToProjectPage()
		{
			Process.Start(new ProcessStartInfo("https://github.com/awaescher/StageManager")
			{
				UseShellExecute = true
			});
		}

		public static bool StartsWithWindows
		{
			get => AutoStart.IsStartup(APP_NAME);
			set => AutoStart.SetStartup(APP_NAME, value);
		}

		private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));
		}

		private void ApplyDesktopIconsSetting()
		{
			if (SceneManager != null)
			{
				// Apply setting immediately by showing or hiding desktop icons
				if (_hideDesktopIcons)
				{
					// Hide desktop icons when setting is enabled
					SceneManager.HideDesktopIcons();
				}
				else
				{
					// Show desktop icons when setting is disabled
					SceneManager.ShowDesktopIcons();
				}
			}
		}

		private void MenuItem_ProjectPage_Click(object sender, RoutedEventArgs e)
		{
			NavigateToProjectPage();
		}

		private void MenuItem_Quit_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}

		private void ContextMenu_Closed(object sender, RoutedEventArgs e)
		{
			StartHook();
		}

		private void ContextMenu_Opened(object sender, RoutedEventArgs e)
		{
			StopHook();
		}
	}

	public enum WindowMode
	{
		OnScreen,
		OffScreen,
		Flyover
	}
}
