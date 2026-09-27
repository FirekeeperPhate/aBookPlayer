using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace aBookPlayer;

enum MediaButton { Play = 0, Pause = 1, Stop = 2, FastForward = 4, Rewind = 5, Next = 6, Previous = 7 }

/// <summary>
/// Windows' System Media Transport Controls: title, author and cover in the volume/media flyout and on the
/// lock screen, and the media keys and Bluetooth headset buttons working even when the window is not in front.
/// The WinRT API is called through its raw COM interfaces, so no 25 MB Windows SDK projection has to ship with
/// the app. Vtable slots follow the order in Windows.Media.winmd (IInspectable's methods are slots 0-5).
/// </summary>
sealed unsafe class MediaControls
{
    static readonly Guid IID_Interop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");        // ISystemMediaTransportControlsInterop
    static readonly Guid IID_Smtc = new("99fa3ff4-1742-42a6-902e-087d41f965ec");           // ISystemMediaTransportControls
    static readonly Guid IID_MusicProperties = new("6bbf0c59-d0a0-4d26-92a0-f978e1d18e7b");
    static readonly Guid IID_StreamReferenceStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");
    static readonly Guid IID_RandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    // TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>
    static readonly Guid IID_ButtonHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4");
    static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
    static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    readonly IntPtr _smtc;
    readonly IntPtr _updater;
    readonly IntPtr _music;
    static Action<MediaButton>? _onButton;
    static IntPtr _handler;

    string? _title, _artist;
    byte[]? _cover;
    int _status = -1;
    (bool Loaded, bool Chapters) _enabled = (true, true);

    MediaControls(IntPtr smtc, IntPtr updater, IntPtr music)
    {
        _smtc = smtc;
        _updater = updater;
        _music = music;
    }

