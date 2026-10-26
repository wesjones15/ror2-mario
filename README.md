> modern/1.5.0 branch: targets RiskOfRain2_DLC4 (Unity 2021.3.45f2). Build/API validation only; in-game validation is pending. Legacy instructions below describe the previous release.

# ror2-mario

Mario's first-stage pod shows the Sunshine Pianta statue and animated M portal.
Interact/X opens the Delfino Plaza still, plays the arrival sting and original
portal-exit sound, then runs the approved 0.8-second particle reveal, wahoo,
normal-rate spin with a gentle 0.8-second descent, and ordinary jump landing.
The portal returns to swirling colors; Luigi uses green cap particles and his
existing pitched voice. The full cap-in-hand Sunshine sequence remains in the
Rust viewer and original native playback API, but the game uses the short profile.

Portal animation is rendered once on the asset worker and cached under the user's
ignored disc assets. The statue/gate geometry uploads once; hidden arrival Mario
stops being processed after handoff. Stock pod graphics are suppressed except the
original descent fire: its mesh particles are 3.5x larger, with the emitter anchored
directly to the pedestal base. Its original material and emission rate remain.
Stock collision is removed after exit. Vanilla falling/crashing sound events remain.
The camera faces the M and the statue retains its own pedestal collision. The
normal pod remains available if optional disc assets or rendering cannot load.
The revised profile is viewer/build/boundary checked; RoR2 playtesting is pending.
The entrance preloads Luigi's mesh and refreshes the seated rider's selection from
the master's loadout before activation, so late passenger/skin initialization does
not leave Mario in Luigi's portal sequence. Green particles and voice pitch follow.
Luigi's entrance is now confirmed by the user's playtest. The fire starts from
`MarioPodDescent.OnEnter`, even when native preparation and state entry run in
either order; it resolves the exact emitter on the spawned clone and logs
`Statue descent fire started`. After landing, the statue, portal camera, pedestal
collider and exit point use a fixed world anchor instead of the animated stock
pod bone. The exit-position fix still needs an in-game check.
The user's next playtest confirmed the fire starts, but the old -950 cm offset
placed it far below the statue. That extra offset is removed; this height correction
still needs an in-game check. The viewer's approximate pivot is not height proof.

Mario's four approved skill portraits now render in Unity on first run after disc
extraction, using the existing native models, moving effects and gameplay slide tuning.
FLUDD uses blue, the three front-facing nozzles form an orange triangle (the wider
Hover is enlarged), Ground Pound uses medium purple, and Wet Slide uses green.
The generated 256x256 BMPs persist under the resolved
`assets/skill-icons/approved-v3/` directory and load on later starts. Delete a card to
regenerate it. No preview PNGs or extracted artwork ship with the plugin.

Native scene/ABI checks: `dotnet run --project tests/PortraitCheck -c Release --
../libsms/target/release/sms.dll ../libsms/assets`. The checks cover spraying water,
the nozzle-only triangle and Hover size, airborne pound flames, and a moving slide's
refraction ribbons. Unity capture appearance still needs manual in-game review.

Portraits use the viewer's fixed centimetre scale, independently of the gameplay
survivor height. The camera follows the rendered feet during fast falls. Portrait
water uses the native 0.35 tint on the flat blue background; gameplay keeps its own
configured water opacity. Effect meshes belong to the portrait root and deactivate
before destruction, so another plugin cannot capture Mario's spray. Cache revision v3
regenerates existing cards automatically. These fixes are build/boundary-checked;
the corrected Unity captures still require manual in-game review.

Risk of Rain 2 host for [libsms](../libsms): Super Mario Sunshine's Mario movement in RoR2.
Targets the 2023 build (1.2.4.4, Unity 2019.4.37, BepInEx 5). It compiles against the copy in
`../Risk Of Rain 2 2023` and installs into the Steam install at
`D:\SteamLibrary\steamapps\common\Risk of Rain 2`. Shared host code lives in
[`../ror2-common`](../ror2-common), which is compiled in.

## MarioTest (first test character)

A survivor called **Mario**: Commando's body and HUD, moved by libsms instead of RoR2's
character controller, and drawn and heard as the disc's Mario.

