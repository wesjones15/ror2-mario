# FLUDD nozzle radial

RB has two jobs. **Tap** swaps between Spray and the selected alternate (Hover by default).
**Hold for 0.5 s** opens a compact wheel for changing that alternate. The wheel sits just above
the Air Pump cooldown picture and the chosen alternate is overlaid on that picture; with No FLUDD
selected the picture is left as it is.

| Input | Action |
| --- | --- |
| Tap RB (pad) / Special (keyboard) | Spray ↔ selected alternate |
| Hold RB / Special 0.5 s | Open the alternate wheel |
| RS (pad) / mouse move (keyboard) | Highlight an alternate |
| Full RS tilt / far mouse move | Choose it and close |
| Release RB / Special, pause, UI focus | Cancel (the button must be released before opening again) |

Segments, clockwise from the top: Hover, Rocket, Turbo, No FLUDD. Each nozzle picture is turned
to point out of its own segment. The pictures are the disc HUD's own `nz01..04` icons
(`sms_view_nozzle_icon_*`), not stand-ins. No FLUDD is the Spray picture struck through: choosing
it calls `sms_sim_select_nozzle(0)` and hides the tank. Choosing Hover/Rocket/Turbo while an
alternate is fitted changes it in place; while Spray is fitted it is only remembered for the next
tap. From No FLUDD, choosing a nozzle gives FLUDD back.

With No FLUDD selected the button is empty. A tap there is reserved for the optional Melee Mario
punch/kick (`Mario.MarioContextualAttack.TryAttack`). Until that provider exists the ability is
omitted and the tap does nothing; hold RB and choose a nozzle to bring FLUDD back.

The selection is a host operation (`SimOps.SelectNozzle`), so it is recorded in the same
multiplayer frame as the step. Network protocol 5 and native ABI 33 are required on every peer.

## Adding an ability

`MarioRadial.RegisterAbility(label, colour, icon, apply)` appends a segment. `apply` runs
inside the driver's physics step when the segment is committed, so any sim call should go
through `SimOps` to be recorded for puppets. It is currently used by the alternate entries;
the wheel no longer adds a Melee A segment. The menu rebuilds its ring for the new segment count.

The empty FLUDD button also discovers an optional Melee Mario A attack through reflection:
`Mario.MarioContextualAttack.TryAttack(CharacterBody body, Vector3 aim)`. When that type is
loaded, tapping RB with No FLUDD fitted is the punch/kick; when it is absent the tap simply does
nothing. MarioTest does not reference the Melee assembly. RT remains Sunshine's spray in either
case.
