using System;
using System.Runtime.InteropServices;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // libsms's collision queries (TMap::checkGround / checkRoof / isTouchedWallsAndMoveXZ)
    // answered with Unity raycasts against RoR2's world layer. Sim units in, sim units out;
    // positions convert with Scale (metres per sim unit) on the same axes, since both games
    // are Y-up and a sim facing angle a points along (sin a, 0, cos a) like a Unity yaw.
    internal static class UnityHost
    {
        public static float Scale = 0.01f;

        // Sunshine's split (TBGCheckData): normals with |y| under 0.2 are walls; the rest are
        // floors (y > 0) or ceilings.
        const float WallNormalY = 0.2f;
        const int WallRays = 16;
        const float GroundReach = 2000f;
        const float RoofReach = 200f;

        static readonly Native.GroundFn groundFn = Ground;
        static readonly Native.RoofFn roofFn = Roof;
        static readonly unsafe Native.WallsFn wallsFn = Walls;

        static int Mask => LayerIndex.world.mask;

        /// The callback table; the delegates live in statics so the GC never collects them.
        public static Native.Host Table() => new Native.Host
        {
            user = IntPtr.Zero,
            ground = Marshal.GetFunctionPointerForDelegate(groundFn),
            roof = Marshal.GetFunctionPointerForDelegate(roofFn),
            walls = Marshal.GetFunctionPointerForDelegate(wallsFn),
            evt = IntPtr.Zero,
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

        static byte Ground(IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit)
        {
            hit = default;
            // A hair above the probe point, so a floor exactly at it counts.
            var from = ToUnity(pos) + Vector3.up * 0.001f;
            if (!Physics.Raycast(from, Vector3.down, out var h, GroundReach, Mask, QueryTriggerInteraction.Ignore))
                return 0;
            hit.height = h.point.y / Scale;
            hit.normal = new Native.Vec3(h.normal.x, h.normal.y, h.normal.z);
            return 1;
        }

        static byte Roof(IntPtr user, ref Native.Vec3 pos, out Native.RoofHit hit)
        {
            hit = default;
            var from = ToUnity(pos) - Vector3.up * 0.001f;
            if (!Physics.Raycast(from, Vector3.up, out var h, RoofReach, Mask, QueryTriggerInteraction.Ignore))
                return 0;
            hit.height = h.point.y / Scale;
            return 1;
        }

        // A ring of horizontal rays out to the cylinder's radius; every wall face they reach
        // inside it pushes the centre out along its horizontal normal. Two passes settle corners.
        static unsafe uint Walls(IntPtr user, ref Native.Vec3 center, float radius, uint max,
            out Native.Vec3 outCenter, Native.WallHit* outWalls)
        {
            var c = ToUnity(center);
            float r = radius * Scale;
            uint n = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                bool moved = false;
                for (int i = 0; i < WallRays; i++)
                {
                    float a = i * (2f * Mathf.PI / WallRays);
                    var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                    if (!Physics.Raycast(c, dir, out var h, r, Mask, QueryTriggerInteraction.Ignore))
                        continue;
                    if (Mathf.Abs(h.normal.y) >= WallNormalY)
                        continue;
                    var nh = new Vector3(h.normal.x, 0f, h.normal.z).normalized;
                    float d = Vector3.Dot(c - h.point, nh);
                    if (d >= r || d < -r)
                        continue;
                    c += nh * (r - d);
                    moved = true;
                    if (n < max && !Seen(outWalls, n, nh))
                    {
                        outWalls[n].normal = new Native.Vec3(nh.x, 0f, nh.z);
                        outWalls[n].flags = 0;
                        n++;
                    }
                }
                if (!moved) break;
            }
            var back = ToSim(c);
            outCenter = new Native.Vec3(back.x, center.y, back.z);
            return n;
        }

        static unsafe bool Seen(Native.WallHit* walls, uint n, Vector3 nh)
        {
            for (uint i = 0; i < n; i++)
            {
                var w = walls[i].normal;
                if (w.x * nh.x + w.z * nh.z > 0.99f) return true;
            }
            return false;
        }
    }
}
