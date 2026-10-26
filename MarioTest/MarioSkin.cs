using System;
using System.Collections.Generic;
using System.IO;
using RoR2;
using Ror2Common;
using UnityEngine;
using Path = System.IO.Path;

namespace MarioTest
{
    /// One alt-skin of Mario: everything that differs from Mario himself. The sim, moves and
    /// FLUDD are always Mario's; a skin swaps the body (libsms-present's `Skin`, whose id is
    /// `Id`), and the host adds a name, a portrait, a voice and small tuning. Adding a skin is a
    /// row in `MarioSkin.Specs` plus a native `Skin` row.
    internal sealed class SkinSpec
    {
        /// The native skin id (`libsms_present::view::Skin::id`).
        public uint Id;
        /// The SkinDef name, the language token and its text, the pause sheet's name and quip.
        public string Name, Token, Text, Title, Quip;
        /// Needs the Melee plugin's cache (Luigi's HSD mesh); otherwise only the Sunshine assets.
        public bool NeedsMelee;
        /// Coins held at one time that unlock it; 0 means it is there from the start.
        public uint UnlockCoins;
        public string UnlockHint, UnlockedText;
        /// Voice pitch ratio with the line's length kept; 1 leaves Mario's voice alone.
        public float VoicePitch = 1f;
        /// Jump height times, on top of Mario's (read each step, so a config change applies).
        public Func<float> JumpScale = () => 1f;
        /// Speak only the skin's own voice, like the game (silent where it has none); the
        /// default falls back to Mario's lines. Only Piantissimo has voice swaps to be strict about.
        public Func<bool> StrictVoice = () => false;
        /// The hidden dpad-up move (Yoshi's Egg Lay, borrowed from the Melee plugin).
        public bool EggLay;

        public SkinDef Def;
        public UnlockableDef Unlock;
        /// The rendered portrait, for the skin's icon and the pause sheet's face.
        public Texture2D Portrait;
        /// In the loadout: its files were there when the body was registered.
        public bool Registered;
    }

    // Alt-skins keep every one of Mario's moves and his FLUDD but draw another body (the native
    // view swaps it; the SkinDef only names it, gives it an icon and lets the loadout select
    // it). Luigi's mesh comes from the Melee cache and is registered only when that cache is
    // complete; Piantissimo's from the Sunshine disc the plugin already extracts.
    internal static class MarioSkin
    {
        public const uint Mario = 0, Luigi = 1, Piantissimo = 2;
        /// Luigi's key, the plaque's number: coins held at one time (see CountCoins).
        public const uint LuigiCoins = 2401;
        /// Luigi's voice is Mario's pitched up 14% with the line's length kept (the Melee trick).
        public const float VoicePitch = 1.14f;

        /// Every skin, indexed by native id. Mario's row only carries his own name and quip.
        public static readonly SkinSpec[] Specs =
        {
            new SkinSpec
            {
                Id = Mario, Name = "MarioSkinDefault", Token = "MARIOTEST_SKIN_DEFAULT", Text = "Mario",
                Quip = "Hero of the Mushroom Kingdom... On Vacation!", Registered = true,
            },
            new SkinSpec
            {
                Id = Luigi, Name = "MarioSkinLuigi", Token = "MARIOTEST_SKIN_LUIGI", Text = "Luigi", Title = "Luigi",
                Quip = "Second Most Famous Mario Brother... On Vacation!",
                NeedsMelee = true, UnlockCoins = LuigiCoins,
                // Luigi's key, as the plaque says: 2401 coins held at once.
                UnlockHint = "L is real 2401", UnlockedText = "Luigi unlocked.",
                VoicePitch = VoicePitch, JumpScale = () => Plugin.LuigiJumpHeight.Value, EggLay = true,
            },
            new SkinSpec
            {
                Id = Piantissimo, Name = "MarioSkinPiantissimo", Token = "MARIOTEST_SKIN_PIANTISSIMO",
                Text = "Piantissimo", Title = "Piantissimo",
                Quip = "Fastest Pianta on Isle Delfino... On Vacation!",
                // His jump voice replaces Mario's jump cues natively (the skin picks the voice set).
                StrictVoice = () => Plugin.PiantissimoGameVoice.Value,
                // Available from the start: his model comes from the disc the plugin extracts.
            },
        };

