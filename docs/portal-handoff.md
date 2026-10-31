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
ownership before seat ejection. It releases with a 0.35-second blend once the
playable view has drawn. Other camera overrides retain ownership; failure,
death, target changes and destruction release the hold (two-second timeout).
`MarioStatuePod` runs its LateUpdate after the driver and remote puppet, so its
native rider disappears in the same rendered frame as the playable rider's
first draw. Both drivers now report actual successful draws.

This changes host camera/view ownership only. Existing native assets remain
`05_gate01.bmd/.btk/.brk`, the statue, and the entrance Mario animation documented
in libsms's behavior spec; no replacement effects or animation assets are used.
The footage and derived frame captures stay local under
`../output/mario-start-video/`.

Validation: `dotnet run --project ../ror2-common/tests/CameraHandoffCheck -c Release`
checks the held shot across seat exit, explicit ready-view release, newer
override ownership, and failed-start timeout. Mario builds against DLC4.
In-game replay of the fixed transition remains pending.
