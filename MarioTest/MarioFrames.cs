using System;
using System.Collections.Generic;
using Ror2Common;

namespace MarioTest
{
    // The wire format of a player's Mario, for the others to puppet him. The player's machine runs
    // the real sim; every call it makes on it for a physics step (the pad and camera for the step
    // itself, the aim, tuning and limits, a launch from a jump pad, a knock-down, a refill) is
    // recorded as an op, in order, with where the sim ended up. Everyone else runs a shadow sim
    // (MarioPuppet) and replays the same ops, so every move, FLUDD's water, the dust and the
    // sounds come out of libsms as they did for the player, instead of being sent as animation
    // state. The recorded position only corrects drift: the shadow sim doesn't see the same enemies.
    // No Unity types here, so the codec is checked outside the game (tests/NetCheck).

    internal enum Op : byte
    {
        Teleport = 1, Tuning = 2, Refill = 3, AddWater = 4, Limits = 5, BoostJump = 6, Launch = 7,
        LaunchPad = 8, KnockDown = 9, SetAim = 10, Down = 11, Step = 12, ShineGet = 13, ShineEnd = 14,
        ForeignWet = 15,
    }

    /// What replaying a frame does to a sim; MarioPuppet's implementation calls libsms.
    internal interface IFrameSink
    {
        void Teleport(Native.Vec3 pos);
        void Tuning(in Native.Tuning tuning);
        void Refill();
        void AddWater(float share);
        void Limits(in Native.Limits limits);
        void BoostJump(float factor);
        void Launch(Native.Vec3 vel);
        void LaunchPad(Native.Vec3 vel, float gravity);
        void KnockDown(Native.Vec3 from, byte kind);
        void SetAim(bool on, short yaw, float pitch);
        void Down();
        void ShineGet(Native.Vec3 pos, short yaw);
        void ShineEnd();
        /// Whether the player's sim counted the ground as wetted by another Mario's water.
        void ForeignWet(bool wet);
        void Step(in Native.Pad pad, short cameraYaw, float dt);
    }

    internal sealed class Frame
    {
        public uint seq;
        public byte[] ops;
        /// Where the player's sim had Mario's feet after the step.
        public Native.Vec3 pos;
    }

    internal static class FrameCodec
    {
        /// Bumped whenever the wire format changes; a batch with another number is ignored.
        public const byte Protocol = 3;

        static void Vec(ByteWriter w, Native.Vec3 v) { w.F32(v.x); w.F32(v.y); w.F32(v.z); }
        static Native.Vec3 Vec(ByteReader r) => new Native.Vec3(r.F32(), r.F32(), r.F32());

        public static void WriteTeleport(ByteWriter w, Native.Vec3 p) { w.U8((byte)Op.Teleport); Vec(w, p); }
        public static void WriteTuning(ByteWriter w, in Native.Tuning t) { w.U8((byte)Op.Tuning); w.Struct(t); }
        public static void WriteRefill(ByteWriter w) => w.U8((byte)Op.Refill);
        public static void WriteAddWater(ByteWriter w, float share) { w.U8((byte)Op.AddWater); w.F32(share); }
        public static void WriteLimits(ByteWriter w, in Native.Limits l) { w.U8((byte)Op.Limits); w.Struct(l); }
        public static void WriteBoostJump(ByteWriter w, float f) { w.U8((byte)Op.BoostJump); w.F32(f); }
        public static void WriteLaunch(ByteWriter w, Native.Vec3 v) { w.U8((byte)Op.Launch); Vec(w, v); }
        public static void WriteLaunchPad(ByteWriter w, Native.Vec3 v, float g) { w.U8((byte)Op.LaunchPad); Vec(w, v); w.F32(g); }
        public static void WriteKnockDown(ByteWriter w, Native.Vec3 from, byte kind) { w.U8((byte)Op.KnockDown); Vec(w, from); w.U8(kind); }
        public static void WriteSetAim(ByteWriter w, bool on, short yaw, float pitch) { w.U8((byte)Op.SetAim); w.Bool(on); w.I16(yaw); w.F32(pitch); }
        public static void WriteDown(ByteWriter w) => w.U8((byte)Op.Down);
        public static void WriteShineGet(ByteWriter w, Native.Vec3 pos, short yaw) { w.U8((byte)Op.ShineGet); Vec(w, pos); w.I16(yaw); }
        public static void WriteShineEnd(ByteWriter w) => w.U8((byte)Op.ShineEnd);
        public static void WriteForeignWet(ByteWriter w, bool wet) { w.U8((byte)Op.ForeignWet); w.Bool(wet); }

