using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Windows;

namespace StageManager.Strategies
{
	/// <summary>
	/// Hides windows by moving them off-screen (out of the visible desktop bounds) so DWM keeps
	/// compositing them and Windows.Graphics.Capture can still produce live frames for the sidebar.
	/// Off-screen positioning is required because WGC captures DWM-composited output (post-alpha) —
	/// the previous alpha=0 trick is invisible to the compositor and would yield transparent frames.
	/// </summary>
	internal class OpacityWindowStrategy : IWindowStrategy
	{
		// Saved on-screen position so Show() can restore the parked window.
		private static readonly Dictionary<IntPtr, (int X, int Y)> _originalPositions = new();

		// Atomic state management
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, System.Threading.SemaphoreSlim> _windowLocks = new();
		private static readonly object _globalLock = new object();

		/// <summary>
		/// Returns the saved on-screen position for a window currently parked off-screen by Hide,
		/// or false if the window has no saved position. Animator uses this to compute the
		/// *intended* incoming-window rect instead of reading the live (off-screen) Bounds.
		/// </summary>
		public static bool TryGetOriginalPosition(IntPtr hWnd, out int x, out int y)
		{
			lock (_globalLock)
			{
				if (_originalPositions.TryGetValue(hWnd, out var pos))
				{
					x = pos.X; y = pos.Y;
					return true;
				}
			}
			x = 0; y = 0;
			return false;
		}

		/// <summary>
		/// Cleans up all per-window state (locks, saved positions) when a window is destroyed.
		/// </summary>
		public static void CleanupWindow(IntPtr hWnd)
		{
			if (_windowLocks.TryRemove(hWnd, out var sem))
			{
				// Acquire before disposing to avoid ObjectDisposedException
				// in Show/Hide if they're mid-operation on this handle.
				sem.Wait();
				sem.Dispose();
			}
			lock (_globalLock)
			{
				_originalPositions.Remove(hWnd);
			}
		}

		private static (int X, int Y) GetOffScreenPoint()
		{
			// Past the right edge of the virtual screen (all monitors combined).
			// +100 keeps a margin in case a monitor is hot-plugged at the right edge.
			var x = (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) + 100;
			var y = (int)(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) + 100;
			return (x, y);
		}

		private static bool ShouldSkipTransparencyForWindow(IntPtr hWnd)
		{
			if (Win32.IsIconic(hWnd))
				return true;

			if (!Win32.IsWindowVisible(hWnd))
				return true;

			if ((long)Win32.GetWindowExStyleLongPtr(hWnd) == 0)
				return true;

			return false;
		}

		public void Show(IWindow window)
		{
			var hWnd = window.Handle;

			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Show SKIPPED (invalid/minimized)", window);

				if (Win32.IsIconic(hWnd))
				{
					window.ShowMinimized();
				}
				return;
			}

			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			lockSem.Wait();
			try
			{
				if (ShouldSkipTransparencyForWindow(hWnd))
				{
					if (Win32.IsIconic(hWnd))
					{
						window.ShowMinimized();
					}
					return;
				}

				lock (_globalLock)
				{
					if (_originalPositions.TryGetValue(hWnd, out var pos))
					{
						Win32.SetWindowPos(hWnd, IntPtr.Zero,
							pos.X, pos.Y, 0, 0,
							Win32.SetWindowPosFlags.IgnoreResize |
							Win32.SetWindowPosFlags.DoNotActivate);
						_originalPositions.Remove(hWnd);
					}
				}

				// Clear WS_EX_TRANSPARENT (mouse-through) — UncloakStartupMinimized in
				// WindowsManager sets it on cloaked startup-minimized windows; this is
				// where it gets cleared the first time the window is shown via a scene.
				var ex = Win32.GetWindowExStyleLongPtr(hWnd);
				Win32.SetWindowStyleExLongPtr(hWnd, ex & ~Win32.WS_EX.WS_EX_TRANSPARENT);

				// Win32Helper.SetAlpha ensures WS_EX_LAYERED is set before applying alpha.
				Log.Window("OPACITY", "Instant show alpha→255", window);
				Win32Helper.SetAlpha(hWnd, 255);

				window.BringToTop();
			}
			finally
			{
				lockSem.Release();
			}
		}

		public void Hide(IWindow window)
		{
			var hWnd = window.Handle;

			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Hide SKIPPED (invalid/minimized)", window);
				return;
			}

			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			lockSem.Wait();
			try
			{
				if (ShouldSkipTransparencyForWindow(hWnd))
				{
					Log.Window("OPACITY", "Hide SKIPPED (double-check)", window);
					return;
				}

				var point = GetOffScreenPoint();
				Log.Window("OPACITY", $"Move off-screen to ({point.X},{point.Y})", window);
				lock (_globalLock)
				{
					if (!_originalPositions.ContainsKey(hWnd))
					{
						Win32.Rect rect = new Win32.Rect();
						Win32.GetWindowRect(hWnd, ref rect);
						_originalPositions[hWnd] = (rect.Left, rect.Top);
					}
				}

				Win32.SetWindowPos(hWnd, IntPtr.Zero,
					point.X, point.Y, 0, 0,
					Win32.SetWindowPosFlags.IgnoreResize |
					Win32.SetWindowPosFlags.DoNotActivate);
			}
			finally
			{
				lockSem.Release();
			}
		}
	}
}
