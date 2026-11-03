namespace Muqet.Thermal;

/// <summary>
/// The two vanilla electric HVAC roles. A heater converts electrical input to
/// local heat; a cooler transfers heat from its cold-side cell to its hot side.
/// Neither creates prescribed temperatures or uses vanilla heat "energy" units.
/// </summary>
public enum ElectricThermalDeviceKind { Heater, Cooler }

/// <summary>
/// Physical and gameplay calibration for a vanilla-powered device. Both active
/// and standby electrical power are real input watts in simulated seconds.
/// The COP and temperature-lift limits are provisional calibration parameters,
/// not claims about a specific real-world refrigerant.
/// </summary>
public readonly record struct ElectricThermalDeviceProfile(
    float ActiveElectricalW,
    float StandbyFraction,
    float RatedCoolingCop = 3f,
    float MaximumCoolingLiftK = 90f,
    float HeaterSafetyCutoffC = 120f)
{
    public void Validate()
    {
        if (!float.IsFinite(ActiveElectricalW) || ActiveElectricalW <= 0
            || !float.IsFinite(StandbyFraction) || StandbyFraction < 0 || StandbyFraction > 1
            || !float.IsFinite(RatedCoolingCop) || RatedCoolingCop <= 0 || RatedCoolingCop > 20
            || !float.IsFinite(MaximumCoolingLiftK) || MaximumCoolingLiftK <= 0
            || !float.IsFinite(HeaterSafetyCutoffC) || HeaterSafetyCutoffC <= -273.15f)
            throw new ArgumentException("Invalid electric appliance power, COP, lift or safety profile.");
    }
}

/// <summary>
/// Immutable equipment placement and capability. Heater exhaust index is -1.
/// Cooler ports must be distinct map cells, including when the hot side is outdoors.
/// </summary>
public readonly record struct ElectricThermalDevice(
    int ControllerId,
    ElectricThermalDeviceKind Kind,
    int IndoorCell,
    int ExhaustCell,
    ElectricThermalDeviceProfile Profile)
{
    public void Validate(int cellCount)
    {
        Profile.Validate();
        if (ControllerId < 0 || (uint)IndoorCell >= (uint)cellCount)
            throw new ArgumentOutOfRangeException(nameof(IndoorCell), "Invalid controller or indoor cell.");
        if (Kind == ElectricThermalDeviceKind.Cooler)
        {
            if ((uint)ExhaustCell >= (uint)cellCount || ExhaustCell == IndoorCell)
                throw new ArgumentException("Cooler needs distinct, in-bounds cold and hot air faces.");
        }
        else if (Kind != ElectricThermalDeviceKind.Heater || ExhaustCell != -1)
            throw new ArgumentException("Heaters have no separate exhaust; use -1.");
    }
}

/// <summary>
/// Equipment's thermal powers. All values are W; the host multiplies by the
/// solver's simulated seconds for its energy ledger. Net thermal injection
/// equals electrical input, including when a powered device is idle.
/// </summary>
public readonly record struct ElectricThermalDeviceOutput(
    int ControllerId,
    int IndoorCell,
    int ExhaustCell,
    float ElectricalInputW,
    float IndoorHeatW,
    float ExhaustHeatW,
    bool ActivelyHeatingOrCooling,
    float CoolingCop)
{
    public double TotalThermalW => (double)IndoorHeatW + ExhaustHeatW;
    public double RemovedFromColdSideW => Math.Max(0, -(double)IndoorHeatW);
}

