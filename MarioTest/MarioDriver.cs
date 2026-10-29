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
        const float VoidReach = Unstuck.VoidReach;
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
        /// Where the player aims (the camera's ray); the Melee Mario A attack's direction.
        internal Vector3 AimDirection => input && input.aimDirection.sqrMagnitude > 1e-6f
            ? input.aimDirection : transform.forward;
        internal bool Drawing => driving && View?.Models != null;
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
            MarioShipParts.Register(body);
            CheckSkinUnlocks();
            // A client's Mario on the server: what healing owes his tank goes to the client.
            if (NetworkServer.active && body && !body.hasEffectiveAuthority) Items.FlushRemote();
            if (!CanDrive)
            {
                if (Local) MarioShipParts.Lost();
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
            using var timing = MarioPerformance.Measure(MarioPerformance.Part.Driver);
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
            // A ground pound, and the get-up after it, are not interrupted by enemy damage: a hit's
            // knockback leaves him be (the sim also refuses a knock-down then); a jump pad still launches.
            if (native && native.TakeExternalVelocity(out var thrown, 2f) && Plugin.ExternalForces.Value
                && (pounding == 0 || native.JumpPadLaunch))
            {
                var v = UnityHost.ToSim(thrown / 30f);
                if (native.JumpPadLaunch)
                    SimOps.LaunchPad(sim, ref v, Mathf.Abs(Physics.gravity.y) / (UnityHost.Scale * 30f * 120f));
                else SimOps.Launch(sim, ref v);
                strictUntil = Time.time + 2f;
            }
            // RoR2 sets him alight (the OnFire buff comes on): Sunshine's fire hit, with the yell and
            // the panicked run. His own sim does it on his machine; a friend's copy gets it replayed.
            bool burning = MarioFire.Burning(body) || Time.time < fireUntil;
            if (burning && !wasBurning && Plugin.BurnPanic.Value && pounding == 0)
                wasBurning = SimOps.Ignite(sim) != 0;
            if (!burning) wasBurning = false;
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
            if (Local && MarioRadial.TakeTap()) TapFludd();
            if (Local && MarioRadial.TakeChoice(out var chosen))
                chosen.Apply?.Invoke(this);
            if (pad.TakeRefill() && Plugin.DpadRefill.Value)
                SimOps.Refill(sim);
            bool secret = pad.TakeUp();
            var step = pad.Take(move, hold == NativeBody.Hold.All, Local && MarioRadial.Open);
            // Facing from Mario toward the camera: the reverse of where the player aims.
            short cameraYaw = UnityHost.Yaw(-input.aimDirection);
            // The lock-on (LB; a keyboard's utility key; the brain's): FLUDD takes its aim, and
            // its head turns, to the target (or where the camera looks), on the ground or in the
            // air. L is now just the ground pound in the air (no crouch, no camera aim). Only a keyboard has L
            // and the lock on one key; the sim then sees L as a pound pressed afresh in the air.
            // A player's lock picks its target with the camera, so it needs Controls.LockOn and
            // CameraAim; an AI's target comes from its brain, which has no camera and is its own
            // gun's aiming (as Olimar's brain sets his cursor), so the player's settings don't
            // gate it.
            bool lHeld = step.l != 0;
            bool locking = pad.LockHeld
                && (Local ? Plugin.LockOn.Value && Plugin.CameraAim.Value : lockTarget != null);
            if (locking)
            {
                if (pad.LockSharesL)
                {
                    if (!lHeld || grounded) poundArmed = false;
                    else if (!lWasHeld) poundArmed = true;
                    step.l = (byte)(poundArmed ? 1 : 0);
                    step.l_analog = (byte)(poundArmed ? 255 : 0);
                }
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
            Aim(false, locking, lockTarget, cycle, cycleDirection, crosshair);
            if (secret && Local && MarioSkin.Current(body).EggLay)
                SecretEggLay();
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
                // Out through the top of the map (a Rocket, a long corkscrew) is no miss: he is put back with no
                // "TOO BAD!".
                bool wentHigh = UnityHost.ToUnity(st.pos).y > fellFrom.y;
                if (Local)
                {
                    if (!lostShown && !wentHigh) TooBad();
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
                // A wall-guard put-back is a local correction, not a move to a new place: the
                // last safe spot and the stuck timers stay valid. Wiping them here starved the
                // recovery whenever the guard fired, so a body held against a collider never
                // reached its three seconds, and one let out under the map never reached its five.
                Resync(true);
                Native.sms_sim_state(sim, out st);
                status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
            }
            bool wasGrounded = grounded;
            grounded = st.on_ground != 0;
            MarioCombat.Watch(st);
            MarioShipParts.Step(body, st, Local);
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
            if (!driving)
            {
                if (Local) MarioRadial.Cancel();
                return;
            }
            smoother.Smooth();
            // The view poses Mario this far back too, so he doesn't ghost in fast moves.
            lagTicks = StepSmoother.LagTicks(Native.sms_tick_hz());
            if (!Local) return;
            if (sim != IntPtr.Zero && !PauseManager.isPaused && localUser != null
                && !localUser.isUIFocused)
            {
                var view = View;
                MarioRadial.Update(pad.MenuHeld, pad.MenuDirection, !pad.MenuFromGamepad,
                    view != null ? view.Handle : IntPtr.Zero);
            }
            else MarioRadial.Cancel();
        }

        /// RB tapped: swap Spray and the selected alternate. With No FLUDD selected the button
        /// is empty, so it becomes the optional Melee Mario punch/kick; until that provider
        /// exists the ability is omitted and a tap there does nothing. Hold RB to fit a nozzle.
        void TapFludd()
        {
            if (sim == IntPtr.Zero) return;
            Native.sms_sim_state(sim, out var now);
            var current = MarioNozzleChoices.FromState(now);
            if (current == MarioNozzleChoice.None)
            {
                if (MarioMeleeAttack.Available) MarioMeleeAttack.TryAttack(body, AimDirection);
                return;
            }
            var target = current == MarioNozzleChoice.Spray
                ? MarioRadial.SelectedAlt : MarioNozzleChoice.Spray;
            SelectNozzle(target);
        }

        /// The radial menu chose the alternate. It is remembered for the next tap; if an
        /// alternate is already fitted, the wheel changes it in place. On Spray only the memory
        /// changes, so choosing an alternate does not disturb a spray.
        internal void ChooseAlternative(MarioNozzleChoice choice)
        {
            if (sim == IntPtr.Zero) return;
            MarioRadial.SetSelectedAlt(choice);
            Plugin.Log.LogInfo(choice == MarioNozzleChoice.None
                ? "FLUDD alternate: No FLUDD"
                : $"FLUDD alternate: {choice}");
            Native.sms_sim_state(sim, out var now);
            var current = MarioNozzleChoices.FromState(now);
            if (current == choice) return;
            if (choice == MarioNozzleChoice.None)
            {
                if (now.has_fludd != 0) SelectNozzle(MarioNozzleChoice.None);
                return;
            }
            if (current == MarioNozzleChoice.Spray) return;
            SelectNozzle(choice);
        }

        /// Fits the nozzle chosen in the radial menu, recording it for the other players'
        /// puppets. Called from the step, so it lands before the sim reads the pad.
        internal void SelectNozzle(MarioNozzleChoice choice)
        {
            if (sim == IntPtr.Zero) return;
            SimOps.SelectNozzle(sim, choice.NativeSlot());
            Plugin.Log.LogInfo(choice == MarioNozzleChoice.None
                ? "FLUDD taken off"
                : $"FLUDD nozzle: {choice}");
        }

        // Mario's models at the body's (smoothed) feet, facing as the sim says.
        void LateUpdate()
        {
            if (!driving || sim == IntPtr.Zero) return;
            if (Local)
            {
                MarioView.Ensure(body);
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
            // RoR2's fire also burns on him while he is alight (the disc's own flames can't be asked
            // to stay on, only to flare), so you see him burning as he runs about.
            flames.Set((Plugin.PoundFlames.Value && pounding == 2 && !view.HasEffects)
                    || (Plugin.BurnPanic.Value && (MarioFire.Burning(body) || Time.time < fireUntil)),
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
        // `ai` is an AI's Mario's target (his brain's lock): his gun's crosshair, aimed straight
        // from the nozzle, since an AI has no camera and its input bank's aim is its walk.
        void Aim(bool camera, bool lockHeld, CharacterBody ai, bool cycle, Vector2 direction, bool crosshair = false)
        {
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
            var nozzle = transform.position - feetToOrigin
                + Vector3.up * (UnityHost.MarioHeight * UnityHost.Scale * 0.55f);
            Vector3 target;
            string note = locked ? "locked" : "";
            if (locked) target = locked.corePosition;
            else target = CrosshairTarget(origin, dir, out note);
            var d = target - nozzle;
            Vector3 aim;
            if (ai)
            {
                // An AI's crosshair is the enemy its brain locked onto: the water goes straight at
                // it from the nozzle, whichever way the camera or the AI's own walk faces. An AI's
                // InputBankTest aim follows its walk or the driver's aim, not its brain's enemy,
                // since the Spray became a gun aimed by the host; this is its trigger's target.
                aim = d.sqrMagnitude > 1e-6f ? d.normalized
                    : (dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward);
            }
            else
            {
                // A crosshair hit that is not out where the camera looks (a collider beside him, or anything close
                // that sits above the nozzle, which would send the water straight up) is not the target: the water
                // goes the way the camera looks. Not for a locked enemy, which can be anywhere.
                if (!locked && d.sqrMagnitude > 1e-4f && Vector3.Angle(d, dir) > MaxCrosshairTurn)
                {
                    note += " turn " + Vector3.Angle(d, dir).ToString("0");
                    d = dir;
                }
                // The crosshair, or a locked enemy that circled or flew past him, can end up beside
                // or behind the nozzle: the water must never spray backward into his own face, so a
                // target that isn't out front falls back to the camera direction (as
                // MeleeAim.ProjectileDirection does). "Perpendicular and up" spray was this: a target
                // off to one side or behind sent the water sideways/backward instead of at the camera.
                aim = Vector3.Dot(d, dir) > 0.05f ? d.normalized : dir.normalized;
            }
            var flat = new Vector3(aim.x, 0f, aim.z);
            float pitch = Mathf.Atan2(aim.y, flat.magnitude);
            if (flat.sqrMagnitude < 1e-4f) flat = new Vector3(dir.x, 0f, dir.z);
            SimOps.SetAim(sim, 1, UnityHost.Yaw(flat), pitch);
            SprayDiag(locked, origin, dir, nozzle, target, note, UnityHost.Yaw(flat), pitch);
        }

        // Luigi's secret dpad move: Yoshi's Egg Lay, borrowed from the Melee plugin through the
        // shared EggLay service. The tongue lashes out, catches, and lays the egg.
        void SecretEggLay()
        {
            if (!EggLay.Ready) return;
            var target = lockShown;
            if (!target) target = FrontEnemy();
            EggLay.TryShow(body, target ? target.corePosition : body.corePosition + body.transform.forward * 10f);
        }

        // The nearest alive enemy within reach and roughly in front of Mario.
        CharacterBody FrontEnemy()
        {
            CharacterBody best = null;
            float bestDist = float.PositiveInfinity;
            var origin = body.corePosition;
            var forward = body.transform.forward;
            foreach (var e in CharacterBody.readOnlyInstancesList)
            {
                if (!e || e == body || !e.healthComponent || !e.healthComponent.alive) continue;
                if (e.teamComponent && body.teamComponent && e.teamComponent.teamIndex == body.teamComponent.teamIndex) continue;
                var d = e.corePosition - origin;
                float dist = d.magnitude;
                if (dist > 6f || (dist > 0.01f && Vector3.Dot(d / dist, forward) < 0.3f)) continue;
                if (dist < bestDist) { bestDist = dist; best = e; }
            }
            return best;
        }

        // A crosshair hit nearer than this to the camera's origin is not taken as the target (a free aim at
        // something that close points the water at its own parallax, up or down), and the water never turns
        // further than MaxCrosshairTurn from where the camera looks to reach its target.
        const float MinCrosshairHit = 2f, MaxCrosshairTurn = 35f;
        static readonly RaycastHit[] crosshairHits = new RaycastHit[24];

        /// Where the camera's ray lands, past Mario's own colliders and anything closer than MinCrosshairHit.
        /// `note` says what was passed over, for SprayDiag.
        Vector3 CrosshairTarget(Vector3 origin, Vector3 dir, out string note)
        {
            note = "";
            int n = Physics.RaycastNonAlloc(origin, dir, crosshairHits, 300f,
                LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            var point = origin + dir * 100f;
            for (int i = 0; i < n; i++)
            {
                var h = crosshairHits[i];
                if (!h.collider) continue;
                if (h.collider.transform.IsChildOf(body.transform))
                {
                    note += " own:" + h.collider.name + "@" + h.distance.ToString("0.00");
                    continue;
                }
                if (h.distance < MinCrosshairHit)
                {
                    note += " near:" + h.collider.name + "@" + h.distance.ToString("0.00");
                    continue;
                }
                if (h.distance < best) { best = h.distance; point = h.point; }
            }
            return point;
        }

        // The water's direction, for the spray that goes straight up on its own: while the Spray is aimed by the
        // camera (RT down, or a lock), the camera, the target, what was passed over, the aim sent to the sim and
        // the direction the sim last sent the water, four times a second, and at once when the water goes
        // well above where the camera looks.
        float sprayLogAt;
        uint sprayLogEmits;

        void SprayDiag(CharacterBody locked, Vector3 origin, Vector3 dir, Vector3 nozzle, Vector3 target, string note,
            short yaw, float pitch)
        {
            if (!Local || !Plugin.SprayDiag.Value) return;
            uint emits = Native.sms_sim_last_spray(sim, out var spray);
            bool fresh = emits != sprayLogEmits;
            sprayLogEmits = emits;
            if (!fresh && Time.unscaledTime - sprayLogAt > 1f) return;
            var sent = new Vector3(spray.dir.x, spray.dir.y, spray.dir.z);
            float camPitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float sentPitch = Mathf.Asin(Mathf.Clamp(sent.y, -1f, 1f)) * Mathf.Rad2Deg;
            bool odd = fresh && spray.spin == 0 && spray.nozzle == 1 && sentPitch - camPitch > 25f;
            if (!odd && Time.unscaledTime - sprayLogAt < 0.25f) return;
            if (odd && Time.unscaledTime - sprayLogAt < 0.1f) return;
            sprayLogAt = Time.unscaledTime;
            Plugin.Log.LogInfo($"SprayDiag{(odd ? " ODD" : "")}: camera dir {Fmt(dir)} (pitch {camPitch:0}) from {Fmt(origin)}; " +
                $"nozzle {Fmt(nozzle)}; target {Fmt(target)}{(locked ? " (locked on " + locked.name + ")" : "")}" +
                $"{(note.Length > 0 ? "; passed over:" + note : "")}; sent to sim yaw {yaw} pitch {pitch * Mathf.Rad2Deg:0}; " +
                $"sim water dir {Fmt(sent)} (pitch {sentPitch:0}) nozzle {spray.nozzle} spin {spray.spin} drops {spray.count} emits {emits} " +
                $"({(fresh ? "this tick" : "stale")})");
        }

        static string Fmt(Vector3 v) => $"({v.x:0.00}, {v.y:0.00}, {v.z:0.00})";

        void StopDriving()
        {
            if (Local) MarioRadial.Cancel();
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
        // Whether he was burning last step, so catching fire reacts once.
        bool wasBurning;
        float fireUntil, nextFireNotice;

        internal void CaughtFire()
        {
            fireUntil = Time.time + 1.5f;
            knockFrom = null;
        }

        /// RoR2 landed a hit (server): one that takes Knockdown.Threshold of a Mario's health knocks him over
        /// on his next step. Falls, damage over time and his own hits don't.
        internal static void OnServerDamage(DamageReport report)
        {
            if (report == null || !report.victimBody) return;
            var info = report.damageInfo;
            if (info == null || info.rejected || report.damageDealt <= 0f) return;
            if (Plugin.BurnPanic.Value && report.attackerBody != report.victimBody
                && (info.IsConsideredFire(report.attackerBody) || MarioFire.FireDot(info.dotIndex)))
            {
                foreach (var d in all)
                {
                    if (!d || d.body != report.victimBody) continue;
                    if (d.driving) d.CaughtFire();
                    else if (Time.time >= d.nextFireNotice)
                    {
                        d.nextFireNotice = Time.time + 0.5f;
                        MarioNet.SendFire(d.body);
                    }
                    break;
                }
                return; // Fire has its own bounce; a threshold knock-down would replace it.
            }
            if (!Plugin.KnockdownEnabled.Value) return;
            if ((info.damageType & (DamageType.FallDamage | DamageType.DoT)) != 0
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
            return unstuck.Lost(feet, Plugin.TooBadDrop.Value);
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
            MarioSims.Add(sim);
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
            if (Local) MarioView.Ensure(body);
            return true;
        }

        /// Checks Luigi's held-coin key and Piantissimo's stage-four key for the local Mario.
        void CheckSkinUnlocks()
        {
            if (Local) MarioSkin.CheckUnlocks(body);
        }

        void Resync(bool keepRecovery = false)
        {
            if (!keepRecovery) unstuck.Reset();
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
                // goes with its square. An alt-skin may jump higher on top of that (Luigi's is 1.2).
                jump_height = Plugin.JumpHeight.Value * Mathf.Pow(NativeBody.JumpScale(body), 2f)
                    * MarioSkin.Current(body).JumpScale(),
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

        /// The local player playing this body, if any (their pad and UI focus).
        void FindLocalUser()
        {
            if (localUser != null && localUser.cachedBody == body)
            {
                MarioPresentation.Observe(localUser, body);
                return;
            }
            localUser = null;
            foreach (var u in LocalUserManager.readOnlyLocalUsersList)
                if (u != null && u.cachedBody == body)
                {
                    localUser = u;
                    MarioPresentation.Observe(u, body);
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
                MarioSims.Remove(sim);
                Native.sms_sim_free(sim);
                sim = IntPtr.Zero;
            }
        }
    }
}
