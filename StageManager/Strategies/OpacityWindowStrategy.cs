using StageManager.Native.Window;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics; // Added for Dictionary

namespace StageManager.Strategies
{
	/// <summary>
	/// Works well with opacity = 0, higher opacity will make the windows appear when clicked
	/// Visual Studio cannot be hidden this way, might be the same with other windows
	/// </summary>
	internal class OpacityWindowStrategy : IWindowStrategy
	{
		[DllImport("user32.dll")]
		static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

		[DllImport("user32.dll")]
		static extern int GetWindowLong(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll")]
		public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

		public const int GWL_EXSTYLE = -20;
		public const int WS_EX_LAYERED = 0x80000;
		public const int WS_EX_TRANSPARENT = 0x20;   // ignore mouse / hit-testing
		public const int LWA_ALPHA = 0x2;

		// Remember previous styles so we can restore them when showing again.
		private static readonly Dictionary<IntPtr, int> _originalStyles = new();
		// Remember original on-screen position when we had to move the window off-screen
		private static readonly Dictionary<IntPtr, (int X, int Y)> _originalPositions = new();

		// Atomic state management
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, System.Threading.SemaphoreSlim> _windowLocks = new();
		private static readonly object _globalLock = new object();

		private const int AnimationDurationMs = 200; // total duration for fade animation
		private const int AnimationSteps = 20;      // number of alpha steps – higher = smoother

		// Window state validation
		private static bool IsWindowValidForTransparency(IntPtr hWnd)
		{
			// Note: IsWindow is not available in Win32 class, so we'll use IsWindowVisible as the primary check
			return StageManager.Native.PInvoke.Win32.IsWindowVisible(hWnd) &&    // Window is visible (not already hidden)
			       !StageManager.Native.PInvoke.Win32.IsIconic(hWnd) &&          // Window is not minimized
			       GetWindowLong(hWnd, GWL_EXSTYLE) != 0;                     // Extended style accessible
		}

		// Cleanup mechanism for destroyed windows
		private static void CleanupWindowLock(IntPtr hWnd)
		{
			_windowLocks.TryRemove(hWnd, out _);
		}

		// Helper to determine when to skip transparency for a window
		private static bool ShouldSkipTransparencyForWindow(IntPtr hWnd)
		{
			// Skip transparency for minimized windows - let Windows handle them natively
			if (StageManager.Native.PInvoke.Win32.IsIconic(hWnd))
				return true;

			// Skip transparency for windows that aren't visible (already hidden/destroyed)
			if (!StageManager.Native.PInvoke.Win32.IsWindowVisible(hWnd))
				return true;

			// Skip transparency if extended style is not accessible
			if (GetWindowLong(hWnd, GWL_EXSTYLE) == 0)
				return true;

			// Otherwise, transparency can be applied
			return false;
		}

		// Helper to run fade animation without blocking the UI thread
		private static void FadeWindow(IntPtr hWnd, byte fromAlpha, byte toAlpha)
		{
			// Ensure window stays layered so alpha changes take effect
			if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_LAYERED) == 0)
			{
				SetWindowLong(hWnd, GWL_EXSTYLE, GetWindowLong(hWnd, GWL_EXSTYLE) | WS_EX_LAYERED);
			}

			int step = (toAlpha - fromAlpha) / AnimationSteps;
			if (step == 0) step = toAlpha > fromAlpha ? 1 : -1;
			int delay = AnimationDurationMs / AnimationSteps;

			System.Threading.Tasks.Task.Run(() =>
			{
				byte alpha = fromAlpha;
				for (int i = 0; i < AnimationSteps; i++)
				{
					SetLayeredWindowAttributes(hWnd, 0, (byte)alpha, LWA_ALPHA);
					alpha = (byte)(alpha + step);
					System.Threading.Thread.Sleep(delay);
				}

				// Ensure final alpha value is set precisely
				SetLayeredWindowAttributes(hWnd, 0, toAlpha, LWA_ALPHA);
			});
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
				if (StageManager.Native.PInvoke.Win32.IsIconic(hWnd))
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
					if (StageManager.Native.PInvoke.Win32.IsIconic(hWnd))
					{
						window.ShowMinimized();
					}
					return;
				}

				var exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

				// Restore original extended style if we stored one (atomically)
				lock (_globalLock)
				{
					if (_originalStyles.TryGetValue(hWnd, out var original))
					{
						SetWindowLong(hWnd, GWL_EXSTYLE, original);
						_originalStyles.Remove(hWnd);
					}
				}

				// Restore original on-screen position if we moved it off-screen (atomically)
				lock (_globalLock)
				{
					if (_originalPositions.TryGetValue(hWnd, out var pos))
					{
						StageManager.Native.PInvoke.Win32.SetWindowPos(hWnd, IntPtr.Zero,
							pos.X, pos.Y, 0, 0,
							StageManager.Native.PInvoke.Win32.SetWindowPosFlags.IgnoreResize |
							StageManager.Native.PInvoke.Win32.SetWindowPosFlags.DoNotActivate);
						_originalPositions.Remove(hWnd);
					}
				}

				// Force-remove WS_EX_TRANSPARENT (mouse-through) in case anything went wrong previously
				int clearedStyle = GetWindowLong(hWnd, GWL_EXSTYLE) & ~WS_EX_TRANSPARENT;
				SetWindowLong(hWnd, GWL_EXSTYLE, clearedStyle);

				// Ensure WS_EX_LAYERED remains so we can control alpha smoothly
				if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_LAYERED) == 0)
				{
					SetWindowLong(hWnd, GWL_EXSTYLE, GetWindowLong(hWnd, GWL_EXSTYLE) | WS_EX_LAYERED);
				}

				// Instantly set full opacity (no fade — the transition animation placeholder covers the reveal)
				Log.Window("OPACITY", "Instant show alpha→255", window);
				SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);

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

				// Store original exstyle atomically
				lock (_globalLock)
				{
					if (!_originalStyles.ContainsKey(hWnd))
					{
						_originalStyles[hWnd] = GetWindowLong(hWnd, GWL_EXSTYLE);
					}
				}

				// Enable layered + transparent styles so we can animate alpha and disable hit-testing afterwards
				var newStyle = GetWindowLong(hWnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TRANSPARENT;
				SetWindowLong(hWnd, GWL_EXSTYLE, newStyle);

				// Instantly hide window by setting alpha to 0 (no fade) so there is no brief overlap
				Log.Window("OPACITY", "Instant hide alpha→0", window);
				SetLayeredWindowAttributes(hWnd, 0, 0, LWA_ALPHA);

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
								StageManager.Native.PInvoke.Win32.Rect rect = new StageManager.Native.PInvoke.Win32.Rect();
								StageManager.Native.PInvoke.Win32.GetWindowRect(hWnd, ref rect);
								_originalPositions[hWnd] = (rect.Left, rect.Top);
							}
						}

						const int OFFSCREEN_OFFSET = 4000; // beyond typical virtual screen bounds
						StageManager.Native.PInvoke.Win32.SetWindowPos(hWnd, IntPtr.Zero,
							OFFSCREEN_OFFSET, OFFSCREEN_OFFSET, 0, 0,
							StageManager.Native.PInvoke.Win32.SetWindowPosFlags.IgnoreResize |
							StageManager.Native.PInvoke.Win32.SetWindowPosFlags.DoNotActivate);
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
			return SetLayeredWindowAttributes(hWnd, 0, 1, LWA_ALPHA);
		}
	}
}