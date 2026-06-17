namespace MelodyMaker.Web.Services;

public sealed class SavedScoreRecord
{
    public string Password { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Title { get; set; } = string.Empty;
    public string MusicXmlUrl { get; set; } = string.Empty;
    public string PdfUrl { get; set; } = string.Empty;
    public string MsczUrl { get; set; } = string.Empty;
    public string MidiUrl { get; set; } = string.Empty;
    public string AudioUrl { get; set; } = string.Empty;
    public string ZipUrl { get; set; } = string.Empty;
    public string JudgeExportUrl { get; set; } = string.Empty;
    public string ChordText { get; set; } = string.Empty;
    public string MainYNote { get; set; } = string.Empty;
    public string SubYNote { get; set; } = string.Empty;
    public string AnalysisReport { get; set; } = string.Empty;
}
