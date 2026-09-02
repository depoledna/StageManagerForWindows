using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace StageProbe;

internal static class StripCapture
{
    /// <summary>
    /// Screenshot of the left strip of the primary monitor, cropped to the
    /// WORK AREA height so taskbar pixels can't fake a card (colored taskbar
    /// content once out-scored a real card in the coherence sweep). All
    /// physical pixels — the process is per-monitor-v2 DPI aware.
    /// </summary>
    public static Bitmap Grab(int widthPx)
    {
        var b = Screen.PrimaryScreen!.Bounds;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        int w = Math.Min(widthPx, b.Width);
        int h = wa.Bottom - b.Y;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.X, b.Y, 0, 0, new Size(w, h));
        return bmp;
    }

    /// <summary>
    /// A short horizontal band of the STAGE area (everything right of the
    /// sidebar), used to poll how fast a scene's windows actually appear.
    /// A band rather than the full screen: one grab is ~200 KB instead of
    /// ~30 MB, so polling every 50 ms doesn't perturb what it measures.
    /// </summary>
    public static Bitmap GrabStageBand(int stageLeftPx, int yPx, int heightPx)
    {
        var b = Screen.PrimaryScreen!.Bounds;
        int x = Math.Clamp(stageLeftPx, 0, b.Width - 1);
        int y = Math.Clamp(yPx, 0, b.Height - 1);
        int h = Math.Min(heightPx, b.Height - y);
        int w = b.Width - x;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.X + x, b.Y + y, 0, 0, new Size(w, h));
        return bmp;
    }
}
