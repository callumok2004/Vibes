using System.Text.Json;

namespace Vibes;

public class AppConfig
{
	public static AppConfig Instance { get; private set; } = new();

	// -- TWITCH ----------------------------------------------------------------
	public string TwitchChannel { get; set; } = "";
	public string BotAccountName { get; set; } = "";
	public bool UseBotAccount { get; set; } = false;
	public bool TwAutoConnect { get; set; } = true;
	public bool AnnounceInChat { get; set; } = true;

	// -- SPOTIFY ---------------------------------------------------------------
	public string SpotifyClientId { get; set; } = "";
	public string SpotifyClientSecret { get; set; } = "";
	public string SpotifyPlaylistId { get; set; } = "";
	public int SpotifyFetchRate { get; set; } = 2;
	public int SpotifyCallbackPort { get; set; } = 8888;
	public bool AddSrToPlaylist { get; set; } = false;
	public bool LimitSrToPlaylist { get; set; } = false;

	// -- SONG REQUESTS ---------------------------------------------------------
	public RequestMode RequestMode { get; set; } = RequestMode.ChatCommand;
	public bool TwSrCommand { get; set; } = true;
	public bool TwSrReward { get; set; } = false;
	public string TwSrCommandTrigger { get; set; } = "!sr";
	public string TwRewardId { get; set; } = "";
	public int TwSrUserLevel { get; set; } = (int)TwitchUserLevel.Viewer;
	public int TwSrCooldown { get; set; } = 5;
	public int TwSrPerUserCooldown { get; set; } = 0;
	public int MaxSongLength { get; set; } = 10;
	public int MaxQueueLength { get; set; } = 0;
	public bool BlockAllExplicitSongs { get; set; } = false;

	// Per user-level max requests (0 = unlimited)
	public int TwSrMaxReqViewer { get; set; } = 3;
	public int TwSrMaxReqFollower { get; set; } = 3;
	public int TwSrMaxReqSubscriber { get; set; } = 5;
	public int TwSrMaxReqSubscriberT2 { get; set; } = 5;
	public int TwSrMaxReqSubscriberT3 { get; set; } = 10;
	public int TwSrMaxReqVip { get; set; } = 5;
	public int TwSrMaxReqModerator { get; set; } = 0;
	public int TwSrMaxReqBroadcaster { get; set; } = 0;

	// Vote skip threshold
	public int VoteSkipCount { get; set; } = 5;

	public bool AutoManageRedemptions { get; set; } = true;

	// Refund channel points on these conditions
	public List<RefundCondition> RefundConditions { get; set; } = [
		RefundCondition.UserLevelTooLow,
		RefundCondition.UserBlocked,
		RefundCondition.SongUnavailable,
		RefundCondition.SongTooLong,
		RefundCondition.TrackIsExplicit,
		RefundCondition.SongBlocked,
		RefundCondition.QueueLimitReached,
		RefundCondition.Cooldown,
		RefundCondition.UserCooldown,
		RefundCondition.MaxRequestsReached,
	];

	// -- BOT RESPONSES ---------------------------------------------------------
	// Placeholders: {user} {artist} {title} {song} {pos} {ttp} {cd} {max} {level} {votes} {needed} {state}
	public string BotRespSuccess { get; set; } = "@{user} - {artist} - {title} added to queue at #{pos}!";
	public string BotRespError { get; set; } = "@{user} Something went wrong adding that song.";
	public string BotRespNoSong { get; set; } = "@{user} No song found for that search.";
	public string BotRespBlacklist { get; set; } = "@{user} That artist or song is blocked.";
	public string BotRespExplicit { get; set; } = "@{user} Explicit songs are not allowed.";
	public string BotRespTooLong { get; set; } = "@{user} Song is too long (max {max} min).";
	public string BotRespCooldown { get; set; } = "@{user} Requests are on cooldown. Try again in {cd}s.";
	public string BotRespUserCooldown { get; set; } = "@{user} You can request again in {cd}s.";
	public string BotRespMaxReq { get; set; } = "@{user} You've reached your max of {max} requests.";
	public string BotRespQueueFull { get; set; } = "@{user} The queue is full ({max} songs).";
	public string BotRespIsInQueue { get; set; } = "@{user} That song is already in the queue.";
	public string BotRespLevelTooLow { get; set; } = "@{user} You need to be at least {level} to request songs.";
	public string BotRespSong { get; set; } = "Now playing: {artist} - {title}{requester}";
	public string BotRespNext { get; set; } = "Up next: {artist} - {title} (requested by @{user})";
	public string BotRespPos { get; set; } = "@{user} Your song is at position #{pos} (~{ttp} min).";
	public string BotRespQueue { get; set; } = "Queue: {queue}";
	public string BotRespRemove { get; set; } = "@{user} Removed your request from the queue.";
	public string BotRespNoQueue { get; set; } = "The queue is empty.";
	public string BotRespSkip { get; set; } = "Skipped by @{user}.";
	public string BotRespVoteSkip { get; set; } = "@{user} voted to skip! ({votes}/{needed})";
	public string BotRespSongLike { get; set; } = "@{user} Added to liked songs!";
	public string BotRespToggleSr { get; set; } = "Song requests are now {state}.";

	// -- COMMANDS --------------------------------------------------------------
	public List<TwitchCommand> Commands { get; set; } = TwitchCommand.Defaults();

	// -- BLOCKLISTS ------------------------------------------------------------
	public List<string> BlockedArtists { get; set; } = [];
	public List<string> BlockedSongs { get; set; } = [];
	public List<string> BlockedUsers { get; set; } = [];

	// -- APP SETTINGS ----------------------------------------------------------
	public bool StartWithWindows { get; set; } = false;
	public bool MinimizeToTray { get; set; } = true;

	// -------------------------------------------------------------------------

	static readonly string ConfigDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibes");
	static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

	public static void Load() {
		if (!File.Exists(ConfigPath)) return;
		try {
			var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
			if (loaded == null) return;
			// Ensure commands list is always populated with defaults for any missing command types
			if (loaded.Commands.Count == 0)
				loaded.Commands = TwitchCommand.Defaults();
			Instance = loaded;
		}
		catch {
			Instance = new();
		}
	}

	public static void Save() {
		Directory.CreateDirectory(ConfigDir);
		File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Instance, JsonOpts));
	}

	static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}