        public static void WriteStep(ByteWriter w, in Native.Pad pad, short cameraYaw, float dt)
        {
            w.U8((byte)Op.Step);
            w.Struct(pad);
            w.I16(cameraYaw);
            w.F32(dt);
        }

        /// Runs one frame's ops on `sink`, in order. False if the frame is corrupt.
        public static bool Replay(Frame frame, IFrameSink sink)
        {
            var r = new ByteReader(frame.ops);
            while (!r.Done)
            {
                switch ((Op)r.U8())
                {
                    case Op.Teleport: sink.Teleport(Vec(r)); break;
                    case Op.Tuning: { var t = r.Struct<Native.Tuning>(); if (!r.Failed) sink.Tuning(t); break; }
                    case Op.Refill: sink.Refill(); break;
                    case Op.AddWater: sink.AddWater(r.F32()); break;
                    case Op.Limits: { var l = r.Struct<Native.Limits>(); if (!r.Failed) sink.Limits(l); break; }
                    case Op.BoostJump: sink.BoostJump(r.F32()); break;
                    case Op.Launch: sink.Launch(Vec(r)); break;
                    case Op.LaunchPad: { var v = Vec(r); sink.LaunchPad(v, r.F32()); break; }
                    case Op.KnockDown: { var v = Vec(r); sink.KnockDown(v, r.U8()); break; }
                    case Op.SetAim: { bool on = r.Bool(); short yaw = r.I16(); sink.SetAim(on, yaw, r.F32()); break; }
                    case Op.Down: sink.Down(); break;
                    case Op.ShineGet: { var v = Vec(r); sink.ShineGet(v, r.I16()); break; }
                    case Op.ShineEnd: sink.ShineEnd(); break;
                    case Op.ForeignWet: sink.ForeignWet(r.Bool()); break;
                    case Op.Step:
                    {
                        var pad = r.Struct<Native.Pad>();
                        short cam = r.I16();
                        float dt = r.F32();
                        if (!r.Failed) sink.Step(pad, cam, dt);
                        break;
                    }
                    default: return false;
                }
                if (r.Failed) return false;
            }
            return true;
        }

        /// A batch of frames from one body: protocol, ABI, the body's network id, flags (bit 0: a
        /// Mario no player controls), then the frames.
        public static byte[] WriteBatch(uint netId, bool clone, List<Frame> frames)
        {
            var w = new ByteWriter(64 + frames.Count * 48);
            w.U8(Protocol);
            w.U8((byte)Native.AbiVersion);
            w.U32(netId);
            w.U8((byte)(clone ? 1 : 0));
            w.U8((byte)frames.Count);
            foreach (var f in frames)
            {
                w.U32(f.seq);
                w.U16((ushort)f.ops.Length);
                w.Bytes(f.ops, 0, f.ops.Length);
                Vec(w, f.pos);
            }
            return w.ToArray();
        }

        public enum BatchStatus { Ok, Corrupt, WrongProtocol, WrongAbi }

        public static BatchStatus ReadBatch(ByteReader r, out uint netId, out bool clone, List<Frame> frames, out byte theirs)
        {
            var status = ReadHeader(r, out netId, out clone, out int count, out theirs);
            return status == BatchStatus.Ok ? ReadFrames(r, count, frames) : status;
        }

