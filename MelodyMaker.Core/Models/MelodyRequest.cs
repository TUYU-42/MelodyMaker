namespace MelodyMaker.Core.Models;

public sealed class MelodyRequest
{
    public string ChordText { get; set; } = string.Empty;
    public int Tempo { get; set; } = 60;
    public string Style { get; set; } = "江南風格";
    public string Emotion { get; set; } = "Neutral";
    public string Key { get; set; } = "Auto";
    public string Mode { get; set; } = "Auto";
    public string SubDensity { get; set; } = "Medium";
    public int SongMinutes { get; set; } = 1;

    // Step 5：Web 版 Keyboard-style Seed Input。
    // 原 WinForms 的鍵盤輸入事件不能直接搬到 Web，改成由 Razor/JavaScript 收集 seed，再傳給 Core generator。
    public bool UseSeedInput { get; set; }
    public string SeedText { get; set; } = string.Empty;
    public string SeedMode { get; set; } = "Motif";
    public int KeyboardOctave { get; set; } = 4;

    // Step 6：Web 版 Expert Mode。
    // 對應原 WinForms 的多小節編輯區與 Update Sub Melody。
    public bool UseExpertMode { get; set; }
    public string ExpertChordText { get; set; } = string.Empty;
    public string ExpertMainYNote { get; set; } = string.Empty;
    public string ExpertSubYNote { get; set; } = string.Empty;
}
