using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Vibes;

public class QueueDisplayItem
{
	public string Position { get; set; } = "";
	public string Title { get; set; } = "";
	public string Artist { get; set; } = "";
	public string Requester { get; set; } = "";
}

public partial class MainWindow : Window
{
	private ICollectionView? _logView;
	private readonly DispatcherTimer _statusTimer;
	private readonly DispatcherTimer _progressTimer;
	private bool _configReady;
	private System.Windows.Forms.NotifyIcon? _trayIcon;
	private static readonly HttpClient _imageHttp = new();

	public MainWindow() {
		InitializeComponent();
		_statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
		_statusTimer.Tick += (_, _) => UpdateStatus();
		_progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_progressTimer.Tick += (_, _) => TickProgress();
	}

	private void Window_Loaded(object sender, RoutedEventArgs e) {
		_logView = CollectionViewSource.GetDefaultView(AppLogger.Instance.Entries);
		_logView.Filter = LogFilter;
		LogList.ItemsSource = _logView;
		AppLogger.Instance.Entries.CollectionChanged += OnLogEntriesChanged;

		TitleVersionText.Text = $"v{VersionInfo.CurrentVersion}";
		VersionInfoText.Text = $"Version {VersionInfo.CurrentVersion}";

		InitConfigControls();
		BuildCommandRows();
		InitTrayIcon();

		SpotifyService.Instance.TrackChanged  += OnTrackChanged;
		SpotifyService.Instance.QueueChanged  += OnQueueChanged;
		SpotifyService.Instance.StatusChanged += s => Dispatcher.Invoke(() => UpdateSpotifyStatus(s));

		TwitchService.Instance.StatusChanged  += s => Dispatcher.Invoke(() => { UpdateTwitchStatus(s); UpdateBotStatusBar(); });
		TwitchService.Instance.MessageReceived += msg => _ = SongRequestProcessor.Instance.HandleMessageAsync(msg);

		if (AppConfig.Instance.TwAutoConnect && TwitchService.Instance.IsAuthorized)
			_ = TryConnectTwitchAsync();

		if (SpotifyService.Instance.IsAuthorized) {
			SpotifyService.Instance.StartPolling();
			UpdateSpotifyStatus("Connected");
		}

		UpdateSrStatus();
		UpdateBotStatusBar();

		_statusTimer.Start();
		_progressTimer.Start();
		AppLogger.Instance.Information("Vibes started");

// #if DEBUG
// 		_ = Task.Run(async () => {
// 			await Task.Delay(3000); // wait for Spotify auth
// 			if (!SpotifyService.Instance.IsAuthorized) return;
// 			const string debugTrackId = "7oX7Q0aa3yF9rA9FppMxHH";
// 			var ok = await SpotifyService.Instance.AddToQueueAsync(debugTrackId);
// 			if (ok) {
// 				SongQueue.Pending.Add(new RequestObject {
// 					TrackId   = debugTrackId,
// 					Title     = "Sway",
// 					Artist    = "Macky Gee",
// 					Requester = "zarpcallum",
// 					Source    = SongRequestSource.Command,
// 				});
// 			}
// 		});
// #endif
	}

	// -- Config initialisation -------------------------------------------------

