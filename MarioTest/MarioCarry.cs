using System;
using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MarioTest
{
    // Mario picks up a teammate like fruit: B near him (Sunshine's grab and carry pose), B to set him
    // down, run and B to throw him. The held player can fight back: pressing his own A (a Mario's
    // jump; an Olimar's or a Melee fighter's primary) breaks him free, and Mario stumbles and lets go.
    //
    // The held body is pinned to Mario's hands on the held player's own machine (his body is his to
    // move). Olimar's and the Melee fighters' drivers already treat a body moved from outside as a
    // teleport and a changed velocity as a launch (StepSmoother.MovedElsewhere, NativeBody's
    // TakeExternalVelocity), so they need nothing: this pins the body after they have moved it, and a
    // throw is one velocity. Everything that has to agree lives in this one assembly (the other
    // plugins carry their own copies of ror2-common, so they cannot share state): Mario's machine asks
    // the server, the server checks it and tells the held player's machine, and the poses and the
    // end go the same way.
    //
    // Carryable (Coins-style config, Mario.CarryTeammates): 0 nobody, 1 Olimar, the Melee fighters and
    // other Marios, 2 any player. Same team only, within reach of Mario's hands.
    internal sealed class MarioCarry : MonoBehaviour
    {
        /// The takeable id of a teammate: this bit and his body's network id.
        internal const uint Bit = 0x80000000u;

        // Mario's machine to the server, and the server on to the machines that own the bodies.
        const short RequestUp = 0x4D10, PoseUp = 0x4D11, EndUp = 0x4D12, EscapeUp = 0x4D13;
        const short BeginDown = 0x4D14, PoseDown = 0x4D15, EndDown = 0x4D16, EscapeDown = 0x4D17, DeniedDown = 0x4D18;

        const float TakeReach = 12f, PoseTimeout = 1.5f, PoseInterval = 0.05f;
        /// How long a thrown or dropped teammate is spared fall damage.
        const float SafeSeconds = 8f;

        static MarioCarry instance;

        // The server: who is held by whom (body network ids).
        static readonly Dictionary<uint, uint> carriedBy = new Dictionary<uint, uint>();

        // Mario's machine: the teammate in his hands.
        static uint heldVictim;
        static bool holding, throwing;
        static float poseAt;
        static Vector3 pendingFrom;

        public static void Register()
        {
            Mp.Register(RequestUp, OnRequest, null);
            Mp.Register(PoseUp, OnPoseUp, null);
            Mp.Register(EndUp, OnEndUp, null);
            Mp.Register(EscapeUp, OnEscapeUp, null);
            Mp.Register(BeginDown, null, (f, r) => ApplyDown(BeginDown, r));
            Mp.Register(PoseDown, null, (f, r) => ApplyDown(PoseDown, r));
            Mp.Register(EndDown, null, (f, r) => ApplyDown(EndDown, r));
            Mp.Register(EscapeDown, null, (f, r) => ApplyDown(EscapeDown, r));
            Mp.Register(DeniedDown, null, (f, r) => ApplyDown(DeniedDown, r));
        }

        void Awake()
        {
            instance = this;
            SceneManager.activeSceneChanged += (a, b) => Clear();
            Run.onRunDestroyGlobal += run => Clear();
        }

        static void Clear()
        {
            carriedBy.Clear();
            holding = throwing = false;
        }

        // ---- who can be carried ----

        static bool Is(CharacterBody body, string typeName)
        {
            // By name: the driver classes live in other plugins.
            foreach (var c in body.GetComponents<MonoBehaviour>())
                for (var t = c ? c.GetType() : null; t != null; t = t.BaseType)
                    if (t.Name == typeName) return true;
            return false;
        }

        static bool Eligible(CharacterBody body)
        {
            int mode = Plugin.CarryTeammates.Value;
            if (mode <= 0) return false;
            return mode >= 2 || body.GetComponent<MarioDriver>() || Is(body, "OlimarDriver") || Is(body, "MeleeDriver");
        }

        static bool SameTeam(CharacterBody a, CharacterBody b) =>
            a.teamComponent && b.teamComponent && a.teamComponent.teamIndex == b.teamComponent.teamIndex;

        static bool Alive(CharacterBody b) => b && b.healthComponent && b.healthComponent.alive;

        // ---- Mario's machine ----

        /// Adds the teammates in reach to Mario's takeables (after the pellets, if any).
        internal static unsafe uint Append(Native.Takeable* output, uint room, Native.Vec3 at, float radius)
        {
            if (room == 0 || holding || Plugin.CarryTeammates.Value <= 0) return 0;
            var self = MarioDriver.Player != null ? MarioDriver.Player.Body : null;
            if (!self) return 0;
            var here = UnityHost.ToUnity(at);
            float reach = radius * UnityHost.Scale + TakeReach * 0f;
            uint n = 0;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (n >= room) break;
                if (!b || b == self || !b.isPlayerControlled || !Alive(b) || !SameTeam(self, b) || !Eligible(b)) continue;
                if (b.netId.Value == 0 || Carried.Is(b)) continue;
                var feet = b.footPosition;
                if ((feet - here).magnitude > reach + 0.9f) continue;
                output[n++] = new Native.Takeable
                {
                    id = Bit | (b.netId.Value & 0x7FFFFFFFu),
                    pos = UnityHost.ToSim(feet),
                    radius = 0.5f / UnityHost.Scale,
                    fruit = -1,
                };
            }
            return n;
        }

        /// His hands close on the teammate: ask the server. Optimistic; a refusal makes him let go.
        internal static byte Take(uint id)
        {
            var self = MarioDriver.Player != null ? MarioDriver.Player.Body : null;
            if (!self || holding) return 0;
            uint victim = id & 0x7FFFFFFFu;
            var go = Mp.Find(victim);
            var body = go ? go.GetComponent<CharacterBody>() : null;
            if (!body || !Alive(body) || !SameTeam(self, body)) return 0;
            holding = true;
            throwing = false;
            heldVictim = victim;
            poseAt = 0f;
            var w = new ByteWriter(8);
            w.U32(self.netId.Value);
            w.U32(victim);
            Mp.SendToServer(RequestUp, w.ToArray());
            return 1;
        }

        static Vector3 Hand(Vector3 feet, float yaw) =>
            feet + Vector3.up * 1.0f + new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)) * 0.55f;

        /// After each step of the local Mario: the teammate in his arms follows his hands.
        internal static void Step(CharacterBody body, in Native.State st, bool local)
        {
            if (!local || !holding) return;
            if (Time.unscaledTime - poseAt < PoseInterval) return;
            poseAt = Time.unscaledTime;
            float yaw = st.model_yaw * Mathf.PI / 32768f;
            SendPose(Hand(UnityHost.ToUnity(st.pos), yaw));
        }

        static void SendPose(Vector3 hand)
        {
            var w = new ByteWriter(24);
            w.U32(Self());
            w.U32(heldVictim);
            w.F32(hand.x); w.F32(hand.y); w.F32(hand.z);
            Mp.SendToServer(PoseUp, w.ToArray(), false);
        }

        static uint Self() => MarioDriver.Player != null && MarioDriver.Player.Body ? MarioDriver.Player.Body.netId.Value : 0;

        static void SendEnd(Vector3 velocity)
        {
            var w = new ByteWriter(24);
            w.U32(Self());
            w.U32(heldVictim);
            w.F32(velocity.x); w.F32(velocity.y); w.F32(velocity.z);
            Mp.SendToServer(EndUp, w.ToArray());
            holding = false;
        }

        /// He died, or the stage ended: the teammate is let go.
        internal static void Lost()
        {
            if (!holding) return;
            SendEnd(Vector3.zero);
            throwing = false;
        }

        /// Sunshine's put and throw, from the step's events. True if it was the teammate's.
        internal static bool OnEvent(ref Native.Event e)
        {
            if (!holding && !throwing) return false;
            uint id = Bit | (heldVictim & 0x7FFFFFFFu);
            switch ((Native.EventKind)e.kind)
            {
                case Native.EventKind.Put:
                {
                    if (!holding || (uint)e.arg != id) return false;
                    float yaw = e.value * Mathf.PI / 32768f;
                    var feet = UnityHost.ToUnity(e.pos);
                    // Set him down in front of Mario.
                    SendPose(feet + new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)) * 1.0f + Vector3.up * 0.1f);
                    SendEnd(Vector3.zero);
                    return true;
                }
                case Native.EventKind.Throw:
                    if (!holding || (uint)e.arg != id) return false;
                    holding = false;
                    throwing = true;
                    pendingFrom = UnityHost.ToUnity(e.pos);
                    return true;
                case Native.EventKind.ThrowAim:
                {
                    if (!throwing) return false;
                    throwing = false;
                    float yaw = e.arg * Mathf.PI / 32768f;
                    var forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
                    var run = UnityHost.ToUnity(e.pos) * 30f;
                    run.y = 0f;
                    SendPose(Hand(pendingFrom, yaw));
                    holding = true; // SendEnd clears it
                    SendEnd(forward * MarioShipParts.ThrowSpeed + run * 0.5f + Vector3.up * MarioShipParts.ThrowLift);
                    return true;
                }
            }
            return false;
        }

        // ---- the server ----

        static NetworkConnection ConnOf(CharacterBody body)
        {
            var master = body ? body.master : null;
            var pcmc = master ? master.playerCharacterMasterController : null;
            return pcmc && pcmc.networkUser ? pcmc.networkUser.connectionToClient : null;
        }

        /// The sender really plays this body (the host's own sender is null).
        static bool Owns(NetworkConnection from, CharacterBody body) =>
            body && (from == null ? body.hasEffectiveAuthority : ConnOf(body) == from);

        static CharacterBody BodyOf(uint netId)
        {
            var go = Mp.Find(netId);
            return go ? go.GetComponent<CharacterBody>() : null;
        }

        /// Delivers a message to the machine that owns `netId`'s body: here, or over the wire.
        static void Route(uint netId, short down, byte[] payload)
        {
            var body = BodyOf(netId);
            if (!body) return;
            if (body.hasEffectiveAuthority) { ApplyDown(down, new ByteReader(payload)); return; }
            var conn = ConnOf(body);
            if (conn != null) Mp.SendTo(conn, down, payload, down != PoseDown);
        }

        static void OnRequest(NetworkConnection from, ByteReader r)
        {
            uint carrierId = r.U32(), victimId = r.U32();
            if (r.Failed) return;
            var carrier = BodyOf(carrierId);
            var victim = BodyOf(victimId);
            bool ok = carrier && victim && carrier != victim && Owns(from, carrier) && Alive(carrier) && Alive(victim)
                && victim.isPlayerControlled && SameTeam(carrier, victim) && Eligible(victim)
                && (carrier.footPosition - victim.footPosition).magnitude <= TakeReach
                && !carriedBy.ContainsKey(victimId) && !carriedBy.ContainsValue(carrierId)
                && !carriedBy.ContainsKey(carrierId);
            var w = new ByteWriter(8);
            if (!ok)
            {
                w.U32(victimId);
                Route(carrierId, DeniedDown, w.ToArray());
                return;
            }
            carriedBy[victimId] = carrierId;
            w.U32(victimId);
            w.U32(carrierId);
            Route(victimId, BeginDown, w.ToArray());
        }

        static void OnPoseUp(NetworkConnection from, ByteReader r)
        {
            uint carrierId = r.U32(), victimId = r.U32();
            var hand = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Failed || !Finite(hand)) return;
            if (!carriedBy.TryGetValue(victimId, out var by) || by != carrierId || !Owns(from, BodyOf(carrierId))) return;
            var w = new ByteWriter(20);
            w.U32(victimId);
            w.F32(hand.x); w.F32(hand.y); w.F32(hand.z);
            Route(victimId, PoseDown, w.ToArray());
        }

        static void OnEndUp(NetworkConnection from, ByteReader r)
        {
            uint carrierId = r.U32(), victimId = r.U32();
            var velocity = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Failed || !Finite(velocity)) return;
            if (!carriedBy.TryGetValue(victimId, out var by) || by != carrierId || !Owns(from, BodyOf(carrierId))) return;
            carriedBy.Remove(victimId);
            if (velocity.magnitude > 40f) velocity = velocity.normalized * 40f;
            var w = new ByteWriter(20);
            w.U32(victimId);
            w.F32(velocity.x); w.F32(velocity.y); w.F32(velocity.z);
            Route(victimId, EndDown, w.ToArray());
        }

        static void OnEscapeUp(NetworkConnection from, ByteReader r)
        {
            uint victimId = r.U32();
            if (r.Failed || !Owns(from, BodyOf(victimId))) return;
            if (!carriedBy.TryGetValue(victimId, out var carrierId)) return;
            carriedBy.Remove(victimId);
            var w = new ByteWriter(8);
            w.U32(victimId);
            Route(carrierId, EscapeDown, w.ToArray());
        }

        static bool Finite(Vector3 v) => !(float.IsNaN(v.x + v.y + v.z) || float.IsInfinity(v.x + v.y + v.z));

        // ---- whichever machine the message is for ----

        static void ApplyDown(short type, ByteReader r)
        {
            switch (type)
            {
                case BeginDown:
                {
                    var body = BodyOf(r.U32());
                    uint carrier = r.U32();
                    if (r.Failed || !body || !body.hasEffectiveAuthority) return;
                    var c = body.GetComponent<Carried>() ?? body.gameObject.AddComponent<Carried>();
                    c.Begin(carrier);
                    break;
                }
                case PoseDown:
                {
                    var body = BodyOf(r.U32());
                    var hand = new Vector3(r.F32(), r.F32(), r.F32());
                    var c = body ? body.GetComponent<Carried>() : null;
                    if (!r.Failed && c) c.Pose(hand);
                    break;
                }
                case EndDown:
                {
                    var body = BodyOf(r.U32());
                    var velocity = new Vector3(r.F32(), r.F32(), r.F32());
                    var c = body ? body.GetComponent<Carried>() : null;
                    if (!r.Failed && c) c.End(velocity);
                    break;
                }
                case EscapeDown:
                {
                    // Mario's machine: the teammate broke free.
                    uint victim = r.U32();
                    if (r.Failed || !holding || victim != heldVictim) return;
                    holding = throwing = false;
                    var driver = MarioDriver.Player;
                    if (driver != null) driver.LetGo(BodyOf(victim));
                    break;
                }
                case DeniedDown:
                {
                    uint victim = r.U32();
                    if (r.Failed || !holding || victim != heldVictim) return;
                    holding = throwing = false;
                    MarioDriver.Player?.LetGo(null);
                    break;
                }
            }
        }

        // ---- the held teammate's machine ----

        /// On the body of a teammate in Mario's hands, on the machine that moves him. Pins him to the
        /// hands every step and frame (after his own driver has moved him), watches for his A, and
        /// lets him go on a throw, a put, a timeout, his death or his own escape.
        [DefaultExecutionOrder(500)]
        internal sealed class Carried : MonoBehaviour
        {
            CharacterBody body;
            CharacterMotor motor;
            InputBankTest input;
            bool isMario;
            Vector3 hand, shown;
            float lastPose;
            bool hasPose, ended;

            internal static bool Is(CharacterBody b) => b && b.GetComponent<Carried>();

            void Awake()
            {
                body = GetComponent<CharacterBody>();
                motor = GetComponent<CharacterMotor>();
                input = GetComponent<InputBankTest>();
                isMario = GetComponent<MarioDriver>();
                lastPose = Time.time;
            }

            internal void Begin(uint carrier) { lastPose = Time.time; ended = false; }

            internal void Pose(Vector3 h)
            {
                hand = h;
                if (!hasPose) { shown = h; hasPose = true; }
                lastPose = Time.time;
            }

            /// The carrier let go: a throw's velocity (m/s), or nothing for a put.
            internal void End(Vector3 velocity)
            {
                if (ended) return;
                ended = true;
                Pin(0f);
                if (velocity.sqrMagnitude > 0.01f && motor)
                {
                    motor.velocity = velocity;
                    if (motor.Motor && motor.Motor.enabled) motor.Motor.ForceUnground();
                }
                if (instance) instance.StartCoroutine(Spare(body));
                Destroy(this);
            }

            /// Mario's grip does not outlast a fall: no fall damage for a few seconds.
            static System.Collections.IEnumerator Spare(CharacterBody b)
            {
                if (!b) yield break;
                var flag = CharacterBody.BodyFlags.IgnoreFallDamage;
                bool had = (b.bodyFlags & flag) != 0;
                b.bodyFlags |= flag;
                yield return new WaitForSeconds(SafeSeconds);
                if (b && !had) b.bodyFlags &= ~flag;
            }

            bool EscapePressed() => input && (isMario ? input.jump.justPressed : input.skill1.justPressed);

            void Escape()
            {
                if (ended) return;
                var w = new ByteWriter(8);
                w.U32(body.netId.Value);
                Mp.SendToServer(EscapeUp, w.ToArray());
                End(Vector3.zero);
            }

            void FixedUpdate()
            {
                if (ended) return;
                if (!body || !Alive(body) || Time.time - lastPose > PoseTimeout) { Escape(); return; }
                if (EscapePressed()) { Escape(); return; }
                Pin(Time.fixedDeltaTime);
            }

            void Update()
            {
                if (ended || !body) return;
                if (EscapePressed()) { Escape(); return; }
                Pin(Time.deltaTime);
            }

            void LateUpdate() { if (!ended) Pin(0f); }

            void Pin(float dt)
            {
                if (!hasPose || !body) return;
                shown = dt > 0f ? Vector3.Lerp(shown, hand, 1f - Mathf.Exp(-30f * dt)) : shown;
                // His feet hang a little under the hands.
                var feetOffset = transform.position - body.footPosition;
                var target = shown - Vector3.up * 0.6f + feetOffset;
                if (motor && motor.Motor && motor.Motor.enabled) motor.Motor.SetPosition(target, true);
                else transform.position = target;
                if (motor) motor.velocity = Vector3.zero;
            }
        }
    }
}