        static string meleeDir;
        static bool ready;
        /// The skins in the loadout, in skin-index order (Mario first, then each registered one).
        static readonly List<SkinSpec> loadout = new List<SkinSpec>();

        /// Whether the Melee cache (Luigi's mesh) was found.
        public static bool Ready => ready;
        public static string MeleeDir => meleeDir;

        public static SkinSpec Spec(uint id) => id < Specs.Length ? Specs[id] : Specs[Mario];

        /// The rendered Luigi portrait, for the pause sheet's face.
        public static Texture2D LuigiPortrait => Specs[Luigi].Portrait;

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

        /// Whether this skin can be offered right now.
        static bool Offered(SkinSpec spec) => spec.Id != Mario && (!spec.NeedsMelee || ready);

        /// The native skin id for a loadout skin index (Mario for anything unregistered).
        static uint FromIndex(uint index) => index < loadout.Count ? loadout[(int)index].Id : Mario;

        /// The skin the body's loadout selected (a native skin id; Mario when none).
        public static uint Selected(CharacterBody body)
        {
            if (loadout.Count < 2 || !body) return Mario;
            // During pod creation the body's SkinDef may not have applied yet. The owning
            // master's per-body loadout already contains the selected skin, including remotely.
            uint skin = body.skinIndex;
            if (body.master && body.master.loadout != null && body.bodyIndex != BodyIndex.None)
                skin = Math.Max(skin, body.master.loadout.bodyLoadoutManager.GetSkinIndex(body.bodyIndex));
            return FromIndex(skin);
        }

        /// The skin the loadout has selected for Mario's body, for scenes with no live body yet
        /// (the character-select display).
        public static uint Selected()
        {
            if (loadout.Count < 2) return Mario;
            var index = BodyCatalog.FindBodyIndex("MarioTestBody");
            if (index == BodyIndex.None) return Mario;
            foreach (var user in LocalUserManager.readOnlyLocalUsersList)
                if (user.userProfile != null)
                    return FromIndex(user.userProfile.loadout.bodyLoadoutManager.GetSkinIndex(index));
            return Mario;
        }

        /// The selected skin's row (Mario's when none).
        public static SkinSpec Current(CharacterBody body) => Spec(Selected(body));

        /// Loads `skin`'s body into an already-open view (Mario's is always there); false when
        /// its files are missing, in which case the view keeps drawing Mario. Load every skin a
        /// view will show before its model table is read (the table is fixed at creation).
        public static bool Load(IntPtr view, uint skin)
        {
            if (skin == Mario) return true;
            var spec = Spec(skin);
            if (!spec.Registered) return false;
            return Native.sms_view_load_skin(view, skin, spec.NeedsMelee ? NativeLib.CString(meleeDir) : null) != 0;
        }

        /// Loads every registered skin (a view whose rider is chosen after it is built).
        /// Returns a bit per loaded skin id.
        public static uint LoadAll(IntPtr view)
        {
            uint loaded = 1u << (int)Mario;
            foreach (var spec in Specs)
                if (spec.Id != Mario && Load(view, spec.Id)) loaded |= 1u << (int)spec.Id;
            return loaded;
        }

        /// Draws `skin` on a view that loaded it, with its voice.
        public static void Apply(IntPtr view, uint skin)
        {
            var spec = Spec(skin);
            Native.sms_view_skin(view, skin);
            Native.sms_view_set_voice_pitch(view, spec.VoicePitch);
            if (spec.StrictVoice()) Native.sms_view_voice_set(view, 2);
        }

