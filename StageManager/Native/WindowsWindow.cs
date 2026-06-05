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
			var title = Title;
			var cls = Class;
			Log.Info("ICON", $"ExtractIcon start: '{title}' class='{cls}' hwnd=0x{_handle:X64} exe='{_processExecutable}'");

			var uwpIcon = TryGetUwpInnerIcon(_handle);
			if (uwpIcon != null)
			{
				Log.Info("ICON", $"  ← UWP inner path returned icon ({uwpIcon.Width}x{uwpIcon.Height}) for '{title}'");
				return uwpIcon;
			}

			var windowIcon = TryGetWindowIcon(_handle);
			if (windowIcon != null)
			{
				Log.Info("ICON", $"  ← WM_GETICON/class path returned icon ({windowIcon.Width}x{windowIcon.Height}) for '{title}'");
				return windowIcon;
			}

			if (string.IsNullOrWhiteSpace(_processExecutable))
			{
				Log.Info("ICON", $"  ← NULL (no process exe) for '{title}'");
				return null;
			}

			try
			{
				var icon = Icon.ExtractAssociatedIcon(_processExecutable);
				Log.Info("ICON", $"  ← ExtractAssociatedIcon('{_processExecutable}') = {(icon != null ? $"{icon.Width}x{icon.Height}" : "null")} for '{title}'");
				return icon;
			}
			catch (IOException ex)
			{
				Log.Info("ICON", $"  ← ExtractAssociatedIcon FAILED: {ex.Message} for '{title}'");
				return null;
			}
		}

		private const uint WM_GETICON = 0x007F;
		private const int ICON_SMALL = 0;
		private const int ICON_BIG = 1;
		private const int ICON_SMALL2 = 2;
		private const int GCLP_HICON = -14;
		private const int GCLP_HICONSM = -34;
		private const uint SMTO_ABORTIFHUNG = 0x0002;

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
			uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

		[DllImport("user32.dll", EntryPoint = "GetClassLongPtr", CharSet = CharSet.Auto)]
		private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll", EntryPoint = "GetClassLong", CharSet = CharSet.Auto)]
		private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

		private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
			=> IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr(unchecked((int)GetClassLong32(hWnd, nIndex)));

		private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

		private static IntPtr FindChildByClass(IntPtr parent, string className)
		{
			IntPtr found = IntPtr.Zero;
			EnumChildWindows(parent, (h, _) =>
			{
				var buf = new StringBuilder(256);
				Win32.GetClassName(h, buf, buf.Capacity + 1);
				if (buf.ToString() == className)
				{
					found = h;
					return false;
				}
				return true;
			}, IntPtr.Zero);
			return found;
		}

		private static Icon? TryGetUwpInnerIcon(IntPtr hwnd)
		{
			if (hwnd == IntPtr.Zero) return null;

			var classBuf = new StringBuilder(256);
			Win32.GetClassName(hwnd, classBuf, classBuf.Capacity + 1);
			var cls = classBuf.ToString();
			if (cls != "ApplicationFrameWindow")
			{
				Log.Info("ICON", $"  UWP unwrap skipped — class='{cls}' is not ApplicationFrameWindow");
				return null;
			}

			var core = FindChildByClass(hwnd, "Windows.UI.Core.CoreWindow");
			if (core == IntPtr.Zero)
			{
				Log.Info("ICON", "  UWP unwrap: no CoreWindow child found");
				return null;
			}
			Log.Info("ICON", $"  UWP unwrap: CoreWindow=0x{core.ToInt64():X}");

			Win32.GetWindowThreadProcessId(core, out var pid);
			string? exe = null;
			if (pid != 0)
			{
				try
				{
					using var proc = Process.GetProcessById((int)pid);
					exe = proc.MainModule?.FileName;
				}
				catch (Exception ex)
				{
					Log.Info("ICON", $"  UWP unwrap: pid lookup failed: {ex.GetType().Name}: {ex.Message}");
				}
			}
			Log.Info("ICON", $"  UWP unwrap: pid={pid} exe='{exe}'");

			var override_ = TryGetKnownUwpOverrideIcon(exe);
			if (override_ != null) return override_;

			if (!string.IsNullOrWhiteSpace(exe))
			{
				try
				{
					var exeIcon = Icon.ExtractAssociatedIcon(exe);
					Log.Info("ICON", $"  UWP unwrap: ExtractAssociatedIcon('{exe}') = {(exeIcon != null ? $"{exeIcon.Width}x{exeIcon.Height}" : "null")}");
					if (exeIcon != null) return exeIcon;
				}
				catch (Exception ex)
				{
					Log.Info("ICON", $"  UWP unwrap: exe extract failed: {ex.Message}");
				}
			}

			var coreIcon = TryGetWindowIcon(core);
			if (coreIcon != null)
			{
				Log.Info("ICON", $"  UWP unwrap: CoreWindow WM_GETICON fallback returned icon ({coreIcon.Width}x{coreIcon.Height})");
				return coreIcon;
			}

			Log.Info("ICON", "  UWP unwrap: nothing worked, returning null");
			return null;
		}

		[DllImport("user32.dll")]
		private static extern bool DestroyIcon(IntPtr hIcon);

		// Known UWP apps where exe-extracted icon has wrong background/style — load the
		// package's transparent PNG asset directly. Keyed by inner exe filename.
		private static readonly Dictionary<string, string> UwpIconOverrides = new(StringComparer.OrdinalIgnoreCase)
		{
			["SystemSettings.exe"] = @"C:\Windows\ImmersiveControlPanel\Images\logo.targetsize-256_altform-unplated.png",
		};

		private static Icon? TryGetKnownUwpOverrideIcon(string? exe)
		{
			if (string.IsNullOrWhiteSpace(exe)) return null;
			var name = Path.GetFileName(exe);
			if (!UwpIconOverrides.TryGetValue(name, out var pngPath)) return null;
			if (!File.Exists(pngPath))
			{
				Log.Info("ICON", $"  UWP override: PNG missing at '{pngPath}'");
				return null;
			}

			try
			{
				using var bmp = new Bitmap(pngPath);
				var hicon = bmp.GetHicon();
				try
				{
					using var borrowed = Icon.FromHandle(hicon);
					var icon = (Icon)borrowed.Clone();
					Log.Info("ICON", $"  UWP override: loaded '{pngPath}' ({icon.Width}x{icon.Height})");
					return icon;
				}
				finally { DestroyIcon(hicon); }
			}
			catch (Exception ex)
			{
				Log.Info("ICON", $"  UWP override FAILED: {ex.Message}");
				return null;
			}
		}

		private static Icon? TryGetWindowIcon(IntPtr hwnd)
		{
			if (hwnd == IntPtr.Zero) return null;

			IntPtr hIcon = IntPtr.Zero;
			int[] iconTypes = { ICON_BIG, ICON_SMALL2, ICON_SMALL };
			foreach (var t in iconTypes)
			{
				if (SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(t), IntPtr.Zero,
					SMTO_ABORTIFHUNG, 100, out var result) != IntPtr.Zero && result != IntPtr.Zero)
				{
					hIcon = result;
					break;
				}
			}

			if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GCLP_HICON);
			if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GCLP_HICONSM);
			if (hIcon == IntPtr.Zero) return null;

			try
			{
				using var borrowed = Icon.FromHandle(hIcon);
				return (Icon)borrowed.Clone();
			}
			catch
			{
				return null;
			}
		}
	}
}