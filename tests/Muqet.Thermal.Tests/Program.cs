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
    ("zones cap at 25 and cover the room", ZoneCapacity),
    ("zones are individually contiguous", ZoneConnectivity),
    ("multi-controller assignment is deterministic", ZoneOwners),
    ("walls and reduced permeability restrict service", ZonePassages),
    ("zone temperatures and envelope losses", ZoneMeasurement),
    ("invalid zone topology rejected", ZoneInvalid),
    ("thermostat band and hysteresis", ThermostatHysteresis),
    ("remote tier relaxation", ThermostatTiers),
    ("core prevents local overheating", ThermostatGuard),
    ("heating and cooling conflicts", ThermostatConflict),
    ("thermostat inputs validated", ThermostatValidation),
    ("prepared cache parity and idle reuse", PreparedParity),
    ("prepared cache sparse invalidation", PreparedSparse),
    ("prepared cache rejects stale and invalid edits", PreparedInvalid),
    ("failed thermal advance preserves published state", AdvanceTransactional),
    ("definition memoization including negative hits", DefinitionMemo),
    ("definition flush and transactional rebuild", DefinitionRefresh),
    ("room measurement generation cache", RoomMemo),
    ("room cache rejects bad topology and snapshots", RoomMemoInvalid),
    ("heater energy and standby heat", HeaterEnergy),
    ("cooler two-sided energy ledger", CoolerEnergy),
    ("cooler hot-face lift and blocked ports", CoolerLift),
    ("frame rebuild is atomic and nonaccumulating", DeviceFrame),
    ("device model input validation", DeviceModelValidation),
    ("HVAC integrated heating publication", HvacIntegratedHeating),
    ("HVAC integrated cooling and exhaust", HvacIntegratedCooling),
    ("HVAC failing frames preserve published state", HvacRollback),
    ("HVAC topology and request validation", HvacTopology),
    ("shadow grid has symmetric edges and stable energy", ShadowGridEnergy),
    ("shadow wall, door, and roof coefficients", ShadowGeometry),
    ("shadow grid rejects unphysical calibration", ShadowInvalid),
    ("material density and thickness govern heat capacity", MaterialCapacity),
    ("material thermal resistance and mountain roofs", MaterialConductance),
    ("material-aware grid energy conservation", MaterialConservation),
    ("invalid material fields fail without state mutation", MaterialInvalid),
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


static void ZoneCapacity()
{
    var room = Enumerable.Repeat(4, 100).ToArray();
    var layout = VirtualControlZoneBuilder.Build(10, 10, room, [new ControlAnchor(7, 4, 90)]);
    True(layout.Zones.Count == 4, "100-cell room should produce four 25-cell zones.");
    True(layout.UnservedCellCount == 0, "All room cells must be served.");
    True(layout.Zones.All(z => z.Cells.Count == 25 && z.ControllerId == 7),
        "Zones must have 25 cells and a common controller.");
    for (int i = 0; i < 100; i++)
        True(layout.ZoneIndexAt(i) >= 0 && layout.ControllerIdAt(i) == 7, "Lost room cell.");
    for (int k = 0; k < 4; k++)
        True(layout.Zones[k].Tier == k, "Tier must increase from the source.");
}

static void ZoneConnectivity()
{
    // U-shaped room, requiring paths that are not Euclidean-nearest.
    var room = Enumerable.Repeat(-1, 9 * 9).ToArray();
    for (int y = 0; y < 9; y++)
    {
        room[y * 9] = 2;
        room[y * 9 + 8] = 2;
    }
    for (int x = 0; x < 9; x++) room[8 * 9 + x] = 2;
    var layout = VirtualControlZoneBuilder.Build(9, 9, room, [new ControlAnchor(3, 2, 0)], 7);
    True(layout.UnservedCellCount == 0, "U-shaped room cells should be reachable.");
    foreach (var zone in layout.Zones)
    {
        True(zone.Cells.Count <= 7, "Zone exceeds capacity.");
        var allowed = zone.Cells.ToHashSet();
        var seen = new HashSet<int> { zone.Cells[0] };
        var q = new Queue<int>();
        q.Enqueue(zone.Cells[0]);
        while (q.Count > 0)
        {
            int p = q.Dequeue(), x = p % 9;
            var adjacent = new List<int>();
            if (x > 0) adjacent.Add(p - 1);
            if (x < 8) adjacent.Add(p + 1);
            if (p >= 9) adjacent.Add(p - 9);
            if (p < 72) adjacent.Add(p + 9);
            foreach (int a in adjacent)
                if (allowed.Contains(a) && seen.Add(a)) q.Enqueue(a);
        }
        True(seen.Count == allowed.Count, "Zone must be connected by cardinal paths.");
    }
}

static void ZoneOwners()
{
    int[] room = Enumerable.Repeat(1, 10).ToArray();
    var a = VirtualControlZoneBuilder.Build(10, 1, room,
        [new ControlAnchor(20, 1, 9), new ControlAnchor(10, 1, 0)], 25);
    var b = VirtualControlZoneBuilder.Build(10, 1, room,
        [new ControlAnchor(10, 1, 0), new ControlAnchor(20, 1, 9)], 25);
    for (int i = 0; i < 10; i++)
    {
        True(a.ControllerIdAt(i) == b.ControllerIdAt(i), "Anchor input order must not change assignment.");
        True(a.ControllerIdAt(i) == (i < 5 ? 10 : 20), "Nearest controller should win.");
    }
    True(a.Zones.Count == 2 && a.Zones.All(z => z.Cells.Count == 5), "Two heaters split the room.");
}

