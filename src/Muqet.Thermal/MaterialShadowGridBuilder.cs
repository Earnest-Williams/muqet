namespace Muqet.Thermal;

/// <summary>
/// Physical bulk material properties. Values are SI: conductivity W/(m K)
/// and volumetric sensible heat capacity J/(m3 K).
/// These immutable profiles may be resolved from DefModExtension overrides or
/// from documented class fallbacks, but never from vanilla abstract heat units.
/// </summary>
public readonly record struct BulkThermalMaterial(
    float ConductivityWPerMK, float VolumetricCapacityJPerM3K)
{
    public void Validate()
    {
        if (!float.IsFinite(ConductivityWPerMK) || ConductivityWPerMK <= 0
            || !float.IsFinite(VolumetricCapacityJPerM3K) || VolumetricCapacityJPerM3K <= 0)
            throw new ArgumentException("Bulk material must have positive finite conductivity and volumetric capacity.");
    }
}

/// <summary>
/// Coarse physical starting values, not experimentally calibrated RimWorld
/// coefficients. These categories are intentionally overridable by Def data.
/// The numeric values describe plausible bulk materials, NOT effective room
/// construction including cavities, mortar, fasteners, air leaks or furnishes.
/// </summary>
public static class ShadowBulkMaterialDefaults
{
    public static BulkThermalMaterial Rock => new(2.2f, 2_100_000f);
    public static BulkThermalMaterial Wood => new(0.14f, 1_350_000f);
    public static BulkThermalMaterial Metal => new(45f, 3_500_000f);
    public static BulkThermalMaterial Soil => new(0.8f, 1_700_000f);
    public static BulkThermalMaterial Water => new(0.6f, 4_180_000f);
    public static BulkThermalMaterial Textile => new(0.06f, 260_000f);
    public static BulkThermalMaterial Masonry => new(1.5f, 1_800_000f);
}

/// <summary>
/// Copied game-thread material and geometry data for one map cell. A solid
/// barrier must supply a material and its effective thickness; an existing
/// roof must supply material and active depth. Terrain is always required.
/// No Verse objects or material Defs cross into this record.
/// </summary>
public readonly record struct MaterialShadowCell(
    ShadowCellKind Kind,
    ShadowRoofKind Roof,
    BulkThermalMaterial Terrain,
    BulkThermalMaterial Structure,
    BulkThermalMaterial RoofMaterial,
    float StructureThicknessM,
    float RoofActiveDepthM)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Roof))
            throw new ArgumentException("Unknown geometry kind.");
        Terrain.Validate();
        bool solid = Kind is ShadowCellKind.SolidBarrier or ShadowCellKind.ClosedDoor;
        if (solid)
        {
            Structure.Validate();
            if (!float.IsFinite(StructureThicknessM) || StructureThicknessM <= 0)
                throw new ArgumentException("Solid barrier thickness must be finite and positive.");
        }
        if (Roof != ShadowRoofKind.Unroofed)
        {
            RoofMaterial.Validate();
            if (!float.IsFinite(RoofActiveDepthM) || RoofActiveDepthM <= 0)
                throw new ArgumentException("Roof effective depth must be finite and positive.");
        }
    }
}

