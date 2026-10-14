# Mario and RoR2's items

How each item in the 2023 build (1.2.4.4, up to Survivors of the Void) works on Mario. The
rules come from `docs/mario_ror2_item_ruleset.md`. Almost every item needs nothing
Mario-specific, because Mario is wired into what items already act on:

- **Damage**: his hits are RoR2 damage from his body (`MarioCombat` through ror2-common's
  `NativeHit`), so damage, crit, on-hit, on-kill and proc-chain items all apply.
- **Stats**: move speed, attack speed, health, armour, regen and shields are RoR2's own stats.
  Move speed scales how far he goes; attack speed, how fast the Spray fires.
- **Skill slots**: his moves sit in RoR2's skill slots (`MarioSkills`), so RoR2 keeps their
  charges and cooldowns. Primary is FLUDD. Secondary is extra mid-air FLUDD uses. Utility is
  full-strength ground pounds. Special is wet belly slides.
- **Sprint**: RoR2's sprint button sets RoR2's sprinting. It adds no speed of its own.
- **Equipment**: RoR2's own equipment slot, on its usual button.
- **Thrown by the world**: anything that throws a body through RoR2's character motor throws
  Mario (ror2-common's `NativeBody`).

Mario-specific code is marked **(Mario)**, and a shared ror2-common piece **(common)**. Tuning
is in the `Items` config section; numbers the ruleset leaves TBD are config defaults.

Nothing here has been tried in game yet.

Newer builds' items are looked up by name (ror2-common's `ByName`), so a build without one just
reads none. Any item that works through the paths above works on Mario in a newer build too.

## Mario's moves as skills

| Slot | Move | Charges | Recharge |
|---|---|---|---|
| Primary | FLUDD | none (never spent) | none |
| Secondary | extra mid-air FLUDD uses | 0, +1 per Backup Magazine | 4 s each, one at a time; starts once he's touched the ground |
| Utility | full-strength ground pound | 1, +2 per Hardlight Afterburner | 6 s (Hardlight shortens it) |
| Special | wet belly slide, requiring wet ground | none (never spent) | none; duration-limited |

- **Mid-air FLUDD uses**: one is free each time Mario leaves the ground. Each start of a nozzle
  in the air costs a use, however long it runs; switching nozzle while airborne costs another.
  With none left, the trigger does nothing until he lands or a charge comes back. Uses don't
  touch the water.
- **Ground pound**: it always works. A charge is spent when the pound lands (on the floor or an enemy), so an interrupted pound costs nothing. Without a charge it does 50% of its damage. (Mario)
- **Wet slide**: a belly slide must reach wet ground before it can glide. FLUDD's water
  impacts leave drying wet patches; a light burst makes a broad patch ahead of Mario.
  Dry ground always gives a regular slide. Wet slides have no charges or recharge.
  The base glide lasts 1.5 s (`Items.WetSlideSeconds`), including on shallow water;
  after that it returns to normal sliding. Pull back to brake. A fresh slide on wet
  ground can glide again immediately. Cooldown reduction extends the duration instead
  of reducing a recharge. Each stock Lysate would have added grants another base duration,
  before the special slot's cooldown-item duration multiplier.

## Cooldown items stretch what has no cooldown

The **(common)** `SkillCooldown.Boost` is 1 / (cooldown ratio ^ 0.5). It's about ×1.15 for one
Alien Head, ×1.8 for four, ×3.2 for eight, with no hard cap. It stretches:
- hover time,
- how long a Spray press lasts before it fades (about 5 s at full),
- rocket height,
- Turbo speed,
- the wet slide's glide (from the special slot, which includes Lysate Cell).

It doesn't touch the tank's size or how fast it drains.

## Water

The tank is Sunshine's. It refills:
- standing in water,
- on D-pad down,
- on each stage,
- from items, as below.

| Source | Water | Items |
|---|---|---|
| Healing | the share of max health the heal asks for, even at full health | Bustling Fungus, Medkit, Monster Tooth, Power Elixir, Foreign Fruit, Lepton Daisy, Mired Urn, Planula, Weeping Fungus, Corpsebloom (counted once) |
| Regeneration beyond Mario's own | the same rate | Cautious Slug, Titanic Knurl, regen buffs |
| Lifesteal | a quarter, at most 5% of the tank a second | Leeching Seed, Harvester's Scythe, Super Massive Leech |
| Bandolier ammo | a full tank (and a charge back in every slot) | Bandolier |
| Brainstalks | bottomless while it lasts (and every charge free) | Brainstalks |

Healing water comes from a ror2-common hook on `HealthComponent.Heal` (`ItemHooks.Healed`),
which runs on the server. A client playing Mario in multiplayer doesn't get it yet.

## Item by item

**Need nothing Mario-specific** (damage, stats, procs, kills, equipment, minions):
- **White**: Armor-Piercing Rounds, Bison Steak, Bundle of Fireworks, Crowbar, Delicate Watch,
  Focus Crystal, Gasoline, Lens-Maker's Glasses, Oddly-shaped Opal, Personal Shield Generator,
  Repulsion Armor Plate, Roll of Pennies, Sticky Bomb, Stun Grenade, Topaz Brooch, Tougher
  Times, Tri-Tip Dagger, Warbanner, Paul's Goat Hoof (move speed).
