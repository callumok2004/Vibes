using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Vibes;

public enum LogLevel
{
	Verbose,
	Debug,
	Information,
	Warning,
	Error,
	Fatal
}

public class LogEntry
{
	public DateTime Timestamp { get; init; }
	public LogLevel Level { get; init; }
	public string Message { get; init; } = "";
	public string LevelShort => Level switch {
		LogLevel.Verbose => "VRB",
		LogLevel.Debug => "DBG",
		LogLevel.Information => "INF",
		LogLevel.Warning => "WRN",
		LogLevel.Error => "ERR",
		LogLevel.Fatal => "FTL",
		_ => "???"
	};
}

public interface ILogger
{
	void Verbose(string messageTemplate, params object[] args);
	void Debug(string messageTemplate, params object[] args);
	void Information(string messageTemplate, params object[] args);
	void Warning(string messageTemplate, params object[] args);
	void Error(string messageTemplate, params object[] args);
	void Fatal(string messageTemplate, params object[] args);
}

public partial class AppLogger : ILogger
{
	public static AppLogger Instance { get; } = new();
	private readonly ObservableCollection<LogEntry> _entries = [];
	public ObservableCollection<LogEntry> Entries => _entries;

	private Dispatcher? _dispatcher;
	public void SetDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

	public void Log(LogLevel level, string messageTemplate, params object[] args) {
		string message = FormatTemplate(messageTemplate, args);
		var entry = new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message };

		if (_dispatcher != null && !_dispatcher.CheckAccess())
			_dispatcher.BeginInvoke(() => _entries.Add(entry));
		else
			_entries.Add(entry);
	}

	public void Verbose(string messageTemplate, params object[] args) => Log(LogLevel.Verbose, messageTemplate, args);
	public void Debug(string messageTemplate, params object[] args) => Log(LogLevel.Debug, messageTemplate, args);
	public void Information(string messageTemplate, params object[] args) => Log(LogLevel.Information, messageTemplate, args);
	public void Warning(string messageTemplate, params object[] args) => Log(LogLevel.Warning, messageTemplate, args);
	public void Error(string messageTemplate, params object[] args) => Log(LogLevel.Error, messageTemplate, args);
	public void Fatal(string messageTemplate, params object[] args) => Log(LogLevel.Fatal, messageTemplate, args);

	private static string FormatTemplate(string template, object[] args) {
		if (args.Length == 0) return template;
		int i = 0;
		return TemplateRegex().Replace(template, m => i < args.Length ? (args[i++]?.ToString() ?? "") : m.Value);
	}

	[GeneratedRegex(@"\{[^}]+\}")]
	private static partial Regex TemplateRegex();
}
