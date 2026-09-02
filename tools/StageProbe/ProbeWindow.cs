using System.Drawing;
using System.Windows.Forms;

namespace StageProbe;

/// <summary>
/// Solid-colour borderless probe window — the whole surface is one flat colour
/// so the tray thumbnail becomes a measurable quad (same trick as the macOS
/// StageRun red probe). Borderless BUT WS_SYSMENU is kept: StageManager's
/// CanLayout filter only tracks windows exposing at least one chrome style bit.
/// </summary>
internal sealed class ProbeForm : Form
{
    private const int WS_SYSMENU = 0x00080000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    public ProbeForm(Color color, int x, int y, int w, int h, string title)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(x, y, w, h);
        BackColor = color;
        Text = title;
        ShowInTaskbar = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_SYSMENU;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Win11 rounds top-level corners by default; measured edges must stay straight.
        int pref = DWMWCP_DONOTROUND;
        _ = Native.DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }
}

internal static class ProbeWindow
{
    /// <summary>Physical-pixel gap between the windows of a multi-window probe.</summary>
    public const int MultiWindowGapPx = 40;

    public static void RunWindows(Args a)
    {
        var color = ColorTranslator.FromHtml("#" + a.Get("color", "FF0000"));
        int x = a.GetInt("x", 600), y = a.GetInt("y", 200);
        int w = a.GetInt("w", 900), h = a.GetInt("h", 600);
        int count = Math.Max(1, a.GetInt("count", 1));
        string title = a.Get("title", "probe");

        // Side by side, never stacked: overlapping windows of one colour make
        // the on-stage pixel area ambiguous, so the arrival check could not tell
        // "both windows rendered" from "one window rendered".
        var forms = new List<Form>();
        for (int i = 0; i < count; i++)
            forms.Add(new ProbeForm(color, x + i * (w + MultiWindowGapPx), y, w, h, count > 1 ? $"{title}-{i + 1}" : title));
        foreach (var f in forms) f.Show();
        Application.Run(new MultiFormContext(forms));
    }

    private sealed class MultiFormContext : ApplicationContext
    {
        // ANY form closing exits the process: the orchestrator tears a probe
        // down with one WM_CLOSE to the main window and expects the whole
        // process (all its windows) to leave so StageManager removes the scene.
        public MultiFormContext(IReadOnlyList<Form> forms)
        {
            foreach (var f in forms)
                f.FormClosed += (_, _) => ExitThread();
        }
    }
}
