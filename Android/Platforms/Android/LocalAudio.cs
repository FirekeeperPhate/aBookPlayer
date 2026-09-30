using System.Net;
using System.Text.Json;

namespace aBookPlayer.Droid;

/// <summary>
/// This phone's long MP3 files played the way the PC serves them (see Mp3Segments): Android's player seeks in a
/// long variable-bitrate MP3 through its Xing table, or as if its bitrate were constant, and lands minutes away from
/// the position it shows (the subtitles and the PC follow that position, not the audio). Their parts, each with an
/// exact length, come from a server inside the app that only the phone itself can reach.
/// </summary>
static class LocalAudio
{
	/// <summary>A file's parts, remembered with its size and date (null: played whole).</summary>
	sealed record Split(long Size, DateTime Written, List<Mp3Segments.Segment>? Segments);

	/// <summary>The files being served, by id (a hash of the path).</summary>
	sealed class Files : IRemoteLibrary
	{
		public readonly Dictionary<string, (string File, List<Mp3Segments.Segment> Segments)> ById = [];
		public string Machine => "this phone";
		public IReadOnlyList<RemoteBookSummary> Books() => [];
		public RemoteBook? Book(string id) => null;
		public byte[]? Cover(string id) => null;
		public string? SubtitleFile(string id) => null;
		public bool SetPosition(string id, RemotePosition position) => false;

		public RemotePartData? Part(string id, int index)
		{
			lock (ById)
			{
				if (!ById.TryGetValue(id, out var served) || index < 0 || index >= served.Segments.Count) return null;
				var segment = served.Segments[index];
				return new RemotePartData(served.File, segment.Offset, segment.Length, segment.Prefix);
			}
		}
	}

	// Smaller files are short enough for Android's own seeking (and splitting reads the whole file)
	const long MinimumSize = 4 * 1024 * 1024;
	static readonly Files Library = new();
	static readonly string Key = AccessKey.New();
	static LibraryServer? _server;

	/// <summary>
	/// How the player gets <paramref name="file"/>: its parts' addresses and exact lengths, or null to play the file
	/// itself (not an MP3, a short one, or one of constant bitrate). Reads the whole file the first time (then
	/// remembered): called off the main thread.
	/// </summary>
	public static (Android.Net.Uri[] Parts, TimeSpan[] Lengths)? PartsOf(string file)
	{
		try
		{
			var info = new FileInfo(file);
			if (!file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !info.Exists || info.Length < MinimumSize) return null;
			var id = RemoteIds.For(file);
			var cache = Path.Combine(FileSystem.CacheDirectory, "mp3parts", id + ".json");
			var split = Load(cache);
			if (split == null || split.Size != info.Length || split.Written != info.LastWriteTimeUtc)
			{
				var segments = Mp3Segments.Split(file);
				split = new Split(info.Length, info.LastWriteTimeUtc, segments is { Count: > 1 } ? segments : null);
				Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
				File.WriteAllText(cache, JsonSerializer.Serialize(split));
			}
			if (split.Segments is not { } parts) return null;
			lock (Library.ById) Library.ById[id] = (file, parts);
			int port = Port();
			var key = AccessKey.Normalize(Key);
			return (parts.Select((_, k) => Android.Net.Uri.Parse($"http://127.0.0.1:{port}/api/books/{id}/parts/{k}?key={key}")!).ToArray(),
				parts.Select(p => TimeSpan.FromSeconds(p.Seconds)).ToArray());
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
		{
			return null; // played as it is
		}
	}

	static Split? Load(string cache)
	{
		try { return File.Exists(cache) ? JsonSerializer.Deserialize<Split>(File.ReadAllText(cache)) : null; }
		catch (Exception e) when (e is IOException or JsonException) { return null; }
	}

	/// <summary>The server's port, started the first time (on loopback: other devices cannot reach it).</summary>
	static int Port()
	{
		lock (Library)
		{
			if (_server == null)
			{
				var server = new LibraryServer(Library, Key, AppInfo.VersionString);
				server.Start(0, address: IPAddress.Loopback);
				_server = server;
			}
			return _server.Port;
		}
	}
}
