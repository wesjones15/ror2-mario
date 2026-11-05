using System;
using RoR2;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // The local Mario player's presentation belongs to their run/user, not to
    // the temporary body. Remote Operation replaces cachedBody; spectating
    // changes cameraRigController.targetBody. Neither ends this ownership.
    [DefaultExecutionOrder(1000)]
    internal sealed class MarioPresentation : MonoBehaviour
    {
        static LocalUser user;
        static NetworkUser networkUser;
        static Run run;
        static CharacterMaster originalMaster;
        static IntPtr hudView;
        static bool tried;

        internal static bool Active => run && Run.instance == run && !RunAudio.Ended
            && user != null && user == LocalUserManager.GetFirstLocalUser()
            && networkUser && user.currentNetworkUser == networkUser;
        internal static Camera Camera => Active && user.cameraRigController ? user.cameraRigController.sceneCam : null;
        internal static LocalUser User => Active ? user : null;

        internal static void Observe(LocalUser owner, CharacterBody mario)
        {
            if (!Run.instance || owner == null || owner != LocalUserManager.GetFirstLocalUser() || !mario) return;
            if (user != owner || run != Run.instance || networkUser != owner.currentNetworkUser) Clear();
            user = owner; networkUser = owner.currentNetworkUser; run = Run.instance;
            originalMaster = mario.master;
        }

        void Awake()
        {
            Run.onRunDestroyGlobal += RunEnded;
            Run.onRunStartGlobal += RunEnded;
        }

        static void RunEnded(Run _) => Clear();

        // Late enough to refresh after a dying body's/corpse's view is disposed.
        internal static void Refresh()
        {
            if (!Active)
            {
                if (user != null) Clear();
                return;
            }
            if (MarioDriver.Player != null) return; // the live driver updates these values
            if (!Plugin.AssetsReady) return;
            if (!tried)
            {
                tried = true;
                // Full view supplies life/coin meters; silent, with no model renderer
                // or sim, and independent of the corpse's native handle.
                hudView = Native.sms_view_new(NativeLib.CString(Plugin.AssetsDir), 0);
            }
            if (hudView == IntPtr.Zero) return;
            var rig = user.cameraRigController;
            var body = rig && rig.targetBody ? rig.targetBody : user.cachedBody;
            var master = user.cachedMaster ? user.cachedMaster : originalMaster;
            MarioCoinHud.UpdateOwned(hudView, master, user);
            MarioLife.Update(hudView, body ? body.healthComponent : null, true);
            MarioHudLayout.Update(true, user);
            // FLUDD's last Mario gauge remains a presentation accessory. No
            // nonexistent drone/spectator tank is advanced or given gameplay powers.
        }

        void LateUpdate()
        {
            Refresh();
            if (Active) MarioVr.Character.ShowWrist();
            // The nozzle wheel is held over the dominant hand, not on the wrist.
            if (Active && MarioVr.Character.WristHud) VrHandPanel.Show();
        }

        void OnGUI()
        {
            if (!Active) return;
            // In VR the HUD goes on the wrist panel instead (MarioVr.Character.WristHud), drawn only as it
            // repaints; the Air Pump overlay sits on RoR2's own HUD, which the VR mod moves.
            bool wrist = MarioVr.Character.WristHud;
            if (wrist && Event.current.type != EventType.Repaint) return;
            MarioVr.Character.OnWrist(() =>
            {
                MarioHud.Draw(); MarioLife.Draw(); MarioCoinHud.Draw();
                if (!wrist) MarioRadial.Draw();
                if (!wrist) MarioRadial.DrawAltIcon();
            });
            if (wrist) MarioRadial.DrawInHand();
        }

        internal static void Clear()
        {
            user = null; networkUser = null; run = null; originalMaster = null;
            if (hudView != IntPtr.Zero) Native.sms_view_free(hudView);
            hudView = IntPtr.Zero; tried = false;
            MarioHud.Reset(); MarioLife.Reset(); MarioCoinHud.Reset(); MarioRadial.Reset(); MarioHudLayout.Restore();
        }

        void OnDestroy()
        {
            Run.onRunDestroyGlobal -= RunEnded;
            Run.onRunStartGlobal -= RunEnded;
            Clear();
            MarioHeatHaze.Shutdown();
        }
    }
}
