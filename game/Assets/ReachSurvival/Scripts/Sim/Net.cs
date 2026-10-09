using System;
using System.IO;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>Message kinds. Client→host requests and host→client state. Same order on every machine.</summary>
    public enum Msg : byte
    {
        // client -> host
        Hello, PlayerState, Fire, Melee, Place, Upgrade, Deploy, Craft, Interact, Respawn, ThrowExplosive,
        // host -> clients
        Welcome, Players, Enemies, Spawn, Remove, Health, Inv, Fx, Toast, Oven,
    }

    /// <summary>
    /// Message pipe. The host runs <see cref="Server"/>; every machine (host included) runs <see cref="Client"/>.
    /// Solo play loops back in-process; online play goes over Netcode for GameObjects + Unity Relay (NetTransport).
    /// </summary>
    public static class Net
    {
        public const ulong HostId = 0;
        public static bool IsHost { get; set; } = true;
        public static ulong LocalId { get; set; } = HostId;

        /// <summary>Set by the online transport; null means solo (loopback).</summary>
        public static INetTransport Transport;

        public static BinaryWriter Begin(Msg m, out MemoryStream ms)
        {
            ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((byte)m);
            return w;
        }

        /// <summary>Client → host.</summary>
        public static void ToHost(MemoryStream ms)
        {
            var bytes = ms.ToArray();
            if (IsHost || Transport == null) Server.Instance?.Receive(LocalId, bytes);
            else Transport.SendToHost(bytes);
        }

        /// <summary>Host → one client.</summary>
        public static void ToClient(ulong client, MemoryStream ms)
        {
            var bytes = ms.ToArray();
            if (client == LocalId) Client.Instance?.Receive(bytes);
            else Transport?.SendToClient(client, bytes);
        }

        /// <summary>Host → everyone (including itself).</summary>
        public static void ToAll(MemoryStream ms, bool reliable = true)
        {
            var bytes = ms.ToArray();
            Client.Instance?.Receive(bytes);
            Transport?.SendToAllClients(bytes, reliable);
        }

        public static void Write(this BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static Vector3 ReadV3(this BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    public interface INetTransport
    {
        void SendToHost(byte[] data);
        void SendToClient(ulong client, byte[] data);
        void SendToAllClients(byte[] data, bool reliable);
    }
}
