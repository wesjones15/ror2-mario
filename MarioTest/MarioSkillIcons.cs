using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Mario's skill portraits, rendered from his own disc files on first run (the same
    // first-run contract as the Melee portraits). Each icon builds a throwaway sim and view,
    // holds the pose the move reads by, and lets ror2-common's SkillCard frame and composite it.
    internal static class MarioSkillIcons
    {
        static readonly Native.GroundFn groundFn = Ground;
        static Native.Host floor = new Native.Host
        {
            ground = Marshal.GetFunctionPointerForDelegate(groundFn),
        };

        static readonly Color Orange = new Color(0.95f, 0.48f, 0.16f);
        static readonly Color Blue = new Color(0.22f, 0.52f, 0.92f);
        static readonly Color Red = new Color(0.92f, 0.28f, 0.16f);
        static readonly Color Cyan = new Color(0.18f, 0.68f, 0.88f);

        public static void RequestAll()
        {
            SkillCard.Request(MarioSkills.Fludd, "MarioFludd", () => Plugin.AssetsReady, Fludd);
            SkillCard.Request(MarioSkills.Air, "MarioAirFludd", () => Plugin.AssetsReady, Air);
            SkillCard.Request(MarioSkills.Pound, "MarioGroundPound", () => Plugin.AssetsReady, Pound);
            SkillCard.Request(MarioSkills.Slide, "MarioWetSlide", () => Plugin.AssetsReady, Slide);
        }

        static byte Ground(IntPtr user, ref Native.Vec3 pos, out Native.GroundHit hit)
        {
            hit = default;
            hit.height = 0f;
            hit.normal = new Native.Vec3(0f, 1f, 0f);
            return 1;
        }

        // FLUDD's head spraying forward, close enough that the water and nozzle fill the card.
        static SkillCardStage Fludd()
        {
            var card = Card("Mario FLUDD", Orange);
            var mario = new Pose(card, "Mario FLUDD portrait", Vector3.zero, withWater: true);
            var pad = default(Native.Pad);
            pad.r = 1;
            pad.r_analog = 255;
            mario.Step(pad, 36);
            mario.Frame(1f / 30f);
            card.Center = mario.At(0f, 1.32f, 0.42f);
            card.Radius = 0.62f;
            card.Yaw = 1.45f;
            card.Pitch = 0.10f;
            card.Zoom = 1.0f;
            card.Distance = 1.65f;
            card.BeforeRender = (cam, layer) => mario.SetLayer(layer);
            card.Disposing = mario.Dispose;
            return card;
        }

        // The three nozzle heads together: Hover, Turbo and Rocket, on Mario's back.
        static SkillCardStage Air()
        {
            var card = Card("Mario Air Pump", Blue);
            var poses = new List<Pose>();
            var nozzles = new[] { 3u, 4u, 2u }; // Hover, Turbo, Rocket
            for (int i = 0; i < nozzles.Length; i++)
            {
                float x = (i - 1) * 1.22f;
                var at = new Vector3(x, 0f, 0f);
                var mario = new Pose(card, "Mario nozzle " + nozzles[i], at);
                mario.SetPortrait(0, 0, 30f, 0, nozzles[i], false);
                mario.Frame(1f / 30f);
                poses.Add(mario);
            }
            card.Center = new Vector3(0f, 1.10f, 0f);
            card.Radius = 2.35f;
            card.Yaw = 2.55f;
            card.Pitch = 0.10f;
            card.Distance = 2.80f;
            card.Disposing = () =>
            {
                foreach (var p in poses) p.Dispose();
                poses.Clear();
            };
            return card;
        }

        // The flaming super pound: the fire at his middle is the subject.
        static SkillCardStage Pound()
        {
            var card = Card("Mario Ground Pound", Red);
            var mario = new Pose(card, "Mario pound portrait", Vector3.zero);
            mario.SetEffectOpacity(0.14f);
            // State-driven flames are emitted by the view each tick; a few frames keep them
            // around him instead of washing the whole card out.
            for (int i = 0; i < 3; i++)
            {
                mario.Step(default, 1);
                mario.SetPortrait(1, 1, 22f, 3, 1, false);
                mario.Frame(1f / 30f);
            }
            card.Center = mario.At(0f, 0.62f, 0f);
            card.Radius = 1.35f;
            card.Yaw = 0.95f;
            card.Pitch = 0.20f;
            card.Distance = 1.55f;
            card.BeforeRender = (cam, layer) => mario.SetLayer(layer);
            card.Disposing = mario.Dispose;
            return card;
        }

        // The belly slide: Mario angled stage right, with the wet spray off his hands and feet.
        static SkillCardStage Slide()
        {
            var card = Card("Mario Wet Slide", Cyan);
            var mario = new Pose(card, "Mario slide portrait", Vector3.zero);
            mario.SetPortrait(2, 2, 50f, 1, 1, true);
            var limits = new Native.Limits
            {
                wet_slide = 2,
                wet_slide_ready = 1,
                wet_slide_flags = 3,
            };
            Native.sms_sim_set_limits(mario.Sim, ref limits);
            var pad = default(Native.Pad);
            pad.b = 1;
            pad.stick_y = 1f;
            mario.Step(pad, 6);
            // Hold the slide's authored pose at frame 50, as catching() does.
            mario.SetPortrait(2, 2, 50f, 1, 1, true);
            mario.Frame(1f / 30f);
            card.Center = mario.At(0f, 0.55f, 0f);
            card.Radius = 1.35f;
            card.Yaw = 0.72f;
            card.Pitch = 0.22f;
            card.Distance = 1.95f;
            card.BeforeRender = (cam, layer) => mario.SetLayer(layer);
            card.Disposing = mario.Dispose;
            return card;
        }

        static SkillCardStage Card(string name, Color tint)
        {
            return new SkillCardStage(name) { Tint = tint };
        }

        sealed class Pose : IDisposable
        {
            readonly IntPtr view;
            readonly IntPtr sim;
            readonly ModelView models;
            readonly MarioFx fx;
            readonly MarioWater water;
            readonly float scale = UnityHost.Scale;

            public IntPtr Sim => sim;

            public Pose(SkillCardStage card, string name, Vector3 at, bool withWater = false)
            {
                var dir = NativeLib.CString(Plugin.AssetsDir);
                view = Native.sms_view_new(dir, 0);
                var pos = new Native.Vec3(at.x / scale, at.y / scale, at.z / scale);
                sim = view != IntPtr.Zero ? Native.sms_sim_new(dir, ref pos, 0) : IntPtr.Zero;
                if (sim == IntPtr.Zero)
                {
                    if (view != IntPtr.Zero) Native.sms_view_free(view);
                    throw new InvalidOperationException("Mario's portrait sim didn't load");
                }
                Native.sms_sim_refill(sim);
                models = ModelView.Create(new SmsViewSource(view), name, card.Root);
                fx = MarioFx.Create(view);
                water = withWater ? MarioWater.Create(view) : null;
                if (models == null)
                {
                    fx?.Dispose();
                    water?.Dispose();
                    Native.sms_view_free(view);
                    Native.sms_sim_free(sim);
                    throw new InvalidOperationException("Mario's portrait model didn't load");
                }
                Native.sms_view_set_fx(view, 1, 1);
            }

            public void SetPortrait(uint status, uint clip, float frame, uint state, uint nozzle, bool wet)
            {
                Native.sms_sim_portrait(sim, status, clip, frame, state, nozzle, (byte)(wet ? 1 : 0));
            }

            public void Step(Native.Pad pad, int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    Native.sms_sim_step(sim, ref floor, ref pad, 0, 1f / 120f, view);
                    Frame(1f / 120f);
                }
            }

            public void Frame(float dt)
            {
                var frame = new Native.ViewFrame
                {
                    dt = dt,
                    forward = new Native.Vec3(0f, 0f, 1f),
                    up = new Native.Vec3(0f, 1f, 0f),
                    volume = 0f,
                };
                Native.sms_view_update(view, sim, ref frame);
                models.Update(scale, d => (
                    new Vector3(d.pos.x, d.pos.y, d.pos.z) * scale,
                    new Quaternion(d.rot_x, d.rot_y, d.rot_z, d.rot_w)));
                if (fx != null) fx.Update(view, scale, Vector3.zero);
                water?.Update(view, scale);
            }

            public Vector3 At(float right, float up, float forward)
            {
                if (Native.sms_sim_state(sim, out var st) == 0) return Vector3.zero;
                return new Vector3(st.pos.x, st.pos.y, st.pos.z) * scale
                    + new Vector3(right, up, forward);
            }

            public void SetLayer(int layer)
            {
                fx?.SetLayer(layer);
                water?.SetLayer(layer);
            }

            public void SetEffectOpacity(float opacity) => fx?.SetOpacity(opacity);

            public void Dispose()
            {
                models?.Dispose();
                fx?.Dispose();
                water?.Dispose();
                if (view != IntPtr.Zero) Native.sms_view_free(view);
                if (sim != IntPtr.Zero) Native.sms_sim_free(sim);
            }
        }
    }
}
