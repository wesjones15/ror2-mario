using System;
using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // The screens Sunshine shows for losing (libsms's sms_view_screenfx_*, from the disc's HUD and
    // wipe pictures): "TOO BAD!", the coloured letters that drop in and bounce as the screen opens
    // from black, for a fall off the map; and GAME OVER, the black screen with the letters cut out of
    // it, for Mario's death. libsms works out each frame (the game's 30 a second, stepped by real
    // time here) and this draws it over RoR2's own screen with IMGUI, on a view of its own so it
    // plays on after Mario's own view is gone (his death, a new stage).
    internal sealed class MarioScreenFx : MonoBehaviour
    {
        enum Kind : byte { TooBad = 0, GameOver = 1 }

        static MarioScreenFx instance;
        IntPtr view;
        bool tried;
        Native.ScreenFx info;
        Native.ScreenSprite[] sprites = new Native.ScreenSprite[16];
        Texture2D[] pictures = new Texture2D[0];
        Texture2D overlay, white;
        uint overlayVersion;
        byte[] pixels = new byte[0], flipped = new byte[0];

        void Awake()
        {
            instance = this;
            Run.onRunDestroyGlobal += RunEnded;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            Run.onRunDestroyGlobal -= RunEnded;
            Clear();
            if (view != IntPtr.Zero) Native.sms_view_free(view);
            view = IntPtr.Zero;
        }

        void RunEnded(Run run) => Clear();

        /// "TOO BAD!" for the local player.
        internal static void TooBad() => instance?.Start(Kind.TooBad);

        /// GAME OVER for the local player.
        internal static void GameOver() => instance?.Start(Kind.GameOver);

        /// Whichever is playing stops (leaving a run, quitting).
        internal static void Stop() => instance?.Clear();

        void Start(Kind kind)
        {
            if (!Plugin.ShowScreenEffects.Value || !Plugin.AssetsReady) return;
            if (view == IntPtr.Zero && !tried)
            {
                tried = true;
                // A coin view is the light one: the screens need only the pictures.
                view = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, 2);
            }
            if (view == IntPtr.Zero) return;
            if (Native.sms_view_screenfx_start(view, (byte)kind) == 0)
            {
                Plugin.Log.LogInfo($"{kind} screen: its pictures aren't in the assets folder " +
                    "(the disc's wipe pictures are fetched when your disc is found; set Paths.Disc)");
                return;
            }
            Plugin.Log.LogInfo($"{kind} screen");
        }

        void Clear()
        {
            if (view != IntPtr.Zero) Native.sms_view_screenfx_stop(view);
            info = default;
            foreach (var t in pictures) if (t) Destroy(t);
            pictures = new Texture2D[0];
            if (overlay) Destroy(overlay);
            overlay = null;
            overlayVersion = 0;
        }

        void Update()
        {
            if (view == IntPtr.Zero || PauseManager.isPaused) return;
            // Real time, so the game's own slowdowns (a death's) don't hold it.
            Native.sms_view_screenfx_step(view, Time.unscaledDeltaTime);
            if (Native.sms_view_screenfx_info(view, out info) == 0 || info.kind == 0) return;
            if (info.finished != 0 && info.fill <= 0f && info.edge <= 0f && info.sprites == 0) Clear();
        }

        Texture2D Picture(uint which)
        {
            if (which >= pictures.Length) Array.Resize(ref pictures, (int)which + 1);
            if (pictures[which]) return pictures[which];
            if (Native.sms_view_screenfx_picture(view, which, out var p, null, 0) == 0) return null;
            int len = (int)(p.width * p.height * 4);
            if (pixels.Length < len) pixels = new byte[len];
            if (Native.sms_view_screenfx_picture(view, which, out p, pixels, (uint)pixels.Length) == 0) return null;
            var tex = new Texture2D((int)p.width, (int)p.height, TextureFormat.RGBA32, false)
            {
                name = $"Mario screen picture {which}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixelData(Flip(pixels, (int)p.width, (int)p.height), 0);
            tex.Apply(false, false);
            return pictures[which] = tex;
        }

        // libsms's rows run top down, Unity's bottom up.
        byte[] Flip(byte[] src, int w, int h)
        {
            if (flipped.Length < w * h * 4) flipped = new byte[w * h * 4];
            int row = w * 4;
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(src, y * row, flipped, (h - 1 - y) * row, row);
            if (flipped.Length == w * h * 4) return flipped;
            var exact = new byte[w * h * 4];
            Buffer.BlockCopy(flipped, 0, exact, 0, exact.Length);
            return exact;
        }

        void UpdateOverlay()
        {
            if (info.overlay_w == 0 || info.overlay_h == 0) return;
            if (overlay && overlayVersion == info.version) return;
            int w = (int)info.overlay_w, h = (int)info.overlay_h;
            if (!overlay || overlay.width != w || overlay.height != h)
            {
                if (overlay) Destroy(overlay);
                overlay = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = "Mario game over overlay",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            int len = w * h * 4;
            if (pixels.Length < len) pixels = new byte[len];
            if (Native.sms_view_screenfx_overlay(view, pixels, (uint)pixels.Length) == 0) return;
            overlay.SetPixelData(Flip(pixels, w, h), 0);
            overlay.Apply(false, false);
            overlayVersion = info.version;
        }

        void Cover(Rect rect, float alpha)
        {
            if (alpha <= 0f) return;
            if (!white)
            {
                white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                white.SetPixel(0, 0, Color.white);
                white.Apply(false, false);
            }
            var before = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, Mathf.Clamp01(alpha));
            GUI.DrawTexture(rect, white);
            GUI.color = before;
        }

        void OnGUI()
        {
            if (view == IntPtr.Zero || info.kind == 0 || Event.current.type != EventType.Repaint) return;
            // Over everything of RoR2's own, its HUD and menus too.
            GUI.depth = -1000;
            float h = Screen.height, scale = h / info.screen_h, w = info.screen_w * scale;
            float x0 = (Screen.width - w) * 0.5f;
            Cover(new Rect(0f, 0f, Screen.width, h), info.fill);
            if (info.overlay_w != 0)
            {
                UpdateOverlay();
                // Past the 4:3 the game's screen is, the black runs on.
                Cover(new Rect(0f, 0f, x0, h), info.edge);
                Cover(new Rect(x0 + w, 0f, Screen.width - x0 - w, h), info.edge);
                if (overlay) GUI.DrawTexture(new Rect(x0, 0f, w, h), overlay, ScaleMode.StretchToFill, true);
            }
            int n = (int)info.sprites;
            if (n == 0) return;
            if (sprites.Length < n) sprites = new Native.ScreenSprite[n];
            Native.sms_view_screenfx_sprites(view, sprites, (uint)sprites.Length);
            var matrix = GUI.matrix;
            var color = GUI.color;
            for (int i = 0; i < n; i++)
            {
                var s = sprites[i];
                var tex = Picture(s.picture);
                if (!tex || s.a <= 0f) continue;
                var middle = new Vector2(x0 + s.x * scale, s.y * scale);
                GUI.matrix = matrix;
                GUIUtility.RotateAroundPivot(s.rotation * Mathf.Rad2Deg, middle);
                GUI.color = new Color(s.r, s.g, s.b, s.a);
                GUI.DrawTexture(new Rect(middle.x - s.w * scale * 0.5f, middle.y - s.h * scale * 0.5f,
                    s.w * scale, s.h * scale), tex, ScaleMode.StretchToFill, true);
            }
            GUI.matrix = matrix;
            GUI.color = color;
        }
    }
}
