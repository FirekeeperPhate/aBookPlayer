using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace aBookPlayer;

// The library shared with phones on the same network (see LibraryServer): File → Share with your phone
public sealed partial class MainForm
{
    LibraryServer? _server;

    /// <summary>Starts (or restarts, after a change) serving the library, if it is shared.</summary>
    void StartSharing(bool interactive = false)
    {
        _server?.Dispose();
        _server = null;
        if (!_settings.ShareLibrary) return;
        _settings.ShareKey ??= AccessKey.New();
        var server = new LibraryServer(new SharedLibrary(LibrarySnapshot, PhonePositionArrived), _settings.ShareKey, UpdateCheck.CurrentVersion.ToString());
        try
        {
            server.Start(_settings.SharePort);
            _server = server;
        }
        catch (SocketException ex)
        {
            server.Dispose();
            var message = $"The library cannot be shared on port {_settings.SharePort}: {ex.Message}";
            if (interactive) MessageBox.Show(this, message + "\n\nChoose another port.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else ShowOsd(message);
        }
    }

    /// <summary>What the server needs from the settings, copied on the UI thread (they change there).</summary>
    SharedLibrary.Snapshot? LibrarySnapshot()
    {
        if (IsDisposed) return null;
        try
        {
            return Invoke(() => new SharedLibrary.Snapshot(_settings.LibraryFolders.ToList(), new Dictionary<string, BookState>(_settings.Books)));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return null; // closing
        }
    }

    /// <summary>A phone posted its position (on the server's thread): handled on the UI thread, where the settings live.</summary>
    void PhonePositionArrived(string path, RemotePosition position, RemoteBook? details)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => ApplyPhonePosition(path, position, details)); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* closing */ }
    }

    /// <summary>
    /// Where a phone is in a book: kept when it is newer than this PC's position (as a position synced through the
    /// shared folder), and the open book, paused, moves there. A book playing here keeps its own.
    /// </summary>
    void ApplyPhonePosition(string path, RemotePosition position, RemoteBook? details)
    {
        bool open = _audioPath != null && SamePath(path, _audioPath);
        if (open && _player.IsPlaying) return;
        var synced = new SyncedPosition(position.Seconds, DateTime.SpecifyKind(position.Updated, DateTimeKind.Utc), position.Finished, position.Machine);
        if (open)
        {
            var local = CurrentBook;
            if (local == null || synced.Updated <= local.EffectivePositionUpdated.AddSeconds(2)) return;
            var target = TimeSpan.FromSeconds(synced.Seconds);
            if (_player.IsLoaded && target < _player.Duration)
            {
                _keepSavedPosition = false;
                _pausedSince = synced.Updated.ToLocalTime(); // smart rewind as if paused over there
                _player.Seek(target);
                FollowSleepChapter(target);
                ShowOsd($"Moved to {FormatTime(target)}, where you stopped on {synced.Machine}");
            }
            local.PositionSeconds = synced.Seconds;
            local.PositionUpdated = synced.Updated;
            local.Finished = synced.Finished;
        }
        else
        {
            if (ApplySyncedPosition(path, synced) == null) return;
            // A book never opened here: what the phone listened to shows in the library like any other
            if (details != null && _settings.GetBook(path) is { } book)
            {
                book.Title ??= details.Title;
                book.Author ??= details.Author;
                book.Series ??= details.Series;
                book.SeriesNumber ??= details.Number;
                book.Asin ??= details.Asin;
                book.SyncKey ??= details.SyncKey;
                if (book.DurationSeconds <= 0) book.DurationSeconds = details.Parts.Sum(p => p.Seconds);
            }
        }
        SaveSettings();
        UpdateUi();
    }

    void ShowShareOptions()
    {
        using (var dlg = new ShareOptionsForm(_settings.ShareLibrary, _settings.SharePort, _settings.ShareKey ?? AccessKey.New()))
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                bool changed = dlg.Share != _settings.ShareLibrary || dlg.Port != _settings.SharePort || dlg.Key != _settings.ShareKey
                               || (dlg.Share && _server == null);
                (_settings.ShareLibrary, _settings.SharePort, _settings.ShareKey) = (dlg.Share, dlg.Port, dlg.Key);
                SaveSettings();
                if (changed) StartSharing(interactive: true);
                if (_server != null && changed) ShowOsd("The library is shared on this network");
            }
        }
        OpenDeferredFile();
    }
}

