using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace StageManager
{
	/// <summary>
	/// Lightweight debug logger. Output goes to <see cref="Debug.WriteLine"/> and,
	/// in DEBUG builds, to a log file next to the executable.
	/// All calls are compiled away in Release builds automatically.
	/// </summary>
	internal static class Log
	{
#if DEBUG
		private static readonly string LogPath = Path.Combine(
			AppContext.BaseDirectory, "stagemanager.log");

		private const long MaxLogSize = 1024 * 1024; // 1 MB

		static Log()
		{
			try
			{
				// Rotate: if the log file exceeds MaxLogSize, keep only the last half
				if (File.Exists(LogPath))
				{
					var fileInfo = new FileInfo(LogPath);
					if (fileInfo.Length > MaxLogSize)
					{
						var lines = File.ReadAllLines(LogPath);
						var keepFrom = lines.Length / 2;
						File.WriteAllLines(LogPath, lines[keepFrom..]);
					}
				}

				Trace.Listeners.Add(new TextWriterTraceListener(LogPath));
				Trace.AutoFlush = true;
				Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LOG] Logging to {LogPath}");
			}
			catch { /* best-effort */ }
		}
#endif

		/// <summary>
		/// Logs a user-initiated action with a visual separator for easy scanning.
		/// </summary>
		[Conditional("DEBUG")]
		public static void Action(string description)
		{
			Debug.WriteLine("");
			Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ──── {description}");
		}

		[Conditional("DEBUG")]
		public static void Info(string tag, string message)
		{
			Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}");
		}

		[Conditional("DEBUG")]
		public static void Info(string tag, string message, IntPtr handle)
		{
			Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message} (0x{handle:X})");
		}

		[Conditional("DEBUG")]
		public static void Window(string tag, string action, Native.Window.IWindow window)
		{
			Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {action}: '{window.Title}' Handle=0x{window.Handle:X} Process='{window.ProcessFileName}' Minimized={window.IsMinimized} Focused={window.IsFocused}");
		}

		[Conditional("DEBUG")]
		public static void Scene(string action, Scene scene)
		{
			Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SCENE] {action}: '{scene?.Title ?? "(null)"}' Id={scene?.Id} Windows={scene?.Windows.Count() ?? 0}");
		}

		[Conditional("DEBUG")]
		public static void Scene(string action, Scene scene, Native.Window.IWindow window)
		{
			Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SCENE] {action}: '{scene?.Title ?? "(null)"}' Id={scene?.Id} Window='{window?.Title}' Handle=0x{window?.Handle:X}");
		}
	}
}
