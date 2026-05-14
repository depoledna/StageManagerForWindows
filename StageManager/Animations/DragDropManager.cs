using StageManager.Animations;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace StageManager.Animations
{
	/// <summary>
	/// Manages the "active window → sidebar" drag flow with a buffer zone shrink effect.
	/// When a window is dragged toward the sidebar, it progressively shrinks to thumbnail size.
	/// </summary>
	internal class DragDropManager
	{
		private enum DragState { None, TrackingWindowDrag, ShrinkingInBuffer }

		internal const double BufferWidthLogical = 120.0;
		private static readonly Rect TargetThumbSize = new Rect(0, 0, 120, 80);

		private readonly SceneManager _sceneManager;
		private readonly DragGhostWindow _ghostWindow;
		private readonly Func<Point> _getDpiScale;
		private readonly Func<double> _getSidebarWidth;
		private readonly Func<IWindow, Rect> _getWindowLogicalRect;
		private readonly Func<IWindow, ImageSource?> _getWindowIcon;
		private readonly Action _syncVisibility;

		private int _stateValue = (int)DragState.None;
		private DragState State
		{
			get => (DragState)Volatile.Read(ref _stateValue);
			set => Volatile.Write(ref _stateValue, (int)value);
		}

		private IWindow? _trackedWindow;
		private Rect _originalWindowRect;
		private double _bufferRightPhysical;
		private double _sidebarWidthPhysical;
		private Win32.WS _originalStyle;
		private DispatcherTimer? _pollTimer;

		public bool IsDragging => State != DragState.None;

		public DragDropManager(
			SceneManager sceneManager,
			DragGhostWindow ghostWindow,
			Func<Point> getDpiScale,
			Func<double> getSidebarWidth,
			Func<IWindow, Rect> getWindowLogicalRect,
			Func<IWindow, ImageSource?> getWindowIcon,
			Action syncVisibility)
		{
			_sceneManager = sceneManager;
			_ghostWindow = ghostWindow;
			_getDpiScale = getDpiScale;
			_getSidebarWidth = getSidebarWidth;
			_getWindowLogicalRect = getWindowLogicalRect;
			_getWindowIcon = getWindowIcon;
			_syncVisibility = syncVisibility;
		}

		public void OnWindowMoveStart(IWindow window)
		{
			if (State != DragState.None) return;
			var scene = _sceneManager.FindSceneForWindow(window);
			if (scene is null || !_sceneManager.IsCurrentScene(scene))
				return;
			if (scene.Windows.Count() <= 1)
				return;

			_trackedWindow = window;
			_originalWindowRect = _getWindowLogicalRect(window);

			var dpi = _getDpiScale();
			_sidebarWidthPhysical = _getSidebarWidth() * dpi.X;
			_bufferRightPhysical = _sidebarWidthPhysical + BufferWidthLogical * dpi.X;

			State = DragState.TrackingWindowDrag;
			StartPolling();
			Log.Window("DRAG", "Move start (tracking)", window);
		}

		private void EnterBufferZone(IWindow window)
		{
			State = DragState.ShrinkingInBuffer;

			_originalStyle = Win32.GetWindowStyleLongPtr(window.Handle);
			Win32.SetWindowStyleLongPtr(window.Handle, _originalStyle & ~Win32.WS.WS_MAXIMIZEBOX);

			var windowRect = _getWindowLogicalRect(window);
			if (windowRect == Rect.Empty)
			{
				State = DragState.TrackingWindowDrag;
				return;
			}
			Log.Info("DRAG", $"Entered buffer zone (windowRect={windowRect})");

			HideRealWindow(window);

			var icon = _getWindowIcon(window);
			_ghostWindow.Show(windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height, icon);
		}

		public async void OnWindowMoveEnd(IWindow window)
		{
			try
			{
				var state = State;
				if (window != _trackedWindow || state == DragState.None)
				{
					Reset();
					return;
				}

				StopPolling();
				Win32.GetCursorPos(out var dropCursor);

				if (state == DragState.ShrinkingInBuffer)
				{
					Win32.SetWindowStyleLongPtr(_trackedWindow.Handle, _originalStyle);

					if (dropCursor.X < _sidebarWidthPhysical)
					{
						Log.Window("DRAG", "Dropped in sidebar, separating window", window);
						_ghostWindow.Hide();
						State = DragState.None;
						_sceneManager.SeparateWindowToNewScene(window);

						await Dispatcher.CurrentDispatcher.InvokeAsync(() => { },
							DispatcherPriority.Loaded);
						_syncVisibility();
					}
					else
					{
						Log.Info("DRAG", "Dropped in buffer zone, cancelling");
						_ghostWindow.Hide();
						RestoreRealWindow(window);
					}
				}
				else
				{
					Log.Window("DRAG", $"Move ended (windowLeft={window.Location.X})", window);
				}

				Reset();
			}
			catch (Exception ex)
			{
				Log.Info("DRAG", $"OnWindowMoveEnd failed: {ex.Message}");
				Reset();
			}
		}

		private void StartPolling()
		{
			StopPolling();
			_pollTimer = new DispatcherTimer(DispatcherPriority.Render);
			_pollTimer.Interval = TimeSpan.FromMilliseconds(16);
			_pollTimer.Tick += PollTick;
			_pollTimer.Start();
		}

		private void StopPolling()
		{
			if (_pollTimer != null)
			{
				_pollTimer.Stop();
				_pollTimer.Tick -= PollTick;
				_pollTimer = null;
			}
		}

		private void PollTick(object? sender, EventArgs e)
		{
			if (_trackedWindow == null)
			{
				StopPolling();
				return;
			}

			if (!Win32.GetCursorPos(out var cursor)) return;
			double mouseX = cursor.X;
			double mouseY = cursor.Y;

			var state = State;

			if (state == DragState.TrackingWindowDrag)
			{
				if (mouseX < _bufferRightPhysical)
					EnterBufferZone(_trackedWindow);
				return;
			}

			if (state != DragState.ShrinkingInBuffer)
			{
				StopPolling();
				return;
			}

			// Exit buffer zone (cursor dragged back right)
			if (mouseX > _bufferRightPhysical)
			{
				ExitBufferZone();
				return;
			}

			// Interpolate ghost size: t=0 at buffer edge, t=1 at sidebar edge
			var bufferWidth = _bufferRightPhysical - _sidebarWidthPhysical;
			var t = Math.Clamp((_bufferRightPhysical - mouseX) / bufferWidth, 0.0, 1.0);
			var dpi = _getDpiScale();

			// Lerp in logical coordinates; convert physical cursor to logical
			var ghostW = Lerp(_originalWindowRect.Width, TargetThumbSize.Width, t);
			var ghostH = Lerp(_originalWindowRect.Height, TargetThumbSize.Height, t);
			var ghostX = mouseX / dpi.X - ghostW / 2;
			var ghostY = mouseY / dpi.Y - ghostH / 2;

			_ghostWindow.Update(ghostX, ghostY, ghostW, ghostH);
		}

		private void ExitBufferZone()
		{
			if (_trackedWindow is null) return;
			Log.Info("DRAG", "Exited buffer zone (cursor moved right)");
			_ghostWindow.Hide();
			Win32.SetWindowStyleLongPtr(_trackedWindow.Handle, _originalStyle);
			RestoreRealWindow(_trackedWindow);
			State = DragState.TrackingWindowDrag;
			// Don't stop polling — PollTick handles TrackingWindowDrag for re-entry
		}

		private void HideRealWindow(IWindow window)
		{
			Win32Helper.SetAlpha(window.Handle, 0);
			Log.Window("DRAG", "Hidden (alpha→0)", window);
		}

		private void RestoreRealWindow(IWindow window)
		{
			Win32Helper.SetAlpha(window.Handle, 255);
			Log.Window("DRAG", "Restored (alpha→255)", window);
		}

		private void Reset()
		{
			StopPolling();
			if (State == DragState.ShrinkingInBuffer && _trackedWindow != null)
			{
				try
				{
					Win32.SetWindowStyleLongPtr(_trackedWindow.Handle, _originalStyle);
					RestoreRealWindow(_trackedWindow);
					_ghostWindow.Hide();
				}
				catch { }
			}
			State = DragState.None;
			_trackedWindow = null;
		}

		internal static double Lerp(double a, double b, double t) => a + (b - a) * t;
	}
}
