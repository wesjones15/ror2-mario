using System;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MarioTest
{
    // Sunshine's Shine Sprite as the trigger of a charged teleporter, for a Mario's own player.
    //
    // When the teleporter finishes charging (its boss, if any, dead) a Shine appears above its
    // middle, on that player's machine only, like the lunar coin counters it sits beside: nobody
    // else sees it, and it changes nothing for a player who isn't Mario. Touching it (walking or
    // jumping into it: Sunshine's `TShine::touchPlayer`, no button) puts Mario in his win pose
    // (libsms's `Mario::get_shine`; the other players' puppets show it too), and when he has landed
    // with the Shine over his head the demo runs as the game's `fireGetStar` starts it: the camera
    // flies the disc's path (`camera_demo_shine_get_*.bck`, DemoCamera), the Shine plays its clip
    // and glows, the fanfare plays and "SHINE!" flies in (MarioScreenFx). A moment into it the
    // teleporter is told to finish, as if it were interacted with, so the stage ends as the demo
    // does, for everyone (a client asks the server: MarioNet.ShineMsg).
    //
    // Where the stage goes is the teleporter's own choice: the stage the run was going to, or what a
    // Lunar Seer had already chosen (the exit controller's own destination). The one change is the
    // last stage before the moon, where the Celestial Portal waits beside the teleporter: the Shine
    // takes the moon, not the loop, as the Seer would have chosen it.
    internal sealed class MarioShine : MonoBehaviour
    {
        enum Phase { None, Appearing, Waiting, Touched, Demo }

        // Sunshine's TShine hit actor is the coin's (radius 70, height 100); Mario's cylinder adds
        // 50 and 160. The Shine hangs well above where Mario stands, so reaching it takes a jump
        // unless the host raises its reach (Shine.TouchRadius).
        const float ReachUp = 1.7f;
        const float AppearSeconds = 1.6f;
        /// Hits can't cut the pickup or the pose short: Sunshine stops the world for it. RoR2's
        /// hidden invincibility is granted as soon as the touch is accepted, and again by the
        /// server for a client's Mario (MarioNet.ShineGuardMsg), so neither the fall into the pose
        /// nor the demo can be interrupted.
        internal const float ShineGuardSeconds = 20f;
        // The Shine turns two degrees a 120 Hz tick, faster as it appears (`unk16C` falls from 7).
        const float SpinDegrees = 240f, AppearSpinDegrees = 840f;

        internal static MarioShine Instance { get; private set; }

        IntPtr view;
        ShineSource source;
        ModelView models;
        MarioFx fx;
        Native.ShineInfo info;
        bool tried, failed;

        Phase phase;
        TeleporterInteraction tele;
        Vector3 rest;
        float age, spin;

        // Touched: who, and how long since.
        MarioDriver toucher;
        float touchAge;

        // The demo: where Mario stood, how he faced, how long it has run.
        float demoAge, requestAt;
        Vector3 demoOrigin;
        short demoYaw;
        bool demoInside, requested;
        ShineCamera camera;
        CameraRigController rig;
        MarioDriver demoDriver;

        void Awake()
        {
            Instance = this;
            SceneManager.activeSceneChanged += SceneChanged;
            Run.onRunDestroyGlobal += RunEnded;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SceneManager.activeSceneChanged -= SceneChanged;
            Run.onRunDestroyGlobal -= RunEnded;
            Reset(false);
            models?.Dispose();
            fx?.Dispose();
            if (view != IntPtr.Zero) Native.sms_view_free(view);
            view = IntPtr.Zero;
        }

        void SceneChanged(Scene from, Scene to) => Reset(false);
        void RunEnded(Run run) => Reset(false);

        /// Back to nothing: the Shine gone, the camera and the pose let go (`endPose` when the demo
        /// ends without the stage: a refused request).
        void Reset(bool endPose)
        {
            if (phase == Phase.Demo || phase == Phase.Touched)
            {
                ReleaseCamera();
                if (endPose && demoDriver) demoDriver.EndShine();
            }
            phase = Phase.None;
            tele = null;
            toucher = null;
            demoDriver = null;
            requested = false;
            models?.HideAll();
            fx?.Hide();
        }

        bool EnsureView()
        {
            if (failed || !Plugin.AssetsReady) return false;
            if (view == IntPtr.Zero && !tried)
            {
                tried = true;
                view = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, 3);
                if (view == IntPtr.Zero || Native.sms_view_shine_info(view, out info) == 0)
                {
                    if (view != IntPtr.Zero) Native.sms_view_free(view);
                    view = IntPtr.Zero;
                    failed = true;
                    Plugin.Log.LogInfo("The Shine Sprite isn't in the assets folder (set Paths.Disc to your Sunshine disc " +
                        "image and restart: its files are fetched with the wipe pictures); a charged teleporter works as usual.");
                    return false;
                }
                source = new ShineSource(view);
                Plugin.Log.LogInfo($"The Shine Sprite: float {info.float_frames} frames, demo {info.demo_frames}, " +
                    $"camera {info.camera_outside_frames}/{info.camera_inside_frames}");
            }
            if (view == IntPtr.Zero) return false;
            if (models == null) models = ModelView.Create(source, "Sunshine Shine Sprite");
            if (fx == null && Shaders.CloudRemap()) fx = MarioFx.Create(view);
            return models != null;
        }

        static bool Charged(TeleporterInteraction t) => t && (int)t.activationState == 3;

        // ---- logic, on the physics step -------------------------------------------------------

        void FixedUpdate()
        {
            if (!Plugin.ShineEnabled.Value || !Run.instance || PauseManager.isPaused)
            {
                if (phase != Phase.None && !Run.instance) Reset(false);
                return;
            }
            float dt = Time.fixedDeltaTime;
            switch (phase)
            {
                case Phase.None: Look(); break;
                case Phase.Appearing:
                    if (!StillCharged()) break;
                    age += dt;
                    if (age >= AppearSeconds) { phase = Phase.Waiting; age = 0f; }
                    break;
                case Phase.Waiting:
                    if (!StillCharged()) break;
                    age += dt;
                    Touch();
                    break;
                case Phase.Touched:
                    touchAge += dt;
                    // The sim takes the pose at once and lands before the demo starts (the event that
                    // starts it). If he never has it (he died, or the touch was refused), the Shine waits
                    // again.
                    if (touchAge > 0.5f && (!toucher || !toucher.WinPose || !toucher.Alive))
                    {
                        phase = Phase.Waiting;
                        toucher = null;
                    }
                    break;
                case Phase.Demo:
                    // The pose is the demo: if it is gone (a hit that got past the guard, a death),
                    // stop the Shine too instead of playing it beside an idle Mario.
                    if (!demoDriver || !demoDriver.WinPose || !demoDriver.Alive) Reset(true);
                    else RunDemo(dt);
                    break;
            }
        }

        bool StillCharged()
        {
            if (Charged(tele)) return true;
            Reset(false);
            return false;
        }

        // A charged teleporter and a Mario to show a Shine to.
        void Look()
        {
            var t = TeleporterInteraction.instance;
            if (!Charged(t) || !MarioPresentation.Active || !EnsureView()) return;
            tele = t;
            rest = t.transform.position + Vector3.up * Mathf.Max(0.2f, Plugin.ShineHeight.Value);
            phase = Phase.Appearing;
            age = 0f;
            spin = 0f;
            // `TShine::appearWithTime`: MSD_SE_SHINE_APPEAR where it shows up.
            Play("MSD_SE_SHINE_APPEAR", rest);
            Plugin.Log.LogInfo($"A Shine Sprite appears over the teleporter at {rest}");
        }

        static void Play(string name, Vector3 at)
        {
            var player = MarioView.Player;
            if (player == null || player.Handle == IntPtr.Zero) return;
            var p = UnityHost.ToSim(at);
            Native.sms_view_sound(player.Handle, NativeLib.CString(name), ref p);
        }

        // `TShine::control` asks for MSD_SE_SHINE_EXIST every frame while it waits (it stops once the
        // Shine is taken): the hum of a Shine from where it hangs.
        static void Hum(Vector3 at)
        {
            var player = MarioView.Player;
            if (player == null || player.Handle == IntPtr.Zero) return;
            var p = UnityHost.ToSim(at);
            Native.sms_view_hold_sound(player.Handle, NativeLib.CString("MSD_SE_SHINE_EXIST"), ref p);
        }

        // `TShine::touchPlayer`: his cylinder (feet to head, wall radius) against the Shine's.
        void Touch()
        {
            var driver = MarioDriver.Player;
            if (driver == null || driver.WinPose || !driver.Alive) return;
            var feet = driver.Feet;
            var delta = feet - rest;
            float reach = Mathf.Max(0.3f, Plugin.ShineTouchRadius.Value);
            // Over its base and under its top, his head reaching the Shine's foot.
            if (delta.x * delta.x + delta.z * delta.z > reach * reach
                || feet.y + UnityHost.MarioHeight * UnityHost.Scale < rest.y
                || feet.y > rest.y + ReachUp) return;
            // Mario turns to the camera for the pose, as the demo's camera path expects him to face it.
            var cam = GameSettings.PlayerCamera();
            var toCamera = cam ? cam.position - feet : -driver.transform.forward;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 0.01f) toCamera = -driver.transform.forward;
            if (!driver.TouchShine(rest, UnityHost.Yaw(toCamera))) return;
            GuardShine(driver.Body);
            toucher = driver;
            touchAge = 0f;
            phase = Phase.Touched;
            Plugin.Log.LogInfo("Mario touches the Shine Sprite");
        }

        /// Sunshine stops the world while Mario takes a Shine, so a hit can't cut the fall into
        /// the pose or the demo short. The server grants the body's hidden invincibility; a client
        /// asks it to for its own Mario.
        static void GuardShine(CharacterBody body)
        {
            if (!body) return;
            if (NetworkServer.active) body.AddTimedBuff(RoR2Content.Buffs.HiddenInvincibility, ShineGuardSeconds);
            else MarioNet.SendShineGuard(body);
        }

        /// The sim's `ShineDemo` event (inside the driver's step, `body` his): he has landed with the
        /// Shine over his head. `feet` and `yaw` are in sim units.
        internal void DemoStarted(CharacterBody body, Native.Vec3 feet, short yaw)
        {
            // Called from the sim's own callback: nothing may throw back into it.
            try { StartDemo(body, feet, yaw); }
            catch (Exception e) { Plugin.Log.LogError($"Shine Get: {e}"); }
        }

        void StartDemo(CharacterBody body, Native.Vec3 feet, short yaw)
        {
            var driver = body ? body.GetComponent<MarioDriver>() : null;
            if (driver == null || driver != MarioDriver.Player || !Plugin.ShineEnabled.Value) return;
            if (phase != Phase.Touched && phase != Phase.Waiting)
            {
                // A Shine this machine didn't offer (nothing to do): let him stand.
                return;
            }
            phase = Phase.Demo;
            demoDriver = driver;
            demoAge = 0f;
            requested = false;
            demoOrigin = UnityHost.ToUnity(feet);
            demoYaw = yaw;
            demoInside = Physics.Raycast(demoOrigin + Vector3.up * (UnityHost.MarioHeight * UnityHost.Scale), Vector3.up,
                12f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
            // How long until the stage is told to end: the demo plays for ShineSeconds in all, and the
            // teleporter then takes its own time to send everyone out.
            float outDuration = TeleportOutSeconds(tele ? tele.sceneExitController : null);
            requestAt = Mathf.Max(0.5f, Plugin.ShineSeconds.Value - Mathf.Clamp(outDuration, 0f, 8f));
            MarioScreenFx.ShineGet();
            SpawnMusic.PlayCue("shineget");
            // In VR the view stays the player's own head (MarioVr): no shot taken over.
            if (Plugin.ShineCameraOn.Value && !MarioVr.Character.FirstPerson(driver.GetComponent<CharacterBody>())) TakeCamera(driver);
            Plugin.Log.LogInfo($"Shine Get: the stage ends in about {Plugin.ShineSeconds.Value:F1} s " +
                $"(teleporter asked at {requestAt:F1} s, camera {(demoInside ? "inside" : "outside")}); Mario: {driver.DrawNote()}");
        }

        // How long RoR2's exit takes from the teleporter finishing to the scene changing (not a public
        // field); a few seconds when it can't be read.
        static readonly System.Reflection.FieldInfo outDuration = typeof(SceneExitController).GetField(
            "teleportOutDuration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);

        static float TeleportOutSeconds(SceneExitController exit)
        {
            try
            {
                if (exit && outDuration != null && outDuration.GetValue(exit) is float seconds && seconds > 0f) return seconds;
            }
            catch (Exception) { }
            return 3f;
        }

        void RunDemo(float dt)
        {
            float was = demoAge;
            demoAge += dt;
            // How Mario is drawn a second in, and once the teleporter has been asked.
            if (demoDriver && ((was < 1f && demoAge >= 1f) || (was < requestAt + 0.5f && demoAge >= requestAt + 0.5f)))
                Plugin.Log.LogInfo($"Shine Get at {demoAge:F1} s: Mario {demoDriver.DrawNote()}");
            if (!requested && demoAge >= requestAt)
            {
                requested = true;
                AskForTheStage();
            }
            // A stage that never came (the request was refused: somebody else's teleporter already
            // finished, or it isn't charged any more): the pose ends and the Shine is gone.
            if (demoAge > Plugin.ShineSeconds.Value + 12f)
            {
                Plugin.Log.LogInfo("The stage didn't end; Mario puts the Shine away");
                Reset(true);
            }
        }

        void AskForTheStage()
        {
            var body = demoDriver ? demoDriver.Body : null;
            if (!tele || !body) return;
            if (NetworkServer.active) Trigger(tele, body);
            else MarioNet.SendShine(tele, body);
        }

        // ---- the server's side: the teleporter's trigger ------------------------------------------

        /// The server finishes a charged teleporter for `by`, as his interacting with it would:
        /// everyone is sent out, to the stage it was going to, or a Lunar Seer's choice, or on the last
        /// stage before the moon, the moon.
        internal static bool Trigger(TeleporterInteraction tele, CharacterBody by)
        {
            if (!NetworkServer.active || !Charged(tele)) return false;
            var exit = tele.sceneExitController;
            if (exit && exit.useRunNextStageScene && !exit.isAlternatePath && tele.shouldAttemptToSpawnMSPortal)
            {
                var moon = Moon();
                if (moon)
                {
                    exit.destinationScene = moon;
                    exit.useRunNextStageScene = false;
                    Plugin.Log.LogInfo($"The Shine takes the Celestial Portal's way: {moon.cachedName}");
                }
            }
            if (by) by.AddTimedBuff(RoR2Content.Buffs.HiddenInvincibility, 10f);
            var interactor = by ? by.GetComponent<Interactor>() : null;
            if (interactor) ((IInteractable)tele).OnInteractionBegin(interactor);
            else tele.mainStateMachine.SetNextState(new TeleporterInteraction.FinishedState());
            Plugin.Log.LogInfo("The Shine ends the stage");
            return true;
        }

        static SceneDef Moon()
        {
            foreach (var e in FindObjectsOfType<SceneExitController>())
            {
                var d = e ? e.destinationScene : null;
                if (d && d.cachedName != null && d.cachedName.StartsWith("moon")) return d;
            }
            return SceneCatalog.GetSceneDefFromSceneName("moon2") ?? SceneCatalog.GetSceneDefFromSceneName("moon");
        }

        // ---- the camera -----------------------------------------------------------------------------

        void TakeCamera(MarioDriver driver)
        {
            var user = driver.LocalUser;
            var r = user != null ? user.cameraRigController : null;
            if (!r) return;
            if (!camera) camera = driver.gameObject.AddComponent<ShineCamera>();
            camera.Init(this);
            rig = r;
            rig.SetOverrideCam(camera, 0.35f);
        }

        void ReleaseCamera()
        {
            if (rig && camera && rig.IsOverrideCam(camera)) rig.SetOverrideCam(null, 0.5f);
            rig = null;
            if (camera) Destroy(camera);
            camera = null;
        }

        /// The demo camera now, in the world; false outside the demo.
        internal bool Shot(out Vector3 position, out Quaternion rotation, out float fov)
        {
            position = default;
            rotation = Quaternion.identity;
            fov = 50f;
            if (phase != Phase.Demo || view == IntPtr.Zero) return false;
            float frame = demoAge * 60f;
            float length = demoInside ? info.camera_inside_frames : info.camera_outside_frames;
            if (length <= 0f || Native.sms_view_camera_shot(view, (byte)(demoInside ? 1 : 0), Mathf.Min(frame, length), out var s) == 0)
                return false;
            // The path is Mario's model space (+Z forward, right-handed): mirrored across X like his
            // models, then turned to how he faces.
            var turn = Quaternion.AngleAxis(demoYaw * 180f / 32768f, Vector3.up);
            Vector3 Local(Native.Vec3 v) => turn * (new Vector3(-v.x, v.y, v.z) * UnityHost.Scale);
            var eye = Local(s.eye);
            var at = Local(s.at);
            var up = turn * new Vector3(-s.up.x, s.up.y, s.up.z);
            position = demoOrigin + eye;
            var look = at - eye;
            rotation = look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look, up) : Quaternion.identity;
            fov = s.fov;
            return true;
        }

        // ---- drawing --------------------------------------------------------------------------------

        void LateUpdate()
        {
            if (phase == Phase.None || view == IntPtr.Zero || models == null || PauseManager.isPaused) return;
            float dt = Time.deltaTime;
            Vector3 pos = rest;
            byte clip = 0;
            float frame = 0f, glow = 1f;
            float yawDegrees;
            switch (phase)
            {
                case Phase.Appearing:
                {
                    // It rises from the teleporter, spinning fast and slowing.
                    float t = Mathf.Clamp01(age / AppearSeconds), ease = 1f - (1f - t) * (1f - t) * (1f - t);
                    pos = rest - Vector3.up * ((1f - ease) * Mathf.Max(0.2f, Plugin.ShineHeight.Value));
                    spin += Mathf.Lerp(AppearSpinDegrees, SpinDegrees, ease) * dt;
                    break;
                }
                case Phase.Touched:
                    // `shine_float`: it floats up as he touches it.
                    clip = 1;
                    frame = Mathf.Min(touchAge * 60f, info.float_frames);
                    spin += SpinDegrees * dt;
                    break;
                case Phase.Demo:
                    // Held over Mario's head by `shine_demo_shine_get`, at his feet and turned as he is,
                    // its glow half size.
                    clip = 2;
                    frame = Mathf.Min(demoAge * 60f, info.demo_frames);
                    pos = demoOrigin;
                    glow = 0.5f;
                    break;
                default:
                    spin += SpinDegrees * dt;
                    break;
            }
            yawDegrees = phase == Phase.Demo ? demoYaw * 180f / 32768f : spin;
            var cam = GameSettings.PlayerCamera();
            var fwd = cam ? cam.forward : Vector3.forward;
            var camUp = cam ? cam.up : Vector3.up;
            var right = Vector3.Cross(camUp, fwd).normalized;
            var sim = UnityHost.ToSim(pos);
            var frameArgs = new Native.ShineFrame
            {
                dt = dt,
                pos = sim,
                yaw = (short)Mathf.RoundToInt(yawDegrees / 180f * 32768f),
                clip = clip,
                frame = frame,
                scale = glow,
                mirror = 1,
                right = new Native.Vec3(right.x, right.y, right.z),
                up = new Native.Vec3(camUp.x, camUp.y, camUp.z),
            };
            // Humming from the moment it shows up until it is taken (the pose's own start).
            if (phase != Phase.Demo && !PauseManager.isPaused) Hum(pos + Vector3.up * (0.71f));
            Native.sms_view_shine_update(view, ref frameArgs);
            var rot = Quaternion.Euler(0f, yawDegrees, 0f);
            source.draw = new ViewDraw
            {
                key = 1, model = 0, pos = new ViewVec3 { x = sim.x, y = sim.y, z = sim.z },
                rot_x = rot.x, rot_y = rot.y, rot_z = rot.z, rot_w = rot.w, alpha = 1f,
            };
            models.Update(UnityHost.Scale);
            fx?.Update(view, UnityHost.Scale, Vector3.zero);
        }

        // One native model, one host-placed instance (the coin's way: MarioCoins).
        sealed class ShineSource : IViewSource
        {
            readonly SmsViewSource native;
            public ViewDraw draw;
            public ShineSource(IntPtr view) { native = new SmsViewSource(view); }
            public uint ModelCount() => native.ModelCount();
            public bool Model(uint m, out ViewModelInfo info) => native.Model(m, out info);
            public uint Batches(uint m, ViewBatch[] o, uint max) => native.Batches(m, o, max);
            public uint Indices(uint m, int[] o, uint max) => native.Indices(m, o, max);
            public uint Uvs(uint m, float[] o, uint max) => native.Uvs(m, o, max);
            public bool Texture(uint id, out ViewTexture info, byte[] rgba, uint len) => native.Texture(id, out info, rgba, len);
            public uint Draws(ViewDraw[] output, uint max)
            {
                if (output == null) return 1;
                if (max == 0) return 0;
                output[0] = draw;
                return 1;
            }
            public IntPtr Positions(out uint count) => native.Positions(out count);
            public IntPtr Normals(out uint count) => native.Normals(out count);
            public IntPtr Colors(out uint count) => native.Colors(out count);
            public IntPtr TextureIds(out uint count) => native.TextureIds(out count);
            public IntPtr FrameUvs(out uint count) => native.FrameUvs(out count);
        }
    }

    // The demo camera as RoR2's camera rig takes it: a component on Mario's body (RoR2's camera
    // modes cast the override to UnityEngine.Object for their null checks, as Olimar's does).
    internal sealed class ShineCamera : MonoBehaviour, ICameraStateProvider
    {
        MarioShine owner;

        public void Init(MarioShine owner) => this.owner = owner;

        public void GetCameraState(CameraRigController rig, ref CameraState state)
        {
            if (owner && owner.Shot(out var position, out var rotation, out var fov))
            {
                state.position = position;
                state.rotation = rotation;
                state.fov = fov;
            }
        }

        public bool IsUserLookAllowed(CameraRigController rig) => false;
        public bool IsUserControlAllowed(CameraRigController rig) => true;
        public bool IsHudAllowed(CameraRigController rig) => true;
    }
}
