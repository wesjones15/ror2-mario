# Mario — Risk of Rain 2 Item Effects Ruleset

**Status:** Agreed design rules and implementation handoff, October 2026. This document describes intentional custom item mappings, not necessarily vanilla RoR2 behavior. Numbers not explicitly agreed are marked **TBD**.

## 1. Architecture and invariants

- Mario's movement, jumps, FLUDD nozzles, Ground Pound, Wet Slide, and custom analog controls retain **Super Mario Sunshine rules** in the Rust simulation. Do not substitute RoR2 default controller mappings or impose conventional cooldowns on every move.
- A C#/Unity integration layer connects RoR2 inventory, stats, damage, proc events, healing, equipment, skill/item bonuses and AI control to Rust. Normal vanilla item behavior should work through standard RoR2 systems where possible; special translations live in the adapter/Rust modifier layer.
- Mario has **one shared, finite FLUDD water tank**. No item currently increases maximum tank capacity. Water pickups still refill it.
- Mario can pick up and use ordinary **orange equipment**. Fuel Cell and Soulbound Catalyst should retain standard equipment behavior.
- Preserve Mario's existing **side jump and spin jump** behavior; movement-item modifications should not alter those moves.

## 2. FLUDD water and healing

- **No passive water regeneration by default.** Cooldown reduction does **not** itself unlock passive water regeneration.
- **Active healing auras** (e.g. Bustling Fungus) gradually refill FLUDD **even at full HP**. Healing items that grant **passive health regeneration** also grant passive water regeneration.
- Lifesteal heals also restore some FLUDD water, but at a **low conversion rate**.
- Water regeneration strength scales with the healing effect's amount/rate, not merely with HP actually missing or recovered. Healing effects may restore water at full health.
- FLUDD **continues regenerating while spraying or otherwise being used**; water intake and expenditure occur concurrently.
- No tank-capacity increases are currently planned. Healing-to-water conversion coefficients, caps and safeguards against healing/proc feedback loops are **TBD**.

## 3. Nozzle behavior and water-origin attacks

| Nozzle | Direction / attack origin | Item interactions |
|---|---|---|
| **Spray** | Forward-directed water | Attack-speed items increase spray rate; cooldown reduction increases maximum continuous Spray activation duration; water consumption rate and tank capacity do not change. |
| **Hover** | Water attack below Mario | Cooldown reduction extends maximum hover duration. |
| **Rocket** | **Area-of-effect water attack below Mario** | Cooldown reduction increases launch height. Rocket water participates in the same damage/proc system. |
| **Turbo** | Water attack **behind Mario** | Movement-speed buffs scale Turbo speed. |

**Water is the attack source.** On-hit effects (including Shuriken interactions) are triggered through water hit events, not simply by selecting or activating a nozzle. Every nozzle participates. Water damage can tick more frequently than item procs.

### FLUDD on-hit proc rule

- **Maximum one item proc attempt per second per target**, shared across all nozzles, with a **0.5 proc coefficient**.
- This limit is **per target**, not one global attempt per second across all enemies.
- All water hit directions, including Turbo's rear attack and Rocket's downward AoE, use this rule.
- **Shuriken:** Charges launch sequentially with a **0.5-second delay between Shurikens** during sustained water attacks. The Shuriken trigger follows water hits, not nozzle activation. Its charged-attack scheduling is distinct from ordinary on-hit proc attempts; implementation should avoid duplicate activations from rapid water ticks.
- **Soldier's Syringe / attack-speed effects:** Increase spray rate **without increasing water consumption rate** for now. Shared tank capacity remains unchanged, providing a balance constraint.

## 4. Airborne FLUDD uses — Backup Magazine

- Mario always has **one baseline FLUDD use while airborne**.
- **Backup Magazine:** Adds **one extra airborne FLUDD use per stack**, drawn from a **single shared pool for every nozzle**—not one pool per nozzle and not Hover-only.
- Any sustained activation of a nozzle counts as **one use**, regardless of how long it runs. Switching nozzles and activating another one while airborne consumes another use. Example: Hover followed by Spray consumes two uses total.
- Extra uses recharge **sequentially, 4 seconds per extra use**. Their recharge begins upon ground contact, and **continues if Mario takes off again**; jumping does not reset or pause recharge.
- Cooldown reduction shortens the extra-use recharge time. The default baseline airborne use is separate from these rechargeable extras.
- Nozzle-use limits are separate from water consumption: having a use available does not provide free water.
- **Implementation clarification to test:** handling partial recharge progress and spending an extra charge while already airborne; preserve continuous recharge progress rather than resetting the queue without cause.

