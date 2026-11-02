using HarmonyLib;
using RimWorld;
using System.Reflection;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Some Waylith builds do not expose the older room-shape notification
/// name. Select only a confirmed method, or let the MapComponent safety
/// poll discover room edits. No patch is installed on a missing target.
/// </summary>
[HarmonyPatch]
internal static class RoomShapeChangedHook
{
    private static MethodBase? target;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        target = AccessTools.Method(typeof(Room), "Notify_RoomShapeChanged")
            ?? AccessTools.Method(typeof(Room), "Notify_RoomShapeOrContainedBedsChanged");
        return target is not null;
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => target;

    [HarmonyPostfix]
    private static void AfterChange(Room __instance)
    {
        __instance.Map?.GetComponent<MuqetMapComponent>()?.MarkDirty();
    }
}

/// <summary>New temperature-controlled equipment needs a fresh anchor.</summary>
[HarmonyPatch(typeof(CompTempControl), nameof(CompTempControl.PostSpawnSetup))]
internal static class TemperatureDeviceSpawnedHook
{
    [HarmonyPostfix]
    private static void AfterSpawn(CompTempControl __instance)
    {
        __instance.parent?.Map?.GetComponent<MuqetMapComponent>()?.MarkDirty();
    }
}
