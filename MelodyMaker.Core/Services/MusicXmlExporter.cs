using System.Globalization;
using System.Text;
using System.Xml;
using MelodyMaker.Core.Models;

namespace MelodyMaker.Core.Services;

public sealed class MusicXmlExporter
{
    private const int TicksPerBeat = 480;
    private const int TicksPerBar = 1920;

    public void WriteScore(string outputPath, MelodyResult result)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("MusicXML output path is empty.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        XmlWriterSettings settings = new()
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        using XmlWriter xw = XmlWriter.Create(outputPath, settings);

        xw.WriteStartDocument();
        xw.WriteDocType(
            "score-partwise",
            "-//Recordare//DTD MusicXML 3.1 Partwise//EN",
            "http://www.musicxml.org/dtds/partwise.dtd",
            null);

        xw.WriteStartElement("score-partwise");
        xw.WriteAttributeString("version", "3.1");

        xw.WriteElementString("movement-title", string.IsNullOrWhiteSpace(result.Title) ? "Generated Music" : result.Title);

        WriteIdentification(xw);

        xw.WriteStartElement("part-list");
        WriteScorePart(xw, "P1", "Melody");
        if (!string.IsNullOrWhiteSpace(result.SubYNote))
            WriteScorePart(xw, "P2", "Sub Melody");
        xw.WriteEndElement();

        int barCount = Math.Max(1, result.ChordText.Length / 5);
        if (barCount <= 0)
            barCount = Math.Max(1, result.MainYNote.Length / 16);

        WritePartFromYNote(
            xw,
            partId: "P1",
            yNote: result.MainYNote,
            chordText: result.ChordText,
            barCount: barCount,
            includeChordSymbols: true,
            tempo: result.Tempo,
            key: result.Key);

        if (!string.IsNullOrWhiteSpace(result.SubYNote))
        {
            WritePartFromYNote(
                xw,
                partId: "P2",
                yNote: result.SubYNote,
                chordText: result.ChordText,
                barCount: barCount,
                includeChordSymbols: false,
                tempo: result.Tempo,
                key: result.Key);
        }

        xw.WriteEndElement();
        xw.WriteEndDocument();
    }

