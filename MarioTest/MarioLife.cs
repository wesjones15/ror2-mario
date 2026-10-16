using System;
using RoR2;
using RoR2.UI;
using UnityEngine;
using Ror2Common;

namespace MarioTest
{
    // Sunshine's life meter, the Shine Sprite sun (libsms's sms_view_life_*: the disc's pieces,
    // a hub and eight rays, lit by health). One image per meter, redrawn by libsms when the lit
    // rays change and uploaded here then.
    //  - The HUD's (which 0, with the red splash and "LIFE") stands in for RoR2's health bar:
    //    health is the rays, whole slices, as a share of the body's full health and shield; the
    //    shield takes the rays after the health's, in the cyan of the air meter. RoR2's health bar
    //    hides (Hud.HideHealthBar), and its numbers are drawn at the meter's lower right instead; the
    //    meter is in the top right (Hud.SunshineLayout), as Sunshine's is.
    //  - The lock-on marker's (which 1, the sun alone with ten rays) is MarioLockMarker's.
    internal sealed class LifeImage
    {
        readonly byte which;
        Texture2D tex;
        uint version;
        byte[] pixels = new byte[0], flipped = new byte[0];
        Native.HudInfo info;
        bool warned;

        public LifeImage(byte which) { this.which = which; }

        public Native.HudInfo Info => info;
        public Texture2D Texture => tex;
        public float Aspect => info.height > 0 ? info.width / (float)info.height : 1f;

        public void Reset()
        {
            if (tex) UnityEngine.Object.Destroy(tex);
            tex = null;
            version = 0;
            info = default;
        }

        /// The meter drawn for `health` and `shield` (shares of it); false without the HUD's files.
        public bool Update(IntPtr view, float health, float shield)
        {
            if (view == IntPtr.Zero) return false;
            if (Native.sms_view_life_set(view, which, health, shield) == 0
                || Native.sms_view_life_info(view, which, out info) == 0 || info.width == 0 || info.height == 0)
            {
                if (!warned) Plugin.Log.LogWarning("Mario's life meter isn't in the assets folder (re-extract them)");
                warned = true;
                return false;
            }
            if (!tex || tex.width != info.width || tex.height != info.height)
            {
                if (tex) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D((int)info.width, (int)info.height, TextureFormat.RGBA32, false)
                {
                    name = which == 0 ? "Mario life meter" : "Mario lock-on marker",
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
                if (Native.sms_view_life_pixels(view, which, pixels, (uint)len) == 0) return false;
                // libsms's rows run top down, Unity's bottom up.
                int row = (int)info.width * 4;
                for (int y = 0; y < info.height; y++)
                    Buffer.BlockCopy(pixels, y * row, flipped, ((int)info.height - 1 - y) * row, row);
                tex.SetPixelData(flipped, 0);
                tex.Apply(false, false);
                version = info.version;
            }
            return true;
        }
    }

    // The HUD's meter: in place of RoR2's health bar for the local player's Mario.
    internal static class MarioLife
    {
        static readonly LifeImage image = new LifeImage(0);
        static bool shown;
        static HealthComponent shownHealth;
        static GUIStyle numbers;
        // The health bar's own bars, hidden by a CanvasGroup of ours (alpha 0), and whether we
        // added that group, so it can go back.
        static readonly System.Collections.Generic.List<CanvasGroup> hidden = new System.Collections.Generic.List<CanvasGroup>();
        static readonly System.Collections.Generic.List<bool> added = new System.Collections.Generic.List<bool>();

        public static void Reset()
        {
            image.Reset();
            shown = false;
            ShowRor2Bars();
        }

        /// A body's health and shield as shares of its full total.
        public static void Shares(HealthComponent health, out float life, out float shield)
        {
            life = shield = 0f;
            if (!health) return;
            float full = health.fullCombinedHealth;
            if (full <= 0f) return;
            life = Mathf.Clamp01(health.health / full);
            shield = Mathf.Clamp01((health.shield + health.barrier) / full);
        }

        /// After the view's update (render rate): the meter for the player's `health`.
        public static void Update(IntPtr view, HealthComponent health)
        {
            shown = false;
            if (view == IntPtr.Zero || !Plugin.ShowLife.Value || !health)
            {
                ShowRor2Bars();
                return;
            }
            shownHealth = health;
            Shares(health, out var life, out var shield);
            shown = image.Update(view, life, shield);
            if (shown && Plugin.HideHealthBar.Value) HideRor2Bars();
            else ShowRor2Bars();
        }

        // RoR2's health bar goes whole, its numbers too (the meter takes its place, and its numbers are
        // drawn beside the meter).
        static void HideRor2Bars()
        {
            foreach (var hud in HUD.readOnlyInstanceList)
            {
                var container = hud && hud.healthBar ? hud.healthBar.gameObject : null;
                if (!container) continue;
                var group = container.GetComponent<CanvasGroup>();
                bool made = false;
                if (!group)
                {
                    group = container.AddComponent<CanvasGroup>();
                    made = true;
                }
                if (!hidden.Contains(group))
                {
                    hidden.Add(group);
                    added.Add(made);
                }
                group.alpha = 0f;
            }
        }

        static void ShowRor2Bars()
        {
            for (int i = 0; i < hidden.Count; i++)
            {
                if (!hidden[i]) continue;
                if (added[i]) UnityEngine.Object.Destroy(hidden[i]);
                else hidden[i].alpha = 1f;
            }
            hidden.Clear();
            added.Clear();
        }

