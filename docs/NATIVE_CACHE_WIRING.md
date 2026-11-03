# M7: native room-membership cache wiring

This milestone integrates the M6 room-membership cache into the read-only RimWorld adapter while preserving the rule that Muqet must not invent temperature data until it owns the physics simulation.

## Changed flow

1. `RimWorldRoomCollector.Capture(map)` visits RimWorld room cells on the game thread and returns a `RoomControlSnapshot` containing the copied row-major room ID map.
2. `RoomControlSnapshot.RoomFingerprint` hashes geometry and room-cell IDs, independent of device positions and the 25-cell zone size.
3. `MuqetMapComponent` compares room fingerprints and rebuilds `RoomTemperatures.SetTopology(...)` only when room membership has changed. Moving a device can refresh virtual controller zones without rebuilding room-cell membership.
4. `CreateRoomMembership()` produces a room ID to cell-index mapping for use by `RoomTemperatureCache`.
5. **No temperature generation is published by this change.** The current adapter does not own a Muqet solver or a valid temperature snapshot; feeding RimWorld's room-uniform readings into a cell-specific cache would be misleading. The future solver publication path must call `PublishTemperatureGeneration` exactly once per committed grid generation, after a complete simulation step.
6. Definition/material caching remains in the independent M6 component. A genuine `ThingDef`/`StuffDef` or `TerrainDef` to physical coefficient resolver does not exist yet; it must be implemented with actual, verified material properties rather than fake values or no-op memoization.

## Invalidation behaviour

- Room structure edits invalidate the original snapshot; room fingerprint changes invalidate cached membership.
- Equipment movement, addition and removal invalidate controller-zone placement but preserve membership if room cells are unchanged.
- `RoomTemperatureCache.SetTopology` atomically replaces room-cell membership and revokes the previously published temperature generation.
- Room measurements remain read-only until Muqet publishes its authoritative temperature snapshot.
- Existing 2,500-tick fallback polling remains active for missed Harmony notifications. It can be optimised in a subsequent change using more granular callbacks.

## Verification

`tests/Muqet.Adapter.Tests` adds deterministic assertions for (a) device moves not changing room fingerprint, (b) room changes affecting room fingerprint, (c) snapshot immutability after mutable input is edited, and (d) a cached room mean being reused within a generation and recomputed on publication.

This deliberately stops short of hooking a live material cache or overriding vanilla heat. Those depend on M8 material coefficients and energy-accounted heat sources.
