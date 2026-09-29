using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AndroidApp = Android.App.Application;

namespace aBookPlayer.Droid;

/// <summary>
/// Books of a PC's library copied to this phone, to listen to away from home. The app fetches the audio files
/// itself, one after the other, going on from where it stopped after a lost connection or when the app is opened
/// again (Android's download manager cannot reach the home network on recent Android). The details, cover and
/// subtitles are saved beside the files, in this app's folder on the phone. A downloaded book stays the PC's book
/// (same position, bookmarks and sync): the player just reads its files from the phone.
/// </summary>
static class Downloads
{
	/// <summary>What is kept about a download, in "book.json" beside its files.</summary>
	sealed class Record
	{
		public string Address { get; set; } = "";
		public string Machine { get; set; } = "";
		public RemoteBook Book { get; set; } = null!;
		public bool Complete { get; set; }
	}

	public enum Status { None, Downloading, Complete, Failed }

	/// <summary>A downloaded book, as the library and the player need it.</summary>
	public sealed record Copy(string Path, string Machine, RemoteBook Book, string[] Parts, string? Cover, string? Subtitles);

	/// <summary>A download going on: how far, and how to stop it.</summary>
	sealed class Running
	{
		public required CancellationTokenSource Stop { get; init; }
		public double Progress;
		public string? Error;
	}

	static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
	static readonly Dictionary<string, Running> Active = [];

	/// <summary>This app's folder on the phone's storage (no permission needed; removed with the app).</summary>
	static string Root => System.IO.Path.Combine(AndroidApp.Context.GetExternalFilesDir(null)!.AbsolutePath, "books");

