using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StageManager.Native
{
	public class WindowsWindow : IWindow
	{
		private IntPtr _handle;
		private bool _didManualHide;

		public event IWindowDelegate? WindowClosed;
		public event IWindowDelegate? WindowUpdated;
		public event IWindowDelegate? WindowFocused;

		public void ClearEvents()
		{
			WindowClosed = null;
			WindowUpdated = null;
			WindowFocused = null;
		}

		private int _processId;
		private string _processName = string.Empty;
		private string _processFileName = string.Empty;
		private string _processExecutable = string.Empty;
		private IWindowLocation? _lastLocation;

		public WindowsWindow(IntPtr handle)
		{
			_handle = handle;

			try
			{
				var process = GetProcessByWindowHandle(_handle);
				_processId = process.Id;
				_processName = process.ProcessName;
				_processExecutable = process.MainModule!.FileName;

				try
				{
					_processFileName = Path.GetFileName(process.MainModule!.FileName);
				}
				catch (System.ComponentModel.Win32Exception)
				{
					_processFileName = "--NA--";
				}
			}
			catch (Exception)
			{
				_processId = -1;
				_processName = "";
				_processFileName = "";
			}
		}

		private Process GetProcessByWindowHandle(IntPtr windowHandle)
		{
			Win32.GetWindowThreadProcessId(windowHandle, out var processId);

			var result = (int)processId;

			var process = Process.GetProcessById(result);

			// handling for UWP apps
			if (process.ProcessName.Contains("ApplicationFrameHost"))
			{
				// TODO
			}

			return process;
		}

		public bool DidManualHide => _didManualHide;

		public string Title
		{
			get
			{
				var buffer = new StringBuilder(255);
				Win32.GetWindowText(_handle, buffer, buffer.Capacity + 1);
				return buffer.ToString();
			}
		}

		public IntPtr Handle => _handle;

		public string Class
		{
			get
			{
				var buffer = new StringBuilder(255);
				Win32.GetClassName(_handle, buffer, buffer.Capacity + 1);
				return buffer.ToString();
			}
		}

		public IWindowLocation Location
		{
			get
			{
				Win32.Rect rect = new Win32.Rect();
				Win32.GetWindowRect(_handle, ref rect);

				WindowState state = WindowState.Normal;
				if (IsMinimized)
				{
					state = WindowState.Minimized;
				}
				else if (IsMaximized)
				{
					state = WindowState.Maximized;
				}

				return new WindowLocation(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, state);
			}
		}

		public void StoreLastLocation()
		{
			_lastLocation = Location;
		}

		public IWindowLocation? PopLastLocation()
		{
			var value = _lastLocation;
			_lastLocation = null;
			return value;
		}

		public Rectangle Offset
		{
			get
			{
				// Window Rect via GetWindowRect
				Win32.Rect rect1 = new Win32.Rect();
				Win32.GetWindowRect(_handle, ref rect1);

				int X1 = rect1.Left;
				int Y1 = rect1.Top;
				int Width1 = rect1.Right - rect1.Left;
				int Height1 = rect1.Bottom - rect1.Top;

				// Window Rect via DwmGetWindowAttribute
				Win32.Rect rect2 = new Win32.Rect();
				int size = Marshal.SizeOf(typeof(Win32.Rect));
				Win32.DwmGetWindowAttribute(_handle, (int)Win32.DwmWindowAttribute.DWMWA_EXTENDED_FRAME_BOUNDS, out rect2, size);

				int X2 = rect2.Left;
				int Y2 = rect2.Top;
				int Width2 = rect2.Right - rect2.Left;
				int Height2 = rect2.Bottom - rect2.Top;

				// Calculate offset
				int X = X1 - X2;
				int Y = Y1 - Y2;
				int Width = Width1 - Width2;
				int Height = Height1 - Height2;

				return new Rectangle(X, Y, Width, Height);
			}
		}

		public int ProcessId => _processId;
		public string ProcessFileName => _processFileName;
		public string ProcessName => _processName;

		public bool CanLayout
		{
			get
			{
				// Determine if the window exposes UI chrome that allows the user to move it. Stationary windows
				// (e.g. menu bar pop-ups, tool windows without a caption or frame) lack these style flags and should
				// therefore be excluded from layout management and scene handling.
				var style = Win32.GetWindowStyleLongPtr(_handle);

				// Detect whether the window exposes any kind of standard window chrome. Many modern
				// applications (Visual Studio, Chrome, etc.) draw their own caption bar and therefore
				// clear the traditional MINIMIZE/MAXIMIZE style bits even though the buttons are
				// visibly present. Treat a window as ‘layoutable’ when it has *any* of the following
				// indicators instead of requiring all three:
				//   • WS_CAPTION  standard title bar
				//   • WS_SYSMENU  has system menu / close button
				//   • WS_MINIMIZEBOX / WS_MAXIMIZEBOX any of the caption buttons available
				//   • WS_THICKFRAME  sizeable frame (classic desktop apps)
				bool hasCaptionControls =
					style.HasFlag(Win32.WS.WS_CAPTION) ||
					style.HasFlag(Win32.WS.WS_SYSMENU) ||
					style.HasFlag(Win32.WS.WS_MINIMIZEBOX) ||
					style.HasFlag(Win32.WS.WS_MAXIMIZEBOX) ||
					style.HasFlag(Win32.WS.WS_THICKFRAME);

				// We experimented with runtime accessibility checks (GetTitleBarInfo) but that
				// excluded legitimate windows that draw a custom title bar. Rely solely on
				// style bits again while relying on explicit process/class ignore lists for
				// special-case pop-ups.

				return _didManualHide ||
					(!Win32Helper.IsCloaked(_handle) /* https://devblogs.microsoft.com/oldnewthing/20200302-00/?p=103507 */ &&
					   Win32Helper.IsAppWindow(_handle) &&
					   Win32Helper.IsAltTabWindow(_handle) &&
					   hasCaptionControls);
			}
		}

		// Removed AreCaptionButtonsVisible – see comment above.

		public bool IsCandidate()
		{
			if (!CanLayout)
				return false;

			var ignoreClasses = new List<string>()
			{
				"TaskManagerWindow",
				"MSCTFIME UI",
				"SHELLDLL_DefView",
				"LockScreenBackstopFrame",
				"Progman",
				"Shell_TrayWnd", // Windows 11 start
				"WorkerW"
			};

			if (ignoreClasses.Contains(Class))
				return false;

			var ignoreProcesses = new List<string>()
			{
				"SearchUI",
				"ShellExperienceHost",
				"PeopleExperienceHost",
				"LockApp",
				"StartMenuExperienceHost",
				"SearchApp",
				"SearchHost", // Windows 11 search
				"search", // Windows 11 RTM search
				"ScreenClippingHost",
				"Microsoft.CmdPal.UI" // VS ‘Command Palette’ floating window
			};

			if (ignoreProcesses.Contains(ProcessName))
				return false;

			return true;
		}

		public bool IsFocused => Win32.GetForegroundWindow() == _handle;
		public bool IsMinimized => Win32.IsIconic(_handle);
		public bool IsMaximized => Win32.IsZoomed(_handle);
		public bool IsMouseMoving { get; internal set; }

		public void Focus()
		{
			if (!IsFocused)
			{
				Win32Helper.ForceForegroundWindow(_handle);
				WindowFocused?.Invoke(this);
			}
		}

		public void Hide()
		{
			if (CanLayout)
			{
				_didManualHide = true;
			}
			Win32.ShowWindow(_handle, Win32.SW.SW_HIDE);
		}

		public void ShowNormal()
		{
			_didManualHide = false;
			Win32.ShowWindow(_handle, Win32.SW.SW_SHOWNOACTIVATE);
		}

		public void ShowMaximized()
		{
			_didManualHide = false;
			Win32.ShowWindow(_handle, Win32.SW.SW_SHOWMAXIMIZED);
		}

		public void ShowMinimized()
		{
			_didManualHide = false;
			Win32.ShowWindow(_handle, Win32.SW.SW_SHOWMINIMIZED);
		}

		public void ShowInCurrentState()
		{
			if (IsMinimized)
				ShowMinimized();
			else if (IsMaximized)
				ShowMaximized();
			else
				ShowNormal();

			WindowUpdated?.Invoke(this);
		}

		public void BringToTop()
		{
			Win32.BringWindowToTop(_handle);
			WindowUpdated?.Invoke(this);
		}

		public void Close()
		{
			Win32Helper.QuitApplication(_handle);
			WindowClosed?.Invoke(this);
		}

		public void NotifyUpdated()
		{
			WindowUpdated?.Invoke(this);
		}

		public override string ToString()
		{
			return $"[{Handle}][{Title}][{Class}][{ProcessName}]";
		}

		public Icon? ExtractIcon()
		{
			if (string.IsNullOrWhiteSpace(_processExecutable))
				return null;

			try
			{
				return Icon.ExtractAssociatedIcon(_processExecutable);
			}
			catch (IOException)
			{
				return null;
			}
		}



	}
}