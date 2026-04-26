using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace StageManager.Services
{
	public record UpdateInfo(string TagName, Version Version, string DownloadUrl, long Size);

	public sealed class UpdateService : IDisposable
	{
		private const string ReleasesUrl =
			"https://api.github.com/repos/depoledna/StageManagerForWindows/releases/latest";

		internal static readonly string StagingFolder = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"StageManager", "updates");

		private readonly HttpClient _httpClient;

		public UpdateService()
		{
			_httpClient = new HttpClient();
			_httpClient.DefaultRequestHeaders.UserAgent.Add(
				new ProductInfoHeaderValue("StageManager-UpdateCheck", "1.0"));
			_httpClient.DefaultRequestHeaders.Accept.Add(
				new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		}

		/// <summary>
		/// Checks the GitHub Releases API for a newer version.
		/// Returns UpdateInfo if an update is available, null if up-to-date.
		/// </summary>
		public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
		{
			var response = await _httpClient.GetAsync(ReleasesUrl, ct).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();

			using var doc = await JsonDocument.ParseAsync(
				await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
				.ConfigureAwait(false);

			var root = doc.RootElement;
			var tagName = root.GetProperty("tag_name").GetString() ?? "";
			var remoteVersion = ParseVersion(tagName);
			var currentVersion = GetCurrentVersion();

			if (remoteVersion <= currentVersion)
				return null;

			var archSuffix = GetCurrentArchitectureSuffix();
			var assetName = $"StageManager-{archSuffix}.exe";

			var assets = root.GetProperty("assets");
			foreach (var asset in assets.EnumerateArray())
			{
				var name = asset.GetProperty("name").GetString() ?? "";
				if (name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
				{
					var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
					ValidateDownloadUrl(downloadUrl);
					var size = asset.GetProperty("size").GetInt64();
					return new UpdateInfo(tagName, remoteVersion, downloadUrl, size);
				}
			}

			return null;
		}

		/// <summary>
		/// Downloads the update exe to the staging folder. Returns the downloaded file path.
		/// Validates file size against the expected size from the GitHub API.
		/// </summary>
		public async Task<string> DownloadUpdateAsync(
			UpdateInfo update,
			IProgress<double>? progress = null,
			CancellationToken ct = default)
		{
			Directory.CreateDirectory(StagingFolder);

			var archSuffix = GetCurrentArchitectureSuffix();
			var filePath = Path.Combine(StagingFolder, $"StageManager-{archSuffix}.exe");

			using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
				.ConfigureAwait(false);
			response.EnsureSuccessStatusCode();

			await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);

			var buffer = new byte[81920];
			long totalRead = 0;
			int bytesRead;
			while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
			{
				await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
				totalRead += bytesRead;
				if (update.Size > 0)
					progress?.Report((double)totalRead / update.Size);
			}

			var actualSize = new FileInfo(filePath).Length;
			if (actualSize != update.Size)
			{
				File.Delete(filePath);
				throw new InvalidOperationException(
					$"Downloaded file size {actualSize} does not match expected {update.Size}. Download may be corrupt.");
			}

			return filePath;
		}

		/// <summary>
		/// Stages the new binary, then atomically swaps: current → .bak, new → current.
		/// Rolls back on failure.
		/// </summary>
		public static void ApplyUpdate(string downloadedExePath)
		{
			var currentExePath = Environment.ProcessPath
				?? throw new InvalidOperationException("Cannot determine current exe path.");
			var backupPath = currentExePath + ".bak";
			var tempPath = currentExePath + ".new";

			// Stage next to the current exe first (ensures the new binary is fully on disk)
			File.Copy(downloadedExePath, tempPath, overwrite: true);

			if (File.Exists(backupPath))
				File.Delete(backupPath);

			File.Move(currentExePath, backupPath);
			try
			{
				File.Move(tempPath, currentExePath);
			}
			catch
			{
				File.Move(backupPath, currentExePath);
				throw;
			}
		}

		/// <summary>
		/// Launches the new exe with --restore-scenes and shuts down the current process.
		/// </summary>
		public static void LaunchAndExit(string? snapshotPath)
		{
			var exePath = Environment.ProcessPath
				?? throw new InvalidOperationException("Cannot determine current exe path.");

			var psi = new ProcessStartInfo { FileName = exePath, UseShellExecute = false };
			if (snapshotPath != null)
			{
				psi.ArgumentList.Add("--restore-scenes");
				psi.ArgumentList.Add(snapshotPath);
			}

			Process.Start(psi);
			Application.Current.Shutdown();
		}

		/// <summary>
		/// Deletes the .bak and .new files next to the running exe (safe to call on every startup).
		/// </summary>
		public static void CleanupOldVersion()
		{
			try
			{
				var exePath = Environment.ProcessPath;
				if (exePath is null) return;

				var backupPath = exePath + ".bak";
				if (File.Exists(backupPath))
					File.Delete(backupPath);

				var tempPath = exePath + ".new";
				if (File.Exists(tempPath))
					File.Delete(tempPath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort cleanup
			}
		}

		/// <summary>
		/// Deletes the staging folder and its contents.
		/// </summary>
		public static void CleanupStagingFolder()
		{
			try
			{
				if (Directory.Exists(StagingFolder))
					Directory.Delete(StagingFolder, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort cleanup
			}
		}

		private static void ValidateDownloadUrl(string url)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
				throw new InvalidOperationException($"Invalid download URL: {url}");

			if (uri.Scheme != "https")
				throw new InvalidOperationException($"Download URL must use HTTPS: {url}");

			if (!uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) &&
				!uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
				!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException($"Untrusted download host: {uri.Host}");
			}
		}

		internal static Version GetCurrentVersion()
		{
			var infoVersion = Assembly.GetExecutingAssembly()
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

			// InformationalVersion may have "+commitsha" suffix
			var plusIndex = infoVersion.IndexOf('+');
			if (plusIndex >= 0)
				infoVersion = infoVersion[..plusIndex];

			return ParseVersion(infoVersion);
		}

		private static Version ParseVersion(string tagOrVersion)
		{
			var cleaned = tagOrVersion.TrimStart('v', 'V');
			if (Version.TryParse(cleaned, out var version))
				return version;
			return new Version(0, 0, 0);
		}

		private static string GetCurrentArchitectureSuffix()
		{
			return RuntimeInformation.ProcessArchitecture switch
			{
				Architecture.Arm64 => "arm64",
				_ => "x64"
			};
		}

		public void Dispose()
		{
			_httpClient.Dispose();
		}
	}
}
