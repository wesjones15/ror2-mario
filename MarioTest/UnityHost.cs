using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ror2Common;
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

        // Sunshine's split (TBGCheckData): normals with |y| under 0.2 are walls; the rest are
        // floors (y > 0) or ceilings.
        const float WallNormalY = 0.2f;
        const float GroundReach = 2000f;
        const float RoofReach = 200f;

        static readonly Native.GroundFn groundFn = Ground;
        static readonly Native.RoofFn roofFn = Roof;
        static readonly unsafe Native.WallsFn wallsFn = Walls;
        static readonly List<Vector3> wallScratch = new List<Vector3>();

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
            if (!WorldProbe.Ground(ToUnity(pos), GroundReach, out var h)) return 0;
            hit.height = h.point.y / Scale;
            hit.normal = new Native.Vec3(h.normal.x, h.normal.y, h.normal.z);
            return 1;
        }

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
    }
}