	private void InitConfigControls() {
		var c = AppConfig.Instance;

		// Twitch
		TwitchChannelInput.Text       = c.TwitchChannel;
		UseBotAccountCheck.IsChecked  = c.UseBotAccount;
		BotAccountInput.Text          = c.BotAccountName;
		AutoConnectCheck.IsChecked    = c.TwAutoConnect;
		UpdateBotAuthStatus();
		OnlyLiveCheck.IsChecked = c.OnlyWorkWhenLive;
		AnnounceInChatCheck.IsChecked = c.AnnounceInChat;

		// Spotify
		SpotifyClientIdInput.Text = c.SpotifyClientId;
		SpotifySecretInput.Password = c.SpotifyClientSecret;
		SpotifyFetchRateInput.Text    = c.SpotifyFetchRate.ToString();
		SpotifyCallbackPortInput.Text = c.SpotifyCallbackPort.ToString();
		SpotifyPlaylistInput.Text     = c.SpotifyPlaylistId;
		AddToPlaylistCheck.IsChecked = c.AddSrToPlaylist;
		LimitToPlaylistCheck.IsChecked = c.LimitSrToPlaylist;

		// SR mode
		ModeCommand.IsChecked = c.RequestMode is RequestMode.ChatCommand or RequestMode.Both;
		ModeReward.IsChecked  = c.RequestMode is RequestMode.ChannelReward or RequestMode.Both;
		ModeBoth.IsChecked    = c.RequestMode == RequestMode.Both;
		SrCommandInput.Text   = c.TwSrCommandTrigger;
		RewardIdInput.Text    = c.TwRewardId;
		AutoManageCheck.IsChecked = c.AutoManageRedemptions;

		ModeCommand.IsChecked = c.RequestMode == RequestMode.ChatCommand;
		ModeReward.IsChecked  = c.RequestMode == RequestMode.ChannelReward;
		ModeBoth.IsChecked    = c.RequestMode == RequestMode.Both;
		UpdateRequestModeVisibility();

		// User level
		LevelViewer.IsChecked     = c.TwSrUserLevel == (int)TwitchUserLevel.Viewer;
		LevelFollower.IsChecked   = c.TwSrUserLevel == (int)TwitchUserLevel.Follower;
		LevelSubscriber.IsChecked = c.TwSrUserLevel == (int)TwitchUserLevel.Subscriber;
		LevelVip.IsChecked        = c.TwSrUserLevel == (int)TwitchUserLevel.Vip;
		LevelModerator.IsChecked  = c.TwSrUserLevel == (int)TwitchUserLevel.Moderator;

		// SR settings
		CooldownInput.Text        = c.TwSrCooldown.ToString();
		PerUserCooldownInput.Text = c.TwSrPerUserCooldown.ToString();
		MaxSongLengthInput.Text   = c.MaxSongLength.ToString();
		MaxQueueInput.Text        = c.MaxQueueLength.ToString();
		BlockExplicitCheck.IsChecked = c.BlockAllExplicitSongs;

		VoteSkipCountInput.Text   = c.VoteSkipCount.ToString();

		// Per-level limits
		MaxReqViewer.Text      = c.TwSrMaxReqViewer.ToString();
		MaxReqFollower.Text    = c.TwSrMaxReqFollower.ToString();
		MaxReqSub.Text         = c.TwSrMaxReqSubscriber.ToString();
		MaxReqSubT2.Text       = c.TwSrMaxReqSubscriberT2.ToString();
		MaxReqSubT3.Text       = c.TwSrMaxReqSubscriberT3.ToString();
		MaxReqVip.Text         = c.TwSrMaxReqVip.ToString();
		MaxReqMod.Text         = c.TwSrMaxReqModerator.ToString();
		MaxReqBroadcaster.Text = c.TwSrMaxReqBroadcaster.ToString();

		// App
		StartWithWindowsCheck.IsChecked = c.StartWithWindows;
		MinimizeToTrayCheck.IsChecked   = c.MinimizeToTray;

		// Responses
		RespSuccessInput.Text      = c.BotRespSuccess;
		RespErrorInput.Text        = c.BotRespError;
		RespNoSongInput.Text       = c.BotRespNoSong;
		RespBlacklistInput.Text    = c.BotRespBlacklist;
		RespExplicitInput.Text     = c.BotRespExplicit;
		RespTooLongInput.Text      = c.BotRespTooLong;
		RespCooldownInput.Text     = c.BotRespCooldown;
		RespUserCooldownInput.Text = c.BotRespUserCooldown;
		RespMaxReqInput.Text       = c.BotRespMaxReq;
		RespQueueFullInput.Text    = c.BotRespQueueFull;
		RespIsInQueueInput.Text    = c.BotRespIsInQueue;
		RespLevelTooLowInput.Text  = c.BotRespLevelTooLow;
		RespSongInput.Text         = c.BotRespSong;
		RespNextInput.Text         = c.BotRespNext;
		RespPosInput.Text          = c.BotRespPos;
		RespQueueInput.Text        = c.BotRespQueue;
		RespRemoveInput.Text       = c.BotRespRemove;
		RespNoQueueInput.Text      = c.BotRespNoQueue;
		RespSkipInput.Text         = c.BotRespSkip;
		RespVoteSkipInput.Text     = c.BotRespVoteSkip;
		RespSongLikeInput.Text     = c.BotRespSongLike;
		RespToggleSrInput.Text     = c.BotRespToggleSr;

		_configReady = true;
	}

