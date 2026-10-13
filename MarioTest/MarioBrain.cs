using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // The decisions of a Mario no player controls (Goobo Jr.'s clone). The AI master only walks
    // (ror2-common's SurvivorAi.Drivers: stand and face the enemy in spray range, else run at it,
    // else follow the leader); this presses Mario's buttons from where the enemy is:
    //  - in spray range with a clear line: hold L, which locks on (the sim aims FLUDD at the
    //    enemy, as the player's L does with a lock-on) and R, a steady spray;
    //  - close and on the ground: a jump, and on the way down over the enemy L, a ground pound;
    //  - further off: sprint, a hop now and then (and when held up), a dive at middling range.
    // Presses are held for a few steps so each reads as one tap.
    internal sealed class MarioBrain
    {
        public struct Presses
        {
            public bool a, b, l, sprint;
            public float r;
            /// The enemy to lock onto while l is held, else null.
            public CharacterBody target;
        }

        // Of Sunshine's six Marios of Spray reach, how much counts as in range.
        const float ReachMarios = 6f * 0.7f;
        // How long a tap is held (s).
        const float Tap = 0.25f, DiveTap = 0.15f, PoundTap = 0.2f;
        // A steady spray, on the move or not.
        const float Spray = 0.6f;

        /// How far the Spray nozzle's water carries, as far as the AI goes (m).
        public static float HoldRange =>
            Mathf.Max(3f, Plugin.SprayReach.Value * ReachMarios * UnityHost.MarioHeight * UnityHost.Scale);

        float jumpHold, jumpWait, diveHold, diveWait, poundHold, comboWait, stuck;
        Vector3 lastPos;
        bool engaged, comboing;

        /// The buttons for the next step. `move` is the AI's walk (world), `dt` the step.
        public Presses Think(CharacterBody body, in Native.State st, Vector3 move, float dt)
        {
            var p = new Presses();
            jumpHold -= dt; jumpWait -= dt; diveHold -= dt; diveWait -= dt;
            poundHold -= dt; comboWait -= dt;
            var enemy = SurvivorAi.Enemy(body);
            if (!enemy)
            {
                engaged = comboing = false;
                stuck = 0f;
                lastPos = body.footPosition;
                p.a = jumpHold > 0f;
                p.sprint = move.sqrMagnitude > 0.01f;
                return p;
            }

            bool grounded = st.on_ground != 0;
            var feet = body.footPosition;
            var at = enemy.corePosition;
            float d = new Vector2(at.x - feet.x, at.z - feet.z).magnitude;
            bool sight = !Physics.Linecast(body.corePosition, at, WorldProbe.Mask, QueryTriggerInteraction.Ignore);
            float range = HoldRange;
            // A little slack once engaged, so the edge doesn't flicker.
            engaged = sight && d <= range * (engaged ? 1.15f : 1f);
            if (grounded) comboing = false;

            if (engaged)
            {
                // Close in: hop onto it, and pound on the way down.
                if (grounded && d < 4f && comboWait <= 0f && jumpHold <= 0f)
                {
                    jumpHold = Tap;
                    comboWait = 6f;
                    comboing = true;
                }
                if (comboing && !grounded && st.vel.y < 0f && st.pounding == 0 && d < 3f && at.y < feet.y + 1f)
                    poundHold = PoundTap;
                // On the ground L locks on; in the air a fresh press pounds, so only when meant.
                p.l = grounded ? jumpHold <= 0f : poundHold > 0f;
                p.r = Spray;
                p.target = grounded && p.l ? enemy : null;
            }
            else
            {
                p.sprint = true;
                if (move.sqrMagnitude > 0.01f)
                {
                    // Held up (a wall, a ledge): jump.
                    stuck = (feet - lastPos).sqrMagnitude < 0.0004f ? stuck + dt : 0f;
                    if (grounded && jumpWait <= 0f && jumpHold <= 0f)
                    {
                        jumpHold = Tap;
                        jumpWait = Random.Range(1f, 1.8f);
                    }
                    if (grounded && stuck > 0.4f && jumpHold <= 0f)
                    {
                        jumpHold = Tap;
                        stuck = 0f;
                    }
                    if (grounded && sight && d > 5f && d < range + 3f && diveWait <= 0f)
                    {
                        diveHold = DiveTap;
                        diveWait = 4f;
                    }
                }
                lastPos = feet;
            }
            p.a = jumpHold > 0f;
            p.b = diveHold > 0f;
            return p;
        }
    }
}
