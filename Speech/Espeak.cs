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

    const int Synchronous = 2, Utf8 = 1, Ipa = 0x02, WithTie = 0x80;
    static readonly object Gate = new();
    static string? _voice;

    /// <summary>Loads the library from <paramref name="folder"/> (it holds "espeak-ng-data" too). Called once.</summary>
    public static void Start(string folder)
    {
        NativeLibrary.Load(Path.Combine(folder, Library));
        fixed (byte* path = Encoding.UTF8.GetBytes(folder + "\0"))
            if (espeak_Initialize(Synchronous, 0, path, 0) < 0) throw new InvalidOperationException("espeak-ng could not start.");
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
