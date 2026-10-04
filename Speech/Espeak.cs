using System.Runtime.InteropServices;
using System.Text;

namespace aBookPlayer;

/// <summary>
/// espeak-ng, used only for what comes before the voice: English text to phonemes (IPA), with numbers, dates and
/// abbreviations read as words. It works clause by clause; each clause comes with the punctuation mark that ended
/// it, which the speech models need for their pauses and intonation.
/// </summary>
static unsafe class Espeak
{
    const string Library = "espeak-ng.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int espeak_Initialize(int output, int bufferLength, byte* path, int options);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int espeak_SetVoiceByName(byte* name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern byte* espeak_TextToPhonemes(byte** text, int textMode, int phonemeMode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetShortPathName(string path, char[]? shortPath, int size);

    // (Without DontExit, espeak-ng ends the whole process when it cannot read its data)
    const int Synchronous = 2, DontExit = 0x8000, Utf8 = 1, Ipa = 0x02, WithTie = 0x80;
    static readonly object Gate = new();
    static string? _voice;

    /// <summary>
    /// Loads the library from <paramref name="folder"/> (it holds "espeak-ng-data" too). Throws when espeak-ng cannot
    /// read its data there.
    /// </summary>
    public static void Start(string folder)
    {
        NativeLibrary.Load(Path.Combine(folder, Library));
        // It answers with the rate of its own voices (22050), read from the data: nothing when it did not find them
        fixed (byte* path = DataPath(folder))
            if (espeak_Initialize(Synchronous, 0, path, DontExit) <= 0)
                throw new InvalidOperationException($"espeak-ng could not read its data in \"{folder}\".");
    }

    /// <summary>
    /// The folder as espeak-ng can open it. On Windows it only manages names in plain ASCII (it checks the folder
    /// in the system's code page and then opens the files in UTF-8), so "C:\Users\Niccolò\…" goes by its short name,
    /// "C:\Users\NICCOL~1\…", which every folder has unless short names were turned off for its disk.
    /// </summary>
    internal static byte[] DataPath(string folder)
    {
        if (folder.All(char.IsAscii)) return Encoding.ASCII.GetBytes(folder + "\0");
        var name = new char[1024];
        int length = GetShortPathName(folder, name, name.Length);
        if (length > 0 && length < name.Length && new string(name, 0, length) is var brief && brief.All(char.IsAscii)) return Encoding.ASCII.GetBytes(brief + "\0");
        throw new InvalidOperationException($"espeak-ng cannot read its data in \"{folder}\": it only manages folders named with plain letters (A–Z), " +
                                            "and this disk gives no short name to the others. Use the portable aBookPlayer from a folder with a plain name: it keeps its data beside it.");
    }

    /// <summary>A clause's phonemes and the mark that ended it (".", ",", "?"…; "" when none).</summary>
    public readonly record struct Clause(string Phonemes, string Terminator);

    /// <summary>
    /// The phonemes of <paramref name="text"/> in the voice "en-us" or "en-gb". With <paramref name="tie"/>, the two
    /// letters of one sound (a diphthong, an affricate) are joined by '^', for Kokoro's own alphabet.
    /// </summary>
    public static List<Clause> Phonemes(string text, string voice, bool tie)
    {
        var clauses = new List<Clause>();
        lock (Gate) // espeak-ng keeps one state for the whole process
        {
            if (_voice != voice)
            {
                fixed (byte* name = Encoding.UTF8.GetBytes(voice + "\0")) espeak_SetVoiceByName(name);
                _voice = voice;
            }
            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            fixed (byte* start = bytes)
            {
                byte* p = start;
                while (p != null && *p != 0)
                {
                    byte* phonemes = espeak_TextToPhonemes(&p, Utf8, tie ? Ipa | WithTie | ('^' << 8) : Ipa);
                    if (phonemes == null) break;
                    string clause = Marshal.PtrToStringUTF8((IntPtr)phonemes)!.Trim();
                    if (clause.Length == 0) continue;
                    // espeak-ng has read one character into the next clause: the mark is just before it, behind
                    // any space, quote or bracket
                    int last = bytes.Length - 2; // the text's last character, when this is its last clause
                    if (p != null && *p != 0)
                    {
                        last = (int)(p - start) - 1; // the character read ahead…
                        while (last > 0 && (bytes[last] & 0xC0) == 0x80) last--; // …(from its first byte, in UTF-8)…
                        last--; // …and the one before it
                    }
                    string terminator = "";
                    for (int i = last; i >= 0; i--)
                    {
                        char c = (char)bytes[i];
                        if (c is ' ' or '"' or '\'' or ')' or ']' or '\n' or '\t') continue;
                        if (c is '.' or ',' or ';' or ':' or '!' or '?') terminator = c.ToString();
                        break;
                    }
                    clauses.Add(new Clause(clause, terminator));
                }
            }
        }
        return clauses;
    }
}
