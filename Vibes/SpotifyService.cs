using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Timers;

namespace Vibes;

public class SpotifyTrackInfo
{
	public string TrackId    { get; set; } = "";
	public string Title      { get; set; } = "";
	public string Artist     { get; set; } = "";
	public string AlbumArt   { get; set; } = "";
	public int    DurationMs { get; set; }
	public int    ProgressMs { get; set; }
	public bool   IsPlaying  { get; set; }
	public bool   IsExplicit { get; set; }
}

public class SpotifyService
{
	public static SpotifyService Instance { get; } = new();

	public event Action<SpotifyTrackInfo?>?        TrackChanged;
	public event Action<List<SpotifyTrackInfo>>?   QueueChanged;
	public event Action<string>?                   StatusChanged;

	public SpotifyTrackInfo? CurrentTrack { get; private set; }
	public bool IsAuthorized =>
		!string.IsNullOrEmpty(Credentials.Instance.SpotifyAccessToken) &&
		!string.IsNullOrEmpty(Credentials.Instance.SpotifyRefreshToken);

	private static readonly HttpClient _http = new();
	private readonly System.Timers.Timer _pollTimer = new();
	private string? _codeVerifier;

	private SpotifyService() {
		_pollTimer.Elapsed += async (_, _) => await PollCurrentTrackAsync();
		_pollTimer.AutoReset = true;
	}

	// -- Auth ------------------------------------------------------------------

