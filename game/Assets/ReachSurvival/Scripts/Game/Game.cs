using System;
using System.Collections;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>Owns the running match on this machine: the world, the local Spartan, and the host/client halves.</summary>
    public sealed class Game : MonoBehaviour
    {
        public static Game Instance { get; private set; }
        public Player LocalPlayer { get; private set; }
        public int Seed { get; private set; }

        void Awake()
        {
            Instance = this;
            ItemDb.Init();
            gameObject.AddComponent<Hud>();
        }

        public IEnumerator Load(Action<string> say)
        {
            yield return World.Build(say);
            gameObject.AddComponent<Client>();
            if (Net.IsHost)
            {
                Seed = Environment.TickCount;
                var server = gameObject.AddComponent<Server>();
                say("Scattering resources and Covenant patrols");
                yield return null;
                server.Begin(Seed);
            }
            say("Dropping your Spartan in");
            Client.Instance.Hello(PlayerName());
            while (LocalPlayer == null) yield return null; // the host's Welcome spawns us
            if (Paths.Arg("autotest") != null) gameObject.AddComponent<AutoTest>();
        }

        public void SpawnLocal(Vector3 at) => LocalPlayer = Player.Create(at, isLocal: true);

        static string PlayerName()
        {
            var n = Paths.Arg("name") ?? Environment.UserName;
            return string.IsNullOrWhiteSpace(n) ? "Spartan" : n.Length > 16 ? n.Substring(0, 16) : n;
        }

        /// <summary>Reach armour is white under a colour-change mask; each Spartan gets a distinct tint.</summary>
        public static Color TeamColor(ulong id)
        {
            var palette = new[]
            {
                new Color(0.42f, 0.55f, 0.36f), new Color(0.75f, 0.30f, 0.28f), new Color(0.30f, 0.45f, 0.75f), new Color(0.85f, 0.70f, 0.30f),
                new Color(0.55f, 0.35f, 0.70f), new Color(0.30f, 0.70f, 0.70f), new Color(0.85f, 0.50f, 0.25f), new Color(0.65f, 0.65f, 0.65f),
            };
            return palette[(int)(id % (ulong)palette.Length)];
        }
    }
}
