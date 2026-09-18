namespace AliveSensor.Core;

/// <summary>
/// The ids of the event types that ship with the mod, so sensors and tests refer to them without loose strings.
/// Everything *about* a type — grade, range, perception, phrases — lives in the event catalog
/// (<c>assets/event-types.json</c>); a type added there needs no constant here to work.
/// </summary>
internal static class EventTypes
{
    public const string Bomb = "bomb";
    public const string SleepOutside = "sleep_outside";
    public const string Gift = "gift";
    public const string Trash = "trash";
    public const string Map = "map";
    public const string Place = "place";
    public const string Room = "room";
    public const string RoomLocked = "room_locked";
    public const string RoomUnlocked = "room_unlocked";
    public const string Talk = "talk";
    public const string Purchase = "purchase";
    public const string Consume = "consume";
    public const string NpcMove = "npc_move";

    /// <summary>Witness range meaning "the whole map".</summary>
    public const int WholeMap = -1;
}
