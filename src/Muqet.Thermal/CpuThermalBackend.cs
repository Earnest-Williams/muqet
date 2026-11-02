namespace Muqet.Thermal;

/// <summary>
/// Deterministic, allocation-free-per-step, two-layer finite-volume reference solver.
/// Internal edges use the same conductance in both directions; an explicit Euler step
/// is split so the sum of outgoing rates from every node stays below 0.9.
///
/// This backend intentionally has no Verse/Unity dependencies, dynamic airflow,
/// radiation or latent heat. It is a conservation baseline for future backends.
/// </summary>
public sealed class CpuThermalBackend : IThermalBackend
{
    public const double StabilityLimit = 0.9;
    public const int MaximumSubsteps = 4096;

    private readonly ThermalGrid grid;
    private readonly float[] nextAir;
    private readonly float[] nextSolid;
    private readonly float[] stagedAir;
    private readonly float[] stagedSolid;
    private ThermalCoefficientCache? prepared;

    public CpuThermalBackend(ThermalGrid grid)
    {
        this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
        nextAir = new float[grid.CellCount];
        nextSolid = new float[grid.CellCount];
        stagedAir = new float[grid.CellCount];
        stagedSolid = new float[grid.CellCount];
    }

    /// <summary>Enable opt-in structural caching. Structural edits require cache invalidation.</summary>
    public ThermalCoefficientCache PrepareCoefficients()
    {
        prepared ??= new ThermalCoefficientCache(grid);
        prepared.Refresh();
        return prepared;
    }

    public int Width => grid.Width;
    public int Height => grid.Height;

    public void CopyAirTemperatures(Span<float> destination)
    {
        if (destination.Length < grid.CellCount)
            throw new ArgumentException("Destination too small.", nameof(destination));
        grid.AirTemperatureC.AsSpan().CopyTo(destination);
    }

    public void CopySolidTemperatures(Span<float> destination)
    {
        if (destination.Length < grid.CellCount)
            throw new ArgumentException("Destination too small.", nameof(destination));
        grid.SolidTemperatureC.AsSpan().CopyTo(destination);
    }

    public ThermalAdvanceResult Advance(double elapsedSeconds, float outdoorTemperatureC)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Elapsed time must be finite and nonnegative.");
        if (!float.IsFinite(outdoorTemperatureC))
            throw new ArgumentOutOfRangeException(nameof(outdoorTemperatureC));

        // Validate the mutable host-owned arrays and recompute the CFL bound. No allocation.
        double maximumRate = prepared is null
            ? ValidateAndFindMaximumRate()
            : ValidateDynamicStateAndGetPreparedRate();
        double before = grid.TotalEnergyJ();
        if (elapsedSeconds == 0)
            return new ThermalAdvanceResult(0, 0, before, before, 0, 0);

        double requested = Math.Ceiling(elapsedSeconds * maximumRate / StabilityLimit);
        if (!double.IsFinite(requested) || requested > MaximumSubsteps)
            throw new InvalidOperationException("Thermal timestep requires too many stable substeps; reduce elapsed time or conductance.");
        int steps = Math.Max(1, (int)requested);
        double h = elapsedSeconds / steps;
        double outdoorJ = 0;
        double sourceWatts = 0;
        for (int i = 0; i < grid.CellCount; i++)
            sourceWatts += (double)grid.AirSourceW[i] + grid.SolidSourceW[i];

        // Never write to published state until every substep has succeeded.
        float[] readAir = grid.AirTemperatureC;
        float[] readSolid = grid.SolidTemperatureC;
        float[] writeAir = nextAir;
        float[] writeSolid = nextSolid;
        int width = grid.Width;
        int n = grid.CellCount;

