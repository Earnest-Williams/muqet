namespace Muqet.Thermal;

/// <summary>
/// Row-major, structure-of-arrays thermal state. An east edge belongs to its western
/// cell; a north edge belongs to its southern cell. Each shared conductance is stored
/// exactly once, so the flux through an internal edge is equal and opposite.
///
/// Temperatures use Celsius (temperature differences are Kelvin); capacities use J/K;
/// conductances use W/K; continuous sources use W. The host chooses the mapping from
/// RimWorld game ticks to simulation seconds.
///
/// The host may edit fields between Advance calls, never while one is running.
/// Unused solid cells have zero capacity, coupling, source and incident solid edges.
/// </summary>
public sealed class ThermalGrid
{
    public int Width { get; }
    public int Height { get; }
    public int CellCount { get; }

    public float[] AirTemperatureC { get; }
    public float[] SolidTemperatureC { get; }
    public float[] AirCapacityJPerK { get; }
    public float[] SolidCapacityJPerK { get; }

    public float[] AirEastWPerK { get; }
    public float[] AirNorthWPerK { get; }
    public float[] SolidEastWPerK { get; }
    public float[] SolidNorthWPerK { get; }
    public float[] AirSolidWPerK { get; }
    public float[] OutdoorWPerK { get; }
    public float[] AirSourceW { get; }
    public float[] SolidSourceW { get; }

    public ThermalGrid(int width, int height, float initialAirC = 20f, float initialSolidC = 20f)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Grid dimensions must be positive.");
        if (!float.IsFinite(initialAirC) || !float.IsFinite(initialSolidC))
            throw new ArgumentException("Initial temperatures must be finite.");

        Width = width;
        Height = height;
        CellCount = checked(width * height);
        AirTemperatureC = new float[CellCount];
        SolidTemperatureC = new float[CellCount];
        AirCapacityJPerK = new float[CellCount];
        SolidCapacityJPerK = new float[CellCount];
        AirEastWPerK = new float[CellCount];
        AirNorthWPerK = new float[CellCount];
        SolidEastWPerK = new float[CellCount];
        SolidNorthWPerK = new float[CellCount];
        AirSolidWPerK = new float[CellCount];
        OutdoorWPerK = new float[CellCount];
        AirSourceW = new float[CellCount];
        SolidSourceW = new float[CellCount];
        Array.Fill(AirTemperatureC, initialAirC);
        Array.Fill(SolidTemperatureC, initialSolidC);
        Array.Fill(AirCapacityJPerK, 1f);
    }

    public int Index(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x), "Cell is outside the grid.");
        return y * Width + x;
    }

    /// <summary>Internal thermal energy relative to 0 degrees Celsius, in joules.</summary>
    public double TotalEnergyJ()
    {
        double sum = 0;
        for (int i = 0; i < CellCount; i++)
            sum += (double)AirTemperatureC[i] * AirCapacityJPerK[i]
                 + (double)SolidTemperatureC[i] * SolidCapacityJPerK[i];
        return sum;
    }
}
