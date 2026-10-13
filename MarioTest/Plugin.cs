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
        internal static ConfigEntry<bool> Effects;
        internal static ConfigEntry<float> SmokeSpeed;
        internal static ConfigEntry<float> StompDamage;
        internal static ConfigEntry<float> PoundDamage;
        internal static ConfigEntry<float> ShockDamage;
        internal static ConfigEntry<float> PoundRadius;
        internal static ConfigEntry<float> DiveDamage;
        internal static ConfigEntry<float> WaterDamage;
        internal static ConfigEntry<bool> FallDamageEnabled;
        internal static ConfigEntry<float> FallSafeHeight, DiveSafeHeight, FallPerHeight, DiveFallMultiplier;
        internal static ConfigEntry<float> BurstMultiplier;
        internal static ConfigEntry<float> WaterProc;
        internal static ConfigEntry<bool> UnlimitedWater;
        internal static ConfigEntry<bool> DpadRefill;
        internal static ConfigEntry<bool> ShowTank;
        internal static ConfigEntry<float> TankX;
        internal static ConfigEntry<float> TankY;
        internal static ConfigEntry<float> TankScale;
        internal static ConfigEntry<bool> CooldownBoosts;
        internal static ConfigEntry<float> CooldownExponent;
        internal static ConfigEntry<bool> AirUses;
        internal static ConfigEntry<float> AirUseRecharge;
        internal static ConfigEntry<float> PoundRecovery;
        internal static ConfigEntry<float> WeakPound;
        internal static ConfigEntry<bool> WetSlideCharges;
        internal static ConfigEntry<bool> WetSlideAnywhere;
        internal static ConfigEntry<float> WetSlideRecharge;
        internal static ConfigEntry<float> WetSlideSeconds;
        internal static ConfigEntry<float> SpraySeconds;
        internal static ConfigEntry<bool> SprayRateFromItems;
        internal static ConfigEntry<float> HealToWater;
        internal static ConfigEntry<float> LifestealToWater;
        internal static ConfigEntry<float> LifestealWaterCap;
        internal static ConfigEntry<float> RegenToWater;
        internal static ConfigEntry<float> ShurikenInterval;
        internal static ConfigEntry<float> QuailScale;
        internal static ConfigEntry<bool> ExternalForces;
        internal static ConfigEntry<bool> CrowdControl;
        internal static ConfigEntry<bool> CameraAim;
        internal static ConfigEntry<bool> SprayBursts;
        internal static ConfigEntry<float> TriggerCurve;
        internal static ConfigEntry<float> LightTapSeconds;
        internal static ConfigEntry<bool> WetSlideGlide;
        internal static ConfigEntry<bool> WetSlideOnWater;
        internal static ConfigEntry<bool> DeathFall;
        internal static ConfigEntry<float> DeathFallSeconds;
        internal static ConfigEntry<bool> ShadowClones;
        internal static ConfigEntry<float> WaterOpacity;
        internal static ConfigEntry<float> WaterRefraction;

        void Awake()
        {
            Log = Logger;
            CommonLog.Log = Logger;
            string here = Path.GetDirectoryName(Info.Location);
            SpawnMusic.Initialize(Config, here);
            RunAudio.Initialize(() =>
            {
                MarioDriver.SettleTheDead();
                MarioView.Reset();
            });

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
            UnlimitedWater = Config.Bind("Fludd", "UnlimitedWater", false,
                "FLUDD's tank never runs dry. Off is Sunshine's: it empties as it sprays and fills " +
                "back up when you hold spray standing in water, and on each new stage.");
            DpadRefill = Config.Bind("Fludd", "DpadRefill", true,
                "D-pad down fills FLUDD's tank, for stages without water to stand in.");
            ShowTank = Config.Bind("Hud", "WaterTank", true,
                "Show Sunshine's water tank gauge (from the disc) while Mario has FLUDD.");
            TankX = Config.Bind("Hud", "TankX", 0.95f,
                "Where the tank's middle is across the screen, 0 left to 1 right.");
            TankY = Config.Bind("Hud", "TankY", 0.6f,
                "Where the tank's middle is down the screen, 0 top to 1 bottom (Sunshine has it in " +
                "the bottom right, where RoR2 puts its skills).");
            TankScale = Config.Bind("Hud", "TankScale", 0.8f,
                "The tank's size; 1 is Sunshine's against the screen's height.");
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
            FallDamageEnabled = Config.Bind("FallDamage", "Enabled", true,
                "Take RoR2 fall damage after very long drops. Ground pounds are always safe.");
            FallSafeHeight = Config.Bind("FallDamage", "SafeHeight", 40f,
                "Safe drop in Mario body heights (160 sim units). Above the default rocket's apex (~34 heights).");
            DiveSafeHeight = Config.Bind("FallDamage", "DiveSafeHeight", 80f,
                "A dive landing fully protects drops up to this many Mario heights.");
            FallPerHeight = Config.Bind("FallDamage", "FractionPerHeight", 0.0125f,
                "Fraction of maximum health per height beyond the safe drop, capped at 0.9 before dive reduction.");
            DiveFallMultiplier = Config.Bind("FallDamage", "DiveMultiplier", 0.5f,
                "Damage multiplier beyond the dive's safe height (0 to 1).");
            BurstMultiplier = Config.Bind("Damage", "BurstMultiplier", 3f,
                "A light press's short burst of the Spray (Fludd.SprayBursts) doesn't last, so each " +
                "of its drops does this many times WaterPerDrop. A burst is about 28 drops, so 3 is " +
                "about 290% a burst if it all hits.");
            WaterProc = Config.Bind("Items", "WaterProcCoefficient", 0.5f,
                "FLUDD's water procs on-hit items at most once a second on each enemy (every nozzle " +
                "shares it), at this proc coefficient; the drops in between do damage only.");
            ShurikenInterval = Config.Bind("Items", "ShurikenInterval", 0.5f,
                "Water hitting counts as using the primary skill (Shuriken throws) at most this " +
                "often, in seconds.");
            CooldownBoosts = Config.Bind("Items", "CooldownBoosts", true,
                "Cooldown items (Alien Head, Purity; Lysate Cell for the slide) make FLUDD hover " +
                "and spray longer, rocket higher and turbo faster, and the wet slide glide longer.");
            CooldownExponent = Config.Bind("Items", "CooldownExponent", 0.5f,
                "Those boosts are 1 / (cooldown ratio ^ this): with 0.5, one Alien Head is x1.15, " +
                "four x1.8, eight x3.2.");
            AirUses = Config.Bind("Items", "AirUses", true,
                "One FLUDD use per time in the air (each start of a nozzle, however long it runs); " +
                "Backup Magazine adds more. Off: as many as the tank allows (Sunshine's).");
            AirUseRecharge = Config.Bind("Items", "AirUseRecharge", 4f,
                "Seconds for each extra mid-air use (the secondary skill's charges) to come back, " +
                "one at a time, starting once Mario has touched the ground.");
            PoundRecovery = Config.Bind("Items", "PoundRecovery", 6f,
                "Seconds for a full-strength ground pound (the utility skill's charge) to come back.");
            WeakPound = Config.Bind("Items", "WeakPound", 0.5f,
                "A ground pound without a charge does this share of its damage.");
            WetSlideCharges = Config.Bind("Items", "WetSlideCharges", true,
                "A wet belly slide on dry ground takes a charge (the special skill); without one the " +
                "slide is plain. Off: Sunshine's (only a slide over FLUDD's water glides).");
            WetSlideOnWater = Config.Bind("Items", "WetSlideOnWater", true,
                "A belly slide over water FLUDD sprayed glides for free, as in Sunshine (a light " +
                "press of spray drops some just ahead). Off: that takes a charge too.");
            WetSlideGlide = Config.Bind("Items", "WetSlideGlide", true,
                "A wet slide keeps its speed as if the stick were held forward (pull back to brake). " +
                "Off: Sunshine's, where it only carries on while you hold forward.");
            WetSlideAnywhere = Config.Bind("Items", "WetSlideAnywhere", true,
                "With a charge, any belly slide glides, not only one over water FLUDD sprayed.");
            WetSlideRecharge = Config.Bind("Items", "WetSlideRecharge", 10f,
                "Seconds for a wet slide charge to come back, counted from the slide's start.");
            WetSlideSeconds = Config.Bind("Items", "WetSlideSeconds", 1.5f,
                "Seconds a wet slide glides before turning plain (cooldown items stretch it). 0: " +
                "as long as it lasts.");
            SpraySeconds = Config.Bind("Items", "SpraySeconds", 0f,
                "A hard cut: seconds the Spray fires before the trigger must be let go (cooldown " +
                "items stretch it). 0: none (with Fludd.SprayBursts a held press already fades out " +
                "after about 5 s, as in Sunshine, and cooldown items stretch that).");
            SprayRateFromItems = Config.Bind("Items", "SprayRateFromItems", true,
                "Attack speed (Soldier's Syringe, Mocha) fires the Spray faster for the same water.");
            HealToWater = Config.Bind("Items", "HealToWater", 1f,
                "Healing fills FLUDD's tank: this share of the tank per max health healed, as the " +
                "heal asks (Bustling Fungus refills even at full health).");
            RegenToWater = Config.Bind("Items", "RegenToWater", 1f,
                "Regeneration beyond Mario's own (Cautious Slug and the like) fills the tank at this " +
                "share of the tank per max health regenerated.");
            LifestealToWater = Config.Bind("Items", "LifestealToWater", 0.25f,
                "Lifesteal (Leeching Seed, Harvester's Scythe) fills the tank at this share instead.");
            LifestealWaterCap = Config.Bind("Items", "LifestealWaterCap", 0.05f,
                "At most this share of the tank a second from lifesteal.");
            QuailScale = Config.Bind("Items", "WaxQuail", 1f,
                "Wax Quail's boost to a sprinting jump, times this (1: RoR2's).");
            SprayBursts = Config.Bind("Fludd", "SprayBursts", true,
                "Sunshine's spray pressure: each press of the trigger is a burst. A light press " +
                "drops a short splash just ahead (handy for wet slides); a full one sprays at full " +
                "reach for about 5 s, then fades until you let go or press deeper. Off: a steady " +
                "stream for as long as the trigger is held.");
            TriggerCurve = Config.Bind("Controls", "TriggerCurve", 2f,
                "The spray trigger's travel is raised to this power, so a gamepad's light press is " +
                "easier to hold (Sunshine's light zone is the first tenth of a GameCube trigger). " +
                "1: linear.");
            LightTapSeconds = Config.Bind("Controls", "LightTapSeconds", 0.2f,
                "On keyboard, a press of the primary key starts as a light press of the trigger: " +
                "tapped, it's a short splash just ahead; held longer than this, a full spray. 0: " +
                "always full.");
            CameraAim = Config.Bind("Controls", "CameraAim", true,
                "Sunshine's L-button camera: while L (the left trigger; the utility key on " +
                "keyboard) is held, FLUDD points where the camera looks, and Mario crouching turns " +
                "with the camera (the stick side-steps).");
            DeathFall = Config.Bind("Look", "DeathFall", true,
                "When Mario dies he falls down as in Sunshine, with his voice, and lies there.");
            DeathFallSeconds = Config.Bind("Look", "DeathFallSeconds", 6f,
                "How long the fallen Mario stays.");
            WaterOpacity = Config.Bind("Look", "WaterOpacity", 0.12f,
                "How much of the water's blue each FLUDD drop lays over what's behind it. Drops " +
                "overlap and add up; the shared glass tint multiplier (0.25) also applies.");
            WaterRefraction = Config.Bind("Look", "WaterRefraction", 1f,
                "FLUDD's water bends what's behind it, as in Sunshine, through RoR2's refraction " +
                "effect: its strength (1: RoR2's own). 0: off (tint only).");
            ShadowClones = Config.Bind("Look", "ShadowClones", true,
                "Marios no player controls (Goobo Jr.'s clone) are drawn as Mario in Shadow " +
                "Mario's colours (from the disc). Off: Commando's model.");
            ExternalForces = Config.Bind("Sim", "ExternalForces", true,
                "RoR2 can throw Mario: jump pads, geysers, knockback, Milky Chrysalis.");
            CrowdControl = Config.Bind("Sim", "CrowdControl", true,
                "Stuns, freezes and shocks stop Mario's controls; roots stop him moving.");
            TraceJumps = Config.Bind("Debug", "TraceJumps", false,
                "For a moment after each jump, log every rendered frame: frame time, physics steps, " +
                "where Mario and the camera are, and Mario's height on screen.");
            LogStatus = Config.Bind("Debug", "LogStatus", true,
                "Log each change of Mario's status (Wait, Running, Jump...) to the BepInEx log.");
            PoundFlames = Config.Bind("Look", "PoundFlames", true,
                "A ground pound that falls long enough to turn super (about 7 m) bursts into " +
                "Sunshine's own flames (from the disc), and lands with a fiery blast.");
            Effects = Config.Bind("Look", "Effects", true,
                "Draw Sunshine's movement dust, bump stars, ground-pound smear, spin-jump swirl, " +
                "belly-slide smoke/spray and stomp bursts from the disc. PoundFlames controls fire separately.");
            SmokeSpeed = Config.Bind("Look", "SmokeSpeed", 2f,
                "Smoke/dust playback speed (0.25-4, 1: Sunshine's original timing). " +
                "2 makes puffs move and clear twice as fast. Other effects keep their original timing.");
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

            ItemHooks.Install();
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
            body.AddComponent<NativeBody>();
            body.AddComponent<MarioDriver>();
            MarioSkills.Add(body, pack);

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

            // For Goobo Jr.'s clone and the like: Commando's AI pressing Mario's buttons.
            SurvivorAi.Register(pack, body, "Prefabs/CharacterMasters/CommandoMonsterMaster", "MarioTestMonsterMaster", Guid);
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
                "ground pound and crouch, right bumper switch nozzle, RoR2's sprint to sprint.\n" +
                "< ! > Keys: jump, primary spray, sprint plant and spray, secondary dive, utility " +
                "ground pound, special switch nozzle.");
            MarioSkills.Tokens(lang);
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
