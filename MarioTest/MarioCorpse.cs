using System;
using UnityEngine;

namespace MarioTest
{
    // The player's Mario after RoR2 killed his body: his sim, knocked out (sms_sim_down: the
    // game's DOWN_LOSER, sliding to a stop and lying down with the "down" voices), keeps
    // stepping with nothing pressed and is drawn where the sim has him, for `seconds`. RoR2
    // removes a dead body a second after it comes to rest, so this lives on its own object
    // (in the stage: a new stage ends it). One at a time; a Mario coming back ends it.
    internal sealed class MarioCorpse : MonoBehaviour
    {
        static MarioCorpse current;
        // Another player's Mario who fell: any number at once, each ending on its own.
        bool others;

        IntPtr sim;
        MarioView view;
        Native.Host host;
        float left;

        /// Takes over `sim` and `view` (freeing both when done).
        public static void Begin(IntPtr sim, MarioView view, float seconds, bool otherPlayers = false)
        {
            if (!otherPlayers) End();
            var c = new GameObject("MarioCorpse").AddComponent<MarioCorpse>();
            c.others = otherPlayers;
            c.sim = sim;
            c.view = view;
            c.host = UnityHost.Table();
            c.left = Mathf.Clamp(seconds, 0f, 30f);
            if (!otherPlayers) current = c;
        }

        public static void End()
        {
            if (current) Destroy(current.gameObject);
            current = null;
        }

        void FixedUpdate()
        {
            left -= Time.fixedDeltaTime;
            if (left <= 0f || sim == IntPtr.Zero)
            {
                Destroy(gameObject);
                return;
            }
            var pad = new Native.Pad();
            Native.sms_sim_step(sim, ref host, ref pad, 0, Time.fixedDeltaTime,
                view != null ? view.Handle : IntPtr.Zero);
        }

        void LateUpdate()
        {
            if (view == null || sim == IntPtr.Zero) return;
            view.Update(sim, 0f);
            Native.sms_sim_state(sim, out var st);
            var feet = UnityHost.ToUnity(st.pos);
            view.Effects(feet);
            view.Draw(feet);
        }

        void OnDestroy()
        {
            if (current == this) current = null;
            view?.Dispose();
            view = null;
            if (sim != IntPtr.Zero)
            {
                Native.sms_sim_free(sim);
                sim = IntPtr.Zero;
            }
        }
    }
}
