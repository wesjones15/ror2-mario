using UnityEngine;

namespace MarioTest
{
    // A notice, never a block: while this machine is still preparing its Sunshine files (a first launch extracts them, builds the audio and portal
    // caches and the VR arms in the background) the player can go on and start; until they finish, Mario is drawn with stand-ins and has no
    // Sunshine sounds, and the first stage's entrance uses the stand-in statue. The same sequence runs on every machine either way.
    internal sealed class MarioPreparingNotice : MonoBehaviour
    {
        GUIStyle style;
        bool logged;

        void OnGUI()
        {
            if (!Plugin.AssetsBusy) return;
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo("Mario: the Sunshine files are still being prepared; a notice is shown (nothing is blocked).");
            }
            if (style == null)
                style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.white } };
            float width = Mathf.Min(760f, Screen.width - 40f);
            GUI.Box(new Rect((Screen.width - width) * .5f, 12f, width, 70f),
                "Mario: the Sunshine files are still being prepared.\nYou can start now; until they finish you will see stand-ins (the first stage's statue too) and hear no Sunshine sounds.", style);
        }
    }
}
