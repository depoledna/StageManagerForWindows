namespace StageProbe;

/// <summary>Minimal --key value parser; first element (the mode) is skipped.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (int i = 1; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            string key = argv[i][2..];
            string val = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "true";
            a._map[key] = val;
        }
        return a;
    }

    public string Get(string key, string fallback) => _map.TryGetValue(key, out var v) ? v : fallback;
    public int GetInt(string key, int fallback) => _map.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
    public bool Has(string key) => _map.ContainsKey(key);
}
