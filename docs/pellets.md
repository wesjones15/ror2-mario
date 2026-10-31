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
