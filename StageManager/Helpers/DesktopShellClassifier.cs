using StageManager.Native.PInvoke;
using System;
using System.Text;

namespace StageManager.Helpers
{
	/// <summary>
	/// Centralised classification of Windows shell desktop windows.
	/// Replaces hand-rolled <c>GetClassName</c> + string compares scattered through the codebase.
	/// </summary>
	public static class DesktopShellClassifier
	{
		public static string GetClassName(IntPtr hwnd)
		{
			var sb = new StringBuilder(256);
			Win32.GetClassName(hwnd, sb, sb.Capacity);
			return sb.ToString();
		}

		/// <summary>Desktop background container (WorkerW or Progman).</summary>
		public static bool IsDesktopBackground(IntPtr hwnd) =>
			IsDesktopBackgroundClass(GetClassName(hwnd));

		/// <summary>Desktop icon view host (SysListView32 or SHELLDLL_DefView).</summary>
		public static bool IsDesktopIconView(IntPtr hwnd) =>
			IsDesktopIconViewClass(GetClassName(hwnd));

		/// <summary>Either background or icon view.</summary>
		public static bool IsDesktopShell(IntPtr hwnd)
		{
			var cls = GetClassName(hwnd);
			return IsDesktopBackgroundClass(cls) || IsDesktopIconViewClass(cls);
		}

		private static bool IsDesktopBackgroundClass(string cls) =>
			string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase);

		private static bool IsDesktopIconViewClass(string cls) =>
			string.Equals(cls, "SysListView32", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(cls, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase);
	}
}
