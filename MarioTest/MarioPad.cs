using Rewired;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Mario's buttons. On a gamepad they're read by position through Rewired's gamepad
    // template, Sunshine's layout on an Xbox-style pad, whatever the player's RoR2 binds:
    // A jump, B dive, right trigger spray (analog: a half press sprays on the move, all the
    // way in plants Mario), left trigger crouch / ground pound (L), right bumper switch nozzle
    // (Sunshine's X). X stays RoR2's interact and Y its equipment, and Mario uses neither, so
    // pressing them never does a Mario move too. On keyboard and mouse the RoR2 binds stand in:
    // jump A, primary half spray, sprint plant, secondary B, utility L, special nozzle.
    //
    // Read every frame; presses are latched until the sim has stepped with them, so a tap
    // between two physics steps still counts. Movement is RoR2's own (the input bank's move
    // vector, from the stick or keys and the camera).
    internal sealed class MarioPad
    {
        // Buttons down now, and down at some point since the sim last stepped.
        bool a, b, l, x, aTap, bTap, lTap, xTap;
        // 0..1.
        float r, rMax;

        public void Read(LocalUser user, InputBankTest input)
        {
            var player = user?.inputPlayer;
            if (player == null || user.isUIFocused)
            {
                a = b = l = x = false;
                r = 0f;
                return;
            }
            var gp = Gamepad(player);
            if (gp != null)
            {
                a = gp.a.value;
                b = gp.b.value;
                l = gp.leftTrigger.value > 0.5f;
                x = gp.rightBumper.value;
                r = Mathf.Clamp01(gp.rightTrigger.value);
            }
            else if (input)
            {
                a = input.jump.down;
                b = input.skill2.down;
                l = input.skill3.down;
                x = input.skill4.down;
                r = input.sprint.down ? 1f : input.skill1.down ? 0.6f : 0f;
            }
            aTap |= a;
            bTap |= b;
            lTap |= l;
            xTap |= x;
            rMax = Mathf.Max(rMax, r);
        }

        /// The pad for the next sim step, with `move` (world XZ); clears the latches.
        public Native.Pad Take(Vector3 move)
        {
            float trigger = Mathf.Max(r, rMax);
            var pad = new Native.Pad
            {
                use_move = 1,
                move_x = move.x,
                move_z = move.z,
                a = B(a || aTap),
                b = B(b || bTap),
                l = B(l || lTap),
                l_analog = (byte)(l || lTap ? 255 : 0),
                x = B(x || xTap),
                // All the way in is the trigger's click (Sunshine's R button).
                r = B(trigger >= 0.98f),
                r_analog = (byte)Mathf.RoundToInt(trigger * 255f),
            };
            aTap = bTap = lTap = xTap = false;
            rMax = 0f;
            return pad;
        }

        static byte B(bool v) => v ? (byte)1 : (byte)0;

        static IGamepadTemplate Gamepad(Player player)
        {
            var last = player.controllers.GetLastActiveController();
            if (last == null || last.type != ControllerType.Joystick) return null;
            if (!last.ImplementsTemplate<IGamepadTemplate>()) return null;
            return last.GetTemplate<IGamepadTemplate>();
        }
    }
}
