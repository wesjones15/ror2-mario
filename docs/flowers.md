# Sunshine flowers

Rare finds on a stage, from `libsms::flower` (decomp: `Enemy/hanasambo.cpp`). Config section
`[Flowers]`: `ChancePerRoll` (per thousand, 0 = off), `RollsPerStage`, `BitePercent`.

- **Flower ring**: 4-8 flowers on a circle. Spray each open with FLUDD; if every flower is open at
  once a ring of coins is thrown from the middle (`TSamboFlowerCoinUnit::checkGenCoin`) and they
  stay open. Miss one and the open ones wilt after `mSLBloomTimer` (300 ticks).
- **Single flower**: a unit of one, one coin.
- **Enemy ring**: 3-6 flower heads (`TSamboHead`) that wake when Mario is near and hop at him.
- **Flower tower** (`THanaSambo`): bursts out of the ground inside 1000 units, sinks past 2000. Its
  bite is the disc's head hit cylinder (60 wide, 20 tall) at the head joint (joint 3 of `sambo.bmd`,
  posed by the same clip the model plays), so it only hurts a Mario the head actually reaches
  (about 260 units out in its attack, a bite at most every half second, none while frozen). A stomp
  or pound kills it; water freezes it a while.
Dead enemies drop coins. Bites take `BitePercent` of max health and knock Mario down.

Particles are the disc's own `.jpa` effects (`ms_smb_kafun_ow` pollen on a bloom, `_oa` on a bite,
`ms_smb_ap_*` rising, `ms_smb_hd_*` sinking, `ms_enm_disap_*` dying), emitted by every machine from the
same events.

## Multiplayer
The server owns one world (placement, the step, bites, coins). Clients mirror snapshots (8 a second,
the things within 130 m of a player, a flower ring whole or not at all) and draw them, so players who
are not Mario see the flowers, towers and heads, with their particles. Only a Mario can wake them: his
sim lands spray and stomps on the mirror's things and reports them (`FlowerHitMsg`); the server applies
them. Enemies chase and bite the nearest Mario only. The sounds are Mario's own sound bank, so a player
without a Mario view sees but does not hear them. Messages 0x4D0A-0x4D0C; ABI 36.

Mario's coins can be picked up by any player teammate (`Coins.TeammatesCollect`, on): same heal, same
`Coins.Gold`.

Not done / approximations: never played, never run on two machines; a respawned Mario's sim does not
carry flower state (the world is separate, so it survives); a head's hop distance is `mSLMoveDist`
over the air time; the tower's pollen while it waits (`createPollen` at frame 16) is not drawn.
