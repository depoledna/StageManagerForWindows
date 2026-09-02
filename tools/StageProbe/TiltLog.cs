using System.Globalization;
using System.Text.RegularExpressions;

namespace StageProbe;

internal sealed record TiltEntry(string Scene, double YTop, double YBottom, double TopDeg, double BottomDeg);

/// <summary>
/// Reads the TILT lines MainWindow.AssignRowTiltsCore writes to
/// stagemanager.log (invariant culture) and keeps the LAST entry per scene.
/// The log stays open in the app, hence FileShare.ReadWrite.
/// </summary>
internal static class TiltLog
{
    private static readonly Regex Rx = new(
        @"TILT.*scene='(?<s>[^']+)'\s+yTop=(?<yt>-?[\d.]+)\s+yBottom=(?<yb>-?[\d.]+).*\btop=(?<t>-?[\d.]+)\s+bottom=(?<b>-?[\d.]+)",
        RegexOptions.Compiled);

    public static Dictionary<string, TiltEntry> ReadLatest(string path)
    {
        var result = new Dictionary<string, TiltEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var m = Rx.Match(line);
            if (!m.Success) continue;
            result[m.Groups["s"].Value] = new TiltEntry(
                m.Groups["s"].Value,
                Parse(m.Groups["yt"].Value),
                Parse(m.Groups["yb"].Value),
                Parse(m.Groups["t"].Value),
                Parse(m.Groups["b"].Value));
        }
        return result;
    }

    /// <summary>Latest entry whose scene name contains the probe name (matches both title and process naming).</summary>
    public static TiltEntry? For(Dictionary<string, TiltEntry> entries, string probeName)
        => entries.Values.FirstOrDefault(e => e.Scene.Contains(probeName, StringComparison.OrdinalIgnoreCase));

    private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
