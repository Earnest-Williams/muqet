namespace Muqet.Thermal;

/// <summary>
/// A frozen, validated copy of a cell's *structural* thermal coefficients. Heat
/// sources and temperatures are deliberately not cached: they can vary each tick.
/// </summary>
internal readonly record struct PreparedThermalCell(
    float AirCapacity, float SolidCapacity,
    float AirEast, float AirNorth, float SolidEast, float SolidNorth,
    float AirSolid, float Outdoor);

/// <summary>
/// Opt-in immutable-until-invalidated coefficient snapshot for CpuThermalBackend.
/// The underlying ThermalGrid retains its original mutable arrays for the
/// reference path. In prepared mode callers MUST invalidate structural edits:
/// unannounced changes are deliberately invisible until marked dirty. Source and
/// temperature edits are always live and do not require invalidation.
///
/// A failed refresh never changes the published snapshot. Dirty changes update
/// only their own captured properties and outgoing-rate calculations for those
/// cells and four neighbours. A segment-max tree maintains the CFL bound.
/// </summary>
public sealed class ThermalCoefficientCache
{
    private readonly ThermalGrid source;
    private readonly PreparedThermalCell[] cells;
    private readonly double[] rateTree;
    private readonly int treeBase;
    private readonly HashSet<int> dirty = new();
    private bool fullDirty = true;

    public ThermalCoefficientCache(ThermalGrid grid)
    {
        source = grid ?? throw new ArgumentNullException(nameof(grid));
        cells = new PreparedThermalCell[grid.CellCount];
        int size = 1;
        while (size < grid.CellCount) size = checked(size * 2);
        treeBase = size;
        rateTree = new double[checked(2 * size)];
    }

    public long TotalCapturedCells { get; private set; }
    public int LastCapturedCells { get; private set; }
    public long Revision { get; private set; }
    public bool HasPendingChanges => fullDirty || dirty.Count != 0;
    public double MaximumOutgoingRate => rateTree[1];

