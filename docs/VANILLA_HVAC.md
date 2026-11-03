# M8: electric heater and cooler energy-budgeted replacement layer

## Current status

This milestone builds the **device energy model and input capture**, while preserving vanilla HVAC operation in RimWorld. The numerical solver, virtual control zones, thermostat policy and Room.Cells collector are already independent components. Muqet does not yet publish authoritative air temperatures to RimWorld and must NOT suppress vanilla heat calls without a complete simulation ownership transition.

## Existing vanilla device behaviour

Vanilla \`Building_Heater.TickRare()\` gets the local/room temperature, uses \`CompTempControl.Props.energyPerSecond\` and \`GenTemperature.ControlTemperatureTempChange\` to alter the room group immediately, then updates \`CompPowerTrader.PowerOutput\`. \`Building_Cooler.TickRare()\` resolves the **south rotated face as cold intake** and **north rotated face as hot exhaust**, computes a temperature-difference efficiency factor, directly modifies the cold room temperature and calls \`GenTemperature.PushHeat\` for exhaust (a nominal 1.25 multiplier). These legacy heat units are not SI watts and must not be inserted directly into a J/K-based solver.

\`CompTempControl\` already provides a player-facing single temperature target and a low-power consumption factor. The new controller layer can wrap the existing target with a configurable comfort band; it does not require a player-placed thermostat for vanilla equipment.

## New equipment model

\`ElectricThermalDeviceModel.Evaluate\` consumes an equipment placement, thermal profile, thermostat decision, current power/port availability and a **single published temperature snapshot**. Outputs are watts: electric input, heat at indoor face, heat at exhaust face, actual cooling COP, and active/idle status. No solver state is mutated by \`Evaluate\`.

- **Heater:** when running, local heat equals electrical input watts (resistive heating). When powered but idle, the configured standby electrical consumption is still deposited as heat locally. Heater thermal cutoff is modelled separately from thermostat control.
- **Cooler:** running cooling extracts a finite rate \`Q_cold = COP * W_input\`. The hot face receives \`Q_hot = Q_cold + W_input\`. COP decreases linearly with positive temperature lift \`max(0, T_exhaust - T_cold)\`, reaching zero at the configured maximum lift. This is a provisional gameplay efficiency approximation, not a fully specified compressor/refrigerant cycle. Powered but idle, standby electricity becomes heat on the hot side.
- **Power/ports:** \`PowerOn = false\` produces no heat or electric draw. Blocked or invalid ports prevent active heat transport. The caller reads flick, failure and power network state rather than inventing supply.
- **No target forcing:** output watts remain finite and are applied locally. The thermostat requests action from zone measurements; the solver determines resulting temperatures according to insulation, air/solid capacities and transport.

\`ElectricThermalSourceFrame.Apply\` builds a complete air source frame from an independent baseline (fires, geothermal and other sources) plus device outputs, checks finite watts and electrical balance, then atomically replaces \`ThermalGrid.AirSourceW\`. Reapplying the same frame does **not** multiply its heat. A failed frame leaves the previous grid untouched.

\`VanillaElectricDeviceCapture\` is a native RimWorld adapter that reads \`Building_Heater\`/\`Building_Cooler\`, actual \`CompPowerTrader.Props.basePowerConsumption\`, \`CompTempControl.targetTemperature\`, idle power factor, power-on/broken status and orientation-dependent ports. Cooling intake is South.RotatedBy and exhaust North.RotatedBy; both are checked for bounds and obstruction. This capture is **read-only** and deliberately separate from the existing heuristic control-anchor selection; the latter must be reconciled to the actual cold-side port before enabling Muqet HVAC ownership.

## Calibrations that require testing

The default cooling COP (3), maximum lift (90 K) and heater cutoff (120 C) are explicit **provisional calibration assumptions**. They are not claims about physical hardware or exact vanilla balance. The native bridge captures nominal electric watts from the Def rather than assuming 175 W/200 W universally, allowing supported mods to change costs. Target bandwidth and 1-degree tier relaxation remain policy settings, not effects on thermal state.

## Next requirements for **active** vanilla replacement

1. Provide a RimWorld map-owned, initialized and conserved thermal grid, with calibrated joules, watts and seconds per tick; publish cell temperatures to game queries and room views.
2. Wire source frames and zone thermostats into one committed simulation generation per update; validate cooler cold/hot face orientation and powered-state transitions in a real 1.7 Waylith game.
3. Only when step 1–2 are fully active, conditionally bypass vanilla heater/cooler \`TickRare\` thermal effects **for those same supported devices**, and explicitly take ownership of electrical power modes and \`CompTempControl.operatingAtHighPower\`. Avoid double injection or a mode where neither solver runs.
4. Ensure fallback to vanilla on unsupported maps/devices; test save/load, flicks, breakdown, exhaust blockage, outdoor exhaust, multiple units, cold-side thermostat placement and mountain freezers.
5. Measure and tune room warmup/freezer capacity in representative 25/50/100-cell rooms, including insulation and leakage, before changing device Def power values or integrating predictive room-response tables.

No patches in this milestone suppress \`TickRare\`, \`PushHeat\`, room temperatures or vanilla power flow. The mod remains observational until an end-to-end authoritative replacement exists.

## Verification

\`\`\`bash
dotnet run --project tests/Muqet.Thermal.Tests/Muqet.Thermal.Tests.csproj -c Release
dotnet run --project tests/Muqet.Adapter.Tests/Muqet.Adapter.Tests.csproj -c Release
dotnet build src/Muqet.RimWorld/Muqet.RimWorld.csproj -c Release
\`\`\`

Added regressions cover heater output and standby power, cooler two-face energy accounting and conservation through the CPU solver, temperature-lift behaviour, blocked faces, idempotent source frames, atomic failure behaviour and invalid-device rejection.
