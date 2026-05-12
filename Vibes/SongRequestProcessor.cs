namespace Vibes;

public class SongRequestProcessor
{
	public static SongRequestProcessor Instance { get; } = new();

	public bool SrEnabled { get; private set; } = true;

	private DateTime _lastGlobalSr = DateTime.MinValue;
	private readonly Dictionary<string, DateTime> _lastUserSr = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _voteSkipUsers = new(StringComparer.OrdinalIgnoreCase);

	private SongRequestProcessor() { }

	// -- Entry point -----------------------------------------------------------

	public async Task HandleMessageAsync(TwitchChatMessage msg) {
		var cfg = AppConfig.Instance;

		// Check all enabled commands first
		foreach (var cmd in cfg.Commands.Where(c => c.IsEnabled)) {
			var trigger = cmd.Trigger.Trim();
			if (!msg.Message.Equals(trigger, StringComparison.OrdinalIgnoreCase) &&
			    !msg.Message.StartsWith(trigger + " ", StringComparison.OrdinalIgnoreCase))
				continue;

			var level = GetUserLevel(msg);
			if (!cmd.AllowedUserLevels.Contains((int)level)) return;

			var args = msg.Message.Length > trigger.Length
				? msg.Message[(trigger.Length + 1)..].Trim()
				: "";

			await HandleCommandAsync(cmd.CommandType, msg, args);
			return;
		}

		// Song request via chat command
		var mode = cfg.RequestMode;
		if (mode is RequestMode.ChatCommand or RequestMode.Both) {
			var srTrigger = cfg.TwSrCommandTrigger.Trim();
			if (msg.Message.StartsWith(srTrigger, StringComparison.OrdinalIgnoreCase)) {
				var query = msg.Message[srTrigger.Length..].Trim();
				if (!string.IsNullOrEmpty(query))
					await ProcessAsync(msg, query, SongRequestSource.Command);
				return;
			}
		}

		// Song request via channel reward
		if (mode is RequestMode.ChannelReward or RequestMode.Both) {
			if (!string.IsNullOrEmpty(msg.RewardId)) {
				var rewardId = cfg.TwRewardId.Trim();
				if (string.IsNullOrEmpty(rewardId) || rewardId == msg.RewardId) {
					var query = msg.Message.Trim();
					if (!string.IsNullOrEmpty(query))
						await ProcessAsync(msg, query, SongRequestSource.Reward);
				}
			}
		}
	}

	// -- Command dispatch ------------------------------------------------------

