# Mario carries Pikmin pellets

With both plugins installed, a Mario can pick up any loose Pikmin pellet the way he lifts fruit or a
Ship Repair part: B near it (Sunshine's grab, the carrying pose and lift sound), carry it, B to put it down,
or run and B to throw it. Throwing is the same arc as a thrown ship part (14 m/s along his facing, a lift of
9, plus half his run speed); a throw that lands in a Dolphin's receiver delivers a ship part, and any pellet
he puts down or throws becomes an ordinary loose pellet again: Pikmin carry it to an Onion as usual.

It needs a Pikmin world, so it works in Ship Repair and in any run with an Olimar (a co-op run with one
Olimar and one Mario, say). A Mario alone with the Olimar plugin installed has no pellets to take.

How: the Olimar plugin's `ShipRepairApi` (version 2) gained `CanCarry`; `Takeables`/`Take` and the server's
hold/put/throw (`ShipRepairGuests`) accept every pellet in the sim, not only ship parts, and the server's
`PikminWorld` ticks held and thrown pellets outside Ship Repair (`ShipRepairGuests.TickLoose`). Mario's
`MarioShipParts` asks `CanCarry` (an older Olimar plugin falls back to `Active`). Clients ask the server,
exactly as for ship parts. libpikmin already held any pellet (`hold_pellet`) and refuses a non-ship pellet
in a receiver (`hand_ship_part`), so nothing changed there.

Never played: neither the carry nor a two-machine run.

## Carrying teammates

B near a teammate on Mario's team lifts him like fruit; B puts him down, run and B throws him (the ship
part throw: 14 m/s along his facing, a lift of 9, plus half his run speed). `Carry.CarryTeammates`: 0
nobody, 1 (default) Olimar, the Melee fighters and other Marios, 2 any player. The held player breaks free
by pressing his own A (a Mario's jump; an Olimar's or a Melee fighter's primary): Mario stumbles and lets
go (`sms_sim_let_go`, ABI 37). Thrown or dropped players take no fall damage for 8 s.

How: `MarioCarry.cs`. The held body is pinned to Mario's hands on the held player's own machine after his
own driver has run (Olimar's and the Melee drivers already read a moved body as a teleport and a changed
velocity as a launch). Mario's machine asks the server (`0x4D10`-`0x4D18`), the server checks team, reach and
ownership and tells the held player's machine; poses stream at 20 Hz. All of it lives in this plugin
because each plugin carries its own copy of ror2-common, so they cannot share message state.

Not done: the held player is full size (a 1.6 m fighter dangles at Mario's chest, clipping him; the plan
is to shrink him to hand size, which needs a per-draw scale in `ModelView` and each driver passing it);
Olimar needs no shrink. Never played, never run on two machines.
