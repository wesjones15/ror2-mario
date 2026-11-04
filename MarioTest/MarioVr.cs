using RoR2;
using Ror2Common;
using UnityEngine;

namespace MarioTest
{
    // Mario played in Blowntobytes' VR mod (ror2-common's Vr): first person from his head, the
    // view only ever turned by the player. The headset is the camera Sunshine's stick and the
    // corkscrew's steering follow; the dominant hand's pointer is the crosshair FLUDD and the
    // lock-on aim at, so looking about never moves the aim. His own body isn't drawn for him (his
    // water, effects and sounds are), and other players see all of him as before. Nothing here
    // runs without the VR mod in a headset.
    internal static class MarioVr
    {
        /// The local player sees this Mario from inside, in a headset.
        internal static bool FirstPerson(CharacterBody body) => Vr.IsLocal(body);

        /// ... with motion controls in Mario's VR layout (VR.Controls).
        internal static bool Controls(CharacterBody body) => Plugin.VrControls.Value && Vr.IsMotionLocal(body);

        /// His body isn't drawn for him (VR.HideBody).
        internal static bool HideBody(CharacterBody body) => Plugin.VrHideBody.Value && FirstPerson(body);

        /// Where the headset faces, flattened: Sunshine's camera heading.
        internal static Vector3 ViewForward(Vector3 fallback) => Vr.HeadFlatForward(fallback);

        /// Where the headset looks (the corkscrew's flight camera).
        internal static Vector3 HeadLook(Vector3 fallback)
        {
            var head = Vr.Head();
            return head ? head.forward : fallback;
        }

        /// The dominant hand's pointer.
        internal static bool Pointer(out Ray ray) => Vr.Pointer(true, out ray);

        // The pointer's own hits closer than this are its hand's neighbours (a wall the hand is in).
        internal const float PointerMinHit = 0.3f;
        // The water turns at most this far off the pointer to reach the point it lands on: the
        // nozzle on his back and the hand are apart, so near targets need more than the camera's 35.
        internal const float PointerMaxTurn = 60f;

        // ---- the wrist HUD -----------------------------------------------------------------------

        /// His HUD goes on the off-hand's wrist panel (VR.WristHud), for whoever is the local
        /// player now: Mario, spectating, or flying a drone.
        internal static bool WristHud => Plugin.VrWristHud.Value && Vr.HandsReady;

        /// The nozzle wheel's pick: where the dominant pointer meets the wheel on the wrist panel,
        /// else `stick`.
        internal static Vector2 WheelDirection(Vector2 stick)
        {
            if (!WristHud || !Pointer(out var ray) || !VrPanel.PointAt(ray, out var gui)) return stick;
            return MarioRadial.PointDirection(gui);
        }

        // ---- the arms ----------------------------------------------------------------------------
        //
        // His own forearms and gloves, cut from his body by libsms (sms_view_set_vr_arms), each on
        // its controller: the glove's middle in the hand's grip, the fingers along the controller,
        // the back of the glove up, the forearm trailing back toward the elbow. They animate with
        // him (a fist, a wave) and flash and freeze with him. While they're drawn the VR mod's
        // stand-in hand is hidden on that controller; a skin with no cut arms (Luigi's and
        // Yoshi's bodies aren't on Mario's rig) keeps the VR mod's hands.

        /// Draws his arms on the controllers in place of his body.
        internal static void DrawArms(CharacterBody body, MarioView view, Vector3 feet)
        {
            bool hands = Vr.HandsReady;
            int drawn = view.DrawArms(feet, side => hands ? ArmAt(side) : null);
            Vr.ShowHandModel(true, drawn == 0);
            Vr.ShowHandModel(false, drawn == 0);
        }

