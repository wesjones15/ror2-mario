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

        /// Draws the pointer's crosshair this frame (VR.Crosshair), `hot` with a lock.
        internal static void Crosshair(CharacterBody body, bool hot)
        {
            if (!Plugin.VrCrosshair.Value || PauseManager.isPaused || !Pointer(out var ray)) return;
            VrCrosshair.Show(Vr.PointerTarget(ray, body.transform, 300f, PointerMinHit, out _), hot);
        }
    }
}
