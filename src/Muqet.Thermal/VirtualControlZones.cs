namespace Muqet.Thermal;

/// <summary>
/// A controller's sensor or outlet position inside an existing game room.
/// The RimWorld adapter must map impassable equipment to a valid adjacent air cell.
/// </summary>
public readonly record struct ControlAnchor(int ControllerId, int RoomId, int CellIndex);

/// <summary>Air-temperature measurements; degrees Celsius, not thermodynamic energy.</summary>
public readonly record struct ZoneTemperatureStatistics(
    double MinimumC, double ColdP10C, double MeanC, double HotP90C, double MaximumC);

/// <summary>
/// A contiguous, controller-owned measurement area. This is NOT a physics boundary:
/// air and solid heat transfer continue normally across control-zone edges.
/// </summary>
public sealed class VirtualControlZone
{
    private readonly int[] cells;
    private readonly IReadOnlyList<int> exposedCells;

    internal VirtualControlZone(int index, int controllerId, int roomId, int tier,
        int[] cellIndices, double envelopeConductanceWPerK)
    {
        Index = index;
        ControllerId = controllerId;
        RoomId = roomId;
        Tier = tier;
        cells = cellIndices;
        exposedCells = Array.AsReadOnly(cells);
        EnvelopeConductanceWPerK = envelopeConductanceWPerK;
    }

    public int Index { get; }
    public int ControllerId { get; }
    public int RoomId { get; }
    /// <summary>0 is the closest group of up to 25 cells; increasing tiers extend outward.</summary>
    public int Tier { get; }
    public IReadOnlyList<int> Cells => exposedCells;
    /// <summary>
    /// Sum of externally supplied envelope-to-environment heat-loss conductances,
    /// in W/K. This is a diagnostic, NOT a second heat-loss term for the solver.
    /// </summary>
    public double EnvelopeConductanceWPerK { get; }
    internal ReadOnlySpan<int> RawCells => cells;
}

/// <summary>
/// Immutable virtual control-zone snapshot, built after a room/topology change.
/// Cell indices are row-major; cells outside served areas map to -1.
/// </summary>
public sealed class VirtualControlZoneLayout
{
    private readonly int[] zoneAt;
    private readonly int[] controllerAt;
    private readonly VirtualControlZone[] zones;
    private readonly IReadOnlyList<VirtualControlZone> exposedZones;

    internal VirtualControlZoneLayout(int width, int height, int[] zoneAt,
        int[] controllerAt, VirtualControlZone[] zones, int unservedCellCount)
    {
        Width = width;
        Height = height;
        this.zoneAt = zoneAt;
        this.controllerAt = controllerAt;
        this.zones = zones;
        exposedZones = Array.AsReadOnly(zones);
        UnservedCellCount = unservedCellCount;
    }

    public int Width { get; }
    public int Height { get; }
    public int CellCount => checked(Width * Height);
    public int UnservedCellCount { get; }
    public IReadOnlyList<VirtualControlZone> Zones => exposedZones;

    public int ZoneIndexAt(int cellIndex) => zoneAt[CheckedIndex(cellIndex)];
    public int ControllerIdAt(int cellIndex) => controllerAt[CheckedIndex(cellIndex)];

    private int CheckedIndex(int index)
    {
        if ((uint)index >= (uint)CellCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return index;
    }

    /// <summary>
    /// Returns observed temperatures for one zone. The mean is an air-temperature
    /// mean (not a heat-capacity-weighted energy mean). Percentiles are interpolated.
    /// The small bounded sort is for thermostat sampling, never the thermal solver.
    /// </summary>
    public ZoneTemperatureStatistics MeasureZone(int zoneIndex, ReadOnlySpan<float> airTemperatureC)
    {
        if ((uint)zoneIndex >= (uint)zones.Length)
            throw new ArgumentOutOfRangeException(nameof(zoneIndex));
        if (airTemperatureC.Length != CellCount)
            throw new ArgumentException("One air-temperature reading is required for every map cell.",
                nameof(airTemperatureC));
        ReadOnlySpan<int> cells = zones[zoneIndex].RawCells;
        Span<float> values = stackalloc float[cells.Length]; // zone capacity is capped at 256
        double sum = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            float value = airTemperatureC[cells[i]];
            if (!float.IsFinite(value))
                throw new ArgumentException("Zone contains a nonfinite temperature.", nameof(airTemperatureC));
            values[i] = value;
            sum += value;
        }
        values.Sort();
        return new ZoneTemperatureStatistics(values[0],
            Percentile(values, 0.10), sum / values.Length,
            Percentile(values, 0.90), values[^1]);
    }

    private static double Percentile(ReadOnlySpan<float> sorted, double fraction)
    {
        double position = fraction * (sorted.Length - 1);
        int low = (int)position;
        int high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }
}

