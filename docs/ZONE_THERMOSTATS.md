# Zone thermostats (M3)

The thermostat layer consumes virtual control zones and an immutable, published temperature snapshot. It requests device operation; it does **not** inject energy or alter thermal coefficients. No RimWorld Harmony adapter or UI is implemented here.

## Control policy

- A controller owns one or more contiguous zones, nominally up to 25 air cells each.
- The innermost zone aims for the chosen minimum/maximum temperature band. Each outer tier relaxes the heating lower bound and cooling upper bound by a configurable amount (default 1 Celsius degree). No gradient is imposed on the actual solver.
- Heating starts below the lower bound, cooling above the upper bound. Hysteresis (default 1 degree) permits a running device to continue until the reading has moved inside its band.
- The **arithmetic air-temperature mean** of each service zone is the controller input. The primary zone's P90 temperature blocks requests that would overheat near the equipment; its P10 similarly blocks overcooling.
- A controller with heating and cooling modes cannot request both simultaneously. Opposing remote-zone demands stop both when the primary zone is already within its desired band, and report a diagnostic. Separate nearby equipment or revised ventilation is required.
- Requests are not guaranteed power or target achievement. The equipment adapter must enforce its power limit, conversion efficiency, power availability, and fuel status.
- After major zone layout reconfiguration or device power-off, call `Reset()` to clear the internal hysteresis state.

## Integration requirements

- Build room cells and permeability on the RimWorld main thread, and do not pass Verse objects into worker code.
- Sensor snapshots should be from one committed simulation generation; do not mix old/new temperatures or drive controllers multiple times per simulation timestep.
- Persist/reconstruct device association and chosen range. Treat stale/disappearing controller anchors and unreachable cells as explicit fallback cases.
- Apply requested heat/cool outputs through energy-budgeted device models, never via a second vanilla `PushHeat` path.
- Future player-facing controls should show coverage, local temperatures, tier offsets, power draw, and reasons a target cannot be met.

## Tests

Five additional regressions cover heating hysteresis, tier relaxation, core safety against remote cold cells, conflicting heat/cool demands, and malformed configuration.

## Limitations

The policy does not yet calculate required wattage, variable-output controls, mixing/advection, room splits or a placeable thermostat. The 25-cell and 1-degree defaults are provisional gameplay choices to validate in map scenarios.
