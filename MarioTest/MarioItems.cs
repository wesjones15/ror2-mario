using System;
using Ror2Common;
using RoR2;
using RoR2.Skills;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // RoR2's items on Mario (docs/mario_ror2_item_ruleset.md). Most items need
    // nothing here: damage, health, armour, crit and every on-hit, on-kill and stat item work
    // through RoR2 because Mario's hits are RoR2 damage from his body (MarioCombat) and his
    // speed follows his move speed stat (Sim.SpeedFromItems). What's here hooks Mario's own
    // moves to what items already change:
    //
    // - His moves sit in RoR2's skill slots (MarioSkills), so RoR2 keeps their charges: the
    //   primary is FLUDD; the secondary holds his extra mid-air FLUDD uses (none until Backup
    //   Magazine adds them, 4 s each, recharging only after he's touched the ground); the
    //   utility his full-strength ground pounds (6 s; Hardlight adds two and shortens it; a
    //   pound without one does half the damage); the special his wet belly slide, requiring
    //   wet ground and limited by duration. It consumes no stock and has no recharge.
    //   Lysate's extra stocks extend that duration instead of adding charges.
    //   Bandolier's ammo restocks them all and fills the tank; Brainstalks makes charges free
    //   and the tank bottomless while it lasts. Heresy items override a slot as for any
    //   survivor: the overriding skill runs when Mario makes that slot's move (Visions as
    //   FLUDD sprays, Hooks as a nozzle starts in mid-air, Strides on a ground pound, Essence
    //   on a slide that would turn wet).
    // - Cooldown items (Alien Head, Purity; and Lysate for the slide) stretch what has no
    //   cooldown: hover time, Spray time, rocket height, Turbo speed, the wet slide's glide
    //   (SkillCooldown.Boost, `1 / ratio ^ Items.CooldownExponent`).
    // - Attack speed (Soldier's Syringe, Mocha) fires the Spray faster for the same water.
    // - Hopoo Feather: mid-air jumps, each the next of the double/triple chain. Wax Quail: a
    //   sprinting jump (not the side somersault or spin) carries further. Sprint is RoR2's
    //   sprint button (Energy Drink, Weeping Fungus); it adds no speed of its own.
    // - H3AD-5T v2: a full-strength ground pound's landing also sets off the item's blast
    //   (its damage and reach growing with the drop), on the item's cooldown.
    // - Healing fills the tank: heals as asked for (Bustling Fungus even at full health),
    //   regeneration beyond Mario's base, and a little of lifesteal, capped.
    //
    // Runs where Mario's sim does (the body's authority). Heals arrive on the server; multiplayer
    // clients playing Mario don't get their water from them yet.
    internal sealed class MarioItems : IDisposable
    {
        /// The Mario whose sim is stepping (its events land here).
        public static MarioItems Current;
        /// MarioCombat is landing one of Mario's own hits: heals now are its lifesteal (Super
        /// Massive Leech heals inside the hit with no proc of its own to tell it by).
        public static bool Dealing;

        readonly CharacterBody body;
        readonly NativeBody native;
        // Looked up when used: CharacterBody sets its skillLocator in its own Awake, which may run
        // after the driver's (the slots were then never found, and no charge was ever spent).
        SkillLocator skills;

        float waterOwed;
        bool refillOwed;
        bool quailOwed;
        bool airJumpSeen;
        bool airSpentAloft;
        bool wasSprinting, wasGrounded;
        float nozzleHeld = 1f, slideHeld = 1f;
        float lifestealSince, lifestealGiven;
        float stomperReadyAt;

        /// The current ground pound's damage, times this (Items.WeakPound without a charge).
        public float PoundStrength { get; private set; } = 1f;
        /// The current pound spent a charge (full strength).
        public bool PoundCharged { get; private set; } = true;
        /// Cooldown items' boost for the nozzles this step.
        public float NozzleBoost { get; private set; } = 1f;

        public MarioItems(CharacterBody body)
        {
            this.body = body;
            native = body ? body.GetComponent<NativeBody>() : null;
            ItemHooks.Healed += OnHealed;
            ItemHooks.AmmoPack += OnAmmoPack;
            if (MarioSkills.Air) MarioSkills.Air.RechargeGate = AirRechargeOpen;
        }

        public void Dispose()
        {
            ItemHooks.Healed -= OnHealed;
            ItemHooks.AmmoPack -= OnAmmoPack;
            if (Current == this) Current = null;
        }

        GenericSkill Slot(SkillSlot s)
        {
            if (!skills && body) skills = body.skillLocator;
            return skills ? skills.GetSkill(s) : null;
        }

        // ---- a client's Mario ----------------------------------------------------------------
        // Heals and Bandolier happen on the server, but the tank is in the player's sim: the server's
        // copy of a client's Mario collects what he is owed and sends it (MarioNet.SendWater).

        float remoteSentAt;

        /// On the server, for a Mario whose sim is on a client's machine: send the tank refills owed.
        public void FlushRemote()
        {
            if (!body || (waterOwed <= 0.0001f && !refillOwed) || Time.fixedTime - remoteSentAt < 0.2f) return;
            remoteSentAt = Time.fixedTime;
            MarioNet.SendWater(body, waterOwed, refillOwed);
            waterOwed = 0f;
            refillOwed = false;
        }

        /// The server says the tank is owed this much (a share of full) and maybe a full refill.
        public void Owed(float share, bool refill)
        {
            waterOwed += share;
            if (refill) refillOwed = true;
        }

        // ---- before and after each step ------------------------------------------------------

        /// Before sms_sim_step: water owed, and this step's limits.
        public void Begin(IntPtr sim, bool sprinting, bool grounded)
        {
            Current = this;
            wasSprinting = sprinting;
            wasGrounded = grounded;
            if (refillOwed)
            {
                SimOps.Refill(sim);
                refillOwed = false;
            }
            waterOwed += RegenWater() * Time.fixedDeltaTime;
            if (waterOwed > 0f)
            {
                SimOps.AddWater(sim, waterOwed);
                waterOwed = 0f;
            }
            var limits = Limits();
            SimOps.Limits(sim, ref limits);
        }

        /// After the step, with Mario's state.
        public void End(IntPtr sim, in Native.State st)
        {
            if (quailOwed)
            {
                quailOwed = false;
                float boost = QuailBoost();
                if (boost > 1f) SimOps.BoostJump(sim, boost);
            }
            if (st.on_ground != 0) airSpentAloft = false;
            if (Current == this) Current = null;
        }

        /// The secondary's max stock last step (-1 before the first).
        int airMaxSeen = -1;
        bool airStockLogged;

        Native.Limits Limits()
        {
            var primary = Slot(SkillSlot.Primary);
            NozzleBoost = Plugin.CooldownBoosts.Value
                ? SkillCooldown.Boost(primary, Plugin.CooldownExponent.Value, ref nozzleHeld)
                : 1f;
            var special = Slot(SkillSlot.Special);
            float slideBoost = Plugin.CooldownBoosts.Value
                ? SkillCooldown.Boost(special, Plugin.CooldownExponent.Value, ref slideHeld)
                : 1f;
            var secondary = Slot(SkillSlot.Secondary);
            // A Backup Magazine raises the secondary's max stock from nothing. The charge it adds
            // comes filled, as extra stock does for every other survivor, instead of empty and
            // waiting out the recharge that only starts once he has touched the ground.
            if (secondary && NativeSkills.Holds(secondary, MarioSkills.Air))
            {
                // The magazine's stacks are the Air Pump's extra uses. RoR2 raises the secondary's max stock
                // from them itself (CharacterBody.RecalculateStats); where it has not (a skill whose base stock
                // is nothing), set the bonus here from the item count, so the uses never depend on that.
                int magazines = ByName.Count(body, "SecondarySkillMagazine");
                if (magazines > 0 && secondary.maxStock < MarioSkills.Air.baseMaxStock + magazines)
                {
                    secondary.SetBonusStockFromBody(magazines);
                    if (!airStockLogged)
                    {
                        airStockLogged = true;
                        Plugin.Log.LogInfo($"Air Pump: {magazines} Backup Magazine(s) set the max stock to {secondary.maxStock}");
                    }
                }
                int max = secondary.maxStock;
                // From the skill's own base on first sight, so a body that spawns holding the
                // magazine (its bonus lands after the skill was filled with nothing) starts with it
                // too, not only one that picks it up.
                if (airMaxSeen < 0) airMaxSeen = Mathf.Max(0, secondary.skillDef.baseMaxStock);
                if (max > airMaxSeen)
                    secondary.stock = Mathf.Min(max, secondary.stock + (max - airMaxSeen));
                airMaxSeen = max;
            }
            bool free = NativeSkills.Unlimited(body);
            var l = new Native.Limits
            {
                spray_rate = Plugin.SprayRateFromItems.Value ? Mathf.Clamp(MinionStats.Of(body).attackSpeed, 0.1f, 10f) : 1f,
                spray_seconds = Plugin.SpraySeconds.Value > 0f ? Plugin.SpraySeconds.Value * NozzleBoost : 0f,
                spray_fade = NozzleBoost,
                hover_time = NozzleBoost,
                turbo_speed = NozzleBoost,
                air_uses = (byte)(Plugin.AirUses.Value ? 1 : 0),
                air_extra = (uint)Mathf.Max(0, free ? 99 : NativeSkills.Charges(secondary, MarioSkills.Air)),
                air_jumps = (uint)Mathf.Max(0, body ? body.maxJumpCount - 1 : 0),
            };
            // Always require wet ground. Refresh availability each step without consulting
            // stock; the sim remembers the duration once per slide, including over water.
            int extraSlideTime = NativeSkills.Holds(special, MarioSkills.Slide)
                ? Mathf.Max(0, special.maxStock - MarioSkills.Slide.baseMaxStock) : 0;
            l.wet_slide = 1;
            l.wet_slide_ready = 1;
            l.wet_slide_flags = (byte)(Plugin.WetSlideGlide.Value ? 1 : 0);
            l.wet_slide_seconds = Plugin.WetSlideSeconds.Value > 0f
                ? Plugin.WetSlideSeconds.Value * (1 + extraSlideTime) * slideBoost : 0f;
            return l;
        }

        /// Rocket height times this (cooldown items).
        public float RocketBoost => NozzleBoost;

        /// Brainstalks: a bottomless tank while it lasts.
        public bool UnlimitedWater => NativeSkills.Unlimited(body);

        // ---- the sim's events (during the step) ----------------------------------------------

        public static void OnEvent(ref Native.Event e) => Current?.Handle(ref e);

        void Handle(ref Native.Event e)
        {
            switch ((Native.EventKind)e.kind)
            {
                case Native.EventKind.AirNozzle:
                {
                    var slot = Slot(SkillSlot.Secondary);
                    if (!NativeSkills.Holds(slot, MarioSkills.Air))
                        slot?.ExecuteIfReady(); // Hooks of Heresy: on every mid-air start
                    else if (e.arg != 0)
                    {
                        NativeSkills.Spend(slot, MarioSkills.Air);
                        airSpentAloft = true;
                    }
                    NativeBody.MarkCombat(body);
                    break;
                }
                case Native.EventKind.PoundStart:
                {
                    // Full strength with a charge on hand; the charge is spent when the pound lands
                    // (PoundLanded), so an interrupted one costs nothing.
                    var utility = Slot(SkillSlot.Utility);
                    PoundCharged = poundOwed = NativeSkills.Ready(utility, MarioSkills.Pound);
                    Plugin.Log.LogDebug(utility
                        ? $"Ground pound: {(PoundCharged ? "a charge to spend on landing" : "no charge")}; the utility slot holds " +
                          $"{(utility.skillDef ? utility.skillDef.skillName : "nothing")}, stock {utility.stock}/{utility.maxStock}, " +
                          $"{utility.cooldownRemaining:0.0}s to the next"
                        : "Ground pound: the body has no utility skill slot");
                    PoundStrength = PoundCharged ? 1f : Mathf.Clamp01(Plugin.WeakPound.Value);
                    NativeBody.MarkCombat(body);
                    break;
                }
                case Native.EventKind.GroundPound:
                    PoundLanded();
                    break;
                case Native.EventKind.EnemyHit:
                    // Landing on an enemy with the pound counts too (value 1: a ground pound).
                    if (e.value == 1) PoundLanded();
                    NativeBody.MarkCombat(body);
                    break;
                case Native.EventKind.WetSlide:
                {
                    var slot = Slot(SkillSlot.Special);
                    if (NativeSkills.Holds(slot, MarioSkills.Slide))
                        body.OnSkillActivated(slot); // skill-use items, without spending stock
                    else
                        slot?.ExecuteIfReady(); // Essence of Heresy keeps its own cooldown
                    break;
                }
                case Native.EventKind.AirJump:
                    airJumpSeen = true;
                    Feather();
                    // RoR2's jump event (Faraday Spur, Earthbound Vessel, Hex of Leaping).
                    native?.Jumped();
                    break;
                case Native.EventKind.Jump:
                    var kind = (Native.JumpKind)e.arg;
                    if (kind == Native.JumpKind.Dive) NativeBody.MarkCombat(body);
                    // Wax Quail: RoR2's sprinting jump, from the ground (not Hopoo's mid-air ones).
                    if (!airJumpSeen && wasSprinting && wasGrounded
                        && (kind == Native.JumpKind.Normal || kind == Native.JumpKind.Second || kind == Native.JumpKind.Triple))
                        quailOwed = true;
                    airJumpSeen = false;
                    if (body && body.characterMotor) body.characterMotor.jumpCount++;
                    native?.Jumped();
                    break;
                case Native.EventKind.Land:
                    if (body && body.characterMotor) body.characterMotor.jumpCount = 0;
                    break;
                case Native.EventKind.WaterEmit:
                {
                    // Visions of Heresy: the overriding primary fires as FLUDD does, at its own
                    // rate (its charges and cooldown decide).
                    var slot = Slot(SkillSlot.Primary);
                    if (slot && !NativeSkills.Holds(slot, MarioSkills.Fludd)) slot.ExecuteIfReady();
                    NativeBody.MarkCombat(body);
                    break;
                }
            }
        }

        // The pound's charge, spent on its first impact (the floor or an enemy).
        bool poundOwed;

        void PoundLanded()
        {
            if (!poundOwed) return;
            poundOwed = false;
            var utility = Slot(SkillSlot.Utility);
            NativeSkills.Spend(utility, MarioSkills.Pound);
            if (utility) Plugin.Log.LogDebug($"Ground pound landed: stock {utility.stock}/{utility.maxStock}, {utility.cooldownRemaining:0.0}s to the next");
        }

        // ---- recharge ------------------------------------------------------------------------

        static bool AirRechargeOpen(GenericSkill slot)
        {
            // Extra uses spent aloft wait for the ground before recharging; the clock then runs
            // on, airborne or not.
            var b = slot ? slot.characterBody : null;
            var items = b ? b.GetComponent<MarioDriver>()?.Items : null;
            return items == null || !items.airSpentAloft;
        }

        // ---- Hopoo Feather and Wax Quail -----------------------------------------------------

        static GameObject featherEffect;

        void Feather()
        {
            if (!body) return;
            if (!featherEffect) featherEffect = LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/FeatherEffect");
            if (featherEffect)
                EffectManager.SpawnEffect(featherEffect, new EffectData { origin = body.footPosition }, true);
        }

        /// GenericCharacterMain.ProcessJump's Wax Quail, as a share of a full run: the boost's
        /// speed, `sqrt(10 * stacks * accel * air control)`, over RoR2's move speed.
        float QuailBoost()
        {
            int n = ByName.Count(body, "JumpBoost");
            if (n <= 0 || !body) return 1f;
            var motor = body.characterMotor;
            float accel = body.acceleration * (motor ? motor.airControl : 0.25f);
            float speed = Mathf.Max(1f, body.moveSpeed);
            if (accel <= 0f) return 1f;
            float extra = Mathf.Sqrt(10f * n * accel) / speed * Mathf.Max(0f, Plugin.QuailScale.Value);
            return 1f + extra;
        }

        // ---- H3AD-5T v2 ----------------------------------------------------------------------

        /// After a full-strength pound landed `drop` metres down, at `at`: the item's blast
        /// (HeadstompersFall.DoStompExplosionAuthority's numbers), on its cooldown.
        public void Stomp(Vector3 at, float drop)
        {
            int n = ByName.Count(body, "FallBoots");
            if (n <= 0 || !body || !PoundCharged || Time.fixedTime < stomperReadyAt) return;
            float maxDistance = Stat("HeadstompersFall", "maxDistance", 30f);
            float t = Mathf.InverseLerp(0f, maxDistance, drop);
            float damage = Mathf.Lerp(Stat("HeadstompersFall", "minimumDamageCoefficient", 10f),
                Stat("HeadstompersFall", "maximumDamageCoefficient", 100f), t);
            float radius = Mathf.Lerp(Stat("HeadstompersFall", "minimumRadius", 5f),
                Stat("HeadstompersFall", "maximumRadius", 100f), t);
            stomperReadyAt = Time.fixedTime + Stat("HeadstompersCooldown", "baseDuration", 10f) / n;
            new BlastAttack
            {
                attacker = body.gameObject,
                inflictor = body.gameObject,
                teamIndex = TeamComponent.GetObjectTeam(body.gameObject),
                position = at,
                procCoefficient = 1f,
                radius = radius,
                baseForce = 200f * damage,
                bonusForce = Vector3.up * 2000f,
                baseDamage = body.damage * damage,
                falloffModel = BlastAttack.FalloffModel.SweetSpot,
                crit = body.RollCrit(),
                damageColorIndex = DamageColorIndex.Item,
                attackerFiltering = AttackerFiltering.NeverHitSelf,
            }.Fire();
            var fx = LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/ImpactEffects/BootShockwave");
            if (fx) EffectManager.SpawnEffect(fx, new EffectData { origin = at, scale = radius }, true);
        }

        /// A Headstompers state's tuning (static, set from RoR2's entity state configs), or
        /// `fallback` if this RoR2 hasn't got it.
        static float Stat(string state, string field, float fallback)
        {
            var t = ByName.Type("EntityStates.Headstompers." + state);
            var f = t?.GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            return f != null && f.GetValue(null) is float v && v > 0f ? v : fallback;
        }

        // ---- healing into water --------------------------------------------------------------

        static ProcType? healOnHit, healOnCrit, repeatHeal;

        void OnHealed(HealthComponent target, float amount, ProcChainMask mask, bool nonRegen)
        {
            if (!body || target != body.healthComponent || !nonRegen) return;
            IsLifesteal(default);
            // Corpsebloom pays a heal out over time as more heals: the first one counted.
            if (repeatHeal.HasValue && mask.HasProc(repeatHeal.Value)) return;
            float max = Mathf.Max(1f, target.fullHealth);
            float share = amount / max;
            if (Dealing || IsLifesteal(mask))
            {
                float now = Time.fixedTime;
                if (now - lifestealSince >= 1f)
                {
                    lifestealSince = now;
                    lifestealGiven = 0f;
                }
                float room = Mathf.Max(0f, Plugin.LifestealWaterCap.Value - lifestealGiven);
                float add = Mathf.Min(room, share * Mathf.Max(0f, Plugin.LifestealToWater.Value));
                lifestealGiven += add;
                waterOwed += add;
            }
            else
            {
                waterOwed += share * Mathf.Max(0f, Plugin.HealToWater.Value);
            }
        }

        static bool looked;

        static bool IsLifesteal(ProcChainMask mask)
        {
            if (!looked)
            {
                looked = true;
                healOnHit = Enum.TryParse("HealOnHit", out ProcType a) ? a : (ProcType?)null;
                healOnCrit = Enum.TryParse("HealOnCrit", out ProcType b) ? b : (ProcType?)null;
                repeatHeal = Enum.TryParse("RepeatHeal", out ProcType c) ? c : (ProcType?)null;
            }
            return (healOnHit.HasValue && mask.HasProc(healOnHit.Value))
                || (healOnCrit.HasValue && mask.HasProc(healOnCrit.Value));
        }

        /// Tank share a second from regeneration beyond Mario's own (Cautious Slug and the
        /// like): RoR2's regen less the body's base at its level.
        float RegenWater()
        {
            if (!body || Plugin.RegenToWater.Value <= 0f) return 0f;
            float level = Mathf.Max(1f, body.level);
            float baseRegen = body.baseRegen + body.levelRegen * (level - 1f);
            float extra = body.regen - baseRegen;
            float max = Mathf.Max(1f, body.maxHealth);
            return extra > 0f ? extra / max * Plugin.RegenToWater.Value : 0f;
        }

        void OnAmmoPack(SkillLocator locator)
        {
            if (body && locator && locator.gameObject == body.gameObject) refillOwed = true;
        }
    }

    // Mario's moves as RoR2 skills (see MarioItems), made once at content load.
    internal static class MarioSkills
    {
        public static NativeSkillDef Fludd, Air, Pound, Slide;

        public static void Add(GameObject body, RoR2.ContentManagement.ContentPack pack)
        {
            var loc = body.GetComponent<SkillLocator>();
            Sprite Icon(SkillSlot s) => loc && loc.GetSkill(s) && loc.GetSkill(s).skillFamily
                ? loc.GetSkill(s).skillFamily.defaultSkillDef?.icon : null;
            Fludd = NativeSkills.Make("MarioFludd", "MARIOTEST_FLUDD_NAME", "MARIOTEST_FLUDD_DESC",
                Icon(SkillSlot.Primary), 0f, 1, true);
            Air = NativeSkills.Make("MarioAirFludd", "MARIOTEST_AIR_NAME", "MARIOTEST_AIR_DESC",
                Icon(SkillSlot.Secondary), Plugin.AirUseRecharge.Value, 0, true);
            Pound = NativeSkills.Make("MarioGroundPound", "MARIOTEST_POUND_NAME", "MARIOTEST_POUND_DESC",
                Icon(SkillSlot.Utility), Plugin.PoundRecovery.Value, 1, true);
            Slide = NativeSkills.Make("MarioWetSlide", "MARIOTEST_SLIDE_NAME", "MARIOTEST_SLIDE_DESC",
                Icon(SkillSlot.Special), 0f, 1, false);
            Slide.requiredStock = 0;
            Slide.stockToConsume = 0;
            // Icons RoR2 ships, swapped in once the catalogs load (the body's own family icons above
            // stay if a name isn't found).
            VanillaIcons.Use(Fludd, "EngiBodyPlaceWalkerTurret");  // TR58 Carbonizer Turret
            VanillaIcons.Use(Air, "EngiBodyPlaceTurret");          // TR12 Gauss Auto-Turret
            VanillaIcons.Use(Pound, "CommandoBodyRoll", 1);        // Tactical Dive, turned to point down
            VanillaIcons.Use(Slide, "CommandoSlide");              // Tactical Slide
            NativeSkills.SetFamily(body, SkillSlot.Primary, Fludd, pack);
            NativeSkills.SetFamily(body, SkillSlot.Secondary, Air, pack);
            NativeSkills.SetFamily(body, SkillSlot.Utility, Pound, pack);
            NativeSkills.SetFamily(body, SkillSlot.Special, Slide, pack);
        }

        // Sunshine's Spray, in units, for a SprayReach multiple (measured: 960, 1,480 and 2,040 at 1, 1.5 and 2).
        static float SprayUnits(float reach) =>
            reach <= 1.5f ? 960f + (reach - 1f) * 1040f : 1480f + (reach - 1.5f) * 1120f;

        public static void Tokens(Language lang)
        {
            // What each skill does for the player: controls, how long, how far, how often. The
            // numbers come from the config, so they stay true when it changes.
            // Sunshine's Spray flies 960 units at SprayReach 1, 1,480 at 1.5 and 2,040 at 2; a
            // standard survivor is 1.8 m tall against Mario's 160 units.
            float reach = SprayUnits(Plugin.SprayReach.Value) * 1.8f / UnityHost.MarioHeight;
            float perSecond = Plugin.WaterDamage.Value * 93f * 100f;
            lang.SetStringByToken("MARIOTEST_FLUDD_NAME", "FLUDD");
            // A burst is about 28 drops, each worth BurstMultiplier times a drop.
            float burst = Plugin.BurstMultiplier.Value * 28f * Plugin.WaterDamage.Value * 100f;
            lang.SetStringByToken("MARIOTEST_FLUDD_DESC",
                "<style=cIsUtility>Tap RT</style> for a splash burst of up to " +
                $"<style=cIsDamage>{burst:0}% damage</style> that wets the ground for a Wet Slide. " +
                "<style=cIsUtility>Hold RT</style> to spray " +
                (Plugin.AimAtCrosshair.Value ? "where your crosshair points " : "") +
                $"up to <style=cIsUtility>{reach:0} m</style> for " +
                $"<style=cIsDamage>{perSecond:0}% damage per second</style>; spin the stick while spraying to spray all round. " +
                "<style=cIsUtility>RB</style> switches nozzle: <style=cIsUtility>Hover</style> floats, " +
                "<style=cIsUtility>Rocket</style> launches skyward, <style=cIsUtility>Turbo</style> dashes. " +
                $"<style=cIsUtility>Hold LB</style> to lock on within {Plugin.LockRange.Value:0} m.");
            lang.SetStringByToken("MARIOTEST_AIR_NAME", "Air Pump");
            lang.SetStringByToken("MARIOTEST_AIR_DESC",
                "<style=cIsUtility>Hold RT in mid-air</style> to start FLUDD. The <style=cIsUtility>Spray</style> can start as often as " +
                "you like. Hover, Rocket and Turbo each take a use: Mario gets one per jump, each charge is " +
                $"another, and one charge returns every <style=cIsUtility>{Plugin.AirUseRecharge.Value:0.#} seconds</style> " +
                "once Mario is back on the ground.");
            lang.SetStringByToken("MARIOTEST_POUND_NAME", "Ground Pound");
            lang.SetStringByToken("MARIOTEST_POUND_DESC",
                "<style=cIsUtility>Press LT in mid-air</style> to slam down. Hits the enemy underneath for " +
                $"<style=cIsDamage>{Plugin.PoundDamage.Value * 100f:0}% damage</style> and sends a shock wave of " +
                $"<style=cIsDamage>{Plugin.ShockDamage.Value * 100f:0}%</style> through everything within " +
                $"<style=cIsUtility>{Plugin.PoundRadius.Value:0} m</style>, growing {Plugin.PoundPerMetre.Value * 100f:0}% for each " +
                "metre fallen. A fall of about 7 m is a super pound, which also ignites. Landing on an enemy is a stomp for " +
                $"<style=cIsDamage>{Plugin.StompDamage.Value * 100f:0}% damage</style> that bounces Mario higher each time in a row. " +
                $"Each charge is one full-strength pound, spent on landing and back after <style=cIsUtility>{Plugin.PoundRecovery.Value:0.#} seconds</style>; " +
                $"without one the pound does {Plugin.WeakPound.Value * 100f:0}%.");
            lang.SetStringByToken("MARIOTEST_SLIDE_NAME", "Wet Slide");
            lang.SetStringByToken("MARIOTEST_SLIDE_DESC",
                "<style=cIsUtility>Press B in mid-air</style> to dive, then belly slide. Diving or sliding into an enemy deals " +
                $"<style=cIsDamage>{Plugin.DiveDamage.Value * 100f:0}% damage</style>. Over wet ground (a light RT splash ahead of you, " +
                "or shallow water) the slide glides for as long as the ground under it stays wet or it is in water, and for " +
                $"<style=cIsUtility>{Plugin.WetSlideSeconds.Value:0.##} seconds</style> after the last wet contact. " +
                "No charges, no cooldown. Cooldown reduction and Lysate Cell lengthen the glide.");
        }
    }
}
