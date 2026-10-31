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

Validation:

- `../ror2-common/tests/CameraPerspectiveCheck`: reconstructs the game's player
  camera at 20 combinations of pitch/yaw and checks exact position, FOV/distance,
  subsequent free orbit, body following, constrained pivots and owned cleanup.
- `../ror2-common/tests/CameraHandoffCheck`: checks adoption before override release
  with no blend, plus seat exit, failed adoption, timeout and timed fallback.
- All three hosts compile against DLC4 with the shared camera helper.

The revised camera has not yet been played in-game.
