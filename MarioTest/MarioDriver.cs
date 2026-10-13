using System;
using Ror2Common;
using RoR2;
using UnityEngine;

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
        NativeBody native;

        /// RoR2's items on this Mario.
        internal MarioItems Items { get; private set; }

        /// A local player plays this Mario (the view, sounds and HUD are theirs).
        bool Local => localUser != null;
        // This Mario has drawn with the player's view (MarioView.Player), so it's this one's to
        // hide and free.
        bool ownsView;
        // A Mario no local player controls: his own silent view (tried once).
        MarioView cloneView;
        bool cloneTried;

        /// The view this Mario draws with: the player's, or his own.
        MarioView View => Local ? MarioView.Player : cloneView;

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

        bool CanDrive =>
            !RunAudio.Ended && !failed && body && motor && input
            && body.hasEffectiveAuthority
            && !body.currentVehicle
            && (!health || health.alive);

        void FixedUpdate()
        {
            spawnAge += Time.fixedDeltaTime;
            if (!CanDrive)
            {
                StopDriving();
                return;
            }
            if (sim == IntPtr.Zero && !Create())
                return;
            // Wait through pod setup; CanDrive stays false while Mario occupies it.
            if (spawnAge > 1f && Local) SpawnMusic.PlayOnce(body);
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
                    Native.sms_sim_launch_pad(sim, ref v, Mathf.Abs(Physics.gravity.y) / (UnityHost.Scale * 30f * 120f));
                else Native.sms_sim_launch(sim, ref v);
            }
            if (knockFrom.HasValue)
            {
                var from = UnityHost.ToSim(knockFrom.Value);
                knockFrom = null;
                if ((pounding == 0 || Plugin.KnockdownInterruptsPound.Value) && Native.sms_sim_knock_down(sim, ref from, 0) != 0)
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
                Native.sms_sim_refill(sim);
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
            Aim(lHeld && Local && !(pad.LockSharesL && locking), locking, lockTarget, cycle, cycleDirection);
            Items.Begin(sim, sprinting, grounded);
            MarioCombat.Begin(body, Items);
            var view = View;
            Native.sms_sim_step(sim, ref host, ref step, cameraYaw, Time.fixedDeltaTime,
                view != null ? view.Handle : IntPtr.Zero);
            MarioCombat.End();
            Native.sms_sim_state(sim, out var st);
            Items.End(sim, st);
            string status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
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
                Native.sms_sim_launch(sim, ref rest);
                Native.sms_sim_state(sim, out st);
                status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
                Plugin.Log.LogInfo($"Mario unstuck after 3 seconds at {recovered}");
            }
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
                if (grounded) native.Landed();
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
        void Aim(bool camera, bool lockHeld, CharacterBody ai, bool cycle, Vector2 direction)
        {
            // An AI's Mario aims at the enemy his brain locked onto.
            camera &= Plugin.CameraAim.Value && input;
            var locked = ai ? ai : enemyLock.Update(body, lockHeld && input, cycle, direction);
            if (!camera && !lockHeld)
            {
                Native.sms_sim_set_aim(sim, 0, 0, 0f);
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
            Native.sms_sim_set_aim(sim, 1, UnityHost.Yaw(flat), pitch);
        }

        void StopDriving()
        {
            unstuck.Reset();
            if (driving)
            {
                pivot?.Use(false);
                flames.Set(false, default, 0f);
                pounding = 0;
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
                if (!d || !d.driving || d.body != report.victimBody) continue;
                d.knockFrom = report.attackerBody ? report.attackerBody.corePosition : info.position;
                return;
            }
        }

        static readonly System.Collections.Generic.List<MarioDriver> all = new System.Collections.Generic.List<MarioDriver>();

        /// Before the run's audio is reset: a Mario who has just died (a game over comes the
        /// moment the last player does) falls down first, keeping his view for the fall.
        internal static void SettleTheDead()
        {
            foreach (var d in all.ToArray())
                if (d && d.driving && d.health && !d.health.alive)
                    d.StopDriving();
        }

        // RoR2 killed the body: Sunshine's fall for good (DOWN_LOSER: the "die" clip and the
        // "down" voices) and the miss jingle. RoR2 removes the body a second after it comes to
        // rest, so the sim and the view go to MarioCorpse to play it out.
        void Died()
        {
            if (Local) SpawnMusic.PlayDeath(body);
            if (Local && sim != IntPtr.Zero && Plugin.DeathFall.Value && ownsView && MarioView.Player != null)
            {
                Native.sms_sim_down(sim);
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
            Plugin.Log.LogInfo($"Mario's sim started at {body.footPosition}");
            UnityHost.LogStageWater();
            if (Local) MarioView.Ensure();
            return true;
        }

        void Resync()
        {
            unstuck.Reset();
            var feet = UnityHost.ToSim(transform.position - feetToOrigin);
            Native.sms_sim_teleport(sim, ref feet);
            smoother.Reset();
            native?.ForgetVelocity();
        }

        /// Sends the config's tuning, and the body's current move speed, when they change.
        void Tune()
        {
            var t = new Native.Tuning
            {
                jump_height = Plugin.JumpHeight.Value,
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
            Native.sms_sim_set_tuning(sim, ref t);
        }

        // The water gauge, for the player playing this Mario.
        void OnGUI()
        {
            if (driving && localUser != null) MarioHud.Draw();
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
