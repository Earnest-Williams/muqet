namespace Muqet.Thermal;

/// <summary>
/// Definition-property memoisation for the game-thread adapter. Inspired by
/// Celsius3's terrain short-hash and ThingDef/StuffDef pair lookups.
///
/// Both successful and unsuccessful lookups are remembered: a missing profile
/// must not trigger repeated XML/mod-extension resolution every frame.
/// T is an immutable, host-owned reference type; this class does not convert
/// material values, infer physics or retain any Verse objects itself.
/// </summary>
public sealed class ThermalDefinitionCache<T> where T : class
{
    // Nullable value is intentional. Dictionary.TryGetValue distinguishes a
    // cached "not found" (key present, null) from a never-queried key.
    private readonly Dictionary<ushort, T?> terrain = new();
    private readonly Dictionary<uint, T?> things = new();

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long NegativeHits { get; private set; }
    public long Generation { get; private set; }
    public int TerrainEntries => terrain.Count;
    public int ThingMaterialEntries => things.Count;

    /// <summary>
    /// Cache by TerrainDef.shortHash. Resolver is invoked only on an uncached
    /// key. A null result is cached as a negative hit.
    /// </summary>
    public bool TryGetTerrain(ushort terrainHash, Func<ushort, T?> resolver, out T? value)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (terrain.TryGetValue(terrainHash, out value))
        {
            Hits++;
            if (value is null) NegativeHits++;
            return value is not null;
        }
        // Failed resolver calls must not poison the cache.
        T? resolved = resolver(terrainHash);
        terrain.Add(terrainHash, resolved);
        Misses++;
        value = resolved;
        return resolved is not null;
    }

    /// <summary>
    /// Cache by ThingDef.shortHash and StuffDef.shortHash. For non-stuffable
    /// items pass the thing hash as the stuff hash. The two halves of the key
    /// must never be combined with addition or a potentially colliding hash.
    /// </summary>
    public bool TryGetThing(ushort thingHash, ushort stuffHash,
        Func<ushort, ushort, T?> resolver, out T? value)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        uint key = ((uint)thingHash << 16) | stuffHash;
        if (things.TryGetValue(key, out value))
        {
            Hits++;
            if (value is null) NegativeHits++;
            return value is not null;
        }
        T? resolved = resolver(thingHash, stuffHash);
        things.Add(key, resolved);
        Misses++;
        value = resolved;
        return resolved is not null;
    }

    /// <summary>
    /// Replace the complete terrain lookup after settings or definition changes.
    /// The batch is validated before publishing, so duplicates/failing
    /// enumerators leave the previously prepared terrain lookup unchanged.
    /// Unlike the lazy path, null entries intentionally represent absent data.
    /// </summary>
    public void ReplaceTerrain(IEnumerable<KeyValuePair<ushort, T?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var replacement = new Dictionary<ushort, T?>();
        foreach (var entry in entries)
            replacement.Add(entry.Key, entry.Value);
        terrain.Clear();
        foreach (var entry in replacement)
            terrain.Add(entry.Key, entry.Value);
        Generation++;
    }

    /// <summary>
    /// Flush all profiles after mod settings/Def database changes. Consumers
    /// must also invalidate any already prepared per-cell coefficient grids.
    /// The cache is deliberately owned by a map/session, not global-static.
    /// </summary>
    public void Flush()
    {
        terrain.Clear();
        things.Clear();
        Generation++;
    }
}