static void ZonePassages()
{
    int[] room = Enumerable.Repeat(2, 6).ToArray();
    float[] east = [1, 0, 1, 1, 1, 0];
    var blocked = VirtualControlZoneBuilder.Build(6, 1, room,
        [new ControlAnchor(1, 2, 0)], eastPermeability: east);
    True(blocked.UnservedCellCount == 4, "Closed barrier disconnects four cells.");
    True(blocked.ZoneIndexAt(2) == -1 && blocked.ControllerIdAt(2) == -1,
        "Blocked cells cannot be silently assigned.");
    east[1] = 0.2f;
    var open = VirtualControlZoneBuilder.Build(6, 1, room,
        [new ControlAnchor(1, 2, 0)], eastPermeability: east);
    True(open.UnservedCellCount == 0, "Partially open passage reconnects the room.");
    True(open.Zones.Count == 1, "One short corridor fits in one zone.");
}

static void ZoneMeasurement()
{
    int[] room = Enumerable.Repeat(0, 5).ToArray();
    float[] loss = [0.2f, 0.3f, 0.4f, 0.5f, 0.6f];
    var layout = VirtualControlZoneBuilder.Build(5, 1, room,
        [new ControlAnchor(1, 0, 0)], envelopeLossWPerK: loss);
    Near(2, layout.Zones[0].EnvelopeConductanceWPerK, 0.000001);
    var measured = layout.MeasureZone(0, [16, 18, 20, 22, 24]);
    Near(16, measured.MinimumC);
    Near(16.8, measured.ColdP10C);
    Near(20, measured.MeanC);
    Near(23.2, measured.HotP90C);
    Near(24, measured.MaximumC);
    Throws<ArgumentException>(() => layout.MeasureZone(0, [16, 18, 20]));
    Throws<ArgumentOutOfRangeException>(() => layout.ZoneIndexAt(-1));
    True(!double.IsNaN(measured.MeanC), "Thermostat must be finite.");
}

static void ZoneInvalid()
{
    int[] room = Enumerable.Repeat(1, 4).ToArray();
    Throws<ArgumentException>(() => VirtualControlZoneBuilder.Build(2, 2, room,
        [new ControlAnchor(1, 2, 0)]));
    Throws<ArgumentException>(() => VirtualControlZoneBuilder.Build(2, 2, room,
        [new ControlAnchor(1, 1, 0), new ControlAnchor(1, 1, 1)]));
    Throws<ArgumentException>(() => VirtualControlZoneBuilder.Build(2, 2, room,
        [new ControlAnchor(1, 1, 0), new ControlAnchor(2, 1, 0)]));
    Throws<ArgumentOutOfRangeException>(() => VirtualControlZoneBuilder.Build(2, 2, room,
        [new ControlAnchor(1, 1, 0)], 0));
    Throws<ArgumentException>(() => VirtualControlZoneBuilder.Build(2, 2, room,
        [new ControlAnchor(1, 1, 0)], eastPermeability: [0, 1, 0, 0]));
}


static VirtualControlZoneLayout OneZone(int size) =>
    VirtualControlZoneBuilder.Build(size, 1, Enumerable.Repeat(0, size).ToArray(),
        [new ControlAnchor(1, 0, 0)]);

static void ThermostatHysteresis()
{
    var layout = OneZone(5);
    var control = new ZoneThermostat(1, new ThermostatBand(19, 23), ThermostatCapabilities.Heating);
    True(control.Evaluate(layout, [18, 18, 18, 18, 18]).RequestHeating, "Cold zone starts heating.");
    True(control.Evaluate(layout, [19.5f, 19.5f, 19.5f, 19.5f, 19.5f]).RequestHeating,
        "Within hysteresis heating remains enabled.");
    True(!control.Evaluate(layout, [20, 20, 20, 20, 20]).RequestHeating,
        "Upper hysteresis threshold stops heating.");
    True(!control.Evaluate(layout, [19.5f, 19.5f, 19.5f, 19.5f, 19.5f]).RequestHeating,
        "Device stays idle until temperature falls below 19.");
    control.Reset();
    True(!control.HeatingActive, "Reset clears state.");
}

static void ThermostatTiers()
{
    var room = Enumerable.Repeat(0, 50).ToArray();
    var layout = VirtualControlZoneBuilder.Build(50, 1, room,
        [new ControlAnchor(1, 0, 0)]);
    True(layout.Zones.Count == 2, "Expected two 25-cell zones.");
    var temps = Enumerable.Repeat(21f, 50).ToArray();
    for (int i = 25; i < 50; i++) temps[i] = 19f;
    var control = new ZoneThermostat(1, new ThermostatBand(20, 23), ThermostatCapabilities.Heating);
    True(!control.Evaluate(layout, temps).RequestHeating,
        "Tier one is allowed a one-degree lower mean.");
    for (int i = 25; i < 50; i++) temps[i] = 18.5f;
    True(control.Evaluate(layout, temps).RequestHeating,
        "Tier one demands heat below relaxed minimum while core is safe.");
}

