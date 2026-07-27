using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Vibes;

public class TwitchChatMessage
{
	public string Login       { get; set; } = "";
	public string DisplayName { get; set; } = "";
	public string Color       { get; set; } = "";
	public string Message     { get; set; } = "";
	public string RewardId      { get; set; } = "";
	public string RedemptionId  { get; set; } = "";
	public Dictionary<string, string> Tags { get; set; } = [];
}

public class CustomReward
{
	public string Id        { get; set; } = "";
	public string Title     { get; set; } = "";
	public int    Cost      { get; set; }
	public string Prompt    { get; set; } = "";
	public bool   IsEnabled { get; set; }
}

// Result of a reward operation - carries a user-facing message and, on success, the reward.
public class RewardResult
{
	public bool           Success { get; init; }
	public string         Message { get; init; } = "";
	public CustomReward?  Reward  { get; init; }

	public static RewardResult Ok(CustomReward r, string msg)  => new() { Success = true,  Reward = r, Message = msg };
	public static RewardResult Fail(string msg)                => new() { Success = false, Message = msg };
}

public class TwitchService
{
	public static TwitchService Instance { get; } = new();

	public event Action<string>?            StatusChanged;
	public event Action<TwitchChatMessage>? MessageReceived;

	public bool IsConnected     { get; private set; }
	public bool IsAuthorized    => !string.IsNullOrEmpty(Credentials.Instance.TwitchAccessToken);
	public bool IsBotAuthorized => !string.IsNullOrEmpty(Credentials.Instance.TwitchBotAccessToken);

	// Main channel connection (read)
	private ClientWebSocket?         _ws;
	private CancellationTokenSource? _cts;

	// Bot connection (send only)
	private ClientWebSocket?         _botWs;
	private CancellationTokenSource? _botCts;

	private static readonly HttpClient _http = new();
	private string _channel = "";

	// Follow status is not in the IRC tags, so it has to be looked up via Helix.
	// Cached per user id; misses are re-checked after a short window so a fresh
	// follow is picked up without hammering the API.
	private readonly Dictionary<string, (bool IsFollower, DateTime CheckedAt)> _followerCache = [];
	private static readonly TimeSpan FollowerCacheTtl = TimeSpan.FromMinutes(5);
	private bool _followerScopeMissing;

	private const string ChannelClientId = "v6jcyt4gcec7vl8luchszezpwnnkip";
	private const string BotClientId     = "uu3ymsw69n8xoz6evskj2ycwt9evr8";
	private const int    ChannelPort     = 7777;
	private const int    BotPort         = 7778;

	private TwitchService() { }

	// -- Auth ------------------------------------------------------------------

	public async Task AuthorizeAsync() {
		StatusChanged?.Invoke("Opening browser for authorization…");
		var token = await ImplicitGrantAsync(ChannelClientId, ChannelPort,
			"chat:read chat:edit channel:read:redemptions channel:manage:redemptions moderator:read:followers");
		_followerScopeMissing = false;
		_followerCache.Clear();
		Credentials.Instance.TwitchAccessToken    = token;
		Credentials.Instance.TwitchBroadcasterId = "";
		Credentials.Save();
		StatusChanged?.Invoke("Authorized");
	}

	public async Task AuthorizeBotAsync() {
		StatusChanged?.Invoke("Opening browser for bot authorization…");
		var token = await ImplicitGrantAsync(BotClientId, BotPort,
			"chat:read chat:edit");
		Credentials.Instance.TwitchBotAccessToken = token;
		Credentials.Save();
		StatusChanged?.Invoke("Bot authorized");
	}

	private static async Task<string> ImplicitGrantAsync(string clientId, int port, string scopes) {
		var redirectUri = $"http://localhost:{port}/callback/";
		var authUrl = "https://id.twitch.tv/oauth2/authorize" +
			$"?client_id={Uri.EscapeDataString(clientId)}" +
			"&response_type=token" +
			$"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
			$"&scope={Uri.EscapeDataString(scopes)}";

		System.Diagnostics.Process.Start(
			new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });

