using System;
using System.Collections.Generic;
using MarioTest;
using Ror2Common;

// Checks the wire format of Mario's multiplayer frames (MarioFrames) outside the game.
static class Program
{
    sealed class Log : IFrameSink
    {
        public readonly List<string> calls = new List<string>();
        public void Teleport(Native.Vec3 p) => calls.Add($"teleport {p.x},{p.y},{p.z}");
        public void Tuning(in Native.Tuning t) => calls.Add($"tuning {t.jump_height},{t.unlimited_water}");
        public void Refill() => calls.Add("refill");
        public void AddWater(float s) => calls.Add($"water {s}");
        public void Limits(in Native.Limits l) => calls.Add($"limits {l.air_extra},{l.wet_slide_seconds}");
        public void BoostJump(float f) => calls.Add($"boost {f}");
        public void Launch(Native.Vec3 v) => calls.Add($"launch {v.x},{v.y},{v.z}");
        public void LaunchPad(Native.Vec3 v, float g) => calls.Add($"pad {v.x},{v.y},{v.z},{g}");
        public void KnockDown(Native.Vec3 f, byte k) => calls.Add($"knock {f.x},{f.y},{f.z},{k}");
        public void SetAim(bool on, short yaw, float pitch) => calls.Add($"aim {on},{yaw},{pitch}");
        public void Down() => calls.Add("down");
        public void Step(in Native.Pad pad, short cam, float dt) => calls.Add($"step a{pad.a} r{pad.r_analog} mx{pad.move_x} cam{cam}");
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static void Main()
    {
        var rec = new FrameRecorder();
        // Frame 0: everything once, in a particular order.
        rec.Begin();
        rec.Teleport(new Native.Vec3(1, 2, 3));
        rec.Tuning(new Native.Tuning { jump_height = 1.6f, unlimited_water = 1 });
        rec.Refill();
        rec.AddWater(0.25f);
        rec.Limits(new Native.Limits { air_extra = 2, wet_slide_seconds = 1.5f });
        rec.SetAim(true, 1234, 0.5f);
        rec.Launch(new Native.Vec3(4, 5, 6));
        rec.LaunchPad(new Native.Vec3(7, 8, 9), 0.01f);
        rec.KnockDown(new Native.Vec3(1, 1, 1), 0);
        rec.Step(new Native.Pad { a = 1, r_analog = 200, move_x = 0.5f, use_move = 1 }, -300, 1f / 60f);
        rec.BoostJump(1.2f);
        rec.End(new Native.Vec3(10, 20, 30));
        // Frame 1: unchanged limits and aim are deduped, only the step goes out.
        rec.Begin();
        rec.Limits(new Native.Limits { air_extra = 2, wet_slide_seconds = 1.5f });
        rec.SetAim(true, 1234, 0.5f);
        rec.Step(new Native.Pad { b = 1 }, 5, 1f / 60f);
        rec.End(new Native.Vec3(11, 20, 30));
        // Frame 2: a changed aim goes out.
        rec.Begin();
        rec.SetAim(false, 0, 0f);
        rec.Step(new Native.Pad(), 0, 1f / 60f);
        rec.End(new Native.Vec3(12, 20, 30));
        // Frame 3: after Refresh the limits go out again.
        rec.Refresh();
        rec.Begin();
        rec.Limits(new Native.Limits { air_extra = 2, wet_slide_seconds = 1.5f });
        rec.Step(new Native.Pad(), 0, 1f / 60f);
        rec.End(new Native.Vec3(13, 20, 30));
        Check(rec.Pending == 4, "four frames pending");

        var bytes = rec.Take(77, true);
        Check(bytes != null && rec.Pending == 0 && rec.Take(77, true) == null, "take empties");
        var frames = new List<Frame>();
        var status = FrameCodec.ReadBatch(new ByteReader(bytes), out uint id, out bool clone, frames, out _);
        Check(status == FrameCodec.BatchStatus.Ok && id == 77 && clone && frames.Count == 4, "batch header");
        Check(frames[0].seq == 0 && frames[3].seq == 3 && frames[2].pos.x == 12f, "seq and position");

        var log = new Log();
        Check(FrameCodec.Replay(frames[0], log), "replay frame 0");
        string[] expect0 =
        {
            "teleport 1,2,3", "tuning 1.6,1", "refill", "water 0.25", "limits 2,1.5", "aim True,1234,0.5",
            "launch 4,5,6", "pad 7,8,9,0.01", "knock 1,1,1,0", "step a1 r200 mx0.5 cam-300", "boost 1.2",
        };
        Check(log.calls.Count == expect0.Length, $"frame 0 has {log.calls.Count} calls");
        for (int i = 0; i < expect0.Length; i++) Check(log.calls[i] == expect0[i], $"call {i}: {log.calls[i]} != {expect0[i]}");

        log.calls.Clear();
        Check(FrameCodec.Replay(frames[1], log) && log.calls.Count == 1 && log.calls[0].StartsWith("step"), "frame 1 is only the step (limits and aim deduped)");
        log.calls.Clear();
        FrameCodec.Replay(frames[2], log);
        Check(log.calls.Count == 2 && log.calls[0] == "aim False,0,0", "frame 2 has the changed aim");
        log.calls.Clear();
        FrameCodec.Replay(frames[3], log);
        Check(log.calls.Count == 2 && log.calls[0] == "limits 2,1.5", "refresh resends limits");

        // Wrong protocol, wrong ABI and truncation are reported, not thrown.
        var wrong = (byte[])bytes.Clone();
        wrong[0] = 99;
        Check(FrameCodec.ReadBatch(new ByteReader(wrong), out _, out _, new List<Frame>(), out byte got) == FrameCodec.BatchStatus.WrongProtocol && got == 99, "protocol");
        wrong = (byte[])bytes.Clone();
        wrong[1] = 7;
        Check(FrameCodec.ReadBatch(new ByteReader(wrong), out _, out _, new List<Frame>(), out got) == FrameCodec.BatchStatus.WrongAbi && got == 7, "abi");
        for (int cut = 0; cut < bytes.Length; cut += 7)
        {
            var part = new byte[cut];
            Array.Copy(bytes, part, cut);
            var s = FrameCodec.ReadBatch(new ByteReader(part), out _, out _, new List<Frame>(), out _);
            Check(s == FrameCodec.BatchStatus.Corrupt, $"truncated at {cut} is corrupt, got {s}");
        }
        // A garbage op fails the replay without calling anything after it.
        var bad = new Frame { ops = new byte[] { (byte)Op.Refill, 250, (byte)Op.Refill } };
        log.calls.Clear();
        Check(!FrameCodec.Replay(bad, log) && log.calls.Count == 1, "unknown op stops the replay");

        // The queue plays in order whatever the arrival order, drops repeats, and skips a gap that stays.
        var q = new FrameQueue();
        foreach (uint s in new uint[] { 5, 7, 6, 5, 8 }) q.Add(new Frame { seq = s, ops = new byte[0] });
        var order = new List<uint>();
        while (q.TryTake(out var f)) order.Add(f.seq);
        Check(string.Join(",", order) == "5,6,7,8", "in order: " + string.Join(",", order));
        q.Add(new Frame { seq = 3, ops = new byte[0] });
        Check(!q.TryTake(out _), "an old frame is ignored");
        var gap = new FrameQueue();
        gap.Add(new Frame { seq = 0, ops = new byte[0] });
        Check(gap.TryTake(out _), "first");
        for (uint s = 2; s < 40; s++) gap.Add(new Frame { seq = s, ops = new byte[0] });
        Check(gap.TryTake(out var after) && after.seq == 2, "a gap that stays is skipped");
        Console.WriteLine("NetCheck: ok");
    }
}