        for (int s = 0; s < steps; s++)
        {
            for (int i = 0; i < n; i++)
            {
                int x = i % width;
                double air = readAir[i];
                double solid = readSolid[i];
                PreparedThermalCell p = prepared is null ? default : prepared[i];
                float coupling = prepared is null ? grid.AirSolidWPerK[i] : p.AirSolid;
                float outdoor = prepared is null ? grid.OutdoorWPerK[i] : p.Outdoor;
                double airFlow = grid.AirSourceW[i]
                    + outdoor * (outdoorTemperatureC - air)
                    + coupling * (solid - air);
                double solidFlow = grid.SolidSourceW[i] + coupling * (air - solid);

                if (x > 0)
                {
                    float ae = prepared is null ? grid.AirEastWPerK[i - 1] : prepared[i - 1].AirEast;
                    float se = prepared is null ? grid.SolidEastWPerK[i - 1] : prepared[i - 1].SolidEast;
                    airFlow += ae * (readAir[i - 1] - air);
                    solidFlow += se * (readSolid[i - 1] - solid);
                }
                if (x + 1 < width)
                {
                    float ae = prepared is null ? grid.AirEastWPerK[i] : p.AirEast;
                    float se = prepared is null ? grid.SolidEastWPerK[i] : p.SolidEast;
                    airFlow += ae * (readAir[i + 1] - air);
                    solidFlow += se * (readSolid[i + 1] - solid);
                }
                if (i >= width)
                {
                    float an = prepared is null ? grid.AirNorthWPerK[i - width] : prepared[i - width].AirNorth;
                    float sn = prepared is null ? grid.SolidNorthWPerK[i - width] : prepared[i - width].SolidNorth;
                    airFlow += an * (readAir[i - width] - air);
                    solidFlow += sn * (readSolid[i - width] - solid);
                }
                if (i + width < n)
                {
                    float an = prepared is null ? grid.AirNorthWPerK[i] : p.AirNorth;
                    float sn = prepared is null ? grid.SolidNorthWPerK[i] : p.SolidNorth;
                    airFlow += an * (readAir[i + width] - air);
                    solidFlow += sn * (readSolid[i + width] - solid);
                }

                double updatedAir = air + h * airFlow / (prepared is null ? grid.AirCapacityJPerK[i] : p.AirCapacity);
                double updatedSolid = (prepared is null ? grid.SolidCapacityJPerK[i] : p.SolidCapacity) > 0
                    ? solid + h * solidFlow / (prepared is null ? grid.SolidCapacityJPerK[i] : p.SolidCapacity)
                    : solid;
                if (!double.IsFinite(updatedAir) || !double.IsFinite(updatedSolid)
                    || updatedAir < -273.15 || updatedSolid < -273.15)
                    throw new InvalidOperationException($"Nonphysical temperature at cell {i}; check heat sources and timestep.");
                writeAir[i] = (float)updatedAir;
                writeSolid[i] = (float)updatedSolid;
                outdoorJ += h * outdoor * (outdoorTemperatureC - air);
            }
            readAir = writeAir;
            readSolid = writeSolid;
            writeAir = ReferenceEquals(readAir, nextAir) ? stagedAir : nextAir;
            writeSolid = ReferenceEquals(readSolid, nextSolid) ? stagedSolid : nextSolid;
        }

        // When the number of steps is even, the second private buffer is used.
        // No substep ever targets the grid arrays.
        Array.Copy(readAir, grid.AirTemperatureC, n);
        Array.Copy(readSolid, grid.SolidTemperatureC, n);

