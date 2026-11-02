using Muqet.Thermal;
using RimWorld;
using Verse;

namespace Muqet.RimWorld;

/// <summary>
/// Captures live RimWorld geometry on the game thread. No Verse references
/// cross into the thermal/zone simulation library.
/// </summary>
internal static class RimWorldRoomCollector
{
    internal static RoomControlSnapshot Capture(Map map)
    {
        int width = map.Size.x, height = map.Size.z;
        int[] roomIds = new int[checked(width * height)];
        Array.Fill(roomIds, -1);

        // Query a representative cell for each Room, then enumerate Room.Cells
        // once per ID. Exclude outdoor-temperature rooms from managed coverage.
        var visitedRooms = new HashSet<int>();
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                Room? room = new IntVec3(x, 0, z).GetRoom(map);
                if (room == null || room.ID < 0 || room.UsesOutdoorTemperature
                    || !visitedRooms.Add(room.ID))
                    continue;

                foreach (IntVec3 cell in room.Cells)
                    if (cell.x >= 0 && cell.x < width && cell.z >= 0 && cell.z < height)
                        roomIds[cell.z * width + cell.x] = room.ID;
            }
        }

        var devices = new List<ControllerPlacement>();
        foreach (Thing thing in map.listerThings.AllThings)
        {
            if (thing is not ThingWithComps building
                || building.GetComp<CompTempControl>() == null)
                continue;
            IntVec3 basePos = thing.Position;
            if (basePos.x < 0 || basePos.x >= width
                || basePos.z < 0 || basePos.z >= height)
                continue;

            IntVec3 facing = basePos + thing.Rotation.FacingCell;
            int outlet = facing.x >= 0 && facing.x < width
                && facing.z >= 0 && facing.z < height
                ? facing.z * width + facing.x : -1;
            devices.Add(new ControllerPlacement(thing.thingIDNumber,
                basePos.z * width + basePos.x, outlet));
        }
        return RoomControlSnapshotBuilder.Build(width, height, roomIds, devices.ToArray());
    }
}
