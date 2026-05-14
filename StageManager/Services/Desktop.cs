using Microsoft.Win32;
using StageManager.Native.PInvoke;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace StageManager.Services
{
	internal class Desktop
	{
		[DllImport("user32.dll", SetLastError = true)]
		static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

		[DllImport("user32.dll", SetLastError = false)]
		static extern IntPtr GetDesktopWindow();

		private const int WM_COMMAND = 0x111;

		public bool GetDesktopIconsVisible()
		{
			using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: false);
			if (key?.GetValue("HideIcons", 0) is int hideIconsValue)
				return hideIconsValue == 0;

			return false;
		}

		private void ToggleDesktopIcons()
		{
			var shellView = GetDesktopSHELLDLL_DefView();
			Log.Info("DESKTOP", $"ToggleDesktopIcons: SHELLDLL_DefView handle=0x{shellView:X}");
			if (shellView == IntPtr.Zero)
			{
				Log.Info("DESKTOP", "ToggleDesktopIcons: SHELLDLL_DefView not found, toggle skipped");
				return;
			}
			var toggleDesktopCommand = new IntPtr(0x7402);
			SendMessage(shellView, WM_COMMAND, toggleDesktopCommand, IntPtr.Zero);
		}

		/// <summary>
		/// Ensures desktop icons are toggled ON (shell-level).
		/// Call at startup for crash recovery.
		/// </summary>
		public void EnsureIconsExist()
		{
			if (!GetDesktopIconsVisible())
			{
				Log.Info("DESKTOP", "EnsureIconsExist: icons were toggled off, restoring");
				ToggleDesktopIcons();
			}
		}

		public void ShowIcons()
		{
			if (!GetDesktopIconsVisible())
			{
				ToggleDesktopIcons();
				Log.Info("DESKTOP", "ShowIcons: toggled on");
			}
		}

		public void HideIcons(bool animate = true)
		{
			if (GetDesktopIconsVisible())
			{
				ToggleDesktopIcons();
				Log.Info("DESKTOP", "HideIcons: toggled off");
			}
		}

		/// <summary>
		/// Restore icons visibility. Call on app shutdown.
		/// </summary>
		public void RestoreIcons()
		{
			ShowIcons();
		}

		static IntPtr GetDesktopSHELLDLL_DefView()
		{
			var hShellViewWin = IntPtr.Zero;
			var hWorkerW = IntPtr.Zero;

			var hProgman = FindWindow("Progman", "Program Manager");
			var hDesktopWnd = GetDesktopWindow();

			if (hProgman != IntPtr.Zero)
			{
				hShellViewWin = FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);

				if (hShellViewWin == IntPtr.Zero)
				{
					// Fallback: when Progman doesn't host DefView (e.g. wallpaper rotation, toggledesktop),
					// scan WorkerW windows instead
					do
					{
						hWorkerW = FindWindowEx(hDesktopWnd, hWorkerW, "WorkerW", null);
						hShellViewWin = FindWindowEx(hWorkerW, IntPtr.Zero, "SHELLDLL_DefView", null);
					} while (hShellViewWin == IntPtr.Zero && hWorkerW != IntPtr.Zero);
				}
			}
			return hShellViewWin;
		}
	}
}