		return await ListenForImplicitTokenAsync(port);
	}

	private static async Task<string> ListenForImplicitTokenAsync(int port) {
		using var listener = new HttpListener();
		listener.Prefixes.Add($"http://localhost:{port}/callback/");
		listener.Start();

		var ctx1 = await listener.GetContextAsync();
		var fragmentJs = Encoding.UTF8.GetBytes(
			"<html><head><style>body{font-family:sans-serif;background:#0e0e10;color:#efeff1;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}</style></head>" +
			"<body><div><h2 style='color:#9146ff'>Vibes</h2><p>Completing authorization…</p></div>" +
			"<script>if(location.hash){location.replace(location.pathname+'?'+location.hash.substring(1));}else{document.body.innerHTML='<div><h2 style=\"color:#9146ff\">Error</h2><p>No token received.</p></div>';}</script>" +
			"</body></html>");
		ctx1.Response.ContentType     = "text/html";
		ctx1.Response.ContentLength64 = fragmentJs.Length;
		await ctx1.Response.OutputStream.WriteAsync(fragmentJs);
		ctx1.Response.Close();

		var ctx2  = await listener.GetContextAsync();
		var query = ctx2.Request.Url?.Query.TrimStart('?') ?? "";
		var token = ParseQueryParam(query, "access_token");

		var done = Encoding.UTF8.GetBytes(
			"<html><head><style>body{font-family:sans-serif;background:#0e0e10;color:#efeff1;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}</style></head>" +
			"<body><div><h2 style='color:#9146ff'>Vibes</h2><p>Twitch connected - you can close this tab.</p></div></body></html>");
		ctx2.Response.ContentType     = "text/html";
		ctx2.Response.ContentLength64 = done.Length;
		await ctx2.Response.OutputStream.WriteAsync(done);
		ctx2.Response.Close();
		listener.Stop();

		if (string.IsNullOrEmpty(token))
			throw new Exception("No access token received from Twitch.");
		return token;
	}

	public async Task RefreshTokenAsync(bool bot = false) {
		if (bot) await AuthorizeBotAsync();
		else     await AuthorizeAsync();
	}

	// -- Connect / disconnect --------------------------------------------------

	public async Task ConnectAsync() {
		var cfg    = AppConfig.Instance;
		_channel   = cfg.TwitchChannel.Trim().ToLower();

		if (string.IsNullOrEmpty(_channel))
			throw new InvalidOperationException("Twitch channel not set.");
		if (string.IsNullOrEmpty(Credentials.Instance.TwitchAccessToken))
			throw new InvalidOperationException("Not authorized - connect via the Twitch button first.");

		// Connect main channel account (reads all chat)
		_cts?.Cancel();
		_ws?.Dispose();
		_cts = new CancellationTokenSource();
		_ws  = new ClientWebSocket();

		StatusChanged?.Invoke("Connecting…");
		await _ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), _cts.Token);
		await SendRawAsync(_ws, $"PASS oauth:{Credentials.Instance.TwitchAccessToken.Trim()}");
		await SendRawAsync(_ws, $"NICK {_channel}");
		await SendRawAsync(_ws, "CAP REQ :twitch.tv/membership twitch.tv/tags twitch.tv/commands");
		await SendRawAsync(_ws, $"JOIN #{_channel}");
		AppLogger.Instance.Information($"Twitch IRC connected as {_channel} in #{_channel}");

		await ConnectBotAsync();
		_ = FetchBroadcasterIdAsync();

		IsConnected = true;
		StatusChanged?.Invoke("Connected");
		_ = ReadLoopAsync(_cts.Token);
	}

	private async Task ConnectBotAsync() {
		var cfg     = AppConfig.Instance;
		var botNick = cfg.BotAccountName.Trim().ToLower();
		if (!cfg.UseBotAccount ||
		    string.IsNullOrEmpty(Credentials.Instance.TwitchBotAccessToken) ||
		    string.IsNullOrEmpty(botNick)) return;

		_botCts?.Cancel();
		_botWs?.Dispose();
		_botCts = new CancellationTokenSource();
		_botWs  = new ClientWebSocket();
		await _botWs.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), _botCts.Token);
		await SendRawAsync(_botWs, $"PASS oauth:{Credentials.Instance.TwitchBotAccessToken.Trim()}");
		await SendRawAsync(_botWs, $"NICK {botNick}");
		await SendRawAsync(_botWs, $"JOIN #{_channel}");
		AppLogger.Instance.Information($"Twitch bot connected as {botNick}");
		_ = BotPingLoopAsync(_botCts.Token);
	}

	public async Task ApplyBotToggleAsync() {
		if (!IsConnected) return;
		if (AppConfig.Instance.UseBotAccount)
			await ConnectBotAsync();
		else {
			_botCts?.Cancel();
			_botWs?.Dispose();
			_botWs = null;
		}
	}

	// -- Helix API ---------------------------------------------------------------

	private async Task FetchBroadcasterIdAsync() {
		if (!string.IsNullOrEmpty(Credentials.Instance.TwitchBroadcasterId)) return;
		try {
			using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credentials.Instance.TwitchAccessToken.Trim());
			req.Headers.Add("Client-Id", ChannelClientId);
			var resp = await _http.SendAsync(req);
			if (!resp.IsSuccessStatusCode) return;
			var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
			var id   = json.GetProperty("data")[0].GetProperty("id").GetString() ?? "";
			Credentials.Instance.TwitchBroadcasterId = id;
			Credentials.Save();
			AppLogger.Instance.Information($"Broadcaster ID: {id}");
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Failed to fetch broadcaster ID: {ex.Message}");
		}
	}

	// Returns false (i.e. treat as a plain viewer) whenever follow status can't be
	// determined - missing scope on an older token, no broadcaster id yet, or an API
	// error. That matches the pre-follower-support behaviour, so nothing breaks.
	public async Task<bool> IsFollowerAsync(string userId) {
		if (_followerScopeMissing || string.IsNullOrEmpty(userId)) return false;

		var broadcasterId = Credentials.Instance.TwitchBroadcasterId;
		if (string.IsNullOrEmpty(broadcasterId)) return false;
		if (userId == broadcasterId) return true;

		if (_followerCache.TryGetValue(userId, out var cached) &&
		    (cached.IsFollower || DateTime.Now - cached.CheckedAt < FollowerCacheTtl))
			return cached.IsFollower;

		try {
			using var req = new HttpRequestMessage(HttpMethod.Get,
				"https://api.twitch.tv/helix/channels/followers" +
				$"?broadcaster_id={broadcasterId}&user_id={userId}");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credentials.Instance.TwitchAccessToken.Trim());
			req.Headers.Add("Client-Id", ChannelClientId);
			var resp = await _http.SendAsync(req);

			if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
				_followerScopeMissing = true;
				AppLogger.Instance.Warning(
					"Follower lookups disabled: the Twitch token is missing the 'moderator:read:followers' scope. " +
					"Re-authorize Twitch to enable the Follower user level.");
				return false;
			}
			if (!resp.IsSuccessStatusCode) {
				AppLogger.Instance.Warning($"Follower lookup failed ({(int)resp.StatusCode}) for user {userId}");
				return false;
			}

			var json     = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
			var follows  = json.TryGetProperty("data", out var data) && data.GetArrayLength() > 0;
			_followerCache[userId] = (follows, DateTime.Now);
			return follows;
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Follower lookup failed for user {userId}: {ex.Message}");
			return false;
		}
	}

	// IRC only exposes the chat message id, never the redemption id, so we look the
	// redemption up by reward + user (+ input) among the channel's UNFULFILLED redemptions.
	public async Task ManageRedemptionAsync(string rewardId, string userLogin, string userInput, bool fulfill) {
		if (string.IsNullOrEmpty(rewardId)) return;
		var redemptionId = await FindUnfulfilledRedemptionIdAsync(rewardId, userLogin, userInput);
		if (string.IsNullOrEmpty(redemptionId)) {
			AppLogger.Instance.Warning(
				$"Couldn't find an unfulfilled redemption for {userLogin} to {(fulfill ? "fulfill" : "refund")}. " +
				"The reward must be one created/owned by Vibes.");
			return;
		}
		await UpdateRedemptionAsync(rewardId, redemptionId, fulfill);
	}

	private async Task<string?> FindUnfulfilledRedemptionIdAsync(string rewardId, string userLogin, string userInput) {
		try {
			if (!IsAuthorized) return null;
			var broadcasterId = BroadcasterIdOrThrow();
			using var req = RewardRequest(HttpMethod.Get,
				$"/redemptions?broadcaster_id={broadcasterId}&reward_id={rewardId}&status=UNFULFILLED&sort=NEWEST&first=50");
			var resp = await _http.SendAsync(req);
			if (!resp.IsSuccessStatusCode) {
				AppLogger.Instance.Warning($"Fetch redemptions failed: {(int)resp.StatusCode} {resp.StatusCode}");
				return null;
			}
			var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
			if (!json.TryGetProperty("data", out var data)) return null;

			string? newestForUser = null;
			foreach (var e in data.EnumerateArray()) {
				var login = e.TryGetProperty("user_login", out var ul) ? ul.GetString() ?? "" : "";
				if (!login.Equals(userLogin, StringComparison.OrdinalIgnoreCase)) continue;
				var input = e.TryGetProperty("user_input", out var ui) ? ui.GetString() ?? "" : "";
				var id    = e.GetProperty("id").GetString();
				// Exact input match wins; otherwise fall back to this user's newest redemption.
				if (input.Trim().Equals(userInput.Trim(), StringComparison.OrdinalIgnoreCase))
					return id;
				newestForUser ??= id;   // list is NEWEST-first
			}
			return newestForUser;
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Redemption lookup error: {ex.Message}");
			return null;
		}
	}

	public async Task UpdateRedemptionAsync(string rewardId, string redemptionId, bool fulfill) {
		if (string.IsNullOrEmpty(rewardId) || string.IsNullOrEmpty(redemptionId)) return;
		var broadcasterId = Credentials.Instance.TwitchBroadcasterId;
		if (string.IsNullOrEmpty(broadcasterId)) return;
		try {
			var status = fulfill ? "FULFILLED" : "CANCELED";
			using var req = new HttpRequestMessage(HttpMethod.Patch,
				$"https://api.twitch.tv/helix/channel_points/custom_rewards/redemptions" +
				$"?broadcaster_id={broadcasterId}&reward_id={rewardId}&id={redemptionId}");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credentials.Instance.TwitchAccessToken.Trim());
			req.Headers.Add("Client-Id", ChannelClientId);
			req.Content = new StringContent($"{{\"status\":\"{status}\"}}", Encoding.UTF8, "application/json");
			var resp = await _http.SendAsync(req);
			if (!resp.IsSuccessStatusCode) {
				var body = await resp.Content.ReadAsStringAsync();
				AppLogger.Instance.Warning($"Redemption update failed ({status}): {(int)resp.StatusCode} {resp.StatusCode} - {body}");
			}
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Redemption update error: {ex.Message}");
		}
	}

	// -- Custom reward management ------------------------------------------------
	// Twitch only lets an app manage rewards that IT created (matching Client-Id),
	// and only on Affiliate/Partner channels.

	private static HttpRequestMessage RewardRequest(HttpMethod method, string query) {
		var req = new HttpRequestMessage(method,
			$"https://api.twitch.tv/helix/channel_points/custom_rewards{query}");
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credentials.Instance.TwitchAccessToken.Trim());
		req.Headers.Add("Client-Id", ChannelClientId);
		return req;
	}

	private static string BroadcasterIdOrThrow() {
		var id = Credentials.Instance.TwitchBroadcasterId;
		if (string.IsNullOrEmpty(id))
			throw new InvalidOperationException("Broadcaster ID unknown - connect to Twitch first.");
		return id;
	}

	private static CustomReward ParseReward(JsonElement e) => new() {
		Id        = e.GetProperty("id").GetString() ?? "",
		Title     = e.GetProperty("title").GetString() ?? "",
		Cost      = e.TryGetProperty("cost", out var c) ? c.GetInt32() : 0,
		Prompt    = e.TryGetProperty("prompt", out var p) ? (p.GetString() ?? "") : "",
		IsEnabled = e.TryGetProperty("is_enabled", out var en) && en.GetBoolean(),
	};

	// Returns only rewards created by this app (the ones we can actually fulfil/cancel).
	public async Task<List<CustomReward>> GetManageableRewardsAsync() {
		var result = new List<CustomReward>();
		if (!IsAuthorized) return result;
		var broadcasterId = BroadcasterIdOrThrow();
		using var req = RewardRequest(HttpMethod.Get,
			$"?broadcaster_id={broadcasterId}&only_manageable_rewards=true");
		var resp = await _http.SendAsync(req);
		if (!resp.IsSuccessStatusCode) {
			AppLogger.Instance.Warning($"Fetch rewards failed: {(int)resp.StatusCode} {resp.StatusCode}");
			return result;
		}
		var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
		if (json.TryGetProperty("data", out var data))
			foreach (var e in data.EnumerateArray())
				result.Add(ParseReward(e));
		return result;
	}

	public async Task<RewardResult> CreateRewardAsync(string title, int cost, string prompt, bool requireInput) {
		try {
			if (!IsAuthorized) return RewardResult.Fail("Not authorized with Twitch.");
			var broadcasterId = BroadcasterIdOrThrow();

			// Avoid duplicates - adopt an existing manageable reward with the same title.
			var existing = (await GetManageableRewardsAsync())
				.FirstOrDefault(r => r.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
			if (existing != null)
				return RewardResult.Ok(existing, $"A reward named \"{title}\" already exists - using it.");

			using var req = RewardRequest(HttpMethod.Post, $"?broadcaster_id={broadcasterId}");
			var payload = new {
				title,
				cost,
				prompt,
				is_user_input_required = requireInput,
				is_enabled = true,
			};
			req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
			var resp = await _http.SendAsync(req);
			var body = await resp.Content.ReadAsStringAsync();
			if (!resp.IsSuccessStatusCode) {
				var msg = ExtractApiMessage(body);
				if (resp.StatusCode == HttpStatusCode.Forbidden)
					msg = "Twitch rejected this (403). Your channel must be Affiliate or Partner to create channel point rewards.";
				AppLogger.Instance.Warning($"Create reward failed: {(int)resp.StatusCode} - {body}");
				return RewardResult.Fail(msg);
			}
			var json   = JsonSerializer.Deserialize<JsonElement>(body);
			var reward = ParseReward(json.GetProperty("data")[0]);
			AppLogger.Instance.Information($"Created channel point reward \"{reward.Title}\" ({reward.Id})");
			return RewardResult.Ok(reward, $"Created reward \"{reward.Title}\".");
		}
		catch (Exception ex) {
			return RewardResult.Fail(ex.Message);
		}
	}

	public async Task<RewardResult> UpdateRewardAsync(string rewardId, string title, int cost, string prompt, bool requireInput) {
		try {
			if (!IsAuthorized) return RewardResult.Fail("Not authorized with Twitch.");
			if (string.IsNullOrEmpty(rewardId)) return RewardResult.Fail("No reward selected to edit.");
			var broadcasterId = BroadcasterIdOrThrow();

			using var req = RewardRequest(HttpMethod.Patch, $"?broadcaster_id={broadcasterId}&id={rewardId}");
			var payload = new {
				title,
				cost,
				prompt,
				is_user_input_required = requireInput,
			};
			req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
			var resp = await _http.SendAsync(req);
			var body = await resp.Content.ReadAsStringAsync();
			if (!resp.IsSuccessStatusCode) {
				var msg = ExtractApiMessage(body);
				if (resp.StatusCode == HttpStatusCode.Forbidden)
					msg = "Twitch rejected this (403). This reward must have been created by Vibes to edit it.";
				AppLogger.Instance.Warning($"Update reward failed: {(int)resp.StatusCode} - {body}");
				return RewardResult.Fail(msg);
			}
			var json   = JsonSerializer.Deserialize<JsonElement>(body);
			var reward = ParseReward(json.GetProperty("data")[0]);
			AppLogger.Instance.Information($"Updated channel point reward \"{reward.Title}\" ({reward.Id})");
			return RewardResult.Ok(reward, $"Updated reward \"{reward.Title}\".");
		}
		catch (Exception ex) {
			return RewardResult.Fail(ex.Message);
		}
	}

	private static string ExtractApiMessage(string body) {
		try {
			var json = JsonSerializer.Deserialize<JsonElement>(body);
			if (json.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } s)
				return s;
		}
		catch { }
		return string.IsNullOrWhiteSpace(body) ? "Unknown error." : body;
	}

	public void Disconnect() {
		_cts?.Cancel();
		_botCts?.Cancel();
		IsConnected = false;
		StatusChanged?.Invoke("Disconnected");
	}

	public async Task SendMessageAsync(string message) {
		if (!IsConnected || string.IsNullOrEmpty(_channel)) return;
		if (AppConfig.Instance.UseBotAccount && _botWs?.State == WebSocketState.Open)
			await SendRawAsync(_botWs, $"PRIVMSG #{_channel} :{message}");
		else
			await SendRawAsync(_ws!, $"PRIVMSG #{_channel} :{message}");
	}

	// -- IRC read loop ---------------------------------------------------------

	private async Task ReadLoopAsync(CancellationToken ct) {
		var buffer   = new byte[16384];
		var leftover = "";
		try {
			while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested) {
				var result = await _ws.ReceiveAsync(buffer, ct);
				if (result.MessageType == WebSocketMessageType.Close) break;

				var text  = leftover + Encoding.UTF8.GetString(buffer, 0, result.Count);
				var lines = text.Split('\n');
				leftover  = lines[^1];

				foreach (var raw in lines[..^1]) {
					var line = raw.TrimEnd('\r');
					if (!string.IsNullOrEmpty(line)) ParseLine(line);
				}
			}
		}
		catch (OperationCanceledException) { return; }
		catch (Exception ex) {
			AppLogger.Instance.Error($"Twitch read error: {ex.Message}");
		}

		IsConnected = false;
		_botCts?.Cancel();

		if (ct.IsCancellationRequested) {
			StatusChanged?.Invoke("Disconnected");
			return;
		}

		AppLogger.Instance.Warning("Twitch IRC disconnected - reconnecting in 3s");
		StatusChanged?.Invoke("Reconnecting…");

		try {
			await Task.Delay(3000, ct);
			await ConnectAsync();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) {
			AppLogger.Instance.Error($"Reconnect failed: {ex.Message}");
			StatusChanged?.Invoke("Disconnected");
		}
	}

	// Keep the bot connection alive with PING responses
	private async Task BotPingLoopAsync(CancellationToken ct) {
		var buffer = new byte[4096];
		try {
			while (_botWs?.State == WebSocketState.Open && !ct.IsCancellationRequested) {
				var result = await _botWs.ReceiveAsync(buffer, ct);
				var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
				foreach (var raw in text.Split('\n')) {
					var line = raw.TrimEnd('\r');
					if (line.StartsWith("PING"))
						await SendRawAsync(_botWs, "PONG :tmi.twitch.tv");
					else if (line.Contains(" NOTICE "))
						AppLogger.Instance.Warning($"Twitch bot NOTICE: {line}");
				}
			}
		}
		catch (OperationCanceledException) { return; }
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Twitch bot connection error: {ex.Message}");
		}

		if (ct.IsCancellationRequested) return;

		AppLogger.Instance.Warning("Twitch bot disconnected - reconnecting in 3s");
		try {
			await Task.Delay(3000, ct);
			await ConnectBotAsync();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) {
			AppLogger.Instance.Error($"Bot reconnect failed: {ex.Message}");
		}
	}

	// -- Line parsing ----------------------------------------------------------

	private void ParseLine(string line) {
		if (line.StartsWith("PING")) {
			_ = SendRawAsync(_ws!, "PONG :tmi.twitch.tv");
			return;
		}

		var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var rest = line;

		if (line.StartsWith('@')) {
			var sp = line.IndexOf(' ');
			if (sp < 0) return;
			foreach (var kv in line[1..sp].Split(';')) {
				var eq = kv.IndexOf('=');
				tags[eq < 0 ? kv : kv[..eq]] = eq < 0 ? "" : kv[(eq + 1)..];
			}
			rest = line[(sp + 1)..];
		}

		if (rest.Contains(" 001 ")) {
			AppLogger.Instance.Information("Twitch IRC authenticated (welcome received)");
			return;
		}

		if (rest.Contains(" PRIVMSG ")) { HandlePrivMsg(rest, tags); return; }

		if (rest.Contains(" NOTICE ")) {
			var colon = rest.IndexOf(':', rest.IndexOf(" NOTICE ") + 8);
			var text  = colon >= 0 ? rest[(colon + 1)..] : rest;
			var msgId = tags.GetValueOrDefault("msg-id");
			AppLogger.Instance.Warning(
				$"Twitch NOTICE{(string.IsNullOrEmpty(msgId) ? "" : $" [{msgId}]")}: {text}");
			return;
		}

		if (rest.StartsWith("RECONNECT") || rest.Contains(" RECONNECT")) {
			AppLogger.Instance.Warning("Twitch sent RECONNECT - server is asking us to reconnect");
			return;
		}
	}

	private void HandlePrivMsg(string line, Dictionary<string, string> tags) {
		var msgIdx = line.IndexOf(" PRIVMSG ");
		if (msgIdx < 0) return;
		var after = line[(msgIdx + 9)..];
		var colon = after.IndexOf(':');
		if (colon < 0) return;
		var message = after[(colon + 1)..];

		var login       = tags.GetValueOrDefault("login") ?? ParseNick(line);
		var displayName = tags.TryGetValue("display-name", out var dn) && dn.Length > 0 ? dn : login;

		MessageReceived?.Invoke(new TwitchChatMessage {
			Login       = login,
			DisplayName = displayName,
			Color       = tags.GetValueOrDefault("color") ?? "",
			Message     = message,
			RewardId     = tags.GetValueOrDefault("custom-reward-id") ?? "",
			RedemptionId = tags.GetValueOrDefault("id") ?? "",
			Tags         = tags,
		});
	}

	// -- Helpers ---------------------------------------------------------------

	private static async Task SendRawAsync(ClientWebSocket ws, string line) {
		if (ws.State != WebSocketState.Open) return;
		var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
		await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
	}

	private static string ParseNick(string line) {
		if (!line.StartsWith(':')) return "";
		var bang = line.IndexOf('!');
		return bang < 0 ? "" : line[1..bang];
	}

	private static string ParseQueryParam(string query, string key) {
		foreach (var pair in query.Split('&')) {
			var idx = pair.IndexOf('=');
			if (idx < 0) continue;
			if (pair[..idx] == key) return Uri.UnescapeDataString(pair[(idx + 1)..]);
		}
		return "";
	}
}