/// <summary>Turns the library's sharing on or off, and shows how a phone connects: a code to scan, or an address and the key.</summary>
sealed class ShareOptionsForm : DarkDialog
{
    readonly CheckBox _share = new() { Text = "Share the library with phones on this network", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly NumericUpDown _port = new()
    {
        Minimum = 1024, Maximum = 65535, Width = 90, BackColor = Theme.Surface, ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center,
    };
    readonly TextBox _key = MakeTextBox();
    readonly Label _addresses = new() { AutoSize = false, ForeColor = Theme.Text };
    readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };

    public bool Share => _share.Checked;
    public int Port => (int)_port.Value;
    public string Key { get; private set; }

    public ShareOptionsForm(bool share, int port, string key) : base("Share with your phone", new Size(760, 396))
    {
        Key = key;
        var info = new Label
        {
            Text = "aBookPlayer on Android can list the books of this PC, play them streaming and copy them to the phone, while this " +
                   "app is open and the phone is on the same network. On the phone, scan the code with the camera, or: Library → ⋮ → " +
                   "Connect to a PC, which finds this PC (or type an address), then the key.",
            Location = new Point(18, 16), Size = new Size(724, 60), ForeColor = Theme.TextDim,
        };
        _share.Checked = share;
        _share.Location = new Point(18, 90);

        var portLabel = new Label { Text = "Port", AutoSize = true, Location = new Point(18, 130) };
        _port.Value = Math.Clamp(port, 1024, 65535);
        _port.Location = new Point(110, 126);

        var keyLabel = new Label { Text = "Access key", AutoSize = true, Location = new Point(18, 168) };
        _key.ReadOnly = true;
        _key.Text = key;
        _key.SetBounds(110, 164, 190, 28);
        var newKey = DialogControls.MakeButton("New key", 104);
        newKey.Location = new Point(310, 162);
        // A new key: phones connected with the old one must be given this one
        newKey.Click += (_, _) =>
        {
            _key.Text = Key = AccessKey.New();
            ShowAddresses();
        };

        var addressLabel = new Label { Text = "Address", AutoSize = true, Location = new Point(18, 208) };
        _addresses.SetBounds(110, 208, 320, 80);

        // The code opens a page on the phone whose button opens the app, connected to this PC
        _qr.SetBounds(530, 86, 190, 190);
        var qrLabel = new Label
        {
            Text = "Scan with the phone's camera (once sharing is on)", Location = new Point(500, 282), Size = new Size(250, 40),
            ForeColor = Theme.TextDim, TextAlign = ContentAlignment.TopCenter,
        };
        _port.ValueChanged += (_, _) => ShowAddresses();
        ShowAddresses();

        Controls.AddRange([info, _share, portLabel, _port, keyLabel, _key, newKey, addressLabel, _addresses, _qr, qrLabel]);
        AcceptButton = AddButton("OK", DialogResult.OK);
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
    }

    /// <summary>This PC's addresses on its networks (Wi-Fi, Ethernet), with the port: one of them is typed on the phone.</summary>
    void ShowAddresses()
    {
        var addresses = LocalAddresses().Select(a => $"{a}:{Port}").ToList();
        _addresses.Text = addresses.Count > 0 ? string.Join("\n", addresses.Take(4)) : "(this PC is not connected to a network)";
        // The code for the first address, the home network's
        var old = _qr.Image;
        _qr.Image = addresses.Count > 0 ? QrImage($"http://{addresses[0]}/connect?key={AccessKey.Normalize(Key)}") : null;
        old?.Dispose();
    }

    static Image QrImage(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        using var stream = new MemoryStream(png);
        // A copy: an image read from a stream needs the stream for as long as it lives
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _qr.Image?.Dispose();
        base.Dispose(disposing);
    }

