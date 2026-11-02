# Virtual temperature-control zones (M2)

Muqet creates **controller service areas**, not extra RimWorld `Room` objects. Zones do not alter air/solid heat-transfer edges. A heater/cooler outputs heat at its physical outlet, while its controller samples room-cell temperatures.

## Building a layout

- RimWorld adapter enumerates `Room.Cells` on the game thread and maps them to row-major cell indices, storing `Room.ID` or `-1` outside managed rooms.
- An anchor (`ControlAnchor`) identifies each heating/cooling controller and one traversable air cell in its room. Impassable buildings must use a suitable nearby air outlet/sensor cell.
- Cardinal air passages are described by optional east/north permeability arrays in `[0,1]`; `0` blocks reach, positive values provide distance cost `1/permeability`. Walls normally block; doors/vents depend on state.
- Multi-source Dijkstra assigns each reachable cell to its nearest traversable controller, with stable ID tie-breaking. Sources don't cross RimWorld room IDs; unserved room cells are counted and left unassigned.
- Each controller's assigned area is partitioned into *connected* zones of at most 25 cells, grown from nearest remaining seeds. Tier 0 is nearest, tier 1 next, and so on. 25 cells is an initial tunable control-policy cap, **not** a physical maximum room size.
- Build returns immutable zones with controller IDs, room IDs, tiers, and per-cell lookups. It rejects duplicate anchors, malformed maps and nonfinite topology values.

## Temperatures, insulation and policy

`layout.MeasureZone(zoneIndex, airTemperatureC)` returns minimum, interpolated 10th percentile, arithmetic mean, 90th percentile and maximum air temperature. A thermostat may choose the mean as its normal sensor and percentiles for diagnostics or overheat/undercooling checks. Temperature sampling does not change zone boundaries.

The optional per-cell `envelopeLossWPerK` is supplied by the host from physical roof/wall/door boundary conductances and summed per zone. It is **diagnostic only**, never injected into the solver twice. A well-insulated zone normally has smaller boundary loss. The actual temperature field is produced by the solver, not forced into tier offsets.

A future policy can progressively relax requested heating/cooling tolerances by, e.g., 1°C per tier. **No temperature offset or thermostat switching logic is implemented in this milestone.** It must avoid perpetual over-heating of near-source cells when distant zones cannot meet their targets.

## Integration and performance

Regenerate a room's layout on room topology or equipment changes, not every tick. Door/vent state changes may alter air passage costs and require rebuilding affected controller assignments, but do not imply a new physical room. Snapshot room-cell lists before starting any worker. The algorithm allocates on rebuild and uses no Verse/Unity objects; it is not part of the per-substep numerical kernel.

Current scope: topology, owner assignment, bounded connected partition, loss statistics and temperature sampling. **Not included:** RimWorld API/Harmony adapter, equipment controller or UI overlay, GPU backend, or transient sensor network.

## Run regressions

```bash
dotnet run --project tests/Muqet.Thermal.Tests/Muqet.Thermal.Tests.csproj -c Release
```

The additional tests cover four 25-cell zones in a 100-cell room, irregular connectivity, deterministic multi-controller ownership, blocked/partially open passages, heat-loss and temperature percentiles, and invalid inputs.
