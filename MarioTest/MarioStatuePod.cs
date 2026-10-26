using System;
using System.Collections.Generic;
using EntityStates;
using EntityStates.SurvivorPod;
using RoR2;
using Ror2Common;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // Keep the stock networked seat, animated descent and its sound events. The approved
    // native entrance owns only the visible model, X sequence and final ejection position.
    internal sealed class MarioStatuePod : MonoBehaviour
    {
        internal static readonly string[] AssetFiles = { "05_gate01.bmd", "05_gate01.btk", "05_gate01.brk", "plaza.bmd", "scene.bin", "efdokangate.bmd", "manhole.bmd", "monumentshine.bmd", "palmleaf.bmd", "palmnormal.bmd", "ms_gatewind_a.jpa", "ms_gatewind_a2.jpa", "ms_gatewind_a3.jpa", "ms_gatewind_b.jpa" };
        public Transform travel;
        public Vector3 gateLocal;
        public Vector3 landingLocal;
        public Quaternion landingRotationLocal;
        VehicleSeat seat;
        SurvivorPodController pod;
        EntityStateMachine machine;
        IntPtr view;
        ModelView models;
        MarioFx fx;
        bool tried, custom, activated, voiced, released;
        float idle;
        float releasedAt;
        CharacterBody passenger;
        bool hideFallback;
        Native.Vec3 finalFeet;
        GameObject collision;
        Transform originalCamera, portalCamera;
        [SerializeField] ParticleSystem descentFlames;
        Vector3 fireLocal, originalFirePosition, originalFireScale;
        Quaternion originalFireRotation;
        ParticleSystemScalingMode originalFireScaling;
        ParticleSystemSimulationSpace originalFireSpace;
        bool fireConfigured, fireStarted, descending, luigiLoaded;
        bool anchored, exitAligned;
        Vector3 landedOrigin, landedExit;
        Quaternion landedRotation;
        // The exit clip drops the rider to the statue's base, which on a slope is inside the
        // hill: its last frames draw his feet in the dirt, and the pod then lets the body out
        // under the terrain (it pops up when the motor is handed over). Cut the clip at the last
        // pose still above the ground, hold it there, and let the body out on that ground.
        bool exitCut, exitPlaced;
        float cutAt, lastAt = -1f;
        Vector3 cutFeet, lastFeet;
        uint entranceSkin = uint.MaxValue;
        readonly Dictionary<Renderer, bool> stockRenderers = new Dictionary<Renderer, bool>();
        readonly List<Renderer> scanRenderers = new List<Renderer>();
        readonly List<ParticleSystem> scanParticles = new List<ParticleSystem>();
        readonly HashSet<ParticleSystem> stoppedParticles = new HashSet<ParticleSystem>();
        readonly Dictionary<Collider, bool> stockColliders = new Dictionary<Collider, bool>();
        readonly List<Collider> scanColliders = new List<Collider>();
        float scanAt;
        bool handedOff;
        Func<ViewDraw, (Vector3, Quaternion)?> placement;
        Func<ViewDraw, bool> hideDraw;
        public bool Ready => custom;
        /// The exit clip was ended early because the next pose would draw the rider into the hill.
        public bool Cut => exitCut;
        public bool Finished { get; private set; }

        public static void Register(CharacterBody body, RoR2.ContentManagement.ContentPack pack)
        {
            var original = body.preferredPodPrefab;
            if (!original) original = LegacyResourcesAPI.Load<GameObject>("Prefabs/NetworkedObjects/SurvivorPod");
            if (!original) { Plugin.Log.LogWarning("Mario statue: no vanilla pod prefab"); return; }
            var prefab = Prefabs.Clone(original, "MarioSunshineStatuePod", Plugin.Guid);
            var seat = prefab.GetComponent<VehicleSeat>();
            var controller = prefab.GetComponent<SurvivorPodController>();
            var locator = prefab.GetComponent<ModelLocator>();
            var model = locator ? locator.modelTransform : null;
            var children = model ? model.GetComponent<ChildLocator>() : null;
            var travel = children ? children.FindChild("Travel") : null;
            if (!seat || !controller || !travel || !seat.exitPosition) {
                Plugin.Log.LogWarning("Mario statue: vanilla pod has no seat/Travel/exit; retaining vanilla pod");
                DestroyImmediate(prefab); return;
            }
            var component = prefab.AddComponent<MarioStatuePod>();
            component.travel = travel;
            // SurvivorPod/Base/mdlEscapePod/EscapePodArmature/Base/Flames:
            // mesh-particle emitter -470276994286075641, donut5Mesh / matEntryBurn.
            // Exact asset and 3.5x placement proven in portal_fast --descent-fire.
            component.ResolveDescentFire();
            // The native landing ends 308.53 cm in front of the M. Anchor that end to the
            // stock exit point; no offset into the pedestal or displacement of the spawn.
            var forward = seat.exitPosition.forward; forward.y = 0;
            if (forward.sqrMagnitude < .01f) forward = prefab.transform.forward;
            var rotation = Quaternion.LookRotation(forward.normalized);
            var capsule = body.GetComponent<CapsuleCollider>();
            var feet = capsule ? capsule.transform.TransformPoint(capsule.center - Vector3.up * (capsule.height * .5f)) : body.footPosition;
            var originToFeet = body.transform.position - feet;
            var landing = seat.exitPosition.position - originToFeet - rotation * Vector3.forward * (308.53f * UnityHost.Scale);
            component.gateLocal = travel.InverseTransformPoint(landing);
            // ExitPosition is a sibling of the animated Base bone. Keep the same landing
            // point in the unanimated pod root, independently of InitialSpawn's slanted path.
            component.landingLocal = prefab.transform.InverseTransformPoint(landing);
            component.landingRotationLocal = Quaternion.Inverse(prefab.transform.rotation) * rotation;
            prefab.GetComponent<EntityStateMachine>().initialStateType = new SerializableEntityStateType(typeof(MarioPodDescent));
            body.preferredPodPrefab = prefab;
            pack.networkedObjectPrefabs.Add(new[] { prefab });
            pack.entityStateTypes.Add(new[] { typeof(MarioPodDescent), typeof(MarioPodLanded), typeof(MarioPodExit), typeof(MarioPodSpent) });
        }

        void Awake()
        {
            seat = GetComponent<VehicleSeat>(); pod = GetComponent<SurvivorPodController>(); machine = GetComponent<EntityStateMachine>();
            ResolveDescentFire();
            placement = Place;
            hideDraw = d => handedOff && d.kind < 240;
        }

        Vector3 Origin
        {
            get {
                if (anchored) return landedOrigin;
                var animated = travel.TransformPoint(gateLocal);
                var landing = transform.TransformPoint(landingLocal);
                // Keep the stock height clock and impact timing; take X/Z from its
                // original landing point so the upright statue travels straight down.
                return new Vector3(landing.x, animated.y, landing.z);
            }
        }

        Quaternion Rotation
        {
            get {
                if (anchored) return landedRotation;
                var forward = (transform.rotation * landingRotationLocal) * Vector3.forward;
                forward.y = 0f;
                return Quaternion.LookRotation(forward.sqrMagnitude > .0001f ? forward : Vector3.forward);
            }
        }

        public void AlignExit()
        {
            // Runtime motor/capsule data is authoritative once the pod has landed.
            // Reconcile any prefab-only foot offset before the exit can be requested.
            if (!travel || activated) return;
            var body = seat.currentPassengerBody;
            var rotation = Rotation;
            var origin = Origin;
            landedExit = seat.exitPosition.position;
            if (body) {
                origin = landedExit - (body.transform.position - body.footPosition)
                    - rotation * Vector3.forward * (308.53f * UnityHost.Scale);
                exitAligned = true;
            }
            // Travel is an animated pod bone. After impact the custom statue, camera,
            // pedestal and eventual ejection must share one fixed world-space anchor.
            landedOrigin = origin; landedRotation = rotation; anchored = true;
            if (portalCamera) { portalCamera.SetParent(transform, true); PlaceCamera(); }
            if (collision) {
                collision.transform.SetParent(transform, true);
                collision.transform.SetPositionAndRotation(Origin, Rotation);
            }
        }

        void PlaceCamera()
        {
            if (!portalCamera) return;
            portalCamera.position = Origin + Rotation * new Vector3(-5.5f, 3.8f, 11.5f) * (UnityHost.Scale * 100f);
            portalCamera.rotation = Quaternion.LookRotation(Origin + Rotation * new Vector3(0, 1.8f, 1.4f) * (UnityHost.Scale * 100f) - portalCamera.position);
        }

        void Prepare()
        {
            if (tried || !Plugin.AssetsSettled) return;
            tried = true;
            if (!Plugin.AssetsReady || !Plugin.DrawModel.Value || !Run.instance || Run.instance.stageClearCount != 0) return;
            try {
                view = Native.sms_view_entrance_fast_new(NativeLib.CString(Plugin.AssetsDir), (byte)(Plugin.Sound.Value ? 1 : 0));
                if (view == IntPtr.Zero) throw new InvalidOperationException("statue/portal assets unavailable");
                // The seat/loadout may arrive after pod preparation. ModelView fixes its
                // model table at creation, so preload Luigi and select the rider separately.
                luigiLoaded = MarioSkin.Ready && MarioSkin.Load(view);
                SyncRiderSkin();
                models = ModelView.Create(new SmsViewSource(view), "Mario Sunshine statue", transform, translucentOpacity: 1f);
                if (models == null) throw new InvalidOperationException("statue renderer unavailable");
                // MarioFx uploads world-space vertices; keep its renderer at world identity.
                fx = MarioFx.Create(view);
                if (fx == null) throw new InvalidOperationException("portal particle renderer unavailable");
                custom = true;
                // Keep animator/audio events and the seat. Hide every stock renderer,
                // including trails/lines/particles; animation cannot re-enable forceRenderingOff.
                HideStockVisuals();
                var camera = new GameObject("Mario portal camera").transform;
                camera.SetParent(anchored ? transform : travel, false);
                originalCamera = pod.cameraBone;
                portalCamera = camera;
                PlaceCamera();
                pod.cameraBone = camera;
                if (Native.sms_view_entrance_bounds(view, out var lo, out var hi) != 0) {
                    collision = new GameObject("Pianta statue pedestal collision");
                    collision.layer = LayerIndex.world.intVal;
                    collision.transform.SetParent(anchored ? transform : travel, false);
                    collision.transform.position = Origin; collision.transform.rotation = Rotation;
                    var box = collision.AddComponent<BoxCollider>();
                    box.center = new Vector3(-(lo.x + hi.x), lo.y + hi.y, lo.z + hi.z) * (.5f * UnityHost.Scale);
                    box.size = new Vector3(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z) * UnityHost.Scale;
                    // In-game feedback: the mesh's luminous region remains below its
                    // emitter. Raise it 600 cm; the preview pivot is not Unity height proof.
                    fireLocal = new Vector3(-(lo.x + hi.x) * .5f, lo.y + 600f, (lo.z + hi.z) * .5f) * UnityHost.Scale;
                    ConfigureDescentFire();
                    // The podium alone is 8.0 x 5.2 x 8.0 m. The map's baked statue shadow
                    // and a floor triangle used to be selected with it and made this
                    // 10.3 x 5.2 x 11.0 m, hanging geometry off the base.
                    Plugin.Log.LogInfo($"Mario statue podium collider {box.size.x:0.0} x {box.size.y:0.0} x {box.size.z:0.0} m");
                }
                Plugin.Log.LogInfo("Mario stage-one statue entrance ready (05_gate01 / particle 0x40)");
            } catch (Exception e) {
                Plugin.Log.LogWarning("Mario statue: " + e.Message + "; using vanilla pod");
                Cleanup();
            }
        }

        void LateUpdate()
        {
            using var timing = MarioPerformance.Measure(MarioPerformance.Part.Portal);
            Prepare();
            if (!custom || !travel || RunAudio.Ended) return;
            if (anchored && !exitAligned && seat.currentPassengerBody && !activated) AlignExit();
            if (collision && !anchored) collision.transform.SetPositionAndRotation(Origin, Rotation);
            PlaceCamera();
            if (!activated) SyncRiderSkin();
            PlaceDescentFire();
            HideStockVisuals();
            if (!PauseManager.isPaused) idle += Time.deltaTime;
            float raw = activated && machine.state is MarioPodExit exit ? exit.Elapsed : idle;
            // A cut holds the clip on its last pose above the ground until the body takes over.
            float time = exitCut ? Mathf.Min(raw, cutAt) : raw;
            var frame = new Native.ViewFrame { forward = new Native.Vec3(0, 0, 1), up = new Native.Vec3(0, 1, 0), volume = GameSettings.SfxVolume() * Mathf.Clamp(Plugin.SoundVolume.Value, 0, 2) };
            var camera = GameSettings.PlayerCamera();
            if (camera) {
                var inv = Quaternion.Inverse(Rotation);
                var f = inv * camera.forward; var u = inv * camera.up;
                frame.forward = new Native.Vec3(f.x, f.y, f.z); frame.up = new Native.Vec3(u.x, u.y, u.z);
                frame.eye = UnityHost.ToSim(inv * (camera.position - Origin));
            }
            Native.sms_view_set_paused(view, (byte)(PauseManager.isPaused ? 1 : 0));
            bool playableDraws = passenger && (passenger.GetComponent<MarioDriver>()?.Drawing == true
                || passenger.GetComponent<MarioPuppet>()?.Drawing == true);
            handedOff = released && (playableDraws || !passenger || !passenger.healthComponent.alive || Time.time - releasedAt > 2f);
            if (handedOff) {
                // HideModel uses forceRenderingOff, not a counter. The normal driver now
                // owns that flag; clearing it here would briefly reveal Commando.
                if (playableDraws) hideFallback = false;
                else GameSettings.HideModel(passenger, ref hideFallback, false);
            }
            // State 2 removes the character from native processing after the real body takes over.
            var result = Native.sms_view_entrance_update(view, time, (byte)(handedOff ? 2 : activated ? 1 : 0), ref frame, out finalFeet);
            if (result == 0) { Finished = true; return; }
            Finished = result == 2;
            // The clip's own landing point is the statue's base. Where that is inside the hill,
            // end the clip at the last pose still above the ground and redraw that pose (the
            // update above already drew the frame that entered it).
            bool exiting = machine.state is MarioPodExit;
            if (activated && exiting && !released && !Finished && !exitCut && CutIfEnteringGround(time))
                Native.sms_view_entrance_update(view, cutAt, 1, ref frame, out finalFeet);
            if (activated && (released || Finished || exitCut)) PlaceEjected();
            models.Update(UnityHost.Scale, placement, hideDraw);
            fx.Update(view, UnityHost.Scale, Vector3.zero, Matrix4x4.TRS(Origin, Rotation, Vector3.one));
            if (activated && !voiced && time >= 0.8f) {
                voiced = true;
                Native.sms_view_mario_voice(view, NativeLib.CString("MSD_SE_MV23_JUMP_LARGE_01"));
            }
        }

        (Vector3, Quaternion)? Place(ViewDraw d) => (Origin + Rotation * new Vector3(-d.pos.x, d.pos.y, d.pos.z) * UnityHost.Scale,
            Rotation * new Quaternion(d.rot_x, d.rot_y, d.rot_z, d.rot_w));

        /// The exit clip's feet (sim space, statue-local) in the world, placed like the draws.
        Vector3 ToWorld(Native.Vec3 p) =>
            Origin + Rotation * new Vector3(-p.x, p.y, p.z) * UnityHost.Scale;

        static readonly RaycastHit[] groundHits = new RaycastHit[8];

        /// True when solid ground sits above the rider's animated feet: the clip would draw him
        /// into the hill. A floor below him is the landing, not an entry, and the pod's own
        /// solids (the statue's pedestal box, the stock shell) are not ground.
        bool GroundAbove(Vector3 feet, out Vector3 surface)
        {
            const float above = 4f, below = 3f;
            surface = feet;
            int n = Physics.RaycastNonAlloc(feet + Vector3.up * above, Vector3.down, groundHits,
                above + below, WorldProbe.Mask, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = groundHits[i];
                if (!h.collider || Ours(h.collider) || h.point.y <= feet.y + .02f) continue;
                if (h.distance >= nearest) continue;
                nearest = h.distance;
                surface = h.point;
            }
            return !float.IsPositiveInfinity(nearest);
        }

        bool Ours(Collider c) =>
            c.transform.IsChildOf(transform) || (passenger && c.transform.IsChildOf(passenger.transform));

        /// Ends the exit clip at the last pose above the ground; the body is let out there.
        bool CutIfEnteringGround(float time)
        {
            var feet = ToWorld(finalFeet);
            if (!GroundAbove(feet, out var surface)) {
                lastAt = time;
                lastFeet = feet;
                return false;
            }
            exitCut = true;
            if (lastAt >= 0f) {
                cutAt = lastAt;
                cutFeet = lastFeet;
            } else {
                // Already inside it on the first frame (a hill above the reveal): use its surface.
                cutAt = 0f;
                cutFeet = surface;
            }
            Plugin.Log.LogInfo($"Statue exit cut at {cutAt:F2} s (feet {feet} would enter the ground); the body is let out at {cutFeet}");
            return true;
        }

        /// Puts the rider's body where the clip ended (or was cut) once the seat lets him go.
        /// Every peer places the copy it owns, so a client's body is not left at the raw exit.
        /// The landing is validated: on a slope the exit point sits inside the hill, and the sim
        /// then falls through to whatever room is below it.
        void PlaceEjected()
        {
            if (exitPlaced || !passenger || passenger.currentVehicle) return;
            exitPlaced = true;
            if (!passenger.hasEffectiveAuthority || !passenger.healthComponent || !passenger.healthComponent.alive) return;
            var height = UnityHost.MarioHeight * UnityHost.Scale;
            var target = exitCut ? cutFeet : ToWorld(finalFeet);
            // A point on or just above the ground is used as it is (the stock exit, or a cut just
            // above the slope). One with ground above it is inside the hill: step out to clear
            // standing ground, or the sim takes him through it into whatever room is below.
            if (GroundAbove(target, out _)) {
                Vector3 clear;
                if (UnityHost.RecoveryGround(target, height * .25f, height * .5f, out clear)
                    || NearestGround(target, height, out clear)) target = clear;
            }
            var feet = passenger.footPosition;
            if ((target - feet).sqrMagnitude <= .0004f) return;
            passenger.transform.position += target - feet;
            Plugin.Log.LogInfo($"Statue exit: body at {passenger.footPosition} ({(exitCut ? "clip cut" : "clip complete")})");
        }

        /// Clear standing ground within three body heights of `sample`, for an exit point that
        /// sits in the terrain.
        static bool NearestGround(Vector3 sample, float height, out Vector3 feet)
        {
            for (int ring = 1; ring <= 3; ring++)
                for (int i = 0; i < 8; i++) {
                    float a = i * (Mathf.PI * .25f) + ring * .4f;
                    var at = sample + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * (ring * height * .5f);
                    if (UnityHost.RecoveryGround(at, height * 2f, height * 3f, out feet)) return true;
                }
            feet = sample;
            return false;
        }

        void SyncRiderSkin()
        {
            var rider = seat.currentPassengerBody;
            if (!rider || view == IntPtr.Zero) return;
            uint selected = luigiLoaded ? MarioSkin.Selected(rider) : MarioSkin.Mario;
            if (selected == entranceSkin) return;
            entranceSkin = selected;
            Native.sms_view_skin(view, (byte)selected);
            Native.sms_view_set_voice_pitch(view, selected == MarioSkin.Luigi ? MarioSkin.VoicePitch : 1f);
            Plugin.Log.LogInfo("Statue entrance rider: " + (selected == MarioSkin.Luigi ? "Luigi" : "Mario"));
        }

        void ConfigureDescentFire()
        {
            if (fireConfigured) return;
            ResolveDescentFire();
            if (!descentFlames) {
                Plugin.Log.LogWarning("Statue descent fire unavailable: exact EscapePodArmature/Base/Flames emitter missing");
                return;
            }
            var t = descentFlames.transform;
            originalFirePosition = t.localPosition;
            originalFireScale = t.localScale;
            originalFireRotation = t.localRotation;
            var main = descentFlames.main;
            originalFireScaling = main.scalingMode;
            originalFireSpace = main.simulationSpace;
            // Local scaling ignored animated ancestor scale. Hierarchy scales the shape
            // AND its mesh particles. Keep the authored rate, lifetime, material and lights.
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            // This is a shell attached to the falling statue. Existing particles must
            // follow its current frame instead of remaining at earlier world positions.
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            fireConfigured = true;
            PlaceDescentFire();
            // Discard particles emitted at the old pod position before the first LateUpdate.
            descentFlames.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            StartDescentFire();
        }

        void ResolveDescentFire()
        {
            // Resolve on the spawned clone too; do not depend on a prefab-side reference
            // surviving registration or on the relative Awake order of the state machine.
            if (descentFlames && descentFlames.transform.IsChildOf(transform)) return;
            var locator = GetComponent<ModelLocator>();
            var model = locator ? locator.modelTransform : null;
            var flames = model ? model.Find("EscapePodArmature/Base/Flames") : null;
            descentFlames = flames ? flames.GetComponent<ParticleSystem>() : null;
        }

        public void BeginDescent()
        {
            descending = true;
            StartDescentFire();
        }

        void StartDescentFire()
        {
            // Either order is valid: OnEnter before native preparation, or preparation
            // before OnEnter. Whichever happens second starts the exact stock emitter.
            if (!descending || !fireConfigured || !descentFlames || fireStarted) return;
            PlaceDescentFire();
            descentFlames.gameObject.SetActive(true);
            var emission = descentFlames.emission; emission.enabled = true;
            var renderer = descentFlames.GetComponent<ParticleSystemRenderer>();
            if (renderer) { renderer.enabled = true; renderer.forceRenderingOff = false; }
            descentFlames.Play(false);
            fireStarted = true;
            Plugin.Log.LogInfo("Statue descent fire started: Flames / matEntryBurn, width 3.5x / height 1.75x, emitter base Y + 600 cm, local particles (original space " + originalFireSpace + ")");
        }

        void PlaceDescentFire()
        {
            if (!fireConfigured || !descentFlames) return;
            // The source flame mesh uses local +Z as its long axis. Point it down
            // world vertical, retaining the source's back-facing Y basis and our yaw,
            // instead of inheriting the tilted stock Base/Flames rotation.
            var fireRotation = Rotation * Quaternion.LookRotation(Vector3.down, Vector3.back);
            descentFlames.transform.SetPositionAndRotation(Origin + Rotation * fireLocal, fireRotation);
            // The authored emitter's local Z runs along world vertical. Keep its
            // radial footprint but halve the tall dimension, including its mesh particles.
            descentFlames.transform.localScale = Vector3.Scale(originalFireScale, new Vector3(3.5f, 3.5f, 1.75f));
        }

        bool IsDescentFire(Transform part) => descentFlames && part.IsChildOf(descentFlames.transform);

        public void Landed()
        {
            descending = false;
            if (fireConfigured && descentFlames)
                descentFlames.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }

        void HideStockVisuals()
        {
            if (Time.unscaledTime >= scanAt) {
                scanAt = Time.unscaledTime + 0.25f;
                GetComponentsInChildren(true, scanRenderers);
                foreach (var r in scanRenderers) {
                    if (!r || !IsStock(r.transform) || IsDescentFire(r.transform)) continue;
                    if (!stockRenderers.ContainsKey(r)) stockRenderers.Add(r, r.forceRenderingOff);
                }
                GetComponentsInChildren(true, scanParticles);
                foreach (var ps in scanParticles) {
                    if (!ps || !IsStock(ps.transform) || IsDescentFire(ps.transform) || !stoppedParticles.Add(ps)) continue;
                    // Suppress other stock pod FX, retaining the resized Flames and audio.
                    // Do not deactivate hierarchy nodes used by the animator.
                    var main = ps.main; main.stopAction = ParticleSystemStopAction.None;
                    var emission = ps.emission; emission.enabled = false;
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
            foreach (var entry in stockRenderers) if (entry.Key && !entry.Key.forceRenderingOff) entry.Key.forceRenderingOff = true;
        }

        bool IsStock(Transform part)
        {
            var rider = seat && seat.currentPassengerBody ? seat.currentPassengerBody : passenger;
            return !(models != null && part.IsChildOf(models.Root)) && !(rider && part.IsChildOf(rider.transform));
        }

        void DisableStockCollision()
        {
            GetComponentsInChildren(true, scanColliders);
            foreach (var collider in scanColliders) {
                if (!collider || !IsStock(collider.transform) || (collision && collider.gameObject == collision)) continue;
                if (!stockColliders.ContainsKey(collider)) stockColliders.Add(collider, collider.enabled);
                collider.enabled = false;
            }
        }

        public void Activate()
        {
            if (!exitAligned) AlignExit();
            SyncRiderSkin();
            activated = true; idle = 0;
            passenger = seat.currentPassengerBody;
            if (seat.currentPassengerBody) SpawnMusic.PlayOnce(seat.currentPassengerBody);
        }

        public void Release()
        {
            if (released) return;
            released = true;
            releasedAt = Time.time;
            // Bridge the physics tick until MarioDriver/MarioPuppet draws the playable
            // body. Stock seat unhide must not flash Commando between the two views.
            GameSettings.HideModel(passenger, ref hideFallback, true);
            if (NetworkServer.active && seat.currentPassengerBody) {
                // Stock release. The positional overload also moves the pod to the exit, which
                // applies the exit marker's offset a second time and drags the pedestal's solid
                // off the statue. The body is put on the clip's own last pose instead.
                seat.EjectPassenger();
            }
            PlaceEjected();
            DisableStockCollision();
        }

        void Cleanup()
        {
            custom = false;
            GameSettings.HideModel(passenger, ref hideFallback, false);
            if (pod && originalCamera) pod.cameraBone = originalCamera;
            if (portalCamera) Destroy(portalCamera.gameObject);
            if (collision) Destroy(collision);
            if (fireConfigured && descentFlames) {
                descentFlames.transform.localPosition = originalFirePosition;
                descentFlames.transform.localScale = originalFireScale;
                descentFlames.transform.localRotation = originalFireRotation;
                var main = descentFlames.main; main.scalingMode = originalFireScaling;
                main.simulationSpace = originalFireSpace;
            }
            foreach (var entry in stockRenderers) if (entry.Key) entry.Key.forceRenderingOff = entry.Value;
            foreach (var entry in stockColliders) if (entry.Key) entry.Key.enabled = entry.Value;
            fx?.Dispose(); fx = null; models?.Dispose(); models = null;
            if (view != IntPtr.Zero) Native.sms_view_free(view);
            view = IntPtr.Zero;
        }
        void OnDestroy() => Cleanup();
    }

    public sealed class MarioPodDescent : Descent
    {
        public override void OnEnter()
        {
            base.OnEnter();
            GetComponent<MarioStatuePod>()?.BeginDescent();
        }
        protected override void TransitionIntoNextState() => outer.SetNextState(new MarioPodLanded());
    }

    public sealed class MarioPodLanded : SurvivorPodBaseState
    {
        public override void OnEnter() {
            base.OnEnter(); PlayAnimation("Base", "Idle");
            var statue = GetComponent<MarioStatuePod>();
            if (statue) { statue.Landed(); statue.AlignExit(); }
            vehicleSeat.handleVehicleExitRequestServer.AddCallback(ExitRequest);
        }
        public override void FixedUpdate() { base.FixedUpdate(); survivorPodController.exitAllowed = true; }
        void ExitRequest(GameObject passenger, ref bool? result) {
            var statue = GetComponent<MarioStatuePod>();
            outer.SetNextState(statue && statue.Ready ? (EntityState)new MarioPodExit() : new PreRelease());
            result = true;
        }
        public override void OnExit() {
            vehicleSeat.handleVehicleExitRequestServer.RemoveCallback(ExitRequest);
            survivorPodController.exitAllowed = false;
            base.OnExit();
        }
    }

    public sealed class MarioPodExit : SurvivorPodBaseState
    {
        public float Elapsed => fixedAge;
        MarioStatuePod statue;
        public override void OnEnter() { base.OnEnter(); statue = GetComponent<MarioStatuePod>(); if (statue) statue.Activate(); }
        public override void FixedUpdate() {
            base.FixedUpdate();
            if (isAuthority && ((!statue || !statue.Ready || statue.Finished || statue.Cut) || fixedAge > 4f)) {
                if (statue) statue.Release();
                else if (NetworkServer.active) vehicleSeat.EjectPassenger();
                outer.SetNextState(new MarioPodSpent());
            }
        }
        public override void OnExit() { if (statue) statue.Release(); base.OnExit(); }
    }

    public sealed class MarioPodSpent : SurvivorPodBaseState
    {
        public override void OnEnter() { base.OnEnter(); PlayAnimation("Base", "Idle"); }
    }
}
