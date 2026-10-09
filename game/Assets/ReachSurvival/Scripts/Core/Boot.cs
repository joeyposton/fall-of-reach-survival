using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReachSurvival
{
    /// <summary>
    /// Starts with the scene: finds the player's games, runs the converter on first launch (or after a game
    /// update), loads the cache, then hands over to <see cref="Game"/>. Shows a plain loading screen meanwhile.
    /// </summary>
    public sealed class Boot : MonoBehaviour
    {
        readonly List<string> lines = new();
        string fatal;
        public static bool Ready { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (FindAnyObjectByType<Boot>() != null) return;
            new GameObject("Boot").AddComponent<Boot>();
        }

        IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);
            Paths.Init();
            GameLog.Init();
            Application.targetFrameRate = 144;
            Say($"Halo: MCC: {Paths.Mcc ?? "not found"}");
            Say($"Rust: {Paths.Rust ?? "not found"}");
            if (Paths.Mcc == null || !Directory.Exists(Path.Combine(Paths.Mcc, "haloreach", "maps")))
            {
                fatal = "Halo: The Master Chief Collection with Halo: Reach is needed. Install it in Steam, then start again.";
                yield break;
            }
            if (Paths.Rust == null || !Directory.Exists(Path.Combine(Paths.Rust, "Bundles", "shared")))
            {
                fatal = "Rust is needed (only its rules are read). Install it in Steam, then start again.";
                yield break;
            }

            yield return RunConverter();
            if (fatal != null) yield break;

            Say("Loading rules");
            yield return null;
            try { Content.LoadRules(); }
            catch (Exception e) { fatal = "Couldn't read the converted data: " + e.Message; yield break; }

            // --join <code>: connect to a friend's game first (their host runs the rules; we build the world from our own files)
            var join = Paths.Arg("join");
            if (!string.IsNullOrEmpty(join) && join != "1")
            {
                Say($"Joining game {join}");
                var t = OnlineNet.Start(host: false, join);
                while (!t.IsCompleted) yield return null;
                if (t.Result == null) { fatal = OnlineNet.Status; yield break; }
            }

            Say("Building Forge World");
            yield return null;
            var game = new GameObject("Game").AddComponent<Game>();
            yield return game.Load(Say);

            // --host: open this game to friends over Unity Relay (solo play needs none of this)
            if (Paths.Arg("host") != null && Net.IsHost) _ = OnlineNet.Start(host: true, null);
            Ready = true;
            lines.Clear();
        }

        IEnumerator RunConverter()
        {
            Say("Reading your Halo: Reach and Rust files (only slow the first time)");
            var psi = new ProcessStartInfo(Paths.Converter,
                $"--mcc \"{Paths.Mcc}\" --rust \"{Paths.Rust}\" --out \"{Paths.Cache}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception e) { fatal = $"Couldn't start the converter ({Paths.Converter}): {e.Message}"; yield break; }
            var queue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) queue.Enqueue(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) queue.Enqueue("ERROR " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            while (!p.HasExited)
            {
                while (queue.TryDequeue(out var l)) Say(l);
                yield return null;
            }
            p.WaitForExit();
            while (queue.TryDequeue(out var l)) Say(l);
            if (p.ExitCode != 0) fatal = "Some of your game files couldn't be read (details above). Check both games are up to date.";
        }

        void Say(string s)
        {
            lines.Add(s);
            if (lines.Count > 18) lines.RemoveAt(0);
            Debug.Log("[boot] " + s);
        }

        void OnGUI()
        {
            if (Ready) return;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var title = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
            var body = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
            GUI.Label(new Rect(60, 50, 1200, 60), "REACH SURVIVAL", title);
            float y = 130;
            foreach (var l in lines) { GUI.Label(new Rect(60, y, Screen.width - 120, 26), l, body); y += 26; }
            if (fatal != null)
            {
                GUI.color = new Color(1f, 0.55f, 0.4f);
                GUI.Label(new Rect(60, y + 20, Screen.width - 120, 80), fatal, body);
            }
        }
    }
}
