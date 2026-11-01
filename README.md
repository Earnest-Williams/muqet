# Muqet

Independent thermal simulation for RimWorld 1.7, developed using Celsius and Celsius3 as architectural references. Muqet is **not** a fork or code transplant.

The initial CPU reference kernel uses an energy-conserving two-layer (air/solid) finite-volume model with symmetric edge conductances, fixed outdoor reservoirs, continuous heating/cooling sources, adaptive stable substeps and an explicit energy ledger.

## Build and check

Requires the .NET 10 SDK. No third-party NuGet packages are needed for the core or regression runner.

```bash
dotnet build src/Muqet.Thermal/Muqet.Thermal.csproj -c Release
dotnet run --project tests/Muqet.Thermal.Tests/Muqet.Thermal.Tests.csproj -c Release
```

The first milestone is a standalone numeric library, **not yet a loadable RimWorld mod**. GPU compute, airflow, RimWorld hooks and save integration are subsequent work. The CPU kernel is retained as the expected-correctness baseline for future GPU parity tests.

See [thermal design](docs/THERMAL_DESIGN.md) for units, mathematics, limitations and roadmap.
