namespace Muqet.Thermal;

/// <summary>A game-thread snapshot of one installed electric heater or cooler.</summary>
public readonly record struct HvacDeviceRequest(
    ElectricThermalDevice Device,
    float TargetTemperatureC,
    bool PowerOn,
    bool PortsReady);

/// <summary>One committed step with its electrical and thermal energy accounting.</summary>
public sealed class HvacSimulationStep
{
    internal HvacSimulationStep(ThermalAdvanceResult thermal, long generation,
        ElectricThermalDeviceOutput[] outputs, double electricalEnergyJ)
    {
        Thermal = thermal;
        PublishedGeneration = generation;
        Devices = Array.AsReadOnly(outputs);
        ElectricalEnergyJ = electricalEnergyJ;
    }

    public ThermalAdvanceResult Thermal { get; }
    public long PublishedGeneration { get; }
    public IReadOnlyList<ElectricThermalDeviceOutput> Devices { get; }
    public double ElectricalEnergyJ { get; }
}

/// <summary>
/// Synchronous end-to-end controller loop:
/// published temperatures -> control zones -> thermostats -> device watts
/// -> source frame -> conservative CPU advance -> new published generation.
///
/// This class does not read Verse, manipulate vanilla temperatures, or assume a
/// RimWorld time scale. The host must provide correct grid coefficients, a
/// topology snapshot, and a number of simulated seconds per update.
/// </summary>
public sealed class HvacSimulationCoordinator
{
    private readonly ThermalGrid grid;
    private readonly CpuThermalBackend backend;
    private readonly float[] previousSources;
    private readonly RoomTemperatureCache roomTemperatures;
    private Dictionary<int, ZoneThermostat> thermostats = new();
    private RoomControlSnapshot? topology;
    private long generation = -1;

    public HvacSimulationCoordinator(ThermalGrid grid, RoomTemperatureCache? roomTemperatures = null)
    {
        this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
        backend = new CpuThermalBackend(grid);
        previousSources = new float[grid.CellCount];
        this.roomTemperatures = roomTemperatures ?? new RoomTemperatureCache();
    }

    public long PublishedGeneration => generation;
    public RoomTemperatureCache RoomTemperatures => roomTemperatures;
    public RoomControlSnapshot? Topology => topology;

    /// <summary>
    /// Attach the game's latest read-only Room.Cells snapshot. Geometry changes
    /// invalidate thermostat hysteresis; moving devices does not invalidate
    /// cached room membership if the room fingerprint is unchanged.
    /// </summary>
    public void BindTopology(RoomControlSnapshot snapshot, bool roomCacheAlreadyPrepared = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Zones.Width != grid.Width || snapshot.Zones.Height != grid.Height)
            throw new ArgumentException("Room topology dimensions do not match thermal grid.", nameof(snapshot));
        if (topology?.Fingerprint == snapshot.Fingerprint)
            return;

