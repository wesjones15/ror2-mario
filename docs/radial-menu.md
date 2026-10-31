# FLUDD nozzle radial

RB has two jobs. **Tap** swaps between Spray and the selected alternate (Hover by default).
**Hold for 0.5 s** opens a compact wheel for changing that alternate. The wheel slides and grows from
the nozzle head above the water tank over 0.28 s, settling to the tank's upper left.
The chosen alternate is overlaid on the Air Pump cooldown picture; with No FLUDD
selected the picture is left as it is.

| Input | Action |
| --- | --- |
| Tap RB (pad) / Special (keyboard) | Spray ↔ selected alternate |
| Hold RB / Special 0.5 s | Open the alternate wheel |
| RS (pad) / mouse move (keyboard) | Highlight an alternate |
| Full RS tilt / far mouse move | Choose it and close |
| Release RB / Special, pause, UI focus | Cancel (the button must be released before opening again) |

Segments, clockwise from the top: Hover, Rocket, Turbo, No FLUDD. Each is centred on an axis, so
up chooses Hover, right Rocket, down Turbo and left No FLUDD. Each nozzle picture remains
upright. Option labels are removed; the selected name remains in the center, with
"Select F.L.U.D.D. Nozzle with RS" above the wheel. All nozzle icons are enlarged;
Hover gets the same additional 1.35x boost in the cooldown overlay, radial and hub.
The generated cooldown portrait is unchanged. The pictures are the disc HUD's own `nz01..04` icons
(`sms_view_nozzle_icon_*`), not stand-ins. No FLUDD is the Spray picture struck through: choosing
it calls `sms_sim_select_nozzle(0)` and hides the tank. Choosing Hover/Rocket/Turbo immediately
equips it, including from Spray. From No FLUDD, choosing a nozzle gives FLUDD back.

Source identity and layout proof: `GC2D/GCConsole2.cpp::loadAfter` binds HUD panes
`nz01..04` from `game_6/scrn/standard_1.blo` (2D pictures; no emitter/model cue).
`cargo run --release -p viewer --example nozzle_radial -- assets output/nozzle-radial.png`
in `libsms/` captures the original icons upright at the new sizes, including the
existing cooldown portrait with its overlay. Add `opening` after the output PNG
to capture four animation frames. The anchor uses the actual nozzle pane within
the gauge, then the same TankX/TankY/TankScale placement as the drawn HUD.
This is a Rust layout proof; the
RoR2 menu and opening animation are **not yet played**.

Opening plays the nozzle box's **0x3801 / MSD_SE_IT_EGG_APPEAR** once, from
`MoveBG/Item.cpp::TNozzleBox::receiveMessage`. Equipping a different alternate
plays **0x484E / MSD_SE_SY_GET_NOZZLE** (`TItemNozzle::touchPlayer`) plus
**0x1989 / MSD_SE_MA_SHINING_EFF_CALM2**, and emits the exact **JPA 0x0E /
ms_itemget1_a** sparkle (`MarioReceiveMsg.cpp::getNozzle` ->
`MarioParticle.cpp::emitGetEffect`). It follows body joint 1's position, with its
authored 120-frame emitter / 35-frame particle lifetime. Re-selecting the current
nozzle and returning to Spray do not retrigger pickup feedback. Selection replay
uses the existing native operation so remote Marios receive the same feedback.
`viewer --example nozzle_pickup` renders the exact sparkle at six frames.

With No FLUDD selected the button is empty. Tapping **RB or RT** there plays Melee Mario's
contextual A attack: libssbm's Mario fighter (`FighterKind::Mario`, `PlMr.dat`/`PlMrAJ.dat`)
selects jab, tilt, smash, dash or aerial from the stick and aim, and the clip is retargeted onto
Sunshine's `ma_mdl1.bmd` for the body. In the air the same tap picks an aerial (neutral, forward,
back, up or down) from the stick and camera. The disc hitbox's percent / 3 is applied as RoR2
damage through `MarioCombat`, so a client's punch is sent to the server and lands as his damage.
The lab fighter's own mixer plays the scripts' sound commands, so each attack has Melee Mario's
voice and swing sounds (heard only while an attack plays, so the lab's steps and landings stay
silent). This needs the Melee plugin installed: its `ssbm.dll`, and its cache, into which it
extracts `PlMr.dat` from the Melee disc on first run (`Paths.MeleeAssets` overrides). Nothing
Melee-related is installed into `MarioTest`; without the Melee plugin the tap does nothing. A Melee plugin provider (`Mario.MarioContextualAttack.TryAttack`) is still
accepted as a fallback, so MarioTest never references the Melee assembly. With FLUDD fitted RT
sprays and RB changes nozzles as before. Without the Melee cache/`ssbm.dll` the empty FLUDD
button does nothing; Sunshine's B dive and every other control are unchanged.

The selection is a host operation (`SimOps.SelectNozzle`), so it is recorded in the same
multiplayer frame as the step; the owner's Melee clip and frame travel in that frame stream too,
so puppets pose Sunshine identically. Network protocol 10 and native ABI 40 are required on every
peer.

## Adding an ability

`MarioRadial.RegisterAbility(label, colour, icon, apply)` appends a segment. `apply` runs
inside the driver's physics step when the segment is committed, so any sim call should go
through `SimOps` to be recorded for puppets. It is currently used by the alternate entries;
the wheel no longer adds a Melee A segment. The menu rebuilds its ring for the new segment count.

The empty FLUDD button's attack is `MarioMelee` (this plugin) first; if it is unavailable the
old reflection provider `Mario.MarioContextualAttack.TryAttack(CharacterBody body, Vector3 aim)`
is tried. RT is only a second Melee button when No FLUDD is fitted.
