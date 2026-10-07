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
  layer. 1 Sunshine unit = 1 cm (`MetresPerUnit` in the config).
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

Controls (your RoR2 binds):

| RoR2           | Sunshine pad | Does                       |
|----------------|--------------|----------------------------|
| move           | stick        | walk, run                  |
| jump           | A            | jumps, wall kicks          |
| primary        | R, half      | spray while moving         |
| secondary      | B            | dive, punch                |
| utility        | L            | ground pound, crouch       |
| sprint (held)  | R, fully in  | plant and spray            |
| special        | X            | switch nozzle              |

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

The log, `BepInEx/LogOutput.log`, should show `libsms ABI 2`, then `Mario (test) registered`.
In a run it should show `Mario's sim started`, `Mario's view: 12 models, 26 textures; sound
...`, and one line per status change (turn that off with `Debug.LogStatus`).

Config is in `BepInEx/config/com.libsms.mariotest.cfg`: `Look.DrawModel`, `Sound.Enabled`,
`Sound.Volume`, `Sim.MetresPerUnit` and `Paths.AssetsDir`.

### Not there yet

- Other players see Commando. Only the player who's Mario sees and hears him; the pose and
  animation aren't networked yet.
- Yoshi, and the FLUDD water's particles and splashes. The water is simulated but not drawn.
- Damage either way: Commando's skills never fire, so Mario can't hurt anything yet. RoR2
  still hurts Mario, but its knockback and fall damage are ignored.
- Collision with characters: Mario only collides with the world layer.
- Water from FLUDD is simulated but not drawn.
- Multiplayer is untested. Every player needs the plugin, and anyone playing Mario needs their
  own extracted `assets/`.

Offline / friends-only co-op only.