/// <summary>
/// Converts thermostat requests into physically accounted power on a single
/// published temperature snapshot. PowerOn/portsReady must come from the game
/// thread, including flick, breakdown, network and obstructed-face conditions.
/// No Verse, Harmony or game-time conversion occurs here.
/// </summary>
public static class ElectricThermalDeviceModel
{
    public static ElectricThermalDeviceOutput Evaluate(
        ElectricThermalDevice device,
        ThermostatDecision control,
        bool powerOn,
        bool portsReady,
        ReadOnlySpan<float> airTemperatureC)
    {
        device.Validate(airTemperatureC.Length);
        if (control.ControllerId != device.ControllerId)
            throw new ArgumentException("Thermostat request belongs to another controller.", nameof(control));

        float indoor = airTemperatureC[device.IndoorCell];
        float exhaust = device.Kind == ElectricThermalDeviceKind.Cooler
            ? airTemperatureC[device.ExhaustCell] : indoor;
        if (!float.IsFinite(indoor) || !float.IsFinite(exhaust)
            || indoor < -273.15f || exhaust < -273.15f)
            throw new ArgumentException("Device faces must have physical air temperatures.", nameof(airTemperatureC));

        if (!powerOn)
            return new(device.ControllerId, device.IndoorCell, device.ExhaustCell,
                0, 0, 0, false, 0);

        ElectricThermalDeviceProfile profile = device.Profile;
        float standby = profile.ActiveElectricalW * profile.StandbyFraction;
        if (device.Kind == ElectricThermalDeviceKind.Heater)
        {
            bool active = portsReady && control.RequestHeating
                && indoor < profile.HeaterSafetyCutoffC;
            float input = active ? profile.ActiveElectricalW : standby;
            // All resistance/standby electrical energy becomes heat. There is
            // no created heat and no arbitrary change of room mean temperature.
            return new(device.ControllerId, device.IndoorCell, -1,
                input, input, 0, active, 0);
        }

        // The equipment's heat sink must be warmer to require positive lift;
        // a cooler with a naturally colder exterior has no extra COP penalty.
        double lift = Math.Max(0, (double)exhaust - indoor);
        double efficiency = Math.Max(0, 1 - lift / profile.MaximumCoolingLiftK);
        float cop = (float)(profile.RatedCoolingCop * efficiency);
        bool run = portsReady && control.RequestCooling && cop > 0;
        float draw = run ? profile.ActiveElectricalW : standby;
        // Cold extraction is a finite heat transfer, not negative energy
        // created from nowhere. The electrical work is rejected on the hot
        // face, even when that face is outdoors.
        float extracted = run ? (float)(draw * (double)cop) : 0;
        float rejected = extracted + draw;
        if (!float.IsFinite(rejected))
            throw new InvalidOperationException("Cooler energy output exceeds representable source power.");
        return new(device.ControllerId, device.IndoorCell, device.ExhaustCell,
            draw, -extracted, rejected, run, run ? cop : 0);
    }
}

/// <summary>
/// Rebuilds one grid's air-source frame from a caller-owned baseline and the
/// electric-device powers. This deliberately REPLACES the previous device frame
/// rather than accumulating outputs every tick; it also preserves other heat
/// sources supplied in the baseline (geothermal, fires, etc).
///
/// Caller must not mutate the baseline or grid concurrently, and baseline must
/// not alias grid.AirSourceW. A staging array makes invalid frames atomic.
/// </summary>
public static class ElectricThermalSourceFrame
{
    public static void Apply(ThermalGrid grid, ReadOnlySpan<float> baselineAirSourceW,
        ReadOnlySpan<ElectricThermalDeviceOutput> devices)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (baselineAirSourceW.Length != grid.CellCount)
            throw new ArgumentException("Baseline source array must cover the map.", nameof(baselineAirSourceW));
        if (baselineAirSourceW.Overlaps(grid.AirSourceW))
            throw new ArgumentException("The baseline must not alias the destination grid array.", nameof(baselineAirSourceW));

        float[] next = baselineAirSourceW.ToArray();
        for (int i = 0; i < next.Length; i++)
            if (!float.IsFinite(next[i]))
                throw new ArgumentException($"Invalid baseline power at cell {i}.", nameof(baselineAirSourceW));

        var assigned = new HashSet<int>();
        foreach (ElectricThermalDeviceOutput flux in devices)
        {
            if (!assigned.Add(flux.ControllerId) || flux.ControllerId < 0
                || (uint)flux.IndoorCell >= (uint)next.Length
                || (flux.ExhaustCell != -1
                    && ((uint)flux.ExhaustCell >= (uint)next.Length
                        || flux.ExhaustCell == flux.IndoorCell)))
                throw new ArgumentException("Invalid or duplicate device flux.", nameof(devices));
            if (!float.IsFinite(flux.ElectricalInputW) || flux.ElectricalInputW < 0
                || !float.IsFinite(flux.IndoorHeatW) || !float.IsFinite(flux.ExhaustHeatW)
                || (flux.ExhaustCell == -1 && flux.ExhaustHeatW != 0))
                throw new ArgumentException("Nonfinite or inconsistent device power.", nameof(devices));
            if (Math.Abs(flux.TotalThermalW - flux.ElectricalInputW)
                > 0.01 * Math.Max(1d, flux.ElectricalInputW))
                throw new ArgumentException("Device source energy fails electrical balance.", nameof(devices));

            Add(flux.IndoorCell, flux.IndoorHeatW);
            if (flux.ExhaustCell >= 0) Add(flux.ExhaustCell, flux.ExhaustHeatW);
        }
        Array.Copy(next, grid.AirSourceW, next.Length);

        void Add(int cell, float watts)
        {
            double result = (double)next[cell] + watts;
            if (!double.IsFinite(result) || result < -float.MaxValue || result > float.MaxValue)
                throw new ArgumentException("Resulting air-source power is not representable.", nameof(devices));
            next[cell] = (float)result;
        }
    }
}