static void ThermostatGuard()
{
    var layout = VirtualControlZoneBuilder.Build(50, 1,
        Enumerable.Repeat(0, 50).ToArray(), [new ControlAnchor(1, 0, 0)]);
    var temps = Enumerable.Repeat(25f, 50).ToArray();
    for (int i = 25; i < 50; i++) temps[i] = 12f;
    var controller = new ZoneThermostat(1, new ThermostatBand(19, 23), ThermostatCapabilities.Heating);
    var d = controller.Evaluate(layout, temps);
    True(!d.RequestHeating && d.HeatingLimitedByCore, "Hot core blocks remote heating demand.");
}

static void ThermostatConflict()
{
    var layout = VirtualControlZoneBuilder.Build(75, 1,
        Enumerable.Repeat(0, 75).ToArray(), [new ControlAnchor(1, 0, 0)]);
    var temps = Enumerable.Repeat(21f, 75).ToArray();
    for (int i = 25; i < 50; i++) temps[i] = 12f;
    for (int i = 50; i < 75; i++) temps[i] = 35f;
    var controller = new ZoneThermostat(1, new ThermostatBand(19, 23),
        ThermostatCapabilities.HeatingAndCooling);
    var d = controller.Evaluate(layout, temps);
    True(d.OpposingZoneDemands && !d.RequestHeating && !d.RequestCooling,
        "Single dual-mode controller cannot chase conflicting remote demands at once.");
    True(d.SampledZones == 3, "Thermostat sees all assigned tiers.");
}

static void ThermostatValidation()
{
    Throws<ArgumentException>(() => new ThermostatBand(23, 19).Validate());
    Throws<ArgumentException>(() => new ThermostatBand(19, 23, 6).Validate());
    Throws<ArgumentException>(() => new ThermostatBand(19, 23, 1, -1).Validate());
    Throws<ArgumentOutOfRangeException>(() =>
        new ZoneThermostat(-1, new ThermostatBand(19, 23), ThermostatCapabilities.Heating));
    Throws<ArgumentOutOfRangeException>(() =>
        new ZoneThermostat(1, new ThermostatBand(19, 23), (ThermostatCapabilities)8));
    var thermostat = new ZoneThermostat(1, new ThermostatBand(19, 23), ThermostatCapabilities.Cooling);
    var layout = OneZone(3);
    Throws<ArgumentException>(() => thermostat.Evaluate(layout, [20, 20]));
    Throws<InvalidOperationException>(() => new ZoneThermostat(7, new ThermostatBand(19, 23),
        ThermostatCapabilities.Heating).Evaluate(layout, [20, 20, 20]));
}


static ThermalGrid PreparedFixture()
{
    var g = new ThermalGrid(11, 7, 15, 5);
    for (int i = 0; i < g.CellCount; i++)
    {
        int x = i % g.Width;
        g.AirTemperatureC[i] = 10 + (i % 21);
        g.SolidTemperatureC[i] = 5 + (i % 15);
        g.AirCapacityJPerK[i] = 20 + (i % 5);
        g.SolidCapacityJPerK[i] = 50 + (i % 11);
        g.AirSolidWPerK[i] = 0.4f;
        g.OutdoorWPerK[i] = 0.02f;
        g.AirSourceW[i] = i % 3 - 1;
        if (x + 1 < g.Width)
        {
            g.AirEastWPerK[i] = 0.15f;
            g.SolidEastWPerK[i] = 0.25f;
        }
        if (i + g.Width < g.CellCount)
        {
            g.AirNorthWPerK[i] = 0.17f;
            g.SolidNorthWPerK[i] = 0.21f;
        }
    }
    return g;
}

static void PreparedParity()
{
    var regular = PreparedFixture();
    var cached = PreparedFixture();
    var x = new CpuThermalBackend(regular);
    var y = new CpuThermalBackend(cached);
    var cache = y.PrepareCoefficients();
    True(cache.LastCapturedCells == cached.CellCount, "First preparation captures all cells.");
    for (int step = 0; step < 20; step++)
    {
        var a = x.Advance(0.3, -3);
        var b = y.Advance(0.3, -3);
        Near(a.EnergyResidualJ, b.EnergyResidualJ, 0.002);
        True(a.Substeps == b.Substeps, "Prepared CFL bound must match reference.");
        for (int i = 0; i < regular.CellCount; i++)
        {
            Near(regular.AirTemperatureC[i], cached.AirTemperatureC[i], 0.00001);
            Near(regular.SolidTemperatureC[i], cached.SolidTemperatureC[i], 0.00001);
        }
    }
    True(cache.Refresh() == 0, "Unchanged topology must not be recaptured.");
    cache.AssertMatchesHost();
}

static void PreparedSparse()
{
    var g = PreparedFixture();
    var backend = new CpuThermalBackend(g);
    var cache = backend.PrepareCoefficients();
    long before = cache.TotalCapturedCells;
    g.AirEastWPerK[15] = 0.6f;
    g.AirCapacityJPerK[16] = 50f;
    cache.InvalidateCell(15);
    cache.InvalidateCell(16);
    Throws<InvalidOperationException>(() => backend.Advance(1, 4));
    True(cache.Refresh() == 2, "Only two modified source cells must be recaptured.");
    True(cache.TotalCapturedCells == before + 2, "Sparse capture counter mismatch.");
    cache.AssertMatchesHost();
    backend.Advance(1, 4);
    var independent = PreparedFixture();
    independent.AirEastWPerK[15] = 0.6f;
    independent.AirCapacityJPerK[16] = 50f;
    new CpuThermalBackend(independent).Advance(1, 4);
    for (int i = 0; i < g.CellCount; i++)
        Near(independent.AirTemperatureC[i], g.AirTemperatureC[i], 0.00001);
}

