using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // Mario's multiplayer messages. The player's machine sends the frames of its sim (MarioFrames)
    // to the server, which relays them to every other client; each of those plays them back on a
    // shadow sim (MarioPuppet). The server's own game is a client of that too. Everyone needs the
    // same plugin build and the Sunshine disc's assets; a mismatch is logged once and that
    // player's Mario looks like Commando to the other.
    internal static class MarioNet
    {
        /// Frames of one Mario's sim: client to server, then server to the other clients.
        public const short FramesMsg = 0x4D01;

        /// A client's Mario's hits, fall and blast: client to server.
        public const short CombatMsg = 0x4D02;
        /// The server knocks a client's Mario over: server to that client.
        public const short KnockMsg = 0x4D03;
        /// Healing and Bandolier owe a client's Mario's tank: server to that client.
        public const short WaterMsg = 0x4D04;
        /// A coin dropped, or was picked up: server to the clients.
        public const short CoinMsg = 0x4D05;
        /// A client's Mario got a Shine at a charged teleporter: client to server.
        public const short ShineMsg = 0x4D06;

        /// A client's Mario touched a coin: client to server.
        public const short CoinTouchMsg = 0x4D07;

        /// One hit a client's Mario made, for the server to land.
        public struct HitRecord
        {
            public uint victim;
            public float coefficient, proc;
            public Vector3 at, force;
            public bool ignite;
            /// The skill it came from (RoR2's DamageSource).
            public byte source;
        }

        static readonly Dictionary<uint, FrameQueue> queues = new Dictionary<uint, FrameQueue>();
        static bool warned;

        public static void Register()
        {
            Mp.Register(FramesMsg, OnServer, OnClient);
            Mp.Register(CombatMsg, OnCombat, null);
            Mp.Register(KnockMsg, null, OnKnock);
            Mp.Register(WaterMsg, null, OnWater);
            Mp.Register(CoinMsg, null, OnCoin);
            Mp.Register(ShineMsg, OnShine, null);
            Mp.Register(CoinTouchMsg, OnCoinTouch, null);
            Run.onRunDestroyGlobal += run => queues.Clear();
        }

        /// The server relays a client's frames to everyone else untouched (the host's own
        /// frames come in with `from` null and go to everyone).
        static void OnServer(NetworkConnection from, ByteReader reader) =>
            Mp.SendToAllExcept(FramesMsg, reader.Raw, from);

        static void OnClient(NetworkConnection from, ByteReader reader)
        {
            using var timing = MarioPerformance.Measure(MarioPerformance.Part.Receive);
            var status = FrameCodec.ReadHeader(reader, out uint netId, out bool clone, out int count, out byte theirs);
            if (status != FrameCodec.BatchStatus.Ok)
            {
                if (status != FrameCodec.BatchStatus.Corrupt && !warned)
                {
                    warned = true;
                    Plugin.Log.LogWarning(status == FrameCodec.BatchStatus.WrongAbi
                        ? $"Another player's Mario runs libsms ABI {theirs}, this one {Native.AbiVersion}: install the same Mario release as everyone else. Their Mario will look like Commando."
                        : $"Another player's Mario uses network protocol {theirs}, this one {FrameCodec.Protocol}: install the same Mario release as everyone else. Their Mario will look like Commando.");
                }
                return;
            }
            // The player's own frames come back through the relay: they already have them.
            var go = Mp.Find(netId);
            var body = go ? go.GetComponent<CharacterBody>() : null;
            if (body && body.hasEffectiveAuthority) return;
            var frames = new List<Frame>(count);
            if (FrameCodec.ReadFrames(reader, count, frames) != FrameCodec.BatchStatus.Ok) return;
            if (!queues.TryGetValue(netId, out var queue))
                queues[netId] = queue = new FrameQueue();
            queue.Clone = clone;
            foreach (var f in frames) queue.Add(f);
        }

        /// The frames waiting for this body's puppet, or null if none have come.
        public static FrameQueue QueueFor(uint netId) =>
            queues.TryGetValue(netId, out var q) ? q : null;

        public static void Drop(uint netId) => queues.Remove(netId);

        static void Vec(ByteWriter w, Vector3 v) { w.F32(v.x); w.F32(v.y); w.F32(v.z); }
        static Vector3 Vec(ByteReader r) => new Vector3(r.F32(), r.F32(), r.F32());
        static bool Finite(Vector3 v) => !(float.IsNaN(v.x + v.y + v.z) || float.IsInfinity(v.x + v.y + v.z));

        /// A client's Mario's step: what he hit, whether FLUDD's water was hitting (Shuriken's
        /// pulse), how far he fell (a share of max health), and any super pound's blast.
        public static void SendCombat(CharacterBody attacker, List<HitRecord> hits,
            List<(Vector3 at, float radius)> blasts, bool pulse, float fall)
        {
            if (!attacker || attacker.netId.Value == 0 || (hits.Count == 0 && blasts.Count == 0 && !pulse && fall <= 0f)) return;
            var w = new ByteWriter(64 + hits.Count * 44);
            w.U8(FrameCodec.Protocol);
            w.U32(attacker.netId.Value);
            w.U8((byte)(pulse ? 1 : 0));
            w.F32(fall);
            w.U8((byte)Mathf.Min(hits.Count, 255));
            for (int i = 0; i < hits.Count && i < 255; i++)
            {
                var h = hits[i];
                w.U32(h.victim); w.F32(h.coefficient); w.F32(h.proc); Vec(w, h.at); Vec(w, h.force); w.Bool(h.ignite); w.U8(h.source);
            }
            w.U8((byte)Mathf.Min(blasts.Count, 8));
            for (int i = 0; i < blasts.Count && i < 8; i++) { Vec(w, blasts[i].at); w.F32(blasts[i].radius); }
            Mp.SendToServer(CombatMsg, w.ToArray());
        }

        /// The server lands a client Mario's hits as his (his damage stat, crits, on-hit items, kill
        /// credit), applies his fall, pulses Shuriken, and shows the blast to everyone.
        static void OnCombat(NetworkConnection from, ByteReader r)
        {
            if (r.U8() != FrameCodec.Protocol) return;
            var go = Mp.Find(r.U32());
            var body = go ? go.GetComponent<CharacterBody>() : null;
            bool pulse = r.Bool();
            float fall = r.F32();
            int n = r.U8();
            // Only another player's Mario: never the host's own (he lands his hits himself).
            bool valid = body && body.GetComponent<MarioDriver>() && !body.hasEffectiveAuthority
                && body.healthComponent && body.healthComponent.alive;
            for (int i = 0; i < n; i++)
            {
                var hit = new HitRecord
                {
                    victim = r.U32(), coefficient = r.F32(), proc = r.F32(), at = Vec(r), force = Vec(r), ignite = r.Bool(), source = r.U8(),
                };
                if (r.Failed) return;
                if (!valid || !Finite(hit.at) || !Finite(hit.force) || !(hit.coefficient > 0f) || !(hit.proc >= 0f)) continue;
                hit.coefficient = Mathf.Min(hit.coefficient, 200f);
                hit.proc = Mathf.Min(hit.proc, 1f);
                var vgo = Mp.Find(hit.victim);
                var victim = vgo ? vgo.GetComponent<CharacterBody>() : null;
                if (victim && victim.healthComponent)
                    MarioCombat.DealFromClient(body, victim.healthComponent, hit);
            }
            int blasts = r.U8();
            for (int i = 0; i < blasts; i++)
            {
                var at = Vec(r);
                float radius = r.F32();
                if (valid && Finite(at) && radius > 0f && radius < 100f) MarioCombat.Blast(at, radius);
            }
            if (!valid || r.Failed) return;
            if (pulse) body.GetComponent<NativeBody>()?.PulsePrimary(Plugin.ShurikenInterval.Value);
            if (fall > 0f && fall <= 1f && GlobalEventManager.instance)
                GlobalEventManager.instance.OnCharacterHitGroundServer(body, new CharacterMotor.HitGroundInfo { velocity = Vector3.down * (body.jumpPower + 20f + fall * 60f), position = body.footPosition, ownerBodyObject = body.gameObject, isValidForEffect = true });
        }

        /// The server tells a client's Mario a powerful hit knocked him over (his sim is on his machine).
        public static void SendKnock(CharacterBody body, Vector3 from)
        {
            var user = body && body.master && body.master.playerCharacterMasterController
                ? body.master.playerCharacterMasterController.networkUser : null;
            var conn = user ? user.connectionToClient : null;
            if (conn == null || body.hasEffectiveAuthority) return;
            var w = new ByteWriter(20);
            w.U32(body.netId.Value);
            Vec(w, from);
            Mp.SendTo(conn, KnockMsg, w.ToArray());
        }

        static void OnKnock(NetworkConnection unused, ByteReader r)
        {
            var go = Mp.Find(r.U32());
            var from = Vec(r);
            if (r.Failed || !go || !Finite(from)) return;
            var body = go.GetComponent<CharacterBody>();
            if (body && body.hasEffectiveAuthority) go.GetComponent<MarioDriver>()?.KnockFrom(from);
        }

        /// The server tells a client's Mario what his tank is owed (healing into water, Bandolier).
        public static void SendWater(CharacterBody body, float share, bool refill)
        {
            var user = body && body.master && body.master.playerCharacterMasterController
                ? body.master.playerCharacterMasterController.networkUser : null;
            var conn = user ? user.connectionToClient : null;
            if (conn == null || body.hasEffectiveAuthority) return;
            var w = new ByteWriter(12);
            w.U32(body.netId.Value);
            w.F32(share);
            w.Bool(refill);
            Mp.SendTo(conn, WaterMsg, w.ToArray());
        }

        static void OnWater(NetworkConnection unused, ByteReader r)
        {
            var go = Mp.Find(r.U32());
            float share = r.F32();
            bool refill = r.Bool();
            if (r.Failed || !go || !(share >= 0f) || share > 100f) return;
            var body = go.GetComponent<CharacterBody>();
            if (body && body.hasEffectiveAuthority) go.GetComponent<MarioDriver>()?.WaterOwed(share, refill);
        }

        // ---- coins -------------------------------------------------------------------------
        // The server drops and picks up the yellow coins (MarioCoins); everyone else draws a copy.

        public static void SendCoin(int index, Vector3 pos, Vector3 velocity, TeamIndex team)
        {
            if (!Mp.Networked) return;
            var w = new ByteWriter(32);
            w.U8(0);
            w.U8((byte)index);
            Vec(w, pos);
            Vec(w, velocity);
            w.U8((byte)team);
            Mp.SendToAll(CoinMsg, w.ToArray());
        }

        /// A client's own Mario touched coin `index` in its copy of the world; the server decides.
        public static void SendCoinTouch(int index, CharacterBody by)
        {
            if (!Mp.Networked || !by || by.netId.Value == 0) return;
            var w = new ByteWriter(8);
            w.U8(FrameCodec.Protocol);
            w.U8((byte)index);
            w.U32(by.netId.Value);
            Mp.SendToServer(CoinTouchMsg, w.ToArray());
        }

        static void OnCoinTouch(NetworkConnection from, ByteReader r)
        {
            if (!MarioCoins.Instance || r.U8() != FrameCodec.Protocol) return;
            int index = r.U8();
            var go = Mp.Find(r.U32());
            var body = go ? go.GetComponent<CharacterBody>() : null;
            if (!r.Failed && body) MarioCoins.Instance.Touched(index, body);
        }

        public static void SendCoinTaken(int index, CharacterBody by, Vector3 pos)
        {
            if (!Mp.Networked) return;
            var w = new ByteWriter(24);
            w.U8(1);
            w.U8((byte)index);
            w.U32(by.netId.Value);
            Vec(w, pos);
            Mp.SendToAll(CoinMsg, w.ToArray());
        }

        /// A Mario's pickup sound and sparks, on whichever of his sims this machine has (the player's
        /// own, or the puppet of another player's).
        public static void CoinCollected(CharacterBody body, Vector3 pos)
        {
            if (!body) return;
            body.GetComponent<MarioDriver>()?.CoinCollected(pos);
            body.GetComponent<MarioPuppet>()?.CoinCollected(pos);
        }

        static void OnCoin(NetworkConnection unused, ByteReader r)
        {
            // The server's own client side gets these too, and already did it.
            if (NetworkServer.active || !MarioCoins.Instance) return;
            byte kind = r.U8();
            int index = r.U8();
            if (kind == 0)
            {
                var pos = Vec(r);
                var vel = Vec(r);
                var team = (TeamIndex)r.U8();
                if (!r.Failed && Finite(pos) && Finite(vel)) MarioCoins.Instance.Spawned(index, pos, vel, team);
            }
            else if (kind == 1)
            {
                var go = Mp.Find(r.U32());
                var pos = Vec(r);
                MarioCoins.Instance.Taken(index);
                if (!r.Failed && go && Finite(pos)) CoinCollected(go.GetComponent<CharacterBody>(), pos);
            }
        }

        // ---- the Shine -----------------------------------------------------------------------------

        /// A client's Mario's Shine ends the stage: ask the server to finish the teleporter.
        public static void SendShine(TeleporterInteraction tele, CharacterBody by)
        {
            if (!tele || !by || tele.netId.Value == 0 || by.netId.Value == 0) return;
            var w = new ByteWriter(12);
            w.U8(FrameCodec.Protocol);
            w.U32(tele.netId.Value);
            w.U32(by.netId.Value);
            Mp.SendToServer(ShineMsg, w.ToArray());
        }

        static void OnShine(NetworkConnection from, ByteReader r)
        {
            if (r.U8() != FrameCodec.Protocol) return;
            var teleObject = Mp.Find(r.U32());
            var bodyObject = Mp.Find(r.U32());
            if (r.Failed || !teleObject || !bodyObject) return;
            var tele = teleObject.GetComponent<TeleporterInteraction>();
            var body = bodyObject.GetComponent<CharacterBody>();
            // Only another player's living Mario: the host's own asks itself.
            if (!tele || !body || !body.GetComponent<MarioDriver>() || body.hasEffectiveAuthority
                || !body.healthComponent || !body.healthComponent.alive) return;
            MarioShine.Trigger(tele, body);
        }

        /// Sends one body's recorded frames.
        public static void Send(FrameRecorder recorder, uint netId, bool clone)
        {
            var payload = recorder.Take(netId, clone);
            if (payload != null)
            {
                MarioPerformance.Sent(payload.Length);
                Mp.SendToServer(FramesMsg, payload);
            }
        }
    }
}
