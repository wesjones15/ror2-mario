using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Mario on the character select screen (ror2-common's DisplayModel): his own sim standing
    // with FLUDD on a flat floor at the display's root (so he breathes, looks about and
    // blinks), drawn through his own view. Each time he's picked he plays an intro, as RoR2's
    // survivors do (Commando's "Display" clip, MUL-T's "PoseForLobby"): he jumps on the spot
    // and says "wahoo" (MV23 JUMP_LARGE, which the sim only picks for running triple jumps,
    // backflips and spins, so the view says it over his small jump's "hup"). The alt-skin
    // (Luigi) plays the same intro and idle; the display follows the loadout's skin, so picking
    // Luigi in the loadout swaps his HSD mesh in here too. Views take a moment to build (the
    // bakes), so released ones wait in a pool for the next lobby.
    internal sealed class MarioDisplay : IDisplayActor
    {
        static readonly Stack<MarioDisplay> spare = new Stack<MarioDisplay>();
        /// Released displays fixed to one skin (the portraits), by skin id.
        static readonly Dictionary<uint, Stack<MarioDisplay>> spareFixed = new Dictionary<uint, Stack<MarioDisplay>>();
        static readonly Native.GroundFn floorFn = Floor;
        static Native.Host floor = new Native.Host { ground = Marshal.GetFunctionPointerForDelegate(floorFn) };

        // The intro: when he jumps, and what he says.
        const float JumpAt = 0.4f, JumpHeld = 0.12f;
        static readonly byte[] Wahoo = NativeLib.CString("MSD_SE_MV23_JUMP_LARGE_01");

        // libsms-ffi's sms_view_mario_voice (additive to its ABI; missing in older builds).
        [DllImport(Native.Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void sms_view_mario_voice(IntPtr view, byte[] nameUtf8);
        static bool voiceMissing;

        IntPtr view;
        IntPtr sim;
        float time;
        bool jumped, wasGrounded = true;
        /// The skin the view currently draws (a native skin id).
        uint skin;
        /// Follow the loadout's skin (the character-select display), or stay fixed (the portrait).
        bool follow;
        public ModelView Models { get; private set; }
        public float Scale => UnityHost.Scale;

        MarioDisplay(IntPtr view, IntPtr sim, ModelView models, uint skin, bool follow)
        {
            this.view = view;
            this.sim = sim;
            Models = models;
            this.skin = skin;
            this.follow = follow;
        }

        /// A display Mario starting his intro, or null (the prefab keeps Commando) if the
        /// disc's files won't load. Follows the loadout's skin: an alt-skin appears when selected.
        public static IDisplayActor Get() => MarioSkin.UsesStandIn(MarioSkin.Selected())
            ? new PrimitiveDisplay(() => MarioSkin.StandInColor(MarioSkin.Selected())) : Create(MarioSkin.Selected(), true);

        /// A display of one alt-skin (Mario's sim and FLUDD, that skin's body), for the skin's
        /// portrait. Null when its files are missing.
        public static IDisplayActor Get(uint skin) => MarioSkin.UsesStandIn(skin)
            ? new PrimitiveDisplay(MarioSkin.StandInColor(skin)) : Create(skin, false);

        static Stack<MarioDisplay> Pool(uint skin, bool follow)
        {
            if (follow) return spare;
            if (!spareFixed.TryGetValue(skin, out var pool)) spareFixed[skin] = pool = new Stack<MarioDisplay>();
            return pool;
        }

        static IDisplayActor Create(uint skin, bool follow)
        {
            if (!Plugin.AssetsReady) return null;
            if (skin != MarioSkin.Mario && !MarioSkin.Spec(skin).Registered) return null;
            var pool = Pool(skin, follow);
            if (pool.Count > 0)
            {
                var d = pool.Pop();
                var origin = new Native.Vec3(0f, 0f, 0f);
                Native.sms_sim_teleport(d.sim, ref origin);
                d.follow = follow;
                d.Begin();
                return d;
            }
            var start = new Native.Vec3(0f, 0f, 0f);
            var view = Native.sms_view_new(NativeLib.CString(Plugin.AssetsDir), (byte)(Plugin.Sound.Value ? 1 : 0));
            if (view == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("Mario's lobby model didn't load; showing Commando");
                return null;
            }
            if (skin != MarioSkin.Mario)
            {
                // The skin draws its own body; missing files keep the Mario view.
                if (!MarioSkin.Load(view, skin))
                {
                    Native.sms_view_free(view);
                    return null;
                }
                MarioSkin.Apply(view, skin);
            }
            var sim = Native.sms_sim_new(NativeLib.CString(Plugin.AssetsDir), ref start, 0);
            MarioSkin.ApplySim(sim, skin);
            var models = sim != IntPtr.Zero ? ModelView.Create(new SmsViewSource(view), MarioSkin.Spec(skin).Text + " (lobby)") : null;
            if (models == null)
            {
                if (sim != IntPtr.Zero) Native.sms_sim_free(sim);
                Native.sms_view_free(view);
                return null;
            }
            var made = new MarioDisplay(view, sim, models, skin, follow);
            made.Begin();
            return made;
        }

        void Begin()
        {
            time = 0f;
            jumped = false;
            wasGrounded = true;
        }

        public void Step(float dt, Transform display)
        {
            if (follow) FollowSkin();
            time += dt;
            var pad = default(Native.Pad);
            // A on the spot, held a moment so the sim's 30 Hz step sees it.
            if (time >= JumpAt && time < JumpAt + JumpHeld) pad.a = 1;
            Native.sms_sim_step(sim, ref floor, ref pad, short.MinValue, dt, view);
            Intro();
            var frame = new Native.ViewFrame
            {
                dt = dt,
                forward = new Native.Vec3(0f, 0f, 1f),
                up = new Native.Vec3(0f, 1f, 0f),
                volume = GameSettings.SfxVolume() * Mathf.Clamp(Plugin.SoundVolume.Value, 0f, 2f),
            };
            // The camera in the display's space, for the shiny parts' reflections and his voice.
            var cam = Camera.main;
            if (cam && display)
            {
                var inv = Quaternion.Inverse(display.rotation);
                var f = inv * cam.transform.forward;
                var u = inv * cam.transform.up;
                var e = UnityHost.ToSim(inv * (cam.transform.position - display.position));
                frame.eye = e;
                frame.forward = new Native.Vec3(f.x, f.y, f.z);
                frame.up = new Native.Vec3(u.x, u.y, u.z);
            }
            Native.sms_view_update(view, sim, ref frame);
        }

        // When the loadout's skin changed, rebuild the view for the new body (Mario, Luigi, ...).
        // The shared sim keeps its clock, so the intro jump and "wahoo" play the same either way.
        void FollowSkin()
        {
            uint want = MarioSkin.Selected();
            if (want == skin) return;
            var newView = Native.sms_view_new(NativeLib.CString(Plugin.AssetsDir), (byte)(Plugin.Sound.Value ? 1 : 0));
            if (newView == IntPtr.Zero) return;
            if (want != MarioSkin.Mario && !MarioSkin.Load(newView, want))
            {
                Native.sms_view_free(newView);
                return;
            }
            MarioSkin.Apply(newView, want);
            var newModels = ModelView.Create(new SmsViewSource(newView), MarioSkin.Spec(want).Text + " (lobby)");
            if (newModels == null)
            {
                Native.sms_view_free(newView);
                return;
            }
            Models.Dispose();
            Native.sms_view_free(view);
            view = newView;
            Models = newModels;
            skin = want;
        }

        // The tick he leaves the ground on the intro's jump, he says "wahoo" over its "hup".
        void Intro()
        {
            if (Native.sms_sim_state(sim, out var st) == 0) return;
            bool grounded = st.on_ground != 0;
            if (!jumped && time >= JumpAt && wasGrounded && !grounded)
            {
                jumped = true;
                if (!voiceMissing)
                {
                    try { sms_view_mario_voice(view, Wahoo); }
                    catch (EntryPointNotFoundException)
                    {
                        voiceMissing = true;
                        Plugin.Log.LogWarning("sms.dll has no sms_view_mario_voice; the lobby jump says its own voice");
                    }
                }
            }
            wasGrounded = grounded;
        }

        public void Release()
        {
            Models.HideAll();
            Pool(skin, follow).Push(this);
        }

        static byte Floor(IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit)
        {
            hit = default;
            hit.normal = new Native.Vec3(0f, 1f, 0f);
            return 1;
        }
    }
}
