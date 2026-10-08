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
- `MarioMain` replaces Commando's main state, so RoR2's own movement, jumping and skills stay
  off.
- `MarioView` loads libsms's view (`sms_view_*`) the first time a Mario starts.
  - It has the body, hands, cap, FLUDD and nozzle, posed from the sim each frame and skinned
    in Rust.
  - Each material's TEV is baked into one texture, which ror2-common's `ModelView` draws with
    RoR2's own shader and lighting. Commando's model is hidden meanwhile.
  - The game's sounds (voice, footsteps, FLUDD) play on our own audio output, since RoR2's
    audio is Wwise, at RoR2's master × SFX volume. There's no Sunshine music.
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
| X, Y             |          | RoR2's interact and equipment, as ever          |

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

The log, `BepInEx/LogOutput.log`, should show `libsms ABI 4`, `Mario is 1.xx m tall`, then
`Mario (test) registered`.
In a run it should show `Mario's sim started`, `Mario's view: 12 models, 26 textures; sound
...`, and one line per status change (turn that off with `Debug.LogStatus`).

Config is in `BepInEx/config/com.libsms.mariotest.cfg`: `Look.DrawModel`, `Look.Height`,
`Sound.Enabled`, `Sound.Volume`, `Sim.*` (jump height, buffer and chain grace, speed items,
solid enemies), `Fludd.*` (nozzle cycling, spray reach), `Damage.*` (stomp, ground pound and
its shock wave, dive, water per drop and its proc coefficient) and `Paths.AssetsDir`.

### Not there yet

- Other players see Commando. Only the player who's Mario sees and hears him; the pose and
  animation aren't networked yet. Mario's hits are the server's: a client playing him deals no
  damage yet.
- Yoshi.
- RoR2's knockback and fall damage don't move Mario.
- Multiplayer is untested. Every player needs the plugin, and anyone playing Mario needs their
  own extracted `assets/`.

Offline / friends-only co-op only.
