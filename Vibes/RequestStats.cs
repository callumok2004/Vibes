using System.Text.Json;

namespace Vibes;

public class SongStat
{
	public string TrackId { get; set; } = "";
	public string Artist { get; set; } = "";
	public string Title { get; set; } = "";
	public int Count { get; set; }
	public DateTime LastRequested { get; set; }
}

public class RequesterStat
{
	public string Name { get; set; } = "";
	public int Count { get; set; }
	public DateTime LastRequested { get; set; }
}

public class RequestStats
{
	public static RequestStats Instance { get; private set; } = new();

	public int TotalRequests { get; set; }
	public Dictionary<string, SongStat> Songs { get; set; } = [];
	public Dictionary<string, RequesterStat> Requesters { get; set; } = [];

	public void Record(SpotifyTrackInfo track, string requester) {
		TotalRequests++;

		if (Songs.TryGetValue(track.TrackId, out var song)) {
			song.Count++;
			song.LastRequested = DateTime.Now;
		}
		else {
			Songs[track.TrackId] = new SongStat {
				TrackId = track.TrackId, Artist = track.Artist, Title = track.Title,
				Count = 1, LastRequested = DateTime.Now,
			};
		}

		var key = requester.ToLowerInvariant();
		if (Requesters.TryGetValue(key, out var user)) {
			user.Count++;
			user.LastRequested = DateTime.Now;
		}
		else {
			Requesters[key] = new RequesterStat {
				Name = requester, Count = 1, LastRequested = DateTime.Now,
			};
		}

		Save();
	}

	public IEnumerable<SongStat> TopSongs(int n) =>
		Songs.Values.OrderByDescending(s => s.Count).ThenByDescending(s => s.LastRequested).Take(n);

	public IEnumerable<RequesterStat> TopRequesters(int n) =>
		Requesters.Values.OrderByDescending(r => r.Count).ThenByDescending(r => r.LastRequested).Take(n);

	public void Reset() {
		TotalRequests = 0;
		Songs.Clear();
		Requesters.Clear();
		Save();
	}

	static readonly string ConfigDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibes");
	static readonly string StatsPath = Path.Combine(ConfigDir, "stats.json");
	static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

	public static void Load() {
		if (!File.Exists(StatsPath)) return;
		try {
			var loaded = JsonSerializer.Deserialize<RequestStats>(File.ReadAllText(StatsPath), JsonOpts);
			if (loaded != null) Instance = loaded;
		}
		catch { Instance = new(); }
	}

	public static void Save() {
		try {
			Directory.CreateDirectory(ConfigDir);
			File.WriteAllText(StatsPath, JsonSerializer.Serialize(Instance, JsonOpts));
		}
		catch (Exception ex) {
			AppLogger.Instance.Warning($"Failed to save stats: {ex.Message}");
		}
	}
}