	public async Task AuthorizeAsync() {
		var clientId = AppConfig.Instance.SpotifyClientId.Trim();
		if (string.IsNullOrEmpty(clientId))
			throw new InvalidOperationException("Spotify Client ID is not set in config.");

		var port        = AppConfig.Instance.SpotifyCallbackPort;
		var redirectUri = $"http://127.0.0.1:{port}/callback/";

		_codeVerifier = GenerateCodeVerifier();
		var codeChallenge = GenerateCodeChallenge(_codeVerifier);

		var scopes = "user-read-currently-playing user-read-playback-state " +
		             "user-modify-playback-state playlist-modify-public " +
		             "playlist-modify-private user-library-modify";

		var authUrl = "https://accounts.spotify.com/authorize" +
			$"?client_id={Uri.EscapeDataString(clientId)}" +
			$"&response_type=code" +
			$"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
			$"&scope={Uri.EscapeDataString(scopes)}" +
			$"&code_challenge_method=S256" +
			$"&code_challenge={codeChallenge}";

		StatusChanged?.Invoke("Opening browser for authorization…");
		System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });

		var code = await ListenForCallbackAsync(port);
		StatusChanged?.Invoke("Exchanging code for tokens…");
		await ExchangeCodeAsync(code, redirectUri);
		StatusChanged?.Invoke("Connected");
		StartPolling();
	}

	private static async Task<string> ListenForCallbackAsync(int port) {
		using var listener = new HttpListener();
		listener.Prefixes.Add($"http://127.0.0.1:{port}/callback/");
		listener.Start();

		var ctx = await listener.GetContextAsync();

		var query = ctx.Request.Url?.Query.TrimStart('?') ?? "";
		var code  = ParseQueryParam(query, "code");

		var html = Encoding.UTF8.GetBytes(
			"<html><head><style>body{font-family:sans-serif;background:#0e0e10;color:#efeff1;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}</style></head>" +
			"<body><div><h2 style='color:#9146ff'>Vibes</h2><p>Spotify connected - you can close this tab.</p></div></body></html>");
		ctx.Response.ContentType   = "text/html";
		ctx.Response.ContentLength64 = html.Length;
		await ctx.Response.OutputStream.WriteAsync(html);
		ctx.Response.Close();
		listener.Stop();

		if (string.IsNullOrEmpty(code))
			throw new Exception("No authorization code received from Spotify.");
		return code;
	}

	private async Task ExchangeCodeAsync(string code, string redirectUri) {
		var body = new FormUrlEncodedContent(new Dictionary<string, string> {
			["grant_type"]    = "authorization_code",
			["code"]          = code,
			["redirect_uri"]  = redirectUri,
			["client_id"]     = AppConfig.Instance.SpotifyClientId.Trim(),
			["code_verifier"] = _codeVerifier!,
		});

		var resp = await _http.PostAsync("https://accounts.spotify.com/api/token", body);
		resp.EnsureSuccessStatusCode();

		var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
		Credentials.Instance.SpotifyAccessToken  = json.GetProperty("access_token").GetString()  ?? "";
		Credentials.Instance.SpotifyRefreshToken = json.GetProperty("refresh_token").GetString() ?? "";
		Credentials.Instance.SpotifyTokenExpiry  = DateTime.UtcNow.AddSeconds(
			json.GetProperty("expires_in").GetInt32() - 60);
		Credentials.Save();
	}

	public async Task RefreshTokenAsync() {
		var refresh = Credentials.Instance.SpotifyRefreshToken;
		if (string.IsNullOrEmpty(refresh)) return;

		var body = new FormUrlEncodedContent(new Dictionary<string, string> {
			["grant_type"]    = "refresh_token",
			["refresh_token"] = refresh,
			["client_id"]     = AppConfig.Instance.SpotifyClientId.Trim(),
		});

		var resp = await _http.PostAsync("https://accounts.spotify.com/api/token", body);
		if (!resp.IsSuccessStatusCode) {
			AppLogger.Instance.Warning($"Spotify token refresh failed: {resp.StatusCode}");
			return;
		}

		var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
		Credentials.Instance.SpotifyAccessToken = json.GetProperty("access_token").GetString() ?? "";
		Credentials.Instance.SpotifyTokenExpiry = DateTime.UtcNow.AddSeconds(
			json.GetProperty("expires_in").GetInt32() - 60);
		if (json.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
			Credentials.Instance.SpotifyRefreshToken = rt.GetString() ?? refresh;
		Credentials.Save();
	}

	// -- Polling ---------------------------------------------------------------

	public void StartPolling() {
		_pollTimer.Interval = Math.Max(1, AppConfig.Instance.SpotifyFetchRate) * 1000.0;
		_pollTimer.Start();
		AppLogger.Instance.Information("Spotify polling started");
	}

	public void StopPolling() {
		_pollTimer.Stop();
	}

	public async Task PollCurrentTrackAsync() {
		if (!IsAuthorized) return;
		try {
			if (DateTime.UtcNow >= Credentials.Instance.SpotifyTokenExpiry)
				await RefreshTokenAsync();

			var token = Credentials.Instance.SpotifyAccessToken;
			if (string.IsNullOrEmpty(token)) return;

			using var req = new HttpRequestMessage(HttpMethod.Get,
				"https://api.spotify.com/v1/me/player/currently-playing");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

			var resp = await _http.SendAsync(req);

			if (resp.StatusCode == HttpStatusCode.NoContent) {
				// Nothing playing
				if (CurrentTrack != null) {
					CurrentTrack = null;
					TrackChanged?.Invoke(null);
				}
				return;
			}

			if (!resp.IsSuccessStatusCode) {
				AppLogger.Instance.Warning($"Spotify API: {resp.StatusCode}");
				return;
			}

			var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
			if (!json.TryGetProperty("item", out var item) || item.ValueKind == JsonValueKind.Null)
				return;

			var artists = item.GetProperty("artists").EnumerateArray()
				.Select(a => a.GetProperty("name").GetString() ?? "")
				.Where(n => !string.IsNullOrEmpty(n));

			var images = item.GetProperty("album").GetProperty("images");
			var albumArt = images.GetArrayLength() > 0
				? images[0].GetProperty("url").GetString() ?? ""
				: "";

			var track = new SpotifyTrackInfo {
				TrackId    = item.GetProperty("id").GetString() ?? "",
				Title      = item.GetProperty("name").GetString() ?? "",
				Artist     = string.Join(", ", artists),
				AlbumArt   = albumArt,
				DurationMs = item.GetProperty("duration_ms").GetInt32(),
				ProgressMs = json.GetProperty("progress_ms").GetInt32(),
				IsPlaying  = json.GetProperty("is_playing").GetBoolean(),
			};

			bool trackChanged = CurrentTrack?.TrackId != track.TrackId;
			CurrentTrack = track;
			if (trackChanged) TrackChanged?.Invoke(track);

			var queue = await GetQueueAsync();
			QueueChanged?.Invoke(queue);
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Spotify poll: {ex.Message}");
		}
	}

	public async Task<SpotifyTrackInfo?> GetTrackAsync(string trackId) {
		try {
			await EnsureTokenAsync();
			var token = Credentials.Instance.SpotifyAccessToken;
			if (string.IsNullOrEmpty(token)) return null;
			using var req = new HttpRequestMessage(HttpMethod.Get,
				$"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(trackId)}");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			var resp = await _http.SendAsync(req);
			if (!resp.IsSuccessStatusCode) return null;

			var json    = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
			var artists = json.GetProperty("artists").EnumerateArray()
				.Select(a => a.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0);
			var images  = json.GetProperty("album").GetProperty("images");
			return new SpotifyTrackInfo {
				TrackId    = json.GetProperty("id").GetString() ?? "",
				Title      = json.GetProperty("name").GetString() ?? "",
				Artist     = string.Join(", ", artists),
				AlbumArt   = images.GetArrayLength() > 0 ? images[0].GetProperty("url").GetString() ?? "" : "",
				DurationMs = json.GetProperty("duration_ms").GetInt32(),
				IsExplicit = json.TryGetProperty("explicit", out var ex) && ex.GetBoolean(),
			};
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Spotify get track: {ex.Message}");
			return null;
		}
	}

	public async Task<SpotifyTrackInfo?> SearchTrackAsync(string query) {
		var token = Credentials.Instance.SpotifyAccessToken;
		if (string.IsNullOrEmpty(token)) return null;
		try {
			await EnsureTokenAsync();
			using var req = new HttpRequestMessage(HttpMethod.Get,
				$"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			var resp = await _http.SendAsync(req);
			if (!resp.IsSuccessStatusCode) return null;

			var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
			if (!json.TryGetProperty("tracks", out var tracks)) return null;
			if (!tracks.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return null;

			var item    = items[0];
			var artists = item.GetProperty("artists").EnumerateArray()
				.Select(a => a.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0);
			var images  = item.GetProperty("album").GetProperty("images");
			return new SpotifyTrackInfo {
				TrackId    = item.GetProperty("id").GetString() ?? "",
				Title      = item.GetProperty("name").GetString() ?? "",
				Artist     = string.Join(", ", artists),
				AlbumArt   = images.GetArrayLength() > 0 ? images[0].GetProperty("url").GetString() ?? "" : "",
				DurationMs = item.GetProperty("duration_ms").GetInt32(),
				IsExplicit = item.TryGetProperty("explicit", out var ex) && ex.GetBoolean(),
			};
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Spotify search: {ex.Message}");
			return null;
		}
	}

	public async Task<bool> AddToQueueAsync(string trackId) {
		try {
			await EnsureTokenAsync();
			var token = Credentials.Instance.SpotifyAccessToken;
			if (string.IsNullOrEmpty(token)) return false;
			using var req = new HttpRequestMessage(HttpMethod.Post,
				$"https://api.spotify.com/v1/me/player/queue?uri=spotify%3Atrack%3A{trackId}");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			var resp = await _http.SendAsync(req);
			return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NoContent;
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Spotify add to queue: {ex.Message}");
			return false;
		}
	}

	public async Task SkipAsync() {
		try {
			await EnsureTokenAsync();
			var token = Credentials.Instance.SpotifyAccessToken;
			if (string.IsNullOrEmpty(token)) return;
			using var req = new HttpRequestMessage(HttpMethod.Post,
				"https://api.spotify.com/v1/me/player/next");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			await _http.SendAsync(req);
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Spotify skip: {ex.Message}");
		}
	}

	public async Task LikeCurrentTrackAsync() {
		try {
			await EnsureTokenAsync();
			var token = Credentials.Instance.SpotifyAccessToken;
			if (string.IsNullOrEmpty(token) || CurrentTrack == null) return;
			using var req = new HttpRequestMessage(HttpMethod.Put,
				$"https://api.spotify.com/v1/me/tracks?ids={CurrentTrack.TrackId}");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			req.Content = new StringContent("", Encoding.UTF8, "application/json");
			await _http.SendAsync(req);
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Spotify like: {ex.Message}");
		}
	}

	public async Task<List<SpotifyTrackInfo>> GetQueueAsync() {
		var token = Credentials.Instance.SpotifyAccessToken;
		if (string.IsNullOrEmpty(token)) return [];

		using var req = new HttpRequestMessage(HttpMethod.Get,
			"https://api.spotify.com/v1/me/player/queue");
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		var resp = await _http.SendAsync(req);
		if (!resp.IsSuccessStatusCode) return [];

		var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
		if (!json.TryGetProperty("queue", out var queueArr)) return [];

		var result = new List<SpotifyTrackInfo>();
		foreach (var item in queueArr.EnumerateArray()) {
			if (item.ValueKind == JsonValueKind.Null) continue;
			var artists = item.GetProperty("artists").EnumerateArray()
				.Select(a => a.GetProperty("name").GetString() ?? "")
				.Where(n => !string.IsNullOrEmpty(n));
			var images = item.GetProperty("album").GetProperty("images");
			result.Add(new SpotifyTrackInfo {
				TrackId  = item.GetProperty("id").GetString() ?? "",
				Title    = item.GetProperty("name").GetString() ?? "",
				Artist   = string.Join(", ", artists),
				AlbumArt = images.GetArrayLength() > 0 ? images[0].GetProperty("url").GetString() ?? "" : "",
				DurationMs = item.GetProperty("duration_ms").GetInt32(),
			});
		}
		return result;
	}

	private async Task EnsureTokenAsync() {
		if (DateTime.UtcNow >= Credentials.Instance.SpotifyTokenExpiry)
			await RefreshTokenAsync();
	}

	// -- Helpers ---------------------------------------------------------------

	private static string GenerateCodeVerifier() {
		var bytes = new byte[32];
		RandomNumberGenerator.Fill(bytes);
		return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	private static string GenerateCodeChallenge(string verifier) {
		var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
		return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
