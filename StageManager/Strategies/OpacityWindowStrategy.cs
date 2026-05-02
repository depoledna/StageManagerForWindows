using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;

namespace StageManager.Strategies
{
	/// <summary>
	/// Works well with opacity = 0, higher opacity will make the windows appear when clicked
	/// Visual Studio cannot be hidden this way, might be the same with other windows
	/// </summary>
	internal class OpacityWindowStrategy : IWindowStrategy
	{
		// Remember previous styles so we can restore them when showing again.
		private static readonly Dictionary<IntPtr, Win32.WS_EX> _originalStyles = new();
		// Remember original on-screen position when we had to move the window off-screen
		private static readonly Dictionary<IntPtr, (int X, int Y)> _originalPositions = new();

		// Atomic state management
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, System.Threading.SemaphoreSlim> _windowLocks = new();
		private static readonly object _globalLock = new object();

		/// <summary>
		/// Cleans up all per-window state (locks, saved styles, saved positions) when a window is destroyed.
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
				_originalStyles.Remove(hWnd);
				_originalPositions.Remove(hWnd);
			}
		}

		// Helper to determine when to skip transparency for a window
		private static bool ShouldSkipTransparencyForWindow(IntPtr hWnd)
		{
			// Skip transparency for minimized windows - let Windows handle them natively
			if (Win32.IsIconic(hWnd))
				return true;

			// Skip transparency for windows that aren't visible (already hidden/destroyed)
			if (!Win32.IsWindowVisible(hWnd))
				return true;

			// Skip transparency if extended style is not accessible
			if ((long)Win32.GetWindowExStyleLongPtr(hWnd) == 0)
				return true;

			// Otherwise, transparency can be applied
			return false;
		}

		public void Show(IWindow window)
		{
			var hWnd = window.Handle;

			// Skip transparency for minimized windows - let Windows handle them natively
			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Show SKIPPED (invalid/minimized)", window);

				// For minimized windows, don't apply any transparency logic
				// Just ensure they're in proper minimized state and return
				if (Win32.IsIconic(hWnd))
				{
					window.ShowMinimized();
				}
				return;
			}

			// Per-window atomic operation
			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			lockSem.Wait();
			try
			{
				// Double-check after acquiring lock
				if (ShouldSkipTransparencyForWindow(hWnd))
				{
					if (Win32.IsIconic(hWnd))
					{
						window.ShowMinimized();
					}
					return;
				}

				// Restore original extended style if we stored one (atomically)
				lock (_globalLock)
				{
					if (_originalStyles.TryGetValue(hWnd, out var original))
					{
						Win32.SetWindowStyleExLongPtr(hWnd, original);
						_originalStyles.Remove(hWnd);
					}
				}

				// Restore original on-screen position if we moved it off-screen (atomically)
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

				// Force-remove WS_EX_TRANSPARENT (mouse-through) in case anything went wrong previously
				var ex = Win32.GetWindowExStyleLongPtr(hWnd);
				Win32.SetWindowStyleExLongPtr(hWnd, ex & ~Win32.WS_EX.WS_EX_TRANSPARENT);

				// Instantly set full opacity (no fade — the transition animation placeholder covers the reveal).
				// Win32Helper.SetAlpha ensures WS_EX_LAYERED is set before applying alpha.
				Log.Window("OPACITY", "Instant show alpha→255", window);
				Win32Helper.SetAlpha(hWnd, 255);

				// Bring window to top immediately so it's in front while fading in
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

			// Skip transparency for minimized windows - let Windows handle them natively
			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Hide SKIPPED (invalid/minimized)", window);
				return; // Skip minimized/invalid windows to prevent issues
			}

			// Per-window atomic operation
			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			lockSem.Wait();
			try
			{
				// Double-check after acquiring lock (double-check locking pattern)
				if (ShouldSkipTransparencyForWindow(hWnd))
				{
					Log.Window("OPACITY", "Hide SKIPPED (double-check)", window);
					return;
				}

				var ex = Win32.GetWindowExStyleLongPtr(hWnd);

				// Store original exstyle atomically
				lock (_globalLock)
				{
					if (!_originalStyles.ContainsKey(hWnd))
					{
						_originalStyles[hWnd] = ex;
					}
				}

				// Enable layered + transparent styles so we can animate alpha and disable hit-testing afterwards
				Win32.SetWindowStyleExLongPtr(hWnd, ex | Win32.WS_EX.WS_EX_LAYERED | Win32.WS_EX.WS_EX_TRANSPARENT);

				// Instantly hide window by setting alpha to 0 (no fade) so there is no brief overlap
				Log.Window("OPACITY", "Instant hide alpha→0", window);
				Win32.SetLayeredWindowAttributes(hWnd, 0, 0, Win32.LWA_ALPHA);

				// Keep mouse-through flag enabled so clicks pass to visible windows underneath
				// Window remains present for live thumbnails.

				// If layered transparency fails, fall back to moving window off-screen handled below.

				// Additional fallback: move window off-screen when transparency is not supported
				if (!_supportsLayeredTransparency(hWnd))
				{
					Log.Window("OPACITY", "Transparency not supported, falling back to off-screen move", window);
					try
					{
						// Store original position only once (atomically)
						lock (_globalLock)
						{
							if (!_originalPositions.ContainsKey(hWnd))
							{
								Win32.Rect rect = new Win32.Rect();
								Win32.GetWindowRect(hWnd, ref rect);
								_originalPositions[hWnd] = (rect.Left, rect.Top);
							}
						}

						const int OFFSCREEN_OFFSET = 4000; // beyond typical virtual screen bounds
						Win32.SetWindowPos(hWnd, IntPtr.Zero,
							OFFSCREEN_OFFSET, OFFSCREEN_OFFSET, 0, 0,
							Win32.SetWindowPosFlags.IgnoreResize |
							Win32.SetWindowPosFlags.DoNotActivate);
					}
					catch { /* ignored */ }
				}
			}
			finally
			{
				lockSem.Release();
			}
		}

		private static bool _supportsLayeredTransparency(IntPtr hWnd)
		{
			// Simple probe – attempt to set alpha 1 and check success
			return Win32.SetLayeredWindowAttributes(hWnd, 0, 1, Win32.LWA_ALPHA);
		}
	}
}