## 5. Cooldown reduction — Mario-specific effects

Cooldown reduction affects **several Mario-specific limits simultaneously** rather than granting conventional RoR2 skill cooldowns:

1. Longer maximum **Hover** duration.
2. Higher **Rocket** launch height.
3. Longer maximum continuous **Spray** activation duration; **no water-efficiency bonus**, reduced consumption, or tank-size increase.
4. Longer maximum **Wet Slide** duration cap. Wet Slide has no recharge or charges.
5. Shorter **Ground Pound** full-strength recovery period.
6. Shorter sequential recharge time for Backup Magazine's **extra airborne FLUDD uses**.
7. Higher **Turbo** nozzle movement speed, **not** longer Turbo duration.

Cooldown effects from **Alien Head** and the cooldown component of **Purity** use this mapping. Purity retains its normal luck penalty. For nozzle performance, derive a multiplier from the effective cooldown reduction (use a dimensionless ratio, not a subtraction of cooldown seconds). Prefer vanilla-like diminishing returns from stacked cooldown items and **no hard 3× cap**; approximately 3× performance should require substantial investment. Tune the curve in testing. Nozzle mappings: **Hover = duration; Spray = duration; Rocket = height; Turbo = speed**. These bonuses do not increase water tank capacity, reduce water consumption, or automatically refill water. Actual recovery/recharge timers continue to use the normal cooldown calculation. Purity retains its luck penalty.

## 6. Ground Pound — Utility

- **Ground Pound is Mario's Utility skill**, not his Special.
- Ground Pound is **always usable**. During its full-strength recovery period, another Ground Pound deals **25% of its otherwise applicable current damage**, including item-based damage modifiers. Only Ground Pound damage is penalized; other attacks are unaffected.
- Cooldown reduction shortens this recovery period. Provisional baseline full-strength recovery: **15 seconds**, subject to playtesting; base damage may be lowered later for balance.
- **Hardlight Afterburner:** Grants additional **full-strength Ground Pound** uses before the recovery penalty applies; also applies its Utility cooldown benefit to Ground Pound recovery.
- **H3AD-5T v2 (Headstompers):** Buffs Mario's **existing Ground Pound**, preserving its animation, hitbox, and **X-button** binding. Do **not** replace Ground Pound with a separate vanilla stomp input.
- Hardlight Afterburner follows its normal Utility bonus (**+2 extra full-strength charges per stack** and its cooldown reduction). Extra full-strength Ground Pounds recover sequentially using the Ground Pound recovery timer; a Ground Pound attempted without a ready full-strength charge still works at 25% damage.

## 7. Wet Slide — Special

- **Wet Slide is Mario's Special skill**.
- Requires **wet ground** to begin gliding: a FLUDD wet patch, naturally wet terrain, or shallow water. A dry slide stays regular.
- Water impacts slightly darken terrain while it is wet; light burst spray wets a broad patch ahead of Mario.
- **No charges and no cooldown.** Limit glide duration instead: baseline **1.5 seconds**, configurable and subject to playtesting, including slides over water.
- A slide may carry onto dry ground after starting on wet ground; the duration cap still applies. The same slide cannot restart its cap by crossing more wet ground.
- **Lysate Cell:** Each extra Special stock becomes **one additional base glide duration**, without adding consumable charges.
- Cooldown reduction extends glide duration through the Special slot's duration multiplier instead of reducing a recharge.
- A fresh slide can glide immediately when it reaches wet ground.

## 8. Mobility and attack-speed item mappings

