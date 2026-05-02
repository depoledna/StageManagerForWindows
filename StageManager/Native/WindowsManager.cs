using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using StageManager.Helpers;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Native
{
	public delegate void WindowDelegate(IWindow window);
	public delegate void WindowCreateDelegate(IWindow window, bool firstCreate);
	public delegate void WindowUpdateDelegate(IWindow window, WindowUpdateType type);

	public class WindowsManager : IWindowsManager
	{
		private volatile bool _active;
		private IDictionary<IntPtr, WindowsWindow> _windows;
		private WinEventDelegate _hookDelegate;

		private WindowsWindow _mouseMoveWindow;
		private readonly object _mouseMoveLock = new object();
		private Win32.HookProc _mouseHook;
		private readonly HashSet<IntPtr> _startupMinimizedHandles = new();

		private IntPtr _currentProcessWindowHandle;
		private int _currentProcessId;
		private DateTime _lastLeftButtonDown;
		// Double-click handling to suppress scene toggle when user double-clicks a desktop item
		private readonly int _doubleClickTime; // system double-click time in ms
		private bool _desktopClickPending;
		private DateTime _desktopClickTime;
		private IntPtr _desktopClickHandle;
		private readonly object _desktopClickLock = new object();
		private DateTime _lastDragEnd = DateTime.MinValue;
		/// <summary>
		/// Raised after a completed < 250 ms left-button click when the desktop (WorkerW/Progman) is the foreground window.
		/// The <see cref="IntPtr"/> argument is the handle of the desktop window that had focus.
		/// </summary>
		public event EventHandler<IntPtr> DesktopShortClick;

#if DEBUG
		// Set this to true while debugging to dump detailed information about how each window is
		// evaluated for scene eligibility. The output is written via Debug.WriteLine.
		private const bool DEBUG_WINDOW_FILTER = true;

		private static string FormatStyleFlags(Win32.WS style)
		{
			var flags = new List<string>();
			if (style.HasFlag(Win32.WS.WS_SYSMENU)) flags.Add("SYSMENU");
			if (style.HasFlag(Win32.WS.WS_MINIMIZEBOX)) flags.Add("MINBOX");
			if (style.HasFlag(Win32.WS.WS_MAXIMIZEBOX)) flags.Add("MAXBOX");
			if (style.HasFlag(Win32.WS.WS_CAPTION)) flags.Add("CAPTION");
			if (style.HasFlag(Win32.WS.WS_THICKFRAME)) flags.Add("THICKFRAME");
			return string.Join("|", flags);
		}
#endif
		/// <summary>
		/// Notifies when a new window handle was created by the manager
		/// </summary>
		public event WindowCreateDelegate WindowCreated;
		/// <summary>
		/// Notifies when a handled window was removed by the manager
		/// </summary>
		public event WindowDelegate WindowDestroyed;
		/// <summary>
		/// Notifies when a handled window was updated by the manager
		/// This is used internally by the workspace manager to apply the update to the window
		/// </summary>
		public event WindowUpdateDelegate WindowUpdated;

		public IEnumerable<IWindow> Windows => _windows.Values;

		public WindowsManager()
		{
			_windows = new Dictionary<IntPtr, WindowsWindow>();
			_hookDelegate = new WinEventDelegate(WindowHook);

			_doubleClickTime = (int)Win32.GetDoubleClickTime();
		}

		public Task Start()
		{
			_active = true;
			Log.Info("STARTUP", "WindowsManager starting, registering hooks...");

			var currentProcess = Process.GetCurrentProcess();
			_currentProcessId = currentProcess.Id;
			_currentProcessWindowHandle = currentProcess.MainWindowHandle;

			// Enumerate + un-cloak startup-minimized windows BEFORE registering hooks so the
			// forced SW_SHOWNOACTIVATE doesn't echo EVENT_SYSTEM_MINIMIZEEND back into our pipeline.
			Win32.EnumWindows((handle, param) =>
			{
				if (Win32Helper.IsAppWindow(handle))
				{
					UncloakStartupMinimized(handle);
					RegisterWindow(handle, false);
				}
				return true;
			}, IntPtr.Zero);

			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY, Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED, Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _hookDelegate, 0, 0, 0);

			_mouseHook = MouseHook;

			var thread = new Thread(() =>
			{
				try
				{
					Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseHook, currentProcess.MainModule.BaseAddress, 0);
					Application.Run();
				}
				catch (Exception ex)
				{
					Log.Fatal("HOOK", $"Hook thread crashed: {ex}");
				}
			});

			thread.Name = "WindowsManager";
			thread.IsBackground = true;
			thread.Start();

			Log.Info("STARTUP", $"WindowsManager started, tracking {_windows.Count} windows");
			return Task.CompletedTask;
		}

		public void Stop()
		{
			_active = false;
			Application.Exit();

			// Restore startup-minimized windows to their original minimized state so the user
			// doesn't find them stuck cloaked at alpha=0 after the app exits.
			foreach (var hwnd in _startupMinimizedHandles)
			{
				try
				{
					var ex = Win32.GetWindowExStyleLongPtr(hwnd);
					Win32.SetWindowStyleExLongPtr(hwnd, ex & ~Win32.WS_EX.WS_EX_TRANSPARENT);
					Win32Helper.SetAlpha(hwnd, 255);
					Win32.ShowWindow(hwnd, Win32.SW.SW_SHOWMINNOACTIVE);
				}
				catch { /* best-effort during shutdown */ }
			}
			_startupMinimizedHandles.Clear();

			// Clean up window event subscriptions to prevent memory leaks
			foreach (var window in _windows.Values)
			{
				window?.ClearEvents();
			}

			// Clear collections to release references
			_windows.Clear();
		}

		private void UncloakStartupMinimized(IntPtr hwnd)
		{
			if (!Win32.IsIconic(hwnd))
				return;

			try
			{
				// Cloak BEFORE the show call so the window emerges already invisible — no flash,
				// no focus steal, no z-order disruption.
				Win32Helper.SetAlpha(hwnd, 0);
				var ex = Win32.GetWindowExStyleLongPtr(hwnd);
				Win32.SetWindowStyleExLongPtr(hwnd, ex | Win32.WS_EX.WS_EX_TRANSPARENT);

				// Restore without activation. DWM now composites the window so its thumbnail goes live.
				Win32.ShowWindow(hwnd, Win32.SW.SW_SHOWNOACTIVATE);

				_startupMinimizedHandles.Add(hwnd);
				Log.Info("STARTUP", $"Uncloaked minimized window 0x{hwnd.ToInt64():X} for live preview");
			}
			catch (Exception ex)
			{
				Log.Fatal("STARTUP", $"Failed to uncloak minimized window 0x{hwnd.ToInt64():X}: {ex.Message}");
			}
		}

		public IWindowsDeferPosHandle DeferWindowsPos(int count)
		{
			var info = Win32.BeginDeferWindowPos(count);
			return new WindowsDeferPosHandle(info);
		}

		private IntPtr MouseHook(int nCode, UIntPtr wParam, IntPtr lParam)
		{
			if (nCode == 0)
			{
				var msg = (uint)wParam;

				if (msg == Win32.WM_LBUTTONDOWN)
				{
					// If we already have a desktop click pending and another click starts within the
					// double-click interval, treat it as a double-click and cancel the pending action.
					lock (_desktopClickLock)
					{
						if (_desktopClickPending && (DateTime.Now - _desktopClickTime).TotalMilliseconds <= _doubleClickTime)
						{
							_desktopClickPending = false; // cancel pending single click
						}
					}

					_lastLeftButtonDown = DateTime.Now;
				}
				else if (msg == Win32.WM_LBUTTONUP)
				{
					HandleWindowMoveEnd();

					// Detect click on desktop surface using window under cursor
					Win32.GetCursorPos(out var cursorPt);
					var windowUnderCursor = Win32.WindowFromPoint(new System.Drawing.Point(cursorPt.X, cursorPt.Y));
					if (windowUnderCursor != IntPtr.Zero)
					{
						if (DesktopShellClassifier.IsDesktopShell(windowUnderCursor))
						{
							// Suppress false desktop clicks from drag-drop mouse-up landing on desktop behind sidebar
							if ((DateTime.Now - _lastDragEnd).TotalMilliseconds < 300)
							{ /* drag just ended — skip desktop click */ }
							else lock (_desktopClickLock)
							{
								_desktopClickPending = true;
								_desktopClickTime = DateTime.Now;
								_desktopClickHandle = windowUnderCursor;
							}

							Task.Run(async () =>
							{
								await Task.Delay(_doubleClickTime);
								lock (_desktopClickLock)
								{
									if (_desktopClickPending && (DateTime.Now - _desktopClickTime).TotalMilliseconds >= _doubleClickTime)
									{
										DesktopShortClick?.Invoke(this, _desktopClickHandle);
									}
									_desktopClickPending = false;
								}
							});
						}
					}
				}
			}

			return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
		}

		private void WindowHook(IntPtr hWinEventHook, Win32.EVENT_CONSTANTS eventType, IntPtr hwnd, Win32.OBJID idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
		{
			if (!_active)
				return;

			if (EventWindowIsValid(idChild, idObject, hwnd))
			{
				switch (eventType)
				{
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW:
						RegisterWindow(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY:
						UnregisterWindow(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED:
						UpdateWindow(hwnd, WindowUpdateType.Hide);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED:
						UpdateWindow(hwnd, WindowUpdateType.Show);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART:
						UpdateWindow(hwnd, WindowUpdateType.MinimizeStart);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND:
						UpdateWindow(hwnd, WindowUpdateType.MinimizeEnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND:
						UpdateWindow(hwnd, WindowUpdateType.Foreground);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART:
						StartWindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND:
						EndWindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE:
						WindowMove(hwnd);
						break;
				}
			}
		}

		private bool EventWindowIsValid(int idChild, Win32.OBJID idObject, IntPtr hwnd)
		{
			return idChild == Win32.CHILDID_SELF && idObject == Win32.OBJID.OBJID_WINDOW && hwnd != IntPtr.Zero;
		}

		private void RegisterWindow(IntPtr handle, bool emitEvent = true)
		{
			if (!_active)
				return;

			if (handle == _currentProcessWindowHandle)
				return;

			if (!_windows.ContainsKey(handle))
			{
				var window = new WindowsWindow(handle);

				if (window.ProcessId < 0 || window.ProcessId == _currentProcessId)
					return;

#if DEBUG
				if (DEBUG_WINDOW_FILTER)
				{
					var preStyle = Win32.GetWindowStyleLongPtr(handle);
					Debug.WriteLine($"[WindowFilter] PRE     0x{((long)preStyle):X8} [{FormatStyleFlags(preStyle)}] {window}");
				}
#endif
				bool candidate = window.IsCandidate();

#if DEBUG
				if (DEBUG_WINDOW_FILTER)
				{
					Debug.WriteLine($"[WindowFilter] {(candidate ? "ACCEPT" : "REJECT")} {window}");
				}
#endif

				if (candidate)
				{
					_windows[handle] = window;
					Log.Window("TRACK", "Registered", window);

					if (emitEvent)
					{
						HandleWindowAdd(window, true);
					}
				}
			}
		}

		private void UnregisterWindow(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				Log.Window("TRACK", "Unregistered", window);
				_windows.Remove(handle);
				HandleWindowRemove(window);
			}
		}

		private void UpdateWindow(IntPtr handle, WindowUpdateType type)
		{
			if (!_active)
				return;

			if (type == WindowUpdateType.Show && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				WindowUpdated?.Invoke(window, type);
			}
			else if (type == WindowUpdateType.Show)
			{
				RegisterWindow(handle);
			}
			else if (type == WindowUpdateType.Hide && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				if (!window.DidManualHide)
				{
					UnregisterWindow(handle);
				}
				else
				{
					WindowUpdated?.Invoke(window, type);
				}
			}
			else if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				WindowUpdated?.Invoke(window, type);
			}
		}

		private void StartWindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				window.StoreLastLocation();

				HandleWindowMoveStart(window);
				WindowUpdated?.Invoke(window, WindowUpdateType.MoveStart);
			}
		}

		private void EndWindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];

				HandleWindowMoveEnd();
				WindowUpdated?.Invoke(window, WindowUpdateType.MoveEnd);
			}
		}

		private void WindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_mouseMoveWindow != null && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				if (_mouseMoveWindow == window)
					WindowUpdated?.Invoke(window, WindowUpdateType.Move);
			}
		}

		private void HandleWindowMoveStart(WindowsWindow window)
		{
			if (!_active)
				return;

			if (_mouseMoveWindow != null)
				_mouseMoveWindow.IsMouseMoving = false;

			_mouseMoveWindow = window;
			window.IsMouseMoving = true;
		}

		public void SuppressNextDesktopClick()
		{
			_lastDragEnd = DateTime.Now;
		}

		private void HandleWindowMoveEnd()
		{
			if (!_active)
				return;

			lock (_mouseMoveLock)
			{
				if (_mouseMoveWindow != null)
				{
					var window = _mouseMoveWindow;
					_mouseMoveWindow = null;
					_lastDragEnd = DateTime.Now;

					window.IsMouseMoving = false;
				}
			}
		}

		private void HandleWindowAdd(IWindow window, bool firstCreate)
		{
			if (!_active)
				return;

			WindowCreated?.Invoke(window, firstCreate);
		}

		private void HandleWindowRemove(IWindow window)
		{
			if (!_active)
				return;

			WindowDestroyed?.Invoke(window);
		}
	}
}
