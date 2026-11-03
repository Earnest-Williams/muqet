namespace Muqet.Thermal;

/// <summary>
/// A game-thread observation of a temperature-control device. BaseCell is the
/// occupied cell; PreferredOutletCell is its facing outlet, or -1 if unknown.
/// Neither field is a solver heat-source location until the host validates it.
/// </summary>
public readonly record struct ControllerPlacement(
    int ControllerId, int BaseCell, int PreferredOutletCell);

/// <summary>
/// Immutable ownership/coverage snapshot. This is a control topology, NOT a
/// replacement for RimWorld rooms or the thermal solver's physical edge grid.
/// </summary>
public sealed class RoomControlSnapshot
{
    private readonly IReadOnlyList<ControlAnchor> anchors;
    private readonly int[] roomIds;
    private readonly IReadOnlyList<int> exposedRoomIds;

    internal RoomControlSnapshot(VirtualControlZoneLayout zones, ControlAnchor[] resolved,
        int skippedDeviceCount, ulong fingerprint, ulong roomFingerprint, int[] roomIdsByCell)
    {
        Zones = zones;
        anchors = Array.AsReadOnly(resolved);
        roomIds = roomIdsByCell;
        exposedRoomIds = Array.AsReadOnly(roomIds);
        SkippedDeviceCount = skippedDeviceCount;
        Fingerprint = fingerprint;
        RoomFingerprint = roomFingerprint;
    }

    public VirtualControlZoneLayout Zones { get; }
    public IReadOnlyList<ControlAnchor> Anchors => anchors;
    public int SkippedDeviceCount { get; }
    public ulong Fingerprint { get; }
    /// <summary>Independent of equipment positions, enabling room-cache reuse on device movement.</summary>
    public ulong RoomFingerprint { get; }
    public IReadOnlyList<int> RoomIdsByCell => exposedRoomIds;

    /// <summary>Build room-cell membership once when RoomFingerprint changes; never per temperature query.</summary>
    public IReadOnlyDictionary<int, int[]> CreateRoomMembership()
    {
        var gathered = new Dictionary<int, List<int>>();
        for (int i = 0; i < roomIds.Length; i++)
        {
            int roomId = roomIds[i];
            if (roomId < 0) continue;
            if (!gathered.TryGetValue(roomId, out List<int>? cells))
            {
                cells = new List<int>();
                gathered.Add(roomId, cells);
            }
            cells.Add(i);
        }
        return gathered.ToDictionary(x => x.Key, x => x.Value.ToArray());
    }
}

/// <summary>
/// Game-independent adapter boundary. The host collects actual Room.Cells and
/// device positions on the game thread, then passes only value snapshots here.
///
/// An outlet in a wall may fall outside a room; choose the preferred facing
/// cell first, then the occupied cell, then cardinal neighbours (N,E,S,W).
/// Candidate collisions are resolved by increasing controller ID. A device
/// with no eligible free room cell is explicitly skipped, not forcibly bound
/// to an unrelated room. No temperatures are modified.
/// </summary>
public static class RoomControlSnapshotBuilder
{
    public static RoomControlSnapshot Build(int width, int height,
        ReadOnlySpan<int> roomIdsByCell, ReadOnlySpan<ControllerPlacement> devices,
        int maxCellsPerZone = VirtualControlZoneBuilder.DefaultMaxCellsPerZone)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        int n = checked(width * height);
        if (roomIdsByCell.Length != n)
            throw new ArgumentException("Room membership must cover the map.", nameof(roomIdsByCell));
        if (maxCellsPerZone < 1 || maxCellsPerZone > VirtualControlZoneBuilder.MaximumZoneCapacity)
            throw new ArgumentOutOfRangeException(nameof(maxCellsPerZone));

        int[] roomIds = roomIdsByCell.ToArray();
        for (int i = 0; i < n; i++)
            if (roomIds[i] < -1)
                throw new ArgumentException("Room IDs must be -1 or nonnegative.", nameof(roomIdsByCell));

        var ordered = devices.ToArray();
        Array.Sort(ordered, static (a, b) => a.ControllerId.CompareTo(b.ControllerId));
        var used = new HashSet<int>();
        var anchors = new List<ControlAnchor>(ordered.Length);
        int skipped = 0;

        // FNV-1a over the full observable topology, not just the zone count.
        // Reordered source lists have the same signature.
        ulong hash = 14695981039346656037UL;
        void Mix(int value) { hash = unchecked((hash ^ (uint)value) * 1099511628211UL); }
        Mix(width);
        Mix(height);
        foreach (int roomId in roomIds) Mix(roomId);
        ulong roomFingerprint = hash;
        Mix(maxCellsPerZone);

        for (int i = 0; i < ordered.Length; i++)
        {
            ControllerPlacement device = ordered[i];
            if (device.ControllerId < 0 || (uint)device.BaseCell >= (uint)n
                || (device.PreferredOutletCell != -1
                    && (uint)device.PreferredOutletCell >= (uint)n))
                throw new ArgumentException("Invalid controller ID or map cell.", nameof(devices));
            if (i > 0 && ordered[i - 1].ControllerId == device.ControllerId)
                throw new ArgumentException("Controller IDs must be unique.", nameof(devices));

            Mix(device.ControllerId);
            Mix(device.BaseCell);
            Mix(device.PreferredOutletCell);

            int resolved = ChooseFreeCell(device, width, height, roomIds, used);
            if (resolved < 0)
            {
                skipped++;
                continue;
            }

            used.Add(resolved);
            anchors.Add(new ControlAnchor(device.ControllerId, roomIds[resolved], resolved));
        }

        ControlAnchor[] resolvedAnchors = anchors.ToArray();
        VirtualControlZoneLayout zones = VirtualControlZoneBuilder.Build(
            width, height, roomIds, resolvedAnchors, maxCellsPerZone);
        return new RoomControlSnapshot(zones, resolvedAnchors, skipped, hash,
            roomFingerprint, roomIds);
    }

    private static int ChooseFreeCell(ControllerPlacement device, int width, int height,
        int[] rooms, HashSet<int> used)
    {
        int origin = device.BaseCell;
        int preferred = device.PreferredOutletCell;
        int x = origin % width, y = origin / width;

        bool Eligible(int index) => index >= 0 && index < rooms.Length
            && rooms[index] >= 0 && !used.Contains(index);

        // Only accept an explicit outlet within one cardinal step of a device.
        // A mistaken far-away outlet could otherwise claim another room.
        if (preferred >= 0)
        {
            int px = preferred % width, py = preferred / width;
            if (Math.Abs(px - x) + Math.Abs(py - y) <= 1 && Eligible(preferred))
                return preferred;
        }

        if (Eligible(origin)) return origin;
        if (y + 1 < height && Eligible(origin + width)) return origin + width;
        if (x + 1 < width && Eligible(origin + 1)) return origin + 1;
        if (y > 0 && Eligible(origin - width)) return origin - width;
        if (x > 0 && Eligible(origin - 1)) return origin - 1;
        return -1;
    }
}