| Item | Mario behavior |
|---|---|
| **Paul's Goat Hoof** | Increases Mario's maximum **analog-stick-driven running speed**, preserving acceleration and analog control; Turbo speed scales with the buff. Sprint input is not required for ordinary movement. |
| **Mocha** | Increases movement speed and FLUDD spray rate; Turbo benefits from the speed increase. |
| **Energy Drink** | The **sprint button** activates sprint-dependent item bonuses, including Energy Drink, but sprint alone does **not** increase Mario's baseline movement speed. Do not use analog-stick magnitude as a sprint substitute. |
| **Red Whip** | Out-of-combat movement speed increase; also affects Turbo speed. |
| **Wax Quail** | Applies its sprint-dependent momentum effect when the **sprint button** is active and its usual trigger conditions are met; **does not alter side jump or spin jump**. |
| **Hopoo Feather** | Additional midair jumps behave as **perfectly timed jump-chain inputs**, enabling Mario to reach triple-jump behavior in the air. Preserve side jump/spin jump. |
| **Soldier's Syringe** | Faster FLUDD spray rate without additional water consumed per second. |
| **H3AD-5T v2** | Buffs Ground Pound; see §6. |
| **Shuriken** | Triggered by water-hit attacks from any nozzle; queued Shurikens stagger by 0.5 seconds; see §3. |

Not every item needs to be equally valuable on Mario; preserve item identity instead of inventing unrelated bonuses solely to equalize power.

## 9. Cooldown reset and equipment items

| Item | Mapping |
|---|---|
| **Bandolier** | Ammo pickups refill FLUDD water and restore Ground Pound full-strength availability. Other relevant recharge interactions can be evaluated during implementation. |
| **Brainstalks** | During its elite-kill effect, temporarily grant **unlimited FLUDD water** and unrestricted full-strength Ground Pounds. Duration follows the item's normal effect. |
| **Alien Head** | Applies the Mario-specific cooldown scaling in §5. |
| **Purity** | Applies Mario-specific cooldown scaling and retains vanilla luck reduction. |
| **Hardlight Afterburner** | Extra full-strength Ground Pounds and reduced Ground Pound recovery (§6). |
| **Lysate Cell** | Additional Wet Slide Special charges (§7). |
| **Fuel Cell** | Vanilla orange-equipment charges and equipment cooldown benefit. |
| **Soulbound Catalyst** | Vanilla orange-equipment cooldown reduction on kills. |

## 10. Clone / AI integration

- **Goobo Jr.** spawns an independently controlled Mario clone with **its own Rust simulation state and its own FLUDD water tank**.
- A RoR2 AI-to-Rust control adapter must issue Mario movement/nozzle/attack commands; merely cloning `CharacterBody` does not guarantee a Rust-controlled clone can act.
- Clone item-stat interactions should use compatible RoR2 hooks and the same water/damage rules, subject to vanilla Goobo Jr. constraints.

## 11. Adapter implementation checklist

- Route RoR2 `CharacterBody` / inventory / stats / health / equipment and item counts to a **centralized Rust-facing modifier snapshot**.
- Route Rust-originating FLUDD water hits and Ground Pound hits through RoR2's **damage and proc pipeline**, with water's per-target 1-second throttle and 0.5 coefficient.
- Make water attacks distinguishable by origin/direction while sharing a unified proc budget per target.
- Implement water refill from healing **effects/rates**, including when actual HP gain is zero; guard against recursive healing/item loops.
- Keep skill classification (**Ground Pound = Utility; Wet Slide = Special**) distinct from physical input mappings and native Sunshine behavior.
- Keep charge accounting (extra airborne nozzle uses, Ground Pound full-strength uses) and Wet Slide duration in Rust or a clearly synchronized adapter; avoid divergent state.
- Support independent Rust state and AI inputs for Goobo Jr.

## 12. Open tuning and implementation questions (not blocking this ruleset)

- Numeric healing-to-water conversion, particularly low lifesteal conversion, and prevention of runaway healing/proc loops.
- Baseline Ground Pound full-strength recovery time; Hardlight Afterburner extra-charge count and recharge semantics.
- Tune diminishing-return nozzle scaling (no hard 3× cap) for Hover duration, Rocket height, Spray duration, Turbo speed, and Wet Slide duration/distance; tune Ground Pound recovery separately.
- Whether water-hit proc throttling should apply across simultaneous independent targets exactly as specified in §3 (current rule: yes), and how Shuriken's 0.5-second queue interacts with its own vanilla charge replenishment.
- Wet Slide base-duration tuning and extra airborne FLUDD-use edge cases.
- Precise interpretations of Wax Quail momentum when sprint input is held, without disrupting Sunshine's side/spin jumps.

---

**Design intent:** Preserve Sunshine's play feel, make vanilla RoR2 items meaningfully modify Mario, and avoid per-item rewrites where standard RoR2 stats, inventory, damage, healing and equipment hooks already suffice.
