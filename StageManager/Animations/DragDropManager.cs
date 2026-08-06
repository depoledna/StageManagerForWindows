using StageManager.Animations;
using StageManager.Controls;
using StageManager.Model;
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

		// Where the cursor sits on the dragged card, in DIPs below its top edge. A window is
		// grabbed by its title bar, so the card has to hang off the pointer the same way at
		// every size — centring it instead makes a full-size window jump under the cursor the
		// moment the drag starts. Clamped so it can never fall past a short card's middle.
		private const double CursorTopOffsetLogical = 16.0;
		private const double CursorTopOffsetMaxFraction = 0.35;

		/// <summary>
		/// Top edge for a card of <paramref name="heightLogical"/> hanging off a cursor at
		/// <paramref name="cursorLogicalY"/>. Shared with the sidebar→stage drag so both
		/// directions grip the card in the same place.
		/// </summary>
		internal static double CardTopFor(double cursorLogicalY, double heightLogical)
			=> cursorLogicalY - Math.Min(CursorTopOffsetLogical, heightLogical * CursorTopOffsetMaxFraction);

		private readonly SceneManager _sceneManager;
		private readonly SidebarDragGhost _ghost;
		private readonly Func<Point> _getDpiScale;
		private readonly Func<double> _getSidebarWidth;
		private readonly Func<Rect> _getOverlayBounds;
		private readonly Func<IWindow, Rect> _getWindowLogicalRect;
		private readonly Func<IWindow, ImageSource?> _getWindowIcon;
		private readonly Action _syncVisibility;
		private readonly double _cornerRadius;

		private int _stateValue = (int)DragState.None;
		private DragState State
		{
			get => (DragState)Volatile.Read(ref _stateValue);
			set => Volatile.Write(ref _stateValue, (int)value);
		}

		private IWindow? _trackedWindow;
		private Rect _originalWindowRect;
		private Size _targetThumbSize = new Size(120, 80);
		private double _bufferRightPhysical;
		private double _sidebarWidthPhysical;
		private Win32.WS _originalStyle;
		// Sampled once per drag, like the tray→stage direction does. The property behind it
		// caches, but this keeps the per-frame path free of delegate calls entirely.
		private Point _dragDpi = new Point(1, 1);

		// Last cursor pair the frame loop actually drew, X and Y packed into one long, so a
		// frame with no mouse movement does no work at all. long.MinValue is the "nothing
		// drawn yet" seed — no real pair packs to it.
		private long _renderedCursor = long.MinValue;
		private bool _frameLoopRunning;

		private static long Pack(int x, int y) => ((long)(uint)x << 32) | (uint)y;

		// True once the OS title-bar move loop has been cancelled and this class drives the
		// window instead. See TakeOverFromMoveLoop.
		private bool _ownsDrag;
		// Cancelling the move loop makes Windows raise MOVESIZEEND immediately. That is our
		// drop signal, so it has to be swallowed once or the drag ends the moment it starts.
		private bool _ignoreNextMoveEnd;

		public bool IsDragging => State != DragState.None;

		public DragDropManager(
			SceneManager sceneManager,
			SidebarDragGhost ghost,
			Func<Point> getDpiScale,
			Func<double> getSidebarWidth,
			Func<Rect> getOverlayBounds,
			Func<IWindow, Rect> getWindowLogicalRect,
			Func<IWindow, ImageSource?> getWindowIcon,
			Action syncVisibility,
			double cornerRadius)
		{
			_sceneManager = sceneManager;
			_ghost = ghost;
			_getDpiScale = getDpiScale;
			_getSidebarWidth = getSidebarWidth;
			_getOverlayBounds = getOverlayBounds;
			_getWindowLogicalRect = getWindowLogicalRect;
			_getWindowIcon = getWindowIcon;
			_syncVisibility = syncVisibility;
			_cornerRadius = cornerRadius;
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

			// Land on the size the tray tile will actually be, from the same law that sizes
			// resting tiles. A fixed 120x80 target used to stretch every card to one aspect
			// ratio and pop the instant the real tile replaced it.
			var (cardW, cardH) = SceneModel.CardSizeDip(_originalWindowRect.Width, _originalWindowRect.Height);
			_targetThumbSize = new Size(Math.Max(1, cardW), Math.Max(1, cardH));

			_dragDpi = _getDpiScale();
			_sidebarWidthPhysical = _getSidebarWidth() * _dragDpi.X;
			_bufferRightPhysical = _sidebarWidthPhysical + BufferWidthLogical * _dragDpi.X;

			State = DragState.TrackingWindowDrag;
			StartFrameLoop();
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

			// Take the window off the OS before parking it, or the move loop drags it straight
			// back out from under the ghost on the very next mouse message.
			TakeOverFromMoveLoop(window);

			// Park off-screen (NOT alpha→0): WGC captures DWM post-alpha, so a hidden-by-alpha
			// window yields transparent frames. Off-screen + full alpha keeps the live card fed.
			HideRealWindow(window);

			var icon = _getWindowIcon(window);
			_ghost.ShowOwned(_getOverlayBounds(), windowRect, window.Handle, icon, _getDpiScale(), _cornerRadius);
		}

		/// <summary>
		/// Ends the OS title-bar move loop so this class owns the drag from here on, exactly
		/// as the tray→stage direction owns its drag from the first mouse-down.
		/// <para>
		/// While the loop runs, Windows re-pins the window to the cursor on every mouse
		/// message. Nothing outside that loop can out-run it, which is why the real window
		/// kept flashing out from under the ghost no matter how fast it was re-parked. The
		/// loop breaks on WM_CANCELMODE, and from then on the window only moves when this
		/// class moves it. The mouse button is still down, so the drop now has to come from
		/// the global mouse hook (<see cref="OnGlobalMouseUp"/>) rather than MOVESIZEEND.
		/// </para>
		/// </summary>
		private void TakeOverFromMoveLoop(IWindow window)
		{
			if (_ownsDrag) return;

			_ownsDrag = true;
			_ignoreNextMoveEnd = true;
			Win32.PostMessage(window.Handle, Win32.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
			Log.Window("DRAG", "Took over from the OS move loop (WM_CANCELMODE)", window);
		}

		/// <summary>
		/// Drop signal once <see cref="TakeOverFromMoveLoop"/> has run — the button comes up
		/// with no move loop left to raise MOVESIZEEND. Returns true when it consumed the
		/// release, so the caller leaves its own click handling alone.
		/// </summary>
		public bool OnGlobalMouseUp(double physicalX, double physicalY)
		{
			if (!_ownsDrag || _trackedWindow is null) return false;
			CompleteDrag(_trackedWindow, State, physicalX, physicalY);
			return true;
		}

		public void OnWindowMoveEnd(IWindow window)
		{
			if (_ignoreNextMoveEnd && window == _trackedWindow)
			{
				// Our own WM_CANCELMODE, not the user letting go.
				_ignoreNextMoveEnd = false;
				Log.Info("DRAG", "MOVESIZEEND from our own take-over, drag continues");
				return;
			}

			var state = State;
			if (window != _trackedWindow || state == DragState.None)
			{
				Reset();
				return;
			}

			Win32.GetCursorPos(out var dropCursor);
			CompleteDrag(window, state, dropCursor.X, dropCursor.Y);
		}

		private async void CompleteDrag(IWindow window, DragState state, double cursorX, double cursorY)
		{
			try
			{
				StopFrameLoop();

				// This button-up ends a drag; it must not also count as a click on the desktop
				// behind the sidebar. The tray→stage direction has always done this in
				// CancelWpfDrag — the stage→tray direction never did, and taking over the move
				// loop removed the accident that used to cover for it: MOVESIZEEND once arrived
				// with the real release, but now fires ~a second earlier at WM_CANCELMODE, far
				// outside the 300 ms window that suppression relies on.
				_sceneManager.WindowsManager.SuppressNextDesktopClick();

				if (state == DragState.ShrinkingInBuffer)
				{
					Win32.SetWindowStyleLongPtr(window.Handle, _originalStyle);

					if (cursorX < _sidebarWidthPhysical)
					{
						Log.Window("DRAG", "Dropped in sidebar, separating window", window);
						_ghost.Hide();
						State = DragState.None;
						_sceneManager.SeparateWindowToNewScene(window);

						await Dispatcher.CurrentDispatcher.InvokeAsync(() => { },
							DispatcherPriority.Loaded);
						_syncVisibility();
					}
					else
					{
						Log.Info("DRAG", "Dropped in buffer zone, cancelling");
						_ghost.Hide();
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
				Log.Info("DRAG", $"CompleteDrag failed: {ex.Message}");
				Reset();
			}
		}

		/// <summary>
		/// Re-parks the dragged window if anything drags it back on screen.
		/// <para>
		/// This is the ONLY thing that re-parks during the shrink — the frame loop deliberately
		/// does not, because a park is six P/Invokes, a semaphore, a cross-process SetWindowPos
		/// and a flushed log line, and doing that every frame is what made the stage→tray
		/// direction judder next to tray→stage. EVENT_OBJECT_LOCATIONCHANGE fires on any move
		/// the window actually makes, so reacting to it costs nothing while nothing moves.
		/// </para>
		/// </summary>
		public void OnWindowMoved(IWindow window)
		{
			if (State != DragState.ShrinkingInBuffer) return;
			if (_trackedWindow is null || window.Handle != _trackedWindow.Handle) return;

			// Our own park raises this event too. Re-parking an already-parked window would
			// bounce that back forever, so only act while it is somewhere visible.
			if (!IsOnScreen(window)) return;

			_sceneManager.ParkWindow(window);
		}

		private static bool IsOnScreen(IWindow window)
		{
			var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
			var loc = window.Location;
			return loc.X < vs.Right && loc.Y < vs.Bottom;
		}

		/// <summary>
		/// Drives the drag off <see cref="CompositionTarget.Rendering"/> — once per composition
		/// frame, whatever the monitor runs at, instead of on a 16 ms timer that both quantized
		/// the cursor path to its own interval and beat against the display's refresh rate.
		/// The tray→stage direction, driven by WPF's MouseMove, has neither problem.
		/// </summary>
		private void StartFrameLoop()
		{
			if (_frameLoopRunning) return;

			_renderedCursor = long.MinValue;
			CompositionTarget.Rendering += OnFrame;
			_frameLoopRunning = true;
		}

		private void StopFrameLoop()
		{
			if (!_frameLoopRunning) return;
			CompositionTarget.Rendering -= OnFrame;
			_frameLoopRunning = false;
		}

		private void OnFrame(object? sender, EventArgs e)
		{
			if (_trackedWindow == null)
			{
				StopFrameLoop();
				return;
			}

			var state = State;
			if (state != DragState.TrackingWindowDrag && state != DragState.ShrinkingInBuffer)
			{
				StopFrameLoop();
				return;
			}

			if (!Win32.GetCursorPos(out var cursor)) return;

			// Nothing moved since the last frame, so nothing below can produce a different
			// result. Every branch here is a function of the cursor alone.
			var packed = Pack(cursor.X, cursor.Y);
			if (packed == _renderedCursor) return;
			_renderedCursor = packed;

			double mouseX = cursor.X;
			double mouseY = cursor.Y;

			if (state == DragState.TrackingWindowDrag)
			{
				// Once we own the drag the OS no longer moves anything, so carrying the
				// window with the cursor outside the buffer is on us. Same grip the ghost
				// uses, and the same one ScenesControl_MouseMove gives the real window past
				// its buffer — so crossing the boundary in either direction moves nothing.
				if (_ownsDrag)
				{
					int winW = (int)(_originalWindowRect.Width * _dragDpi.X);
					Win32.SetWindowPos(_trackedWindow.Handle, IntPtr.Zero,
						(int)(mouseX - winW / 2.0),
						(int)(CardTopFor(mouseY / _dragDpi.Y, _originalWindowRect.Height) * _dragDpi.Y),
						0, 0,
						Win32.SetWindowPosFlags.IgnoreResize | Win32.SetWindowPosFlags.DoNotActivate);
				}

				if (mouseX < _bufferRightPhysical)
					EnterBufferZone(_trackedWindow);
				return;
			}

			// Exit buffer zone (cursor dragged back right)
			if (mouseX > _bufferRightPhysical)
			{
				ExitBufferZone();
				return;
			}

			// No re-park here: the move loop that used to fight the park is gone (see
			// TakeOverFromMoveLoop) and OnWindowMoved catches anything that still moves.

			// Interpolate ghost size: t=0 at buffer edge, t=1 at sidebar edge
			var bufferWidth = _bufferRightPhysical - _sidebarWidthPhysical;
			var t = Math.Clamp((_bufferRightPhysical - mouseX) / bufferWidth, 0.0, 1.0);

			// Lerp in logical coordinates; convert physical cursor to logical
			var ghostW = Lerp(_originalWindowRect.Width, _targetThumbSize.Width, t);
			var ghostH = Lerp(_originalWindowRect.Height, _targetThumbSize.Height, t);
			var ghostX = mouseX / _dragDpi.X - ghostW / 2;
			var ghostY = CardTopFor(mouseY / _dragDpi.Y, ghostH);

			// Flat on stage (t=0) → full tray tilt at the sidebar edge (t=1), matching the
			// resting tray card so the handoff into the tray has no pop.
			var skew = Lerp(0.0, CompositionThumbnail.TrayTiltDegrees, t);
			_ghost.UpdatePositionAndSize(ghostX, ghostY, ghostW, ghostH, skew);
		}

		private void ExitBufferZone()
		{
			if (_trackedWindow is null) return;
			Log.Info("DRAG", "Exited buffer zone (cursor moved right)");
			_ghost.Hide();
			Win32.SetWindowStyleLongPtr(_trackedWindow.Handle, _originalStyle);
			RestoreRealWindow(_trackedWindow);
			State = DragState.TrackingWindowDrag;
			// Don't stop the frame loop — OnFrame handles TrackingWindowDrag for re-entry
		}

		private void HideRealWindow(IWindow window)
		{
			_sceneManager.ParkWindow(window);
			Log.Window("DRAG", "Parked off-screen (alpha intact for WGC)", window);
		}

		private void RestoreRealWindow(IWindow window)
		{
			_sceneManager.RestoreWindow(window);
			Log.Window("DRAG", "Restored to saved on-stage rect", window);
		}

		private void Reset()
		{
			StopFrameLoop();
			if (State == DragState.ShrinkingInBuffer && _trackedWindow != null)
			{
				try
				{
					Win32.SetWindowStyleLongPtr(_trackedWindow.Handle, _originalStyle);
					RestoreRealWindow(_trackedWindow);
					_ghost.Hide();
				}
				catch { }
			}
			State = DragState.None;
			_trackedWindow = null;
			_ownsDrag = false;
			_ignoreNextMoveEnd = false;
		}

		internal static double Lerp(double a, double b, double t) => a + (b - a) * t;
	}
}
