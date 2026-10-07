using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EntityStates;
using Ror2Common;
using RoR2;
using UnityEngine;
using Path = System.IO.Path;

namespace MarioTest
{
    // A test survivor for libsms's Mario in Risk of Rain 2: Commando's body and skills, with
    // the body moved by the sim instead of RoR2's character controller, and drawn and heard
    // as the disc's Mario. No R2API: content goes in through RoR2's own content-pack hook
    // (ror2-common's ContentProvider).
    [BepInPlugin(Guid, "Mario (test)", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.libsms.mariotest";

        internal static ManualLogSource Log;
        internal static string AssetsDir;
        internal static ConfigEntry<bool> LogStatus;
        internal static ConfigEntry<bool> DrawModel;
        internal static ConfigEntry<bool> Sound;
        internal static ConfigEntry<float> SoundVolume;
        internal static ConfigEntry<float> JumpHeight;
        internal static ConfigEntry<float> JumpBuffer;
        internal static ConfigEntry<float> ChainGrace;

        void Awake()
        {
            Log = Logger;
            CommonLog.Log = Logger;
            string here = Path.GetDirectoryName(Info.Location);

            var assets = Config.Bind("Paths", "AssetsDir", "",
                "libsms's assets/ folder (extracted from your own Super Mario Sunshine disc). " +
                "Empty: read assets_dir.txt next to the plugin, then $SMS_ASSETS.");
            UnityHost.Scale = Config.Bind("Sim", "MetresPerUnit", 0.01f,
                "Metres per Sunshine unit. 0.01 makes Mario 1.6 m tall.").Value;
            JumpHeight = Config.Bind("Sim", "JumpHeight", 1.6f,
                "Jump heights times this. 1 is Sunshine's (a standing jump is 2.3 m, a triple 8 m, " +
                "at 0.01 m per unit); RoR2's levels are built for higher jumps.");
            JumpBuffer = Config.Bind("Sim", "JumpBuffer", 0.1f,
                "Seconds a jump press just before landing is remembered, so it still jumps (and " +
                "chains into the double and triple jump). 0 is Sunshine's: the press is lost.");
            ChainGrace = Config.Bind("Sim", "ChainGrace", 0.1f,
                "Extra seconds after Sunshine's landing window (0.13 s) in which a jump still " +
                "chains into the double or triple jump. 0 is Sunshine's.");
            LogStatus = Config.Bind("Debug", "LogStatus", true,
                "Log each change of Mario's status (Wait, Running, Jump...) to the BepInEx log.");
            DrawModel = Config.Bind("Look", "DrawModel", true,
                "Draw Mario with the disc's models (off: Commando's model).");
            Sound = Config.Bind("Sound", "Enabled", true,
                "Play Mario's sounds from the disc (voice, footsteps, FLUDD) on the default output device.");
            SoundVolume = Config.Bind("Sound", "Volume", 1f,
                "Mario's sounds, on top of RoR2's master and SFX volume (0-2).");
            AssetsDir = ResolveAssets(assets.Value, here);

            if (!Native.Load(Path.Combine(here, "sms.dll"), out string error))
            {
                Log.LogError(error);
                return;
            }
            if (AssetsDir == null || !Directory.Exists(Path.Combine(AssetsDir, "params_mario")))
            {
                Log.LogError($"No libsms assets at '{AssetsDir}' (need params_mario/). " +
                             "Set Paths.AssetsDir in BepInEx/config/" + Guid + ".cfg.");
                return;
            }
            Log.LogInfo($"libsms ABI {Native.sms_abi_version()}, assets {AssetsDir}");

            ContentProvider.Register(Guid, AddContent);
            Language.onCurrentLanguageChanged += AddTokens;
        }

        static string ResolveAssets(string configured, string here)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            string file = Path.Combine(here, "assets_dir.txt");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            return Environment.GetEnvironmentVariable("SMS_ASSETS");
        }

        static void AddContent(RoR2.ContentManagement.ContentPack pack)
        {
            var commando = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterBodies/CommandoBody");
            var commandoDisplay = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterDisplays/CommandoDisplay");
            if (!commando || !commandoDisplay)
            {
                Log.LogError($"Commando not found (body {(bool)commando}, display {(bool)commandoDisplay})");
                return;
            }

            var body = Prefabs.Clone(commando, "MarioTestBody", Guid);
            var cb = body.GetComponent<CharacterBody>();
            cb.baseNameToken = "MARIOTEST_BODY_NAME";
            cb.subtitleNameToken = "MARIOTEST_BODY_SUBTITLE";
            cb.bodyColor = new Color(0.9f, 0.1f, 0.1f);
            foreach (var esm in body.GetComponents<EntityStateMachine>())
                if (esm.customName == "Body")
                    esm.mainStateType = new SerializableEntityStateType(typeof(MarioMain));
            body.AddComponent<MarioDriver>();

            var display = Prefabs.Clone(commandoDisplay, "MarioTestDisplay", Guid);

            var survivor = ScriptableObject.CreateInstance<SurvivorDef>();
            survivor.cachedName = "MarioTest";
            survivor.bodyPrefab = body;
            survivor.displayPrefab = display;
            survivor.displayNameToken = "MARIOTEST_BODY_NAME";
            survivor.descriptionToken = "MARIOTEST_BODY_DESCRIPTION";
            survivor.outroFlavorToken = "MARIOTEST_BODY_OUTRO";
            survivor.mainEndingEscapeFailureFlavorToken = "MARIOTEST_BODY_FAIL";
            survivor.primaryColor = cb.bodyColor;
            survivor.desiredSortPosition = 100f;

            pack.bodyPrefabs.Add(new[] { body });
            pack.survivorDefs.Add(new[] { survivor });
            pack.entityStateTypes.Add(new[] { typeof(MarioMain) });
            Log.LogInfo("Mario (test) registered");
        }

        static void AddTokens()
        {
            var lang = Language.currentLanguage;
            if (lang == null) return;
            lang.SetStringByToken("MARIOTEST_BODY_NAME", "Mario (test)");
            lang.SetStringByToken("MARIOTEST_BODY_SUBTITLE", "Super Mario Sunshine movement");
            lang.SetStringByToken("MARIOTEST_BODY_DESCRIPTION",
                "Super Mario Sunshine's Mario, moved by libsms.\n\n" +
                "< ! > Jump: A. Primary: spray. Secondary: B (dive, punch).\n" +
                "< ! > Utility: L (ground pound, crouch). Sprint: plant and spray. Special: switch nozzle.");
            lang.SetStringByToken("MARIOTEST_BODY_OUTRO", "..and so he left, still wet.");
            lang.SetStringByToken("MARIOTEST_BODY_FAIL", "..and so he vanished, mid-triple-jump.");
        }
    }

    // The body's main state: RoR2's animation bookkeeping (BaseCharacterMain) without
    // GenericCharacterMain's movement, jumping and skill use, which belong to the sim now.
    public class MarioMain : BaseCharacterMain
    {
    }
}