    /// <summary>Registers the window with Windows' media controls; null where they are not available.</summary>
    public static MediaControls? TryCreate(IntPtr hwnd, Action<MediaButton> onButton)
    {
        IntPtr factory = 0, smtc = 0, updater = 0, music = 0;
        try
        {
            factory = ActivationFactory("Windows.Media.SystemMediaTransportControls", IID_Interop);
            var iid = IID_Smtc;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(factory, 6))(factory, hwnd, &iid, &smtc)); // GetForWindow
            Check(Call(smtc, 8, &updater));                                              // get_DisplayUpdater
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(updater, 7))(updater, 1)); // put_Type(Music)
            Check(Call(updater, 12, &music));                                            // get_MusicProperties

            _onButton = onButton;
            _handler = CreateHandler();
            long token;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(smtc, 32))(smtc, _handler, &token)); // add_ButtonPressed

            var controls = new MediaControls(smtc, updater, music);
            smtc = updater = music = 0; // owned by the instance from here on
            controls.SetBool(11, true);  // put_IsEnabled
            controls.SetBool(13, true);  // put_IsPlayEnabled
            controls.SetBool(17, true);  // put_IsPauseEnabled
            controls.SetBool(15, true);  // put_IsStopEnabled
            return controls;
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(factory);
            Release(smtc);
            Release(updater);
            Release(music);
        }
    }

    /// <summary>Updates what Windows shows; only what changed is sent.</summary>
    public void Update(bool loaded, bool playing, bool hasChapters, string? title, string? artist, byte[]? cover)
    {
        try
        {
            if (_enabled != (loaded, hasChapters))
            {
                _enabled = (loaded, hasChapters);
                SetBool(13, loaded);                  // put_IsPlayEnabled
                SetBool(17, loaded);                  // put_IsPauseEnabled
                SetBool(15, loaded);                  // put_IsStopEnabled
                SetBool(25, loaded && hasChapters);   // put_IsPreviousEnabled
                SetBool(27, loaded && hasChapters);   // put_IsNextEnabled
            }

            // MediaPlaybackStatus: Closed = 0, Stopped = 2, Playing = 3, Paused = 4
            int status = !loaded ? 0 : playing ? 3 : 4;
            if (status != _status)
            {
                _status = status;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(_smtc, 7))(_smtc, status));
            }

            if (title != _title || artist != _artist || !ReferenceEquals(cover, _cover))
            {
                _title = title;
                _artist = artist;
                bool coverChanged = !ReferenceEquals(cover, _cover);
                _cover = cover;
                SetString(_music, 7, title ?? "");     // put_Title
                SetString(_music, 11, artist ?? "");   // put_Artist
                if (coverChanged) SetThumbnail(cover);
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_updater, 17))(_updater)); // Update
            }
        }
        catch { /* the media flyout is a nicety: never let it disturb playback */ }
    }

    void SetBool(int slot, bool value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(_smtc, slot))(_smtc, value ? (byte)1 : (byte)0));

    static void SetString(IntPtr obj, int slot, string value)
    {
        IntPtr hstring = 0;
        Check(WindowsCreateString(value, value.Length, &hstring));
        try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, hstring)); }
        finally { WindowsDeleteString(hstring); }
    }

    /// <summary>Cover picture: bytes → IStream → IRandomAccessStream → RandomAccessStreamReference.</summary>
    void SetThumbnail(byte[]? cover)
    {
        IntPtr statics = 0, stream = 0, randomAccess = 0, reference = 0;
        try
        {
            if (cover != null && cover.Length > 0)
            {
                fixed (byte* data = cover) stream = SHCreateMemStream(data, (uint)cover.Length);
                if (stream == 0) return;
                var iid = IID_RandomAccessStream;
                Check(CreateRandomAccessStreamOverStream(stream, 0, &iid, &randomAccess));
                statics = ActivationFactory("Windows.Storage.Streams.RandomAccessStreamReference", IID_StreamReferenceStatics);
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 8))(statics, randomAccess, &reference)); // CreateFromStream
            }
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(_updater, 11))(_updater, reference)); // put_Thumbnail (null clears it)
        }
        finally
        {
            Release(reference);
            Release(statics);
            Release(randomAccess);
            Release(stream);
        }
    }

    // ── The ButtonPressed handler: a minimal COM object (QueryInterface, AddRef, Release, Invoke) ──

    static IntPtr CreateHandler()
    {
        var vtable = (IntPtr*)NativeMemory.Alloc(4, (nuint)sizeof(IntPtr));
        vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseHandler;
        vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&Invoke;
        var obj = (IntPtr*)NativeMemory.Alloc(1, (nuint)sizeof(IntPtr));
        obj[0] = (IntPtr)vtable;
        return (IntPtr)obj; // lives as long as the app: never freed
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == IID_IUnknown || *iid == IID_ButtonHandler || *iid == IID_IAgileObject)
        {
            *result = self;
            return 0;
        }
        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint AddRef(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint ReleaseHandler(IntPtr self) => 1;

    /// <summary>Called on a background thread with the event args (ISystemMediaTransportControlsButtonPressedEventArgs).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Invoke(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            int button;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, 6))(args, &button) >= 0) // get_Button
                _onButton?.Invoke((MediaButton)button);
        }
        catch { /* must not throw across the COM boundary */ }
        return 0;
    }

    // ── Helpers ──

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    static int Call(IntPtr obj, int slot, IntPtr* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, result);

    static void Release(IntPtr obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    static IntPtr ActivationFactory(string className, Guid iid)
    {
        IntPtr hstring = 0, factory = 0;
        Check(WindowsCreateString(className, className.Length, &hstring));
        try { Check(RoGetActivationFactory(hstring, &iid, &factory)); }
        finally { WindowsDeleteString(hstring); }
        return factory;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, IntPtr* hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, IntPtr* factory);

    [DllImport("shlwapi.dll")]
    static extern IntPtr SHCreateMemStream(byte* data, uint size);

    [DllImport("shcore.dll")]
    static extern int CreateRandomAccessStreamOverStream(IntPtr stream, int options, Guid* iid, IntPtr* result);
}
