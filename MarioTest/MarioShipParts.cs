using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Bootstrap;
using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Mario in Ship Repair, which the Olimar plugin runs: it lands him a Dolphin at the teleporter and owns the
    // ship parts. This is his half. The parts are takeables for Sunshine's own pick-up (B near one: the grab,
    // the carrying pose, the lift sound), kept in front of him while he carries it, and a put or a throw
    // hands them back to the mode, which sets them down or puts them into his Dolphin's receiver if they
    // land there. The two plugins meet through the Olimar plugin's ShipRepairApi, found by reflection, so
    // Mario still runs without Olimar installed (and then has no Ship Repair).
    internal static unsafe class MarioShipParts
    {
        const string OlimarGuid = "com.libpikmin.olimartest";
        const string BodyName = "MarioTestBody";
        /// A thrown part leaves at this many metres a second along his facing and this much upward, plus
        /// half his own horizontal speed.
        const float ThrowSpeed = 14f, ThrowLift = 9f;

        static bool tried;
        static Func<bool> active;
        static Action<CharacterBody> register;
        static Func<Vector3, float, uint[], Vector3[], float[], int> takeables;
        static Func<uint, bool> take;
        static Action<uint, Vector3, float> hold, put;
        static Action<uint, Vector3, Vector3> throwPart;
        static Action<uint> drop;

        static readonly Native.TakeablesFn takeablesFn = Takeables;
        static readonly Native.TakeFn takeFn = Take;
        public static IntPtr TakeablesPtr => Marshal.GetFunctionPointerForDelegate(takeablesFn);
        public static IntPtr TakePtr => Marshal.GetFunctionPointerForDelegate(takeFn);

        static readonly uint[] ids = new uint[16];
        static readonly Vector3[] positions = new Vector3[16];
        static readonly float[] radii = new float[16];
        static readonly Dictionary<uint, float> radiusOf = new Dictionary<uint, float>();
        static readonly HashSet<CharacterBody> registered = new HashSet<CharacterBody>();

        // The part in his hands (its id and half-width in metres), and a throw whose aim is still to come.
        static uint heldPart;
        static bool holding;
        static float heldRadius;
        static uint pendingThrow;
        static bool throwing;
        static Vector3 pendingFrom;

        /// Finds the Olimar plugin's API, once. False without it.
        public static bool Bind()
        {
            if (tried) return active != null;
            tried = true;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(OlimarGuid, out var info) || info.Instance == null) return false;
                var api = info.Instance.GetType().Assembly.GetType("OlimarTest.ShipRepairApi");
                if (api == null) return false;
                // Its parts are public statics of plain and Unity types.
                T Method<T>(string name) where T : Delegate =>
                    (T)Delegate.CreateDelegate(typeof(T), api.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new MissingMethodException(name));
                active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), api.GetProperty("Active").GetGetMethod());
                register = Method<Action<CharacterBody>>("RegisterGuest");
                takeables = Method<Func<Vector3, float, uint[], Vector3[], float[], int>>("Takeables");
                take = Method<Func<uint, bool>>("Take");
                hold = Method<Action<uint, Vector3, float>>("Hold");
                put = Method<Action<uint, Vector3, float>>("Put");
                throwPart = Method<Action<uint, Vector3, Vector3>>("Throw");
                drop = Method<Action<uint>>("Drop");
                Method<Action<string>>("AllowGuest")(BodyName);
                Plugin.Log.LogInfo("Mario can play Ship Repair (the Olimar plugin is installed).");
                return true;
            }
            catch (Exception e)
            {
                active = null;
                Plugin.Log.LogWarning($"Mario could not reach the Olimar plugin's Ship Repair: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        static bool Playing => Bind() && active();

        /// Every Mario's body (on the server too, for a client's): tells the mode he is a player.
        public static void Register(CharacterBody body)
        {
            if (!body || !Playing || !registered.Add(body)) return;
            registered.RemoveWhere(b => !b);
            register(body);
        }

        static unsafe uint Takeables(IntPtr user, ref Native.Vec3 pos, float radius, Native.Takeable* output, uint max)
        {
            try
            {
                if (!Playing) return 0;
                int n = Math.Min(takeables(UnityHost.ToUnity(pos), radius * UnityHost.Scale, ids, positions, radii), (int)max);
                for (int i = 0; i < n; i++)
                {
                    radiusOf[ids[i]] = radii[i];
                    output[i] = new Native.Takeable
                    {
                        id = ids[i], pos = UnityHost.ToSim(positions[i]), radius = radii[i] / UnityHost.Scale, fruit = -1,
                    };
                }
                return (uint)Math.Max(0, n);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Ship part pick-up: {e.GetType().Name}: {e.Message}");
                return 0;
            }
        }

        static byte Take(IntPtr user, uint id)
        {
            try
            {
                if (!Playing || !take(id)) return 0;
                heldPart = id;
                holding = true;
                heldRadius = radiusOf.TryGetValue(id, out var r) ? r : 1f;
                return 1;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Ship part take: {e.GetType().Name}: {e.Message}");
                return 0;
            }
        }

        static Vector3 Hand(Vector3 feet, float yawRadians) =>
            feet + Vector3.up * 0.95f + new Vector3(Mathf.Sin(yawRadians), 0f, Mathf.Cos(yawRadians)) * (0.35f + heldRadius);

        /// After each step of the local Mario: the part he carries stays in his arms.
        public static void Step(CharacterBody body, in Native.State st, bool local)
        {
            if (!local || !holding) return;
            try
            {
                float yaw = st.model_yaw * Mathf.PI / 32768f;
                hold(heldPart, Hand(UnityHost.ToUnity(st.pos), yaw), yaw);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Ship part hold: {e.GetType().Name}: {e.Message}"); }
        }

        /// He died, or the stage ended: the part is let go.
        public static void Lost()
        {
            if (!holding) return;
            holding = false;
            throwing = false;
            try { drop(heldPart); } catch (Exception) { }
        }

        /// Sunshine's put and throw, from the step's events.
        public static void OnEvent(ref Native.Event e)
        {
            if (!holding && !throwing) return;
            try
            {
                switch ((Native.EventKind)e.kind)
                {
                    case Native.EventKind.Put:
                    {
                        if (!holding || (uint)e.arg != heldPart) return;
                        holding = false;
                        float yaw = e.value * Mathf.PI / 32768f;
                        var feet = UnityHost.ToUnity(e.pos);
                        var ahead = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)) * (heldRadius + 0.8f);
                        put(heldPart, feet + ahead + Vector3.up * 0.2f, yaw);
                        break;
                    }
                    case Native.EventKind.Throw:
                        if (!holding || (uint)e.arg != heldPart) return;
                        holding = false;
                        throwing = true;
                        pendingThrow = heldPart;
                        pendingFrom = UnityHost.ToUnity(e.pos);
                        break;
                    case Native.EventKind.ThrowAim:
                    {
                        if (!throwing) return;
                        throwing = false;
                        float yaw = e.arg * Mathf.PI / 32768f;
                        var forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
                        var run = UnityHost.ToUnity(e.pos) * 30f;
                        run.y = 0f;
                        throwPart(pendingThrow, Hand(pendingFrom, yaw), forward * ThrowSpeed + run * 0.5f + Vector3.up * ThrowLift);
                        break;
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Ship part put/throw: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
