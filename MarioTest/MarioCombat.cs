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
    // (ror2-common's ProcLimiter), at Items.WaterProcCoefficient, the rest at 0. A light
    // press's short burst (libsms's BurstHit) doesn't last, so each of its drops does
    // Damage.BurstMultiplier times as much.
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
        static readonly HashSet<HealthComponent> seen = new HashSet<HealthComponent>();
        // Host callbacks within one sms_sim_step see the same Unity collider poses. Movement
        // and damage run outside that call; invalidate before every owner's step.
        static readonly Dictionary<HealthComponent, Bounds> cylinders = new Dictionary<HealthComponent, Bounds>();
        // The sim's ids for this step's enemies.
        static readonly Dictionary<uint, HealthComponent> byId = new Dictionary<uint, HealthComponent>();
        // Hits reported during the step, landed after it.
        static readonly List<(HealthComponent victim, int kind)> blows = new List<(HealthComponent, int)>();
        // Each wet enemy's drops this step, their damage in drops (bursts count for more), and
        // where they hit (summed).
        static readonly Dictionary<HealthComponent, (int drops, float weight, Vector3 sum)> water =
            new Dictionary<HealthComponent, (int, float, Vector3)>();
        static bool pounded, superPound;
        static float fallFraction;
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

        // A client's Mario deals nothing itself (damage is the server's): the step's hits, the fall,
        // the Shuriken pulse and the super pound's blast go to the server (MarioNet.SendCombat),
        // which lands them as this Mario's.
        static readonly List<MarioNet.HitRecord> outHits = new List<MarioNet.HitRecord>();
        static readonly List<(Vector3 at, float radius)> outBlasts = new List<(Vector3, float)>();
        static bool outPulse;
        static float outFall;

        public static void Begin(CharacterBody body, MarioItems with)
        {
            outHits.Clear();
            outBlasts.Clear();
            outPulse = false;
            outFall = 0f;
            attacker = body;
            items = with;
            team = body && body.teamComponent ? body.teamComponent.teamIndex : TeamIndex.Player;
            byId.Clear();
            cylinders.Clear();
            blows.Clear();
            water.Clear();
            pounded = false;
            fallFraction = 0f;
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
                // 0 stomp, 1 ground pound, 2 dive or slide, 3 the Turbo corkscrew dive.
                float coefficient = kind == 0 ? Plugin.StompDamage.Value
                    : kind == 1 ? Plugin.PoundDamage.Value * PoundScale(poundTop - mario.y) * strength
                    : kind == 3 ? Plugin.CorkscrewDamage.Value
                    : kind == 4 ? Plugin.SuperCorkscrewDamage.Value
                    : kind == 5 ? Plugin.RocketSpinDamage.Value
                    : Plugin.DiveDamage.Value;
                var at = victim.body ? victim.body.corePosition : victim.transform.position;
                var push = kind == 5 ? Vector3.up * 2500f
                    : kind >= 2 ? Flat(at - mario).normalized * (kind == 4 ? 4000f : kind == 3 ? 2400f : 1200f) : Vector3.down * 1500f;
                var type = kind == 1 && fallingSuper && ignite ? DamageType.IgniteOnHit : DamageType.Generic;
                // Stomps are his basic attack (the primary), pounds the utility, the dive and the
                // belly slide the special.
                var source = kind == 0 ? DamageSource.Primary : kind == 1 ? DamageSource.Utility : DamageSource.Special;
                Hit(body, victim, coefficient, at, push, 1f, type, source);
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
                    Hit(body, victim, coefficient, at, push, 1f, type, DamageSource.Utility);
                }
                if (superPound && Plugin.PoundFlames.Value)
                {
                    if (NetworkServer.active) Blast(poundAt, radius);
                    else outBlasts.Add((poundAt, radius));
                }
            }
            // H3AD-5T v2 on a full-strength pound.
            if (pounded) it?.Stomp(poundAt, poundDrop);
            foreach (var kv in water)
            {
                var (drops, weight, sum) = kv.Value;
                var at = sum / drops;
                Hit(body, kv.Key, weight * Plugin.WaterDamage.Value, at,
                    Flat(at - mario).normalized * (40f * drops),
                    waterProcs.Coefficient(kv.Key, Plugin.WaterProc.Value), DamageType.Generic, DamageSource.Primary, true);
            }
            // Water hitting counts as the primary in use, for Shuriken: a throw at most every
            // half second while it keeps hitting.
            if (water.Count > 0)
            {
                if (NetworkServer.active) body.GetComponent<NativeBody>()?.PulsePrimary(Plugin.ShurikenInterval.Value);
                else outPulse = true;
            }
            if (fallFraction > 0f && !NetworkServer.active && body.healthComponent && body.healthComponent.alive)
                outFall = fallFraction;
            if (fallFraction > 0f && NetworkServer.active && GlobalEventManager.instance
                && body.healthComponent && body.healthComponent.alive)
            {
                // RoR2's OnCharacterHitGroundServer converts excess vertical speed to
                // max-health damage: (abs(v.y) - jumpPower - 20) / 60. Supply our height
                // policy through that entry point so fall boots, armor, Frailty and
                // Eclipse's lethal falls retain the game's normal behavior.
                float speed = body.jumpPower + 20f + fallFraction * 60f;
                GlobalEventManager.instance.OnCharacterHitGroundServer(body, new CharacterMotor.HitGroundInfo { velocity = Vector3.down * speed, position = body.footPosition, ownerBodyObject = body.gameObject, isValidForEffect = true });
            }
            blows.Clear();
            water.Clear();
            if (!NetworkServer.active)
                MarioNet.SendCombat(body, outHits, outBlasts, outPulse, outFall);
            outHits.Clear();
            outBlasts.Clear();
        }

        /// What the server does with a client Mario's hit: the same as his own would be.
        internal static void DealFromClient(CharacterBody body, HealthComponent victim, in MarioNet.HitRecord hit)
        {
            Hit(body, victim, hit.coefficient, hit.at, hit.force, hit.proc,
                hit.ignite ? DamageType.IgniteOnHit : DamageType.Generic, (DamageSource)hit.source, hit.water);
        }

        /// The super pound's landing (Sunshine's MS_M_SPHIPD_HIT_*): RoR2's ignite burst.
        internal static void Blast(Vector3 at, float radius)
        {
            if (!blast) blast = LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/ImpactEffects/IgniteExplosionVFX");
            if (!blast) return;
            EffectManager.SpawnEffect(blast, new EffectData { origin = at, scale = radius },
                UnityEngine.Networking.NetworkServer.active);
        }

        /// One Melee A hitbox at `at`: every enemy in reach takes the disc percent / 3. On a
        /// client the hits are queued and sent to the server in `End`, the same path stomps and
        /// water use, so a client's Melee punches land with his damage stat and items.
        internal static void MeleeHit(CharacterBody body, Vector3 at, float radius, float coefficient, Vector3 force)
        {
            if (coefficient <= 0f) return;
            foreach (var victim in Near(at, radius)) Hit(body, victim, coefficient, at, force, 1f);
        }

        /// `source`: the skill it came from, for the items that only answer skill hits (Breaching
        /// Fin, Luminous Shot, Infernal Mark, Nightmare Demon, Vile Censer).
        static void Hit(CharacterBody body, HealthComponent victim, float coefficient, Vector3 at, Vector3 force,
            float proc, DamageType type = DamageType.Generic, DamageSource source = DamageSource.Primary, bool waterHit = false)
        {
            if (!victim || !victim.alive || coefficient <= 0f) return;
            if (!NetworkServer.active)
            {
                // Sent after the step; the server lands it.
                if (victim.body && victim.body.netId.Value != 0)
                    outHits.Add(new MarioNet.HitRecord
                    {
                        victim = victim.body.netId.Value, coefficient = coefficient, at = at, force = force,
                        proc = proc, ignite = (type & DamageType.IgniteOnHit) != 0, source = (byte)source,
                        water = waterHit,
                    });
                return;
            }
            DamageTypeCombo combo = type;
            // Classify on the server, including client-owned Mario's hits. Never multiply a
            // client coefficient twice, and never grant the water bonus to stomps/pounds.
            if (waterHit && MarioFire.Vulnerable(victim.body))
                coefficient *= Mathf.Max(1f, Plugin.FireWaterMultiplier.Value);
            combo.damageSource = source;
            var spec = new NativeHitSpec
            {
                coefficient = coefficient,
                position = at,
                force = force,
                procCoefficient = proc,
                damageType = combo,
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
                case Native.EventKind.FallImpact:
                    if (Plugin.FallDamageEnabled.Value)
                        fallFraction = Mathf.Max(fallFraction, MarioFallDamage.Fraction(e.value / 160f, e.arg,
                            Plugin.FallSafeHeight.Value, Plugin.DiveSafeHeight.Value,
                            Plugin.FallPerHeight.Value, Plugin.DiveFallMultiplier.Value));
                    break;
                case Native.EventKind.EnemyHit:
                    if (byId.TryGetValue((uint)e.arg, out var hc))
                    {
                        blows.Add((hc, (int)e.value));
                        // The super corkscrew's impact costs Mario a share of his health, the way a fall does.
                        if ((int)e.value == 4) fallFraction = Mathf.Max(fallFraction, Mathf.Clamp01(Plugin.SuperCorkscrewRecoil.Value));
                    }
                    break;
                case Native.EventKind.Put:
                case Native.EventKind.Throw:
                case Native.EventKind.ThrowAim:
                    MarioShipParts.OnEvent(ref e);
                    break;
                case Native.EventKind.WaterHit:
                case Native.EventKind.BurstHit:
                case Native.EventKind.SpinHit:
                    if (byId.TryGetValue((uint)e.arg, out var wet))
                    {
                        water.TryGetValue(wet, out var w);
                        int n = Math.Max(1, (int)e.value);
                        // A drop from a light press's burst or a spin jump's spray counts for more (its own
                        // multiple of WaterPerDrop).
                        float k = e.kind == (uint)Native.EventKind.BurstHit
                            ? Mathf.Max(0f, Plugin.BurstMultiplier.Value)
                            : e.kind == (uint)Native.EventKind.SpinHit ? Mathf.Max(0f, Plugin.SpinSprayMultiplier.Value) : 1f;
                        water[wet] = (w.drops + n, w.weight + n * k, w.sum + UnityHost.ToUnity(e.pos));
                    }
                    break;
                case Native.EventKind.ShineDemo:
                    MarioShine.Instance?.DemoStarted(attacker, e.pos, (short)e.arg);
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
            seen.Clear();
            int n = Physics.OverlapSphereNonAlloc(at, radius, overlap, LayerIndex.entityPrecise.mask,
                QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var hb = overlap[i] ? overlap[i].GetComponent<HurtBox>() : null;
                if (!hb) continue;
                var hc = hb.healthComponent;
                if (!hc || !hc.alive) continue;
                if (attacker && hc.body == attacker) continue;
                if (!TeamManager.IsTeamEnemy(team, hb.teamIndex)) continue;
                if (!seen.Add(hc)) continue;
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
            if (cylinders.TryGetValue(hc, out bounds)) return true;
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
            if (any) cylinders[hc] = bounds;
            return any;
        }
    }
}
