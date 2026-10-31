using System;
using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine;

namespace MarioTest
{
    // Lunar coins lying in the world, as Sunshine's blue coin, for the Mario player only: RoR2's own
    // pickup model is hidden on this machine and the disc's spinning blue coin is drawn where it is.
    // Nothing about the pickup changes (it is still RoR2's lunar coin to everyone, and the HUD counter
    // and its chime are MarioCoinHud's), so this is purely what the local Mario sees. It lies on the
    // ground at a yellow coin's height (not where RoR2's model hovers and bobs), and pops and bounces
    // there when it first shows up, as the yellow ones do (CoinBounce).
    internal sealed class MarioLunarCoin : MonoBehaviour
    {
        const int Capacity = MarioCoins.Capacity;
        const float ScanSeconds = 0.25f;

        readonly List<PickupDisplay> found = new List<PickupDisplay>();
        readonly List<GameObject> hidden = new List<GameObject>();
        // Each coin's place on the ground and its bounce there, from when it was first seen.
        sealed class Lying { public bool grounded; public CoinBounce path; public float since; }
        readonly Dictionary<PickupDisplay, Lying> lying = new Dictionary<PickupDisplay, Lying>();
        MarioCoins.CoinSource source;
        ModelView models;
        IntPtr view;
        bool tried;
        float scanned;

        static readonly System.Reflection.FieldInfo ModelObject =
            typeof(PickupDisplay).GetField("modelObject", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        static readonly System.Reflection.FieldInfo State =
            typeof(PickupDisplay).GetField("pickupState", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        void OnDestroy()
        {
            Restore();
            models?.Dispose();
            models = null;
            if (view != IntPtr.Zero) Native.sms_view_free(view);
            view = IntPtr.Zero;
        }

        bool EnsureModel()
        {
            if (!Plugin.AssetsReady) return false;
            if (view == IntPtr.Zero && !tried)
            {
                tried = true;
                view = Native.sms_view_new_look(NativeLib.CString(Plugin.AssetsDir), 0, 4);
                if (view == IntPtr.Zero)
                    Plugin.Log.LogInfo("Blue coin model did not load (scene/mapobj/coin_blue.bmd); lunar coins keep RoR2's model.");
                else
                    source = new MarioCoins.CoinSource(view);
            }
            if (view == IntPtr.Zero) return false;
            if (models == null) models = ModelView.Create(source, "Sunshine blue coins");
            return models != null;
        }

        static GameObject ModelOf(PickupDisplay display) => ModelObject?.GetValue(display) as GameObject;

        static bool IsLunarCoin(PickupDisplay display)
        {
            if (State == null || !(State.GetValue(display) is UniquePickup pickup)) return false;
            var def = pickup.pickupIndex.isValid ? PickupCatalog.GetPickupDef(pickup.pickupIndex) : null;
            return def != null && def.coinValue > 0u;
        }

        // Put RoR2's models back (another survivor, or the coins' own run ended).
        void Restore()
        {
            foreach (var go in hidden) if (go) go.SetActive(true);
            hidden.Clear();
            found.Clear();
            lying.Clear();
        }

        Lying Place(PickupDisplay display, Vector3 at)
        {
            if (lying.TryGetValue(display, out var l)) return l;
            l = new Lying { since = Time.time };
            // RoR2's coin hangs over where it landed: the ground under it, at a yellow coin's height.
            if (MarioCoins.Ground(at, out float ground)
                && CoinBounce.Make(new Vector3(at.x, ground + MarioCoins.RestHeight, at.z),
                    Vector3.up * MarioCoins.PopUp, ground + MarioCoins.RestHeight, out l.path))
                l.grounded = true;
            lying[display] = l;
            return l;
        }

        void LateUpdate()
        {
            if (source != null) source.count = 0;
            if (!Plugin.LunarCoinBlue.Value || !Run.instance || MarioDriver.Player == null || !EnsureModel())
            {
                if (hidden.Count > 0) Restore();
                models?.Update(UnityHost.Scale);
                return;
            }
            if (Time.unscaledTime - scanned > ScanSeconds)
            {
                scanned = Time.unscaledTime;
                found.Clear();
                foreach (var display in FindObjectsOfType<PickupDisplay>())
                    if (display && IsLunarCoin(display) && found.Count < Capacity) found.Add(display);
                // Forget the ones picked up.
                if (lying.Count > found.Count)
                {
                    var keep = new HashSet<PickupDisplay>(found);
                    var gone = new List<PickupDisplay>();
                    foreach (var d in lying.Keys) if (!d || !keep.Contains(d)) gone.Add(d);
                    foreach (var d in gone) lying.Remove(d);
                }
            }
            var turn = Quaternion.Euler(0f, Time.time * 240f, 0f);
            for (int i = found.Count - 1; i >= 0; i--)
            {
                var display = found[i];
                if (!display) { found.RemoveAt(i); continue; }
                var model = ModelOf(display);
                var at = display.transform.position;
                if (model && model.activeSelf)
                {
                    model.SetActive(false);
                    if (!hidden.Contains(model)) hidden.Add(model);
                }
                var place = Place(display, at);
                if (place.grounded) at = place.path.At(Time.time - place.since, out _);
                else if (model) at = model.transform.position;
                var pos = UnityHost.ToSim(at);
                source.draws[source.count++] = new ViewDraw
                {
                    key = (ulong)display.GetInstanceID(), model = 0, pos = new ViewVec3 { x = pos.x, y = pos.y, z = pos.z },
                    rot_x = turn.x, rot_y = turn.y, rot_z = turn.z, rot_w = turn.w, alpha = 1f,
                };
            }
            hidden.RemoveAll(go => !go);
            models.Update(UnityHost.Scale);
        }
    }
}
