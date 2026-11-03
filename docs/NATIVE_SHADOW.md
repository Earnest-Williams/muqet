# M10: native non-authoritative thermal shadow

The RimWorld 1.7 adapter now runs a **separate per-map thermal simulation** alongside vanilla. It does not disable, intercept, or replace vanilla heaters, coolers, temperature reads, power calculations, saves or temperature effects.

## Input collection

- Capture one observational per-cell temperature from RimWorld, roof classification (unroofed / thin / thick) and passage classification (air / wall / door open / door closed) on the **game thread**.
- Build an independent two-layer `ThermalGrid` with explicit **provisional** coefficients from `ShadowThermalCalibration`: air heat capacity, solid heat capacity, air exchange, air-solid exchange, roof-to-outside conductance and weakened wall/door passages.
- Capture vanilla electric heater/cooler state and thermostat targets, and resolve supported device cold-side anchors from the existing room-control snapshot. Devices whose ports cannot be reconciled are skipped, never guessed.
- Run `HvacSimulationCoordinator` at 250-game-tick intervals with a provisional, explicitly declared mapping of 60 game ticks per model second. Its watts and heat are counted only in the **shadow grid**.
- Measure mean absolute deviation of **indoor room cells** from the vanilla per-cell observation and report it (with energy residual and active HVAC count) in developer logs approximately every 2,500 ticks. This is diagnostic divergence, **not a claim of accuracy**.

## Invalidation and lifecycle

- The host owns a `NativeThermalShadow` per `MuqetMapComponent`. No static map references or background use of Verse objects.
- Room membership changes rebuild the shadow based on newly observed vanilla temperatures; controller changes bind a fresh control-zone layout.
- Geometry is classified and hashed on each 250-tick shadow step, **independently of room IDs**. Door opening and roof changes can affect conductances without changing `Room.Cells`, and cause a safe shadow reset on detection.
- Shadow runs only after the native room snapshot is present. Errors disable the shadow for the map with a diagnostic log and never affect vanilla gameplay or ownership.
- The original vanilla temperature field is not fed back into the shadow after initialization, preventing it from becoming an inadvertent second copy of vanilla's physics.

## Important calibration limits

These coefficients are deliberately not yet based on real material/StuffDef thickness or an exhaustive source inventory. Current assumptions include:

| Quantity | Provisional value |
|---|---:|
| Air heat capacity per cell | 3,015 J/K |
| Effective normal solid capacity | 120,000 J/K |
| Effective wall/door capacity | 280,000 J/K |
| Open-air neighbour conductance | 40 W/K |
| Closed-door passage | 1 W/K |
| Wall passage | 0.3 W/K |
| Bare outdoor coupling | 120 W/K |
| Thin roof coupling | 1.5 W/K |
| Thick roof coupling | 0.3 W/K |

The cell-air capacity assumes a 1 m² floor by 2.5 m air column. Indoor passive heat sources, terrain, food/pawns, greenhouse effects, fire, geysers, complex airflow and detailed door leakage are not yet modelled. The shadow therefore **cannot** provide a trustworthy numerical parity verdict against vanilla or replace its simulation.

The current shadow uses per-cell `GetTemperature` as a seed. In vanilla this may be a room-group average, so the initial thermal field is often uniform even if the eventual Muqet field will differ.

The CPU shadow remains synchronous and can be expensive on large maps. Profile CPU usage and memory before enabling by default in production. The 250-tick cadence is exploratory. All calculations stay on the game thread for now, so performance may require offloading immutable input snapshots to a worker later.

## What active replacement still requires

1. Real material coefficients from `TerrainDef`, `ThingDef`, stuff, roof types and their physical geometry; replace placeholder calibration values.
2. More heat sources, appropriate outdoor boundary handling, and physically validated cooling/heating power and time-scale calibrations.
3. Reliable temperature publication into RimWorld's cell and room queries, overlays and pawn safety with a single authoritative generation and save/load support.
4. A **conditional per-map ownership transition**: intercept vanilla HVAC only when the authoritative Muqet solver is operational. Unsupported conditions must continue with vanilla rather than losing HVAC altogether.
5. In-game acceptance on a real 1.7 Waylith build: map load, doors/vents, mountain freezer, insulated rooms, exhaust direction, device flick/breakdown, save/load and large-map performance.

## Tests

The new pure-thermal regressions validate symmetric air and solid edge conductances, conservation, passage and roof coefficients, map boundary edges, and malformed calibration/geometry. CI also builds the native 1.7 adapter and runs the earlier thermostat, source and cache suites.
