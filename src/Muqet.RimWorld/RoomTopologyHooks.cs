using HarmonyLib;
using RimWorld;
using Verse;

namespace Muqet.RimWorld;

/// <summary>Schedule rebuilding AFTER RimWorld finishes a room edit.</summary>
[HarmonyPatch(typeof(Room), nameof(Room.Notify_RoomShapeOrContainedBedsChanged))]
internal static class RoomShapeChangedHook
{
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
