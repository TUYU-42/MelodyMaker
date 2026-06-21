using System.Text;
using MelodyMaker.Core.Models;
using MelodyMaker.Core.Services;
using MelodyMaker.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MelodyMaker.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IWebHostEnvironment _environment;
    private readonly MelodyGenerator _melodyGenerator;
    private readonly MusicXmlExporter _musicXmlExporter;
    private readonly MuseScoreConverter _museScoreConverter;
    private readonly GeneratedFilePackager _generatedFilePackager;
    private readonly JudgeExportWriter _judgeExportWriter;
    private readonly SavedScoreRepository _savedScoreRepository;
    private readonly GeneratedScoreEmailService _generatedScoreEmailService;

    public IndexModel(
        IWebHostEnvironment environment,
        MelodyGenerator melodyGenerator,
        MusicXmlExporter musicXmlExporter,
        MuseScoreConverter museScoreConverter,
        GeneratedFilePackager generatedFilePackager,
        JudgeExportWriter judgeExportWriter,
        SavedScoreRepository savedScoreRepository,
        GeneratedScoreEmailService generatedScoreEmailService)
    {
        _environment = environment;
        _melodyGenerator = melodyGenerator;
        _musicXmlExporter = musicXmlExporter;
        _museScoreConverter = museScoreConverter;
        _generatedFilePackager = generatedFilePackager;
        _judgeExportWriter = judgeExportWriter;
        _savedScoreRepository = savedScoreRepository;
        _generatedScoreEmailService = generatedScoreEmailService;
    }

    [BindProperty]
    public MelodyRequest Input { get; set; } = new()
    {
        Tempo = 60,
        Style = "江南風格",
        Emotion = "Neutral",
        Key = "Auto",
        Mode = "Auto",
        SubDensity = "Medium",
        SongMinutes = 1
    };

    [BindProperty]
    public string? CurrentJobId { get; set; }

    [BindProperty]
    public string? RecipientEmail { get; set; }

    [BindProperty]
    public string? LookupPassword { get; set; }

    [BindProperty]
    public string? PostedMusicXmlUrl { get; set; }

    [BindProperty]
    public string? PostedPdfUrl { get; set; }

    [BindProperty]
    public string? PostedMsczUrl { get; set; }

    [BindProperty]
    public string? PostedMidiUrl { get; set; }

    [BindProperty]
    public string? PostedAudioUrl { get; set; }

    [BindProperty]
    public string? PostedZipUrl { get; set; }

    [BindProperty]
    public string? PostedJudgeExportUrl { get; set; }

    [BindProperty]
    public string? PostedChordText { get; set; }

    [BindProperty]
    public string? PostedMainYNote { get; set; }

    [BindProperty]
    public string? PostedSubYNote { get; set; }

    [BindProperty]
    public string? PostedAnalysisReport { get; set; }

    public bool HasResult { get; private set; }
    public bool HasChordOnlyResult { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? WarningMessage { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? SavedPassword { get; private set; }
    public string? MusicXmlUrl { get; private set; }
    public string? PdfUrl { get; private set; }
    public string? MsczUrl { get; private set; }
    public string? MidiUrl { get; private set; }
    public string? AudioUrl { get; private set; }
    public string? ZipUrl { get; private set; }
    public string? JudgeExportUrl { get; private set; }
    public string? OutputChordText { get; private set; }
    public string? OutputMainYNote { get; private set; }
    public string? OutputSubYNote { get; private set; }
    public string? AnalysisReport { get; private set; }
    public IReadOnlyList<MelodyBarEditRow> ExpertRows { get; private set; } = Array.Empty<MelodyBarEditRow>();

    public void OnGet()
    {
    }

    public IActionResult OnPostGenerateChord()
    {
        try
        {
            string chordText = _melodyGenerator.GenerateChordText(Input);
            Input.ChordText = chordText;
            OutputChordText = chordText;
            HasChordOnlyResult = true;
            AnalysisReport = "已使用移植後的 WinForms chord progression 邏輯產生和弦。你可以直接按 Generate Music 生成樂譜。";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    public Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        return OnPostGenerateScore(cancellationToken);
    }

    public async Task<IActionResult> OnPostGenerateScore(CancellationToken cancellationToken)
    {
        try
        {
            MelodyResult result = _melodyGenerator.Generate(Input);
            await PublishResultAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.ToString();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostGenerateExpertScore(CancellationToken cancellationToken)
    {
        try
        {
            MelodyResult result = _melodyGenerator.GenerateFromExpert(Input);
            await PublishResultAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.ToString();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUpdateSubMelody(CancellationToken cancellationToken)
    {
        try
        {
            MelodyResult result = _melodyGenerator.RegenerateSubMelodyFromExpert(Input);
            await PublishResultAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.ToString();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostEmailGenerated(CancellationToken cancellationToken)
    {
        RestorePostedResultState();

        try
        {
            await _generatedScoreEmailService.SendScoreAsync(CurrentJobId ?? string.Empty, RecipientEmail ?? string.Empty, cancellationToken);
            SuccessMessage = "Email sent with the available score files.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    public IActionResult OnPostSaveGenerated()
    {
        RestorePostedResultState();

        try
        {
            SavedScoreRecord saved = _savedScoreRepository.Save(new SavedScoreRecord
            {
                JobId = CurrentJobId ?? string.Empty,
                Title = "Generated Music",
                MusicXmlUrl = PostedMusicXmlUrl ?? string.Empty,
                PdfUrl = PostedPdfUrl ?? string.Empty,
                MsczUrl = PostedMsczUrl ?? string.Empty,
                MidiUrl = PostedMidiUrl ?? string.Empty,
                AudioUrl = PostedAudioUrl ?? string.Empty,
                ZipUrl = PostedZipUrl ?? string.Empty,
                JudgeExportUrl = PostedJudgeExportUrl ?? string.Empty,
                ChordText = PostedChordText ?? string.Empty,
                MainYNote = PostedMainYNote ?? string.Empty,
                SubYNote = PostedSubYNote ?? string.Empty,
                AnalysisReport = PostedAnalysisReport ?? string.Empty
            });

            SavedPassword = saved.Password;
            SuccessMessage = $"Score saved. Password: {saved.Password}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    public IActionResult OnPostLoadSaved()
    {
        try
        {
            SavedScoreRecord? saved = _savedScoreRepository.FindByPassword(LookupPassword);
            if (saved is null)
            {
                ErrorMessage = "No saved score was found for that password.";
                return Page();
            }

            CurrentJobId = saved.JobId;
            MusicXmlUrl = EmptyToNull(saved.MusicXmlUrl);
            PdfUrl = EmptyToNull(saved.PdfUrl);
            MsczUrl = EmptyToNull(saved.MsczUrl);
            MidiUrl = EmptyToNull(saved.MidiUrl);
            AudioUrl = EmptyToNull(saved.AudioUrl);
            ZipUrl = EmptyToNull(saved.ZipUrl);
            JudgeExportUrl = EmptyToNull(saved.JudgeExportUrl);
            OutputChordText = saved.ChordText;
            OutputMainYNote = saved.MainYNote;
            OutputSubYNote = saved.SubYNote;
            AnalysisReport = saved.AnalysisReport;
            ExpertRows = _melodyGenerator.BuildExpertRows(new MelodyResult
            {
                ChordText = saved.ChordText,
                MainYNote = saved.MainYNote,
                SubYNote = saved.SubYNote
            });
            HasResult = true;
            SuccessMessage = $"Loaded saved score from {saved.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    private async Task PublishResultAsync(MelodyResult result, CancellationToken cancellationToken)
    {
        string jobId = DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Guid.NewGuid().ToString("N")[..8];
        CurrentJobId = jobId;
        string outputFolder = Path.Combine(_environment.WebRootPath, "generated", jobId);
        Directory.CreateDirectory(outputFolder);

        string musicXmlPath = Path.Combine(outputFolder, "score.musicxml");
        string pdfPath = Path.Combine(outputFolder, "score.pdf");
        string msczPath = Path.Combine(outputFolder, "score.mscz");
        string midiPath = Path.Combine(outputFolder, "score.mid");
        string mp3Path = Path.Combine(outputFolder, "score.mp3");
        string wavPath = Path.Combine(outputFolder, "score.wav");
        string zipPath = Path.Combine(outputFolder, "score_package.zip");
        string judgeExportPath = Path.Combine(outputFolder, "judge_export.txt");

        _musicXmlExporter.WriteScore(musicXmlPath, result);

        HasResult = true;
        MusicXmlUrl = $"/generated/{jobId}/score.musicxml";
        OutputChordText = result.ChordText;
        OutputMainYNote = result.MainYNote;
        OutputSubYNote = result.SubYNote;
        AnalysisReport = result.AnalysisReport;
        ExpertRows = _melodyGenerator.BuildExpertRows(result);
        _judgeExportWriter.WriteJudgeExport(judgeExportPath, result, ExpertRows);
        JudgeExportUrl = $"/generated/{jobId}/judge_export.txt";

        // 讓結果下方的 Expert Mode 編輯表單直接帶入這次輸出的資料。
        Input.ChordText = result.ChordText;
        Input.ExpertChordText = result.ChordText;
        Input.ExpertMainYNote = result.MainYNote;
        Input.ExpertSubYNote = result.SubYNote;
        Input.UseExpertMode = true;
        ModelState.Remove("Input.ChordText");
        ModelState.Remove("Input.ExpertChordText");
        ModelState.Remove("Input.ExpertMainYNote");
        ModelState.Remove("Input.ExpertSubYNote");
        ModelState.Remove("Input.UseExpertMode");

        StringBuilder warningBuilder = new();

        try
        {
            await _museScoreConverter.ConvertAsync(musicXmlPath, pdfPath, cancellationToken);
            await _museScoreConverter.ConvertAsync(musicXmlPath, msczPath, cancellationToken);
            await _museScoreConverter.ConvertAsync(musicXmlPath, midiPath, cancellationToken);

            PdfUrl = $"/generated/{jobId}/score.pdf";
            MsczUrl = $"/generated/{jobId}/score.mscz";
            MidiUrl = $"/generated/{jobId}/score.mid";
        }
        catch (Exception convertException)
        {
            warningBuilder.AppendLine("MusicXML 已經產生，但 MuseScore 轉 PDF / MSCZ / MIDI 時發生錯誤。");
            warningBuilder.AppendLine("請確認 MuseScore 已安裝，且 appsettings.json 的 MuseScore:ExePath 正確。");
            warningBuilder.AppendLine(convertException.Message);
        }

        if (warningBuilder.Length == 0)
        {
            try
            {
                await _museScoreConverter.ConvertAsync(musicXmlPath, mp3Path, cancellationToken);
                AudioUrl = $"/generated/{jobId}/score.mp3";
            }
            catch (Exception mp3Exception)
            {
                try
                {
                    await _museScoreConverter.ConvertAsync(musicXmlPath, wavPath, cancellationToken);
                    AudioUrl = $"/generated/{jobId}/score.wav";

                    warningBuilder.AppendLine("MuseScore MP3 匯出失敗，已自動改用 WAV 播放。");
                    warningBuilder.AppendLine(mp3Exception.Message);
                }
                catch (Exception wavException)
                {
                    warningBuilder.AppendLine("PDF / MSCZ / MIDI 已產生，但瀏覽器音訊檔 MP3 / WAV 匯出失敗。你仍可下載 MIDI 或 MSCZ。");
                    warningBuilder.AppendLine("MP3 error: " + mp3Exception.Message);
                    warningBuilder.AppendLine("WAV error: " + wavException.Message);
                }
            }
        }

        if (warningBuilder.Length > 0)
            WarningMessage = warningBuilder.ToString();

        string packagedZipPath = _generatedFilePackager.CreateZip(
            zipPath,
            new[] { musicXmlPath, pdfPath, msczPath, midiPath, mp3Path, wavPath, judgeExportPath });

        if (System.IO.File.Exists(packagedZipPath))
            ZipUrl = $"/generated/{jobId}/score_package.zip";
    }

    private void RestorePostedResultState()
    {
        MusicXmlUrl = PostedMusicXmlUrl;
        PdfUrl = PostedPdfUrl;
        MsczUrl = PostedMsczUrl;
        MidiUrl = PostedMidiUrl;
        AudioUrl = PostedAudioUrl;
        ZipUrl = PostedZipUrl;
        JudgeExportUrl = PostedJudgeExportUrl;
        OutputChordText = PostedChordText;
        OutputMainYNote = PostedMainYNote;
        OutputSubYNote = PostedSubYNote;
        AnalysisReport = PostedAnalysisReport;
        HasResult = !string.IsNullOrWhiteSpace(CurrentJobId) || !string.IsNullOrWhiteSpace(MusicXmlUrl);

        if (!string.IsNullOrWhiteSpace(OutputChordText) ||
            !string.IsNullOrWhiteSpace(OutputMainYNote) ||
            !string.IsNullOrWhiteSpace(OutputSubYNote))
        {
            ExpertRows = _melodyGenerator.BuildExpertRows(new MelodyResult
            {
                ChordText = OutputChordText ?? string.Empty,
                MainYNote = OutputMainYNote ?? string.Empty,
                SubYNote = OutputSubYNote ?? string.Empty
            });
        }
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
