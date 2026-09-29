using System.Text.Json;

namespace aBookPlayer;

/// <summary>A book's position as last saved on some PC.</summary>
sealed record SyncedPosition(double Seconds, DateTime Updated, bool Finished, string Machine);

/// <summary>
/// Keeps the place in each book in step between PCs through a folder they share (OneDrive, Dropbox, a NAS…).
/// Each PC writes only its own file ("aBookPlayer sync\&lt;PC name&gt;.json"), so cloud sync never has to merge
/// concurrent edits; reading takes the newest position among all files. A book is recognized by its name and
/// size, since it may live in a different folder on each PC.
/// </summary>
static class BookSync
{
    const string SubFolder = "aBookPlayer sync";

    sealed class Entry
    {
        public double Seconds { get; set; }
        public DateTime Updated { get; set; }
        public bool Finished { get; set; }
        public string? Title { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static string? _lastWritten;
    static int _writing;

    /// <summary>
    /// This device's name, as other devices see it ("continuing where you stopped on …") and in its file's name.
    /// The PC's name on Windows; the phone sets its own (Android has no machine name of its own, only "localhost").
    /// </summary>
    public static string MachineName { get; set; } = Environment.MachineName;

    /// <summary>Characters Windows forbids in file names: the file is written by one device and read by all.</summary>
    static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    internal static string FileNameFor(string machine) =>
        string.Concat(machine.Trim().Select(c => c < ' ' || Forbidden.Contains(c) ? '_' : c)).TrimEnd('.', ' ') is { Length: > 0 } name ? name + ".json" : "device.json";

    static string MachineFileName => FileNameFor(MachineName);

    /// <summary>
    /// Identifies a book on another PC, where it may live in a different folder: the Audible ASIN when the
    /// export's tags or the name carry it (it survives renaming, re-exporting and moving the files), otherwise
    /// the file name (or folder name) plus the total size. Null if it cannot be read.
    /// </summary>
    public static string? KeyFor(string path, string? asin = null)
    {
        if (!string.IsNullOrWhiteSpace(asin)) return "asin:" + asin.Trim().ToUpperInvariant();
        try
        {
            if (BookSource.IsFolder(path))
            {
                long size = BookSource.PartsOf(path).Sum(p => new FileInfo(p).Length);
                return $"{BookSource.DisplayName(path).ToLowerInvariant()}|{size}";
            }
            return $"{Path.GetFileName(path).ToLowerInvariant()}|{new FileInfo(path).Length}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The key 1.6 saved this book under, computed exactly as 1.6 did: the raw folder name (with its ASIN, if any)
    /// and the size of every audio file in it, duplicates included. <see cref="KeyFor"/> now differs for Audible
    /// exports (ASIN), for names it cleans up and for folders whose duplicate files are skipped.
    /// </summary>
    public static string? LegacyKeyFor(string path)
    {
        try
        {
            if (BookSource.IsFolder(path))
            {
                long size = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(AudioFormats.IsSupported)
                    .Sum(p => new FileInfo(p).Length);
                return $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).ToLowerInvariant()}|{size}";
            }
            return $"{Path.GetFileName(path).ToLowerInvariant()}|{new FileInfo(path).Length}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The newest position of every book the other devices saved, by key: for a list of books, reading each
    /// device's file once instead of once per book.
    /// </summary>
    public static Dictionary<string, SyncedPosition> ReadAll(string folder)
    {
        var all = new Dictionary<string, SyncedPosition>();
        try
        {
            var dir = Path.Combine(folder, SubFolder);
            if (!Directory.Exists(dir)) return all;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), MachineFileName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file), Json);
                    foreach (var (key, e) in entries ?? [])
                        if (e != null && (!all.TryGetValue(key, out var best) || e.Updated > best.Updated))
                            all[key] = new SyncedPosition(e.Seconds, DateTime.SpecifyKind(e.Updated, DateTimeKind.Utc), e.Finished, Path.GetFileNameWithoutExtension(file));
                }
                catch { /* a file being synced right now, or damaged: skip it */ }
            }
        }
        catch { /* folder offline */ }
        return all;
    }

    /// <summary>
    /// The newest position saved by another PC for this book, if any. <paramref name="legacyKey"/> is the
    /// name-and-size key earlier versions saved under: accepted as well, so positions stored before the books
    /// were recognized by their ASIN are not ignored.
    /// </summary>
    public static SyncedPosition? Find(string folder, string key, string? legacyKey = null)
    {
        SyncedPosition? best = null;
        try
        {
            var dir = Path.Combine(folder, SubFolder);
            if (!Directory.Exists(dir)) return null;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), MachineFileName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file), Json);
                    if (entries == null || !entries.TryGetValue(key, out var e))
                    {
                        if (legacyKey == null || entries == null || !entries.TryGetValue(legacyKey, out e)) continue;
                    }
                    if (best == null || e.Updated > best.Updated)
                        best = new SyncedPosition(e.Seconds, DateTime.SpecifyKind(e.Updated, DateTimeKind.Utc), e.Finished, Path.GetFileNameWithoutExtension(file));
                }
                catch { /* a file being synced right now, or damaged: skip it */ }
            }
        }
        catch { /* folder offline */ }
        return best;
    }

    /// <summary>Writes this PC's positions (in the background; skipped if nothing changed or a write is still running).</summary>
    public static Task Publish(string folder, IEnumerable<BookState> books)
    {
        var entries = new Dictionary<string, Entry>();
        foreach (var b in books)
            if (b.SyncKey != null && (!entries.TryGetValue(b.SyncKey, out var existing) || existing.Updated < b.EffectivePositionUpdated))
                entries[b.SyncKey] = new Entry { Seconds = b.PositionSeconds, Updated = b.EffectivePositionUpdated, Finished = b.Finished, Title = b.Title };
        var text = JsonSerializer.Serialize(entries, Json);
        var written = folder + "|" + text; // another folder must get the file even with the same content
        if (written == _lastWritten || Interlocked.Exchange(ref _writing, 1) == 1) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                var dir = Path.Combine(folder, SubFolder);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, MachineFileName);
                File.WriteAllText(file + ".tmp", text);
                File.Move(file + ".tmp", file, overwrite: true);
                _lastWritten = written;
            }
            catch { /* offline: try again at the next save */ }
            finally
            {
                Interlocked.Exchange(ref _writing, 0);
            }
        });
    }
}
