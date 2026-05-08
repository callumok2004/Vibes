namespace Vibes;

public class TwitchCommand
{
	public CommandType CommandType { get; set; }
	public string Name { get; set; } = "";
	public string Trigger { get; set; } = "";
	public bool IsEnabled { get; set; } = true;
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

	public static string? GetRequester(string trackId) =>
		Pending.FirstOrDefault(r => r.TrackId == trackId)?.Requester;

	public static void MarkPlayed(string trackId) =>
		Pending.RemoveAll(r => r.TrackId == trackId);
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
