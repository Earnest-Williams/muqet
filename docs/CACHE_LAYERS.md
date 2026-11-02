# Muqet multi-layer caching (M6)

## Sources and design decisions

This milestone adapts **mechanisms**, not code, from the two Celsius implementations:

- Celsius3 `ThermalsCache` memoizes terrain definitions and `ThingDef.shortHash << 16 | StuffDef.shortHash` material pairs, including absent entries. Muqet adds `ThermalDefinitionCache<T>` using the same key strategy with an explicit flush and transactional terrain rebuild.
- Celsius3 maintains a cached per-map `ThermalProps[]` per building position. Garwel's Celsius caches structural thermal coefficients and invalidates only affected cells. Muqet's `ThermalCoefficientCache` from M5 already covers the latter; actual material extraction from RimWorld definitions is **not yet implemented**.
- Both Celsius versions cache room-average temperature. Garwel's newer implementation caches `Room.Cells` and tracks a published generation. Muqet adds `RoomTemperatureCache` with a topology-generation and a separately published temperature-generation boundary.

## API and invariants

`ThermalDefinitionCache<T>` is an independent C# component, accepting game-supplied `ushort` short hashes and immutable profile values. `TryGetTerrain` and `TryGetThing` lazily resolve and memoize both successful and null results. A thrown resolver does not enter the cache. `ReplaceTerrain` creates a validated new lookup first; `Flush` invalidates all material and terrain profiles. After flushing, the host must also invalidate prepared per-cell coefficients so stale derived values are not used.

`RoomTemperatureCache.SetTopology` snapshots room-to-cell membership once per structural layout; invalid input cannot replace a valid layout. `PublishTemperatureGeneration` advances the snapshot generation. The first room query in a generation computes mean, P10, P90 and extremes; later queries reuse the result. The snapshot must remain unchanged for the duration of that generation: **never mutate a published temperature array and expect cached statistics to update**. Room membership and physical heat-transfer edges are independent.

## Proposed adapter wiring

1. In native RimWorld adapter, initialize a per-map definition cache once the Def database is available. Resolve terrain profiles by short hash; resolve building/material combinations by the pair of short hashes.
2. On mod-settings/Def changes, flush definition profiles, refresh any derived per-cell coefficient arrays and invalidate room thermal diagnostics as needed.
3. On map topology events, derive room membership from `Room.Cells` on the game thread and pass a snapshot to `RoomTemperatureCache.SetTopology`. Do not enumerate room cells for every device query.
4. On each completed simulation publication, increment a monotonically increasing published temperature generation, then compute room statistics on demand. Do not mix one generation's cells with another's temperatures.
5. Queue/coalesce heat-source changes per simulation frame; keep heat inputs live rather than treating them as part of static material coefficients. The energy-conserving source queue and game temperature publication are separate later milestones.

## Limitations

These are **reusable pure C# cache components and regression tests**. Neither cache is yet wired to a running RimWorld physics simulation. The existing read-only room adapter is not the authority for thermal state, and the numeric solver still runs per cell. This does not implement precomputed rectangular-room heat-response tables or the game-integrated heater/cooler.

A per-map cache is preferable to a process-global cache for mutable mod settings and load/unload lifecycle; sharing across maps is a later, explicit performance decision. Generic immutable definition profiles may be safely reused once a valid scope is established. The adapter must never pass live Verse objects into a background solver.

## Acceptance

Definition-positive/negative-hit and flush tests; transactional terrain replacement tests; generation cache reuse tests; failure-safe topology rebuild; invalid snapshot and generation rejection. Existing solver, zone, thermostat, room-adapter and native assembly CI remain in the same pipeline.

References: [Celsius3 ThermalsCache](https://github.com/Earnest-Williams/Celsius3/blob/master/Source/ThermalProps.cs), [Celsius2.7 ThermalWorker](https://github.com/GarwelGarwel/Celsius/blob/master/Source/ThermalWorker.cs), [Celsius2.7 ThermalSnapshot](https://github.com/GarwelGarwel/Celsius/blob/master/Source/ThermalSnapshot.cs).
