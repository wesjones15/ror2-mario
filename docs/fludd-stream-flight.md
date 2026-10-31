# FLUDD stream and corkscrew steering

Airborne type-0 water now draws as one coverage surface where drops overlap,
instead of layering a separate tint and refraction sphere for every segment.
Separated droplets still break apart. The shared Rust presentation builds the
surface from Sunshine's original `timg/watermask.bti`; RoR2 uses its contour
alpha and combined-slope UVs with existing shared transparency/refraction materials.

Both Turbo and regular corkscrews follow the local camera left/right. Looking
up/down relative to the camera angle when the roll starts gently changes the
flight angle (35% of the change, at most 15 degrees). Returning the camera to
that starting pitch levels the flight. Stick circles still sustain the roll.

Source identity, labelled three-frame viewer proof, headless checks and host
approximation limits: `libsms/docs/fludd-stream-flight.md`.

Install the matching **ABI 41** `sms.dll` and `MarioTest.dll` together.
Flight-camera replay uses **network protocol 11**.
ABI 41 adds `sms_sim_equip_nozzle` (the radial menu's pickup feedback) beside the
silent quick-swap `sms_sim_select_nozzle`; the frame stream gains its own equip
operation.
RoR2 visual appearance and steering feel are **not yet played**.