        // Where RoR2's health bar starts, left edge and middle, on the screen (GUI pixels, y down).
        static bool BarLeft(out Vector2 at)
        {
            at = default;
            foreach (var hud in HUD.readOnlyInstanceList)
            {
                var container = hud && hud.healthBar ? hud.healthBar.barContainer : null;
                if (!container) continue;
                var canvas = container.GetComponentInParent<Canvas>();
                var camera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                var corners = new Vector3[4];
                container.GetWorldCorners(corners);
                var left = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[1]) * 0.5f);
                at = new Vector2(left.x, Screen.height - left.y);
                return true;
            }
            return false;
        }

        /// From OnGUI: draws the meter unless the game is paused or its HUD is off.
        public static void Draw()
        {
            if (!shown || !image.Texture || Event.current.type != EventType.Repaint) return;
            if (PauseManager.isPaused || !HUD.cvHudEnable.value) return;
            var info = image.Info;
            float scale = Screen.height / (float)info.screen_h * Mathf.Clamp(Plugin.LifeScale.Value, 0.1f, 5f);
            float w = info.width * scale, h = info.height * scale;
            // Where Hud.LifeX and LifeY say, or (negative) the top right, as Sunshine has it, or with
            // Hud.SunshineLayout off, RoR2's health bar, over its left end.
            float cx = Plugin.LifeX.Value * Screen.width;
            float cy = Plugin.LifeY.Value * Screen.height;
            if (Plugin.LifeX.Value < 0f || Plugin.LifeY.Value < 0f)
            {
                if (Plugin.SunshineLayout.Value)
                {
                    float edge = 0.02f * Screen.height;
                    cx = Screen.width - w * 0.5f - edge;
                    cy = h * 0.5f + edge;
                }
                else if (BarLeft(out var bar)) { cx = bar.x + w * 0.4f; cy = bar.y; }
                else { cx = 0.4f * Screen.width; cy = 0.9f * Screen.height; }
            }
            cx = Mathf.Clamp(cx, w * 0.5f, Screen.width - w * 0.5f);
            cy = Mathf.Clamp(cy, h * 0.5f, Screen.height - h * 0.5f);
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h), image.Texture, ScaleMode.StretchToFill, true);
            if (Plugin.HideHealthBar.Value) Numbers(cx + w * 0.5f, cy + h * 0.5f);
        }

        // RoR2's health numbers (current/full, shield included, as its bar shows them), at the meter's
        // lower right, where its bar's would be missed.
        static void Numbers(float right, float bottom)
        {
            var health = shownHealth;
            if (!health) return;
            if (numbers == null)
            {
                numbers = new GUIStyle(GUI.skin.label) { wordWrap = false };
                numbers.normal.textColor = Color.white;
            }
            numbers.fontSize = Mathf.Max(10, Mathf.RoundToInt(Screen.height * 0.032f));
            string text = $"{Mathf.CeilToInt(health.combinedHealth)}/{Mathf.CeilToInt(health.fullCombinedHealth)}";
            // Right-aligned by hand, its lower edge a little over the meter's.
            var size = numbers.CalcSize(new GUIContent(text));
            var rect = new Rect(Mathf.Min(right, Screen.width) - size.x, Mathf.Min(bottom, Screen.height) - size.y * 0.85f, size.x, size.y);
            var before = GUI.color;
            // A dark edge, so it reads over anything.
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            foreach (var d in new[] { new Vector2(-1.5f, 0f), new Vector2(1.5f, 0f), new Vector2(0f, -1.5f), new Vector2(0f, 1.5f) })
                GUI.Label(new Rect(rect.x + d.x, rect.y + d.y, rect.width, rect.height), text, numbers);
            GUI.color = Color.white;
            GUI.Label(rect, text, numbers);
            GUI.color = before;
        }
    }

    // The lock-on marker, Olimar's cursor in Sunshine's colours: the life meter's sun alone,
    // small and turning, floating on the locked enemy (ror2-common's LockMarker places it as
    // Olimar's is, facing Mario's eye). Its rays are the enemy's health when
    // Look.LockMarkerHealth is on, whole slices of ten.
    internal sealed class MarioLockMarker
    {
        readonly LifeImage image = new LifeImage(1);
        MarkerQuad quad;
        float turn;

        /// For this frame: the marker on `target` seen from `eye`, or hidden when none.
        public void Update(IntPtr view, CharacterBody target, Vector3 eye)
        {
            if (!Plugin.LockMarker.Value || view == IntPtr.Zero || !target)
            {
                Hide();
                return;
            }
            float life = 1f, shield = 0f;
            if (Plugin.LockMarkerHealth.Value && target.healthComponent)
                MarioLife.Shares(target.healthComponent, out life, out shield);
            if (!image.Update(view, life, shield) || !image.Texture)
            {
                Hide();
                return;
            }
            if (quad == null) quad = new MarkerQuad("Mario lock-on marker");
            // Where Olimar's would hang, in metres (Mario's 160 units are 1.6 m).
            var at = LockMarker.Place(eye, target, 0.25f, 0.6f);
            turn = (turn + Time.deltaTime * Plugin.LockMarkerSpin.Value) % 360f;
            quad.Show(image.Texture, image.Aspect, at, eye, Mathf.Max(0.05f, Plugin.LockMarkerSize.Value), turn);
        }

        public void Hide()
        {
            if (quad != null) quad.Hide();
        }

        public void Dispose()
        {
            if (quad != null) quad.Dispose();
            quad = null;
            image.Reset();
        }
    }
}
