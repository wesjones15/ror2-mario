using System;
using System.Runtime.InteropServices;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Approved native viewer scenes; all models/materials/effects still come from the
    // player's disc. Shared SkillCard renders once and caches under assets/skill-icons.
    internal static class MarioSkillIcons
    {
        static readonly Native.GroundFn groundFn = Ground;
        static Native.Host floor = new Native.Host { ground = Marshal.GetFunctionPointerForDelegate(groundFn) };

        public static void RequestAll()
        {
            SkillCard.Request(MarioSkills.Fludd, "MarioFludd", () => Plugin.AssetsReady, Fludd, () => Plugin.AssetsDir);
            SkillCard.Request(MarioSkills.Air, "MarioAirFludd", () => Plugin.AssetsReady, Air, () => Plugin.AssetsDir);
            SkillCard.Request(MarioSkills.Pound, "MarioGroundPound", () => Plugin.AssetsReady, Pound, () => Plugin.AssetsDir);
            SkillCard.Request(MarioSkills.Slide, "MarioWetSlide", () => Plugin.AssetsReady, Slide, () => Plugin.AssetsDir);
        }

        static byte Ground(IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit)
        {
            hit = new Native.GroundHit { normal = new Native.Vec3(0f, 1f, 0f), flags = 1u << 6 };
            return 1;
        }

        static SkillCardStage Fludd()
        {
            var card = Card("Mario FLUDD", new Color(0.06f, 0.26f, 0.56f), 0.95f, 0.08f, 1.05f);
            var mario = new Pose(card, Vector3.zero, true);
            mario.SetEffectOpacity(0.7f);
            mario.Step(new Native.Pad { r = 1, r_analog = 255 }, 90);
            card.Center = mario.At(0.18f, 1.5f, 0f);
            Finish(card, mario);
            return card;
        }

        static SkillCardStage Air()
        {
            var card = Card("Mario Air Pump", new Color(0.78f, 0.32f, 0.07f), 0f, 0.12f, 2.8f);
            var mario = new Pose(card, Vector3.zero) { NozzleGroup = true };
            // WaterGun.cpp nozzleBmdData: hover_wg / back_wg / rocket_wg. The
            // native still life emits only these three draws, in the approved triangle.
            mario.Frame(0f);
            card.Center = Vector3.zero;
            Finish(card, mario);
            return card;
        }

        static SkillCardStage Pound()
        {
            var card = Card("Mario Ground Pound", new Color32(116, 70, 158, 255), 0.65f, 0.12f, 1.7f);
            var mario = new Pose(card, new Vector3(0f, 200f, 0f));
            // MarioParticle.cpp emitBlurHipDropSuper: ms_m_sphipd_a..d (0x11A..D).
            // Actually fall, so this is the airborne flame, never repeated landing bursts.
            mario.SetPortrait(1, 1, 22f, 3, 1, false);
            mario.Step(default, 24);
            card.Center = mario.At(0f, 0.3f, 0f);
            Finish(card, mario);
            return card;
        }

        static SkillCardStage Slide()
        {
            var card = Card("Mario Wet Slide", new Color(0.06f, 0.30f, 0.13f), -0.785f, 0.42f, 3f);
            var mario = new Pose(card, Vector3.zero);
            mario.SetEffectOpacity(0.7f);
            // Use the real sim's slide momentum and the existing gameplay tuning/effects.
            // MarioParticle.cpp frontSlipEffect: 0x1EA ribbon + 0x112 / 0x113 spray.
            var tuning = new Native.Tuning { jump_height = 1f, move_speed = 1f, spray_reach = 1f,
                stomp_bounce = 1f, rocket_height = 1f, stomp_jump = 1f, traction = 1f,
                slide_fx_scale = Mathf.Max(0.1f, Plugin.SlideSprayScale.Value),
                slide_splashes = (byte)(Plugin.SlideSplashes.Value ? 1 : 0) };
            Native.sms_sim_set_tuning(mario.Sim, ref tuning);
            var limits = new Native.Limits { wet_slide = 1, wet_slide_ready = 1,
                wet_slide_flags = (byte)(Plugin.WetSlideGlide.Value ? 1 : 0), wet_slide_seconds = 2f };
            Native.sms_sim_set_limits(mario.Sim, ref limits);
            var velocity = new Native.Vec3(0f, 0f, 45f);
            Native.sms_sim_launch(mario.Sim, ref velocity);
            mario.SetPortrait(2, 2, 50f, 1, 1, true);
            mario.Step(default, 96);
            card.Center = mario.At(0f, 0.5f, 0f);
            Finish(card, mario);
            return card;
        }

        static SkillCardStage Card(string name, Color color, float nativeYaw, float pitch, float distance)
        {
            // ModelView mirrors native X for Unity's handedness; camera orbits follow it.
            return new SkillCardStage(name) { Tint = color, Yaw = -nativeYaw, Pitch = pitch,
                CameraDistance = distance, FieldOfView = 0.9f * Mathf.Rad2Deg, Radius = 1f };
        }

        static void Finish(SkillCardStage card, Pose pose)
        {
            card.BeforeRender = (camera, layer) => { pose.Frame(0f, camera); pose.SetLayer(layer); };
            card.Disposing = pose.Dispose;
        }

        sealed class Pose : IDisposable
        {
            readonly IntPtr view;
            readonly ModelView models;
            readonly MarioFx fx;
            readonly MarioWater water;
            readonly float scale = UnityHost.Scale;
            public IntPtr Sim { get; }
            public bool NozzleGroup;

            public Pose(SkillCardStage card, Vector3 at, bool withWater = false)
            {
                var dir = NativeLib.CString(Plugin.AssetsDir);
                view = Native.sms_view_new(dir, 0);
                var pos = new Native.Vec3(at.x / scale, at.y / scale, at.z / scale);
                Sim = view != IntPtr.Zero ? Native.sms_sim_new(dir, ref pos, 0) : IntPtr.Zero;
                if (Sim == IntPtr.Zero)
                {
                    if (view != IntPtr.Zero) Native.sms_view_free(view);
                    throw new InvalidOperationException("Mario's portrait sim didn't load");
                }
                Native.sms_sim_refill(Sim);
                models = ModelView.Create(new SmsViewSource(view), "Mario skill portrait", card.Root);
                fx = MarioFx.Create(view);
                water = withWater ? MarioWater.Create(view) : null;
                if (models == null) { Dispose(); throw new InvalidOperationException("Mario's portrait model didn't load"); }
                Native.sms_view_set_fx(view, 1, 1);
            }

            public void SetPortrait(uint status, uint clip, float frame, uint state, uint nozzle, bool wet)
                => Native.sms_sim_portrait(Sim, status, clip, frame, state, nozzle, (byte)(wet ? 1 : 0));

            public void Step(Native.Pad pad, int ticks)
            {
                for (int i = 0; i < ticks; i++)
                { Native.sms_sim_step(Sim, ref floor, ref pad, 0, 1f / 120f, view); Frame(1f / 120f); }
            }

            public void Frame(float dt, Camera camera = null)
            {
                var forward = camera ? camera.transform.forward : Vector3.forward;
                var up = camera ? camera.transform.up : Vector3.up;
                var frame = new Native.ViewFrame { dt = dt, volume = 0f,
                    forward = new Native.Vec3(forward.x, forward.y, forward.z),
                    up = new Native.Vec3(up.x, up.y, up.z) };
                Native.sms_view_update(view, Sim, ref frame);
                if (NozzleGroup) Native.sms_view_nozzle_portrait(view);
                models.Update(scale);
                fx?.Update(view, scale, Vector3.zero);
                water?.Update(view, scale);
            }

            public Vector3 At(float x, float y, float z)
            {
                if (Native.sms_sim_state(Sim, out var state) == 0) throw new InvalidOperationException("Portrait pose unavailable");
                return new Vector3(state.pos.x, state.pos.y, state.pos.z) * scale + new Vector3(x, y, z);
            }
            public void SetLayer(int layer) { fx?.SetLayer(layer); water?.SetLayer(layer); }
            public void SetEffectOpacity(float opacity) => fx?.SetOpacity(opacity);
            public void Dispose()
            {
                models?.Dispose(); fx?.Dispose(); water?.Dispose();
                if (view != IntPtr.Zero) Native.sms_view_free(view);
                if (Sim != IntPtr.Zero) Native.sms_sim_free(Sim);
            }
        }
    }
}
