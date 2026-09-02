using System.Windows.Forms;

namespace StageProbe;

/// <summary>
/// Windows twin of the macOS StageRun probe: verifies StageManager's tray card
/// edge angles end-to-end. Two modes:
///   stageprobe window --color RRGGBB [--x --y --w --h --count --title]
///       shows solid-colour borderless probe window(s); one renamed copy of
///       this exe per probe gives each its own process name and therefore its
///       own StageManager scene.
///   stageprobe run --log &lt;stagemanager.log&gt; [--out &lt;dir&gt;] [--switches a,b,..] [--keep]
///       spawns 6 probes (one with 2 windows), drives scene switches by focus,
///       screenshots the strip after each, fits card edges sub-pixel and checks
///       them against the app's TILT log and the macOS position law.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Native.AttachConsole(-1);
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: all pixels physical

        var mode = args.Length > 0 ? args[0] : "";
        try
        {
            switch (mode)
            {
                case "window":
                    ProbeWindow.RunWindows(Args.Parse(args));
                    return 0;
                case "run":
                    return Orchestrator.Run(Args.Parse(args));
                default:
                    Console.Error.WriteLine("usage: stageprobe window --color RRGGBB [--x --y --w --h --count --title]");
                    Console.Error.WriteLine("       stageprobe run --log <stagemanager.log> [--out <dir>] [--switches a,b,c] [--keep]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"stageprobe failed: {ex}");
            return 1;
        }
    }
}
