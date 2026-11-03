namespace Muqet.Thermal;

[Flags]
public enum ThermostatCapabilities
{
    Heating = 1,
    Cooling = 2,
    HeatingAndCooling = Heating | Cooling,
}

/// <summary>
/// The desired comfort band for tier 0. Further 25-cell tiers may be allowed
/// progressively wider temperature bounds, without modifying physical temperatures.
/// Hysteresis prevents switching at precisely the same temperature on/off.
/// </summary>
public readonly record struct ThermostatBand(
    float MinimumC,
    float MaximumC,
    float HysteresisC = 1f,
    float RelaxationPerTierC = 1f)
{
    public void Validate()
    {
        if (!float.IsFinite(MinimumC) || !float.IsFinite(MaximumC)
            || MinimumC < -273.15f || MaximumC <= MinimumC)
            throw new ArgumentException("Thermostat requires a finite, increasing comfort band above absolute zero.");
        if (!float.IsFinite(HysteresisC) || HysteresisC < 0 || HysteresisC > MaximumC - MinimumC)
            throw new ArgumentException("Hysteresis must fit inside the comfort band.");
        if (!float.IsFinite(RelaxationPerTierC) || RelaxationPerTierC < 0)
            throw new ArgumentException("Tier relaxation must be finite and nonnegative.");
    }

    public double MinimumForTier(int tier)
    {
        if (tier < 0) throw new ArgumentOutOfRangeException(nameof(tier));
        return (double)MinimumC - tier * RelaxationPerTierC;
    }

    public double MaximumForTier(int tier)
    {
        if (tier < 0) throw new ArgumentOutOfRangeException(nameof(tier));
        return (double)MaximumC + tier * RelaxationPerTierC;
    }
}

/// <summary>
/// A request to equipment, not direct temperature manipulation. The game adapter
/// decides what heating/cooling power a device can actually deliver and accounts
/// for that energy in the thermal kernel.
/// </summary>
public readonly record struct ThermostatDecision(
    int ControllerId,
    bool RequestHeating,
    bool RequestCooling,
    bool OpposingZoneDemands,
    bool HeatingLimitedByCore,
    bool CoolingLimitedByCore,
    int SampledZones,
    double CoreMeanC,
    double CoreColdP10C,
    double CoreHotP90C,
    double MaximumHeatingDeficitC,
    double MaximumCoolingExcessC);

/// <summary>
/// Mutable hysteresis controller operating on an immutable virtual-zone layout.
/// Each Evaluate consumes a SINGLE published air-temperature snapshot. This class
/// does not run the physics solver and is not safe for concurrent mutation.
///
/// The main sensor is each zone's mean. The core's 90th/10th percentile stops
/// a remote cold/hot tier from pushing the nearest 25-cell area outside the
/// chosen comfort band. Zones never impose target temperatures on the grid.
/// </summary>
public sealed class ZoneThermostat
{
    private readonly ThermostatCapabilities capabilities;
    private readonly ThermostatBand band;
    private bool heating;
    private bool cooling;

    public ZoneThermostat(int controllerId, ThermostatBand band,
        ThermostatCapabilities capabilities)
    {
        if (controllerId < 0) throw new ArgumentOutOfRangeException(nameof(controllerId));
        band.Validate();
        if (((int)capabilities & ~(int)ThermostatCapabilities.HeatingAndCooling) != 0
            || capabilities == 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        ControllerId = controllerId;
        this.band = band;
        this.capabilities = capabilities;
    }

    public int ControllerId { get; }
    public ThermostatBand Band => band;
    public ThermostatCapabilities Capabilities => capabilities;
    public bool HeatingActive => heating;
    public bool CoolingActive => cooling;

    /// <summary>Clone hysteresis without mutating a published controller if a frame fails.</summary>
    public ZoneThermostat CopyState()
    {
        var copy = new ZoneThermostat(ControllerId, band, capabilities);
        copy.heating = heating;
        copy.cooling = cooling;
        return copy;
    }

    /// <summary>Reset on a control-group reconfiguration or a deliberate power-off.</summary>
    public void Reset()
    {
        heating = false;
        cooling = false;
    }

    public ThermostatDecision Evaluate(VirtualControlZoneLayout layout,
        ReadOnlySpan<float> airTemperatureC)
    {
        ArgumentNullException.ThrowIfNull(layout);
        // Run snapshot length checks BEFORE reading any temperatures or changing state.
        if (airTemperatureC.Length != layout.CellCount)
            throw new ArgumentException("Air snapshot length does not match map.", nameof(airTemperatureC));

        bool demandHeat = false, demandCool = false;
        double heatDeficit = 0, coolExcess = 0;
        int count = 0;
        ZoneTemperatureStatistics core = default;
        bool foundCore = false;

        foreach (VirtualControlZone zone in layout.Zones)
        {
            if (zone.ControllerId != ControllerId) continue;
            var sample = layout.MeasureZone(zone.Index, airTemperatureC);
            if (zone.Tier == 0)
            {
                if (foundCore)
                    throw new InvalidOperationException("More than one primary zone for controller.");
                foundCore = true;
                core = sample;
            }

            count++;
            double low = band.MinimumForTier(zone.Tier);
            double high = band.MaximumForTier(zone.Tier);
            double mean = sample.MeanC;
            heatDeficit = Math.Max(heatDeficit, low - mean);
            coolExcess = Math.Max(coolExcess, mean - high);
            // A running heater/cooler stops further inside the comfort band.
            // A newly idle device must cross the outer start threshold.
            if (mean < low + (heating ? band.HysteresisC : 0))
                demandHeat = true;
            if (mean > high - (cooling ? band.HysteresisC : 0))
                demandCool = true;
        }

        if (!foundCore)
            throw new InvalidOperationException($"No primary control zone for controller {ControllerId}.");

        bool heatCapable = (capabilities & ThermostatCapabilities.Heating) != 0;
        bool coolCapable = (capabilities & ThermostatCapabilities.Cooling) != 0;

        // Do not let an unheated far corner make the nearest service area overheat;
        // likewise, do not supercool the core while chasing a remote hot corner.
        bool heatGuard = core.HotP90C < band.MaximumC;
        bool coolGuard = core.ColdP10C > band.MinimumC;
        bool wantHeat = heatCapable && demandHeat && heatGuard;
        bool wantCool = coolCapable && demandCool && coolGuard;
        bool opposed = heatCapable && coolCapable && demandHeat && demandCool;

        if (wantHeat && wantCool)
        {
            // When the core is outside its comfort range, favour core recovery.
            // Inside that range, opposing remote demands need more distributed
            // equipment or changed zoning. Never run both on one controller.
            if (core.MeanC < band.MinimumC)
                wantCool = false;
            else if (core.MeanC > band.MaximumC)
                wantHeat = false;
            else
                wantHeat = wantCool = false;
        }

        heating = wantHeat;
        cooling = wantCool;
        return new ThermostatDecision(ControllerId, heating, cooling, opposed,
            heatCapable && demandHeat && !heatGuard,
            coolCapable && demandCool && !coolGuard,
            count, core.MeanC, core.ColdP10C, core.HotP90C,
            heatDeficit, coolExcess);
    }
}
