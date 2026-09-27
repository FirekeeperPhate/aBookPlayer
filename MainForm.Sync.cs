namespace aBookPlayer;

// Position sync between PCs through a shared folder (see BookSync)
public sealed partial class MainForm
{
    DateTime _lastSyncCheck;
    Task _lastPublish = Task.CompletedTask;

    /// <summary>
    /// Before a book is restored: if another PC listened further more recently, take its position.
    /// Returns that PC's name, or null when the local position is already the newest.
    /// </summary>
    string? ApplySyncedPosition(string path, SyncedPosition? synced)
    {
        if (synced == null) return null;
        var local = _settings.GetBook(path);
        if (local != null && synced.Updated <= local.EffectivePositionUpdated.AddSeconds(2)) return null;
        var book = _settings.RememberBook(path, synced.Seconds, local?.SubtitleFile, local?.SubtitleOffsetMs ?? 0);
        book.PositionUpdated = synced.Updated; // it is that PC's position, not a newer one of ours
        book.Finished = synced.Finished;
        return synced.Machine;
    }

    /// <summary>Back to the window with the book paused: another PC may have moved on in the meantime.</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_settings.SyncFolder is not { } folder || _audioPath is not { } path || _player.IsPlaying || !_player.IsLoaded) return;
        if (CurrentBook?.SyncKey is not { } key || DateTime.Now - _lastSyncCheck < TimeSpan.FromSeconds(10)) return;
        _lastSyncCheck = DateTime.Now;
        _ = CheckSyncedPositionAsync(folder, path, key);
    }

    async Task CheckSyncedPositionAsync(string folder, string path, string key)
    {
        // Another PC still on 1.6 saved it under the old key (name and size)
        var synced = await Task.Run(() => BookSync.Find(folder, key, BookSync.LegacyKeyFor(path) is { } old && old != key ? old : null));
        // Only if nothing changed meanwhile: same book, still paused
        if (synced == null || !SamePath(path, _audioPath) || _player.IsPlaying) return;
        var local = CurrentBook;
        if (local == null || synced.Updated <= local.EffectivePositionUpdated.AddSeconds(2)) return;
        var target = TimeSpan.FromSeconds(synced.Seconds);
        if (target >= _player.Duration) return;
        _keepSavedPosition = false;
        _pausedSince = synced.Updated.ToLocalTime(); // smart rewind as if paused over there
        _player.Seek(target);
        local.PositionSeconds = synced.Seconds;
        local.PositionUpdated = synced.Updated;
        local.Finished = synced.Finished;
        FollowSleepChapter(target);
        ShowOsd($"Moved to {FormatTime(target)}, where you stopped on {synced.Machine}");
        UpdateUi();
    }

    void PublishSyncedPositions()
    {
        if (_settings.SyncFolder is { } folder) _lastPublish = BookSync.Publish(folder, _settings.Books.Values);
    }

    void ShowSyncOptions()
    {
        using (var dlg = new SyncOptionsForm(_settings.SyncFolder))
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _settings.SyncFolder = dlg.Folder;
                // Books opened before syncing was turned on need their key to be shared
                if (_audioPath != null && CurrentBook is { SyncKey: null } book) book.SyncKey = BookSync.KeyFor(_audioPath, book.Asin);
                SaveSettings();
                if (dlg.Folder != null) ShowOsd("Positions are now synced through the shared folder");
            }
        }
        OpenDeferredFile();
    }
}

/// <summary>Chooses (or clears) the shared folder used to sync positions between PCs.</summary>
sealed class SyncOptionsForm : DarkDialog
{
    readonly TextBox _folder = MakeTextBox();

    public string? Folder { get; private set; }

    public SyncOptionsForm(string? current) : base("Sync between PCs", new Size(560, 250))
    {
        Folder = current;
        var info = new Label
        {
            Text = "Choose a folder that all your PCs share, for example one inside OneDrive or Dropbox, or on a NAS. " +
                   "aBookPlayer saves the place in each book there, and on another PC continues from the most recent one.\n\n" +
                   "Books are recognized by name and size, so they can be in different folders on each PC.",
            Location = new Point(18, 16), Size = new Size(524, 96), ForeColor = Theme.TextDim,
        };
        _folder.ReadOnly = true;
        _folder.Text = current ?? "(sync is off)";
        _folder.SetBounds(18, 124, 410, 28);
        var browse = DialogControls.MakeButton("Browse…", 104);
        browse.Location = new Point(438, 122);
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Shared folder for syncing positions", UseDescriptionForTitle = true };
            if (Folder != null) dlg.InitialDirectory = Folder;
            else if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive) dlg.InitialDirectory = oneDrive;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Folder = dlg.SelectedPath;
            _folder.Text = Folder;
        };
        Controls.AddRange([info, _folder, browse]);

        AcceptButton = AddButton("OK", DialogResult.OK);
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        var off = AddButton("Turn off", width: 96);
        off.Click += (_, _) =>
        {
            Folder = null;
            _folder.Text = "(sync is off)";
        };
    }
}