    /// <summary>
    /// Mark a cell whose local capacity, outdoor coupling, air/solid coupling,
    /// or EAST/NORTH edge changed. For edges stored at cell i, mark cell i.
    /// Related neighbour rates are refreshed automatically.
    /// </summary>
    public void InvalidateCell(int index)
    {
        if ((uint)index >= (uint)cells.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (!fullDirty) dirty.Add(index);
    }

    public void InvalidateAll()
    {
        fullDirty = true;
        dirty.Clear();
    }

    internal PreparedThermalCell this[int index] => cells[index];

    /// <summary>
    /// Prepare dirty coefficients. This is intentionally a topology-boundary
    /// operation; it is O(1) when no geometry/material properties have changed.
    /// </summary>
    public int Refresh()
    {
        LastCapturedCells = 0;
        if (fullDirty) return Rebuild();
        if (dirty.Count == 0) return 0;

        // Capture everything before applying any updates: material and adjacent
        // capacities may change together. A bad batch cannot corrupt the cache.
        var pending = new Dictionary<int, PreparedThermalCell>(dirty.Count);
        foreach (int index in dirty)
            pending.Add(index, ReadCell(index));

        PreparedThermalCell At(int index) =>
            pending.TryGetValue(index, out PreparedThermalCell replacement)
                ? replacement : cells[index];

        var affected = new HashSet<int>();
        foreach (int i in dirty)
            AddCardinalNeighbours(i, affected);
        var rates = new Dictionary<int, double>(affected.Count);
        foreach (int index in affected)
            rates.Add(index, ValidateOutgoingRate(index, At));

        foreach (var pair in pending)
            cells[pair.Key] = pair.Value;
        foreach (var pair in rates)
            SetRate(pair.Key, pair.Value);
        LastCapturedCells = dirty.Count;
        TotalCapturedCells += dirty.Count;
        Revision++;
        dirty.Clear();
        return LastCapturedCells;
    }

    private int Rebuild()
    {
        var rebuilt = new PreparedThermalCell[cells.Length];
        for (int i = 0; i < rebuilt.Length; i++)
            rebuilt[i] = ReadCell(i);

        var nextTree = new double[rateTree.Length];
        for (int i = 0; i < rebuilt.Length; i++)
            nextTree[treeBase + i] = ValidateOutgoingRate(i, index => rebuilt[index]);
        for (int i = treeBase - 1; i > 0; i--)
            nextTree[i] = Math.Max(nextTree[2 * i], nextTree[2 * i + 1]);

        // Commit only after the complete batch has passed validation.
        Array.Copy(rebuilt, cells, cells.Length);
        Array.Copy(nextTree, rateTree, nextTree.Length);
        fullDirty = false;
        dirty.Clear();
        LastCapturedCells = cells.Length;
        TotalCapturedCells += cells.Length;
        Revision++;
        return LastCapturedCells;
    }

    private PreparedThermalCell ReadCell(int i)
    {
        float airC = source.AirCapacityJPerK[i];
        float solidC = source.SolidCapacityJPerK[i];
        if (!float.IsFinite(airC) || airC <= 0)
            throw new ArgumentException($"Invalid air capacity at cell {i}.");
        if (!float.IsFinite(solidC) || solidC < 0)
            throw new ArgumentException($"Invalid solid capacity at cell {i}.");

        float ae = Nonnegative(source.AirEastWPerK[i], i, nameof(source.AirEastWPerK));
        float an = Nonnegative(source.AirNorthWPerK[i], i, nameof(source.AirNorthWPerK));
        float se = Nonnegative(source.SolidEastWPerK[i], i, nameof(source.SolidEastWPerK));
        float sn = Nonnegative(source.SolidNorthWPerK[i], i, nameof(source.SolidNorthWPerK));
        float coupling = Nonnegative(source.AirSolidWPerK[i], i, nameof(source.AirSolidWPerK));
        float outdoors = Nonnegative(source.OutdoorWPerK[i], i, nameof(source.OutdoorWPerK));
        if ((i % source.Width == source.Width - 1 && (ae != 0 || se != 0))
            || (i + source.Width >= cells.Length && (an != 0 || sn != 0)))
            throw new ArgumentException($"Thermal edge crosses a map boundary at cell {i}.");

        return new PreparedThermalCell(airC, solidC, ae, an, se, sn, coupling, outdoors);
    }

    private static float Nonnegative(float value, int i, string name)
    {
        if (!float.IsFinite(value) || value < 0)
            throw new ArgumentException($"Invalid {name} at cell {i}.");
        return value;
    }

    private double ValidateOutgoingRate(int i, Func<int, PreparedThermalCell> at)
    {
        PreparedThermalCell c = at(i);
        int x = i % source.Width, width = source.Width;
        double airSum = (double)c.AirSolid + c.Outdoor + c.AirEast + c.AirNorth;
        double solidSum = (double)c.AirSolid + c.SolidEast + c.SolidNorth;
        if (x > 0)
        {
            var west = at(i - 1);
            airSum += west.AirEast;
            solidSum += west.SolidEast;
        }
        if (i >= width)
        {
            var south = at(i - width);
            airSum += south.AirNorth;
            solidSum += south.SolidNorth;
        }
        if (c.SolidCapacity == 0 && solidSum != 0)
            throw new ArgumentException($"Inactive solid cell {i} has an incident solid edge or coupling.");
        if (x + 1 < width && c.SolidEast > 0 && at(i + 1).SolidCapacity <= 0)
            throw new ArgumentException($"Solid edge {i} connects to an inactive cell.");
        if (i + width < cells.Length && c.SolidNorth > 0 && at(i + width).SolidCapacity <= 0)
            throw new ArgumentException($"Solid edge {i} connects to an inactive cell.");

        return Math.Max(airSum / c.AirCapacity,
            c.SolidCapacity > 0 ? solidSum / c.SolidCapacity : 0);
    }

    private void AddCardinalNeighbours(int i, HashSet<int> affected)
    {
        affected.Add(i);
        int x = i % source.Width;
        if (x > 0) affected.Add(i - 1);
        if (x + 1 < source.Width) affected.Add(i + 1);
        if (i >= source.Width) affected.Add(i - source.Width);
        if (i + source.Width < cells.Length) affected.Add(i + source.Width);
    }

    private void SetRate(int index, double rate)
    {
        int position = treeBase + index;
        rateTree[position] = rate;
        while (position > 1)
        {
            position /= 2;
            rateTree[position] = Math.Max(rateTree[2 * position], rateTree[2 * position + 1]);
        }
    }

    /// <summary>
    /// Optional O(map area) diagnostic for tests/integration. Detects missing
    /// invalidations before running a prepared step; not needed on every tick.
    /// </summary>
    public void AssertMatchesHost()
    {
        if (HasPendingChanges)
            throw new InvalidOperationException("Refresh coefficient edits before checking the host.");
        for (int i = 0; i < cells.Length; i++)
            if (cells[i] != ReadCell(i))
                throw new InvalidOperationException($"Unannounced coefficient edit at cell {i}.");
    }
}
