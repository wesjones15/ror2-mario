# Portal exit camera handoff

The supplied 60 fps clip (`Risk of Rain 2 2026.10.31 - 16.57.34.02.mp4`)
shows the view jump at about 6.15 seconds. The matching player log reports the
slope-safe exit cut at 1.57 seconds, followed by ejection and sim creation at
the same feet position `(120.15, 105.94, 115.98)`. The sim's initial position
therefore agrees with the cut animation; this report does not justify changing
the native animation or its slope check.

RoR2 DLC4 `SurvivorPodController.UpdateCameras` removes its own camera override
on passenger exit using a 0.05-second transition. `VehicleSeat.OnPassengerExit`
also runs its exit teleport before Mario's `PlaceEjected` adjustment and before
the next `MarioDriver.FixedUpdate` starts the playable simulation/pivot.
Previously `Drawing` meant the view's model table existed, which did not prove
the playable view had drawn a frame.

`CameraHandoff` in ror2-common captures the last rendered camera shot and takes
ownership before seat ejection. Once the playable view has drawn, it blends the
held shot into the normal camera state over 0.35 seconds. It keeps the first
ready frame on the held shot, advances at most 1/30 second per rendered frame,
and returns ownership after rendering the exact endpoint. Slow frames extend
the transition instead of skipping most of its visible motion. Player look and
control are enabled during that blend. Other camera overrides retain ownership; failure,
death, target changes and destruction release the hold (two-second timeout).
`MarioStatuePod` runs its LateUpdate after the driver and remote puppet, so its
native rider disappears in the same rendered frame as the playable rider's
first draw. Both drivers now report actual successful draws.

This changes host camera/view ownership only. Existing native assets remain
`05_gate01.bmd/.btk/.brk`, the statue, and the entrance Mario animation documented
in libsms's behavior spec; no replacement effects or animation assets are used.
The footage and derived frame captures stay local under
`../output/mario-start-video/`.

The follow-up clip (`Risk of Rain 2 2026.10.31 - 17.15.14.03.mp4`) still shows a
large camera step around 5.1 seconds with the previous build. Its installed DLL
matches the staged DLL. A long initialization frame can consume RoR2's entire
ordinary camera lerp: `CameraRigController.EarlyUpdate` advances it using the
full `Time.deltaTime`. The helper therefore owns the visible blend rather than
depending on that timer. The log now records the ready-frame duration to help
confirm this explanation during another playthrough. Derived captures are in
`../output/mario-start-video-2/`.

Validation: `dotnet run --project ../ror2-common/tests/CameraHandoffCheck -c Release`
checks the held shot across seat exit, a 600 ms initialization frame, another
600 ms frame during the blend, duplicate samples within one frame, the exact
endpoint, newer override ownership, and failed-start timeout. Mario builds
against DLC4. In-game confirmation of this revision remains pending.
