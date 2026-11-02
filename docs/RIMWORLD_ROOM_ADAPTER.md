# M4: RimWorld 1.7 room and device snapshot adapter

**Status:** experimental, compiled in CI only until a local Waylith game test is performed.

The native mod project targets net10.0 and references RimWorld 1.7 beta
reference assemblies through Krafs.Rimworld.Ref. It uses Harmony 3 to
invalidate a per-map snapshot when RimWorld signals a room-shape change or
a temperature-control device spawns. A 2,500-game-tick safety poll discovers
despawns and changes that did not raise either notification.

## What is captured

1. Read RimWorld rooms on the game thread. Visit each managed Room once
   and enumerate its Room.Cells into a row-major room-ID array.
2. Ignore cells whose room uses outdoor temperature, reserving -1 for
   unmanaged cells. This is a control-policy choice, not a physical boundary.
3. Find spawned ThingWithComps devices with CompTempControl. Keep their
   stable per-game IDs, occupied cells and facing cells as candidates.
4. Resolve each candidate to a room-air cell (facing outlet first, then
   occupied cell and N/E/S/W neighbours), with deterministic ID collision
   handling. A candidate without a usable cell is skipped and counted.
5. Build bounded 25-cell virtual control zones with the M2 solver-independent
   builder. The result is held in a MapComponent with a generation counter.

The adapter is intentionally **observation-only**. It does not currently
replace RimWorld temperature propagation, alter room temperatures, call
PushHeat, drive a cooler, alter a heater's power draw or sample Muqet solver
temperatures. Existing game temperature behavior remains active.

## Why no heat interception yet?

RimWorld's CompTempControl target is an in-game set point, not physical
wattage. Its devices may have directional exhaust and different energy
costs. We must establish outlet choice, source units and power accounting
before applying a thermostat request to the Muqet thermal grid. Doing it
early risks double injection through vanilla PushHeat and the new solver.

In particular, this stage uses each Thing's facing cell only as a candidate
**control anchor**. It does not assert that this is the correct hot/cold
source cell on every vanilla/modded device.

## Build and tests

    dotnet run --project tests/Muqet.Thermal.Tests/Muqet.Thermal.Tests.csproj -c Release
    dotnet run --project tests/Muqet.Adapter.Tests/Muqet.Adapter.Tests.csproj -c Release
    dotnet build src/Muqet.RimWorld/Muqet.RimWorld.csproj -c Release

The native assembly output is configured under Mod/1.7/Assemblies.
Copy Mod/ as a development mod after building. Do not package Harmony
or RimWorld reference assemblies with the mod.

## Runtime acceptance still needed

- Launch an actual 1.7 Waylith build with Harmony 3; check patch installation
  and loaded assembly version rather than relying on a successful compilation.
- Spawn a heater/cooler; inspect snapshots and confirm exactly one controller
  per intended device, including wall-mounted and rotated coolers.
- Build, remove and open/close walls/doors/vents; verify room IDs and update
  cadence, including outside/roof transition and two neighbouring rooms.
- Save/reload and switch maps; check generation reset, no static map leak and
  no duplicate patch registration.
- Benchmark Room.Cells enumeration and the 2,500-tick fall-back scan on
  250x250 and 476x476 occupied maps.
- Inspect the exact game-build calls for cooler outlets before any physical
  source interception. No inlet/outlet or heater watt conversion is asserted.

Next stage (M5): conductance/material snapshots, physical air/solid capacities
and tick-to-second calibration, temperature publication, and source accounting.
Only then replace vanilla heat calls with a patch that prevents double heating.
