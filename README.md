# ror2-mario

Risk of Rain 2 host for [libsms](../libsms): Super Mario Sunshine's Mario movement in RoR2.
Targets the 2023 build (1.2.4.4, Unity 2019.4.37, BepInEx 5) in `../Risk Of Rain 2 2023`.

## MarioTest (first test character)

A survivor called **Mario (test)**: Commando's body, model and HUD, with the body moved by
libsms instead of RoR2's character controller. It's here to feel out movement and camera
before any model or sound work.

How it works:
- `sms.dll` is `libsms/crates/libsms-ffi`, the sim behind a C ABI.
- `MarioDriver` runs it on the client that owns the body. Each `FixedUpdate` it turns off the
  KinematicCharacterMotor, steps the sim, and puts the body at Mario's feet. RoR2's own
  network transform shows that to everyone else.
- `UnityHost` answers the sim's ground, roof and wall probes with raycasts against RoR2's world
  layer. 1 Sunshine unit = 1 cm (`MetresPerUnit` in the config).
- `MarioMain` replaces Commando's main state, so RoR2's own movement, jumping and skills stay
  off.
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

The log, `BepInEx/LogOutput.log`, should show `libsms ABI 1`, then `Mario (test) registered`,
and in a run `Mario's sim started` plus one line per status change (turn off with
`Debug.LogStatus`).

### Not there yet

- Mario's model, animation and sounds (it's Commando, playing Commando's animations).
- Damage either way: Commando's skills never fire, so Mario can't hurt anything yet. RoR2
  still hurts Mario, but its knockback and fall damage are ignored.
- Collision with characters: Mario only collides with the world layer.
- Water from FLUDD is simulated but not drawn.
- Multiplayer is untested. Every player needs the plugin, and anyone playing Mario needs their
  own extracted `assets/`.

Offline / friends-only co-op only.
