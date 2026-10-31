# Sunshine flowers

Rare finds on a stage, from `libsms::flower` (decomp: `Enemy/hanasambo.cpp`). Config section
`[Flowers]`: `ChancePerRoll` (per thousand, 0 = off), `RollsPerStage`, `BitePercent`.

- **Flower ring**: 4-8 flowers on a circle. Spray each open with FLUDD; if every flower is open at
  once a ring of coins is thrown from the middle (`TSamboFlowerCoinUnit::checkGenCoin`) and they
  stay open. Miss one and the open ones wilt after `mSLBloomTimer` (300 ticks).
- **Single flower**: a unit of one, one coin.
- **Enemy ring**: 3-6 flower heads (`TSamboHead`) that wake when Mario is near and hop at him.
- **Flower tower** (`THanaSambo`): bursts out of the ground inside 1000 units, bites inside 500,
  sinks past 2000. A stomp or pound kills it; water freezes it a while.
Dead enemies drop coins. Bites take `BitePercent` of max health and knock Mario down.

Models/clips come from `assets/enemy/sambo*` (stage archives; an older assets folder re-extracts on
the next run), tuning from `params_enemy/{samboflower,hanasambo,sambohead}.prm`. ABI 35.

Not done / approximations: runs only on the server's machine for the host's Mario (co-op clients
do not see it); the tower's bite is a cylinder at its base, not the posed head joint; no pollen or
burst particles yet; the open chime names (`MSD_SE_OBJ_FLOWER_OPEN_n`) are untested; heads'
hop distance is `mSLMoveDist` over the air time; never played.
