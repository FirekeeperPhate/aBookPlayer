using NAudio.Vorbis;
using NAudio.Wave;

namespace aBookPlayer;

/// <summary>
/// Supported audio formats. Apart from OGG (NVorbis, ~100 KB), decoding relies on what is built into
/// NAudio or Windows (Media Foundation), so extra formats add no weight to the app.
/// </summary>
static class AudioFormats
{
    public static readonly string[] Extensions =
        [".mp3", ".m4a", ".m4b", ".aac", ".mp4", ".wma", ".wav", ".flac", ".aiff", ".aif", ".ogg"];

    public static string DialogFilter =>
        $"Audio files ({string.Join(";", Extensions.Select(e => "*" + e))})|{string.Join(";", Extensions.Select(e => "*" + e))}|All files (*.*)|*.*";

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a seekable stream decoding to PCM/float. Can scan the whole file: call it off the UI thread.</summary>
    public static WaveStream Open(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".mp3":
                return new Mp3FileReader(path);
            case ".ogg":
                return new VorbisWaveReader(path);
            case ".aiff" or ".aif":
                return new AiffFileReader(path);
            case ".wav":
                var wav = new WaveFileReader(path);
                if (wav.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
                    return wav;
                wav.Dispose(); // compressed WAV (ADPCM, etc.): let Windows decode it
                return new MediaFoundationReader(path);
            default:
                // M4A/M4B/AAC/MP4/WMA/FLAC: Windows' built-in decoders
                return new MediaFoundationReader(path);
        }
    }
}
