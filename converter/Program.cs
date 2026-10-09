// Reach Survival converter (GPL-3.0).
// Reads the player's own Halo: MCC (Reach) and Rust installs and writes a cache the game loads.
// Nothing from either game ships with the mashup.
//
// usage: ReachSurvival.Convert --mcc <MCC folder> --rust <Rust folder> --out <cache folder> [--only halo|rust] [--force 1]
using System.Text.Json;
using ReachSurvival.Convert;

var opts = new Dictionary<string, string>();
for (int i = 0; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
if (!opts.TryGetValue("out", out var outDir)) { Console.Error.WriteLine("missing --out"); return 2; }
Directory.CreateDirectory(outDir);
var only = opts.GetValueOrDefault("only", "");

var manifestPath = Path.Combine(outDir, "manifest.json");
var manifest = File.Exists(manifestPath)
    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))
    : new Dictionary<string, string>();

int rc = 0;
if (only is "" or "halo")
{
    if (!opts.TryGetValue("mcc", out var mcc)) { Console.Error.WriteLine("missing --mcc"); return 2; }
    var stamp = Stamp.Of(Path.Combine(mcc, "haloreach", "maps"));
    if (manifest.GetValueOrDefault("halo") != stamp || opts.ContainsKey("force"))
    {
        rc |= HaloExport.Run(mcc, Path.Combine(outDir, "halo"));
        if (rc == 0) manifest["halo"] = stamp;
    }
    else Log.Info("halo: cache up to date");
}
if (only is "" or "rust")
{
    if (!opts.TryGetValue("rust", out var rust)) { Console.Error.WriteLine("missing --rust"); return 2; }
    var stamp = Stamp.Of(Path.Combine(rust, "Bundles", "shared"));
    if (manifest.GetValueOrDefault("rust") != stamp || opts.ContainsKey("force"))
    {
        rc |= RustExport.Run(rust, Path.Combine(outDir, "rust"));
        if (rc == 0) manifest["rust"] = stamp;
    }
    else Log.Info("rust: cache up to date");
}
File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
Log.Info(rc == 0 ? "done" : "finished with errors");
return rc;

namespace ReachSurvival.Convert
{
    static class Log
    {
        // The game shows these lines on its loading screen, so keep them short and player-readable.
        public static void Info(string s) { Console.WriteLine(s); Console.Out.Flush(); }
        public static void Error(string s) { Console.WriteLine("ERROR " + s); Console.Out.Flush(); }
    }

    static class Stamp
    {
        /// <summary>Size + newest write time of a folder's files: changes whenever the game updates.</summary>
        public static string Of(string dir)
        {
            if (!Directory.Exists(dir)) return "missing";
            long size = 0, newest = 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
            {
                size += f.Length;
                newest = Math.Max(newest, f.LastWriteTimeUtc.Ticks);
            }
            return $"{size}:{newest}";
        }
    }
}
