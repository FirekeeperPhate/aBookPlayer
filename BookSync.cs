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

    static string MachineFileName => string.Concat(Environment.MachineName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json";

    /// <summary>File or folder name plus total size, e.g. "the hobbit.m4b|412345678"; null if it cannot be read.</summary>
    public static string? KeyFor(string path)
    {
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

    /// <summary>The newest position saved by another PC for this book, if any.</summary>
    public static SyncedPosition? Find(string folder, string key)
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
                    if (entries != null && entries.TryGetValue(key, out var e) && (best == null || e.Updated > best.Updated))
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
