namespace Vibes;

public class TwitchCommand
{
	public CommandType CommandType { get; set; }
	public string Name { get; set; } = "";
	public string Trigger { get; set; } = "";
	public List<string> Aliases { get; set; } = [];
	public bool IsEnabled { get; set; } = true;
	public int CooldownSeconds { get; set; } = 0;
	public List<int> AllowedUserLevels { get; set; } = [0, 1, 2, 3, 4, 5, 6, 7];

	public static List<TwitchCommand> Defaults() => [
		new() { CommandType = CommandType.Song,      Name = "Now Playing",    Trigger = "!song",     IsEnabled = true  },
		new() { CommandType = CommandType.Next,      Name = "Next Song",      Trigger = "!next",     IsEnabled = true  },
		new() { CommandType = CommandType.Queue,     Name = "Queue",          Trigger = "!queue",    IsEnabled = true  },
		new() { CommandType = CommandType.Position,  Name = "Queue Position", Trigger = "!pos",      IsEnabled = false  },
		new() { CommandType = CommandType.Remove,    Name = "Remove Request", Trigger = "!remove",   IsEnabled = false  },
		new() { CommandType = CommandType.Skip,      Name = "Skip",           Trigger = "!skip",     IsEnabled = false,  AllowedUserLevels = [6, 7] },
		new() { CommandType = CommandType.Voteskip,  Name = "Vote Skip",      Trigger = "!voteskip", IsEnabled = false, AllowedUserLevels = [0, 1, 2, 3, 4, 5, 6, 7] },
		new() { CommandType = CommandType.Songlike,  Name = "Like Song",      Trigger = "!like",     IsEnabled = false  },
		new() { CommandType = CommandType.Volume,    Name = "Volume",         Trigger = "!vol",      IsEnabled = false, AllowedUserLevels = [6, 7] },
		new() { CommandType = CommandType.PlayPause, Name = "Play/Pause",     Trigger = "!playpause",IsEnabled = false, AllowedUserLevels = [6, 7] },
		new() { CommandType = CommandType.Commands,  Name = "Commands List",  Trigger = "!commands", IsEnabled = false  },
		new() { CommandType = CommandType.BanSong,   Name = "Ban Song",       Trigger = "!bansong",  IsEnabled = false, AllowedUserLevels = [6, 7] },
		new() { CommandType = CommandType.ToggleSr,  Name = "Toggle SR",      Trigger = "!togglesr", IsEnabled = false,  AllowedUserLevels = [6, 7] },
	];
}

public static class SongQueue
{
	// In-memory list of pending requests - populated by Twitch commands/rewards.
	// Matched against Spotify's queue by TrackId to show requester badges.
	public static List<RequestObject> Pending { get; } = [];

	private static readonly Lock _lock = new();
	private static readonly Dictionary<string, int> _missStrikes = [];

	// Spotify's queue endpoint only returns a limited upcoming horizon; if the
	// snapshot is at least this long, a missing track may just be beyond it.
	private const int SpotifyQueueHorizon = 20;
	private const int MissesBeforeDrop = 2;
	// Never prune a request younger than this - Spotify's queue can lag a few
	// seconds behind a song we just added.
	private const int GraceSeconds = 20;

	public static void Add(RequestObject request) {
		lock (_lock) {
			Pending.Add(request);
			_missStrikes.Remove(request.TrackId);
		}
	}

	public static string? GetRequester(string trackId) {
		lock (_lock)
			return Pending.FirstOrDefault(r => r.TrackId == trackId)?.Requester;
	}

	public static void MarkPlayed(string trackId) {
		lock (_lock) {
			Pending.RemoveAll(r => r.TrackId == trackId);
			_missStrikes.Remove(trackId);
		}
	}

	// Drop pending requests that are no longer in Spotify's queue (e.g. the
	// streamer removed them directly in Spotify), while tolerating the queue's
	// limited horizon and Spotify's eventual consistency.
	public static void Reconcile(List<SpotifyTrackInfo> liveQueue, string? currentTrackId) {
		bool horizonTruncated = liveQueue.Count >= SpotifyQueueHorizon;
		var live = new HashSet<string>(liveQueue.Select(t => t.TrackId), StringComparer.Ordinal);
		if (!string.IsNullOrEmpty(currentTrackId)) live.Add(currentTrackId);

		lock (_lock) {
			foreach (var r in Pending.Where(r => !r.IsPlayed).ToList()) {
				if (live.Contains(r.TrackId)) { _missStrikes.Remove(r.TrackId); continue; }
				if (horizonTruncated) continue;
				if ((DateTime.Now - r.RequestedAt).TotalSeconds < GraceSeconds) continue;

				var misses = _missStrikes.GetValueOrDefault(r.TrackId) + 1;
				if (misses >= MissesBeforeDrop) {
					Pending.Remove(r);
					_missStrikes.Remove(r.TrackId);
					AppLogger.Instance.Information(
						$"Removed stale request (gone from Spotify queue): {r.Artist} - {r.Title} ({r.Requester})");
				}
				else _missStrikes[r.TrackId] = misses;
			}
		}
	}
}

public class RequestObject
{
	public string TrackId { get; set; } = "";
	public string Artist { get; set; } = "";
	public string Title { get; set; } = "";
	public string AlbumCover { get; set; } = "";
	public string Length { get; set; } = "";
	public string Requester { get; set; } = "";
	public int DurationMs { get; set; }
	public SongRequestSource Source { get; set; }
	public DateTime RequestedAt { get; set; } = DateTime.Now;
	public bool IsPlayed { get; set; } = false;

	public string Display => $"{Artist} - {Title}";
}
