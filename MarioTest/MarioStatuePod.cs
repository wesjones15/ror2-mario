using System;
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
        public Quaternion gateRotation;
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
        Renderer[] stockRenderers;
        public bool Ready => custom;
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
            // The native landing ends 308.53 cm in front of the M. Anchor that end to the
            // stock exit point; no offset into the pedestal or displacement of the spawn.
            var forward = seat.exitPosition.forward; forward.y = 0;
            if (forward.sqrMagnitude < .01f) forward = prefab.transform.forward;
            var rotation = Quaternion.LookRotation(forward.normalized);
            var capsule = body.GetComponent<CapsuleCollider>();
            var feet = capsule ? capsule.transform.TransformPoint(capsule.center - Vector3.up * (capsule.height * .5f)) : body.footPosition;
            var originToFeet = body.transform.position - feet;
            component.gateLocal = travel.InverseTransformPoint(seat.exitPosition.position - originToFeet - rotation * Vector3.forward * (308.53f * UnityHost.Scale));
            component.gateRotation = Quaternion.Inverse(travel.rotation) * rotation;
            prefab.GetComponent<EntityStateMachine>().initialStateType = new SerializableEntityStateType(typeof(MarioPodDescent));
            body.preferredPodPrefab = prefab;
            pack.networkedObjectPrefabs.Add(new[] { prefab });
            pack.entityStateTypes.Add(new[] { typeof(MarioPodDescent), typeof(MarioPodLanded), typeof(MarioPodExit), typeof(MarioPodSpent) });
        }

        void Awake()
        {
            seat = GetComponent<VehicleSeat>(); pod = GetComponent<SurvivorPodController>(); machine = GetComponent<EntityStateMachine>();
            stockRenderers = GetComponentsInChildren<Renderer>(true);
        }

        Vector3 Origin => travel.TransformPoint(gateLocal);
        Quaternion Rotation => travel.rotation * gateRotation;

        public void AlignExit()
        {
            // Runtime motor/capsule data is authoritative once the pod has landed.
            // Reconcile any prefab-only foot offset before the exit can be requested.
            var body = seat.currentPassengerBody;
            if (!body || !travel) return;
            gateLocal = travel.InverseTransformPoint(seat.exitPosition.position - (body.transform.position - body.footPosition)
                - Rotation * Vector3.forward * (308.53f * UnityHost.Scale));
            if (portalCamera) {
                portalCamera.position = Origin + Rotation * new Vector3(-5.5f, 3.8f, 11.5f) * (UnityHost.Scale * 100f);
                portalCamera.rotation = Quaternion.LookRotation(Origin + Rotation * new Vector3(0, 1.8f, 1.4f) * (UnityHost.Scale * 100f) - portalCamera.position);
            }
            if (collision) { collision.transform.position = Origin; collision.transform.rotation = Rotation; }
        }

        void Prepare()
        {
            if (tried || !Plugin.AssetsSettled) return;
            tried = true;
            if (!Plugin.AssetsReady || !Plugin.DrawModel.Value || !Run.instance || Run.instance.stageClearCount != 0) return;
            try {
                view = Native.sms_view_entrance_new(NativeLib.CString(Plugin.AssetsDir), (byte)(Plugin.Sound.Value ? 1 : 0));
                if (view == IntPtr.Zero) throw new InvalidOperationException("statue/portal assets unavailable");
                // The rider's skin: Luigi lands from the gate as Luigi.
                var rider = seat.currentPassengerBody;
                if (rider && MarioSkin.Selected(rider) == MarioSkin.Luigi && MarioSkin.Load(view))
                    Native.sms_view_skin(view, 1);
                models = ModelView.Create(new SmsViewSource(view), "Mario Sunshine statue", transform);
                if (models == null) throw new InvalidOperationException("statue renderer unavailable");
                // MarioFx uploads world-space vertices; keep its renderer at world identity.
                fx = MarioFx.Create(view);
                if (fx == null) throw new InvalidOperationException("portal particle renderer unavailable");
                custom = true;
                // Only the shell: retain the original animator, animation events, crash FX,
                // networking, collision and passenger protection throughout descent.
                foreach (var renderer in stockRenderers)
                    if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) renderer.enabled = false;
                var camera = new GameObject("Mario portal camera").transform;
                camera.SetParent(travel, false);
                camera.position = Origin + Rotation * new Vector3(-5.5f, 3.8f, 11.5f) * (UnityHost.Scale * 100f);
                camera.rotation = Quaternion.LookRotation(Origin + Rotation * new Vector3(0, 1.8f, 1.4f) * (UnityHost.Scale * 100f) - camera.position);
                originalCamera = pod.cameraBone;
                portalCamera = camera;
                pod.cameraBone = camera;
                if (Native.sms_view_entrance_bounds(view, out var lo, out var hi) != 0) {
                    collision = new GameObject("Pianta statue pedestal collision");
                    collision.layer = LayerIndex.world.intVal;
                    collision.transform.SetParent(travel, false);
                    collision.transform.position = Origin; collision.transform.rotation = Rotation;
                    var box = collision.AddComponent<BoxCollider>();
                    box.center = new Vector3(-(lo.x + hi.x), lo.y + hi.y, lo.z + hi.z) * (.5f * UnityHost.Scale);
                    box.size = new Vector3(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z) * UnityHost.Scale;
                }
                Plugin.Log.LogInfo("Mario stage-one statue entrance ready (05_gate01 / particle 0x40)");
            } catch (Exception e) {
                Plugin.Log.LogWarning("Mario statue: " + e.Message + "; using vanilla pod");
                Cleanup();
            }
        }

        void LateUpdate()
        {
            Prepare();
            if (!custom || !travel || RunAudio.Ended) return;
            if (!PauseManager.isPaused) idle += Time.deltaTime;
            float time = activated && machine.state is MarioPodExit exit ? exit.Elapsed : released ? 6f + idle : idle;
            var frame = new Native.ViewFrame { forward = new Native.Vec3(0, 0, 1), up = new Native.Vec3(0, 1, 0), volume = GameSettings.SfxVolume() * Mathf.Clamp(Plugin.SoundVolume.Value, 0, 2) };
            var camera = GameSettings.PlayerCamera();
            if (camera) {
                var inv = Quaternion.Inverse(Rotation);
                var f = inv * camera.forward; var u = inv * camera.up;
                frame.forward = new Native.Vec3(f.x, f.y, f.z); frame.up = new Native.Vec3(u.x, u.y, u.z);
                frame.eye = UnityHost.ToSim(inv * (camera.position - Origin));
            }
            Native.sms_view_set_paused(view, (byte)(PauseManager.isPaused ? 1 : 0));
            // Once ejected, retain only the statue/portal, not a duplicate landing Mario.
            var result = Native.sms_view_entrance_update(view, time, (byte)(activated ? 1 : 0), ref frame, out finalFeet);
            if (result == 0) { Finished = true; return; }
            Finished = result == 2;
            bool playableDraws = passenger && (passenger.GetComponent<MarioDriver>()?.Drawing == true
                || passenger.GetComponent<MarioPuppet>()?.Drawing == true);
            bool handedOff = released && (playableDraws || !passenger || !passenger.healthComponent.alive || Time.time - releasedAt > 2f);
            if (handedOff) {
                // HideModel uses forceRenderingOff, not a counter. The normal driver now
                // owns that flag; clearing it here would briefly reveal Commando.
                if (playableDraws) hideFallback = false;
                else GameSettings.HideModel(passenger, ref hideFallback, false);
            }
            models.Update(UnityHost.Scale, d => (Origin + Rotation * new Vector3(-d.pos.x, d.pos.y, d.pos.z) * UnityHost.Scale, Rotation * new Quaternion(d.rot_x, d.rot_y, d.rot_z, d.rot_w)), d => handedOff && d.kind < 240);
            fx.Update(view, UnityHost.Scale, Vector3.zero, Matrix4x4.TRS(Origin, Rotation, Vector3.one));
            if (activated && !voiced && time >= 1f) {
                voiced = true;
                Native.sms_view_mario_voice(view, NativeLib.CString("MSD_SE_MV23_JUMP_LARGE_01"));
            }
        }

        public void Activate()
        {
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
                // The visual anchor places native feet at precisely this stock body-origin
                // exit. Use the seat's own safe exit even if a native renderer failed.
                seat.EjectPassenger(seat.exitPosition.position, Rotation);
            }
        }

        void Cleanup()
        {
            custom = false;
            GameSettings.HideModel(passenger, ref hideFallback, false);
            if (pod && originalCamera) pod.cameraBone = originalCamera;
            if (portalCamera) Destroy(portalCamera.gameObject);
            if (collision) Destroy(collision);
            if (stockRenderers != null) foreach (var r in stockRenderers) if (r && (r is MeshRenderer || r is SkinnedMeshRenderer)) r.enabled = true;
            fx?.Dispose(); fx = null; models?.Dispose(); models = null;
            if (view != IntPtr.Zero) Native.sms_view_free(view);
            view = IntPtr.Zero;
        }
        void OnDestroy() => Cleanup();
    }

    public sealed class MarioPodDescent : Descent
    {
        protected override void TransitionIntoNextState() => outer.SetNextState(new MarioPodLanded());
    }

    public sealed class MarioPodLanded : SurvivorPodBaseState
    {
        public override void OnEnter() {
            base.OnEnter(); PlayAnimation("Base", "Idle");
            GetComponent<MarioStatuePod>()?.AlignExit();
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
            if (isAuthority && ((!statue || !statue.Ready || statue.Finished) || fixedAge > 8f)) {
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