        double after = grid.TotalEnergyJ();
        return new ThermalAdvanceResult(steps, elapsedSeconds, before, after, sourceWatts * elapsedSeconds, outdoorJ);
    }

    private double ValidateDynamicStateAndGetPreparedRate()
    {
        ThermalCoefficientCache cache = prepared!;
        if (cache.HasPendingChanges)
            throw new InvalidOperationException("Refresh invalidated coefficient changes before advancing.");
        for (int i = 0; i < grid.CellCount; i++)
        {
            float air = grid.AirTemperatureC[i], solid = grid.SolidTemperatureC[i];
            float airSource = grid.AirSourceW[i], solidSource = grid.SolidSourceW[i];
            if (!float.IsFinite(air) || !float.IsFinite(solid)
                || air < -273.15f || solid < -273.15f
                || !float.IsFinite(airSource) || !float.IsFinite(solidSource))
                throw new ArgumentException($"Invalid dynamic thermal state at cell {i}.");
            if (cache[i].SolidCapacity == 0 && solidSource != 0)
                throw new ArgumentException($"Inactive solid cell {i} has a source.");
        }
        return cache.MaximumOutgoingRate;
    }

    private double ValidateAndFindMaximumRate()
    {
        int n = grid.CellCount;
        int width = grid.Width;
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            int x = i % width;
            float airCapacity = grid.AirCapacityJPerK[i];
            float solidCapacity = grid.SolidCapacityJPerK[i];
            RequireFinite(grid.AirTemperatureC[i], i, nameof(grid.AirTemperatureC));
            RequireFinite(grid.SolidTemperatureC[i], i, nameof(grid.SolidTemperatureC));
            RequireFinite(airCapacity, i, nameof(grid.AirCapacityJPerK));
            RequireFinite(solidCapacity, i, nameof(grid.SolidCapacityJPerK));
            if (airCapacity <= 0 || solidCapacity < 0)
                throw new ArgumentException($"Nonpositive air capacity or negative solid capacity at cell {i}.");
            if (grid.AirTemperatureC[i] < -273.15f || grid.SolidTemperatureC[i] < -273.15f)
                throw new ArgumentException($"Temperature below absolute zero at cell {i}.");

            float ae = Nonnegative(grid.AirEastWPerK[i], i, nameof(grid.AirEastWPerK));
            float an = Nonnegative(grid.AirNorthWPerK[i], i, nameof(grid.AirNorthWPerK));
            float se = Nonnegative(grid.SolidEastWPerK[i], i, nameof(grid.SolidEastWPerK));
            float sn = Nonnegative(grid.SolidNorthWPerK[i], i, nameof(grid.SolidNorthWPerK));
            float coupling = Nonnegative(grid.AirSolidWPerK[i], i, nameof(grid.AirSolidWPerK));
            float outdoors = Nonnegative(grid.OutdoorWPerK[i], i, nameof(grid.OutdoorWPerK));
            RequireFinite(grid.AirSourceW[i], i, nameof(grid.AirSourceW));
            RequireFinite(grid.SolidSourceW[i], i, nameof(grid.SolidSourceW));

            if ((x == width - 1 && (ae != 0 || se != 0))
                || (i + width >= n && (an != 0 || sn != 0)))
                throw new ArgumentException($"Conductance crosses the grid boundary at cell {i}.");
            if (solidCapacity == 0 && (coupling != 0 || se != 0 || sn != 0 || grid.SolidSourceW[i] != 0))
                throw new ArgumentException($"Inactive solid cell {i} has a source or connection.");
            if (x + 1 < width && se > 0 && grid.SolidCapacityJPerK[i + 1] <= 0)
                throw new ArgumentException($"Solid edge {i} connects to an inactive cell.");
            if (i + width < n && sn > 0 && grid.SolidCapacityJPerK[i + width] <= 0)
                throw new ArgumentException($"Solid edge {i} connects to an inactive cell.");
            if (solidCapacity == 0 &&
                ((x > 0 && grid.SolidEastWPerK[i - 1] > 0)
                || (i >= width && grid.SolidNorthWPerK[i - width] > 0)))
                throw new ArgumentException($"Inactive solid cell {i} receives an edge.");

            double airSum = coupling + outdoors + ae + an;
            double solidSum = coupling + se + sn;
            if (x > 0)
            {
                airSum += grid.AirEastWPerK[i - 1];
                solidSum += grid.SolidEastWPerK[i - 1];
            }
            if (i >= width)
            {
                airSum += grid.AirNorthWPerK[i - width];
                solidSum += grid.SolidNorthWPerK[i - width];
            }
            max = Math.Max(max, airSum / airCapacity);
            if (solidCapacity > 0)
                max = Math.Max(max, solidSum / solidCapacity);
        }
        return max;
    }

    private static float Nonnegative(float value, int index, string name)
    {
        RequireFinite(value, index, name);
        if (value < 0) throw new ArgumentException($"Negative {name} at cell {index}.");
        return value;
    }

    private static void RequireFinite(float value, int index, string name)
    {
        if (!float.IsFinite(value)) throw new ArgumentException($"Nonfinite {name} at cell {index}.");
    }
}
