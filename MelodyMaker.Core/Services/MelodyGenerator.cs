using System.Globalization;
using System.Text;
using MelodyMaker.Core.Models;

namespace MelodyMaker.Core.Services;

/// <summary>
/// Step 4：把原本 WinForms Form1.cs 的核心音樂生成概念搬進 ASP.NET Core 可重用服務。
/// 這個類別刻意不引用 System.Windows.Forms，不使用 TextBox / ComboBox / MessageBox，之後才能部署到 Azure。
///
/// 已搬入的 WinForms 核心模組：
/// 1. Style / Emotion / Key / Mode 解析。
/// 2. 依歌曲長度產生 chord progression。
/// 3. 江南 / Pop / JPop / Jazz 的 chord template 與功能和聲偏好。
/// 4. 使用 2024_PitchSet.xml / 2024_RhythmSet.xml 的舊版素材庫產生主旋律。
/// 5. 依和弦與情緒產生主旋律、副旋律。
/// 6. 旋律後處理：小節補滿、休止符合併、結尾解決、音域限制。
/// 7. 分析報告：音符數、休止比例、跳進比例、和弦支持率。
/// 8. Step 5：移植 Keyboard-style Seed Input 概念，讓使用者鍵盤輸入的 motif 影響主旋律。
/// 9. Step 7：移植 harmony score / target range / 平行五八度 / 風格分析報告。
/// 10. Step 8：移植江南 ThemeSeed 與 A / A' / A / A'' 起句結構，並保留 judge export 可用的分析欄位。
/// 11. Step 11：移植江南 functional journey / tonic orbit closing / melody post-processing，提高與原 WinForms 音樂輸出的相似度。
/// 12. Step 12：移植原 WinForms 副旋律 counter melody 邏輯，依主旋律逐音產生下方聲部。
/// 13. Step 13：強化 XML Rhythm/Pitch selection、chord-specific melody penalty 與 per-bar post-processing，讓輸出更接近原 WinForms。
/// 14. Step 14：針對 Step 13 的速度問題做效能最佳化：降低重複副旋律 refine、減少 XML 全量排序、加入取樣與早停。
/// </summary>
public sealed class MelodyGenerator
{
    private const int TicksPerBeat = 480;
    private const int TicksPerBar = 1920;

    private readonly LegacyPatternRepository _repository;

    public MelodyGenerator()
        : this(null)
    {
    }

    public MelodyGenerator(string? dataFolder)
    {
        _repository = LegacyPatternRepository.Load(dataFolder);
    }

    private enum MelodyStyle
    {
        JiangNan,
        Pop,
        JPop,
        Jazz
    }

    private enum EmotionType
    {
        Neutral,
        Calm,
        Bright,
        Sad,
        Tense,
        Energetic
    }

    private enum ModeType
    {
        Auto,
        Major,
        Minor,
        Pentatonic,
        Dorian,
        Mixolydian
    }

    private enum ChordDegree
    {
        I,
        ii,
        iii,
        IV,
        V,
        V7,
        vi,
        viiDim,
        bIII,
        bVI,
        bVII,
        II,
        III7,
        VI7,
        iv,
        Unknown
    }

    private enum TimeSectionRole
    {
        Opening,
        Development,
        Contrast,
        Closing
    }

    /// <summary>
    /// Step 11：江南和聲旅程功能分類，對應原 WinForms 的 JiangNanHarmonyFunction。
    /// Home 包含真正的 I 與 iii/vi 暫時的家；Bridge 是 ii/IV；Outside 是 V/V7/vii°。
    /// </summary>
    private enum JiangNanHarmonyFunction
    {
        Home,
        Bridge,
        Outside
    }

    private sealed record TimeSectionInfo(TimeSectionRole Role, int StartBar, int EndBar)
    {
        public bool ContainsBar(int barIndex) => barIndex >= StartBar && barIndex <= EndBar;
    }

    private sealed record ChordInfo(string Code, ChordDegree Degree, int RootSemitone, string DisplayName, bool MinorKeyContext = false);

    private sealed record NoteChoice(string Pitch, int Semitone, bool IsChordTone);

    /// <summary>
    /// Step 9：完整音樂品質優先移植用候選。
    /// 原 WinForms 不是只生成一次，而是會用和聲、風格、旋律線條與 XML pattern 品質來挑結果。
    /// Web 版把這個概念改成後端候選排序，目標是讓輸出音樂更接近 WinForms 原版。
    /// </summary>
    private sealed record CompleteSongCandidate(
        IReadOnlyList<string> MainBars,
        string MainYNote,
        string SubYNote,
        int Score,
        string ScoreBreakdown);

    /// <summary>
    /// Step 10：對應原 WinForms 的 xmlDraftMainMelodyBars / xmlDraftChordProgression / hasXmlMelodyDraft。
    /// 這個草稿不是 UI 狀態，而是「先從 XML 旋律素材產生主旋律，再反推和弦」的 melody-first 生成路徑。
    /// 目標是讓沒有手動輸入和弦時，Web 版的江南結果更接近原 WinForms。
    /// </summary>
    private sealed record LegacyMelodyDraft(
        IReadOnlyList<string> MainBars,
        string InferredChordText,
        string Report);

    /// <summary>
    /// Step 8：原 WinForms 的 JiangNanThemeSeed 移植版。
    /// 目的不是保存 UI 狀態，而是在後端生成期間固定一個 A 主題，讓前 4 小節形成
    /// A / A' / A / A''，避免江南風格每小節都像重新隨機抽樣。
    /// </summary>
    private sealed record JiangNanThemeSeed(
        IReadOnlyList<string> Pitches,
        IReadOnlyList<string> Rhythms,
        string ContourSignature,
        string Source,
        int NonRestCount);

    public string GenerateChordText(MelodyRequest request)
    {
        MelodyStyle style = ParseStyle(request.Style);
        EmotionType emotion = ParseEmotion(request.Emotion);
        ModeType mode = ResolveModeForStyle(style, ParseMode(request.Mode), emotion);
        int tempo = NormalizeTempo(request.Tempo);
        int barCount = GetSongBarCount(request.SongMinutes, tempo);
        string effectiveKey = GetEffectiveKeyText(request.Key, style, emotion);
        int keyRoot = GetSelectedKeyRootSemitone(effectiveKey);
        bool minorKey = IsMinorKey(effectiveKey, mode);

        // Step 10：Gen Chord 按鈕若是江南風格，也改成原 WinForms 的 melody-first 反推和弦。
        if (style == MelodyStyle.JiangNan && string.IsNullOrWhiteSpace(request.ChordText))
        {
            List<string> seedPitches = request.UseSeedInput
                ? ParseSeedPitches(request.SeedText, request.KeyboardOctave, keyRoot, minorKey, mode)
                : new List<string>();

            JiangNanThemeSeed? themeSeed = BuildJiangNanThemeSeed(
                emotion,
                mode,
                keyRoot,
                minorKey,
                GetChordInfo(GetChordCodeFromDegree(ChordDegree.I), keyRoot, minorKey),
                seedPitches);

            LegacyMelodyDraft? draft = TryBuildXmlMelodyDraftAndInferChords(
                style,
                emotion,
                mode,
                keyRoot,
                minorKey,
                barCount,
                seedPitches,
                request.SeedMode,
                themeSeed);

            if (draft is not null && ValidateChordProgression(draft.InferredChordText))
                return draft.InferredChordText;
        }

        return GenerateChordProgressionBySongLength(style, emotion, barCount);
    }

    public MelodyResult Generate(MelodyRequest request)
    {
        MelodyStyle style = ParseStyle(request.Style);
        EmotionType emotion = ParseEmotion(request.Emotion);
        ModeType mode = ResolveModeForStyle(style, ParseMode(request.Mode), emotion);
        int tempo = NormalizeTempo(request.Tempo);
        int barCount = GetSongBarCount(request.SongMinutes, tempo);
        string effectiveKey = GetEffectiveKeyText(request.Key, style, emotion);
        int keyRoot = GetSelectedKeyRootSemitone(effectiveKey);
        bool minorKey = IsMinorKey(effectiveKey, mode);

        string chordText = NormalizeOrGenerateChordText(request.ChordText, style, emotion, barCount);
        if (!ValidateChordProgression(chordText))
            chordText = GenerateChordProgressionBySongLength(style, emotion, barCount);

        List<string> seedPitches = request.UseSeedInput
            ? ParseSeedPitches(request.SeedText, request.KeyboardOctave, keyRoot, minorKey, mode)
            : new List<string>();

        JiangNanThemeSeed? jiangNanThemeSeed = null;
        LegacyMelodyDraft? legacyMelodyDraft = null;

        // Step 10：原 WinForms 的江南 Gen Chord 不是單純先配和弦，
        // 而是會先用 XML PitchSet/RhythmSet 產生主旋律草稿，再由旋律反推 chord progression。
        // 只有在使用者沒有手動輸入和弦時啟用，避免覆蓋使用者指定的和聲。
        bool hasManualChordInput = !string.IsNullOrWhiteSpace(request.ChordText);
        if (style == MelodyStyle.JiangNan && !hasManualChordInput)
        {
            jiangNanThemeSeed = BuildJiangNanThemeSeed(
                emotion,
                mode,
                keyRoot,
                minorKey,
                GetChordInfo(GetChordCodeFromDegree(ChordDegree.I), keyRoot, minorKey),
                seedPitches);

            legacyMelodyDraft = TryBuildXmlMelodyDraftAndInferChords(
                style,
                emotion,
                mode,
                keyRoot,
                minorKey,
                barCount,
                seedPitches,
                request.SeedMode,
                jiangNanThemeSeed);

            if (legacyMelodyDraft is not null && ValidateChordProgression(legacyMelodyDraft.InferredChordText))
                chordText = legacyMelodyDraft.InferredChordText;
        }

        jiangNanThemeSeed ??= style == MelodyStyle.JiangNan
            ? BuildJiangNanThemeSeed(
                emotion,
                mode,
                keyRoot,
                minorKey,
                GetChordInfo(GetChordCodeAtBar(chordText, 0), keyRoot, minorKey),
                seedPitches)
            : null;

        CompleteSongCandidate bestCandidate = BuildBestCompleteSongCandidate(
            style,
            emotion,
            mode,
            keyRoot,
            minorKey,
            chordText,
            barCount,
            seedPitches,
            request.SeedMode,
            request.SubDensity,
            jiangNanThemeSeed,
            legacyMelodyDraft);

        string mainYNote = bestCandidate.MainYNote;
        string subYNote = bestCandidate.SubYNote;

        string analysisReport = AnalyzeGeneratedMusic(mainYNote, subYNote, chordText, style, emotion, barCount, keyRoot, minorKey, seedPitches, request.UseSeedInput, request.SeedMode);
        analysisReport += BuildStep8ThemeSeedReport(jiangNanThemeSeed);
        if (legacyMelodyDraft is not null)
            analysisReport += Environment.NewLine + Environment.NewLine + legacyMelodyDraft.Report;
        analysisReport += Environment.NewLine + bestCandidate.ScoreBreakdown;

        return new MelodyResult
        {
            Title = BuildTitle(style, emotion, effectiveKey, mode.ToString()),
            ChordText = chordText,
            MainYNote = mainYNote,
            SubYNote = subYNote,
            Tempo = tempo,
            Key = NormalizeKeyName(effectiveKey),
            Mode = mode.ToString(),
            AnalysisReport = analysisReport
        };
    }



    /// <summary>
    /// Step 6：把原 WinForms Expert Mode 的「使用目前小節資料重新輸出」改成 Web 可用版本。
    /// 使用者可以在網頁上手動修改 ChordText / Main YNote / Sub YNote，再重新產生 MusicXML / PDF / MIDI。
    /// </summary>
    public MelodyResult GenerateFromExpert(MelodyRequest request)
    {
        MelodyStyle style = ParseStyle(request.Style);
        EmotionType emotion = ParseEmotion(request.Emotion);
        ModeType mode = ResolveModeForStyle(style, ParseMode(request.Mode), emotion);
        int tempo = NormalizeTempo(request.Tempo);
        string effectiveKey = GetEffectiveKeyText(request.Key, style, emotion);
        int keyRoot = GetSelectedKeyRootSemitone(effectiveKey);
        bool minorKey = IsMinorKey(effectiveKey, mode);

        string mainYNote = NormalizeYNoteText(request.ExpertMainYNote);
        string subYNote = NormalizeYNoteText(request.ExpertSubYNote);

        if (string.IsNullOrWhiteSpace(mainYNote))
            return Generate(request);

        int inferredBars = Math.Max(
            1,
            Math.Max(
                GetYNoteBarCount(mainYNote),
                GetChordTextBarCount(string.IsNullOrWhiteSpace(request.ExpertChordText) ? request.ChordText : request.ExpertChordText)));

        inferredBars = Math.Clamp(inferredBars, 1, 80);

        string chordSource = string.IsNullOrWhiteSpace(request.ExpertChordText)
            ? request.ChordText
            : request.ExpertChordText;

        string chordText = NormalizeOrGenerateChordText(chordSource, style, emotion, inferredBars);
        string fallbackPitch = PitchFromSemitone(keyRoot, minorKey ? 4 : 5);
        mainYNote = NormalizeYNoteToBarCount(mainYNote, inferredBars, fallbackPitch);

        if (string.IsNullOrWhiteSpace(subYNote))
        {
            subYNote = GenerateSubMelodyForChordTextWithHarmonyRefinement(
                style,
                emotion,
                request.SubDensity,
                chordText,
                keyRoot,
                minorKey,
                inferredBars,
                mainYNote);
        }
        else
        {
            subYNote = NormalizeYNoteToBarCount(subYNote, inferredBars, "00");
        }

        string report = AnalyzeGeneratedMusic(
            mainYNote,
            subYNote,
            chordText,
            style,
            emotion,
            inferredBars,
            keyRoot,
            minorKey,
            Array.Empty<string>(),
            false,
            request.SeedMode);

        report += Environment.NewLine + Environment.NewLine +
                  "Step 6 Expert Mode：已使用你在網頁上編輯的 ChordText / Main YNote / Sub YNote 重新產生樂譜。" + Environment.NewLine +
                  "這對應原 WinForms 的 RenderGeneratedSongToExpertMode / 多小節手動編輯概念，但改成 Web 表單回傳資料。";

        return new MelodyResult
        {
            Title = BuildTitle(style, emotion, effectiveKey, mode.ToString()) + " / Expert Mode",
            ChordText = chordText,
            MainYNote = mainYNote,
            SubYNote = subYNote,
            Tempo = tempo,
            Key = NormalizeKeyName(effectiveKey),
            Mode = mode.ToString(),
            AnalysisReport = report
        };
    }

    /// <summary>
    /// Step 6：移植原 WinForms 的 Update Sub Melody 概念。
    /// 保留目前主旋律與和弦，只根據 SubDensity / Style / Emotion 重新產生副旋律。
    /// </summary>
    public MelodyResult RegenerateSubMelodyFromExpert(MelodyRequest request)
    {
        MelodyStyle style = ParseStyle(request.Style);
        EmotionType emotion = ParseEmotion(request.Emotion);
        ModeType mode = ResolveModeForStyle(style, ParseMode(request.Mode), emotion);
        int tempo = NormalizeTempo(request.Tempo);
        string effectiveKey = GetEffectiveKeyText(request.Key, style, emotion);
        int keyRoot = GetSelectedKeyRootSemitone(effectiveKey);
        bool minorKey = IsMinorKey(effectiveKey, mode);

        string mainYNote = NormalizeYNoteText(request.ExpertMainYNote);
        if (string.IsNullOrWhiteSpace(mainYNote))
            return Generate(request);

        int inferredBars = Math.Max(
            1,
            Math.Max(
                GetYNoteBarCount(mainYNote),
                GetChordTextBarCount(string.IsNullOrWhiteSpace(request.ExpertChordText) ? request.ChordText : request.ExpertChordText)));

        inferredBars = Math.Clamp(inferredBars, 1, 80);

        string chordSource = string.IsNullOrWhiteSpace(request.ExpertChordText)
            ? request.ChordText
            : request.ExpertChordText;

        string chordText = NormalizeOrGenerateChordText(chordSource, style, emotion, inferredBars);
        mainYNote = NormalizeYNoteToBarCount(mainYNote, inferredBars, PitchFromSemitone(keyRoot, minorKey ? 4 : 5));
        string subYNote = GenerateSubMelodyForChordTextWithHarmonyRefinement(
            style,
            emotion,
            request.SubDensity,
            chordText,
            keyRoot,
            minorKey,
            inferredBars,
            mainYNote);

        string report = AnalyzeGeneratedMusic(
            mainYNote,
            subYNote,
            chordText,
            style,
            emotion,
            inferredBars,
            keyRoot,
            minorKey,
            Array.Empty<string>(),
            false,
            request.SeedMode);

        report += Environment.NewLine + Environment.NewLine +
                  "Step 6 Update Sub Melody：已保留主旋律與和弦，重新生成副旋律。" + Environment.NewLine +
                  "這對應原 WinForms 的 btnUpdateSubMelody_Click，但 Web 版不操作 groupBox 控制項，而是用 Expert textarea 資料重算。";

        return new MelodyResult
        {
            Title = BuildTitle(style, emotion, effectiveKey, mode.ToString()) + " / Updated Counter Melody",
            ChordText = chordText,
            MainYNote = mainYNote,
            SubYNote = subYNote,
            Tempo = tempo,
            Key = NormalizeKeyName(effectiveKey),
            Mode = mode.ToString(),
            AnalysisReport = report
        };
    }

    public IReadOnlyList<MelodyBarEditRow> BuildExpertRows(MelodyResult result)
    {
        int barCount = Math.Max(
            1,
            Math.Max(
                GetChordTextBarCount(result.ChordText),
                Math.Max(GetYNoteBarCount(result.MainYNote), GetYNoteBarCount(result.SubYNote))));

        List<string> mainBars = SplitYNoteIntoBars(result.MainYNote, barCount);
        List<string> subBars = SplitYNoteIntoBars(result.SubYNote, barCount);
        int keyRoot = GetSelectedKeyRootSemitone(result.Key);
        bool minorKey = NormalizeText(result.Key).Contains("minor", StringComparison.OrdinalIgnoreCase) ||
                        NormalizeText(result.Mode).Equals("Minor", StringComparison.OrdinalIgnoreCase);

        List<MelodyBarEditRow> rows = new(capacity: barCount);
        for (int bar = 0; bar < barCount; bar++)
        {
            string chordCode = GetChordCodeAtBar(result.ChordText, bar);
            rows.Add(new MelodyBarEditRow
            {
                BarNumber = bar + 1,
                ChordCode = chordCode,
                ChordName = GetChordDisplayName(chordCode, keyRoot, minorKey),
                MainYNote = bar < mainBars.Count ? mainBars[bar] : "0001",
                SubYNote = bar < subBars.Count ? subBars[bar] : "0001",
                MainPreview = YNoteToPreview(bar < mainBars.Count ? mainBars[bar] : string.Empty),
                SubPreview = YNoteToPreview(bar < subBars.Count ? subBars[bar] : string.Empty)
            });
        }

        return rows;
    }

