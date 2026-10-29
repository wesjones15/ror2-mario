using System;
using System.Reflection;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Optional hook for the Melee plugin's Mario, when that fighter exists: a contextual A attack
    // (the same one the other Melee fighters have) on Mario's right trigger, replacing the spray.
    // This file deliberately has no reference to the Melee assembly. The other plugin publishes
    // `Mario.MarioContextualAttack.TryAttack(CharacterBody body, Vector3 aim)`; if it is absent,
    // `Available` stays false: Mario keeps his normal RT spray and the radial menu omits the A
    // attack segment. The reflection lookup runs once per load, so dropping the Melee plugin in
    // (or out) changes the controls without either mod depending on the other.
    internal static class MarioMeleeAttack
    {
        static bool resolved;
        static MethodInfo method;

        public static bool Available
        {
            get
            {
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
            if (!Available || !body) return false;
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
            resolved = false;
            method = null;
        }
    }
}