- **Green**: AtG Missile, Berzerker's Pauldron, Chronobauble, Death Mark, Fuel Cell, Ghor's
  Tome, Hunter's Harpoon, Ignition Tank, Infusion, Kjaro's Band, Runald's Band, Old Guillotine,
  Old War Stealthkit, Predatory Instincts, Razorwire, Regenerating Scrap, Shipping Request Form,
  Squid Polyp, Ukulele, War Horn.
- **Red**: 57 Leaf Clover, Aegis, Ben's Raincoat, Bottled Chaos, Brilliant Behemoth, Ceremonial
  Dagger, Defensive Microbots, Dio's Best Friend, Frost Relic, Happiest Mask, Interstellar Desk
  Plant, Laser Scope, N'kuhana's Opinion, Pocket I.C.B.M., Rejuvenation Rack, Resonance Disc,
  Sentient Meat Hook, Shattering Justice, Soulbound Catalyst, Spare Drone Parts, Symbiotic
  Scorpion, Tesla Coil, Wake of Vultures.
- **Boss**: Charged Perforator, Defense Nucleus, Empathy Cores, Genesis Loop, Halcyon Seed,
  Irradiant Pearl, Molten Perforator, Pearl, Queen's Gland, Shatterspleen, Titanic Knurl.
- **Lunar**: Beads of Fealty, Brittle Crown, Defiant Gouge, Egocentrism, Eulogy Zero, Focused
  Convergence, Gesture of the Drowned, Mercurial Rachis, Shaped Glass, Stone Flux Pauldron,
  Transcendence.
- **Void**: Benthic Bloom, Encrusted Key, Lost Seer's Lenses, Needletick, Newly Hatched Zoea,
  Plasma Shrimp, Pluripotent Larva, Polylute, Safer Spaces, Singularity Band, Tentabauble,
  Voidsent Flame.
- **Elite aspects**, and every orange equipment that doesn't move its user.

**Through Mario's moves**:

| Item | On Mario |
|---|---|
| Soldier's Syringe, Mocha, Predatory Instincts, any attack speed | the Spray fires faster for the same water |
| Paul's Goat Hoof, Mocha, Red Whip, Hunter's Harpoon, any move speed | he runs faster; the Turbo too |
| Energy Drink | its bonus while RoR2's sprint is on; sprint alone adds nothing |
| Rose Buckler, Little Disciple, Weeping Fungus | while RoR2's sprint is on |
| Red Whip | FLUDD, pounds, stomps and dives end out-of-combat (common `NativeBody.MarkCombat`) |
| Wax Quail | a sprinting jump from the ground (normal, double, triple; not side or spin) carries further, by RoR2's formula (Mario) |
| Hopoo Feather | a mid-air jump per stack, each the next of the double/triple chain, with RoR2's feather puff; during a dive it pulls out into a fresh jump (Mario; libsms `Limits::air_jumps`) |
| Backup Magazine | +1 mid-air FLUDD use per stack |
| Hardlight Afterburner | +2 full-strength pounds per stack, faster recovery |
| Lysate Cell | +1 base wet-slide duration per stack, plus its special-slot duration multiplier; no charges |
| Alien Head, Purity, Light Flux Pauldron | slot cooldowns, and the stretches above (Purity keeps its luck penalty) |
| Bandolier | refills the tank and restores a charge in every slot |
| Brainstalks | bottomless tank, free charges, while it lasts |
| H3AD-5T v2 | a full-strength pound's landing also sets off the item's blast (1000-10000% by the drop, its reach growing too), on its own cooldown (10 s ÷ stacks). RoR2's interact-held stomp stays off Mario (common `ItemHooks`) (Mario) |
| Shuriken | FLUDD's water hitting counts as the primary in use, at most every 0.5 s, so throws follow the water and not the trigger (common `NativeBody.PulsePrimary`) |
| Every on-hit item | FLUDD's water procs at most once a second per enemy, every nozzle sharing it, at 0.5; the drops in between still do damage (common `ProcLimiter`) |
| Visions of Heresy | fires as FLUDD sprays |
| Hooks of Heresy | fires as a nozzle starts in mid-air |
| Strides of Heresy | runs on a ground pound (the pound is full strength when it's ready) |
| Essence of Heresy | runs when a slide would turn wet |
| Goobo Jr. | the clone is a Mario with his own sim and tank, walked by Commando's AI master (common `SurvivorAi.Drivers`) and pressed by `MarioBrain`, drawn in RoR2's gummy clone material |
| Milky Chrysalis | its lift throws Mario (common `NativeBody` velocity bridge) |
| Volcanic Egg, Eccentric Vase | RoR2 carries him as a vehicle; the sim waits |
| Fuel Cell, Soulbound Catalyst, Executive Card, every equipment | RoR2's own equipment slot |

**World**:
- Jump pads, geysers and knockback over 2 m/s throw Mario.
- Stuns, freezes and shocks stop his controls; roots (Tar, void jailers, Hooks of Heresy) stop
  him moving.
- `Sim.ExternalForces` and `Sim.CrowdControl` turn these off.

## Not done

- Goobo Jr.'s clone is silent: only the player's Mario has the disc's sounds.
- A multiplayer client playing Mario gets no water from healing (heals happen on the server).
- Mario takes no fall damage (RoR2 calls it from the character motor's landing).
- The skill icons are Commando's.