    private static void WriteIdentification(XmlWriter xw)
    {
        xw.WriteStartElement("identification");

        xw.WriteStartElement("creator");
        xw.WriteAttributeString("type", "composer");
        xw.WriteString("MelodyMaker Web");
        xw.WriteEndElement();

        xw.WriteStartElement("encoding");
        xw.WriteElementString("software", "MelodyMaker ASP.NET Core");
        xw.WriteElementString("encoding-date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        xw.WriteEndElement();

        xw.WriteEndElement();
    }

    private static void WriteScorePart(XmlWriter xw, string id, string partName)
    {
        xw.WriteStartElement("score-part");
        xw.WriteAttributeString("id", id);
        xw.WriteElementString("part-name", partName);
        xw.WriteEndElement();
    }

    private static void WritePartFromYNote(
        XmlWriter xw,
        string partId,
        string yNote,
        string chordText,
        int barCount,
        bool includeChordSymbols,
        int tempo,
        string key)
    {
        xw.WriteStartElement("part");
        xw.WriteAttributeString("id", partId);

        int noteIndex = 0;
        int totalNotes = string.IsNullOrEmpty(yNote) ? 0 : yNote.Length / 4;

        for (int bar = 0; bar < barCount; bar++)
        {
            xw.WriteStartElement("measure");
            xw.WriteAttributeString("number", (bar + 1).ToString(CultureInfo.InvariantCulture));

            if (bar == 0)
                WriteMeasureAttributes(xw, key);

            if (bar == 0 && includeChordSymbols)
                WriteTempoDirection(xw, tempo);

            if (includeChordSymbols)
            {
                string? sectionLabel = GetSectionLabel(bar, barCount);
                if (!string.IsNullOrWhiteSpace(sectionLabel))
                    WriteRehearsalDirection(xw, sectionLabel);

                string? dynamicMark = GetDynamicMark(bar, barCount);
                if (!string.IsNullOrWhiteSpace(dynamicMark))
                    WriteDynamicDirection(xw, dynamicMark);
            }

            if (includeChordSymbols && chordText.Length >= (bar + 1) * 5)
            {
                string chordCode = chordText.Substring(bar * 5, 5);
                WriteHarmonySymbol(xw, chordCode);
                WriteWordsDirection(xw, GetChordDisplayName(chordCode));
            }

            List<ScoreEvent> rawEvents = ReadBarEvents(yNote, ref noteIndex, totalNotes);
            List<ScoreEvent> safeEvents = NormalizeBarEvents(rawEvents);

            foreach (ScoreEvent ev in safeEvents)
                WriteEvent(xw, ev.Pitch, ev.Duration);

            if (bar == barCount - 1)
                WriteFinalBarline(xw);

            xw.WriteEndElement();
        }

        xw.WriteEndElement();
    }

    private sealed class ScoreEvent
    {
        public string Pitch { get; }
        public int Duration { get; set; }

        public ScoreEvent(string pitch, int duration)
        {
            Pitch = string.IsNullOrWhiteSpace(pitch) ? "00" : pitch;
            Duration = duration;
        }
    }

    private static List<ScoreEvent> ReadBarEvents(string yNote, ref int noteIndex, int totalNotes)
    {
        List<ScoreEvent> events = new();
        int tickInBar = 0;

        while (noteIndex < totalNotes && tickInBar < TicksPerBar)
        {
            string pitch = yNote.Substring(noteIndex * 4, 2);
            string rhythm = yNote.Substring(noteIndex * 4 + 2, 2);
            int duration = GetDuration(rhythm);

            if (duration <= 0)
                duration = TicksPerBeat;

            int remaining = TicksPerBar - tickInBar;
            int clippedDuration = Math.Min(duration, remaining);

            if (clippedDuration > 0)
                events.Add(new ScoreEvent(pitch, clippedDuration));

            tickInBar += clippedDuration;
            noteIndex++;
        }

        return events;
    }

    private static List<ScoreEvent> NormalizeBarEvents(List<ScoreEvent> rawEvents)
    {
        List<ScoreEvent> merged = MergeAdjacentRests(rawEvents);
        List<ScoreEvent> normalized = new();

        if (merged.Count == 0)
        {
            normalized.Add(new ScoreEvent("00", TicksPerBar));
            return normalized;
        }

        const int unit = 60;
        int remaining = TicksPerBar;

        for (int i = 0; i < merged.Count; i++)
        {
            int eventsLeft = merged.Count - i - 1;
            int minForRestEvents = eventsLeft * unit;
            int duration;

            if (i == merged.Count - 1)
            {
                duration = remaining;
            }
            else
            {
                duration = QuantizeDuration(merged[i].Duration, unit);
                int maxAllowed = remaining - minForRestEvents;

                if (maxAllowed < unit)
                    maxAllowed = remaining;

                duration = Math.Clamp(duration, unit, maxAllowed);
            }

            if (duration <= 0)
                continue;

            normalized.Add(new ScoreEvent(merged[i].Pitch, duration));
            remaining -= duration;

            if (remaining <= 0)
                break;
        }

        if (remaining > 0)
        {
            if (normalized.Count > 0 && IsRest(normalized[^1].Pitch))
                normalized[^1].Duration += remaining;
            else
                normalized.Add(new ScoreEvent("00", remaining));
        }

        return MergeAdjacentRests(normalized);
    }

    private static int QuantizeDuration(int duration, int unit)
    {
        int q = (int)Math.Round(duration / (double)unit) * unit;
        return Math.Max(unit, q);
    }

    private static List<ScoreEvent> MergeAdjacentRests(IEnumerable<ScoreEvent> events)
    {
        List<ScoreEvent> result = new();

        foreach (ScoreEvent ev in events)
        {
            if (ev.Duration <= 0)
                continue;

            if (result.Count > 0 && IsRest(result[^1].Pitch) && IsRest(ev.Pitch))
            {
                result[^1].Duration += ev.Duration;
            }
            else
            {
                result.Add(new ScoreEvent(ev.Pitch, ev.Duration));
            }
        }

        return result;
    }

    private static void WriteMeasureAttributes(XmlWriter xw, string key)
    {
        xw.WriteStartElement("attributes");
        xw.WriteElementString("divisions", TicksPerBeat.ToString(CultureInfo.InvariantCulture));

        xw.WriteStartElement("key");
        xw.WriteElementString("fifths", GetFifths(key).ToString(CultureInfo.InvariantCulture));
        xw.WriteEndElement();

        xw.WriteStartElement("time");
        xw.WriteElementString("beats", "4");
        xw.WriteElementString("beat-type", "4");
        xw.WriteEndElement();

        xw.WriteStartElement("clef");
        xw.WriteElementString("sign", "G");
        xw.WriteElementString("line", "2");
        xw.WriteEndElement();

        xw.WriteEndElement();
    }

    private static int GetFifths(string key)
    {
        return key switch
        {
            "G" or "E minor" => 1,
            "D" or "B minor" => 2,
            "A" or "F# minor" => 3,
            "E" or "C# minor" => 4,
            "F" or "D minor" => -1,
            "Bb" or "G minor" => -2,
            "Eb" or "C minor" => -3,
            _ => 0
        };
    }

    private static void WriteTempoDirection(XmlWriter xw, int tempo)
    {
        xw.WriteStartElement("direction");
        xw.WriteAttributeString("placement", "above");
        xw.WriteStartElement("direction-type");
        xw.WriteStartElement("metronome");
        xw.WriteElementString("beat-unit", "quarter");
        xw.WriteElementString("per-minute", tempo.ToString(CultureInfo.InvariantCulture));
        xw.WriteEndElement();
        xw.WriteEndElement();
        xw.WriteStartElement("sound");
        xw.WriteAttributeString("tempo", tempo.ToString(CultureInfo.InvariantCulture));
        xw.WriteEndElement();
        xw.WriteEndElement();
    }

    private static void WriteWordsDirection(XmlWriter xw, string words)
    {
        if (string.IsNullOrWhiteSpace(words))
            return;

        xw.WriteStartElement("direction");
        xw.WriteAttributeString("placement", "above");
        xw.WriteStartElement("direction-type");
        xw.WriteStartElement("words");
        xw.WriteAttributeString("font-weight", "bold");
        xw.WriteString(words);
        xw.WriteEndElement();
        xw.WriteEndElement();
        xw.WriteEndElement();
    }

    private static void WriteRehearsalDirection(XmlWriter xw, string label)
    {
        xw.WriteStartElement("direction");
        xw.WriteAttributeString("placement", "above");
        xw.WriteStartElement("direction-type");
        xw.WriteStartElement("rehearsal");
        xw.WriteAttributeString("font-weight", "bold");
        xw.WriteString(label);
        xw.WriteEndElement();
        xw.WriteEndElement();
        xw.WriteEndElement();
    }

    private static void WriteDynamicDirection(XmlWriter xw, string dynamicMark)
    {
        xw.WriteStartElement("direction");
        xw.WriteAttributeString("placement", "below");
        xw.WriteStartElement("direction-type");
        xw.WriteStartElement("dynamics");
        xw.WriteStartElement(dynamicMark);
        xw.WriteEndElement();
        xw.WriteEndElement();
        xw.WriteEndElement();
        xw.WriteEndElement();
    }

    private static void WriteHarmonySymbol(XmlWriter xw, string chordCode)
    {
        string displayName = GetChordDisplayName(chordCode);
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        ParseDisplayChord(displayName, out string rootStep, out int rootAlter, out string kindText);

        xw.WriteStartElement("harmony");
        xw.WriteStartElement("root");
        xw.WriteElementString("root-step", rootStep);
        if (rootAlter != 0)
            xw.WriteElementString("root-alter", rootAlter.ToString(CultureInfo.InvariantCulture));
        xw.WriteEndElement();
        xw.WriteElementString("kind", kindText);
        xw.WriteEndElement();
    }

    private static void ParseDisplayChord(string displayName, out string rootStep, out int rootAlter, out string kindText)
    {
        rootStep = displayName.Length > 0 ? displayName[0].ToString() : "C";
        rootAlter = displayName.Contains("#", StringComparison.Ordinal) ? 1 :
                    displayName.Contains("b", StringComparison.Ordinal) ? -1 : 0;

        if (displayName.Contains("dim", StringComparison.OrdinalIgnoreCase))
            kindText = "diminished";
        else if (displayName.Contains("m", StringComparison.Ordinal) && !displayName.StartsWith("M", StringComparison.Ordinal))
            kindText = "minor";
        else if (displayName.Contains("7", StringComparison.Ordinal))
            kindText = "dominant";
        else
            kindText = "major";
    }

    private static string? GetSectionLabel(int bar, int barCount)
    {
        if (bar == 0)
            return "A";

        // Step 8：江南前四小節標 A / A' / A / A''，對應 WinForms ThemeSeed 起句結構。
        if (bar == 1)
            return "A'";
        if (bar == 2)
            return "A";
        if (bar == 3)
            return "A''";

        int contrastStart = Math.Max(4, (int)Math.Floor(barCount * 0.55));
        int closingStart = Math.Max(contrastStart + 1, (int)Math.Floor(barCount * 0.78));

        if (bar == contrastStart)
            return "B";
        if (bar == closingStart)
            return "C";

        return null;
    }

    private static string? GetDynamicMark(int bar, int barCount)
    {
        if (bar == 0)
            return "mp";

        int contrastStart = Math.Max(4, (int)Math.Floor(barCount * 0.55));
        int closingStart = Math.Max(contrastStart + 1, (int)Math.Floor(barCount * 0.78));

        if (bar == contrastStart)
            return "mf";
        if (bar == closingStart)
            return "p";

        return null;
    }

    private static void WriteFinalBarline(XmlWriter xw)
    {
        xw.WriteStartElement("barline");
        xw.WriteAttributeString("location", "right");
        xw.WriteElementString("bar-style", "light-heavy");
        xw.WriteEndElement();
    }

    private static void WriteEvent(XmlWriter xw, string pitch, int duration)
    {
        if (duration <= 0)
            return;

        foreach (int chunk in SplitDuration(duration))
            WriteNote(xw, pitch, chunk);
    }

    private static IEnumerable<int> SplitDuration(int duration)
    {
        int[] supported = [1920, 1440, 960, 720, 480, 360, 240, 180, 120, 60];
        int remaining = duration;

        while (remaining > 0)
        {
            int chosen = supported.FirstOrDefault(s => s <= remaining);
            if (chosen <= 0)
                chosen = 60;

            yield return chosen;
            remaining -= chosen;
        }
    }

    private static void WriteNote(XmlWriter xw, string pitch, int duration)
    {
        bool rest = IsRest(pitch);

        xw.WriteStartElement("note");

        if (rest)
        {
            xw.WriteStartElement("rest");
            xw.WriteEndElement();
        }
        else
        {
            PitchToMusicXml(pitch, out string step, out int alter, out int octave);

            xw.WriteStartElement("pitch");
            xw.WriteElementString("step", step);
            if (alter != 0)
                xw.WriteElementString("alter", alter.ToString(CultureInfo.InvariantCulture));
            xw.WriteElementString("octave", octave.ToString(CultureInfo.InvariantCulture));
            xw.WriteEndElement();
        }

        xw.WriteElementString("duration", duration.ToString(CultureInfo.InvariantCulture));
        xw.WriteElementString("voice", "1");

        string type = GetNoteType(duration);
        if (!string.IsNullOrEmpty(type))
        {
            xw.WriteElementString("type", type);
            if (IsDottedDuration(duration))
                xw.WriteElementString("dot", string.Empty);
        }

        xw.WriteEndElement();
    }

    private static bool IsRest(string pitch)
    {
        return string.IsNullOrWhiteSpace(pitch) || pitch == "00";
    }

    private static void PitchToMusicXml(string pitch, out string step, out int alter, out int octave)
    {
        step = "C";
        alter = 0;
        octave = 4;

        if (string.IsNullOrWhiteSpace(pitch) || pitch.Length < 2)
            return;

        string letter = pitch[..1];
        string octaveText = pitch[1..];

        if (letter == "c") { step = "C"; alter = 1; }
        else if (letter == "d") { step = "D"; alter = 1; }
        else if (letter == "f") { step = "F"; alter = 1; }
        else if (letter == "g") { step = "G"; alter = 1; }
        else if (letter == "a") { step = "A"; alter = 1; }
        else { step = letter.ToUpperInvariant(); alter = 0; }

        if (int.TryParse(octaveText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedOctave))
            octave = parsedOctave;
    }

    private static string GetNoteType(int duration)
    {
        return duration switch
        {
            1920 => "whole",
            1440 => "half",
            960 => "half",
            720 => "quarter",
            480 => "quarter",
            360 => "eighth",
            240 => "eighth",
            180 => "16th",
            120 => "16th",
            90 => "32nd",
            60 => "32nd",
            _ => string.Empty
        };
    }

    private static bool IsDottedDuration(int duration)
    {
        return duration is 1440 or 720 or 360 or 180 or 90;
    }

    private static string GetChordDisplayName(string chordCode)
    {
        if (string.IsNullOrWhiteSpace(chordCode))
            return string.Empty;

        if (chordCode.Length >= 3 && chordCode[0] == 'X')
        {
            char root = chordCode[1];
            char quality = chordCode[2];

            return root switch
            {
                'C' => quality == '7' ? "C7" : "C",
                'D' => quality == '7' ? "D7" : "Dm",
                'E' => quality == '7' ? "E7" : "Em",
                'F' => quality == '7' ? "F7" : "F",
                'G' => quality == '7' ? "G7" : "G",
                'A' => quality == '7' ? "A7" : "Am",
                'B' => "Bdim",
                'H' => "Eb",
                'L' => "Ab",
                'K' => "Bb",
                '2' => "D",
                '3' => "E7",
                '6' => "A7",
                'm' => "Fm",
                _ => chordCode
            };
        }

        return chordCode;
    }

    private static int GetDuration(string key)
    {
        return key switch
        {
            "00" or "11" => 3840,
            "1:" => 3360,
            "1." or "12" or "21" => 2880,
            "41" or "14" => 2400,
            "81" or "18" => 2160,
            "1S" or "S1" => 2040,
            "1T" or "T1" => 1980,
            "1U" or "U1" => 1950,
            "01" or "22" => 1920,
            "2:" => 1680,
            "2." or "24" or "42" => 1440,
            "28" or "82" => 1200,
            "2S" or "S2" => 1080,
            "2T" or "T2" => 1020,
            "2U" or "U2" => 990,
            "02" or "44" => 960,
            "4:" => 840,
            "4." or "48" or "84" => 720,
            "13" => 640,
            "4S" or "S4" => 600,
            "4T" or "T4" => 540,
            "4U" or "U4" => 510,
            "04" => 480,
            "8:" => 420,
            "8." or "8S" or "S8" => 360,
            "23" => 320,
            "8T" or "T8" => 300,
            "8U" or "U8" => 270,
            "08" => 240,
            "S:" => 210,
            "S." or "ST" or "TS" => 180,
            "43" => 160,
            "SU" or "US" => 150,
            "SV" => 135,
            "16" or "0S" => 120,
            "T:" => 105,
            "T." or "TU" or "UT" => 90,
            "83" => 80,
            "32" or "0T" => 60,
            "U." => 45,
            "S3" => 40,
            "64" or "0U" => 30,
            "T3" => 20,
            "0V" => 15,
            "U3" => 10,
            _ => 0
        };
    }
}
