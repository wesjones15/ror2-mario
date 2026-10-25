using System;
using System.IO;
using RoR2;
using Ror2Common;
using UnityEngine;
using Path = System.IO.Path;

namespace MarioTest
{
    // Luigi: an alt-skin that keeps every one of Mario's moves and his FLUDD but draws Luigi's
    // HSD mesh from the Melee cache (the native view swaps the body; the SkinDef only names it,
    // gives it an icon and lets the loadout select it). It is registered only when the Melee
    // cache is complete, so a player without it never sees the option.
    internal static class MarioSkin
    {
        public const uint Mario = 0, Luigi = 1;

        static string meleeDir;
        static bool ready;
        static SkinDef luigiSkin;
        /// The rendered Luigi portrait, for the skin's icon and the pause sheet's face.
        public static Texture2D LuigiPortrait { get; private set; }

        public static bool Ready => ready;
        public static string MeleeDir => meleeDir;

        /// Resolves the Melee cache (the folder the Melee plugin extracted) and reports whether
        /// Luigi's files are there.
        public static void Discover(string configured, string here)
        {
            meleeDir = Resolve(configured, here);
            ready = meleeDir != null
                && File.Exists(Path.Combine(meleeDir, "PlLgNr.dat"))
                && File.Exists(Path.Combine(meleeDir, "PlCo.dat"));
        }

        static string Resolve(string configured, string here)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            string env = Environment.GetEnvironmentVariable("SSBM_ASSETS");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            // The Melee plugin's own cache, beside its plugin folder (BepInEx/plugins/Melee/assets).
            foreach (var rel in new[] { "../Melee/assets", "assets", "Melee/assets" })
            {
                var p = Path.GetFullPath(Path.Combine(here, rel));
                if (File.Exists(Path.Combine(p, "PlLgNr.dat"))) return p;
            }
            // The Melee plugin's pointer file, wherever its cache actually lives.
            var pointer = Path.GetFullPath(Path.Combine(here, "../Melee/assets_dir.txt"));
            if (File.Exists(pointer))
            {
                var target = File.ReadAllText(pointer).Trim();
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(Path.Combine(target, "PlLgNr.dat"))) return target;
            }
            return null;
        }

        /// The skin the body's loadout selected: 0 Mario, 1 Luigi (when available).
        public static uint Selected(CharacterBody body) => ready && body && body.skinIndex >= 1 ? Luigi : Mario;

        /// The skin the loadout has selected for Mario's body, for scenes with no live body yet
        /// (the character-select display): 0 Mario, 1 Luigi (when available).
        public static uint Selected()
        {
            if (!ready) return Mario;
            var index = BodyCatalog.FindBodyIndex("MarioTestBody");
            if (index == BodyIndex.None) return Mario;
            foreach (var user in LocalUserManager.readOnlyLocalUsersList)
                if (user.userProfile != null)
                    return user.userProfile.loadout.bodyLoadoutManager.GetSkinIndex(index) >= 1 ? Luigi : Mario;
            return Mario;
        }

        /// Loads Luigi's HSD mesh into an already-open view; false when the cache is incomplete.
        public static bool Load(IntPtr view) => ready && Native.sms_view_set_luigi(view, NativeLib.CString(meleeDir)) != 0;

        /// Registers Mario's skins on the body (Mario default, then Luigi) and starts the first-run
        /// Portrait job that renders Luigi and hands the texture back as the skin's icon. Mario's
        /// own portrait (the body's) is requested elsewhere, unchanged.
        public static void Register(GameObject body)
        {
            var model = body.GetComponent<ModelLocator>().modelTransform;
            var controller = model.GetComponent<ModelSkinController>();
            if (!controller) controller = model.gameObject.AddComponent<ModelSkinController>();
            var character = model.GetComponent<CharacterModel>();
            // Mario always replaces Commando's placeholder; Luigi joins only when his cache is there.
            var mario = Def("MarioSkinDefault", "MARIOTEST_SKIN_DEFAULT", model, character, Icon(new Color(0.9f, 0.1f, 0.1f)));
            if (!ready)
            {
                controller.skins = new[] { mario };
                return;
            }
            luigiSkin = Def("MarioSkinLuigi", "MARIOTEST_SKIN_LUIGI", model, character, null);
            controller.skins = new[] { mario, luigiSkin };
            // The Luigi portrait: Mario's framing and pose (the same sim state), the skin's icon.
            Portrait.Request(body, MarioDisplay.GetLuigi, tex =>
            {
                LuigiPortrait = tex;
                luigiSkin.icon = Sprite(tex);
            }, framing: PortraitFraming.ThreeQuarter);
        }

        static SkinDef Def(string name, string nameToken, Transform root, CharacterModel character, Sprite icon)
        {
            var skin = ScriptableObject.CreateInstance<SkinDef>();
            skin.name = name;
            skin.nameToken = nameToken;
            skin.rootObject = root.gameObject;
            skin.baseSkins = Array.Empty<SkinDef>();
            skin.icon = icon;
            var p = ScriptableObject.CreateInstance<SkinDefParams>();
            p.name = name + "Params";
            p.rendererInfos = character.baseRendererInfos;
            p.gameObjectActivations = Array.Empty<SkinDefParams.GameObjectActivation>();
            p.meshReplacements = Array.Empty<SkinDefParams.MeshReplacement>();
            p.projectileGhostReplacements = Array.Empty<SkinDefParams.ProjectileGhostReplacement>();
            p.minionSkinReplacements = Array.Empty<SkinDefParams.MinionSkinReplacement>();
            p.lightReplacements = Array.Empty<CharacterModel.LightInfo>();
            skin.skinDefParams = p;
            skin.optimizedSkinDefParams = p;
            return skin;
        }

        static Sprite Sprite(Texture2D texture) =>
            UnityEngine.Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));

        static Sprite Icon(Color color)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var pixels = new Color32[64];
            var suit = (Color32)color;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pixels[y * 8 + x] = suit;
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, true);
            return Sprite(texture);
        }

        public static void Tokens(Language language)
        {
            language.SetStringByToken("MARIOTEST_SKIN_DEFAULT", "Mario");
            language.SetStringByToken("MARIOTEST_SKIN_LUIGI", "Luigi");
        }
    }
}