    private static MelodyStyle ParseStyle(string? text)
    {
        string value = NormalizeText(text);

        if (value.Contains("江南", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("jiang", StringComparison.OrdinalIgnoreCase))
            return MelodyStyle.JiangNan;

        if (value.Equals("jpop", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("j-pop", StringComparison.OrdinalIgnoreCase))
            return MelodyStyle.JPop;

        if (value.Contains("jazz", StringComparison.OrdinalIgnoreCase))
            return MelodyStyle.Jazz;

        return MelodyStyle.Pop;
    }

    private static EmotionType ParseEmotion(string? text)
    {
        string value = NormalizeText(text);

        if (value.Contains("平靜", StringComparison.OrdinalIgnoreCase) || value.Equals("calm", StringComparison.OrdinalIgnoreCase))
            return EmotionType.Calm;
        if (value.Contains("明亮", StringComparison.OrdinalIgnoreCase) || value.Equals("bright", StringComparison.OrdinalIgnoreCase))
            return EmotionType.Bright;
        if (value.Contains("悲", StringComparison.OrdinalIgnoreCase) || value.Equals("sad", StringComparison.OrdinalIgnoreCase))
            return EmotionType.Sad;
        if (value.Contains("緊張", StringComparison.OrdinalIgnoreCase) || value.Equals("tense", StringComparison.OrdinalIgnoreCase))
            return EmotionType.Tense;
        if (value.Contains("激昂", StringComparison.OrdinalIgnoreCase) || value.Equals("energetic", StringComparison.OrdinalIgnoreCase))
            return EmotionType.Energetic;

        return EmotionType.Neutral;
    }

    private static ModeType ParseMode(string? text)
    {
        string value = NormalizeText(text);
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return ModeType.Auto;
        if (value.Equals("minor", StringComparison.OrdinalIgnoreCase) || value.Contains("小調", StringComparison.OrdinalIgnoreCase)) return ModeType.Minor;
        if (value.Equals("pentatonic", StringComparison.OrdinalIgnoreCase) || value.Contains("五聲", StringComparison.OrdinalIgnoreCase)) return ModeType.Pentatonic;
        if (value.Equals("dorian", StringComparison.OrdinalIgnoreCase)) return ModeType.Dorian;
        if (value.Equals("mixolydian", StringComparison.OrdinalIgnoreCase)) return ModeType.Mixolydian;
        return ModeType.Major;
    }

    private static ModeType ResolveModeForStyle(MelodyStyle style, ModeType requestedMode, EmotionType emotion)
    {
        // Step 15：Style isolation。之前頁面預設是 Pentatonic，
        // 使用者只改 Style 沒改 Mode 時，Pop/JPop/Jazz 仍會走五聲音階，
        // 聽起來就會全部偏江南。Auto 會依風格選預設 mode。
        if (requestedMode != ModeType.Auto)
            return requestedMode;

        return style switch
        {
            MelodyStyle.JiangNan => ModeType.Major,
            MelodyStyle.Jazz => emotion == EmotionType.Tense
                ? ModeType.Mixolydian
                : emotion == EmotionType.Sad ? ModeType.Dorian : ModeType.Major,
            _ => emotion == EmotionType.Sad ? ModeType.Minor : ModeType.Major
        };
    }

    private static string NormalizeText(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();

    private static int NormalizeTempo(int tempo) => Math.Clamp(tempo <= 0 ? 60 : tempo, 40, 240);

    private static int GetSongBarCount(int songMinutes, int tempo)
    {
        int minutes = Math.Clamp(songMinutes <= 0 ? 1 : songMinutes, 1, 10);
        int bars = (int)Math.Round(minutes * tempo / 4.0);

        if (bars < 4)
            bars = 4;

        if (bars % 4 != 0)
            bars = ((bars / 4) + 1) * 4;

        return bars;
    }

    private static string NormalizeKeyName(string? key)
    {
        string value = NormalizeText(key);
        return string.IsNullOrWhiteSpace(value) ? "C" : value;
    }

    private static string GetEffectiveKeyText(string? key, MelodyStyle style, EmotionType emotion)
    {
        string value = NormalizeText(key);
        if (!string.IsNullOrWhiteSpace(value) && !value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return value;

        return GetAutoKeyText(style, emotion);
    }

    private static string GetAutoKeyText(MelodyStyle style, EmotionType emotion)
    {
        if (style == MelodyStyle.JiangNan)
            return "C";

        if (style == MelodyStyle.Pop)
        {
            if (emotion == EmotionType.Sad) return "A minor";
            if (emotion == EmotionType.Calm) return "F";
            if (emotion == EmotionType.Bright) return "G";
            if (emotion == EmotionType.Energetic) return "D";
            if (emotion == EmotionType.Tense) return "E minor";
            return "C";
        }

        if (style == MelodyStyle.JPop)
        {
            if (emotion == EmotionType.Sad) return "E minor";
            if (emotion == EmotionType.Bright) return "G";
            if (emotion == EmotionType.Energetic) return "A";
            if (emotion == EmotionType.Tense) return "F# minor";
            return "G";
        }

        if (style == MelodyStyle.Jazz)
        {
            if (emotion == EmotionType.Sad) return "Bb";
            if (emotion == EmotionType.Bright) return "F";
            if (emotion == EmotionType.Energetic) return "Eb";
            return "C";
        }

        return "C";
    }

    private static string BuildTitle(MelodyStyle style, EmotionType emotion, string? key, string? mode)
    {
        string styleName = style == MelodyStyle.JiangNan ? "江南風格" : style.ToString();
        string keyName = string.IsNullOrWhiteSpace(key) ? "C" : key.Trim();
        string modeName = string.IsNullOrWhiteSpace(mode) ? "Major" : mode.Trim();
        return $"{styleName} / {emotion} / {keyName} {modeName}";
    }

    private string NormalizeOrGenerateChordText(string? rawChordText, MelodyStyle style, EmotionType emotion, int barCount)
    {
        string cleaned = new string((rawChordText ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (cleaned.Length >= 5)
        {
            List<string> chords = SplitChordText(cleaned)
                .Where(c => c.Length == 5 && c[0] == 'X')
                .ToList();

            if (chords.Count > 0)
            {
                while (chords.Count < barCount)
                    chords.Add(chords[^1]);

                return string.Concat(chords.Take(barCount));
            }
        }

        return GenerateBestChordProgression(style, emotion, barCount);
    }

    private string GenerateBestChordProgression(MelodyStyle style, EmotionType emotion, int barCount)
    {
        // Step 9：原 WinForms 的 Gen Chord 會依模板、轉接分數、段落與風格挑比較好的進行。
        // Web 版這裡不再只拿第一個模板，而是把 ChordProgressions.txt 與原版風格模板一起評分。
        List<string> candidates = new();

        void AddCandidate(string? chordText)
        {
            if (string.IsNullOrWhiteSpace(chordText))
                return;

            string normalized = ExpandChordTextToBarCount(chordText, barCount);
            if (ValidateChordProgression(normalized) && !candidates.Contains(normalized, StringComparer.Ordinal))
                candidates.Add(normalized);
        }

        AddCandidate(GenerateChordProgressionBySongLength(style, emotion, barCount));

        foreach (LegacyChordProgression progression in _repository.ChordProgressions)
            AddCandidate(progression.ChordText);

        foreach (ChordDegree[] pattern in GetStyleChordPatterns(style, emotion))
            AddCandidate(BuildChordTextFromPattern(pattern, style, emotion, barCount));

        if (style == MelodyStyle.JiangNan)
            AddCandidate(GenerateClassicJiangNanFallbackChordProgression(barCount));

        if (candidates.Count == 0)
            return GenerateChordProgressionBySongLength(style, emotion, barCount);

        return candidates
            .Select(c => new { ChordText = c, Score = ScoreChordProgressionOriginalFidelity(c, style, emotion, barCount) })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.ChordText, StringComparer.Ordinal)
            .First()
            .ChordText;
    }

    private static string ExpandChordTextToBarCount(string chordText, int barCount)
    {
        List<string> chords = SplitChordText(new string((chordText ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()))
            .Where(c => c.Length == 5 && c[0] == 'X')
            .ToList();

        if (chords.Count == 0)
            return string.Empty;

        List<string> result = new(capacity: barCount);
        for (int i = 0; i < barCount; i++)
            result.Add(chords[i % chords.Count]);

        return RepairCadence(string.Concat(result));
    }

    private string BuildChordTextFromPattern(ChordDegree[] pattern, MelodyStyle style, EmotionType emotion, int barCount)
    {
        if (pattern.Length == 0)
            pattern = [ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV];

        List<ChordDegree> committed = new(capacity: barCount);
        List<string> result = new(capacity: barCount);
        List<TimeSectionInfo> sections = BuildTimeSections(barCount);

        for (int bar = 0; bar < barCount; bar++)
        {
            TimeSectionRole role = GetSectionForBar(sections, bar).Role;
            bool isPhraseEnding = (bar + 1) % 4 == 0;
            bool isSongEnding = bar == barCount - 1;
            ChordDegree degree = pattern[bar % pattern.Length];

            degree = AdjustChordDegreeForOriginalSections(degree, style, emotion, role, isPhraseEnding, isSongEnding, bar, barCount);
            degree = LimitBorrowedChordDensity(degree, style, emotion, role, committed, bar, barCount);

            if (committed.Count > 0)
                degree = EnsureTransitionOrFallback(committed[^1], degree, style, emotion, role);

            if (style == MelodyStyle.JiangNan)
                degree = SanitizeJiangNanChordDegreeStrict(degree);

            committed.Add(degree);
            result.Add(GetChordCodeFromDegree(degree));
        }

        return RepairCadence(string.Concat(result));
    }

    private static ChordDegree AdjustChordDegreeForOriginalSections(
        ChordDegree degree,
        MelodyStyle style,
        EmotionType emotion,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding,
        int barIndex,
        int totalBars)
    {
        if (style == MelodyStyle.JiangNan && IsBorrowedOrSecondaryDegree(degree))
            degree = GetFunctionalSubstituteForSpice(degree, style, emotion, role);

        if (isSongEnding)
            return emotion == EmotionType.Sad && style != MelodyStyle.JiangNan ? ChordDegree.vi : ChordDegree.I;

        if (barIndex == totalBars - 2)
            return style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V;

        if (isPhraseEnding)
        {
            if (role == TimeSectionRole.Contrast && emotion == EmotionType.Sad)
                return style == MelodyStyle.JiangNan ? ChordDegree.I : ChordDegree.bVI;
            if (emotion is EmotionType.Tense or EmotionType.Energetic)
                return style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V;
            return style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V;
        }

        if (role == TimeSectionRole.Opening && IsBorrowedOrSecondaryDegree(degree) && emotion is not EmotionType.Tense and not EmotionType.Energetic)
            return style == MelodyStyle.JPop && emotion == EmotionType.Sad ? ChordDegree.vi : ChordDegree.I;

        if (role == TimeSectionRole.Closing && degree == ChordDegree.viiDim && emotion != EmotionType.Tense)
            return ChordDegree.V;

        return degree;
    }

    private static ChordDegree LimitBorrowedChordDensity(
        ChordDegree degree,
        MelodyStyle style,
        EmotionType emotion,
        TimeSectionRole role,
        IReadOnlyList<ChordDegree> previousDegrees,
        int barIndex,
        int totalBars)
    {
        if (style == MelodyStyle.JiangNan)
            return IsBorrowedOrSecondaryDegree(degree) ? GetFunctionalSubstituteForSpice(degree, style, emotion, role) : degree;

        if (!IsBorrowedOrSecondaryDegree(degree))
            return degree;

        if (barIndex >= totalBars - 2 || role == TimeSectionRole.Closing)
            return GetFunctionalSubstituteForSpice(degree, style, emotion, role);

        int recent4 = previousDegrees.Skip(Math.Max(0, previousDegrees.Count - 4)).Count(IsBorrowedOrSecondaryDegree);
        int recent8 = previousDegrees.Skip(Math.Max(0, previousDegrees.Count - 8)).Count(IsBorrowedOrSecondaryDegree);
        int max4 = style == MelodyStyle.Jazz ? 2 : emotion is EmotionType.Tense or EmotionType.Energetic ? 2 : 1;
        int max8 = style == MelodyStyle.Jazz ? 4 : 3;

        if (recent4 >= max4 || recent8 >= max8)
            return GetFunctionalSubstituteForSpice(degree, style, emotion, role);

        if (previousDegrees.Count > 0 && IsBorrowedOrSecondaryDegree(previousDegrees[^1]) && style != MelodyStyle.Jazz)
            return GetFunctionalSubstituteForSpice(degree, style, emotion, role);

        return degree;
    }

    private static ChordDegree EnsureTransitionOrFallback(ChordDegree previousDegree, ChordDegree candidateDegree, MelodyStyle style, EmotionType emotion, TimeSectionRole role)
    {
        if (IsValidChordTransition(previousDegree, candidateDegree, style))
            return candidateDegree;

        ChordDegree[] fallback =
        [
            GetFunctionalSubstituteForSpice(candidateDegree, style, emotion, role),
            ChordDegree.IV,
            ChordDegree.vi,
            ChordDegree.ii,
            style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V,
            ChordDegree.I
        ];

        foreach (ChordDegree degree in fallback)
        {
            if (IsValidChordTransition(previousDegree, degree, style))
                return degree;
        }

        return ChordDegree.I;
    }

    private static bool IsValidChordTransition(ChordDegree from, ChordDegree to, MelodyStyle style)
    {
        if (style == MelodyStyle.JiangNan)
        {
            if (!IsJiangNanAllowedDegreeForProgression(from) || !IsJiangNanAllowedDegreeForProgression(to))
                return false;

            bool fromDominant = from is ChordDegree.V or ChordDegree.V7 or ChordDegree.viiDim;
            bool toPredominant = to is ChordDegree.IV or ChordDegree.ii;
            if (fromDominant && toPredominant)
                return false;
        }

        if (style == MelodyStyle.Jazz && (from == ChordDegree.V || from == ChordDegree.V7) && to == ChordDegree.IV)
            return false;

        return GetChordTransitionScore(from, to) + GetStyleChordBonus(style, to) >= 45;
    }

    private static bool IsJiangNanAllowedDegreeForProgression(ChordDegree degree)
    {
        return degree is ChordDegree.I or ChordDegree.IV or ChordDegree.V or ChordDegree.vi or ChordDegree.ii or ChordDegree.iii or ChordDegree.V7;
    }

    private static bool IsBorrowedOrSecondaryDegree(ChordDegree degree)
    {
        return degree is ChordDegree.bIII or ChordDegree.bVI or ChordDegree.bVII or ChordDegree.II or ChordDegree.III7 or ChordDegree.VI7 or ChordDegree.iv or ChordDegree.viiDim;
    }

    private static ChordDegree GetFunctionalSubstituteForSpice(ChordDegree degree, MelodyStyle style, EmotionType emotion, TimeSectionRole role)
    {
        return degree switch
        {
            ChordDegree.bIII => emotion == EmotionType.Sad ? ChordDegree.vi : ChordDegree.iii,
            ChordDegree.bVI => emotion == EmotionType.Sad ? ChordDegree.vi : ChordDegree.IV,
            ChordDegree.bVII => role == TimeSectionRole.Contrast ? ChordDegree.V : ChordDegree.IV,
            ChordDegree.II => ChordDegree.V,
            ChordDegree.III7 => ChordDegree.vi,
            ChordDegree.VI7 => ChordDegree.ii,
            ChordDegree.iv => emotion == EmotionType.Sad ? ChordDegree.vi : ChordDegree.IV,
            ChordDegree.viiDim => ChordDegree.V,
            _ => degree
        };
    }

    private static int ScoreChordProgressionOriginalFidelity(string chordText, MelodyStyle style, EmotionType emotion, int barCount)
    {
        List<string> chords = SplitChordText(chordText).Take(barCount).ToList();
        if (chords.Count == 0)
            return int.MinValue / 2;

        int score = 0;
        int tonicCount = 0;
        int dominantCount = 0;
        int spiceCount = 0;

        for (int i = 0; i < chords.Count; i++)
        {
            ChordDegree degree = GetChordInfo(chords[i]).Degree;
            if (degree is ChordDegree.I or ChordDegree.vi) tonicCount++;
            if (degree is ChordDegree.V or ChordDegree.V7 or ChordDegree.viiDim) dominantCount++;
            if (IsBorrowedOrSecondaryDegree(degree)) spiceCount++;

            score += GetStyleChordBonus(style, degree);
            score += GetEmotionChordBonus(emotion, degree);

            if (style == MelodyStyle.JiangNan && !IsJiangNanAllowedDegreeForProgression(degree))
                score -= 80;

            ChordDegree previous = i > 0 ? GetChordInfo(chords[i - 1]).Degree : ChordDegree.Unknown;
            if (i > 0)
            {
                int transition = GetChordTransitionScore(previous, degree);
                if (!IsValidChordTransition(previous, degree, style))
                    score -= 90;
                score += transition;
            }

            if (style == MelodyStyle.JiangNan)
            {
                score += GetJiangNanFunctionalJourneyScore(previous, degree, i, barCount);
                score += GetJiangNanJourneyTemplateScore(degree, previous, i, barCount, style);
                score += GetJiangNanTonicOrbitClosingScore(degree, previous, i, barCount);

                if (i == barCount - 2)
                {
                    if (degree == ChordDegree.I) score += 42;
                    if (degree == ChordDegree.IV) score += 28;
                    if (IsJiangNanTemporaryHomeDegree(degree)) score += 22;
                    if (degree == ChordDegree.V7) score -= 24;
                }
            }

            if ((i + 1) % 4 == 0 && (degree == ChordDegree.V || degree == ChordDegree.V7 || degree == ChordDegree.I || degree == ChordDegree.vi))
                score += 25;
        }

        if (chords.Count > 0 && GetChordInfo(chords[0]).Degree == ChordDegree.I) score += 35;
        if (chords.Count >= 2 && (GetChordInfo(chords[^2]).Degree == ChordDegree.V || GetChordInfo(chords[^2]).Degree == ChordDegree.V7)) score += 55;
        if (chords.Count >= 1 && GetChordInfo(chords[^1]).Degree == ChordDegree.I) score += 70;

        if (tonicCount == 0) score -= 100;
        if (dominantCount == 0) score -= 100;
        if (style != MelodyStyle.Jazz && spiceCount > Math.Max(1, barCount / 4)) score -= (spiceCount - Math.Max(1, barCount / 4)) * 45;
        if (style == MelodyStyle.Jazz && spiceCount >= 2) score += 45;

        return score;
    }

    private static int GetChordTransitionScore(ChordDegree from, ChordDegree to)
    {
        if (from == ChordDegree.Unknown || to == ChordDegree.Unknown) return 0;
        if (from == to) return 58;
        if (from == ChordDegree.III7 && to == ChordDegree.vi) return 108;
        if (from == ChordDegree.VI7 && to == ChordDegree.ii) return 108;
        if (from == ChordDegree.II && (to == ChordDegree.V || to == ChordDegree.V7)) return 108;
        if (from == ChordDegree.iv && (to == ChordDegree.I || to == ChordDegree.vi)) return 98;
        if (from == ChordDegree.bVII && (to == ChordDegree.I || to == ChordDegree.IV || to == ChordDegree.vi)) return 90;
        if (from == ChordDegree.bVI && (to == ChordDegree.bVII || to == ChordDegree.V || to == ChordDegree.I)) return 92;
        if (from == ChordDegree.bIII && (to == ChordDegree.IV || to == ChordDegree.vi || to == ChordDegree.bVI)) return 82;
        if ((from == ChordDegree.I || from == ChordDegree.vi) && (to == ChordDegree.iv || to == ChordDegree.bVII || to == ChordDegree.bVI)) return 78;
        if ((from == ChordDegree.IV || from == ChordDegree.vi) && (to == ChordDegree.III7 || to == ChordDegree.VI7)) return 82;
        if ((from == ChordDegree.I || from == ChordDegree.IV) && to == ChordDegree.II) return 72;

        return from switch
        {
            ChordDegree.I => to switch { ChordDegree.vi => 95, ChordDegree.IV => 90, ChordDegree.V => 90, ChordDegree.V7 => 90, ChordDegree.ii => 80, ChordDegree.iii => 70, ChordDegree.viiDim => 35, _ => 15 },
            ChordDegree.ii => to switch { ChordDegree.V7 => 100, ChordDegree.V => 95, ChordDegree.viiDim => 75, ChordDegree.IV => 65, ChordDegree.I => 50, ChordDegree.vi => 45, ChordDegree.iii => 35, _ => 15 },
            ChordDegree.iii => to switch { ChordDegree.vi => 92, ChordDegree.IV => 85, ChordDegree.ii => 65, ChordDegree.V => 60, ChordDegree.V7 => 60, ChordDegree.I => 55, ChordDegree.viiDim => 35, _ => 15 },
            ChordDegree.IV => to switch { ChordDegree.V => 100, ChordDegree.V7 => 100, ChordDegree.I => 88, ChordDegree.ii => 75, ChordDegree.vi => 65, ChordDegree.iii => 45, ChordDegree.viiDim => 50, _ => 15 },
            ChordDegree.V => to switch { ChordDegree.I => 100, ChordDegree.vi => 86, ChordDegree.IV => 50, ChordDegree.ii => 42, ChordDegree.iii => 50, ChordDegree.V7 => 82, ChordDegree.viiDim => 35, _ => 15 },
            ChordDegree.V7 => to switch { ChordDegree.I => 105, ChordDegree.vi => 84, ChordDegree.V => 60, ChordDegree.IV => 38, ChordDegree.ii => 35, ChordDegree.iii => 40, ChordDegree.viiDim => 30, _ => 15 },
            ChordDegree.vi => to switch { ChordDegree.IV => 96, ChordDegree.ii => 90, ChordDegree.V => 86, ChordDegree.V7 => 86, ChordDegree.I => 72, ChordDegree.iii => 65, ChordDegree.viiDim => 45, _ => 15 },
            ChordDegree.viiDim => to switch { ChordDegree.I => 100, ChordDegree.iii => 65, ChordDegree.V => 55, ChordDegree.V7 => 55, ChordDegree.vi => 45, ChordDegree.ii => 40, ChordDegree.IV => 35, _ => 15 },
            _ => IsBorrowedOrSecondaryDegree(from) || IsBorrowedOrSecondaryDegree(to) ? 42 : 15
        };
    }

    private static int GetStyleChordBonus(MelodyStyle style, ChordDegree degree)
    {
        return style switch
        {
            MelodyStyle.JiangNan => degree switch { ChordDegree.I => 30, ChordDegree.IV => 20, ChordDegree.V => 20, ChordDegree.vi => -4, ChordDegree.ii => -2, ChordDegree.iii => -2, _ => -30 },
            MelodyStyle.Pop => degree switch { ChordDegree.I => 20, ChordDegree.V => 18, ChordDegree.vi => 18, ChordDegree.IV => 18, ChordDegree.iii => 8, ChordDegree.ii => 8, ChordDegree.iv or ChordDegree.bVII or ChordDegree.III7 or ChordDegree.VI7 => 6, _ => 0 },
            MelodyStyle.JPop => degree switch { ChordDegree.IV => 20, ChordDegree.V => 20, ChordDegree.iii => 18, ChordDegree.vi => 18, ChordDegree.ii => 10, ChordDegree.III7 or ChordDegree.VI7 or ChordDegree.bVI or ChordDegree.bVII => 12, _ => 4 },
            MelodyStyle.Jazz => degree switch { ChordDegree.ii => 25, ChordDegree.V7 => 25, ChordDegree.I => 20, ChordDegree.VI7 => 16, ChordDegree.III7 => 12, ChordDegree.iv or ChordDegree.bVII => 10, _ => 0 },
            _ => 0
        };
    }

    private static int GetEmotionChordBonus(EmotionType emotion, ChordDegree degree)
    {
        return emotion switch
        {
            EmotionType.Calm => degree is ChordDegree.I or ChordDegree.IV or ChordDegree.vi ? 8 : 0,
            EmotionType.Bright => degree is ChordDegree.I or ChordDegree.IV or ChordDegree.V or ChordDegree.II ? 8 : 0,
            EmotionType.Sad => degree is ChordDegree.vi or ChordDegree.iv or ChordDegree.iii or ChordDegree.bVI ? 10 : 0,
            EmotionType.Tense => degree is ChordDegree.V7 or ChordDegree.viiDim or ChordDegree.bVI or ChordDegree.bVII ? 12 : 0,
            EmotionType.Energetic => degree is ChordDegree.I or ChordDegree.V or ChordDegree.II or ChordDegree.bVII ? 10 : 0,
            _ => 0
        };
    }

    private string GenerateChordProgressionBySongLength(MelodyStyle style, EmotionType emotion, int barCount)
    {
        if (style == MelodyStyle.JiangNan)
            return GenerateClassicJiangNanFallbackChordProgression(barCount);

        ChordDegree[] pattern = PickStylePattern(style, emotion, barCount);
        List<string> result = new(capacity: barCount);

        for (int bar = 0; bar < barCount; bar++)
        {
            ChordDegree degree;

            if (bar == barCount - 1)
            {
                degree = style == MelodyStyle.Jazz ? ChordDegree.I : ChordDegree.I;
            }
            else if ((bar + 1) % 8 == 0)
            {
                degree = GeneratePhraseEndingChord(style, emotion, bar, barCount);
            }
            else if ((bar + 1) % 4 == 0)
            {
                degree = style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V;
            }
            else
            {
                degree = pattern[bar % pattern.Length];
            }

            result.Add(GetChordCodeFromDegree(degree));
        }

        return RepairCadence(string.Concat(result));
    }

    private string GenerateClassicJiangNanFallbackChordProgression(int barCount)
    {
        ChordDegree[] journey =
        [
            ChordDegree.I,
            ChordDegree.vi,
            ChordDegree.IV,
            ChordDegree.V,
            ChordDegree.I,
            ChordDegree.iii,
            ChordDegree.IV,
            ChordDegree.V,
            ChordDegree.vi,
            ChordDegree.IV,
            ChordDegree.ii,
            ChordDegree.V,
            ChordDegree.I,
            ChordDegree.vi,
            ChordDegree.IV,
            ChordDegree.I
        ];

        List<string> chords = new(capacity: barCount);
        for (int i = 0; i < barCount; i++)
        {
            ChordDegree degree = journey[i % journey.Length];

            // 江南最後兩小節強化回家感。
            if (i == barCount - 2)
                degree = ChordDegree.V;
            else if (i == barCount - 1)
                degree = ChordDegree.I;

            chords.Add(GetChordCodeFromDegree(SanitizeJiangNanChordDegreeStrict(degree)));
        }

        return string.Concat(chords);
    }

    private static ChordDegree SanitizeJiangNanChordDegreeStrict(ChordDegree degree)
    {
        // 原 WinForms 版本對江南用更保守的功能替代：避免 vi/ii/iii 太直接造成西式流行感過強。
        return degree switch
        {
            ChordDegree.vi => ChordDegree.I,
            ChordDegree.ii => ChordDegree.IV,
            ChordDegree.iii => ChordDegree.V,
            ChordDegree.V7 => ChordDegree.V,
            ChordDegree.viiDim => ChordDegree.V,
            ChordDegree.I or ChordDegree.IV or ChordDegree.V => degree,
            _ => ChordDegree.I
        };
    }

    private ChordDegree[] PickStylePattern(MelodyStyle style, EmotionType emotion, int barCount)
    {
        List<ChordDegree[]> patterns = GetStyleChordPatterns(style, emotion);

        if (patterns.Count == 0)
            return [ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV];

        // 依歌曲長度與情緒穩定挑選，不完全隨機，讓同樣輸入比較容易重現。
        int index = Math.Abs((style, emotion, barCount, DateTime.UtcNow.Second).GetHashCode()) % patterns.Count;
        return patterns[index];
    }

    private static List<ChordDegree[]> GetStyleChordPatterns(MelodyStyle style, EmotionType emotion)
    {
        List<ChordDegree[]> patterns = new();

        if (style == MelodyStyle.JiangNan)
        {
            patterns.Add([ChordDegree.I, ChordDegree.vi, ChordDegree.ii, ChordDegree.V, ChordDegree.I, ChordDegree.iii, ChordDegree.IV, ChordDegree.I]);
            patterns.Add([ChordDegree.I, ChordDegree.IV, ChordDegree.ii, ChordDegree.V, ChordDegree.I, ChordDegree.vi, ChordDegree.V, ChordDegree.I]);
            patterns.Add([ChordDegree.I, ChordDegree.iii, ChordDegree.IV, ChordDegree.V, ChordDegree.I, ChordDegree.vi, ChordDegree.ii, ChordDegree.I]);
        }
        else if (style == MelodyStyle.Pop)
        {
            patterns.Add([ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV, ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV]);
            patterns.Add([ChordDegree.vi, ChordDegree.IV, ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV, ChordDegree.I, ChordDegree.V]);
            patterns.Add([ChordDegree.I, ChordDegree.III7, ChordDegree.vi, ChordDegree.bVII, ChordDegree.IV, ChordDegree.iv, ChordDegree.I, ChordDegree.V]);
            patterns.Add([ChordDegree.I, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V, ChordDegree.iii, ChordDegree.III7, ChordDegree.vi, ChordDegree.IV]);
            patterns.Add([ChordDegree.I, ChordDegree.bVII, ChordDegree.IV, ChordDegree.iv, ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV]);
        }
        else if (style == MelodyStyle.JPop)
        {
            patterns.Add([ChordDegree.IV, ChordDegree.V, ChordDegree.iii, ChordDegree.vi, ChordDegree.ii, ChordDegree.V, ChordDegree.I, ChordDegree.I]);
            patterns.Add([ChordDegree.IV, ChordDegree.V, ChordDegree.iii, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V, ChordDegree.I, ChordDegree.III7]);
            patterns.Add([ChordDegree.vi, ChordDegree.IV, ChordDegree.V, ChordDegree.I, ChordDegree.bVI, ChordDegree.bVII, ChordDegree.I, ChordDegree.V]);
            patterns.Add([ChordDegree.IV, ChordDegree.V, ChordDegree.iii, ChordDegree.vi, ChordDegree.iv, ChordDegree.I, ChordDegree.II, ChordDegree.V]);
            patterns.Add([ChordDegree.I, ChordDegree.III7, ChordDegree.vi, ChordDegree.bVI, ChordDegree.IV, ChordDegree.V, ChordDegree.iii, ChordDegree.vi]);
        }
        else if (style == MelodyStyle.Jazz)
        {
            patterns.Add([ChordDegree.ii, ChordDegree.V7, ChordDegree.I, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V7, ChordDegree.I, ChordDegree.V7]);
            patterns.Add([ChordDegree.I, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V7, ChordDegree.iii, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V7]);
            patterns.Add([ChordDegree.I, ChordDegree.bVII, ChordDegree.IV, ChordDegree.iv, ChordDegree.iii, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V7]);
            patterns.Add([ChordDegree.ii, ChordDegree.V7, ChordDegree.I, ChordDegree.III7, ChordDegree.vi, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V7]);
        }

        ApplyEmotionChordPatterns(style, emotion, patterns);

        if (patterns.Count == 0)
        {
            patterns.Add([ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV, ChordDegree.I, ChordDegree.V, ChordDegree.I, ChordDegree.I]);
        }

        return patterns;
    }

    private static void AddWeightedPattern(List<ChordDegree[]> patterns, ChordDegree[] pattern, int weight)
    {
        weight = Math.Max(1, weight);
        for (int i = 0; i < weight; i++)
            patterns.Add(pattern);
    }

    private static void ApplyEmotionChordPatterns(MelodyStyle style, EmotionType emotion, List<ChordDegree[]> patterns)
    {
        if (emotion == EmotionType.Neutral)
            return;

        if (style == MelodyStyle.JiangNan)
        {
            if (emotion is EmotionType.Calm or EmotionType.Sad)
                AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.vi, ChordDegree.ii, ChordDegree.V, ChordDegree.I, ChordDegree.iii, ChordDegree.IV, ChordDegree.I], 2);
            else if (emotion is EmotionType.Bright or EmotionType.Energetic)
                AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.IV, ChordDegree.ii, ChordDegree.V, ChordDegree.I, ChordDegree.vi, ChordDegree.V7, ChordDegree.I], 2);
            else if (emotion == EmotionType.Tense)
                AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.ii, ChordDegree.V7, ChordDegree.I, ChordDegree.vi, ChordDegree.IV, ChordDegree.viiDim, ChordDegree.I], 1);
            return;
        }

        if (emotion == EmotionType.Calm)
        {
            AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.vi, ChordDegree.IV, ChordDegree.I, ChordDegree.ii, ChordDegree.IV, ChordDegree.iv, ChordDegree.I], 2);
        }
        else if (emotion == EmotionType.Bright)
        {
            AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.IV, ChordDegree.II, ChordDegree.V, ChordDegree.I, ChordDegree.V, ChordDegree.IV, ChordDegree.I], 2);
        }
        else if (emotion == EmotionType.Sad)
        {
            AddWeightedPattern(patterns, [ChordDegree.vi, ChordDegree.iii, ChordDegree.IV, ChordDegree.iv, ChordDegree.I, ChordDegree.III7, ChordDegree.vi, ChordDegree.vi], 3);
            AddWeightedPattern(patterns, [ChordDegree.vi, ChordDegree.bVI, ChordDegree.bVII, ChordDegree.I, ChordDegree.iv, ChordDegree.I, ChordDegree.V, ChordDegree.vi], 2);
        }
        else if (emotion == EmotionType.Tense)
        {
            AddWeightedPattern(patterns, [ChordDegree.ii, ChordDegree.V7, ChordDegree.viiDim, ChordDegree.vi, ChordDegree.bVI, ChordDegree.bVII, ChordDegree.V7, ChordDegree.I], 3);
            AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.III7, ChordDegree.vi, ChordDegree.VI7, ChordDegree.ii, ChordDegree.V7, ChordDegree.I, ChordDegree.V7], 2);
        }
        else if (emotion == EmotionType.Energetic)
        {
            AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.IV, ChordDegree.bVII, ChordDegree.IV, ChordDegree.V, ChordDegree.I], 2);
            AddWeightedPattern(patterns, [ChordDegree.I, ChordDegree.II, ChordDegree.V, ChordDegree.vi, ChordDegree.IV, ChordDegree.V, ChordDegree.I, ChordDegree.V], 1);
        }
    }

    private static ChordDegree GeneratePhraseEndingChord(MelodyStyle style, EmotionType emotion, int bar, int totalBars)
    {
        if (bar == totalBars - 1)
            return ChordDegree.I;

        if (style == MelodyStyle.Jazz)
            return ChordDegree.V7;

        if (emotion == EmotionType.Sad && (bar + 1) % 8 == 0)
            return ChordDegree.vi;

        return ChordDegree.V;
    }

    private static string RepairCadence(string chordText)
    {
        List<string> chords = SplitChordText(chordText).ToList();
        if (chords.Count == 0)
            return chordText;

        if (chords.Count >= 2)
            chords[^2] = GetChordCodeFromDegree(ChordDegree.V);

        chords[^1] = GetChordCodeFromDegree(ChordDegree.I);
        return string.Concat(chords);
    }

    private static string GetChordCodeFromDegree(ChordDegree degree)
    {
        return degree switch
        {
            ChordDegree.I => "XC501",
            ChordDegree.ii => "XD501",
            ChordDegree.iii => "XE501",
            ChordDegree.IV => "XF501",
            ChordDegree.V => "XG501",
            ChordDegree.V7 => "XG701",
            ChordDegree.vi => "XA501",
            ChordDegree.viiDim => "XB501",
            ChordDegree.bIII => "XH501",
            ChordDegree.bVI => "XL501",
            ChordDegree.bVII => "XK501",
            ChordDegree.II => "X2501",
            ChordDegree.III7 => "X3701",
            ChordDegree.VI7 => "X6701",
            ChordDegree.iv => "Xm501",
            _ => "XC501"
        };
    }

    private static IEnumerable<string> SplitChordText(string chordText)
    {
        for (int i = 0; i + 5 <= chordText.Length; i += 5)
            yield return chordText.Substring(i, 5);
    }

    private static string GetChordCodeAtBar(string chordText, int bar)
    {
        if (string.IsNullOrWhiteSpace(chordText))
            return "XC501";

        int chordCount = chordText.Length / 5;
        if (chordCount <= 0)
            return "XC501";

        int index = Math.Clamp(bar, 0, chordCount - 1);
        return chordText.Substring(index * 5, 5);
    }

    private static bool ValidateChordProgression(string chordText)
    {
        List<string> chords = SplitChordText(chordText).ToList();
        if (chords.Count == 0)
            return false;

        if (chords.Any(c => c.Length != 5 || c[0] != 'X'))
            return false;

        int homeLike = chords.Count(c => GetChordInfo(c).Degree is ChordDegree.I or ChordDegree.vi);
        int dominantLike = chords.Count(c => GetChordInfo(c).Degree is ChordDegree.V or ChordDegree.V7);
        return homeLike > 0 && dominantLike > 0;
    }

    private CompleteSongCandidate BuildBestCompleteSongCandidate(
        MelodyStyle style,
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        string chordText,
        int barCount,
        IReadOnlyList<string> seedPitches,
        string? seedMode,
        string subDensity,
        JiangNanThemeSeed? jiangNanThemeSeed,
        LegacyMelodyDraft? legacyMelodyDraft)
    {
        int attemptCount = GetOriginalFidelityCandidateAttemptCount(style, barCount);
        CompleteSongCandidate? best = null;
        string requestedDensity = string.IsNullOrWhiteSpace(subDensity) ? "Medium" : subDensity.Trim();

        if (legacyMelodyDraft is not null && legacyMelodyDraft.MainBars.Count == barCount)
        {
            string draftMainYNote = string.Concat(legacyMelodyDraft.MainBars);

            // Step 14：候選階段先用單一 density 快速評估，避免每個候選都跑 Low/Medium/High refinement。
            // 最終只會對最佳候選重新做完整 harmony refinement，因此音樂品質保留，但速度明顯改善。
            string draftSubYNote = GenerateSubMelodyForMainAndChordText(
                style,
                emotion,
                requestedDensity,
                chordText,
                keyRoot,
                minorKey,
                barCount,
                draftMainYNote);

            int draftScore = ScoreCompleteSongCandidateOriginalFidelity(
                draftMainYNote,
                draftSubYNote,
                chordText,
                style,
                emotion,
                barCount,
                keyRoot,
                minorKey,
                out string draftBreakdown);

            // Melody-first 草稿是原 WinForms 江南流程的核心。只要品質沒有明顯失敗，就優先保留它。
            draftScore += style == MelodyStyle.JiangNan ? 140 : 60;
            best = new CompleteSongCandidate(
                legacyMelodyDraft.MainBars,
                draftMainYNote,
                draftSubYNote,
                draftScore,
                draftBreakdown + Environment.NewLine + "Step 10 Melody-first XML draft：此候選來自原 WinForms 的 XML 草稿 + 反推和弦流程，已加權優先。 ");
        }

        int earlyStopScore = GetOriginalFidelityEarlyStopScore(style, barCount);
        int noImproveCount = 0;

        for (int attempt = 0; attempt < attemptCount; attempt++)
        {
            List<string> mainBars = GenerateMainMelodyBarsOnce(
                style,
                emotion,
                mode,
                keyRoot,
                minorKey,
                chordText,
                barCount,
                seedPitches,
                seedMode,
                jiangNanThemeSeed);

            string mainYNote = string.Concat(mainBars);

            // Step 14：候選排序階段不要對每組候選都試 Low / Medium / High。
            // 這是 Step 13 變慢的主要來源之一；先用使用者指定密度粗評，最後只 refine 最佳候選。
            string subYNote = GenerateSubMelodyForMainAndChordText(
                style,
                emotion,
                requestedDensity,
                chordText,
                keyRoot,
                minorKey,
                barCount,
                mainYNote);

            int score = ScoreCompleteSongCandidateOriginalFidelity(
                mainYNote,
                subYNote,
                chordText,
                style,
                emotion,
                barCount,
                keyRoot,
                minorKey,
                out string breakdown);

            CompleteSongCandidate candidate = new(mainBars, mainYNote, subYNote, score, breakdown);

            if (best is null || candidate.Score > best.Score)
            {
                best = candidate;
                noImproveCount = 0;
            }
            else
            {
                noImproveCount++;
            }

            // Step 14：如果已經找到高品質候選，提早停止。這通常不會明顯降低結果，
            // 但會避免 1 分鐘以上歌曲一直跑滿所有候選。
            if (best is not null && best.Score >= earlyStopScore && noImproveCount >= 4)
                break;
        }

        if (best is null)
            return new CompleteSongCandidate([], string.Empty, string.Empty, 0, "Step 9 原版候選排序：未產生候選。");

        // Step 14：只對最佳主旋律做一次完整副旋律 harmony refinement。
        string refinedSubYNote = GenerateSubMelodyForChordTextWithHarmonyRefinement(
            style,
            emotion,
            subDensity,
            chordText,
            keyRoot,
            minorKey,
            barCount,
            best.MainYNote);

        int finalScore = ScoreCompleteSongCandidateOriginalFidelity(
            best.MainYNote,
            refinedSubYNote,
            chordText,
            style,
            emotion,
            barCount,
            keyRoot,
            minorKey,
            out string finalBreakdown);

        string speedNote = Environment.NewLine +
            "Step 14 效能最佳化：已啟用。候選階段使用快速副旋律粗評，最後才對最佳候選做完整 harmony refinement；" +
            $"本次候選上限={attemptCount}，早停門檻={earlyStopScore}。";

        return new CompleteSongCandidate(
            best.MainBars,
            best.MainYNote,
            refinedSubYNote,
            finalScore,
            finalBreakdown + speedNote);
    }

    private static int GetOriginalFidelityEarlyStopScore(MelodyStyle style, int barCount)
    {
        int baseScore = style == MelodyStyle.JiangNan ? 760 : 700;
        if (barCount >= 32) baseScore -= 55;
        if (barCount >= 64) baseScore -= 70;
        return baseScore;
    }

    private List<string> GenerateMainMelodyBarsOnce(
        MelodyStyle style,
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        string chordText,
        int barCount,
        IReadOnlyList<string> seedPitches,
        string? seedMode,
        JiangNanThemeSeed? jiangNanThemeSeed)
    {
        List<TimeSectionInfo> sections = BuildTimeSections(barCount);
        List<string> mainBars = new(capacity: barCount);
        string? previousPitch = null;
        for (int bar = 0; bar < barCount; bar++)
        {
            ChordInfo chord = GetChordInfo(GetChordCodeAtBar(chordText, bar), keyRoot, minorKey);
            TimeSectionInfo section = GetSectionForBar(sections, bar);
            double progress = barCount <= 1 ? 1.0 : bar / (double)(barCount - 1);
            bool isPhraseEnding = (bar + 1) % 4 == 0 || bar == barCount - 1;
            bool isSongEnding = bar == barCount - 1;

            string mainBar = GenerateSongCandidateFast(
                style,
                emotion,
                mode,
                keyRoot,
                minorKey,
                chord,
                bar,
                barCount,
                section.Role,
                progress,
                isPhraseEnding,
                isSongEnding,
                previousPitch,
                seedPitches,
                seedMode,
                jiangNanThemeSeed);

            mainBar = PostProcessMainMelodyBar(mainBar, chord, keyRoot, minorKey, isSongEnding);
            previousPitch = GetLastPitch(mainBar) ?? previousPitch;
            mainBars.Add(mainBar);
        }

        // Step 11：原 WinForms 在每小節生成後仍會做跨小節後處理
        // （五聲合法化、前後小節平滑、樂句/全曲收束）。Web 版在這裡補上。
        return ApplyLegacyMainMelodyPostProcessing(
            mainBars,
            chordText,
            style,
            emotion,
            keyRoot,
            minorKey);
    }

    private static int GetOriginalFidelityCandidateAttemptCount(MelodyStyle style, int barCount)
    {
        // Step 14：Step 13 之後每個候選都會做 XML scoring + 後處理；候選數太高會讓 Web 請求明顯變慢。
        // 這裡保留多候選排序，但把候選數調整到較適合網站即時生成的範圍。
        int baseCount = style == MelodyStyle.JiangNan ? 18 : 12;
        if (barCount >= 32) baseCount -= 4;
        if (barCount >= 64) baseCount -= 4;
        return Math.Clamp(baseCount, 6, 20);
    }

    private static int ScoreCompleteSongCandidateOriginalFidelity(
        string mainYNote,
        string subYNote,
        string chordText,
        MelodyStyle style,
        EmotionType emotion,
        int barCount,
        int keyRoot,
        bool minorKey,
        out string breakdown)
    {
        int harmonyScore = CalculateHarmonyScore(mainYNote, subYNote, style, emotion, keyRoot);
        GetHarmonyScoreTargetRange(style, emotion, out int targetMin, out int targetMax, out int targetCenter, out int _, out int tolerance);
        int harmonyDistance = GetScoreRangeDistance(harmonyScore, targetMin, targetMax);
        double chordSupport = CalculateChordSupportRate(mainYNote, chordText, barCount, keyRoot, minorKey);
        int mainLargeLeaps = CountMainMelodyLargeLeaps(mainYNote);
        int subLargeLeaps = CountSubMelodyLargeLeaps(subYNote);
        int parallelPerfect = CountParallelPerfectIntervals(mainYNote, subYNote);
        int sharpCount = CountStyleColorNotesInYNote(mainYNote, style, keyRoot);
        double restRate = CalculateRestRate(mainYNote);
        int rhythmDiversity = CountRhythmKinds(mainYNote);
        int phraseEndingScore = ScorePhraseEndings(mainYNote, chordText, barCount, style, keyRoot, minorKey);
        int contourScore = ScoreMelodyContourOriginalStyle(mainYNote, style, emotion);
        int stylePenalty = GetStyleCharacterPenalty(mainYNote, subYNote, style, emotion, keyRoot);
        int jiangNanJourneyScore = style == MelodyStyle.JiangNan
            ? ScoreJiangNanFullSongJourney(chordText, mainYNote, barCount, keyRoot, minorKey)
            : 0;

        int score = 0;
        score += Math.Max(0, 220 - harmonyDistance * 18 - Math.Abs(harmonyScore - targetCenter) * 2);
        score += (int)Math.Round(chordSupport * 180.0);
        score += phraseEndingScore;
        score += contourScore;
        score += jiangNanJourneyScore;
        score += rhythmDiversity * 8;
        score -= mainLargeLeaps * (style == MelodyStyle.JiangNan ? 28 : 18);
        score -= subLargeLeaps * 14;
        score -= parallelPerfect * 35;
        score -= stylePenalty * 8;

        if (restRate > 0.42) score -= 80;
        else if (restRate > 0.30) score -= 35;
        else if (restRate >= 0.06 && restRate <= 0.22) score += 25;

        if (style == MelodyStyle.JiangNan)
        {
            score += ScoreJiangNanPentatonicPurity(mainYNote, keyRoot) * 4;
            if (sharpCount > 0) score -= sharpCount * 35;
        }
        else if (style == MelodyStyle.Jazz)
        {
            if (sharpCount == 0) score -= 20;
            else score += Math.Min(40, sharpCount * 5);
        }

        breakdown =
            "Step 9 原版音樂品質候選排序：已啟用。" + Environment.NewLine +
            $"候選總分：{score}；Harmony={harmonyScore}，目標={targetMin}-{targetMax}，距離={harmonyDistance}，容忍={tolerance}" + Environment.NewLine +
            $"Chord Support={chordSupport:P0}，PhraseEndingScore={phraseEndingScore}，ContourScore={contourScore}，JiangNanJourney={jiangNanJourneyScore}，RhythmKinds={rhythmDiversity}" + Environment.NewLine +
            $"MainLargeLeaps={mainLargeLeaps}，SubLargeLeaps={subLargeLeaps}，ParallelPerfect={parallelPerfect}，SharpCount={sharpCount}，RestRate={restRate:P0}" + Environment.NewLine +
            "此步驟優先移植會直接影響音樂結果的 WinForms 邏輯：完整候選排序、和弦轉接分數、風格/情緒 chord scoring、XML pattern 優先評分。Step 11 進一步加入江南 functional journey、tonic orbit closing 與跨小節旋律後處理。Step 13 再補 XML rhythm/pitch selection、chord-specific melody penalty 與單小節 post-processing。Step 15 加入 Style Isolation，避免非江南風格被預設 Pentatonic / 江南 XML pattern 拉回江南聽感。";

        return score;
    }

    private static double CalculateRestRate(string yNote)
    {
        List<(string Pitch, string Rhythm)> events = ParseYNoteEvents(yNote);
        if (events.Count == 0)
            return 1.0;
        return events.Count(e => e.Pitch == "00") / (double)events.Count;
    }

    private static int CountRhythmKinds(string yNote)
    {
        return ParseYNoteEvents(yNote).Select(e => e.Rhythm).Distinct(StringComparer.Ordinal).Count();
    }

    private static int ScorePhraseEndings(string mainYNote, string chordText, int barCount, MelodyStyle style, int keyRoot, bool minorKey)
    {
        List<string> bars = SplitYNoteIntoBars(mainYNote, barCount);
        int score = 0;

        for (int bar = 0; bar < Math.Min(barCount, bars.Count); bar++)
        {
            bool phraseEnding = (bar + 1) % 4 == 0 || bar == barCount - 1;
            if (!phraseEnding)
                continue;

            string? lastPitch = GetLastPitch(bars[bar]);
            if (string.IsNullOrWhiteSpace(lastPitch) || lastPitch == "00")
                continue;

            ChordInfo chord = GetChordInfo(GetChordCodeAtBar(chordText, bar), keyRoot, minorKey);
            if (IsChordSupportedPitch(lastPitch, chord)) score += bar == barCount - 1 ? 60 : 28;
            if (NormalizeSemitone(PitchToAbsoluteSemitone(lastPitch)) == chord.RootSemitone) score += bar == barCount - 1 ? 50 : 18;
            if (style == MelodyStyle.JiangNan && IsJiangNanCadencePitchClass(lastPitch, keyRoot)) score += 22;
        }

        return score;
    }

    private static bool IsJiangNanCadencePitchClass(string pitch, int keyRoot)
    {
        int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
        int degree = NormalizeSemitone(pc - keyRoot);
        return degree is 0 or 2 or 7;
    }

    private static int ScoreJiangNanPentatonicPurity(string mainYNote, int keyRoot)
    {
        HashSet<int> allowed = [0, 2, 4, 7, 9];
        int score = 0;
        foreach ((string pitch, _) in ParseYNoteEvents(mainYNote))
        {
            if (pitch == "00")
                continue;
            int degree = NormalizeSemitone(PitchToAbsoluteSemitone(pitch) - keyRoot);
            score += allowed.Contains(degree) ? 3 : -15;
        }
        return score;
    }

    private static int ScoreMelodyContourOriginalStyle(string mainYNote, MelodyStyle style, EmotionType emotion)
    {
        List<int> pitches = ParseYNoteEvents(mainYNote)
            .Where(e => e.Pitch != "00")
            .Select(e => PitchToAbsoluteSemitone(e.Pitch))
            .ToList();

        if (pitches.Count <= 2)
            return -40;

        int score = 0;
        int directionChanges = 0;
        int repeated = 0;
        int stepwise = 0;
        int previousSign = 0;

        for (int i = 1; i < pitches.Count; i++)
        {
            int diff = pitches[i] - pitches[i - 1];
            int abs = Math.Abs(diff);
            if (abs == 0) repeated++;
            if (abs <= 2) stepwise++;
            int sign = Math.Sign(diff);
            if (sign != 0 && previousSign != 0 && sign != previousSign)
                directionChanges++;
            if (sign != 0)
                previousSign = sign;
        }

        double stepRate = stepwise / Math.Max(1.0, pitches.Count - 1.0);
        if (style == MelodyStyle.JiangNan)
        {
            if (stepRate >= 0.55) score += 80;
            if (directionChanges >= 2) score += 35;
            if (repeated > pitches.Count / 3) score -= 35;
        }
        else if (style == MelodyStyle.Pop)
        {
            if (stepRate >= 0.35) score += 40;
            if (repeated >= 2 && repeated <= pitches.Count / 3) score += 15;
        }
        else if (style == MelodyStyle.JPop)
        {
            if (directionChanges >= 3) score += 35;
            if (pitches.Max() - pitches.Min() >= 10) score += 20;
        }
        else if (style == MelodyStyle.Jazz)
        {
            if (directionChanges >= 3) score += 25;
            if (stepRate < 0.75) score += 25;
        }

        if (emotion == EmotionType.Calm && stepRate >= 0.60) score += 25;
        if (emotion == EmotionType.Energetic && pitches.Max() - pitches.Min() >= 9) score += 25;
        if (emotion == EmotionType.Sad && pitches[^1] <= pitches[0]) score += 20;

        return score;
    }

    private LegacyMelodyDraft? TryBuildXmlMelodyDraftAndInferChords(
        MelodyStyle style,
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        int barCount,
        IReadOnlyList<string> seedPitches,
        string? seedMode,
        JiangNanThemeSeed? jiangNanThemeSeed)
    {
        if (barCount <= 0 || _repository.PitchPatterns.Count == 0 || _repository.RhythmPatterns.Count == 0)
            return null;

        List<TimeSectionInfo> sections = BuildTimeSections(barCount);
        List<string> draftBars = new(capacity: barCount);
        string previousLastPitch = "00";
        for (int barIndex = 0; barIndex < barCount; barIndex++)
        {
            TimeSectionInfo section = GetSectionForBar(sections, barIndex);
            bool isPhraseEnding = (barIndex + 1) % 4 == 0 || barIndex == barCount - 1;
            bool isSongEnding = barIndex == barCount - 1;
            ChordDegree provisionalDegree = GetXmlDraftProvisionalDegree(style, emotion, section.Role, barIndex, barCount);
            ChordInfo provisionalChord = GetChordInfo(GetChordCodeFromDegree(provisionalDegree), keyRoot, minorKey);
            string? barYNote = null;

            if (style == MelodyStyle.JiangNan && jiangNanThemeSeed is not null && barIndex < 4)
            {
                barYNote = BuildJiangNanThemedOpeningBar(
                    jiangNanThemeSeed,
                    provisionalChord,
                    keyRoot,
                    minorKey,
                    barIndex,
                    isPhraseEnding,
                    isSongEnding);
            }

            if (string.IsNullOrWhiteSpace(barYNote))
            {
                LegacyRhythmPattern? rhythm = PickRhythmPatternForXmlMelodyDraft(
                    style,
                    emotion,
                    section.Role,
                    barIndex,
                    barCount,
                    isPhraseEnding,
                    isSongEnding,
                    jiangNanThemeSeed);

                if (rhythm is null)
                    return null;

                string? pitchPattern = FindMelodyForXmlDraft(
                    rhythm,
                    style,
                    emotion,
                    mode,
                    keyRoot,
                    minorKey,
                    provisionalChord,
                    section.Role,
                    previousLastPitch,
                    barIndex,
                    barCount,
                    isPhraseEnding,
                    isSongEnding,
                    jiangNanThemeSeed);

                if (string.IsNullOrWhiteSpace(pitchPattern))
                    return null;

                pitchPattern = ApplyLegacyMelodyPostProcessingToPattern(
                    pitchPattern,
                    rhythm.Pattern,
                    provisionalChord,
                    style,
                    emotion,
                    keyRoot,
                    minorKey,
                    previousLastPitch,
                    section.Role,
                    isPhraseEnding,
                    isSongEnding);

                List<string> pitchTokens = TokenizePattern(pitchPattern, 2);
                List<string> rhythmTokens = TokenizePattern(rhythm.Pattern, 2);
                barYNote = ZipPitchAndRhythm(pitchTokens, rhythmTokens);
            }

            barYNote = NormalizeSingleBarYNote(ApplySeedMotifToBar(barYNote, seedPitches, barIndex, seedMode, isSongEnding));
            draftBars.Add(barYNote);

            string? last = GetLastPitch(barYNote);
            if (!string.IsNullOrWhiteSpace(last))
                previousLastPitch = last;
        }

        string inferredChordText = InferChordProgressionFromMelodyBars(draftBars, style, emotion, keyRoot, minorKey);
        if (string.IsNullOrWhiteSpace(inferredChordText) || SplitChordText(inferredChordText).Count() != draftBars.Count)
            return null;

        inferredChordText = RepairMelodyFirstChordProgression(inferredChordText, draftBars, style, emotion, keyRoot, minorKey);

        string report =
            "Step 10 Melody-first XML Draft：已啟用。" + Environment.NewLine +
            "對應原 WinForms 的 GenerateXmlMelodyDraftAndInferChords / xmlDraftMainMelodyBars / InferChordProgressionFromMelodyBars。" + Environment.NewLine +
            $"XML 草稿小節數：{draftBars.Count}；反推和弦小節數：{SplitChordText(inferredChordText).Count()}。" + Environment.NewLine +
            "這一步讓江南風格在未手動輸入和弦時，先由 PitchSet/RhythmSet 生成主旋律，再根據旋律決定和聲，音樂結果會更接近原 WinForms。" + Environment.NewLine +
            "Step 13：XML 草稿已套用 rhythm style score、chord-specific melody penalty 與單小節 post-processing。";

        return new LegacyMelodyDraft(draftBars, inferredChordText, report);
    }

    private LegacyRhythmPattern? PickRhythmPatternForXmlMelodyDraft(
        MelodyStyle style,
        EmotionType emotion,
        TimeSectionRole role,
        int barIndex,
        int totalBars,
        bool isPhraseEnding,
        bool isSongEnding,
        JiangNanThemeSeed? themeSeed)
    {
        List<(LegacyRhythmPattern Rhythm, int Weight)> weighted = new();

        foreach (LegacyRhythmPattern rhythm in _repository.RhythmPatterns)
        {
            if (rhythm.TotalTick != 0 && rhythm.TotalTick != TicksPerBar)
                continue;

            if (rhythm.Length <= 0 || rhythm.Pattern.Length < rhythm.Length * 2)
                continue;

            int weight = 20;

            if (style == MelodyStyle.JiangNan)
                weight += GetJiangNanXmlDraftRhythmWeight(rhythm.Pattern, rhythm.Length, role, barIndex, totalBars, isPhraseEnding, isSongEnding, themeSeed);
            else
                weight += GetGenericXmlDraftRhythmWeight(style, emotion, rhythm.Pattern, rhythm.Length, role, isPhraseEnding, isSongEnding);

            weight = Math.Clamp(weight, 1, 180);
            weighted.Add((rhythm, weight));
        }

        if (weighted.Count == 0)
            return null;

        int totalWeight = weighted.Sum(x => x.Weight);
        int roll = Random.Shared.Next(Math.Max(1, totalWeight));
        int cumulative = 0;

        foreach ((LegacyRhythmPattern rhythm, int weight) in weighted)
        {
            cumulative += weight;
            if (roll < cumulative)
                return rhythm;
        }

        return weighted[^1].Rhythm;
    }

    private static int GetJiangNanXmlDraftRhythmWeight(
        string rhythmPattern,
        int length,
        TimeSectionRole role,
        int barIndex,
        int totalBars,
        bool isPhraseEnding,
        bool isSongEnding,
        JiangNanThemeSeed? themeSeed)
    {
        int weight = 0;

        if (role == TimeSectionRole.Opening)
        {
            if (length >= 3 && length <= 6) weight += 34;
            if (length <= 2) weight -= 44;
            if (length > 7) weight -= 18;
            if (StartsWithLongRhythm(rhythmPattern)) weight += length <= 2 ? -20 : 8;
        }
        else if (role == TimeSectionRole.Development)
        {
            if (length >= 4 && length <= 6) weight += 24;
            if (length <= 2) weight -= 10;
            if (length > 8) weight -= 16;
        }
        else if (role == TimeSectionRole.Contrast)
        {
            if (length >= 5 && length <= 8) weight += 32;
            if (length <= 2) weight -= 16;
            if (rhythmPattern.Contains("08", StringComparison.Ordinal) || rhythmPattern.Contains("16", StringComparison.Ordinal)) weight += 8;
        }
        else if (role == TimeSectionRole.Closing)
        {
            if (length >= 2 && length <= 5) weight += 32;
            if (length > 7) weight -= 30;
            if (StartsWithLongRhythm(rhythmPattern)) weight += 10;
        }

        if (isPhraseEnding)
        {
            if (length <= 5) weight += 18;
            if (length > 7) weight -= 22;
        }

        if (isSongEnding)
        {
            if (length <= 4) weight += 36;
            if (length > 6) weight -= 50;
        }

        if (barIndex < 2)
        {
            if (length >= 3 && length <= 6) weight += 24;
            if (length <= 2) weight -= 52;
        }

        if (themeSeed is not null && role == TimeSectionRole.Opening && barIndex > 0 && barIndex < 4)
        {
            weight += GetRhythmPatternSimilarityScore(rhythmPattern, string.Concat(themeSeed.Rhythms)) / 2;
            weight -= Math.Abs(length - themeSeed.Rhythms.Count) * 5;
        }

        // rhythm-only 階段尚不知道哪些位置是休止；休止比例會在 pitch pattern 評分時處理。
        return weight;
    }

    private static int GetGenericXmlDraftRhythmWeight(
        MelodyStyle style,
        EmotionType emotion,
        string rhythmPattern,
        int length,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding)
    {
        int weight = 0;
        if (style == MelodyStyle.Jazz || emotion == EmotionType.Energetic)
        {
            if (length >= 5 && length <= 9) weight += 24;
            if (rhythmPattern.Contains("08", StringComparison.Ordinal) || rhythmPattern.Contains("16", StringComparison.Ordinal)) weight += 10;
        }
        else if (emotion == EmotionType.Calm || emotion == EmotionType.Sad)
        {
            if (length >= 3 && length <= 6) weight += 20;
            if (length > 8) weight -= 18;
        }
        else
        {
            if (length >= 4 && length <= 8) weight += 18;
        }

        if (role == TimeSectionRole.Contrast && length >= 5) weight += 8;
        if (isPhraseEnding && length <= 6) weight += 8;
        if (isSongEnding && length <= 5) weight += 16;
        return weight;
    }

    private string? FindMelodyForXmlDraft(
        LegacyRhythmPattern rhythm,
        MelodyStyle style,
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        ChordInfo provisionalChord,
        TimeSectionRole role,
        string previousLastPitch,
        int barIndex,
        int totalBars,
        bool isPhraseEnding,
        bool isSongEnding,
        JiangNanThemeSeed? themeSeed)
    {
        List<(string Pattern, int Score)> candidates = new();

        foreach (LegacyPitchPattern pitch in _repository.PitchPatterns.Where(p => p.Length == rhythm.Length))
        {
            List<string> transposed = TokenizePattern(pitch.Pattern, 2)
                .Select(p => TransposePitchFromC(p, keyRoot, minorKey, mode))
                .Select(p => style == MelodyStyle.JiangNan
                    ? ForcePitchToJiangNanScale(p, keyRoot, minOctave: 3, maxOctave: 5)
                    : ClampPitch(p, 3, style == MelodyStyle.JPop || emotion == EmotionType.Energetic ? 6 : 5))
                .ToList();

            string pattern = string.Concat(transposed);
            int score = GetXmlDraftMelodyWeight(
                pattern,
                rhythm.Pattern,
                style,
                emotion,
                provisionalChord,
                role,
                previousLastPitch,
                barIndex,
                totalBars,
                isPhraseEnding,
                isSongEnding,
                themeSeed);

            candidates.Add((pattern, score));
        }

        if (candidates.Count == 0)
            return null;

        string selected = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(_ => Random.Shared.Next())
            .First()
            .Pattern;

        selected = ApplyXmlDraftPhraseEnding(selected, provisionalChord, style, keyRoot, minorKey, isPhraseEnding, isSongEnding);
        return selected;
    }

    private static int GetXmlDraftMelodyWeight(
        string pitchPattern,
        string rhythmPattern,
        MelodyStyle style,
        EmotionType emotion,
        ChordInfo provisionalChord,
        TimeSectionRole role,
        string previousLastPitch,
        int barIndex,
        int totalBars,
        bool isPhraseEnding,
        bool isSongEnding,
        JiangNanThemeSeed? themeSeed)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2).Where(p => p != "00").ToList();
        if (pitches.Count == 0)
            return -1000;

        int score = 0;
        string firstPitch = pitches[0];
        string lastPitch = pitches[^1];
        int largeLeaps = CountLargeLeaps(pitches);
        string contour = BuildContourSignature(pitches);
        int restTicks = CountRestTicks(pitchPattern, rhythmPattern);
        double restRatio = restTicks / (double)Math.Max(1, TicksPerBar);

        if (pitches.Count >= 3 && pitches.Count <= 7) score += 38;
        if (barIndex < 4 && pitches.Count <= 2) score -= 80;
        if (barIndex < 4 && firstPitch == "00") score -= 40;

        if (!string.IsNullOrWhiteSpace(previousLastPitch) && previousLastPitch != "00")
        {
            int diff = Math.Abs(PitchToAbsoluteSemitone(firstPitch) - PitchToAbsoluteSemitone(previousLastPitch));
            if (diff <= 2) score += 32;
            else if (diff <= 5) score += 16;
            else if (diff >= 9) score -= 55;
        }

        if (IsChordSupportedPitch(firstPitch, provisionalChord)) score += 14;
        if (IsChordSupportedPitch(lastPitch, provisionalChord)) score += isPhraseEnding ? 34 : 16;
        score -= largeLeaps * (style == MelodyStyle.JiangNan ? 28 : 16);

        if (style == MelodyStyle.JiangNan)
        {
            score += ScoreJiangNanLegacyPatternShape(pitchPattern, provisionalChord) * 3;
            score -= CountSharpPitches(pitchPattern) * 45;
            if (contour.Contains("UD", StringComparison.Ordinal) || contour.Contains("DU", StringComparison.Ordinal)) score += 16;
            if (IsJiangNanCadencePitch(lastPitch)) score += isPhraseEnding ? 26 : 8;
            if (restRatio > 0.52 && barIndex < totalBars - 2) score -= 50;
            if (themeSeed is not null && role == TimeSectionRole.Opening && barIndex < 4)
                score += GetContourSimilarityScore(contour, themeSeed.ContourSignature) / 2;
        }
        else
        {
            if (contour.Contains("UD", StringComparison.Ordinal) || contour.Contains("DU", StringComparison.Ordinal)) score += 10;
        }

        if (role == TimeSectionRole.Opening)
        {
            if (pitches.Count <= 6) score += 12;
            if (PitchToAbsoluteSemitone(firstPitch) <= PitchToAbsoluteSemitone("G5")) score += 8;
        }
        else if (role == TimeSectionRole.Development)
        {
            if (pitches.Count >= 4 && pitches.Count <= 7) score += 14;
        }
        else if (role == TimeSectionRole.Contrast)
        {
            if (pitches.Count >= 5) score += 16;
            if (pitches.Max(PitchToAbsoluteSemitone) >= PitchToAbsoluteSemitone("A5")) score += 12;
        }
        else if (role == TimeSectionRole.Closing)
        {
            if (PitchToAbsoluteSemitone(lastPitch) <= PitchToAbsoluteSemitone("G5")) score += 14;
            if (largeLeaps == 0) score += 10;
        }

        if (isSongEnding)
        {
            if (IsChordSupportedPitch(lastPitch, provisionalChord)) score += 26;
            if (restRatio > 0.20) score += 8;
        }

        if (emotion == EmotionType.Calm && largeLeaps == 0) score += 16;
        if (emotion == EmotionType.Bright && contour.Count(c => c == 'U') >= 2) score += 10;
        if (emotion == EmotionType.Sad && contour.Count(c => c == 'D') >= 2) score += 10;
        if (emotion == EmotionType.Energetic && pitches.Count >= 6) score += 14;

        score += GetLegacyRhythmStyleScore(style, emotion, role, isPhraseEnding, isSongEnding, rhythmPattern, TokenizePattern(rhythmPattern, 2).Count, TicksPerBar) / 3;
        score += ScoreLegacyChordSpecificMelodyLine(pitches, style, emotion, provisionalChord, role, isPhraseEnding, isSongEnding);

        return score;
    }

    private static string ApplyXmlDraftPhraseEnding(
        string pitchPattern,
        ChordInfo provisionalChord,
        MelodyStyle style,
        int keyRoot,
        bool minorKey,
        bool isPhraseEnding,
        bool isSongEnding)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2);
        int lastIndex = pitches.FindLastIndex(p => p != "00");
        if (lastIndex < 0)
            return pitchPattern;

        if (isSongEnding)
        {
            pitches[lastIndex] = style == MelodyStyle.JiangNan
                ? ForcePitchToJiangNanScale(PitchFromSemitone(keyRoot, minorKey ? 4 : 5), keyRoot, minOctave: 3, maxOctave: 5)
                : PitchFromSemitone(provisionalChord.RootSemitone, minorKey ? 4 : 5);
        }
        else if (isPhraseEnding && !IsChordSupportedPitch(pitches[lastIndex], provisionalChord))
        {
            pitches[lastIndex] = MoveLastPitchTowardChordTone(pitches[lastIndex], provisionalChord, style, keyRoot);
        }

        return string.Concat(pitches);
    }

    private static string InferChordProgressionFromMelodyBars(
        IReadOnlyList<string> melodyBars,
        MelodyStyle style,
        EmotionType emotion,
        int keyRoot,
        bool minorKey)
    {
        if (melodyBars.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        ChordDegree previousDegree = ChordDegree.Unknown;
        int totalBars = melodyBars.Count;

        for (int bar = 0; bar < totalBars; bar++)
        {
            ChordDegree degree;
            if (bar == totalBars - 1)
                degree = emotion == EmotionType.Sad && style != MelodyStyle.JiangNan ? ChordDegree.vi : ChordDegree.I;
            else if (bar == totalBars - 2)
                degree = style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V;
            else
                degree = InferChordDegreeForMelodyBar(melodyBars[bar], previousDegree, bar, totalBars, style, emotion, keyRoot, minorKey);

            if (style == MelodyStyle.JiangNan)
                degree = SanitizeJiangNanChordDegreeStrict(degree);

            if (previousDegree != ChordDegree.Unknown)
                degree = EnsureTransitionOrFallback(previousDegree, degree, style, emotion, GetSectionForBar(BuildTimeSections(totalBars), bar).Role);

            sb.Append(GetChordCodeFromDegree(degree));
            previousDegree = degree;
        }

        return sb.ToString();
    }

    private static ChordDegree InferChordDegreeForMelodyBar(
        string barYNote,
        ChordDegree previousDegree,
        int barIndex,
        int totalBars,
        MelodyStyle style,
        EmotionType emotion,
        int keyRoot,
        bool minorKey)
    {
        ChordDegree bestDegree = ChordDegree.I;
        int bestScore = int.MinValue;

        foreach (ChordDegree candidate in GetMelodyFirstChordCandidates(style))
        {
            if (previousDegree != ChordDegree.Unknown && !IsMelodyFirstTransitionAllowed(previousDegree, candidate, style))
                continue;

            int score = ScoreChordDegreeAgainstMelodyBar(barYNote, candidate, previousDegree, barIndex, totalBars, style, emotion, keyRoot, minorKey);
            score += GetMelodyFirstTransitionPreference(previousDegree, candidate, barIndex, totalBars, style);
            score += GetJiangNanJourneyTemplateScore(candidate, previousDegree, barIndex, totalBars, style);

            if (score > bestScore)
            {
                bestScore = score;
                bestDegree = candidate;
            }
        }

        return bestScore == int.MinValue ? GetMelodyFirstFallbackChordDegree(previousDegree, style) : bestDegree;
    }

    private static IReadOnlyList<ChordDegree> GetMelodyFirstChordCandidates(MelodyStyle style)
    {
        if (style == MelodyStyle.JiangNan)
            return [ChordDegree.I, ChordDegree.vi, ChordDegree.iii, ChordDegree.IV, ChordDegree.ii, ChordDegree.V, ChordDegree.V7];

        return [ChordDegree.I, ChordDegree.V, ChordDegree.vi, ChordDegree.iii, ChordDegree.IV, ChordDegree.ii, ChordDegree.V7];
    }

    private static int ScoreChordDegreeAgainstMelodyBar(
        string barYNote,
        ChordDegree degree,
        ChordDegree previousDegree,
        int barIndex,
        int totalBars,
        MelodyStyle style,
        EmotionType emotion,
        int keyRoot,
        bool minorKey)
    {
        List<(string Pitch, string Rhythm)> events = ParseYNoteEvents(barYNote);
        if (events.Count == 0)
            return -9999;

        HashSet<int> chordTones = GetChordTonePitchClassesForDegree(degree, keyRoot, minorKey);
        int rootPc = NormalizeSemitone(GetDegreeRootSemitone(degree, keyRoot, minorKey));
        int thirdPc = GetDegreeThirdPitchClass(degree, keyRoot, minorKey);
        int fifthPc = NormalizeSemitone(rootPc + (degree == ChordDegree.viiDim ? 6 : 7));
        int score = 0;
        int nonRestCount = 0;
        int chordToneHits = 0;
        int tick = 0;
        int noteIndex = 0;
        int lastNonRestPc = -1;

        foreach ((string pitch, string rhythm) in events)
        {
            int duration = Math.Max(0, GetDuration(rhythm));
            if (pitch != "00")
            {
                int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
                bool strong = tick == 0 || tick == TicksPerBar / 2 || noteIndex == 0 || noteIndex == events.Count - 1;
                nonRestCount++;

                if (chordTones.Contains(pc))
                {
                    chordToneHits++;
                    score += strong ? 34 : 16;
                }
                else
                {
                    score += strong ? -22 : -8;
                }

                if (pc == rootPc) score += strong ? 18 : 6;
                if (pc == thirdPc) score += strong ? 13 : 5;
                if (pc == fifthPc) score += strong ? 10 : 4;

                if (style == MelodyStyle.JiangNan)
                {
                    int degreePc = NormalizeSemitone(pc - keyRoot);
                    if (degreePc is 0 or 2 or 4 or 7 or 9) score += 6;
                    else score -= 24;
                }

                lastNonRestPc = pc;
            }

            tick += duration <= 0 ? TicksPerBeat : duration;
            noteIndex++;
        }

        if (nonRestCount == 0)
            return -300;

        score += (int)Math.Round(100.0 * chordToneHits / nonRestCount);

        bool phraseEnding = (barIndex + 1) % 4 == 0 || barIndex == totalBars - 1;
        if (phraseEnding && lastNonRestPc >= 0)
        {
            if (lastNonRestPc == rootPc) score += 40;
            else if (chordTones.Contains(lastNonRestPc)) score += 22;
            else score -= 20;
        }

        if (barIndex == 0 && degree == ChordDegree.I) score += 35;
        if (barIndex == totalBars - 2 && degree is ChordDegree.V or ChordDegree.V7) score += 65;
        if (barIndex == totalBars - 1 && degree == ChordDegree.I) score += 85;

        score += GetStyleChordBonus(style, degree);
        score += GetEmotionChordBonus(emotion, degree);
        if (previousDegree != ChordDegree.Unknown)
            score += GetChordTransitionScore(previousDegree, degree) / 2;

        if (style == MelodyStyle.JiangNan)
        {
            score += GetJiangNanFunctionalJourneyScore(previousDegree, degree, barIndex, totalBars);
            score += GetJiangNanJourneyTemplateScore(degree, previousDegree, barIndex, totalBars, style);
            score += GetJiangNanTonicOrbitClosingScore(degree, previousDegree, barIndex, totalBars);

            if (!IsJiangNanFunctionallyForwardMotion(previousDegree, degree) && barIndex < totalBars - 2)
                score -= 18;
        }

        if (minorKey && degree == ChordDegree.vi) score += 10;
        return score;
    }

    private static HashSet<int> GetChordTonePitchClassesForDegree(ChordDegree degree, int keyRoot, bool minorKey)
    {
        int root = NormalizeSemitone(GetDegreeRootSemitone(degree, keyRoot, minorKey));
        int[] intervals = GetChordIntervals(degree, minorKey);
        return intervals.Select(i => NormalizeSemitone(root + i)).ToHashSet();
    }

    private static int GetDegreeRootSemitone(ChordDegree degree)
    {
        return degree switch
        {
            ChordDegree.I => 0,
            ChordDegree.ii => 2,
            ChordDegree.iii => 4,
            ChordDegree.IV => 5,
            ChordDegree.V or ChordDegree.V7 => 7,
            ChordDegree.vi => 9,
            ChordDegree.viiDim => 11,
            ChordDegree.bIII => 3,
            ChordDegree.bVI => 8,
            ChordDegree.bVII => 10,
            ChordDegree.II => 2,
            ChordDegree.III7 => 4,
            ChordDegree.VI7 => 9,
            ChordDegree.iv => 5,
            _ => 0
        };
    }

    private static int GetDegreeRootSemitone(ChordDegree degree, int keyRoot, bool minorKey)
    {
        int root = NormalizeSemitone(keyRoot);

        if (!minorKey)
            return NormalizeSemitone(root + GetDegreeRootSemitone(degree));

        return NormalizeSemitone(degree switch
        {
            ChordDegree.I => root,
            ChordDegree.ii => root + 2,
            ChordDegree.iii or ChordDegree.bIII => root + 3,
            ChordDegree.IV or ChordDegree.iv => root + 5,
            ChordDegree.V or ChordDegree.V7 => root + 7,
            ChordDegree.vi or ChordDegree.bVI => root + 8,
            ChordDegree.viiDim => root + 11,
            ChordDegree.bVII => root + 10,
            ChordDegree.II => root + 2,
            ChordDegree.III7 => root + 3,
            ChordDegree.VI7 => root + 8,
            _ => root
        });
    }

    private static int[] GetChordIntervals(ChordDegree degree, bool minorKey)
    {
        if (degree == ChordDegree.viiDim)
            return [0, 3, 6];

        if (degree is ChordDegree.V7 or ChordDegree.III7 or ChordDegree.VI7 or ChordDegree.II)
            return [0, 4, 7, 10];

        return IsMinorTriadDegree(degree, minorKey) ? [0, 3, 7] : [0, 4, 7];
    }

    private static bool IsMinorTriadDegree(ChordDegree degree, bool minorKey)
    {
        if (!minorKey)
            return degree is ChordDegree.ii or ChordDegree.iii or ChordDegree.vi or ChordDegree.iv;

        return degree is ChordDegree.I or ChordDegree.IV or ChordDegree.iv;
    }

    private static int GetDegreeThirdPitchClass(ChordDegree degree, int keyRoot, bool minorKey)
    {
        int root = NormalizeSemitone(GetDegreeRootSemitone(degree, keyRoot, minorKey));
        int third = IsMinorTriadDegree(degree, minorKey) || degree == ChordDegree.viiDim ? 3 : 4;
        return NormalizeSemitone(root + third);
    }

    private static bool IsMelodyFirstTransitionAllowed(ChordDegree previousDegree, ChordDegree candidateDegree, MelodyStyle style)
    {
        if (previousDegree == ChordDegree.Unknown)
            return true;

        if (style == MelodyStyle.JiangNan)
        {
            if (!IsJiangNanAllowedDegreeForProgression(previousDegree) || !IsJiangNanAllowedDegreeForProgression(candidateDegree))
                return false;
        }

        return IsValidChordTransition(previousDegree, candidateDegree, style) || GetChordTransitionScore(previousDegree, candidateDegree) >= 50;
    }

    private static int GetMelodyFirstTransitionPreference(ChordDegree previousDegree, ChordDegree candidateDegree, int barIndex, int totalBars, MelodyStyle style)
    {
        if (previousDegree == ChordDegree.Unknown)
            return candidateDegree == ChordDegree.I ? 28 : 0;

        int score = GetChordTransitionScore(previousDegree, candidateDegree) / 2;
        bool phraseEnding = (barIndex + 1) % 4 == 0 || barIndex == totalBars - 1;

        if (phraseEnding && candidateDegree is ChordDegree.V or ChordDegree.V7 or ChordDegree.I) score += 24;
        if (style == MelodyStyle.JiangNan && previousDegree == ChordDegree.I && candidateDegree is ChordDegree.vi or ChordDegree.iii) score += 16;
        if (style == MelodyStyle.JiangNan && (previousDegree is ChordDegree.IV or ChordDegree.ii) && (candidateDegree is ChordDegree.V or ChordDegree.V7)) score += 18;
        if (style == MelodyStyle.JiangNan && (previousDegree is ChordDegree.V or ChordDegree.V7) && candidateDegree == ChordDegree.I) score += 24;

        return score;
    }

    private static ChordDegree GetMelodyFirstFallbackChordDegree(ChordDegree previousDegree, MelodyStyle style)
    {
        if (previousDegree == ChordDegree.Unknown)
            return ChordDegree.I;

        return previousDegree switch
        {
            ChordDegree.I => style == MelodyStyle.JiangNan ? ChordDegree.vi : ChordDegree.V,
            ChordDegree.vi => ChordDegree.IV,
            ChordDegree.iii => ChordDegree.IV,
            ChordDegree.IV => ChordDegree.V,
            ChordDegree.ii => style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V,
            ChordDegree.V or ChordDegree.V7 => ChordDegree.I,
            _ => ChordDegree.I
        };
    }

    private static ChordDegree GetXmlDraftProvisionalDegree(MelodyStyle style, EmotionType emotion, TimeSectionRole role, int barIndex, int totalBars)
    {
        if (barIndex == totalBars - 1)
            return emotion == EmotionType.Sad && style != MelodyStyle.JiangNan ? ChordDegree.vi : ChordDegree.I;
        if (barIndex == totalBars - 2)
            return style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V;

        return role switch
        {
            TimeSectionRole.Opening => barIndex % 2 == 0 ? ChordDegree.I : ChordDegree.vi,
            TimeSectionRole.Development => barIndex % 2 == 0 ? ChordDegree.IV : ChordDegree.ii,
            TimeSectionRole.Contrast => style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V,
            TimeSectionRole.Closing => barIndex % 2 == 0 ? ChordDegree.IV : ChordDegree.V,
            _ => ChordDegree.I
        };
    }


    private static JiangNanHarmonyFunction GetJiangNanHarmonyFunction(ChordDegree degree)
    {
        if (degree is ChordDegree.ii or ChordDegree.IV)
            return JiangNanHarmonyFunction.Bridge;

        if (degree is ChordDegree.V or ChordDegree.V7 or ChordDegree.viiDim)
            return JiangNanHarmonyFunction.Outside;

        return JiangNanHarmonyFunction.Home;
    }

    private static bool IsJiangNanTemporaryHomeDegree(ChordDegree degree)
    {
        return degree is ChordDegree.iii or ChordDegree.vi;
    }

    private static bool IsJiangNanFunctionallyForwardMotion(ChordDegree previousDegree, ChordDegree candidateDegree)
    {
        if (previousDegree == ChordDegree.Unknown || candidateDegree == ChordDegree.Unknown)
            return true;

        JiangNanHarmonyFunction prevFunc = GetJiangNanHarmonyFunction(previousDegree);
        JiangNanHarmonyFunction candFunc = GetJiangNanHarmonyFunction(candidateDegree);

        if (prevFunc == JiangNanHarmonyFunction.Home && candFunc == JiangNanHarmonyFunction.Bridge)
            return true;
        if (prevFunc == JiangNanHarmonyFunction.Bridge && candFunc == JiangNanHarmonyFunction.Outside)
            return true;
        if (prevFunc == JiangNanHarmonyFunction.Outside && candFunc == JiangNanHarmonyFunction.Home)
            return true;

        return prevFunc == JiangNanHarmonyFunction.Home && candFunc == JiangNanHarmonyFunction.Home &&
               (previousDegree == ChordDegree.I || IsJiangNanTemporaryHomeDegree(previousDegree) || IsJiangNanTemporaryHomeDegree(candidateDegree));
    }

    private static int GetJiangNanJourneyPhase16(int barIndex, int totalBars)
    {
        if (totalBars <= 1)
            return 15;
        if (barIndex <= 0)
            return 0;
        if (barIndex >= totalBars - 1)
            return 15;

        int phase = (int)Math.Floor((barIndex * 16.0) / totalBars);
        return Math.Clamp(phase, 0, 15);
    }

    private static bool IsJiangNanStrongReturnHomePoint(int phase, int barIndex, int totalBars)
    {
        return barIndex >= totalBars - 1 || phase == 12;
    }

    private static bool IsNearSongEndingBar(int barIndex, int totalBars)
    {
        return barIndex >= Math.Max(0, totalBars - 2);
    }

    private static bool IsJiangNanClosingOrbitZone(int barIndex, int totalBars)
    {
        if (totalBars <= 0)
            totalBars = 16;

        int closingBars = Math.Max(4, (int)Math.Ceiling(totalBars * 0.25));
        return barIndex >= Math.Max(0, totalBars - closingBars);
    }

    private static int GetJiangNanFunctionalJourneyScore(ChordDegree previousDegree, ChordDegree candidateDegree, int barIndex, int totalBars)
    {
        if (candidateDegree == ChordDegree.Unknown)
            return -100;

        if (previousDegree == ChordDegree.Unknown)
        {
            if (candidateDegree == ChordDegree.I) return 18;
            if (IsJiangNanTemporaryHomeDegree(candidateDegree)) return 4;
            return -6;
        }

        JiangNanHarmonyFunction prevFunc = GetJiangNanHarmonyFunction(previousDegree);
        JiangNanHarmonyFunction candFunc = GetJiangNanHarmonyFunction(candidateDegree);
        bool nearSongEnding = IsNearSongEndingBar(barIndex, totalBars);
        int score = 0;

        if (prevFunc == JiangNanHarmonyFunction.Home && candFunc == JiangNanHarmonyFunction.Bridge)
            score += 16;
        else if (prevFunc == JiangNanHarmonyFunction.Bridge && candFunc == JiangNanHarmonyFunction.Outside)
            score += 18;
        else if (prevFunc == JiangNanHarmonyFunction.Outside && candFunc == JiangNanHarmonyFunction.Home)
            score += 20;
        else if (prevFunc == JiangNanHarmonyFunction.Bridge && candFunc == JiangNanHarmonyFunction.Home)
            score += 8;
        else if (prevFunc == JiangNanHarmonyFunction.Home && candFunc == JiangNanHarmonyFunction.Home)
            score += 5;
        else if (prevFunc == JiangNanHarmonyFunction.Outside && candFunc == JiangNanHarmonyFunction.Bridge)
            score -= 12;

        if (IsJiangNanTemporaryHomeDegree(candidateDegree))
        {
            score += 8;
            if (previousDegree == ChordDegree.I || IsJiangNanTemporaryHomeDegree(previousDegree))
                score += 6;
            if (prevFunc is JiangNanHarmonyFunction.Outside or JiangNanHarmonyFunction.Bridge)
                score += 6;
            if (nearSongEnding)
                score -= 30;
        }

        if (candidateDegree == ChordDegree.I)
        {
            if (nearSongEnding)
                score += 30;
            else if (previousDegree == ChordDegree.I)
                score -= 8;

            if ((previousDegree is ChordDegree.V or ChordDegree.V7) && !nearSongEnding)
                score -= 24;
        }

        if (candidateDegree == ChordDegree.V7)
        {
            if (barIndex == totalBars - 2 || GetJiangNanJourneyPhase16(barIndex, totalBars) >= 10)
                score += 8;
            else
                score -= 8;
        }

        return score;
    }

    private static int GetJiangNanTonicOrbitClosingScore(ChordDegree degree, ChordDegree previousDegree, int barIndex, int totalBars)
    {
        if (!IsJiangNanClosingOrbitZone(barIndex, totalBars))
            return 0;

        int barsLeft = totalBars - barIndex - 1;
        bool trueHome = degree == ChordDegree.I;
        bool tempHome = IsJiangNanTemporaryHomeDegree(degree);
        JiangNanHarmonyFunction function = GetJiangNanHarmonyFunction(degree);
        bool bridge = function == JiangNanHarmonyFunction.Bridge;
        bool outside = function == JiangNanHarmonyFunction.Outside;
        int score = 0;

        if (barsLeft == 0)
        {
            if (trueHome) score += 180;
            if (tempHome) score -= 160;
            if (outside) score -= 120;
            if (bridge) score -= 60;
            return score;
        }

        if (barsLeft == 1)
        {
            if (trueHome) score += 70;
            if (tempHome) score += 34;
            if (bridge) score += 22;
            if (degree == ChordDegree.V) score -= 12;
            if (degree == ChordDegree.V7) score -= 36;
        }
        else if (barsLeft <= 3)
        {
            if (trueHome) score += 36;
            if (tempHome) score += 34;
            if (degree == ChordDegree.IV) score += 24;
            if (degree == ChordDegree.ii) score += 8;
            if (degree == ChordDegree.V) score += 4;
            if (degree == ChordDegree.V7) score -= 20;
        }
        else
        {
            if (trueHome) score += 24;
            if (tempHome) score += 22;
            if (degree == ChordDegree.IV) score += 16;
            if (degree == ChordDegree.ii) score += 4;
            if (outside) score -= 8;
        }

        if (previousDegree == ChordDegree.I && tempHome)
            score += 12;
        if (IsJiangNanTemporaryHomeDegree(previousDegree) && trueHome)
            score += 12;
        if (IsJiangNanTemporaryHomeDegree(previousDegree) && tempHome && previousDegree != degree)
            score += 8;
        if ((previousDegree is ChordDegree.V or ChordDegree.V7) && trueHome && barsLeft > 1)
            score -= 30;

        return score;
    }

    private static int ScoreJiangNanFullSongJourney(string chordText, string mainYNote, int barCount, int keyRoot, bool minorKey)
    {
        List<string> chords = SplitChordText(chordText).Take(barCount).ToList();
        if (chords.Count == 0)
            return 0;

        List<string> bars = SplitYNoteIntoBars(mainYNote, barCount);
        int score = 0;
        ChordDegree previous = ChordDegree.Unknown;

        for (int bar = 0; bar < chords.Count; bar++)
        {
            ChordDegree degree = GetChordInfo(chords[bar]).Degree;
            score += GetJiangNanJourneyTemplateScore(degree, previous, bar, barCount, MelodyStyle.JiangNan);
            score += GetJiangNanFunctionalJourneyScore(previous, degree, bar, barCount);
            score += GetJiangNanTonicOrbitClosingScore(degree, previous, bar, barCount);

            if (!IsJiangNanFunctionallyForwardMotion(previous, degree) && bar < barCount - 2)
                score -= 18;

            if (bar < bars.Count)
            {
                string? lastPitch = GetLastPitch(bars[bar]);
                if (!string.IsNullOrWhiteSpace(lastPitch) && lastPitch != "00")
                {
                    if ((bar + 1) % 4 == 0 || bar == barCount - 1)
                    {
                        int degreePc = NormalizeSemitone(PitchToAbsoluteSemitone(lastPitch) - keyRoot);
                        if (degreePc is 0 or 2 or 7) score += 26;
                        if (bar == barCount - 1 && degreePc == 0) score += 60;
                    }
                }
            }

            previous = degree;
        }

        return score;
    }

    private static int GetJiangNanJourneyTemplateScore(ChordDegree candidate, ChordDegree previousDegree, int barIndex, int totalBars, MelodyStyle style)
    {
        if (style != MelodyStyle.JiangNan || candidate == ChordDegree.Unknown)
            return 0;

        int phase = GetJiangNanJourneyPhase16(barIndex, totalBars);
        JiangNanHarmonyFunction function = GetJiangNanHarmonyFunction(candidate);
        bool tempHome = IsJiangNanTemporaryHomeDegree(candidate);
        bool bridge = function == JiangNanHarmonyFunction.Bridge;
        bool outside = function == JiangNanHarmonyFunction.Outside;
        bool trueHome = candidate == ChordDegree.I;
        int score = 0;

        // 對應原 WinForms 的 16-phase 江南旅程：
        // 家 → 暫時的家 → 橋 → 外面 → 暫時休息 → 橋 → 外面 → 真正回家。
        switch (phase)
        {
            case 0:
                if (trueHome) score += 42;
                if (tempHome) score += 4;
                if (bridge) score -= 4;
                if (outside) score -= 28;
                break;
            case 1:
                if (trueHome) score += 18;
                if (tempHome) score += 24;
                if (bridge) score += 4;
                if (outside) score -= 18;
                break;
            case 2:
                if (tempHome) score += 28;
                if (trueHome) score += 8;
                if (bridge) score += 8;
                if (outside) score -= 8;
                break;
            case 3:
                if (bridge) score += 28;
                if (tempHome) score += 14;
                if (outside) score += 8;
                if (trueHome) score -= 10;
                break;
            case 4:
                if (tempHome) score += 30;
                if (bridge) score += 10;
                if (trueHome) score += 6;
                if (outside) score -= 10;
                break;
            case 5:
                if (bridge) score += 28;
                if (tempHome) score += 8;
                if (outside) score += 4;
                if (trueHome) score += 2;
                break;
            case 6:
                if (outside) score += 24;
                if (bridge) score += 12;
                if (tempHome) score += 12;
                if (trueHome) score -= 8;
                break;
            case 7:
                if (outside) score += 22;
                if (bridge) score += 18;
                if (tempHome) score += 12;
                if (trueHome) score -= 22;
                break;
            case 8:
                if (bridge) score += 32;
                if (tempHome) score += 10;
                if (outside) score += 8;
                if (trueHome) score -= 16;
                break;
            case 9:
                if (outside) score += 34;
                if (bridge) score += 8;
                if (tempHome) score += 4;
                if (trueHome) score -= 20;
                break;
            case 10:
                if (tempHome) score += 28;
                if (bridge) score += 8;
                if (trueHome) score += 4;
                if (outside) score -= 8;
                break;
            case 11:
                if (bridge) score += 26;
                if (outside) score += 20;
                if (tempHome) score += 4;
                if (trueHome) score -= 22;
                break;
            case 12:
                if (trueHome) score += 28;
                if (tempHome) score += 18;
                if (bridge) score += 8;
                if (outside) score -= 8;
                break;
            case 13:
                if (bridge) score += 30;
                if (tempHome) score += 6;
                if (outside) score += 8;
                if (trueHome) score += 4;
                break;
            case 14:
                if (outside) score += 42;
                if (bridge) score += 12;
                if (trueHome) score -= 18;
                if (tempHome) score -= 14;
                break;
            default:
                if (trueHome) score += 120;
                if (outside) score -= 28;
                if (bridge) score -= 16;
                if (tempHome) score -= 90;
                break;
        }

        if (previousDegree != ChordDegree.Unknown)
        {
            JiangNanHarmonyFunction prevFunc = GetJiangNanHarmonyFunction(previousDegree);

            if (prevFunc == JiangNanHarmonyFunction.Outside && trueHome)
                score += IsJiangNanStrongReturnHomePoint(phase, barIndex, totalBars) ? 24 : -30;

            if (previousDegree == ChordDegree.I && trueHome && phase > 1 && phase < 15)
                score -= 18;

            if (IsJiangNanTemporaryHomeDegree(previousDegree) && tempHome && previousDegree != candidate)
                score += 10;

            if (IsJiangNanTemporaryHomeDegree(previousDegree) && bridge)
                score += 12;

            if (prevFunc == JiangNanHarmonyFunction.Bridge && outside)
                score += 10;
        }

        return score;
    }

    private static string RepairMelodyFirstChordProgression(
        string chordText,
        IReadOnlyList<string> melodyBars,
        MelodyStyle style,
        EmotionType emotion,
        int keyRoot,
        bool minorKey)
    {
        List<ChordDegree> degrees = SplitChordText(chordText).Select(c => GetChordInfo(c).Degree).ToList();
        if (degrees.Count == 0)
            return chordText;

        for (int i = 0; i < degrees.Count; i++)
        {
            if (style == MelodyStyle.JiangNan)
                degrees[i] = SanitizeJiangNanChordDegreeStrict(degrees[i]);

            if (i > 0 && !IsValidChordTransition(degrees[i - 1], degrees[i], style))
                degrees[i] = EnsureTransitionOrFallback(degrees[i - 1], degrees[i], style, emotion, GetSectionForBar(BuildTimeSections(degrees.Count), i).Role);
        }

        if (degrees.Count >= 2)
            degrees[^2] = style == MelodyStyle.Jazz ? ChordDegree.V7 : ChordDegree.V;
        degrees[^1] = emotion == EmotionType.Sad && style != MelodyStyle.JiangNan ? ChordDegree.vi : ChordDegree.I;

        for (int i = 1; i < degrees.Count; i++)
        {
            if (!IsValidChordTransition(degrees[i - 1], degrees[i], style))
                degrees[i] = EnsureTransitionOrFallback(degrees[i - 1], degrees[i], style, emotion, GetSectionForBar(BuildTimeSections(degrees.Count), i).Role);
        }

        return string.Concat(degrees.Select(GetChordCodeFromDegree));
    }

    private static bool StartsWithLongRhythm(string rhythmPattern)
    {
        if (string.IsNullOrWhiteSpace(rhythmPattern) || rhythmPattern.Length < 2)
            return false;
        return GetDuration(rhythmPattern[..2]) >= TicksPerBeat;
    }

    private static int CountRestTicks(string pitchPattern, string rhythmPattern)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2);
        List<string> rhythms = TokenizePattern(rhythmPattern, 2);
        int count = Math.Min(pitches.Count, rhythms.Count);
        int ticks = 0;
        for (int i = 0; i < count; i++)
        {
            if (pitches[i] == "00")
                ticks += Math.Max(0, GetDuration(rhythms[i]));
        }
        return ticks;
    }

    private static int GetRhythmPatternSimilarityScore(string pattern, string prototype)
    {
        List<string> a = TokenizePattern(pattern, 2);
        List<string> b = TokenizePattern(prototype, 2);
        int count = Math.Min(a.Count, b.Count);
        if (count == 0)
            return 0;

        int score = 0;
        for (int i = 0; i < count; i++)
        {
            int da = GetDuration(a[i]);
            int db = GetDuration(b[i]);
            int diff = Math.Abs(da - db);
            if (diff == 0) score += 20;
            else if (diff <= 120) score += 12;
            else if (diff <= 240) score += 6;
        }

        score -= Math.Abs(a.Count - b.Count) * 8;
        return score;
    }

    private static int GetContourSimilarityScore(string contourA, string contourB)
    {
        if (string.IsNullOrWhiteSpace(contourA) || string.IsNullOrWhiteSpace(contourB))
            return 0;

        int count = Math.Min(contourA.Length, contourB.Length);
        int score = 0;
        for (int i = 0; i < count; i++)
            score += contourA[i] == contourB[i] ? 18 : -4;

        score -= Math.Abs(contourA.Length - contourB.Length) * 6;
        return score;
    }

    private static bool IsJiangNanCadencePitch(string pitch)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch == "00")
            return false;

        int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
        return pc is 0 or 2 or 4 or 7 or 9;
    }

    private string GenerateSongCandidateFast(
        MelodyStyle style,
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        ChordInfo chord,
        int barIndex,
        int totalBars,
        TimeSectionRole sectionRole,
        double songProgress,
        bool isPhraseEnding,
        bool isSongEnding,
        string? previousPitch,
        IReadOnlyList<string> seedPitches,
        string? seedMode,
        JiangNanThemeSeed? jiangNanThemeSeed)
    {
        if (style == MelodyStyle.JiangNan)
        {
            if (jiangNanThemeSeed is not null && barIndex < 4)
            {
                string? themedBar = BuildJiangNanThemedOpeningBar(
                    jiangNanThemeSeed,
                    chord,
                    keyRoot,
                    minorKey,
                    barIndex,
                    isPhraseEnding,
                    isSongEnding);

                if (!string.IsNullOrWhiteSpace(themedBar))
                    return ApplySeedMotifToBar(themedBar, seedPitches, barIndex, seedMode, isSongEnding);
            }

            string? xmlBar = TryGenerateXmlBasedJiangNanBar(
                emotion,
                mode,
                keyRoot,
                minorKey,
                chord,
                barIndex,
                totalBars,
                sectionRole,
                isPhraseEnding,
                isSongEnding,
                previousPitch);

            if (!string.IsNullOrWhiteSpace(xmlBar))
                return ApplySeedMotifToBar(xmlBar, seedPitches, barIndex, seedMode, isSongEnding);
        }

        string? genericXmlBar = TryGenerateXmlBasedLegacyBar(
            style,
            emotion,
            mode,
            keyRoot,
            minorKey,
            chord,
            barIndex,
            totalBars,
            sectionRole,
            isPhraseEnding,
            isSongEnding,
            previousPitch);

        if (!string.IsNullOrWhiteSpace(genericXmlBar))
            return ApplySeedMotifToBar(genericXmlBar, seedPitches, barIndex, seedMode, isSongEnding);

        string rhythmPattern = PickRhythmPatternFromRepository(style, emotion, sectionRole, isPhraseEnding, isSongEnding)
            ?? PickRhythmPattern(style, emotion, sectionRole, isPhraseEnding, isSongEnding);
        List<string> rhythmTokens = TokenizePattern(rhythmPattern, 2);
        int noteCount = rhythmTokens.Count;

        List<NoteChoice> pitchPool = BuildPitchPool(style, emotion, mode, keyRoot, minorKey, chord, sectionRole, songProgress);
        List<string> pitches = GeneratePitchLineFromPool(
            pitchPool,
            noteCount,
            chord,
            emotion,
            sectionRole,
            isPhraseEnding,
            isSongEnding,
            previousPitch);

        string generatedBar = ZipPitchAndRhythm(pitches, rhythmTokens);
        return ApplySeedMotifToBar(generatedBar, seedPitches, barIndex, seedMode, isSongEnding);
    }

    private JiangNanThemeSeed? BuildJiangNanThemeSeed(
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        ChordInfo openingChord,
        IReadOnlyList<string> seedPitches)
    {
        // 1) 若使用者有 Keyboard Seed，優先把 seed 轉成江南五聲主題。
        // 這對應原 WinForms：Seed Input 會同時影響 gen chord 與 gen melody 的起始素材。
        List<string> usableSeed = seedPitches
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != "00")
            .Select(p => ForcePitchToJiangNanScale(p, keyRoot, minOctave: 3, maxOctave: 5))
            .Where(p => p != "00")
            .Take(6)
            .ToList();

        if (usableSeed.Count >= 3)
        {
            List<string> seedRhythms = BuildJiangNanThemeRhythms(usableSeed.Count);
            usableSeed = FitPitchTokenCount(usableSeed, seedRhythms.Count, keyRoot, minorKey);
            return new JiangNanThemeSeed(
                usableSeed,
                seedRhythms,
                BuildContourSignature(usableSeed),
                "Keyboard Seed",
                usableSeed.Count(p => p != "00"));
        }

        // 2) 沒有 seed 時，從舊版 XML PitchSet / RhythmSet 中挑一個清楚的短動機作為 A 主題。
        List<LegacyRhythmPattern> rhythms = _repository.RhythmPatterns
            .Where(r => r.TotalTick == TicksPerBar && r.Length >= 4 && r.Length <= 8)
            .ToList();

        if (rhythms.Count > 0 && _repository.PitchPatterns.Count > 0)
        {
            var candidates = new List<(LegacyPitchPattern Pitch, LegacyRhythmPattern Rhythm, int Score)>();

            foreach (LegacyRhythmPattern rhythm in SampleRhythmPatterns(rhythms, maxCount: 48, seed: keyRoot + 17))
            {
                foreach (LegacyPitchPattern pitch in SamplePitchPatternsByLength(rhythm.Length, maxCount: 56, seed: keyRoot + rhythm.Length * 13))
                {
                    int score = ScoreJiangNanThemeCandidate(
                        pitch.Pattern,
                        rhythm.Pattern,
                        emotion,
                        openingChord);

                    candidates.Add((pitch, rhythm, score));
                }
            }

            var selected = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(_ => Random.Shared.Next())
                .FirstOrDefault();

            if (selected.Pitch is not null && selected.Rhythm is not null)
            {
                List<string> pitches = TokenizePattern(selected.Pitch.Pattern, 2)
                    .Select(p => TransposePitchFromC(p, keyRoot, minorKey, mode))
                    .Select(p => ForcePitchToJiangNanScale(p, keyRoot, minOctave: 3, maxOctave: 5))
                    .ToList();

                List<string> rhythmTokens = TokenizePattern(selected.Rhythm.Pattern, 2).ToList();

                if (pitches.Count == rhythmTokens.Count && pitches.Any(p => p != "00"))
                {
                    return new JiangNanThemeSeed(
                        pitches,
                        rhythmTokens,
                        BuildContourSignature(pitches),
                        "XML PitchSet/RhythmSet",
                        pitches.Count(p => p != "00"));
                }
            }
        }

        // 3) XML 缺資料時的保底，維持江南五聲 1-2-3-5-3 的起句感。
        List<string> fallback = new()
        {
            PitchFromSemitone(keyRoot, 4),
            PitchFromSemitone(keyRoot + 2, 4),
            PitchFromSemitone(keyRoot + 4, 4),
            PitchFromSemitone(keyRoot + 7, 4),
            PitchFromSemitone(keyRoot + 4, 4)
        };

        fallback = fallback
            .Select(p => ForcePitchToJiangNanScale(p, keyRoot, minOctave: 3, maxOctave: 5))
            .ToList();

        return new JiangNanThemeSeed(
            fallback,
            ["04", "04", "08", "08", "04"],
            BuildContourSignature(fallback),
            "Built-in fallback motif",
            fallback.Count);
    }

    private static int ScoreJiangNanThemeCandidate(
        string pitchPattern,
        string rhythmPattern,
        EmotionType emotion,
        ChordInfo openingChord)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2).Where(p => p != "00").ToList();
        List<string> rhythms = TokenizePattern(rhythmPattern, 2).ToList();
        int score = 0;

        if (pitches.Count >= 4 && pitches.Count <= 6) score += 28;
        if (pitches.Count >= 7) score -= 10;
        if (rhythms.Count >= 4 && rhythms.Count <= 8) score += 12;
        if (rhythms.Contains("4.") || rhythms.Contains("8.")) score += 6;
        if (rhythms.SequenceEqual(new[] { "04", "04", "08", "08", "04" })) score += 10;

        if (pitches.Count > 0)
        {
            if (IsChordSupportedPitch(pitches[0], openingChord)) score += 12;
            if (IsChordSupportedPitch(pitches[^1], openingChord)) score += 16;
        }

        score -= CountLargeLeaps(pitches) * 18;
        score -= CountSharpPitches(pitchPattern) * 10;

        string contour = BuildContourSignature(pitches);
        if (contour.Contains("UD", StringComparison.Ordinal) || contour.Contains("DU", StringComparison.Ordinal)) score += 10;
        if (emotion == EmotionType.Calm && pitches.Count <= 5) score += 8;
        if (emotion == EmotionType.Bright && contour.Count(c => c == 'U') >= 2) score += 7;

        return score;
    }

    private static string? BuildJiangNanThemedOpeningBar(
        JiangNanThemeSeed themeSeed,
        ChordInfo chord,
        int keyRoot,
        bool minorKey,
        int barIndex,
        bool isPhraseEnding,
        bool isSongEnding)
    {
        if (themeSeed.Pitches.Count == 0 || themeSeed.Pitches.Count != themeSeed.Rhythms.Count)
            return null;

        List<string> pitches = themeSeed.Pitches.ToList();
        List<string> rhythms = themeSeed.Rhythms.ToList();

        int phraseSlot = barIndex % 4;

        // A / A' / A / A''：第 1、3 小節保留輪廓；第 2 小節做輕微應答；第 4 小節做收束。
        if (phraseSlot == 1)
        {
            for (int i = Math.Max(1, pitches.Count / 2); i < pitches.Count; i++)
            {
                if (pitches[i] != "00")
                    pitches[i] = MoveJiangNanPitchByScaleSteps(pitches[i], i % 2 == 0 ? 1 : -1, keyRoot, minOctave: 3, maxOctave: 5);
            }
        }
        else if (phraseSlot == 3)
        {
            for (int i = 1; i < pitches.Count - 1; i++)
            {
                if (pitches[i] != "00" && i % 2 == 1)
                    pitches[i] = MoveJiangNanPitchByScaleSteps(pitches[i], -1, keyRoot, minOctave: 3, maxOctave: 5);
            }
        }

        int lastPitchIndex = pitches.FindLastIndex(p => p != "00");
        if (lastPitchIndex >= 0)
        {
            bool shouldCadence = phraseSlot == 3 || isPhraseEnding || isSongEnding;
            if (shouldCadence)
                pitches[lastPitchIndex] = ResolveJiangNanThemeEndingPitch(chord, keyRoot, minorKey, isSongEnding);
            else if (!IsChordSupportedPitch(pitches[lastPitchIndex], chord))
                pitches[lastPitchIndex] = MovePitchTowardChordToneInJiangNanScale(pitches[lastPitchIndex], chord, keyRoot);
        }

        pitches = pitches
            .Select(p => ForcePitchToJiangNanScale(p, keyRoot, minOctave: 3, maxOctave: 5))
            .ToList();

        return ZipPitchAndRhythm(pitches, rhythms);
    }

    private static List<string> BuildJiangNanThemeRhythms(int count)
    {
        return count switch
        {
            <= 3 => ["04", "04", "02"],
            4 => ["04", "08", "08", "02"],
            5 => ["04", "04", "08", "08", "04"],
            _ => ["08", "08", "08", "08", "04", "04"]
        };
    }

    private static List<string> FitPitchTokenCount(List<string> pitches, int count, int keyRoot, bool minorKey)
    {
        List<string> result = pitches.Where(p => p != "00").Take(count).ToList();
        while (result.Count < count)
            result.Add(PitchFromSemitone(keyRoot, minorKey ? 4 : 5));
        return result;
    }

    private static int CountSharpPitches(string pitchPattern)
    {
        int count = 0;
        foreach (string pitch in TokenizePattern(pitchPattern, 2))
        {
            if (pitch != "00" && ContainsSharpPitch(pitch))
                count++;
        }
        return count;
    }

    private static string BuildContourSignature(IReadOnlyList<string> pitches)
    {
        List<int> values = pitches
            .Where(p => p != "00")
            .Select(PitchToAbsoluteSemitone)
            .ToList();

        if (values.Count <= 1)
            return "flat";

        StringBuilder sb = new();
        for (int i = 1; i < values.Count; i++)
        {
            int diff = values[i] - values[i - 1];
            if (diff > 0) sb.Append('U');
            else if (diff < 0) sb.Append('D');
            else sb.Append('S');
        }

        return sb.ToString();
    }

    private static string ForcePitchToJiangNanScale(string pitch, int keyRoot, int minOctave, int maxOctave)
    {
        if (pitch == "00")
            return pitch;

        int sourceAbs = PitchToAbsoluteSemitone(pitch);
        int minAbs = minOctave * 12;
        int maxAbs = (maxOctave * 12) + 11;
        int[] pentatonic = [0, 2, 4, 7, 9];

        int bestAbs = sourceAbs;
        int bestDistance = int.MaxValue;

        for (int octave = minOctave; octave <= maxOctave; octave++)
        {
            foreach (int interval in pentatonic)
            {
                int candidate = octave * 12 + NormalizeSemitone(keyRoot + interval);
                if (candidate < minAbs || candidate > maxAbs)
                    continue;

                int distance = Math.Abs(candidate - sourceAbs);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestAbs = candidate;
                }
            }
        }

        return PitchFromAbsoluteSemitone(bestAbs);
    }

    private static string MoveJiangNanPitchByScaleSteps(string pitch, int steps, int keyRoot, int minOctave, int maxOctave)
    {
        if (pitch == "00" || steps == 0)
            return pitch;

        int[] pentatonic = [0, 2, 4, 7, 9];
        List<int> scaleAbs = new();
        for (int octave = minOctave; octave <= maxOctave; octave++)
        {
            foreach (int interval in pentatonic)
                scaleAbs.Add(octave * 12 + NormalizeSemitone(keyRoot + interval));
        }

        scaleAbs = scaleAbs.Distinct().OrderBy(v => v).ToList();
        if (scaleAbs.Count == 0)
            return pitch;

        int sourceAbs = PitchToAbsoluteSemitone(ForcePitchToJiangNanScale(pitch, keyRoot, minOctave, maxOctave));
        int nearestIndex = 0;
        int nearestDistance = int.MaxValue;

        for (int i = 0; i < scaleAbs.Count; i++)
        {
            int distance = Math.Abs(scaleAbs[i] - sourceAbs);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        int targetIndex = Math.Clamp(nearestIndex + steps, 0, scaleAbs.Count - 1);
        return PitchFromAbsoluteSemitone(scaleAbs[targetIndex]);
    }

    private static string ResolveJiangNanThemeEndingPitch(ChordInfo chord, int keyRoot, bool minorKey, bool isSongEnding)
    {
        if (isSongEnding)
            return PitchFromSemitone(keyRoot, minorKey ? 4 : 5);

        int[] chordTones = GetChordTonePitchClasses(chord).ToArray();
        int[] pentatonic = [0, 2, 4, 7, 9];
        int targetPc = chordTones.FirstOrDefault(pc => pentatonic.Contains(NormalizeSemitone(pc - keyRoot)));

        if (targetPc == 0 && !chordTones.Contains(0))
            targetPc = keyRoot;

        return ForcePitchToJiangNanScale(PitchFromSemitone(targetPc, 4), keyRoot, minOctave: 3, maxOctave: 5);
    }

    private static string MovePitchTowardChordToneInJiangNanScale(string pitch, ChordInfo chord, int keyRoot)
    {
        HashSet<int> chordTones = GetChordTonePitchClasses(chord);
        string best = ForcePitchToJiangNanScale(pitch, keyRoot, minOctave: 3, maxOctave: 5);
        int bestScore = chordTones.Contains(NormalizeSemitone(PitchToAbsoluteSemitone(best))) ? 0 : 100;

        foreach (int step in new[] { -2, -1, 1, 2 })
        {
            string candidate = MoveJiangNanPitchByScaleSteps(pitch, step, keyRoot, minOctave: 3, maxOctave: 5);
            int score = chordTones.Contains(NormalizeSemitone(PitchToAbsoluteSemitone(candidate))) ? 0 : Math.Abs(step) + 10;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private IEnumerable<LegacyRhythmPattern> SampleRhythmPatterns(IEnumerable<LegacyRhythmPattern> source, int maxCount, int seed)
    {
        List<LegacyRhythmPattern> list = source as List<LegacyRhythmPattern> ?? source.ToList();
        if (list.Count <= maxCount)
            return list;

        int step = Math.Max(1, list.Count / maxCount);
        int offset = Math.Abs(seed) % step;
        List<LegacyRhythmPattern> sampled = new(capacity: maxCount);

        for (int i = offset; i < list.Count && sampled.Count < maxCount; i += step)
            sampled.Add(list[i]);

        // 補少量隨機樣本，避免每次都只拿固定位置造成風格過窄。
        int guard = 0;
        while (sampled.Count < maxCount && guard++ < maxCount * 3)
        {
            LegacyRhythmPattern candidate = list[Random.Shared.Next(list.Count)];
            if (!sampled.Contains(candidate))
                sampled.Add(candidate);
        }

        return sampled;
    }

    private IEnumerable<LegacyPitchPattern> SamplePitchPatternsByLength(int length, int maxCount, int seed)
    {
        List<LegacyPitchPattern> matches = _repository.PitchPatterns
            .Where(p => p.Length == length)
            .ToList();

        if (matches.Count <= maxCount)
            return matches;

        int step = Math.Max(1, matches.Count / maxCount);
        int offset = Math.Abs(seed) % step;
        List<LegacyPitchPattern> sampled = new(capacity: maxCount);

        for (int i = offset; i < matches.Count && sampled.Count < maxCount; i += step)
            sampled.Add(matches[i]);

        int guard = 0;
        while (sampled.Count < maxCount && guard++ < maxCount * 3)
        {
            LegacyPitchPattern candidate = matches[Random.Shared.Next(matches.Count)];
            if (!sampled.Contains(candidate))
                sampled.Add(candidate);
        }

        return sampled;
    }

    private static string BuildStep8ThemeSeedReport(JiangNanThemeSeed? themeSeed)
    {
        if (themeSeed is null)
            return string.Empty;

        return Environment.NewLine + Environment.NewLine +
               "Step 8 JiangNan ThemeSeed：已啟用。" + Environment.NewLine +
               $"主題來源：{themeSeed.Source}" + Environment.NewLine +
               $"A 主題輪廓：{themeSeed.ContourSignature}" + Environment.NewLine +
               $"A 主題音數：{themeSeed.NonRestCount}" + Environment.NewLine +
               "前四小節使用 A / A' / A / A'' 結構，對應原 WinForms 的 currentJiangNanThemeSeed 設計。";
    }

    private string? TryGenerateXmlBasedLegacyBar(
        MelodyStyle style,
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        ChordInfo chord,
        int barIndex,
        int totalBars,
        TimeSectionRole sectionRole,
        bool isPhraseEnding,
        bool isSongEnding,
        string? previousPitch)
    {
        // Step 9：把原版 2024_PitchSet / 2024_RhythmSet 的使用範圍從江南擴大到所有風格。
        // 原 WinForms 的音樂感很大一部分來自 XML pattern，而不是純隨機音階走向。
        if (_repository.PitchPatterns.Count == 0 || _repository.RhythmPatterns.Count == 0)
            return null;

        List<LegacyRhythmPattern> rhythms = PickRhythmCandidatesFromRepository(
            style,
            emotion,
            sectionRole,
            isPhraseEnding,
            isSongEnding,
            minLength: 2,
            maxLength: 12,
            maxCount: 56);

        if (rhythms.Count == 0)
            return null;

        List<(LegacyPitchPattern Pitch, LegacyRhythmPattern Rhythm, int Score)> candidates = new();

        foreach (LegacyRhythmPattern rhythm in rhythms)
        {
            foreach (LegacyPitchPattern pitch in SamplePitchPatternsByLength(rhythm.Length, maxCount: 56, seed: barIndex * 31 + rhythm.Length * 7))
            {
                int score = ScoreXmlPatternForLegacyStyle(
                    pitch.Pattern,
                    rhythm.Pattern,
                    style,
                    emotion,
                    chord,
                    sectionRole,
                    isPhraseEnding,
                    isSongEnding,
                    previousPitch);

                candidates.Add((pitch, rhythm, score));
            }
        }

        if (candidates.Count == 0)
            return null;

        var selected = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(_ => Random.Shared.Next())
            .FirstOrDefault();

        if (selected.Pitch is null || selected.Rhythm is null)
            return null;

        List<string> pitches = TokenizePattern(selected.Pitch.Pattern, 2)
            .Select(p => TransposePitchFromC(p, keyRoot, minorKey, mode))
            .Select(p => style == MelodyStyle.JiangNan
                ? ForcePitchToJiangNanScale(p, keyRoot, minOctave: 3, maxOctave: 5)
                : ClampPitch(p, style == MelodyStyle.Jazz ? 3 : 3, emotion == EmotionType.Energetic || style == MelodyStyle.JPop ? 6 : 5))
            .ToList();

        List<string> rhythmTokens = TokenizePattern(selected.Rhythm.Pattern, 2).ToList();
        if (pitches.Count != rhythmTokens.Count)
            return null;

        if (pitches.Count > 0)
        {
            if (isSongEnding)
                pitches[^1] = style == MelodyStyle.JiangNan
                    ? ForcePitchToJiangNanScale(PitchFromSemitone(keyRoot, 4), keyRoot, minOctave: 3, maxOctave: 5)
                    : PitchFromSemitone(chord.RootSemitone, minorKey ? 4 : 5);
            else if (isPhraseEnding && !IsChordSupportedPitch(pitches[^1], chord))
                pitches[^1] = MoveLastPitchTowardChordTone(pitches[^1], chord, style, keyRoot);
        }

        string processedPitchPattern = ApplyLegacyMelodyPostProcessingToPattern(
            string.Concat(pitches),
            selected.Rhythm.Pattern,
            chord,
            style,
            emotion,
            keyRoot,
            minorKey,
            previousPitch ?? "00",
            sectionRole,
            isPhraseEnding,
            isSongEnding);

        return ZipPitchAndRhythm(TokenizePattern(processedPitchPattern, 2), rhythmTokens);
    }

    private static int ScoreXmlPatternForLegacyStyle(
        string pitchPattern,
        string rhythmPattern,
        MelodyStyle style,
        EmotionType emotion,
        ChordInfo chord,
        TimeSectionRole sectionRole,
        bool isPhraseEnding,
        bool isSongEnding,
        string? previousPitch)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2).Where(p => p != "00").ToList();
        List<string> rhythms = TokenizePattern(rhythmPattern, 2).ToList();
        int score = 0;

        if (pitches.Count >= 4 && pitches.Count <= 8) score += 35;
        if (pitches.Count > 8 && style is MelodyStyle.JPop or MelodyStyle.Jazz) score += 12;
        if (pitches.Count > 9 && emotion == EmotionType.Calm) score -= 25;
        if (rhythms.Count >= 4 && rhythms.Count <= 8) score += 18;

        // Step 15：XML 資料庫本身偏江南素材時，非江南風格必須做 style separation。
        // 否則 Pop/JPop/Jazz 會一直抽到五聲 + 長音開頭的江南型 pattern。
        bool pentatonicDominant = IsPentatonicDominantPattern(pitches);
        int nonPentatonicDiatonicCount = CountNonPentatonicDiatonicPitches(pitches);
        int jiangNanRhythmStrength = GetJiangNanRhythmSignatureStrength(rhythmPattern, rhythms.Count);

        if (style != MelodyStyle.JiangNan)
        {
            if (pentatonicDominant)
                score -= style == MelodyStyle.Jazz ? 95 : 55;
            else
                score += 18;

            score += nonPentatonicDiatonicCount * (style == MelodyStyle.JPop ? 14 : 9);
            score -= jiangNanRhythmStrength * (style == MelodyStyle.Pop ? 2 : 1);

            if (style == MelodyStyle.Jazz)
                score += CountChromaticColorPitches(pitches) * 26;
        }
        else
        {
            if (pentatonicDominant) score += 28;
            score += jiangNanRhythmStrength;
        }

        if (pitches.Count > 0)
        {
            if (IsChordSupportedPitch(pitches[0], chord)) score += 18;
            if (IsChordSupportedPitch(pitches[^1], chord)) score += isPhraseEnding ? 35 : 14;

            if (previousPitch is not null && previousPitch != "00")
            {
                int diff = Math.Abs(PitchToAbsoluteSemitone(pitches[0]) - PitchToAbsoluteSemitone(previousPitch));
                if (diff <= 2) score += 25;
                else if (diff <= 5) score += 12;
                else if (diff >= 9) score -= 40;
            }
        }

        int largeLeaps = CountLargeLeaps(pitches);
        score -= largeLeaps * (style == MelodyStyle.JiangNan ? 22 : 13);

        string contour = BuildContourSignature(pitches);
        if (style == MelodyStyle.JiangNan)
        {
            score += ScoreJiangNanLegacyPatternShape(pitchPattern, chord) * 2;
            score -= CountSharpPitches(pitchPattern) * 35;
        }
        else if (style == MelodyStyle.Pop)
        {
            if (contour.Contains("UD", StringComparison.Ordinal) || contour.Contains("DU", StringComparison.Ordinal)) score += 12;
            if (largeLeaps <= 1) score += 15;
        }
        else if (style == MelodyStyle.JPop)
        {
            if (contour.Count(c => c == 'U') >= 2) score += 16;
            if (pitches.Count >= 6) score += 10;
        }
        else if (style == MelodyStyle.Jazz)
        {
            int sharp = CountSharpPitches(pitchPattern);
            if (sharp >= 1) score += 18;
            if (largeLeaps >= 1 && largeLeaps <= 3) score += 10;
        }

        if (sectionRole == TimeSectionRole.Opening && pitches.Count <= 6) score += 10;
        if (sectionRole == TimeSectionRole.Contrast && pitches.Count >= 6) score += 12;
        if (sectionRole == TimeSectionRole.Closing && IsChordSupportedPitch(pitches.LastOrDefault("00"), chord)) score += 20;
        if (isSongEnding && pitches.Count > 0) score += 25;

        if (emotion == EmotionType.Calm && largeLeaps == 0) score += 14;
        if (emotion == EmotionType.Bright && contour.Count(c => c == 'U') >= 2) score += 10;
        if (emotion == EmotionType.Sad && contour.Count(c => c == 'D') >= 2) score += 10;
        if (emotion == EmotionType.Energetic && pitches.Count >= 7) score += 16;

        if (rhythms.Contains("4.") || rhythms.Contains("8.")) score += style == MelodyStyle.JiangNan ? 10 : 5;
        if (rhythms.Distinct(StringComparer.Ordinal).Count() >= 3) score += style == MelodyStyle.Jazz ? 12 : 6;

        score += GetLegacyRhythmStyleScore(style, emotion, sectionRole, isPhraseEnding, isSongEnding, rhythmPattern, rhythms.Count, TicksPerBar) / 2;
        score += ScoreLegacyChordSpecificMelodyLine(pitches, style, emotion, chord, sectionRole, isPhraseEnding, isSongEnding);

        return score;
    }


    /// <summary>
    /// Step 13：原 WinForms GetRhythmStyleScore 的 Web 重構版。
    /// 這個分數會同時影響 XML draft、一般 XML bar 與 fallback rhythm selection。
    /// </summary>
    private static int GetLegacyRhythmStyleScore(
        MelodyStyle style,
        EmotionType emotion,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding,
        string rhythmPattern,
        int length,
        int totalTick)
    {
        int score = 100;

        if (totalTick != 0)
            score -= Math.Abs(totalTick - TicksPerBar) / 30;

        if (style == MelodyStyle.JiangNan)
        {
            if (length == 1 || length == 2) score += 15;
            else if (length == 3 || length == 4) score += 25;
            else if (length == 5 || length == 6) score += 10;
            else if (length > 6) score -= (length - 6) * 15;

            if (rhythmPattern.Contains("4.", StringComparison.Ordinal) ||
                rhythmPattern.Contains("8.", StringComparison.Ordinal) ||
                rhythmPattern.Contains("2.", StringComparison.Ordinal))
                score += 80;

            if (rhythmPattern.StartsWith("2.", StringComparison.Ordinal) ||
                rhythmPattern.StartsWith("4.", StringComparison.Ordinal) ||
                rhythmPattern.StartsWith("02", StringComparison.Ordinal) ||
                rhythmPattern.StartsWith("01", StringComparison.Ordinal))
                score += 40;

            score += GetJiangNanABRhythmScore(rhythmPattern, length, role, isPhraseEnding);
            score -= GetJiangNanLongRhythmPenalty(rhythmPattern);
        }
        else if (style == MelodyStyle.Pop)
        {
            score -= Math.Abs(length - 6) * 7;
            if (length >= 4 && length <= 8) score += 12;
        }
        else if (style == MelodyStyle.JPop)
        {
            score -= Math.Abs(length - 8) * 6;
            if (length >= 6 && length <= 10) score += 16;
            if (rhythmPattern.Contains("08", StringComparison.Ordinal) || rhythmPattern.Contains("16", StringComparison.Ordinal)) score += 8;
        }
        else if (style == MelodyStyle.Jazz)
        {
            score -= Math.Abs(length - 8) * 5;
            if (rhythmPattern.Contains("00", StringComparison.Ordinal)) score += 20;
            if ((rhythmPattern.Contains("2.", StringComparison.Ordinal) || rhythmPattern.Contains("4.", StringComparison.Ordinal)) && rhythmPattern.Contains("08", StringComparison.Ordinal)) score += 25;
            if (rhythmPattern.Contains("4.", StringComparison.Ordinal) && rhythmPattern.Contains("08", StringComparison.Ordinal)) score += 50;
            if (rhythmPattern.Contains("2.", StringComparison.Ordinal) && rhythmPattern.Contains("04", StringComparison.Ordinal)) score += 40;
        }

        if (style != MelodyStyle.JiangNan)
        {
            int jiangNanSignature = GetJiangNanRhythmSignatureStrength(rhythmPattern, length);
            score -= style switch
            {
                MelodyStyle.Pop => jiangNanSignature * 2,
                MelodyStyle.JPop => jiangNanSignature,
                MelodyStyle.Jazz => jiangNanSignature / 2,
                _ => jiangNanSignature
            };
        }

        score += GetLegacyEmotionRhythmScore(emotion, length, rhythmPattern);
        score += GetLegacyTimeSectionRhythmScore(role, isPhraseEnding, isSongEnding, length, rhythmPattern);
        return score;
    }

    private static int GetJiangNanRhythmSignatureStrength(string rhythmPattern, int length)
    {
        int score = 0;
        bool dotted = rhythmPattern.Contains("4.", StringComparison.Ordinal) ||
                      rhythmPattern.Contains("8.", StringComparison.Ordinal) ||
                      rhythmPattern.Contains("2.", StringComparison.Ordinal);
        bool longStart = rhythmPattern.StartsWith("02", StringComparison.Ordinal) ||
                         rhythmPattern.StartsWith("01", StringComparison.Ordinal) ||
                         rhythmPattern.StartsWith("04", StringComparison.Ordinal) ||
                         rhythmPattern.StartsWith("2.", StringComparison.Ordinal) ||
                         rhythmPattern.StartsWith("4.", StringComparison.Ordinal);
        bool shortPentatonicFlow = length >= 3 && length <= 6;

        if (dotted) score += 28;
        if (longStart) score += 24;
        if (shortPentatonicFlow) score += 10;
        if (rhythmPattern.Contains("0808", StringComparison.Ordinal) && length <= 6) score += 8;
        return score;
    }

    private static bool IsPentatonicDominantPattern(IReadOnlyList<string> pitches)
    {
        List<string> real = pitches.Where(p => p != "00").ToList();
        if (real.Count < 3)
            return false;

        int pentatonic = real.Count(IsRelativeMajorPentatonicPitch);
        return pentatonic >= Math.Ceiling(real.Count * 0.82);
    }

    private static bool IsRelativeMajorPentatonicPitch(string pitch)
    {
        int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
        return pc is 0 or 2 or 4 or 7 or 9;
    }

    private static int CountNonPentatonicDiatonicPitches(IReadOnlyList<string> pitches)
    {
        int count = 0;
        foreach (string pitch in pitches)
        {
            if (pitch == "00")
                continue;
            int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
            if (pc is 5 or 11)
                count++;
        }
        return count;
    }

    private static int CountChromaticColorPitches(IReadOnlyList<string> pitches)
    {
        int count = 0;
        foreach (string pitch in pitches)
        {
            if (pitch == "00")
                continue;
            int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
            if (pc is 1 or 3 or 6 or 8 or 10 || pitch.Contains('#'))
                count++;
        }
        return count;
    }

    private static int GetLegacyEmotionRhythmScore(EmotionType emotion, int length, string rhythmPattern)
    {
        int score = 0;
        if (emotion == EmotionType.Calm)
        {
            if (length >= 2 && length <= 5) score += 18;
            if (length > 8) score -= 25;
            if (rhythmPattern.Contains("02", StringComparison.Ordinal) || rhythmPattern.Contains("04", StringComparison.Ordinal)) score += 8;
        }
        else if (emotion == EmotionType.Bright)
        {
            if (length >= 5 && length <= 8) score += 14;
            if (rhythmPattern.Contains("08", StringComparison.Ordinal)) score += 8;
        }
        else if (emotion == EmotionType.Sad)
        {
            if (length <= 6) score += 12;
            if (rhythmPattern.Contains("4.", StringComparison.Ordinal) || rhythmPattern.Contains("02", StringComparison.Ordinal)) score += 10;
        }
        else if (emotion == EmotionType.Tense)
        {
            if (length >= 6) score += 10;
            if (rhythmPattern.Contains("16", StringComparison.Ordinal)) score += 10;
        }
        else if (emotion == EmotionType.Energetic)
        {
            if (length >= 7) score += 18;
            if (rhythmPattern.Contains("08", StringComparison.Ordinal) || rhythmPattern.Contains("16", StringComparison.Ordinal)) score += 12;
        }
        return score;
    }

    private static int GetLegacyTimeSectionRhythmScore(TimeSectionRole role, bool isPhraseEnding, bool isSongEnding, int length, string rhythmPattern)
    {
        int score = 0;
        if (role == TimeSectionRole.Opening)
        {
            if (length >= 3 && length <= 6) score += 12;
            if (length > 8) score -= 18;
        }
        else if (role == TimeSectionRole.Development)
        {
            if (length >= 4 && length <= 8) score += 10;
        }
        else if (role == TimeSectionRole.Contrast)
        {
            if (length >= 6) score += 14;
            if (rhythmPattern.Contains("08", StringComparison.Ordinal) || rhythmPattern.Contains("16", StringComparison.Ordinal)) score += 6;
        }
        else if (role == TimeSectionRole.Closing)
        {
            if (length <= 6) score += 15;
            if (length > 8) score -= 20;
        }

        if (isPhraseEnding && length <= 6) score += 18;
        if (isSongEnding && length <= 5) score += 30;
        if (isSongEnding && length > 8) score -= 40;
        return score;
    }

    private static int GetJiangNanABRhythmScore(string rhythmPattern, int length, TimeSectionRole role, bool isPhraseEnding)
    {
        int score = 0;
        bool dotted = rhythmPattern.Contains("4.", StringComparison.Ordinal) || rhythmPattern.Contains("8.", StringComparison.Ordinal) || rhythmPattern.Contains("2.", StringComparison.Ordinal);
        bool longStart = rhythmPattern.StartsWith("02", StringComparison.Ordinal) || rhythmPattern.StartsWith("04", StringComparison.Ordinal) || rhythmPattern.StartsWith("2.", StringComparison.Ordinal) || rhythmPattern.StartsWith("4.", StringComparison.Ordinal);
        bool flowingTail = rhythmPattern.EndsWith("0808", StringComparison.Ordinal) || rhythmPattern.EndsWith("0408", StringComparison.Ordinal) || rhythmPattern.EndsWith("0804", StringComparison.Ordinal);

        if (role == TimeSectionRole.Opening && (longStart || dotted)) score += 20;
        if (role == TimeSectionRole.Development && flowingTail) score += 12;
        if (role == TimeSectionRole.Contrast && length >= 5 && length <= 8) score += 14;
        if (role == TimeSectionRole.Closing && (longStart || length <= 4)) score += 18;
        if (isPhraseEnding && length <= 5) score += 12;
        return score;
    }

    private static int GetJiangNanLongRhythmPenalty(string rhythmPattern)
    {
        if (string.IsNullOrWhiteSpace(rhythmPattern))
            return 0;

        int penalty = 0;
        int tickInBar = 0;
        int veryShortCount = 0;
        foreach (string rhythm in TokenizePattern(rhythmPattern, 2))
        {
            int dur = GetDuration(rhythm);
            if (dur <= 0)
                continue;

            if (dur <= 120)
            {
                penalty += 55;
                veryShortCount++;
            }
            else if (dur < 240)
            {
                penalty += 28;
                veryShortCount++;
            }
            else if (dur == 240)
            {
                penalty += 6;
            }

            if (dur > TicksPerBeat * 3)
                penalty += 18;

            int tickInBeat = tickInBar % TicksPerBeat;
            if (tickInBeat != 0 && tickInBeat + dur > TicksPerBeat)
                penalty += 10;

            tickInBar += dur;
        }

        if (veryShortCount >= 3)
            penalty += veryShortCount * 18;

        return penalty;
    }

    private static int ScoreLegacyChordSpecificMelodyLine(
        IReadOnlyList<string> pitches,
        MelodyStyle style,
        EmotionType emotion,
        ChordInfo chord,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding)
    {
        if (pitches.Count == 0)
            return -120;

        int score = 0;
        string previous = "00";
        int previousDirection = 0;

        for (int i = 0; i < pitches.Count; i++)
        {
            string pitch = pitches[i];
            if (pitch == "00")
                continue;

            bool strong = i == 0 || i == pitches.Count / 2;
            bool final = i == pitches.Count - 1;
            bool chordTone = IsChordSupportedPitch(pitch, chord);
            bool sharp = pitch.Contains('#');
            int current = PitchToAbsoluteSemitone(pitch);
            int previousAbs = previous == "00" ? current : PitchToAbsoluteSemitone(previous);
            int diff = previous == "00" ? 0 : Math.Abs(current - previousAbs);
            int direction = previous == "00" ? previousDirection : Math.Sign(current - previousAbs);

            score -= GetChordSpecificMelodyPenalty(style, chord, pitch, strong, final);
            score -= GetTimeSectionMelodyPenalty(role, isPhraseEnding, isSongEnding, current, previousAbs, final, chordTone, sharp, direction, diff);

            if (diff <= 2 && previous != "00") score += style == MelodyStyle.JiangNan ? 10 : 6;
            if (diff >= 9) score -= style == MelodyStyle.JiangNan ? 35 : 22;

            previous = pitch;
            if (direction != 0)
                previousDirection = direction;
        }

        return score;
    }

    private static int GetChordSpecificMelodyPenalty(MelodyStyle style, ChordInfo chord, string pitch, bool strongPosition, bool finalPosition)
    {
        if (pitch == "00")
            return finalPosition ? 30 : 8;

        bool chordTone = IsChordSupportedPitch(pitch, chord);
        int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
        HashSet<int> chordTones = GetChordTonePitchClasses(chord);
        int penalty = 0;

        if (!chordTone && (strongPosition || finalPosition))
            penalty += finalPosition ? 55 : 28;
        else if (!chordTone)
            penalty += style == MelodyStyle.Jazz ? 5 : 12;

        if (NormalizeSemitone(pc - chord.RootSemitone) == 1 && style != MelodyStyle.Jazz)
            penalty += 18;

        if (style == MelodyStyle.JiangNan)
        {
            // 江南允許五聲經過音，但強拍/收尾仍要接近和弦或主音家族。
            if (!IsJiangNanCadencePitch(pitch) && finalPosition)
                penalty += 35;
            if (pitch.Contains('#'))
                penalty += 45;
        }
        else if (style == MelodyStyle.Pop || style == MelodyStyle.JPop)
        {
            if (strongPosition && !chordTones.Contains(pc)) penalty += 10;
        }
        else if (style == MelodyStyle.Jazz)
        {
            if (!chordTone && !pitch.Contains('#')) penalty += 3;
            if (pitch.Contains('#')) penalty -= 6;
        }

        return Math.Max(0, penalty);
    }

    private static int GetTimeSectionMelodyPenalty(
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding,
        int candidateAbs,
        int previousAbs,
        bool finalPosition,
        bool isChordTone,
        bool isSharp,
        int direction,
        int diff)
    {
        int penalty = 0;

        if (role == TimeSectionRole.Opening)
        {
            if (candidateAbs > PitchToAbsoluteSemitone("D5")) penalty += 12;
            if (diff > 7) penalty += 12;
        }
        else if (role == TimeSectionRole.Development)
        {
            if (direction > 0 && candidateAbs >= PitchToAbsoluteSemitone("G4")) penalty -= 2;
        }
        else if (role == TimeSectionRole.Contrast)
        {
            if (candidateAbs >= PitchToAbsoluteSemitone("C5")) penalty -= 6;
            if (diff >= 5 && diff <= 12) penalty -= 3;
        }
        else if (role == TimeSectionRole.Closing)
        {
            if (candidateAbs > PitchToAbsoluteSemitone("E5")) penalty += 14;
            if (isSharp) penalty += 15;
            if (diff > 7) penalty += 12;
        }

        if (isPhraseEnding)
        {
            if (finalPosition && isChordTone) penalty -= 35;
            if (finalPosition && !isChordTone) penalty += 45;
            if (finalPosition && isSharp) penalty += 55;
            if (diff > 5) penalty += 10;
        }

        if (isSongEnding)
        {
            if (finalPosition && isChordTone) penalty -= 50;
            if (finalPosition && !isChordTone) penalty += 70;
            if (isSharp) penalty += 70;
            if (candidateAbs > PitchToAbsoluteSemitone("C5")) penalty += 18;
        }

        return penalty;
    }

    private static string ApplyLegacyMelodyPostProcessingToPattern(
        string pitchPattern,
        string rhythmPattern,
        ChordInfo chord,
        MelodyStyle style,
        EmotionType emotion,
        int keyRoot,
        bool minorKey,
        string previousPitch,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2).ToList();
        List<string> rhythms = TokenizePattern(rhythmPattern, 2).ToList();
        int count = Math.Min(pitches.Count, rhythms.Count);
        if (count == 0)
            return pitchPattern;

        List<(string Pitch, string Rhythm)> events = new(capacity: count);
        string localPrevious = string.IsNullOrWhiteSpace(previousPitch) ? "00" : previousPitch;

        for (int i = 0; i < count; i++)
        {
            string pitch = pitches[i];
            if (pitch == "00")
            {
                // 原 WinForms 正式輸出會減少主旋律大量休止；Web 版在 XML pattern 層先做保守修補。
                if ((i == 0 || isPhraseEnding || isSongEnding) && Random.Shared.NextDouble() < 0.75)
                    pitch = GetNearestMainPitchForPostProcess(localPrevious, chord, style, keyRoot, minorKey, avoidPitch: "00");
            }
            else
            {
                if (style == MelodyStyle.JiangNan)
                    pitch = ForcePitchToJiangNanScale(pitch, keyRoot, minOctave: 3, maxOctave: 5);
                else
                    pitch = ClampPitch(pitch, 3, emotion == EmotionType.Energetic || style == MelodyStyle.JPop ? 6 : 5);

                bool strong = i == 0 || i == count / 2;
                bool final = i == count - 1;
                if (!IsChordSupportedPitch(pitch, chord) && (strong || final || Random.Shared.NextDouble() < 0.30))
                    pitch = style == MelodyStyle.JiangNan
                        ? MovePitchTowardChordToneInJiangNanScale(pitch, chord, keyRoot)
                        : MoveLastPitchTowardChordTone(pitch, chord, style, keyRoot);

                if (localPrevious != "00")
                {
                    int diff = Math.Abs(PitchToAbsoluteSemitone(pitch) - PitchToAbsoluteSemitone(localPrevious));
                    if (diff >= (style == MelodyStyle.JiangNan ? 8 : 11))
                        pitch = SmoothBarEntrancePitch(pitch, localPrevious, chord, style, keyRoot);
                    if (pitch == localPrevious && style != MelodyStyle.Jazz)
                        pitch = GetNearestMainPitchForPostProcess(pitch, chord, style, keyRoot, minorKey, avoidPitch: localPrevious);
                }
            }

            events.Add((pitch, rhythms[i]));
            if (pitch != "00")
                localPrevious = pitch;
        }

        events = NormalizeBarDurationEvents(events, keyRoot, minorKey);
        events = RepairRepeatedAndLargeLeapEvents(events, chord, style, keyRoot);
        events = ApplyNonJiangNanStyleColorEvents(events, chord, style, keyRoot, emotion);

        int last = events.FindLastIndex(e => e.Pitch != "00");
        if (last >= 0)
        {
            string lastPitch = events[last].Pitch;
            if (isSongEnding)
                lastPitch = style == MelodyStyle.JiangNan
                    ? ForcePitchToJiangNanScale(PitchFromSemitone(keyRoot, 4), keyRoot, minOctave: 3, maxOctave: 5)
                    : PitchFromSemitone(chord.RootSemitone, minorKey ? 4 : 5);
            else if (isPhraseEnding)
                lastPitch = style == MelodyStyle.JiangNan
                    ? ResolveJiangNanThemeEndingPitch(chord, keyRoot, minorKey, false)
                    : MoveLastPitchTowardChordTone(lastPitch, chord, style, keyRoot);
            events[last] = (lastPitch, events[last].Rhythm);
        }

        return string.Concat(events.Select(e => e.Pitch));
    }

    private static string GetNearestMainPitchForPostProcess(
        string sourcePitch,
        ChordInfo chord,
        MelodyStyle style,
        int keyRoot,
        bool minorKey,
        string avoidPitch)
    {
        string source = string.IsNullOrWhiteSpace(sourcePitch) || sourcePitch == "00"
            ? PitchFromSemitone(chord.RootSemitone, minorKey ? 4 : 5)
            : sourcePitch;

        int sourceAbs = PitchToAbsoluteSemitone(source);
        HashSet<int> chordTones = GetChordTonePitchClasses(chord);
        int[] scale = style == MelodyStyle.JiangNan ? [0, 2, 4, 7, 9] : [0, 2, 4, 5, 7, 9, 11];

        string best = source;
        int bestScore = int.MaxValue;
        for (int octave = 3; octave <= (style == MelodyStyle.JPop ? 6 : 5); octave++)
        {
            foreach (int interval in scale)
            {
                int abs = octave * 12 + NormalizeSemitone(keyRoot + interval);
                string candidate = PitchFromAbsoluteSemitone(abs);
                if (candidate == avoidPitch)
                    continue;

                int score = Math.Abs(abs - sourceAbs) * 8;
                if (!chordTones.Contains(NormalizeSemitone(abs))) score += style == MelodyStyle.JiangNan ? 18 : 10;
                if (style == MelodyStyle.JiangNan && candidate.Contains('#')) score += 40;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return style == MelodyStyle.JiangNan
            ? ForcePitchToJiangNanScale(best, keyRoot, minOctave: 3, maxOctave: 5)
            : best;
    }

    private static int ScoreJiangNanLegacyPatternShape(string pitchPattern, ChordInfo chord)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2).Where(p => p != "00").ToList();
        if (pitches.Count == 0)
            return 0;

        int score = 0;
        if (pitches.Count >= 4 && pitches.Count <= 7) score += 15;
        if (IsChordSupportedPitch(pitches[0], chord)) score += 8;
        if (IsChordSupportedPitch(pitches[^1], chord)) score += 14;
        if (BuildContourSignature(pitches).Contains("UD", StringComparison.Ordinal)) score += 8;
        score -= CountLargeLeaps(pitches) * 12;
        return score;
    }

    private static string MoveLastPitchTowardChordTone(string pitch, ChordInfo chord, MelodyStyle style, int keyRoot)
    {
        if (pitch == "00")
            return pitch;

        if (style == MelodyStyle.JiangNan)
            return MovePitchTowardChordToneInJiangNanScale(pitch, chord, keyRoot);

        HashSet<int> tones = GetChordTonePitchClasses(chord);
        int source = PitchToAbsoluteSemitone(pitch);
        string best = pitch;
        int bestScore = tones.Contains(NormalizeSemitone(source)) ? 0 : 1000;

        for (int offset = -7; offset <= 7; offset++)
        {
            int candidateAbs = source + offset;
            int score = Math.Abs(offset) * 10;
            if (!tones.Contains(NormalizeSemitone(candidateAbs)))
                score += 100;

            if (score < bestScore)
            {
                bestScore = score;
                best = PitchFromAbsoluteSemitone(candidateAbs);
            }
        }

        return best;
    }

    private string? TryGenerateXmlBasedJiangNanBar(
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        ChordInfo chord,
        int barIndex,
        int totalBars,
        TimeSectionRole sectionRole,
        bool isPhraseEnding,
        bool isSongEnding,
        string? previousPitch)
    {
        List<LegacyRhythmPattern> rhythms = PickRhythmCandidatesFromRepository(
            MelodyStyle.JiangNan,
            emotion,
            sectionRole,
            isPhraseEnding,
            isSongEnding,
            minLength: 4,
            maxLength: 11,
            maxCount: 42);

        if (rhythms.Count == 0 || _repository.PitchPatterns.Count == 0)
            return null;

        List<(LegacyPitchPattern Pitch, LegacyRhythmPattern Rhythm, int Score)> candidates = new();

        foreach (LegacyRhythmPattern rhythm in rhythms)
        {
            foreach (LegacyPitchPattern pitch in SamplePitchPatternsByLength(rhythm.Length, maxCount: 48, seed: barIndex * 37 + rhythm.Length * 11))
            {
                int score = ScoreXmlPatternForJiangNan(
                    pitch.Pattern,
                    rhythm.Pattern,
                    emotion,
                    chord,
                    sectionRole,
                    isPhraseEnding,
                    isSongEnding,
                    previousPitch);

                candidates.Add((pitch, rhythm, score));
            }
        }

        if (candidates.Count == 0)
            return null;

        var selected = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(_ => Random.Shared.Next())
            .First();

        List<string> pitches = TokenizePattern(selected.Pitch.Pattern, 2)
            .Select(p => TransposePitchFromC(p, keyRoot, minorKey, mode))
            .Select(p => ClampPitch(p, 3, emotion == EmotionType.Energetic ? 6 : 5))
            .ToList();

        List<string> rhythms2 = TokenizePattern(selected.Rhythm.Pattern, 2);

        if (isSongEnding && pitches.Count > 0)
            pitches[^1] = PitchFromSemitone(keyRoot, minorKey ? 4 : 5);

        string processedPitchPattern = ApplyLegacyMelodyPostProcessingToPattern(
            string.Concat(pitches),
            selected.Rhythm.Pattern,
            chord,
            MelodyStyle.JiangNan,
            emotion,
            keyRoot,
            minorKey,
            previousPitch ?? "00",
            sectionRole,
            isPhraseEnding,
            isSongEnding);

        return ZipPitchAndRhythm(TokenizePattern(processedPitchPattern, 2), rhythms2);
    }

    private int ScoreXmlPatternForJiangNan(
        string pitchPattern,
        string rhythmPattern,
        EmotionType emotion,
        ChordInfo chord,
        TimeSectionRole sectionRole,
        bool isPhraseEnding,
        bool isSongEnding,
        string? previousPitch)
    {
        List<string> pitches = TokenizePattern(pitchPattern, 2).Where(p => p != "00").ToList();
        List<string> rhythms = TokenizePattern(rhythmPattern, 2);
        int score = 0;

        if (pitches.Count >= 4 && pitches.Count <= 8) score += 20;
        if (emotion == EmotionType.Calm && pitches.Count <= 6) score += 12;
        if (emotion == EmotionType.Energetic && pitches.Count >= 6) score += 12;

        if (pitches.Count > 0)
        {
            string first = pitches[0];
            string last = pitches[^1];

            if (IsChordSupportedPitch(first, chord)) score += 8;
            if (IsChordSupportedPitch(last, chord)) score += isPhraseEnding ? 25 : 10;

            if (previousPitch is not null)
            {
                int diff = Math.Abs(PitchToAbsoluteSemitone(first) - PitchToAbsoluteSemitone(previousPitch));
                if (diff <= 2) score += 18;
                else if (diff <= 5) score += 8;
                else if (diff >= 9) score -= 25;
            }
        }

        int largeLeaps = CountLargeLeaps(pitches);
        score -= largeLeaps * 12;

        if (sectionRole == TimeSectionRole.Opening && pitches.Count <= 6) score += 6;
        if (sectionRole == TimeSectionRole.Contrast && pitches.Count >= 6) score += 8;
        if (sectionRole == TimeSectionRole.Closing && IsJiangNanHomeFamily(chord.Degree)) score += 10;
        if (isSongEnding && IsJiangNanHomeFamily(chord.Degree)) score += 30;

        if (rhythms.Contains("4.") || rhythms.Contains("8.")) score += 5;
        if (rhythms.Count >= 8 && emotion == EmotionType.Calm) score -= 8;

        score += GetLegacyRhythmStyleScore(MelodyStyle.JiangNan, emotion, sectionRole, isPhraseEnding, isSongEnding, rhythmPattern, rhythms.Count, TicksPerBar) / 2;
        score += ScoreLegacyChordSpecificMelodyLine(pitches, MelodyStyle.JiangNan, emotion, chord, sectionRole, isPhraseEnding, isSongEnding);

        return score;
    }

    private static int CountLargeLeaps(IReadOnlyList<string> pitches)
    {
        int count = 0;
        for (int i = 1; i < pitches.Count; i++)
        {
            int diff = Math.Abs(PitchToAbsoluteSemitone(pitches[i]) - PitchToAbsoluteSemitone(pitches[i - 1]));
            if (diff >= 7)
                count++;
        }
        return count;
    }

    private static bool IsJiangNanHomeFamily(ChordDegree degree)
    {
        return degree is ChordDegree.I or ChordDegree.vi or ChordDegree.iii;
    }


    /// <summary>
    /// Step 13：對應原 WinForms 的 PickRhythmRowForStyle / GetRhythmStyleScore。
    /// Web 版若 XML rhythm repository 可用，就不再只用 hard-coded fallback rhythm，
    /// 而是依風格、情緒與段落角色挑接近原版的節奏 row。
    /// </summary>
    private string? PickRhythmPatternFromRepository(
        MelodyStyle style,
        EmotionType emotion,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding)
    {
        if (_repository.RhythmPatterns.Count == 0)
            return null;

        List<LegacyRhythmPattern> candidates = PickRhythmCandidatesFromRepository(
            style,
            emotion,
            role,
            isPhraseEnding,
            isSongEnding,
            minLength: 1,
            maxLength: 12,
            maxCount: 1);

        if (candidates.Count == 0)
            return null;

        return candidates[0].Pattern;
    }

    private List<LegacyRhythmPattern> PickRhythmCandidatesFromRepository(
        MelodyStyle style,
        EmotionType emotion,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding,
        int minLength,
        int maxLength,
        int maxCount)
    {
        List<LegacyRhythmPattern> source = _repository.RhythmPatterns
            .Where(r =>
                r.TotalTick == TicksPerBar &&
                r.Length >= minLength &&
                r.Length <= maxLength &&
                r.Pattern.Length >= r.Length * 2)
            .ToList();

        if (source.Count == 0)
            return new List<LegacyRhythmPattern>();

        List<LegacyRhythmPattern> selected = new(capacity: Math.Min(maxCount, source.Count));
        HashSet<string> seen = new(StringComparer.Ordinal);
        int attempts = Math.Max(maxCount * 4, 12);

        for (int attempt = 0; attempt < attempts && selected.Count < maxCount; attempt++)
        {
            int bestScore = int.MinValue;
            List<LegacyRhythmPattern> bestRows = new();

            foreach (LegacyRhythmPattern rhythm in source)
            {
                int score = GetLegacyRhythmStyleScore(
                    style,
                    emotion,
                    role,
                    isPhraseEnding,
                    isSongEnding,
                    rhythm.Pattern,
                    rhythm.Length,
                    rhythm.TotalTick);

                score += Random.Shared.Next(0, 6);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRows.Clear();
                    bestRows.Add(rhythm);
                }
                else if (score == bestScore)
                {
                    bestRows.Add(rhythm);
                }
            }

            if (bestRows.Count == 0)
                continue;

            LegacyRhythmPattern chosen = bestRows[Random.Shared.Next(bestRows.Count)];
            if (seen.Add(chosen.Pattern))
                selected.Add(chosen);
        }

        if (selected.Count > 0)
            return selected;

        return [source[Random.Shared.Next(source.Count)]];
    }

    private static string PickRhythmPattern(MelodyStyle style, EmotionType emotion, TimeSectionRole role, bool isPhraseEnding, bool isSongEnding)
    {
        if (isSongEnding)
            return "04040404";

        if (style == MelodyStyle.Jazz)
        {
            string[] jazz = ["0808080808080808", "0408080408", "080804080408", "4.080808"];
            return jazz[Random.Shared.Next(jazz.Length)];
        }

        if (style == MelodyStyle.JPop || emotion == EmotionType.Energetic)
        {
            string[] dense = ["0808080808080808", "08080808080408", "0408080808", "080804080804"];
            return dense[Random.Shared.Next(dense.Length)];
        }

        if (emotion == EmotionType.Calm || isPhraseEnding)
        {
            string[] calm = ["04040404", "4.080808", "0808080802", "02080408"];
            return calm[Random.Shared.Next(calm.Length)];
        }

        string[] normal = ["04040404", "0808080808080808", "0404080804", "0808080802", "0408080408"];
        return normal[Random.Shared.Next(normal.Length)];
    }

    private static List<NoteChoice> BuildPitchPool(
        MelodyStyle style,
        EmotionType emotion,
        ModeType mode,
        int keyRoot,
        bool minorKey,
        ChordInfo chord,
        TimeSectionRole sectionRole,
        double progress)
    {
        int minOctave = style == MelodyStyle.JPop || emotion == EmotionType.Bright ? 4 : 3;
        int maxOctave = style == MelodyStyle.JPop || emotion == EmotionType.Energetic ? 6 : 5;

        if (emotion == EmotionType.Calm || emotion == EmotionType.Sad)
            maxOctave = Math.Min(maxOctave, 5);

        int[] scale = GetScaleIntervals(style, mode, minorKey, emotion);
        HashSet<int> chordTones = GetChordTonePitchClasses(chord);
        List<NoteChoice> pool = new();

        for (int octave = minOctave; octave <= maxOctave; octave++)
        {
            foreach (int interval in scale)
            {
                int pc = NormalizeSemitone(keyRoot + interval);
                int abs = octave * 12 + pc;
                string pitch = PitchFromAbsoluteSemitone(abs);
                pool.Add(new NoteChoice(pitch, abs, chordTones.Contains(pc)));
            }
        }

        if (style == MelodyStyle.Jazz || emotion == EmotionType.Tense)
        {
            foreach (int approach in new[] { chord.RootSemitone - 1, chord.RootSemitone + 1, chord.RootSemitone + 6 })
            {
                int pc = NormalizeSemitone(approach);
                for (int octave = 4; octave <= 5; octave++)
                    pool.Add(new NoteChoice(PitchFromAbsoluteSemitone(octave * 12 + pc), octave * 12 + pc, false));
            }
        }

        if (sectionRole == TimeSectionRole.Contrast || progress > 0.55)
        {
            pool = pool
                .Where(n => GetOctave(n.Pitch) >= 4)
                .Concat(pool.Where(n => n.IsChordTone))
                .ToList();
        }

        return pool.Count == 0 ? [new NoteChoice(PitchFromSemitone(keyRoot, 4), 4 * 12 + keyRoot, true)] : pool;
    }

    private static List<string> GeneratePitchLineFromPool(
        List<NoteChoice> pool,
        int noteCount,
        ChordInfo chord,
        EmotionType emotion,
        TimeSectionRole role,
        bool isPhraseEnding,
        bool isSongEnding,
        string? previousPitch)
    {
        List<string> result = new(capacity: noteCount);
        int? previousSemitone = previousPitch is null ? null : PitchToAbsoluteSemitone(previousPitch);
        int direction = role == TimeSectionRole.Contrast || emotion == EmotionType.Bright || emotion == EmotionType.Energetic ? 1 : -1;

        for (int i = 0; i < noteCount; i++)
        {
            bool strongBeat = i == 0 || i == noteCount / 2 || i == noteCount - 1;
            bool needChordTone = strongBeat || isPhraseEnding || Random.Shared.NextDouble() < 0.45;

            IEnumerable<NoteChoice> candidates = pool;
            if (needChordTone)
                candidates = candidates.Where(n => n.IsChordTone).DefaultIfEmpty(pool[Random.Shared.Next(pool.Count)]);

            if (previousSemitone is not null)
            {
                int maxLeap = emotion == EmotionType.Energetic ? 9 : 5;
                candidates = candidates
                    .Where(n => Math.Abs(n.Semitone - previousSemitone.Value) <= maxLeap)
                    .DefaultIfEmpty(pool.OrderBy(n => Math.Abs(n.Semitone - previousSemitone.Value)).First());
            }

            List<NoteChoice> ranked = candidates
                .OrderByDescending(n => ScorePitchCandidate(n, chord, previousSemitone, direction, strongBeat, emotion))
                .Take(8)
                .ToList();

            NoteChoice chosen = ranked[Random.Shared.Next(ranked.Count)];
            result.Add(chosen.Pitch);
            previousSemitone = chosen.Semitone;

            if (i > 0 && i % 3 == 0)
                direction *= -1;
        }

        if (isSongEnding && result.Count > 0)
            result[^1] = PitchFromSemitone(chord.RootSemitone, 4);
        else if (isPhraseEnding && result.Count > 0)
            result[^1] = PitchFromSemitone(chord.RootSemitone, 4);

        return result;
    }

    private static int ScorePitchCandidate(NoteChoice candidate, ChordInfo chord, int? previousSemitone, int direction, bool strongBeat, EmotionType emotion)
    {
        int score = 0;
        if (candidate.IsChordTone) score += strongBeat ? 30 : 12;
        if (NormalizeSemitone(candidate.Semitone) == chord.RootSemitone) score += strongBeat ? 15 : 4;

        if (previousSemitone is not null)
        {
            int diff = candidate.Semitone - previousSemitone.Value;
            int abs = Math.Abs(diff);
            if (abs <= 2) score += 18;
            else if (abs <= 5) score += 8;
            else score -= 15;

            if (Math.Sign(diff) == direction) score += 4;
        }

        int octave = candidate.Semitone / 12;
        if (emotion == EmotionType.Calm && octave <= 4) score += 5;
        if (emotion == EmotionType.Bright && octave >= 5) score += 6;
        if (emotion == EmotionType.Sad && octave <= 4) score += 7;

        return score + Random.Shared.Next(0, 8);
    }


    private static List<string> ApplyLegacyMainMelodyPostProcessing(
        IReadOnlyList<string> mainBars,
        string chordText,
        MelodyStyle style,
        EmotionType emotion,
        int keyRoot,
        bool minorKey)
    {
        if (mainBars.Count == 0)
            return new List<string>();

        List<string> processed = new(capacity: mainBars.Count);
        string previousLastPitch = "00";

        for (int bar = 0; bar < mainBars.Count; bar++)
        {
            bool phraseEnding = (bar + 1) % 4 == 0 || bar == mainBars.Count - 1;
            bool songEnding = bar == mainBars.Count - 1;
            ChordInfo chord = GetChordInfo(GetChordCodeAtBar(chordText, bar), keyRoot, minorKey);
            List<(string Pitch, string Rhythm)> events = ParseYNoteEvents(mainBars[bar]);

            if (events.Count == 0)
                events.Add((PitchFromSemitone(keyRoot, 4), "04"));

            events = NormalizeBarDurationEvents(events, keyRoot, minorKey);

            for (int i = 0; i < events.Count; i++)
            {
                string pitch = events[i].Pitch;
                if (pitch == "00")
                    continue;

                if (style == MelodyStyle.JiangNan)
                    pitch = ForcePitchToJiangNanScale(pitch, keyRoot, minOctave: 3, maxOctave: 5);
                else
                    pitch = ClampPitch(pitch, 3, emotion == EmotionType.Energetic ? 6 : 5);

                // 跨小節平滑：原 WinForms 的 post-process 會記住 recentMainMelodyPitches，避免下一小節突然大跳。
                if (i == 0 && previousLastPitch != "00")
                    pitch = SmoothBarEntrancePitch(pitch, previousLastPitch, chord, style, keyRoot);

                events[i] = (pitch, events[i].Rhythm);
            }

            events = RepairRepeatedAndLargeLeapEvents(events, chord, style, keyRoot);
            events = ApplyNonJiangNanStyleColorEvents(events, chord, style, keyRoot, emotion);

            int lastIndex = events.FindLastIndex(e => e.Pitch != "00");
            if (lastIndex >= 0)
            {
                string lastPitch = events[lastIndex].Pitch;

                if (songEnding)
                {
                    lastPitch = style == MelodyStyle.JiangNan
                        ? ForcePitchToJiangNanScale(PitchFromSemitone(keyRoot, 4), keyRoot, minOctave: 3, maxOctave: 5)
                        : PitchFromSemitone(chord.RootSemitone, minorKey ? 4 : 5);
                }
                else if (phraseEnding)
                {
                    lastPitch = style == MelodyStyle.JiangNan
                        ? ResolveJiangNanThemeEndingPitch(chord, keyRoot, minorKey, false)
                        : MoveLastPitchTowardChordTone(lastPitch, chord, style, keyRoot);
                }
                else if (!IsChordSupportedPitch(lastPitch, chord) && Random.Shared.NextDouble() < 0.35)
                {
                    lastPitch = MoveLastPitchTowardChordTone(lastPitch, chord, style, keyRoot);
                }

                events[lastIndex] = (lastPitch, events[lastIndex].Rhythm);
            }

            string barText = string.Concat(events.Select(e => e.Pitch + e.Rhythm));
            barText = NormalizeSingleBarYNote(barText);
            processed.Add(barText);
            previousLastPitch = GetLastPitch(barText) ?? previousLastPitch;
        }

        return processed;
    }

    private static List<(string Pitch, string Rhythm)> ApplyNonJiangNanStyleColorEvents(
        List<(string Pitch, string Rhythm)> events,
        ChordInfo chord,
        MelodyStyle style,
        int keyRoot,
        EmotionType emotion)
    {
        if (style == MelodyStyle.JiangNan || events.Count == 0)
            return events;

        List<int> realIndexes = events
            .Select((e, i) => (e.Pitch, Index: i))
            .Where(x => x.Pitch != "00")
            .Select(x => x.Index)
            .ToList();

        if (realIndexes.Count == 0)
            return events;

        bool hasNonPentatonicColor = realIndexes.Any(i => !IsRelativeMajorPentatonicPitchByKey(events[i].Pitch, keyRoot));
        bool needsColor = !hasNonPentatonicColor || style == MelodyStyle.Jazz;
        if (!needsColor)
            return events;

        int chosenIndex = realIndexes.Count >= 3
            ? realIndexes[Math.Min(realIndexes.Count - 2, Math.Max(1, realIndexes.Count / 2))]
            : realIndexes[0];

        string source = events[chosenIndex].Pitch;
        int[] targetIntervals = style switch
        {
            MelodyStyle.Jazz => [10, 3, 6, 1, 11],   // b7, b3, tritone / approach tone, leading tone
            MelodyStyle.JPop => [11, 5, 2, 7],       // leading tone and IV color make it less pentatonic
            _ => [5, 11, 4, 7]                       // Pop: IV / leading tone color, then chord-safe fallback
        };

        string colored = MovePitchToNearestRelativeClass(source, keyRoot, targetIntervals, minOctave: 3, maxOctave: style == MelodyStyle.JPop || emotion == EmotionType.Energetic ? 6 : 5);

        if (style != MelodyStyle.Jazz && !IsChordSupportedPitch(colored, chord))
        {
            // Pop / JPop 不要硬塞太刺耳的色彩音；如果很不合和弦，就選最近和弦音。
            string chordSafe = MoveLastPitchTowardChordTone(colored, chord, style, keyRoot);
            if (!IsRelativeMajorPentatonicPitchByKey(chordSafe, keyRoot) || Random.Shared.NextDouble() < 0.45)
                colored = chordSafe;
        }

        if (colored != source)
            events[chosenIndex] = (colored, events[chosenIndex].Rhythm);

        return events;
    }

    private static bool IsRelativeMajorPentatonicPitchByKey(string pitch, int keyRoot)
    {
        int rel = NormalizeSemitone(PitchToAbsoluteSemitone(pitch) - keyRoot);
        return rel is 0 or 2 or 4 or 7 or 9;
    }

    private static string MovePitchToNearestRelativeClass(string sourcePitch, int keyRoot, IReadOnlyList<int> targetIntervals, int minOctave, int maxOctave)
    {
        if (sourcePitch == "00")
            return sourcePitch;

        int sourceAbs = PitchToAbsoluteSemitone(sourcePitch);
        string best = sourcePitch;
        int bestScore = int.MaxValue;

        for (int octave = minOctave; octave <= maxOctave; octave++)
        {
            foreach (int interval in targetIntervals)
            {
                int abs = octave * 12 + NormalizeSemitone(keyRoot + interval);
                int score = Math.Abs(abs - sourceAbs);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = PitchFromAbsoluteSemitone(abs);
                }
            }
        }

        return best;
    }

    private static List<(string Pitch, string Rhythm)> NormalizeBarDurationEvents(
        List<(string Pitch, string Rhythm)> events,
        int keyRoot,
        bool minorKey)
    {
        List<(string Pitch, string Rhythm)> result = new();
        int tickSum = 0;

        foreach ((string pitch, string rhythm) in events)
        {
            if (tickSum >= TicksPerBar)
                break;

            int duration = Math.Max(0, GetDuration(rhythm));
            if (duration <= 0)
                duration = TicksPerBeat;

            if (tickSum + duration > TicksPerBar)
                duration = TicksPerBar - tickSum;

            string normalizedRhythm = DurationToRhythmToken(duration);
            result.Add((string.IsNullOrWhiteSpace(pitch) ? PitchFromSemitone(keyRoot, minorKey ? 4 : 5) : pitch, normalizedRhythm));
            tickSum += GetDuration(normalizedRhythm);
        }

        while (tickSum < TicksPerBar)
        {
            int remain = TicksPerBar - tickSum;
            string rhythm = DurationToRhythmToken(remain);
            result.Add(("00", rhythm));
            tickSum += GetDuration(rhythm);
        }

        return result;
    }

    private static string SmoothBarEntrancePitch(string pitch, string previousLastPitch, ChordInfo chord, MelodyStyle style, int keyRoot)
    {
        if (pitch == "00" || previousLastPitch == "00")
            return pitch;

        int diff = PitchToAbsoluteSemitone(pitch) - PitchToAbsoluteSemitone(previousLastPitch);
        if (Math.Abs(diff) <= 7)
            return pitch;

        string candidate = pitch;
        int direction = diff > 0 ? -1 : 1;
        for (int i = 0; i < 4 && Math.Abs(PitchToAbsoluteSemitone(candidate) - PitchToAbsoluteSemitone(previousLastPitch)) > 7; i++)
        {
            candidate = style == MelodyStyle.JiangNan
                ? MoveJiangNanPitchByScaleSteps(candidate, direction, keyRoot, minOctave: 3, maxOctave: 5)
                : PitchFromAbsoluteSemitone(PitchToAbsoluteSemitone(candidate) + direction * 2);
        }

        return style == MelodyStyle.JiangNan
            ? MovePitchTowardChordToneInJiangNanScale(candidate, chord, keyRoot)
            : MoveLastPitchTowardChordTone(candidate, chord, style, keyRoot);
    }

    private static List<(string Pitch, string Rhythm)> RepairRepeatedAndLargeLeapEvents(
        List<(string Pitch, string Rhythm)> events,
        ChordInfo chord,
        MelodyStyle style,
        int keyRoot)
    {
        List<(string Pitch, string Rhythm)> result = events.ToList();
        string previousPitch = "00";
        string previousPreviousPitch = "00";

        for (int i = 0; i < result.Count; i++)
        {
            string pitch = result[i].Pitch;
            if (pitch == "00")
                continue;

            if (previousPitch != "00")
            {
                int diff = Math.Abs(PitchToAbsoluteSemitone(pitch) - PitchToAbsoluteSemitone(previousPitch));
                if (diff >= (style == MelodyStyle.JiangNan ? 8 : 11))
                    pitch = SmoothBarEntrancePitch(pitch, previousPitch, chord, style, keyRoot);
            }

            if (previousPreviousPitch == pitch && previousPitch == pitch && style == MelodyStyle.JiangNan)
                pitch = MoveJiangNanPitchByScaleSteps(pitch, Random.Shared.Next(0, 2) == 0 ? 1 : -1, keyRoot, minOctave: 3, maxOctave: 5);

            result[i] = (pitch, result[i].Rhythm);
            previousPreviousPitch = previousPitch;
            previousPitch = pitch;
        }

        return result;
    }

    private static string PostProcessMainMelodyBar(string yNote, ChordInfo chord, int keyRoot, bool minorKey, bool isSongEnding)
    {
        List<(string Pitch, string Rhythm)> events = ParseYNoteEvents(yNote);
        if (events.Count == 0)
            events.Add((PitchFromSemitone(keyRoot, 4), "04"));

        int tickSum = events.Sum(e => GetDuration(e.Rhythm));
        while (tickSum < TicksPerBar)
        {
            int remain = TicksPerBar - tickSum;
            string rhythm = DurationToRhythmToken(remain);
            events.Add(("00", rhythm));
            tickSum += GetDuration(rhythm);
        }

        if (isSongEnding)
        {
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Pitch != "00")
                {
                    events[i] = (PitchFromSemitone(keyRoot, minorKey ? 4 : 5), events[i].Rhythm);
                    break;
                }
            }
        }

        return string.Concat(events.Select(e => e.Pitch + e.Rhythm));
    }

    private static string GenerateSubMelodyBar(
        MelodyStyle style,
        EmotionType emotion,
        string? densityText,
        ChordInfo chord,
        int keyRoot,
        bool minorKey,
        int barIndex,
        int totalBars)
    {
        // 保留沒有主旋律上下文時的 fallback。Step 12 的主要副旋律路徑會改走
        // GenerateSubMelodyBarFromMainBar()，它會根據主旋律實際音高產生 counter melody。
        string density = NormalizeText(densityText).ToLowerInvariant();
        List<string> chordPitches = GetChordPitches(chord, octave: 3);

        if (density == "low" || emotion == EmotionType.Calm)
        {
            string root = chordPitches[0];
            string fifth = chordPitches[Math.Min(2, chordPitches.Count - 1)];
            return root + "02" + fifth + "02";
        }

        if (density == "high" || style == MelodyStyle.Jazz || emotion == EmotionType.Energetic)
        {
            string p0 = chordPitches[0];
            string p1 = chordPitches[Math.Min(1, chordPitches.Count - 1)];
            string p2 = chordPitches[Math.Min(2, chordPitches.Count - 1)];
            string p3 = chordPitches.Count >= 4 ? chordPitches[3] : p1;
            return p0 + "08" + p1 + "08" + p2 + "08" + p1 + "08" + p0 + "08" + p1 + "08" + p3 + "08" + p2 + "08";
        }

        return chordPitches[0] + "04" + chordPitches[Math.Min(1, chordPitches.Count - 1)] + "04" + chordPitches[Math.Min(2, chordPitches.Count - 1)] + "04" + chordPitches[Math.Min(1, chordPitches.Count - 1)] + "04";
    }

    /// <summary>
    /// Step 12：移植原 WinForms 的 counter melody 產生邏輯。
    /// 舊 Web 版副旋律只用 chord arpeggio；這裡改成逐音讀主旋律，
    /// 依垂直音程、前一個副旋律、平行五八度、情緒與江南五聲限制選下方對旋律。
    /// </summary>
    private static string GenerateSubMelodyBarFromMainBar(
        MelodyStyle style,
        EmotionType emotion,
        string? densityText,
        string mainBarYNote,
        ChordInfo chord,
        int keyRoot,
        bool minorKey,
        int barIndex,
        int totalBars,
        ref string previousMainPitch,
        ref string previousSubPitch)
    {
        List<(string Pitch, string Rhythm)> events = ParseYNoteEvents(NormalizeSingleBarYNote(mainBarYNote));
        if (events.Count == 0)
            events.Add((PitchFromSemitone(keyRoot, minorKey ? 4 : 5), "04"));

        string density = NormalizeText(densityText);
        string densityKey = density.ToLowerInvariant();
        int noteCount = events.Count;
        string[] subPitches = Enumerable.Repeat("00", noteCount).ToArray();
        string[] mainPitches = new string[noteCount];
        string[] rhythms = new string[noteCount];
        int[] durations = new int[noteCount];
        int[] tickStarts = new int[noteCount];

        bool isSongEnding = barIndex >= totalBars - 1;
        bool isSectionEnding = (barIndex + 1) % 4 == 0 || isSongEnding;
        int generatedSubCount = 0;
        int fallbackIndex = -1;
        int tickInBar = 0;

        string localPreviousMainPitch = previousMainPitch;
        string localPreviousSubPitch = previousSubPitch;

        for (int noteIndex = 0; noteIndex < noteCount; noteIndex++)
        {
            string mainPitch = NormalizePitchToken(events[noteIndex].Pitch);
            string rhythm = NormalizeRhythmToken(events[noteIndex].Rhythm);
            int duration = Math.Max(GetDuration(rhythm), TicksPerBeat / 4);

            mainPitches[noteIndex] = mainPitch;
            rhythms[noteIndex] = rhythm;
            durations[noteIndex] = duration;
            tickStarts[noteIndex] = tickInBar;

            bool isRest = mainPitch == "00";
            bool durationOk = duration >= GetMinimumSubMelodyDuration(style, emotion);
            bool mutedForEnding = ShouldMuteSubMelodyForTimeSectionEnding(isSectionEnding, isSongEnding, noteIndex, noteCount);
            bool isSubPosition = ShouldGenerateSubAtPosition(style, emotion, densityKey, tickInBar, noteIndex, noteCount, isSongEnding);

            if (mutedForEnding && !ShouldKeepCounterMelodyDuringEnding(noteIndex, noteCount))
                isSubPosition = false;

            if (!isRest && fallbackIndex < 0 && duration >= 120 && (tickInBar <= TicksPerBar / 2 || noteIndex == 0))
                fallbackIndex = noteIndex;

            string subPitch = "00";

            if (!isRest && durationOk && isSubPosition)
            {
                subPitch = SelectBestCounterPitchBelowMain(
                    style,
                    emotion,
                    mainPitch,
                    chord,
                    keyRoot,
                    localPreviousSubPitch,
                    localPreviousMainPitch);
            }

            if (subPitch == "00" && !isRest && durationOk && localPreviousSubPitch != "00" &&
                ShouldSustainCounterMelodyThroughGap(style, emotion, densityKey, tickInBar, noteIndex, noteCount, duration, isSongEnding))
            {
                if (IsUsableCounterPitchUnderMain(localPreviousSubPitch, mainPitch, style, emotion))
                    subPitch = localPreviousSubPitch;
                else
                    subPitch = SelectBestCounterPitchBelowMain(style, emotion, mainPitch, chord, keyRoot, localPreviousSubPitch, localPreviousMainPitch);
            }

            if (subPitch != "00")
            {
                subPitches[noteIndex] = subPitch;
                localPreviousSubPitch = subPitch;
                generatedSubCount++;
            }

            if (mainPitch != "00")
                localPreviousMainPitch = mainPitch;

            tickInBar += duration;
        }

        if (generatedSubCount == 0 && fallbackIndex >= 0)
        {
            string fallbackPitch = SelectBestCounterPitchBelowMain(
                style,
                emotion,
                mainPitches[fallbackIndex],
                chord,
                keyRoot,
                previousSubPitch,
                previousMainPitch);

            if (fallbackPitch != "00")
            {
                subPitches[fallbackIndex] = fallbackPitch;
                generatedSubCount++;
            }
        }

        int targetSubCount = GetTargetCounterMelodyNotesPerBar(style, densityKey, noteCount);
        for (int noteIndex = 0; noteIndex < noteCount && generatedSubCount < targetSubCount; noteIndex++)
        {
            if (subPitches[noteIndex] != "00" || mainPitches[noteIndex] == "00" || durations[noteIndex] < 120)
                continue;

            if (!IsGoodCounterMelodyFillPosition(style, densityKey, tickStarts[noteIndex], noteIndex, noteCount, isSongEnding))
                continue;

            string fillPitch = SelectBestCounterPitchBelowMain(
                style,
                emotion,
                mainPitches[noteIndex],
                chord,
                keyRoot,
                localPreviousSubPitch,
                localPreviousMainPitch);

            if (fillPitch != "00")
            {
                subPitches[noteIndex] = fillPitch;
                localPreviousSubPitch = fillPitch;
                generatedSubCount++;
            }
        }

        if (style == MelodyStyle.JiangNan)
        {
            StrengthenJiangNanSubMelodyLine(
                subPitches,
                mainPitches,
                chord,
                durations,
                tickStarts,
                keyRoot,
                emotion,
                densityKey,
                isSongEnding,
                ref localPreviousMainPitch,
                ref localPreviousSubPitch);
        }

        for (int i = 0; i < subPitches.Length; i++)
        {
            if (subPitches[i] != "00")
                previousSubPitch = subPitches[i];

            if (mainPitches[i] != "00")
                previousMainPitch = mainPitches[i];
        }

        StringBuilder sb = new();
        for (int i = 0; i < noteCount; i++)
            sb.Append(subPitches[i]).Append(rhythms[i]);

        return NormalizeSingleBarYNote(sb.ToString());
    }

    private static List<string> GetChordPitches(ChordInfo chord, int octave)
    {
        int[] intervals = GetChordIntervals(chord.Degree, chord.MinorKeyContext);
        return intervals.Select(i => PitchFromAbsoluteSemitone(octave * 12 + NormalizeSemitone(chord.RootSemitone + i))).ToList();
    }

    private static List<TimeSectionInfo> BuildTimeSections(int barCount)
    {
        if (barCount <= 4)
            return [new TimeSectionInfo(TimeSectionRole.Opening, 0, barCount - 1)];

        int q1 = Math.Max(0, (int)Math.Floor(barCount * 0.25) - 1);
        int q2 = Math.Max(q1 + 1, (int)Math.Floor(barCount * 0.55) - 1);
        int q3 = Math.Max(q2 + 1, (int)Math.Floor(barCount * 0.78) - 1);

        return
        [
            new TimeSectionInfo(TimeSectionRole.Opening, 0, q1),
            new TimeSectionInfo(TimeSectionRole.Development, q1 + 1, q2),
            new TimeSectionInfo(TimeSectionRole.Contrast, q2 + 1, q3),
            new TimeSectionInfo(TimeSectionRole.Closing, q3 + 1, barCount - 1)
        ];
    }

    private static TimeSectionInfo GetSectionForBar(IEnumerable<TimeSectionInfo> sections, int bar)
    {
        return sections.FirstOrDefault(s => s.ContainsBar(bar)) ?? new TimeSectionInfo(TimeSectionRole.Opening, 0, int.MaxValue);
    }

    private static int[] GetScaleIntervals(MelodyStyle style, ModeType mode, bool minorKey, EmotionType emotion)
    {
        if (style == MelodyStyle.JiangNan || mode == ModeType.Pentatonic)
            return [0, 2, 4, 7, 9];

        if (mode == ModeType.Dorian)
            return [0, 2, 3, 5, 7, 9, 10];

        if (mode == ModeType.Mixolydian)
            return [0, 2, 4, 5, 7, 9, 10];

        if (mode == ModeType.Minor || minorKey || emotion == EmotionType.Sad)
            return [0, 2, 3, 5, 7, 8, 10];

        return [0, 2, 4, 5, 7, 9, 11];
    }

    private static bool IsMinorKey(string? key, ModeType mode)
    {
        string value = NormalizeText(key).ToLowerInvariant();
        return mode == ModeType.Minor || value.Contains("minor") || value.Contains("小調");
    }

    private static int GetSelectedKeyRootSemitone(string? key)
    {
        string value = NormalizeText(key)
            .Replace("minor", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("小調", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return value switch
        {
            "C" => 0,
            "C#" or "Db" => 1,
            "D" => 2,
            "D#" or "Eb" => 3,
            "E" => 4,
            "F" => 5,
            "F#" or "Gb" => 6,
            "G" => 7,
            "G#" or "Ab" => 8,
            "A" => 9,
            "A#" or "Bb" => 10,
            "B" or "Cb" => 11,
            _ => 0
        };
    }

    private static ChordInfo GetChordInfo(string chordCode)
    {
        // Legacy/default path: keep C-major-relative semantics for progression scoring,
        // where only Degree is relevant. Generation paths should use the overload
        // with keyRoot/minorKey so XC/XD/XF... become scale degrees in the selected key.
        return GetChordInfo(chordCode, keyRoot: 0, minorKey: false);
    }

    private static ChordInfo GetChordInfo(string chordCode, int keyRoot, bool minorKey)
    {
        if (string.IsNullOrWhiteSpace(chordCode) || chordCode.Length < 2)
            return new ChordInfo("XC501", ChordDegree.I, NormalizeSemitone(keyRoot), PitchClassToDisplayName(keyRoot), minorKey);

        ChordDegree degree = GetChordDegree(chordCode);
        int root = degree == ChordDegree.Unknown
            ? NormalizeSemitone(keyRoot + LetterCodeToSemitone(chordCode[1]))
            : GetDegreeRootSemitone(degree, keyRoot, minorKey);

        return new ChordInfo(
            chordCode,
            degree,
            NormalizeSemitone(root),
            GetChordDisplayName(chordCode, keyRoot, minorKey),
            minorKey);
    }

    private static ChordDegree GetChordDegree(string chordCode)
    {
        if (string.IsNullOrWhiteSpace(chordCode) || chordCode.Length < 2)
            return ChordDegree.Unknown;

        char root = chordCode[1];
        char quality = chordCode.Length >= 3 ? chordCode[2] : '5';

        return root switch
        {
            'C' => ChordDegree.I,
            'D' => ChordDegree.ii,
            'E' => ChordDegree.iii,
            'F' => ChordDegree.IV,
            'G' => quality == '7' ? ChordDegree.V7 : ChordDegree.V,
            'A' => ChordDegree.vi,
            'B' => ChordDegree.viiDim,
            'H' => ChordDegree.bIII,
            'L' => ChordDegree.bVI,
            'K' => ChordDegree.bVII,
            '2' => ChordDegree.II,
            '3' => ChordDegree.III7,
            '6' => ChordDegree.VI7,
            'm' => ChordDegree.iv,
            _ => ChordDegree.Unknown
        };
    }

    private static string GetChordDisplayName(string chordCode)
    {
        return GetChordDisplayName(chordCode, keyRoot: 0, minorKey: false);
    }

    private static string GetChordDisplayName(string chordCode, int keyRoot, bool minorKey)
    {
        if (string.IsNullOrWhiteSpace(chordCode) || chordCode.Length < 2)
            return chordCode;

        ChordDegree degree = GetChordDegree(chordCode);
        if (degree == ChordDegree.Unknown)
            return chordCode;

        string rootName = PitchClassToDisplayName(GetDegreeRootSemitone(degree, keyRoot, minorKey));

        if (degree == ChordDegree.viiDim)
            return rootName + "dim";

        if (degree is ChordDegree.V7 or ChordDegree.III7 or ChordDegree.VI7 or ChordDegree.II)
            return rootName + "7";

        return IsMinorTriadDegree(degree, minorKey) ? rootName + "m" : rootName;
    }

    private static int LetterCodeToSemitone(char root)
    {
        return root switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            'H' => 3,
            'L' => 8,
            'K' => 10,
            '2' => 2,
            '3' => 4,
            '6' => 9,
            'm' => 5,
            _ => 0
        };
    }

    private static string PitchClassToDisplayName(int pitchClass)
    {
        return NormalizeSemitone(pitchClass) switch
        {
            0 => "C",
            1 => "C#",
            2 => "D",
            3 => "Eb",
            4 => "E",
            5 => "F",
            6 => "F#",
            7 => "G",
            8 => "Ab",
            9 => "A",
            10 => "Bb",
            11 => "B",
            _ => "C"
        };
    }

    private static HashSet<int> GetChordTonePitchClasses(ChordInfo chord)
    {
        int[] intervals = GetChordIntervals(chord.Degree, chord.MinorKeyContext);
        return intervals.Select(i => NormalizeSemitone(chord.RootSemitone + i)).ToHashSet();
    }

    private static bool IsChordSupportedPitch(string pitch, ChordInfo chord)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch == "00")
            return false;

        int pc = NormalizeSemitone(PitchToAbsoluteSemitone(pitch));
        HashSet<int> chordTones = GetChordTonePitchClasses(chord);
        return chordTones.Contains(pc);
    }

    private static string TransposePitchFromC(string pitch, int keyRoot, bool minorKey, ModeType mode)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch == "00")
            return "00";

        int abs = PitchToAbsoluteSemitone(pitch);
        int transposed = abs + keyRoot;

        if (minorKey || mode == ModeType.Minor)
        {
            // 舊資料多以 C/A 五聲色彩為中心。小調時稍微下修 E/A 類音，保留古風感。
            int pc = NormalizeSemitone(abs);
            if (pc == 4) transposed -= 1;
            if (pc == 9) transposed -= 1;
        }

        return PitchFromAbsoluteSemitone(transposed);
    }

    private static string ZipPitchAndRhythm(IReadOnlyList<string> pitches, IReadOnlyList<string> rhythms)
    {
        int count = Math.Min(pitches.Count, rhythms.Count);
        if (count <= 0)
            return "0001";

        StringBuilder sb = new();
        for (int i = 0; i < count; i++)
        {
            string pitch = NormalizePitchToken(pitches[i]);
            string rhythm = NormalizeRhythmToken(rhythms[i]);
            sb.Append(pitch).Append(rhythm);
        }

        return sb.ToString();
    }

    private static string NormalizePitchToken(string? pitch)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch == "00")
            return "00";

        pitch = pitch.Trim();
        if (pitch.Length == 1)
            return pitch.ToUpperInvariant() + "4";

        return pitch[..2];
    }

    private static string NormalizeRhythmToken(string? rhythm)
    {
        if (string.IsNullOrWhiteSpace(rhythm))
            return "04";

        rhythm = rhythm.Trim();
        return rhythm.Length >= 2 ? rhythm[..2] : rhythm + "0";
    }

    private static List<string> TokenizePattern(string pattern, int tokenSize)
    {
        List<string> tokens = new();
        if (string.IsNullOrEmpty(pattern) || tokenSize <= 0)
            return tokens;

        for (int i = 0; i + tokenSize <= pattern.Length; i += tokenSize)
            tokens.Add(pattern.Substring(i, tokenSize));

        return tokens;
    }

    private static List<(string Pitch, string Rhythm)> ParseYNoteEvents(string yNote)
    {
        List<(string Pitch, string Rhythm)> events = new();
        if (string.IsNullOrWhiteSpace(yNote))
            return events;

        for (int i = 0; i + 4 <= yNote.Length; i += 4)
            events.Add((yNote.Substring(i, 2), yNote.Substring(i + 2, 2)));

        return events;
    }

    private static string? GetLastPitch(string yNote)
    {
        foreach ((string pitch, _) in ParseYNoteEvents(yNote).AsEnumerable().Reverse())
        {
            if (pitch != "00")
                return pitch;
        }

        return null;
    }

    private static List<string> ParseSeedPitches(
        string? seedText,
        int keyboardOctave,
        int keyRoot,
        bool minorKey,
        ModeType mode)
    {
        List<string> result = new();
        if (string.IsNullOrWhiteSpace(seedText))
            return result;

        int octave = Math.Clamp(keyboardOctave <= 0 ? 4 : keyboardOctave, 2, 6);
        string normalized = seedText
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(";", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        foreach (string rawToken in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? pitch = ParseSeedPitchToken(rawToken, octave);
            if (pitch is null)
                continue;

            result.Add(ClampPitch(pitch, 3, 6));
            if (result.Count >= 64)
                break;
        }

        if (result.Count == 0 && mode == ModeType.Pentatonic)
        {
            result.Add(PitchFromSemitone(keyRoot, minorKey ? 4 : 5));
            result.Add(PitchFromSemitone(keyRoot + 2, minorKey ? 4 : 5));
            result.Add(PitchFromSemitone(keyRoot + 4, minorKey ? 4 : 5));
        }

        return result;
    }

    private static string? ParseSeedPitchToken(string token, int defaultOctave)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        token = token.Trim();
        if (token.Equals("rest", StringComparison.OrdinalIgnoreCase) || token == "00")
            return "00";

        char letter = token[0];
        int pitchClass = letter switch
        {
            'C' => 0,
            'c' => 1,
            'D' => 2,
            'd' => 3,
            'E' => 4,
            'F' => 5,
            'f' => 6,
            'G' => 7,
            'g' => 8,
            'A' => 9,
            'a' => 10,
            'B' => 11,
            _ => -1
        };

        if (pitchClass < 0)
            return null;

        int octave = defaultOctave;
        string remaining = token.Length > 1 ? token[1..] : string.Empty;

        // 也接受 C#4 / Db4 這類輸入，內部仍轉為原本 YNote 的小寫升記號表示法。
        if (remaining.StartsWith("#", StringComparison.Ordinal))
        {
            pitchClass = NormalizeSemitone(pitchClass + 1);
            remaining = remaining[1..];
        }
        else if (remaining.StartsWith("b", StringComparison.OrdinalIgnoreCase))
        {
            pitchClass = NormalizeSemitone(pitchClass - 1);
            remaining = remaining[1..];
        }

        if (int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedOctave))
            octave = Math.Clamp(parsedOctave, 1, 7);

        return PitchFromSemitone(pitchClass, octave);
    }

    private static string ApplySeedMotifToBar(
        string generatedBar,
        IReadOnlyList<string> seedPitches,
        int barIndex,
        string? seedMode,
        bool isSongEnding)
    {
        if (seedPitches.Count == 0 || isSongEnding)
            return generatedBar;

        List<(string Pitch, string Rhythm)> events = ParseYNoteEvents(generatedBar);
        if (events.Count == 0)
            return generatedBar;

        string mode = NormalizeText(seedMode);
        bool chordSeed = mode.Contains("chord", StringComparison.OrdinalIgnoreCase) || mode.Contains("和弦", StringComparison.OrdinalIgnoreCase);
        bool mixedSeed = mode.Contains("mixed", StringComparison.OrdinalIgnoreCase) || mode.Contains("混合", StringComparison.OrdinalIgnoreCase);

        int maxReplaceCount = chordSeed ? Math.Min(3, events.Count) : Math.Min(seedPitches.Count, events.Count);
        if (mixedSeed)
            maxReplaceCount = Math.Min(Math.Max(2, seedPitches.Count), events.Count);

        int transpose = (barIndex % 4) switch
        {
            0 => 0,
            1 => 2,
            2 => -2,
            _ => 0
        };

        int replaced = 0;
        for (int i = 0; i < events.Count && replaced < maxReplaceCount; i++)
        {
            if (events[i].Pitch == "00")
                continue;

            string sourcePitch = seedPitches[replaced % seedPitches.Count];
            if (sourcePitch == "00")
            {
                replaced++;
                continue;
            }

            int abs = PitchToAbsoluteSemitone(sourcePitch) + transpose;
            string appliedPitch = ClampPitch(PitchFromAbsoluteSemitone(abs), 3, 6);
            events[i] = (appliedPitch, events[i].Rhythm);
            replaced++;
        }

        return string.Concat(events.Select(e => e.Pitch + e.Rhythm));
    }

    private string AnalyzeGeneratedMusic(
        string mainYNote,
        string subYNote,
        string chordText,
        MelodyStyle style,
        EmotionType emotion,
        int barCount,
        int keyRoot,
        bool minorKey,
        IReadOnlyList<string> seedPitches,
        bool seedInputEnabled,
        string? seedMode)
    {
        List<(string Pitch, string Rhythm)> mainEvents = ParseYNoteEvents(mainYNote);
        List<(string Pitch, string Rhythm)> subEvents = ParseYNoteEvents(subYNote);

        int noteCount = mainEvents.Count(e => e.Pitch != "00");
        int restCount = mainEvents.Count(e => e.Pitch == "00");
        int subNoteCount = subEvents.Count(e => e.Pitch != "00");
        int largeLeapCount = CountMainMelodyLargeLeaps(mainYNote);
        int subLargeLeapCount = CountSubMelodyLargeLeaps(subYNote);
        int parallelPerfectCount = CountParallelPerfectIntervals(mainYNote, subYNote);
        int sharpCount = CountStyleColorNotesInYNote(mainYNote, style, keyRoot);
        int harmonyScore = CalculateHarmonyScore(mainYNote, subYNote, style, emotion, keyRoot);

        GetHarmonyScoreTargetRange(
            style,
            emotion,
            out int targetMinScore,
            out int targetMaxScore,
            out int targetCenterScore,
            out int maxTry,
            out int acceptTolerance);

        int rangeDistance = GetScoreRangeDistance(harmonyScore, targetMinScore, targetMaxScore);
        string harmonyStatus = IsScoreInTargetRange(harmonyScore, targetMinScore, targetMaxScore)
            ? "在目標範圍內"
            : IsScoreCloseEnoughToRange(harmonyScore, targetMinScore, targetMaxScore, acceptTolerance)
                ? "接近目標範圍"
                : "偏離目標範圍，需要調整副旋律或主旋律";

        double chordSupportRate = CalculateChordSupportRate(mainYNote, chordText, barCount, keyRoot, minorKey);
        double restRate = mainEvents.Count == 0 ? 0 : restCount / (double)mainEvents.Count;
        double subDensityRate = subEvents.Count == 0 ? 0 : subNoteCount / (double)subEvents.Count;
        int rhythmDiversity = mainEvents.Select(e => e.Rhythm).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int qualityScore = CalculateOverallQualityScore(
            harmonyScore,
            chordSupportRate,
            restRate,
            largeLeapCount,
            subLargeLeapCount,
            parallelPerfectCount,
            sharpCount,
            style,
            emotion);

        string dataStatus = _repository.PitchPatterns.Count > 0 && _repository.RhythmPatterns.Count > 0
            ? $"已載入舊版 XML 素材庫：Pitch={_repository.PitchPatterns.Count}, Rhythm={_repository.RhythmPatterns.Count}"
            : "未載入 XML 素材庫，使用內建 fallback patterns";

        string seedStatus = seedInputEnabled
            ? $"Step 5 Seed Input：已啟用，SeedMode={NormalizeText(seedMode)}, SeedNotes={string.Join(" ", seedPitches.Take(24))}"
            : "Step 5 Seed Input：未啟用";

        List<string> suggestions = BuildQualitySuggestions(
            style,
            emotion,
            harmonyScore,
            targetMinScore,
            targetMaxScore,
            chordSupportRate,
            restRate,
            largeLeapCount,
            subLargeLeapCount,
            parallelPerfectCount,
            sharpCount,
            rhythmDiversity);

        StringBuilder sb = new();
        sb.AppendLine("Step 7：已移植原 WinForms 的 harmony score / analysis report 核心概念到 Web 版。");
        sb.AppendLine(dataStatus);
        sb.AppendLine(seedStatus);
        sb.AppendLine();
        sb.AppendLine("【基本參數】");
        sb.AppendLine($"風格：{style}");
        sb.AppendLine($"情緒：{emotion}");
        sb.AppendLine($"小節數：{barCount}");
        sb.AppendLine();
        sb.AppendLine("【旋律統計】");
        sb.AppendLine($"主旋律事件數：{mainEvents.Count}");
        sb.AppendLine($"主旋律實音數：{noteCount}");
        sb.AppendLine($"副旋律事件數：{subEvents.Count}");
        sb.AppendLine($"副旋律實音數：{subNoteCount}");
        sb.AppendLine($"休止比例：{restRate:P1}");
        sb.AppendLine($"副旋律密度：{subDensityRate:P1}");
        sb.AppendLine($"節奏種類數：{rhythmDiversity}");
        sb.AppendLine();
        sb.AppendLine("【和聲 / 對位品質】");
        sb.AppendLine($"Harmony Score：{harmonyScore}/100（目標 {targetMinScore}-{targetMaxScore}，中心 {targetCenterScore}，狀態：{harmonyStatus}，距離：{rangeDistance}）");
        sb.AppendLine($"Overall Quality Score：{qualityScore}/100（{GetQualityGrade(qualityScore)}）");
        sb.AppendLine($"主旋律大跳次數：{largeLeapCount}");
        sb.AppendLine($"副旋律大跳次數：{subLargeLeapCount}");
        sb.AppendLine($"平行五度 / 八度疑慮：{parallelPerfectCount}");
        sb.AppendLine($"升記號 / 半音色彩數：{sharpCount}");
        sb.AppendLine($"和弦支持率：{chordSupportRate:P1}");
        sb.AppendLine($"原 WinForms target range 參數：maxTry={maxTry}, acceptTolerance={acceptTolerance}");
        sb.AppendLine();
        sb.AppendLine("【風格判斷】");
        sb.AppendLine(GetStyleAnalysisComment(style, emotion, harmonyScore, chordSupportRate, restRate, largeLeapCount, sharpCount));
        sb.AppendLine();
        sb.AppendLine("【建議】");
        foreach (string suggestion in suggestions)
            sb.AppendLine("- " + suggestion);
        sb.AppendLine();
        sb.AppendLine("已移植到 Web 的原 WinForms 概念：CalculateHarmonyScore、GetHarmonyScoreTargetRange、IsScoreInTargetRange、CountParallelPerfectIntervals、CountSubMelodyLargeLeaps、GetStyleCharacterPenalty、AnalyzeGeneratedMusic、GenerateChordProgressionBySongLength、GenerateSongCandidateFast、GenerateSubMelodyBar、Step 12 counter melody 逐音對位、Expert Mode、Update Sub Melody、Keyboard-style Seed Input、Step 10 Melody-first XML Draft + Infer Chord Progression、Step 11 JiangNan functional journey / tonic orbit closing / melody post-processing。");
        sb.AppendLine("桌面即時播放 mciSendString 已改成：MuseScore 匯出 MP3/WAV，交給瀏覽器 audio controls 播放。Step 8 已繼續搬江南 ThemeSeed / A-A'-A-A'' 起句結構；Step 10 已把原 WinForms 江南 XML 草稿與反推和弦流程搬進 Web Core；Step 11 補上江南旅程分數、合段 tonic orbit 與跨小節旋律後處理；Step 12 補上副旋律逐音對位、江南 counter line 強化、重複音修正與平行五八度避開。judge export 已改為 Web 端文字檔輸出。");

        return sb.ToString();
    }

    private static int CalculateHarmonyScore(string mainYNote, string subYNote, MelodyStyle style, EmotionType emotion, int keyRoot = 0)
    {
        int score = 100;

        int parallelCount = CountParallelPerfectIntervals(mainYNote, subYNote);
        int subLargeLeapCount = CountSubMelodyLargeLeaps(subYNote);
        int subNoteCount = CountValidNotes(subYNote);

        int parallelPenalty = 35;
        int largeLeapPenalty = 8;

        if (style == MelodyStyle.JPop)
            largeLeapPenalty = 4;
        else if (style == MelodyStyle.Jazz)
            largeLeapPenalty = 5;
        else if (style == MelodyStyle.Pop)
            largeLeapPenalty = 7;
        else if (style == MelodyStyle.JiangNan)
            largeLeapPenalty = 10;

        if (emotion == EmotionType.Calm || emotion == EmotionType.Sad)
            largeLeapPenalty += 3;
        else if (emotion == EmotionType.Energetic)
            largeLeapPenalty = Math.Max(2, largeLeapPenalty - 3);
        else if (emotion == EmotionType.Tense)
            largeLeapPenalty = Math.Max(3, largeLeapPenalty - 2);

        score -= parallelCount * parallelPenalty;
        score -= subLargeLeapCount * largeLeapPenalty;

        if (style == MelodyStyle.Pop && subNoteCount > 0 && subNoteCount < 2)
            score -= 6;
        else if (style != MelodyStyle.Pop && subNoteCount < 2)
            score -= 10;

        score -= GetStyleCharacterPenalty(mainYNote, subYNote, style, emotion, keyRoot);

        return Math.Clamp(score, 0, 100);
    }

    private static void GetHarmonyScoreTargetRange(
        MelodyStyle style,
        EmotionType emotion,
        out int minScore,
        out int maxScore,
        out int targetCenter,
        out int maxTry,
        out int acceptTolerance)
    {
        minScore = 80;
        maxScore = 95;
        targetCenter = 88;
        maxTry = 3;
        acceptTolerance = 2;

        if (style == MelodyStyle.JiangNan)
        {
            minScore = 85;
            maxScore = 100;
            targetCenter = 94;
            maxTry = 3;
            acceptTolerance = 2;
        }
        else if (style == MelodyStyle.Pop)
        {
            minScore = 80;
            maxScore = 95;
            targetCenter = 88;
            maxTry = 3;
            acceptTolerance = 2;
        }
        else if (style == MelodyStyle.JPop)
        {
            minScore = 72;
            maxScore = 92;
            targetCenter = 84;
            maxTry = 4;
            acceptTolerance = 3;
        }
        else if (style == MelodyStyle.Jazz)
        {
            minScore = 70;
            maxScore = 88;
            targetCenter = 80;
            maxTry = 3;
            acceptTolerance = 3;
        }

        if (emotion == EmotionType.Calm)
        {
            minScore += 3;
            maxScore += 3;
            targetCenter += 4;
        }
        else if (emotion == EmotionType.Sad)
        {
            minScore -= 2;
            maxScore += 1;
            targetCenter -= 1;
        }
        else if (emotion == EmotionType.Tense)
        {
            minScore -= 8;
            maxScore -= 3;
            targetCenter -= 6;
            acceptTolerance += 1;
        }
        else if (emotion == EmotionType.Energetic)
        {
            minScore -= 4;
            maxScore += 1;
            targetCenter -= 2;
            maxTry += 1;
        }

        minScore = Math.Clamp(minScore, 0, 100);
        maxScore = Math.Clamp(maxScore, minScore, 100);
        targetCenter = Math.Clamp(targetCenter, minScore, maxScore);
    }

    private static int GetScoreRangeDistance(int score, int minScore, int maxScore)
    {
        if (score < minScore)
            return minScore - score;

        if (score > maxScore)
            return score - maxScore;

        return 0;
    }

    private static bool IsScoreCloseEnoughToRange(int score, int minScore, int maxScore, int acceptTolerance)
    {
        int distance = GetScoreRangeDistance(score, minScore, maxScore);
        return distance <= acceptTolerance;
    }

    private static bool IsScoreInTargetRange(int score, int minScore, int maxScore)
    {
        return score >= minScore && score <= maxScore;
    }

    private static int CountParallelPerfectIntervals(string mainYNote, string subYNote)
    {
        int count = 0;

        if (string.IsNullOrEmpty(mainYNote) || string.IsNullOrEmpty(subYNote))
            return count;

        int len = Math.Min(mainYNote.Length, subYNote.Length);
        string previousMainPitch = "00";
        string previousSubPitch = "00";

        for (int i = 0; i + 3 < len; i += 4)
        {
            string currentMainPitch = mainYNote.Substring(i, 2);
            string currentSubPitch = subYNote.Substring(i, 2);

            if (currentMainPitch != "00" && currentSubPitch != "00")
            {
                if (IsParallelPerfectInterval(previousMainPitch, previousSubPitch, currentMainPitch, currentSubPitch))
                    count++;

                previousMainPitch = currentMainPitch;
                previousSubPitch = currentSubPitch;
            }
        }

        return count;
    }

    private static bool IsParallelPerfectInterval(
        string previousMainPitch,
        string previousSubPitch,
        string currentMainPitch,
        string currentSubPitch)
    {
        if (previousMainPitch == "00" || previousSubPitch == "00" || currentMainPitch == "00" || currentSubPitch == "00")
            return false;

        int prevMainIdx = PitchToAbsoluteSemitone(previousMainPitch);
        int prevSubIdx = PitchToAbsoluteSemitone(previousSubPitch);
        int currMainIdx = PitchToAbsoluteSemitone(currentMainPitch);
        int currSubIdx = PitchToAbsoluteSemitone(currentSubPitch);

        int prevType = GetPerfectIntervalType(Math.Abs(prevMainIdx - prevSubIdx));
        int currType = GetPerfectIntervalType(Math.Abs(currMainIdx - currSubIdx));

        if (prevType == 0 || currType == 0)
            return false;

        if (prevType != currType)
            return false;

        int mainMove = currMainIdx - prevMainIdx;
        int subMove = currSubIdx - prevSubIdx;

        if (mainMove == 0 || subMove == 0)
            return false;

        return Math.Sign(mainMove) == Math.Sign(subMove);
    }

    private static int GetPerfectIntervalType(int diff)
    {
        int intervalClass = diff % 12;
        if (intervalClass == 7)
            return 5;
        if (intervalClass == 0)
            return 8;
        return 0;
    }

    private static int CountSharpNotesInYNote(string yNote)
    {
        int count = 0;
        if (string.IsNullOrEmpty(yNote))
            return count;

        for (int i = 0; i + 3 < yNote.Length; i += 4)
        {
            string pitch = yNote.Substring(i, 2);
            if (pitch == "00")
                continue;
            if (ContainsSharpPitch(pitch))
                count++;
        }

        return count;
    }

    private static bool ContainsSharpPitch(string pitch)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch == "00")
            return false;

        return pitch[0] is 'c' or 'd' or 'f' or 'g' or 'a' || pitch.Contains('#');
    }

    private static int CountStyleColorNotesInYNote(string yNote, MelodyStyle style, int keyRoot)
    {
        if (style != MelodyStyle.JiangNan)
            return CountSharpNotesInYNote(yNote);

        // 江南五聲在 D/G/A/E 等調本來就會有 F#/C#；不要把「調號內音」當成雜音懲罰。
        HashSet<int> jiangNanDegrees = [0, 2, 4, 7, 9];
        int count = 0;

        foreach ((string pitch, _) in ParseYNoteEvents(yNote))
        {
            if (pitch == "00")
                continue;

            int degree = NormalizeSemitone(PitchToAbsoluteSemitone(pitch) - keyRoot);
            if (!jiangNanDegrees.Contains(degree))
                count++;
        }

        return count;
    }

    private static int CountMainMelodyLargeLeaps(string mainYNote)
    {
        int count = 0;
        if (string.IsNullOrEmpty(mainYNote))
            return count;

        int? previousIdx = null;
        for (int i = 0; i + 3 < mainYNote.Length; i += 4)
        {
            string pitch = mainYNote.Substring(i, 2);
            if (pitch == "00")
                continue;

            int idx = PitchToAbsoluteSemitone(pitch);
            if (previousIdx is not null)
            {
                int diff = Math.Abs(idx - previousIdx.Value);
                if (diff >= 7)
                    count++;
            }

            previousIdx = idx;
        }

        return count;
    }

    private static int CountSubMelodyLargeLeaps(string subYNote)
    {
        int count = 0;
        if (string.IsNullOrEmpty(subYNote))
            return count;

        int? previousIdx = null;
        for (int i = 0; i + 3 < subYNote.Length; i += 4)
        {
            string pitch = subYNote.Substring(i, 2);
            if (pitch == "00")
                continue;

            int idx = PitchToAbsoluteSemitone(pitch);
            if (previousIdx is not null)
            {
                int diff = Math.Abs(idx - previousIdx.Value);
                if (diff > 7)
                    count++;
            }

            previousIdx = idx;
        }

        return count;
    }

    private static int CountValidNotes(string yNote)
    {
        int count = 0;
        if (string.IsNullOrEmpty(yNote))
            return count;

        for (int i = 0; i + 3 < yNote.Length; i += 4)
        {
            string pitch = yNote.Substring(i, 2);
            if (pitch != "00")
                count++;
        }

        return count;
    }

    private static int GetStyleCharacterPenalty(string mainYNote, string subYNote, MelodyStyle style, EmotionType emotion, int keyRoot = 0)
    {
        int penalty = 0;
        int sharpCount = CountStyleColorNotesInYNote(mainYNote, style, keyRoot);
        int mainLeapCount = CountMainMelodyLargeLeaps(mainYNote);
        int subLeapCount = CountSubMelodyLargeLeaps(subYNote);

        if (style == MelodyStyle.JiangNan)
        {
            penalty += sharpCount * 8;
            if (mainLeapCount > 2) penalty += (mainLeapCount - 2) * 5;
        }
        else if (style == MelodyStyle.Pop)
        {
            penalty += sharpCount * 6;
            if (mainLeapCount > 3) penalty += (mainLeapCount - 3) * 4;
        }
        else if (style == MelodyStyle.JPop)
        {
            penalty += sharpCount * 4;
            if (mainLeapCount == 0) penalty += 6;
        }
        else if (style == MelodyStyle.Jazz)
        {
            if (sharpCount == 0) penalty += 4;
            if (sharpCount > 6) penalty += (sharpCount - 6) * 5;
            if (subLeapCount == 0) penalty += 3;
            if (mainLeapCount < 2) penalty += 3;
        }

        if (emotion == EmotionType.Calm)
        {
            penalty += sharpCount * 6;
            if (mainLeapCount > 2) penalty += (mainLeapCount - 2) * 6;
        }
        else if (emotion == EmotionType.Sad)
        {
            if (sharpCount > 2) penalty += (sharpCount - 2) * 5;
            if (mainLeapCount > 4) penalty += (mainLeapCount - 4) * 4;
        }
        else if (emotion == EmotionType.Tense)
        {
            if (sharpCount == 0 && style == MelodyStyle.Jazz) penalty += 3;
        }
        else if (emotion == EmotionType.Energetic)
        {
            if (mainLeapCount == 0) penalty += 5;
        }

        return penalty;
    }

    private static double CalculateChordSupportRate(string mainYNote, string chordText, int barCount, int keyRoot, bool minorKey)
    {
        if (barCount <= 0)
            return 0;

        List<string> bars = SplitYNoteIntoBars(mainYNote, barCount);
        int supported = 0;
        int checkedNotes = 0;

        for (int bar = 0; bar < Math.Min(barCount, bars.Count); bar++)
        {
            ChordInfo chord = GetChordInfo(GetChordCodeAtBar(chordText, bar), keyRoot, minorKey);
            foreach ((string pitch, _) in ParseYNoteEvents(bars[bar]))
            {
                if (pitch == "00")
                    continue;

                checkedNotes++;
                if (IsChordSupportedPitch(pitch, chord))
                    supported++;
            }
        }

        return checkedNotes == 0 ? 0 : supported / (double)checkedNotes;
    }

    private static int CalculateOverallQualityScore(
        int harmonyScore,
        double chordSupportRate,
        double restRate,
        int mainLargeLeapCount,
        int subLargeLeapCount,
        int parallelPerfectCount,
        int sharpCount,
        MelodyStyle style,
        EmotionType emotion)
    {
        int score = (int)Math.Round(harmonyScore * 0.55 + chordSupportRate * 100.0 * 0.30 + 15.0);

        if (restRate > 0.45) score -= 10;
        else if (restRate > 0.30) score -= 4;
        else if (restRate >= 0.08 && restRate <= 0.25) score += 3;

        score -= Math.Max(0, mainLargeLeapCount - 3) * 3;
        score -= Math.Max(0, subLargeLeapCount - 2) * 2;
        score -= parallelPerfectCount * 8;

        if (style == MelodyStyle.JiangNan && sharpCount > 0)
            score -= sharpCount * 4;
        if (style == MelodyStyle.Jazz && sharpCount == 0)
            score -= 2;
        if (emotion == EmotionType.Calm && mainLargeLeapCount > 2)
            score -= 4;

        return Math.Clamp(score, 0, 100);
    }

    private static string GetQualityGrade(int qualityScore)
    {
        if (qualityScore >= 90) return "Excellent";
        if (qualityScore >= 80) return "Good";
        if (qualityScore >= 70) return "Acceptable";
        if (qualityScore >= 60) return "Needs Review";
        return "Needs Major Revision";
    }

    private static string GetStyleAnalysisComment(
        MelodyStyle style,
        EmotionType emotion,
        int harmonyScore,
        double chordSupportRate,
        double restRate,
        int largeLeapCount,
        int sharpCount)
    {
        if (style == MelodyStyle.JiangNan)
        {
            return sharpCount == 0 && largeLeapCount <= 2
                ? "江南風格判斷：五聲音階與平穩線條保持良好，適合輸出成較清雅的樂譜。"
                : "江南風格判斷：目前仍有較多半音或跳進，若想更像江南民樂，可降低升記號比例並增加級進。";
        }

        if (style == MelodyStyle.Jazz)
        {
            return harmonyScore <= 88 && chordSupportRate >= 0.35
                ? "Jazz 風格判斷：保留了一些張力，和聲分數不必追求 100；若聽感過硬，可降低半音或增加和弦內音。"
                : "Jazz 風格判斷：目前張力可能不足或和弦支撐偏弱，可增加 guide tone / passing tone 但避免連續大跳。";
        }

        if (style == MelodyStyle.JPop)
        {
            return largeLeapCount >= 1
                ? "JPop 風格判斷：有旋律跳進與情緒起伏，適合副歌型旋律；注意不要讓大跳過於密集。"
                : "JPop 風格判斷：旋律較平，若想更有副歌感，可提高音域或加入少量跳進。";
        }

        if (emotion == EmotionType.Calm && restRate < 0.08)
            return "Pop 風格判斷：目前音符較密，平靜情緒可增加休止或長音。";

        return "Pop 風格判斷：可唱性與和弦支撐是主要重點；目前可用和弦支持率與大跳次數判斷是否需要微調。";
    }

    private static List<string> BuildQualitySuggestions(
        MelodyStyle style,
        EmotionType emotion,
        int harmonyScore,
        int targetMinScore,
        int targetMaxScore,
        double chordSupportRate,
        double restRate,
        int mainLargeLeapCount,
        int subLargeLeapCount,
        int parallelPerfectCount,
        int sharpCount,
        int rhythmDiversity)
    {
        List<string> suggestions = new();

        if (harmonyScore < targetMinScore)
            suggestions.Add("Harmony Score 偏低：建議先按 Update Sub Melody，讓副旋律重新避開大跳與平行五八度。");
        else if (harmonyScore > targetMaxScore && style == MelodyStyle.Jazz)
            suggestions.Add("Jazz 的 Harmony Score 過高可能代表張力不足，可嘗試 Tense 情緒或提高 Sub Melody Density。");
        else
            suggestions.Add("Harmony Score 已接近目前風格目標，可以優先檢查譜面可讀性與聽感。 ");

        if (parallelPerfectCount > 0)
            suggestions.Add("偵測到平行五度 / 八度疑慮：可在 Expert Mode 中微調副旋律相鄰音，或重新 Update Sub Melody。");

        if (chordSupportRate < 0.35)
            suggestions.Add("和弦支持率偏低：主旋律強拍可多放 root / third / fifth，讓 MusicXML 譜面更穩。 ");
        else if (chordSupportRate > 0.85 && (style is MelodyStyle.Jazz or MelodyStyle.JPop))
            suggestions.Add("和弦支持率很高：旋律很穩，但可能較保守；JPop/Jazz 可加入少量經過音提升表情。 ");

        if (restRate > 0.45)
            suggestions.Add("休止比例偏高：旋律可能太空，可提高 Seed Input 密度或改用 Bright/Energetic。 ");
        else if (restRate < 0.03 && emotion == EmotionType.Calm)
            suggestions.Add("平靜情緒下音符太滿：可在 Expert Mode 增加休止或長音。 ");

        if (style == MelodyStyle.JiangNan && sharpCount > 0)
            suggestions.Add("江南風格建議減少升記號 / 半音，讓旋律更接近五聲音階。 ");

        if (mainLargeLeapCount > 5)
            suggestions.Add("主旋律大跳偏多：可降低 Emotion 強度，或用 Expert Mode 把跳進改成級進。 ");

        if (subLargeLeapCount > 3)
            suggestions.Add("副旋律大跳偏多：建議按 Update Sub Melody，或把 Sub Melody Density 改成 Low / Medium。 ");

        if (rhythmDiversity <= 1 && style != MelodyStyle.JiangNan)
            suggestions.Add("節奏種類偏少：可改用 JPop/Jazz 或提高情緒強度，讓節奏更有變化。 ");

        if (suggestions.Count == 0)
            suggestions.Add("目前分析沒有明顯警告，可以下載 PDF / MSCZ 進 MuseScore 進一步人工修譜。 ");

        return suggestions;
    }


    private static int GetChordTextBarCount(string? chordText)
    {
        if (string.IsNullOrWhiteSpace(chordText))
            return 0;

        string cleaned = new string(chordText.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Math.Max(0, cleaned.Length / 5);
    }

    private static int GetMinimumSubMelodyDuration(MelodyStyle style, EmotionType emotion)
    {
        if (style == MelodyStyle.JiangNan)
            return 120;

        if (style == MelodyStyle.Jazz || style == MelodyStyle.JPop || emotion is EmotionType.Tense or EmotionType.Energetic)
            return 120;

        if (style == MelodyStyle.Pop || emotion is EmotionType.Calm or EmotionType.Sad)
            return 240;

        return 120;
    }

    private static bool ShouldGenerateSubAtPosition(
        MelodyStyle style,
        EmotionType emotion,
        string density,
        int tickInBar,
        int noteIndex,
        int noteCount,
        bool isSongEnding)
    {
        if (noteIndex == noteCount - 1 && isSongEnding)
            return false;

        bool low = density.Equals("low", StringComparison.OrdinalIgnoreCase);
        bool high = density.Equals("high", StringComparison.OrdinalIgnoreCase) || emotion is EmotionType.Tense or EmotionType.Energetic;

        if (style == MelodyStyle.JiangNan)
        {
            if (low)
                return tickInBar == 0 || tickInBar == TicksPerBar / 2 || noteIndex == noteCount - 1;

            if (high)
                return true;

            return tickInBar is 0 or 480 or 960 or 1440 || noteIndex % 2 == 0 || noteIndex == noteCount - 1;
        }

        if (style == MelodyStyle.Pop)
        {
            if (low) return tickInBar == 0 || noteIndex == noteCount - 1;
            if (high) return tickInBar is 0 or 480 or 960 or 1440 || noteIndex == noteCount - 1;
            return tickInBar == 0 || tickInBar == 960 || noteIndex == noteCount - 1;
        }

        if (style == MelodyStyle.JPop)
        {
            if (low) return tickInBar == 0 || tickInBar == 960 || noteIndex == noteCount - 1;
            if (high) return tickInBar % 240 == 0 || noteIndex == noteCount - 1;
            return tickInBar is 0 or 480 or 960 || noteIndex == noteCount - 1;
        }

        if (style == MelodyStyle.Jazz)
        {
            if (low) return tickInBar is 0 or 960;
            if (high) return tickInBar % 240 == 0 || noteIndex == noteCount - 1;
            return tickInBar % 480 == 0 || noteIndex == noteCount - 1;
        }

        return tickInBar == 0 || tickInBar == 960 || noteIndex == noteCount - 1;
    }

    private static bool ShouldMuteSubMelodyForTimeSectionEnding(bool isSectionEnding, bool isSongEnding, int noteIndex, int noteCount)
    {
        if (!isSectionEnding && !isSongEnding)
            return false;

        if (noteCount <= 1)
            return false;

        // 原 WinForms 後期版本只在最後一顆讓副旋律退開，避免整個尾句都是休止。
        return noteIndex >= noteCount - 1;
    }

    private static bool ShouldKeepCounterMelodyDuringEnding(int noteIndex, int noteCount)
    {
        if (noteCount <= 2)
            return true;

        return noteIndex < noteCount - 1;
    }

    private static bool ShouldSustainCounterMelodyThroughGap(
        MelodyStyle style,
        EmotionType emotion,
        string density,
        int tickInBar,
        int noteIndex,
        int noteCount,
        int duration,
        bool isSongEnding)
    {
        if (duration < 120)
            return false;

        if (noteIndex == noteCount - 1 && isSongEnding)
            return false;

        bool low = density.Equals("low", StringComparison.OrdinalIgnoreCase);
        bool high = density.Equals("high", StringComparison.OrdinalIgnoreCase);

        if (style == MelodyStyle.JiangNan)
            return high || !low || tickInBar == 0 || tickInBar == TicksPerBar / 2;

        if (high)
            return true;

        if (low)
            return tickInBar == 0;

        return style switch
        {
            MelodyStyle.Pop => tickInBar is 0 or 960,
            MelodyStyle.JPop => tickInBar is 0 or 480 or 960,
            MelodyStyle.Jazz => tickInBar % 480 == 0 || emotion == EmotionType.Tense,
            _ => false
        };
    }

    private static int GetTargetCounterMelodyNotesPerBar(MelodyStyle style, string density, int noteCount)
    {
        if (noteCount <= 1)
            return 1;

        bool low = density.Equals("low", StringComparison.OrdinalIgnoreCase);
        bool high = density.Equals("high", StringComparison.OrdinalIgnoreCase);

        if (style == MelodyStyle.JiangNan)
        {
            if (low)
                return Math.Min(Math.Max(2, noteCount / 2), noteCount);

            if (high)
                return noteCount;

            return Math.Min(Math.Max(3, (int)Math.Ceiling(noteCount * 0.75)), noteCount);
        }

        if (low)
            return 1;

        return style switch
        {
            MelodyStyle.Pop => high ? Math.Min(3, noteCount) : Math.Min(2, noteCount),
            MelodyStyle.JPop or MelodyStyle.Jazz => high ? Math.Min(4, noteCount) : Math.Min(3, noteCount),
            _ => Math.Min(2, noteCount)
        };
    }

    private static bool IsGoodCounterMelodyFillPosition(
        MelodyStyle style,
        string density,
        int tickInBar,
        int noteIndex,
        int noteCount,
        bool isSongEnding)
    {
        if (noteIndex == noteCount - 1 && isSongEnding)
            return false;

        if (tickInBar == 0 || tickInBar == TicksPerBar / 2)
            return true;

        if (density.Equals("high", StringComparison.OrdinalIgnoreCase))
            return tickInBar is 480 or 1440 || noteIndex == noteCount - 1;

        if (style is MelodyStyle.JPop or MelodyStyle.Jazz)
            return tickInBar == 480 || noteIndex == noteCount - 1;

        return noteIndex == noteCount - 1;
    }

    private static string SelectBestCounterPitchBelowMain(
        MelodyStyle style,
        EmotionType emotion,
        string mainPitch,
        ChordInfo chord,
        int keyRoot,
        string previousSubPitch,
        string previousMainPitch)
    {
        if (string.IsNullOrWhiteSpace(mainPitch) || mainPitch == "00")
            return "00";

        int mainAbs = PitchToAbsoluteSemitone(mainPitch);
        if (mainAbs < 0)
            return "00";

        HashSet<int> preferredPitchClasses = BuildCounterCandidatePitchClasses(style, chord, keyRoot);
        string bestPitch = "00";
        int bestScore = int.MaxValue;
        bool foundCandidate = false;

        int maxVertical = style switch
        {
            MelodyStyle.Jazz => 17,
            MelodyStyle.JPop => 14,
            _ => 14
        };

        if (emotion == EmotionType.Calm)
            maxVertical = Math.Min(maxVertical, 10);
        else if (emotion == EmotionType.Tense)
            maxVertical = Math.Max(maxVertical, 16);

        int minSubAbs = PitchToAbsoluteSemitone(emotion == EmotionType.Sad ? "C3" : "E3");

        for (int octave = 2; octave <= 5; octave++)
        {
            foreach (int pitchClass in preferredPitchClasses)
            {
                int candidateAbs = octave * 12 + NormalizeSemitone(pitchClass);
                string candidate = PitchFromAbsoluteSemitone(candidateAbs);

                if (!IsPitchAllowedForCounter(style, candidate, keyRoot))
                    continue;

                int verticalDiff = mainAbs - candidateAbs;
                if (verticalDiff < 3 || verticalDiff > maxVertical)
                    continue;

                if (candidateAbs < minSubAbs)
                    continue;

                int score = GetVerticalIntervalPenalty(verticalDiff, style, emotion);

                if (previousSubPitch != "00")
                {
                    int previousSubAbs = PitchToAbsoluteSemitone(previousSubPitch);
                    int move = Math.Abs(candidateAbs - previousSubAbs);
                    score += style switch
                    {
                        MelodyStyle.JiangNan or MelodyStyle.Pop => move * 5,
                        MelodyStyle.JPop => move * 3,
                        MelodyStyle.Jazz => move * 3,
                        _ => move * 4
                    };

                    if (move == 0) score += style == MelodyStyle.JiangNan ? 8 : 4;
                    if (move > (style == MelodyStyle.Jazz ? 10 : 7)) score += style == MelodyStyle.Jazz ? 25 : 40;
                }

                if (IsParallelPerfectInterval(previousMainPitch, previousSubPitch, mainPitch, candidate))
                    score += style == MelodyStyle.Jazz ? 700 : 1000;

                score += GetMotionPenalty(previousMainPitch, previousSubPitch, mainPitch, candidate);
                score += GetEmotionSubMelodyPenalty(emotion, style, verticalDiff, candidateAbs);

                if (style == MelodyStyle.JiangNan && !GetChordTonePitchClasses(chord).Contains(NormalizeSemitone(candidateAbs)))
                    score += 8;

                if (style == MelodyStyle.Jazz && GetChordTonePitchClasses(chord).Contains(NormalizeSemitone(candidateAbs)))
                    score -= 4;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestPitch = candidate;
                    foundCandidate = true;
                }
            }
        }

        return foundCandidate ? bestPitch : "00";
    }

    private static HashSet<int> BuildCounterCandidatePitchClasses(MelodyStyle style, ChordInfo chord, int keyRoot)
    {
        if (style == MelodyStyle.JiangNan)
        {
            int[] pentatonic = [0, 2, 4, 7, 9];
            HashSet<int> result = pentatonic.Select(i => NormalizeSemitone(keyRoot + i)).ToHashSet();

            foreach (int chordTone in GetChordTonePitchClasses(chord))
            {
                int degree = NormalizeSemitone(chordTone - keyRoot);
                if (pentatonic.Contains(degree))
                    result.Add(chordTone);
            }

            return result;
        }

        HashSet<int> pcs = GetChordTonePitchClasses(chord);
        if (style == MelodyStyle.Jazz)
        {
            pcs.Add(NormalizeSemitone(chord.RootSemitone + 2));
            pcs.Add(NormalizeSemitone(chord.RootSemitone + 10));
        }

        return pcs;
    }

    private static bool IsPitchAllowedForCounter(MelodyStyle style, string pitch, int keyRoot)
    {
        if (pitch == "00")
            return true;

        int abs = PitchToAbsoluteSemitone(pitch);
        int octave = abs / 12;
        if (octave < 2 || octave > 5)
            return false;

        if (style == MelodyStyle.JiangNan)
        {
            int degree = NormalizeSemitone(abs - keyRoot);
            return degree is 0 or 2 or 4 or 7 or 9;
        }

        return true;
    }

    private static bool IsUsableCounterPitchUnderMain(string counterPitch, string mainPitch, MelodyStyle style, EmotionType emotion)
    {
        if (counterPitch == "00" || mainPitch == "00")
            return false;

        int counterAbs = PitchToAbsoluteSemitone(counterPitch);
        int mainAbs = PitchToAbsoluteSemitone(mainPitch);
        int verticalDiff = mainAbs - counterAbs;
        int maxVertical = style == MelodyStyle.Jazz || emotion == EmotionType.Tense ? 17 : 14;

        if (emotion == EmotionType.Calm)
            maxVertical = Math.Min(maxVertical, 10);

        return verticalDiff >= 3 && verticalDiff <= maxVertical;
    }

    private static void StrengthenJiangNanSubMelodyLine(
        string[] subPitches,
        string[] mainPitches,
        ChordInfo chord,
        int[] durations,
        int[] tickStarts,
        int keyRoot,
        EmotionType emotion,
        string density,
        bool isSongEnding,
        ref string previousMainPitch,
        ref string previousSubPitch)
    {
        bool low = density.Equals("low", StringComparison.OrdinalIgnoreCase);
        bool high = density.Equals("high", StringComparison.OrdinalIgnoreCase);

        for (int noteIndex = 0; noteIndex < subPitches.Length; noteIndex++)
        {
            if (mainPitches[noteIndex] == "00" || durations[noteIndex] < 120)
                continue;

            if (noteIndex == subPitches.Length - 1 && isSongEnding)
                continue;

            bool shouldFill = high || !low;
            if (low)
                shouldFill = tickStarts[noteIndex] == 0 || tickStarts[noteIndex] == TicksPerBar / 2 || noteIndex == subPitches.Length - 1;

            if (!shouldFill)
                continue;

            if (subPitches[noteIndex] != "00")
            {
                if (WouldCreateTooMuchRepeatedJiangNanSubPitch(subPitches, noteIndex, subPitches[noteIndex]))
                {
                    string alternative = GetAlternativeJiangNanCounterPitch(
                        mainPitches[noteIndex],
                        chord,
                        subPitches[noteIndex],
                        previousSubPitch,
                        previousMainPitch,
                        keyRoot,
                        emotion);

                    if (alternative != "00")
                        subPitches[noteIndex] = alternative;
                }

                previousSubPitch = subPitches[noteIndex];
                previousMainPitch = mainPitches[noteIndex];
                continue;
            }

            string fillPitch = "00";
            if (previousSubPitch != "00" && IsUsableCounterPitchUnderMain(previousSubPitch, mainPitches[noteIndex], MelodyStyle.JiangNan, emotion))
                fillPitch = previousSubPitch;

            if (fillPitch == "00")
                fillPitch = SelectBestCounterPitchBelowMain(MelodyStyle.JiangNan, emotion, mainPitches[noteIndex], chord, keyRoot, previousSubPitch, previousMainPitch);

            if (fillPitch != "00")
            {
                if (WouldCreateTooMuchRepeatedJiangNanSubPitch(subPitches, noteIndex, fillPitch))
                {
                    string alternative = GetAlternativeJiangNanCounterPitch(
                        mainPitches[noteIndex],
                        chord,
                        fillPitch,
                        previousSubPitch,
                        previousMainPitch,
                        keyRoot,
                        emotion);

                    if (alternative != "00")
                        fillPitch = alternative;
                }

                subPitches[noteIndex] = fillPitch;
                previousSubPitch = fillPitch;
            }

            previousMainPitch = mainPitches[noteIndex];
        }
    }

    private static bool WouldCreateTooMuchRepeatedJiangNanSubPitch(string[] subPitches, int noteIndex, string candidatePitch)
    {
        if (string.IsNullOrWhiteSpace(candidatePitch) || candidatePitch == "00")
            return false;

        int run = 0;
        for (int i = noteIndex - 1; i >= 0; i--)
        {
            if (subPitches[i] == candidatePitch)
                run++;
            else if (subPitches[i] == "00")
                continue;
            else
                break;
        }

        return run >= 3;
    }

    private static string GetAlternativeJiangNanCounterPitch(
        string mainPitch,
        ChordInfo chord,
        string avoidPitch,
        string previousSubPitch,
        string previousMainPitch,
        int keyRoot,
        EmotionType emotion)
    {
        if (mainPitch == "00")
            return "00";

        int mainAbs = PitchToAbsoluteSemitone(mainPitch);
        string bestPitch = "00";
        int bestScore = int.MaxValue;
        int[] pentatonic = [0, 2, 4, 7, 9];

        for (int octave = 3; octave <= 5; octave++)
        {
            foreach (int interval in pentatonic)
            {
                string candidate = PitchFromSemitone(keyRoot + interval, octave);
                if (candidate == avoidPitch)
                    continue;

                int candidateAbs = PitchToAbsoluteSemitone(candidate);
                int verticalDiff = mainAbs - candidateAbs;
                if (verticalDiff < 3 || verticalDiff > 14)
                    continue;

                int score = GetVerticalIntervalPenalty(verticalDiff, MelodyStyle.JiangNan, emotion);

                if (previousSubPitch != "00")
                {
                    int move = Math.Abs(candidateAbs - PitchToAbsoluteSemitone(previousSubPitch));
                    score += move * 4;
                    if (move == 0) score += 40;
                    if (move > 7) score += 35;
                }

                if (IsParallelPerfectInterval(previousMainPitch, previousSubPitch, mainPitch, candidate))
                    score += 1000;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestPitch = candidate;
                }
            }
        }

        return bestPitch;
    }

    private static int GetVerticalIntervalPenalty(int verticalDiff, MelodyStyle style, EmotionType emotion)
    {
        int intervalClass = NormalizeSemitone(verticalDiff);

        if (style == MelodyStyle.JiangNan)
        {
            if (intervalClass is 8 or 9) return 0;
            if (intervalClass == 7) return 4;
            if (intervalClass is 3 or 4) return 10;
            if (intervalClass == 5) return 18;
            if (intervalClass == 0) return 32;
            return 70;
        }

        if (intervalClass is 3 or 4 or 8 or 9)
            return 0;

        if (style == MelodyStyle.Jazz || emotion == EmotionType.Tense)
        {
            if (intervalClass is 10 or 2) return 16;
            if (intervalClass == 6) return 28;
        }

        if (intervalClass == 7)
            return style == MelodyStyle.Jazz || emotion == EmotionType.Tense ? 12 : 16;

        if (intervalClass == 0)
            return emotion == EmotionType.Calm ? 18 : 25;

        if (intervalClass == 5)
            return 40;

        return emotion is EmotionType.Calm or EmotionType.Sad ? 95 : 85;
    }

    private static int GetMotionPenalty(
        string previousMainPitch,
        string previousSubPitch,
        string currentMainPitch,
        string currentSubPitch)
    {
        if (previousMainPitch == "00" || previousSubPitch == "00" || currentMainPitch == "00" || currentSubPitch == "00")
            return 0;

        int previousMainAbs = PitchToAbsoluteSemitone(previousMainPitch);
        int previousSubAbs = PitchToAbsoluteSemitone(previousSubPitch);
        int currentMainAbs = PitchToAbsoluteSemitone(currentMainPitch);
        int currentSubAbs = PitchToAbsoluteSemitone(currentSubPitch);

        int mainMove = currentMainAbs - previousMainAbs;
        int subMove = currentSubAbs - previousSubAbs;

        if (mainMove == 0 || subMove == 0)
            return 0;

        return Math.Sign(mainMove) == Math.Sign(subMove) ? 8 : -4;
    }

    private static int GetEmotionSubMelodyPenalty(EmotionType emotion, MelodyStyle style, int verticalDiff, int candidateAbs)
    {
        int intervalClass = NormalizeSemitone(verticalDiff);
        int score = 0;

        if (emotion == EmotionType.Calm)
        {
            if (intervalClass is 3 or 4 or 8 or 9) score -= 6;
            if (intervalClass is 7 or 0) score += 8;
        }
        else if (emotion == EmotionType.Bright)
        {
            if (candidateAbs >= PitchToAbsoluteSemitone("C4")) score -= 3;
            if (intervalClass is 3 or 4) score -= 4;
        }
        else if (emotion == EmotionType.Sad)
        {
            if (candidateAbs <= PitchToAbsoluteSemitone("B3")) score -= 5;
            if (intervalClass is 8 or 9) score -= 3;
        }
        else if (emotion == EmotionType.Tense)
        {
            if (style == MelodyStyle.Jazz && (intervalClass is 10 or 2 or 6)) score -= 5;
        }
        else if (emotion == EmotionType.Energetic)
        {
            if (candidateAbs >= PitchToAbsoluteSemitone("C4")) score -= 4;
        }

        return score;
    }

    private string GenerateSubMelodyForMainAndChordText(
        MelodyStyle style,
        EmotionType emotion,
        string? densityText,
        string chordText,
        int keyRoot,
        bool minorKey,
        int barCount,
        string mainYNote)
    {
        List<string> mainBars = SplitYNoteIntoBars(mainYNote, barCount);
        List<string> subBars = new(capacity: barCount);
        string previousMainPitch = "00";
        string previousSubPitch = "00";

        for (int bar = 0; bar < barCount; bar++)
        {
            ChordInfo chord = GetChordInfo(GetChordCodeAtBar(chordText, bar), keyRoot, minorKey);
            string mainBar = bar < mainBars.Count ? mainBars[bar] : NormalizeSingleBarYNote("00" + "01");
            string subBar = GenerateSubMelodyBarFromMainBar(
                style,
                emotion,
                densityText,
                mainBar,
                chord,
                keyRoot,
                minorKey,
                bar,
                barCount,
                ref previousMainPitch,
                ref previousSubPitch);

            subBars.Add(subBar);
        }

        return string.Concat(subBars);
    }

    private string GenerateSubMelodyForChordText(
        MelodyStyle style,
        EmotionType emotion,
        string? densityText,
        string chordText,
        int keyRoot,
        bool minorKey,
        int barCount)
    {
        List<string> bars = new(capacity: barCount);
        for (int bar = 0; bar < barCount; bar++)
        {
            ChordInfo chord = GetChordInfo(GetChordCodeAtBar(chordText, bar), keyRoot, minorKey);
            bars.Add(GenerateSubMelodyBar(style, emotion, densityText, chord, keyRoot, minorKey, bar, barCount));
        }

        return string.Concat(bars);
    }

    private string GenerateSubMelodyForChordTextWithHarmonyRefinement(
        MelodyStyle style,
        EmotionType emotion,
        string? densityText,
        string chordText,
        int keyRoot,
        bool minorKey,
        int barCount,
        string mainYNote)
    {
        GetHarmonyScoreTargetRange(
            style,
            emotion,
            out int targetMinScore,
            out int targetMaxScore,
            out int targetCenterScore,
            out _,
            out _);

        string requestedDensity = string.IsNullOrWhiteSpace(densityText) ? "Medium" : densityText.Trim();
        string[] candidateDensities = new[] { requestedDensity, "Low", "Medium", "High" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? bestSubYNote = null;
        int bestSortKey = int.MaxValue;
        int bestCenterDistance = int.MaxValue;

        foreach (string candidateDensity in candidateDensities)
        {
            string candidateSubYNote = GenerateSubMelodyForMainAndChordText(style, emotion, candidateDensity, chordText, keyRoot, minorKey, barCount, mainYNote);
            int candidateScore = CalculateHarmonyScore(mainYNote, candidateSubYNote, style, emotion, keyRoot);
            int rangeDistance = GetScoreRangeDistance(candidateScore, targetMinScore, targetMaxScore);
            int centerDistance = Math.Abs(candidateScore - targetCenterScore);
            int parallelCount = CountParallelPerfectIntervals(mainYNote, candidateSubYNote);
            int subLeapCount = CountSubMelodyLargeLeaps(candidateSubYNote);

            // 先靠近 target range，再靠近 target center；額外懲罰平行五八度與副旋律大跳。
            int sortKey = rangeDistance * 100 + centerDistance + parallelCount * 40 + subLeapCount * 6;

            if (bestSubYNote is null || sortKey < bestSortKey || (sortKey == bestSortKey && centerDistance < bestCenterDistance))
            {
                bestSubYNote = candidateSubYNote;
                bestSortKey = sortKey;
                bestCenterDistance = centerDistance;
            }
        }

        return bestSubYNote ?? GenerateSubMelodyForMainAndChordText(style, emotion, requestedDensity, chordText, keyRoot, minorKey, barCount, mainYNote);
    }

    private static string NormalizeYNoteText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != ',' && c != ';').ToArray());
        if (cleaned.Length < 4)
            return string.Empty;

        StringBuilder sb = new();
        for (int i = 0; i + 4 <= cleaned.Length; i += 4)
        {
            string pitch = NormalizePitchToken(cleaned.Substring(i, 2));
            string rhythm = NormalizeRhythmToken(cleaned.Substring(i + 2, 2));

            if (GetDuration(rhythm) <= 0)
                rhythm = "04";

            sb.Append(pitch).Append(rhythm);
        }

        return sb.ToString();
    }

    private static int GetYNoteBarCount(string? yNote)
    {
        yNote = NormalizeYNoteText(yNote);
        if (string.IsNullOrWhiteSpace(yNote))
            return 0;

        int totalTicks = 0;
        foreach (var (_, rhythm) in ParseYNoteEvents(yNote))
        {
            int duration = GetDuration(rhythm);
            totalTicks += duration > 0 ? duration : TicksPerBeat;
        }

        if (totalTicks <= 0)
            return 0;

        return (int)Math.Ceiling(totalTicks / (double)TicksPerBar);
    }

    private static string NormalizeYNoteToBarCount(string? rawYNote, int barCount, string fallbackPitch)
    {
        string yNote = NormalizeYNoteText(rawYNote);
        if (barCount <= 0)
            barCount = 1;

        if (string.IsNullOrWhiteSpace(yNote))
        {
            string restBar = fallbackPitch == "00" ? "0001" : fallbackPitch + "01";
            return string.Concat(Enumerable.Repeat(restBar, barCount));
        }

        List<string> bars = SplitYNoteIntoBars(yNote, barCount);
        while (bars.Count < barCount)
            bars.Add("0001");

        if (bars.Count > barCount)
            bars = bars.Take(barCount).ToList();

        return string.Concat(bars.Select(NormalizeSingleBarYNote));
    }

    private static string NormalizeSingleBarYNote(string barYNote)
    {
        List<(string Pitch, string Rhythm)> events = ParseYNoteEvents(NormalizeYNoteText(barYNote));
        if (events.Count == 0)
            return "0001";

        StringBuilder sb = new();
        int tick = 0;

        foreach ((string pitch, string rhythm) in events)
        {
            if (tick >= TicksPerBar)
                break;

            int duration = GetDuration(rhythm);
            if (duration <= 0)
                duration = TicksPerBeat;

            int remaining = TicksPerBar - tick;
            if (duration > remaining)
            {
                string clipped = DurationToRhythmToken(remaining);
                sb.Append(pitch).Append(clipped);
                tick = TicksPerBar;
                break;
            }

            sb.Append(pitch).Append(rhythm);
            tick += duration;
        }

        if (tick < TicksPerBar)
            sb.Append("00").Append(DurationToRhythmToken(TicksPerBar - tick));

        return sb.ToString();
    }

    private static List<string> SplitYNoteIntoBars(string? yNote, int requestedBarCount = 0)
    {
        yNote = NormalizeYNoteText(yNote);
        List<string> bars = new();
        if (string.IsNullOrWhiteSpace(yNote))
        {
            for (int i = 0; i < requestedBarCount; i++)
                bars.Add("0001");
            return bars;
        }

        StringBuilder current = new();
        int tick = 0;

        foreach ((string pitch, string rhythm) in ParseYNoteEvents(yNote))
        {
            int duration = GetDuration(rhythm);
            if (duration <= 0)
                duration = TicksPerBeat;

            if (tick >= TicksPerBar)
            {
                bars.Add(NormalizeSingleBarYNote(current.ToString()));
                current.Clear();
                tick = 0;
            }

            current.Append(pitch).Append(rhythm);
            tick += duration;

            if (tick >= TicksPerBar)
            {
                bars.Add(NormalizeSingleBarYNote(current.ToString()));
                current.Clear();
                tick = 0;
            }
        }

        if (current.Length > 0)
            bars.Add(NormalizeSingleBarYNote(current.ToString()));

        while (requestedBarCount > 0 && bars.Count < requestedBarCount)
            bars.Add("0001");

        if (requestedBarCount > 0 && bars.Count > requestedBarCount)
            bars = bars.Take(requestedBarCount).ToList();

        return bars;
    }

    private static string YNoteToPreview(string? yNote)
    {
        List<string> tokens = ParseYNoteEvents(NormalizeYNoteText(yNote))
            .Take(12)
            .Select(e => e.Pitch == "00" ? $"Rest-{e.Rhythm}" : $"{PitchToDisplayName(e.Pitch)}-{e.Rhythm}")
            .ToList();

        if (tokens.Count == 0)
            return "Rest";

        return string.Join(" ", tokens);
    }

    private static string PitchToDisplayName(string pitch)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch == "00")
            return "Rest";

        string name = pitch[0] switch
        {
            'C' => "C",
            'c' => "C#",
            'D' => "D",
            'd' => "D#",
            'E' => "E",
            'F' => "F",
            'f' => "F#",
            'G' => "G",
            'g' => "G#",
            'A' => "A",
            'a' => "A#",
            'B' => "B",
            _ => pitch[0].ToString()
        };

        string octave = pitch.Length >= 2 ? pitch[1..] : string.Empty;
        return name + octave;
    }

    private static int NormalizeSemitone(int value)
    {
        int result = value % 12;
        return result < 0 ? result + 12 : result;
    }

    private static int PitchToAbsoluteSemitone(string pitch)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch == "00" || pitch.Length < 2)
            return 4 * 12;

        int pc = PitchLetterToSemitone(pitch[0]);
        int octave = int.TryParse(pitch[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 4;
        return octave * 12 + pc;
    }

    private static int PitchLetterToSemitone(char letter)
    {
        return letter switch
        {
            'C' => 0,
            'c' => 1,
            'D' => 2,
            'd' => 3,
            'E' => 4,
            'F' => 5,
            'f' => 6,
            'G' => 7,
            'g' => 8,
            'A' => 9,
            'a' => 10,
            'B' => 11,
            _ => 0
        };
    }

    private static string PitchFromSemitone(int pitchClass, int octave)
    {
        return PitchFromAbsoluteSemitone(octave * 12 + NormalizeSemitone(pitchClass));
    }

    private static string PitchFromAbsoluteSemitone(int abs)
    {
        int octave = Math.Clamp(abs / 12, 1, 7);
        int pc = NormalizeSemitone(abs);
        string letter = pc switch
        {
            0 => "C",
            1 => "c",
            2 => "D",
            3 => "d",
            4 => "E",
            5 => "F",
            6 => "f",
            7 => "G",
            8 => "g",
            9 => "A",
            10 => "a",
            11 => "B",
            _ => "C"
        };

        return letter + octave.ToString(CultureInfo.InvariantCulture);
    }

    private static int GetOctave(string pitch)
    {
        if (string.IsNullOrWhiteSpace(pitch) || pitch.Length < 2)
            return 4;

        return int.TryParse(pitch[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave) ? octave : 4;
    }

    private static string ClampPitch(string pitch, int minOctave, int maxOctave)
    {
        if (pitch == "00")
            return pitch;

        int abs = PitchToAbsoluteSemitone(pitch);
        int octave = abs / 12;

        while (octave < minOctave)
        {
            abs += 12;
            octave++;
        }

        while (octave > maxOctave)
        {
            abs -= 12;
            octave--;
        }

        return PitchFromAbsoluteSemitone(abs);
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

    private static string DurationToRhythmToken(int duration)
    {
        if (duration >= 1920) return "01";
        if (duration >= 1440) return "2.";
        if (duration >= 960) return "02";
        if (duration >= 720) return "4.";
        if (duration >= 480) return "04";
        if (duration >= 360) return "8.";
        if (duration >= 240) return "08";
        if (duration >= 120) return "16";
        return "32";
    }
}