How it works:
- `sms.dll` is `libsms/crates/libsms-ffi`, the sim behind a C ABI.
- `MarioDriver` runs it on the client that owns the body. Each `FixedUpdate` it turns off the
  KinematicCharacterMotor, steps the sim, and puts the body at Mario's feet. RoR2's own
  network transform shows that to everyone else.
- `UnityHost` answers the sim's ground, roof and wall probes with raycasts against RoR2's world
  layer. Mario is as tall as Commando (his collision capsule; `Look.Height` overrides), which
  sets the metres per Sunshine unit, and so his speed, jumps and FLUDD's reach.
- `MarioCombat` answers the sim's enemy queries with the hurt boxes of characters on other
  teams. Mario stomps them (bouncing higher on each stomp in a row, as in Sunshine), ground
  pounds them (a shock wave on landing too), dives into them, and is kept out of them; FLUDD's
  drops hit them. Each hit lands as RoR2 damage from Mario's body (ror2-common's `NativeHit`),
  so his damage stat, crits and on-hit items apply. Water damage is this plugin's, not
  Sunshine's.
- Wet-slide side waves use the disc's broad crossed water ribbon and indirect wave maps,
  with a faint pale-water tint under them (`Look.SlideWaveTint`) so they show even where refraction doesn't,
  through FLUDD water's existing RoR2 refraction material (`Look.WaterRefraction`).
  Their scale follows Sunshine's looping emitter curve; droplets and spray remain separate.
  Wave normals and coverage are uploaded separately, with compensated strength so RoR2's
  distortion shader retains the subtle crests. The log reports pixels passing its cutoff.
- Enemies Mario kills drop a yellow coin from the Sunshine ISO, including item-proc kills.
  Touching one restores one life-meter segment (one eighth of full health-meter capacity),
  through RoR2 healing, with Sunshine's pickup chime and sparkles. Coins fall onto terrain,
  spin, and expire after 60 seconds. Friendly kills and other survivors' kills do not drop them.
- **Several Marios.** Any Mario takes any Mario's coin: a client's own machine tells the server when its Mario
  touches one (the server's copy of that body can lag), as well as the server's own check. A belly slide glides
  over another Mario's wet spots as over his own (every machine's sims ask each other, `sms_sim_wet_at`). A
  Backup Magazine's charge arrives filled, including on a body that spawns holding one.
- **Replay backlog.** Another player's Mario is played back one recorded step per update, hurrying when behind. A
  backlog is replayed down to the prebuffer, spread over updates by a 6 ms budget (at least four steps), and in
  full beyond 90 steps (about 1.8 s), so a slow machine never falls seconds behind. (0.3.0 capped it at four steps,
  which a machine under about 13 updates a second could never drain.)
- Movement speed items scale how far Mario gets each step (`Sim.SpeedFromItems`).
- `MarioItems` hooks RoR2's items to Mario's moves; [ITEMS.md](ITEMS.md) goes through them
  all. His moves sit in RoR2's skill slots so items keep their charges: FLUDD is the primary,
  extra mid-air FLUDD uses the secondary (Backup Magazine), full-strength ground pounds the
  utility (Hardlight) and wet belly slides the special (Lysate Cell extends their duration).
  Wet slides require wet ground and have no charge or cooldown. The rules for these moves
  (one free FLUDD use per jump, duration-limited wet slides, Hopoo Feather's chain jumps, the Spray's
  rate and limit) are libsms's `Limits`, all off unless a host sets them, so libsms's Mario
  stays Sunshine's.
- ror2-common's `NativeBody` does what RoR2's motor would for the items: jump pads, knockback
  and Milky Chrysalis throw Mario, stuns and roots hold him, RoR2's sprint button sprints.
  Jump pads preserve RoR2's complete ballistic trajectory and gravity until collision,
  bypassing Sunshine's air braking and speed caps so elevated targets remain reachable. A move
  breaks the flight once it has got going (about a tenth of a second): a dive, a ground pound, the
  Rocket or Hover, the Turbo held, a spin jump or a mid-air jump. Normal air control and gravity
  come back, and the pad's sideways speed carries on beside the move, bleeding off over a couple
  of seconds (`pad_carry` in libsms).
