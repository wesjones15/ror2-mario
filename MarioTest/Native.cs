using System;
using System.Runtime.InteropServices;
using Ror2Common;

namespace MarioTest
{
    // The libsms C ABI (libsms/crates/libsms-ffi, SMS_ABI_VERSION 7). Layouts mirror the
    // #[repr(C)] structs there field for field; Load checks them with sms_struct_sizes. Sim
    // units: about 1 cm, Y up, a facing angle a (i16, 0x10000 per turn) points along
    // (sin a, 0, cos a). The view's structs are ror2-common's (ViewModelInfo...), which
    // libpikmin's pik_view_* shares.
    internal static class Native
    {
        public const string Dll = "sms";
        public const uint AbiVersion = 7;

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
            /// arg: the enemy's id; value: 0 stomp, 1 ground pound, 2 dive or slide.
            EnemyHit = 12,
            /// arg: the enemy's id; value: the drop's attack; pos: where it hit.
            WaterHit = 13,
        }

        /// An enemy for the sim: a standing cylinder at its feet.
        [StructLayout(LayoutKind.Sequential)]
        public struct Enemy
        {
            public uint id;
            public Vec3 pos;
            public float radius, height;
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
            /// 1 falling in a ground pound, 2 in a super pound (Sunshine's flames), else 0.
            public byte pounding, pad0, pad1, pad2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ViewFrame
        {
            public float dt;
            public Vec3 eye, forward, up;
            public float volume;
            /// 120 Hz ticks behind the sim that Mario is drawn (our smoothing); the water too.
            public float lag;
        }

