using System.Collections.Generic;
using RoR2;
using RoR2.UI;
using UnityEngine;

namespace MarioTest
{
    // Sunshine's HUD, with RoR2's own pieces moved to make room (Hud.SunshineLayout), while Mario is
    // the local player's body:
    //  - the life meter (the Shine Sprite sun) is in the top right, and RoR2's health bar is gone, its
    //    numbers drawn beneath the meter (MarioLife);
    //  - items keep RoR2's top-middle position; difficulty, timer and stage move beneath health;
    //  - the skill and equipment icons (the cooldowns) move from the bottom right to the bottom middle;
    //  - FLUDD's tank gauge takes the bottom right (MarioHud).
    // RoR2's coin counters (top left) keep their place, and Sunshine's coin counters follow them. Nothing
    // RoR2 needs is hidden: only moved, and put back as it was when he isn't the player's body.
    //
    // Where RoR2's elements are is measured on the screen, not assumed, so it holds at any resolution or
    // HUD scale: each cluster is nudged until what it holds sits where it should, checked a few times a
    // second.
    internal static class MarioHudLayout
    {
        sealed class Moved
        {
            public RectTransform rect;
            public Vector2 home;
        }

        static readonly List<Moved> moved = new List<Moved>();
        static float nextCheck;
        static bool logged;

        // Pixels from the screen's edges that Sunshine's HUD keeps clear.
        const float Margin = 0.02f;

        /// Every frame Mario is the local player's body: `on` arranges RoR2's HUD for `user`, false puts it back.
        public static void Update(bool on, LocalUser user)
        {
            if (!on || !Plugin.SunshineLayout.Value || user == null)
            {
                Restore();
                return;
            }
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.2f;
            // Earlier stages' HUDs are gone, and their entries with them.
            moved.RemoveAll(m => !m.rect);
            foreach (var hud in HUD.readOnlyInstanceList)
                if (hud && hud.localUserViewer == user)
                {
                    Arrange(hud);
                    return;
                }
        }

        /// RoR2's own arrangement again.
        public static void Restore()
        {
            foreach (var m in moved)
                if (m.rect) m.rect.anchoredPosition = m.home;
            moved.Clear();
            nextCheck = 0f;
        }

        static Moved Track(RectTransform rect)
        {
            foreach (var m in moved)
                if (m.rect == rect) return m;
            var made = new Moved { rect = rect, home = rect.anchoredPosition };
            moved.Add(made);
            return made;
        }

        // The child of the HUD's spring canvas that holds `part`: the cluster to move.
        static RectTransform ClusterOf(Component part)
        {
            for (var t = part ? part.transform : null; t && t.parent; t = t.parent)
                if (t.parent.name == "SpringCanvas") return t as RectTransform;
            return null;
        }

        static float Scale(HUD hud)
        {
            var root = hud.mainContainerCanvas ? hud.mainContainerCanvas.rootCanvas : null;
            return root && root.scaleFactor > 0f ? root.scaleFactor : 1f;
        }

        // The union of these rects on the screen (pixels, y up), or false if none is usable.
        static bool Measure(HUD hud, IEnumerable<Component> parts, out Rect screen)
        {
            screen = default;
            bool any = false;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            var canvas = hud.mainContainerCanvas ? hud.mainContainerCanvas.rootCanvas : null;
            var camera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var corners = new Vector3[4];
            foreach (var part in parts)
            {
                var rect = part ? part.transform as RectTransform : null;
                if (!rect) continue;
                rect.GetWorldCorners(corners);
                foreach (var c in corners)
                {
                    var p = RectTransformUtility.WorldToScreenPoint(camera, c);
                    x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                    y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
                    any = true;
                }
            }
            if (any) screen = Rect.MinMaxRect(x0, y0, x1, y1);
            return any;
        }

        // Nudges `cluster` by `dx`, `dy` screen pixels (y up) once they matter.
        static void Shift(HUD hud, RectTransform cluster, float dx, float dy)
        {
            if (Mathf.Abs(dx) < 1.5f && Mathf.Abs(dy) < 1.5f) return;
            Track(cluster);
            cluster.anchoredPosition += new Vector2(dx, dy) / Scale(hud);
        }

        static void Arrange(HUD hud)
        {
            float w = Screen.width, h = Screen.height, margin = Margin * h;

            // The cooldowns: middle of the screen, where they already are vertically.
            var parts = new List<Component>();
            if (hud.skillIcons != null) parts.AddRange(hud.skillIcons);
            if (hud.equipmentIcons != null) parts.AddRange(hud.equipmentIcons);
            var skills = ClusterOf(parts.Count > 0 ? parts[0] : null);
            if (skills && Measure(hud, parts, out var bar))
            {
                Shift(hud, skills, w * 0.5f - bar.center.x, 0f);
                // The pick-up notices that sit above the cooldowns' old place: out of the way of the new one.
                var notices = hud.mainContainer ? hud.mainContainer.transform.Find("NotificationArea") as RectTransform : null;
                if (notices)
                {
                    var saved = Track(notices);
                    notices.anchoredPosition = new Vector2(saved.home.x, saved.home.y + 80f);
                }
                if (!logged)
                    Plugin.Log.LogInfo($"Sunshine HUD layout: cooldowns measured at {bar}");
            }

            // The game-mode panel owns the difficulty bar, timer, stage and objectives. Move its
            // visible header as one unit; do not move TopCenterCluster (items and the boss bar).
            var difficulty = hud.GetComponentInChildren<DifficultyBarController>(true);
            var panel = difficulty ? difficulty.transform.parent as RectTransform : null;
            if (panel && MarioLife.TryBounds(out var life))
            {
                var header = new List<Component>();
                // Measuring the direct children avoids the scrolling bar's offscreen segments,
                // and avoids the panel's own stretched rect, which includes empty space.
                foreach (Transform child in panel)
                    if (child.gameObject.activeInHierarchy && child.name != "RightInfoBar"
                        && child.name != "DifficultyTutorial") header.Add(child);
                if (Measure(hud, header, out var bounds))
                    Shift(hud, panel, w - margin - bounds.xMax,
                        h - life.yMax - margin * 0.5f - bounds.yMax);
            }

            if (!logged && skills)
            {
                logged = true;
                Plugin.Log.LogInfo($"Sunshine HUD layout: cooldowns {(skills ? skills.name : "not found")} to the middle, " +
                    "items at top middle, difficulty beneath life");
            }
        }
    }
}
