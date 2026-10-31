# FLUDD enemy impacts

FLUDD contacts now draw Sunshine's yellow hit marker: JPA emitter **0xE7**,
`ms_enm_wathit.jpa`, and play enemy-water-hit sound **0x6802**,
`MSD_SE_EN_COMMON_W_HIT_OK`. These are separate from the ordinary splash and
splash sound. The native water-hit path supplies contacts for RoR2 enemies and
the flower world; `Look.Effects` controls the flash. No damage balance changed.

The owner records presentation-only impact positions in its frame stream, and
remote Mario puppets replay the same native effect and sound without applying
damage again. Install matching `MarioTest.dll` and `sms.dll`: **ABI 39, network
protocol 9**. Existing frame peers with another protocol are rejected.

Source identity, labelled multi-frame Rust viewer proof, sound-gate approximation
and headless checks are documented in `libsms/docs/enemy-water-hit.md`. The basic
flash/sound are implemented; Sunshine's full spatial and continuous sound-set
modulation is not. In-game appearance, volume and multiplayer are **not yet played**.