        // Inspect the owner before allocating frames: the relay echoes our own movement too.
        // Kept separate from the body decoder without changing the wire format.
        public static BatchStatus ReadHeader(ByteReader r, out uint netId, out bool clone, out int count, out byte theirs)
        {
            netId = 0;
            clone = false;
            count = 0;
            theirs = r.U8();
            byte abi = r.U8();
            if (r.Failed) return BatchStatus.Corrupt;
            if (theirs != Protocol) return BatchStatus.WrongProtocol;
            if (abi != (byte)Native.AbiVersion) { theirs = abi; return BatchStatus.WrongAbi; }
            netId = r.U32();
            clone = (r.U8() & 1) != 0;
            count = r.U8();
            if (r.Failed) return BatchStatus.Corrupt;
            return BatchStatus.Ok;
        }

        public static BatchStatus ReadFrames(ByteReader r, int count, List<Frame> frames)
        {
            for (int i = 0; i < count; i++)
            {
                var f = new Frame { seq = r.U32() };
                int n = r.U16();
                if (r.Failed || r.Remaining < n + 12) return BatchStatus.Corrupt;
                f.ops = new byte[n];
                for (int k = 0; k < n; k++) f.ops[k] = r.U8();
                f.pos = Vec(r);
                if (r.Failed) return BatchStatus.Corrupt;
                frames.Add(f);
            }
            return BatchStatus.Ok;
        }
    }

    /// The player's side: builds one frame per physics step from the calls made on the sim, and
    /// batches them for sending. Calls dedupe the state the puppet already has (limits and aim go
    /// out only when they change), until `Refresh` makes the next step resend everything.
    internal sealed class FrameRecorder
    {
        readonly ByteWriter ops = new ByteWriter(128);
        readonly List<Frame> batch = new List<Frame>();
        uint seq;
        bool haveLimits, haveAim, haveWet, lastWet;
        Native.Limits lastLimits;
        bool lastAimOn;
        short lastAimYaw;
        float lastAimPitch;

        public int Pending => batch.Count;
        public uint Seq => seq;

        public void Begin() => ops.Clear();

        /// Forget what the puppet has, so tuning, limits and aim are all sent again (a
        /// periodic refresh covers a puppet that joined late).
        public void Refresh() => haveLimits = haveAim = haveWet = false;

        public void Teleport(Native.Vec3 p) => FrameCodec.WriteTeleport(ops, p);
        public void Tuning(in Native.Tuning t) => FrameCodec.WriteTuning(ops, t);
        public void Refill() => FrameCodec.WriteRefill(ops);
        public void AddWater(float share) => FrameCodec.WriteAddWater(ops, share);
        public void BoostJump(float f) => FrameCodec.WriteBoostJump(ops, f);
        public void Launch(Native.Vec3 v) => FrameCodec.WriteLaunch(ops, v);
        public void LaunchPad(Native.Vec3 v, float g) => FrameCodec.WriteLaunchPad(ops, v, g);
        public void KnockDown(Native.Vec3 from, byte kind) => FrameCodec.WriteKnockDown(ops, from, kind);
        public void Down() => FrameCodec.WriteDown(ops);
        public void ShineGet(Native.Vec3 pos, short yaw) => FrameCodec.WriteShineGet(ops, pos, yaw);
        public void ShineEnd() => FrameCodec.WriteShineEnd(ops);
        public void Step(in Native.Pad pad, short cameraYaw, float dt) => FrameCodec.WriteStep(ops, pad, cameraYaw, dt);

        public void Limits(in Native.Limits l)
        {
            if (haveLimits && l.Equals(lastLimits)) return;
            haveLimits = true;
            lastLimits = l;
            FrameCodec.WriteLimits(ops, l);
        }

