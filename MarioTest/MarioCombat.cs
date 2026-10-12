using System;
using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // Mario against RoR2's enemies. The sim asks which enemies are near (Host.enemies): the
    // characters on other teams whose hurt boxes are in reach, each as a standing cylinder
    // around all its hurt boxes. Mario stomps, pounds and is pushed by those, and FLUDD's drops
    // hit them; the sim reports each hit as an event, and after the step they land as RoR2
    // damage from Mario's body (ror2-common's NativeHit: his damage stat, crits, on-hit items,
    // kill credit). Water doing damage is this plugin's, not Sunshine's (where most enemies only
    // flinch or slip); each enemy's drops for a step land as one hit, so on-hit items see about
    // 30 hits a second; on-hit items proc on at most one of those a second per enemy
    // (ror2-common's ProcLimiter), at Items.WaterProcCoefficient, the rest at 0.
    //
    // A ground pound hits harder the further it fell (Damage.PoundPerMetre; not Sunshine's):
    // the landing reports its drop, and a pound onto an enemy mid-fall counts from where the
    // fall began (Watch). A super pound (Sunshine's flaming one) can set its victims alight and
    // lands with a fiery blast.
    //
    // MarioDriver calls Begin before each sms_sim_step, End after it, and Watch with the state
    // after that. Hits are the server's: a client playing Mario (multiplayer is untested) deals
    // none.
    internal static class MarioCombat
    {
        static CharacterBody attacker;
        static TeamIndex team;
        static readonly Collider[] overlap = new Collider[128];
        static readonly List<HealthComponent> found = new List<HealthComponent>();
        // The sim's ids for this step's enemies.
        static readonly Dictionary<uint, HealthComponent> byId = new Dictionary<uint, HealthComponent>();
        // Hits reported during the step, landed after it.
        static readonly List<(HealthComponent victim, int kind)> blows = new List<(HealthComponent, int)>();
        static readonly Dictionary<HealthComponent, (int drops, Vector3 sum)> water =
            new Dictionary<HealthComponent, (int, Vector3)>();
        static bool pounded, superPound;
        static Vector3 poundAt;
        // The landing's drop (metres); where the pound's fall began (world Y), and whether it
        // has turned super.
        static float poundDrop, poundTop;
        static bool fallingSuper;
        static GameObject blast;
        static MarioItems items;
        // FLUDD's on-hit items: at most once a second per target, shared by every nozzle.
        static readonly ProcLimiter waterProcs = new ProcLimiter(1f);

        static readonly Native.EventFn eventFn = OnEvent;
        static readonly unsafe Native.EnemiesFn enemiesFn = Enemies;
        public static IntPtr EventPtr => System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(eventFn);
        public static IntPtr EnemiesPtr => System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(enemiesFn);

        public static void Begin(CharacterBody body, MarioItems with)
        {
            attacker = body;
            items = with;
            team = body && body.teamComponent ? body.teamComponent.teamIndex : TeamIndex.Player;
            byId.Clear();
            blows.Clear();
            water.Clear();
            pounded = false;
        }

        /// Follows Mario's ground pound between steps.
        public static void Watch(in Native.State st)
        {
            if (st.pounding == 0) poundTop = UnityHost.ToUnity(st.pos).y;
            fallingSuper = st.pounding == 2;
        }

        /// Ground pound damage times this for a fall of `drop` metres.
        static float PoundScale(float drop) =>
            1f + Mathf.Clamp(drop, 0f, 500f) * Mathf.Max(0f, Plugin.PoundPerMetre.Value);

        /// Lands the step's hits.
        public static void End()
        {
            var body = attacker;
            var it = items;
            attacker = null;
            items = null;
            if (!body) return;
            Vector3 mario = body.footPosition;
            bool ignite = Plugin.SuperPoundIgnites.Value;
            // A pound without a charge (the utility skill's) does Items.WeakPound of it.
            float strength = it != null ? it.PoundStrength : 1f;
            foreach (var (victim, kind) in blows)
            {
                // 0 stomp, 1 ground pound, 2 dive or slide.
                float coefficient = kind == 0 ? Plugin.StompDamage.Value
                    : kind == 1 ? Plugin.PoundDamage.Value * PoundScale(poundTop - mario.y) * strength
                    : Plugin.DiveDamage.Value;
                var at = victim.body ? victim.body.corePosition : victim.transform.position;
                var push = kind == 2 ? Flat(at - mario).normalized * 1200f : Vector3.down * 1500f;
                var type = kind == 1 && fallingSuper && ignite ? DamageType.IgniteOnHit : DamageType.Generic;
                Hit(body, victim, coefficient, at, push, 1f, type);
            }
            if (pounded && Plugin.PoundRadius.Value > 0f)
            {
                // The landing's shock wave (Sunshine's mHipdropRadius reach), on every enemy
                // around, the pounded one too.
                float radius = Plugin.PoundRadius.Value * (superPound ? 1.5f : 1f);
                float coefficient = Plugin.ShockDamage.Value * PoundScale(poundDrop) * strength;
                var type = superPound && ignite ? DamageType.IgniteOnHit : DamageType.Generic;
                foreach (var victim in Near(poundAt, radius))
                {
                    var at = victim.body ? victim.body.corePosition : victim.transform.position;
                    var push = Flat(at - poundAt).normalized * 1000f + Vector3.up * 1000f;
                    Hit(body, victim, coefficient, at, push, 1f, type);
                }
                if (superPound && Plugin.PoundFlames.Value) Blast(poundAt, radius);
            }
            // H3AD-5T v2 on a full-strength pound.
            if (pounded) it?.Stomp(poundAt, poundDrop);
            foreach (var kv in water)
            {
                var (drops, sum) = kv.Value;
                var at = sum / drops;
                Hit(body, kv.Key, drops * Plugin.WaterDamage.Value, at,
                    Flat(at - mario).normalized * (40f * drops),
                    waterProcs.Coefficient(kv.Key, Plugin.WaterProc.Value));
            }
            // Water hitting counts as the primary in use, for Shuriken: a throw at most every
            // half second while it keeps hitting.
            if (water.Count > 0 && NetworkServer.active)
                body.GetComponent<NativeBody>()?.PulsePrimary(Plugin.ShurikenInterval.Value);
            blows.Clear();
            water.Clear();
        }

        /// The super pound's landing (Sunshine's MS_M_SPHIPD_HIT_*): RoR2's ignite burst.
        static void Blast(Vector3 at, float radius)
        {
            if (!blast) blast = LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/ImpactEffects/IgniteExplosionVFX");
            if (!blast) return;
            EffectManager.SpawnEffect(blast, new EffectData { origin = at, scale = radius },
                UnityEngine.Networking.NetworkServer.active);
        }

        static void Hit(CharacterBody body, HealthComponent victim, float coefficient, Vector3 at, Vector3 force,
            float proc, DamageType type = DamageType.Generic)
        {
            if (!victim || !victim.alive || coefficient <= 0f) return;
            var spec = new NativeHitSpec
            {
                coefficient = coefficient,
                position = at,
                force = force,
                procCoefficient = proc,
                damageType = type,
                color = DamageColorIndex.Default,
            };
            MarioItems.Dealing = true;
            try { NativeHit.Deal(body, victim, spec); }
            finally { MarioItems.Dealing = false; }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        static void OnEvent(IntPtr user, ref Native.Event e)
        {
            MarioItems.OnEvent(ref e);
            switch ((Native.EventKind)e.kind)
            {
                case Native.EventKind.EnemyHit:
                    if (byId.TryGetValue((uint)e.arg, out var hc)) blows.Add((hc, (int)e.value));
                    break;
                case Native.EventKind.WaterHit:
                    if (byId.TryGetValue((uint)e.arg, out var wet))
                    {
                        water.TryGetValue(wet, out var w);
                        water[wet] = (w.drops + Math.Max(1, (int)e.value), w.sum + UnityHost.ToUnity(e.pos));
                    }
                    break;
                case Native.EventKind.GroundPound:
                    pounded = true;
                    superPound = e.arg != 0;
                    poundDrop = e.value * UnityHost.Scale;
                    poundAt = attacker ? attacker.footPosition : Vector3.zero;
                    break;
            }
        }

        /// Living characters on other teams with a hurt box within `radius` of `at`.
        static List<HealthComponent> Near(Vector3 at, float radius)
        {
            found.Clear();
            int n = Physics.OverlapSphereNonAlloc(at, radius, overlap, LayerIndex.entityPrecise.mask,
                QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var hb = overlap[i] ? overlap[i].GetComponent<HurtBox>() : null;
                if (!hb) continue;
                var hc = hb.healthComponent;
                if (!hc || !hc.alive || found.Contains(hc)) continue;
                if (attacker && hc.body == attacker) continue;
                if (!TeamManager.IsTeamEnemy(team, hb.teamIndex)) continue;
                found.Add(hc);
            }
            return found;
        }

        static unsafe uint Enemies(IntPtr user, ref Native.Vec3 pos, float radius, Native.Enemy* output, uint max)
        {
            uint n = 0;
            foreach (var hc in Near(UnityHost.ToUnity(pos), radius * UnityHost.Scale))
            {
                if (n >= max) break;
                if (!Cylinder(hc, out var bounds)) continue;
                uint id = (uint)hc.GetInstanceID();
                byId[id] = hc;
                var feet = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                output[n++] = new Native.Enemy
                {
                    id = id,
                    pos = UnityHost.ToSim(feet),
                    radius = Mathf.Max(bounds.extents.x, bounds.extents.z) / UnityHost.Scale,
                    height = bounds.size.y / UnityHost.Scale,
                };
            }
            return n;
        }

        /// The box around all of a character's active hurt boxes.
        static bool Cylinder(HealthComponent hc, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var group = hc.body ? hc.body.hurtBoxGroup : null;
            if (group && group.hurtBoxes != null)
            {
                foreach (var hb in group.hurtBoxes)
                {
                    if (!hb || !hb.collider || !hb.collider.enabled || !hb.gameObject.activeInHierarchy) continue;
                    if (any) bounds.Encapsulate(hb.collider.bounds);
                    else bounds = hb.collider.bounds;
                    any = true;
                }
            }
            return any;
        }
    }
}
