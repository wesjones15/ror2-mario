# FLUDD stream and corkscrew steering

Airborne type-0 water is drawn as the disc's exact droplets again. The shared
Rust CPU merge (one coverage surface from `timg/watermask.bti`) is opt-in and
**off in gameplay**: head-on it flattened the spray into one card, and while
moving its grid contour read as a torn sheet. The native surface and its
side/head-on/moving proof remain in `libsms` for a future screen-space pass.
Separated droplets and their per-drop accumulation are unchanged otherwise.

Both Turbo and regular corkscrews follow the local camera left/right. Looking
up/down relative to the camera angle when the roll starts gently changes the
flight angle (35% of the change, at most 15 degrees). Returning the camera to
that starting pitch levels the flight. Stick circles still sustain the roll.

Source identity, labelled side/head-on/moving viewer proof, headless checks and
host approximation limits: `libsms/docs/fludd-stream-flight.md`.

Install the matching **ABI 41** `sms.dll` and `MarioTest.dll` together.
Flight-camera and equipment replay use **network protocol 12**.
ABI 41 adds `sms_sim_equip_nozzle` (the radial menu's pickup feedback) beside the
silent quick-swap `sms_sim_select_nozzle`; the frame stream gains its own equip
operation.
The current fitted nozzle is also sent silently when it changes and in each
periodic refresh (about three seconds), including No FLUDD. A late joiner no
longer needs the original wheel-selection packet to use the correct nozzle.
Existing puppets skip fitting an already correct nozzle so a refresh does not
reset charging or spraying; it never replays the nozzle-box pickup feedback.
The merged water surface was played and rejected; the restored droplet path is
**not yet re-played**.
