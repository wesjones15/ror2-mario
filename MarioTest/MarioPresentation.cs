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
        // Showing another player's Mario's HUD, and which; and our HUD cleared for a body that isn't a Mario.
        static bool watching, cleared;
        static CharacterBody watched;

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
            // Spectating: the HUD is the watched player's character's, whoever this player plays.
            var spectated = LocalUsers.Spectated();
            var puppet = spectated ? spectated.GetComponent<MarioPuppet>() : null;
            if (puppet && (puppet.HudHandle != IntPtr.Zero || puppet.HudSim != IntPtr.Zero) && Run.instance && !RunAudio.Ended)
            {
                var player = LocalUserManager.GetFirstLocalUser();
                if (watched != spectated) { watched = spectated; MarioCoinHud.Reset(); }
                var watchedMaster = spectated.master;
                var network = watchedMaster && watchedMaster.playerCharacterMasterController
                    ? watchedMaster.playerCharacterMasterController.networkUser : null;
                MarioCoinHud.UpdateOwned(puppet.HudHandle, watchedMaster, player, network);
                MarioLife.Update(puppet.HudHandle, spectated.healthComponent, true);
                if (puppet.HudHandle == IntPtr.Zero) MarioHud.UpdateBuiltin(puppet.HudSim);
                MarioHudLayout.Update(true, player);
                watching = true;
                cleared = false;
                return;
            }
            if (watching) { watching = false; watched = null; ClearHud(); }
            if (!Active)
            {
                if (user != null) Clear();
                return;
            }
            if (MarioDriver.Player != null) return; // the live driver updates these values
            // Watching somebody who isn't a Mario: not our HUD.
            if (spectated)
            {
                if (!cleared) { cleared = true; ClearHud(); }
                return;
            }
            cleared = false;
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

        static void ClearHud()
        {
            MarioHud.Hide();
            MarioLife.Update(IntPtr.Zero, null);
            MarioCoinHud.UpdateOwned(IntPtr.Zero, null, null);
            MarioHudLayout.Restore();
        }

        void LateUpdate()
        {
            Refresh();
            if (Active || watching) MarioVr.Character.ShowWrist();
            // The nozzle wheel is held over the dominant hand, not on the wrist.
            if (Active && !watching && MarioVr.Character.WristHud) VrHandPanel.Show();
        }

        void OnGUI()
        {
            if (!Active && !watching) return;
            // In VR the HUD goes on the wrist panel instead (MarioVr.Character.WristHud), drawn only as it
            // repaints; the Air Pump overlay sits on RoR2's own HUD, which the VR mod moves.
            bool wrist = MarioVr.Character.WristHud;
            if (wrist && Event.current.type != EventType.Repaint) return;
            MarioVr.Character.OnWrist(() =>
            {
                MarioHud.Draw(); MarioLife.Draw(); MarioCoinHud.Draw();
                if (!wrist && !watching) MarioRadial.Draw();
                if (!wrist && !watching) MarioRadial.DrawAltIcon();
            });
            if (wrist && !watching) MarioRadial.DrawInHand();
        }

        internal static void Clear()
        {
            user = null; networkUser = null; run = null; originalMaster = null;
            watching = cleared = false; watched = null;
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
