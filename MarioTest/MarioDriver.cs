using System;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // Puts the body's movement in libsms's hands. On the client with authority over the body it
    // turns off RoR2's character controller (the KinematicCharacterMotor), feeds the player's
    // input to the 120 Hz sim each FixedUpdate and moves the body to wherever Mario ends up.
    // Everyone else sees the result through RoR2's own CharacterNetworkTransform. On that
    // client Mario is drawn with the disc's models (MarioView) in place of Commando's, and
    // the game's sounds play; other players still see Commando for now.
    //
    // Controls: MarioPad (Sunshine's layout on a gamepad, RoR2's binds on keyboard). Enemies:
    // MarioCombat (stomps, pounds and FLUDD's water as RoR2 damage). Items: MarioItems, and
    // ror2-common's NativeBody for what RoR2's motor and main state would do (throws, crowd
    // control, sprinting). A Mario no local player controls (Goobo Jr.'s clone) runs its own sim
    // on MarioBrain's buttons and is drawn silent in RoR2's gummy clone material (MarioView.Clone).
    // When the player's Mario dies he falls down for good as in Sunshine, the miss jingle
    // plays, and MarioCorpse plays the fall out after RoR2 removes the body.
    //
    // The camera follows a pivot of our own on the body (ror2-common's CameraPivot: Commando's
    // hangs off his hidden, still animated and turning model, and swayed the camera against
    // Mario), and this runs before RoR2's camera (which moves in Update) so it sees this
    // frame's smoothed body.
    [DefaultExecutionOrder(-200)]
    public class MarioDriver : MonoBehaviour
    {
        CharacterBody body;
        CharacterMotor motor;
        InputBankTest input;
        CharacterDirection direction;
        HealthComponent health;

        IntPtr sim;
        bool failed;
        bool driving;
        Native.Host host;
        // Body origin minus feet: the sim tracks Mario's feet.
        Vector3 feetToOrigin;
        // The body drawn smoothly between sim steps, and how far behind the sim that is
        // (ror2-common's StepSmoother).
        StepSmoother smoother;
        string lastStatus;
        bool bodyHidden;
        // How far behind the sim the body is drawn (Update's smoothing), in 120 Hz ticks.
        float lagTicks;
        // Debug.TraceJumps: rendered frames left to log, physics steps since the last one, and
        // where Mario's feet were drawn.
        int traceFrames, fixedSteps;
        Vector3 drawnFeet;
        CameraTargetParams cameraTarget;
        static MarioDriver traced;
        CameraPivot pivot;
        readonly MarioPad pad = new MarioPad();
        readonly MarioFlames flames = new MarioFlames();
        readonly Unstuck unstuck = new Unstuck();
        // How far below him ground still means he is not over a void (a vertical stage's floor can
        // be hundreds of metres down).
        const float VoidReach = 2000f;
        // "TOO BAD!" has started for a fall (it ends with him standing again).
        bool lostShown;
        // Keeps Mario out of walls the sim let him into (ror2-common's WallGuard); strict for a
        // while after a hit or a launch, when he is thrown fast and the wall rings are likeliest
        // to miss.
        readonly WallGuard guard = new WallGuard();
        float strictUntil;
        float sweepLogAt;
        readonly EnemyLock enemyLock = new EnemyLock();
        readonly MarioBrain brain = new MarioBrain();
        // L held last step, and a ground pound armed by a fresh L press in the air.
        bool lWasHeld, poundArmed;
        // The sim's ground pound stage (Native.State.pounding).
        byte pounding;
        LocalUser localUser;
        float spawnAge;
        // The tuning last sent to the sim.
        Native.Tuning tuning;
        // Standing in water as of the last step (Native.State.in_water).
        byte inWater;
        // On the ground as of the last step.
        bool grounded = true;
        // His velocity on the last step in the air, for RoR2's landing event.
        Vector3 airVelocity;
        NativeBody native;
        // The frames sent to the other players' puppets (MarioNet), when there are any.
        FrameRecorder recorder;
        int framesSent, sinceSend;

        /// RoR2's items on this Mario.
        internal MarioItems Items { get; private set; }

        /// A local player plays this Mario (the view, sounds and HUD are theirs).
        bool Local => localUser != null;
        // The lock-on marker, and the enemy the lock holds now (set by Aim, shown in LateUpdate).
        readonly MarioLockMarker lockMarker = new MarioLockMarker();
        CharacterBody lockShown;
        // This Mario has drawn with the player's view (MarioView.Player), so it's this one's to
        // hide and free.
        bool ownsView;
        // A Mario no local player controls: his own silent view (tried once).
        MarioView cloneView;
        bool cloneTried;

        /// The Mario a local player is playing, or null.
        internal static MarioDriver Player
        {
            get
            {
                foreach (var d in all)
                    if (d && d.Local && d.driving) return d;
                return null;
            }
        }

        internal CharacterBody Body => body;
        internal LocalUser LocalUser => localUser;
        internal bool Alive => !health || health.alive;
        /// Where his feet are now.
        internal Vector3 Feet => body.footPosition;
        // The Shine Get pose as of the last step (Native.State.win_demo), and a touch or an end
        // waiting for the next one.
        byte winDemo;
        Vector3? shineAt;
        short shineYaw;
        bool shineEnd;

        /// He is in the Shine Get pose (falling into it, or holding the Shine up).
        internal bool WinPose => winDemo != 0 || shineAt.HasValue;

        /// He touches a Shine at `at` (world), turned to `yaw`: he takes the pose on his next step.
        /// False if he can't (not driven by his player, or already posing).
        internal bool TouchShine(Vector3 at, short yaw)
        {
            if (!driving || sim == IntPtr.Zero || WinPose) return false;
            shineAt = at;
            shineYaw = yaw;
            return true;
        }

        /// The Shine Get demo ends without the stage: he stands again.
        internal void EndShine() => shineEnd = true;

        /// The view this Mario draws with: the player's, or his own.
        MarioView View => Local ? MarioView.Player : cloneView;

        /// A host-owned yellow coin was collected: Sunshine's pickup sound and sparks.
        internal void CoinCollected(Vector3 position)
        {
            var shown = View;
            if (shown == null || shown.Handle == IntPtr.Zero) return;
            var at = UnityHost.ToSim(position);
            Native.sms_view_coin_get(shown.Handle, ref at);
        }

        void Awake()
        {
            body = GetComponent<CharacterBody>();
            motor = GetComponent<CharacterMotor>();
            input = GetComponent<InputBankTest>();
            direction = GetComponent<CharacterDirection>();
            health = GetComponent<HealthComponent>();
            cameraTarget = GetComponent<CameraTargetParams>();
            pivot = new CameraPivot(body);
            smoother = new StepSmoother(transform);
            host = UnityHost.Table();
            native = GetComponent<NativeBody>();
            Items = new MarioItems(body);
            all.Add(this);
        }

        // A stage's floor can load after the body appears: wait in place for it (ror2-common's GroundGate).
        readonly GroundGate ground = new GroundGate();

        bool CanDrive =>
            Plugin.AssetsReady && !RunAudio.Ended && !failed && body && motor && input
            && body.hasEffectiveAuthority
            && !body.currentVehicle
            && (!health || health.alive);

        void FixedUpdate()
        {
            spawnAge += Time.fixedDeltaTime;
            // A client's Mario on the server: what healing owes his tank goes to the client.
            if (NetworkServer.active && body && !body.hasEffectiveAuthority) Items.FlushRemote();
            if (!CanDrive)
            {
                StopDriving();
                return;
            }
            if (sim == IntPtr.Zero && !Create())
                return;
            if (!ground.Ready(body.footPosition, Time.fixedDeltaTime, "Mario"))
            {
                // RoR2's own motor would fall too: keep it off, and him where he is.
                if (motor.Motor.enabled) motor.Motor.enabled = false;
                return;
            }
            // Wait through pod setup; CanDrive stays false while Mario occupies it.
            if (spawnAge > 1f && Local) SpawnMusic.PlayOnce(body);
            BeginRecording();
            if (motor.Motor.enabled)
                motor.Motor.enabled = false;
            if (!driving || smoother.MovedElsewhere)
                Resync();
            if (!driving)
            {
                pivot.Use(true);
                // Back from the dead (Dio's Best Friend): the old Mario stops lying there.
                if (Local) MarioCorpse.End();
            }
            driving = true;

            // Jump pads, knockback, Milky Chrysalis: whatever RoR2 put on the motor since the
            // last step throws Mario (mVel is per 30 Hz frame); a nudge under 2 m/s (a light
            // hit's knockback) leaves him be.
            if (native && native.TakeExternalVelocity(out var thrown, 2f) && Plugin.ExternalForces.Value)
            {
                var v = UnityHost.ToSim(thrown / 30f);
                if (native.JumpPadLaunch)
                    SimOps.LaunchPad(sim, ref v, Mathf.Abs(Physics.gravity.y) / (UnityHost.Scale * 30f * 120f));
                else SimOps.Launch(sim, ref v);
                strictUntil = Time.time + 2f;
            }
            if (knockFrom.HasValue)
            {
                var from = UnityHost.ToSim(knockFrom.Value);
                knockFrom = null;
                strictUntil = Time.time + 3f;
                if ((pounding == 0 || Plugin.KnockdownInterruptsPound.Value) && SimOps.KnockDown(sim, ref from, 0) != 0)
                    Plugin.Log.LogInfo("Mario is knocked down by a powerful hit");
            }
            var hold = native && Plugin.CrowdControl.Value ? native.Control() : NativeBody.Hold.None;
            var move = hold == NativeBody.Hold.None ? input.moveVector : Vector3.zero;
            // An AI's Mario presses his own buttons (MarioBrain).
            bool brained = !Local && SurvivorAi.HasAi(body);
            pad.Brained = brained;
            CharacterBody lockTarget = null;
            if (brained)
            {
                Native.sms_sim_state(sim, out var now);
                var presses = brain.Think(body, now, move, Time.fixedDeltaTime);
                pad.Press(presses);
                lockTarget = presses.target;
            }
            bool sprinting = native && native.UpdateSprint(pad.TakeSprint(), move.sqrMagnitude > 0.01f,
                hold == NativeBody.Hold.None);

            Tune();
            if (pad.TakeRefill() && Plugin.DpadRefill.Value)
                SimOps.Refill(sim);
            var step = pad.Take(move, hold == NativeBody.Hold.All);
            // Facing from Mario toward the camera: the reverse of where the player aims.
            short cameraYaw = UnityHost.Yaw(-input.aimDirection);
            // The lock-on (LB; a keyboard's utility key; the brain's): FLUDD takes its aim, and
            // its head turns, to the target (or where the camera looks), on the ground or in the
            // air, and R's click (which plants and crouches him) is held back so R sprays on the
            // move. L stays Sunshine's: crouch, ground pound, the camera. Only a keyboard has L
            // and the lock on one key; the sim then sees L as a pound pressed afresh in the air.
            bool lHeld = step.l != 0;
            bool locking = pad.LockHeld && Plugin.LockOn.Value && Plugin.CameraAim.Value && (Local || lockTarget);
            if (locking)
            {
                if (pad.LockSharesL)
                {
                    if (!lHeld || grounded) poundArmed = false;
                    else if (!lWasHeld) poundArmed = true;
                    step.l = (byte)(poundArmed ? 1 : 0);
                    step.l_analog = (byte)(poundArmed ? 255 : 0);
                }
                step.r = 0;
            }
            else poundArmed = false;
            lWasHeld = lHeld;
            bool cycle = pad.TakeCycle(out var cycleDirection);
            // The Spray follows the crosshair while RT is down (AimAtCrosshair); the sim's last state says
            // which nozzle is fitted.
            bool crosshair = false;
            if (Local && Plugin.AimAtCrosshair.Value && step.r_analog > 0)
            {
                Native.sms_sim_state(sim, out var fitted);
                crosshair = fitted.nozzle == 1;
            }
            Aim(lHeld && Local && !(pad.LockSharesL && locking), locking, lockTarget, cycle, cycleDirection, crosshair);
            Items.Begin(sim, sprinting, grounded);
            // The Shine Get pose, inside the step so the other players' puppets get it too.
            if (shineAt.HasValue)
            {
                var at = UnityHost.ToSim(shineAt.Value);
                shineAt = null;
                SimOps.ShineGet(sim, ref at, shineYaw);
            }
            if (shineEnd)
            {
                shineEnd = false;
                SimOps.ShineEnd(sim);
            }
            MarioCombat.Begin(body, Items);
            var view = View;
            SimOps.Step(sim, ref host, ref step, cameraYaw, Time.fixedDeltaTime,
                view != null ? view.Handle : IntPtr.Zero);
            MarioCombat.End();
            Native.sms_sim_state(sim, out var st);
            winDemo = st.win_demo;
            Items.End(sim, st);
            string status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
            // "TOO BAD!" starts as soon as he is plainly lost, well below where he last stood with no
            // ground under him for a long way, a good while before the stage would send him back.
            if (Local && !lostShown && Lost(UnityHost.ToUnity(st.pos)))
            {
                lostShown = true;
                TooBad();
            }
            if (st.on_ground != 0) lostShown = false;
            // A long drop onto lower ground (a vertical stage's top to its bottom) is not a fall off
            // the map: only a void, or the stage's own kill zone, sends him back.
            float anyDrop = Unstuck.AnyDrop;
            if (unstuck.TryGetSafe(out var lastSafe) && lastSafe.y - UnityHost.ToUnity(st.pos).y >= Unstuck.AnyDrop
                && WorldProbe.Ground(UnityHost.ToUnity(st.pos), VoidReach, out _))
                anyDrop = float.PositiveInfinity;
            if (unstuck.Step(UnityHost.ToUnity(st.pos),
                MarioUnstuck.Fallen(status, st.on_ground != 0, st.vel.y), st.on_ground != 0,
                native && native.Control() != NativeBody.Hold.None, Time.fixedDeltaTime,
                UnityHost.MarioHeight * UnityHost.Scale, UnityHost.RecoveryGround, out var recovered))
            {
                transform.position = recovered + feetToOrigin;
                Resync();
                // Teleport clears velocity but preserves the old status. A zero-speed launch
                // leaves the knock-down/slide so he can land and accept input again.
                var rest = new Native.Vec3(0f, 0f, 0f);
                SimOps.Launch(sim, ref rest);
                Native.sms_sim_state(sim, out st);
                status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
                Plugin.Log.LogInfo($"Mario unstuck after 3 seconds at {recovered}");
            }
            // Off the map: RoR2's kill zones never see him (his motor is off), so he would fall for
            // ever. Back to where he last stood, unhurt (ror2-common's Unstuck.Fell).
            else if (unstuck.Fell(UnityHost.ToUnity(st.pos), UnityHost.MarioHeight * UnityHost.Scale,
                UnityHost.RecoveryGround, out var fellFrom, anyDrop))
            {
                Plugin.Log.LogInfo($"Mario fell off the map at {UnityHost.ToUnity(st.pos)}; back on the surface at {fellFrom}");
                // Sunshine's "Too Bad!" (the miss jingle) for a fall: already playing, if he fell far
                // enough for it to start early. He is safe from hits as he stands again.
                if (Local)
                {
                    if (!lostShown) TooBad();
                    lostShown = false;
                    if (NetworkServer.active && body)
                        body.AddTimedBuff(RoR2Content.Buffs.HiddenInvincibility, 1.5f);
                }
                transform.position = fellFrom + feetToOrigin;
                Resync();
                var rest = new Native.Vec3(0f, 0f, 0f);
                SimOps.Launch(sim, ref rest);
                Native.sms_sim_state(sim, out st);
                status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
            }
            // The sim's wall rings can miss (a thin pole between rays, a corner, a step so long it
            // ends inside the wall, where a ray hits no back face), and Mario then walks out of the
            // map. Sweep the step itself; a wall crossed puts him back where he stood.
            // The guard also undoes a step that leaves him inside a wall (a hit's knock-back or a
            // launch against one), which the sweep alone can't see; a hit is swept more closely.
            // Being hurt (any of the knock-down statuses) counts as a hit too.
            bool strict = Time.time < strictUntil || status.EndsWith("Down") || status.StartsWith("Damage");
            if (guard.Step(UnityHost.ToUnity(st.pos), UnityHost.MarioHeight * UnityHost.Scale,
                UnityHost.SweepHeights, UnityHost.SweepWallNormalY, strict, MarioUnstuck.Climbing(status),
                out var back, out var through))
            {
                if (Time.time >= sweepLogAt)
                {
                    sweepLogAt = Time.time + 1f;
                    Plugin.Log.LogInfo($"Mario put back at {back}: {through} (status {status}{(strict ? ", struck" : "")})");
                }
                transform.position = back + feetToOrigin;
                Resync();
                Native.sms_sim_state(sim, out st);
                status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
            }
            bool wasGrounded = grounded;
            grounded = st.on_ground != 0;
            MarioCombat.Watch(st);
            pounding = st.pounding;
            if (st.in_water != inWater)
            {
                inWater = st.in_water;
                Plugin.Log.LogInfo(inWater != 0 ? $"Mario is in water ({UnityHost.LastWater})" : "Mario is out of the water");
            }

            fixedSteps++;
            smoother.Stepped(UnityHost.ToUnity(st.pos) + feetToOrigin);

            // mVel is per 30 Hz frame. RoR2 reads these for animation, camera and AI.
            if (native)
            {
                native.WroteVelocity(UnityHost.ToUnity(st.vel) * 30f);
                // Touching down raises RoR2's landing event with the speed he came down at
                // (Mycelium Cap's spores); standing, it just ends a jump pad's hold.
                if (grounded && !wasGrounded) native.Landed(airVelocity);
                else if (grounded) native.Landed();
                else airVelocity = UnityHost.ToUnity(st.vel) * 30f;
            }
            else motor.velocity = UnityHost.ToUnity(st.vel) * 30f;
            motor.Motor.GroundingStatus.IsStableOnGround = st.on_ground != 0;
            motor.Motor.GroundingStatus.FoundAnyGround = st.on_ground != 0;
            if (direction)
            {
                var facing = UnityHost.Forward(st.model_yaw);
                direction.forward = facing;
                direction.moveVector = facing;
            }

            EndRecording(st);

            if (Plugin.LogStatus.Value)
            {
                if (status != lastStatus)
                {
                    Plugin.Log.LogInfo($"{status} ({NativeLib.Utf8(Native.sms_sim_clip_name(sim))})");
                    lastStatus = status;
                    if (Plugin.TraceJumps.Value && status.Contains("Jump") && !status.Contains("Slip") && !status.Contains("End"))
                    {
                        traceFrames = 200;
                        traced = this;
                        Plugin.Log.LogInfo("trace: frame dt_ms fixed a lag feet(x,y,z) pivot(x,y,z) cam(x,y,z) screen(x,y) yaw");
                    }
                }
            }
        }

        // With other players in the game, everything this step does to the sim is recorded and sent
        // to them (MarioFrames); they replay it on a shadow sim (MarioPuppet).
        const int FramesPerMessage = 3, RefreshFrames = 180;

        void BeginRecording()
        {
            SimOps.Rec = null;
            if (!Mp.Networked)
            {
                recorder = null;
                return;
            }
            if (recorder == null)
            {
                recorder = new FrameRecorder();
                // The first frame carries everything a puppet needs to start from.
                tuning = default;
            }
            else if (++sinceSend > RefreshFrames)
            {
                sinceSend = 0;
                recorder.Refresh();
                tuning = default;
            }
            recorder.Begin();
            SimOps.Rec = recorder;
        }

        void EndRecording(in Native.State st)
        {
            SimOps.Rec = null;
            if (recorder == null) return;
            recorder.End(st.pos);
            if (recorder.Pending >= FramesPerMessage)
                MarioNet.Send(recorder, body.netId.Value, !Local && SurvivorAi.HasAi(body));
        }

        void Update()
        {
            FindLocalUser();
            pad.Read(localUser, input);
            if (!driving) return;
            smoother.Smooth();
            // The view poses Mario this far back too, so he doesn't ghost in fast moves.
            lagTicks = StepSmoother.LagTicks(Native.sms_tick_hz());
        }

        // Mario's models at the body's (smoothed) feet, facing as the sim says.
        void LateUpdate()
        {
            if (!driving || sim == IntPtr.Zero) return;
            if (Local)
            {
                MarioView.Ensure();
                ownsView = true;
            }
            else if (!cloneTried)
            {
                cloneTried = true;
                cloneView = MarioView.Clone();
            }
            var view = View;
            if (view == null)
            {
                ShowMario(false);
                return;
            }
            view.Update(sim, lagTicks);
            if (view.Models == null) return;
            var feet = transform.position - feetToOrigin;
            drawnFeet = feet;
            float height = UnityHost.MarioHeight * UnityHost.Scale;
            // The game's flames when the disc's particles are there; RoR2's fire otherwise.
            view.Effects(feet);
            flames.Set(Plugin.PoundFlames.Value && pounding == 2 && !view.HasEffects,
                feet + Vector3.up * (height * 0.5f), height);
            view.Draw(feet);
            ShowMario(true);
            if (Local)
            {
                // The life meter for the player's health, and the marker on the locked enemy
                // as seen from Mario's chest.
                MarioLife.Update(view.Handle, health);
                MarioCoinHud.Update(view.Handle, body, localUser);
                MarioHudLayout.Update(true, localUser);
                lockMarker.Update(view.Handle, lockShown,
                    feet + Vector3.up * (height * 0.55f));
            }
        }

        // Mario's models in place of Commando's, or back.
        void ShowMario(bool show)
        {
            var view = View;
            if (!show && view != null && (ownsView || view == cloneView)) view.Hide();
            GameSettings.HideModel(body, ref bodyHidden, show && view != null && view.Models != null);
        }

        // FLUDD's aim, as the sim takes it (libsms's host aim: the nozzle's pitch and head, the
        // water's heading, and a standing or crouching Mario turning to it). `camera` is
        // Sunshine's L-button camera (Controls.CameraAim): FLUDD points where the camera looks
        // (at what's under the crosshair). `lockHeld` is the lock-on (Controls.LockOn):
        // ror2-common's EnemyLock picks the enemy nearest where the camera points (Olimar's RT
        // does the same), `cycle` steps to the next toward `direction` on screen, and FLUDD
        // points at it, so the spray trigger sprays it; with none in reach it's the crosshair.
        void Aim(bool camera, bool lockHeld, CharacterBody ai, bool cycle, Vector2 direction, bool crosshair = false)
        {
            // An AI's Mario aims at the enemy his brain locked onto.
            camera = ((camera && Plugin.CameraAim.Value) || crosshair) && input;
            enemyLock.Range = Plugin.LockRange.Value;
            var locked = ai ? ai : enemyLock.Update(body, lockHeld && input, cycle, direction);
            lockShown = lockHeld && Local ? locked : null;
            if (!camera && !lockHeld)
            {
                SimOps.SetAim(sim, 0, 0, 0f);
                return;
            }
            var origin = input.aimOrigin;
            var dir = input.aimDirection;
            Vector3 target;
            if (locked) target = locked.corePosition;
            else target = Physics.Raycast(origin, dir, out var hit, 300f,
                LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore)
                ? hit.point : origin + dir * 100f;
            var nozzle = transform.position - feetToOrigin
                + Vector3.up * (UnityHost.MarioHeight * UnityHost.Scale * 0.55f);
            var d = target - nozzle;
            var flat = new Vector3(d.x, 0f, d.z);
            if (flat.sqrMagnitude < 0.01f) flat = new Vector3(dir.x, 0f, dir.z);
            float pitch = Mathf.Atan2(d.y, flat.magnitude);
            SimOps.SetAim(sim, 1, UnityHost.Yaw(flat), pitch);
        }

        void StopDriving()
        {
            unstuck.Reset();
            guard.Reset();
            if (driving)
            {
                pivot?.Use(false);
                flames.Set(false, default, 0f);
                pounding = 0;
                // RoR2's own health bar and no marker while he's not Mario.
                lockMarker.Hide();
                lockShown = null;
                if (Local)
                {
                    MarioLife.Update(IntPtr.Zero, null);
                    MarioCoinHud.Update(IntPtr.Zero, null, null);
                    MarioHudLayout.Restore();
                }
                if (health && !health.alive)
                    Died();
                else
                {
                    ShowMario(false);
                    if (ownsView && (!health || RunAudio.Ended)) MarioView.Reset();
                }
            }
            driving = false;
        }

        // A powerful hit's source, for the next step to knock Mario down from.
        Vector3? knockFrom;

        /// RoR2 landed a hit (server): one that takes Knockdown.Threshold of a Mario's health knocks him over
        /// on his next step. Falls, damage over time and his own hits don't.
        internal static void OnServerDamage(DamageReport report)
        {
            if (!Plugin.KnockdownEnabled.Value || report == null || !report.victimBody) return;
            var info = report.damageInfo;
            if (info == null || (info.damageType & (DamageType.FallDamage | DamageType.DoT)) != 0
                || report.attackerBody == report.victimBody) return;
            var hc = report.victim;
            if (!hc || hc.fullCombinedHealth <= 0f
                || report.damageDealt < Plugin.KnockdownThreshold.Value * hc.fullCombinedHealth) return;
            foreach (var d in all)
            {
                if (!d || d.body != report.victimBody) continue;
                var from = report.attackerBody ? report.attackerBody.corePosition : info.position;
                if (d.driving) d.knockFrom = from;
                // A client's Mario falls over in his own sim: tell him.
                else MarioNet.SendKnock(d.body, from);
                return;
            }
        }

        /// The server says healing owes the player's tank this much, or a full refill.
        internal void WaterOwed(float share, bool refill) => Items.Owed(share, refill);

        /// The server says a powerful hit knocks this (the player's own) Mario over.
        internal void KnockFrom(Vector3 from) => knockFrom = from;

        static readonly System.Collections.Generic.List<MarioDriver> all = new System.Collections.Generic.List<MarioDriver>();

        /// Before the run's audio is reset: a Mario who has just died (a game over comes the
        /// moment the last player does) falls down first, keeping his view for the fall.
        internal static void SettleTheDead()
        {
            foreach (var d in all.ToArray())
                if (d && d.driving && d.health && !d.health.alive)
                    d.StopDriving();
        }

        // The jingle and the screen of a miss, and a pause: nothing hits him while the screen is black.
        void TooBad()
        {
            SpawnMusic.PlayMiss(body);
            MarioScreenFx.TooBad(body.master);
            if (NetworkServer.active && body)
                body.AddTimedBuff(RoR2Content.Buffs.HiddenInvincibility, 2.5f);
        }

        // Far enough below where he last stood, with nothing to land on for a long way: a void.
        bool Lost(Vector3 feet)
        {
            if (!unstuck.TryGetSafe(out var safe)) return false;
            return safe.y - feet.y > Plugin.TooBadDrop.Value && !WorldProbe.Ground(feet, VoidReach, out _);
        }

        // RoR2 killed the body: Sunshine's fall for good (DOWN_LOSER: the "die" clip and the
        // "down" voices) and the miss jingle. RoR2 removes the body a second after it comes to
        // rest, so the sim and the view go to MarioCorpse to play it out.
        void Died()
        {
            if (Local)
            {
                // Sunshine: a life in hand makes it a miss ("TOO BAD!", with the lives count going
                // down); with none left, the game is over. Dio's Best Friend and the Pluripotent
                // Larva are the lives (the body is dead, but they are used when he is revived).
                if (MarioScreenFx.ExtraLives(body.master) > 0)
                {
                    SpawnMusic.PlayMiss(body);
                    MarioScreenFx.TooBad(body.master);
                }
                else
                {
                    SpawnMusic.PlayDeath(body);
                    MarioScreenFx.GameOver();
                }
            }
            if (Local && sim != IntPtr.Zero && Plugin.DeathFall.Value && ownsView && MarioView.Player != null)
            {
                SimOps.Down(sim);
                // Commando's ragdoll stays hidden: Mario is the one lying there.
                MarioCorpse.Begin(sim, MarioView.TakePlayer(), Plugin.DeathFallSeconds.Value);
                sim = IntPtr.Zero;
                ownsView = false;
                Plugin.Log.LogInfo("Mario is down");
                return;
            }
            if (cloneView != null)
            {
                // A clone just goes, like Goobo Jr.'s.
                cloneView.Dispose();
                cloneView = null;
                return;
            }
            ShowMario(false);
            if (ownsView) MarioView.Reset();
        }

        bool Create()
        {
            feetToOrigin = transform.position - body.footPosition;
            var feet = UnityHost.ToSim(body.footPosition);
            short yaw = UnityHost.Yaw(direction ? direction.forward : transform.forward);
            sim = Native.sms_sim_new(NativeLib.CString(Plugin.AssetsDir), ref feet, yaw);
            if (sim == IntPtr.Zero)
            {
                failed = true;
                Plugin.Log.LogError($"libsms could not start from {Plugin.AssetsDir}; Mario stays put");
                return false;
            }
            tuning = default;
            Tune();
            // Probe what RoR2's own bodies collide with, not just the world layer.
            WorldProbe.UseBodyCollision(motor, gameObject);
            Plugin.Log.LogInfo($"Mario's sim started at {body.footPosition}");
            UnityHost.LogStageWater();
            if (Local) MarioView.Ensure();
            return true;
        }

        void Resync()
        {
            unstuck.Reset();
            guard.Placed(transform.position - feetToOrigin);
            var feet = UnityHost.ToSim(transform.position - feetToOrigin);
            SimOps.Teleport(sim, ref feet);
            smoother.Reset();
            native?.ForgetVelocity();
        }

        /// Sends the config's tuning, and the body's current move speed, when they change.
        void Tune()
        {
            var t = new Native.Tuning
            {
                // Jump power items (Faraday Spur, Hex of Leaping) scale takeoff speed, so height
                // goes with its square.
                jump_height = Plugin.JumpHeight.Value * Mathf.Pow(NativeBody.JumpScale(body), 2f),
                // Hex of Acceleration's slippery ground.
                traction = NativeBody.AccelerationScale(body),
                jump_buffer = Plugin.JumpBuffer.Value,
                chain_grace = Plugin.ChainGrace.Value,
                move_speed = Plugin.SpeedFromItems.Value ? Mathf.Clamp(MinionStats.Of(body).moveSpeed, 0.1f, 10f) : 1f,
                spray_reach = Plugin.SprayReach.Value,
                stomp_bounce = Plugin.StompBounce.Value,
                rocket_height = Plugin.RocketHeight.Value * Items.RocketBoost,
                pound_jump = Plugin.PoundJump.Value,
                stomp_jump = Plugin.StompJump.Value,
                short_hop = Plugin.ShortHop.Value,
                enemy_push = (byte)(Plugin.SolidEnemies.Value ? 1 : 0),
                cycle_nozzles = (byte)(Plugin.CycleNozzles.Value ? 1 : 0),
                unlimited_water = (byte)(Plugin.UnlimitedWater.Value || Items.UnlimitedWater ? 1 : 0),
                // The AI holds its primary down: a steady stream for clones.
                spray_steady = (byte)(Plugin.SprayBursts.Value && Local ? 0 : 1),
                shallow_wet = (byte)(Plugin.ShallowWetSlide.Value ? 1 : 0),
                slide_splashes = (byte)(Plugin.SlideSplashes.Value ? 1 : 0),
                slide_fx_scale = Mathf.Max(0.1f, Plugin.SlideSprayScale.Value),
                knockdown_cancel = (byte)(Plugin.KnockdownGetUpEarly.Value ? 1 : 0),
            };
            if (t.Equals(tuning)) return;
            tuning = t;
            SimOps.Tuning(sim, ref t);
        }

        // The water gauge, for the player playing this Mario.
        void OnGUI()
        {
            if (driving && localUser != null)
            {
                MarioHud.Draw();
                MarioLife.Draw();
                MarioCoinHud.Draw();
            }
        }

        /// The local player playing this body, if any (their pad and UI focus).
        void FindLocalUser()
        {
            if (localUser != null && localUser.cachedBody == body) return;
            localUser = null;
            foreach (var u in LocalUserManager.readOnlyLocalUsersList)
                if (u != null && u.cachedBody == body)
                {
                    localUser = u;
                    break;
                }
        }

        // Debug.TraceJumps: one line per rendered frame of the player's camera, taken as it
        // renders, so it shows exactly where Mario and the camera were for that frame.
        static void OnPreCull(Camera cam)
        {
            var d = traced;
            if (!d || d.traceFrames <= 0) return;
            var player = GameSettings.PlayerCamera();
            if (!player || cam.transform != player) return;
            d.traceFrames--;
            float a = StepSmoother.Alpha;
            var pivot = d.cameraTarget && d.cameraTarget.cameraPivotTransform ? d.cameraTarget.cameraPivotTransform.position : Vector3.zero;
            var scr = cam.WorldToScreenPoint(d.drawnFeet);
            var c = cam.transform.position;
            var f = d.drawnFeet;
            var models = d.View?.Models;
            float yaw = models != null && models.DrawCount > 0
                ? new Quaternion(models.DrawAt(0).rot_x, models.DrawAt(0).rot_y, models.DrawAt(0).rot_z, models.DrawAt(0).rot_w).eulerAngles.y
                : float.NaN;
            Plugin.Log.LogInfo($"trace: {Time.frameCount} {Time.deltaTime * 1000f:F2} {d.fixedSteps} {a:F2} {d.lagTicks:F2} " +
                $"({f.x:F3},{f.y:F3},{f.z:F3}) ({pivot.x:F3},{pivot.y:F3},{pivot.z:F3}) ({c.x:F3},{c.y:F3},{c.z:F3}) ({scr.x:F1},{scr.y:F1}) {yaw:F1}");
            d.fixedSteps = 0;
        }

        static bool hooked;

        void Start()
        {
            if (hooked) return;
            hooked = true;
            Camera.onPreCull += OnPreCull;
        }

        void OnDestroy()
        {
            if (traced == this) traced = null;
            all.Remove(this);
            Items?.Dispose();
            pivot?.Use(false);
            flames.Set(false, default, 0f);
            lockMarker.Dispose();
            if (sim != IntPtr.Zero && ownsView) MarioView.Reset();
            cloneView?.Dispose();
            cloneView = null;
            if (sim != IntPtr.Zero)
            {
                Native.sms_sim_free(sim);
                sim = IntPtr.Zero;
            }
        }
    }
}
