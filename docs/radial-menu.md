# FLUDD nozzle radial

Hold the change-nozzle button to open a compact wheel of FLUDD's nozzles. A partial right-stick
tilt highlights a segment; pushing the stick to full tilt fits it and closes the wheel at once.
Releasing the button without a full tilt cancels. The wheel does not pause the game.

| Input | Action |
| --- | --- |
| Hold RB (pad) / Special (keyboard) | Open the wheel |
| RS (pad) / mouse move (keyboard) | Highlight a segment |
| Full RS tilt / far mouse move | Fit the highlighted nozzle and close |
| Release RB / Special, pause, UI focus | Cancel (the button must be released before opening again) |

Segments, clockwise from the top: Spray, Hover, Rocket, Turbo, No FLUDD. Each nozzle picture is
turned to point out of its own segment. The pictures are the disc HUD's own `nz01..04` icons
(`sms_view_nozzle_icon_*`), not stand-ins. No FLUDD is the Spray picture struck through: fitting
it calls `sms_sim_select_nozzle(0)` and hides the tank; fitting a nozzle afterwards gives FLUDD
back with a full tank.

The selection is a host operation (`SimOps.SelectNozzle`), so it is recorded in the same
multiplayer frame as the step. Network protocol 5 and native ABI 33 are required on every peer.

## Adding an ability

`MarioRadial.RegisterAbility(label, colour, icon, apply)` appends a segment. `apply` runs
inside the driver's physics step when the segment is committed, so any sim call should go
through `SimOps` to be recorded for puppets. The menu rebuilds its ring for the new segment count.

The wheel also discovers an optional Melee Mario A attack through reflection:
`Mario.MarioContextualAttack.TryAttack(CharacterBody body, Vector3 aim)`. When that type is
loaded, RT becomes the contextual A instead of the spray and an **A ATTACK** segment appears.
When it is absent, both are omitted and Mario's normal RT spray is untouched; MarioTest does not
reference the Melee assembly.