        if (!roomCacheAlreadyPrepared &&
            (topology == null || topology.RoomFingerprint != snapshot.RoomFingerprint))
        {
            roomTemperatures.SetTopology(grid.CellCount, snapshot.CreateRoomMembership());
        }
        thermostats.Clear();
        topology = snapshot;
    }

    /// <summary>
    /// Advance one complete frame. An exception restores the previous source
    /// array and does not publish generation or thermostat hysteresis state.
    /// All supplied device controller IDs must be present in the topology, and
    /// their indoor faces must agree with the topology's controller anchors.
    /// </summary>
    public HvacSimulationStep Advance(double seconds, float outdoorTemperatureC,
        ReadOnlySpan<HvacDeviceRequest> deviceRequests,
        ReadOnlySpan<float> baselineAirSourceW)
    {
        if (topology is null)
            throw new InvalidOperationException("Bind room topology before advancing.");
        if (baselineAirSourceW.Length != grid.CellCount)
            throw new ArgumentException("Baseline source array must cover the map.", nameof(baselineAirSourceW));
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!float.IsFinite(outdoorTemperatureC))
            throw new ArgumentOutOfRangeException(nameof(outdoorTemperatureC));

        var anchors = new Dictionary<int, int>();
        foreach (ControlAnchor anchor in topology.Anchors)
            anchors.Add(anchor.ControllerId, anchor.CellIndex);

        var nextControllers = new Dictionary<int, ZoneThermostat>();
        var outputs = new ElectricThermalDeviceOutput[deviceRequests.Length];
        double totalElectricWatts = 0;
        for (int k = 0; k < deviceRequests.Length; k++)
        {
            HvacDeviceRequest request = deviceRequests[k];
            ElectricThermalDevice device = request.Device;
            device.Validate(grid.CellCount);
            if (!float.IsFinite(request.TargetTemperatureC)
                || request.TargetTemperatureC < -271.15f || request.TargetTemperatureC > 1999f)
                throw new ArgumentException("Invalid thermostat target.", nameof(deviceRequests));
            if (!anchors.TryGetValue(device.ControllerId, out int anchor)
                || anchor != device.IndoorCell)
                throw new ArgumentException("Device indoor face does not match its control-zone anchor.",
                    nameof(deviceRequests));
            if (nextControllers.ContainsKey(device.ControllerId))
                throw new ArgumentException("Device IDs must be unique.", nameof(deviceRequests));

            ThermostatCapabilities capability = device.Kind == ElectricThermalDeviceKind.Heater
                ? ThermostatCapabilities.Heating : ThermostatCapabilities.Cooling;
            var band = new ThermostatBand(request.TargetTemperatureC - 1f,
                request.TargetTemperatureC + 1f);
            ZoneThermostat thermostat;
            if (thermostats.TryGetValue(device.ControllerId, out ZoneThermostat? current)
                && current.Band == band && current.Capabilities == capability)
                thermostat = current.CopyState();
            else
                thermostat = new ZoneThermostat(device.ControllerId, band, capability);

            // A powered-down or blocked device must not preserve an old
            // hysteresis request that predates its next restart.
            if (!request.PowerOn || !request.PortsReady)
                thermostat.Reset();
            ThermostatDecision decision = thermostat.Evaluate(topology.Zones, grid.AirTemperatureC);
            var output = ElectricThermalDeviceModel.Evaluate(device, decision,
                request.PowerOn, request.PortsReady, grid.AirTemperatureC);
            outputs[k] = output;
            nextControllers.Add(device.ControllerId, thermostat);
            totalElectricWatts += output.ElectricalInputW;
        }

        Array.Copy(grid.AirSourceW, previousSources, grid.CellCount);
        try
        {
            ElectricThermalSourceFrame.Apply(grid, baselineAirSourceW, outputs);
            ThermalAdvanceResult thermal = backend.Advance(seconds, outdoorTemperatureC);
            generation++;
            roomTemperatures.PublishTemperatureGeneration(generation);
            thermostats = nextControllers;
            return new HvacSimulationStep(thermal, generation, outputs, totalElectricWatts * seconds);
        }
        catch
        {
            // Published temperatures are already transactional in the CPU backend.
            // Restore source powers as well, so failed frames are repeatable.
            Array.Copy(previousSources, grid.AirSourceW, grid.CellCount);
            throw;
        }
    }

    /// <summary>Copy only a completed temperature generation; never expose the mutable grid buffer.</summary>
    public void CopyPublishedAirTemperatures(Span<float> destination)
    {
        if (generation < 0)
            throw new InvalidOperationException("The simulation has not published a complete step.");
        backend.CopyAirTemperatures(destination);
    }

    public ZoneTemperatureStatistics ReadRoom(int roomId)
    {
        if (generation < 0)
            throw new InvalidOperationException("No thermal generation has been published.");
        return roomTemperatures.ReadRoom(roomId, grid.AirTemperatureC);
    }
}
