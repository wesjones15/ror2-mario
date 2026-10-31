using System;
using System.Runtime.InteropServices;
using Ror2Common;

namespace MarioTest
{
    // The libsms C ABI (libsms/crates/libsms-ffi, SMS_ABI_VERSION 33). Layouts mirror the
    // #[repr(C)] structs there field for field; Load checks them with sms_struct_sizes. Sim
    // units: about 1 cm, Y up, a facing angle a (i16, 0x10000 per turn) points along
    // (sin a, 0, cos a). The view's structs are ror2-common's (ViewModelInfo...), which
    // libpikmin's pik_view_* shares.
    internal static class Native
    {
        public const string Dll = "sms";
        public const uint AbiVersion = 36;
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_heat_new(byte[] dir);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_heat_free(IntPtr heat);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_heat_texture(IntPtr heat, out ViewTexture texture, [Out] byte[] rgba, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_heat_frame(IntPtr heat, float seconds, [Out] float[] values, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_entrance_new(byte[] dir, byte sound);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_entrance_fast_new(byte[] dir, byte sound);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_entrance_cache_complete(byte[] dir);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_prepare_entrance(byte[] dir, byte[] msg, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_entrance_bounds(IntPtr view, out Vec3 lo, out Vec3 hi);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_entrance_update(IntPtr view, float seconds, byte activated, ref ViewFrame frame, out Vec3 position);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_mario_voice(IntPtr view, byte[] name);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_set_voice_pitch(IntPtr view, float ratio);
        /// Whose voice the Mario cues play: 0 Mario's, 1 Piantissimo's jump voice (rest Mario's),
        /// 2 Piantissimo exactly as the game has him (jump voice only). sms_view_skin picks 0 or 1.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_voice_set(IntPtr view, uint set);
        // ABI 20 refraction maps: R=1, G=4*Y, B=coverage, A=4*X. Separate the mask and
        // compensate material strength so RoR2's near-flat-normal discard keeps the crests.
        public const float RefractionNormalGain = 4f;

        [StructLayout(LayoutKind.Sequential)]
        public struct Vec3
        {
            public float x, y, z;
            public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        }

        /// The sim's last FLUDD emit (sms_sim_last_spray): a diagnostic of which way the water was sent.
        [StructLayout(LayoutKind.Sequential)]
        public struct Spray
        {
            public Vec3 pos, dir;
            public float power;
            public uint count;
            /// NozzleKind + 1.
            public byte nozzle, spin;
            public uint emits;
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
            /// A ground pound's wind-up began.
            PoundStart = 14,
            /// Limits: FLUDD started in mid-air and spent a use; arg 1 an extra, 0 the free one.
            AirNozzle = 15,
            /// Limits: a mid-air jump was spent.
            AirJump = 16,
            /// Limits: a belly slide turned wet and spent the charge.
            WetSlide = 17,
            // As WaterHit, by a drop of a light press's burst.
            BurstHit = 18,
            /// value: fall in sim units; arg bits 1 dive, 2 ground pound, 4 cushioned.
            FallImpact = 19,
            /// He touched a Shine at `pos`; arg is the yaw it turned him to.
            ShineTouch = 20,
            /// He has landed with the Shine over his head: the Shine Get demo begins (pos: where he
            /// stands, arg: his yaw).
            ShineDemo = 21,
            /// A water drop from a spin jump's spray hit.
            SpinHit = 22,
            /// He set the held object `arg` down: pos where he stood, value his facing (a 16-bit angle).
            Put = 23,
            /// He threw the held object `arg` from `pos`; ThrowAim follows.
            Throw = 24,
            /// With the Throw before it: arg his facing (a 16-bit angle), pos his velocity.
            ThrowAim = 25,
        }

        /// Jump kinds (Event.arg of Jump).
        public enum JumpKind
        {
            Normal = 0, Second = 1, Triple = 2, Back = 3, Long = 4, UTurn = 5, Wall = 6, Spin = 7,
            Dive = 8, Forced = 9,
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
            /// 1 falling in a ground pound, 2 in a super pound (Sunshine's flames), 3 in the get-up after the
            /// landing (hits do not interrupt any of them), else 0.
            public byte pounding;
            /// 1 standing in shallow water (UnityHost's water callback).
            public byte in_water;
            /// 1 in the Shine Get pose and still falling, 2 holding the Shine up, else 0.
            public byte win_demo;
            /// The fitted nozzle: 0 without FLUDD, else 1 Spray, 2 Rocket, 5 Hover, 6 Turbo.
            public byte nozzle;
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
            /// The belly slide's spray this many times as big (0 or 1: the game's).
            public float slide_fx_scale;
            /// Running speeds up and brakes this many times as quickly (his acceleration stat).
            public float traction;
            public byte enemy_push, cycle_nozzles, unlimited_water;
            /// 1: the Spray holds steady at the trigger's depth; 0: the game's bursts.
            public byte spray_steady;
            /// 1: shallow water (RoR2's) wets the ground as FLUDD's water does.
            public byte shallow_wet;
            /// 1: a wet belly slide also throws libsms's old stand-in splashes, over the game's spray.
            public byte slide_splashes;
            /// 1: A, the stick, B, L or R gets him up once a knock-down's slide has nearly spent itself.
            public byte knockdown_cancel;
        }

        /// The host's own limits on FLUDD and the slide (libsms::Limits); zeroes are the game's.
        /// Sent before every step: the sim counts air_extra and wet_slide_ready down as it
        /// spends them.
        [StructLayout(LayoutKind.Sequential)]
        public struct Limits
        {
            public float spray_rate, spray_seconds, hover_time, turbo_speed, wet_slide_seconds;
            /// A Spray press lasts this many times as long before it fades.
            public float spray_fade;
            public uint air_extra, air_jumps;
            /// 1: mid-air nozzle starts spend uses (one free per time off the ground).
            public byte air_uses;
            /// 0 the game's, 1 requires wet ground, 2 allows dry ground (unused by this host).
            public byte wet_slide;
            public byte wet_slide_ready; // always 1 here: duration-only, no recharge gate
            /// Bit 0: wet slides glide on (as if the stick were held forward); bit 1: a slide
            /// over FLUDD's water is wet for free.
            public byte wet_slide_flags;
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
            public uint wet_quads;
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

        /// FLUDD's water gauge: where it sits on the game's screen_w x screen_h screen.
        [StructLayout(LayoutKind.Sequential)]
        public struct HudInfo
        {
            public int x, y;
            public uint width, height, screen_w, screen_h, version;
            public byte shown, pad0, pad1, pad2;
        }

        /// The losing screens' state (kind 0 none, 1 "TOO BAD!", 2 GAME OVER): black over the screen at
        /// fill, the overlay (black pixels with alpha) over the middle 4:3 and black at edge past it, and
        /// how many sprites to place on its screen_w x screen_h screen.
        [StructLayout(LayoutKind.Sequential)]
        public struct ScreenFx
        {
            public uint kind, finished, version;
            public float fill, edge;
            public uint sprites, overlay_w, overlay_h;
            public float screen_w, screen_h;
            public float card_x, card_y, card_w, card_h;
            public float card_r, card_g, card_b, card_a;
        }

        /// One call of sms_view_shine_update: where the Shine is and which of its animations.
        [StructLayout(LayoutKind.Sequential)]
        public struct ShineFrame
        {
            public float dt;
            public Vec3 pos;
            public short yaw;
            /// 0 waiting, 1 shine_float, 2 shine_demo_shine_get.
            public byte clip;
            /// 1: the host draws the Shine mirrored across X (ModelView does), so the glow follows.
            public byte mirror;
            /// Frame of that clip, 60 a second.
            public float frame, scale;
            /// The camera's right and up, for the glow's billboards.
            public Vec3 right, up;
        }

        /// The Shine's animations' and the demo camera's lengths in clip frames.
        [StructLayout(LayoutKind.Sequential)]
        public struct ShineInfo
        {
            public float float_frames, demo_frames, camera_outside_frames, camera_inside_frames;
        }

        /// The Shine Get camera at one moment (Mario's feet at the origin, facing +Z).
        [StructLayout(LayoutKind.Sequential)]
        public struct CameraShot
        {
            public Vec3 eye, at, up;
            public float fov;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ScreenSprite
        {
            public uint picture;
            public float x, y, w, h, rotation;
            public float r, g, b, a;
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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate byte WaterFn(IntPtr user, ref Vec3 pos, out float level);

        /// Something Mario can pick up (SmsTakeable): fruit is -1 for none.
        [StructLayout(LayoutKind.Sequential)]
        public struct Takeable
        {
            public uint id;
            public Vec3 pos;
            public float radius;
            public int fruit;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate uint TakeablesFn(IntPtr user, ref Vec3 pos, float radius, Takeable* output, uint max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate byte TakeFn(IntPtr user, uint id);

        [StructLayout(LayoutKind.Sequential)]
        public struct Host
        {
            public IntPtr user;
            public IntPtr ground, roof, walls, evt, enemies, water, takeables, take;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_abi_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_tick_hz();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_assets_complete(byte[] dirUtf8);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_find_disc(byte[] dirsUtf8, byte[] pathUtf8, uint pathLen);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_extract(byte[] discUtf8, byte[] outDirUtf8, byte[] msgUtf8, uint msgLen);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_extract_extras(byte[] discUtf8, byte[] outDirUtf8, byte[] msgUtf8, uint msgLen);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_render_stings(byte[] dirUtf8, byte[] msgUtf8, uint msgLen);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_struct_sizes([Out] uint[] sizes, uint max);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_sim_new(byte[] assetsDirUtf8, ref Vec3 pos, short yaw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_free(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_sim_step(IntPtr sim, ref Host host, ref Pad pad, short cameraYaw, float dt, IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_sim_state(IntPtr sim, out State state);
        /// Whether this sim's own FLUDD water has wetted the ground at `pos` (1), for another Mario to slide on.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_sim_wet_at(IntPtr sim, ref Vec3 pos);
        /// Says whether another Mario's wet ground is under this sim for its next step.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_set_foreign_wet(IntPtr sim, uint wet);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_sim_status_name(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_sim_clip_name(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_teleport(IntPtr sim, ref Vec3 pos);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_set_tuning(IntPtr sim, ref Tuning tuning);
        /// 1 for a Yoshi skin: his mid-air flutter (fresh A press in the air) and orange juice.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_set_yoshi_skin(IntPtr sim, byte on);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_refill(IntPtr sim);
        /// Fits FLUDD's nozzle at once (0 no FLUDD, 1 Spray, 2 Hover, 3 Rocket, 4 Turbo); fitting
        /// one while FLUDD is off gives it back. 0 for an unknown slot.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_sim_select_nozzle(IntPtr sim, uint slot);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_set_limits(IntPtr sim, ref Limits limits);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_add_water(IntPtr sim, float share);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_launch(IntPtr sim, ref Vec3 vel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_launch_pad(IntPtr sim, ref Vec3 vel, float gravity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_set_aim(IntPtr sim, byte on, short yaw, float pitch);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_sim_last_spray(IntPtr sim, out Spray spray);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_down(IntPtr sim);
        /// A hit from `from` knocks Mario down (damage kind 0: the common enemy's) without touching the sim's health.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_sim_knock_down(IntPtr sim, ref Vec3 from, byte kind);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_sim_ignite(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_sim_water_touches_segment(IntPtr sim, ref Vec3 a, ref Vec3 b, float radius);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_wet(IntPtr sim);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_boost_jump(IntPtr sim, float factor);
        /// Mario touches a Shine at `pos`, turned `yaw`: the win pose. 1 if he took it.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_sim_shine_get(IntPtr sim, ref Vec3 pos, short yaw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_shine_end(IntPtr sim);
        /// Presentation-only: hold a status, clip and frame for a skill portrait (see libsms-ffi).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_sim_portrait(IntPtr sim,
            uint status, uint clip, float frame, uint state, uint nozzle, byte wet);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_new(byte[] assetsDirUtf8, byte sound);
        /// look: 0 Mario, 1 Shadow Mario's colours (unused by the host now), 2 the yellow coin, 3 the Shine.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sms_view_prop_clip(IntPtr view, byte[] nameUtf8, out float frames);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_prop_update(IntPtr view, short yaw, int clip, float frame, byte mirror);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_effect(IntPtr view, byte[] nameUtf8, ref Vec3 pos, float scale);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_effects_step(IntPtr view, float dt, ref Vec3 right, ref Vec3 up);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_fw_new();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_sim_set_flowers(IntPtr sim, IntPtr fw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_fw_setup(IntPtr fw, byte[] assetsDirUtf8, ref FlowerClips clips);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_fw_set_table(IntPtr fw, uint chancePerMille, uint ring, uint single, uint enemyRing, uint tower);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_fw_roll(IntPtr fw, float r, float s);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_fw_place(IntPtr fw, uint spawn, ref Vec3 pos, float yaw);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_fw_step(IntPtr fw, ref Host host, Vec3[] marios, uint count, float dt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_fw_hit(IntPtr fw, ulong id, uint kind);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_fw_take_hits(IntPtr fw, [Out] FlowerHit[] hits, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_fw_items(IntPtr fw, [Out] FlowerItem[] items, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_fw_mirror(IntPtr fw, FlowerItem[] items, uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_fw_events(IntPtr fw, [Out] FlowerEvent[] events, uint max);

        /// Clip lengths in sim ticks (a clip's frames * 2); 0 keeps the core's guess.
        [StructLayout(LayoutKind.Sequential)]
        public struct FlowerClips
        {
            public uint flower_open, tower_appear, tower_hide, tower_attack_start, tower_attack, tower_attack_end, tower_freeze, tower_die;
        }

        /// One flower (kind 0), tower (1) or head (2): the phase / state, ticks in it, where it stands.
        [StructLayout(LayoutKind.Sequential)]
        public struct FlowerItem
        {
            public byte kind, state, pad0, pad1;
            public uint handle, age;
            public Vec3 pos;
            public float yaw;
        }

        /// 1 bloomed, 2 wilted, 3 a coin leaves a ring (pos, vel), 4 a tower rose, 5 it sank, 6 a bite
        /// (a: 0 tower, 1 head; target: which Mario of the step), 7 died (a: 1 tower, 2 head), 8 a head
        /// hopped, 9 a tower froze.
        [StructLayout(LayoutKind.Sequential)]
        public struct FlowerEvent
        {
            public uint kind;
            public int a, target;
            public Vec3 pos, vel;
        }

        /// A hit a sim reported: kind 0 water, 1 a stomp or other attack.
        [StructLayout(LayoutKind.Sequential)]
        public struct FlowerHit
        {
            public ulong id;
            public uint kind, pad;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_new_look(byte[] assetsDirUtf8, byte sound, byte look);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_free(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_skin(IntPtr view, uint skin);
        /// One column-major 4x4 per Sunshine joint, or `count` 0 to clear the override.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_set_pose(IntPtr view, float[] matrices, uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_set_luigi(IntPtr view, byte[] meleeDirUtf8);
        /// Makes a skin drawable (see MarioSkin): `dirUtf8` is the Melee cache for Luigi, null for
        /// a skin that comes from the Sunshine assets. 1 when its files were there.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_load_skin(IntPtr view, uint skin, byte[] dirUtf8);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_status(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_model_count(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_model(IntPtr view, uint model, out ViewModelInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_batches(IntPtr view, uint model, [Out] ViewBatch[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_indices(IntPtr view, uint model, [Out] int[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_uvs(IntPtr view, uint model, [Out] float[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_vertex_alphas(IntPtr view, uint model, [Out] float[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_texture(IntPtr view, uint id, out ViewTexture info, [Out] byte[] rgba, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_texture_version(IntPtr view, uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_update(IntPtr view, IntPtr sim, ref ViewFrame frame);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_draws(IntPtr view, [Out] ViewDraw[] output, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_positions(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_normals(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_colors(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_texture_ids(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_frame_uvs(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_water_vertices(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_water_info(IntPtr view, out WaterInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_coin_get(IntPtr view, ref Vec3 pos);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_water_texture(IntPtr view, uint which, out ViewTexture info, [Out] byte[] rgba, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_fx_info(IntPtr view, out FxInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_set_fx(IntPtr view, byte movement, byte flames);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_nozzle_portrait(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_fx_texture_world_space(IntPtr view, uint texture);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_set_smoke_speed(IntPtr view, float speed);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_fx_vertices(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sms_view_fx_batches(IntPtr view, out uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_fx_texture_version(IntPtr view, uint which);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_fx_texture(IntPtr view, uint which, out ViewTexture info, [Out] byte[] rgba, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_hud_info(IntPtr view, out HudInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_hud_pixels(IntPtr view, [Out] byte[] rgba, uint len);
        /// FLUDD's nozzle icons from the HUD (0 Spray, 1 Hover, 2 Rocket, 3 Turbo) for the radial menu.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_nozzle_icon_info(IntPtr view, uint index, out HudInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_nozzle_icon_pixels(IntPtr view, uint index, [Out] byte[] rgba, uint len);
        /// Mario's life meter `which` (0 the HUD's, with the splash and text; 1 the sun alone, ten rays):
        /// health and shield as shares of the meter, then its place and size and its pixels as the water gauge's.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_life_set(IntPtr view, byte which, float health, float shield);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_life_info(IntPtr view, byte which, out HudInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_life_pixels(IntPtr view, byte which, [Out] byte[] rgba, uint len);
        /// The HUD's coin counters (0 yellow, 1 blue): the number to show, then place, size and pixels as the gauge's.
        /// The losing screens ("TOO BAD!", GAME OVER): start one (0 or 1), step it in seconds, stop it, and
        /// read what it shows: state, sprites (back to front), their pictures and the overlay's pixels.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_sound(IntPtr view, byte[] nameUtf8, ref Vec3 pos);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_hold_sound(IntPtr view, byte[] nameUtf8, ref Vec3 pos);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_set_paused(IntPtr view, byte paused);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_shine_update(IntPtr view, ref ShineFrame frame);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_shine_info(IntPtr view, out ShineInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_camera_shot(IntPtr view, byte inside, float frame, out CameraShot shot);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_screenfx_start(IntPtr view, byte which);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_screenfx_step(IntPtr view, float seconds);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sms_view_screenfx_stop(IntPtr view);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_screenfx_info(IntPtr view, out ScreenFx info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint sms_view_screenfx_sprites(IntPtr view, [Out] ScreenSprite[] sprites, uint max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_screenfx_picture(IntPtr view, uint which, out HudInfo info, [Out] byte[] rgba, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_screenfx_overlay(IntPtr view, [Out] byte[] rgba, uint len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_coins_set(IntPtr view, byte which, uint value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_coins_info(IntPtr view, byte which, out HudInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern byte sms_view_coins_pixels(IntPtr view, byte which, [Out] byte[] rgba, uint len);

        /// Loads sms.dll from the plugin folder and checks it speaks this plugin's ABI with the
        /// same struct layouts.
        public static bool Load(string path, out string error)
        {
            if (!NativeLib.Load(path, out error)) return false;
            try
            {
                var zero = new Vec3();
                sms_sim_water_touches_segment(IntPtr.Zero, ref zero, ref zero, 0f);
                sms_sim_wet(IntPtr.Zero);
            }
            catch (EntryPointNotFoundException)
            {
                error = $"{path} lacks FLUDD's fire/wet contact support; update sms.dll with MarioTest.dll";
                return false;
            }
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
                ("FxInfo", NativeLib.Size<FxInfo>()), ("HudInfo", NativeLib.Size<HudInfo>()),
                ("Limits", NativeLib.Size<Limits>()),
                ("ScreenFx", NativeLib.Size<ScreenFx>()), ("ScreenSprite", NativeLib.Size<ScreenSprite>()),
                ("ShineFrame", NativeLib.Size<ShineFrame>()), ("ShineInfo", NativeLib.Size<ShineInfo>()),
                ("CameraShot", NativeLib.Size<CameraShot>()),
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
    internal sealed class SmsViewSource : IViewSource, IVertexAlphaSource, IAnimatedTextureSource
    {
        public readonly IntPtr Handle;

        public SmsViewSource(IntPtr view) { Handle = view; }

        public uint ModelCount() => Native.sms_view_model_count(Handle);
        public bool Model(uint model, out ViewModelInfo info) => Native.sms_view_model(Handle, model, out info) != 0;
        public uint Batches(uint model, ViewBatch[] output, uint max) => Native.sms_view_batches(Handle, model, output, max);
        public uint Indices(uint model, int[] output, uint max) => Native.sms_view_indices(Handle, model, output, max);
        public uint Uvs(uint model, float[] output, uint max) => Native.sms_view_uvs(Handle, model, output, max);
        public uint VertexAlphas(uint model, float[] output, uint max) => Native.sms_view_vertex_alphas(Handle, model, output, max);
        public bool Texture(uint id, out ViewTexture info, byte[] rgba, uint len) => Native.sms_view_texture(Handle, id, out info, rgba, len) != 0;
        public uint Draws(ViewDraw[] output, uint max) => Native.sms_view_draws(Handle, output, max);
        public IntPtr Positions(out uint count) => Native.sms_view_positions(Handle, out count);
        public IntPtr Normals(out uint count) => Native.sms_view_normals(Handle, out count);
        public IntPtr Colors(out uint count) => Native.sms_view_colors(Handle, out count);
        public IntPtr TextureIds(out uint count) => Native.sms_view_texture_ids(Handle, out count);
        public IntPtr FrameUvs(out uint count) => Native.sms_view_frame_uvs(Handle, out count);
        public uint TextureVersion(uint id) => Native.sms_view_texture_version(Handle, id);

        /// Whose body the view draws: 0 Mario, 1 Luigi (an alt-skin whose HSD mesh comes from the
        /// Melee cache), 2 Piantissimo (a model on Mario's rig).
        public void SetSkin(uint skin) => Native.sms_view_skin(Handle, skin);

        /// Draws the body with a host pose (one model-space matrix per joint); null clears it.
        public bool SetPose(float[] matrices, uint count) =>
            Native.sms_view_set_pose(Handle, matrices, count) != 0;

        /// Loads Luigi's HSD mesh from the Melee cache; false when the cache is incomplete.
        public bool LoadLuigi(byte[] meleeDirUtf8) => Native.sms_view_set_luigi(Handle, meleeDirUtf8) != 0;
    }
}
