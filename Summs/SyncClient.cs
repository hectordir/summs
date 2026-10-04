using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Summs;

/// <summary>
/// One piece of shared state, e.g. "timer/TOP/1" or "insight/Syndra". Stamp is the shared clock
/// (game seconds) of the change; a null Data means it was removed.
/// </summary>
sealed record SyncItem(string Key, double Stamp, JsonElement? Data);

sealed record JoinResult(List<SyncItem> Items, double? ClearStamp);

/// <summary>
/// Shares the timers with the teammates in the same room through the Summs server (SignalR).
/// Keeps a copy of the room's state, which it sends again whenever it (re)joins, so nothing is
/// lost if the connection drops or the server restarts. Changes are kept if newer (by Stamp).
/// Everything runs on the UI thread; events are raised there too.
/// </summary>
sealed class SyncClient : IDisposable
{
    public const string DefaultServer = "https://summs-server-production.up.railway.app";
    public const string TimerPrefix = "timer/";
    const int RetryMs = 5000;

    readonly HubConnection _connection;
    readonly SynchronizationContext _ui;
    readonly System.Windows.Forms.Timer _retry = new() { Interval = RetryMs };
    readonly Dictionary<string, SyncItem> _items = new();
    // Keys changed while not in the room (or while joining), to send once in it.
    readonly HashSet<string> _pending = new();
    double? _clearStamp;
    bool _pendingClear;
    string? _room;       // the room we want to be in
    string? _joinedRoom; // the room the server has us in
    bool _syncing, _resync;

    /// <summary>A teammate's change (or a removal, with null Data) to apply locally.</summary>
    public event Action<SyncItem>? ItemReceived;

    /// <summary>How many Summs are in the room, this one included; 0 when not connected.</summary>
    public event Action<int>? PeersChanged;

    public SyncClient(string server)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _connection = new HubConnectionBuilder()
            .WithUrl(server.TrimEnd('/') + "/sync")
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _connection.On<List<SyncItem>>("Items", items => _ = Post(() =>
        {
            if (_room is not null)
                foreach (var item in items)
                    Receive(item);
        }));
        _connection.On<double>("Cleared", stamp => _ = Post(() =>
        {
            if (_room is not null)
                ReceiveClear(stamp);
        }));
        _connection.On<int>("Peers", peers => _ = Post(() =>
        {
            if (_room is not null)
                PeersChanged?.Invoke(peers);
        }));