static void PreparedInvalid()
{
    var g = PreparedFixture();
    var backend = new CpuThermalBackend(g);
    var cache = backend.PrepareCoefficients();
    g.AirNorthWPerK[0] = float.NaN;
    cache.InvalidateCell(0);
    Throws<ArgumentException>(() => cache.Refresh());
    True(cache.HasPendingChanges, "Invalid batch must remain dirty.");
    g.AirNorthWPerK[0] = 0.24f;
    True(cache.Refresh() == 1, "Repaired cell should refresh.");
    cache.AssertMatchesHost();
    g.AirSourceW[0] = float.PositiveInfinity;
    Throws<ArgumentException>(() => backend.Advance(1, 10));
    g.AirSourceW[0] = 1;
    g.AirEastWPerK[0] = 0.9f;
    Throws<InvalidOperationException>(() => cache.AssertMatchesHost());
}

static void AdvanceTransactional()
{
    foreach (bool cachedMode in new[] { false, true })
    {
        var g = new ThermalGrid(2, 1, 20);
        g.AirCapacityJPerK[0] = 1;
        g.AirCapacityJPerK[1] = 1;
        g.AirEastWPerK[0] = 1;
        g.AirSourceW[0] = -500;
        var backend = new CpuThermalBackend(g);
        if (cachedMode) backend.PrepareCoefficients();
        // A sufficiently high negative source produces a substep failure.
        Throws<InvalidOperationException>(() => backend.Advance(1, 15));
        Near(20, g.AirTemperatureC[0]);
        Near(20, g.AirTemperatureC[1]);
    }
}


static void DefinitionMemo()
{
    var c = new ThermalDefinitionCache<string>();
    int hits = 0;
    True(c.TryGetThing(12, 21, (_, _) => { hits++; return "wood"; }, out var one) && one == "wood", "First resolution.");
    True(c.TryGetThing(12, 21, (_, _) => throw new Exception("Repeated resolution"), out var two) && two == "wood", "Cached.");
    True(!c.TryGetThing(12, 22, (_, _) => { hits++; return null; }, out _), "Missing value.");
    True(!c.TryGetThing(12, 22, (_, _) => throw new Exception("Negative miss repeated"), out _), "Negative cache.");
    True(c.TryGetTerrain(7, _ => { hits++; return "granite"; }, out _), "Terrain lookup.");
    True(c.TryGetTerrain(7, _ => throw new Exception("Repeated terrain resolution"), out _), "Terrain cache.");
    True(hits == 3 && c.Hits == 3 && c.Misses == 3 && c.NegativeHits == 1, "Counters.");
    True(c.ThingMaterialEntries == 2 && c.TerrainEntries == 1, "Entry count.");
}

static void DefinitionRefresh()
{
    var c = new ThermalDefinitionCache<string>();
    c.ReplaceTerrain(new Dictionary<ushort, string?> { [1] = "A", [2] = null });
    long gen = c.Generation;
    Throws<ArgumentException>(() => c.ReplaceTerrain(new[]
    {
        new KeyValuePair<ushort, string?>(1, "B"),
        new KeyValuePair<ushort, string?>(1, "C")
    }));
    True(c.Generation == gen && c.TryGetTerrain(1, _ => "WRONG", out var x) && x == "A",
        "Failed terrain replacement must preserve prior values.");
    c.Flush();
    True(c.Generation == gen + 1 && c.TerrainEntries == 0, "Flush must invalidate values.");
}

static void RoomMemo()
{
    var c = new RoomTemperatureCache();
    c.SetTopology(5, new Dictionary<int, int[]> { [4] = [0, 1, 2, 3, 4] });
    c.PublishTemperatureGeneration(1);
    float[] original = [16, 18, 20, 22, 24];
    var a = c.ReadRoom(4, original);
    Near(20, a.MeanC);
    Near(16.8, a.ColdP10C);
    True(c.ComputedSamples == 1, "First sample calculated.");
    var b = c.ReadRoom(4, original);
    Near(a.MeanC, b.MeanC);
    True(c.CacheHits == 1 && c.ComputedSamples == 1, "Same generation uses cache.");
    original[0] = 40;
    // A published snapshot must be immutable within one generation.
    Near(20, c.ReadRoom(4, original).MeanC);
    c.PublishTemperatureGeneration(2);
    Near(24.8, c.ReadRoom(4, original).MeanC);
    True(c.ComputedSamples == 2, "New generation recomputes.");
}

