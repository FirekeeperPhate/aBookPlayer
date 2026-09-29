namespace aBookPlayer;

/// <summary>What a book's files say about it: the library shows it for books never opened.</summary>
sealed record BookDetails(string Title, string? Author, string? Series, int? Number);

/// <summary>
/// A book is either a single audio file or a folder of audio files (one per chapter, often split in
/// "CD 1", "CD 2"… subfolders) played in Explorer's order as one continuous book. Opening its audio is up to each
/// app (NAudio on Windows); this is about its files, names, subtitles and details.
/// </summary>
static class BookSource
{
    public static bool IsFolder(string path) => Directory.Exists(path);

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>The audio files of a folder book, in Explorer's natural order ("2" before "10").</summary>
    public static string[] PartsOf(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(AudioFormats.IsSupported)
            .Select(f => Path.GetRelativePath(folder, f))
            .ToList();
        files.Sort(NaturalCompare);
        return AudibleExport.SelectParts(files.Select(f => Path.Combine(folder, f))).ToArray();
    }

    /// <summary>
    /// Natural order of file names ("2" before "10"). The Windows app sets it to Explorer's own comparison, so the
    /// parts play exactly in the order the user sees them; elsewhere <see cref="NaturalOrder.Compare"/>.
    /// </summary>
    public static Comparison<string> NaturalCompare { get; set; } = NaturalOrder.Compare;

    /// <summary>Book name for lists: the file name without extension, or the folder name.</summary>
    public static string DisplayName(string path) =>
        AudibleExport.DisplayName(IsFolder(path)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : Path.GetFileNameWithoutExtension(path));

