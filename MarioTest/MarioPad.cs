using Rewired;
using RoR2;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Mario's buttons. On a gamepad they're read by position through Rewired's gamepad
    // template, Sunshine's layout on an Xbox-style pad, whatever the player's RoR2 binds:
    // A jump, B dive, right trigger spray on the move at every pressure, left trigger
    // crouch / ground pound (L), left bumper the lock-on (RB then cycles the target), and
    // right bumper taps between Spray and the selected alternate or, held 0.5 s, opens the
    // alternate radial (Sunshine's X is no longer sent to the sim). X stays RoR2's interact
    // and Y its equipment, and Mario uses neither, so pressing them never does a Mario move
    // too. On keyboard and mouse the RoR2 binds stand in: jump A, primary half spray, sprint
    // full spray, secondary B, utility L, special nozzle radial.
    //
    // Spray pressure (Fludd.SprayBursts): a light press is a short splash just ahead. On a
    // gamepad the trigger's travel is curved (Controls.TriggerCurve) so that's easier to hold;
    // on keyboard a primary press starts light and turns full if held past
    // Controls.LightTapSeconds, and a tap is kept light long enough to splash.
    //
    // RoR2's sprint button (its own bind) is read as is: it sprints for RoR2's sprint items and
    // adds no speed of its own (MarioDriver).
    //
    // A Mario no player controls (Goobo Jr.'s clone) is pressed by MarioBrain (which also tops
    // his tank back up with the D-pad refill); with no brain he takes the AI's presses from the
    // input bank: jump A, primary full spray, secondary B, utility L, sprint.
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
        // The trigger's press edge (the empty FLUDD button's second Melee tap), and whether it
        // was down at the previous read.
        bool rTap, rDown;
        // D-pad down (refill the tank), and up (the secret Egg Lay): held, and pressed since the
        // last step.
        bool down, downTap, up, upTap;
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

        /// The local player is in VR with motion controls (MarioVr.Controls): the controllers are
        /// read as they are (ror2-common's VrPad) in Sunshine's layout, the analog trigger kept.
        public bool MotionControls;
        readonly VrPad vr = new VrPad();

        /// The lock-on is held: the left bumper on a gamepad, the brain's for an AI, else (a
        /// keyboard has no spare key) the same button as L (LockSharesL).
        public bool LockHeld { get; private set; }
        /// On a keyboard L and the lock are one key.
        public bool LockSharesL { get; private set; }
        // RB pressed while locked, and the right stick's screen direction then.
        bool cycle, rb;
        Vector2 cycleDirection;

        /// The nozzle radial menu is held (RB, or the special key on a keyboard): it opens until
        /// the button comes up. `MenuFromGamepad` says the direction is the right stick; a
        /// keyboard host then points at a segment with the mouse.
        public bool MenuHeld { get; private set; }
        public bool MenuFromGamepad { get; private set; }
        public Vector2 MenuDirection { get; private set; }

        /// A brain's buttons for the next step.
        public void Press(in MarioBrain.Presses p)
        {
            a = p.a;
            b = p.b;
            l = p.l;
            x = false;
            r = p.r;
            sprint = p.sprint;
            LockHeld = p.lockHeld;
            LockSharesL = false;
            MenuHeld = false;
            MenuFromGamepad = false;
            MenuDirection = Vector2.zero;
            // The D-pad refill (an AI would otherwise run its tank dry for good); an AI never does the secret.
            down = p.down;
            downTap |= down;
            up = false;
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
                MenuHeld = false;
                MenuFromGamepad = false;
                MenuDirection = Vector2.zero;
                r = input.skill1.down ? 1f : 0f;
                sprint = input.sprint.down;
                Latch();
                return;
            }
            if (player == null || user.isUIFocused)
            {
                a = b = l = x = down = up = sprint = LockHeld = false;
                MenuHeld = MenuFromGamepad = false;
                MenuDirection = Vector2.zero;
                r = 0f;
                return;
            }
            sprint = input && input.sprint.down;
            var gp = MotionControls ? null : Gamepad(player);
            if (MotionControls)
            {
                // Right-handed (the VR mod's left-handed mode swaps the hands): dominant A jump,
                // B dive, trigger spray, grip RB; off-hand trigger L, grip the lock-on (LB). The
                // VR mod's interact (A) and jump (B) still go to RoR2 too, which Mario's moves
                // don't read. Its right stick turns, so the stick is only read at a cycle (and by
                // the wheel, which takes the pointer in VR: MarioRadial).
                vr.Read(false);
                var d = vr.Dominant;
                var o = vr.Off;
                a = d.Primary;
                b = d.Secondary;
                l = o.Trigger > 0.5f;
                LockHeld = o.Grip > 0.5f;
                LockSharesL = false;
                bool bumper = d.Grip > 0.5f;
                if (LockHeld && bumper && !rb)
                {
                    cycle = true;
                    cycleDirection = d.Stick;
                }
                MenuHeld = bumper && !LockHeld;
                MenuFromGamepad = true;
                MenuDirection = d.Stick;
                x = false;
                rb = bumper;
                float t = d.Trigger;
                r = t >= 0.98f ? 1f : Mathf.Pow(t, Mathf.Clamp(Plugin.TriggerCurve.Value, 0.2f, 5f));
                down = up = false;
            }
            else if (gp != null)
            {
                a = gp.a.value;
                b = gp.b.value;
                l = gp.leftTrigger.value > 0.5f;
                // LB locks on; RB then cycles the target instead of opening the nozzle menu.
                LockHeld = gp.leftBumper.value;
                LockSharesL = false;
                bool bumper = gp.rightBumper.value;
                if (LockHeld && bumper && !rb)
                {
                    cycle = true;
                    cycleDirection = gp.rightStick.value;
                }
                // The nozzle menu owns RB now. The sim's own X edge stays off: the host fits
                // the nozzle the player releases the menu on.
                MenuHeld = bumper && !LockHeld;
                MenuFromGamepad = true;
                MenuDirection = gp.rightStick.value;
                x = false;
                rb = bumper;
                float t = Mathf.Clamp01(gp.rightTrigger.value);
                r = t >= 0.98f ? 1f : Mathf.Pow(t, Mathf.Clamp(Plugin.TriggerCurve.Value, 0.2f, 5f));
                bool d = gp.dPad.down.value;
                downTap |= d && !down;
                down = d;
                bool u = gp.dPad.up.value;
                upTap |= u && !up;
                up = u;
            }
            else if (input)
            {
                a = input.jump.down;
                b = input.skill2.down;
                l = input.skill3.down;
                // No spare key: the utility key locks on as well as being L, and the special
                // key cycles the target while it's held or opens the nozzle menu otherwise.
                LockHeld = l;
                LockSharesL = true;
                bool special = input.skill4.down;
                if (LockHeld && special && !rb) cycle = true;
                MenuHeld = special && !LockHeld;
                MenuFromGamepad = false;
                MenuDirection = Vector2.zero;
                x = false;
                rb = special;
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
            bool down = r > 0.5f;
            rTap |= down && !rDown;
            rDown = down;
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
        /// (stunned, frozen): nothing pressed. `radial` (the local player's nozzle menu is up):
        /// the trigger stays released, so a trigger held over from the menu does not spray.
        public Native.Pad Take(Vector3 move, bool held = false, bool radial = false)
        {
            if (held)
            {
                aTap = bTap = lTap = xTap = false;
                rTap = false;
                rMax = 0f;
                return new Native.Pad { use_move = 1 };
            }
            // A trigger pull is not a spray while the nozzle wheel is up: RB and RT are both
            // held when a player picks a nozzle without letting go of the trigger by mistake.
            // Drop the latched peak too, so a tap during the menu does not fire on release.
            if (radial) rMax = 0f;
            if (radial) rTap = false;
            float trigger = radial ? 0f : Mathf.Max(r, rMax);
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
                // Keep Sunshine's digital R click released: RoR2 needs movement at full
                // pressure, while the analog value still gives Spray its full burst.
                r = 0,
                r_analog = (byte)Mathf.RoundToInt(trigger * 255f),
            };
            aTap = bTap = lTap = xTap = false;
            rMax = 0f;
            return pad;
        }

        /// A target cycle was asked for since the last call, toward `direction` on screen (the
        /// right stick; zero for the default).
        public bool TakeCycle(out Vector2 direction)
        {
            direction = cycleDirection;
            bool c = cycle;
            cycle = false;
            return c;
        }

        /// D-pad down was pressed since the last call.
        public bool TakeRefill()
        {
            bool t = downTap;
            downTap = false;
            return t;
        }

        /// D-pad up was pressed since the last call (the secret Egg Lay).
        public bool TakeUp()
        {
            bool t = upTap;
            upTap = false;
            return t;
        }

        /// The trigger was pressed since the last call. With FLUDD off it is the second Melee
        /// attack button (RB is the first); with FLUDD on the driver ignores it and the trigger
        /// sprays as usual.
        public bool TakeTriggerTap()
        {
            bool t = rTap;
            rTap = false;
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
