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

    public MuqetMapComponent(Map map) : base(map) { }

    public RoomControlSnapshot? Snapshot { get; private set; }
    public int SnapshotGeneration { get; private set; }
    public int LastCaptureTick { get; private set; }

    public void MarkDirty() => dirty = true;

    public override void MapComponentTick()
    {
        int tick = Find.TickManager.TicksGame;
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
            Snapshot = captured;
            SnapshotGeneration++;
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
