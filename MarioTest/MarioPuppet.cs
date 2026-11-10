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
        // Past this the shadow is somewhere else entirely (a stomp's bounce, a push): relocate it.
        const float FarDrift = 400f;

        CharacterBody body;
        HealthComponent health;
        IntPtr sim;
        MarioView view;
        Native.Host host;
        GameObject anchor;
        StepSmoother smoother;
        FrameQueue queue;
        bool playing, hidden, failed, drawn;
        internal bool Drawing => playing && drawn;
        /// His view's native handle (the life meter, coin counters and water gauge are read from it), or zero.
        internal IntPtr HudHandle => view != null ? view.Handle : IntPtr.Zero;
        internal IntPtr HudSim => sim;
        float lagTicks;
        Native.State state;
        // The owner's Melee A pose, replayed from the frame stream.
        byte meleeMove;
        float meleeFrame;
        int replayRenderFrame = -1, replayedThisFrame;
        double replayMsThisFrame;

        Native.EventFn onEvent;
        readonly MarioFallbackEffects fallbackEffects = new MarioFallbackEffects();
        static readonly unsafe Native.EnemiesFn noEnemies = (IntPtr user, ref Native.Vec3 pos, float radius, Native.Enemy* output, uint max) => 0u;

        static readonly System.Collections.Generic.List<MarioPuppet> live = new System.Collections.Generic.List<MarioPuppet>();

        /// A yellow coin was taken by a player who has no Mario of his own (Olimar, a Melee fighter):
        /// Sunshine's pickup sound and sparks through the first other player's Mario this machine shows.
        internal static bool PlayPickup(Vector3 position)
        {
            foreach (var p in live)
                if (p && p.view != null && p.queue != null && !p.queue.Clone && p.Drawing)
                {
                    p.CoinCollected(position);
                    return true;
                }
            return false;
        }

        void Awake()
        {
            live.Add(this);
            body = GetComponent<CharacterBody>();
            health = GetComponent<HealthComponent>();
            host = UnityHost.Table();
            // The shadow sim uses its landing cue for presentation. Its owner deals the hits.
            onEvent = OnEvent;
            host.evt = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(onEvent);
            host.enemies = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(noEnemies);
        }

        void OnEvent(IntPtr user, ref Native.Event e) => fallbackEffects.Observe(e);

        /// This body is another player's Mario that this machine should show.
        bool Wanted =>
            body && !body.hasEffectiveAuthority && !Plugin.AssetsBusy && !RunAudio.Ended && !failed
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

        void FixedUpdate()
        {
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
            uint skin = MarioSkin.Selected(body);
            view = MarioView.Remote(queue.Clone, skin);
            if (view != null && view.Models == null)
            {
                view?.Dispose();
                view = null;
            }
            var feet = UnityHost.ToSim(body.footPosition);
            sim = Native.sms_sim_new(NativeLib.CString(OptionalAssets.SimulationPath(Plugin.AssetsDir, Plugin.AssetsReady)), ref feet,
                UnityHost.Yaw(body.transform.forward));
            MarioSims.Add(sim, body);
            if (!queue.Clone) MarioSkin.ApplySim(sim, skin);
            if (sim == IntPtr.Zero)
            {
                failed = true;
                view?.Dispose();
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
            fallbackEffects.Begin();
            if (!FrameCodec.Replay(frame, this)) return;
            Native.sms_sim_state(sim, out state);
            // Put the shadow back where the player's sim had him if the two have drifted apart.
            var d = new Vector3(state.pos.x - frame.pos.x, state.pos.y - frame.pos.y, state.pos.z - frame.pos.z);
            if (d.sqrMagnitude > Drift * Drift)
            {
                MarioPerformance.Corrected();
                var at = frame.pos;
                // A small drift keeps his speed and state (a slide, dive or jump chain would
                // die in a teleport, which stops him); only a big one is a relocation.
                if (d.sqrMagnitude > FarDrift * FarDrift) Native.sms_sim_teleport(sim, ref at);
                else Native.sms_sim_nudge(sim, ref at);
                Native.sms_sim_state(sim, out state);
                if (anchor) anchor.transform.position = UnityHost.ToUnity(state.pos);
                smoother.Reset();
            }
            smoother.Stepped(UnityHost.ToUnity(state.pos));
            fallbackEffects.Draw(view, state.pos);
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
            drawn = false;
            if (sim == IntPtr.Zero) return;
            if (view == null) {
                if (primitive == null) primitive = new MarioShapes();
                if(!jrLooked){jrLooked=true;jr=GetComponent<KoopaJrBody>();}
                if(jr && jr.HidesMario){primitive.Hide();jr.Draw(anchor.transform.position,sim);}
                else primitive.Draw(sim, anchor.transform.position, Quaternion.Euler(0, state.face_yaw * (180f / 32768f), 0), MarioSkin.Selected(body), lagTicks, fallbackEffects.TakeRocketLaunch());
                GameSettings.HideModel(body, ref hidden, true); drawn = true; return;
            }
            MarioMelee.ApplyPose(view, meleeMove, meleeFrame);
            view.Spectated = LocalUsers.IsSpectated(body);
            if (!jrLooked)
            {
                jrLooked = true;
                jr = GetComponent<KoopaJrBody>();
            }
            // Another player's Shadow Mario mid brush stroke: his upper body in its pose.
            if (jr) jr.PoseBrush(sim);
            view.Update(sim, lagTicks);
            if (view.Models == null) return;
            var feet = anchor.transform.position;
            view.Effects(feet);
            view.Frozen = NativeBody.Of(body ? body.gameObject : null)?.Frozen == true;
            // Another player's Bowser Jr.: Shadow Mario's body hides while he is Bowser Jr. or under the goop.
            if (jr && jr.HidesMario) view.Draw(feet, HideEverything);
            else view.Draw(feet);
            if (jr) jr.Draw(feet, sim);
            GameSettings.HideModel(body, ref hidden, true);
            drawn = true;
        }

        KoopaJrBody jr;
        bool jrLooked;
        static bool HideEverything(ViewDraw _) => true;
        MarioShapes primitive;

        void Teardown()
        {
            primitive?.Hide();
            drawn = false;
            if (jr) jr.Draw(Vector3.zero, IntPtr.Zero);
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
            primitive?.Dispose();
            live.Remove(this);
            Teardown();
            if (body) MarioNet.Drop(body.netId.Value);
        }

        // ---- the recorded calls, on the shadow sim -----------------------------------------------

        public void Teleport(Native.Vec3 pos) => Native.sms_sim_teleport(sim, ref pos);
        public void Tuning(in Native.Tuning tuning) { var t = tuning; Native.sms_sim_set_tuning(sim, ref t); }
        public void Refill() => Native.sms_sim_refill(sim);
        // The swap cue (MSD_SE_SY_SELECT_POMP) and the nozzle-box chime are Sunshine's system
        // sounds, for the player who pressed the button. A spectator's replay fits the same
        // nozzle without them (select_nozzle is switch_nozzle less its cue), so another player's
        // swaps never sound from across the map or pile up when ops replay in a catch-up burst.
        // The pickup sparkle and chime still play for an Equip when he is near.
        const float EquipSoundRange = 25f;
        int nozzleOps;
        float nozzleOpsAt;

        void CountNozzleOp(string what)
        {
            // A replay that fits nozzles many times in a second is worth a line in the log.
            float now = Time.unscaledTime;
            if (now - nozzleOpsAt > 1f) { nozzleOpsAt = now; nozzleOps = 0; }
            if (++nozzleOps == 4 && body)
                Plugin.Log.LogInfo($"Nozzle churn from {body.GetDisplayName()}: {what} replayed 4+ times in a second");
        }

        bool NearListener()
        {
            var cam = Camera.main;
            return !cam || !body || (cam.transform.position - body.footPosition).sqrMagnitude <= EquipSoundRange * EquipSoundRange;
        }

        public void SelectNozzle(byte slot)
        {
            CountNozzleOp("select");
            Native.sms_sim_select_nozzle(sim, slot);
        }

        public void EquipNozzle(byte slot)
        {
            CountNozzleOp("equip");
            if (NearListener()) Native.sms_sim_equip_nozzle(sim, slot);
            else Native.sms_sim_select_nozzle(sim, slot);
        }
        public void SyncNozzle(byte slot)
        {
            // Re-fitting an already correct nozzle resets its charging/emission
            // state. Periodic refreshes must leave an existing puppet's spray alone.
            if (Native.sms_sim_state(sim, out var current) != 0
                && MarioNozzleChoices.FromState(current).NativeSlot() != slot)
                Native.sms_sim_select_nozzle(sim, slot);
        }
        public void AddWater(float share) => Native.sms_sim_add_water(sim, share);
        public void Limits(in Native.Limits limits) { var l = limits; Native.sms_sim_set_limits(sim, ref l); }
        public void BoostJump(float factor) => Native.sms_sim_boost_jump(sim, factor);
        public void Gravity(float scale) => Native.sms_sim_set_gravity_scale(sim, scale);
        public void Launch(Native.Vec3 vel) => Native.sms_sim_launch(sim, ref vel);
        public void LaunchPad(Native.Vec3 vel, float gravity) => Native.sms_sim_launch_pad(sim, ref vel, gravity);
        public void KnockDown(Native.Vec3 from, byte kind) => Native.sms_sim_knock_down(sim, ref from, kind);
        public void Ignite() => Native.sms_sim_ignite(sim);
        public void Soaked() => Native.sms_sim_wet(sim);
        public void Carried(bool on) => Native.sms_sim_set_carried(sim, (byte)(on ? 1 : 0));
        public void WaterImpact(Native.Vec3 pos)
        {
            if (view != null) Native.sms_view_water_hit(view.Handle, ref pos);
            fallbackEffects.WaterImpact(view, UnityHost.ToUnity(pos));
        }
        public void SetAim(bool on, short yaw, float pitch) => Native.sms_sim_set_aim(sim, (byte)(on ? 1 : 0), yaw, pitch);
        public void FlightCamera(bool on, short yaw, float pitch) => Native.sms_sim_set_flight_camera(sim, (byte)(on ? 1 : 0), yaw, pitch);
        public void Down() => Native.sms_sim_down(sim);
        public void ShineGet(Native.Vec3 pos, short yaw) => Native.sms_sim_shine_get(sim, ref pos, yaw);
        public void ShineEnd() => Native.sms_sim_shine_end(sim);
        // What the player's own sim was told, not what this machine's other sims say: they differ when
        // the wet spot is one of a Mario this machine no longer has (a dead spectator's own).
        bool foreignWet;
        public void ForeignWet(bool wet) => foreignWet = wet;
        public void Melee(byte move, float frame) { meleeMove = move; meleeFrame = frame; }

        public void Step(in Native.Pad pad, short cameraYaw, float dt)
        {
            var p = pad;
            Native.sms_sim_set_foreign_wet(sim, foreignWet ? 1u : 0u);
            Native.sms_sim_step(sim, ref host, ref p, cameraYaw, dt, view != null ? view.Handle : IntPtr.Zero);
        }
    }
}
