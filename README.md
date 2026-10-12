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
- `MarioMain` replaces Commando's main state, so RoR2's own movement, jumping and skills stay
  off.
- `MarioView` loads libsms's view (`sms_view_*`) the first time a Mario starts.
  - It has the body, hands, cap, FLUDD and nozzle, posed from the sim each frame and skinned
    in Rust.
  - Each material's TEV is baked into one texture, which ror2-common's `ModelView` draws with
    RoR2's own shader and lighting. Commando's model is hidden meanwhile.
  - The game's sounds (voice, footsteps, FLUDD) play on our own audio output, since RoR2's
    audio is Wwise, at RoR2's master × SFX volume. A short Delfino Plaza sting plays when Mario emerges; RoR2 keeps its soundtrack.
- No R2API: the survivor is registered through RoR2's content-pack hook.

Controls, on a gamepad, by button position (Sunshine's layout, whatever your RoR2 binds):

| Pad              | Sunshine | Does                                            |
|------------------|----------|-------------------------------------------------|
| left stick       | stick    | walk, run                                       |
| A                | A        | jumps, wall kicks                               |
| B                | B        | dive, punch                                     |
| right trigger    | R        | spray; all the way in plants Mario and sprays   |
| left trigger     | L        | ground pound (in the air), crouch               |
| right bumper     | X        | switch nozzle: Spray, Hover, Rocket, Turbo      |
| D-pad down       |          | fill FLUDD's tank (`Fludd.DpadRefill`)          |
| X, Y             |          | RoR2's interact and equipment, as ever          |
| RoR2's sprint    |          | sprint, for sprint items (no speed of its own)  |

On keyboard and mouse your RoR2 binds stand in: jump (A), primary (half spray), sprint (plant
and spray), secondary (B), utility (L), special (switch nozzle).

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

The log, `BepInEx/LogOutput.log`, should show `libsms ABI 8`, `Mario is 1.xx m tall`, then
`Mario (test) registered`.
In a run it should show `Mario's sim started`, `Mario's view: 12 models, 26 textures; sound
...`, and one line per status change (turn that off with `Debug.LogStatus`).

Config is in `BepInEx/config/com.libsms.mariotest.cfg`: `Look.DrawModel`, `Look.Height`,
`Look.PoundFlames`, `Sound.Enabled`, `Sound.Volume`, `Sim.*` (jump height, buffer and chain
grace, speed items, solid enemies, stomp bounce, pound jump, timed stomp jump, shortest hop),
`Fludd.*` (nozzle cycling, spray reach, rocket height, unlimited water, D-pad refill), `Hud.*`
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

### Not there yet

- Other players see Commando. Only the player who's Mario sees and hears him; the pose and
  animation aren't networked yet. Mario's hits are the server's: a client playing him deals no
  damage yet.
- Yoshi.
- RoR2's fall damage doesn't hurt Mario.
- Goobo Jr.'s Mario clone looks like Commando, and the skill icons are Commando's.
- Multiplayer is untested. Every player needs the plugin, and anyone playing Mario needs their
  own extracted `assets/`.

Offline / friends-only co-op only.

## Arrival music

Phase 6: Mario plays approximately five opening beats (2.5 seconds) of Delfino Plaza once per local body after leaving the pod. Stage arrivals and respawns can play it again. SpawnMusic.Enabled and SpawnMusic.Volume control it; master and music volume also apply. The build synthesizes spawn.wav from your extracted disc audio into the ignored dist folder. Rebuild after extracting assets if the clip is missing. No music data is committed.
