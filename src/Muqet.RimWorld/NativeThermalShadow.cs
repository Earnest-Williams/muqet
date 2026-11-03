using Muqet.Thermal;
using RimWorld;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Non-authoritative map-local simulation. Vanilla continues to own all
/// temperature queries, devices, saves, electricity and heat calls.
/// Shadow data is never written back to RimWorld.
///
/// Explicit pilot time mapping: 60 game ticks = one model second.
/// This is an adjustable calibration, not a claim of realistic game-world
/// time. No other sources (fires, vents, geothermal, ambient pawns) are yet
/// represented; comparative results must NOT be used as gameplay parity.
/// </summary>
internal sealed class NativeThermalShadow
{
    internal const int IntervalTicks = 250;
    internal const double SecondsPerGameTick = 1d / 60d;

    private readonly Map map;
    private ThermalGrid? grid;
    private HvacSimulationCoordinator? coordinator;
    private ulong roomFingerprint;
    private ulong geometryFingerprint;
    private readonly Dictionary<int, ElectricThermalDeviceOutput> lastOutputs = new();
    private int previousTick = -1;
    private int lastLoggedTick = -1;
    private bool faulted;

    internal NativeThermalShadow(Map map) => this.map = map;

    internal long PublishedGeneration => coordinator?.PublishedGeneration ?? -1;
    internal int ActiveDeviceCount => lastOutputs.Values.Count(o => o.ActivelyHeatingOrCooling);
    internal double LastRoomTemperatureMeanErrorC { get; private set; } = double.NaN;
    internal string? LastFailure { get; private set; }

    internal void Run(int tick, RoomControlSnapshot snapshot)
    {
        if (faulted || (previousTick >= 0 && tick - previousTick < IntervalTicks))
            return;

        try
        {
            // Doors/roof changes need not change Room.Cells. Classify the
            // current physical layout on each shadow step and reseed safely
            // when its geometry signature changes.
            var observedCells = new ShadowCell[snapshot.Zones.CellCount];
            var observedTemperatures = new float[snapshot.Zones.CellCount];
            RimWorldShadowCollector.Capture(map, observedCells, observedTemperatures);
            ulong observedGeometry = Fingerprint(observedCells);
            if (coordinator is null || roomFingerprint != snapshot.RoomFingerprint
                || observedGeometry != geometryFingerprint)
                Initialize(snapshot, tick, observedCells, observedTemperatures, observedGeometry);

            if (grid is null || coordinator is null)
                return;

            // Build a single device input frame. Unsupported/unaligned devices
            // are explicitly skipped, not applied at a guessed cell.
            var observations = VanillaElectricDeviceCapture.Capture(map);
            var anchors = snapshot.Anchors.ToDictionary(x => x.ControllerId, x => x.CellIndex);
            var devices = new List<HvacDeviceRequest>();
            foreach (var observed in observations)
            {
                if (anchors.TryGetValue(observed.Device.ControllerId, out int cell)
                    && cell == observed.Device.IndoorCell)
                    devices.Add(new HvacDeviceRequest(observed.Device,
                        observed.VanillaTargetC, observed.PowerOn, observed.PortsReady));
            }

            // The original grid contains all cells, including outdoor exhaust.
            // The zero baseline is deliberate: only electric HVAC sources are
            // currently represented; no double accounting with vanilla heat.
            int elapsed = Math.Max(1, tick - previousTick);
            double seconds = elapsed * SecondsPerGameTick;
            float outdoors = map.mapTemperature.OutdoorTemp;
            var step = coordinator.Advance(seconds, outdoors,
                devices.ToArray(), new float[grid.CellCount]);
            previousTick = tick;
            lastOutputs.Clear();
            foreach (var output in step.Devices)
                lastOutputs.Add(output.ControllerId, output);

            // Only compare against a snapshot of actual vanilla temperatures.
            // Diagnostics are a divergence indicator, not a validation of
            // physical material calibration or absence of missing sources.
            var current = observedTemperatures;
            double sum = 0;
            int sampled = 0;
            foreach (var room in snapshot.CreateRoomMembership())
                foreach (int i in room.Value)
                {
                    sum += Math.Abs(grid.AirTemperatureC[i] - current[i]);
                    sampled++;
                }
            LastRoomTemperatureMeanErrorC = sampled > 0 ? sum / sampled : double.NaN;

            if (Prefs.DevMode && (lastLoggedTick < 0 || tick - lastLoggedTick >= 2500))
            {
                lastLoggedTick = tick;
                Log.Message("[Muqet/shadow] generation=" + step.PublishedGeneration
                    + " sampledCells=" + sampled + " activeHVAC=" + ActiveDeviceCount
                    + " meanAbsDeltaC=" + LastRoomTemperatureMeanErrorC.ToString("F2")
                    + " energyResidualJ=" + step.Thermal.EnergyResidualJ.ToString("F3"));
            }
        }
        catch (Exception e)
        {
            // A fault never suppresses or redirects vanilla heat. Do not retry
            // automatically into an unknown partially captured topology.
            faulted = true;
            LastFailure = e.Message;
            Log.Warning("[Muqet/shadow] disabled for this map after error: " + e);
        }
    }

    private static ulong Fingerprint(ReadOnlySpan<ShadowCell> cells)
    {
        ulong hash = 14695981039346656037UL;
        foreach (ShadowCell cell in cells)
        {
            hash = unchecked((hash ^ (byte)cell.Kind) * 1099511628211UL);
            hash = unchecked((hash ^ (byte)cell.Roof) * 1099511628211UL);
        }
        return hash;
    }

    private void Initialize(RoomControlSnapshot snapshot, int tick,
        ShadowCell[] cells, float[] vanilla, ulong observedGeometry)
    {
        grid = ShadowThermalGridBuilder.Build(snapshot.Zones.Width, snapshot.Zones.Height,
            cells, vanilla, ShadowThermalCalibration.Provisional);
        coordinator = new HvacSimulationCoordinator(grid);
        coordinator.BindTopology(snapshot);
        roomFingerprint = snapshot.RoomFingerprint;
        geometryFingerprint = observedGeometry;
        previousTick = tick;
        lastOutputs.Clear();
        LastRoomTemperatureMeanErrorC = double.NaN;
    }

    internal void TopologyChanged(RoomControlSnapshot snapshot)
    {
        if (coordinator is null || faulted) return;
        if (snapshot.RoomFingerprint != roomFingerprint)
        {
            // Temperature and capacity may have changed structurally; preserve
            // vanilla safety by reseeding the shadow on its next run.
            coordinator = null;
            grid = null;
            previousTick = -1;
            return;
        }
        coordinator.BindTopology(snapshot);
    }
}