        /// Host tuning; ones and zeroes are the game.
        [StructLayout(LayoutKind.Sequential)]
        public struct Tuning
        {
            public float jump_height, jump_buffer, chain_grace;
            public float move_speed, spray_reach, stomp_bounce, rocket_height;
            public float pound_jump, stomp_jump, short_hop;
            public byte enemy_push, cycle_nozzles, pad0, pad1;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WaterVertex
        {
            public Vec3 pos;
            public float u, v, alpha;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WaterInfo
        {
            public uint drop_quads, splash_quads;
            public float r, g, b, a;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FxVertex
        {
            public Vec3 pos;
            public float u, v, r, g, b, a;
        }

        /// `blend` 0 added, 1 see-through.
        [StructLayout(LayoutKind.Sequential)]
        public struct FxBatch
        {
            public uint texture, first_vertex, vertex_count, blend;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FxInfo
        {
            public uint batches, vertices, textures;
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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate uint EnemiesFn(IntPtr user, ref Vec3 pos, float radius, Enemy* output, uint max);

        [StructLayout(LayoutKind.Sequential)]
        public struct Host
        {
            public IntPtr user;
            public IntPtr ground, roof, walls, evt, enemies;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_abi_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_tick_hz();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_struct_sizes([Out] uint[] sizes, uint max);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_sim_new(byte[] assetsDirUtf8, ref Vec3 pos, short yaw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_free(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_sim_step(IntPtr sim, ref Host host, ref Pad pad, short cameraYaw, float dt, IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_sim_state(IntPtr sim, out State state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_sim_status_name(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_sim_clip_name(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_teleport(IntPtr sim, ref Vec3 pos);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_set_tuning(IntPtr sim, ref Tuning tuning);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_new(byte[] assetsDirUtf8, byte sound);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_free(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_status(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_model_count(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_model(IntPtr view, uint model, out ViewModelInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_batches(IntPtr view, uint model, [Out] ViewBatch[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_indices(IntPtr view, uint model, [Out] int[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_uvs(IntPtr view, uint model, [Out] float[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_texture(IntPtr view, uint id, out ViewTexture info, [Out] byte[] rgba, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_update(IntPtr view, IntPtr sim, ref ViewFrame frame);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_draws(IntPtr view, [Out] ViewDraw[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_positions(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_normals(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_colors(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_texture_ids(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_frame_uvs(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_water_vertices(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_water_info(IntPtr view, out WaterInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_water_texture(IntPtr view, uint which, out ViewTexture info, [Out] byte[] rgba, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_fx_info(IntPtr view, out FxInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_fx_vertices(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_fx_batches(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_fx_texture_version(IntPtr view, uint which);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_fx_texture(IntPtr view, uint which, out ViewTexture info, [Out] byte[] rgba, uint len);

        /// Loads sms.dll from the plugin folder and checks it speaks this plugin's ABI with the
        /// same struct layouts.
        public static bool Load(string path, out string error)
        {
            if (!NativeLib.Load(path, out error)) return false;
            uint v = sms_abi_version();
            if (v != AbiVersion)
            {
                error = $"{path} speaks ABI {v}, this plugin wants {AbiVersion}; rebuild both";
                return false;
            }
            var native = new uint[32];
            uint n = sms_struct_sizes(native, (uint)native.Length);
            var diff = NativeLib.CompareSizes(native, n, new[]
            {
                ("Vec3", NativeLib.Size<Vec3>()), ("GroundHit", NativeLib.Size<GroundHit>()),
                ("RoofHit", NativeLib.Size<RoofHit>()), ("WallHit", NativeLib.Size<WallHit>()),
                ("Event", NativeLib.Size<Event>()), ("Host", NativeLib.Size<Host>()),
                ("Pad", NativeLib.Size<Pad>()), ("State", NativeLib.Size<State>()),
                ("ModelInfo", NativeLib.Size<ViewModelInfo>()), ("Batch", NativeLib.Size<ViewBatch>()),
                ("Texture", NativeLib.Size<ViewTexture>()), ("Draw", NativeLib.Size<ViewDraw>()),
                ("ViewFrame", NativeLib.Size<ViewFrame>()), ("Tuning", NativeLib.Size<Tuning>()),
                ("WaterVertex", NativeLib.Size<WaterVertex>()), ("WaterInfo", NativeLib.Size<WaterInfo>()),
                ("Enemy", NativeLib.Size<Enemy>()),
                ("FxVertex", NativeLib.Size<FxVertex>()), ("FxBatch", NativeLib.Size<FxBatch>()),
                ("FxInfo", NativeLib.Size<FxInfo>()),
            });
            if (diff != null)
            {
                error = $"{path}'s structs differ from this plugin's ({diff}); rebuild both";
                return false;
            }
            return true;
        }
    }

    // sms_view_* as ror2-common's IViewSource.
    internal sealed class SmsViewSource : IViewSource
    {
        public readonly IntPtr Handle;

        public SmsViewSource(IntPtr view) { Handle = view; }

        public uint ModelCount() => Native.sms_view_model_count(Handle);
        public bool Model(uint model, out ViewModelInfo info) => Native.sms_view_model(Handle, model, out info) != 0;
        public uint Batches(uint model, ViewBatch[] output, uint max) => Native.sms_view_batches(Handle, model, output, max);
        public uint Indices(uint model, int[] output, uint max) => Native.sms_view_indices(Handle, model, output, max);
        public uint Uvs(uint model, float[] output, uint max) => Native.sms_view_uvs(Handle, model, output, max);
        public bool Texture(uint id, out ViewTexture info, byte[] rgba, uint len) => Native.sms_view_texture(Handle, id, out info, rgba, len) != 0;
        public uint Draws(ViewDraw[] output, uint max) => Native.sms_view_draws(Handle, output, max);
        public IntPtr Positions(out uint count) => Native.sms_view_positions(Handle, out count);
        public IntPtr Normals(out uint count) => Native.sms_view_normals(Handle, out count);
        public IntPtr Colors(out uint count) => Native.sms_view_colors(Handle, out count);
        public IntPtr TextureIds(out uint count) => Native.sms_view_texture_ids(Handle, out count);
        public IntPtr FrameUvs(out uint count) => Native.sms_view_frame_uvs(Handle, out count);
    }
}
