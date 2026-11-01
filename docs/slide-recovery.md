# Slide recovery

The supplied log records repeated host `Unstuck.ThroughFloor` corrections while
Mario descends Aqueduct geometry. One sends him from
`(-130.84, 6.85, 49.29)` back to `(-124.68, 12.08, 70.38)` after an earlier floor
crossing at `(-130.08, 8.71, 52.41)`. These are local recovery teleports.

The detector retained the crossing's world-space height. Sliding downhill or
leaving a prop could put Mario more than a body height below that old point
without actually being underneath the floor anymore. The retained candidate
also prevented the lower terrain from becoming a new safe standing point.

The shared detector now rechecks a floor at Mario's current X/Z each step and
tracks that surface's current height. When that surface no longer covers his
feet, it clears the candidate. A genuine fall beneath the current surface still
recovers once the whole body is under it. Wall sweeps, stuck recovery and map-zone
recovery retain their existing checks; sliding states are not exempted wholesale.

Candidate contacts no longer log as confirmed falls. Actual floor-recovery logs
include the step's endpoints and Mario's status for follow-up diagnosis.

Validation: the shared UnstuckCheck covers prop-edge descent and downhill sliding
at 30, 60 and 120 Hz, plus real floor penetration, shallow sinks, ordinary drops
and the existing recovery cases. Changes are on `modern/1.5.0` in ror2-common and
the existing modern Mario worktree. No FLUDD feature branch or native simulation
behavior is changed. In-game validation remains pending.
