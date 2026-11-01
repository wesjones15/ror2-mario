# Portal exit camera handoff

Player control begins from the intro's existing shot. Position, orientation,
camera distance and FOV stay continuous; the player can immediately look around
and move Mario with RoR2's normal follow camera.

The supplied recordings show the camera reframing when the seat releases Mario.
The matching logs show the playable sim starting at the cut animation's exact
feet position. The latest log reports a 17 ms ready frame, and the camera still
moves. A transition toward the default player framing is itself unwanted here.

RoR2 DLC4 `SurvivorPodController.UpdateCameras` removes its camera override on
passenger exit. `CameraModePlayerBasic.MatchStateInternal` preserves the look
direction, but the follow camera retains its own distance spring and authored
local camera offset. A slower blend still changes the composition.

`CameraHandoff` captures the rendered shot before seat ejection. Once the driver
or remote puppet has actually drawn, `ContinueFromHere` uses the shared
`CameraPerspective` helper to adopt that shot as player framing, then removes
the camera override without a blend. The helper:

- Uses the game's current pivot, including `CalculateTargetPivotPosition`'s
  obstruction check and vertical offset.
- Derives the local camera offset from the held shot and undoes
  `CameraModePlayerBasic.UpdateInternal`'s pitch-dependent distance factor.
- Installs a body-owned camera-parameter override for the offset and FOV.
- Initializes the normal camera's `currentCameraDistance` and clears its
  `cameraDistanceVelocity` through the instance exposed by `DebugGetInstanceData`.
- Releases its parameter override when the body is destroyed. Normal look input,
  following, camera collision and temporary camera overrides remain game-owned.

The statue's LateUpdate runs after the playable views, retiring the native rider
in the same rendered frame as the playable rider's first draw. A failed startup
or unavailable camera API retains the existing timed-release fallback.

This changes host camera ownership/parameters. The native entrance animation,
`05_gate01.bmd/.btk/.brk`, statue, particles and slope-safe exit cut are unchanged.
Source clips and derived frames remain local in `../output/mario-start-video*/`.

## Centered player framing

The intro camera now aims through the landing's X/Z from its first frame:
`(0, 180, 308.53)` in Sunshine units, rather than `(0, 180, 140)`.
The earlier aim left the final Mario to the side of the optical axis, and adopting
that shot retained the lateral offset throughout gameplay. Camera placement stays
fixed; the corrected direction already exists before the exit and is handed off
unchanged. The landing and camera share `LandingForward` in the host.

The return point is the existing `ENTRANCE_RETURN` in
`libsms-present/src/entrance.rs`, read from dolpic1's Bianco `MarioPositionObj`.
`sms-main/src/Player/MarioAutodemo.cpp::TMario::warpOut` selects the existing
`demo_gate_out_appear_get`, `rolling_get` and `get2` clips. This is a framing
correction, with no new effect asset: the gate is model `05_gate01`
(`ModelGate.cpp::loadAfter`), and the existing warp-out particle emitter is cue
`0x40` (`MarioParticle.cpp::warpOutEffect`, kind 0).

Viewer-first proof: `libsms/crates/viewer/examples/portal_fast.rs` uses
`ENTRANCE_RETURN.z` for its aim. Local captures are
`../output/mario-camera-centered/frame_0100.png` (fall/spin) and
`frame_0149.png` (landing), with a screen-center guide and source labels.
The host change followed these captures; player-camera adoption is unchanged.

Validation:

- `../ror2-common/tests/CameraPerspectiveCheck`: reconstructs the game's player
  camera at 20 combinations of pitch/yaw and checks exact position, FOV/distance,
  subsequent free orbit, body following, constrained pivots and owned cleanup.
- `../ror2-common/tests/CameraHandoffCheck`: checks adoption before override release
  with no blend, plus seat exit, failed adoption, timeout and timed fallback.
- All three hosts compile against DLC4 with the shared camera helper.

The revised camera has not yet been played in-game.
