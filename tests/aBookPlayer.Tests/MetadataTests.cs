using System.Text;
using static aBookPlayer.Tests.TestFiles;

namespace aBookPlayer.Tests;

public class MetadataTests
{
    static MediaInfo ReadFile(byte[] content, string extension)
    {
        var path = TempPath(extension);
        File.WriteAllBytes(path, content);
        return MediaMetadata.Read(path);
    }

    static (string, double)[] Summary(MediaInfo info) =>
        info.Chapters.Select(c => (c.Title, c.Start.TotalSeconds)).ToArray();

    [Fact]
    public void Mp3_id3_title_artist_and_chapters()
    {
        var tag = Id3Tag(
            Id3Frame("TIT2", Utf16Text("Titolo di prova")),
            Id3Frame("TPE1", [3, .. Encoding.UTF8.GetBytes("Artista è")]),
            Id3Chapter("ch1", 0, 12_000, "Introduzione"),
            Id3Chapter("ch2", 12_000, 22_000, "Finale àèìòù"));
        var info = ReadFile([.. tag, .. new byte[100]], ".mp3");

        Assert.Equal("Titolo di prova", info.Title);
        Assert.Equal("Artista è", info.Artist);
        Assert.Equal([("Introduzione", 0.0), ("Finale àèìòù", 12.0)], Summary(info));
    }

    [Fact]
    public void M4b_quicktime_chapter_track_and_itunes_tags()
    {
        var info = ReadFile(QuickTimeChapterFile([("Opening", 0), ("Middle", 10_000), ("Ending àèì", 20_000)], 30_000), ".m4b");

        Assert.Equal("QT Title", info.Title);
        Assert.Equal("QT Artist", info.Artist);
        Assert.Equal("QT Album", info.Album);
        Assert.Equal([("Opening", 0.0), ("Middle", 10.0), ("Ending àèì", 20.0)], Summary(info));
    }

    [Fact]
    public void M4b_nero_chapters_in_a_second_udta_box()
    {
        // No tref/chap here, so the reader must fall back to Nero chapters
        var moov = Box("moov",
            Box("udta", Box("Xtra", Zeros(0))),
            Box("udta", NeroChapters(("One", 0), ("Two", 61_500)), ITunesTags("Nero Title", "A", "B")));
        var info = ReadFile([.. Box("ftyp", Encoding.ASCII.GetBytes("M4A "), Zeros(4)), .. moov], ".m4a");

        Assert.Equal("Nero Title", info.Title);
        Assert.Equal([("One", 0.0), ("Two", 61.5)], Summary(info));
    }

    [Fact]
    public void Flac_vorbis_comments_and_chapters()
    {
        var info = ReadFile(FlacWithComments(
            "TITLE=FLAC title", "ARTIST=Flac Artist", "ALBUM=Flac Album",
            "CHAPTER001=00:00:00.000", "CHAPTER001NAME=Uno",
            "CHAPTER002=00:01:02.500", "CHAPTER002NAME=Due àè"), ".flac");

        Assert.Equal("FLAC title", info.Title);
        Assert.Equal("Flac Album", info.Album);
        Assert.Equal([("Uno", 0.0), ("Due àè", 62.5)], Summary(info));
    }

    [Fact]
    public void Malformed_files_do_not_throw()
    {
        var info = ReadFile([(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0x7F, 0x7F, 0x7F, 0x7F, 1, 2, 3], ".mp3");
        Assert.Empty(info.Chapters);
        Assert.Empty(ReadFile(Box("moov", Box("trak", Zeros(3))), ".m4b").Chapters);
    }
}
