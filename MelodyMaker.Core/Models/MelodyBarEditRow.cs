namespace MelodyMaker.Core.Models;

/// <summary>
/// Web 版 Expert Mode 的單小節資料。
/// 對應原本 WinForms 的 groupBox2 動態小節編輯概念，但改成 Razor table / textarea 可傳回後端。
/// </summary>
public sealed class MelodyBarEditRow
{
    public int BarNumber { get; set; }
    public string ChordCode { get; set; } = string.Empty;
    public string ChordName { get; set; } = string.Empty;
    public string MainYNote { get; set; } = string.Empty;
    public string SubYNote { get; set; } = string.Empty;
    public string MainPreview { get; set; } = string.Empty;
    public string SubPreview { get; set; } = string.Empty;
}
