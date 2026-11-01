namespace Muqet.Thermal;

/// <summary>Energy accounting for one completed advance. Outdoor energy is signed.</summary>
public readonly record struct ThermalAdvanceResult(
    int Substeps,
    double SimulatedSeconds,
    double EnergyBeforeJ,
    double EnergyAfterJ,
    double SourceEnergyJ,
    double OutdoorEnergyJ)
{
    public double EnergyResidualJ => EnergyAfterJ - EnergyBeforeJ - SourceEnergyJ - OutdoorEnergyJ;
}
