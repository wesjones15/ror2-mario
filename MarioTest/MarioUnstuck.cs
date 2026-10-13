using UnityEngine;

namespace MarioTest
{
    // Host recovery for RoR2 geometry; this is not a Sunshine movement rule. Status names
    // follow libsms's MarioStatus mirror (spec: Player/MarioStatus.hpp). Measure actual
    // displacement, since a blocked sim can still report a large falling/sliding velocity.
    internal sealed class MarioUnstuck
    {
        internal delegate bool GroundProbe(Vector3 sample, float rise, float drop, out Vector3 feet);
        const float Delay = 3f;
        Vector3 anchor, safeFeet;
        bool watching, hasSafeFeet;
        float elapsed, safeAge;

        // External teleports, vehicles, death and loss of authority invalidate old ground.
        public void Reset()
        {
            watching = hasSafeFeet = false;
            elapsed = safeAge = 0f;
        }

        internal static bool Fallen(string status, bool grounded, float verticalVelocity)
        {
            switch (status)
            {
                case "DownLoser": case "Hanging": case "Ascend": case "Descend":
                    return false;
                case "BackDown": case "ForeDown": case "ShortBackDown": case "ShortForeDown":
                case "SafeBackDown": case "SafeForeDown": case "CatchDown": case "LandSafeDown":
                case "JumpShortBackDown": case "JumpShortForeDown": case "JumpBackDown":
                case "JumpForeDown": case "Catch": case "CatchStop": case "CatchLost":
                case "SlipFore": case "SlipBack":
                    return true;
                default:
                    return !grounded && verticalVelocity < 0f;
            }
        }

        public bool Step(Vector3 feet, bool fallen, bool grounded, bool held, float dt,
            float height, GroundProbe probe, out Vector3 destination)
        {
            destination = default;
            float tolerance = Mathf.Max(0.05f, height * 0.08f);
            safeAge += dt;
            if (!fallen && grounded && !held && safeAge >= 0.25f)
            {
                safeAge = 0f;
                if (probe(feet, height * 0.25f, height * 0.5f, out var safe)
                    && (safe - feet).sqrMagnitude <= tolerance * tolerance)
                {
                    safeFeet = safe;
                    hasSafeFeet = true;
                }
            }
            if (!fallen || held)
            {
                watching = false;
                elapsed = 0f;
                return false;
            }
            if (!watching || (feet - anchor).sqrMagnitude > tolerance * tolerance)
            {
                anchor = feet;
                elapsed = 0f;
                watching = true;
            }
            elapsed += dt;
            // Float accumulation must not delay a 180-step / 60 Hz timeout by a tick.
            if (elapsed + 0.0001f < Delay) return false;
            elapsed = 0f; // Retry at most once every three seconds if no clear floor exists.

            // Start above the obstruction, then widen the search around it. The probe checks
            // floor support and standing clearance, including the fallen pod door.
            for (int ring = 0; ring <= 3; ring++)
            {
                int count = ring == 0 ? 1 : 12;
                for (int i = 0; i < count; i++)
                {
                    float angle = i * (2f * Mathf.PI / count);
                    var sample = feet + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * (ring * height);
                    if (probe(sample, height * 2f, height * 3f, out destination)
                        && (destination - feet).sqrMagnitude > tolerance * tolerance)
                        return true;
                }
            }
            // A previously clear spot can become obstructed by a moving prop: recheck it.
            return hasSafeFeet && probe(safeFeet, height * 0.25f, height * 0.5f, out destination)
                && (destination - feet).sqrMagnitude > tolerance * tolerance;
        }
    }
}
