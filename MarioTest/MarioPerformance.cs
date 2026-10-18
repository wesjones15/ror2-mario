using System;
using System.Diagnostics;
using System.Text;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // Opt-in CPU timings from the actual game. No per-frame strings or heap allocations;
    // one summary per ten seconds. These do not measure GPU time or network latency.
    internal static class MarioPerformance
    {
        internal enum Part { Driver, View, Effects, Models, Coins, Replay, Receive }
        static readonly double[] milliseconds = new double[7], maximum = new double[7];
        static readonly int[] calls = new int[7];
        static float since;
        static int frames, slowFrames, backlog, corrections, packets;
        static long bytes;
        static double frameSeconds, worstFrame;
        static Run run;

        static bool Enabled => Plugin.PerformanceLog != null && Plugin.PerformanceLog.Value
            && Run.instance && !PauseManager.isPaused;

        internal readonly struct Timing : IDisposable
        {
            readonly Part part;
            readonly long start;
            internal Timing(Part part, bool enabled)
            {
                this.part = part;
                start = enabled ? Stopwatch.GetTimestamp() : 0;
            }
            public void Dispose()
            {
                if (start == 0) return;
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                int i = (int)part;
                milliseconds[i] += ms;
                maximum[i] = Math.Max(maximum[i], ms);
                calls[i]++;
            }
        }

        internal static Timing Measure(Part part) => new Timing(part, Enabled);
        internal static void Backlog(int count) { if (Enabled) backlog = Math.Max(backlog, count); }
        internal static void Corrected() { if (Enabled) corrections++; }
        internal static void Sent(int length) { if (Enabled) { packets++; bytes += length; } }

        static void Reset()
        {
            Array.Clear(milliseconds, 0, milliseconds.Length);
            Array.Clear(maximum, 0, maximum.Length);
            Array.Clear(calls, 0, calls.Length);
            frames = slowFrames = backlog = corrections = packets = 0;
            bytes = 0;
            frameSeconds = worstFrame = 0;
            since = Time.realtimeSinceStartup;
        }

        internal static void Report()
        {
            if (!Enabled) { run = null; return; }
            if (run != Run.instance) { run = Run.instance; Reset(); return; }
            double dt = Time.unscaledDeltaTime;
            frames++;
            frameSeconds += dt;
            worstFrame = Math.Max(worstFrame, dt);
            if (dt > 1.0 / 30.0) slowFrames++;
            float seconds = Time.realtimeSinceStartup - since;
            if (seconds < 10f) return;
            var text = new StringBuilder("Mario performance: ");
            text.Append(NetworkServer.active ? "host" : "client");
            text.Append($" bodies={CharacterBody.readOnlyInstancesList.Count}, fps={frames / Math.Max(frameSeconds, .001):F1}, " +
                $"worstFrame={worstFrame * 1000:F1}ms, framesOver33ms={slowFrames}/{frames}; CPU ms/s (max call ms): ");
            for (int i = 0; i < calls.Length; i++)
                text.Append($"{(Part)i}={milliseconds[i] / seconds:F2} ({maximum[i]:F2}, n={calls[i]}) ");
            text.Append($"; movementOut={packets} packets/{bytes} payload bytes, maxReplayQueue={backlog}, driftCorrections={corrections}");
            Plugin.Log.LogInfo(text.ToString());
            Reset();
        }
    }
}
