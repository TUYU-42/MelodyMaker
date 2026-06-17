using System.Text;
using MelodyMaker.Core.Models;

namespace MelodyMaker.Core.Services;

/// <summary>
/// Step 8：把原 WinForms 內「Export for Judge / judge 檢查用輸出」的概念改成 Web 後端可用版本。
/// 這裡先輸出可讀的 txt 檔，方便比對 ChordText、主旋律、副旋律、每小節拆解與分析報告；
/// 後續如果比賽或評測規格需要固定格式，可以直接在這個 service 裡集中修改。
/// </summary>
public sealed class JudgeExportWriter
{
    public string WriteJudgeExport(
        string outputPath,
        MelodyResult result,
        IReadOnlyList<MelodyBarEditRow> expertRows)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Judge export output path is empty.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        StringBuilder sb = new();
        sb.AppendLine("# MelodyMaker Web Judge Export");
        sb.AppendLine("# Step 8 migrated from WinForms export/judge concept.");
        sb.AppendLine();
        sb.AppendLine("[META]");
        sb.AppendLine($"Title={result.Title}");
        sb.AppendLine($"Tempo={result.Tempo}");
        sb.AppendLine($"Key={result.Key}");
        sb.AppendLine($"Mode={result.Mode}");
        sb.AppendLine();

        sb.AppendLine("[RAW]");
        sb.AppendLine($"ChordText={result.ChordText}");
        sb.AppendLine($"MainYNote={result.MainYNote}");
        sb.AppendLine($"SubYNote={result.SubYNote}");
        sb.AppendLine();

        sb.AppendLine("[BARS]");
        sb.AppendLine("Bar\tChordCode\tChordName\tMainYNote\tSubYNote\tMainPreview\tSubPreview");
        foreach (MelodyBarEditRow row in expertRows)
        {
            sb.Append(row.BarNumber).Append('\t')
              .Append(row.ChordCode).Append('\t')
              .Append(row.ChordName).Append('\t')
              .Append(row.MainYNote).Append('\t')
              .Append(row.SubYNote).Append('\t')
              .Append(row.MainPreview).Append('\t')
              .AppendLine(row.SubPreview);
        }
        sb.AppendLine();

        sb.AppendLine("[ANALYSIS]");
        sb.AppendLine(result.AnalysisReport ?? string.Empty);

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }
}
