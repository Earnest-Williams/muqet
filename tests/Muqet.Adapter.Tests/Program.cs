using Muqet.Thermal;

var checks = new (string Name, Action Test)[]
{
    ("facing outlet selects the correct room", OutletFacing),
    ("wall-mounted device resolves adjacent air", WallMount),
    ("two controllers receive different anchors", Collision),
    ("skipped devices never create phantom zones", Missing),
    ("room split never crosses into other room", SplitRooms),
    ("input ordering is deterministic", Deterministic),
    ("room and device mutations update fingerprint", ChangedFingerprint),
    ("far-away outlet cannot seize another room", RejectDistantOutlet),
    ("invalid placements are rejected", RejectInvalid)
};
int fails = 0;
foreach (var (name, test) in checks)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { fails++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
}
Console.WriteLine((checks.Length - fails) + "/" + checks.Length + " passed");
return fails == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Reject<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected exception " + typeof(T).Name);
}
static void OutletFacing()
{
    // Device in wall cell #2, indoor room on left and right.
    // Facing-right must select #3, not arbitrarily select left.
    int[] rooms = { 1, 1, -1, 2, 2 };
    var snapshot = RoomControlSnapshotBuilder.Build(5, 1, rooms,
        [new ControllerPlacement(4, 2, 3)]);
    Require(snapshot.Anchors.Count == 1 && snapshot.Anchors[0].CellIndex == 3
        && snapshot.Anchors[0].RoomId == 2, "Selected wrong outlet room.");
}
static void WallMount()
{
    int[] rooms = { 2, 2, 2, -1, 2, 2, 2, 2, 2 };
    var snapshot = RoomControlSnapshotBuilder.Build(3, 3, rooms,
        [new ControllerPlacement(1, 3, -1)]);
    Require(snapshot.Anchors[0].CellIndex == 6, "North-first deterministic fallback failed.");
    Require(snapshot.Zones.UnservedCellCount == 0, "Reachable room must be covered.");
}
static void Collision()
{
    int[] rooms = { 3, 3, 3, 3 };
    var snapshot = RoomControlSnapshotBuilder.Build(4, 1, rooms,
        [new ControllerPlacement(5, 0, 1), new ControllerPlacement(2, 2, 1)]);
    Require(snapshot.Anchors[0].ControllerId == 2 && snapshot.Anchors[0].CellIndex == 1,
        "Lower ID should claim contested outlet.");
    Require(snapshot.Anchors[1].ControllerId == 5 && snapshot.Anchors[1].CellIndex == 0,
        "Other device should fall back to its base cell.");
    Require(snapshot.SkippedDeviceCount == 0, "Both controllers should be served.");
}
static void Missing()
{
    int[] rooms = { -1, -1, -1, -1 };
    var snapshot = RoomControlSnapshotBuilder.Build(2, 2, rooms,
        [new ControllerPlacement(7, 0, -1)]);
    Require(snapshot.SkippedDeviceCount == 1 && snapshot.Anchors.Count == 0
        && snapshot.Zones.Zones.Count == 0, "Unserved device must be visible.");
}
static void SplitRooms()
{
    int[] rooms = { 1, 1, -1, 2, 2 };
    var snapshot = RoomControlSnapshotBuilder.Build(5, 1, rooms,
        [new ControllerPlacement(9, 1, 1)]);
    Require(snapshot.Zones.UnservedCellCount == 2, "Other room must remain unserved.");
    Require(snapshot.Zones.ControllerIdAt(4) == -1, "Zones crossed room boundary.");
}
static void Deterministic()
{
    int[] rooms = Enumerable.Repeat(8, 20).ToArray();
    ControllerPlacement[] a = [new(9, 15, 14), new(4, 3, 2)];
    ControllerPlacement[] b = [a[1], a[0]];
    var one = RoomControlSnapshotBuilder.Build(20, 1, rooms, a, 5);
    var two = RoomControlSnapshotBuilder.Build(20, 1, rooms, b, 5);
    Require(one.Fingerprint == two.Fingerprint, "Fingerprint depends on device iteration order.");
    Require(one.Anchors.SequenceEqual(two.Anchors), "Resolved anchors vary by order.");
}
static void ChangedFingerprint()
{
    var original = RoomControlSnapshotBuilder.Build(3, 1, new[] { 1, 1, 1 },
        [new ControllerPlacement(11, 0, 1)]);
    var split = RoomControlSnapshotBuilder.Build(3, 1, new[] { 1, 1, 2 },
        [new ControllerPlacement(11, 0, 1)]);
    var moved = RoomControlSnapshotBuilder.Build(3, 1, new[] { 1, 1, 1 },
        [new ControllerPlacement(11, 2, 1)]);
    Require(original.Fingerprint != split.Fingerprint, "Room split was not detected.");
    Require(original.Fingerprint != moved.Fingerprint, "Device position was not detected.");
}
static void RejectDistantOutlet()
{
    var snapshot = RoomControlSnapshotBuilder.Build(5, 1, new[] { 1, 1, -1, 2, 2 },
        [new ControllerPlacement(12, 0, 3)]);
    Require(snapshot.Anchors[0].CellIndex == 0,
        "A non-adjacent preferred outlet must be ignored.");
}
static void RejectInvalid()
{
    int[] rooms = { 1, 1 };
    Reject<ArgumentException>(() => RoomControlSnapshotBuilder.Build(2, 1, rooms,
        [new ControllerPlacement(1, 0, 1), new ControllerPlacement(1, 1, 0)]));
    Reject<ArgumentException>(() => RoomControlSnapshotBuilder.Build(2, 1, rooms,
        [new ControllerPlacement(2, 6, -1)]));
    Reject<ArgumentException>(() => RoomControlSnapshotBuilder.Build(2, 1, rooms,
        [new ControllerPlacement(2, 0, 9)]));
    Reject<ArgumentException>(() => RoomControlSnapshotBuilder.Build(2, 1,
        new[] { 1 }, [new ControllerPlacement(2, 0, -1)]));
}
