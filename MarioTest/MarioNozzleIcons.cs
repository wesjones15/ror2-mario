using System;
using UnityEngine;

namespace MarioTest
{
    // FLUDD's four nozzle pictures from the disc's HUD (`nz01..04`, WaterGun.cpp's nozzle order:
    // Spray, Hover, Rocket, Turbo), as textures for the radial menu. libsms keeps the HUD's
    // pictures; this only uploads the selected view's copy once. A view without the HUD files
    // leaves null textures and the menu falls back to coloured labels.
    internal static class MarioNozzleIcons
    {
        static readonly Texture2D[] icons = new Texture2D[4];
        static IntPtr loadedFor;

        /// Icon `index` (0 Spray, 1 Hover, 2 Rocket, 3 Turbo), or null when the view has no HUD.
        public static Texture2D Get(int index, IntPtr view)
        {
            if (view == IntPtr.Zero || index < 0 || index >= icons.Length) return null;
            if (loadedFor != view)
            {
                Reset();
                loadedFor = view;
            }
            return icons[index] ?? (icons[index] = Load(index, view));
        }

        static Texture2D Load(int index, IntPtr view)
        {
            try
            {
                if (Native.sms_view_nozzle_icon_info(view, (uint)index, out var info) == 0
                    || info.width == 0 || info.height == 0)
                    return null;
                int len = (int)(info.width * info.height * 4);
                var pixels = new byte[len];
                if (Native.sms_view_nozzle_icon_pixels(view, (uint)index, pixels, (uint)len) == 0)
                    return null;
                // libsms's rows run top down, Unity's bottom up.
                var flipped = new byte[len];
                int row = (int)info.width * 4;
                for (int y = 0; y < info.height; y++)
                    Buffer.BlockCopy(pixels, y * row, flipped, ((int)info.height - 1 - y) * row, row);
                var tex = new Texture2D((int)info.width, (int)info.height, TextureFormat.RGBA32, false)
                {
                    name = "FLUDD nozzle icon " + index,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
                tex.SetPixelData(flipped, 0);
                tex.Apply(false, false);
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"FLUDD's nozzle icons didn't load: {e.Message}");
                return null;
            }
        }

        public static void Reset()
        {
            for (int i = 0; i < icons.Length; i++)
            {
                if (icons[i]) UnityEngine.Object.Destroy(icons[i]);
                icons[i] = null;
            }
            loadedFor = IntPtr.Zero;
        }
    }
}
