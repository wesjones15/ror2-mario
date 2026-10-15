using System;
using System.Collections.Generic;
using RoR2;
using RoR2.UI;
using UnityEngine;
using Ror2Common;

namespace MarioTest
{
    // Sunshine's coin counters (libsms's sms_view_coins_*: the disc's coin icon, "x" and digit
    // pictures laid out as the game's HUD does), standing in for RoR2's gold and lunar coin
    // counters:
    //  - the yellow coin shows RoR2's gold (CharacterMaster.money; each yellow coin Mario picks
    //    up is worth Coins.Gold of it, MarioCoins);
    //  - the blue coin shows the player's lunar coins, only once there is one (RoR2 does the
    //    same), and a lunar coin picked up plays Sunshine's blue coin jingle.
    // RoR2's own counters hide while ours show (Hud.CoinCounters), and each of ours sits where
    // the one it replaces is unless Hud.CoinX / CoinY say otherwise.
    internal sealed class CoinImage
    {
        readonly byte which;
        Texture2D tex;
        uint version;
        byte[] pixels = new byte[0], flipped = new byte[0];
        Native.HudInfo info;
        bool warned;

        public CoinImage(byte which) { this.which = which; }

        public Native.HudInfo Info => info;
        public Texture2D Texture => tex;

        public void Reset()
        {
            if (tex) UnityEngine.Object.Destroy(tex);
            tex = null;
            version = 0;
            info = default;
        }

