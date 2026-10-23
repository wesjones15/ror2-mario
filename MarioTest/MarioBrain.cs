using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // The decisions of a Mario no player controls (Goobo Jr.'s clone). The AI master only walks
    // (ror2-common's SurvivorAi.Drivers: stand and face the enemy in spray range, else run at it,
    // else follow the leader); this presses Mario's buttons from where the enemy is:
    //  - in spray range with a clear line: lock on (the left bumper's job for a player, so FLUDD
    //    aims at the enemy) and hold R, the Spray being his gun: a steady stream for as long as
    //    the enemy stays in reach;
    //  - close and on the ground: a jump, and on the way down over the enemy L, a ground pound,
    //    with the spray still going (the pound interrupts it);
    //  - further off: sprint, a hop now and then (and when held up), a dive at middling range.
    // The stream drains the tank in a few seconds and an AI has no player to pump it, so it
    // presses the D-pad refill when it runs low and keeps firing.
    // Presses are held for a few steps so each reads as one tap.
    internal sealed class MarioBrain
    {
        public struct Presses
        {
            public bool a, b, l, sprint, lockHeld, down;
            public float r;
            /// The enemy to lock onto while lockHeld is held, else null.
            public CharacterBody target;
        }

        // Of Sunshine's six Marios of Spray reach, how much counts as in range.
        const float ReachMarios = 6f * 0.7f;
        // How long a tap is held (s).
        const float Tap = 0.25f, DiveTap = 0.15f, PoundTap = 0.2f;
        // The trigger all the way down: the Spray is a plain full press now (no pressure to
        // read), which is fine for an AI that only runs around as Goobo Jr.'s clone.
        const float Spray = 1f;
        // The tank, when this low (0..1), is refilled: about a second of stream left.
        const float Refill = 0.15f;

        /// How far the Spray nozzle's water carries, as far as the AI goes (m).
        public static float HoldRange =>
            Mathf.Max(3f, Plugin.SprayReach.Value * ReachMarios * UnityHost.MarioHeight * UnityHost.Scale);

        float jumpHold, jumpWait, diveHold, diveWait, poundHold, comboWait, stuck;
        Vector3 lastPos;
        bool spraying, comboing;

        /// The buttons for the next step. `move` is the AI's walk (world), `dt` the step.
        public Presses Think(CharacterBody body, in Native.State st, Vector3 move, float dt)
        {
            var p = new Presses();
            jumpHold -= dt; jumpWait -= dt; diveHold -= dt; diveWait -= dt;
            poundHold -= dt; comboWait -= dt;
            var enemy = SurvivorAi.Enemy(body);
            if (!enemy)
            {
                Stop();
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
            // A little slack once spraying, so the edge doesn't flicker.
            bool inReach = sight && d <= range * (spraying ? 1.15f : 1f);
            if (inReach != spraying)
            {
                spraying = inReach;
                if (inReach) Plugin.Log.LogInfo($"Goobo Mario sprays {enemy.name} at {d:0.0} m");
            }
            if (grounded) comboing = false;

            if (inReach)
            {
                // The spray is his gun: locked on, trigger down, for as long as the enemy is in
                // reach (a dry tank puffs air, so the refill below comes first).
                p.lockHeld = true;
                p.target = enemy;
                p.r = st.water > 0f ? Spray : 0f;
                // Close in: hop onto it, and pound on the way down.
                if (grounded && d < 4f && comboWait <= 0f && jumpHold <= 0f)
                {
                    jumpHold = Tap;
                    comboWait = 6f;
                    comboing = true;
                }
                if (comboing && !grounded && st.vel.y < 0f && st.pounding == 0 && d < 3f && at.y < feet.y + 1f)
                    poundHold = PoundTap;
                // A pound stops the pump: no water while it lasts.
                p.l = !grounded && poundHold > 0f;
                if (p.l) p.r = 0f;
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
            // A steady stream empties the tank in a few seconds, and an AI has no player to pump
            // it (the D-pad) or stand in water: refill when low, so the next enemy meets the spray.
            if (Plugin.DpadRefill.Value && st.water < Refill)
            {
                p.down = true;
                Plugin.Log.LogInfo("Goobo Mario refills FLUDD");
            }
            p.a = jumpHold > 0f;
            p.b = diveHold > 0f;
            return p;
        }

        void Stop()
        {
            if (spraying) Plugin.Log.LogInfo("Goobo Mario stops spraying: no enemy");
            spraying = comboing = false;
            stuck = 0f;
            lastPos = default;
        }
    }
}