static void RoomMemoInvalid()
{
    var c = new RoomTemperatureCache();
    c.SetTopology(3, new Dictionary<int, int[]> { [1] = [0, 1] });
    c.PublishTemperatureGeneration(1);
    Throws<ArgumentException>(() => c.SetTopology(3, new Dictionary<int, int[]>
    {
        [1] = [0, 1], [2] = [1, 2]
    }));
    True(c.RoomCount == 1, "Bad topology must not replace a good cache.");
    Throws<ArgumentException>(() => c.ReadRoom(1, [12]));
    Throws<ArgumentOutOfRangeException>(() => c.PublishTemperatureGeneration(1));
    Throws<KeyNotFoundException>(() => c.ReadRoom(8, [15, 15, 15]));
    Near(15, c.ReadRoom(1, [15, 15, 15]).MeanC);
}


static ThermostatDecision Request(int id, bool heat = false, bool cool = false) =>
    new(id, heat, cool, false, false, false, 1, 20, 20, 20, 0, 0);

static void HeaterEnergy()
{
    var device = new ElectricThermalDevice(1, ElectricThermalDeviceKind.Heater, 0, -1,
        new ElectricThermalDeviceProfile(500, 0.1f));
    var heating = ElectricThermalDeviceModel.Evaluate(device, Request(1, heat: true),
        powerOn: true, portsReady: true, [15f]);
    Near(500, heating.ElectricalInputW);
    Near(500, heating.IndoorHeatW);
    Near(0, heating.ExhaustHeatW);
    True(heating.ActivelyHeatingOrCooling, "Heat must be active.");
    var standby = ElectricThermalDeviceModel.Evaluate(device, Request(1),
        powerOn: true, portsReady: true, [15f]);
    Near(50, standby.IndoorHeatW);
    Near(50, standby.ElectricalInputW);
    True(!standby.ActivelyHeatingOrCooling, "Standby is not heating mode.");
    var off = ElectricThermalDeviceModel.Evaluate(device, Request(1, heat: true),
        powerOn: false, portsReady: true, [15f]);
    Near(0, off.TotalThermalW);
    var blocked = ElectricThermalDeviceModel.Evaluate(device, Request(1, heat: true),
        powerOn: true, portsReady: false, [15f]);
    Near(50, blocked.TotalThermalW);
    var cutoff = ElectricThermalDeviceModel.Evaluate(device, Request(1, heat: true),
        powerOn: true, portsReady: true, [130f]);
    True(!cutoff.ActivelyHeatingOrCooling, "Heater must respect safety cutoff.");
}

static void CoolerEnergy()
{
    var device = new ElectricThermalDevice(2, ElectricThermalDeviceKind.Cooler, 0, 1,
        new ElectricThermalDeviceProfile(200, 0.1f, 3, 90));
    var running = ElectricThermalDeviceModel.Evaluate(device, Request(2, cool: true),
        powerOn: true, portsReady: true, [20f, 20f]);
    Near(200, running.ElectricalInputW);
    Near(-600, running.IndoorHeatW);
    Near(800, running.ExhaustHeatW);
    Near(200, running.TotalThermalW);
    Near(3, running.CoolingCop);
    True(running.ActivelyHeatingOrCooling, "Cooler must run.");
    var grid = new ThermalGrid(2, 1, 20);
    grid.AirCapacityJPerK[0] = grid.AirCapacityJPerK[1] = 1000;
    ElectricThermalSourceFrame.Apply(grid, new float[2], [running]);
    var result = new CpuThermalBackend(grid).Advance(0.1, 20);
    Near(200 * 0.1, result.SourceEnergyJ, 0.001);
    Conserved(result, 0.001);
    Near(19.94, grid.AirTemperatureC[0], 0.00001);
    Near(20.08, grid.AirTemperatureC[1], 0.00001);
    var standby = ElectricThermalDeviceModel.Evaluate(device, Request(2),
        powerOn: true, portsReady: true, [20f, 40f]);
    Near(20, standby.ElectricalInputW);
    Near(0, standby.IndoorHeatW);
    Near(20, standby.ExhaustHeatW);
    Near(20, standby.TotalThermalW);
}

static void CoolerLift()
{
    var d = new ElectricThermalDevice(4, ElectricThermalDeviceKind.Cooler, 0, 1,
        new ElectricThermalDeviceProfile(250, 0.1f, 4, 80));
    var cooled = ElectricThermalDeviceModel.Evaluate(d, Request(4, cool: true),
        true, true, [20f, 60f]);
    Near(2, cooled.CoolingCop);
    Near(-500, cooled.IndoorHeatW);
    Near(750, cooled.ExhaustHeatW);
    var impossible = ElectricThermalDeviceModel.Evaluate(d, Request(4, cool: true),
        true, true, [20f, 100f]);
    True(!impossible.ActivelyHeatingOrCooling, "Maximum thermal lift blocks cooling.");
    Near(25, impossible.ExhaustHeatW);
    var closed = ElectricThermalDeviceModel.Evaluate(d, Request(4, cool: true),
        true, false, [20f, 30f]);
    True(!closed.ActivelyHeatingOrCooling, "Blocked ports must stop heat transport.");
    var naturallyCool = ElectricThermalDeviceModel.Evaluate(d, Request(4, cool: true),
        true, true, [30f, 0f]);
    Near(4, naturallyCool.CoolingCop);
}

