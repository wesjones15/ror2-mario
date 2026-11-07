# Development

Source layout, builds and checks for `ror2-mario`. Run the commands below from the repository root. Player installation, controls and configuration are in the [README](../README.md).

## How the repositories connect

| Repository | Role |
| --- | --- |
| **ror2-mario** (this repository) | Register the survivor and skins, read controls, query Unity terrain/enemies, translate combat and items, and coordinate HUD, presentation and networking. The plugin DLL retains its historical name, `MarioTest.dll`. |
| [libsms](https://github.com/wesjones15/libsms) | Sunshine behavior, FLUDD simulation, extraction, animation, presentation and audio; exposes the `sms.dll` C API. |
| [ror2-common](https://github.com/wesjones15/ror2-common) | Shared C# movement/combat/item bridges, rendering, UI, input, networking, audio and local file handling. Compiled into this plugin. |
| [libcommon](https://github.com/wesjones15/libcommon) / [libssbm](https://github.com/wesjones15/libssbm) | Shared Rust primitives and Melee asset support used by the native build. Full contextual Melee attacks also use the [Melee plugin](https://github.com/wesjones15/ror2-samus) at runtime. |
| [ror2-characters](https://github.com/wesjones15/ror2-characters) | Package assembly and releases for Mario, Olimar and the Melee fighters. |

`MarioDriver` steps the native simulation and places the RoR2 body at Mario's feet; `MarioMain` replaces vanilla movement/skill execution. `UnityHost` answers collision queries and `MarioCombat` connects native hits to RoR2 hurtboxes. The native library produces the posed models and effects that the host draws in Unity. Original-game behavior is reimplemented from decompilation research, rather than shipping the original game's executable.

## Building from source

Use Windows, PowerShell 5.1, a current Rust toolchain supporting edition 2024, and a .NET SDK. The plugin targets `netstandard2.1`; the headless C# checks target .NET 6. You also need local RoR2 DLC4 and BepInEx assemblies to compile against.

Keep these repositories as siblings, preserving their names:

```text
workspace/
  ror2-mario/       modern/1.5.0
  ror2-common/      modern/1.5.0
  libsms/           modern/1.5.0
  libcommon/        modern/1.5.0
  libssbm/          main
```

`libssbm` is a source dependency of the native presentation build even when the optional Melee gameplay integration is not being used. Extracted game assets are not needed to build the plugin.

Set `$referenceDirectory` to the directory containing the RoR2 and BepInEx assemblies you intend to compile against. Pass it explicitly with `-RoR2Dir`; there is no machine-specific default. Staging is resolved relative to the source checkout as workspace `target/`, independently of the reference directory.

From `ror2-mario/`:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -RoR2Dir $referenceDirectory -NoInstall
```

This builds `sms.dll` and `MarioTest.dll` into `dist/MarioTest/`. Without `-NoInstall`, the script also stages the plugin and SharedFiles beneath a workspace `target/BepInEx/plugins/` directory; `-InstallDir` can select a subdirectory within workspace `target/`. Copy staged files into the intended game/mod-manager profile yourself.

## Validation and limitations

Headless checks cover selected movement, damage, networking and presentation boundaries. Examples, from this repository:

```powershell
dotnet run --project tests/FallDamageCheck -c Release
dotnet run --project tests/UnstuckCheck -c Release
```

Run native checks from `libsms/` with `cargo test --release`. Disc-dependent checks need the local asset cache; see the native behavior spec for coverage. Tests and builds do not establish in-game appearance, balance or headset behavior.

Recent fire effects, heat haze, nozzle UI, statue/portal changes, Bowser Jr. and VR include work documented as awaiting in-game review. Treat multiplayer as requiring further co-op verification: the owning player runs Mario's simulation, other peers replay recorded inputs, and gameplay outcomes are routed through the server. Peers need matching builds, compatible unfinished-content settings and their own local assets. Use offline play or private friends-only co-op.

Further reading: [native behavior spec](https://github.com/wesjones15/libsms/blob/modern/1.5.0/docs/behavior-spec.md), [nozzle/attack details](../docs/radial-menu.md), and [shared multiplayer architecture](https://github.com/wesjones15/ror2-common/blob/modern/1.5.0/MULTIPLAYER.md). Historical documents may retain old setup or version notes.
