using Muqet.Thermal;
using RimWorld;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Optional physical parameters on TerrainDef, ThingDef, StuffDef or RoofDef.
/// Both values must be supplied together. With no extension, material-category
/// defaults are used and counted as provisional in the M11 documentation.
/// </summary>
public sealed class MuqetThermalMaterialExtension : DefModExtension
{
    public float conductivityWPerMK = float.NaN;
    public float volumetricCapacityJPerM3K = float.NaN;
}

/// <summary>
/// Game-thread-only conversion of real RimWorld def IDs, stuff and geometry
/// into immutable material snapshots. It uses Celsius3-style definition caches
/// but NEVER shares Verse objects or live game defs with the numerical solver.
/// </summary>
internal sealed class RimWorldMaterialResolver
{
    private sealed record Profile(BulkThermalMaterial Material);
    private readonly ThermalDefinitionCache<Profile> cache = new();

    internal void Flush() => cache.Flush();
    internal long CacheHits => cache.Hits;
    internal long CacheMisses => cache.Misses;

    internal BulkThermalMaterial Terrain(TerrainDef def)
    {
        cache.TryGetTerrain(def.shortHash, _ => new Profile(ResolveTerrain(def)), out Profile? profile);
        return profile!.Material;
    }

    internal BulkThermalMaterial Structure(Building building)
    {
        ThingDef def = building.def;
        ThingDef stuff = building.Stuff ?? def;
        cache.TryGetThing(def.shortHash, stuff.shortHash,
            (_, _) => new Profile(ResolveBuilding(def, stuff)), out Profile? profile);
        return profile!.Material;
    }

    internal static BulkThermalMaterial Roof(RoofDef roof)
    {
        BulkThermalMaterial fallback = roof.isThickRoof || roof.isNatural
            ? ShadowBulkMaterialDefaults.Rock : ShadowBulkMaterialDefaults.Wood;
        return WithExtension(roof, fallback);
    }

    private static BulkThermalMaterial ResolveTerrain(TerrainDef def)
    {
        BulkThermalMaterial fallback = def.IsWater ? ShadowBulkMaterialDefaults.Water
            : def.IsRiver ? ShadowBulkMaterialDefaults.Water
            : ShadowBulkMaterialDefaults.Soil;
        return WithExtension(def, fallback);
    }

    private static BulkThermalMaterial ResolveBuilding(ThingDef def, ThingDef stuff)
    {
        BulkThermalMaterial fallback = def.building?.isNaturalRock == true
            ? ShadowBulkMaterialDefaults.Rock : ShadowBulkMaterialDefaults.Masonry;
        if (stuff.stuffProps is { } material)
        {
            if (material.categories.Contains(StuffCategoryDefOf.Woody))
                fallback = ShadowBulkMaterialDefaults.Wood;
            else if (material.categories.Contains(StuffCategoryDefOf.Metallic))
                fallback = ShadowBulkMaterialDefaults.Metal;
            else if (material.categories.Contains(StuffCategoryDefOf.Stony))
                fallback = ShadowBulkMaterialDefaults.Rock;
            else if (material.categories.Contains(StuffCategoryDefOf.Fabric)
                || material.categories.Contains(StuffCategoryDefOf.Leathery))
                fallback = ShadowBulkMaterialDefaults.Textile;
        }

        // Explicit thing override beats stuff override; otherwise material
        // identity (StuffDef) supplies the bulk properties.
        var onThing = def.GetModExtension<MuqetThermalMaterialExtension>();
        var onStuff = stuff.GetModExtension<MuqetThermalMaterialExtension>();
        return FromExtension(onThing ?? onStuff, fallback);
    }

    private static BulkThermalMaterial WithExtension(Def def, BulkThermalMaterial fallback)
        => FromExtension(def.GetModExtension<MuqetThermalMaterialExtension>(), fallback);

    private static BulkThermalMaterial FromExtension(
        MuqetThermalMaterialExtension? extension, BulkThermalMaterial fallback)
    {
        if (extension is null) return fallback;
        var result = new BulkThermalMaterial(
            extension.conductivityWPerMK, extension.volumetricCapacityJPerM3K);
        result.Validate();
        return result;
    }
}

/// <summary>Captures material and temperature fields on the game's main thread.</summary>
internal static class RimWorldMaterialShadowCollector
{
    internal static void Capture(Map map, RimWorldMaterialResolver resolver,
        MaterialShadowCell[] material, float[] temperature)
    {
        int width = map.Size.x, height = map.Size.z;
        if (material.Length != checked(width * height) || temperature.Length != material.Length)
            throw new ArgumentException("Material snapshot lengths do not match map dimensions.");

        for (int z = 0; z < height; z++)
        for (int x = 0; x < width; x++)
        {
            int index = z * width + x;
            IntVec3 c = new(x, 0, z);
            RoofDef? roof = map.roofGrid.RoofAt(c);
            TerrainDef terrain = map.terrainGrid.TerrainAt(c);
            Building? edifice = c.GetEdifice(map);
            Building_Door? door = edifice as Building_Door;

            ShadowCellKind kind = door is not null
                ? (door.Open ? ShadowCellKind.OpenDoor : ShadowCellKind.ClosedDoor)
                : c.Impassable(map) ? ShadowCellKind.SolidBarrier : ShadowCellKind.OpenAir;
            ShadowRoofKind roofKind = roof is null ? ShadowRoofKind.Unroofed
                : roof.isThickRoof ? ShadowRoofKind.ThickRoof : ShadowRoofKind.ThinRoof;

            bool barrier = kind is ShadowCellKind.SolidBarrier or ShadowCellKind.ClosedDoor;
            BulkThermalMaterial structural = barrier && edifice is not null
                ? resolver.Structure(edifice) : default;
            float thickness = barrier && edifice is not null
                ? door is not null ? 0.18f
                : edifice.def.building?.isNaturalRock == true ? 1.25f : 0.35f
                : 0;

            BulkThermalMaterial roofMaterial = roof is null
                ? default : RimWorldMaterialResolver.Roof(roof);
            float roofDepth = roof is null ? 0 : roof.isThickRoof ? 1.5f : 0.18f;

            float observed = c.GetTemperature(map);
            if (!float.IsFinite(observed) || observed < -273.15f)
                throw new InvalidOperationException($"Invalid vanilla temperature at cell {index}.");

            material[index] = new MaterialShadowCell(kind, roofKind,
                resolver.Terrain(terrain), structural, roofMaterial, thickness, roofDepth);
            material[index].Validate();
            temperature[index] = observed;
        }
    }
}
