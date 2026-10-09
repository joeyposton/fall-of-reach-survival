using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Where the player's games are, where our cache lives, and the launch options.
    /// Melty launches us with --mcc "{game}" --rust "{game:rust}"; when run by hand we look in Steam's libraries.
    /// </summary>
    public static class Paths
    {
        public static string Mcc { get; private set; }
        public static string Rust { get; private set; }
        public static string Cache { get; private set; }
        public static string LogFile { get; private set; }
        public static readonly Dictionary<string, string> Args = new();

        public static void Init()
        {
            var argv = Environment.GetCommandLineArgs();
            for (int i = 1; i < argv.Length; i++)
            {
                if (!argv[i].StartsWith("--")) continue;
                var key = argv[i].Substring(2);
                Args[key] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "1";
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Cache = Path.Combine(local, "ReachSurvival", "cache");
            // a second copy on the same PC (testing) can log elsewhere with --log <file name>
            LogFile = Path.Combine(local, "ReachSurvival", "logs", Arg("log") ?? "latest.log");
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));

            Mcc = Arg("mcc") ?? Environment.GetEnvironmentVariable("REACHSURVIVAL_MCC") ?? FindSteamGame("Halo The Master Chief Collection");
            Rust = Arg("rust") ?? Environment.GetEnvironmentVariable("REACHSURVIVAL_RUST") ?? FindSteamGame("Rust");
        }

        public static string Arg(string key) => Args.TryGetValue(key, out var v) ? v : null;

        /// <summary>The converter ships next to the game (Converter/); in the editor it is the sibling project's build.</summary>
        public static string Converter
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("REACHSURVIVAL_CONVERTER");
                if (!string.IsNullOrEmpty(env)) return env;
                var shipped = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Converter", "ReachSurvival.Convert.exe");
                if (File.Exists(shipped)) return shipped;
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "converter", "bin", "Debug", "net9.0-windows", "ReachSurvival.Convert.exe"));
            }
        }

        static string FindSteamGame(string folder)
        {
            foreach (var steam in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            {
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    var dir = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", folder);
                    if (Directory.Exists(dir)) return dir;
                }
            }
            return null;
        }
    }
}