        // Where a side's arm space goes: on that controller (Mario's right arm on the right one),
        // turned by VR.ArmPitch / VR.ArmRoll (the left's roll mirrored) and VR.ArmForward along it.
        static (Vector3, Quaternion)? ArmAt(int side)
        {
            bool left = side == 1;
            var hand = Vr.HandBySide(left);
            if (!hand) return null;
            float roll = Plugin.VrArmRoll.Value * (left ? -1f : 1f);
            var rot = hand.rotation * Quaternion.Euler(Plugin.VrArmPitch.Value, 0f, roll);
            var at = hand.position + rot * (Vector3.forward * Plugin.VrArmForward.Value);
            // With the view turned over in a flip, the hands are seen turned with it.
            return VrRig.Turned(at, rot);
        }

        // ---- the eye -----------------------------------------------------------------------------
        //
        // The VR mod holds the view at a fixed eye height on the body (ror2-common's VrRig). His
        // head's height above his feet moves it from there: down low in a wet slide or a crouch,
        // back up as he stands. Only the height: a spin or the corkscrew swings the head round
        // him, and following that would orbit the view. The height is smoothed (the headset's own
        // motion never is), and a somersault or backflip holds the standing eye unless VR.FlipCamera
        // turns the view over with him instead, about his middle and only while it's drawn.

        // His head's height standing about, as the view's zero (learnt as he waits and runs).
        static float standing = -1f;
        static float eyeOffset;
        static Quaternion flipTurn = Quaternion.identity;
        // The offset settles toward the head this quickly (seconds), and a cut-short flip turns back.
        const float EyeSmoothing = 0.08f, FlipRecover = 0.15f;

        static bool Flip(string status) => status == "UltraJump" || status == "BackJump";
        static bool Standing(string status) => status == "Wait" || status == "Run";

        /// After the frame's draw: moves the view with his head (VR.HeadFollow) and, with
        /// VR.FlipCamera, over through a flip.
        internal static void FollowHead(CharacterBody body, MarioView view, Vector3 feet, string status, bool grounded, float height)
        {
            if (!FirstPerson(body) || view == null || !view.HeadOffset(out var head)) return;
            float h = head.y;
            if (standing < 0f) standing = h;
            if (grounded && Standing(status)) standing += (h - standing) * (1f - Mathf.Exp(-Time.deltaTime / 1f));
            bool flip = Flip(status);
            float target = flip ? 0f : (h - standing) * Mathf.Clamp01(Plugin.VrHeadFollow.Value);
            target = Mathf.Clamp(target, -standing * 0.85f, standing * 0.15f);
            eyeOffset += (target - eyeOffset) * (1f - Mathf.Exp(-Time.deltaTime / EyeSmoothing));
            VrRig.EyeOffset(body, eyeOffset);

            var want = Quaternion.identity;
            var up = Vector3.up;
            var forward = view.DrawnRotation * Vector3.forward;
            forward.y = 0f;
            if (flip && Plugin.VrFlips.Value && forward.sqrMagnitude > 1e-4f && head.sqrMagnitude > 1e-6f)
            {
                forward.Normalize();
                // How far round he has gone: his head's angle from straight up, forward positive
                // (a somersault pitches the view down, a backflip up), about his right.
                float angle = Mathf.Atan2(Vector3.Dot(head, forward), Vector3.Dot(head, up)) * Mathf.Rad2Deg;
                want = Quaternion.AngleAxis(angle, Vector3.Cross(up, forward));
                flipTurn = want;
            }
            else flipTurn = Quaternion.Slerp(flipTurn, want, 1f - Mathf.Exp(-Time.deltaTime / FlipRecover));
            if (Quaternion.Angle(flipTurn, Quaternion.identity) > 0.5f)
                VrRig.RenderTurn(flipTurn, feet + Vector3.up * (height * 0.5f));
            else flipTurn = Quaternion.identity;
        }

        /// Draws the pointer's crosshair this frame (VR.Crosshair), `hot` with a lock.
        internal static void Crosshair(CharacterBody body, bool hot)
        {
            if (!Plugin.VrCrosshair.Value || PauseManager.isPaused || !Pointer(out var ray)) return;
            VrCrosshair.Show(Vr.PointerTarget(ray, body.transform, 300f, PointerMinHit, out _), hot);
        }
    }
}
