using Rewired;
using RoR2;
using RoR2.UI;
using Ror2Common;

namespace MarioTest
{
    // The pause window chooses the section for the player's last input device.
    internal static class MarioControls
    {
        static string Pad(Rewired.Player player, PadButton button, string fallback) =>
            GlyphOverride.PhysicalGlyph(player, button) ?? fallback;

        static string Key(MPEventSystem eventSystem, string action, string fallback)
        {
            if (!eventSystem) return fallback;
            var glyph = Glyphs.GetGlyphString(eventSystem, action, AxisRange.Full,
                MPEventSystem.InputSource.MouseAndKeyboard, true);
            return string.IsNullOrEmpty(glyph) ? fallback : glyph;
        }

        public static ControlSheet Sheet(CharacterBody body)
        {
            var user = LocalUserManager.GetFirstLocalUser();
            var player = user?.inputPlayer;
            var eventSystem = user?.eventSystem;
            string A = Pad(player, PadButton.A, "A"), B = Pad(player, PadButton.B, "B");
            string LB = Pad(player, PadButton.LB, "LB"), RB = Pad(player, PadButton.RB, "RB");
            string LT = Pad(player, PadButton.LT, "LT"), RT = Pad(player, PadButton.RT, "RT");
            string LS = Pad(player, PadButton.LS, "LS");
            string jump = Key(eventSystem, "Jump", "Jump");
            string secondary = Key(eventSystem, "SecondarySkill", "Secondary");
            string utility = Key(eventSystem, "UtilitySkill", "Utility");
            string primary = Key(eventSystem, "PrimarySkill", "Primary");
            string special = Key(eventSystem, "SpecialSkill", "Special");
            string sprint = Key(eventSystem, "Sprint", "Sprint");
            string move = Key(eventSystem, "MoveVertical", "W / S") + " / "
                + Key(eventSystem, "MoveHorizontal", "A / D");

            var skin = MarioSkin.Current(body);
            var sheet = new ControlSheet
            {
                // An alt-skin swaps the name and quip (Mario's skin leaves the title unset).
                Title = skin.Title,
                Subtitle = skin.Quip,
                // The face follows the selected skin (its rendered portrait; null keeps Mario's own).
                Portrait = skin.Portrait,
            };
            sheet.Pad("")
                .Add(A, "Jump")
                .Add(B, "Dive (try sliding on\n<pos=170>wet terrain!)")
                .Add(LS, "Move (click: Sprint)\n<pos=170>(spin: Spin)")
                .Add(LT + " in air", "Ground Pound");
            sheet.Pad("F.L.U.D.D.")
                .Add(RT, "Spray Water / Use Nozzle")
                .Add(RB, "Tap: Spray <-> Alternate")
                .Add("hold " + RB + " 0.5s", "Choose Alternate (No FLUDD,\n<pos=170>Hover, Rocket, Turbo)")
                .Add("hold " + LB, "Lock on to Target")
                .Add(LB + " + " + RT, "Spray Target")
                .Add(LB + " + tap " + RB, "Cycle target");

            // MarioPad reads RoR2 bindings for keyboard and mouse. Utility also
            // locks on while held, and Special cycles targets during that hold.
            sheet.Keys("")
                .Add(jump, "Jump")
                .Add(secondary, "Dive (try sliding on\n<pos=170>wet terrain!)")
                .Add(move, "Move (circle: Spin)")
                .Add(sprint, "Sprint / full spray")
                .Add(utility + " in air", "Ground Pound");
            sheet.Keys("F.L.U.D.D.")
                .Add(primary, "Spray Water / Use Nozzle")
                .Add(special, "Tap: Spray <-> Alternate")
                .Add("hold " + special + " 0.5s", "Choose Alternate (No FLUDD,\n<pos=170>Hover, Rocket, Turbo)")
                .Add("hold " + utility, "Lock on to Target")
                .Add(utility + " + " + primary, "Spray Target")
                .Add(utility + " + tap " + special, "Cycle target");
            ControlSheetLayout.SpaceRows(sheet);
            return sheet;
        }
    }
}