	/// <summary>The book's folder, named after its path (the PC's address and the book's id).</summary>
	static string FolderOf(string path) =>
		System.IO.Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..20].ToLowerInvariant());

	static string PartFile(string folder, RemoteBook book, int index) =>
		System.IO.Path.Combine(folder, $"{index + 1:000}{System.IO.Path.GetExtension(book.Parts[index].Name).ToLowerInvariant()}");

	static Record? Load(string folder)
	{
		try
		{
			var file = System.IO.Path.Combine(folder, "book.json");
			return File.Exists(file) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(file), Json) : null;
		}
		catch { return null; }
	}

	static void Save(string folder, Record record)
	{
		var file = System.IO.Path.Combine(folder, "book.json");
		File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(record, Json));
		File.Move(file + ".tmp", file, overwrite: true);
	}

	/// <summary>Starts copying a PC's book to the phone: its details, cover and subtitles, then its audio files.</summary>
	public static async Task StartAsync(string path, RemoteServer server)
	{
		ClearError(path);
		lock (Active)
			if (Active.ContainsKey(path)) return;
		var client = RemoteBooks.Client(server);
		var id = RemoteBooks.Parse(path)!.Value.Id;
		var book = await client.BookAsync(id);
		var folder = FolderOf(path);
		Directory.CreateDirectory(folder);
		if (book.HasCover && await TryAsync(() => client.CoverAsync(id)) is { } cover)
			await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, "cover"), cover);
		if (book.HasSubtitles && await TryAsync(() => client.SubtitlesAsync(id)) is { } srt)
			await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, "subtitles.srt"), srt);
		Save(folder, new Record { Address = server.Address, Machine = server.Machine, Book = book });
		Run(path, folder, server, book);

		static async Task<byte[]?> TryAsync(Func<Task<byte[]?>> get)
		{
			try { return await get(); }
			catch { return null; }
		}
	}

	/// <summary>Goes on with the downloads left unfinished (the app was closed, the PC was off), for the PCs that answered.</summary>
	public static void Resume(ICollection<string> reachable)
	{
		try
		{
			foreach (var (folder, record) in Records().ToList())
			{
				if (record.Complete) continue;
				var path = RemoteBooks.PathOf(record.Address, record.Book.Id);
				lock (Active)
					if (Active.ContainsKey(path)) continue;
				if (reachable.Contains(record.Address) && RemoteBooks.ServerOf(path) is { } server) Run(path, folder, server, record.Book);
			}
		}
		catch { /* storage not available */ }
	}

	/// <summary>Fetches the audio files not yet on the phone, in the background.</summary>
	static void Run(string path, string folder, RemoteServer server, RemoteBook book)
	{
		var running = new Running { Stop = new CancellationTokenSource() };
		lock (Active)
		{
			if (Active.ContainsKey(path)) return;
			Active[path] = running;
		}
		_ = Task.Run(async () =>
		{
			var client = RemoteBooks.Client(server);
			int count = book.Parts.Count;
			try
			{
				for (int i = 0; i < count; i++)
				{
					var file = PartFile(folder, book, i);
					if (File.Exists(file)) continue;
					int index = i;
					// Into ".part" first: a file under its own name is complete
					await client.DownloadPartAsync(book.Id, i, file + ".part",
						(have, total) => running.Progress = (index + (total > 0 ? (double)have / total : 0)) / count, running.Stop.Token);
					File.Move(file + ".part", file, overwrite: true);
				}
				if (Load(folder) is { } record)
				{
					record.Complete = true;
					Save(folder, record);
				}
				running.Progress = 1;
			}
			catch (OperationCanceledException) when (running.Stop.IsCancellationRequested) { /* stopped */ }
			catch (Exception ex)
			{
				// Kept (with its error) until tried again
				running.Error = RemoteBooks.Explain(ex, server.Machine);
			}
			finally
			{
				lock (Active)
					if (running.Error == null && Active.TryGetValue(path, out var current) && current == running) Active.Remove(path);
			}
		});
	}

	/// <summary>Stops a download, or deletes a downloaded book (the PC keeps it, and it can still be streamed).</summary>
	public static void Remove(string path)
	{
		lock (Active)
		{
			if (Active.Remove(path, out var running)) running.Stop.Cancel();
		}
		var folder = FolderOf(path);
		// The copy may still be closing its file: tried again for a moment
		_ = Task.Run(async () =>
		{
			for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
			{
				try { Directory.Delete(folder, recursive: true); }
				catch { await Task.Delay(300); }
			}
		});
	}

	/// <summary>Where the book's download is, and how far (0–1) while it goes on.</summary>
	public static (Status Status, double Progress) StatusOf(string path)
	{
		lock (Active)
		{
			if (Active.TryGetValue(path, out var running))
				return running.Error != null ? (Status.Failed, running.Progress) : (Status.Downloading, running.Progress);
		}
		if (Load(FolderOf(path)) is not { } record) return (Status.None, 0);
		// Unfinished and not going on (its PC is not connected): taken up again by trying again
		return record.Complete ? (Status.Complete, 1) : (Status.Failed, 0);
	}

	/// <summary>Why a download stopped, to show.</summary>
	public static string? ErrorOf(string path)
	{
		lock (Active) return Active.TryGetValue(path, out var running) ? running.Error : null;
	}

	static void ClearError(string path)
	{
		lock (Active)
			if (Active.TryGetValue(path, out var running) && running.Error != null) Active.Remove(path);
	}

	/// <summary>The book's copy on the phone, when it is complete.</summary>
	public static Copy? CopyOf(string path) => CopyIn(FolderOf(path), path, Load(FolderOf(path)));

	static Copy? CopyIn(string folder, string path, Record? record)
	{
		if (record is not { Complete: true }) return null;
		var parts = Enumerable.Range(0, record.Book.Parts.Count).Select(i => PartFile(folder, record.Book, i)).ToArray();
		if (!parts.All(File.Exists)) return null;
		var cover = System.IO.Path.Combine(folder, "cover");
		var subtitles = System.IO.Path.Combine(folder, "subtitles.srt");
		return new Copy(path, record.Machine, record.Book, parts, File.Exists(cover) ? cover : null, File.Exists(subtitles) ? subtitles : null);
	}

	static IEnumerable<(string Folder, Record Record)> Records()
	{
		if (!Directory.Exists(Root)) yield break;
		foreach (var folder in Directory.EnumerateDirectories(Root))
			if (Load(folder) is { } record) yield return (folder, record);
	}

	/// <summary>Every book copied to the phone (for the library, also when its PC cannot be reached).</summary>
	public static List<Copy> All()
	{
		try
		{
			return Records().Select(r => CopyIn(r.Folder, RemoteBooks.PathOf(r.Record.Address, r.Record.Book.Id), r.Record))
				.OfType<Copy>().ToList();
		}
		catch { return []; } // storage not available
	}
}