/// <summary>
/// Explicit physical-to-grid conversion constants. The two-layer solver has
/// only one local solid node, so floors, walls and roofs at a cell contribute
/// to its combined effective heat capacity. Solid/air surface conductances are
/// reduced by an internal contact resistance to avoid instantly equilibrating
/// a large mountain mass with the air.
/// </summary>
public readonly record struct MaterialShadowCalibration(
    float CellAirCapacityJPerK,
    float TerrainActiveDepthM,
    float AirMixingWPerK,
    float SurfaceFilmWPerM2K,
    float ClosedDoorLeakageWPerK,
    float UnroofedOutdoorWPerK,
    float MaxSolidLateralWPerK)
{
    public static MaterialShadowCalibration Provisional => new(
        CellAirCapacityJPerK: 3015f,
        TerrainActiveDepthM: 0.12f,
        AirMixingWPerK: 40f,
        SurfaceFilmWPerM2K: 8f,
        ClosedDoorLeakageWPerK: 1f,
        UnroofedOutdoorWPerK: 120f,
        MaxSolidLateralWPerK: 10f);

    public void Validate()
    {
        if (!float.IsFinite(CellAirCapacityJPerK) || CellAirCapacityJPerK <= 0
            || !float.IsFinite(TerrainActiveDepthM) || TerrainActiveDepthM <= 0
            || !float.IsFinite(AirMixingWPerK) || AirMixingWPerK <= 0
            || !float.IsFinite(SurfaceFilmWPerM2K) || SurfaceFilmWPerM2K <= 0
            || !float.IsFinite(ClosedDoorLeakageWPerK) || ClosedDoorLeakageWPerK < 0
            || !float.IsFinite(UnroofedOutdoorWPerK) || UnroofedOutdoorWPerK < 0
            || !float.IsFinite(MaxSolidLateralWPerK) || MaxSolidLateralWPerK < 0)
            throw new ArgumentException("Material-to-grid calibration has invalid parameters.");
    }
}

/// <summary>
/// Material-aware two-layer shadow grid. Effective conduction through a
/// barrier is represented by symmetric heat-transfer edges. These edges do
/// NOT represent mass advection/airflow through a wall. Thick mountain roofs
/// have a finite coupled rock capacity but no instantaneous *outdoor weather*
/// link; a deep-geothermal reservoir will require a distinct future solver node.
/// </summary>
public static class MaterialShadowGridBuilder
{
    public static ThermalGrid Build(int width, int height,
        ReadOnlySpan<MaterialShadowCell> cells,
        ReadOnlySpan<float> initialAirC,
        MaterialShadowCalibration calibration)
    {
        calibration.Validate();
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        int n = checked(width * height);
        if (cells.Length != n || initialAirC.Length != n)
            throw new ArgumentException("Material and temperature field lengths must match map.");

        var grid = new ThermalGrid(width, height);
        for (int i = 0; i < n; i++)
        {
            MaterialShadowCell cell = cells[i];
            cell.Validate();
            float t = initialAirC[i];
            if (!float.IsFinite(t) || t < -273.15f)
                throw new ArgumentException($"Invalid initial thermal temperature at cell {i}.");
            bool solid = IsBarrier(cell.Kind);
            double cap = (double)calibration.TerrainActiveDepthM * cell.Terrain.VolumetricCapacityJPerM3K;
            double coupling = SurfaceContact(cell.Terrain.ConductivityWPerMK,
                calibration.TerrainActiveDepthM, calibration.SurfaceFilmWPerM2K);

            if (solid)
            {
                cap += (double)cell.StructureThicknessM * cell.Structure.VolumetricCapacityJPerM3K;
                coupling += SurfaceContact(cell.Structure.ConductivityWPerMK,
                    cell.StructureThicknessM, calibration.SurfaceFilmWPerM2K);
            }
            if (cell.Roof != ShadowRoofKind.Unroofed)
            {
                cap += (double)cell.RoofActiveDepthM * cell.RoofMaterial.VolumetricCapacityJPerM3K;
                coupling += SurfaceContact(cell.RoofMaterial.ConductivityWPerMK,
                    cell.RoofActiveDepthM, calibration.SurfaceFilmWPerM2K);
            }

            if (!double.IsFinite(cap) || cap <= 0 || cap > float.MaxValue
                || !double.IsFinite(coupling) || coupling > float.MaxValue)
                throw new ArgumentException($"Unrepresentable bulk thermal properties at cell {i}.");

            grid.AirTemperatureC[i] = t;
            grid.SolidTemperatureC[i] = t;
            grid.AirCapacityJPerK[i] = calibration.CellAirCapacityJPerK;
            grid.SolidCapacityJPerK[i] = (float)cap;
            grid.AirSolidWPerK[i] = (float)coupling;
            grid.OutdoorWPerK[i] = cell.Roof switch
            {
                ShadowRoofKind.Unroofed => calibration.UnroofedOutdoorWPerK,
                ShadowRoofKind.ThinRoof => RoofOutdoorConductance(
                    cell.RoofMaterial.ConductivityWPerMK, cell.RoofActiveDepthM,
                    calibration.SurfaceFilmWPerM2K),
                // Thick mountain stone exchanges with its *finite rock mass*,
                // not the instantly changing outdoor weather reservoir.
                ShadowRoofKind.ThickRoof => 0,
                _ => throw new ArgumentException("Invalid roof type.")
            };
        }

        for (int i = 0; i < n; i++)
        {
            if (i % width + 1 < width)
            {
                grid.AirEastWPerK[i] = InterfaceConductance(cells[i], cells[i + 1], calibration);
                grid.SolidEastWPerK[i] = SolidLateral(cells[i], cells[i + 1], calibration);
            }
            if (i + width < n)
            {
                grid.AirNorthWPerK[i] = InterfaceConductance(cells[i], cells[i + width], calibration);
                grid.SolidNorthWPerK[i] = SolidLateral(cells[i], cells[i + width], calibration);
            }
        }
        return grid;
    }

