namespace aBookPlayer;

/// <summary>Cover pictures as Windows images (the bytes come from <see cref="CoverArt"/>, shared with the Android app).</summary>
static class CoverImages
{
    /// <summary>Decodes the picture, or null if the bytes are not a readable image.</summary>
    public static Image? ToImage(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var decoded = Image.FromStream(ms);
            return new Bitmap(decoded); // independent of the stream
        }
        catch { return null; }
    }
}
