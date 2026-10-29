using System;
using System.Reflection;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Optional hook for the Melee plugin's Mario, when that fighter exists: a contextual A attack
    // (the same one the other Melee fighters have) on the empty FLUDD button. With No FLUDD
    // selected, tapping RB asks this hook for a punch/kick; RT stays Sunshine's spray. This file
    // deliberately has no reference to the Melee assembly. The other plugin publishes
    // `Mario.MarioContextualAttack.TryAttack(CharacterBody body, Vector3 aim)`; if it is absent,
    // `Available` stays false and the empty-button tap is omitted. The reflection lookup runs
    // once per load, so dropping the Melee plugin in (or out) changes the controls without
    // either mod depending on the other.
    internal static class MarioMeleeAttack
    {
        static bool resolved;
        static MethodInfo method;

        public static bool Available
        {
            get
            {
                if (MarioMelee.Available) return true;
                if (!resolved)
                {
                    resolved = true;
                    try
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            var type = asm.GetType("Mario.MarioContextualAttack");
                            if (type == null) continue;
                            method = type.GetMethod("TryAttack",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                                null, new[] { typeof(CharacterBody), typeof(Vector3) }, null);
                            if (method != null) break;
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Melee Mario's A attack is unavailable: {e.Message}");
                    }
                }
                return method != null;
            }
        }

        /// Try to start the Melee Mario's contextual A. False when there is no provider or it
        /// refused (a move already playing, for example).
        public static bool TryAttack(CharacterBody body, Vector3 aim)
        {
            if (!body) return false;
            // This plugin's own Melee Mario first (libssbm's A selector plus the ported
            // Sunshine animation); the Melee plugin's provider is the older optional path.
            if (MarioMelee.TryAttack(body, aim)) return true;
            if (!Available) return false;
            try
            {
                return method.Invoke(null, new object[] { body, aim }) is bool started && started;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Melee Mario's A attack failed: {e.Message}");
                return false;
            }
        }

        public static void Reset()
        {
            MarioMelee.Reset();
            resolved = false;
            method = null;
        }
    }
}
