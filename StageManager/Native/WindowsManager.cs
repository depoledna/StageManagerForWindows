using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Native
{
	public delegate void WindowDelegate(IWindow window);
	public delegate void WindowCreateDelegate(IWindow window, bool firstCreate);
	public delegate void WindowUpdateDelegate(IWindow window, WindowUpdateType type);

	public class WindowsManager : IWindowsManager
	{
		private bool _active;
		private IDictionary<IntPtr, WindowsWindow> _windows;
		private WinEventDelegate _hookDelegate;

		private WindowsWindow _mouseMoveWindow;
		private readonly object _mouseMoveLock = new object();
		private Win32.HookProc _mouseHook;

		private Dictionary<WindowsWindow, bool> _floating;
		private IntPtr _currentProcessWindowHandle;
		private int _currentProcessId;
		private DateTime _lastLeftButtonDown;
		// Double-click handling to suppress scene toggle when user double-clicks a desktop item
		private readonly int _doubleClickTime; // system double-click time in ms
		private bool _desktopClickPending;
		private DateTime _desktopClickTime;
		private IntPtr _desktopClickHandle;
		private readonly object _desktopClickLock = new object();
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

		public event EventHandler<IntPtr> UntrackedFocus;

		/// <summary>
		/// Notifies when a window focuses itself
		/// </summary>
		public event WindowFocusDelegate WindowFocused;

		public event EventHandler WindowMoved;

		/// <summary>
		/// Notifies when a window updated itself
		/// This is used to externally notify when an update was applied to a window
		/// </summary>
		public event WindowDelegate ExternalWindowUpdate;
		/// <summary>
		/// Notifies when a window closes itself
		/// This is used to externally notify when a window was closed
		/// </summary>
		public event WindowDelegate ExternalWindowClosed;

		public IEnumerable<IWindow> Windows => _windows.Values;

		public WindowsManager()
		{
			_windows = new Dictionary<IntPtr, WindowsWindow>();
			_floating = new Dictionary<WindowsWindow, bool>();
			_hookDelegate = new WinEventDelegate(WindowHook);

			// Cache system double-click time for later use
			_doubleClickTime = (int)100;
		}

		public Task Start()
		{
			_active = true;
			Log.Info("STARTUP", "WindowsManager starting, registering hooks...");

			var currentProcess = Process.GetCurrentProcess();
			_currentProcessId = currentProcess.Id;
			_currentProcessWindowHandle = currentProcess.MainWindowHandle;

			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY, Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED, Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookDelegate, 0, 0, 0);
			Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _hookDelegate, 0, 0, 0);

			_mouseHook = MouseHook;

			Win32.EnumWindows((handle, param) =>
			{
				if (Win32Helper.IsAppWindow(handle))
				{
					RegisterWindow(handle, false);
				}
				return true;
			}, IntPtr.Zero);

			var thread = new Thread(() =>
			{
				Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseHook, currentProcess.MainModule.BaseAddress, 0);
				Application.Run();
			});

			thread.Name = "WindowsManager";
			thread.Start();

			Log.Info("STARTUP", $"WindowsManager started, tracking {_windows.Count} windows");
			return Task.CompletedTask;
		}

		public void Stop()
		{
			_active = false;

			// Clean up window event subscriptions to prevent memory leaks
			foreach (var window in _windows.Values)
			{
				if (window != null)
				{
					window.WindowFocused -= null;
					window.WindowUpdated -= null;
					window.WindowClosed -= null;
				}
			}

			// Clear collections to release references
			_windows.Clear();
			_floating.Clear();
		}

		public IWindowsDeferPosHandle DeferWindowsPos(int count)
		{
			var info = Win32.BeginDeferWindowPos(count);
			return new WindowsDeferPosHandle(info);
		}

		public void ToggleFocusedWindowTiling()
		{
			if (!_active)
				return;

			var window = _windows.Values.FirstOrDefault(w => w.IsFocused);

			if (window != null)
			{
				if (_floating.ContainsKey(window))
				{
					_floating.Remove(window);
					HandleWindowAdd(window, false);
				}
				else
				{
					_floating[window] = true;
					HandleWindowRemove(window);
					window.BringToTop();
				}
				window.Focus();
			}
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

					// Detect short click (<250 ms) and desktop in foreground
					var clickDuration = DateTime.Now - _lastLeftButtonDown;
					if (clickDuration.TotalMilliseconds < 250)
					{
						var foreground = Win32.GetForegroundWindow();
						if (foreground != IntPtr.Zero)
						{
							// Check class name of the foreground window
							var buffer = new System.Text.StringBuilder(256);
							Win32.GetClassName(foreground, buffer, buffer.Capacity);
							var cls = buffer.ToString();
							if (cls == "WorkerW" || cls == "Progman")
							{
								// Schedule the desktop click after the system double-click interval.
								// This allows us to detect and ignore double-clicks on desktop items.
								lock (_desktopClickLock)
								{
									_desktopClickPending = true;
									_desktopClickTime = DateTime.Now;
									_desktopClickHandle = foreground;
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
					window.WindowFocused += (sender) => HandleWindowFocused(sender);
					window.WindowUpdated += (sender) => HandleWindowUpdated(sender);
					window.WindowClosed += (sender) => HandleWindowClosed(sender);

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
			else
			{
				UntrackedFocus?.Invoke(this, handle);
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

		private void HandleWindowFocused(IWindow window)
		{
			if (!_active)
				return;

			WindowFocused?.Invoke(window);
		}

		private void HandleWindowUpdated(IWindow window)
		{
			if (!_active)
				return;

			ExternalWindowUpdate?.Invoke(window);
		}

		private void HandleWindowClosed(IWindow window)
		{
			if (!_active)
				return;

			ExternalWindowClosed?.Invoke(window);
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

					window.IsMouseMoving = false;
					WindowMoved?.Invoke(window, EventArgs.Empty);
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
