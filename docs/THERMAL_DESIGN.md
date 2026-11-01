# Muqet thermal design: CPU reference (M1)

## Goal

Muqet is an **independent** thermal implementation for RimWorld 1.7. Garwel's Celsius and lxnt's Celsius3 are research references, not its codebase. This first milestone is intentionally pure C# without Verse, Unity, Harmony, GPU SDKs or third-party NuGet packages. The CPU backend is the correctness oracle for later acceleration.

## Representation and units

- Two separate fields per cell: **air** temperature and **solid** temperature (terrain/buildings represented as aggregate local thermal mass for now).
- Celsius for temperature; Kelvin for temperature differences; joules per kelvin (J/K) for heat capacity; watts per kelvin (W/K) for conductance; watts for heat source power; **seconds** for the solver time step.
- Row-major flat arrays. The horizontal/east edge `East[i]` connects cell `i` to `i+1`, except at the last column. The north edge `North[i]` connects `i` to `i+Width`, except at the top row. The conductance for an edge is stored **once** and used symmetrically by both cells.
- `AirSolid[i]` couples the two layers; `Outdoor[i]` couples air to a fixed environmental reservoir. Solid cells can be absent (`SolidCapacity[i] = 0`) provided no solid source or edge connects them.
- The host is responsible for topology and material coefficients, conversion between game ticks and simulation seconds, and deciding how a building/roof affects edges. **No gameplay adapter is included in M1.**

## Discretisation

For a cell's air node, with heat capacity `C`, temperature `T`, neighbour temperatures `T_j`, shared edge conductances `G_ij`, outdoor temperature `T_o`, air-solid conductance `G_as`, and continuous heating power `P`:

```text
C dT/dt = sum_j G_ij (T_j - T) + G_as (T_solid - T)
        + G_out (T_o - T) + P
```

The solid node has the corresponding equal-and-opposite air-solid flux, its own solid neighbours, and its own power input. Internal edges conserve thermal energy in exact arithmetic because every pair contributes `+G * (T_j - T_i)` and `-G * (T_j - T_i)`. The outdoor reservoir and sources are the only intentional energy transfers across the model boundary.

Updates use a **gather stencil** instead of scatter/atomics. One output is written per cell from a double-buffered prior snapshot, making the algorithm straightforward to reproduce in a GPU compute shader. Explicit Euler is subdivided so `dt * max_node(sum_conductances / capacity) <= 0.9`; this ensures nonnegative neighbour/reservoir weights when there are no sources. When the requested interval would require over 4,096 substeps it fails rather than silently losing simulated time.

The result includes an energy ledger: before, after, total injected source energy, integrated outdoor exchange and residual. Floating-point roundoff is expected; the residual should remain small relative to the stored energy and source transfers.

## Scope exclusions (intentional)

- No directional air mass/enthalpy advection (doors, roof vents, wind and buoyancy).
- No distinct radiation flux, pressure/vacuum, humidity, phase-change latent heat, or material-specific temperature-dependent coefficients.
- No terrain freezing, autoignition, pawn weather effects, rendering, save format or Harmony hooks.
- No automatic timestep-to-tick calibration or RimWorld heat-source conversion. We must not silently equate an existing RimWorld abstract heat number with physical watts.
- CPU `Advance` is synchronous and assumes exclusive ownership of mutable grid arrays during the call. The future GPU implementation may expose async dispatch/readback in addition to the parity API.

## Verification

`dotnet run --project tests/Muqet.Thermal.Tests/Muqet.Thermal.Tests.csproj -c Release`

The no-dependency console regression suite checks isolated equilibrium; horizontal, vertical, air-solid and solid-solid conservation; outdoor/source accounting; automatic substeps and excessive-timestep rejection; invalid data rejection; repeat-run determinism; and a 250 x 250 grid.

## Next milestones

1. Establish executable numerical tests in CI and measure CPU performance on representative map sizes (including 476 x 476); add a simple benchmark executable with timing distributions.
2. Build a RimWorld integration adapter that prepares dirty thermal edges/material coefficients, queues source power, handles save checkpoints, and publishes CPU temperature snapshots. Ensure no Verse/Unity objects are accessed from background workers.
3. Add a **conservative airflow/enthalpy transport** pass with door/window/roof openings, wind-driven exchange and buoyancy approximations. Validate greenhouse roof-vent stability and enclosed geothermal heating.
4. Prototype Vulkan compute with the same edge convention, state arrays and numeric parity fixtures. Keep the CPU backend as automatic fallback and regression oracle.

Licensing: these are newly authored Muqet files, not copied from either reference implementation. Choose an explicit project license before distribution.
