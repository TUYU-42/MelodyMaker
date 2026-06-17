using System.Globalization;
using System.Xml.Linq;

namespace MelodyMaker.Core.Services;

internal sealed class LegacyPatternRepository
{
    public IReadOnlyList<LegacyPitchPattern> PitchPatterns { get; }
    public IReadOnlyList<LegacyRhythmPattern> RhythmPatterns { get; }
    public IReadOnlyList<LegacyChordProgression> ChordProgressions { get; }

    private LegacyPatternRepository(
        IReadOnlyList<LegacyPitchPattern> pitchPatterns,
        IReadOnlyList<LegacyRhythmPattern> rhythmPatterns,
        IReadOnlyList<LegacyChordProgression> chordProgressions)
    {
        PitchPatterns = pitchPatterns;
        RhythmPatterns = rhythmPatterns;
        ChordProgressions = chordProgressions;
    }

    public static LegacyPatternRepository Load(string? dataFolder)
    {
        List<LegacyPitchPattern> pitchPatterns = new();
        List<LegacyRhythmPattern> rhythmPatterns = new();
        List<LegacyChordProgression> chordProgressions = new();

        if (!string.IsNullOrWhiteSpace(dataFolder) && Directory.Exists(dataFolder))
        {
            string pitchPath = Path.Combine(dataFolder, "2024_PitchSet.xml");
            string rhythmPath = Path.Combine(dataFolder, "2024_RhythmSet.xml");
            string chordPath = Path.Combine(dataFolder, "ChordProgressions.txt");

            if (File.Exists(pitchPath))
                pitchPatterns.AddRange(ReadPitchPatterns(pitchPath));

            if (File.Exists(rhythmPath))
                rhythmPatterns.AddRange(ReadRhythmPatterns(rhythmPath));

            if (File.Exists(chordPath))
                chordProgressions.AddRange(ReadChordProgressions(chordPath));
        }

        if (pitchPatterns.Count == 0)
            pitchPatterns.AddRange(CreateFallbackPitchPatterns());

        if (rhythmPatterns.Count == 0)
            rhythmPatterns.AddRange(CreateFallbackRhythmPatterns());

        if (chordProgressions.Count == 0)
            chordProgressions.AddRange(CreateFallbackChordProgressions());

        return new LegacyPatternRepository(pitchPatterns, rhythmPatterns, chordProgressions);
    }

    private static IEnumerable<LegacyPitchPattern> ReadPitchPatterns(string path)
    {
        XDocument doc = XDocument.Load(path);

        foreach (XElement row in doc.Descendants("dtPitchSet"))
        {
            string? pattern = row.Element("pitch")?.Value.Trim();
            string? lengthText = row.Element("length")?.Value.Trim();

            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            int length = ParseInt(lengthText, pattern.Length / 2);
            if (length <= 0 || pattern.Length < length * 2)
                continue;

            yield return new LegacyPitchPattern(pattern, length);
        }
    }

    private static IEnumerable<LegacyRhythmPattern> ReadRhythmPatterns(string path)
    {
        XDocument doc = XDocument.Load(path);

        foreach (XElement row in doc.Descendants("dtRhythmSet"))
        {
            string? pattern = row.Element("rhythm")?.Value.Trim();
            string? lengthText = row.Element("length")?.Value.Trim();
            string? totalTickText = row.Element("totalTick")?.Value.Trim();

            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            int length = ParseInt(lengthText, pattern.Length / 2);
            int totalTick = ParseInt(totalTickText, 1920);

            if (length <= 0 || pattern.Length < length * 2)
                continue;

            yield return new LegacyRhythmPattern(pattern, length, totalTick);
        }
    }

    private static IEnumerable<LegacyChordProgression> ReadChordProgressions(string path)
    {
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || !line.Contains(','))
                continue;

            string[] parts = line.Split(',', 2);
            string name = parts[0].Trim();
            string chordText = new string(parts[1].Where(c => !char.IsWhiteSpace(c)).ToArray());

            if (chordText.Length >= 5)
                yield return new LegacyChordProgression(name, chordText);
        }
    }

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static IEnumerable<LegacyPitchPattern> CreateFallbackPitchPatterns()
    {
        string[] patterns =
        [
            "C5D5E5G5", "E5G5A5G5", "A5G5E5D5", "C5E5G5C6",
            "G5E5D5C5", "C5D5E5D5C5", "E5G5A5C6A5", "C5E5D5G5E5"
        ];

        return patterns.Select(p => new LegacyPitchPattern(p, p.Length / 2));
    }

    private static IEnumerable<LegacyRhythmPattern> CreateFallbackRhythmPatterns()
    {
        return
        [
            new LegacyRhythmPattern("04040404", 4, 1920),
            new LegacyRhythmPattern("0808080808080808", 8, 1920),
            new LegacyRhythmPattern("0808080802", 5, 1920),
            new LegacyRhythmPattern("0404080804", 5, 1920),
            new LegacyRhythmPattern("4.080808", 4, 1920)
        ];
    }

    private static IEnumerable<LegacyChordProgression> CreateFallbackChordProgressions()
    {
        return
        [
            new LegacyChordProgression("1564流行萬用", "XC501XG501XA501XF501"),
            new LegacyChordProgression("江南穩定8小節", "XC501XA501XF501XG501XC501XA501XD501XG501"),
            new LegacyChordProgression("王道進行8小節", "XF501XG501XE501XA501XF501XG501XC501XC501"),
            new LegacyChordProgression("小調情緒8小節", "XA501XF501XC501XG501XA501XF501XD501XG501")
        ];
    }
}

internal sealed record LegacyPitchPattern(string Pattern, int Length);
internal sealed record LegacyRhythmPattern(string Pattern, int Length, int TotalTick);
internal sealed record LegacyChordProgression(string Name, string ChordText);
