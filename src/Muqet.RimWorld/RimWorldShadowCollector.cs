using Muqet.Thermal;
using RimWorld;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Copies a per-cell thermal observation on the game thread. The classes are
/// provisional: they do not infer wall material, density, door leakage or soil
/// properties from Defs. These observations never change any vanilla state.
/// </summary>
internal static class RimWorldShadowCollector
{
    internal static void Capture(Map map, ShadowCell[] geometry, float[] temperatureC)
    {
        int width = map.Size.x, height = map.Size.z;
        if (geometry.Length != checked(width * height) || temperatureC.Length != geometry.Length)
            throw new ArgumentException("Shadow capture array length mismatch.");

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = z * width + x;
                IntVec3 c = new(x, 0, z);
                RoofDef? roof = map.roofGrid.RoofAt(c);
                ShadowRoofKind roofKind = roof is null ? ShadowRoofKind.Unroofed
                    : roof.isThickRoof ? ShadowRoofKind.ThickRoof : ShadowRoofKind.ThinRoof;
                ShadowCellKind kind = c.Impassable(map)
                    ? ShadowCellKind.SolidBarrier : ShadowCellKind.OpenAir;

                // The door can report impassable while shut; classify it
                // separately so a change in openness affects conductance.
                foreach (Thing thing in map.thingGrid.ThingsListAtFast(c))
                    if (thing is Building_Door door)
                    {
                        kind = door.Open ? ShadowCellKind.OpenDoor : ShadowCellKind.ClosedDoor;
                        break;
                    }

                float observed = c.GetTemperature(map);
                if (!float.IsFinite(observed) || observed < -273.15f)
                    throw new InvalidOperationException($"Invalid vanilla temperature at cell {i}.");
                geometry[i] = new ShadowCell(kind, roofKind);
                temperatureC[i] = observed;
            }
        }
    }
}
