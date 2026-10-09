using System.IO;
using UnityEngine;

namespace ReachSurvival
{
    public enum EKind : byte { Node, Piece, Beacon, Smelter, Armory, Door, Satchel, Enemy, Drop }

    /// <summary>
    /// Anything in the world besides Spartans and the terrain. The host owns the truth; clients get copies via
    /// Spawn/Health/Remove messages. <see cref="Sub"/> is the sheet row: node type, piece id, item id or enemy id.
    /// </summary>
    public sealed class Ent
    {
        public int Id;
        public EKind Kind;
        public string Sub = "";
        public Vector3 Pos;
        public float Yaw;
        public int Grade;
        public float Hp, MaxHp;
        public int Parent;      // door: its doorway piece; satchel: the entity it stuck to (0 = ground)
        public ulong Owner;
        public bool Open;       // door open / smelter lit

        public void Write(BinaryWriter w)
        {
            w.Write(Id); w.Write((byte)Kind); w.Write(Sub); w.Write(Pos); w.Write(Yaw);
            w.Write((byte)Grade); w.Write(Hp); w.Write(MaxHp); w.Write(Parent); w.Write(Owner); w.Write(Open);
        }

        public static Ent Read(BinaryReader r) => new Ent
        {
            Id = r.ReadInt32(), Kind = (EKind)r.ReadByte(), Sub = r.ReadString(), Pos = r.ReadV3(), Yaw = r.ReadSingle(),
            Grade = r.ReadByte(), Hp = r.ReadSingle(), MaxHp = r.ReadSingle(), Parent = r.ReadInt32(), Owner = r.ReadUInt64(), Open = r.ReadBoolean(),
        };

        public Quaternion Rot => Quaternion.Euler(0f, Yaw, 0f);
    }

    /// <summary>Halo vitals as plain numbers, so the host can run them for every Spartan and Covenant.</summary>
    public sealed class VitalsState
    {
        public float MaxBody, MaxShield, Body, Shield, StunTime, RechargeTime, LastHit = -99f;
        public bool Dead => Body <= 0f;

        public VitalsState(float body, float shield, float stun, float recharge)
        {
            MaxBody = Body = body; MaxShield = Shield = shield; StunTime = stun; RechargeTime = Mathf.Max(0.1f, recharge);
        }

        public static VitalsState Of(string statsRow, string shieldRow = null)
        {
            var s = Content.HaloStats[statsRow];
            float stun = 7f, recharge = 3f;
            if (shieldRow != null && Content.HaloStats.TryGetValue(shieldRow, out var sh)) { stun = sh["shieldStunTime"]; recharge = sh["shieldRechargeTime"]; }
            else if (s.TryGetValue("shieldStunTime", out var st)) { stun = st; recharge = s["shieldRechargeTime"]; }
            return new VitalsState(s["body"], s.TryGetValue("shield", out var shv) ? shv : 0f, stun, recharge);
        }

        public void Damage(float amount, float now)
        {
            if (Dead) return;
            LastHit = now;
            float toShield = Mathf.Min(Shield, amount);
            Shield -= toShield;
            Body = Mathf.Max(0f, Body - (amount - toShield));
        }

        public void Tick(float now, float dt)
        {
            if (!Dead && MaxShield > 0f && now - LastHit > StunTime && Shield < MaxShield)
                Shield = Mathf.Min(MaxShield, Shield + MaxShield / RechargeTime * dt);
        }

        public void Restore() { Body = MaxBody; Shield = MaxShield; LastHit = -99f; }
    }

    /// <summary>Put on every collider the host may shoot, so a raycast hit can be turned back into a target.</summary>
    public sealed class EntityRef : MonoBehaviour
    {
        public bool IsPlayer;
        public ulong PlayerId;
        public int EntId;
    }
}
