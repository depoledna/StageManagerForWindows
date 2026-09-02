using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StageProbe;

internal sealed record EdgeFit(double AngleDeg, double ResidualPx, int Columns);

internal sealed record QuadResult(
    string Label,
    int PixelCount,
    double LeftXPx,
    double RightXPx,
    double TopMidYPx,
    double BottomMidYPx,
    EdgeFit Top,
    EdgeFit Bottom);

/// <summary>
/// Finds the solid-colour card for one probe colour in a strip screenshot and
/// fits its top/bottom edges, sub-pixel, macOS StageRun style. The sidebar is
/// translucent, so the wallpaper bleeds through and can colour-match anywhere;
/// global column extremes are useless. Instead: per column keep the longest
/// contiguous matching run of plausible card height, then take the longest
/// x-interval whose runs are column-to-column coherent (straight edges) — only
/// the card looks like that. Ends trimmed (corner radius + AA), least-squares
/// line through the rest. Positive angle = right end rises (screen y down),
/// matching CompositionThumbnail's edge-angle DPs.
/// </summary>
internal static class QuadFit
{
    private const int MinRunPx = 40;      // shortest plausible card column
    private const int MaxRunPx = 900;     // tallest plausible card column
    private const int MinColumns = 60;    // narrowest plausible card
    private const int CoherenceMaxStepPx = 4;
    private const double TrimFraction = 0.14;

    private readonly record struct ColumnRun(int Top, int Bottom)
    {
        public int Length => Bottom - Top + 1;
    }

    public static QuadResult? Fit(Bitmap bmp, string label, Color target)
    {
        var runs = BestRunPerColumn(bmp, target);
        var (start, end) = LongestCoherentInterval(runs);
        if (end - start + 1 < MinColumns) return null;

        int trim = (int)((end - start) * TrimFraction);
        var xs = Enumerable.Range(start + trim, end - start + 1 - 2 * trim)
            .Where(x => runs[x].HasValue)
            .ToArray();
        if (xs.Length < MinColumns / 2) return null;

        var top = FitLine(xs, x => runs[x]!.Value.Top);
        var bottom = FitLine(xs, x => runs[x]!.Value.Bottom);

        // The app-icon badge notches the card's lower-left corner and breaks
        // run coherence there, truncating the interval. The icon only eats the
        // BOTTOM of a column, so extend the span outward wherever the column's
        // top still lies on the fitted top edge — that recovers the card's true
        // width without letting wallpaper noise back in.
        int left = start, right = end;
        while (left - 1 >= 0 && runs[left - 1] is { } lr
            && Math.Abs(lr.Top - (top.A + top.B * (left - 1))) <= CoherenceMaxStepPx)
            left--;
        while (right + 1 < runs.Length && runs[right + 1] is { } rr
            && Math.Abs(rr.Top - (top.A + top.B * (right + 1))) <= CoherenceMaxStepPx)
            right++;

        double midX = (left + right) / 2.0;
        int pixels = 0;
        for (int x = left; x <= right; x++)
            if (runs[x] is { } r)
                pixels += r.Length;

        return new QuadResult(
            label, pixels, left, right,
            top.A + top.B * midX, bottom.A + bottom.B * midX,
            new EdgeFit(-Math.Atan(top.B) * 180.0 / Math.PI, top.Rms, xs.Length),
            new EdgeFit(-Math.Atan(bottom.B) * 180.0 / Math.PI, bottom.Rms, xs.Length));
    }

    private static ColumnRun?[] BestRunPerColumn(Bitmap bmp, Color target)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);

        var runs = new ColumnRun?[bmp.Width];
        for (int x = 0; x < bmp.Width; x++)
        {
            ColumnRun? best = null;
            int runStart = -1;
            for (int y = 0; y <= bmp.Height; y++)
            {
                bool match = y < bmp.Height && MatchPixel(pixels, y * data.Stride + x * 4, target);
                if (match && runStart < 0) runStart = y;
                if (!match && runStart >= 0)
                {
                    int len = y - runStart;
                    if (len is >= MinRunPx and <= MaxRunPx && len > (best?.Length ?? 0))
                        best = new ColumnRun(runStart, y - 1);
                    runStart = -1;
                }
            }
            runs[x] = best;
        }
        return runs;
    }

    private static (int Start, int End) LongestCoherentInterval(ColumnRun?[] runs)
    {
        int bestStart = 0, bestEnd = -1, start = -1;
        for (int x = 0; x < runs.Length; x++)
        {
            bool coherent = runs[x] is { } r
                && (start < 0 || x == start
                    || (runs[x - 1] is { } p
                        && Math.Abs(r.Top - p.Top) <= CoherenceMaxStepPx
                        && Math.Abs(r.Bottom - p.Bottom) <= CoherenceMaxStepPx));
            if (runs[x].HasValue && (start < 0 || coherent))
            {
                if (start < 0) start = x;
                if (x - start > bestEnd - bestStart) { bestStart = start; bestEnd = x; }
            }
            else
            {
                start = runs[x].HasValue ? x : -1;
            }
        }
        return (bestStart, bestEnd);
    }

    // BGRA. Threshold survives the tray's 0.8 opacity blend over both dark and
    // light backdrops: 255 -> >=204 + 0.2*bg, 0 -> <=0.2*bg = 51 worst case.
    // The dark bound is deliberately tight (70, not ~110): a deep-blue-sky
    // wallpaper (r~40 g~90 b~180) otherwise matches pure blue and out-scores
    // the real card in the coherence sweep.
    private static bool MatchPixel(byte[] px, int i, Color t)
        => Match(px[i + 2], t.R) && Match(px[i + 1], t.G) && Match(px[i], t.B);

    private static bool Match(byte c, byte t) => t > 127 ? c > 150 : c < 70;

    /// <summary>
    /// Count pixels matching a probe colour. Used on stage-band grabs to see
    /// how much of a scene's windows have actually been painted.
    /// </summary>
    public static int CountMatching(Bitmap bmp, Color target)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);

        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (MatchPixel(px, y * data.Stride + x * 4, target))
                    n++;
        return n;
    }

    private static (double A, double B, double Rms) FitLine(int[] xs, Func<int, int> yOf)
    {
        double n = xs.Length, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var x in xs)
        {
            double y = yOf(x);
            sx += x; sy += y; sxx += (double)x * x; sxy += x * y;
        }
        double denom = n * sxx - sx * sx;
        double b = denom == 0 ? 0 : (n * sxy - sx * sy) / denom;
        double a = (sy - b * sx) / n;

        double ss = 0;
        foreach (var x in xs)
        {
            double e = yOf(x) - (a + b * x);
            ss += e * e;
        }
        return (a, b, Math.Sqrt(ss / n));
    }
}
