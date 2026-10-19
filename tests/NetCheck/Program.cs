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
        public void ShineGet(Native.Vec3 p, short yaw) => calls.Add($"shine {p.x},{p.y},{p.z},{yaw}");
        public void ShineEnd() => calls.Add("shine end");
        public void ForeignWet(bool wet) => calls.Add($"wet {wet}");
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
        rec.ForeignWet(true);
        rec.Step(new Native.Pad { a = 1, r_analog = 200, move_x = 0.5f, use_move = 1 }, -300, 1f / 60f);
        rec.BoostJump(1.2f);
        rec.ShineGet(new Native.Vec3(5, 6, 7), -1234);
        rec.ShineEnd();
        rec.End(new Native.Vec3(10, 20, 30));
        // Frame 1: unchanged limits and aim are deduped, only the step goes out.
        rec.Begin();
        rec.Limits(new Native.Limits { air_extra = 2, wet_slide_seconds = 1.5f });
        rec.SetAim(true, 1234, 0.5f);
        rec.ForeignWet(true);
        rec.Step(new Native.Pad { b = 1 }, 5, 1f / 60f);
        rec.End(new Native.Vec3(11, 20, 30));
        // Frame 2: a changed aim goes out.
        rec.Begin();
        rec.SetAim(false, 0, 0f);
        rec.ForeignWet(false);
        rec.Step(new Native.Pad(), 0, 1f / 60f);
        rec.End(new Native.Vec3(12, 20, 30));
        // Frame 3: after Refresh the limits go out again.
        rec.Refresh();
        rec.Begin();
        rec.Limits(new Native.Limits { air_extra = 2, wet_slide_seconds = 1.5f });
        rec.ForeignWet(false);
        rec.Step(new Native.Pad(), 0, 1f / 60f);
        rec.End(new Native.Vec3(13, 20, 30));
        Check(rec.Pending == 4, "four frames pending");

        var bytes = rec.Take(77, true);
        Check(bytes != null && rec.Pending == 0 && rec.Take(77, true) == null, "take empties");
        var frames = new List<Frame>();
        var status = FrameCodec.ReadBatch(new ByteReader(bytes), out uint id, out bool clone, frames, out _);
        Check(status == FrameCodec.BatchStatus.Ok && id == 77 && clone && frames.Count == 4, "batch header");
        Check(frames[0].seq == 0 && frames[3].seq == 3 && frames[2].pos.x == 12f, "seq and position");

        // The owner can reject an echo after the fixed-size header, without decoding any ops.
        var header = new ByteReader(bytes);
        Check(FrameCodec.ReadHeader(header, out uint headerId, out bool headerClone, out int count, out _) == FrameCodec.BatchStatus.Ok
            && headerId == 77 && headerClone && count == 4 && header.Remaining == bytes.Length - 8, "header only");
        var splitFrames = new List<Frame>();
        Check(FrameCodec.ReadFrames(header, count, splitFrames) == FrameCodec.BatchStatus.Ok && header.Done
            && splitFrames.Count == frames.Count, "split decoding consumes the same packet");
        for (int i = 0; i < frames.Count; i++)
            Check(splitFrames[i].seq == frames[i].seq && Convert.ToBase64String(splitFrames[i].ops) == Convert.ToBase64String(frames[i].ops), "split ops preserved");
        for (int cut = 0; cut < 8; cut++)
            Check(FrameCodec.ReadHeader(new ByteReader(bytes, 0, cut), out _, out _, out _, out _) == FrameCodec.BatchStatus.Corrupt, "short header");

        var log = new Log();
        Check(FrameCodec.Replay(frames[0], log), "replay frame 0");
        string[] expect0 =
        {
            "teleport 1,2,3", "tuning 1.6,1", "refill", "water 0.25", "limits 2,1.5", "aim True,1234,0.5",
            "launch 4,5,6", "pad 7,8,9,0.01", "knock 1,1,1,0", "wet True", "step a1 r200 mx0.5 cam-300", "boost 1.2",
            "shine 5,6,7,-1234", "shine end",
        };
        Check(log.calls.Count == expect0.Length, $"frame 0 has {log.calls.Count} calls");
        for (int i = 0; i < expect0.Length; i++) Check(log.calls[i] == expect0[i], $"call {i}: {log.calls[i]} != {expect0[i]}");

        log.calls.Clear();
        Check(FrameCodec.Replay(frames[1], log) && log.calls.Count == 1 && log.calls[0].StartsWith("step"), "frame 1 is only the step (limits and aim deduped)");
        log.calls.Clear();
        FrameCodec.Replay(frames[2], log);
        Check(log.calls.Count == 3 && log.calls[0] == "aim False,0,0" && log.calls[1] == "wet False", "frame 2 has the changed aim and wet state");
        log.calls.Clear();
        FrameCodec.Replay(frames[3], log);
        Check(log.calls.Count == 3 && log.calls[0] == "limits 2,1.5" && log.calls[1] == "wet False", "refresh resends limits and the wet state");

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

        // A full queue must not turn one stall into hundreds of synchronous sim steps.
        Check(FrameQueue.PlaybackSteps(3) == 1 && FrameQueue.PlaybackSteps(8) == 1
            && FrameQueue.PlaybackSteps(9) == 2 && FrameQueue.PlaybackSteps(30) == 2, "normal playback unchanged");
        Check(FrameQueue.PlaybackSteps(600) == 4, "deep queues have a hard step bound");
        foreach (int depth in new[] { 50, 200, 600 })
        {
            Check(FrameQueue.KeepReplaying(depth, 0, 4, 99), "one indivisible step makes progress");
            Check(!FrameQueue.KeepReplaying(depth, 1, 4, 8), "expensive first step exhausts budget");
            Check(FrameQueue.KeepReplaying(depth, 3, 4, 2), "cheap work can catch up");
            Check(!FrameQueue.KeepReplaying(depth, 4, 600, 0), "step cap holds even with spare time");
        }
        var stalled = new FrameQueue();
        for (uint s = 0; s < 600; s++) stalled.Add(new Frame { seq = s, ops = frames[0].ops });
        uint expected = 0;
        int updates = 0;
        while (stalled.Count > 0)
        {
            int plan = FrameQueue.PlaybackSteps(stalled.Count);
            double elapsed = 0;
            int done = 0;
            while (FrameQueue.KeepReplaying(stalled.Count, done, plan, elapsed) && stalled.TryTake(out var next))
            {
                Check(next.seq == expected++, "catch-up keeps every frame in order");
                log.calls.Clear();
                Check(FrameCodec.Replay(next, log) && log.calls.Count == expect0.Length, "catch-up keeps every op");
                elapsed += 8;
                done++;
            }
            Check(done == 1, "an 8 ms sim step never starts a catch-up burst");
            updates++;
        }
        Check(expected == 600 && updates == 600, "all queued operations eventually replay");
        stalled = new FrameQueue();
        for (uint s = 0; s < 600; s++) stalled.Add(new Frame { seq = s, ops = frames[0].ops });
        while (stalled.Count > 0) { while (stalled.TryTake(out var drain)) { } }
        stalled.Clear();
        stalled.Add(new Frame { seq = 0, ops = Array.Empty<byte>() });
        Check(stalled.TryTake(out var restarted) && restarted.seq == 0, "cleared queue can restart sequence");
        Console.WriteLine("NetCheck: ok");
    }
}
