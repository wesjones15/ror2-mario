using System;
using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Another player's Mario, on this machine. His own machine runs the real sim and sends what
    // it was told each physics step (MarioFrames); this runs a shadow sim of libsms on the same
    // calls, a few frames behind, and draws it the way MarioDriver draws the player's own Mario
    // (the disc's models, FLUDD's water, the dust and flames, and his sounds from where he is).
    // The body itself is moved by RoR2's own network transform, as ever, and Commando's model on it
    // stays hidden. There are no enemies in the shadow sim, so the player's recorded position
    // pulls it back when a stomp or a push makes the two differ.
    //
    // Playback keeps a few frames queued so a late packet doesn't stall it: it waits for the
    // queue to fill, plays one frame per physics step, and plays extra when the queue grows (a
    // burst after a stall) to catch up.
    internal sealed class MarioPuppet : MonoBehaviour, IFrameSink
    {
        // Frames to hold before starting, and the backlog that makes playback hurry.
        const int Prebuffer = FrameQueue.Prebuffer;
        // How far (sim units, about 1 cm) the shadow sim may drift before it is put back.
        const float Drift = 30f;

        CharacterBody body;
        HealthComponent health;
        IntPtr sim;
        MarioView view;
        Native.Host host;
        GameObject anchor;
        StepSmoother smoother;
        FrameQueue queue;
        bool playing, hidden, failed;
        internal bool Drawing => playing && view?.Models != null;
        float lagTicks;
        Native.State state;
        int replayRenderFrame = -1, replayedThisFrame;
        double replayMsThisFrame;

        static readonly Native.EventFn noEvent = (IntPtr user, ref Native.Event e) => { };
        static readonly unsafe Native.EnemiesFn noEnemies = (IntPtr user, ref Native.Vec3 pos, float radius, Native.Enemy* output, uint max) => 0u;

        void Awake()
        {
            body = GetComponent<CharacterBody>();
            health = GetComponent<HealthComponent>();
            host = UnityHost.Table();
            // A shadow sim hears no enemies and raises no hits: the player's own machine deals them.
            host.evt = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(noEvent);
            host.enemies = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(noEnemies);
        }

        /// This body is another player's Mario that this machine should show.
        bool Wanted =>
            body && !body.hasEffectiveAuthority && Plugin.AssetsReady && !RunAudio.Ended && !failed
            && (!health || health.alive) && Mp.Networked;

        // His body died: he falls down for good, as the player's own Mario does (MarioCorpse), after
        // the body is gone too.
        void Fall()
        {
            if (sim == IntPtr.Zero || view == null || !Plugin.DeathFall.Value) return;
            Native.sms_sim_down(sim);
            MarioCorpse.Begin(sim, view, Plugin.DeathFallSeconds.Value, true);
            // The corpse owns them now.
            sim = IntPtr.Zero;
            view = null;
            Teardown();
        }

        static bool warnedAssets;

        void FixedUpdate()
        {
            if (!warnedAssets && body && !body.hasEffectiveAuthority && !Plugin.AssetsReady
                && MarioNet.QueueFor(body.netId.Value) != null)
            {
                warnedAssets = true;
                Plugin.Log.LogWarning("Another player is Mario, but this machine has no Sunshine assets yet " +
                    "(set Paths.Disc to your own disc): they will look like Commando.");
            }
            if (!Wanted)
            {
                if (health && !health.alive && body && !body.hasEffectiveAuthority) Fall();
                else Teardown();
                return;
            }
            queue = MarioNet.QueueFor(body.netId.Value);
            if (queue == null) return;
            if (sim == IntPtr.Zero && !Create()) return;

            int waiting = queue.Count;
            if (!playing)
            {
                if (waiting < Prebuffer) return;
                playing = true;
            }
            else if (waiting == 0)
            {
                // Starved: wait for the queue to fill again rather than guess.
                playing = false;
                return;
            }
            using var timing = MarioPerformance.Measure(MarioPerformance.Part.Replay);
            MarioPerformance.Backlog(waiting);
            if (replayRenderFrame != Time.frameCount)
            {
                replayRenderFrame = Time.frameCount;
                replayedThisFrame = 0;
                replayMsThisFrame = 0;
            }
            int steps = FrameQueue.PlaybackSteps(waiting);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int done = 0; ; done++)
            {
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                // Unity can run several FixedUpdates before drawing. They share this budget.
                if (!FrameQueue.KeepReplaying(queue.Count, replayedThisFrame,
                    FrameQueue.MaxCatchUpSteps, replayMsThisFrame + elapsed)) break;
                if (!FrameQueue.KeepReplaying(queue.Count, done, steps, elapsed) || !queue.TryTake(out var frame)) break;
                Play(frame);
                replayedThisFrame++;
            }
            replayMsThisFrame += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
        }

        bool Create()
        {
            view = MarioView.Remote(queue.Clone);
            if (view == null || view.Models == null)
            {
                view?.Dispose();
                view = null;
                failed = true;
                return false;
            }
            var feet = UnityHost.ToSim(body.footPosition);
            sim = Native.sms_sim_new(NativeLib.CString(Plugin.AssetsDir), ref feet,
                UnityHost.Yaw(body.transform.forward));
            MarioSims.Add(sim);
            if (sim == IntPtr.Zero)
            {
                failed = true;
                view.Dispose();
                view = null;
                return false;
            }
            anchor = new GameObject("MarioPuppetAnchor");
            anchor.transform.position = body.footPosition;
            smoother = new StepSmoother(anchor.transform);
            Plugin.Log.LogInfo($"Showing another player's Mario ({body.GetDisplayName()}) at {body.footPosition}");
            return true;
        }

        void Play(Frame frame)
        {
            if (!FrameCodec.Replay(frame, this)) return;
            Native.sms_sim_state(sim, out state);
            // Put the shadow back where the player's sim had him if the two have drifted apart.
            var d = new Vector3(state.pos.x - frame.pos.x, state.pos.y - frame.pos.y, state.pos.z - frame.pos.z);
            if (d.sqrMagnitude > Drift * Drift)
            {
                MarioPerformance.Corrected();
                var at = frame.pos;
                Native.sms_sim_teleport(sim, ref at);
                Native.sms_sim_state(sim, out state);
                if (anchor) anchor.transform.position = UnityHost.ToUnity(state.pos);
                smoother.Reset();
            }
            smoother.Stepped(UnityHost.ToUnity(state.pos));
        }

        /// This Mario picked up a coin: Sunshine's pickup sound and sparks.
        public void CoinCollected(Vector3 position)
        {
            if (view == null || view.Handle == IntPtr.Zero) return;
            var at = UnityHost.ToSim(position);
            Native.sms_view_coin_get(view.Handle, ref at);
        }

        void Update()
        {
            if (sim == IntPtr.Zero) return;
            smoother.Smooth();
            lagTicks = StepSmoother.LagTicks(Native.sms_tick_hz());
        }

        void LateUpdate()
        {
            if (sim == IntPtr.Zero || view == null) return;
            view.Update(sim, lagTicks);
            if (view.Models == null) return;
            var feet = anchor.transform.position;
            view.Effects(feet);
            view.Draw(feet);
            GameSettings.HideModel(body, ref hidden, true);
        }

        void Teardown()
        {
            if (view != null)
            {
                view.Hide();
                view.Dispose();
                view = null;
            }
            // A body that is falling over keeps Commando's ragdoll hidden: Mario is the one lying there.
            if (!(health && !health.alive && failed == false && hidden)) GameSettings.HideModel(body, ref hidden, false);
            if (sim != IntPtr.Zero)
            {
                MarioSims.Remove(sim);
                Native.sms_sim_free(sim);
                sim = IntPtr.Zero;
            }
            if (anchor) Destroy(anchor);
            anchor = null;
            playing = false;
        }

        void OnDestroy()
        {
            Teardown();
            if (body) MarioNet.Drop(body.netId.Value);
        }

        // ---- the recorded calls, on the shadow sim -----------------------------------------------

        public void Teleport(Native.Vec3 pos) => Native.sms_sim_teleport(sim, ref pos);
        public void Tuning(in Native.Tuning tuning) { var t = tuning; Native.sms_sim_set_tuning(sim, ref t); }
        public void Refill() => Native.sms_sim_refill(sim);
        public void AddWater(float share) => Native.sms_sim_add_water(sim, share);
        public void Limits(in Native.Limits limits) { var l = limits; Native.sms_sim_set_limits(sim, ref l); }
        public void BoostJump(float factor) => Native.sms_sim_boost_jump(sim, factor);
        public void Launch(Native.Vec3 vel) => Native.sms_sim_launch(sim, ref vel);
        public void LaunchPad(Native.Vec3 vel, float gravity) => Native.sms_sim_launch_pad(sim, ref vel, gravity);
        public void KnockDown(Native.Vec3 from, byte kind) => Native.sms_sim_knock_down(sim, ref from, kind);
        public void Ignite() => Native.sms_sim_ignite(sim);
        public void SetAim(bool on, short yaw, float pitch) => Native.sms_sim_set_aim(sim, (byte)(on ? 1 : 0), yaw, pitch);
        public void Down() => Native.sms_sim_down(sim);
        public void ShineGet(Native.Vec3 pos, short yaw) => Native.sms_sim_shine_get(sim, ref pos, yaw);
        public void ShineEnd() => Native.sms_sim_shine_end(sim);
        // What the player's own sim was told, not what this machine's other sims say: they differ when
        // the wet spot is one of a Mario this machine no longer has (a dead spectator's own).
        bool foreignWet;
        public void ForeignWet(bool wet) => foreignWet = wet;

        public void Step(in Native.Pad pad, short cameraYaw, float dt)
        {
            var p = pad;
            Native.sms_sim_set_foreign_wet(sim, foreignWet ? 1u : 0u);
            Native.sms_sim_step(sim, ref host, ref p, cameraYaw, dt, view != null ? view.Handle : IntPtr.Zero);
        }
    }
}