        /// Registers Mario's skins on the body (Mario default, then each offered one) and starts
        /// the first-run Portrait jobs that render them and hand the textures back as the skins'
        /// icons. Mario's own portrait (the body's) is requested elsewhere, unchanged.
        public static void Register(GameObject body, RoR2.ContentManagement.ContentPack pack)
        {
            var model = body.GetComponent<ModelLocator>().modelTransform;
            var controller = model.GetComponent<ModelSkinController>();
            if (!controller) controller = model.gameObject.AddComponent<ModelSkinController>();
            var character = model.GetComponent<CharacterModel>();
            loadout.Clear();
            var defs = new List<SkinDef>();
            // Mario always replaces Commando's placeholder; the others join only when offered.
            var mario = Specs[Mario];
            mario.Def = Def(mario.Name, mario.Token, model, character, Icon(new Color(0.9f, 0.1f, 0.1f)));
            loadout.Add(mario);
            defs.Add(mario.Def);
            foreach (var spec in Specs)
            {
                if (spec.Id == Mario) continue;
                spec.Registered = Offered(spec);
                if (!spec.Registered) continue;
                spec.Def = Def(spec.Name, spec.Token, model, character, null);
                if (spec.UnlockCoins > 0)
                {
                    // The loadout shows the lock and the hint until MarioDriver sees the coin
                    // counter reach it.
                    var unlock = ScriptableObject.CreateInstance<UnlockableDef>();
                    unlock.cachedName = "Skins.Mario." + spec.Text;
                    unlock.nameToken = spec.Def.nameToken;
                    var hint = spec.UnlockHint;
                    var done = spec.UnlockedText;
                    unlock.getHowToUnlockString = () => hint;
                    unlock.getUnlockedString = () => done;
                    spec.Def.unlockableDef = unlock;
                    spec.Unlock = unlock;
                    pack.unlockableDefs.Add(new[] { unlock });
                }
                loadout.Add(spec);
                defs.Add(spec.Def);
            }
            controller.skins = defs.ToArray();
            foreach (var spec in Specs)
            {
                if (spec.Id == Mario || !spec.Registered) continue;
                var s = spec;
                // The portrait: Mario's framing and pose (the same sim state), the skin's icon.
                Portrait.Request(body, () => MarioDisplay.Get(s.Id), tex =>
                {
                    s.Portrait = tex;
                    s.Def.icon = Sprite(tex);
                }, framing: PortraitFraming.ThreeQuarter);
            }
        }

        /// A skin's coin key: at least that many coins held at one time (the HUD's gold; Luigi's
        /// is 2401, as the plaque says). Not a running total: spending the coins back down is
        /// fine, but the counter itself has to reach it while playing Mario.
        public static void CountCoins(CharacterBody body)
        {
            if (!body || !body.master || !Run.instance) return;
            foreach (var spec in Specs)
                if (spec.Unlock && spec.UnlockCoins > 0 && body.master.money >= spec.UnlockCoins)
                    Grant(body, spec);
        }

        /// Grants a skin to the local player who owns `body`, once its coin counter holds the
        /// skin's key (MarioDriver checks it each step). Safe to call every step: an owned unlock
        /// is a no-op, so the log line happens once.
        public static void Grant(CharacterBody body, SkinSpec spec)
        {
            if (!spec.Unlock) return;
            foreach (var user in LocalUserManager.readOnlyLocalUsersList)
            {
                if (user.userProfile == null) continue;
                if (body && user.cachedBody && user.cachedBody != body) continue;
                if (user.userProfile.HasUnlockable(spec.Unlock)) continue;
                user.userProfile.GrantUnlockable(spec.Unlock);
                user.userProfile.RequestEventualSave();
                Plugin.Log.LogInfo($"{spec.Text} unlocked: {spec.UnlockCoins} coins held at one time");
            }
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
            foreach (var spec in Specs) language.SetStringByToken(spec.Token, spec.Text);
        }
    }
}
