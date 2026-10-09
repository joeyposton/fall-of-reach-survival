using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Playing together over Unity Relay (no port forwarding). The host's game opens a private session and logs
    /// "Hosting at &lt;join code&gt;" (Melty turns that into a join link); friends start with --join &lt;code&gt;.
    /// Messages ride Netcode for GameObjects' named messages; nothing else of NGO is used.
    /// </summary>
    public sealed class OnlineNet : MonoBehaviour, INetTransport
    {
        public const int MaxPlayers = 8;
        const string Channel = "rs";
        public static OnlineNet Instance { get; private set; }
        public static string Status { get; private set; } = "";
        public ISession Session { get; private set; }
        NetworkManager nm;
        readonly List<ulong> targets = new();

        public static async Task<OnlineNet> Start(bool host, string code)
        {
            var go = new GameObject("Online");
            DontDestroyOnLoad(go);
            var me = go.AddComponent<OnlineNet>();
            Instance = me;
            try
            {
                Status = "Connecting to Unity services";
                // a separate profile per copy, so two copies on one PC are two players
                var profile = (Paths.Arg("name") ?? (host ? "host" : "guest")) + "_" + System.Diagnostics.Process.GetCurrentProcess().Id;
                await UnityServices.InitializeAsync(new InitializationOptions().SetProfile(Sanitize(profile)));
                if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
                me.BuildNetworkManager();
                if (host)
                {
                    Status = "Opening your game to friends";
                    var opts = new SessionOptions { Name = "Reach Survival", MaxPlayers = MaxPlayers, IsPrivate = true }.WithRelayNetwork();
                    me.Session = await MultiplayerService.Instance.CreateSessionAsync(opts);
                    // Melty reads this exact line from our log to give friends a join link.
                    Debug.Log("Hosting at " + me.Session.Code);
                    Status = "Friends can join with code " + me.Session.Code;
                }
                else
                {
                    Status = "Joining " + code;
                    me.Session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant());
                    while (!me.nm.IsConnectedClient) await Task.Yield();
                    Net.IsHost = false;
                    Net.LocalId = me.nm.LocalClientId;
                    Status = "Joined";
                }
                Net.Transport = me;
                return me;
            }
            catch (Exception e)
            {
                Status = (host ? "Couldn't open the game to friends: " : "Couldn't join: ") + e.Message;
                Debug.LogWarning("[online] " + Status);
                return null;
            }
        }

        static string Sanitize(string s)
        {
            var chars = new List<char>();
            foreach (var ch in s) if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') chars.Add(ch);
            var r = new string(chars.ToArray());
            return r.Length > 30 ? r.Substring(0, 30) : r.Length == 0 ? "player" : r;
        }

        void BuildNetworkManager()
        {
            var go = new GameObject("NetworkManager");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            var utp = go.AddComponent<UnityTransport>();
            nm = go.AddComponent<NetworkManager>();
            nm.NetworkConfig = new NetworkConfig { NetworkTransport = utp, EnableSceneManagement = false, ConnectionApproval = false, TickRate = 30 };
            go.SetActive(true);
            nm.SetSingleton();
            nm.OnServerStarted += Register;
            nm.OnClientStarted += Register;
            nm.OnClientDisconnectCallback += id =>
            {
                if (nm.IsServer) Server.Instance?.Leave(id);
                else Hud.Instance?.Toast("Lost the connection to the host", 10f);
            };
        }

        void Register()
        {
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(Channel);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Channel, OnMessage);
        }

        void OnMessage(ulong sender, FastBufferReader reader)
        {
            if (!reader.TryBeginRead(sizeof(int))) return;
            reader.ReadValue(out int len);
            if (len < 1 || len > (1 << 22) || !reader.TryBeginRead(len)) return;
            var bytes = new byte[len];
            reader.ReadBytesSafe(ref bytes, len);
            if (nm.IsServer) Server.Instance?.Receive(sender, bytes);
            else Client.Instance?.Receive(bytes);
        }

        void Send(IReadOnlyList<ulong> to, byte[] data, bool reliable)
        {
            if (nm == null || to.Count == 0) return;
            using var w = new FastBufferWriter(data.Length + 8, Allocator.Temp, 1 << 22);
            w.WriteValueSafe(data.Length);
            w.WriteBytesSafe(data);
            var delivery = reliable ? (data.Length > 1100 ? NetworkDelivery.ReliableFragmentedSequenced : NetworkDelivery.ReliableSequenced)
                                    : data.Length > 1100 ? NetworkDelivery.ReliableFragmentedSequenced : NetworkDelivery.Unreliable;
            nm.CustomMessagingManager.SendNamedMessage(Channel, to, w, delivery);
        }

        public void SendToHost(byte[] data)
        {
            if (nm == null || !nm.IsConnectedClient) return;
            targets.Clear(); targets.Add(NetworkManager.ServerClientId);
            Send(targets, data, true);
        }

        public void SendToClient(ulong client, byte[] data)
        {
            if (nm == null || !nm.IsServer) return;
            targets.Clear(); targets.Add(client);
            Send(targets, data, true);
        }

        public void SendToAllClients(byte[] data, bool reliable)
        {
            if (nm == null || !nm.IsServer) return;
            targets.Clear();
            foreach (var id in nm.ConnectedClientsIds) if (id != nm.LocalClientId) targets.Add(id);
            Send(targets, data, reliable);
        }

        public int PlayerCount => nm != null && nm.IsServer ? nm.ConnectedClientsIds.Count : 0;

        async void OnApplicationQuit()
        {
            try { if (Session != null) await Session.LeaveAsync(); } catch { }
        }
    }
}
