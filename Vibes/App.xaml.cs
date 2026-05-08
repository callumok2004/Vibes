using System.Windows;

namespace Vibes;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		AppConfig.Load();
		Credentials.Load();
		AppLogger.Instance.SetDispatcher(Dispatcher);
		base.OnStartup(e);
	}
}
