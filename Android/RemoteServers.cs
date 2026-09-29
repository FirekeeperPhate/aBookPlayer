namespace aBookPlayer.Droid;

/// <summary>A PC whose library this phone reads (File → Share with your phone on the PC).</summary>
sealed class RemoteServer
{
    /// <summary>"192.168.1.20:52780".</summary>
    public string Address { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>The PC's name, as it introduced itself ("MYPC").</summary>
    public string Machine { get; set; } = "";
}

/// <summary>
/// The PCs connected, and the books they serve. A remote book is known by a path of its own,
/// "abook://192.168.1.20:52780/&lt;id&gt;", so its state (position, speed, bookmarks) is kept like a local book's.
/// </summary>
static class RemoteBooks
{
    const string Scheme = "abook://";
    static readonly Dictionary<string, RemoteLibraryClient> Clients = [];

    public static bool IsRemote(string path) => path.StartsWith(Scheme, StringComparison.Ordinal);

    public static string PathOf(string address, string id) => $"{Scheme}{address}/{id}";

    /// <summary>The server's address and the book's id, for a remote book's path.</summary>
    public static (string Address, string Id)? Parse(string path)
    {
        if (!IsRemote(path)) return null;
        var rest = path[Scheme.Length..];
        int slash = rest.LastIndexOf('/');
        return slash > 0 ? (rest[..slash], rest[(slash + 1)..]) : null;
    }

    public static RemoteServer? ServerOf(string path) =>
        Parse(path) is { } p ? App.Settings.Servers.FirstOrDefault(s => s.Address == p.Address) : null;

    /// <summary>The client for a connected PC (one per address, kept: it holds the connections).</summary>
    public static RemoteLibraryClient Client(RemoteServer server)
    {
        lock (Clients)
        {
            if (Clients.TryGetValue(server.Address, out var client) && client.Key == AccessKey.Normalize(server.Key)) return client;
            client?.Dispose();
            return Clients[server.Address] = new RemoteLibraryClient(server.Address, server.Key);
        }
    }

    /// <summary>
    /// Tells a PC where this phone is in one of its books, when that moved since the last time (in the background;
    /// if the PC does not answer, it is told later, by <see cref="SendPending"/>).
    /// </summary>
    public static void SendPosition(string path, BookState book)
    {
        if (ServerOf(path) is not { } server || Parse(path) is not { } p) return;
        var updated = book.EffectivePositionUpdated;
        if (updated == default || (App.Settings.SentPositions.TryGetValue(path, out var sent) && sent >= updated)) return;
        var position = new RemotePosition(book.PositionSeconds, DateTime.SpecifyKind(updated, DateTimeKind.Utc), book.Finished, BookSync.MachineName);
        var client = Client(server);
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.SendPositionAsync(p.Id, position, timeout.Token);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    App.Settings.SentPositions[path] = updated;
                    App.Settings.Save();
                });
            }
            catch { /* away from home, or the PC is off: told later */ }
        });
    }

    /// <summary>The positions a PC was not told about (listened to away from home), now that it answers.</summary>
    public static void SendPending(ICollection<string> reachable)
    {
        foreach (var (path, book) in App.Settings.Books.ToList())
            if (Parse(path) is { } p && reachable.Contains(p.Address)) SendPosition(path, book);
    }

    /// <summary>"The PC did not accept the key", "MYPC cannot be reached…": what went wrong, for a message.</summary>
    public static string Explain(Exception ex, string who) => ex switch
    {
        UnauthorizedAccessException => $"{who} did not accept the access key. Check it in aBookPlayer on the PC: File → Share with your phone.",
        FormatException => ex.Message,
        _ => $"{who} cannot be reached ({ex.Message}).\n\nIs aBookPlayer open on the PC, with File → Share with your phone turned on, " +
             "and is this phone on the same network? The first time, Windows may ask to allow aBookPlayer through its firewall.",
    };
}
