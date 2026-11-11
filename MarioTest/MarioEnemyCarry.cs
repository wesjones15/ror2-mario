using System;
using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MarioTest
{
    // Mario picks up a small enemy like Sunshine's fruit and throws it. B near one (standing or walking slowly: the game's grab, the
    // same `TAKE` the ship parts and teammates use), B again to throw it, standing, walking or running (a throwing kind,
    // libsms's `TAKE_THROWS`), and in the air B throws it too instead of diving. The libsms core does all of that; this is the host's half,
    // laid out like MarioCarry's but for enemies, which belong to the server:
    //
    //   Mario's machine: offers the small enemies in reach as takeables, asks the server when his hand closes, and streams where his
    //   hands are while he holds one, then sends the end (a throw's velocity, or none).
    //   The server: checks the request, holds the enemy at the hands (pinned each step, stunned so it cannot act), and on the end lets it
    //   go with the velocity. A thrown enemy hurts the enemies it hits on the way and itself and its neighbours where it lands.
    //
    // Small: not a boss or champion, human-sized hull, at most Carry.EnemyMaxHealth base health, and able to be stunned.
    internal sealed class MarioEnemyCarry : MonoBehaviour
    {
        /// The takeable id of an enemy: this bit and its network id (libsms's `carry::TAKE_THROWS`; the teammates' bit is above it).
        internal const uint Bit = 0x40000000u;
        internal const uint Mask = 0xC0000000u;

        const short RequestUp = 0x4D30, PoseUp = 0x4D31, EndUp = 0x4D32, DeniedDown = 0x4D33;
        const float TakeReach = 3.5f, PoseTimeout = 1.5f, PoseInterval = 0.05f, FlightSeconds = 4f;
        /// Where the thrown enemy's own landing hurts its neighbours (metres).
        const float ImpactRadius = 2.5f;

        static MarioEnemyCarry instance;

        // The server: who holds which enemy, and the enemies in flight.
        sealed class Hold { public uint carrier; public Vector3 hand, shown; public float poseAt; public bool pinned; }
        sealed class Flight { public uint enemy, carrier; public float start; public readonly HashSet<uint> hit = new HashSet<uint>(); }
        static readonly Dictionary<uint, Hold> held = new Dictionary<uint, Hold>();
        static readonly List<Flight> flights = new List<Flight>();
        static readonly Collider[] overlap = new Collider[64];
        static readonly List<HealthComponent> found = new List<HealthComponent>();
        static readonly HashSet<HealthComponent> seen = new HashSet<HealthComponent>();

        // Mario's machine: the enemy in his hands.
        static uint heldEnemy;
        static bool holding, throwing;
        static float poseAt;
        static Vector3 pendingFrom;

        public static void Register()
        {
            Mp.Register(RequestUp, OnRequest, null);
            Mp.Register(PoseUp, OnPoseUp, null);
            Mp.Register(EndUp, OnEndUp, null);
            Mp.Register(DeniedDown, null, (f, r) => OnDenied(r));
        }

        void Awake()
        {
            instance = this;
            SceneManager.activeSceneChanged += (a, b) => Clear();
            Run.onRunDestroyGlobal += run => Clear();
        }

        static void Clear()
        {
            held.Clear();
            flights.Clear();
            holding = throwing = false;
        }

        // ---- who can be picked up ----

        static bool Alive(CharacterBody b) => b && b.healthComponent && b.healthComponent.alive;

        static bool Small(CharacterBody b)
        {
            if (!b || b.isPlayerControlled || b.isBoss || b.isChampion) return false;
            if (b.hullClassification != HullClassification.Human) return false;
            if (b.baseMaxHealth > Plugin.EnemyCarryMaxHealth.Value) return false;
            var stun = b.GetComponent<SetStateOnHurt>();
            return stun && stun.canBeStunned;
        }

        static bool Hostile(CharacterBody carrier, CharacterBody b) =>
            carrier && b && carrier.teamComponent && b.teamComponent
            && TeamManager.IsTeamEnemy(carrier.teamComponent.teamIndex, b.teamComponent.teamIndex);

        // ---- Mario's machine ----

        /// Adds the small enemies in reach to Mario's takeables.
        internal static unsafe uint Append(Native.Takeable* output, uint room, Native.Vec3 at, float radius)
        {
            if (room == 0 || holding || !Plugin.EnemyCarry.Value) return 0;
            var self = MarioDriver.Player != null ? MarioDriver.Player.Body : null;
            if (!self) return 0;
            var here = UnityHost.ToUnity(at);
            float reach = radius * UnityHost.Scale;
            uint n = 0;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (n >= room) break;
                if (!b || b == self || b.netId.Value == 0 || !Alive(b) || !Hostile(self, b) || !Small(b)) continue;
                var feet = b.footPosition;
                float r = Mathf.Max(.4f, b.radius);
                if ((feet - here).magnitude > reach + r + .5f) continue;
                output[n++] = new Native.Takeable
                {
                    id = Bit | (b.netId.Value & ~Mask),
                    pos = UnityHost.ToSim(feet),
                    radius = r / UnityHost.Scale,
                    fruit = -1,
                };
            }
            return n;
        }

        static uint Self() => MarioDriver.Player != null && MarioDriver.Player.Body ? MarioDriver.Player.Body.netId.Value : 0;

        /// His hand closes on the enemy: ask the server. Optimistic; a refusal makes him let go.
        internal static byte Take(uint id)
        {
            var self = MarioDriver.Player != null ? MarioDriver.Player.Body : null;
            if (!self || holding) return 0;
            uint enemy = id & ~Mask;
            var body = BodyOf(enemy);
            if (!body || !Alive(body) || !Hostile(self, body) || !Small(body)) return 0;
            holding = true;
            throwing = false;
            heldEnemy = enemy;
            poseAt = 0f;
            var w = new ByteWriter(8);
            w.U32(self.netId.Value);
            w.U32(enemy);
            Mp.SendToServer(RequestUp, w.ToArray());
            return 1;
        }

        static Vector3 Hand(Vector3 feet, float yaw) =>
            feet + Vector3.up * 1.0f + new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)) * 0.55f;

        /// After each step of the local Mario: the enemy in his arms follows his hands.
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
            w.U32(heldEnemy);
            w.F32(hand.x); w.F32(hand.y); w.F32(hand.z);
            Mp.SendToServer(PoseUp, w.ToArray(), false);
        }

        static void SendEnd(Vector3 velocity)
        {
            var w = new ByteWriter(24);
            w.U32(Self());
            w.U32(heldEnemy);
            w.F32(velocity.x); w.F32(velocity.y); w.F32(velocity.z);
            Mp.SendToServer(EndUp, w.ToArray());
            holding = false;
        }

        /// He died, or the stage ended: the enemy is let go.
        internal static void Lost()
        {
            if (!holding) return;
            SendEnd(Vector3.zero);
            throwing = false;
        }

        /// Sunshine's put and throw, from the step's events. True if it was the enemy's.
        internal static bool OnEvent(ref Native.Event e)
        {
            if (!holding && !throwing) return false;
            uint id = Bit | (heldEnemy & ~Mask);
            switch ((Native.EventKind)e.kind)
            {
                case Native.EventKind.Put:
                {
                    if (!holding || (uint)e.arg != id) return false;
                    float yaw = e.value * Mathf.PI / 32768f;
                    var feet = UnityHost.ToUnity(e.pos);
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

        /// The server refused: he lets go with nothing in his hands.
        static void OnDenied(ByteReader r)
        {
            uint enemy = r.U32();
            if (r.Failed || !holding || enemy != heldEnemy) return;
            holding = throwing = false;
            MarioDriver.Player?.LetGo(null);
        }

        // ---- the server ----

        static CharacterBody BodyOf(uint netId)
        {
            var go = Mp.Find(netId);
            return go ? go.GetComponent<CharacterBody>() : null;
        }

        static NetworkConnection ConnOf(CharacterBody body)
        {
            var master = body ? body.master : null;
            var pcmc = master ? master.playerCharacterMasterController : null;
            return pcmc && pcmc.networkUser ? pcmc.networkUser.connectionToClient : null;
        }

        /// The sender really plays this body (the host's own sender is null).
        static bool Owns(NetworkConnection from, CharacterBody body) =>
            body && (from == null ? body.hasEffectiveAuthority : ConnOf(body) == from);

        static bool Finite(Vector3 v) => !(float.IsNaN(v.x + v.y + v.z) || float.IsInfinity(v.x + v.y + v.z));

        static void Deny(uint carrierId, uint enemyId)
        {
            var w = new ByteWriter(4);
            w.U32(enemyId);
            var carrier = BodyOf(carrierId);
            if (!carrier) return;
            if (carrier.hasEffectiveAuthority) { OnDenied(new ByteReader(w.ToArray())); return; }
            var conn = ConnOf(carrier);
            if (conn != null) Mp.SendTo(conn, DeniedDown, w.ToArray(), true);
        }

        static void OnRequest(NetworkConnection from, ByteReader r)
        {
            uint carrierId = r.U32(), enemyId = r.U32();
            if (r.Failed) return;
            var carrier = BodyOf(carrierId);
            var enemy = BodyOf(enemyId);
            bool ok = Plugin.EnemyCarry.Value && carrier && enemy && Owns(from, carrier) && Alive(carrier) && Alive(enemy)
                && Hostile(carrier, enemy) && Small(enemy)
                && (carrier.footPosition - enemy.footPosition).magnitude <= TakeReach
                && !held.ContainsKey(enemyId) && !HoldsAny(carrierId);
            if (!ok) { Deny(carrierId, enemyId); return; }
            held[enemyId] = new Hold { carrier = carrierId, hand = enemy.footPosition + Vector3.up, shown = enemy.footPosition + Vector3.up, poseAt = Time.time };
            Stun(enemy, .3f);
        }

        static bool HoldsAny(uint carrierId)
        {
            foreach (var h in held.Values) if (h.carrier == carrierId) return true;
            return false;
        }

        static void OnPoseUp(NetworkConnection from, ByteReader r)
        {
            uint carrierId = r.U32(), enemyId = r.U32();
            var hand = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Failed || !Finite(hand)) return;
            if (!held.TryGetValue(enemyId, out var hold) || hold.carrier != carrierId || !Owns(from, BodyOf(carrierId))) return;
            hold.hand = hand;
            hold.poseAt = Time.time;
        }

        static void OnEndUp(NetworkConnection from, ByteReader r)
        {
            uint carrierId = r.U32(), enemyId = r.U32();
            var velocity = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Failed || !Finite(velocity)) return;
            if (!held.TryGetValue(enemyId, out var hold) || hold.carrier != carrierId || !Owns(from, BodyOf(carrierId))) return;
            if (velocity.magnitude > 40f) velocity = velocity.normalized * 40f;
            Release(enemyId, velocity);
        }

        static void Stun(CharacterBody enemy, float seconds)
        {
            var stun = enemy ? enemy.GetComponent<SetStateOnHurt>() : null;
            if (stun && stun.canBeStunned) stun.SetStun(seconds);
        }

        /// Lets the enemy go: a throw's velocity (m/s) sends it flying, none sets it down.
        static void Release(uint enemyId, Vector3 velocity)
        {
            if (!held.TryGetValue(enemyId, out var hold)) return;
            held.Remove(enemyId);
            var enemy = BodyOf(enemyId);
            if (!Alive(enemy)) return;
            bool thrown = velocity.sqrMagnitude > .01f;
            Stun(enemy, thrown ? 1.2f : .4f);
            if (!thrown) return;
            var motor = enemy.characterMotor;
            var body = enemy.GetComponent<Rigidbody>();
            if (motor)
            {
                motor.velocity = velocity;
                if (motor.Motor && motor.Motor.enabled) motor.Motor.ForceUnground();
            }
            else if (body) body.velocity = velocity;
            flights.Add(new Flight { enemy = enemyId, carrier = hold.carrier, start = Time.time });
        }

        void FixedUpdate()
        {
            if (!NetworkServer.active) return;
            if (held.Count > 0)
            {
                foreach (uint id in new List<uint>(held.Keys))
                {
                    var hold = held[id];
                    var enemy = BodyOf(id);
                    var carrier = BodyOf(hold.carrier);
                    if (!Alive(enemy) || !Alive(carrier) || Time.time - hold.poseAt > PoseTimeout) { Release(id, Vector3.zero); continue; }
                    hold.shown = hold.pinned ? Vector3.Lerp(hold.shown, hold.hand, 1f - Mathf.Exp(-30f * Time.fixedDeltaTime)) : hold.hand;
                    hold.pinned = true;
                    Pin(enemy, hold.shown);
                    Stun(enemy, .3f);
                }
            }
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                if (!Fly(flights[i])) flights.RemoveAt(i);
            }
        }

        /// The enemy hangs a little under the hands.
        static void Pin(CharacterBody enemy, Vector3 hand)
        {
            var target = hand - Vector3.up * 0.5f + (enemy.transform.position - enemy.footPosition);
            var motor = enemy.characterMotor;
            if (motor && motor.Motor && motor.Motor.enabled)
            {
                motor.Motor.SetPosition(target, true);
                motor.velocity = Vector3.zero;
                return;
            }
            var body = enemy.GetComponent<Rigidbody>();
            if (body) { body.position = target; body.velocity = Vector3.zero; }
            enemy.transform.position = target;
        }

        /// One step of a thrown enemy: the enemies it strikes on the way, then its landing. False when it is done.
        static bool Fly(Flight f)
        {
            var enemy = BodyOf(f.enemy);
            var carrier = BodyOf(f.carrier);
            if (!Alive(enemy) || !carrier || Time.time - f.start > FlightSeconds) return false;
            var at = enemy.corePosition;
            var motor = enemy.characterMotor;
            var rb = enemy.GetComponent<Rigidbody>();
            var velocity = motor ? motor.velocity : rb ? rb.velocity : Vector3.zero;
            var push = velocity.sqrMagnitude > .01f ? velocity.normalized * 1500f : Vector3.up * 800f;
            foreach (var victim in Victims(carrier, at, enemy.radius + .5f))
            {
                if (victim.body == enemy || !f.hit.Add(victim.body.netId.Value)) continue;
                Deal(carrier, victim, Plugin.EnemyThrowDamage.Value, at, push);
            }
            bool landed = Time.time - f.start > .15f && (motor ? motor.isGrounded : velocity.sqrMagnitude < 4f);
            if (!landed) return true;
            // The landing: the thrown enemy and everything around it.
            foreach (var victim in Victims(carrier, enemy.footPosition + Vector3.up * .5f, ImpactRadius))
            {
                if (victim.body != enemy && !f.hit.Add(victim.body.netId.Value)) continue;
                Deal(carrier, victim, Plugin.EnemyThrowDamage.Value, at, push);
            }
            return false;
        }

        static IEnumerable<HealthComponent> Victims(CharacterBody carrier, Vector3 at, float radius)
        {
            found.Clear();
            seen.Clear();
            int n = Physics.OverlapSphereNonAlloc(at, radius, overlap, LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var hb = overlap[i] ? overlap[i].GetComponent<HurtBox>() : null;
                var hc = hb ? hb.healthComponent : null;
                if (!hc || !hc.alive || !hc.body || hc.body == carrier) continue;
                if (!TeamManager.IsTeamEnemy(carrier.teamComponent.teamIndex, hb.teamIndex)) continue;
                if (seen.Add(hc)) found.Add(hc);
            }
            return found.ToArray();
        }

        static void Deal(CharacterBody carrier, HealthComponent victim, float coefficient, Vector3 at, Vector3 force)
        {
            if (!victim || !victim.alive || coefficient <= 0f) return;
            var spec = new NativeHitSpec
            {
                coefficient = coefficient,
                position = at,
                force = force,
                procCoefficient = 1f,
                damageType = DamageType.Generic,
                color = DamageColorIndex.Default,
            };
            MarioItems.Dealing = true;
            try { NativeHit.Deal(carrier, victim, spec); }
            finally { MarioItems.Dealing = false; }
        }
    }
}
