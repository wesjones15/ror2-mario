using System;
using Ror2Common;
using RoR2;
using RoR2.Skills;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioTest
{
    // RoR2's items on Mario (pikmin-main/DOCS/mario_ror2_item_ruleset.md). Most items need
    // nothing here: damage, health, armour, crit and every on-hit, on-kill and stat item work
    // through RoR2 because Mario's hits are RoR2 damage from his body (MarioCombat) and his
    // speed follows his move speed stat (Sim.SpeedFromItems). What's here hooks Mario's own
    // moves to what items already change:
    //
    // - His moves sit in RoR2's skill slots (MarioSkills), so RoR2 keeps their charges: the
    //   primary is FLUDD; the secondary holds his extra mid-air FLUDD uses (none until Backup
    //   Magazine adds them, 4 s each, recharging only after he's touched the ground); the
    //   utility his full-strength ground pounds (6 s; Hardlight adds two and shortens it; a
    //   pound without one does half the damage); the special his wet belly slides on dry
    //   ground (10 s from the slide's start; Lysate Cell adds one; without one the slide is
    //   plain; over water FLUDD sprayed a slide glides for free, as in Sunshine).
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
        readonly SkillLocator skills;

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
            skills = body ? body.skillLocator : null;
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

        GenericSkill Slot(SkillSlot s) => skills ? skills.GetSkill(s) : null;

        // ---- before and after each step ------------------------------------------------------

        /// Before sms_sim_step: water owed, and this step's limits.
        public void Begin(IntPtr sim, bool sprinting, bool grounded)
        {
            Current = this;
            wasSprinting = sprinting;
            wasGrounded = grounded;
            if (refillOwed)
            {
                Native.sms_sim_refill(sim);
                refillOwed = false;
            }
            waterOwed += RegenWater() * Time.fixedDeltaTime;
            if (waterOwed > 0f)
            {
                Native.sms_sim_add_water(sim, waterOwed);
                waterOwed = 0f;
            }
            var limits = Limits();
            Native.sms_sim_set_limits(sim, ref limits);
        }

        /// After the step, with Mario's state.
        public void End(IntPtr sim, in Native.State st)
        {
            if (quailOwed)
            {
                quailOwed = false;
                float boost = QuailBoost();
                if (boost > 1f) Native.sms_sim_boost_jump(sim, boost);
            }
            if (st.on_ground != 0) airSpentAloft = false;
            if (Current == this) Current = null;
        }

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
            if (Plugin.WetSlideCharges.Value)
            {
                var slot = special;
                bool ready = free || (NativeSkills.Holds(slot, MarioSkills.Slide) ? slot.stock > 0 : slot && slot.IsReady());
                l.wet_slide = (byte)(Plugin.WetSlideAnywhere.Value ? 2 : 1);
                l.wet_slide_ready = (byte)(ready ? 1 : 0);
                l.wet_slide_flags = (byte)((Plugin.WetSlideGlide.Value ? 1 : 0) | (Plugin.WetSlideOnWater.Value ? 2 : 0));
                l.wet_slide_seconds = Plugin.WetSlideSeconds.Value > 0f ? Plugin.WetSlideSeconds.Value * slideBoost : 0f;
            }
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
                    PoundCharged = NativeSkills.Spend(Slot(SkillSlot.Utility), MarioSkills.Pound);
                    PoundStrength = PoundCharged ? 1f : Mathf.Clamp01(Plugin.WeakPound.Value);
                    NativeBody.MarkCombat(body);
                    break;
                case Native.EventKind.WetSlide:
                    NativeSkills.Spend(Slot(SkillSlot.Special), MarioSkills.Slide);
                    break;
                case Native.EventKind.AirJump:
                    airJumpSeen = true;
                    Feather();
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
                case Native.EventKind.EnemyHit:
                    NativeBody.MarkCombat(body);
                    break;
            }
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
                Icon(SkillSlot.Special), Plugin.WetSlideRecharge.Value, 1, false);
            NativeSkills.SetFamily(body, SkillSlot.Primary, Fludd, pack);
            NativeSkills.SetFamily(body, SkillSlot.Secondary, Air, pack);
            NativeSkills.SetFamily(body, SkillSlot.Utility, Pound, pack);
            NativeSkills.SetFamily(body, SkillSlot.Special, Slide, pack);
        }

        public static void Tokens(Language lang)
        {
            lang.SetStringByToken("MARIOTEST_FLUDD_NAME", "FLUDD");
            lang.SetStringByToken("MARIOTEST_FLUDD_DESC",
                "Spray, Hover, Rocket and Turbo, from one tank. Every drop is an attack; items proc once a second " +
                "per target. Attack speed sprays faster for the same water. Cooldown items hover and spray longer, " +
                "rocket higher and turbo faster.");
            lang.SetStringByToken("MARIOTEST_AIR_NAME", "Air Pump");
            lang.SetStringByToken("MARIOTEST_AIR_DESC",
                "One FLUDD use per jump. Each charge is one more in mid-air, recharging once you've landed.");
            lang.SetStringByToken("MARIOTEST_POUND_NAME", "Ground Pound");
            lang.SetStringByToken("MARIOTEST_POUND_DESC",
                $"Hits harder the further you fall. Each charge is a full-strength pound; without one it does {Plugin.WeakPound.Value * 100f:0}%.");
            lang.SetStringByToken("MARIOTEST_SLIDE_NAME", "Wet Slide");
            lang.SetStringByToken("MARIOTEST_SLIDE_DESC",
                "Each charge turns a belly slide on dry ground wet, gliding far. Over water FLUDD sprayed " +
                "(a light press drops some ahead) a slide glides for free.");
        }
    }
}
