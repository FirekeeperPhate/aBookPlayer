namespace aBookPlayer.Droid;

/// <summary>
/// The book to go on with once one is finished: the next unfinished one of its series, among the books listened to and
/// those in the library folders (the same search as the Windows app's). Reads files: call it off the UI thread.
/// </summary>
static class NextInSeries
{
	public static SeriesCandidate? Find(string path, string? series, int? number, Dictionary<string, BookState> known, List<string> folders)
	{
		if (string.IsNullOrWhiteSpace(series) || number is not int n) return null;
		var candidates = known
			.Where(b => b.Key != path && BookSource.Exists(b.Key))
			.Select(b => new SeriesCandidate(b.Key, b.Value.Title ?? BookSource.DisplayName(b.Key), b.Value.Series, b.Value.SeriesNumber, b.Value.Finished))
			.ToList();

		// Books never opened: their details are usually cached already (the library read them)
		List<string> unopened;
		try
		{
			unopened = LibraryScanner.Scan(folders, CancellationToken.None).Select(Path.GetFullPath)
				.Where(p => p != path && !known.ContainsKey(p)).ToList();
		}
		catch { unopened = []; }
		var unread = new List<string>();
		foreach (var p in unopened)
		{
			if (LibraryDetailsCache.StampOf(p) is { } stamp && LibraryDetailsCache.Get(p, stamp) is { } d)
				candidates.Add(new SeriesCandidate(p, d.Title, d.Series, d.Number, false));
			else unread.Add(p);
		}
		if (SeriesOrder.Next(series, n, candidates) is { } next) return next;

		// Not read yet: the ones stored beside this book, where the rest of a series usually is
		var read = SeriesOrder.Nearby(path, unread).Take(200).Select(p =>
		{
			try
			{
				var (details, _) = BookSource.ReadDetails(p);
				if (LibraryDetailsCache.StampOf(p) is { } stamp) LibraryDetailsCache.Put(p, stamp, details);
				return new SeriesCandidate(p, details.Title, details.Series, details.Number, false);
			}
			catch { return null; }
		}).OfType<SeriesCandidate>().ToList();
		LibraryDetailsCache.Save();
		return SeriesOrder.Next(series, n, read);
	}
}
