# Mario in Risk of Rain 2

Play **Super Mario Sunshine's Mario, with FLUDD**, as a Risk of Rain 2 survivor. Chain jumps, wall kicks, dives and wet slides; fight with water, stomps and ground pounds; and use Hover, Rocket and Turbo to cross RoR2's stages.

This repository is the **C# RoR2 plugin**. Mario's movement and FLUDD run in [libsms](https://github.com/wesjones15/libsms), a game-independent Rust reimplementation using [doldecomp/sms](https://github.com/doldecomp/sms) as a behavioral reference. RoR2 supplies the stages, enemies, health, inventory and progression. The plugin connects the two and adapts Mario's moves for RoR2 combat and items.

Models, animations, textures, effects and sounds are extracted locally from **your own Sunshine disc image**. Game assets are not included in the repository or release packages.

The `modern/1.5.0` branch targets the project's **RoR2 DLC4 build, Unity 2021.3 and BepInEx 5**. For packaged downloads and installation instructions, start with [ror2-characters](https://github.com/wesjones15/ror2-characters). The `legacy/1.2.4.4` branch is frozen.

## Playing Mario

Mario uses Sunshine-style movement rather than RoR2's normal character controller. His main resources are **water, airtime and movement**, with RoR2 skill slots supporting item interactions.

- **Movement:** normal/double/triple jumps, spin jumps, backflips, wall kicks, ledge hanging, dives and belly slides. Jump scale and movement are adapted for RoR2 terrain.
- **FLUDD:** Spray fights at range; Hover extends airtime; Rocket gains height; Turbo gives speed and a corkscrew attack. Water damages enemies, has a bonus against fire-related targets, and can deflect enemy projectiles and extinguish fire trails.
- **Close combat:** stomps, dives and ground pounds deal RoR2 damage. A longer pound ignites, a stick circle during its fall makes it spin, and Turbo's corkscrew can build into a stronger flaming attack.
- **Recovery:** kills drop healing coins. Mario can refill FLUDD in stage water, through healing-related item effects, with D-pad down, and at the start of each stage. Fall and stuck recovery support the custom controller.
- **Sunshine presentation:** the life sun, tank and coin counters, character sounds, movement effects, statue-pod entrance and teleporter Shine sequence accompany the moves. RoR2 keeps its soundtrack, with short Sunshine arrival and loss cues.

This is an adaptation rather than a complete Sunshine port. Water is an offensive weapon, item scaling changes several move limits, and deep-water swimming is not implemented.

## Controls

Gamepad buttons are **by physical position**, using Xbox-style names and Sunshine's layout, independently of your RoR2 skill bindings. The pause menu also has a controls page.

| Gamepad | Action |
| --- | --- |
| Left stick | Walk/run; circle the stick for spin inputs. |
| A | Jump and wall kick. |
| B | Dive, punch and carrying actions as the current state permits. Dive onto wet ground to wet-slide. |
| Right trigger (RT) | Spray water / use the equipped nozzle. Analog pressure controls the Spray. |
| Left trigger (LT) | Ground pound in the air; crouch on the ground; aim FLUDD at the crosshair. |
| Hold left bumper (LB) | Lock onto an enemy and aim FLUDD at it while moving or airborne. |
| Tap right bumper (RB) | Swap between Spray and the selected alternate nozzle. While locked, cycle targets instead. |
| Hold RB for 0.5 seconds | Open the alternate wheel; use the right stick to choose Hover, Rocket, Turbo or No FLUDD. |
| D-pad down | Refill the tank when `Fludd.DpadRefill` is enabled. |
| X / Y | RoR2 interact / equipment. |
| RoR2 sprint | Activate sprint-dependent items; sprint adds no speed of its own. |

Keyboard and mouse use your **RoR2 bindings**:

| RoR2 binding | Mario action |
| --- | --- |
| Movement / Jump | Move / jump. |
| Primary | Spray / use the nozzle. Tap for a light splash; hold for full spray. |
| Secondary | Dive / B action. |
| Utility | L action, including a ground pound on a fresh airborne press; hold for lock-on. |
| Special | Tap to swap Spray/alternate; hold for the nozzle wheel and choose with the mouse. While locked, tap to cycle targets. |
| Sprint | Sprint / full spray. |
| Interact / Equipment | Normal RoR2 actions. |

**Aiming and pressure.** Spray follows the crosshair by default; lock-on takes priority while held. LB chooses a visible enemy near the camera's aim and retains it until released. With LB held, RB cycles toward the right-stick direction. A light trigger burst wets the ground ahead for a slide; a held full Spray fades after roughly five seconds until released or pressed deeper.

**Nozzle wheel.** Up selects Hover, right Rocket, left Turbo and down No FLUDD. A full stick tilt or far mouse movement chooses and closes it; releasing the button cancels. With No FLUDD, tapping RT or RB can perform contextual Melee Mario attacks when the optional Melee plugin and its extracted Mario files are available. The rest of Mario's movement remains usable without them. See [nozzle and attack details](docs/radial-menu.md).

## Combat and RoR2 items

Native attacks become RoR2 damage from Mario's body, so damage stats, crits, kill credit and applicable item effects use RoR2's systems. Rapid water hits have a per-target proc limiter; their visible hit rate does not give every droplet a separate item proc.

| Skill slot | Move/resource | Important item interaction |
| --- | --- | --- |
| Primary | FLUDD | Attack speed increases Spray rate. |
| Secondary | Air Pump: extra mid-air nozzle uses | Backup Magazine adds air uses. |
| Utility | Full-strength Ground Pound stock | Hardlight Afterburner adds charges; an empty-stock pound has reduced damage. |
| Special | Wet Slide | Lysate Cell extends glide duration; wet slides have no charge or cooldown. |

