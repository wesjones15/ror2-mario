# ror2-mario

Risk of Rain 2 host for [libsms](../libsms): Super Mario Sunshine's Mario movement in RoR2.
Targets the 2023 build (1.2.4.4, Unity 2019.4.37, BepInEx 5). It compiles against the copy in
`../Risk Of Rain 2 2023` and installs into the Steam install at
`D:\SteamLibrary\steamapps\common\Risk of Rain 2`. Shared host code lives in
[`../ror2-common`](../ror2-common), which is compiled in.

## MarioTest (first test character)

A survivor called **Mario (test)**: Commando's body and HUD, moved by libsms instead of RoR2's
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
- Movement speed items scale how far Mario gets each step (`Sim.SpeedFromItems`).
- `MarioItems` hooks RoR2's items to Mario's moves; [ITEMS.md](ITEMS.md) goes through them
  all. His moves sit in RoR2's skill slots so items keep their charges: FLUDD is the primary,
  extra mid-air FLUDD uses the secondary (Backup Magazine), full-strength ground pounds the
  utility (Hardlight) and wet belly slides the special (Lysate Cell). The rules for those
  (one free FLUDD use per jump, charged wet slides, Hopoo Feather's chain jumps, the Spray's
  rate and limit) are libsms's `Limits`, all off unless a host sets them, so libsms's Mario
  stays Sunshine's.
- ror2-common's `NativeBody` does what RoR2's motor would for the items: jump pads, knockback
  and Milky Chrysalis throw Mario, stuns and roots hold him, RoR2's sprint button sprints.
  Jump pads preserve RoR2's complete ballistic trajectory and gravity until collision,
  bypassing Sunshine's air braking and speed caps so elevated targets remain reachable.
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
- When Mario dies he falls down for good as in Sunshine (the "die" clip and his "down" voices)
  and Sunshine's miss jingle (the "Too Bad!" one) plays. RoR2 removes a dead body a second
  after it comes to rest, so `MarioCorpse` plays the fall out (`Look.DeathFall`,
  `Look.DeathFallSeconds`, `SpawnMusic.DeathSting`).
- No R2API: the survivor is registered through RoR2's content-pack hook.

Controls, on a gamepad, by button position (Sunshine's layout, whatever your RoR2 binds):

| Pad              | Sunshine | Does                                            |
|------------------|----------|-------------------------------------------------|
| left stick       | stick    | walk, run                                       |
| A                | A        | jumps, wall kicks                               |
| B                | B        | dive, punch                                     |
| right trigger    | R        | spray; all the way in plants Mario and sprays   |
| left trigger     | L        | ground pound (in the air), crouch; locks on, aims FLUDD |
| right bumper     | X        | switch nozzle: Spray, Hover, Rocket, Turbo      |
| D-pad down       |          | fill FLUDD's tank (`Fludd.DpadRefill`)          |
| X, Y             |          | RoR2's interact and equipment, as ever          |
| RoR2's sprint    |          | sprint, for sprint items (no speed of its own)  |

On keyboard and mouse your RoR2 binds stand in: jump (A), primary (spray), sprint (plant
and spray), secondary (B), utility (L), special (switch nozzle).

The spray trigger works as in Sunshine (`Fludd.SprayBursts`): each press is a burst. A light
press drops a short splash just ahead, which a belly slide then glides over; a full press
sprays at full reach for about 5 s and then fades until you let go or press deeper. The
trigger's travel is curved so the light zone is easier to hold (`Controls.TriggerCurve`). On
keyboard a tap of the primary is a light press and holding it sprays at full
(`Controls.LightTapSeconds`).

Holding L is Sunshine's L-button camera (`Controls.CameraAim`): FLUDD points where the camera
looks (at whatever is under the crosshair), and Mario, crouching, turns with the camera while
the stick side-steps.

Holding L also locks onto an enemy (`Controls.LockOn`), as Olimar's RT does: the one nearest where
the camera points, within 25 m and in line of sight, kept until you let go. FLUDD then points at it
(and a crouching Mario turns to it), so the spray trigger sprays it; with no enemy in reach it
points at the crosshair as before.

### Build and install

Needs the Rust toolchain, the .NET SDK, and libsms's `assets/` (extracted from your own disc;
see libsms's README).

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script builds `sms.dll` and `MarioTest.dll`, then copies them, plus an `assets_dir.txt`
pointing at `libsms/assets`, into `<game>/BepInEx/plugins/MarioTest/`. It compiles against the
game's own DLLs, so no NuGet game libraries are involved. Use `-RoR2Dir` for another install
and `-NoInstall` to only build into `dist/`.

The log, `BepInEx/LogOutput.log`, should show `libsms ABI 12`, `Mario is 1.xx m tall`, then
`Mario (test) registered`.
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

The detector/search regression check runs without the game:

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

- Other players see Commando. Only the player who's Mario sees and hears him; the pose and
  animation aren't networked yet. Mario's hits are the server's: a client playing him deals no
  damage yet.
- Yoshi.
- The skill icons are Commando's.
- Multiplayer is untested. Every player needs the plugin, and anyone playing Mario needs their
  own extracted `assets/`.

Offline / friends-only co-op only.

Mario's movement effects come from the disc's particle files: starting/skidding/landing dust,
wall-slide and wall-kick dust, bump stars, the ground-pound smear and landing burst,
spin-jump swirls, belly-slide smoke/spray and enemy-stomp bursts.
`Look.Effects` controls these independently of `Look.PoundFlames`, which controls the super
pound's flames and fiery landing. Both default to on and work with sound disabled.
Smoke stays where it was emitted when Mario jumps, and particle motion blends between
updates for smooth drawing at the display's frame rate.
Both normal and super ground-pound landing effects stay at the impact point when Mario
bounces back up. The falling super pound's flames remain attached to Mario.
Ground-pound smoke puffs also retain their birth height while spreading and fading,
so their own upward velocity cannot lift the smoke during a rebound. This applies to both
normal and super pound smoke, and both follow `Look.SmokeSpeed`.
`Look.SmokeSpeed` defaults to 2 (twice the original smoke/dust playback speed); set it to 1
for the disc's timing. This changes smoke only, including its motion and lifetime.

FLUDD's water uses RoR2's refraction material with a faint blue tint. `Look.WaterOpacity`
defaults to 0.12 before the shared glass tint multiplier of 0.25 (effective peak drop
opacity 0.03, reduced further by its mask and lifetime fade). Splashes use the same shared
multiplier. Juice keeps its nearly opaque tint. `Look.WaterRefraction` controls distortion;
the log confirms the material loaded with `FLUDD's water refracts`.

## Arrival music

Mario plays approximately five opening beats (2.5 seconds) of Delfino Plaza once a run, as he leaves the pod on the first stage; later stages and respawns stay quiet. When he dies, Sunshine's miss jingle plays (about 4.5 seconds). SpawnMusic.Enabled, SpawnMusic.DeathSting and SpawnMusic.Volume control them; master and music volume also apply. The build synthesizes spawn.wav and death.wav from your extracted disc audio into the ignored dist folder. Rebuild after extracting assets if a clip is missing. No music data is committed.