static void DeviceFrame()
{
    var grid = new ThermalGrid(3, 1);
    float[] baseline = [12f, 3f, -1f];
    var heater = new ElectricThermalDeviceOutput(1, 0, -1, 50, 50, 0, true, 0);
    var cooler = new ElectricThermalDeviceOutput(2, 1, 2, 10, -30, 40, true, 3);
    ElectricThermalSourceFrame.Apply(grid, baseline, [heater, cooler]);
    Near(62, grid.AirSourceW[0]);
    Near(-27, grid.AirSourceW[1]);
    Near(39, grid.AirSourceW[2]);
    ElectricThermalSourceFrame.Apply(grid, baseline, [heater, cooler]);
    Near(62, grid.AirSourceW[0]); // must not accumulate twice
    float[] snapshot = (float[])grid.AirSourceW.Clone();
    Throws<ArgumentException>(() => ElectricThermalSourceFrame.Apply(grid, baseline,
        [heater, new ElectricThermalDeviceOutput(2, 1, 2, 10, -500, 40, true, 1)]));
    for (int i = 0; i < 3; i++)
        Near(snapshot[i], grid.AirSourceW[i]);
    ElectricThermalSourceFrame.Apply(grid, baseline, []);
    Near(12, grid.AirSourceW[0]);
    Near(3, grid.AirSourceW[1]);
    Near(-1, grid.AirSourceW[2]);
}

static void DeviceModelValidation()
{
    Throws<ArgumentException>(() => new ElectricThermalDeviceProfile(0, .1f).Validate());
    Throws<ArgumentException>(() => new ElectricThermalDeviceProfile(100, -1).Validate());
    Throws<ArgumentException>(() => new ElectricThermalDevice(1,
        ElectricThermalDeviceKind.Cooler, 0, 0,
        new ElectricThermalDeviceProfile(200, .1f)).Validate(2));
    var heater = new ElectricThermalDevice(1,
        ElectricThermalDeviceKind.Heater, 0, -1, new ElectricThermalDeviceProfile(150, .1f));
    Throws<ArgumentException>(() => ElectricThermalDeviceModel.Evaluate(heater,
        Request(2, heat: true), true, true, [20f]));
    var grid = new ThermalGrid(1, 1);
    Throws<ArgumentException>(() => ElectricThermalSourceFrame.Apply(grid, grid.AirSourceW, []));
    Throws<ArgumentException>(() => ElectricThermalSourceFrame.Apply(grid, [float.PositiveInfinity], []));
}


static HvacSimulationCoordinator HvacFixture(ThermalGrid grid, params ControllerPlacement[] placements)
{
    var membership = Enumerable.Repeat(1, grid.CellCount).ToArray();
    var layout = RoomControlSnapshotBuilder.Build(grid.Width, grid.Height, membership, placements);
    var coordinator = new HvacSimulationCoordinator(grid);
    coordinator.BindTopology(layout);
    return coordinator;
}

static void HvacIntegratedHeating()
{
    var grid = new ThermalGrid(3, 1, 15);
    for (int i = 0; i < 3; i++) grid.AirCapacityJPerK[i] = 100;
    var coordinator = HvacFixture(grid, new ControllerPlacement(4, 0, 0));
    var heater = new ElectricThermalDevice(4, ElectricThermalDeviceKind.Heater, 0, -1,
        new ElectricThermalDeviceProfile(200, .1f));
    var request = new HvacDeviceRequest(heater, 21, true, true);
    var step = coordinator.Advance(.1, 15, [request], new float[3]);
    Near(20, step.Thermal.SourceEnergyJ, .0001);
    Near(20, step.ElectricalEnergyJ, .0001);
    Conserved(step.Thermal, .001);
    Near(15.2, grid.AirTemperatureC[0], .00001);
    True(step.Devices[0].ActivelyHeatingOrCooling, "Heating must run below the temperature band.");
    True(step.PublishedGeneration == 0, "First publication is generation zero.");
    var copy = new float[3];
    coordinator.CopyPublishedAirTemperatures(copy);
    Near(15.2, copy[0], .00001);
    Near((15.2 + 15 + 15) / 3, coordinator.ReadRoom(1).MeanC, .00001);

    var next = coordinator.Advance(.1, 15, [request], new float[3]);
    True(next.PublishedGeneration == 1, "Completed frame increments published generation.");
    Near(20, next.ElectricalEnergyJ, .001);
    True(coordinator.RoomTemperatures.TemperatureGeneration == 1,
        "Room measurements share published generation.");
}

static void HvacIntegratedCooling()
{
    var grid = new ThermalGrid(3, 1, 30);
    for (int i = 0; i < 3; i++) grid.AirCapacityJPerK[i] = 1000;
    var c = HvacFixture(grid, new ControllerPlacement(8, 0, 0));
    var cooler = new ElectricThermalDevice(8, ElectricThermalDeviceKind.Cooler, 0, 2,
        new ElectricThermalDeviceProfile(200, .1f, 3, 90));
    var step = c.Advance(.5, 30, [new HvacDeviceRequest(cooler, 20, true, true)], new float[3]);
    True(step.Devices[0].ActivelyHeatingOrCooling, "Cooler must request active power.");
    Near(-600, step.Devices[0].IndoorHeatW, .01);
    Near(800, step.Devices[0].ExhaustHeatW, .01);
    Near(100, step.ElectricalEnergyJ, .0001);
    Conserved(step.Thermal, .005);
    Near(29.7, grid.AirTemperatureC[0], .00001);
    Near(30.4, grid.AirTemperatureC[2], .00001);
}