- `MarioMain` replaces Commando's main state, so RoR2's own movement, jumping and skills stay
  off.
- `MarioView` loads libsms's view (`sms_view_*`) the first time the player's Mario starts.
  - It has the body, hands, cap, FLUDD and nozzle, posed from the sim each frame and skinned
    in Rust.
  - Each material's TEV is baked into one texture, which ror2-common's `ModelView` draws with
    RoR2's own shader and lighting. Commando's model is hidden meanwhile.
  - The game's sounds (voice, footsteps, FLUDD) play on our own audio output, since RoR2's
    audio is Wwise, at RoR2's master × SFX volume. A short Delfino Plaza sting plays when Mario emerges on the run's first stage; RoR2 keeps its soundtrack.
  - A Mario no player controls (Goobo Jr.'s clone) gets a view of his own, silent and drawn in
    RoR2's own gummy clone material (`matGummyClone`, a DLC1 addressable; `Look.GummyClones`).
  - Its AI master only walks him (common `SurvivorAi.Drivers`: stand and face the enemy within
    spray range, else run at it, else follow the leader). `MarioBrain` presses his buttons: in
    range it locks on (the left bumper's job for a player, so FLUDD aims at the enemy) and holds
    R, a full press: the Spray is his gun, and it refills the tank when it runs low; close up it
    jumps and ground pounds; further off it sprints, hops, and dives at middling range.
- When Mario dies he falls down for good as in Sunshine (the "die" clip and his "down" voices)
  and Sunshine's game over song plays (falling off the map plays the miss jingle, the "Too Bad!" one,
  before he is put back on the surface). RoR2 removes a dead body a second
  after it comes to rest, so `MarioCorpse` plays the fall out (`Look.DeathFall`,
  `Look.DeathFallSeconds`, `SpawnMusic.DeathSting`).
- No R2API: the survivor is registered through RoR2's content-pack hook.

Controls, on a gamepad, by button position (Sunshine's layout, whatever your RoR2 binds):

| Pad              | Sunshine | Does                                            |
|------------------|----------|-------------------------------------------------|
| left stick       | stick    | walk, run                                       |
| A                | A        | jumps, wall kicks                               |
| B                | B        | dive, punch                                     |
| right trigger    | R analog | spray while moving, up to full pressure         |
| left trigger     | L        | ground pound (in the air), crouch; aims FLUDD at the crosshair |
| left bumper      |          | hold to lock onto an enemy and aim FLUDD at it, grounded or airborne |
| right bumper     | X        | switch nozzle: Spray, Hover, Rocket, Turbo; with LB held, next target |
| D-pad down       |          | fill FLUDD's tank (`Fludd.DpadRefill`)          |
| X, Y             |          | RoR2's interact and equipment, as ever          |
| RoR2's sprint    |          | sprint, for sprint items (no speed of its own)  |

On keyboard and mouse your RoR2 binds stand in: jump (A), primary (spray), sprint (full
spray), secondary (B), utility (L), special (switch nozzle).

The spray trigger works as in Sunshine (`Fludd.SprayBursts`): each press is a burst. A light
press drops a short splash just ahead, which a belly slide then glides over; a full press
sprays at full reach for about 5 s and then fades until you let go or press deeper. The
trigger's travel is curved so the light zone is easier to hold (`Controls.TriggerCurve`). On
keyboard a tap of the primary is a light press and holding it sprays at full
(`Controls.LightTapSeconds`).

The Spray goes where the crosshair points while RT is down (`Controls.AimAtCrosshair`, on), as every RoR2
survivor's gun does: Mario runs where you steer and the nozzle follows the camera even at full pressure.
Only the Spray; Hover, Rocket and Turbo are unchanged. A lock-on (LB) still aims at its
target, and turning the setting off gives Sunshine's: the way Mario faces.

With `Hud.SunshineLayout` (on) the HUD is arranged like Sunshine's while you play Mario, without hiding
anything RoR2 needs: the life meter (the Shine Sprite sun) is in the top right, RoR2's health bar is gone and
its numbers (n/m) use RoR2's damage font at half their former size beneath the doubled life meter.
Difficulty, timer and stage sit below health. FLUDD's tank hugs the bottom-right corner with a small
margin, RoR2's cooldowns move to the bottom middle, and items remain at top middle.
RoR2's gold and lunar coin counters stay in the top left, with Sunshine's
coin counters on them. The moves are measured on the screen and put back when Mario isn't your body
(`MarioHudLayout`); the log says what was measured. Off: RoR2's places, the meter over the health bar's left end.

The HUD's skill icons show the pad buttons Mario actually uses (ror2-common `GlyphOverride`): RT for FLUDD
and the Air Pump, LT for the Ground Pound, B for the Wet Slide. On keyboard they are RoR2's binds, as
before. The skills' names and descriptions on the character select screen give the same buttons.

Holding LB locks onto an enemy (`Controls.LockOn`), as Olimar's RT does: the one nearest where the
camera points, within `Controls.LockRange` (60 m) and in line of sight, kept until you let go, on the ground or in the
air. FLUDD's head swivels and tilts to it, its water goes at it whichever way Mario faces or runs,
and R sprays it; a standing Mario turns to face it. Pressing RB with LB held steps to the next
target toward where the right stick points (right, with it centred), and doesn't switch nozzles.
With no enemy in reach it points where the camera looks (what's under the crosshair). Mario
doesn't crouch for LB, and R's full press (which plants and crouches him) is held back while it's
held, so the spray is on the move. L (LT) stays Sunshine's: it crouches, ground pounds in the air,
and while held points FLUDD where the camera looks (`Controls.CameraAim`), with the lock taking
over if LB is held too. On a keyboard there's no spare key, so the utility key is both L and the
lock (a fresh press in the air pounds; it doesn't crouch while locked) and the special key cycles.

### First run

Mario uses the same first-run extraction flow as Olimar. Set `Paths.Disc` in
`BepInEx/config/com.libsms.mariotest.cfg` to your own Sunshine ISO/GCM/CISO (any region),
or put the image beside the plugin or beside the assets folder. `SMS_DISC` also works.
RVZ/WIA/WBFS must be converted to ISO first. The plugin extracts models, animation, tuning,
sounds, HUD and particle effects on a background thread, and repairs incomplete old caches.
The worker also generates the arrival/death jingles in `AssetsDir/ror2_audio/`; no game
assets or generated music need to ship with the plugin.
A complete cache is reused on later launches without the disc.

`Paths.AssetsDir` overrides `assets_dir.txt` next to the plugin, then `SMS_ASSETS`, then
`assets/` next to the plugin. Empty pointer files are ignored. Mario remains registered while
extraction runs; his sim, views and coins wait for the complete cache. If extraction fails or
no disc is available, he keeps Commando as a stand-in; the log explains what to fix before
restarting. If selected before extraction finishes, select another survivor and Mario again
to refresh the lobby display.

### Build and install

Needs the Rust toolchain and the .NET SDK. Extracted game assets are optional at build
time; the plugin can build its cache from your own disc on first run.

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script builds `sms.dll` and `MarioTest.dll`, then copies them, plus an `assets_dir.txt`
pointing at `libsms/assets`, into `<game>/BepInEx/plugins/MarioTest/`. It compiles against the
game's own DLLs, so no NuGet game libraries are involved. The pointer can name a folder
that does not exist yet; first run creates it. The install copies only these three files,
and stings load from the cache. Use `-RoR2Dir` for another install and `-NoInstall` to
only build into `dist/`.

The log, `BepInEx/LogOutput.log`, should show `libsms ABI 21`, `Mario is 1.xx m tall`, then
`Mario registered`.
In a run it should show `Mario's sim started`, `Mario's view: 12 models, 26 textures; sound
...`, and one line per status change (turn that off with `Debug.LogStatus`).

Config is in `BepInEx/config/com.libsms.mariotest.cfg`: `Look.DrawModel`, `Look.Height`,
`Look.Effects`, `Look.PoundFlames`, `Look.DeathFall`, `Look.GummyClones`, `Sound.Enabled`, `Sound.Volume`,
`Sim.*` (jump height, buffer and chain grace, speed items, solid enemies, stomp bounce, pound
jump, timed stomp jump, shortest hop), `Fludd.*` (nozzle cycling, spray reach, spray bursts,
rocket height, unlimited water, D-pad refill), `Controls.*` (camera aim, trigger curve,
keyboard light tap), `Hud.*`
(Sunshine's water tank gauge: on or off, where, how big), `Damage.*` (stomp, ground pound and its shock wave, damage
per metre a pound falls, super pounds igniting, dive, water per drop), `Items.*` (see
ITEMS.md: the skills' charges and recharges, cooldown stretches, water from healing, the
water's proc limit, Shuriken and Wax Quail), `Sim.ExternalForces`, `Sim.CrowdControl` and
`Paths.AssetsDir`.

FLUDD's tank empties as in Sunshine and fills back up while you hold spray standing in water
(anything RoR2 builds as water: Unity's Water layer, or colliders, materials or footstep
surfaces named for water; the log says what each stage has, and when Mario steps in and out),
with D-pad down, and on each new stage, since Mario starts every stage afresh. The gauge is
Sunshine's own, drawn from the disc (re-extract older assets for its `game_6/` folder).

Mario automatically unsticks after staying within about 13 cm (at his default height) for
three seconds while fallen, sliding on his belly, or trying to fall. Recovery checks for
supported ground with room for his standing body above and around the obstruction, then
tries his last verified safe ground if necessary. If nothing is clear, it retries after
another three seconds. Standing still, hovering, hanging from a ledge, death, pod occupancy,
and crowd control do not count toward the timer. External teleports clear the timer and
remembered ground. Recovery preserves health, items, nozzle and tank; the log reports
`Mario unstuck after 3 seconds`.

The detector/search is ror2-common's `Unstuck` (Mario supplies which of his statuses count as
fallen, `MarioUnstuck.Fallen`, and the ground probe). Its regression check runs without the game
(`dotnet run --project ../ror2-common/tests/UnstuckCheck/UnstuckCheck.csproj -c Release`); the
status check is here:

```
dotnet run --project tests/UnstuckCheck/UnstuckCheck.csproj -c Release
```

In-game verification (Unity collision queries require the game): trap Mario under the opened
pod door and in terrain, and confirm recovery after three seconds; confirm a free fall or
moving slide continues normally, an idle/hovering/hanging Mario stays put, and entering a
pod, a teleport or death clears a partly elapsed timer. Check that the destination has solid
ground and headroom and that FLUDD's nozzle and remaining water survive recovery.

### Fall damage

Mario takes fall damage only beyond 40 body heights; a full default rocket reaches about
34.7 heights, so returning to its launch floor is safe. Ground pounds are always safe.
A dive landing fully protects drops through 80 heights and halves damage beyond that.
Normal damage grows by 1.25% maximum health per extra height, capped at 90% before dive
reduction. Water, Yoshi and slow landings also protect him. RoR2 handles fall immunity items,
armor and Frailty/Eclipse modifiers. Tune these rules with `FallDamage.*`.

Headless checks: `dotnet run --project tests/FallDamageCheck -c Release` and libsms's
`cargo test -p libsms --test scenarios fall_impact`. In game, verify a full rocket return,
an extreme normal fall, a dive from the same height, and a ground pound; repeat with
H3AD-5T v2 and Frailty/Eclipse when relevant. Multiplayer remains untested.

### Not there yet

- Multiplayer is implemented but has never been run in a two-player game: other players see
  Mario through a shadow sim that replays his inputs (`MarioPuppet`), and a client Mario's hits,
  falls and coin pickups go through the server. Every player needs the same plugin build and their
  own extracted `assets/`. See [../ror2-common/MULTIPLAYER.md](../ror2-common/MULTIPLAYER.md) for how it
  works and the test plan.
- Yoshi.
- The skill icons are Commando's.

Offline / friends-only co-op only.

FLUDD's water impacts leave terrain wet patches, slightly darkening the ground
(`Look.GroundWetness`, 0.12 by default) before fading as they dry. A light RT burst wets
six circles in a trail from Mario's feet forward in his aim, as a short splash does in Sunshine. Wet-slide eligibility
uses these same surface footprints, rather than a nearby droplet's distance.
Shallow water (anything RoR2 builds as water, see `Water in this stage`) also counts as
wet ground (`Sim.ShallowWetSlide`). A belly slide must reach wet ground to begin gliding;
dry ground stays a normal slide. There are no wet-slide charges or recharge. Every wet
slide has the `Items.WetSlideSeconds` duration cap (1.5 s by default), including in water.
Cooldown reduction and Lysate Cell extend that cap; a new slide on wet ground can start
immediately. Old `WetSlideCharges`, `WetSlideAnywhere`, `WetSlideOnWater`, and
`WetSlideRecharge` config entries are ignored.
The wet slide's spray is `Look.SlideSprayScale` (1.4) times Sunshine's size. The extra
stand-in splashes remain off (`Look.SlideSplashes`).

When Mario falls off the map, Sunshine's "TOO BAD!" letters (coloured, dropping in and bouncing) play as the screen opens from
black (he is untouchable for those seconds); when he dies with none left, its GAME OVER screen (black with the letters cut out of it, the scene
showing through, and a red box coming in behind it at the end) plays with the song (`Hud.ScreenEffects`). With an extra life
(Dio's Best Friend, a Pluripotent Larva) a death is a miss instead: the miss jingle and TOO BAD, with Sunshine's "MARIO x N" lives
banner (`Hud.LivesScale`) sliding in and counting down as RoR2 uses the life. A fall off the map starts TOO BAD (and its jingle) as soon as he is `Hud.TooBadDrop` (20 m) below where he last stood with no ground under him for a long way, and he is put back later, when the stage's kill zone or 150 m takes him. A real game over plays TOO BAD's letters under the wipe as the game does. Both are drawn smoothly between the game's 30 frames a second, at `Hud.ScreenSpeed` (1.25) times its pace. A fall off the map shows the banner too if he has
extra lives, and never otherwise. They come from the disc's HUD and wipe pictures; a folder extracted
before the wipe pictures existed gets them when the disc is found (`Paths.Disc`).

Sunshine's coin counters (`Hud.CoinCounters`) replace RoR2's gold and lunar coin counters: the yellow coin shows
RoR2's gold and the blue coin the lunar coins (once there is one), drawn from the disc's coin icon and digit
pictures and green paint backplates like the game's HUD (more digits than its three: up to seven). Each sits where the counter it replaces
was, or where `Hud.CoinX`/`CoinY` say, `Hud.CoinScale` big. Each yellow coin Mario picks up (the ones his kills
drop) is also worth `Coins.Gold` (1) RoR2 gold. The 3D lunar coins in the world are unchanged.

A Shine Sprite is the teleporter's trigger (`Shine.*`, `MarioShine`). When a teleporter has charged (its boss dead) a
Shine from the disc rises above its middle, `Shine.Height` (1.6 m) up, humming (`MSD_SE_SHINE_EXIST`, from where it hangs, until it is taken), for the player who is Mario and nobody
else (a local change like the coin counters: other players and non-Mario players see no difference). Touching
it, by walking or jumping into it (no button; `Shine.TouchRadius`), is Sunshine's Shine Get: Mario lands
(`MARIO_STATUS_WIN_DEMO`, libsms `shine.rs`) and holds it over his head, the camera flies the disc's own
`camera_demo_shine_get_*` path, the Shine plays `shine_demo_shine_get` and glows, the Shine Get fanfare
(`MSD_BGM_GET_SHINE`) plays, and "SHINE!" flies in (`Hud.ScreenEffects`). `Shine.Seconds` (6.3) from the pose
the stage ends, for everyone: the teleporter is finished as if Mario had interacted with it, so the stage is
the one the run was going to. Two exceptions, both the teleporter's own rules: a Lunar Seer's choice made
before the boss fight is kept (the exit controller already has its destination), and on the last stage before
the moon, where the Celestial Portal stands beside the teleporter, the Shine takes the moon, not the loop.
The other portals are never taken. A client's Mario asks the server to finish the teleporter
(`MarioNet.ShineMsg`); the other players' puppets show him in the pose. The Shine and the camera path are extra
files fetched from the disc with the wipe pictures (`Paths.Disc`). A lunar coin plays Sunshine's actual blue-coin
pickup sound (`MSD_SE_SY_BLUE_COIN_GET`; `SpawnMusic.Cues` controls playback).

Sunshine's life meter (`Hud.LifeMeter`) shows Mario's health in place of RoR2's health bar: the
Shine Sprite sun from the disc, a hub and rays in a ring of eight, lit in whole rays by his health as
a share of his full health and shield (RoR2's own values are untouched), the shield's rays after them
in the cyan of Sunshine's air meter. The hub dims to grey under half, as in the game. RoR2's bars
hide, with smaller damage-font numbers below the sun (`Hud.HideHealthBar`); the meter sits at top right
with `Hud.SunshineLayout`, or where `Hud.LifeX`/`LifeY` say, `Hud.LifeScale` big.

While locked on (`Controls.LockOn`), the same sun, alone and with its empty top filled to ten rays,
turns on the enemy (`Look.LockMarker`): ror2-common's `LockMarker` puts it where Olimar's cursor
goes (on the line from Mario's chest to the middle of the enemy's hurt boxes, `margin` in front of
the first it enters) and it faces Mario. Its rays are the enemy's health (`Look.LockMarkerHealth`),
`LockMarkerSize` metres wide, `LockMarkerSpin` degrees a second.

A powerful hit knocks Mario over (`Knockdown.*`): one that takes a quarter of his max health and
shield (`Threshold`), apart from falls and damage over time, plays Sunshine's short fall away
from the attacker (`damageExec`, libsms's `sms_sim_knock_down`) with his big hurt voice, but
costs the sim's health nothing; RoR2 keeps his health. He isn't stunned: once the slide has nearly
stopped, A, the stick, B, L or R gets him up at once (`GetUpEarly`). A dive isn't knocked out of,
as in the game; a ground pound is, as in the game (`InterruptsPound` off keeps it going). A second
knock-down can't land for a second. Hits are the server's, so a client playing Mario (untested)
gets none.

Mario's movement effects come from the disc's particle files: starting/skidding/landing dust,
wall-slide and wall-kick dust, bump stars, the ground-pound smear and landing burst,
spin-jump swirls, belly-slide smoke/spray and enemy-stomp bursts.
`Look.Effects` controls these independently of `Look.PoundFlames`, which controls the super
pound's flames and fiery landing. Both default to on and work with sound disabled.
Smoke stays where it was emitted when Mario jumps, and particle motion blends between
updates for smooth drawing at the display's frame rate.
Both normal and super ground-pound landing effects stay at the impact point when Mario
bounces back up. The falling super pound's flames remain attached to Mario.
Every smoke or dust cloud (run-start dust, landings, belly-slide smoke, wall slide and wall
kick, and normal and super pound smoke) retains its birth height while spreading and
fading, so its own upward velocity cannot lift it, in a rebound or otherwise. They all follow
`Look.SmokeSpeed`.
`Look.SmokeSpeed` defaults to 2 (twice the original smoke/dust playback speed); set it to 1
for the disc's timing. This changes smoke only, including its motion and lifetime.

FLUDD's water uses RoR2's refraction material with a faint blue tint. `Look.WaterOpacity`
defaults to 0.12 before the shared glass tint multiplier of 0.25 (effective peak drop
opacity 0.03, reduced further by its mask and lifetime fade). Splashes use the same shared
multiplier. Juice keeps its nearly opaque tint. `Look.WaterRefraction` controls distortion;
the log confirms the material loaded with `FLUDD's water refracts`.

## Arrival music

Mario plays approximately five opening beats (2.5 seconds) of Delfino Plaza once a run, as he leaves the pod on the first stage; later stages and respawns stay quiet. When he dies, Sunshine's game over song plays (about 8 seconds); falling off the map plays the miss jingle ("Too Bad!", about 4.5 seconds) before he is put back. A lunar coin picked up plays the blue coin jingle (`SpawnMusic.Cues`). SpawnMusic.Enabled, SpawnMusic.DeathSting, SpawnMusic.Volume and SpawnMusic.DeathVolume (1.15, extra gain for the death sting) control them; master and music volume also apply. The build synthesizes spawn.wav, death.wav (the miss jingle), gameover.wav and bluecoin.wav from your extracted disc audio into the ignored dist folder. Rebuild after extracting assets if a clip is missing. No music data is committed.