        /// What the player's sim was told about other Marios' water, so the shadow sim uses the same and
        /// a slide on a friend's wet spot looks the same on every machine.
        public void ForeignWet(bool wet)
        {
            if (haveWet && wet == lastWet) return;
            haveWet = true;
            lastWet = wet;
            FrameCodec.WriteForeignWet(ops, wet);
        }

        public void SetAim(bool on, short yaw, float pitch)
        {
            if (haveAim && on == lastAimOn && yaw == lastAimYaw && pitch == lastAimPitch) return;
            haveAim = true;
            lastAimOn = on;
            lastAimYaw = yaw;
            lastAimPitch = pitch;
            FrameCodec.WriteSetAim(ops, on, yaw, pitch);
        }

        /// Finishes the step: the frame with where the sim has Mario's feet now.
        public void End(Native.Vec3 pos)
        {
            batch.Add(new Frame { seq = seq++, ops = ops.ToArray(), pos = pos });
            ops.Clear();
        }

        /// The frames since the last call, as a message payload, or null if there are none.
        public byte[] Take(uint netId, bool clone)
        {
            if (batch.Count == 0) return null;
            // At most 255 per message; the driver sends long before that.
            var bytes = FrameCodec.WriteBatch(netId, clone, batch.Count > 255 ? batch.GetRange(0, 255) : batch);
            if (batch.Count > 255) batch.RemoveRange(0, 255);
            else batch.Clear();
            return bytes;
        }
    }

    /// The other side: frames in sequence order, however they arrived.
    internal sealed class FrameQueue
    {
        internal const int Prebuffer = 3;
        /// A backlog this deep (about 1.8 s of Mario) is replayed in one go whatever it costs:
        /// a long hitch beats a Mario who is seconds behind and stays there.
        internal const int HardBacklog = 90;
        internal const int MinCatchUpSteps = 4;
        internal const double CatchUpBudgetMs = 6.0;

        /// How many frames to replay this update. Every op is replayed in order, never skipped;
        /// a backlog is replayed down to the prebuffer.
        internal static int PlaybackSteps(int waiting) => waiting > 30
            ? waiting - Prebuffer : waiting > 8 ? 2 : 1;

        /// Whether to replay another frame after `done` of `planned` this update. A modest
        /// backlog is spread over updates by a time budget (so a hitch is not repaid with a
        /// bigger one) but never starves: a few steps always run, and a deep backlog runs fully.
        internal static bool KeepReplaying(int waitingNow, int done, int planned, double elapsedMs) =>
            done < planned && waitingNow > 0
            && (done < MinCatchUpSteps || waitingNow > HardBacklog || elapsedMs < CatchUpBudgetMs);

        readonly SortedDictionary<uint, Frame> waiting = new SortedDictionary<uint, Frame>();
        uint next;
        bool started;

        public int Count => waiting.Count;
        public bool Clone { get; set; }

        public void Add(Frame f)
        {
            if (started && f.seq < next) return;
            if (!started)
            {
                started = true;
                next = f.seq;
            }
            waiting[f.seq] = f;
            // A puppet that never plays them (or a stream that stopped) mustn't grow without bound.
            while (waiting.Count > 600)
            {
                var e = waiting.GetEnumerator();
                e.MoveNext();
                waiting.Remove(e.Current.Key);
            }
        }

        /// The next frame in sequence, if it has arrived.
        public bool TryTake(out Frame f)
        {
            if (waiting.TryGetValue(next, out f))
            {
                waiting.Remove(next);
                next++;
                return true;
            }
            // A gap that stays (an unordered channel gave up on one): skip to what is there.
            if (waiting.Count > 30)
            {
                var e = waiting.GetEnumerator();
                e.MoveNext();
                next = e.Current.Key;
                return TryTake(out f);
            }
            f = null;
            return false;
        }

        public void Clear()
        {
            waiting.Clear();
            started = false;
            next = 0;
        }
    }
}
