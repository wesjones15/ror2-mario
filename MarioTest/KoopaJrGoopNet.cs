using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RoR2;
using Ror2Common;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // The goop world in multiplayer (libsms-ffi goop.rs `sms_goop_set_mirror`). The server's world
    // is the real one: it steps the goop's lifetime and the critters and deals their harm. Every
    // client keeps a mirror of it, which its own sims stand in, paint and clean (so a client's
    // Bowser Jr. sees his paint at once and his Mario friends slip in it), and which logs what
    // they did for the server to redo on its own (sms_goop_take_ops / sms_goop_apply_ops):
    // stamps, the sign, FLUDD's cleaning, goop balls and seeds, stomps and water on the critters.
    // The server sends back its chunks as they change and its critters a few times a second;
    // a chunk the client itself just changed waits a moment, so its own paint doesn't blink
    // out before the server has redone it. Sounds and particles go to every machine.
    internal sealed partial class KoopaJrGoop
    {
        const byte Protocol = 1;
        const float ChunkEvery = 0.1f, CritterEvery = 0.1f, NetRange = 120f, HoldSeconds = 0.6f;
        const int ChunksPerSend = 16, NetMaxChunks = 1024, MaxOps = 512;

        // The server: each chunk's version as last sent.
        readonly Dictionary<(int, int), uint> sentChunks = new Dictionary<(int, int), uint>();
        readonly List<(int, int)> goneChunks = new List<(int, int)>();
        readonly int[] netKeys = new int[NetMaxChunks * 2];
        readonly uint[] netVersions = new uint[NetMaxChunks];
        readonly Native.Critter[] netCritters = new Native.Critter[MaxCritters];
        float chunksAt, crittersAt;
        int chunkCursor;

        // A client: chunks its own sims just changed, and the server's copies waiting on them.
        readonly Dictionary<(int, int), float> heldUntil = new Dictionary<(int, int), float>();
        readonly Dictionary<(int, int), byte[]> deferred = new Dictionary<(int, int), byte[]>();
        readonly List<(int, int)> released = new List<(int, int)>();
        readonly Native.GoopOp[] ops = new Native.GoopOp[MaxOps];
        bool helloSent;
        float snapAt;
        readonly Dictionary<ulong, Vector3> shown = new Dictionary<ulong, Vector3>();

        // Sounds and particles to send: this step's.
        readonly ByteWriter fxOut = new ByteWriter(256);
        int fxCount;
        readonly Dictionary<string, byte[]> netNames = new Dictionary<string, byte[]>();
        readonly Dictionary<IntPtr, string> nameStrings = new Dictionary<IntPtr, string>();

        // Scratch for a chunk's texels.
        readonly byte[] netDensity = new byte[256], netKinds = new byte[256];
        readonly float[] netHeights = new float[256];

        public static void RegisterNet()
        {
            Mp.Register(MarioNet.GoopChunksMsg, null, (from, r) => { if (!NetworkServer.active) instance?.OnChunks(r); });
            Mp.Register(MarioNet.GoopCrittersMsg, null, (from, r) => { if (!NetworkServer.active) instance?.OnCritters(r); });
            Mp.Register(MarioNet.GoopOpsMsg, (from, r) => instance?.OnOps(from, r), null);
            Mp.Register(MarioNet.GoopFxMsg, (from, r) => instance?.OnFxFromClient(from, r),
                (from, r) => { if (!NetworkServer.active) instance?.OnFx(r); });
            Mp.Register(MarioNet.GoopHelloMsg, (from, r) => instance?.OnHello(r), null);
        }

        void NetReset()
        {
            sentChunks.Clear();
            heldUntil.Clear();
            deferred.Clear();
            shown.Clear();
            helloSent = false;
            fxOut.Clear();
            fxCount = 0;
        }

        // ---- sounds and particles ----

        string NameString(IntPtr p)
        {
            if (nameStrings.TryGetValue(p, out var s)) return s;
            return nameStrings[p] = Marshal.PtrToStringAnsi(p) ?? "";
        }

        byte[] NetName(string s)
        {
            if (netNames.TryGetValue(s, out var b)) return b;
            if (netNames.Count > 512) netNames.Clear();
            return netNames[s] = NativeLib.CString(s);
        }

        /// A world sound (sound) or particle burst played here, and queued for the other machines.
        void QueueFx(bool sound, IntPtr name, Native.Vec3 pos, float scale)
        {
            if (!Mp.Networked || name == IntPtr.Zero || fxCount >= 200) return;
            fxOut.U8((byte)(sound ? 0 : 1));
            fxOut.Str(NameString(name));
            fxOut.F32(pos.x); fxOut.F32(pos.y); fxOut.F32(pos.z);
            fxOut.F32(scale);
            fxCount++;
        }

        byte[] TakeFx()
        {
            if (fxCount == 0) return null;
            var w = new ByteWriter(8 + fxOut.Length);
            w.U8(Protocol);
            w.U16((ushort)fxCount);
            w.Append(fxOut);
            fxOut.Clear();
            fxCount = 0;
            return w.ToArray();
        }

        /// Plays a batch of sounds and bursts; returns false if it was unreadable.
        bool PlayFx(ByteReader r)
        {
            if (r.U8() != Protocol) return false;
            int n = r.U16();
            for (int i = 0; i < n; i++)
            {
                bool sound = r.U8() == 0;
                string name = r.Str();
                var pos = new Native.Vec3(r.F32(), r.F32(), r.F32());
                float scale = r.F32();
                if (r.Failed) return false;
                if (string.IsNullOrEmpty(name) || name.Length > 64 || !float.IsFinite(pos.x + pos.y + pos.z)) continue;
                if (sound) Sound(NetName(name), pos);
                else Emit(NetName(name), pos, float.IsFinite(scale) ? Mathf.Clamp(scale, 0.05f, 10f) : 1f);
            }
            return true;
        }

        void OnFx(ByteReader r)
        {
            if (ready) PlayFx(r);
        }

        /// A client's own sims' sounds and bursts: played here and passed to the others.
        void OnFxFromClient(NetworkConnection from, ByteReader r)
        {
            if (from == null || !ready) return;
            if (PlayFx(r)) Mp.SendToAllExcept(MarioNet.GoopFxMsg, r.Raw, from);
        }

        // ---- the server ----

        void NetServer()
        {
            var fx = TakeFx();
            if (!Mp.Networked) return;
            if (fx != null) Mp.SendToAll(MarioNet.GoopFxMsg, fx);
            float now = Time.time;
            if (now - chunksAt >= ChunkEvery)
            {
                chunksAt = now;
                SendChunks();
            }
            if (now - crittersAt >= CritterEvery)
            {
                crittersAt = now;
                SendCritters();
            }
        }

        /// The chunks that changed since they were last sent (a few at a time), and the ones gone.
        void SendChunks()
        {
            uint total = Native.sms_goop_chunk_list(world, netKeys, netVersions, NetMaxChunks);
            int n = (int)Math.Min(total, (uint)NetMaxChunks);
            var w = new ByteWriter(2048);
            w.U8(Protocol);
            int countAt = w.Length;
            w.U16(0);
            int count = 0;
            var live = new HashSet<(int, int)>();
            // Start where the last send stopped, so a busy world's chunks all get their turn.
            int start = n > 0 ? chunkCursor % n : 0;
            for (int j = 0; j < n; j++)
            {
                int i = (start + j) % n;
                var key = (netKeys[2 * i], netKeys[2 * i + 1]);
                live.Add(key);
                if (count >= ChunksPerSend) continue;
                if (sentChunks.TryGetValue(key, out uint v) && v == netVersions[i]) continue;
                if (Native.sms_goop_chunk(world, key.Item1, key.Item2, netDensity, netKinds, netHeights) == 0) continue;
                WriteChunk(w, key);
                sentChunks[key] = netVersions[i];
                count++;
                if (count == ChunksPerSend) chunkCursor = i + 1;
            }
            goneChunks.Clear();
            foreach (var key in sentChunks.Keys)
                if (!live.Contains(key)) goneChunks.Add(key);
            foreach (var key in goneChunks)
            {
                sentChunks.Remove(key);
                w.I32(key.Item1);
                w.I32(key.Item2);
                w.U8(0);
                count++;
            }
            if (count == 0) return;
            var bytes = w.ToArray();
            bytes[countAt] = (byte)(count & 0xFF);
            bytes[countAt + 1] = (byte)(count >> 8);
            Mp.SendToAll(MarioNet.GoopChunksMsg, bytes);
        }

        /// One chunk: which texels have goop (a bit each), the lowest height, then each such
        /// texel's density, kind and height above that lowest (whole sim units).
        void WriteChunk(ByteWriter w, (int, int) key)
        {
            w.I32(key.Item1);
            w.I32(key.Item2);
            w.U8(1);
            float low = float.MaxValue;
            for (int i = 0; i < 256; i++)
                if (netDensity[i] > 0 && float.IsFinite(netHeights[i])) low = Mathf.Min(low, netHeights[i]);
            if (low == float.MaxValue) low = 0f;
            for (int b = 0; b < 32; b++)
            {
                byte bits = 0;
                for (int k = 0; k < 8; k++)
                {
                    int i = b * 8 + k;
                    if (netDensity[i] > 0 && float.IsFinite(netHeights[i])) bits |= (byte)(1 << k);
                }
                w.U8(bits);
            }
            w.F32(low);
            for (int i = 0; i < 256; i++)
            {
                if (netDensity[i] == 0 || !float.IsFinite(netHeights[i])) continue;
                w.U8(netDensity[i]);
                w.U8(netKinds[i]);
                w.U16((ushort)Mathf.Clamp(Mathf.RoundToInt(netHeights[i] - low), 0, 65535));
            }
        }

        /// The critters near any player, for the clients to draw and aim at.
        void SendCritters()
        {
            uint total = Native.sms_goop_critters(world, netCritters, MaxCritters);
            int n = (int)Math.Min(total, (uint)MaxCritters);
            var w = new ByteWriter(16 + n * 44);
            w.U8(Protocol);
            int countAt = w.Length;
            w.U8(0);
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var c = netCritters[i];
                if (!NearAPlayer(UnityHost.ToUnity(c.pos))) continue;
                w.U32((uint)(c.id >> 32));
                w.U32((uint)c.id);
                w.F32(c.pos.x); w.F32(c.pos.y); w.F32(c.pos.z);
                w.F32(c.yaw);
                w.F32(c.scale);
                w.F32(c.lean);
                w.U32(c.clip_time);
                w.U8(c.kind);
                w.U8(c.clip);
                w.U8(c.state);
                count++;
            }
            // An empty list is sent too: the last critter went.
            if (count == 0 && sentNoCritters) return;
            sentNoCritters = count == 0;
            var bytes = w.ToArray();
            bytes[countAt] = (byte)count;
            Mp.SendToAll(MarioNet.GoopCrittersMsg, bytes, reliable: false);
        }

        bool sentNoCritters;

        static bool NearAPlayer(Vector3 p)
        {
            foreach (var b in CharacterBody.readOnlyInstancesList)
                if (b && b.isPlayerControlled && (b.footPosition - p).sqrMagnitude < NetRange * NetRange) return true;
            return false;
        }

        /// A client's mirror's changes, redone on the server's world. Its Bowser Jr.'s shots are his.
        void OnOps(NetworkConnection from, ByteReader r)
        {
            if (from == null || !ready || r.U8() != Protocol) return;
            uint netId = r.U32();
            int n = r.U16();
            if (r.Failed || n <= 0 || n > MaxOps) return;
            var list = new Native.GoopOp[n];
            for (int i = 0; i < n; i++) list[i] = r.Struct<Native.GoopOp>();
            if (r.Failed) return;
            var go = Mp.Find(netId);
            var body = go ? go.GetComponent<CharacterBody>() : null;
            // Only another player's Mario or Bowser Jr. (the host's own sims paint the world itself).
            if (!body || body.hasEffectiveAuthority || !body.GetComponent<MarioDriver>()) return;
            ulong owner = KoopaJrBody.Is(body) ? KoopaJrBody.IdFor(body) : 0;
            Native.sms_goop_apply_ops(world, ref host, list, (uint)n, owner);
            // What redoing them set off: played here, and on the machines but the sender's
            // (it already had its own).
            var jr = jrs.Count > 0 ? jrs[0] : body;
            uint e;
            while ((e = Native.sms_goop_events(world, events, (uint)events.Length)) > 0)
            {
                for (int i = 0; i < e; i++) Handle(events[i], jr);
                if (e < events.Length) break;
            }
            var fx = TakeFx();
            if (fx != null) Mp.SendToAllExcept(MarioNet.GoopFxMsg, fx, from);
        }

        /// A client's mirror is new (its first Bowser Jr., or a new stage): send it everything.
        void OnHello(ByteReader r)
        {
            if (r.U8() != Protocol) return;
            sentChunks.Clear();
            sentNoCritters = false;
        }

        // ---- a client ----

        void NetClient()
        {
            if (!helloSent)
            {
                helloSent = true;
                var hello = new ByteWriter(4);
                hello.U8(Protocol);
                Mp.SendToServer(MarioNet.GoopHelloMsg, hello.ToArray());
            }
            float now = Time.time;
            // What this machine's sims did to the world: to the server, holding the chunks it touched.
            uint n = Native.sms_goop_take_ops(world, ops, MaxOps);
            var player = MarioDriver.Player;
            var body = player != null ? player.GetComponent<CharacterBody>() : null;
            if (n > 0 && body && body.netId.Value != 0)
            {
                var w = new ByteWriter(16 + (int)n * 48);
                w.U8(Protocol);
                w.U32(body.netId.Value);
                w.U16((ushort)n);
                for (int i = 0; i < n; i++)
                {
                    w.Struct(ops[i]);
                    Hold(ops[i], now);
                }
                Mp.SendToServer(MarioNet.GoopOpsMsg, w.ToArray());
            }
            // Its own sims' sounds and bursts (the critters are the server's, and send their own).
            uint e;
            while ((e = Native.sms_goop_events(world, events, (uint)events.Length)) > 0)
            {
                for (int i = 0; i < e; i++) Handle(events[i], null);
                if (e < events.Length) break;
            }
            var fx = TakeFx();
            if (fx != null) Mp.SendToServer(MarioNet.GoopFxMsg, fx);
            // The server's copies of chunks no longer held.
            released.Clear();
            foreach (var kv in heldUntil)
                if (kv.Value <= now) released.Add(kv.Key);
            foreach (var key in released)
            {
                heldUntil.Remove(key);
                if (deferred.TryGetValue(key, out var data))
                {
                    deferred.Remove(key);
                    ApplyChunk(key, data);
                }
            }
        }

        void Hold(in Native.GoopOp op, float now)
        {
            float reach;
            switch (op.kind)
            {
                case Native.GoopOpKind.Stamp: case Native.GoopOpKind.Clean: reach = op.a; break;
                case Native.GoopOpKind.Sign: reach = op.a * 1.4143f; break;
                default: return;
            }
            if (!(reach > 0f) || reach > 4096f) return;
            int x0 = Mathf.FloorToInt((op.pos.x - reach) / ChunkUnits), x1 = Mathf.FloorToInt((op.pos.x + reach) / ChunkUnits);
            int z0 = Mathf.FloorToInt((op.pos.z - reach) / ChunkUnits), z1 = Mathf.FloorToInt((op.pos.z + reach) / ChunkUnits);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    heldUntil[(x, z)] = now + HoldSeconds;
        }

        void OnChunks(ByteReader r)
        {
            if (!ready || r.U8() != Protocol) return;
            int n = r.U16();
            for (int c = 0; c < n; c++)
            {
                var key = (r.I32(), r.I32());
                byte flag = r.U8();
                byte[] data = null;
                if (flag == 1)
                {
                    var bits = r.Bytes(32);
                    float low = r.F32();
                    if (r.Failed || bits == null) return;
                    int live = 0;
                    for (int i = 0; i < 256; i++)
                        if ((bits[i >> 3] & (1 << (i & 7))) != 0) live++;
                    data = new byte[32 + 4 + live * 4];
                    Buffer.BlockCopy(bits, 0, data, 0, 32);
                    Buffer.BlockCopy(BitConverter.GetBytes(low), 0, data, 32, 4);
                    for (int i = 0; i < live; i++)
                    {
                        data[36 + i * 4] = r.U8();
                        data[37 + i * 4] = r.U8();
                        ushort dh = r.U16();
                        data[38 + i * 4] = (byte)dh;
                        data[39 + i * 4] = (byte)(dh >> 8);
                    }
                }
                if (r.Failed) return;
                if (heldUntil.ContainsKey(key)) deferred[key] = data;
                else ApplyChunk(key, data);
            }
        }

        /// The server's copy of a chunk (null: gone) into the mirror.
        void ApplyChunk((int, int) key, byte[] data)
        {
            if (data == null)
            {
                Native.sms_goop_chunk_drop(world, key.Item1, key.Item2);
                return;
            }
            float low = BitConverter.ToSingle(data, 32);
            int at = 36;
            for (int i = 0; i < 256; i++)
            {
                if ((data[i >> 3] & (1 << (i & 7))) == 0)
                {
                    netDensity[i] = 0;
                    netKinds[i] = 0;
                    netHeights[i] = 0f;
                    continue;
                }
                netDensity[i] = data[at];
                netKinds[i] = data[at + 1];
                netHeights[i] = low + (data[at + 2] | (data[at + 3] << 8));
                at += 4;
            }
            Native.sms_goop_chunk_set(world, key.Item1, key.Item2, netDensity, netKinds, netHeights);
        }

        void OnCritters(ByteReader r)
        {
            if (!ready || r.U8() != Protocol) return;
            int n = Math.Min((int)r.U8(), MaxCritters);
            var list = new Native.Critter[n];
            for (int i = 0; i < n; i++)
            {
                ulong id = ((ulong)r.U32() << 32) | r.U32();
                list[i] = new Native.Critter
                {
                    id = id,
                    pos = new Native.Vec3(r.F32(), r.F32(), r.F32()),
                    yaw = r.F32(),
                    scale = r.F32(),
                    lean = r.F32(),
                    clip_time = r.U32(),
                    kind = r.U8(),
                    clip = r.U8(),
                    state = r.U8(),
                    squash = 1f,
                };
            }
            if (r.Failed) return;
            Native.sms_goop_mirror_critters(world, list, (uint)n);
            snapAt = Time.time;
        }

        /// A client draws the server's critters eased toward where they were last sent, their
        /// clips run on since.
        void Smooth(ref Native.Critter c)
        {
            if (NetworkServer.active) return;
            var target = UnityHost.ToUnity(c.pos);
            float k = 1f - Mathf.Exp(-15f * Time.deltaTime);
            var at = shown.TryGetValue(c.id, out var was) && (was - target).sqrMagnitude < 9f
                ? Vector3.Lerp(was, target, k) : target;
            shown[c.id] = at;
            c.pos = UnityHost.ToSim(at);
            c.clip_time += (uint)Mathf.Clamp((Time.time - snapAt) * Native.sms_tick_hz(), 0f, 60f);
        }
    }
}
