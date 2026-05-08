using System.Text.Json;

namespace Vibes;

public class Credentials
{
	public static Credentials Instance { get; private set; } = new();

	public string TwitchAccessToken { get; set; } = "";
	public string TwitchRefreshToken { get; set; } = "";
	public string TwitchUserId { get; set; } = "";
	public string TwitchUserLogin { get; set; } = "";
	public DateTime TwitchTokenExpiry { get; set; }

	public string TwitchBotAccessToken { get; set; } = "";
	public string TwitchBotRefreshToken { get; set; } = "";
	public DateTime TwitchBotTokenExpiry { get; set; }

	public string SpotifyAccessToken { get; set; } = "";
	public string SpotifyRefreshToken { get; set; } = "";
	public string SpotifyDeviceId { get; set; } = "";
	public DateTime SpotifyTokenExpiry { get; set; }

	static readonly string CredPath = System.IO.Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibes", "credentials.json");

	public static void Load() {
		if (!System.IO.File.Exists(CredPath)) return;
		try {
			Instance = JsonSerializer.Deserialize<Credentials>(System.IO.File.ReadAllText(CredPath)) ?? new();
		}
		catch {
			Instance = new();
		}
	}

	public static void Save() {
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CredPath)!);
		System.IO.File.WriteAllText(CredPath, JsonSerializer.Serialize(Instance, new JsonSerializerOptions { WriteIndented = true }));
	}
}
