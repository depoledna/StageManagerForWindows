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

			RestoreScenesPath = ParseRestoreScenesArg(e.Args);
			UpdateService.CleanupOldVersion();
			if (RestoreScenesPath is null)
				UpdateService.CleanupStagingFolder();

			Services.ThemeManager.ApplyTheme();
			Services.ThemeManager.StartListening();

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