	private async Task HandleCommandAsync(CommandType type, TwitchChatMessage msg, string args) {
		var cfg   = AppConfig.Instance;
		var user  = msg.DisplayName;
		var login = msg.Login;

		switch (type) {
			case CommandType.Song: {
				var track = SpotifyService.Instance.CurrentTrack;
				if (track == null) { await ReplyAsync("Nothing is playing right now."); return; }
				var req = SongQueue.Pending.FirstOrDefault(r => r.TrackId == track.TrackId);
				var requester = req != null ? $", requested by @{req.Requester}" : "";
				await ReplyAsync(Format(cfg.BotRespSong, artist: track.Artist, title: track.Title, requester: requester));
				break;
			}
			case CommandType.Next: {
				var next = SongQueue.Pending.FirstOrDefault();
				if (next == null) { await ReplyAsync(cfg.BotRespNoQueue); return; }
				await ReplyAsync(Format(cfg.BotRespNext, user: next.Requester, artist: next.Artist, title: next.Title));
				break;
			}
			case CommandType.Queue: {
				if (cfg.CloudflareQueueEnabled && !string.IsNullOrEmpty(cfg.CloudflareQueueUrl)) {
					await ReplyAsync(cfg.CloudflareQueueUrl);
					return;
				}
				var pending = SongQueue.Pending.Take(5).ToList();
				if (pending.Count == 0) { await ReplyAsync(cfg.BotRespNoQueue); return; }
				var list = string.Join(" | ", pending.Select((r, i) => $"#{i + 1} {r.Artist} - {r.Title}"));
				await ReplyAsync(Format(cfg.BotRespQueue, queue: list));
				break;
			}
			case CommandType.Position: {
				var idx = SongQueue.Pending.FindIndex(r =>
					r.Requester.Equals(user, StringComparison.OrdinalIgnoreCase));
				if (idx < 0) { await ReplyAsync(Format("@{user} You don't have a song in the queue.", user: user)); return; }
				var req = SongQueue.Pending[idx];
				var ttp = SongQueue.Pending.Take(idx)
					.Sum(r => r.DurationMs > 0 ? r.DurationMs : 210_000) / 60_000;
				if (SpotifyService.Instance.CurrentTrack is { } ct)
					ttp += (ct.DurationMs - ct.ProgressMs) / 60_000;
				await ReplyAsync(Format(cfg.BotRespPos, user: user, pos: idx + 1, ttp: (int)ttp));
				break;
			}
			case CommandType.Remove: {
				var req = SongQueue.Pending.FirstOrDefault(r =>
					r.Requester.Equals(user, StringComparison.OrdinalIgnoreCase));
				if (req == null) { await ReplyAsync(Format("@{user} You don't have a song in the queue.", user: user)); return; }
				SongQueue.Pending.Remove(req);
				await ReplyAsync(Format(cfg.BotRespRemove, user: user));
				break;
			}
			case CommandType.Skip: {
				await SpotifyService.Instance.SkipAsync();
				await ReplyAsync(Format(cfg.BotRespSkip, user: user));
				break;
			}
			case CommandType.Voteskip: {
				_voteSkipUsers.Add(login);
				var needed = cfg.VoteSkipCount;
				var votes  = _voteSkipUsers.Count;
				if (votes >= needed) {
					_voteSkipUsers.Clear();
					await SpotifyService.Instance.SkipAsync();
					await ReplyAsync(Format(cfg.BotRespSkip, user: user));
				} else {
					await ReplyAsync(Format(cfg.BotRespVoteSkip, user: user, votes: votes, needed: needed));
				}
				break;
			}
			case CommandType.Songlike: {
				var track = SpotifyService.Instance.CurrentTrack;
				if (track == null) return;
				await SpotifyService.Instance.LikeCurrentTrackAsync();
				await ReplyAsync(Format(cfg.BotRespSongLike, user: user));
				break;
			}
			case CommandType.BanSong: {
				var track = SpotifyService.Instance.CurrentTrack;
				if (track == null) return;
				if (!cfg.BlockedSongs.Contains(track.Title, StringComparer.OrdinalIgnoreCase))
					cfg.BlockedSongs.Add(track.Title);
				AppConfig.Save();
				await SpotifyService.Instance.SkipAsync();
				await ReplyAsync($"Banned and skipped: {track.Artist} - {track.Title}");
				break;
			}
			case CommandType.ToggleSr: {
				SrEnabled = !SrEnabled;
				await ReplyAsync(Format(cfg.BotRespToggleSr, state: SrEnabled ? "enabled" : "disabled"));
				break;
			}
			case CommandType.Commands: {
				var triggers = cfg.Commands.Where(c => c.IsEnabled).Select(c => c.Trigger);
				await ReplyAsync("Commands: " + string.Join(", ", triggers));
				break;
			}
		}
	}

	// -- Core validation + queue -----------------------------------------------

