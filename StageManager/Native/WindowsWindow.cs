using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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

			// Before falling back to the exe: a UWP exe carries no icon resource, so
			// ExtractAssociatedIcon below happily returns Windows' GENERIC application icon
			// and reports success. The real artwork — the one the taskbar and Start draw —
			// is declared in the package manifest.
			var packageLogo = TryGetUwpPackageLogoIcon(exe);
			if (packageLogo != null) return packageLogo;

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
				var icon = LoadPngAsIcon(pngPath);
				Log.Info("ICON", $"  UWP override: loaded '{pngPath}' ({icon.Width}x{icon.Height})");
				return icon;
			}
			catch (Exception ex)
			{
				Log.Info("ICON", $"  UWP override FAILED: {ex.Message}");
				return null;
			}
		}

		#region UWP package logo
		// How far up from the inner exe to look for the manifest. The exe can sit a couple of
		// folders inside the package (…\<package>\Notepad\Notepad.exe); the bound stops a
		// non-packaged exe from walking to the drive root.
		private const int MaxPackageRootDepth = 5;

		/// <summary>
		/// The icon the taskbar draws for a packaged (UWP/MSIX) app: the Square44x44Logo
		/// declared in its AppxManifest.
		/// <para>
		/// Needed because a UWP app's exe has no icon resource of its own, so
		/// <see cref="Icon.ExtractAssociatedIcon"/> returns Windows' generic application icon
		/// and reports success — indistinguishable, to the caller, from having found the real
		/// thing. That is why Calculator showed a blank document glyph while its taskbar button
		/// was correct.
		/// </para>
		/// <para>
		/// Two shorter-looking routes do not work here. <c>AppDisplayInfo.GetLogo</c> returns
		/// the TILE logo — for Calculator a glyph filling 31% of a large transparent square,
		/// against 75% for the taskbar asset, so the icon would render at under half the size of
		/// every other app's. <c>IShellItemImageFactory</c> on shell:AppsFolder does give the
		/// right artwork, but hands back an HBITMAP whose alpha channel Image.FromHbitmap
		/// discards, so recovering it needs GetObject/DIBSECTION plus an AUMID lookup — more
		/// interop than this, not less.
		/// </para>
		/// </summary>
		private static Icon? TryGetUwpPackageLogoIcon(string? exe)
		{
			if (string.IsNullOrWhiteSpace(exe)) return null;

			try
			{
				var packageRoot = FindPackageRoot(Path.GetDirectoryName(exe));
				if (packageRoot is null)
				{
					Log.Info("ICON", $"  UWP manifest: no AppxManifest.xml above '{exe}'");
					return null;
				}

				var declared = ReadSquare44LogoPath(Path.Combine(packageRoot, "AppxManifest.xml"));
				if (declared is null)
				{
					Log.Info("ICON", $"  UWP manifest: no Square44x44Logo in '{packageRoot}'");
					return null;
				}

				var asset = ResolveBestAssetVariant(Path.Combine(packageRoot, declared));
				if (asset is null)
				{
					Log.Info("ICON", $"  UWP manifest: no asset on disk for '{declared}'");
					return null;
				}

				var icon = LoadPngAsIcon(asset);
				Log.Info("ICON", $"  UWP manifest: loaded '{asset}' ({icon.Width}x{icon.Height})");
				return icon;
			}
			catch (Exception ex)
			{
				Log.Info("ICON", $"  UWP manifest FAILED: {ex.GetType().Name}: {ex.Message}");
				return null;
			}
		}

		private static string? FindPackageRoot(string? startDirectory)
		{
			var dir = startDirectory;
			for (var i = 0; i < MaxPackageRootDepth && !string.IsNullOrEmpty(dir); i++)
			{
				if (File.Exists(Path.Combine(dir, "AppxManifest.xml")))
					return dir;
				dir = Path.GetDirectoryName(dir);
			}
			return null;
		}

		/// <summary>
		/// Matched on local names: VisualElements lives in a uap namespace whose version differs
		/// between manifests, so binding to one URI would work for some packages and silently
		/// miss others.
		/// </summary>
		private static string? ReadSquare44LogoPath(string manifestPath)
		{
			var visual = XDocument.Load(manifestPath).Descendants()
				.FirstOrDefault(e => e.Name.LocalName == "VisualElements");
			var logo = visual?.Attributes()
				.FirstOrDefault(a => a.Name.LocalName == "Square44x44Logo")?.Value;
			return string.IsNullOrWhiteSpace(logo) ? null : logo;
		}

		/// <summary>
		/// The manifest names a LOGICAL asset — Calculator declares Assets\CalculatorAppList.png
		/// and no such file exists. What ships are qualifier variants of it, so the declared path
		/// has to be treated as a stem.
		/// </summary>
		private static string? ResolveBestAssetVariant(string declaredPath)
		{
			var dir = Path.GetDirectoryName(declaredPath);
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

			var stem = Path.GetFileNameWithoutExtension(declaredPath);
			var ext = Path.GetExtension(declaredPath);

			// Unplated first: the bare glyph on transparency, which is what the taskbar draws.
			// Plated variants bake in a solid accent-coloured square that would sit on the
			// sidebar as a coloured tile.
			return LargestVariant(dir, $"{stem}.targetsize-*_altform-unplated{ext}")
				?? LargestVariant(dir, $"{stem}.targetsize-*{ext}")
				?? LargestVariant(dir, $"{stem}.scale-*{ext}")
				?? (File.Exists(declaredPath) ? declaredPath : null);
		}

		private static string? LargestVariant(string directory, string pattern)
			=> Directory.EnumerateFiles(directory, pattern)
				// contrast-black / contrast-white are flat accessibility shapes.
				.Where(p => !p.Contains("contrast-", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(QualifierNumber)
				.FirstOrDefault();

		// targetsize-256_altform-unplated → 256, scale-200 → 200. Only ever compared against
		// numbers from the SAME pattern, so absolute pixels and percentages never mix.
		private static int QualifierNumber(string path)
		{
			var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"-(\d+)");
			return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
		}

		private static Icon LoadPngAsIcon(string pngPath)
		{
			using var bitmap = new Bitmap(pngPath);
			return BitmapToIcon(bitmap);
		}

		private static Icon BitmapToIcon(Bitmap bitmap)
		{
			var hicon = bitmap.GetHicon();
			try
			{
				using var borrowed = Icon.FromHandle(hicon);
				return (Icon)borrowed.Clone();
			}
			finally { DestroyIcon(hicon); }
		}
		#endregion

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