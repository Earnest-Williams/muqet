# M11 — material-aware thermal shadow calibration

This milestone replaces the native shadow's uniformly assigned wall/floor/roof coefficients with **material-resolved bulk properties**, using actual RimWorld cell geometry, `TerrainDef`, `ThingDef`, `StuffDef`, and `RoofDef` as inputs. **No vanilla temperature control is intercepted.** The model remains shadow-only.

## Sources and hierarchy

On the RimWorld game thread, `RimWorldMaterialShadowCollector` captures each cell's edifice, terrain, door state, roof and observed vanilla air temperature. `RimWorldMaterialResolver` uses the M6 `ThermalDefinitionCache` by terrain short hash and ThingDef/StuffDef short-hash pair, so repeat definitions are not repeatedly resolved.

Resolution priority is:

1. A `MuqetThermalMaterialExtension` on a building `ThingDef`, then on its material `StuffDef`, if present.
2. Otherwise, a coarse category fallback: wood, metal, stone, fabric/leather, or generic masonry. Natural mineable rock gets the rock profile.
3. `TerrainDef` extension or default soil/water profile.
4. `RoofDef` extension or thick/natural rock and thin/constructed timber fallback.

The optional `DefModExtension` exposes bulk conductivity in **W/(m K)** and volumetric sensible heat capacity in **J/(m³ K)**. Both fields must be supplied; invalid explicit values fail fast rather than introducing nonphysical coefficients.

Illustrative XML structure:

```xml
<modExtensions>
  <li Class="Muqet.RimWorld.MuqetThermalMaterialExtension">
    <conductivityWPerMK>0.14</conductivityWPerMK>
    <volumetricCapacityJPerM3K>1350000</volumetricCapacityJPerM3K>
  </li>
</modExtensions>
```

These material properties are **not** an entire wall's R-value. In particular, wall thickness and construction details are represented separately, and all fallback profiles require balancing/testing.

## Physical discretisation and assumptions

`MaterialShadowGridBuilder` builds one air node and one aggregate solid node per map cell, using the immutable `MaterialShadowCell` snapshot. It includes:

- Bulk solid thermal capacity from active terrain depth, barrier thickness and roof depth, without replacing the existing cell's air mass.
- Conduction through barriers determined from material conductivity, half-thickness resistances and surface film resistance.
- Open door/open air passage exchange versus low closed-door leakage. The conductive barrier path is not treated as moving air mass.
- Roof-to-outdoor resistance through a thin roof and exterior/interior surface films.
- **Thick mountain roofs have no immediate coupling to outside weather.** Their finite rock mass participates in the solid node instead; a geothermal/deep-rock boundary is a separate future extension.
- Shared east/north edges are symmetric, so adjacent cell exchanges conserve total energy up to floating-point roundoff.

Provisional assumptions include terrain effective depth 0.12 m, normal wall 0.35 m, closed door 0.18 m, natural rock wall 1.25 m and thick rock roof depth 1.5 m. These are modelling parameters, **not** a statement about RimWorld sprite or game-cell dimensions.

The simplified two-layer solver still aggregates wall, floor and roof into one solid temperature node. It does not yet model separate wall-face temperatures, ground-depth diffusion, heat radiative transfer, pressure-driven ventilation, humidity or detailed source inventory.

## Corrections over M10

- Walls no longer all receive the same capacity and conductance irrespective of material.
- Thick rock roofs no longer track immediate outdoor temperature changes through a direct roof-to-outside conductance.
- Room geometry invalidation hashes the **actual captured material profile and effective thickness/depth**, not merely roof/door enums.
- The same observational, map-local shadow state is rebuilt when the captured geometry signature changes. It cannot alter vanilla behaviour.
- An impassable cell with no discoverable edifice uses a conservative provisional rock fallback rather than an invalid zero-capacity material.

## Verification and remaining gates

Tests cover bulk thermal inertia versus material and thickness, wood-versus-metal conductivity, thick rock roof weather isolation, door constriction, conservation and invalid profiles. Earlier native 1.7 build, HVAC, thermostat, room-cache and numerical tests remain in CI.

Before making the model authoritative, verify the real game build's material extension resolution, roof material categories, door movement, save/load and large-map performance. Calibrate material constants with realistic 5×5 and 10×10 rooms, mountain freezers and exterior heat sinks. The game still owns temperatures, appliances and saves. A shadow/vanilla mismatch cannot yet establish whether the model or vanilla is physically preferable because other sources and ventilation are absent.

Source references: [Celsius material extensions](https://github.com/GarwelGarwel/Celsius/blob/master/Source/ThingThermalProperties.cs), [StuffThermalProperties](https://github.com/GarwelGarwel/Celsius/blob/master/Source/StuffThermalProperties.cs).
