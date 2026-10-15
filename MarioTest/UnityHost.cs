using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // libsms's collision queries (TMap::checkGround / checkRoof / isTouchedWallsAndMoveXZ)
    // answered by ror2-common's WorldProbe against RoR2's world layer. Sim units in, sim units
    // out; positions convert with Scale (metres per sim unit) on the same axes, since both
    // games are Y-up and a sim facing angle a points along (sin a, 0, cos a) like a Unity yaw.
    internal static class UnityHost
    {
        public static float Scale = 0.01f;
        /// Mario's standing height in Sunshine units (the sim's head clearance).
        public const float MarioHeight = 160f;

        // Sunshine's split (TBGCheckData): normals with |y| under 0.2 are walls; the rest are
        // floors (y > 0) or ceilings.
        const float WallNormalY = 0.2f;
        const float GroundReach = 2000f;
        // Where a floor is pretended over a pit: 500 m below the probe, in sim units.
        const float VoidFloorDrop = 50000f;
        const float RoofReach = 200f;

        static readonly Native.GroundFn groundFn = Ground;
        static readonly Native.RoofFn roofFn = Roof;
        static readonly unsafe Native.WallsFn wallsFn = Walls;
        static readonly Native.WaterFn waterFn = Water;
        static readonly List<Vector3> wallScratch = new List<Vector3>();

        /// The callback table; the delegates live in statics so the GC never collects them.
        public static Native.Host Table() => new Native.Host
        {
            user = IntPtr.Zero,
            ground = Marshal.GetFunctionPointerForDelegate(groundFn),
            roof = Marshal.GetFunctionPointerForDelegate(roofFn),
            walls = Marshal.GetFunctionPointerForDelegate(wallsFn),
            evt = MarioCombat.EventPtr,
            enemies = MarioCombat.EnemiesPtr,
            water = Marshal.GetFunctionPointerForDelegate(waterFn),
        };

        public static Vector3 ToUnity(Native.Vec3 v) => new Vector3(v.x, v.y, v.z) * Scale;
        public static Native.Vec3 ToSim(Vector3 v)
        {
            v /= Scale;
            return new Native.Vec3(v.x, v.y, v.z);
        }

        /// A Unity direction's sim facing angle.
        public static short Yaw(Vector3 dir) =>
            unchecked((short)Mathf.RoundToInt(Mathf.Atan2(dir.x, dir.z) * 32768f / Mathf.PI));

        public static Vector3 Forward(short yaw)
        {
            float a = yaw * Mathf.PI / 32768f;
            return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        /// A supported, clear standing position for host recovery, in metres. The capsule
        /// uses libsms's 50-unit wall radius and 160-unit height (Mario::WALL_RADIUS/HEIGHT).
        public static bool RecoveryGround(Vector3 sample, float rise, float drop, out Vector3 feet)
        {
            return WorldProbe.StandingGround(sample, rise, drop, MarioHeight * Scale, 50f * Scale, out feet);
        }

        static byte Ground(IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit)
        {
            hit = default;
            if (!WorldProbe.Ground(ToUnity(pos), GroundReach, out var h))
            {
                // No floor at all. Sunshine's answer is a void, which turns Mario back (a gap
                // in the map was an invisible wall). RoR2's characters walk off such an edge
                // and fall to the kill zone, so Mario gets a floor out of reach below him, within
                // the 100,000 units where libsms still takes the floor for real.
                hit.height = pos.y - VoidFloorDrop;
                hit.normal = new Native.Vec3(0f, 1f, 0f);
                return 1;
            }
            hit.height = h.point.y / Scale;
            hit.normal = new Native.Vec3(h.normal.x, h.normal.y, h.normal.z);
            return 1;
        }

        /// Heights above Mario's feet, in metres, where a step is swept for walls (MarioDriver):
        /// near the sim's own wall rings (30 and 150 units) and the middle of him.
        public static readonly float[] SweepHeights = { 0.3f, 0.9f, 1.5f };
        public const float SweepWallNormalY = WallNormalY;

        static byte Roof(IntPtr user, ref Native.Vec3 pos, out Native.RoofHit hit)
        {
            hit = default;
            if (!WorldProbe.Roof(ToUnity(pos), RoofReach, out var h)) return 0;
            hit.height = h.point.y / Scale;
            return 1;
        }

        static unsafe uint Walls(IntPtr user, ref Native.Vec3 center, float radius, uint max,
            out Native.Vec3 outCenter, Native.WallHit* outWalls)
        {
            wallScratch.Clear();
            var c = WorldProbe.PushOutOfWalls(ToUnity(center), radius * Scale, WallNormalY, wallScratch, (int)max);
            for (int i = 0; i < wallScratch.Count; i++)
            {
                outWalls[i].normal = new Native.Vec3(wallScratch[i].x, 0f, wallScratch[i].z);
                outWalls[i].flags = 0;
            }
            var back = ToSim(c);
            outCenter = new Native.Vec3(back.x, center.y, back.z);
            return (uint)wallScratch.Count;
        }

        // ---- water ----------------------------------------------------------------------
        // RoR2 has no water for the sim to read, so anything that looks like water counts:
        // colliders (triggers too) on Unity's "Water" layer, or whose object, material or
        // footstep surface (SurfaceDefProvider) is named for water. Answers libsms's
        // TPoolManager::getWaterLevel stand-in: the surface over Mario's feet, if any.

        const int UnityWaterLayer = 4;
        static readonly RaycastHit[] waterHits = new RaycastHit[16];
        static readonly Collider[] waterOverlaps = new Collider[16];
        static readonly Dictionary<int, bool> waterCache = new Dictionary<int, bool>();
        /// The water Mario was last found in (for the log).
        public static string LastWater;

        static byte Water(IntPtr user, ref Native.Vec3 pos, out float level)
        {
            level = 0f;
            var feet = ToUnity(pos);
            float height = MarioHeight * Scale;
            float best = float.NegativeInfinity;
            Collider found = null;
            // Down from over his head to a little under his feet: water surfaces, and water
            // he stands on (a solid surface counts as a puddle over it).
            int n = Physics.RaycastNonAlloc(feet + Vector3.up * height, Vector3.down, waterHits,
                height + 0.05f, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var h = waterHits[i];
                if (h.point.y > best && IsWater(h.collider))
                {
                    best = h.point.y;
                    found = h.collider;
                }
            }
            // Or inside a water volume altogether (rays starting inside one miss it).
            if (found == null)
            {
                n = Physics.OverlapSphereNonAlloc(feet + Vector3.up * (0.1f * height), 0.05f * height,
                    waterOverlaps, ~0, QueryTriggerInteraction.Collide);
                for (int i = 0; i < n; i++)
                {
                    var c = waterOverlaps[i];
                    if (c && c.bounds.max.y > best && IsWater(c))
                    {
                        best = c.bounds.max.y;
                        found = c;
                    }
                }
            }
            if (found == null) return 0;
            LastWater = found.name;
            level = Mathf.Max(best, feet.y + 0.02f * height) / Scale;
            return 1;
        }

        static bool IsWater(Collider c)
        {
            if (!c) return false;
            int id = c.GetInstanceID();
            if (waterCache.TryGetValue(id, out bool w)) return w;
            w = c.gameObject.layer == UnityWaterLayer
                || Watery(c.name) || Watery(c.transform.parent ? c.transform.parent.name : null)
                || (c.sharedMaterial && Watery(c.sharedMaterial.name));
            if (!w)
            {
                var r = c.GetComponent<Renderer>();
                w = r && r.sharedMaterial && Watery(r.sharedMaterial.name);
            }
            if (!w)
            {
                var s = c.GetComponent<SurfaceDefProvider>();
                w = s && s.surfaceDef && Watery(s.surfaceDef.name);
            }
            if (waterCache.Count > 4096) waterCache.Clear();
            waterCache[id] = w;
            return w;
        }

        static bool Watery(string name) =>
            name != null && name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0;

        /// Logs what in this stage counts as water (once a stage, for tuning IsWater).
        public static void LogStageWater()
        {
            var names = new List<string>();
            int count = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (!IsWater(c)) continue;
                count++;
                if (names.Count < 8 && !names.Contains(c.name)) names.Add(c.name);
            }
            Plugin.Log.LogInfo(count == 0
                ? "No water in this stage for FLUDD to fill up from"
                : $"Water in this stage: {count} colliders ({string.Join(", ", names)})");
        }
    }
}
