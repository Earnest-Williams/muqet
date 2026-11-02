namespace Muqet.Thermal;

/// <summary>
/// Per-map, per-published-generation room statistics. Never substitutes cached
/// temperatures for solver state. Room-cell membership is cached independently
/// of temperature generations and replaced transactionally on topology change.
/// </summary>
public sealed class RoomTemperatureCache
{
    private sealed class Membership
    {
        public required int[] Cells;
        public long SampleGeneration = -1;
        public ZoneTemperatureStatistics Sample;
    }

    private readonly Dictionary<int, Membership> rooms = new();
    private long topologyGeneration;
    private long temperatureGeneration = -1;
    private int mapCellCount;

    public long TopologyGeneration => topologyGeneration;
    public long TemperatureGeneration => temperatureGeneration;
    public int RoomCount => rooms.Count;
    public long ComputedSamples { get; private set; }
    public long CacheHits { get; private set; }

    public void SetTopology(int mapCells, IEnumerable<KeyValuePair<int, int[]>> membership)
    {
        if (mapCells <= 0) throw new ArgumentOutOfRangeException(nameof(mapCells));
        ArgumentNullException.ThrowIfNull(membership);
        var staged = new Dictionary<int, Membership>();
        var claimed = new HashSet<int>();
        foreach (var pair in membership)
        {
            if (pair.Key < 0 || pair.Value is null || pair.Value.Length == 0)
                throw new ArgumentException("Every room requires an ID and nonempty cells.", nameof(membership));
            int[] cells = (int[])pair.Value.Clone();
            foreach (int cell in cells)
                if ((uint)cell >= (uint)mapCells || !claimed.Add(cell))
                    throw new ArgumentException("Duplicate/out-of-bounds room cell.", nameof(membership));
            staged.Add(pair.Key, new Membership { Cells = cells });
        }
        rooms.Clear();
        foreach (var pair in staged) rooms.Add(pair.Key, pair.Value);
        mapCellCount = mapCells;
        topologyGeneration++;
        temperatureGeneration = -1;
    }

    public void PublishTemperatureGeneration(long generation)
    {
        if (generation < 0 || (temperatureGeneration >= 0 && generation <= temperatureGeneration))
            throw new ArgumentOutOfRangeException(nameof(generation), "Temperature generation must increase.");
        temperatureGeneration = generation;
    }

    public ZoneTemperatureStatistics ReadRoom(int roomId, ReadOnlySpan<float> temperatures)
    {
        if (temperatureGeneration < 0)
            throw new InvalidOperationException("Publish a temperature generation before sampling.");
        if (temperatures.Length != mapCellCount)
            throw new ArgumentException("Snapshot length mismatch.", nameof(temperatures));
        if (!rooms.TryGetValue(roomId, out Membership? membership))
            throw new KeyNotFoundException($"Unrecognised room {roomId}.");

        if (membership.SampleGeneration == temperatureGeneration)
        {
            CacheHits++;
            return membership.Sample;
        }
        int[] cells = membership.Cells;
        float[] readings = new float[cells.Length];
        double sum = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            float t = temperatures[cells[i]];
            if (!float.IsFinite(t))
                throw new ArgumentException("Room has nonfinite temperature.", nameof(temperatures));
            sum += t;
            readings[i] = t;
        }
        Array.Sort(readings);
        static double Percentile(float[] sorted, double p)
        {
            double at = (sorted.Length - 1) * p;
            int a = (int)at, b = Math.Min(a + 1, sorted.Length - 1);
            return sorted[a] + (sorted[b] - sorted[a]) * (at - a);
        }
        var result = new ZoneTemperatureStatistics(readings[0], Percentile(readings, .1),
            sum / cells.Length, Percentile(readings, .9), readings[^1]);
        membership.Sample = result;
        membership.SampleGeneration = temperatureGeneration;
        ComputedSamples++;
        return result;
    }
}
