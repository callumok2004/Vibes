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

	private const string ChannelClientId = "v6jcyt4gcec7vl8luchszezpwnnkip";
	private const string BotClientId     = "uu3ymsw69n8xoz6evskj2ycwt9evr8";
	private const int    ChannelPort     = 7777;
	private const int    BotPort         = 7778;

	private TwitchService() { }

	// -- Auth ------------------------------------------------------------------

	public async Task AuthorizeAsync() {
		StatusChanged?.Invoke("Opening browser for authorization…");
		var token = await ImplicitGrantAsync(ChannelClientId, ChannelPort,
			"chat:read chat:edit channel:read:redemptions channel:manage:redemptions");
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
			if (!resp.IsSuccessStatusCode)
				AppLogger.Instance.Warning($"Redemption update failed ({status}): {resp.StatusCode}");
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Redemption update error: {ex.Message}");
		}
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
				foreach (var line in text.Split('\n')) {
					if (line.TrimEnd('\r').StartsWith("PING"))
						await SendRawAsync(_botWs, "PONG :tmi.twitch.tv");
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

		if (rest.Contains(" PRIVMSG ")) HandlePrivMsg(rest, tags);
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
