using System;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // FLUDD's water gauge, Sunshine's HUD tank (libsms's sms_view_hud_*: the disc's layout and
    // pictures, drawn as the game draws them into one small image). Uploaded when its picture
    // changes and drawn over the screen where Hud.TankX/TankY put it, sized as the game sizes
    // it against the screen's height (its screen is 480 lines) times Hud.TankScale.
    internal static class MarioHud
    {
        static Texture2D tex;
        static uint version;
        static byte[] pixels = new byte[0];
        static byte[] flipped = new byte[0];
        static Native.HudInfo info;
        static bool shown, warned;

        /// After sms_view_update: takes this frame's gauge.
        public static void Update(IntPtr view)
        {
            shown = false;
            if (view == IntPtr.Zero || !Plugin.ShowTank.Value) return;
            if (Native.sms_view_hud_info(view, out info) == 0)
            {
                if (!warned) Plugin.Log.LogWarning("FLUDD's water gauge isn't in the assets folder (re-extract them)");
                warned = true;
                return;
            }
            if (info.shown == 0 || info.width == 0 || info.height == 0) return;
            if (!tex || tex.width != info.width || tex.height != info.height)
            {
                if (tex) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D((int)info.width, (int)info.height, TextureFormat.RGBA32, false)
                {
                    name = "FLUDD water gauge",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                version = info.version + 1;
            }
            if (version != info.version)
            {
                int len = (int)(info.width * info.height * 4);
                if (pixels.Length != len)
                {
                    pixels = new byte[len];
                    flipped = new byte[len];
                }
                if (Native.sms_view_hud_pixels(view, pixels, (uint)len) == 0) return;
                // libsms's rows run top down, Unity's bottom up.
                int row = (int)info.width * 4;
                for (int y = 0; y < info.height; y++)
                    Buffer.BlockCopy(pixels, y * row, flipped, ((int)info.height - 1 - y) * row, row);
                tex.SetPixelData(flipped, 0);
                tex.Apply(false, false);
                version = info.version;
            }
            shown = true;
        }

        /// From OnGUI: draws the gauge unless the game is paused or its HUD is off.
        public static void Draw()
        {
            if (!shown || !tex || Event.current.type != EventType.Repaint) return;
            if (PauseManager.isPaused || !RoR2.UI.HUD.cvHudEnable.value) return;
            float scale = Screen.height / (float)info.screen_h * Mathf.Clamp(Plugin.TankScale.Value, 0.1f, 5f);
            float w = info.width * scale, h = info.height * scale;
            float cx = Mathf.Clamp01(Plugin.TankX.Value) * Screen.width;
            float cy = Mathf.Clamp01(Plugin.TankY.Value) * Screen.height;
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h), tex, ScaleMode.StretchToFill, true);
        }
    }
}
