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
    // Spray pressure (Fludd.SprayBursts): a light press is a short splash just ahead. On a
    // gamepad the trigger's travel is curved (Controls.TriggerCurve) so that's easier to hold;
    // on keyboard a primary press starts light and turns full if held past
    // Controls.LightTapSeconds, and a tap is kept light long enough to splash.
    //
    // RoR2's sprint button (its own bind) is read as is: it sprints for RoR2's sprint items and
    // adds no speed of its own (MarioDriver).
    //
    // A Mario no player controls (Goobo Jr.'s clone) is pressed by MarioBrain; with no brain he
    // takes the AI's presses from the input bank: jump A, primary half spray, secondary B,
    // utility L, sprint.
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
        // D-pad down (refill the tank): held, and pressed since the last step.
        bool down, downTap;
        // RoR2's sprint: held, and pressed since the last step.
        bool sprint, sprintTap;
        // Keyboard primary: down last frame, when it went down, and a tap's light splash
        // lasting until this time.
        bool primary;
        float primaryAt, lightUntil = -1f;
        // A light press: raw R 15 of Sunshine's 0-255 (its light zone is 10 to about 27).
        const float Light = 15f / 255f;
        // Water takes the handles' 10 ticks and a light burst about 28 more: a tap holds the
        // light press this long.
        const float LightBurst = 0.35f;

        /// A brain presses this Mario's buttons (Press), so the input bank's are ignored.
        public bool Brained;

        /// A brain's buttons for the next step.
        public void Press(in MarioBrain.Presses p)
        {
            a = p.a;
            b = p.b;
            l = p.l;
            x = false;
            r = p.r;
            sprint = p.sprint;
            Latch();
        }

        public void Read(LocalUser user, InputBankTest input)
        {
            var player = user?.inputPlayer;
            if (user == null && Brained) return;
            if (user == null && input)
            {
                // The AI's buttons.
                a = input.jump.down;
                b = input.skill2.down;
                l = input.skill3.down;
                x = false;
                r = input.skill1.down ? 0.6f : 0f;
                sprint = input.sprint.down;
                Latch();
                return;
            }
            if (player == null || user.isUIFocused)
            {
                a = b = l = x = down = sprint = false;
                r = 0f;
                return;
            }
            sprint = input && input.sprint.down;
            var gp = Gamepad(player);
            if (gp != null)
            {
                a = gp.a.value;
                b = gp.b.value;
                l = gp.leftTrigger.value > 0.5f;
                x = gp.rightBumper.value;
                float t = Mathf.Clamp01(gp.rightTrigger.value);
                r = t >= 0.98f ? 1f : Mathf.Pow(t, Mathf.Clamp(Plugin.TriggerCurve.Value, 0.2f, 5f));
                bool d = gp.dPad.down.value;
                downTap |= d && !down;
                down = d;
            }
            else if (input)
            {
                a = input.jump.down;
                b = input.skill2.down;
                l = input.skill3.down;
                x = input.skill4.down;
                r = input.sprint.down ? 1f : Primary(input.skill1.down);
            }
            Latch();
        }

        void Latch()
        {
            aTap |= a;
            bTap |= b;
            lTap |= l;
            xTap |= x;
            sprintTap |= sprint;
            rMax = Mathf.Max(rMax, r);
        }

        // The keyboard primary as a trigger: light at first, full once held past the tap time.
        float Primary(bool down)
        {
            float tap = Plugin.LightTapSeconds.Value;
            float now = Time.unscaledTime;
            if (down && !primary) primaryAt = now;
            if (!down && primary && tap > 0f && now - primaryAt < tap) lightUntil = primaryAt + LightBurst;
            primary = down;
            if (down) return tap > 0f && now - primaryAt < tap ? Light : 0.6f;
            return now < lightUntil ? Light : 0f;
        }

        /// RoR2's sprint was pressed since the last call.
        public bool TakeSprint()
        {
            bool t = sprint || sprintTap;
            sprintTap = false;
            return t;
        }

        /// The pad for the next sim step, with `move` (world XZ); clears the latches. `held`
        /// (stunned, frozen): nothing pressed.
        public Native.Pad Take(Vector3 move, bool held = false)
        {
            if (held)
            {
                aTap = bTap = lTap = xTap = false;
                rMax = 0f;
                return new Native.Pad { use_move = 1 };
            }
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

        /// D-pad down was pressed since the last call.
        public bool TakeRefill()
        {
            bool t = downTap;
            downTap = false;
            return t;
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
