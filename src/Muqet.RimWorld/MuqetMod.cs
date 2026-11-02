using HarmonyLib;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Installs read-only topology invalidation hooks exactly once. This adapter
/// intentionally does not suppress vanilla heat delivery or write room temps.
/// </summary>
public sealed class MuqetMod : Mod
{
    public MuqetMod(ModContentPack content) : base(content)
    {
        new Harmony("EarnestWilliams.Muqet").PatchAll(typeof(MuqetMod).Assembly);
    }
}