/// <summary>
/// Builds bounded, connected thermal-control zones without modifying RimWorld rooms
/// or the solver grid. This runs on the host's topology/snapshot thread, not per tick.
///
/// roomIdsByCell: -1 for outside/ignored cells; otherwise the game's room ID.
/// Permeability: optional shared east/north air-passage factors in [0,1]; 0 blocks
/// traversal, 1 is an unobstructed connection. They control control-zone reach,
/// not physical conductance. With omitted arrays every same-room cardinal edge opens.
/// envelopeLossWPerK: optional per-cell boundary heat-loss conductance calculated by
/// the host from walls, roofs and other insulation (diagnostics only).
/// </summary>
public static class VirtualControlZoneBuilder
{
    public const int DefaultMaxCellsPerZone = 25;
    public const int MaximumZoneCapacity = 256;

    public static VirtualControlZoneLayout Build(int width, int height,
        ReadOnlySpan<int> roomIdsByCell,
        ReadOnlySpan<ControlAnchor> controllers,
        int maxCellsPerZone = DefaultMaxCellsPerZone,
        ReadOnlySpan<float> eastPermeability = default,
        ReadOnlySpan<float> northPermeability = default,
        ReadOnlySpan<float> envelopeLossWPerK = default)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        int n = checked(width * height);
        if (roomIdsByCell.Length != n)
            throw new ArgumentException("Room membership length must match map dimensions.", nameof(roomIdsByCell));
        if (maxCellsPerZone is < 1 or > MaximumZoneCapacity)
            throw new ArgumentOutOfRangeException(nameof(maxCellsPerZone));
        if ((!eastPermeability.IsEmpty && eastPermeability.Length != n)
            || (!northPermeability.IsEmpty && northPermeability.Length != n)
            || (!envelopeLossWPerK.IsEmpty && envelopeLossWPerK.Length != n))
            throw new ArgumentException("Every supplied per-cell field must match map dimensions.");

        var rooms = roomIdsByCell.ToArray();
        float[]? east = eastPermeability.IsEmpty ? null : eastPermeability.ToArray();
        float[]? north = northPermeability.IsEmpty ? null : northPermeability.ToArray();
        float[]? heatLoss = envelopeLossWPerK.IsEmpty ? null : envelopeLossWPerK.ToArray();
        for (int i = 0; i < n; i++)
        {
            if (rooms[i] < -1)
                throw new ArgumentException("Room IDs must be nonnegative, or -1 for unassigned.", nameof(roomIdsByCell));
            if (east is not null)
            {
                ValidatePermeability(east[i], nameof(eastPermeability));
                if (i % width == width - 1 && east[i] != 0)
                    throw new ArgumentException("East passage crosses a map row.", nameof(eastPermeability));
            }
            if (north is not null)
            {
                ValidatePermeability(north[i], nameof(northPermeability));
                if (i + width >= n && north[i] != 0)
                    throw new ArgumentException("North passage crosses the map boundary.", nameof(northPermeability));
            }
            if (heatLoss is not null && (!float.IsFinite(heatLoss[i]) || heatLoss[i] < 0))
                throw new ArgumentException("Envelope conductance must be finite and nonnegative.",
                    nameof(envelopeLossWPerK));
        }

        var anchors = controllers.ToArray();
        Array.Sort(anchors, static (a, b) => a.ControllerId.CompareTo(b.ControllerId));
        var anchorCells = new HashSet<int>();
        for (int i = 0; i < anchors.Length; i++)
        {
            ControlAnchor a = anchors[i];
            if (a.ControllerId < 0 || a.RoomId < 0 || (uint)a.CellIndex >= (uint)n
                || rooms[a.CellIndex] != a.RoomId)
                throw new ArgumentException("Controller must have a nonnegative ID and anchor inside its room.",
                    nameof(controllers));
            if (i > 0 && anchors[i - 1].ControllerId == a.ControllerId)
                throw new ArgumentException("Controller IDs must be unique.", nameof(controllers));
            if (!anchorCells.Add(a.CellIndex))
                throw new ArgumentException("Controllers cannot use the same anchor cell; group them upstream.",
                    nameof(controllers));
        }

        var ownerAt = new int[n];
        Array.Fill(ownerAt, -1);
        var distance = new double[n];
        Array.Fill(distance, double.PositiveInfinity);
        var frontier = new PriorityQueue<int, (double Distance, int ControllerId, int CellIndex)>();
        foreach (ControlAnchor anchor in anchors)
        {
            ownerAt[anchor.CellIndex] = anchor.ControllerId;
            distance[anchor.CellIndex] = 0;
            frontier.Enqueue(anchor.CellIndex, (0, anchor.ControllerId, anchor.CellIndex));
        }

