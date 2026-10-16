using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    [BepInPlugin(Guid, "Mario", "0.2.2.1")]
    // Use the pack's fixes first when installed; no pack APIs are required by this plugin.
    [BepInDependency("___riskofthunder.RoR2BepInExPack", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.libsms.mariotest";

        internal static ManualLogSource Log;
        internal static string AssetsDir;
        static volatile bool assetsBusy, assetsReady;
        bool musicInitialized;
        internal static bool AssetsSettled => !assetsBusy;
        internal static bool AssetsReady => !assetsBusy && assetsReady;
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
        internal static ConfigEntry<bool> CameraAim, LockOn;
        internal static ConfigEntry<bool> SprayBursts;
        internal static ConfigEntry<float> TriggerCurve;
        internal static ConfigEntry<float> LightTapSeconds;
        internal static ConfigEntry<bool> WetSlideGlide;
        internal static ConfigEntry<bool> ShallowWetSlide;
        internal static ConfigEntry<bool> ShowLife, HideHealthBar, LockMarker, LockMarkerHealth;
        internal static ConfigEntry<bool> ShowCoins, ShowScreenEffects, LunarCoinSecret, LunarCoinBlue;
        internal static ConfigEntry<bool> ShineEnabled, ShineCameraOn;
        internal static ConfigEntry<float> ShineHeight, ShineTouchRadius, ShineSeconds;
        internal static ConfigEntry<float> CoinX, CoinY, CoinScale, LivesScale, ScreenSpeed, TooBadDrop;
        internal static ConfigEntry<int> CoinGold;
        internal static ConfigEntry<float> LockRange;
        internal static ConfigEntry<float> LifeX, LifeY, LifeScale, LockMarkerSize, LockMarkerSpin;
        internal static ConfigEntry<bool> KnockdownEnabled, KnockdownGetUpEarly, KnockdownInterruptsPound;
        internal static ConfigEntry<float> KnockdownThreshold;
        internal static ConfigEntry<float> SlideSprayScale;
        internal static ConfigEntry<bool> SlideSplashes;
        internal static ConfigEntry<bool> DeathFall;
        internal static ConfigEntry<float> DeathFallSeconds;
        internal static ConfigEntry<bool> GummyClones;
        internal static ConfigEntry<float> WaterOpacity;
        internal static ConfigEntry<float> SlideWaveTint;
        internal static ConfigEntry<float> WaterRefraction;
        internal static ConfigEntry<float> GroundWetness;

        void Awake()
        {
            Log = Logger;
            GroundWetness = Config.Bind("Look", "GroundWetness", 0.12f,
                new ConfigDescription("How much FLUDD's wet patches darken terrain, fading as they dry. 0 hides the marks; wet-slide eligibility is unchanged.",
                    new AcceptableValueRange<float>(0f, 0.35f)));
            CommonLog.Log = Logger;
            string here = Path.GetDirectoryName(Info.Location);
            RunAudio.Initialize(() =>
            {
                MarioDriver.SettleTheDead();
                MarioView.Reset();
            });

            var assets = Config.Bind("Paths", "AssetsDir", "",
                "libsms's assets/ folder (extracted from your own Super Mario Sunshine disc). " +
                "Empty: read assets_dir.txt next to the plugin, then $SMS_ASSETS, then assets/ next to the plugin.");
            var disc = Config.Bind("Paths", "Disc", "",
                "Your Super Mario Sunshine (GameCube) disc image (.iso/.gcm/.ciso), used once to " +
                "build AssetsDir if missing or incomplete. Empty: check $SMS_DISC, the plugin folder " +
                "and next to AssetsDir. Any region is supported; convert RVZ/WIA/WBFS to ISO first.");
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
            ShowLife = Config.Bind("Hud", "LifeMeter", true,
                "Show Sunshine's life meter (the Shine Sprite sun, from the disc) for Mario's health: " +
                "whole rays of eight, the shield's after them in cyan. RoR2's own values are unchanged.");
            HideHealthBar = Config.Bind("Hud", "HideHealthBar", true,
                "Hide RoR2's health bar (its numbers stay) while the life meter shows.");
            LifeX = Config.Bind("Hud", "LifeX", -1f,
                "Where the life meter's middle is across the screen, 0 left to 1 right. Negative: over the " +
                "left end of RoR2's health bar.");
            LifeY = Config.Bind("Hud", "LifeY", -1f,
                "Where the life meter's middle is down the screen, 0 top to 1 bottom. Negative: at RoR2's health bar.");
            LifeScale = Config.Bind("Hud", "LifeScale", 0.45f,
                "The life meter's size; 1 is Sunshine's against the screen's height.");
            ShowCoins = Config.Bind("Hud", "CoinCounters", true,
                "Show Sunshine's coin counters (from the disc) for RoR2's gold (the yellow coin) and lunar coins " +
                "(the blue coin), and hide RoR2's own while they show. A lunar coin also plays Sunshine's " +
                "blue coin jingle.");
            LunarCoinSecret = Config.Bind("Hud", "LunarCoinSecret", true,
                "A lunar coin plays Sunshine's \"puzzle solved\" chime (MSD_SE_SY_PROBLEM_SOLVED). Off: the blue " +
                "coin's own jingle (MSD_SE_SY_BLUE_COIN_GET), which sounds like a save.");
            LunarCoinBlue = Config.Bind("Hud", "LunarCoinBlueCoin", true,
                "Lunar coins lying in the world show as Sunshine's spinning blue coin (for you alone) instead of RoR2's " +
                "lunar coin model.");
            ShineEnabled = Config.Bind("Shine", "Enabled", true,
                "When a teleporter has charged (its boss dead), a Shine Sprite from your Sunshine disc appears above " +
                "its middle, for you alone. Touching it (walking or jumping into it) is Sunshine's Shine Get: Mario's " +
                "pose, the camera, the fanfare and \"SHINE!\", and the teleporter finishes as if you had used it: " +
                "to the stage the run was going to, to a Lunar Seer's choice, or on the stage before the moon, to the moon.");
            ShineHeight = Config.Bind("Shine", "Height", 1.6f,
                new ConfigDescription("Metres above the teleporter's middle that the Shine hangs.",
                    new AcceptableValueRange<float>(0.2f, 6f)));
            ShineTouchRadius = Config.Bind("Shine", "TouchRadius", 1.5f,
                new ConfigDescription("Metres around the Shine, across the ground, that Mario touches it from. " +
                    "Sunshine's own is about 1.2.", new AcceptableValueRange<float>(0.3f, 6f)));
            ShineSeconds = Config.Bind("Shine", "Seconds", 6.3f,
                new ConfigDescription("Seconds from Mario's pose to the stage ending (Sunshine's demo fades out in about " +
                    "that). The teleporter's own send-off is part of it.", new AcceptableValueRange<float>(3f, 15f)));
            ShineCameraOn = Config.Bind("Shine", "Camera", true,
                "Fly the camera along Sunshine's Shine Get path during the pose.");
            ShowScreenEffects = Config.Bind("Hud", "ScreenEffects", true,
                "Show Sunshine's \"TOO BAD!\" letters (the screen opening from black) when Mario falls off the " +
                "map, and its GAME OVER screen when he dies. They come from the disc's HUD and wipe pictures.");
            ScreenSpeed = Config.Bind("Hud", "ScreenSpeed", 1.25f,
                new ConfigDescription("How fast the TOO BAD / GAME OVER screens play against Sunshine's own " +
                    "timing (1 is the game's; they are drawn smoothly between its 30 frames a second).",
                    new AcceptableValueRange<float>(0.25f, 3f)));
            TooBadDrop = Config.Bind("Hud", "TooBadDrop", 20f,
                new ConfigDescription("Metres below where Mario last stood, with no ground under him for a " +
                    "long way, at which TOO BAD starts and its jingle plays on a fall off the map. He is put " +
                    "back later, when the stage's kill zone (or 150 m) takes him.",
                    new AcceptableValueRange<float>(5f, 140f)));
            LivesScale = Config.Bind("Hud", "LivesScale", 0.7f,
                "The size of the \"MARIO x N\" banner shown with TOO BAD when Mario has an extra life " +
                "(Dio's Best Friend, a Pluripotent Larva); 1 is Sunshine's against the screen's height.");
            CoinX = Config.Bind("Hud", "CoinX", -1f,
                "Where the counters' left edge is across the screen, 0 left to 1 right. Negative: where " +
                "RoR2's own gold and lunar coin counters are.");
            CoinY = Config.Bind("Hud", "CoinY", -1f,
                "Where the yellow counter's middle is down the screen, 0 top to 1 bottom (the blue one goes " +
                "below it). Negative: where RoR2's own are.");
            CoinScale = Config.Bind("Hud", "CoinScale", 0.4f,
                "The counters' size; 1 is Sunshine's against the screen's height.");
            CoinGold = Config.Bind("Coins", "Gold", 1,
                new ConfigDescription("RoR2 gold each yellow coin Mario picks up is worth, on top of the " +
                    "health it heals. 0: none.", new AcceptableValueRange<int>(0, 1000)));
            LockRange = Config.Bind("Controls", "LockRange", 60f,
                new ConfigDescription("How far away, in metres, the lock-on can pick an enemy (with a clear line): " +
                    "far enough for flying ones. FLUDD's water still falls short of the far ones.",
                    new AcceptableValueRange<float>(5f, 200f)));
            LockMarker = Config.Bind("Look", "LockMarker", true,
                "While locked on (Controls.LockOn), a small turning sun made from Sunshine's life meter " +
                "floats on the enemy, placed as Olimar's cursor is.");
            LockMarkerHealth = Config.Bind("Look", "LockMarkerHealth", true,
                "The marker's rays are the enemy's health, whole rays of ten. Off: always whole.");
            LockMarkerSize = Config.Bind("Look", "LockMarkerSize", 0.6f, "The marker's width in metres.");
            LockMarkerSpin = Config.Bind("Look", "LockMarkerSpin", 150f, "How fast the marker turns, degrees a second.");
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
            ShallowWetSlide = Config.Bind("Sim", "ShallowWetSlide", true,
                "Standing in shallow water (anything RoR2 builds as water) wets the ground like " +
                "FLUDD's water does, so a belly slide there glides wet for its duration limit " +
                "instead of dragging as Sunshine's water does.");
            KnockdownEnabled = Config.Bind("Knockdown", "Enabled", true,
                "A powerful hit knocks Mario over (Sunshine's short fall), without stunning him: see " +
                "GetUpEarly. His health is RoR2's; the sim's isn't touched.");
            KnockdownThreshold = Config.Bind("Knockdown", "Threshold", 0.25f,
                new ConfigDescription("A single hit that takes this share of his max health (and shield) knocks him over.",
                    new AcceptableValueRange<float>(0.01f, 1f)));
            KnockdownGetUpEarly = Config.Bind("Knockdown", "GetUpEarly", true,
                "Once the fall's slide has nearly stopped, A, the stick, B, L or R gets him up at once " +
                "instead of after the whole get-up clip. Off: Sunshine's.");
            KnockdownInterruptsPound = Config.Bind("Knockdown", "InterruptsPound", true,
                "A knock-down can take him out of a ground pound, as a hit does in Sunshine. Off: a hit " +
                "during the pound only hurts.");
            SlideSplashes = Config.Bind("Look", "SlideSplashes", false,
                "A wet belly slide also throws libsms's old stand-in splashes (a home-made effect, " +
                "drawn like FLUDD's water) on top of Sunshine's own slide spray. Off: only the game's.");
            SlideSprayScale = Config.Bind("Look", "SlideSprayScale", 1.4f,
                "The wet belly slide's spray (Sunshine's own) is this many times as big as the game's.");
            WetSlideGlide = Config.Bind("Items", "WetSlideGlide", true,
                "A wet slide keeps its speed as if the stick were held forward (pull back to brake). " +
                "Off: Sunshine's, where it only carries on while you hold forward.");
            WetSlideSeconds = Config.Bind("Items", "WetSlideSeconds", 1.5f,
                "Seconds a slide that starts over wet ground glides before turning plain. No charges " +
                "or cooldown; cooldown reduction stretches this time and each Lysate Cell adds another " +
                "base duration. 0: as long as the slide lasts.");
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
            LockOn = Config.Bind("Controls", "LockOn", true,
                "While the left bumper (the utility key on keyboard) is held, lock onto the enemy " +
                "nearest where the camera points (within Controls.LockRange, 60 m, in line of sight) and aim FLUDD " +
                "at it, so the spray trigger sprays it without Mario crouching. Right bumper " +
                "then cycles targets. Needs CameraAim.");
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
            SlideWaveTint = Config.Bind("Look", "SlideWaveTint", 0.35f,
                "How visible the wet slide's side waves are as pale water, on top of their " +
                "refraction (which is subtle, and can vanish over some backgrounds). 0: refraction only.");
            GummyClones = Config.Bind("Look", "GummyClones", true,
                "Marios no player controls (Goobo Jr.'s clone) are drawn in RoR2's gummy clone " +
                "material. Off: Commando's model.");
            ExternalForces = Config.Bind("Sim", "ExternalForces", true,
                "RoR2 can throw Mario: jump pads, geysers, knockback, Milky Chrysalis.");
            CrowdControl = Config.Bind("Sim", "CrowdControl", true,
                "Stuns, freezes and shocks stop Mario's controls; roots stop him moving.");
            TraceJumps = Config.Bind("Debug", "TraceJumps", false,
                "For a moment after each jump, log every rendered frame: frame time, physics steps, " +
                "where Mario and the camera are, and Mario's height on screen.");
            LogStatus = Config.Bind("Debug", "LogStatus", false,
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
            Log.LogInfo($"libsms ABI {Native.sms_abi_version()}, assets {AssetsDir}");
            PrepareAssets(disc.Value, here);

            ItemHooks.Install();
            GlobalEventManager.onServerDamageDealt += MarioDriver.OnServerDamage;
            gameObject.AddComponent<MarioCoins>();
            gameObject.AddComponent<MarioScreenFx>();
            gameObject.AddComponent<MarioShine>();
            gameObject.AddComponent<MarioLunarCoin>();
            MarioNet.Register();
            // The HUD's skill icons name the buttons Mario's sim reads (MarioPad), not RoR2's binds.
            GlyphOverride.Install();
            GlyphOverride.Set("MarioTestBody", null, new Dictionary<SkillSlot, PadButton[]>
            {
                [SkillSlot.Primary] = new[] { PadButton.RT },
                [SkillSlot.Secondary] = new[] { PadButton.RT },
                [SkillSlot.Utility] = new[] { PadButton.LT },
                [SkillSlot.Special] = new[] { PadButton.B },
            });
            ContentProvider.Register(Guid, AddContent);
            Language.onCurrentLanguageChanged += AddTokens;
        }

        static string ResolveAssets(string configured, string here)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            string file = Path.Combine(here, "assets_dir.txt");
            if (File.Exists(file))
            {
                string pointer = File.ReadAllText(file).Trim();
                if (!string.IsNullOrWhiteSpace(pointer)) return pointer;
            }
            string env = Environment.GetEnvironmentVariable("SMS_ASSETS");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return Path.Combine(here, "assets");
        }

        // Match Olimar's first-run flow: register content now, extract on a worker, and let
        // sims/views wait for the complete cache. Never read files while they are being written.
        static void PrepareAssets(string configuredDisc, string here)
        {
            assetsReady = Native.sms_assets_complete(NativeLib.CString(AssetsDir)) != 0;
            string stings = Path.Combine(AssetsDir, "ror2_audio");
            // The disc's wipe pictures are an optional piece: a folder without them works, and gets
            // them whenever the disc is found.
            bool needExtras = !File.Exists(Path.Combine(AssetsDir, "wipe", "wipe_gameover.bti"))
                || !File.Exists(Path.Combine(AssetsDir, "wipe", "wipe_m_mark.bti"))
                || !File.Exists(Path.Combine(AssetsDir, "scene", "mapobj", "shine.bmd"))
                || !File.Exists(Path.Combine(AssetsDir, "camera", "camera_demo_shine_get_outside.bck"));
            bool needStings = !(File.Exists(Path.Combine(stings, "spawn.wav"))
                && File.Exists(Path.Combine(stings, "death.wav"))
                && File.Exists(Path.Combine(stings, "gameover.wav"))
                && File.Exists(Path.Combine(stings, "bluecoin.wav"))
                && File.Exists(Path.Combine(stings, "shineget.wav"))
                && File.Exists(Path.Combine(stings, "secret.wav")))
                // Stings cached before the left ear was connected are right-ear only: render them
                // again (libsms's STINGS_VERSION).
                || !File.Exists(Path.Combine(stings, "stings.version"))
                || File.ReadAllText(Path.Combine(stings, "stings.version")).Trim() != "2";
            if (assetsReady && !needStings && !needExtras)
                return;
            string disc = configuredDisc?.Trim();
            if ((!assetsReady || needExtras) && string.IsNullOrEmpty(disc))
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(AssetsDir)) ?? here;
                var found = new byte[32768];
                if (Native.sms_find_disc(NativeLib.CString(here + ";" + parent), found, (uint)found.Length) != 0)
                    disc = Utf8(found);
            }
            if (assetsReady && !needStings && (string.IsNullOrEmpty(disc) || !File.Exists(disc)))
            {
                // Only the optional pictures are missing, and there is no disc to take them from.
                Log.LogInfo("Sunshine's wipe pictures (the GAME OVER screen), Shine Sprite and Shine Get camera aren't " +
                            "all in the assets folder; set Paths.Disc to your Sunshine disc image to fetch them.");
                return;
            }
            if (!assetsReady && (string.IsNullOrEmpty(disc) || !File.Exists(disc)))
            {
                Log.LogError($"No complete Sunshine assets at '{AssetsDir}' and no disc image to build them from. " +
                             $"Set Paths.Disc in BepInEx/config/{Guid}.cfg to your own Sunshine ISO/GCM/CISO " +
                             "and restart. Mario uses Commando until his disc assets are ready.");
                return;
            }
            assetsBusy = true;
            Log.LogInfo(assetsReady ? "Preparing Sunshine arrival/death stings..."
                : $"Extracting Sunshine assets from {disc} into {AssetsDir}...");
            var worker = new Thread(() =>
            {
                try
                {
                    var msg = new byte[4096];
                    if (!assetsReady)
                    {
                        byte ok = Native.sms_extract(NativeLib.CString(disc), NativeLib.CString(AssetsDir), msg, (uint)msg.Length);
                        assetsReady = ok != 0 && Native.sms_assets_complete(NativeLib.CString(AssetsDir)) != 0;
                        if (assetsReady) Log.LogInfo($"Extracted: {Utf8(msg)}");
                        else Log.LogError($"Extraction failed: {Utf8(msg)}. Set Paths.Disc and restart to retry.");
                    }
                    // Device-free rendering from the same disc banks. Failure only silences the
                    // stings; Mario's sim and normal sounds can still use the complete extraction.
                    if (assetsReady)
                    {
                        if (Native.sms_render_stings(NativeLib.CString(AssetsDir), msg, (uint)msg.Length) != 0)
                            Log.LogInfo($"Sunshine audio: {Utf8(msg)}");
                        else Log.LogWarning($"Sunshine stings unavailable: {Utf8(msg)}");
                        if (needExtras && !string.IsNullOrEmpty(disc) && File.Exists(disc))
                        {
                            if (Native.sms_extract_extras(NativeLib.CString(disc), NativeLib.CString(AssetsDir), msg, (uint)msg.Length) != 0)
                                Log.LogInfo($"Sunshine wipe pictures, Shine Sprite and camera: {Utf8(msg)}");
                            else Log.LogWarning($"Sunshine wipe pictures, Shine Sprite and camera unavailable: {Utf8(msg)}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogError($"Extraction failed: {e}");
                }
                finally
                {
                    assetsBusy = false;
                }
            }) { IsBackground = true, Name = "sunshine extract" };
            worker.Start();
        }

        void Update()
        {
            if (musicInitialized || !AssetsSettled) return;
            musicInitialized = true;
            // Main thread: bind config and load the worker's finished PCM for RoR2 playback.
            string folder = Path.Combine(AssetsDir, "ror2_audio");
            if (AssetsReady) SpawnMusic.Initialize(Config, folder);
        }

        static string Utf8(byte[] buffer)
        {
            int end = Array.IndexOf(buffer, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
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
            body.AddComponent<MarioPuppet>();
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

            // For Goobo Jr.'s clone and the like: Commando's AI walking him, MarioBrain pressing his buttons.
            var ai = SurvivorAi.Register(pack, body, "Prefabs/CharacterMasters/CommandoMonsterMaster", "MarioTestMonsterMaster", Guid);
            if (ai) SurvivorAi.Drivers(ai, MarioBrain.HoldRange);
            pack.bodyPrefabs.Add(new[] { body });
            pack.survivorDefs.Add(new[] { survivor });
            pack.entityStateTypes.Add(new[] { typeof(MarioMain) });
            Log.LogInfo("Mario registered");
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
            lang.SetStringByToken("MARIOTEST_BODY_NAME", "Mario");
            lang.SetStringByToken("MARIOTEST_BODY_SUBTITLE", "Hero of Isle Delfino");
            lang.SetStringByToken("MARIOTEST_BODY_DESCRIPTION",
                "Mario came for a vacation, but trouble follows him everywhere. This time he has FLUDD, a high-pressure " +
                "water pack that sprays, hovers, rockets and turbo-dashes him out of any jam.\n\n" +
                "< ! > Blast enemies with FLUDD's four nozzles: Spray, Hover, Rocket and Turbo.\n" +
                "< ! > Stomp, dive and ground pound for heavy hits, and bounce higher with every stomp in a row.\n" +
                "< ! > Belly slide across wet ground to dodge danger or ram straight through it.\n" +
                "< ! > Collect the coins enemies drop to heal.");
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
