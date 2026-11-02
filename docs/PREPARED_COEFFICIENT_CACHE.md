# M5: prepared thermal-coefficient caching

The CPU reference backend now supports **opt-in cached structural coefficients**. Call `CpuThermalBackend.PrepareCoefficients()` to enable. For ordinary uncached mode, `Advance` retains the conservative full-map validation path.

## Why this cache exists

Garwel's Celsius 2.7 caches per-cell conductances and refreshes only dirty local changes, instead of repeating material lookups during each thermal step. Muqet now offers that optimisation without requiring a global room-response table or introducing thermal barriers between virtual 25-cell controller zones.

- A `ThermalCoefficientCache` holds a validated copy of **air and solid capacities, east/north conductances, air-solid coupling and outdoor coupling**. The cached arrays are used during integration and its CFL maximum rate is maintained in a segment-max tree.
- **Temperature and source power remain live**: edits to temperatures and W sources are checked each advance without structural invalidation.
- **Structural edits require explicit invalidation**. `InvalidateCell(i)` marks the cell storing an edge or material field; `InvalidateAll()` is available when changing many properties. `Refresh()` updates just dirty cells and recomputes stability rates for themselves and adjacent cells.
- `Refresh()` validates an entire dirty batch before publishing any change. Invalid refresh leaves the published coefficients intact and the batch dirty. `Advance()` refuses to use a cache with pending invalidations.
- `AssertMatchesHost()` is an optional O(N) diagnostic to detect accidental structural edits made without invalidation; normal cached advances intentionally do not pay that cost.
- `TotalCapturedCells`, `LastCapturedCells`, `Revision` measure coefficient-refresh work.

The CPU kernel now stages every substep in private ping-pong buffers and publishes to `ThermalGrid` only when the **whole advance** completes. Numerical errors during intermediate substeps cannot partially modify published temperature arrays. This does not make source/host mutation safe during a concurrent advance; that remains prohibited.

## Important limitations

A cached coefficient snapshot is a **contract**, not magic dirty detection: the exposed `ThermalGrid` structural arrays are writable. The RimWorld adapter must invalidate the exact cells when terrain, roof, building, material or permeability changes; otherwise prepared mode continues using the last valid snapshot. The diagnostic assertion detects this in development.

Current coefficient computation and material extraction are not yet connected to the read-only RimWorld room adapter. We still need an authoritative physical material/geometry converter and heating-power model; no existing vanilla heat output is automatically interpreted as SI watts.

Do not confuse this cache with a precomputed rectangular-room thermal response: **every simulated air and solid cell still advances**. Room-response prediction remains a separate optimisation to benchmark against this baseline.

## Verification

```
dotnet run --project tests/Muqet.Thermal.Tests/Muqet.Thermal.Tests.csproj -c Release
```

Added numerical regressions cover uncached/prepared parity, no-change refresh, sparse dirty updates and adjacent rate propagation, failed refresh rollback, stale edit detection, dynamic source validation, and no partial temperature publication after a failed advance.

## Recommended next phase

1. Connect terrain/structure/door event invalidations and calibrated material coefficients in the RimWorld adapter.
2. Benchmark identical 250x250 and 476x476 maps with cached and uncached CPU paths under static and mixed-edit workloads.
3. Evaluate rectangular-room steady-state response predictions as a **thermostat preview**, not as replacement temperatures, with energy accounting left in the authoritative solver.