    private static bool IsBarrier(ShadowCellKind kind) =>
        kind is ShadowCellKind.SolidBarrier or ShadowCellKind.ClosedDoor;

    /// <summary>
    /// Effective (non-advective) thermal contact from air to half the solid
    /// thickness, limited by an air/surface film resistance.
    /// </summary>
    private static double SurfaceContact(double conductivity, double thickness, double film)
        => 1d / (1d / film + thickness / (2d * conductivity));

    private static float RoofOutdoorConductance(double conductivity, double depth, double film)
    {
        // Two films, internal and external, plus roof conduction in series.
        double g = 1d / (2d / film + depth / conductivity);
        return checked((float)g);
    }

    private static float InterfaceConductance(
        MaterialShadowCell a, MaterialShadowCell b, MaterialShadowCalibration c)
    {
        bool sa = IsBarrier(a.Kind), sb = IsBarrier(b.Kind);
        if (!sa && !sb) return c.AirMixingWPerK;

        // Each barrier node sits approximately at its own midplane. The
        // resistance along a cardinal edge includes half the thickness
        // of any barriers at either end plus surface films. This is
        // conducted heat, NOT airflow across an impassable tile.
        double resistance = 2d / c.SurfaceFilmWPerM2K;
        if (sa) resistance += a.StructureThicknessM / (2d * a.Structure.ConductivityWPerMK);
        if (sb) resistance += b.StructureThicknessM / (2d * b.Structure.ConductivityWPerMK);
        double conductive = 1d / resistance;
        if (a.Kind == ShadowCellKind.ClosedDoor || b.Kind == ShadowCellKind.ClosedDoor)
        {
            // Closed doors may leak air at low rate, unless a true wall is
            // also present along this edge.
            if (a.Kind != ShadowCellKind.SolidBarrier && b.Kind != ShadowCellKind.SolidBarrier)
                conductive += c.ClosedDoorLeakageWPerK;
        }
        return (float)Math.Min(conductive, c.AirMixingWPerK);
    }

    private static float SolidLateral(
        MaterialShadowCell a, MaterialShadowCell b, MaterialShadowCalibration c)
    {
        // Effective floor-to-floor conduction. All cells have a terrain
        // slab; use the harmonic mean so a low-k slab dominates an edge.
        double ka = a.Terrain.ConductivityWPerMK;
        double kb = b.Terrain.ConductivityWPerMK;
        double harmonicK = 2d * ka * kb / (ka + kb);
        double wPerK = harmonicK * c.TerrainActiveDepthM;
        return (float)Math.Min(wPerK, c.MaxSolidLateralWPerK);
    }
}
