using System;
using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Mario against RoR2's enemies. The sim asks which enemies are near (Host.enemies): the
    // characters on other teams whose hurt boxes are in reach, each as a standing cylinder
    // around all its hurt boxes. Mario stomps, pounds and is pushed by those, and FLUDD's drops
    // hit them; the sim reports each hit as an event, and after the step they land as RoR2
    // damage from Mario's body (ror2-common's NativeHit: his damage stat, crits, on-hit items,
    // kill credit). Water doing damage is this plugin's, not Sunshine's (where most enemies only
    // flinch or slip); each enemy's drops for a step land as one hit, so on-hit items see about
    // 30 hits a second at a low proc coefficient rather than one per drop.
    //
    // MarioDriver calls Begin before each sms_sim_step and End after it. Hits are the server's:
    // a client playing Mario (multiplayer is untested) deals none.
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

        static readonly Native.EventFn eventFn = OnEvent;
        static readonly unsafe Native.EnemiesFn enemiesFn = Enemies;
        public static IntPtr EventPtr => System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(eventFn);
        public static IntPtr EnemiesPtr => System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(enemiesFn);

        public static void Begin(CharacterBody body)
        {
            attacker = body;
            team = body && body.teamComponent ? body.teamComponent.teamIndex : TeamIndex.Player;
            byId.Clear();
            blows.Clear();
            water.Clear();
            pounded = false;
        }

        /// Lands the step's hits.
        public static void End()
        {
            var body = attacker;
            attacker = null;
            if (!body) return;
            Vector3 mario = body.footPosition;
            foreach (var (victim, kind) in blows)
            {
                // 0 stomp, 1 ground pound, 2 dive or slide.
                float coefficient = kind == 0 ? Plugin.StompDamage.Value
                    : kind == 1 ? Plugin.PoundDamage.Value
                    : Plugin.DiveDamage.Value;
                var at = victim.body ? victim.body.corePosition : victim.transform.position;
                var push = kind == 2 ? Flat(at - mario).normalized * 1200f : Vector3.down * 1500f;
                Hit(body, victim, coefficient, at, push, 1f);
            }
            if (pounded && Plugin.PoundRadius.Value > 0f)
            {
                // The landing's shock wave (Sunshine's mHipdropRadius reach), on every enemy
                // around, the pounded one too.
                float radius = Plugin.PoundRadius.Value * (superPound ? 1.5f : 1f);
                foreach (var victim in Near(poundAt, radius))
                {
                    var at = victim.body ? victim.body.corePosition : victim.transform.position;
                    var push = Flat(at - poundAt).normalized * 1000f + Vector3.up * 1000f;
                    Hit(body, victim, Plugin.ShockDamage.Value, at, push, 1f);
                }
            }
            foreach (var kv in water)
            {
                var (drops, sum) = kv.Value;
                var at = sum / drops;
                Hit(body, kv.Key, drops * Plugin.WaterDamage.Value, at,
                    Flat(at - mario).normalized * (40f * drops), Plugin.WaterProc.Value);
            }
            blows.Clear();
            water.Clear();
        }

        static void Hit(CharacterBody body, HealthComponent victim, float coefficient, Vector3 at, Vector3 force, float proc)
        {
            if (!victim || !victim.alive || coefficient <= 0f) return;
            var spec = new NativeHitSpec
            {
                coefficient = coefficient,
                position = at,
                force = force,
                procCoefficient = proc,
                damageType = DamageType.Generic,
                color = DamageColorIndex.Default,
            };
            NativeHit.Deal(body, victim, spec);
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        static void OnEvent(IntPtr user, ref Native.Event e)
        {
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
