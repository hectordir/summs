using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;

namespace Summs.Server;

/// <summary>
/// Relays timer changes between the Summs of a team. Each client computes its room id from the
/// game (a hash), so teammates meet without exchanging codes. Clients call Join, Put, Clear and
/// Leave; they receive Items, Cleared and Peers.
/// </summary>
sealed partial class RoomHub(RoomRegistry rooms) : Hub
{
    const int MaxItemsPerCall = 64, MaxKeyLength = 64, MaxDataLength = 1024;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex RoomIdPattern();

    public async Task<JoinResult> Join(string roomId, List<SyncItem> items, double? clearStamp)
    {
        Spend();
        if (!RoomIdPattern().IsMatch(roomId))
            throw new HubException("Sala no válida");
        items = Validate(items);

        await LeaveCurrentAsync();
        var room = rooms.Join(Context.ConnectionId, roomId) ?? throw new HubException("Sala llena");
        // Joined to the group first, so nothing sent from now on is missed; anything that arrives
        // twice (here and in the snapshot) is harmless.
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        bool cleared;
        List<SyncItem> accepted;
        JoinResult snapshot;
        int peers;
        lock (room)
        {
            // What the client had while disconnected; it rebuilds the room after a server restart.
            cleared = clearStamp is double stamp && double.IsFinite(stamp) && room.Clear(stamp);
            accepted = items.Where(room.Accept).ToList();
            snapshot = room.Snapshot();
            peers = room.Connections.Count;
        }

        var others = Clients.OthersInGroup(roomId);
        if (cleared)
            await others.SendAsync("Cleared", clearStamp);
        if (accepted.Count > 0)
            await others.SendAsync("Items", accepted);
        await Clients.Group(roomId).SendAsync("Peers", peers);
        return snapshot;
    }

    public async Task Put(List<SyncItem> items)
    {
        Spend();
        if (rooms.RoomOf(Context.ConnectionId) is not (string roomId, Room room))
            return;
        items = Validate(items);

        List<SyncItem> accepted;
        lock (room)
            accepted = items.Where(room.Accept).ToList();
        if (accepted.Count > 0)
            await Clients.OthersInGroup(roomId).SendAsync("Items", accepted);
    }

    public async Task Clear(double stamp)
    {
        Spend();
        if (!double.IsFinite(stamp) || rooms.RoomOf(Context.ConnectionId) is not (string roomId, Room room))
            return;

        bool cleared;
        lock (room)
            cleared = room.Clear(stamp);
        if (cleared)
            await Clients.OthersInGroup(roomId).SendAsync("Cleared", stamp);
    }

    public Task Leave()
    {
        Spend();
        return LeaveCurrentAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception) => LeaveCurrentAsync();

    async Task LeaveCurrentAsync()
    {
        if (rooms.Leave(Context.ConnectionId) is not (string roomId, Room room))
            return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        int peers;
        lock (room)
            peers = room.Connections.Count;
        await Clients.Group(roomId).SendAsync("Peers", peers);
    }

    static List<SyncItem> Validate(List<SyncItem>? items)
    {
        if (items is null || items.Count > MaxItemsPerCall)
            throw new HubException("Demasiados cambios");
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item?.Key) || item.Key.Length > MaxKeyLength || !double.IsFinite(item.Stamp)
                || item.Data?.GetRawText().Length > MaxDataLength)
                throw new HubException("Cambio no válido");
        }
        // The data is parsed from the incoming message, which is released after the call.
        return items.Select(i => i with { Data = i.Data?.Clone() }).ToList();
    }

    /// <summary>Per-connection rate limit: a burst of 40 calls, refilled at 4 per second.</summary>
    void Spend()
    {
        const double Capacity = 40, PerSecond = 4;
        var bucket = (TokenBucket)(Context.Items[nameof(TokenBucket)] ??= new TokenBucket { Tokens = Capacity, At = DateTime.UtcNow });
        lock (bucket)
        {
            var now = DateTime.UtcNow;
            bucket.Tokens = Math.Min(Capacity, bucket.Tokens + (now - bucket.At).TotalSeconds * PerSecond);
            bucket.At = now;
            if (bucket.Tokens < 1)
                throw new HubException("Demasiados mensajes");
            bucket.Tokens--;
        }
    }

    sealed class TokenBucket
    {
        public double Tokens;
        public DateTime At;
    }
}
