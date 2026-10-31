using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RoR2;
using RoR2.Projectile;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MarioTest
{
    // FLUDD's water as a shield (Fludd.SprayDeflects; not Sunshine's, where nothing shoots back):
    // while a Mario's drops are in the air,
    //  - an enemy projectile whose flight this step crosses them is turned back at whoever fired it
    //    (or straight back) and is that Mario's from then on: his team, his kill credit;
    //  - an enemy bullet, or a laser's blast at the far end of its beam (the Stone Golem's), whose
    //    line crosses them stops at the water and hurts nobody past it.
    // The water is the sims' own drops (MarioSims: the owner's sim and the replay sims alike, as
    // fire trails are put out in MarioFire), never an aim cone. Server only: RoR2's enemies shoot there.
    internal sealed class MarioDeflect : MonoBehaviour
    {
        // About a projectile's own size (most are a few tenths of a metre across), and a bullet's.
        const float ProjectileRadius = 0.5f, BulletRadius = 0.3f;
        // A blast this far from its attacker is a laser's end, not his own slam or nova.
        const float LaserReach = 4f;

        delegate bool HitCallback(BulletAttack bullet, ref BulletAttack.BulletHit hit);
        delegate BlastAttack.Result BlastFire(BlastAttack blast);
        static Hook bulletHook, blastHook;
        static bool logged;

        // Where each enemy projectile was last step, for the segment it flew since.
        readonly Dictionary<ProjectileController, Vector3> last = new Dictionary<ProjectileController, Vector3>();
        readonly HashSet<ProjectileController> seen = new HashSet<ProjectileController>();
        readonly List<ProjectileController> gone = new List<ProjectileController>();

        const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static void Initialize()
        {
            if (bulletHook != null) return;
            try
            {
                bulletHook = new Hook(typeof(BulletAttack).GetMethod("DefaultHitCallbackImplementation", Any),
                    typeof(MarioDeflect).GetMethod(nameof(OnBulletHit), Any));
                blastHook = new Hook(typeof(BlastAttack).GetMethod("Fire", Any, null, Type.EmptyTypes, null),
                    typeof(MarioDeflect).GetMethod(nameof(OnBlast), Any));
                Plugin.Log.LogInfo("FLUDD deflects: bullet and laser hooks installed");
            }
            catch (Exception e)
            {
                bulletHook?.Dispose(); bulletHook = null;
                blastHook?.Dispose(); blastHook = null;
                Plugin.Log.LogError("FLUDD cannot stop bullets on this game build (projectiles are still turned back): " + e);
            }
        }

        void Awake() => SceneManager.activeSceneChanged += SceneChanged;
        void OnDestroy() => SceneManager.activeSceneChanged -= SceneChanged;
        void SceneChanged(Scene from, Scene to) { last.Clear(); logged = false; }

        static bool On => Plugin.SprayDeflects.Value && NetworkServer.active && MarioSims.Count > 0;

        // An attacker on a team against `mario`'s.
        static bool Against(CharacterBody mario, TeamIndex team) =>
            mario && mario.teamComponent && team != TeamIndex.None
            && TeamMask.GetEnemyTeams(mario.teamComponent.teamIndex).HasTeam(team);

        static TeamIndex TeamOf(GameObject attacker)
        {
            var t = attacker ? attacker.GetComponent<TeamComponent>() : null;
            return t ? t.teamIndex : TeamIndex.None;
        }

        static void Note(string what)
        {
            if (logged) return;
            logged = true;
            Plugin.Log.LogInfo($"FLUDD's water stops {what} (first this stage)");
        }

        // ---- projectiles: turned back ----------------------------------------------------------

        void FixedUpdate()
        {
            if (!On)
            {
                if (last.Count > 0) last.Clear();
                return;
            }
            seen.Clear();
            foreach (var p in InstanceTracker.GetInstancesList<ProjectileController>())
            {
                if (!p || p.cannotBeDeleted || !p.teamFilter) continue;
                var at = p.transform.position;
                seen.Add(p);
                if (!last.TryGetValue(p, out var from)) from = at;
                last[p] = at;
                var rb = p.GetComponent<Rigidbody>();
                if (!rb || rb.isKinematic && !p.GetComponent<ProjectileSimple>()) continue;
                if (!MarioSims.WaterTouches(from, at, ProjectileRadius, out var mario)) continue;
                if (!Against(mario, p.teamFilter.teamIndex)) continue;
                TurnBack(p, rb, mario);
            }
            gone.Clear();
            foreach (var p in last.Keys) if (!seen.Contains(p)) gone.Add(p);
            foreach (var p in gone) last.Remove(p);
        }

        static void TurnBack(ProjectileController p, Rigidbody rb, CharacterBody mario)
        {
            var at = p.transform.position;
            float speed = rb.velocity.magnitude;
            var simple = p.GetComponent<ProjectileSimple>();
            if (simple) speed = Mathf.Max(speed, simple.desiredForwardSpeed);
            // At whoever fired it, else back the way it came.
            var shooter = p.owner ? p.owner.GetComponent<CharacterBody>() : null;
            var back = shooter ? shooter.corePosition - at : -rb.velocity;
            if (back.sqrMagnitude < 1e-4f) back = -p.transform.forward;
            back.Normalize();
            p.transform.rotation = Quaternion.LookRotation(back);
            rb.velocity = back * speed;
            // Mario's from now on: it hits his enemies, and its kills are his.
            // (It flew through its shooter's colliders; now it hits him, and passes through Mario's.)
            p.IgnoreCollisionsWithOwner(false);
            p.teamFilter.teamIndex = mario.teamComponent.teamIndex;
            p.Networkowner = mario.gameObject;
            p.IgnoreCollisionsWithOwner(true);
            var target = p.GetComponent<ProjectileTargetComponent>();
            if (target) target.target = shooter ? shooter.coreTransform : null;
            Note("a projectile");
        }

        // ---- bullets and lasers: stopped -------------------------------------------------------

        // A bullet that reaches this hit through the water stops at it, and deals nothing.
        static bool OnBulletHit(HitCallback orig, BulletAttack bullet, ref BulletAttack.BulletHit hit)
        {
            try
            {
                if (On && bullet != null && hit != null
                    && MarioSims.WaterTouches(bullet.origin, hit.point, BulletRadius, out var mario)
                    && Against(mario, TeamOf(bullet.owner)))
                {
                    Note("a bullet");
                    return false;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("FLUDD bullet check: " + e); }
            return orig(bullet, ref hit);
        }

        // A laser's blast (one its attacker sets off himself, well away from him) whose beam crossed the water.
        static BlastAttack.Result OnBlast(BlastFire orig, BlastAttack blast)
        {
            try
            {
                var body = blast != null && blast.attacker && blast.inflictor == blast.attacker
                    ? blast.attacker.GetComponent<CharacterBody>() : null;
                if (On && body)
                {
                    var from = body.aimOrigin;
                    if ((blast.position - from).sqrMagnitude > LaserReach * LaserReach
                        && MarioSims.WaterTouches(from, blast.position, BulletRadius, out var mario)
                        && Against(mario, blast.teamIndex))
                    {
                        Note("a laser");
                        return default;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("FLUDD laser check: " + e); }
            return orig(blast);
        }
    }
}
