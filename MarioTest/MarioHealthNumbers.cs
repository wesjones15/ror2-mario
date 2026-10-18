using UnityEngine;
using UnityEngine.AddressableAssets;

namespace MarioTest
{
    // DamageNumberManager's original 4x4 digit atlas. Use the asset itself so another survivor's
    // temporary damage-font replacement cannot change Mario's health readout.
    internal static class MarioHealthNumbers
    {
        static Texture2D atlas;

        public static void Draw(float center, float top, int current, int full)
        {
            if (!atlas)
                atlas = Addressables.LoadAssetAsync<Texture2D>(
                    "RoR2/Base/Core/DamageNumberManager/texDamageNumbers.png").WaitForCompletion();
            if (!atlas) return;
            string text = $"{current}/{full}";
            // Half the previous 0.032-screen-height font. Square atlas cells include their own outline.
            float size = Screen.height * 0.016f, advance = size * 0.75f;
            float left = center - ((text.Length - 1) * advance + size) * 0.5f;
            var color = GUI.color;
            var matrix = GUI.matrix;
            GUI.color = Color.white;
            for (int i = 0; i < text.Length; i++)
            {
                var rect = new Rect(left + i * advance, top, size, size);
                int digit = text[i] - '0';
                if (digit >= 0 && digit <= 9)
                    GUI.DrawTextureWithTexCoords(rect, atlas,
                        new Rect(digit % 4 * 0.25f, 0.75f - digit / 4 * 0.25f, 0.25f, 0.25f), true);
                else
                {
                    // The atlas has digits and K/M/B/!, but no fraction slash.
                    GUIUtility.RotateAroundPivot(24f, rect.center);
                    GUI.color = Color.black;
                    GUI.DrawTexture(new Rect(rect.center.x - size * 0.10f, top + size * 0.05f,
                        size * 0.20f, size * 0.9f), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(rect.center.x - size * 0.045f, top + size * 0.10f,
                        size * 0.09f, size * 0.8f), Texture2D.whiteTexture);
                    GUI.matrix = matrix;
                }
            }
            GUI.color = color;
        }
    }
}
