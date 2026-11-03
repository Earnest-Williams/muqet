# M9: End-to-end HVAC integration loop (non-authoritative)

## What is implemented

`HvacSimulationCoordinator` is a game-independent integration layer joining previously independent systems:

1. Bind one `RoomControlSnapshot` containing real-room membership and virtual thermostat control zones.
2. Read a single committed air-temperature state.
3. Obtain one `HvacDeviceRequest` per captured electric heater/cooler, including target, power, port-readiness and geometry.
4. Construct/advance the per-controller hysteresis thermostat using its **25-cell tiered zones**. On a changed target or zone layout, reset its control state.
5. Turn thermostat decisions into electrical draw and hot/cold watts through `ElectricThermalDeviceModel`.
6. Build an **entire** air-source frame from a separate baseline plus equipment output. The previous equipment output does not accumulate.
7. Run `CpuThermalBackend.Advance` with supplied simulated seconds and outdoor temperature.
8. Commit an incremented publication generation only after a successful complete solver advance; make room means and percentiles available from the same generation.

A failed frame restores the previous source array and does not publish a generation or replace stored thermostat hysteresis. The CPU solver itself now stages all substeps before publishing temperatures.

## Native correction

`RimWorldRoomCollector` now uses the rotated **south (cold)** side of `Building_Cooler` as its controller anchor, aligning with `VanillaElectricDeviceCapture`. The previous generic `Rotation.FacingCell` heuristic selected the opposite side and could misassign the controller to the exhaust room. Ordinary electric heaters use their own cell. Modded `CompTempControl` devices continue to use the generic heuristic until a profile explicitly defines their thermal ports.

## Important boundaries

This is an **end-to-end numerical loop, not activated gameplay replacement**. The native `MuqetMapComponent` is presently observation-only: it captures room topology and can supply suitable device observations but **does not initialize a physically parameterized thermal grid, transfer building/roof/terrain coefficients, or own game temperature publication**.

- Do not patch away `Building_Heater.TickRare`, `Building_Cooler.TickRare`, `Room.Temperature`, `MapTemperature.TemperatureUpdate` or `GenTemperature.PushHeat` yet. Without authoritative source + environment coverage, doing so would disable or distort gameplay.
- Do not use vanilla abstract heater/cooler `energyPerSecond` as joules or watts. Capture actual electric power where available, and make any gameplay thermal scaling explicit.
- `HvacSimulationCoordinator` is intentionally synchronous. Run only on owned simulation buffers, not concurrently with RimWorld mutation.
- The host must convert game ticks to **simulated seconds** by a declared calibration parameter. No silent 1:1 tick-to-second conversion.
- Publication to RimWorld game query methods, pawn safety and UI, and save/load migration require a validated activation protocol with a reliable rollback path.
- When the room snapshot changes, bind the new topology before any further HVAC step. No obsolete thermostat may control a split room.
- Structural building changes must invalidate the coefficient cache; device changes must invalidate controller ownership. A cooling exhaust placed outdoors remains in the **global physical grid** even when it is not in a controlled indoor room.

## Acceptance results and tests

M9 regression tests exercise end-to-end heating, cooling and exhaust conservation, repeated generations with room statistics, rollback after nonphysical thermal sources, rejection of mismatched anchors and duplicate device IDs. All existing solver, thermostat, coefficient, cache and native adapter tests remain enabled in CI.

## Next concrete integration milestone: map-owned thermal state

Build a **shadow-only** RimWorld state publisher first, with actual material coefficients, roof/terrain heat loss and a calibrated time mapping. It should record parity and performance against vanilla without replacing vanilla heat. Transition to authoritative ownership only once room temperatures, heat sources, doors and passive environmental effects have verified replacement coverage and safe save/load behavior.
