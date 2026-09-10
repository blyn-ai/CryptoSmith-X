using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Live;

/// <summary>
/// The rooms this process is running, one per asset — created by the first person to watch an asset
/// and destroyed by the last one to leave it.
///
/// The reference counting lives here rather than inside <see cref="LiveRoom"/> because it is about
/// the DICTIONARY: a room that stopped its own timer but stayed in the map would be handed to the
/// next viewer as a room that never ticks, and nothing would say so. Joining and leaving therefore
/// happen under the same lock as the map itself.
/// </summary>
public sealed class LiveRooms : IDisposable
{
    private readonly PairPageLoad _load;
    private readonly HubStream _hub;
    private readonly LiveNotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LiveRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public LiveRooms(
        PairPageLoad load,
        HubStream hub,
        LiveNotifier notifier,
        TimeProvider clock,
        ILoggerFactory loggers)
    {
        _load = load;
        _hub = hub;
        _notifier = notifier;
        _clock = clock;
        _loggers = loggers;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _rooms.Count;
            }
        }
    }

    /// <summary>The room for this asset, made if nobody was watching it yet.</summary>
    public LiveRoom Room(string baseFamily)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(baseFamily, out var room))
            {
                _rooms[baseFamily] = room = new LiveRoom(
                    baseFamily, _load, _hub, _notifier, _clock, _loggers.CreateLogger<LiveRoom>());
            }

            return room;
        }
    }

    /// <summary>Stops watching, and closes the room when that was the last viewer. Called from a
    /// finally: a reader who closed the laptop lid must not leave a room ticking for the life of the
    /// process.</summary>
    public void Leave(LiveRoom room, System.Threading.Channels.ChannelReader<LiveFrame> reader)
    {
        lock (_gate)
        {
            room.Leave(reader);
            if (room.Viewers == 0 && _rooms.TryGetValue(room.BaseFamily, out var held) && ReferenceEquals(held, room))
            {
                _rooms.Remove(room.BaseFamily);
                room.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var room in _rooms.Values)
            {
                room.Dispose();
            }

            _rooms.Clear();
        }
    }
}
