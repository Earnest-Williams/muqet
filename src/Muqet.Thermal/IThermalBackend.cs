namespace Muqet.Thermal;

/// <summary>
/// Synchronous reference contract. A future GPU implementation may additionally
/// expose asynchronous dispatch and readback, but must match these results within
/// documented floating-point tolerance at each published frame.
/// </summary>
public interface IThermalBackend
{
    int Width { get; }
    int Height { get; }
    ThermalAdvanceResult Advance(double elapsedSeconds, float outdoorTemperatureC);
    void CopyAirTemperatures(Span<float> destination);
    void CopySolidTemperatures(Span<float> destination);
}
