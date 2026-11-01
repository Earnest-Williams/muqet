using Muqet.Thermal;

var tests = new (string Name, Action Run)[]
{
    ("isolated grid remains steady", Isolated),
    ("equal-and-opposite horizontal heat flux", TwoCells),
    ("vertical edge transfers without row wrapping", VerticalAndBoundary),
    ("air-solid exchange conserves energy", AirSolid),
    ("solid-solid exchange conserves energy", SolidEdge),
    ("outdoor exchange is accounted for", Outdoors),
    ("source power integrates over elapsed seconds", Source),
    ("combined source and outdoor accounting", Combined),
    ("stiff conductance is automatically substepped", AutomaticSubsteps),
    ("excessively stiff advance fails rather than dropping time", ExcessiveSubsteps),
    ("zero-time advance is inert", ZeroTime),
    ("invalid properties are rejected", RejectInvalid),
    ("two backends with identical inputs are deterministic", Determinism),
    ("250x250 conservation regression", LargeMap),
};

int failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void Near(double expected, double actual, double tolerance = 0.0001)
{
    if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance)
        throw new Exception($"Expected {expected:G17}, got {actual:G17} (absolute tolerance {tolerance}).");
}

static void True(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static void Conserved(ThermalAdvanceResult result, double tolerance = 0.0001)
{
    Near(0, result.EnergyResidualJ, tolerance);
}

static void Isolated()
{
    var grid = new ThermalGrid(4, 3, 15);
    grid.AirTemperatureC[5] = 90;
    var solver = new CpuThermalBackend(grid);
    var before = grid.TotalEnergyJ();
    var step = solver.Advance(100, 0);
    Near(before, grid.TotalEnergyJ());
    Near(90, grid.AirTemperatureC[5]);
    True(step.Substeps == 1, "An isolated grid should need a single step.");
    Conserved(step);
}

static void TwoCells()
{
    var grid = new ThermalGrid(2, 1, 0);
    grid.AirCapacityJPerK[0] = grid.AirCapacityJPerK[1] = 10;
    grid.AirTemperatureC[0] = 100;
    grid.AirEastWPerK[0] = 2;
    var step = new CpuThermalBackend(grid).Advance(1, 20);
    Near(80, grid.AirTemperatureC[0]);
    Near(20, grid.AirTemperatureC[1]);
    Near(1000, grid.TotalEnergyJ());
    Conserved(step);
}

static void VerticalAndBoundary()
{
    var grid = new ThermalGrid(2, 2, 0);
    grid.AirTemperatureC[0] = 100;
    grid.AirNorthWPerK[0] = 0.1f;
    new CpuThermalBackend(grid).Advance(1, 0);
    Near(90, grid.AirTemperatureC[0], 0.0001);
    Near(10, grid.AirTemperatureC[2], 0.0001);
    Near(0, grid.AirTemperatureC[1]);
    Near(0, grid.AirTemperatureC[3]);
}

static void AirSolid()
{
    var grid = new ThermalGrid(1, 1, 100, 0);
    grid.AirCapacityJPerK[0] = 2;
    grid.SolidCapacityJPerK[0] = 8;
    grid.AirSolidWPerK[0] = 1;
    var step = new CpuThermalBackend(grid).Advance(0.1, 0);
    Near(95, grid.AirTemperatureC[0]);
    Near(1.25, grid.SolidTemperatureC[0]);
    Conserved(step);
}

static void SolidEdge()
{
    var grid = new ThermalGrid(2, 1, 0, 0);
    grid.SolidCapacityJPerK[0] = grid.SolidCapacityJPerK[1] = 10;
    grid.SolidTemperatureC[0] = 100;
    grid.SolidEastWPerK[0] = 1;
    var step = new CpuThermalBackend(grid).Advance(1, 0);
    Near(90, grid.SolidTemperatureC[0]);
    Near(10, grid.SolidTemperatureC[1]);
    Conserved(step);
}

static void Outdoors()
{
    var grid = new ThermalGrid(1, 1, 20);
    grid.AirCapacityJPerK[0] = 10;
    grid.OutdoorWPerK[0] = 1;
    var step = new CpuThermalBackend(grid).Advance(2, 0);
    Near(16, grid.AirTemperatureC[0]);
    Near(-40, step.OutdoorEnergyJ);
    Conserved(step);
}

static void Source()
{
    var grid = new ThermalGrid(1, 1, 20);
    grid.AirCapacityJPerK[0] = 2;
    grid.AirSourceW[0] = 5;
    var step = new CpuThermalBackend(grid).Advance(4, -20);
    Near(30, grid.AirTemperatureC[0]);
    Near(20, step.SourceEnergyJ);
    Conserved(step);
}

static void Combined()
{
    var grid = new ThermalGrid(1, 1, 20, 10);
    grid.AirCapacityJPerK[0] = 2;
    grid.SolidCapacityJPerK[0] = 8;
    grid.AirSolidWPerK[0] = 2;
    grid.OutdoorWPerK[0] = 0.7f;
    grid.AirSourceW[0] = 11;
    grid.SolidSourceW[0] = -2;
    var step = new CpuThermalBackend(grid).Advance(1.25, 3);
    Near(11.25, step.SourceEnergyJ);
    Conserved(step, 0.0001);
    True(grid.AirTemperatureC[0] < 20, "Air should cool with this combination of terms.");
}

static void AutomaticSubsteps()
{
    var grid = new ThermalGrid(2, 1, 0);
    grid.AirTemperatureC[0] = 100;
    grid.AirEastWPerK[0] = 100;
    var step = new CpuThermalBackend(grid).Advance(1, 20);
    True(step.Substeps > 100, $"Expected >100 substeps; got {step.Substeps}.");
    True(grid.AirTemperatureC[0] >= 0 && grid.AirTemperatureC[0] <= 100, "First cell overshot.");
    True(grid.AirTemperatureC[1] >= 0 && grid.AirTemperatureC[1] <= 100, "Second cell overshot.");
    Near(100, grid.TotalEnergyJ(), 0.0001);
    Conserved(step, 0.0001);
}

static void ExcessiveSubsteps()
{
    var grid = new ThermalGrid(2, 1, 0);
    grid.AirEastWPerK[0] = 10000;
    var solver = new CpuThermalBackend(grid);
    Throws<InvalidOperationException>(() => solver.Advance(1, 0));
    Near(0, grid.TotalEnergyJ());
}

static void ZeroTime()
{
    var grid = new ThermalGrid(2, 1, 30);
    grid.AirEastWPerK[0] = 0.1f;
    var solver = new CpuThermalBackend(grid);
    var result = solver.Advance(0, 0);
    True(result.Substeps == 0, "Zero time should not dispatch.");
    Span<float> read = stackalloc float[2];
    solver.CopyAirTemperatures(read);
    Near(30, read[0]);
    Near(30, read[1]);
    Throws<ArgumentException>(() => solver.CopyAirTemperatures(new float[1]));
    Conserved(result);
}

static void RejectInvalid()
{
    var grid = new ThermalGrid(2, 2);
    var solver = new CpuThermalBackend(grid);
    grid.AirCapacityJPerK[0] = 0;
    Throws<ArgumentException>(() => solver.Advance(1, 20));
    grid.AirCapacityJPerK[0] = 1;
    grid.AirEastWPerK[0] = float.NaN;
    Throws<ArgumentException>(() => solver.Advance(1, 20));
    grid.AirEastWPerK[0] = 0;
    grid.AirEastWPerK[1] = 1; // row boundary, not a neighbour
    Throws<ArgumentException>(() => solver.Advance(1, 20));
    grid.AirEastWPerK[1] = 0;
    grid.AirSolidWPerK[0] = 1; // no solid capacity
    Throws<ArgumentException>(() => solver.Advance(1, 20));
    grid.AirSolidWPerK[0] = 0;
    grid.SolidCapacityJPerK[0] = 1;
    grid.SolidEastWPerK[0] = 1; // second solid cell inactive
    Throws<ArgumentException>(() => solver.Advance(1, 20));
    grid.SolidEastWPerK[0] = 0;
    grid.AirSourceW[0] = float.PositiveInfinity;
    Throws<ArgumentException>(() => solver.Advance(1, 20));
}

static void Determinism()
{
    ThermalGrid Create()
    {
        var g = new ThermalGrid(17, 11, 18, 6);
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                int i = g.Index(x, y);
                g.AirCapacityJPerK[i] = 2 + (i % 5);
                g.SolidCapacityJPerK[i] = 3 + (i % 7);
                g.AirTemperatureC[i] = 12 + (i % 19);
                g.SolidTemperatureC[i] = 4 + (i % 13);
                g.AirSolidWPerK[i] = 0.25f;
                g.OutdoorWPerK[i] = 0.05f;
                g.AirSourceW[i] = (i % 3) - 1;
                if (x + 1 < g.Width)
                {
                    g.AirEastWPerK[i] = 0.2f;
                    g.SolidEastWPerK[i] = 0.4f;
                }
                if (y + 1 < g.Height)
                {
                    g.AirNorthWPerK[i] = 0.3f;
                    g.SolidNorthWPerK[i] = 0.2f;
                }
            }
        return g;
    }
    var a = Create();
    var b = Create();
    var x = new CpuThermalBackend(a);
    var y = new CpuThermalBackend(b);
    for (int n = 0; n < 15; n++)
    {
        var r1 = x.Advance(0.4, 11);
        var r2 = y.Advance(0.4, 11);
        True(r1 == r2, "Diagnostic results differ between identical runs.");
    }
    for (int i = 0; i < a.CellCount; i++)
    {
        True(BitConverter.SingleToInt32Bits(a.AirTemperatureC[i]) == BitConverter.SingleToInt32Bits(b.AirTemperatureC[i]), "Air differs.");
        True(BitConverter.SingleToInt32Bits(a.SolidTemperatureC[i]) == BitConverter.SingleToInt32Bits(b.SolidTemperatureC[i]), "Solid differs.");
    }
}

static void LargeMap()
{
    var grid = new ThermalGrid(250, 250, 20);
    grid.AirTemperatureC[grid.Index(125, 125)] = 100;
    for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
        {
            int i = grid.Index(x, y);
            if (x + 1 < grid.Width) grid.AirEastWPerK[i] = 0.1f;
            if (y + 1 < grid.Height) grid.AirNorthWPerK[i] = 0.1f;
        }
    var solver = new CpuThermalBackend(grid);
    for (int i = 0; i < 5; i++)
    {
        var result = solver.Advance(1, -20);
        Conserved(result, 0.1);
    }
    Near(20, grid.AirTemperatureC[grid.Index(1, 1)]);
    True(grid.AirTemperatureC[grid.Index(125, 125)] < 100, "Hot cell did not diffuse.");
}