        /// The counter drawn for `value`; false without the HUD's files.
        public bool Update(IntPtr view, uint value)
        {
            if (view == IntPtr.Zero) return false;
            if (Native.sms_view_coins_set(view, which, value) == 0
                || Native.sms_view_coins_info(view, which, out info) == 0 || info.width == 0 || info.height == 0)
            {
                if (!warned) Plugin.Log.LogWarning("Sunshine's coin counters aren't in the assets folder (re-extract them)");
                warned = true;
                return false;
            }
            if (!tex || tex.width != info.width || tex.height != info.height)
            {
                if (tex) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D((int)info.width, (int)info.height, TextureFormat.RGBA32, false)
                {
                    name = which == 0 ? "Mario yellow coin counter" : "Mario blue coin counter",
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
                if (Native.sms_view_coins_pixels(view, which, pixels, (uint)len) == 0) return false;
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

    internal static class MarioCoinHud
    {
        static readonly CoinImage yellow = new CoinImage(0), blue = new CoinImage(1);
        static bool yellowShown, blueShown;
        // Where RoR2's own counters are (GUI pixels, y down), when found.
        static Vector2 yellowAt, blueAt;
        static bool yellowFound, blueFound;
        // RoR2's counters, hidden by a CanvasGroup of ours (alpha 0), and whether we added the
        // group, so it can go back.
        static readonly List<CanvasGroup> hidden = new List<CanvasGroup>();
        static readonly List<bool> added = new List<bool>();
        // The lunar coins last seen, for the jingle (none until the first reading).
        static long lunarSeen = -1;

        public static void Reset()
        {
            yellow.Reset();
            blue.Reset();
            yellowShown = blueShown = false;
            lunarSeen = -1;
            ShowRor2();
        }

        static uint LunarCoins(LocalUser user)
        {
            var network = user?.currentNetworkUser;
            return network ? network.lunarCoins : 0u;
        }

        /// After the view's update (render rate): the counters for the player's gold and lunar
        /// coins. Null `view` hands RoR2's own counters back.
        public static void Update(IntPtr view, CharacterBody body, LocalUser user)
        {
            yellowShown = blueShown = false;
            if (view == IntPtr.Zero || !Plugin.ShowCoins.Value || !body)
            {
                ShowRor2();
                lunarSeen = -1;
                return;
            }
            uint gold = body.master ? body.master.money : 0u;
            uint lunar = LunarCoins(user);
            // A lunar coin picked up (not spent, and not the first reading): the jingle.
            if (lunarSeen >= 0 && lunar > lunarSeen) SpawnMusic.PlayCue("bluecoin");
            lunarSeen = lunar;

            yellowShown = yellow.Update(view, gold);
            blueShown = lunar > 0 && blue.Update(view, lunar);
            Where();
            if (yellowShown || blueShown) HideRor2(yellowShown, blueShown);
            else ShowRor2();
        }

        // Where each of RoR2's counters is, left edge and middle, on the screen.
        static void Where()
        {
            yellowFound = blueFound = false;
            foreach (var hud in HUD.readOnlyInstanceList)
            {
                if (!hud) continue;
                if (hud.moneyText)
                    yellowFound = Left(hud.moneyText.transform as RectTransform, out yellowAt);
                if (hud.lunarCoinContainer)
                    blueFound = Left(hud.lunarCoinContainer.transform as RectTransform, out blueAt);
                return;
            }
        }

        static bool Left(RectTransform rect, out Vector2 at)
        {
            at = default;
            if (!rect) return false;
            var canvas = rect.GetComponentInParent<Canvas>();
            var camera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var left = RectTransformUtility.WorldToScreenPoint(camera, (corners[0] + corners[1]) * 0.5f);
            at = new Vector2(left.x, Screen.height - left.y);
            return true;
        }

        static void HideRor2(bool gold, bool lunar)
        {
            foreach (var hud in HUD.readOnlyInstanceList)
            {
                if (!hud) continue;
                if (gold && hud.moneyText) Hide(hud.moneyText.gameObject);
                // The lunar counter is hidden whenever ours shows, or RoR2's would sit alone;
                // its container is the icon and the number.
                if (hud.lunarCoinContainer) Hide(hud.lunarCoinContainer);
            }
        }

        static void Hide(GameObject go)
        {
            var group = go.GetComponent<CanvasGroup>();
            bool made = false;
            if (!group)
            {
                group = go.AddComponent<CanvasGroup>();
                made = true;
            }
            if (!hidden.Contains(group))
            {
                hidden.Add(group);
                added.Add(made);
            }
            group.alpha = 0f;
        }

        static void ShowRor2()
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

        /// From OnGUI: draws the counters unless the game is paused or its HUD is off.
        public static void Draw()
        {
            if (Event.current.type != EventType.Repaint || PauseManager.isPaused || !HUD.cvHudEnable.value) return;
            float scale = Screen.height / 480f * Mathf.Clamp(Plugin.CoinScale.Value, 0.1f, 5f);
            bool set = Plugin.CoinX.Value >= 0f && Plugin.CoinY.Value >= 0f;
            var home = new Vector2(Plugin.CoinX.Value * Screen.width, Plugin.CoinY.Value * Screen.height);
            float below = 0f;
            if (yellowShown && yellow.Texture)
            {
                var at = set ? home : yellowFound ? yellowAt : new Vector2(0.03f * Screen.width, 0.55f * Screen.height);
                below = Put(yellow, at, scale);
            }
            if (blueShown && blue.Texture)
            {
                // Where RoR2's lunar counter was, else under the yellow one.
                var at = set ? home + Vector2.up * below
                    : blueFound ? blueAt : new Vector2(0.03f * Screen.width, 0.55f * Screen.height + below);
                Put(blue, at, scale);
            }
        }

        // Draws a counter with its left edge at `at.x` and its icon's row centred on `at.y`;
        // returns the height it takes, for the one under it.
        static float Put(CoinImage image, Vector2 at, float scale)
        {
            var info = image.Info;
            float w = info.width * scale, h = info.height * scale;
            // The icon sits in the image's lower left, the number climbing away from it.
            float iconMid = h * 0.72f;
            var rect = new Rect(Mathf.Clamp(at.x, 0f, Mathf.Max(0f, Screen.width - w)),
                Mathf.Clamp(at.y - iconMid, 0f, Mathf.Max(0f, Screen.height - h)), w, h);
            GUI.DrawTexture(rect, image.Texture, ScaleMode.StretchToFill, true);
            return h;
        }
    }
}
