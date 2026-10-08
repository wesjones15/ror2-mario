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
    // Controls (RoR2 binds): jump = A, primary = spray (half trigger), secondary = B (dive,
    // punch), utility = L (ground pound, crouch), sprint = R fully in (plant and spray),
    // special = X (switch nozzle).
    //
    // The camera follows a pivot of our own on the body, not Commando's (that one hangs off his
    // hidden, still animated and turning model, and swayed the camera against Mario), and this
    // runs before RoR2's camera (which moves in Update) so it sees this frame's smoothed body.
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
        // Body origin after the last two sim steps, for smoothing between FixedUpdates, and
        // where we last put it, to notice when something else (a teleporter, an out-of-bounds
        // rescue) moves the body.
        Vector3 prev, cur, written;
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
        Transform pivot, commandoPivot;

        void Awake()
        {
            body = GetComponent<CharacterBody>();
            motor = GetComponent<CharacterMotor>();
            input = GetComponent<InputBankTest>();
            direction = GetComponent<CharacterDirection>();
            health = GetComponent<HealthComponent>();
            cameraTarget = GetComponent<CameraTargetParams>();
            host = UnityHost.Table();
        }

        bool CanDrive =>
            !failed && body && motor && input
            && body.hasEffectiveAuthority
            && !body.currentVehicle
            && (!health || health.alive);

        void FixedUpdate()
        {
            if (!CanDrive)
            {
                if (driving)
                {
                    ShowMario(false);
                    UsePivot(false);
                }
                driving = false;
                return;
            }
            if (sim == IntPtr.Zero && !Create())
                return;
            if (motor.Motor.enabled)
                motor.Motor.enabled = false;
            if (!driving || (transform.position - written).sqrMagnitude > 0.25f)
                Resync();
            if (!driving) UsePivot(true);
            driving = true;

            var pad = ReadPad();
            // Facing from Mario toward the camera: the reverse of where the player aims.
            short cameraYaw = UnityHost.Yaw(-input.aimDirection);
            Native.sms_sim_step(sim, ref host, ref pad, cameraYaw, Time.fixedDeltaTime, MarioView.Handle);
            Native.sms_sim_state(sim, out var st);

            fixedSteps++;
            prev = cur;
            cur = UnityHost.ToUnity(st.pos) + feetToOrigin;
            // Where Update's smoothing starts this step: never ahead of what's drawn.
            transform.position = prev;
            written = prev;

            // mVel is per 30 Hz frame. RoR2 reads these for animation, camera and AI.
            motor.velocity = UnityHost.ToUnity(st.vel) * 30f;
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
                string status = NativeLib.Utf8(Native.sms_sim_status_name(sim));
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
            if (!driving) return;
            float a = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            var p = Vector3.Lerp(prev, cur, a);
            lagTicks = (1f - a) * Time.fixedDeltaTime * Native.sms_tick_hz();
            transform.position = p;
            written = p;
        }

        // Mario's models at the body's (smoothed) feet, facing as the sim says.
        void LateUpdate()
        {
            if (!driving || sim == IntPtr.Zero) return;
            MarioView.Update(sim, lagTicks);
            var models = MarioView.Models;
            if (models == null) return;
            var feet = transform.position - feetToOrigin;
            drawnFeet = feet;
            models.Update(UnityHost.Scale, d => (feet, new Quaternion(d.rot_x, d.rot_y, d.rot_z, d.rot_w)));
            ShowMario(true);
        }

        // Mario's models in place of Commando's, or back.
        void ShowMario(bool show)
        {
            if (!show) MarioView.Hide();
            GameSettings.HideModel(body, ref bodyHidden, show && MarioView.Models != null);
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
            var tuning = new Native.Tuning
            {
                jump_height = Plugin.JumpHeight.Value,
                jump_buffer = Plugin.JumpBuffer.Value,
                chain_grace = Plugin.ChainGrace.Value,
            };
            Native.sms_sim_set_tuning(sim, ref tuning);
            Plugin.Log.LogInfo($"Mario's sim started at {body.footPosition}");
            MarioView.Ensure();
            return true;
        }

        void Resync()
        {
            var feet = UnityHost.ToSim(transform.position - feetToOrigin);
            Native.sms_sim_teleport(sim, ref feet);
            prev = cur = written = transform.position;
        }

        // The camera on our pivot (at the height of Commando's, over the body's origin), or back
        // on Commando's.
        void UsePivot(bool ours)
        {
            if (!cameraTarget) return;
            if (ours)
            {
                if (cameraTarget.cameraPivotTransform == pivot && pivot) return;
                commandoPivot = cameraTarget.cameraPivotTransform;
                if (!pivot)
                {
                    pivot = new GameObject("MarioCameraPivot").transform;
                    pivot.SetParent(transform, false);
                    float h = commandoPivot ? commandoPivot.position.y - transform.position.y : 1.5f;
                    pivot.localPosition = new Vector3(0f, Mathf.Clamp(h, 0.3f, 3f), 0f);
                }
                cameraTarget.cameraPivotTransform = pivot;
            }
            else if (commandoPivot && cameraTarget.cameraPivotTransform == pivot)
                cameraTarget.cameraPivotTransform = commandoPivot;
        }

        Native.Pad ReadPad()
        {
            var move = input.moveVector;
            bool plant = input.sprint.down;
            bool spray = input.skill1.down;
            return new Native.Pad
            {
                use_move = 1,
                move_x = move.x,
                move_z = move.z,
                a = B(input.jump.down),
                b = B(input.skill2.down),
                l = B(input.skill3.down),
                l_analog = (byte)(input.skill3.down ? 255 : 0),
                r = B(plant),
                r_analog = (byte)(plant ? 255 : spray ? 150 : 0),
                x = B(input.skill4.down),
            };
        }

        static byte B(bool v) => v ? (byte)1 : (byte)0;

        // Debug.TraceJumps: one line per rendered frame of the player's camera, taken as it
        // renders, so it shows exactly where Mario and the camera were for that frame.
        static void OnPreCull(Camera cam)
        {
            var d = traced;
            if (!d || d.traceFrames <= 0) return;
            var player = GameSettings.PlayerCamera();
            if (!player || cam.transform != player) return;
            d.traceFrames--;
            float a = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            var pivot = d.cameraTarget && d.cameraTarget.cameraPivotTransform ? d.cameraTarget.cameraPivotTransform.position : Vector3.zero;
            var scr = cam.WorldToScreenPoint(d.drawnFeet);
            var c = cam.transform.position;
            var f = d.drawnFeet;
            var models = MarioView.Models;
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
            UsePivot(false);
            if (driving) MarioView.Hide();
            if (sim != IntPtr.Zero)
            {
                Native.sms_sim_free(sim);
                sim = IntPtr.Zero;
            }
        }
    }
}
