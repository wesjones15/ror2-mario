using RoR2;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Mario played in Blowntobytes' VR mod: ror2-common's VrCharacter (first person from his
    // head, the view only ever turned by the player, his body hidden for him with his own
    // forearms and gloves on the controllers, the pointer's crosshair, the wrist HUD, the opt-in
    // flip) plus what's his own. The headset is the camera Sunshine's stick and the corkscrew's
    // steering follow; the dominant hand's pointer is where FLUDD and the lock-on aim, so looking
    // about never moves the aim. His layout is MarioPad's; the nozzle wheel is picked by pointing
    // at it on the wrist. Nothing here runs without the VR mod in a headset.
    internal static class MarioVr
    {
        static VrCharacter character;
        /// The shared VR character, with Mario's [VR] settings.
        internal static VrCharacter Character => character ?? (character = new VrCharacter(Plugin.VrSettings));

        /// ... with motion controls in Mario's VR layout (VR.Controls).
        internal static bool Controls(CharacterBody body) => Plugin.VrControls.Value && Character.Motion(body);

        // The water turns at most this far off the pointer to reach the point it lands on: the
        // nozzle on his back and the hand are apart, so near targets need more than the camera's 35.
        internal const float PointerMaxTurn = 60f;

        /// The nozzle wheel's pick: where the dominant pointer meets the wheel on the wrist panel,
        /// else `stick`.
        internal static Vector2 WheelDirection(Vector2 stick)
        {
            if (!Character.WristHud || !Character.Pointer(out var ray) || !VrPanel.PointAt(ray, out var gui)) return stick;
            return MarioRadial.PointDirection(gui);
        }

        /// Draws his arms on the controllers in place of his body (libsms cuts them:
        /// sms_view_set_vr_arms). A skin with no cut keeps the VR mod's hands.
        internal static void DrawArms(MarioView view, Vector3 feet) => view.DrawArms(feet, Character);

        // His somersault and backflip are the flips; standing about (Wait, Run) is the eye's zero.
        static VrCharacter.Pose Pose(string status, bool grounded) =>
            status == "UltraJump" || status == "BackJump" ? VrCharacter.Pose.Flip
            : grounded && (status == "Wait" || status == "Run") ? VrCharacter.Pose.Standing
            : VrCharacter.Pose.Other;

        /// After his pose, before the arms: the view follows his head (VR.HeadFollow) and, with
        /// VR.FlipCamera, turns over through a flip.
        internal static void FollowHead(CharacterBody body, MarioView view, Vector3 feet, string status, bool grounded, float height)
        {
            Character.PlaceBehind(body);
            if (view == null || !Character.FirstPerson(body) || !view.HeadOffset(out var head)) return;
            Character.FollowHead(body, head, view.DrawnRotation * Vector3.forward, feet, height, Pose(status, grounded));
        }
    }
}
