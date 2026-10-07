using System;
using System.Runtime.InteropServices;

namespace MarioTest
{
    // The libsms C ABI (libsms/crates/libsms-ffi/src/lib.rs, SMS_ABI_VERSION 1). Layouts mirror
    // the #[repr(C)] structs there field for field. Sim units: about 1 cm, Y up, a facing angle
    // a (i16, 0x10000 per turn) points along (sin a, 0, cos a).
    internal static class Native
    {
        public const string Dll = "sms";
        public const uint AbiVersion = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct Vec3
        {
            public float x, y, z;
            public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GroundHit
        {
            public float height;
            public Vec3 normal;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RoofHit
        {
            public float height;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WallHit
        {
            public Vec3 normal;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Event
        {
            public uint kind;
            public int arg;
            public float value;
            public Vec3 pos;
            public IntPtr name;
        }

        public enum EventKind : uint
        {
            Other = 0, Jump = 1, Land = 2, GroundPound = 3, WallBump = 4, WaterEmit = 5,
            NozzleChanged = 6, TriggerFired = 7, Fell = 8, Damaged = 9, Down = 10, Sound = 11,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Pad
        {
            public float stick_x, stick_y;
            public float move_x, move_z;
            public byte use_move, a, b, l, r, x, l_analog, r_analog;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct State
        {
            public Vec3 pos;
            public Vec3 vel;
            public float forward_vel, anim_frame, water, alpha;
            public short face_yaw, model_yaw, health;
            public byte on_ground, has_fludd;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate byte GroundFn(IntPtr user, ref Vec3 pos, out GroundHit hit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate byte RoofFn(IntPtr user, ref Vec3 pos, out RoofHit hit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate uint WallsFn(IntPtr user, ref Vec3 center, float radius, uint max,
            out Vec3 outCenter, WallHit* outWalls);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventFn(IntPtr user, ref Event e);

        [StructLayout(LayoutKind.Sequential)]
        public struct Host
        {
            public IntPtr user;
            public IntPtr ground, roof, walls, evt;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sms_abi_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sms_sim_new(byte[] assetsDirUtf8, ref Vec3 pos, short yaw);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sms_sim_free(IntPtr sim);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sms_sim_step(IntPtr sim, ref Host host, ref Pad pad, short cameraYaw, float dt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte sms_sim_state(IntPtr sim, out State state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sms_sim_status_name(IntPtr sim);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sms_sim_clip_name(IntPtr sim);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sms_sim_teleport(IntPtr sim, ref Vec3 pos);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        /// Loads sms.dll from the plugin folder up front, so the [DllImport]s above bind to it
        /// whatever Mono's search path is.
        public static bool Load(string path, out string error)
        {
            if (LoadLibraryW(path) == IntPtr.Zero)
            {
                error = $"LoadLibrary({path}) failed: Win32 error {Marshal.GetLastWin32Error()}";
                return false;
            }
            uint v = sms_abi_version();
            if (v != AbiVersion)
            {
                error = $"{path} speaks ABI {v}, this plugin wants {AbiVersion}; rebuild both";
                return false;
            }
            error = null;
            return true;
        }

        public static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            var bytes = new byte[n];
            Marshal.Copy(p, bytes, 0, n);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public static byte[] CString(string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);
            return b;
        }
    }
}
