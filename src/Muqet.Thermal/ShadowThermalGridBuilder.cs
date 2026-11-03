namespace Muqet.Thermal;

/// <summary>
/// Coarse geometry class observed on the RimWorld game thread. These are
/// proxies used in developer-only shadow comparisons, NOT a material database.
/// </summary>
public enum ShadowCellKind : byte
{
    OpenAir,
    SolidBarrier,
    OpenDoor,
    ClosedDoor
}

public enum ShadowRoofKind : byte { Unroofed, ThinRoof, ThickRoof }

public readonly record struct ShadowCell(ShadowCellKind Kind, ShadowRoofKind Roof);

/// <summary>
/// Explicit, provisional geometrical transfer coefficients. A RimWorld cell is
/// assumed to occupy 1 m^2, with an assumed 2.5 m air column. Actual building
/// stuff/terrain properties and airflow are not implemented by this profile.
///
/// All capacities are J/K; conductances W/K. These values are *not* claimed to
/// reproduce real RimWorld heat behavior or material-specific construction.
/// </summary>
public readonly record struct ShadowThermalCalibration(
    float CellAirCapacityJPerK,
    float OrdinarySolidCapacityJPerK,
    float BarrierSolidCapacityJPerK,
    float AirMixingWPerK,
    float ClosedDoorPassageWPerK,
    float BarrierPassageWPerK,
    float OrdinaryAirSolidWPerK,
    float BarrierAirSolidWPerK,
    float SolidLateralWPerK,
    float UnroofedOutdoorWPerK,
    float ThinRoofOutdoorWPerK,
    float ThickRoofOutdoorWPerK)
{
    public static ShadowThermalCalibration Provisional => new(
        CellAirCapacityJPerK: 3015f,           // 1m² x 2.5m x 1.2kg/m³ x 1005J/(kg K)
        OrdinarySolidCapacityJPerK: 120000f,  // estimated effective floor/contents mass
        BarrierSolidCapacityJPerK: 280000f,   // estimated effective wall/door mass
        AirMixingWPerK: 40f,                   // effective convection / mixing, not molecular diffusion
        ClosedDoorPassageWPerK: 1f,
        BarrierPassageWPerK: 0.3f,
        OrdinaryAirSolidWPerK: 3f,
        BarrierAirSolidWPerK: 12f,
        SolidLateralWPerK: 2f,
        UnroofedOutdoorWPerK: 120f,
        ThinRoofOutdoorWPerK: 1.5f,
        ThickRoofOutdoorWPerK: 0.3f);

    public void Validate()
    {
        if (!float.IsFinite(CellAirCapacityJPerK) || CellAirCapacityJPerK <= 0
            || !float.IsFinite(OrdinarySolidCapacityJPerK) || OrdinarySolidCapacityJPerK <= 0
            || !float.IsFinite(BarrierSolidCapacityJPerK) || BarrierSolidCapacityJPerK <= 0)
            throw new ArgumentException("Shadow capacities must be finite and positive.");
        ReadOnlySpan<float> coefficients =
        [
            AirMixingWPerK, ClosedDoorPassageWPerK, BarrierPassageWPerK,
            OrdinaryAirSolidWPerK, BarrierAirSolidWPerK, SolidLateralWPerK,
            UnroofedOutdoorWPerK, ThinRoofOutdoorWPerK, ThickRoofOutdoorWPerK
        ];
        foreach (float g in coefficients)
            if (!float.IsFinite(g) || g < 0)
                throw new ArgumentException("Shadow conductances must be finite and nonnegative.");
    }
}

/// <summary>
/// Builds an independent per-map two-layer grid from *snapshotted* cell classes.
/// No Verse/Unity code runs here. Each internal edge is represented exactly
/// once and used by both adjacent cells, conserving energy across the edge.
///
/// The air node in a solid-barrier cell is an effective surface/boundary
/// intermediary rather than a claim that air flows through a wall.
/// </summary>
public static class ShadowThermalGridBuilder
{
    public static ThermalGrid Build(int width, int height,
        ReadOnlySpan<ShadowCell> geometry,
        ReadOnlySpan<float> initialTemperatureC,
        ShadowThermalCalibration calibration)
    {
        calibration.Validate();
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        int count = checked(width * height);
        if (geometry.Length != count || initialTemperatureC.Length != count)
            throw new ArgumentException("Shadow cell classes and temperatures must match map area.");

        var grid = new ThermalGrid(width, height);
        for (int i = 0; i < count; i++)
        {
            ShadowCell cell = geometry[i];
            if (!Enum.IsDefined(cell.Kind) || !Enum.IsDefined(cell.Roof))
                throw new ArgumentException($"Unknown shadow geometry classification at cell {i}.");
            float temp = initialTemperatureC[i];
            if (!float.IsFinite(temp) || temp < -273.15f)
                throw new ArgumentException($"Invalid initial temperature at cell {i}.");

            bool barrier = cell.Kind is ShadowCellKind.SolidBarrier or ShadowCellKind.ClosedDoor;
            grid.AirTemperatureC[i] = temp;
            grid.SolidTemperatureC[i] = temp;
            grid.AirCapacityJPerK[i] = calibration.CellAirCapacityJPerK;
            grid.SolidCapacityJPerK[i] = barrier
                ? calibration.BarrierSolidCapacityJPerK
                : calibration.OrdinarySolidCapacityJPerK;
            grid.AirSolidWPerK[i] = barrier
                ? calibration.BarrierAirSolidWPerK : calibration.OrdinaryAirSolidWPerK;
            grid.OutdoorWPerK[i] = cell.Roof switch
            {
                ShadowRoofKind.Unroofed => calibration.UnroofedOutdoorWPerK,
                ShadowRoofKind.ThinRoof => calibration.ThinRoofOutdoorWPerK,
                ShadowRoofKind.ThickRoof => calibration.ThickRoofOutdoorWPerK,
                _ => throw new ArgumentException("Invalid roof type.")
            };
        }

        for (int i = 0; i < count; i++)
        {
            if (i % width < width - 1)
            {
                grid.AirEastWPerK[i] = Passage(geometry[i].Kind, geometry[i + 1].Kind, calibration);
                grid.SolidEastWPerK[i] = calibration.SolidLateralWPerK;
            }
            if (i + width < count)
            {
                grid.AirNorthWPerK[i] = Passage(geometry[i].Kind, geometry[i + width].Kind, calibration);
                grid.SolidNorthWPerK[i] = calibration.SolidLateralWPerK;
            }
        }
        return grid;
    }

    private static float Passage(ShadowCellKind a, ShadowCellKind b,
        ShadowThermalCalibration calibration)
    {
        if (a == ShadowCellKind.SolidBarrier || b == ShadowCellKind.SolidBarrier)
            return calibration.BarrierPassageWPerK;
        if (a == ShadowCellKind.ClosedDoor || b == ShadowCellKind.ClosedDoor)
            return calibration.ClosedDoorPassageWPerK;
        return calibration.AirMixingWPerK;
    }
}