    /// <summary>Like <see cref="DisplayName"/> but without touching the disk (for lists that may include offline drives).</summary>
    public static string NameFromPath(string path) =>
        AudibleExport.DisplayName(AudioFormats.IsSupported(path)
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));

    /// <summary>Where the book's subtitles go: next to the file, or inside the folder named after it.</summary>
    public static string SubtitlePath(string path) =>
        IsFolder(path) ? Path.Combine(path, DisplayName(path) + ".srt") : Path.ChangeExtension(path, ".srt");

    /// <summary>The English translation made by the transcription ("Book.en.srt"), shown under the subtitles.</summary>
    public static string TranslationPath(string path) => Path.ChangeExtension(SubtitlePath(path), ".en.srt");

    /// <summary>
    /// The subtitles to load with this book, if there are any. An Audible export (Libation) keeps them in the
    /// book's folder under its own long name ("Book_ Series, Book 2 [ASIN].srt"), not under the folder's name.
    /// </summary>
    public static string? FindSubtitle(string path)
    {
        var named = SubtitlePath(path);
        if (File.Exists(named)) return named;
        if (!IsFolder(path)) return null;
        try
        {
            var translation = TranslationPath(path);
            var candidates = Directory.EnumerateFiles(path, "*.srt")
                .Select(f => (Path: f, Name: Path.GetFileNameWithoutExtension(f)))
                // The subtitle file of one chapter covers only part of the book: never load it for all of it
                .Where(c => AudibleExport.Parse(c.Name).Part == null)
                .OrderByDescending(c => new FileInfo(c.Path).Length)
                .ToList();
            // The translation goes under the subtitles, not in their place (unless it is all there is)
            if (candidates.Count > 1) candidates.RemoveAll(c => string.Equals(c.Path, translation, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 0) return null;
            var asin = AudibleExport.Parse(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))).Asin;
            return (candidates.FirstOrDefault(c => asin != null && c.Name.Contains(asin, StringComparison.OrdinalIgnoreCase)).Path
                    ?? candidates[0].Path);
        }
        catch { return null; }
    }

    /// <summary>
    /// Title, author, series and cover of a book never opened, from the first file's tags or the export's names,
    /// without opening its audio. Reads the disk: call it off the UI thread.
    /// </summary>
    public static (BookDetails Details, byte[]? Cover) ReadDetails(string path)
    {
        bool folder = IsFolder(path);
        var first = folder ? PartsOf(path).FirstOrDefault() : path;
        var info = first != null ? MediaMetadata.Read(first) : new MediaInfo();
        var name = AudibleExport.Parse(folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path));
        var title = (folder ? info.Album : info.Title) ?? name.Title ?? DisplayName(path);
        var cover = info.Cover ?? CoverArt.FromFolder(folder ? path : Path.GetDirectoryName(path), bookFolder: folder);
        return (new BookDetails(title, info.Artist, info.Series ?? name.Series, info.SeriesNumber ?? name.SeriesNumber), cover);
    }

    /// <summary>Title, author, chapters and cover of a single-file book.</summary>
    public static MediaInfo ReadFileInfo(string path)
    {
        var info = MediaMetadata.Read(path);
        info.Cover ??= CoverArt.FromFolder(Path.GetDirectoryName(path), bookFolder: false);
        return info;
    }

    /// <summary>
    /// Title, author, chapters and cover of a folder book: one chapter per file (or the file's own chapters), placed
    /// at <paramref name="partStart"/>(i), the start of part i in the book, which only the audio can tell
    /// (<paramref name="partStart"/>(parts.Count) is the book's length).
    /// </summary>
    public static MediaInfo ReadFolderInfo(string path, IReadOnlyList<string> parts, Func<int, TimeSpan> partStart)
    {
        var book = new MediaInfo();
        for (int i = 0; i < parts.Count; i++)
        {
            var part = MediaMetadata.Read(parts[i]);
            var start = partStart(i);
            if (i == 0)
            {
                // Audiobook rips usually put the book title in the album tag and the author in the artist
                book.Title = part.Album ?? DisplayName(path);
                book.Artist = part.Artist;
                book.Cover = part.Cover;
            }
            book.Asin ??= part.Asin;
            book.Series ??= part.Series;
            book.SeriesNumber ??= part.SeriesNumber;
            if (part.Chapters.Count > 1)
                foreach (var c in part.Chapters)
                    book.Chapters.Add(new Chapter(c.Title, start + c.Start, c.End > TimeSpan.Zero ? start + c.End : TimeSpan.Zero));
            else
                book.Chapters.Add(new Chapter(ChapterName(parts[i], part), start, partStart(i + 1)));
        }
        // The folder name carries the export's title, series and ASIN even when the tags do not (as ReadDetails
        // reads them for the library: the book must not change series once opened)
        var folder = AudibleExport.Parse(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        book.Title ??= folder.Title;
        book.Asin ??= folder.Asin;
        book.Series ??= folder.Series;
        book.SeriesNumber ??= folder.SeriesNumber;
        book.Cover ??= CoverArt.FromFolder(path);
        return book;
    }

    /// <summary>
    /// Chapter title of one file of a folder book: the export names each file after its chapter
    /// ("… - 07 - Chapter 6"), which beats the tag, where the book and the number are repeated
    /// ("2 - Dungeon Crawler Carl: Chapter 1").
    /// </summary>
    static string ChapterName(string path, MediaInfo info)
    {
        var name = AudibleExport.Parse(Path.GetFileNameWithoutExtension(path));
        if (name.Chapter != null && (name.Asin != null || info.Asin != null)) return name.Chapter;
        return info.Title ?? Path.GetFileNameWithoutExtension(path);
    }
}

/// <summary>
/// Natural order of names, like Windows Explorer: runs of digits compare as numbers ("CD 2" before "CD 10",
/// "02" and "2" alike), the rest ignoring case.
/// </summary>
static class NaturalOrder
{
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                // Compare as numbers of any length: without leading zeros, the longer is larger, else digit by digit
                var x = a.AsSpan(si, i - si).TrimStart('0');
                var y = b.AsSpan(sj, j - sj).TrimStart('0');
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                int digits = x.SequenceCompareTo(y);
                if (digits != 0) return Math.Sign(digits);
                // Same number written with more leading zeros ("02" and "2") comes first, as in Explorer
                if (i - si != j - sj) return (j - sj).CompareTo(i - si);
                continue;
            }
            int c = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
            if (c != 0) return c;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
