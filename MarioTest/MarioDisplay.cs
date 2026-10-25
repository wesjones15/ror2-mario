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
    // backflips and spins, so the view says it over his small jump's "hup"). Views take a
    // moment to build (the bakes), so released ones wait in a pool for the next lobby.
    internal sealed class MarioDisplay : IDisplayActor
    {
        static readonly Stack<MarioDisplay> spare = new Stack<MarioDisplay>();
        static readonly Stack<MarioDisplay> spareLuigi = new Stack<MarioDisplay>();
        static readonly Native.GroundFn floorFn = Floor;
        static Native.Host floor = new Native.Host { ground = Marshal.GetFunctionPointerForDelegate(floorFn) };

        // The intro: when he jumps, and what he says.
        const float JumpAt = 0.4f, JumpHeld = 0.12f;
        static readonly byte[] Wahoo = NativeLib.CString("MSD_SE_MV23_JUMP_LARGE_01");

        // libsms-ffi's sms_view_mario_voice (additive to its ABI; missing in older builds).
        [DllImport(Native.Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void sms_view_mario_voice(IntPtr view, byte[] nameUtf8);
        static bool voiceMissing;

        readonly IntPtr view;
        IntPtr sim;
        float time;
        bool jumped, wasGrounded = true;
        readonly bool luigi;
        public ModelView Models { get; }
        public float Scale => UnityHost.Scale;

        MarioDisplay(IntPtr view, IntPtr sim, ModelView models, bool luigi)
        {
            this.view = view;
            this.sim = sim;
            Models = models;
            this.luigi = luigi;
        }

        /// A display Mario starting his intro, or null (the prefab keeps Commando) if the
        /// disc's files won't load.
        public static IDisplayActor Get() => Create(false);

        /// A display Luigi (the alt-skin): Mario's sim and FLUDD, Luigi's HSD mesh, for the
        /// character-select scene and the skin's portrait. Null without the Melee cache.
        public static IDisplayActor GetLuigi() => Create(true);

        static IDisplayActor Create(bool luigi)
        {
            if (!Plugin.AssetsReady) return null;
            if (luigi && !MarioSkin.Ready) return null;
            var pool = luigi ? spareLuigi : spare;
            if (pool.Count > 0)
            {
                var d = pool.Pop();
                var origin = new Native.Vec3(0f, 0f, 0f);
                Native.sms_sim_teleport(d.sim, ref origin);
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
            if (luigi)
            {
                // The skin draws Luigi's HSD mesh; a missing cache keeps the Mario view.
                if (!MarioSkin.Load(view))
                {
                    Native.sms_view_free(view);
                    return null;
                }
                Native.sms_view_skin(view, 1);
            }
            var sim = Native.sms_sim_new(NativeLib.CString(Plugin.AssetsDir), ref start, 0);
            var models = sim != IntPtr.Zero ? ModelView.Create(new SmsViewSource(view), luigi ? "Luigi (lobby)" : "Mario (lobby)") : null;
            if (models == null)
            {
                if (sim != IntPtr.Zero) Native.sms_sim_free(sim);
                Native.sms_view_free(view);
                return null;
            }
            var made = new MarioDisplay(view, sim, models, luigi);
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
            (luigi ? spareLuigi : spare).Push(this);
        }

        static byte Floor(IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit)
        {
            hit = default;
            hit.normal = new Native.Vec3(0f, 1f, 0f);
            return 1;
        }
    }
}
