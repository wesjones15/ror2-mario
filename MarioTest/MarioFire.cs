using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // RoR2's fire classification and FLUDD's host-only interactions. Water geometry comes
    // from libsms, shared by the owner and replay sims; it is never an aim-cone estimate.
    internal static class MarioFire
    {
        static readonly DotController.DotIndex[] fires = {
            DotController.DotIndex.Burn, DotController.DotIndex.PercentBurn,
            DotController.DotIndex.StrongerBurn, DotController.DotIndex.Helfire,
        };

        internal static bool FireDot(DotController.DotIndex index) => Array.IndexOf(fires, index) >= 0;

        internal static bool Burning(CharacterBody body)
        {
            if (!body) return false;
            if (body.HasBuff(RoR2Content.Buffs.OnFire)) return true;
            foreach (var index in fires)
            {
                var buff = DotController.GetDotDef(index)?.associatedBuff;
                if (buff && body.HasBuff(buff)) return true;
            }
            return false;
        }

        internal static bool Vulnerable(CharacterBody body)
        {
            if (!body) return false;
            if (Burning(body) || body.HasBuff(RoR2Content.Buffs.AffixRed)) return true;
            switch (BodyCatalog.GetBodyName(body.bodyIndex))
            {
                case "WispBody": case "GreaterWispBody": case "ArchWispBody":
                case "AncientWispBody": case "MagmaWormBody": case "FireSpiritBody":
                    return true;
                default: return false;
            }
        }

        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo points = typeof(DamageTrail).GetField("pointsList", Instance);
        static readonly Type pointType = points?.FieldType.GetGenericArguments()[0];
        static readonly FieldInfo position = pointType?.GetField("position", Instance);
        static readonly FieldInfo startTime = pointType?.GetField("localStartTime", Instance);
        static readonly FieldInfo segment = pointType?.GetField("segmentTransform", Instance);
        sealed class State
        {
            public State() { }
            public readonly HashSet<float> quenched = new HashSet<float>();
            public readonly HashSet<float> alive = new HashSet<float>();
        }
        static ConditionalWeakTable<DamageTrail, State> trails = new ConditionalWeakTable<DamageTrail, State>();
        static ILHook damageHook;
        static Hook updateHook;

        internal static void Initialize()
        {
            if (damageHook != null) return;
            try
            {
                if (points == null || position == null || startTime == null || segment == null)
                    throw new MissingFieldException("DamageTrail point layout changed");
                // DLC4 DamageTrail.DoDamage visits segment (local 5 -> local 0), with
                // point index local 4. Skip only its overlap query, then advance the previous
                // point normally. Removing points would join the two dry ends across a gap.
                damageHook = new ILHook(typeof(DamageTrail).GetMethod("DoDamage", Instance), PatchTrailDamage);
                updateHook = new Hook(typeof(DamageTrail).GetMethod("MyFixedUpdate", Instance),
                    (Action<Action<DamageTrail, float>, DamageTrail, float>)AfterUpdate);
                Run.onRunDestroyGlobal += _ => trails = new ConditionalWeakTable<DamageTrail, State>();
                Plugin.Log.LogInfo("FLUDD fire trails: segment damage and visibility hooks installed");
            }
            catch (Exception e)
            {
                updateHook?.Dispose(); updateHook = null;
                damageHook?.Dispose(); damageHook = null;
                Plugin.Log.LogError("FLUDD cannot extinguish fire trails on this game build: " + e);
            }
        }

        internal static void PatchTrailDamage(ILContext il)
        {
            var skip = new ILCursor(il);
            if (!skip.TryGotoNext(MoveType.Before,
                i => i.MatchLdloc(5), i => i.MatchStloc(0), i => i.MatchLdloc(4)))
                throw new InvalidOperationException("DamageTrail segment continuation changed");
            var next = skip.MarkLabel();
            var c = new ILCursor(il);
            if (!c.TryGotoNext(MoveType.Before,
                i => i.MatchLdloc(5), i => i.MatchLdloc(0), i => i.MatchCall<Vector3>("op_Subtraction")))
                throw new InvalidOperationException("DamageTrail overlap geometry changed");
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldloc, il.Body.Variables[4]);
            c.Emit(OpCodes.Ldloc, il.Body.Variables[5]);
            c.Emit(OpCodes.Ldloc_0);
            c.EmitDelegate<Func<DamageTrail, int, Vector3, Vector3, bool>>(Quenched);
            c.Emit(OpCodes.Brtrue, next);
        }

        static bool FireTrail(DamageTrail trail) => trail &&
            (trail.name.StartsWith("FireTrail", StringComparison.Ordinal)
                || (trail.damageType & (DamageType.IgniteOnHit | DamageType.PercentIgniteOnHit)) != 0
                || (trail.damageType.damageTypeExtended & DamageTypeExtended.FireNoIgnite) != 0);

        static bool Quenched(DamageTrail trail, int index, Vector3 a, Vector3 b)
        {
            if (!Plugin.ExtinguishFireTrails.Value || !FireTrail(trail)) return false;
            var list = (IList)points.GetValue(trail);
            if (list == null || index < 0 || index >= list.Count) return false;
            var state = trails.GetOrCreateValue(trail);
            float key = (float)startTime.GetValue(list[index]);
            if (state.quenched.Contains(key)) return true;
            if (!MarioSims.WaterTouches(a, b, Mathf.Max(trail.radius, trail.height))) return false;
            state.quenched.Add(key);
            return true;
        }

        static void AfterUpdate(Action<DamageTrail, float> original, DamageTrail trail, float dt)
        {
            original(trail, dt);
            if (!Plugin.ExtinguishFireTrails.Value || !FireTrail(trail)) return;
            var list = (IList)points.GetValue(trail);
            if (list == null || list.Count < 2) return;
            var state = trails.GetOrCreateValue(trail);
            state.alive.Clear();
            for (int i = 0; i < list.Count - 1; i++)
            {
                object point = list[i];
                state.alive.Add((float)startTime.GetValue(point));
                var a = (Vector3)position.GetValue(point);
                var b = (Vector3)position.GetValue(list[i + 1]);
                if (!Quenched(trail, i, a, b)) continue;
                var visual = (Transform)segment.GetValue(point);
                if (visual) visual.gameObject.SetActive(false);
            }
            state.quenched.IntersectWith(state.alive);
        }
    }
}
