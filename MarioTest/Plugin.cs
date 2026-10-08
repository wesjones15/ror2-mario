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
        internal static ConfigEntry<bool> TraceJumps;
        internal static ConfigEntry<bool> DrawModel;
        internal static ConfigEntry<bool> Sound;
        internal static ConfigEntry<float> SoundVolume;
        internal static ConfigEntry<float> JumpHeight;
        internal static ConfigEntry<float> JumpBuffer;
        internal static ConfigEntry<float> ChainGrace;
        internal static ConfigEntry<float> Height;
        internal static ConfigEntry<bool> SpeedFromItems;
        internal static ConfigEntry<bool> SolidEnemies;
        internal static ConfigEntry<bool> CycleNozzles;
        internal static ConfigEntry<float> SprayReach;
        internal static ConfigEntry<float> StompBounce;
        internal static ConfigEntry<float> RocketHeight;
        internal static ConfigEntry<float> PoundJump;
        internal static ConfigEntry<float> StompJump;
        internal static ConfigEntry<float> ShortHop;
        internal static ConfigEntry<float> PoundPerMetre;
        internal static ConfigEntry<bool> SuperPoundIgnites;
        internal static ConfigEntry<bool> PoundFlames;
        internal static ConfigEntry<float> StompDamage;
        internal static ConfigEntry<float> PoundDamage;
        internal static ConfigEntry<float> ShockDamage;
        internal static ConfigEntry<float> PoundRadius;
        internal static ConfigEntry<float> DiveDamage;
        internal static ConfigEntry<float> WaterDamage;
        internal static ConfigEntry<float> WaterProc;

        void Awake()
        {
            Log = Logger;
            CommonLog.Log = Logger;
            string here = Path.GetDirectoryName(Info.Location);

            var assets = Config.Bind("Paths", "AssetsDir", "",
                "libsms's assets/ folder (extracted from your own Super Mario Sunshine disc). " +
                "Empty: read assets_dir.txt next to the plugin, then $SMS_ASSETS.");
            Height = Config.Bind("Look", "Height", 0f,
                "Mario's height in metres; everything he does scales with it (his speed, jumps " +
                "and FLUDD's reach). 0: as tall as Commando.");
            JumpHeight = Config.Bind("Sim", "JumpHeight", 1.6f,
                "Jump heights times this. 1 is Sunshine's (a standing jump is 2.3 m, a triple 8 m, " +
                "at 0.01 m per unit); RoR2's levels are built for higher jumps.");
            JumpBuffer = Config.Bind("Sim", "JumpBuffer", 0.1f,
                "Seconds a jump press just before landing is remembered, so it still jumps (and " +
                "chains into the double and triple jump). 0 is Sunshine's: the press is lost.");
            ChainGrace = Config.Bind("Sim", "ChainGrace", 0.1f,
                "Extra seconds after Sunshine's landing window (0.13 s) in which a jump still " +
                "chains into the double or triple jump. 0 is Sunshine's.");
            SpeedFromItems = Config.Bind("Sim", "SpeedFromItems", true,
                "Movement speed items and buffs (Paul's Goat Hoof, Energy Drink...) make Mario cover " +
                "more ground, as they speed up other survivors.");
            SolidEnemies = Config.Bind("Sim", "SolidEnemies", true,
                "Enemies are solid to Mario: he can't walk into them, and running into one at a " +
                "dash knocks him down. Off is Sunshine's, where most enemies just hurt him.");
            CycleNozzles = Config.Bind("Fludd", "CycleNozzles", true,
                "Switch nozzle steps through Spray, Hover, Rocket and Turbo. Off is Sunshine's: " +
                "Spray and Hover.");
            StompBounce = Config.Bind("Sim", "StompBounce", 2f,
                "Bouncing off an enemy's head goes this many times as high as in Sunshine (on top " +
                "of JumpHeight). Three stomps in a row climb higher each time.");
            PoundJump = Config.Bind("Sim", "PoundJump", 0.75f,
                "Jump as a ground pound lands to leap back up this share of the height it fell " +
                "from (never lower than a triple jump). 0 is Sunshine's: no pound jump.");
            StompJump = Config.Bind("Sim", "StompJump", 2f,
                "Press jump just before landing on an enemy, or just after (an eighth of a second " +
                "either way), and the bounce goes this many times as high. 1 is Sunshine's.");
            ShortHop = Config.Bind("Sim", "ShortHop", 0.4f,
                "Letting go of jump early only cuts the jump once it has risen this share of a " +
                "full one (0 to 1): 0.4 makes a tap about two thirds of a full jump. 0 is " +
                "Sunshine's, where a tap is about two fifths.");
            RocketHeight = Config.Bind("Fludd", "RocketHeight", 2f,
                "The Rocket nozzle launches Mario about this many times as high as in Sunshine " +
                "(1: about 17 Marios).");
            SprayReach = Config.Bind("Fludd", "SprayReach", 2f,
                "The Spray nozzle's water flies about this many times as far as in Sunshine " +
                "(1: about 6 Marios).");
            StompDamage = Config.Bind("Damage", "Stomp", 4f,
                "Landing on an enemy, as a multiple of Mario's damage (4 = 400%).");
            PoundDamage = Config.Bind("Damage", "GroundPound", 6f,
                "Ground pounding onto an enemy.");
            ShockDamage = Config.Bind("Damage", "PoundShockWave", 3f,
                "A ground pound's landing, on every enemy within PoundRadius.");
            PoundRadius = Config.Bind("Damage", "PoundRadius", 4f,
                "Metres a ground pound's landing reaches (half again for a super pound). 0: none.");
            PoundPerMetre = Config.Bind("Damage", "PoundPerMetre", 0.1f,
                "Ground pound damage (on the enemy pounded and the shock wave) grows by this " +
                "share for each metre fallen: 0.1 makes a 10 m pound do double. 0: flat.");
            SuperPoundIgnites = Config.Bind("Damage", "SuperPoundIgnites", true,
                "A super ground pound (one that fell long enough to burst into flames, about 7 m) " +
                "sets what it hits on fire.");
            DiveDamage = Config.Bind("Damage", "Dive", 2.5f,
                "Diving or belly sliding into an enemy.");
            WaterDamage = Config.Bind("Damage", "WaterPerDrop", 0.035f,
                "Each FLUDD drop that hits (not Sunshine's: its water mostly only stuns). The Spray " +
                "fires about 93 a second, so 0.035 is about 330% a second on one enemy.");
            WaterProc = Config.Bind("Damage", "WaterProcCoefficient", 0.1f,
                "On-hit item chance for each enemy's drops in a 30 Hz water step (about 30 a second).");
            TraceJumps = Config.Bind("Debug", "TraceJumps", false,
                "For a moment after each jump, log every rendered frame: frame time, physics steps, " +
                "where Mario and the camera are, and Mario's height on screen.");
            LogStatus = Config.Bind("Debug", "LogStatus", true,
                "Log each change of Mario's status (Wait, Running, Jump...) to the BepInEx log.");
            PoundFlames = Config.Bind("Look", "PoundFlames", true,
                "A ground pound that falls long enough to turn super (about 7 m) bursts into " +
                "Sunshine's own flames (from the disc), and lands with a fiery blast.");
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

            SetScale(commando);
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
            if (DrawModel.Value)
            {
                DisplayModel.Attach(display, Guid, MarioDisplay.Get);
                Portrait.Request(body, MarioDisplay.Get);
            }

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

        /// Metres per Sunshine unit, from Look.Height or Commando's collision capsule.
        static void SetScale(GameObject commando)
        {
            float height = Height.Value;
            string from = "Look.Height";
            if (height <= 0f)
            {
                var capsule = commando.GetComponent<CapsuleCollider>();
                height = capsule ? capsule.height * Mathf.Abs(commando.transform.localScale.y) : 1.8f;
                from = capsule ? "Commando's capsule" : "a default";
            }
            UnityHost.Scale = Mathf.Clamp(height, 0.2f, 20f) / UnityHost.MarioHeight;
            Log.LogInfo($"Mario is {height:F2} m tall ({from}): {UnityHost.Scale:F5} m per unit");
        }

        static void AddTokens()
        {
            var lang = Language.currentLanguage;
            if (lang == null) return;
            lang.SetStringByToken("MARIOTEST_BODY_NAME", "Mario (test)");
            lang.SetStringByToken("MARIOTEST_BODY_SUBTITLE", "Super Mario Sunshine movement");
            lang.SetStringByToken("MARIOTEST_BODY_DESCRIPTION",
                "Super Mario Sunshine's Mario, moved by libsms.\n\n" +
                "< ! > Pad: A jump, B dive, right trigger spray (all the way in to plant), left trigger " +
                "ground pound and crouch, right bumper switch nozzle.\n" +
                "< ! > Keys: jump, primary spray, sprint plant and spray, secondary dive, utility " +
                "ground pound, special switch nozzle.");
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
