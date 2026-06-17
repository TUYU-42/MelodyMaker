namespace MelodyMaker.Core.Models;

public sealed class MelodyResult
{
    public string Title { get; set; } = "Generated Music";
    public string ChordText { get; set; } = string.Empty;
    public string MainYNote { get; set; } = string.Empty;
    public string SubYNote { get; set; } = string.Empty;
    public int Tempo { get; set; } = 80;
    public string Key { get; set; } = "C";
    public string Mode { get; set; } = "Major";
    public string AnalysisReport { get; set; } = string.Empty;
}
