using Ror2Common;

namespace MarioTest
{
    // Mario's entry in the pause menu's controls window (ror2-common PauseControls).
    internal static class MarioControls
    {
        public static ControlSheet Sheet()
        {
            var sheet = new ControlSheet
            {
                Subtitle = "Sunshine's plumber, with FLUDD on his back",
                Footer = "X stays RoR2's interact and Y its equipment. Moves are Sunshine's: stomps, pounds and dives hurt what they hit.",
            };

            sheet.Pad("Moving").Add("Left stick", "Run, walk, turn")
                .Add("A", "Jump (A A A in rhythm: double, triple)")
                .Add("B", "Dive; slide on the ground")
                .Add("LT", "Ground pound in the air")
                .Add("Stick circle", "Spin jump (in the air)");
            sheet.Pad("FLUDD")
                .Add("RT", "Spray: light press a splash, full a stream")
                .Add("RB", "Switch nozzle")
                .Add("LB", "Hold to lock on (RB then cycles)")
                .Add("D-pad down", "Refill the tank")
                .Add("Stick circle + RT", "Ground spin spray");
            // The ship part row is for Ship Repair only: it is on the sheet only while that mode is the one being played.
            bool repair = Plugin.ShipRepairSelected();
            var padCombos = sheet.Pad("Combinations")
                .Add("RT then A", "Rocket: charge, then launch")
                .Add("Circle in a rocket", "Spin: double height, burns")
                .Add("Turbo + A", "Dash jump")
                .Add("Circle, Turbo held in air", "Corkscrew (keep circling; 4 s burns)")
                .Add("B onto wet ground", "Wet slide");
            if (repair) padCombos.Add("B by a ship part", "Pick up, carry, put or throw into the Dolphin");

            sheet.Keys("Moving").Add("Move", "Run, walk, turn")
                .Add("Jump", "Jump (again in rhythm: double, triple)")
                .Add("Secondary", "Dive; slide on the ground")
                .Add("Utility", "Ground pound in the air")
                .Add("Mouse circle", "Spin jump: circle the move keys");
            sheet.Keys("FLUDD")
                .Add("Primary", "Spray: tap a splash, hold a stream")
                .Add("Sprint", "Full spray")
                .Add("Special", "Switch nozzle");
            var keyCombos = sheet.Keys("Combinations")
                .Add("Primary then Jump", "Rocket: charge, then launch")
                .Add("Turbo + Jump", "Dash jump")
                .Add("Circle, Turbo held in air", "Corkscrew (keep circling; 4 s burns)")
                .Add("Secondary onto wet", "Wet slide");
            if (repair) keyCombos.Add("Secondary by a ship part", "Pick up, carry, put or throw into the Dolphin");
            return sheet;
        }
    }
}