    static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                // The home network first: an Ethernet or Wi-Fi adapter with a router. VPN tunnels (WireGuard, OpenVPN)
                // and virtual switches (Hyper-V) are rarely what the phone can reach
                .OrderBy(Rank)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        static int Rank(NetworkInterface n)
        {
            bool router = n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            bool physical = n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                            && !new[] { "Virtual", "VPN", "WireGuard", "TAP-", "Hyper-V" }.Any(w => n.Description.Contains(w, StringComparison.OrdinalIgnoreCase));
            return (router, physical) switch { (true, true) => 0, (false, true) => 1, (true, false) => 2, _ => 3 };
        }
    }
}

/// <summary>
/// The library as the server serves it: the books in the library folders and those listened to, their details (from
/// the history, or read from the files and cached), their files and lengths (read when a phone opens one).
/// Called on the server's threads: the settings are copied through <see cref="Snapshot"/>.
/// </summary>
sealed class SharedLibrary(Func<SharedLibrary.Snapshot?> snapshot, Action<string, RemotePosition, RemoteBook?>? positionArrived = null) : IRemoteLibrary
{
    public sealed record Snapshot(List<string> Folders, Dictionary<string, BookState> Books);

    sealed record Opened(string Stamp, RemoteBook Book, string[] Parts, string? Subtitles);

    readonly object _gate = new();
    Dictionary<string, string> _paths = [];
    List<RemoteBookSummary> _list = [];
    DateTime _listed;
    readonly Dictionary<string, Opened> _opened = new(StringComparer.OrdinalIgnoreCase);

    public string Machine => BookSync.MachineName;

    public IReadOnlyList<RemoteBookSummary> Books() => List(fresh: false);

    List<RemoteBookSummary> List(bool fresh)
    {
        lock (_gate)
            if (!fresh && DateTime.UtcNow - _listed < TimeSpan.FromSeconds(10)) return _list;
        if (snapshot() is not { } snap) return [];
        var history = new Dictionary<string, BookState>(snap.Books, StringComparer.OrdinalIgnoreCase);
        var paths = LibraryScanner.Scan(snap.Folders, CancellationToken.None).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var known in history.Keys)
            if (BookSource.Exists(known)) paths.Add(Path.GetFullPath(known));

