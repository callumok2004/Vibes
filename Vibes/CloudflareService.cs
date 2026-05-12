using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Vibes;

public class CloudflareService
{
	public static CloudflareService Instance { get; } = new();

	private static readonly HttpClient _http = new();
	private const string ApiBase = "https://api.cloudflare.com/client/v4";

	private CancellationTokenSource? _pushDebounce;
	private string _lastPushedPayload = "";

	private CloudflareService() { }

	// -- Deploy ----------------------------------------------------------------

	public async Task<string> DeployAsync(string accountId, string apiToken, string workerName) {
		AppLogger.Instance.Information("Cloudflare: deploying worker…");

		var kvId = await GetOrCreateKvNamespaceAsync(accountId, apiToken, "vibes-queue");
		Credentials.Instance.CloudflareKvNamespaceId = kvId;
		AppLogger.Instance.Information($"Cloudflare: KV namespace ready ({kvId})");

		await UploadWorkerAsync(accountId, apiToken, workerName, kvId);
		AppLogger.Instance.Information("Cloudflare: worker script uploaded");

		await EnableWorkerDevAsync(accountId, apiToken, workerName);

		var login = AppConfig.Instance.TwitchChannel.Trim().ToLower();
		if (!string.IsNullOrEmpty(login)) {
			await SetKvValueAsync(accountId, apiToken, kvId, "owner", login);
			AppLogger.Instance.Information($"Cloudflare: owner set to {login}");
		} else {
			AppLogger.Instance.Warning("Cloudflare: TwitchChannel is empty, owner not set — push will claim on first use");
		}

		var subdomain = await GetSubdomainAsync(accountId, apiToken);
		var url = $"https://{workerName}.{subdomain}.workers.dev";

		Credentials.Save();
		AppLogger.Instance.Information($"Cloudflare: deployed at {url}");
		return url;
	}

	private async Task<string> GetOrCreateKvNamespaceAsync(string accountId, string apiToken, string title) {
		var list = await GetAsync($"{ApiBase}/accounts/{accountId}/storage/kv/namespaces?per_page=100", apiToken);
		foreach (var ns in list.GetProperty("result").EnumerateArray()) {
			if (ns.GetProperty("title").GetString() == title)
				return ns.GetProperty("id").GetString()!;
		}
		var created = await PostAsync($"{ApiBase}/accounts/{accountId}/storage/kv/namespaces", apiToken,
			new { title });
		return created.GetProperty("result").GetProperty("id").GetString()!;
	}

	private async Task UploadWorkerAsync(string accountId, string apiToken, string workerName, string kvId) {
		var metadata = JsonSerializer.Serialize(new {
			main_module = "worker.js",
			bindings    = new[] { new { type = "kv_namespace", name = "QUEUE_KV", namespace_id = kvId } },
			compatibility_date = "2024-01-01",
		});

		using var form = new MultipartFormDataContent();
		var metaContent = new StringContent(metadata, Encoding.UTF8, "application/json");
		metaContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"metadata\"" };
		form.Add(metaContent);

