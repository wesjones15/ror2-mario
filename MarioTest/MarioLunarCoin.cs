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
    // and its chime are MarioCoinHud's), so this is purely what the local Mario sees.
    internal sealed class MarioLunarCoin : MonoBehaviour
    {
        const int Capacity = MarioCoins.Capacity;
        const float ScanSeconds = 0.25f;

        readonly List<PickupDisplay> found = new List<PickupDisplay>();
        readonly List<GameObject> hidden = new List<GameObject>();
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
            }
            var turn = Quaternion.Euler(0f, Time.time * 240f, 0f);
            for (int i = found.Count - 1; i >= 0; i--)
            {
                var display = found[i];
                if (!display) { found.RemoveAt(i); continue; }
                var model = ModelOf(display);
                var at = model ? model.transform.position : display.transform.position;
                if (model && model.activeSelf)
                {
                    model.SetActive(false);
                    if (!hidden.Contains(model)) hidden.Add(model);
                }
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