	private void BuildCommandRows() {
		CommandsStack.Children.Clear();
		foreach (var cmd in AppConfig.Instance.Commands) {
			var row = BuildCommandRow(cmd);
			CommandsStack.Children.Add(row);
		}
	}

	private UIElement BuildCommandRow(TwitchCommand cmd) {
		var border = new Border {
			Margin = new Thickness(0, 0, 0, 2),
			Padding = new Thickness(14, 10, 14, 10),
			Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10)),
			CornerRadius = new CornerRadius(4)
		};

		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		// Enabled toggle
		var toggle = new CheckBox { IsChecked = cmd.IsEnabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
		toggle.Checked   += (_, _) => { cmd.IsEnabled = true;  AppConfig.Save(); };
		toggle.Unchecked += (_, _) => { cmd.IsEnabled = false; AppConfig.Save(); };
		Grid.SetColumn(toggle, 0);

		// Name label
		var label = new TextBlock {
			Text = cmd.Name,
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 13,
			Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xF1)),
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(label, 1);

		// Trigger input
		var trigger = new TextBox { Text = cmd.Trigger, Style = (Style)FindResource("SettingsInput") };
		trigger.TextChanged += (_, _) => { cmd.Trigger = trigger.Text.Trim(); AppConfig.Save(); };
		Grid.SetColumn(trigger, 2);

		grid.Children.Add(toggle);
		grid.Children.Add(label);
		grid.Children.Add(trigger);
		border.Child = grid;
		return border;
	}

	private void UpdateRequestModeVisibility() {
		if (CommandTriggerRow == null) return;
		bool cmd    = ModeCommand.IsChecked == true || ModeBoth.IsChecked == true;
		bool reward = ModeReward.IsChecked  == true || ModeBoth.IsChecked == true;
		CommandTriggerRow.Visibility = cmd    ? Visibility.Visible : Visibility.Collapsed;
		RewardIdRow.Visibility       = reward ? Visibility.Visible : Visibility.Collapsed;
	}


	// -- Generic config change handlers ---------------------------------------

	private void Cfg_Changed(object sender, TextChangedEventArgs e) {
		if (!_configReady) return;
		var c = AppConfig.Instance;

		c.TwitchChannel        = TwitchChannelInput.Text.Trim();
		c.BotAccountName       = BotAccountInput.Text.Trim();
		c.SpotifyClientId      = SpotifyClientIdInput.Text.Trim();
		c.SpotifyPlaylistId    = SpotifyPlaylistInput.Text.Trim();
		c.TwSrCommandTrigger   = SrCommandInput.Text.Trim();
		c.TwRewardId           = RewardIdInput.Text.Trim();

		if (int.TryParse(SpotifyFetchRateInput.Text,    out int fr))  c.SpotifyFetchRate      = Math.Max(1, fr);
		if (int.TryParse(SpotifyCallbackPortInput.Text, out int cp))  c.SpotifyCallbackPort   = cp is >= 1024 and <= 65535 ? cp : 8888;
		if (int.TryParse(CooldownInput.Text,         out int cd))  c.TwSrCooldown          = Math.Max(0, cd);
		if (int.TryParse(PerUserCooldownInput.Text,  out int pcd)) c.TwSrPerUserCooldown   = Math.Max(0, pcd);
		if (int.TryParse(MaxSongLengthInput.Text,    out int msl)) c.MaxSongLength         = Math.Max(0, msl);
		if (int.TryParse(MaxQueueInput.Text,         out int mq))  c.MaxQueueLength        = Math.Max(0, mq);
if (int.TryParse(VoteSkipCountInput.Text,    out int vs))  c.VoteSkipCount         = Math.Max(1, vs);

		if (int.TryParse(MaxReqViewer.Text,      out int v))  c.TwSrMaxReqViewer      = Math.Max(0, v);
		if (int.TryParse(MaxReqFollower.Text,    out int f))  c.TwSrMaxReqFollower    = Math.Max(0, f);
		if (int.TryParse(MaxReqSub.Text,         out int s))  c.TwSrMaxReqSubscriber  = Math.Max(0, s);
		if (int.TryParse(MaxReqSubT2.Text,       out int s2)) c.TwSrMaxReqSubscriberT2= Math.Max(0, s2);
		if (int.TryParse(MaxReqSubT3.Text,       out int s3)) c.TwSrMaxReqSubscriberT3= Math.Max(0, s3);
		if (int.TryParse(MaxReqVip.Text,         out int vi)) c.TwSrMaxReqVip         = Math.Max(0, vi);
		if (int.TryParse(MaxReqMod.Text,         out int mo)) c.TwSrMaxReqModerator   = Math.Max(0, mo);
		if (int.TryParse(MaxReqBroadcaster.Text, out int br)) c.TwSrMaxReqBroadcaster = Math.Max(0, br);

		c.BotRespSuccess      = RespSuccessInput.Text;
		c.BotRespError        = RespErrorInput.Text;
		c.BotRespNoSong       = RespNoSongInput.Text;
		c.BotRespBlacklist    = RespBlacklistInput.Text;
		c.BotRespExplicit     = RespExplicitInput.Text;
		c.BotRespTooLong      = RespTooLongInput.Text;
		c.BotRespCooldown     = RespCooldownInput.Text;
		c.BotRespUserCooldown = RespUserCooldownInput.Text;
		c.BotRespMaxReq       = RespMaxReqInput.Text;
		c.BotRespQueueFull    = RespQueueFullInput.Text;
		c.BotRespIsInQueue    = RespIsInQueueInput.Text;
		c.BotRespLevelTooLow  = RespLevelTooLowInput.Text;
		c.BotRespSong         = RespSongInput.Text;
		c.BotRespNext         = RespNextInput.Text;
		c.BotRespPos          = RespPosInput.Text;
		c.BotRespQueue        = RespQueueInput.Text;
		c.BotRespRemove       = RespRemoveInput.Text;
		c.BotRespNoQueue      = RespNoQueueInput.Text;
		c.BotRespSkip         = RespSkipInput.Text;
		c.BotRespVoteSkip     = RespVoteSkipInput.Text;
		c.BotRespSongLike     = RespSongLikeInput.Text;
		c.BotRespToggleSr     = RespToggleSrInput.Text;

		AppConfig.Save();
	}

	private void Cfg_CheckChanged(object sender, RoutedEventArgs e) {
		if (!_configReady) return;
		var c = AppConfig.Instance;
		var botWasEnabled = c.UseBotAccount;
		c.UseBotAccount       = UseBotAccountCheck.IsChecked    == true;
		if (c.UseBotAccount != botWasEnabled) _ = TwitchService.Instance.ApplyBotToggleAsync();
		c.TwAutoConnect       = AutoConnectCheck.IsChecked      == true;
		c.OnlyWorkWhenLive    = OnlyLiveCheck.IsChecked         == true;
		c.AnnounceInChat      = AnnounceInChatCheck.IsChecked   == true;
		c.AddSrToPlaylist     = AddToPlaylistCheck.IsChecked    == true;
		c.LimitSrToPlaylist   = LimitToPlaylistCheck.IsChecked  == true;
		c.BlockAllExplicitSongs   = BlockExplicitCheck.IsChecked   == true;
		c.AutoManageRedemptions   = AutoManageCheck.IsChecked      == true;

		c.StartWithWindows    = StartWithWindowsCheck.IsChecked == true;
		c.MinimizeToTray      = MinimizeToTrayCheck.IsChecked   == true;
		AppConfig.Save();
		UpdateSrStatus();
		UpdateBotStatusBar();
	}


	private void SpotifySecret_Changed(object sender, RoutedEventArgs e) {
		if (!_configReady) return;
		AppConfig.Instance.SpotifyClientSecret = SpotifySecretInput.Password;
		AppConfig.Save();
	}

	private void RequestMode_Changed(object sender, RoutedEventArgs e) {
		if (!_configReady) return;
		if      (ModeBoth.IsChecked    == true) AppConfig.Instance.RequestMode = RequestMode.Both;
		else if (ModeReward.IsChecked  == true) AppConfig.Instance.RequestMode = RequestMode.ChannelReward;
		else                                    AppConfig.Instance.RequestMode = RequestMode.ChatCommand;
		AppConfig.Save();
		UpdateRequestModeVisibility();
	}

	private void UserLevel_Changed(object sender, RoutedEventArgs e) {
		if (!_configReady) return;
		AppConfig.Instance.TwSrUserLevel =
			LevelModerator.IsChecked  == true ? (int)TwitchUserLevel.Moderator  :
			LevelVip.IsChecked        == true ? (int)TwitchUserLevel.Vip        :
			LevelSubscriber.IsChecked == true ? (int)TwitchUserLevel.Subscriber :
			LevelFollower.IsChecked   == true ? (int)TwitchUserLevel.Follower   :
			                                    (int)TwitchUserLevel.Viewer;
		AppConfig.Save();
	}

	// -- Tab switching ---------------------------------------------------------

	private void Tab_Checked(object sender, RoutedEventArgs e) {
		if (MainPanel == null) return;
		MainPanel.Visibility  = TabMain.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
		QueuePanel.Visibility = TabQueue.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
		LogsPanel.Visibility  = TabLogs.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
		ConfigPanel.Visibility= TabConfig.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
		GuidePanel.Visibility = TabGuide.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
	}

	// -- Window chrome ---------------------------------------------------------

	private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
	private void MaximizeClick(object sender, RoutedEventArgs e) =>
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
	private void CloseClick(object sender, RoutedEventArgs e) => Close();

	protected override void OnStateChanged(EventArgs e) {
		base.OnStateChanged(e);
		MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
		OuterBorder.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
	}

	// -- Tray ------------------------------------------------------------------

	private void InitTrayIcon() {
		System.Drawing.Icon icon;
		var sri = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"));
		icon = sri != null ? new System.Drawing.Icon(sri.Stream) : System.Drawing.SystemIcons.Application;

		var menu = new System.Windows.Forms.ContextMenuStrip();
		var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Vibes");
		openItem.Click += (_, _) => ShowFromTray();
		var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
		exitItem.Click += (_, _) => Dispatcher.Invoke(Close);
		menu.Items.Add(openItem);
		menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
		menu.Items.Add(exitItem);

		_trayIcon = new System.Windows.Forms.NotifyIcon {
			Icon = icon, Text = "Vibes", ContextMenuStrip = menu, Visible = false
		};
		_trayIcon.DoubleClick += (_, _) => ShowFromTray();
	}

	private void TrayClick(object sender, RoutedEventArgs e) {
		Hide();
		_trayIcon!.Visible = true;
	}

	private void ShowFromTray() {
		Dispatcher.Invoke(() => {
			Show();
			if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
			Activate();
			_trayIcon!.Visible = false;
		});
	}

	protected override void OnClosed(EventArgs e) {
		_trayIcon?.Dispose();
		base.OnClosed(e);
	}

	// -- Status bar ------------------------------------------------------------

	private void UpdateStatus() {
		if (!TwitchService.Instance.IsConnected) {
			TwitchDot.Fill        = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
			TwitchStatusText.Text = "Twitch: disconnected";
		}
		if (!SpotifyService.Instance.IsAuthorized) {
			SpotifyDot.Fill        = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
			SpotifyStatusText.Text = "Spotify: disconnected";
		}
	}

	// -- Logs tab --------------------------------------------------------------

	private void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => _logView?.Refresh();
	private void LogFilterChanged(object sender, RoutedEventArgs e) => _logView?.Refresh();

	private void CopyLogs_Click(object sender, RoutedEventArgs e) {
		var sb = new System.Text.StringBuilder();
		foreach (var entry in AppLogger.Instance.Entries)
			sb.AppendLine($"{entry.Timestamp:HH:mm:ss.ff} [{entry.LevelShort}] {entry.Message}");
		Clipboard.SetText(sb.ToString());
	}

	private bool LogFilter(object obj) {
		if (obj is not LogEntry entry) return false;
		bool levelOk = entry.Level switch {
			LogLevel.Verbose     => FilterVerbose.IsChecked == true,
			LogLevel.Debug       => FilterDebug.IsChecked   == true,
			LogLevel.Information => FilterInfo.IsChecked    == true,
			LogLevel.Warning     => FilterWarn.IsChecked    == true,
			LogLevel.Error       => FilterError.IsChecked   == true,
			LogLevel.Fatal       => FilterFatal.IsChecked   == true,
			_ => true
		};
		if (!levelOk) return false;
		if (!string.IsNullOrWhiteSpace(LogSearch.Text) &&
			!entry.Message.Contains(LogSearch.Text, StringComparison.OrdinalIgnoreCase))
			return false;
		return true;
	}

	private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) {
		if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0) {
			Dispatcher.BeginInvoke(DispatcherPriority.Background, () => {
				if (LogList.Items.Count > 0)
					LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
			});
		}
	}

	// -- Spotify ---------------------------------------------------------------

	private async void ConnectSpotify_Click(object sender, RoutedEventArgs e) {
		ConnectSpotifyBtn.IsEnabled = false;
		try {
			await SpotifyService.Instance.AuthorizeAsync();
		}
		catch (Exception ex) {
			AppLogger.Instance.Error($"Spotify auth failed: {ex.Message}");
			UpdateSpotifyStatus("Authorization failed");
		}
		finally {
			ConnectSpotifyBtn.IsEnabled = true;
		}
	}

	private void UpdateSpotifyStatus(string status) {
		var connected = SpotifyService.Instance.IsAuthorized;
		SpotifyDot.Fill        = connected
			? new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54))
			: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
		SpotifyStatusText.Text = $"Spotify: {status.ToLower()}";
		if (SpotifyAuthStatus != null) {
			SpotifyAuthStatus.Text       = status;
			SpotifyAuthStatus.Foreground = connected
				? new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54))
				: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
		}
		if (ConnectSpotifyBtn != null)
			ConnectSpotifyBtn.Content = connected ? "Reconnect" : "Connect";
	}

	private void UpdateSrStatus() {
		var enabled = AppConfig.Instance.TwSrCommand || AppConfig.Instance.TwSrReward;
		SrDot.Fill        = enabled
			? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF))
			: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
		SrStatusText.Text = $"Requests: {(enabled ? "on" : "off")}";
	}

	private void UpdateBotStatusBar() {
		var cfg       = AppConfig.Instance;
		var usesBot   = cfg.UseBotAccount;
		var authorized = TwitchService.Instance.IsBotAuthorized;
		var connected  = TwitchService.Instance.IsConnected;
		if (!usesBot) {
			BotDot.Fill       = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
			BotStatusText.Text = "Bot: disabled";
		} else if (!authorized) {
			BotDot.Fill       = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
			BotStatusText.Text = "Bot: not authorized";
		} else {
			BotDot.Fill       = connected
				? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF))
				: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
			BotStatusText.Text = connected ? "Bot: connected" : "Bot: authorized";
		}
	}

	private void OnQueueChanged(List<SpotifyTrackInfo> queue) {
		Dispatcher.Invoke(() => {
			var items = queue.Select((t, i) => new QueueDisplayItem {
				Position  = $"#{i + 1}",
				Title     = t.Title,
				Artist    = t.Artist,
				Requester = SongQueue.GetRequester(t.TrackId) ?? "",
			}).ToList();

			QueueListBox.ItemsSource = items;
			QueueCountText.Text = items.Count > 0 ? $"{items.Count} track{(items.Count == 1 ? "" : "s")}" : "";
		});
	}

	private string? _lastPlayedTrackId;

	private void OnTrackChanged(SpotifyTrackInfo? track) {
		Dispatcher.InvokeAsync(async () => {
			if (track == null) {
				SongTitleText.Text   = "Nothing playing";
				SongArtistText.Text  = "";
				RequesterBadge.Visibility = Visibility.Collapsed;
				AlbumArtImage.Source = null;
				TrackProgress.Value  = 0;
				TrackPositionText.Text = "0:00";
				TrackDurationText.Text = "0:00";
				return;
			}

			// Mark previous track as played when a new one starts
			if (_lastPlayedTrackId != null && _lastPlayedTrackId != track.TrackId)
				SongQueue.MarkPlayed(_lastPlayedTrackId);
			_lastPlayedTrackId = track.TrackId;

			SongTitleText.Text  = track.Title;
			SongArtistText.Text = track.Artist;
			TrackDurationText.Text = FormatMs(track.DurationMs);

			var requester = SongQueue.GetRequester(track.TrackId);
			if (!string.IsNullOrEmpty(requester)) {
				RequesterText.Text = requester;
				RequesterBadge.Visibility = Visibility.Visible;
			}
			else {
				RequesterBadge.Visibility = Visibility.Collapsed;
			}

			if (!string.IsNullOrEmpty(track.AlbumArt)) {
				try {
					var bytes = await _imageHttp.GetByteArrayAsync(track.AlbumArt);
					var bmp   = new BitmapImage();
					bmp.BeginInit();
					bmp.StreamSource = new System.IO.MemoryStream(bytes);
					bmp.CacheOption  = BitmapCacheOption.OnLoad;
					bmp.EndInit();
					AlbumArtImage.Source = bmp;
				}
				catch { AlbumArtImage.Source = null; }
			}
			else {
				AlbumArtImage.Source = null;
			}
		});
	}

	private void TickProgress() {
		var track = SpotifyService.Instance.CurrentTrack;
		if (track == null || track.DurationMs <= 0) return;
		if (!track.IsPlaying) return;

		track.ProgressMs = Math.Min(track.ProgressMs + 1000, track.DurationMs);
		double pct = (double)track.ProgressMs / track.DurationMs * 100;
		TrackProgress.Value    = pct;
		TrackPositionText.Text = FormatMs(track.ProgressMs);
	}

	private static string FormatMs(int ms) {
		var t = TimeSpan.FromMilliseconds(ms);
		return t.Hours > 0
			? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}"
			: $"{t.Minutes}:{t.Seconds:D2}";
	}

	// -- Twitch ----------------------------------------------------------------

	private async void ConnectTwitch_Click(object sender, RoutedEventArgs e) {
		ConnectTwitchBtn.IsEnabled = false;
		try {
			if (!TwitchService.Instance.IsAuthorized)
				await TwitchService.Instance.AuthorizeAsync();
			await TryConnectTwitchAsync();
		}
		catch (Exception ex) {
			AppLogger.Instance.Error($"Twitch auth failed: {ex.Message}");
			UpdateTwitchStatus("Authorization failed");
		}
		finally {
			ConnectTwitchBtn.IsEnabled = true;
		}
	}

	private async void ReauthorizeTwitch_Click(object sender, RoutedEventArgs e) {
		try {
			await TwitchService.Instance.AuthorizeAsync();
			await TryConnectTwitchAsync();
		}
		catch (Exception ex) {
			AppLogger.Instance.Error($"Twitch reauthorize failed: {ex.Message}");
			UpdateTwitchStatus("Reauthorization failed");
		}
	}

	private async Task TryConnectTwitchAsync() {
		try {
			await TwitchService.Instance.ConnectAsync();
		}
		catch (Exception ex) {
			AppLogger.Instance.Error($"Twitch connect failed: {ex.Message}");
			UpdateTwitchStatus("Connection failed");
		}
	}

	private async void AuthorizeBot_Click(object sender, RoutedEventArgs e) {
		AuthorizeBotBtn.IsEnabled = false;
		try {
			await TwitchService.Instance.AuthorizeBotAsync();
			UpdateBotAuthStatus();
		}
		catch (Exception ex) {
			AppLogger.Instance.Error($"Bot authorization failed: {ex.Message}");
		}
		finally {
			AuthorizeBotBtn.IsEnabled = true;
		}
	}

	private void UpdateBotAuthStatus() {
		if (BotAuthStatus == null) return;
		var authorized = TwitchService.Instance.IsBotAuthorized;
		BotAuthStatus.Text       = authorized ? "Authorized" : "Not authorized";
		BotAuthStatus.Foreground = authorized
			? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF))
			: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
	}

	private void UpdateTwitchStatus(string status) {
		var connected = TwitchService.Instance.IsConnected;
		TwitchDot.Fill        = connected
			? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF))
			: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
		TwitchStatusText.Text = $"Twitch: {status.ToLower()}";
		if (TwitchAuthStatus != null) {
			TwitchAuthStatus.Text       = status;
			TwitchAuthStatus.Foreground = connected
				? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF))
				: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
		}
		if (ConnectTwitchBtn != null)
			ConnectTwitchBtn.Content = connected ? "Reconnect" : "Connect";
	}

	// -- Config sidebar nav ----------------------------------------------------

	private void Nav_Checked(object sender, RoutedEventArgs e) {
		if (CatTwitch == null) return;
		CatTwitch.Visibility    = NavTwitch.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
		CatSpotify.Visibility   = NavSpotify.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
		CatRequests.Visibility  = NavRequests.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
		CatLimits.Visibility    = NavLimits.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
		CatResponses.Visibility = NavResponses.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
		CatCommands.Visibility  = NavCommands.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
		CatApp.Visibility       = NavApp.IsChecked       == true ? Visibility.Visible : Visibility.Collapsed;
	}

	private void SettingsSearch_Changed(object sender, TextChangedEventArgs e) {
		if (CatTwitch == null) return;
		var cats = new[] { CatTwitch, CatSpotify, CatRequests, CatLimits, CatResponses, CatCommands, CatApp };
		var q = SettingsSearch.Text.Trim();

		// Always restore all row visibility first
		foreach (var cat in cats)
			foreach (var child in cat.Children)
				if (child is FrameworkElement el) el.Visibility = Visibility.Visible;

		if (string.IsNullOrEmpty(q)) {
			// Restore sidebar selection
			Nav_Checked(sender, e);
			return;
		}

		// Show all categories, hide rows whose Tag doesn't match
		foreach (var cat in cats) cat.Visibility = Visibility.Visible;
		foreach (var cat in cats) {
			foreach (var child in cat.Children) {
				if (child is Border row && row.Tag is string tag)
					row.Visibility = tag.Contains(q, StringComparison.OrdinalIgnoreCase)
						? Visibility.Visible : Visibility.Collapsed;
			}
		}
	}

	// -- Version check ---------------------------------------------------------

	private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) {
		UpdateStatusText.Text = "Checking...";
		UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
		try {
			var (updateAvailable, latest, url) = await VersionInfo.CheckForUpdateAsync();
			if (updateAvailable) {
				UpdateStatusText.Text = $"Update available: {latest}";
				UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF));
				if (url != null) {
					var result = MessageBox.Show($"New version available: {latest}\n\nOpen release page?", "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
					if (result == MessageBoxResult.Yes)
						System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
				}
			}
			else {
				UpdateStatusText.Text = "You're on the latest version.";
				UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
			}
		}
		catch {
			UpdateStatusText.Text = "Failed to check for updates.";
			UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0x45, 0x34));
		}
	}
}

// -- Value converters ----------------------------------------------------------

public class LogLevelToColorConverter : IValueConverter
{
	static readonly Brush VerboseBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55)));
	static readonly Brush DebugBrush   = Frozen(new SolidColorBrush(Color.FromRgb(0xAD, 0xAD, 0xB8)));
	static readonly Brush InfoBrush    = Frozen(new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF)));
	static readonly Brush WarnBrush    = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x1A)));
	static readonly Brush ErrorBrush   = Frozen(new SolidColorBrush(Color.FromRgb(0xEB, 0x45, 0x34)));
	static readonly Brush FatalBrush   = Frozen(new SolidColorBrush(Colors.Red));
	static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		=> value is LogLevel level ? level switch {
			LogLevel.Verbose     => VerboseBrush,
			LogLevel.Debug       => DebugBrush,
			LogLevel.Information => InfoBrush,
			LogLevel.Warning     => WarnBrush,
			LogLevel.Error       => ErrorBrush,
			LogLevel.Fatal       => FatalBrush,
			_ => Brushes.White
		} : Brushes.White;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