Cooldown reduction also improves moves that would otherwise gain little from it, extending hover/spray/glide and improving Rocket/Turbo performance. Hopoo Feathers supply extra chain jumps; Bandolier replenishes resources; Brainstalks removes resource limits while active. Healing, regeneration and lifesteal can refill water, with separate scaling and a lifesteal cap.

A wet slide requires wet terrain or shallow stage water. By default, it keeps gliding while wet and for **two seconds after the last wet contact**; cooldown reduction and Lysate Cell extend that time. Dry ground starts a normal belly slide.

Mario starts at **160 health, gaining 48 per level**. Coins from his kills heal through RoR2's health system. His fall-damage rules allow substantial drops, with extra protection for dives and ground pounds. These values and damage/resource rules are configurable in [`MarioTest/Plugin.cs`](MarioTest/Plugin.cs).

For detailed item behavior, see [ITEMS.md](ITEMS.md) and the [item ruleset](docs/mario_ror2_item_ruleset.md). Those documents contain historical notes; current configuration defaults and implementation take precedence. The [native behavior spec](https://github.com/wesjones15/libsms/blob/modern/1.5.0/docs/behavior-spec.md) records Sunshine mechanics, tuning and unresolved fidelity questions.

## Skins and optional content

| Character | Unlock and requirements |
| --- | --- |
| Mario | Default. Uses Sunshine assets. |
| Luigi | Hold **2,401 coins at once** as Mario. Also needs a Melee USA 1.02 disc for his model; the Mario plugin extracts it without requiring the Melee plugin. Higher jumps and a pitched voice distinguish him. |
| Il Piantissimo | Complete the **stage 4 teleporter event as Mario**, while alive. Uses the Sunshine disc. |
| Yoshi | Defeat **Mithrix's final form** while playing Mario; no final hit or escape completion is required. Uses the Sunshine disc, with flutter and juice-colored spray. |

Unlocks persist in the player's profile. A skin requiring additional assets must have those assets available before it can be offered.

Optional integrations include contextual Melee Mario attacks, carrying teammates and Pikmin pellets, and participation in Olimar's Ship Repair mode when the relevant companion content is installed. Bowser Jr./Shadow Mario is a separate **unfinished survivor**, hidden by default behind the shared `ShowUnfinished` setting in `BepInEx/config/Ror2Common.Experimental.cfg`.

**VR integration** provides a first-person head view, pointer-aimed FLUDD, controller arms, a nozzle wheel selected by pointing and a wrist HUD when a compatible VR mod is active. Its controls mirror the flat layout: right trigger sprays, left trigger is L, left grip locks and right grip swaps/opens the wheel; A jumps and B dives. Left-handed mode mirrors the layout. This integration still needs headset playtesting; Luigi/Yoshi use fallback hands, and full-screen effects remain desktop-only.

## First run and configuration

Install the precompiled character package and **wesleyasaurus-SharedFiles** with the release's required BepInEx dependencies. Then:

1. Put your Sunshine **ISO, GCM or CISO** in `BepInEx/plugins/wesleyasaurus-SharedFiles/put_games_here/`. Any filename works; one level of subfolders is supported. Convert RVZ/WIA/WBFS to a supported image first.
2. Launch RoR2. The plugin extracts models, tuning, animation, audio, effects and HUD data on a background worker into `wesleyasaurus-SharedFiles/assets/sunshine/`.
3. Let extraction finish before playing. If the lobby display has not refreshed, select another survivor and then Mario again.

A complete cache is reused on later launches. Keep the disc available when updating if an extraction refresh is needed. Optional Melee content needs a **USA revision 1.02** Melee disc in the same folder. No disc images or extracted caches should be included when sharing the mod.

Current builds find the shared folder automatically. Older instructions using `Paths.Disc`, `Paths.AssetsDir`, `SMS_DISC` or `assets_dir.txt` do not describe this plugin's current setup. See the [SharedFiles guide](https://github.com/wesjones15/ror2-common/blob/modern/1.5.0/SHARED-FILES.md).

Mario's configuration is `BepInEx/config/com.libsms.mariotest.cfg`. The main groups cover movement (`Sim`), water/nozzles (`Fludd`), aim and input (`Controls`), health/damage/items (`Body`, `Damage`, `Items`, `FallDamage`, `Knockdown`), presentation (`Look`, `Hud`, `Sound`, `SpawnMusic`) and optional `VR` behavior.

If Mario appears as Commando or assets fail to prepare, inspect `BepInEx/LogOutput.log` for the missing disc/cache or native-library error. Use matching plugin/native DLLs from the same build.
## Current status and co-op

Recent fire effects, heat haze, nozzle UI, statue/portal changes, Bowser Jr. and VR include work awaiting in-game review. Multiplayer uses an owner-run simulation, remote input replay and server-routed gameplay outcomes. Peers need matching builds, compatible unfinished-content settings and their own local assets. Use offline play or private friends-only co-op; recent changes need further co-op playtesting.

Source overview and contributor instructions: [Development](https://github.com/wesjones15/ror2-mario/blob/modern/1.5.0/docs/Development.md).

## License and credits

Original contributions by **wesleyasaurus** are dedicated to the public domain under
[CC0 1.0 Universal](LICENSE), to the extent wesleyasaurus owns the relevant rights.
Attribution for those contributions is appreciated but not required. Third-party software
and original game content retain their respective rights and terms.
See [CREDITS.md](CREDITS.md) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
