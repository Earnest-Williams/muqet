using Muqet.Thermal;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Map lifecycle owner for control topology. Invalidation hooks coalesce all
/// changes into a single next-tick rebuild. A slow safety poll also detects
/// device changes that do not rebuild rooms. No worker touches game objects.
/// </summary>
public sealed class MuqetMapComponent : MapComponent
{
    private const int SafetyPollTicks = 2500;
    private const int FailedRetryTicks = 250;
    private bool dirty = true;
    private int nextPollTick;
    private int retryAfterTick;
    private NativeThermalShadow? shadow;

    public MuqetMapComponent(Map map) : base(map) { }

    public RoomControlSnapshot? Snapshot { get; private set; }
    public int SnapshotGeneration { get; private set; }
    public int LastCaptureTick { get; private set; }
    /// <summary>Captured RimWorld room-cell membership, independent of controller positions.</summary>
    public RoomTemperatureCache RoomTemperatures { get; } = new();
    public int RoomMembershipRefreshes { get; private set; }
    /// <summary>Shadow is independent of the game temperature grid; -1 until first step.</summary>
    public long ShadowGeneration => shadow?.PublishedGeneration ?? -1;
    public double ShadowMeanAbsoluteDeltaC => shadow?.LastRoomTemperatureMeanErrorC ?? double.NaN;
    public string? ShadowFailure => shadow?.LastFailure;

    public void MarkDirty() => dirty = true;

    public override void MapComponentTick()
    {
        int tick = Find.TickManager.TicksGame;
        if (Snapshot is not null)
        {
            shadow ??= new NativeThermalShadow(map);
            shadow.Run(tick, Snapshot);
        }

        if (tick < retryAfterTick || (!dirty && tick < nextPollTick))
            return;
        nextPollTick = tick + SafetyPollTicks;

        try
        {
            RoomControlSnapshot captured = RimWorldRoomCollector.Capture(map);
            LastCaptureTick = tick;
            dirty = false;
            if (Snapshot != null && Snapshot.Fingerprint == captured.Fingerprint)
                return;
            if (Snapshot == null || Snapshot.RoomFingerprint != captured.RoomFingerprint)
            {
                RoomTemperatures.SetTopology(captured.Zones.CellCount, captured.CreateRoomMembership());
                RoomMembershipRefreshes++;
            }
            Snapshot = captured;
            shadow?.TopologyChanged(captured);
            SnapshotGeneration++;
            if (Prefs.DevMode)
                Log.Message($"[Muqet] map topology generation {SnapshotGeneration}: "
                    + $"controllers={captured.Anchors.Count}, zones={captured.Zones.Zones.Count}, "
                    + $"unserved cells={captured.Zones.UnservedCellCount}, "
                    + $"skipped devices={captured.SkippedDeviceCount}");
        }
        catch (Exception e)
        {
            // During map generation or room-region rebuilding, defer a retry
            // rather than poisoning or modifying the colony's thermal state.
            retryAfterTick = tick + FailedRetryTicks;
            dirty = true;
            Log.Warning("[Muqet] Room snapshot deferred: " + e.Message);
        }
    }
}
