using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StageManager.Services
{
	public static class SceneSnapshot
	{
		public record SceneEntry(string Key, long[] Handles);

		public record Snapshot(long[] ActiveSceneHandles, SceneEntry[] Scenes);

		private static readonly string SnapshotDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"StageManager", "updates");

		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = false,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		/// <summary>
		/// Serializes the scene layout to a JSON file. Returns the file path.
		/// </summary>
		public static string Save(Snapshot snapshot)
		{
			Directory.CreateDirectory(SnapshotDir);
			var path = Path.Combine(SnapshotDir, "scene-snapshot.json");
			var json = JsonSerializer.Serialize(snapshot, JsonOptions);
			File.WriteAllText(path, json);
			return path;
		}

		/// <summary>
		/// Loads a snapshot from the given path and deletes the file (consumed once).
		/// Returns null if the file doesn't exist or is corrupt.
		/// </summary>
		public static Snapshot? Load(string path)
		{
			try
			{
				if (!File.Exists(path))
					return null;

				var json = File.ReadAllText(path);
				var snapshot = JsonSerializer.Deserialize<Snapshot>(json, JsonOptions);

				File.Delete(path);
				return snapshot;
			}
			catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
			{
				return null;
			}
		}
	}
}