	private async Task ProcessAsync(TwitchChatMessage msg, string query, SongRequestSource source) {
		var cfg   = AppConfig.Instance;
		var user  = msg.DisplayName;
		var login = msg.Login;

		if (!SrEnabled) return;

		bool isReward = source == SongRequestSource.Reward;

		async Task Refund(RefundCondition cond) {
			if (!isReward || !cfg.AutoManageRedemptions || !cfg.RefundConditions.Contains(cond)) return;
			AppLogger.Instance.Information($"Refunding redemption for {user}: {cond}");
			await TwitchService.Instance.UpdateRedemptionAsync(msg.RewardId, msg.RedemptionId, fulfill: false);
		}

		async Task Fulfill() {
			if (!isReward || !cfg.AutoManageRedemptions) return;
			AppLogger.Instance.Information($"Fulfilling redemption for {user}");
			await TwitchService.Instance.UpdateRedemptionAsync(msg.RewardId, msg.RedemptionId, fulfill: true);
		}

		// Blocked user
		if (cfg.BlockedUsers.Any(u => u.Equals(login, StringComparison.OrdinalIgnoreCase))) {
			await ReplyAsync(Format(cfg.BotRespBlacklist, user: user));
			await Refund(RefundCondition.UserBlocked);
			return;
		}

		// User level
		var level = GetUserLevel(msg);
		if ((int)level < cfg.TwSrUserLevel) {
			await ReplyAsync(Format(cfg.BotRespLevelTooLow, user: user,
				level: LevelName((TwitchUserLevel)cfg.TwSrUserLevel)));
			await Refund(RefundCondition.UserLevelTooLow);
			return;
		}

		var now = DateTime.Now;

		// Global cooldown
		if (cfg.TwSrCooldown > 0) {
			var remaining = cfg.TwSrCooldown - (now - _lastGlobalSr).TotalSeconds;
			if (remaining > 0) {
				await ReplyAsync(Format(cfg.BotRespCooldown, user: user, cd: (int)Math.Ceiling(remaining)));
				await Refund(RefundCondition.Cooldown);
				return;
			}
		}

		// Per-user cooldown
		if (cfg.TwSrPerUserCooldown > 0 && _lastUserSr.TryGetValue(login, out var lastUser)) {
			var remaining = cfg.TwSrPerUserCooldown - (now - lastUser).TotalSeconds;
			if (remaining > 0) {
				await ReplyAsync(Format(cfg.BotRespUserCooldown, user: user, cd: (int)Math.Ceiling(remaining)));
				await Refund(RefundCondition.UserCooldown);
				return;
			}
		}

		// Per-user max requests
		var maxReq = GetMaxRequests(level, cfg);
		if (maxReq > 0) {
			var count = SongQueue.Pending.Count(r =>
				r.Requester.Equals(user, StringComparison.OrdinalIgnoreCase) && !r.IsPlayed);
			if (count >= maxReq) {
				await ReplyAsync(Format(cfg.BotRespMaxReq, user: user, max: maxReq));
				await Refund(RefundCondition.MaxRequestsReached);
				return;
			}
		}

		// Queue length cap
		if (cfg.MaxQueueLength > 0 && SongQueue.Pending.Count(r => !r.IsPlayed) >= cfg.MaxQueueLength) {
			await ReplyAsync(Format(cfg.BotRespQueueFull, user: user, max: cfg.MaxQueueLength));
			await Refund(RefundCondition.QueueLimitReached);
			return;
		}

		// Resolve query - URL/URI or search term
		var trackId = ExtractSpotifyTrackId(query);
		SpotifyTrackInfo? track;
		if (trackId != null)
			track = await SpotifyService.Instance.GetTrackAsync(trackId);
		else
			track = await SpotifyService.Instance.SearchTrackAsync(query);

		if (track == null || string.IsNullOrEmpty(track.TrackId)) {
			await ReplyAsync(Format(cfg.BotRespNoSong, user: user));
			await Refund(RefundCondition.SongUnavailable);
			return;
		}

		// Blocked artist / song
		if (cfg.BlockedArtists.Any(a => track.Artist.Contains(a, StringComparison.OrdinalIgnoreCase)) ||
		    cfg.BlockedSongs.Any(s => track.Title.Contains(s, StringComparison.OrdinalIgnoreCase))) {
			await ReplyAsync(Format(cfg.BotRespBlacklist, user: user));
			await Refund(RefundCondition.SongBlocked);
			return;
		}

		// Explicit
		if (cfg.BlockAllExplicitSongs && track.IsExplicit) {
			await ReplyAsync(Format(cfg.BotRespExplicit, user: user));
			await Refund(RefundCondition.TrackIsExplicit);
			return;
		}

		// Song length
		if (cfg.MaxSongLength > 0 && track.DurationMs > cfg.MaxSongLength * 60_000) {
			await ReplyAsync(Format(cfg.BotRespTooLong, user: user, max: cfg.MaxSongLength));
			await Refund(RefundCondition.SongTooLong);
			return;
		}

		// Already queued
		if (SongQueue.Pending.Any(r => r.TrackId == track.TrackId && !r.IsPlayed)) {
			await ReplyAsync(Format(cfg.BotRespIsInQueue, user: user));
			await Refund(RefundCondition.SongAlreadyInQueue);
			return;
		}

		// Add to Spotify queue
		var ok = await SpotifyService.Instance.AddToQueueAsync(track.TrackId);
		if (!ok) {
			await ReplyAsync(Format(cfg.BotRespError, user: user));
			await Refund(RefundCondition.SongUnavailable);
			return;
		}

		SongQueue.Pending.Add(new RequestObject {
			TrackId     = track.TrackId,
			Title       = track.Title,
			Artist      = track.Artist,
			AlbumCover  = track.AlbumArt,
			DurationMs  = track.DurationMs,
			Requester   = user,
			Source      = source,
		});

		_lastGlobalSr      = now;
		_lastUserSr[login] = now;

		var pos = SongQueue.Pending.Count(r => !r.IsPlayed);
		await ReplyAsync(Format(cfg.BotRespSuccess,
			user: user, artist: track.Artist, title: track.Title, pos: pos));

		AppLogger.Instance.Information($"SR: {user} -> {track.Artist} - {track.Title} (#{pos})");
		await Fulfill();
	}