        // Multi-source Dijkstra: shortest traversable path wins. Equal distances
        // resolve to the smaller controller ID, independent of input ordering.
        while (frontier.TryDequeue(out int cell, out var priority))
        {
            if (priority.Distance != distance[cell] || priority.ControllerId != ownerAt[cell])
                continue;
            int x = cell % width;
            if (x > 0) Relax(cell, cell - 1, east is null ? 1f : east[cell - 1],
                rooms, distance, ownerAt, frontier);
            if (x + 1 < width) Relax(cell, cell + 1, east is null ? 1f : east[cell],
                rooms, distance, ownerAt, frontier);
            if (cell >= width) Relax(cell, cell - width, north is null ? 1f : north[cell - width],
                rooms, distance, ownerAt, frontier);
            if (cell + width < n) Relax(cell, cell + width, north is null ? 1f : north[cell],
                rooms, distance, ownerAt, frontier);
        }

        var owned = new Dictionary<int, List<int>>(anchors.Length);
        foreach (ControlAnchor anchor in anchors)
            owned.Add(anchor.ControllerId, new List<int>());
        int unserved = 0;
        for (int i = 0; i < n; i++)
        {
            if (ownerAt[i] >= 0)
                owned[ownerAt[i]].Add(i);
            else if (rooms[i] >= 0)
                unserved++;
        }

        var zoneAt = new int[n];
        Array.Fill(zoneAt, -1);
        var zones = new List<VirtualControlZone>();
        var stamps = new int[n];
        int generation = 0;
        foreach (ControlAnchor anchor in anchors)
        {
            List<int> order = owned[anchor.ControllerId];
            order.Sort((a, b) =>
            {
                int comparison = distance[a].CompareTo(distance[b]);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });

            int firstUnassigned = 0;
            int tier = 0;
            while (firstUnassigned < order.Count)
            {
                while (firstUnassigned < order.Count && zoneAt[order[firstUnassigned]] != -1)
                    firstUnassigned++;
                if (firstUnassigned == order.Count)
                    break;

                int zoneIndex = zones.Count;
                generation++;
                var group = new List<int>(maxCellsPerZone);
                var candidates = new PriorityQueue<int, (double Distance, int CellIndex)>();
                int seed = order[firstUnassigned];
                stamps[seed] = generation;
                candidates.Enqueue(seed, (distance[seed], seed));

                // Each group grows from a single seed along traversable edges,
                // so even a long, irregular room produces individually connected zones.
                while (group.Count < maxCellsPerZone && candidates.TryDequeue(out int cell, out _))
                {
                    if (zoneAt[cell] != -1) continue;
                    zoneAt[cell] = zoneIndex;
                    group.Add(cell);
                    int x = cell % width;
                    if (x > 0) Enqueue(cell - 1, east is null ? 1f : east[cell - 1],
                        anchor.ControllerId, ownerAt, zoneAt, stamps, generation, distance, candidates);
                    if (x + 1 < width) Enqueue(cell + 1, east is null ? 1f : east[cell],
                        anchor.ControllerId, ownerAt, zoneAt, stamps, generation, distance, candidates);
                    if (cell >= width) Enqueue(cell - width, north is null ? 1f : north[cell - width],
                        anchor.ControllerId, ownerAt, zoneAt, stamps, generation, distance, candidates);
                    if (cell + width < n) Enqueue(cell + width, north is null ? 1f : north[cell],
                        anchor.ControllerId, ownerAt, zoneAt, stamps, generation, distance, candidates);
                }

                double totalLoss = 0;
                if (heatLoss is not null)
                    foreach (int i in group)
                        totalLoss += heatLoss[i];

                zones.Add(new VirtualControlZone(zoneIndex, anchor.ControllerId, anchor.RoomId,
                    tier++, group.ToArray(), totalLoss));
            }
        }

        return new VirtualControlZoneLayout(width, height, zoneAt,
            ownerAt, zones.ToArray(), unserved);
    }

    private static void ValidatePermeability(float value, string field)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentException("Air passage permeability must be between 0 and 1.", field);
    }

    private static void Relax(int from, int to, float passage, int[] rooms,
        double[] distance, int[] owner,
        PriorityQueue<int, (double Distance, int ControllerId, int CellIndex)> frontier)
    {
        if (passage <= 0 || rooms[from] != rooms[to]) return;
        double proposed = distance[from] + 1d / passage;
        int controller = owner[from];
        if (proposed < distance[to] || (proposed == distance[to] && controller < owner[to]))
        {
            distance[to] = proposed;
            owner[to] = controller;
            frontier.Enqueue(to, (proposed, controller, to));
        }
    }

    private static void Enqueue(int to, float passage, int controller,
        int[] ownerAt, int[] zoneAt, int[] stamps, int generation,
        double[] distance, PriorityQueue<int, (double Distance, int CellIndex)> frontier)
    {
        if (passage <= 0 || ownerAt[to] != controller || zoneAt[to] != -1
            || stamps[to] == generation)
            return;
        stamps[to] = generation;
        frontier.Enqueue(to, (distance[to], to));
    }
}