        _connection.Reconnecting += _ => Post(() =>
        {
            Log.Write("Sincronización: conexión perdida, reconectando");
            _joinedRoom = null;
            PeersChanged?.Invoke(0);
        });
        _connection.Reconnected += connectionId => Post(() => _ = SyncAsync());
        _connection.Closed += _ => Post(() =>
        {
            _joinedRoom = null;
            PeersChanged?.Invoke(0);
            if (_room is not null)
                _retry.Start();
        });
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            _ = SyncAsync();
        };
    }

    /// <summary>
    /// Room id for a list of parts (e.g. the ten players of a game plus the team), so everyone
    /// who sees the same game computes the same id without sharing anything.
    /// </summary>
    public static string RoomId(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant();

    /// <summary>Joins a room (forgetting the previous one), or leaves and disconnects with null.</summary>
    public void SetRoom(string? room)
    {
        if (room == _room)
            return;
        _room = room;
        _items.Clear();
        _pending.Clear();
        _clearStamp = null;
        _pendingClear = false;
        PeersChanged?.Invoke(0);
        _ = SyncAsync();
    }

    /// <summary>Shares a local change, unless a newer one for the same key is known.</summary>
    public void Put(SyncItem item)
    {
        if (_room is null || !IsNewer(item))
            return;
        _items[item.Key] = item;
        if (_joinedRoom == _room && _connection.State == HubConnectionState.Connected)
            Send("Put", new List<SyncItem> { item });
        else
            _pending.Add(item.Key);
    }

    /// <summary>Shares a local "clear all": every timer changed at or before the stamp is gone.</summary>
    public void Clear(double stamp)
    {
        if (_room is null || stamp <= _clearStamp)
            return;
        ForgetTimersUpTo(stamp);
        if (_joinedRoom == _room && _connection.State == HubConnectionState.Connected)
            Send("Clear", stamp);
        else
            _pendingClear = true;
    }

    bool IsNewer(SyncItem item) =>
        !(item.Key.StartsWith(TimerPrefix) && item.Stamp < _clearStamp)
        && !(_items.TryGetValue(item.Key, out var current) && item.Stamp < current.Stamp);

    void Receive(SyncItem item)
    {
        if (!IsNewer(item))
            return;
        // Joining sends back what we already have; only actual changes are raised.
        if (_items.TryGetValue(item.Key, out var current) && current.Stamp == item.Stamp
            && current.Data?.GetRawText() == item.Data?.GetRawText())
            return;
        _items[item.Key] = item;
        ItemReceived?.Invoke(item);
    }

    /// <summary>A teammate's "clear all": removes the timers it covers, raising each removal.</summary>
    void ReceiveClear(double stamp)
    {
        if (stamp <= _clearStamp)
            return;
        foreach (var key in ForgetTimersUpTo(stamp))
            ItemReceived?.Invoke(new SyncItem(key, stamp, null));
    }

    List<string> ForgetTimersUpTo(double stamp)
    {
        _clearStamp = stamp;
        var removed = _items.Values
            .Where(i => i.Key.StartsWith(TimerPrefix) && i.Stamp <= stamp)
            .Select(i => i.Key).ToList();
        foreach (var key in removed)
            _items.Remove(key);
        return removed;
    }

    /// <summary>Brings the connection in line with the wanted room; reruns if that changes meanwhile.</summary>
    async Task SyncAsync()
    {
        if (_syncing)
        {
            _resync = true;
            return;
        }

        _syncing = true;
        try
        {
            do
            {
                _resync = false;
                await SyncOnceAsync();
            } while (_resync);
        }
        catch (Exception ex)
        {
            Log.Write($"Sincronización: {ex.GetBaseException().Message}");
            _joinedRoom = null;
            if (_room is not null)
                _retry.Start();
        }
        finally
        {
            _syncing = false;
        }
    }

    async Task SyncOnceAsync()
    {
        if (_room is null)
        {
            _joinedRoom = null;
            if (_connection.State != HubConnectionState.Disconnected)
                await _connection.StopAsync();
            return;
        }

        if (_connection.State == HubConnectionState.Disconnected)
        {
            await _connection.StartAsync();
            Log.Write("Sincronización: conectado");
        }
        // While reconnecting, Reconnected runs this again.
        if (_connection.State != HubConnectionState.Connected || _joinedRoom == _room)
            return;

        // Everything known so far goes with the join; what changes meanwhile is sent after.
        string room = _room;
        _pending.Clear();
        _pendingClear = false;
        var result = await _connection.InvokeAsync<JoinResult>("Join", room, _items.Values.ToList(), _clearStamp);
        if (room != _room)
            return; // SetRoom ran meanwhile and asked for another pass.

        _joinedRoom = room;
        Log.Write("Sincronización: en la sala " + room[..8]);
        if (result.ClearStamp is double stamp)
            ReceiveClear(stamp);
        foreach (var item in result.Items)
            Receive(item);

        if (_pendingClear && _clearStamp is double clear)
            Send("Clear", clear);
        var pending = _pending.Where(_items.ContainsKey).Select(key => _items[key]).ToList();
        if (pending.Count > 0)
            Send("Put", pending);
        _pending.Clear();
        _pendingClear = false;
    }

    async void Send(string method, object argument)
    {
        try
        {
            await _connection.SendAsync(method, argument);
        }
        catch (Exception ex)
        {
            // The rejoin after reconnecting sends the whole state again.
            Log.Write($"Sincronización: no se pudo enviar ({ex.GetBaseException().Message})");
        }
    }

    Task Post(Action action)
    {
        _ui.Post(_ => action(), null);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _retry.Dispose();
        _ = _connection.DisposeAsync();
    }

    /// <summary>Keeps trying to reconnect: quickly at first, then every 30 seconds.</summary>
    sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, context.PreviousRetryCount)));
    }
}
