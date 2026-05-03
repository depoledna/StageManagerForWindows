using AsyncAwaitBestPractices;
using Microsoft.Xaml.Behaviors.Core;
using SharpHook;
using StageManager.Animations;
using StageManager.Controls;
using StageManager.Model;
using StageManager.Native;
using StageManager.Services;
using StageManager.Native.PInvoke;
using StageManager.Native.Interop;
using StageManager.Native.Window;
using System;
using System.IO;
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
		private long _mouseX;
		private CancellationTokenSource _cancellationTokenSource;
		private SceneModel _removedCurrentScene;
		private SceneModel _mouseDownScene;
		private bool _hideDesktopIcons;

		// WPF-native drag state (all UI thread, no cross-thread issues)
		private enum SidebarDragPhase { None, InSidebar, InBuffer, PastBuffer }
		private SceneModel _wpfDragScene;
		private Point _wpfDragStartPoint;
		private SidebarDragPhase _sidebarDragPhase;
		private bool IsSidebarDragging => _sidebarDragPhase != SidebarDragPhase.None;
		private IWindow _sidebarDragWindow;
		private Rect _sidebarDragThumbRect;
		private Rect _sidebarDragWindowRect;
		private Point _sidebarDragDpi;
		private double _sidebarDragBufferLeft;
		private double _sidebarDragBufferRight;
		private readonly SceneTransitionAnimator _sceneTransitionAnimator = new SceneTransitionAnimator();
		private readonly SidebarDragGhost _sidebarDragGhost;
		private readonly DebugZoneOverlay _debugZoneOverlay;

		private DragDropManager _dragDropManager;
		private readonly DragGhostWindow _dragGhostWindow = new DragGhostWindow();
		private readonly IconOverlayManager _iconOverlay = new();
		private readonly UpdateService _updateService = new();
		private string? _filterProcessKey;

		// Filter morph: enter decelerates (EaseOut), exit accelerates (EaseIn).
		private static readonly Duration _filterMorphDuration = new Duration(TimeSpan.FromMilliseconds(250));
		private static readonly IEasingFunction _filterEaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
		private static readonly IEasingFunction _filterEaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
		private readonly Dictionary<Guid, int> _filterAnimGen = new();
		private int _filterIconGen;

		// Grace window covering WindowsManager.MouseHook → DesktopShortClick latency (gated by
		// the OS double-click time, typically ~500ms). SharpHook clears the filter at T=0 but
		// the desktop toggle event fires later — predicate must report "filter was just cleared"
		// during the gap to suppress the toggle.
		private DateTime _filterClearedAt = DateTime.MinValue;
		private static readonly TimeSpan _filterClearGrace = TimeSpan.FromMilliseconds(750);

		// Flips true when the startup slide-in animation completes. Until then, OnRenderSizeChanged
		// must NOT yank Left back to 0 — the window is intentionally parked at -Width so DWM thumbnails
		// and scene previews stay culled while setup runs (scenes added, foreground scene switched).
		private bool _startupSlideComplete = false;

		public event PropertyChangedEventHandler PropertyChanged;

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
			_sidebarDragGhost = new SidebarDragGhost(_sceneTransitionAnimator);
			_debugZoneOverlay = new DebugZoneOverlay(_sceneTransitionAnimator);

			// Load initial setting BEFORE UI initialization
			_hideDesktopIcons = Settings.GetHideDesktopIcons();

			InitializeComponent();

			// Set DataContext AFTER setting is loaded
			DataContext = this;

			_overlapCheckTimer = new Timer(OverlapCheck, null, 2500, TIMERINTERVAL_MILLISECONDS);

			SwitchSceneCommand = new ActionCommand(async model =>
			{
				if (_filterProcessKey != null)
					ClearAppFilter();
				var sceneModel = (SceneModel)model;
				await AnimatedSwitchTo(sceneModel.Scene);
			});

			_iconOverlay.OnIconClicked = ToggleAppFilter;
		}

		private void ToggleAppFilter(string processKey)
		{
			var prior = _filterProcessKey;
			_filterProcessKey = (prior == processKey) ? null : processKey;
			_iconOverlay.HighlightedProcessKey = _filterProcessKey;

			var action = prior == null ? "SET"
				: _filterProcessKey == null ? "CLEAR (toggle off)"
				: "SWAP";
			Log.Info("FILTER", $"ToggleAppFilter: action={action} prior='{prior ?? "<none>"}' new='{_filterProcessKey ?? "<none>"}'");

			AnimateSyncVisibility();
		}

		private void ClearAppFilter()
		{
			if (_filterProcessKey == null) return;
			Log.Info("FILTER", $"ClearAppFilter: cleared filter='{_filterProcessKey}'");
			_filterClearedAt = DateTime.Now;
			_filterProcessKey = null;
			_iconOverlay.HighlightedProcessKey = null;
			AnimateSyncVisibility();
		}

		private async Task<bool> AnimatedSwitchTo(Scene scene)
		{
			// Block while a transition or drag is in flight
			if (_sceneTransitionAnimator.IsAnimating || _sidebarDragGhost.IsActive || IsSidebarDragging || (_dragDropManager?.IsDragging ?? false))
			{
				Log.Info("TRANSITION", $"BLOCKED: switch to '{scene?.Title}' while animation/drag in progress");
				return false;
			}

			if (SceneManager != null && SceneManager.IsCurrentScene(scene))
			{
				Log.Info("TRANSITION", $"Already on '{scene?.Title}', skipping");
				return false;
			}

			// Cancel pending deferred icon refresh — scene-switch flips IsVisible on the
			// destination, a late UpdateIcons would render an empty tile.
			_filterIconGen++;

			var sceneModel = AllScenes.FirstOrDefault(s => s?.Id == scene?.Id);
			if (sceneModel == null)
			{
				Log.Info("TRANSITION", $"No sidebar model for '{scene?.Title}', instant switch");
				return await SceneManager!.SwitchTo(scene);
			}

			var dpi = Dpi;
			Log.Action($"Scene switch: '{_removedCurrentScene?.Title ?? "(none)"}' → '{sceneModel.Title}' | scenes={Scenes.Count} dpi={dpi.X:F2},{dpi.Y:F2}");

			SceneManager.RestoreMinimizedInvisibly(sceneModel.Scene);

			var sidebarSlot = GetSceneThumbnailScreenBounds(sceneModel);
			var incomingTarget = GetSceneWindowBounds(sceneModel);
			Log.Info("TRANSITION", $"Bounds: sidebarSlot={sidebarSlot} incomingTarget={incomingTarget}");

			var outgoingModel = _removedCurrentScene;
			var outgoingSource = Rect.Empty;
			if (outgoingModel != null)
				outgoingSource = GetCurrentSceneWindowBounds();
			Log.Info("TRANSITION", $"Outgoing: model={outgoingModel != null} source={outgoingSource}");

			if (outgoingSource != Rect.Empty)
			{
				Log.Info("TRANSITION", "Pre-hiding current scene windows");
				SceneManager.HideCurrentSceneWindows();
			}

			Log.Info("TRANSITION", $"Hiding sidebar item '{sceneModel.Title}' (reserving space)");
			sceneModel.IsHiddenButReserved = true;
			sceneModel.IsVisible = false;

			if (sidebarSlot != Rect.Empty && incomingTarget != Rect.Empty)
			{
				Log.Info("TRANSITION", "Starting animation");
				await _sceneTransitionAnimator.AnimateSceneTransitionAsync(
					GetWorkAreaBounds(),
					sidebarSlot, incomingTarget, sceneModel,
					outgoingSource, sidebarSlot, outgoingModel);
				Log.Info("TRANSITION", "Animation completed");
			}

			Log.Info("TRANSITION", "Calling SwitchTo");
			var switched = await SceneManager!.SwitchTo(scene);
			if (!switched)
			{
				Log.Info("TRANSITION", "SwitchTo blocked, restoring sidebar state");
				SyncVisibilityByUpdatedTimeStamp();
			}
			Log.Info("TRANSITION", $"SwitchTo completed, switched={switched} scenes={Scenes.Count}");
			return switched;
		}

		protected override void OnInitialized(EventArgs e)
		{
			base.OnInitialized(e);

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			_lastWidth = Width;

			// Start hidden AND parked off-screen. Opacity=0 hides WPF content but DWM live thumbnails
			// follow window position regardless of opacity — Left=-Width keeps them culled by DWM
			// during setup so the user only sees the final slide-in.
			Opacity = 0;
			Left = -Width;

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
				SceneManager.WindowsManager.WindowUpdated -= OnWindowUpdatedForDrag;
			}

			StopHook();

			// Dispose the overlap check timer to stop background operations
			_overlapCheckTimer?.Dispose();

			trayIcon.Dispose();

			// Dispose SceneManager properly
			SceneManager?.Dispose();

			// Clean up animation overlay, drag ghost, and icon overlay
			_sceneTransitionAnimator?.Dispose();
			_dragGhostWindow?.Dispose();
			_iconOverlay?.Dispose();
			_updateService?.Dispose();

			base.OnClosed(e);
		}

		protected override async void OnContentRendered(EventArgs e)
		{
			base.OnContentRendered(e);

			Log.Info("STARTUP", "MainWindow content rendered, initializing...");

			var windowsManager = new WindowsManager();
			SceneManager = new SceneManager(windowsManager, HideDesktopIcons);
			SceneManager.IsAppFilterActive = () =>
				_filterProcessKey != null
				|| (DateTime.Now - _filterClearedAt) < _filterClearGrace;

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
			SceneManager.AnimatedSwitch = scene => Dispatcher.InvokeAsync(() => AnimatedSwitchTo(scene)).Task.Unwrap();

			// Wire up drag-and-drop manager
			_dragDropManager = new DragDropManager(
				SceneManager,
				_dragGhostWindow,
				() => Dpi,
				() => _lastWidth,
				w => WindowToLogicalRect(w),
				w => AllScenes.Where(s => s != null).SelectMany(s => s.Windows).FirstOrDefault(wm => wm.Handle == w.Handle)?.Icon,
				() => SyncVisibilityByUpdatedTimeStamp());
			SceneManager.WindowsManager.WindowUpdated += OnWindowUpdatedForDrag;

			AddInitialScenes();

			// Pre-create the overlay window so the first animation has no HWND-creation lag
			_sceneTransitionAnimator.WarmUp(GetWorkAreaBounds());
			ShowDebugDragZones();

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

			// All icon-overlay enable, position calc, opacity reveal, and slide-in animations happen
			// inside a single Loaded-priority dispatcher slot. Window stays at Left=-Width with
			// Opacity=0 throughout setup, so the user only sees the slide-in.
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				var startupDuration = TimeSpan.FromSeconds(0.5);
				var startupEasing = new PowerEase { EasingMode = EasingMode.EaseOut };

				// Force layout pass so ItemContainerGenerator has produced visual containers.
				// Coords are computed relative to the window (TranslatePoint, not PointToScreen)
				// since the HWND is parked at Left=-Width.
				UpdateLayout();

				_iconOverlay.Enabled = true;
				var visible = Scenes.Where(s => s.IsVisible).ToList();
				// Window is parked at Left=-Width during setup, so PointToScreen would offset
				// icon coords by -Width. Use TranslatePoint (window-relative) directly — the
				// final on-screen position is Left=0, so window-relative == screen-relative.
				_iconOverlay.UpdateIcons(visible, sm =>
				{
					var c = scenesControl.ItemContainerGenerator.ContainerFromItem(sm) as FrameworkElement;
					if (c == null) return Rect.Empty;
					var tl = c.TranslatePoint(new Point(0, 0), this);
					return new Rect(tl.X, tl.Y, c.ActualWidth, c.ActualHeight);
				}, GetWorkAreaBounds());

				// Reveal: alpha→1 while still off-screen, then animate Left from -Width to 0 in
				// lockstep with the icon-overlay canvas RenderTransform (same duration + easing).
				Opacity = 1;
				_iconOverlay.SlideIn(-Width, startupDuration, startupEasing);

				var slideIn = new DoubleAnimationUsingKeyFrames { Duration = new Duration(startupDuration) };
				slideIn.KeyFrames.Add(new EasingDoubleKeyFrame(-Width, KeyTime.FromPercent(0)));
				slideIn.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0), startupEasing));
				slideIn.Completed += (_, _) => _startupSlideComplete = true;
				BeginAnimation(LeftProperty, slideIn);
			});
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

			RefreshIconOverlay();
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
				currentModel.IsHiddenButReserved = false;
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

			// Desktop view: stow scene windows but keep the scene tray visible.
			if (args.Current is null)
			{
				Mode = WindowMode.OnScreen;
			}

			RefreshIconOverlay();
		}

		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
			var area = this.GetMonitorWorkSize();
			// Stay parked off-screen until the startup slide-in animation finishes; otherwise this
			// firing during the initial layout pass yanks the window back to Left=0 and leaks the
			// pre-slide visual state (scenes appearing, active scene removal, icon flicker).
			this.Left = _startupSlideComplete ? 0 : -Width;
			this.Top = 0;
			this.Height = area.Height;
			RefreshIconOverlay();
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

				RefreshIconOverlay();
			});
		}

		private void OnWindowUpdatedForDrag(IWindow window, WindowUpdateType type)
		{
			switch (type)
			{
				case WindowUpdateType.MoveStart:
					Dispatcher.Invoke(() => _dragDropManager?.OnWindowMoveStart(window));
					break;
				case WindowUpdateType.MoveEnd:
					Dispatcher.Invoke(() => _dragDropManager?.OnWindowMoveEnd(window));
					break;
			}
		}

		private void OnMousePressed(object? sender, MouseHookEventArgs e)
		{
			// Filter-clear gate runs before the foreground guard: outside-clicks (desktop, other apps)
			// move foreground away from us, so the guard below would otherwise miss them.
			if (_filterProcessKey != null)
			{
				var screenPoint = new Point(e.Data.X, e.Data.Y);
				Dispatcher.Invoke(() =>
				{
					if (_filterProcessKey != null && !IsPointInsideAnyVisibleScene(screenPoint))
						ClearAppFilter();
				});
			}

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
			if (EnableWindowPullToScene)
			{
				// WPF drag is handled by ScenesControl_PreviewMouseLeftButtonUp — only legacy pull remains
				if (!IsSidebarDragging && e.Data.X > _lastWidth && _mouseDownScene is object)
				{
					Log.Info("DRAG", $"Pulled window from scene '{_mouseDownScene.Title}' (mouseX={e.Data.X} > sidebarWidth={_lastWidth})");
					this.Dispatcher.Invoke(() =>
					{
						SceneManager.PopWindowFrom(_mouseDownScene.Scene).SafeFireAndForget();
					});
				}
				_mouseDownScene = null;
			}
		}

		private SceneModel FindSceneByPoint(Point p)
		{
			var thisWindow = new WindowsWindow(_thisHandle);
			var pointOnWindow = new Point(p.X - thisWindow.Location.X, p.Y - thisWindow.Location.Y);

			var dpi = Dpi;

			pointOnWindow.X /= dpi.X;
			pointOnWindow.Y /= dpi.Y;

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

		private bool IsPointInsideAnyVisibleScene(Point screenPoint)
		{
			foreach (var scene in Scenes)
			{
				if (!scene.IsVisible) continue;
				var rect = GetSceneThumbnailScreenBounds(scene);
				if (rect != Rect.Empty && rect.Contains(screenPoint))
					return true;
			}
			return false;
		}

		#region WPF Sidebar Drag (Flow 2: sidebar → active)

		private const double WpfDragThreshold = 10.0;

		private void ScenesControl_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (_sceneTransitionAnimator.IsAnimating || _sidebarDragGhost.IsActive) return;

			// Find which SceneModel was clicked
			var hit = e.OriginalSource as FrameworkElement;
			SceneModel scene = null;
			while (hit != null)
			{
				if (hit.DataContext is SceneModel sm) { scene = sm; break; }
				hit = VisualTreeHelper.GetParent(hit) as FrameworkElement;
			}
			if (scene == null) return;

			_wpfDragScene = scene;
			_wpfDragStartPoint = e.GetPosition(this);
			_sidebarDragPhase = SidebarDragPhase.None;
			_sidebarDragWindow = null;
			Log.Info("DRAG", $"WPF mousedown on '{scene.Title}' at ({_wpfDragStartPoint.X:F0},{_wpfDragStartPoint.Y:F0})");
		}

		private void ScenesControl_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (_wpfDragScene == null) return;
			if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
			{
				CancelWpfDrag();
				return;
			}

			var pos = e.GetPosition(this);

			if (!IsSidebarDragging)
			{
				var dx = pos.X - _wpfDragStartPoint.X;
				var dy = pos.Y - _wpfDragStartPoint.Y;
				if (Math.Sqrt(dx * dx + dy * dy) < WpfDragThreshold) return;

				_sidebarDragPhase = SidebarDragPhase.InSidebar;
				Mouse.Capture(scenesControl, System.Windows.Input.CaptureMode.SubTree);
				Log.Info("DRAG", $"WPF drag started from '{_wpfDragScene.Title}'");

				// Resolve the window that will be popped (same as PopWindowFrom picks)
				_sidebarDragWindow = _wpfDragScene.Scene.Windows.LastOrDefault();

				// Compute rects for interpolation
				_sidebarDragThumbRect = GetSceneThumbnailScreenBounds(_wpfDragScene);
				_sidebarDragWindowRect = _sidebarDragWindow != null
					? WindowToLogicalRect(_sidebarDragWindow)
					: Rect.Empty;
				if (_sidebarDragWindowRect == Rect.Empty)
					_sidebarDragWindowRect = new Rect(0, 0, 800, 600);

				_sidebarDragDpi = Dpi;
				_sidebarDragBufferLeft = _lastWidth;
				_sidebarDragBufferRight = _lastWidth + DragDropManager.BufferWidthLogical;

				var overlayBounds = GetWorkAreaBounds();
				if (_sidebarDragThumbRect != Rect.Empty && overlayBounds != Rect.Empty)
					_sidebarDragGhost.Show(overlayBounds, _sidebarDragThumbRect, _wpfDragScene);
				else
					Log.Info("DRAG", $"Ghost skipped: overlay={overlayBounds == Rect.Empty} thumb={_sidebarDragThumbRect == Rect.Empty}");
			}

			if (!IsSidebarDragging) return;

			var screenPos = PointToScreen(pos);
			var dpi = _sidebarDragDpi;
			double cursorLogicalX = screenPos.X / dpi.X;
			double cursorLogicalY = screenPos.Y / dpi.Y;

			var prevPhase = _sidebarDragPhase;

			// Transition back from PastBuffer: hide real window, restore ghost
			if (_sidebarDragPhase == SidebarDragPhase.PastBuffer && pos.X <= _sidebarDragBufferRight)
			{
				HideSidebarDragRealWindow();
				_sidebarDragGhost.SetVisible(true);
				Log.Info("DRAG", $"Phase: PastBuffer → {(pos.X <= _sidebarDragBufferLeft ? "InSidebar" : "InBuffer")}");
			}

			if (pos.X <= _sidebarDragBufferLeft)
			{
				_sidebarDragPhase = SidebarDragPhase.InSidebar;

				_sidebarDragGhost.UpdatePositionAndSize(
					cursorLogicalX - _sidebarDragThumbRect.Width / 2,
					cursorLogicalY - _sidebarDragThumbRect.Height / 2,
					_sidebarDragThumbRect.Width,
					_sidebarDragThumbRect.Height);
			}
			else if (pos.X <= _sidebarDragBufferRight)
			{
				_sidebarDragPhase = SidebarDragPhase.InBuffer;

				double t = Math.Clamp((pos.X - _sidebarDragBufferLeft) / DragDropManager.BufferWidthLogical, 0.0, 1.0);
				double ghostW = DragDropManager.Lerp(_sidebarDragThumbRect.Width, _sidebarDragWindowRect.Width, t);
				double ghostH = DragDropManager.Lerp(_sidebarDragThumbRect.Height, _sidebarDragWindowRect.Height, t);

				_sidebarDragGhost.UpdatePositionAndSize(
					cursorLogicalX - ghostW / 2,
					cursorLogicalY - ghostH / 2,
					ghostW, ghostH);
			}
			else
			{
				// --- Past buffer zone: show real window ---
				bool entering = _sidebarDragPhase != SidebarDragPhase.PastBuffer;
				if (entering)
				{
					_sidebarDragGhost.SetVisible(false);
					_sidebarDragPhase = SidebarDragPhase.PastBuffer;
					Log.Info("DRAG", $"Phase: {prevPhase} → PastBuffer");
				}

				// Position window at cursor (physical pixels), then show on entry
				if (_sidebarDragWindow != null)
				{
					int winW = (int)(_sidebarDragWindowRect.Width * dpi.X);
					int winH = (int)(_sidebarDragWindowRect.Height * dpi.Y);
					int winX = (int)(screenPos.X - winW / 2.0);
					int winY = (int)(screenPos.Y - winH / 2.0);
					Win32.SetWindowPos(_sidebarDragWindow.Handle, IntPtr.Zero,
						winX, winY, winW, winH,
						Win32.SetWindowPosFlags.DoNotActivate);
				}

				if (entering)
					ShowSidebarDragRealWindow();
			}
		}

		private void ScenesControl_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (IsSidebarDragging)
			{
				e.Handled = true; // Suppress SwitchSceneCommand

				var pos = e.GetPosition(this);
				var phase = _sidebarDragPhase;

				if ((phase == SidebarDragPhase.PastBuffer || (phase == SidebarDragPhase.InBuffer && pos.X > _lastWidth))
					&& _wpfDragScene != null)
				{
					Log.Info("DRAG", $"WPF drop (phase={phase}), pulling from '{_wpfDragScene.Title}'");
					var scene = _wpfDragScene.Scene;
					CancelWpfDrag();
					SceneManager.PopWindowFrom(scene).SafeFireAndForget();
				}
				else
				{
					Log.Info("DRAG", $"WPF drag cancelled (phase={phase})");
					CancelWpfDrag();
				}
				return;
			}

			// Not dragging — let SwitchSceneCommand fire normally
			_wpfDragScene = null;
		}

		private void CancelWpfDrag()
		{
			if (_sidebarDragPhase == SidebarDragPhase.PastBuffer)
				HideSidebarDragRealWindow();
			_sidebarDragGhost.Hide();
			_sidebarDragPhase = SidebarDragPhase.None;
			_wpfDragScene = null;
			_sidebarDragWindow = null;
			Mouse.Capture(null);
			SceneManager?.WindowsManager.SuppressNextDesktopClick();
		}

		private void ShowSidebarDragRealWindow()
		{
			if (_sidebarDragWindow == null) return;
			if (_sidebarDragWindow.IsMinimized)
			{
				Win32Helper.SetAlpha(_sidebarDragWindow.Handle, 0);
				_sidebarDragWindow.ShowNormal();
				Log.Info("DRAG", "Sidebar drag: restored minimized window");
			}
			Win32Helper.SetAlpha(_sidebarDragWindow.Handle, 255);
			Win32.SetWindowPos(_sidebarDragWindow.Handle, Win32.HWND_TOPMOST,
				0, 0, 0, 0,
				Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.IgnoreMove | Win32.SetWindowPosFlags.IgnoreResize);
			Log.Info("DRAG", "Sidebar drag: real window shown (alpha→255, topmost)");
		}

		private void HideSidebarDragRealWindow()
		{
			if (_sidebarDragWindow == null) return;
			Win32.SetWindowPos(_sidebarDragWindow.Handle, Win32.HWND_NOTOPMOST,
				0, 0, 0, 0,
				Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.IgnoreMove | Win32.SetWindowPosFlags.IgnoreResize);
			Win32Helper.SetAlpha(_sidebarDragWindow.Handle, 0);
			Log.Info("DRAG", "Sidebar drag: real window hidden (alpha→0, topmost removed)");
		}

		#endregion

		private void SyncVisibilityByUpdatedTimeStamp()
		{
			var scenes = Scenes.OrderByDescending(s => s.Updated).ToArray();

			if (_filterProcessKey == null)
			{
				for (int i = 0; i < scenes.Length; i++)
					scenes[i].IsVisible = i < MAX_SCENES;
				Log.Info("FILTER", $"SyncVisibility: filter=<none> total={scenes.Length} shown={Math.Min(scenes.Length, MAX_SCENES)} (cap={MAX_SCENES})");
				return;
			}

			int shown = 0, hidden = 0;
			foreach (var scene in scenes)
			{
				var processNames = scene.Windows.Select(w => w.Window?.ProcessFileName ?? "").ToArray();
				var match = processNames.Any(p => p == _filterProcessKey);
				scene.IsVisible = match;
				if (match) shown++; else hidden++;
				Log.Info("FILTER", $"SyncVisibility: scene='{scene.Title}' processes=[{string.Join(",", processNames)}] match={match} → IsVisible={match}");
			}
			Log.Info("FILTER", $"SyncVisibility: filter='{_filterProcessKey}' shown={shown} hidden={hidden} total={scenes.Length}");
		}

		// Animated counterpart to SyncVisibilityByUpdatedTimeStamp for filter-toggle paths.
		private async void AnimateSyncVisibility()
		{
			var iconGen = ++_filterIconGen;

			var scenes = Scenes.OrderByDescending(s => s.Updated).ToArray();
			bool[] target = new bool[scenes.Length];
			if (_filterProcessKey == null)
			{
				for (int i = 0; i < scenes.Length; i++) target[i] = i < MAX_SCENES;
			}
			else
			{
				for (int i = 0; i < scenes.Length; i++)
					target[i] = scenes[i].Windows.Any(w => w.Window?.ProcessFileName == _filterProcessKey);
			}

			// FLIP-First: capture each visible scene's container Y in window coords.
			double[] oldY = new double[scenes.Length];
			for (int i = 0; i < scenes.Length; i++)
				oldY[i] = scenes[i].IsVisible ? GetSceneInnerY(scenes[i]) : double.NaN;

			bool[] originalVisible = new bool[scenes.Length];
			for (int i = 0; i < scenes.Length; i++) originalVisible[i] = scenes[i].IsVisible;

			// Phase order matters: enter (IsVisible=true) before survivor measurement so the
			// FLIP From offset reflects post-enter layout.
			var pendingExits = new List<TaskCompletionSource>();
			int transitioning = 0;
			for (int i = 0; i < scenes.Length; i++)
			{
				var s = scenes[i];
				if (target[i] && !originalVisible[i]) { AnimateSceneEnter(s); transitioning++; }
			}
			UpdateLayout();
			for (int i = 0; i < scenes.Length; i++)
			{
				var s = scenes[i];
				if (target[i] && originalVisible[i] && !double.IsNaN(oldY[i]))
					RestoreSceneVisible(s, oldY[i]);
			}
			for (int i = 0; i < scenes.Length; i++)
			{
				var s = scenes[i];
				if (!target[i] && originalVisible[i])
				{
					var tcs = new TaskCompletionSource();
					pendingExits.Add(tcs);
					AnimateSceneExit(s, tcs);
					transitioning++;
				}
			}
			Log.Info("FILTER", $"AnimateSyncVisibility: filter='{_filterProcessKey ?? "<none>"}' transitioning={transitioning}");

			// Concurrent icon morph: kick off immediately so exiter icons fade out alongside their
			// scene fade (SET) and enterer icons fade in alongside their scene appearance (CLEAR).
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			var visibleNow = scenes.Where((_, i) => target[i]).ToList();
			_iconOverlay.UpdateIcons(visibleNow, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds());
			Log.Info("FILTER_TIMING", $"first UpdateIcons at t={stopwatch.ElapsedMilliseconds}ms exits={pendingExits.Count} visibleNow={visibleNow.Count}");

			if (pendingExits.Count == 0) return;

			// SET path: layout doesn't collapse until exits complete; reposition survivor icons
			// against final layout afterward. Safety timeout covers SnapshotAndReplace swallowing Completed.
			var allExits = Task.WhenAll(pendingExits.Select(t => t.Task));
			var safety = Task.Delay(_filterMorphDuration.TimeSpan + TimeSpan.FromMilliseconds(50));
			await Task.WhenAny(allExits, safety);
			Log.Info("FILTER_TIMING", $"exits completed at t={stopwatch.ElapsedMilliseconds}ms");
			if (iconGen != _filterIconGen) return;
			UpdateLayout();
			var visibleAtFinal = Scenes.Where(s => s.IsVisible).ToList();
			_iconOverlay.UpdateIcons(visibleAtFinal, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds());
			Log.Info("FILTER_TIMING", $"second UpdateIcons at t={stopwatch.ElapsedMilliseconds}ms visibleFinal={visibleAtFinal.Count}");
		}

		private void AnimateSceneEnter(SceneModel s)
		{
			var gen = NextFilterGen(s.Id);
			s.IsVisible = true;
			if (TryGetSceneInnerGrid(s) is FrameworkElement inner) { StartEnterAnimation(inner); return; }
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				if (CurrentFilterGen(s.Id) != gen) return;
				if (TryGetSceneInnerGrid(s) is FrameworkElement inner2)
					StartEnterAnimation(inner2);
			});
		}

		// From=0 explicit; FillBehavior.Stop reverts to style-base Opacity=0.8.
		private static void StartEnterAnimation(FrameworkElement inner) =>
			inner.BeginAnimation(UIElement.OpacityProperty,
				Anim.From(0, 0.8, _filterMorphDuration, _filterEaseOut, FillBehavior.Stop));

		private void AnimateSceneExit(SceneModel s, TaskCompletionSource? tcs = null)
		{
			var gen = NextFilterGen(s.Id);
			if (TryGetSceneInnerGrid(s) is not FrameworkElement inner)
			{
				s.IsVisible = false;
				tcs?.TrySetResult();
				return;
			}
			// HoldEnd keeps Opacity=0 painted until IsVisible flips — no flash before collapse.
			var opacityAnim = Anim.To(0, _filterMorphDuration, _filterEaseIn);
			opacityAnim.Completed += (_, _) =>
			{
				if (CurrentFilterGen(s.Id) == gen)
					s.IsVisible = false;
				tcs?.TrySetResult();
			};
			inner.BeginAnimation(UIElement.OpacityProperty, opacityAnim, HandoffBehavior.SnapshotAndReplace);
		}

		// FLIP target = ContentPresenter, not inner Grid. The Grid carries the SceneOpacity
		// style's ScaleTransform consumed by the hover storyboard — replacing its
		// RenderTransform would break `(RenderTransform).(ScaleTransform.ScaleX)` resolution.
		private void RestoreSceneVisible(SceneModel s, double oldYAbs = double.NaN)
		{
			NextFilterGen(s.Id);
			if (TryGetSceneInnerGrid(s) is not FrameworkElement inner) return;
			inner.BeginAnimation(UIElement.OpacityProperty, Anim.To(0.8, _filterMorphDuration, _filterEaseOut, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);

			if (double.IsNaN(oldYAbs)) return;

			var container = TryGetSceneItemContainer(s);
			if (container is null) return;

			double currentLayoutY = GetSceneInnerY(s);
			if (double.IsNaN(currentLayoutY)) return;
			double from = oldYAbs - currentLayoutY;

			if (Math.Abs(from) > 0.5)
			{
				// Pre-seed local Y = from BEFORE BeginAnimation. Without this, render thread
				// can paint one frame at the layout-only position (local Y=0 → visual at newY)
				// before the animation clock ticks, causing a forward-back-forward visual dance.
				// HoldEnd holds animated value at To=0 indefinitely so the seeded local doesn't
				// resurface post-animation; SnapshotAndReplace keeps subsequent cycles clean.
				if (container.RenderTransform is not TranslateTransform tt)
				{
					tt = new TranslateTransform { Y = from };
					container.RenderTransform = tt;
				}
				else
				{
					tt.BeginAnimation(TranslateTransform.YProperty, null);
					tt.Y = from;
				}
				var slide = Anim.From(from, 0, _filterMorphDuration, _filterEaseOut);
				tt.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
			}
		}

		private double GetSceneInnerY(SceneModel s)
		{
			var container = TryGetSceneItemContainer(s);
			if (container is null) return double.NaN;
			try { return container.TransformToVisual(this).Transform(new Point(0, 0)).Y; }
			catch { return double.NaN; }
		}

		private FrameworkElement? TryGetSceneItemContainer(SceneModel s)
			=> scenesControl.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;

		private int NextFilterGen(Guid id)
		{
			var v = (_filterAnimGen.TryGetValue(id, out var c) ? c : 0) + 1;
			_filterAnimGen[id] = v;
			return v;
		}

		private int CurrentFilterGen(Guid id) => _filterAnimGen.TryGetValue(id, out var c) ? c : 0;

		private FrameworkElement? TryGetSceneInnerGrid(SceneModel s)
		{
			var container = scenesControl.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;
			if (container == null) return null;
			if (VisualTreeHelper.GetChildrenCount(container) == 0) return null;
			return VisualTreeHelper.GetChild(container, 0) as FrameworkElement;
		}

		private void RefreshIconOverlay(double xOffset = 0)
		{
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				// Flush sidebar layout — Remove+Insert during selection invalidates async.
				UpdateLayout();
				var visible = Scenes.Where(s => s.IsVisible).ToList();
				_iconOverlay.UpdateIcons(visible, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds(), xOffset);
			});
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
			var duration = TimeSpan.FromSeconds(0.5);
			var easingFunction = new PowerEase { EasingMode = easingMode };

			if (isIncoming)
			{
				// Snap to final position, force layout, position icons at correct coords, then animate
				BeginAnimation(LeftProperty, null);
				Left = 0;
				UpdateLayout();
				_iconOverlay.Enabled = true;
				var visible = Scenes.Where(s => s.IsVisible).ToList();
				_iconOverlay.UpdateIcons(visible, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds());
				_iconOverlay.SlideIn(-Width, duration, easingFunction);
				Left = -Width;
			}
			else
			{
				_iconOverlay.Enabled = false;
				_iconOverlay.SlideOut(-Width, duration, easingFunction);
			}

			var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
			animation.KeyFrames.Add(new EasingDoubleKeyFrame(-Width, KeyTime.FromPercent(0)));
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
			Interlocked.Exchange(ref _mouseX, e.Data.X);

			if (Mode == WindowMode.OffScreen && e.Data.X <= 44)
			{
				Dispatcher.Invoke(() => Mode = WindowMode.Flyover);
			}
		}

		private void OverlapCheck(object? _)
		{
			// Don't hide the sidebar while dragging a window toward it
			if (_dragDropManager?.IsDragging == true) return;

			var currentWindows = SceneManager.GetCurrentWindows().ToArray(); // in case the enumeration changes
			UpdateModeByWindows(currentWindows);
		}

		private void UpdateModeByWindows(IEnumerable<IWindow> windows)
		{
			// Freeze sidebar mode in desktop view: scene windows are stowed (alpha=0) but still
			// report layout positions, so doesOverlap() would falsely flag overlap and hide the tray.
			if (SceneManager?.IsDesktopView == true)
				return;

			bool doesOverlap(IWindowLocation loc) => loc.State == Native.Window.WindowState.Maximized || (loc.State == Native.Window.WindowState.Normal && (loc.X * 2) < _lastWidth);

			var anyOverlappingWindows = windows.Any(w => doesOverlap(w.Location));

			var containsMouse = Interlocked.Read(ref _mouseX) <= _lastWidth;
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

		[System.Diagnostics.Conditional("DEBUG")]
		private void ShowDebugDragZones()
		{
			var dpi = Dpi;
			var sidebarW = _lastWidth;
			var bufferW = DragDropManager.BufferWidthLogical;
			var workArea = GetWorkAreaBounds();
			_debugZoneOverlay.Show(
				new Rect(0, 0, sidebarW, workArea.Height),
				new Rect(sidebarW, 0, bufferW, workArea.Height),
				workArea);
		}

		/// <summary>
		/// DPI scale factors for converting between physical and logical coordinates.
		/// Cached lazily; invalidated on <see cref="OnDpiChanged"/>.
		/// </summary>
		private Point _dpi = new(1.0, 1.0);
		private bool _dpiCached;

		private Point Dpi
		{
			get
			{
				if (_dpiCached) return _dpi;
				var source = PresentationSource.FromVisual(this);
				if (source?.CompositionTarget == null)
					return _dpi; // pre-source-init: return fallback but don't cache it
				_dpi = new Point(source.CompositionTarget.TransformToDevice.M11, source.CompositionTarget.TransformToDevice.M22);
				_dpiCached = true;
				return _dpi;
			}
		}

		protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
		{
			base.OnDpiChanged(oldDpi, newDpi);
			_dpiCached = false;
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

				var dpi = Dpi;

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
		/// Converts a window's Location (physical pixels) to WPF logical units.
		/// Returns Rect.Empty if the window is minimized, offscreen-parked, or invalid.
		/// </summary>
		private Rect WindowToLogicalRect(Native.Window.IWindow window)
		{
			if (window == null || window.IsMinimized)
				return Rect.Empty;

			var loc = window.Location;
			if (loc.Width <= 0 || loc.Height <= 0 || loc.X < -10000)
				return Rect.Empty;

			var dpi = Dpi;
			return new Rect(loc.X / dpi.X, loc.Y / dpi.Y, loc.Width / dpi.X, loc.Height / dpi.Y);
		}

		private Rect GetSceneWindowBounds(SceneModel sceneModel)
		{
			var window = sceneModel.Scene.Windows.FirstOrDefault(w => !w.IsMinimized);
			var rect = WindowToLogicalRect(window);
			return rect != Rect.Empty ? rect : GetWorkAreaBounds();
		}

		private Rect GetCurrentSceneWindowBounds()
		{
			var window = SceneManager.GetCurrentWindows().FirstOrDefault(w => !w.IsMinimized);
			return WindowToLogicalRect(window);
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

				var dpi = Dpi;

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
			if (SceneManager == null) return;

			if (_hideDesktopIcons)
				SceneManager.HideDesktopIcons();
			else
				SceneManager.ShowDesktopIcons();
		}

		private void MenuItem_ProjectPage_Click(object sender, RoutedEventArgs e)
		{
			NavigateToProjectPage();
		}

		private enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

		private UpdateState _updateState = UpdateState.Idle;
		private UpdateInfo? _availableUpdate;
		private double _downloadProgress;
		private string? _downloadedPath;
		private readonly string _currentVersionString = UpdateService.GetCurrentVersion().ToString();

		public string AppHeaderText => $"Stage Manager v{_currentVersionString}";

		public string UpdateMenuText => _updateState switch
		{
			UpdateState.Idle => "Check for updates",
			UpdateState.Checking => "Checking...",
			UpdateState.UpToDate => "Up to date",
			UpdateState.Available => $"Update to {_availableUpdate!.TagName}",
			UpdateState.Downloading => $"Downloading...  {_downloadProgress:P0}",
			UpdateState.Ready => "Restart to update",
			UpdateState.Error => "Update failed \u00b7 Retry",
			_ => "Check for updates"
		};

		private void SetUpdateState(UpdateState state)
		{
			_updateState = state;
			RaisePropertyChanged(nameof(UpdateMenuText));
		}

		private async void MenuItem_CheckForUpdates_Click(object sender, RoutedEventArgs e)
		{
			switch (_updateState)
			{
				case UpdateState.Idle:
				case UpdateState.UpToDate:
				case UpdateState.Error:
					await PerformUpdateCheckAsync();
					break;
				case UpdateState.Available:
					await PerformDownloadAsync();
					break;
				case UpdateState.Ready:
					PerformApplyAndRestart();
					break;
			}
		}

		private async Task PerformUpdateCheckAsync()
		{
			SetUpdateState(UpdateState.Checking);
			try
			{
				var update = await _updateService.CheckForUpdateAsync();
				if (update is null)
				{
					SetUpdateState(UpdateState.UpToDate);
				}
				else
				{
					_availableUpdate = update;
					SetUpdateState(UpdateState.Available);
				}
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update check failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		private async Task PerformDownloadAsync()
		{
			if (_availableUpdate is null) return;
			SetUpdateState(UpdateState.Downloading);
			try
			{
				var progress = new Progress<double>(p =>
				{
					_downloadProgress = p;
					RaisePropertyChanged(nameof(UpdateMenuText));
				});

				_downloadedPath = await _updateService.DownloadUpdateAsync(_availableUpdate, progress);
				SetUpdateState(UpdateState.Ready);
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update download failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		private void PerformApplyAndRestart()
		{
			if (_downloadedPath is null) return;
			try
			{
				if (trayIcon.ContextMenu is { IsOpen: true } menu)
					menu.IsOpen = false;
				var snapshotPath = SceneSnapshot.Save(SceneManager.CreateSnapshot());
				UpdateService.ApplyUpdate(_downloadedPath);
				UpdateService.LaunchAndExit(snapshotPath);
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update apply failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
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
