namespace Vibes;

public enum TwitchUserLevel
{
	Viewer = 0,
	Follower = 1,
	Subscriber = 2,
	SubscriberT2 = 3,
	SubscriberT3 = 4,
	Vip = 5,
	Moderator = 6,
	Broadcaster = 7
}

public enum CommandType
{
	SongRequest,
	Song,
	Next,
	Skip,
	Voteskip,
	Remove,
	Position,
	Queue,
	Songlike,
	Volume,
	PlayPause,
	Commands,
	BanSong,
	ToggleSr
}

public enum RequestMode
{
	ChatCommand,
	ChannelReward,
	Both
}

public enum SongRequestSource
{
	Command,
	Reward,
	Bits
}

public enum RefundCondition
{
	UserLevelTooLow,
	UserBlocked,
	SpotifyNotConnected,
	SongUnavailable,
	ArtistBlocked,
	SongTooLong,
	SongAlreadyInQueue,
	NoSongFound,
	TrackIsExplicit,
	SongBlocked,
	QueueLimitReached
}
