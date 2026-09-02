using AsyncAwaitBestPractices;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using StageManager.Services;

namespace StageManager
{
	public partial class App : Application
	{
		internal static string? RestoreScenesPath { get; private set; }

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			// Before anything that logs a frame number, so the count covers the whole run.
			FrameClock.Start();

			RestoreScenesPath = ParseRestoreScenesArg(e.Args);
			UpdateService.CleanupOldVersion();
			if (RestoreScenesPath is null)
				UpdateService.CleanupStagingFolder();

			Services.ThemeManager.ApplyTheme();
			Services.ThemeManager.StartListening();

			// Consent is per process and only affects sessions started after it lands, so
			// it goes out as early as possible. Not awaited: the tray tiles it misses are
			// parked off-screen, where the indicator it suppresses cannot be seen anyway.
			Composition.CaptureBorder.RequestAsync().SafeFireAndForget();

			// Log-only — intentionally NOT setting args.Handled so the app terminates
			DispatcherUnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"UI thread: {args.Exception}");
			};

			AppDomain.CurrentDomain.UnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unhandled: {args.ExceptionObject}");
			};

			TaskScheduler.UnobservedTaskException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unobserved task: {args.Exception}");
			};
		}

		protected override void OnExit(ExitEventArgs e)
		{
			Services.ThemeManager.StopListening();
			base.OnExit(e);
		}

		private static string? ParseRestoreScenesArg(string[] args)
		{
			var index = Array.IndexOf(args, "--restore-scenes");
			if (index < 0 || index + 1 >= args.Length)
				return null;

			var path = Path.GetFullPath(args[index + 1]);
			var expectedDir = Path.GetFullPath(UpdateService.StagingFolder);

			if (!path.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase))
				return null;

			return path;
		}
	}
}