        var ids = new Dictionary<string, string>();
        var list = new List<RemoteBookSummary>();
        foreach (var path in paths)
        {
            var id = RemoteIds.For(path);
            ids[id] = path;
            var state = history.GetValueOrDefault(path);
            var entry = new LibraryEntry { Path = path, State = state, Scanned = state == null ? Details(path) : null };
            list.Add(new RemoteBookSummary(id, entry.Title, entry.Author, entry.Series, entry.SeriesNumber,
                state?.DurationSeconds ?? 0, state?.PositionSeconds ?? 0, state?.EffectivePositionUpdated ?? default, state?.Finished ?? false, state?.SyncKey ?? KeyOf(path)));
        }
        LibraryDetailsCache.Save();
        list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        lock (_gate)
        {
            _paths = ids;
            _list = list;
            _listed = DateTime.UtcNow;
        }
        return list;
    }

    /// <summary>
    /// The key a book never opened here would be synced under (ASIN from the export's name, or name and size): a
    /// phone with the same book in its own folder then knows it is the same.
    /// </summary>
    static string? KeyOf(string path)
    {
        var name = BookSource.IsFolder(path) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);
        return BookSync.KeyFor(path, AudibleExport.Parse(name).Asin);
    }

    static BookDetails? Details(string path)
    {
        var stamp = LibraryDetailsCache.StampOf(path);
        if (stamp != null && LibraryDetailsCache.Get(path, stamp) is { } known) return known;
        try
        {
            var (details, _) = BookSource.ReadDetails(path);
            if (stamp != null) LibraryDetailsCache.Put(path, stamp, details);
            return details;
        }
        catch { return null; }
    }

    /// <summary>The book's path; a book added since the last list is found by listing again.</summary>
    string? PathOf(string id)
    {
        lock (_gate)
            if (_paths.TryGetValue(id, out var path)) return path;
        List(fresh: true);
        lock (_gate) return _paths.GetValueOrDefault(id);
    }

    BookState? StateOf(string path) =>
        snapshot() is { } snap ? new Dictionary<string, BookState>(snap.Books, StringComparer.OrdinalIgnoreCase).GetValueOrDefault(path) : null;

    public RemoteBook? Book(string id)
    {
        if (PathOf(id) is not { } path || Open(id, path) is not { } opened) return null;
        // Where this PC is in the book now (it moves while it plays here)
        var state = StateOf(path);
        return opened.Book with
        {
            PositionSeconds = state?.PositionSeconds ?? 0,
            PositionUpdated = state?.EffectivePositionUpdated ?? default,
            Finished = state?.Finished ?? false,
        };
    }

    /// <summary>The book's files, lengths and chapters: read once (it decodes their headers), again if the book changed.</summary>
    Opened? Open(string id, string path)
    {
        var stamp = LibraryDetailsCache.StampOf(path);
        if (stamp == null) return null;
        lock (_gate)
            if (_opened.TryGetValue(path, out var known) && known.Stamp == stamp) return known;

        var (info, reader) = BookAudio.OpenWithInfo(path);
        string[] parts;
        double[] lengths;
        using (reader)
        {
            if (reader is ConcatenatedWaveStream joined)
            {
                parts = BookSource.PartsOf(path).Take(joined.PartCount).ToArray();
                lengths = parts.Select((_, i) => (joined.PartStart(i + 1) - joined.PartStart(i)).TotalSeconds).ToArray();
            }
            else
            {
                parts = [path];
                lengths = [reader.TotalTime.TotalSeconds];
            }
        }
        var state = StateOf(path);
        bool edited = state is { DetailsEdited: true };
        var subtitles = state?.SubtitleFile is { Length: > 0 } chosen && File.Exists(chosen) ? chosen : BookSource.FindSubtitle(path);
        var book = new RemoteBook(id,
            edited ? state!.Title ?? BookSource.DisplayName(path) : string.IsNullOrWhiteSpace(info.Title) ? BookSource.DisplayName(path) : info.Title!,
            edited ? state!.Author : info.Artist,
            edited ? state!.Series : info.Series,
            edited ? state!.SeriesNumber : info.SeriesNumber,
            info.Asin,
            state?.SyncKey ?? BookSync.KeyFor(path, info.Asin),
            parts.Select((p, i) => new RemotePart(Path.GetFileName(p), lengths[i])).ToList(),
            info.Chapters.OrderBy(c => c.Start).Select(c => new RemoteChapter(c.Title, c.Start.TotalSeconds)).ToList(),
            HasCover: info.Cover is { Length: > 0 },
            HasSubtitles: subtitles != null,
            0, default, false);
        var opened = new Opened(stamp, book, parts, subtitles);
        lock (_gate) _opened[path] = opened;
        return opened;
    }

    public string? PartFile(string id, int index) =>
        PathOf(id) is { } path && Open(id, path) is { } opened && index >= 0 && index < opened.Parts.Length ? opened.Parts[index] : null;

    public byte[]? Cover(string id)
    {
        if (PathOf(id) is not { } path) return null;
        try { return BookSource.ReadDetails(path).Cover; }
        catch { return null; }
    }

    public string? SubtitleFile(string id) => PathOf(id) is { } path ? Open(id, path)?.Subtitles : null;

    /// <summary>A phone's position: handed to the app (which keeps it if it is newer), with the book's details if a phone opened it.</summary>
    public bool SetPosition(string id, RemotePosition position)
    {
        if (PathOf(id) is not { } path) return false;
        Opened? opened;
        lock (_gate) _opened.TryGetValue(path, out opened);
        positionArrived?.Invoke(path, position, opened?.Book);
        // The list says where the PC is: the next one must include this
        lock (_gate) _listed = default;
        return true;
    }
}
