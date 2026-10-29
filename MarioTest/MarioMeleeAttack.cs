using System;
using System.Reflection;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Mario's contextual A attack on the empty FLUDD button. With No FLUDD selected, tapping RB
    // or RT starts it: `MarioMelee` runs libssbm's Mario fighter and draws its clip on Sunshine.
    // If that is unavailable, this file still discovers the Melee plugin's provider through
    // reflection (`Mario.MarioContextualAttack.TryAttack(CharacterBody body, Vector3 aim)`), so
    // neither mod depends on the other. The lookup runs once per load.
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