	// -- Helpers ---------------------------------------------------------------

	private static Task ReplyAsync(string msg) =>
		AppConfig.Instance.AnnounceInChat
			? TwitchService.Instance.SendMessageAsync(msg)
			: Task.CompletedTask;

	private static TwitchUserLevel GetUserLevel(TwitchChatMessage msg) {
		var badges = msg.Tags.GetValueOrDefault("badges") ?? "";
		if (badges.Contains("broadcaster"))     return TwitchUserLevel.Broadcaster;
		if (badges.Contains("moderator"))       return TwitchUserLevel.Moderator;
		if (badges.Contains("vip"))             return TwitchUserLevel.Vip;
		if (badges.Contains("subscriber/3000")) return TwitchUserLevel.SubscriberT3;
		if (badges.Contains("subscriber/2000")) return TwitchUserLevel.SubscriberT2;
		if (badges.Contains("subscriber"))      return TwitchUserLevel.Subscriber;
		return TwitchUserLevel.Viewer;
	}

	private static int GetMaxRequests(TwitchUserLevel level, AppConfig cfg) => level switch {
		TwitchUserLevel.Broadcaster  => cfg.TwSrMaxReqBroadcaster,
		TwitchUserLevel.Moderator    => cfg.TwSrMaxReqModerator,
		TwitchUserLevel.Vip          => cfg.TwSrMaxReqVip,
		TwitchUserLevel.SubscriberT3 => cfg.TwSrMaxReqSubscriberT3,
		TwitchUserLevel.SubscriberT2 => cfg.TwSrMaxReqSubscriberT2,
		TwitchUserLevel.Subscriber   => cfg.TwSrMaxReqSubscriber,
		TwitchUserLevel.Follower     => cfg.TwSrMaxReqFollower,
		_                            => cfg.TwSrMaxReqViewer,
	};

	private static string LevelName(TwitchUserLevel level) => level switch {
		TwitchUserLevel.Broadcaster  => "Broadcaster",
		TwitchUserLevel.Moderator    => "Moderator",
		TwitchUserLevel.Vip          => "VIP",
		TwitchUserLevel.SubscriberT3 => "Tier 3 Subscriber",
		TwitchUserLevel.SubscriberT2 => "Tier 2 Subscriber",
		TwitchUserLevel.Subscriber   => "Subscriber",
		TwitchUserLevel.Follower     => "Follower",
		_                            => "Viewer",
	};

	private static string? ExtractSpotifyTrackId(string query) {
		var q = query.Trim();
		if (q.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
			return q["spotify:track:".Length..].Split('?')[0].Trim();
		if (q.StartsWith("http", StringComparison.OrdinalIgnoreCase)) {
			if (!Uri.TryCreate(q, UriKind.Absolute, out var uri)) return null;
			var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length >= 2 && segments[^2].Equals("track", StringComparison.OrdinalIgnoreCase))
				return segments[^1];
			return null;
		}
		return null;
	}

	private static string Format(string template,
		string user = "", string artist = "", string title = "", string queue = "",
		string state = "", string level = "", string requester = "",
		int pos = 0, int cd = 0, int max = 0, int ttp = 0, int votes = 0, int needed = 0) {
		return template
			.Replace("{user}",      user)
			.Replace("{artist}",    artist)
			.Replace("{title}",     title)
			.Replace("{song}",      string.IsNullOrEmpty(artist) ? title : $"{artist} - {title}")
			.Replace("{queue}",     queue)
			.Replace("{state}",     state)
			.Replace("{level}",     level)
			.Replace("{requester}", requester)
			.Replace("{pos}",       pos.ToString())
			.Replace("{cd}",        cd.ToString())
			.Replace("{max}",       max.ToString())
			.Replace("{ttp}",       ttp.ToString())
			.Replace("{votes}",     votes.ToString())
			.Replace("{needed}",    needed.ToString());
	}
}
