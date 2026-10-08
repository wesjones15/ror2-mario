using System;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Mario's look and sound, loaded once from the disc's files the first time a Mario needs
    // them and kept across stages: libsms's view (sms_view_*: the models posed and skinned,
    // each material's TEV baked into one texture, and the game's sounds on our own output,
    // since RoR2 plays through Wwise), ror2-common's ModelView putting it in the scene,
    // FLUDD's water (MarioWater) and the game's particle effects (MarioFx).
    internal static class MarioView
    {
        static bool tried;
        static IntPtr view;
        static ModelView models;
        static MarioWater water;
        static MarioFx fx;
        static readonly ViewDraw[] firstDraw = new ViewDraw[1];

        /// The native view (for sms_sim_step's sounds and sms_view_update), or zero.
        public static IntPtr Handle => view;
        public static ModelView Models => models;
        /// The game's own particles are drawn (else MarioFlames stands in).
        public static bool HasEffects => fx != null;

        public static bool Ensure()
        {
            if (tried) return models != null;
            tried = true;
            if (!Plugin.DrawModel.Value && !Plugin.Sound.Value) return false;
            try
            {
                view = Native.sms_view_new(NativeLib.CString(Plugin.AssetsDir), (byte)(Plugin.Sound.Value ? 1 : 0));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Mario's view: {e}");
                view = IntPtr.Zero;
            }
            if (view == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"Mario's models didn't load from {Plugin.AssetsDir}; he stays Commando");
                return false;
            }
            Plugin.Log.LogInfo($"Mario's view: {NativeLib.Utf8(Native.sms_view_status(view))}");
            if (Plugin.DrawModel.Value)
            {
                models = ModelView.Create(new SmsViewSource(view), "Mario");
                // Sunshine's hit flash is pink (`addDamageFog`).
                if (models != null) models.FlashColor = new Color(1f, 0f, 0.5f, 0.6f);
                water = MarioWater.Create(view);
                fx = MarioFx.Create(view);
            }
            return models != null;
        }

        /// Poses `sim` for this frame, draws its water, and plays its sounds from the player's
        /// camera. `lag` is how many 120 Hz ticks behind the sim Mario is drawn.
        public static void Update(IntPtr sim, float lag)
        {
            if (view == IntPtr.Zero || sim == IntPtr.Zero) return;
            var frame = new Native.ViewFrame
            {
                dt = Time.deltaTime,
                volume = GameSettings.SfxVolume() * Mathf.Clamp(Plugin.SoundVolume.Value, 0f, 2f),
                lag = lag,
                forward = new Native.Vec3(0f, 0f, 1f),
                up = new Native.Vec3(0f, 1f, 0f),
            };
            var cam = GameSettings.PlayerCamera();
            if (cam)
            {
                frame.eye = UnityHost.ToSim(cam.position);
                frame.forward = new Native.Vec3(cam.forward.x, cam.forward.y, cam.forward.z);
                frame.up = new Native.Vec3(cam.up.x, cam.up.y, cam.up.z);
            }
            Native.sms_view_update(view, sim, ref frame);
            water?.Update(view, UnityHost.Scale);
        }

        /// Draws this frame's particle effects (after Update) with Mario drawn at `feet`: they
        /// move with him from where the sim has him to where he's drawn.
        public static void Effects(Vector3 feet)
        {
            if (fx == null || view == IntPtr.Zero) return;
            if (!Plugin.PoundFlames.Value) { fx.Hide(); return; }
            var offset = Vector3.zero;
            if (Native.sms_view_draws(view, firstDraw, 1) > 0)
            {
                var p = firstDraw[0].pos;
                offset = feet - new Vector3(p.x, p.y, p.z) * UnityHost.Scale;
            }
            fx.Update(view, UnityHost.Scale, offset);
        }

        /// Hides the models, the water and the effects (until the next Update).
        public static void Hide()
        {
            models?.HideAll();
            water?.Hide();
            fx?.Hide();
        }
    }
}
