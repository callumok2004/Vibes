using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Vibes;

public static class VersionInfo
{
	const string GitHubApiUrl = "https://api.github.com/repos/callumok2004/Vibes/releases/latest";

	public static string CurrentVersion { get; } = GetCurrentVersion();

	static string GetCurrentVersion()
	{
		var attr = Assembly.GetExecutingAssembly()
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

		if (attr?.InformationalVersion is { } v && v.StartsWith('v'))
			return v;

		if (attr?.InformationalVersion is { Length: > 0 } raw)
			return raw;

		var ver = Assembly.GetExecutingAssembly().GetName().Version;
		return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "dev";
	}

	public static async Task<(bool UpdateAvailable, string LatestVersion, string? ReleaseUrl)> CheckForUpdateAsync()
	{
		using var client = new HttpClient();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Vibes");
		client.Timeout = TimeSpan.FromSeconds(10);

		var response = await client.GetAsync(GitHubApiUrl);
		response.EnsureSuccessStatusCode();

		using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
		var root = doc.RootElement;

		string latest = root.GetProperty("tag_name").GetString() ?? "";
		string? url = root.GetProperty("html_url").GetString();

		var current = CurrentVersion.Split('+')[0];
		bool updateAvailable = !string.IsNullOrEmpty(latest)
			&& !string.Equals(latest.TrimStart('v'), current.TrimStart('v'), StringComparison.OrdinalIgnoreCase);

		return (updateAvailable, latest, url);
	}
}
