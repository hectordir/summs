using System.Collections.Concurrent;
using System.Text.Json;

namespace Summs.Server;

/// <summary>
/// One piece of shared state, e.g. "timer/TOP/1" or "insight/Syndra". Stamp is the shared clock
/// (game seconds) of the change; a null Data means it was removed. The server doesn't look
/// inside Data, it only keeps the newest version of each key.
/// </summary>
public sealed record SyncItem(string Key, double Stamp, JsonElement? Data);

/// <summary>ClearStamp is null if "clear all" was never used in the room.</summary>
public sealed record JoinResult(List<SyncItem> Items, double? ClearStamp);

/// <summary>The shared state of one team in one game. Only touched under its own lock.</summary>
sealed class Room
{
    public const string TimerPrefix = "timer/";
    const int MaxItems = 200;

    readonly Dictionary<string, SyncItem> _items = new();

    public HashSet<string> Connections { get; } = new();
    public DateTime EmptySince { get; set; } = DateTime.UtcNow;

    /// <summary>Every timer changed at or before this stamp was wiped by "clear all".</summary>
    public double? ClearStamp { get; private set; }

    /// <summary>Keeps the item if it's newer than what we have; ties go to the latest arrival.</summary>
    public bool Accept(SyncItem item)
    {
        if (item.Key.StartsWith(TimerPrefix) && item.Stamp < ClearStamp)
            return false;
        if (_items.TryGetValue(item.Key, out var current) ? item.Stamp < current.Stamp : _items.Count >= MaxItems)
            return false;
        _items[item.Key] = item;
        return true;
    }

    public bool Clear(double stamp)
    {
        if (stamp <= ClearStamp)
            return false;
        ClearStamp = stamp;
        foreach (var key in _items.Where(i => i.Key.StartsWith(TimerPrefix) && i.Value.Stamp <= stamp).Select(i => i.Key).ToList())
            _items.Remove(key);
        return true;
    }

    public JoinResult Snapshot() => new(_items.Values.ToList(), ClearStamp);
}

/// <summary>Rooms by id and which room each connection is in. Rooms only live in memory.</summary>
sealed class RoomRegistry
{
    const int MaxRooms = 10_000;
    public const int MaxConnectionsPerRoom = 10;

    readonly ConcurrentDictionary<string, Room> _rooms = new();
    readonly ConcurrentDictionary<string, string> _roomOf = new(); // connection id -> room id

    public int RoomCount => _rooms.Count;

    /// <summary>Puts the connection in the room, creating it if needed. Null if it can't take more.</summary>
    public Room? Join(string connectionId, string roomId)
    {
        while (true)
        {
            if (!_rooms.ContainsKey(roomId) && _rooms.Count >= MaxRooms)
                return null;
            var room = _rooms.GetOrAdd(roomId, _ => new Room());
            lock (room)
            {
                // RemoveEmpty may have dropped it between GetOrAdd and the lock: start over.
                if (!_rooms.TryGetValue(roomId, out var current) || current != room)
                    continue;
                if (room.Connections.Count >= MaxConnectionsPerRoom)
                    return null;
                room.Connections.Add(connectionId);
            }
            _roomOf[connectionId] = roomId;
            return room;
        }
    }

    /// <summary>Takes the connection out of its room; returns that room's id and what's left of it.</summary>
    public (string RoomId, Room Room)? Leave(string connectionId)
    {
        if (!_roomOf.TryRemove(connectionId, out var roomId) || !_rooms.TryGetValue(roomId, out var room))
            return null;
        lock (room)
        {
            room.Connections.Remove(connectionId);
            if (room.Connections.Count == 0)
                room.EmptySince = DateTime.UtcNow;
        }
        return (roomId, room);
    }

    public (string RoomId, Room Room)? RoomOf(string connectionId) =>
        _roomOf.TryGetValue(connectionId, out var roomId) && _rooms.TryGetValue(roomId, out var room)
            ? (roomId, room)
            : null;

    /// <summary>
    /// Drops rooms that have been empty for a while. Empty rooms are kept briefly so a client
    /// that reconnects (or a server restart) doesn't lose the game's timers.
    /// </summary>
    public void RemoveEmpty(TimeSpan grace)
    {
        var cutoff = DateTime.UtcNow - grace;
        foreach (var (id, room) in _rooms)
        {
            lock (room)
            {
                if (room.Connections.Count == 0 && room.EmptySince < cutoff)
                    _rooms.TryRemove(id, out _);
            }
        }
    }
}

sealed class RoomCleanup(RoomRegistry rooms) : BackgroundService
{
    // Long enough to reconnect, shorter than the gap before a rematch with the same ten players.
    static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            rooms.RemoveEmpty(Grace);
    }
}