		var scriptContent = new StringContent(WorkerScript, Encoding.UTF8, "application/javascript+module");
		scriptContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"worker.js\"", FileName = "\"worker.js\"" };
		form.Add(scriptContent);

		using var req = new HttpRequestMessage(HttpMethod.Put,
			$"{ApiBase}/accounts/{accountId}/workers/scripts/{workerName}");
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
		req.Content = form;

		var resp = await _http.SendAsync(req);
		var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
		if (!json.GetProperty("success").GetBoolean())
			throw new Exception($"Worker upload failed: {json}");
	}

	private async Task EnableWorkerDevAsync(string accountId, string apiToken, string workerName) {
		using var req = new HttpRequestMessage(HttpMethod.Post,
			$"{ApiBase}/accounts/{accountId}/workers/scripts/{workerName}/subdomain");
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
		req.Content = new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json");
		await _http.SendAsync(req);
	}

	private async Task<string> GetSubdomainAsync(string accountId, string apiToken) {
		var resp = await GetAsync($"{ApiBase}/accounts/{accountId}/workers/subdomain", apiToken);
		return resp.GetProperty("result").GetProperty("subdomain").GetString()!;
	}

	// -- Queue push ------------------------------------------------------------

	public void SchedulePush() {
		var cfg = AppConfig.Instance;
		if (!cfg.CloudflareQueueEnabled || string.IsNullOrEmpty(cfg.CloudflareWorkerUrl)) return;

		_pushDebounce?.Cancel();
		_pushDebounce = new CancellationTokenSource();
		var ct = _pushDebounce.Token;

		_ = Task.Run(async () => {
			try {
				await Task.Delay(2000, ct);
				await PushAsync(cfg.CloudflareWorkerUrl);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) {
				AppLogger.Instance.Warning($"Queue page push failed: {ex.Message}");
			}
		}, ct);
	}

	private async Task PushAsync(string workerUrl) {
		var track    = SpotifyService.Instance.CurrentTrack;
		var pending  = SongQueue.Pending.Where(r => !r.IsPlayed && r.TrackId != track?.TrackId).ToList();
		var requester = track != null
			? SongQueue.Pending.FirstOrDefault(r => r.TrackId == track.TrackId)?.Requester ?? ""
			: "";

		var payload = new {
			nowPlaying = track == null ? null : new {
				title     = track.Title,
				artist    = track.Artist,
				albumArt  = track.AlbumArt,
				requester,
			},
			queue = pending.Select((r, i) => new {
				pos       = i + 1,
				title     = r.Title,
				artist    = r.Artist,
				albumArt  = r.AlbumCover,
				requester = r.Requester,
			}).ToArray(),
		};

		var json = JsonSerializer.Serialize(payload);
		if (json == _lastPushedPayload) return;
		_lastPushedPayload = json;
		using var req = new HttpRequestMessage(HttpMethod.Post, $"{workerUrl}/update");
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
			Credentials.Instance.TwitchAccessToken.Trim());
		req.Content = new StringContent(json, Encoding.UTF8, "application/json");
		var resp = await _http.SendAsync(req);
		if (!resp.IsSuccessStatusCode)
			AppLogger.Instance.Warning($"Queue push failed: {resp.StatusCode}");
	}

	// -- Helpers ---------------------------------------------------------------

	private async Task<JsonElement> GetAsync(string url, string apiToken) {
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
		var resp = await _http.SendAsync(req);
		return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
	}

	private static async Task SetKvValueAsync(string accountId, string apiToken, string kvId, string key, string value) {
		using var req = new HttpRequestMessage(HttpMethod.Put,
			$"{ApiBase}/accounts/{accountId}/storage/kv/namespaces/{kvId}/values/{key}");
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
		req.Content = new StringContent(value, Encoding.UTF8, "text/plain");
		await _http.SendAsync(req);
	}

	private async Task<JsonElement> PostAsync(string url, string apiToken, object body) {
		using var req = new HttpRequestMessage(HttpMethod.Post, url);
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
		req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		var resp = await _http.SendAsync(req);
		return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
	}

	// -- Worker script ---------------------------------------------------------

	private const string WorkerScript = """
export default {
  async fetch(request, env) {
    const { method, url } = request;
    const path = new URL(url).pathname.replace(/\/$/, '') || '/';
    if (method === 'POST' && path === '/update') return handleUpdate(request, env);
    if (method === 'GET'  && path === '/json')   return handleJson(env);
    if (method === 'GET'  && path === '/')        return handlePage(env);
    return new Response('Not found', { status: 404 });
  }
};

async function handleUpdate(request, env) {
  const auth  = request.headers.get('Authorization') ?? '';
  const token = auth.startsWith('Bearer ') ? auth.slice(7) : '';
  if (!token) return new Response('Unauthorized', { status: 401 });

  const validate = await fetch('https://id.twitch.tv/oauth2/validate', {
    headers: { 'Authorization': `OAuth ${token}` }
  });
  if (!validate.ok) return new Response('Invalid token', { status: 401 });
  const { login } = await validate.json();

  const owner = await env.QUEUE_KV.get('owner');
  console.log(`[vibes] token login="${login}" kv owner="${owner}"`);
  if (!owner) {
    console.log(`[vibes] no owner in KV — claiming as "${login}"`);
    await env.QUEUE_KV.put('owner', login);
  } else if (owner !== login) {
    console.log(`[vibes] forbidden: "${login}" does not match owner "${owner}"`);
    return new Response('Forbidden', { status: 403 });
  }

  const data = await request.json();
  await env.QUEUE_KV.put('queue', JSON.stringify({ ...data, channel: login, updatedAt: Date.now() }), { expirationTtl: 86400 });
  return new Response('OK');
}

async function handleJson(env) {
  const data = await env.QUEUE_KV.get('queue', 'json') ?? {};
  return new Response(JSON.stringify(data), {
    headers: { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' }
  });
}

async function handlePage(env) {
  const data = await env.QUEUE_KV.get('queue', 'json');
  return new Response(renderPage(data), {
    headers: { 'Content-Type': 'text/html; charset=utf-8' }
  });
}

function renderPage(data) {
  const channel = data?.channel ?? '';
  return `<!DOCTYPE html>
<html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${esc(channel ? channel + "'s Queue" : 'Queue')} - Vibes</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#0e0e10;color:#efeff1;padding:32px 24px;max-width:640px;margin:0 auto}
h1{font-size:22px;font-weight:700;margin-bottom:2px}
.sub{font-size:12px;color:#4a4a55;margin-bottom:28px}
.dot{display:inline-block;width:6px;height:6px;border-radius:50%;background:#4a4a55;margin-right:6px;vertical-align:middle;transition:background .3s}
.dot.live{background:#00c853}
.section-label{font-size:10px;font-weight:700;color:#adadb8;letter-spacing:.08em;text-transform:uppercase;margin-bottom:8px}
.now-playing{display:flex;align-items:center;gap:14px;background:#18181b;border:1px solid #9146ff55;border-radius:8px;padding:14px 16px;margin-bottom:24px}
.np-art{width:56px;height:56px;border-radius:4px;object-fit:cover;flex-shrink:0;background:#2d2d35}
.np-info{min-width:0}
.np-title{font-size:15px;font-weight:600}
.np-meta{font-size:12px;color:#adadb8;margin-top:3px}
.req{color:#9146ff}
.song{display:flex;align-items:center;gap:12px;background:#18181b;border-radius:6px;padding:10px 14px;margin-bottom:2px}
.song-art{width:36px;height:36px;border-radius:3px;object-fit:cover;flex-shrink:0;background:#2d2d35}
.pos{font-size:11px;color:#4a4a55;width:22px;text-align:right;flex-shrink:0}
.song-title{font-size:13px;font-weight:500}
.song-meta{font-size:11px;color:#4a4a55;margin-top:2px}
.empty{color:#4a4a55;font-size:13px;padding:12px 0}
.queue-header{display:flex;justify-content:space-between;align-items:center;margin-bottom:8px}
</style>
</head><body>
<h1 id="title">${esc(channel ? channel + "'s Queue" : 'Queue')}</h1>
<div class="sub"><span class="dot" id="dot"></span><span id="status">Connecting…</span></div>
<div id="np"></div>
<div class="queue-header">
  <div class="section-label" id="qlabel">Queue</div>
</div>
<div id="qlist"></div>
<script>
function esc(s){return String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
const INTERVAL = 10;
let last = null, countdown = INTERVAL;
setInterval(() => {
  countdown--;
  document.getElementById('status').textContent = countdown > 0 ? \`Updating in \${countdown}s\` : 'Updating…';
}, 1000);
async function poll() {
  try {
    const r = await fetch('/json');
    const d = await r.json();
    const sig = JSON.stringify(d);
    if (sig !== last) { last = sig; render(d); }
    countdown = INTERVAL;
    document.getElementById('dot').className = 'dot live';
    document.getElementById('status').textContent = \`Updating in \${countdown}s\`;
  } catch {
    countdown = INTERVAL;
    document.getElementById('dot').className = 'dot';
    document.getElementById('status').textContent = 'Reconnecting…';
  }
}
function render(d) {
  const np = d.nowPlaying;
  const q  = d.queue ?? [];
  const ch = d.channel ?? '';
  if (ch) {
    document.getElementById('title').textContent = ch + "'s Queue";
    document.title = ch + "'s Queue - Vibes";
  }
  document.getElementById('np').innerHTML = np ? \`
    <div class="section-label">Now Playing</div>
    <div class="now-playing">
      \${np.albumArt ? \`<img class="np-art" src="\${esc(np.albumArt)}" alt="">\` : ''}
      <div class="np-info">
        <div class="np-title">\${esc(np.title)}</div>
        <div class="np-meta">\${esc(np.artist)}\${np.requester ? \` <span class="req">• requested by \${esc(np.requester)}</span>\` : ''}</div>
      </div>
    </div>\` : '';
  document.getElementById('qlabel').textContent = \`Queue (\${q.length})\`;
  document.getElementById('qlist').innerHTML = q.length ? q.map(item => \`
    <div class="song">
      \${item.albumArt ? \`<img class="song-art" src="\${esc(item.albumArt)}" alt="">\` : \`<span class="pos">#\${item.pos}</span>\`}
      <div class="info">
        <div class="song-title">\${esc(item.title)}</div>
        <div class="song-meta">\${esc(item.artist)}\${item.requester ? \` <span class="req">• \${esc(item.requester)}</span>\` : ''}</div>
      </div>
    </div>\`).join('') : '<div class="empty">The queue is empty.</div>';
}
poll();
setInterval(poll, 10000);
</script>
</body></html>`;
}

function esc(s){
  return String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
""";
}