static void HvacRollback()
{
    var grid = new ThermalGrid(2, 1, 20);
    grid.AirCapacityJPerK[0] = 1;
    grid.AirCapacityJPerK[1] = 1;
    grid.AirEastWPerK[0] = 1;
    grid.AirSourceW[0] = 4;
    var c = HvacFixture(grid, new ControllerPlacement(7, 0, 0));
    var heater = new ElectricThermalDevice(7, ElectricThermalDeviceKind.Heater, 0, -1,
        new ElectricThermalDeviceProfile(10, 0));
    Throws<InvalidOperationException>(() => c.Advance(1, 20,
        [new HvacDeviceRequest(heater, 30, true, true)], [-500f, 0f]));
    True(c.PublishedGeneration == -1, "Failed advance must not publish temperatures.");
    Near(20, grid.AirTemperatureC[0]);
    Near(4, grid.AirSourceW[0]);
    Throws<InvalidOperationException>(() => c.CopyPublishedAirTemperatures(new float[2]));

    var good = c.Advance(.1, 20, [new HvacDeviceRequest(heater, 30, true, true)], new float[2]);
    True(good.PublishedGeneration == 0 && good.Devices[0].ActivelyHeatingOrCooling,
        "Good frame after failed frame must start from unchanged hysteresis.");
}

static void HvacTopology()
{
    var grid = new ThermalGrid(2, 1, 20);
    var c = new HvacSimulationCoordinator(grid);
    var heater = new ElectricThermalDevice(5, ElectricThermalDeviceKind.Heater, 1, -1,
        new ElectricThermalDeviceProfile(100, .1f));
    Throws<InvalidOperationException>(() => c.Advance(1, 20,
        [new HvacDeviceRequest(heater, 25, true, true)], new float[2]));
    var snap = RoomControlSnapshotBuilder.Build(2, 1, [1, 1],
        [new ControllerPlacement(5, 0, 0)]);
    c.BindTopology(snap);
    Throws<ArgumentException>(() => c.Advance(1, 20,
        [new HvacDeviceRequest(heater, 25, true, true)], new float[2]));
    var correct = heater with { IndoorCell = 0 };
    Throws<ArgumentException>(() => c.Advance(1, 20,
        [new HvacDeviceRequest(correct, 25, true, true),
         new HvacDeviceRequest(correct, 25, true, true)], new float[2]));
    True(c.PublishedGeneration == -1, "Bad requests must not publish.");
}


static void ShadowGridEnergy()
{
    var p = ShadowThermalCalibration.Provisional;
    ShadowCell[] geometry = Enumerable.Repeat(new ShadowCell(ShadowCellKind.OpenAir,
        ShadowRoofKind.ThickRoof), 12).ToArray();
    float[] temps = Enumerable.Range(0, 12).Select(i => (float)(15 + i)).ToArray();
    var grid = ShadowThermalGridBuilder.Build(4, 3, geometry, temps, p);
    True(grid.AirEastWPerK[3] == 0 && grid.AirNorthWPerK[8] == 0,
        "Edges may not wrap rows or extend beyond the map.");
    True(grid.AirEastWPerK[0] > 0 && grid.AirNorthWPerK[0] > 0, "Open room requires connected air.");
    var result = new CpuThermalBackend(grid).Advance(.25, 21);
    Conserved(result, .02);
}

static void ShadowGeometry()
{
    var p = ShadowThermalCalibration.Provisional;
    ShadowCell[] cells =
    [
        new(ShadowCellKind.OpenAir, ShadowRoofKind.Unroofed),
        new(ShadowCellKind.ClosedDoor, ShadowRoofKind.ThinRoof),
        new(ShadowCellKind.SolidBarrier, ShadowRoofKind.ThickRoof),
        new(ShadowCellKind.OpenDoor, ShadowRoofKind.ThinRoof)
    ];
    var grid = ShadowThermalGridBuilder.Build(4, 1, cells, [20f, 20f, 20f, 20f], p);
    Near(p.ClosedDoorPassageWPerK, grid.AirEastWPerK[0]);
    Near(p.BarrierPassageWPerK, grid.AirEastWPerK[1]);
    Near(p.BarrierPassageWPerK, grid.AirEastWPerK[2]);
    Near(p.UnroofedOutdoorWPerK, grid.OutdoorWPerK[0]);
    Near(p.ThinRoofOutdoorWPerK, grid.OutdoorWPerK[1]);
    Near(p.ThickRoofOutdoorWPerK, grid.OutdoorWPerK[2]);
    Near(p.BarrierSolidCapacityJPerK, grid.SolidCapacityJPerK[2]);
    Near(20, grid.AirTemperatureC[0]);
}

static void ShadowInvalid()
{
    var c = ShadowThermalCalibration.Provisional;
    Throws<ArgumentException>(() => ShadowThermalGridBuilder.Build(2, 1,
        [new(ShadowCellKind.OpenAir, ShadowRoofKind.ThinRoof)],
        [20f], c));
    Throws<ArgumentException>(() => ShadowThermalGridBuilder.Build(1, 1,
        [new(ShadowCellKind.OpenAir, ShadowRoofKind.ThinRoof)],
        [float.NaN], c));
    Throws<ArgumentException>(() => ShadowThermalGridBuilder.Build(1, 1,
        [new(ShadowCellKind.OpenAir, ShadowRoofKind.ThinRoof)],
        [20f], c with { CellAirCapacityJPerK = 0 }));
}


