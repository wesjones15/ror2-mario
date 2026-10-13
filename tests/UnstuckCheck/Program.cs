using System;
using MarioTest;
using UnityEngine;

static class Program
{
    const float Height = 1.6f;
    static readonly Vector3 Origin = Vector3.zero;
    static MarioUnstuck.GroundProbe probe = Floor;
    static int searches;

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static bool Floor(Vector3 sample, float rise, float drop, out Vector3 feet)
    {
        searches++;
        feet = new Vector3(sample.x, 0.03f, sample.z);
        return true;
    }

    static bool Blocked(Vector3 sample, float rise, float drop, out Vector3 feet)
    {
        searches++;
        feet = default;
        return false;
    }

    static bool Step(MarioUnstuck monitor, Vector3 feet, bool fallen = true,
        bool grounded = false, bool held = false, float dt = 1f / 60f) =>
        monitor.Step(feet, fallen, grounded, held, dt, Height, probe, out _);

    static void Main()
    {
        // Use the production detector and search policy with a host probe supplied by each
        // scenario. No Unity runtime, game assets or native sim are needed.
        foreach (int hz in new[] { 30, 60, 120 })
        {
            var monitor = new MarioUnstuck();
            searches = 0;
            for (int i = 0; i < hz * 3 - 1; i++)
                Check(!Step(monitor, Origin, dt: 1f / hz), "Recovered before three seconds");
            Check(searches == 0, "Searched before the timeout");
            Check(Step(monitor, Origin, dt: 1f / hz), "Missed three-second timeout");
        }

        var m = new MarioUnstuck();
        for (int i = 0; i < 600; i++)
            Check(!Step(m, new Vector3(i * 0.01f, 0f, 0f)), "Per-step motion mistaken for stuck");
        m.Reset();
        for (int i = 0; i < 600; i++)
            Check(!Step(m, new Vector3(0f, -i * 0.01f, 0f)), "Normal free fall interrupted");
        m.Reset();
        for (int i = 0; i < 600; i++)
            Check(!Step(m, Origin, fallen: false), "Idle/steady hover interrupted");
        m.Reset();
        for (int i = 0; i < 179; i++)
            Check(!Step(m, new Vector3(i % 2 * 0.01f, 0f, 0f)), "Jitter timeout too early");
        Check(Step(m, Origin), "Small collision jitter prevented recovery");

        foreach (bool held in new[] { false, true })
        {
            m.Reset();
            for (int i = 0; i < 150; i++) Step(m, Origin);
            Check(!Step(m, Origin, fallen: held, grounded: !held, held: held), "Idle/held recovery");
            for (int i = 0; i < 179; i++)
                Check(!Step(m, Origin), "Idle/held time carried into timeout");
            Check(Step(m, Origin), "Timeout did not restart after idle/held");
        }
        m.Reset();
        for (int i = 0; i < 150; i++) Step(m, Origin);
        m.Reset(); // Resync, pod entry/exit, death, authority loss.
        for (int i = 0; i < 179; i++) Check(!Step(m, Origin), "Reset kept elapsed time");
        Check(Step(m, Origin), "Reset disabled future recovery");

        // A clear current position is rejected: escape to a different spot on the first ring.
        m.Reset();
        Vector3 target = default;
        for (int i = 0; i < 180; i++)
            m.Step(Origin, true, false, false, 1f / 60f, Height, Floor, out target);
        Check(target.sqrMagnitude >= Height * Height, "Recovered into the same trap");

        // No local solution: return to previously supported ground only after rechecking it.
        for (int blocked = 0; blocked < 3; blocked++)
        {
            m.Reset();
            probe = Floor;
            for (int i = 0; i < 20; i++) Step(m, Origin, fallen: false, grounded: true);
            if (blocked == 2) m.Reset(); // External teleport invalidates the remembered spot.
            MarioUnstuck.GroundProbe onlyOldGround =
                (Vector3 sample, float rise, float drop, out Vector3 feet) => {
                    feet = new Vector3(0f, 0.03f, 0f);
                    return blocked != 1 && sample.sqrMagnitude < 1f;
                };
            bool recovered = false;
            for (int i = 0; i < 180; i++)
                recovered = m.Step(new Vector3(20f, 0f, 0f), true, false, false,
                    1f / 60f, Height, onlyOldGround, out target);
            Check(recovered == (blocked == 0), "Unsafe or stale fallback ground");
            if (recovered) Check(target.sqrMagnitude < 1f, "Did not use remembered safe ground");
        }

        m.Reset(); searches = 0; probe = Blocked;
        for (int i = 0; i < 180; i++) Check(!Step(m, Origin), "Recovered without safe ground");
        Check(searches == 37, "Unexpected local search coverage");
        for (int i = 0; i < 179; i++) Step(m, Origin);
        Check(searches == 37, "Failed search retried every tick");
        Step(m, Origin);
        Check(searches == 74, "Failed search did not retry after three seconds");

        foreach (string status in new[] { "BackDown", "ForeDown", "ShortBackDown", "ShortForeDown",
            "SafeBackDown", "SafeForeDown", "CatchDown", "LandSafeDown", "JumpShortBackDown",
            "JumpShortForeDown", "JumpBackDown", "JumpForeDown", "Catch", "CatchStop", "CatchLost",
            "SlipFore", "SlipBack" })
            Check(MarioUnstuck.Fallen(status, true, 0f), "Missed prone state: " + status);
        foreach (string status in new[] { "Wait", "Run", "Squat", "HipAttackEnd" })
            Check(!MarioUnstuck.Fallen(status, true, 0f), "Standing state treated as fallen");
        foreach (string status in new[] { "DownLoser", "Hanging", "Ascend", "Descend" })
            Check(!MarioUnstuck.Fallen(status, false, -10f), "Death/ledge state treated as fallen");
        Check(MarioUnstuck.Fallen("Landing", false, -10f), "Blocked falling not detected");
        Check(!MarioUnstuck.Fallen("Jump", false, 10f), "Rising jump treated as fallen");
        Check(!MarioUnstuck.Fallen("Landing", false, 0f), "Steady hover treated as fallen");
        Console.WriteLine("PASS: timeout at 30/60/120 Hz, motion, jitter, idle, crowd control, reset, " +
            "nearby escape, fallback revalidation, failed-search retry, and fallen states");
    }
}

// Small managed stand-ins for the vector/math operations used by the detector. Collision
// safety remains UnityHost.RecoveryGround's responsibility and needs an in-game check.
namespace UnityEngine
{
    struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
    }
    static class Mathf
    {
        public const float PI = (float)Math.PI;
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Sin(float a) => (float)Math.Sin(a);
        public static float Cos(float a) => (float)Math.Cos(a);
    }
}
