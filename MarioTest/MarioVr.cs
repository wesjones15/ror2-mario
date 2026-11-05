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
    // about never moves the aim. His layout is MarioPad's; the nozzle wheel opens over the
    // dominant hand and is picked with its stick. Nothing here runs without the VR mod in a headset.
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

        /// The nozzle wheel's pick: it's held over the dominant hand (MarioRadial.DrawInHand), so
        /// that hand's stick.
        internal static Vector2 WheelDirection(Vector2 stick) => stick;

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
            bool first = view != null && Character.FirstPerson(body);
            MarioFx.VrFirstPerson = first;
            // The eye at his own head, not the VR mod's human-height one: his eyes sit about an
            // eighth of his height over the head joint (at the neck), under the cap's brim.
            Character.EyeAtHead = true;
            Character.EyeAboveHead = height * 0.12f;
            if (first && view.HeadOffset(out var head))
                Character.FollowHead(body, head, view.DrawnRotation * Vector3.forward, feet, height, Pose(status, grounded));
            // The swirl and the water's facing follow the headset's own eye (next tick).
            var eye = first ? GameSettings.PlayerCamera() : null;
            view?.SetVrEye(eye ? Mathf.Max(eye.position.y - feet.y, 0.01f) : 0f);
        }
    }
}