static MaterialShadowCell SampleMaterialCell(ShadowCellKind kind = ShadowCellKind.OpenAir,
    ShadowRoofKind roof = ShadowRoofKind.Unroofed,
    BulkThermalMaterial? structure = null, float thickness = 0.35f)
{
    return new MaterialShadowCell(kind, roof, ShadowBulkMaterialDefaults.Soil,
        structure ?? ShadowBulkMaterialDefaults.Wood,
        ShadowBulkMaterialDefaults.Rock, thickness, roof == ShadowRoofKind.Unroofed ? 0 : 1.5f);
}

static void MaterialCapacity()
{
    var calibration = MaterialShadowCalibration.Provisional;
    var wood = SampleMaterialCell(ShadowCellKind.SolidBarrier, structure: ShadowBulkMaterialDefaults.Wood);
    var granite = SampleMaterialCell(ShadowCellKind.SolidBarrier, structure: ShadowBulkMaterialDefaults.Rock);
    var grid = MaterialShadowGridBuilder.Build(2, 1, [wood, granite], [20f, 20f], calibration);
    True(grid.SolidCapacityJPerK[1] > grid.SolidCapacityJPerK[0],
        "Equal-thickness rock must have greater thermal mass than wood.");
    var thick = granite with { StructureThicknessM = 0.7f };
    var doubled = MaterialShadowGridBuilder.Build(1, 1, [thick], [20f], calibration);
    Near(grid.SolidCapacityJPerK[1] + 0.35 * granite.Structure.VolumetricCapacityJPerM3K,
        doubled.SolidCapacityJPerK[0], 0.5);
}

static void MaterialConductance()
{
    var c = MaterialShadowCalibration.Provisional;
    var air = SampleMaterialCell();
    var timber = SampleMaterialCell(ShadowCellKind.SolidBarrier, structure: ShadowBulkMaterialDefaults.Wood);
    var metal = SampleMaterialCell(ShadowCellKind.SolidBarrier, structure: ShadowBulkMaterialDefaults.Metal);
    var wallWood = MaterialShadowGridBuilder.Build(2, 1, [air, timber], [20f, 10f], c);
    var wallMetal = MaterialShadowGridBuilder.Build(2, 1, [air, metal], [20f, 10f], c);
    True(wallWood.AirEastWPerK[0] < wallMetal.AirEastWPerK[0],
        "Metal conducts heat faster than a timber barrier.");
    True(wallWood.AirEastWPerK[0] < c.AirMixingWPerK,
        "Barrier conduction must not be treated as open airflow.");

    var mountain = SampleMaterialCell(roof: ShadowRoofKind.ThickRoof);
    var thin = mountain with { Roof = ShadowRoofKind.ThinRoof, RoofActiveDepthM = 0.18f };
    var roofs = MaterialShadowGridBuilder.Build(2, 1, [mountain, thin], [20f, 20f], c);
    Near(0, roofs.OutdoorWPerK[0], 0);
    True(roofs.OutdoorWPerK[1] > 0, "Constructed roof must exchange with external weather.");
    True(roofs.SolidCapacityJPerK[0] > roofs.SolidCapacityJPerK[1],
        "Mountain roof represents more thermal inertia than an exposed thin roof.");
}

static void MaterialConservation()
{
    var c = MaterialShadowCalibration.Provisional;
    MaterialShadowCell[] cells =
    [
        SampleMaterialCell(),
        SampleMaterialCell(ShadowCellKind.ClosedDoor, structure: ShadowBulkMaterialDefaults.Metal),
        SampleMaterialCell(ShadowCellKind.SolidBarrier, structure: ShadowBulkMaterialDefaults.Wood),
        SampleMaterialCell(roof: ShadowRoofKind.ThinRoof)
    ];
    var grid = MaterialShadowGridBuilder.Build(4, 1, cells, [30f, 20f, 10f, 0f], c);
    var outflow = new CpuThermalBackend(grid).Advance(1, 0);
    // The bulk slab stores millions of joules; single-precision field
    // publication can produce sub-joule roundoff in the energy ledger.
    Conserved(outflow, 0.5);
    var doors = MaterialShadowGridBuilder.Build(2, 1,
        [SampleMaterialCell(), SampleMaterialCell(ShadowCellKind.ClosedDoor)],
        [20f, 15f], c);
    True(doors.AirEastWPerK[0] < c.AirMixingWPerK,
        "Closed doors must not behave like unobstructed room air.");
}

static void MaterialInvalid()
{
    var c = MaterialShadowCalibration.Provisional;
    var bad = SampleMaterialCell(ShadowCellKind.SolidBarrier) with { StructureThicknessM = 0 };
    Throws<ArgumentException>(() => MaterialShadowGridBuilder.Build(1, 1, [bad], [20f], c));
    var invalid = SampleMaterialCell() with { Terrain = new BulkThermalMaterial(float.NaN, 1_000f) };
    Throws<ArgumentException>(() => MaterialShadowGridBuilder.Build(1, 1, [invalid], [20f], c));
    Throws<ArgumentException>(() => MaterialShadowGridBuilder.Build(1, 1,
        [SampleMaterialCell()], [20f], c with { SurfaceFilmWPerM2K = 0 }));
}
