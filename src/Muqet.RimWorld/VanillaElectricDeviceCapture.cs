using Muqet.Thermal;
using RimWorld;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Values captured on RimWorld's game thread. This is an adapter INPUT, not
/// evidence that Muqet has become the authoritative temperature simulation.
/// </summary>
public readonly record struct VanillaElectricDeviceObservation(
    ElectricThermalDevice Device,
    float VanillaTargetC,
    bool PowerOn,
    bool PortsReady);

/// <summary>
/// Identifies vanilla powered heaters/coolers and resolves their physical ports.
/// Cooler orientation follows the vanilla Building_Cooler convention: the
/// South.RotatedBy(rot) face is cold; North.RotatedBy(rot) is exhaust.
/// Both faces are checked against the actual map, not guessed from Room.Cells.
///
/// This class only observes devices. Until Muqet owns temperature publishing,
/// vanilla Building_Heater.TickRare/Building_Cooler.TickRare must remain active.
/// </summary>
public static class VanillaElectricDeviceCapture
{
    public static bool TryCapture(Building building, out VanillaElectricDeviceObservation observation)
    {
        observation = default;
        if (building is not Building_Heater && building is not Building_Cooler)
            return false;

        Map? map = building.Map;
        if (map is null || !building.Spawned)
            return false;

        CompTempControl? control = building.GetComp<CompTempControl>();
        CompPowerTrader? power = building.GetComp<CompPowerTrader>();
        if (control is null || power is null)
            return false;

        float activeW = power.Props.basePowerConsumption;
        float idleFactor = control.Props.lowPowerConsumptionFactor;
        float target = control.targetTemperature;
        if (!float.IsFinite(activeW) || activeW <= 0
            || !float.IsFinite(idleFactor) || idleFactor < 0 || idleFactor > 1
            || !float.IsFinite(target))
            return false;

        IntVec3 cold = building.Position;
        IntVec3 hot = IntVec3.Invalid;
        ElectricThermalDeviceKind kind = ElectricThermalDeviceKind.Heater;
        if (building is Building_Cooler)
        {
            kind = ElectricThermalDeviceKind.Cooler;
            cold = building.Position + IntVec3.South.RotatedBy(building.Rotation);
            hot = building.Position + IntVec3.North.RotatedBy(building.Rotation);
        }

        bool coldValid = cold.InBounds(map) && !cold.Impassable(map);
        bool hotValid = kind != ElectricThermalDeviceKind.Cooler
            || (hot.InBounds(map) && !hot.Impassable(map));
        if (!cold.InBounds(map) || (kind == ElectricThermalDeviceKind.Cooler && !hot.InBounds(map)))
            return false;

        int width = map.Size.x;
        var device = new ElectricThermalDevice(building.thingIDNumber, kind,
            cold.z * width + cold.x,
            kind == ElectricThermalDeviceKind.Cooler ? hot.z * width + hot.x : -1,
            new ElectricThermalDeviceProfile(activeW, idleFactor));
        observation = new VanillaElectricDeviceObservation(device, target,
            power.PowerOn && !building.IsBrokenDown(), coldValid && hotValid);
        return true;
    }

    /// <summary>
    /// Collect a fresh snapshot each time the future authoritative equipment
    /// scheduler runs: power and target values may change without room changes.
    /// Do not cache operational state in the structural RoomControlSnapshot.
    /// </summary>
    public static List<VanillaElectricDeviceObservation> Capture(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var result = new List<VanillaElectricDeviceObservation>();
        foreach (Thing thing in map.listerThings.AllThings)
            if (thing is Building building && TryCapture(building, out var observation))
                result.Add(observation);
        result.Sort(static (a, b) => a.Device.ControllerId.CompareTo(b.Device.ControllerId));
        return result;
    }
}
