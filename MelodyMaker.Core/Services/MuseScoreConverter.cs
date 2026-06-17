using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MelodyMaker.Core.Services;

/// <summary>
/// Wraps MuseScore command-line export.
/// Input:  score.musicxml
/// Output: score.pdf / score.mscz / score.mid / score.svg ... depending on output file extension.
/// </summary>
public sealed class MuseScoreConverter
{
    private readonly string? _configuredMuseScorePath;

    public MuseScoreConverter(string? configuredMuseScorePath)
    {
        _configuredMuseScorePath = configuredMuseScorePath;
    }

    public async Task ConvertAsync(
        string inputMusicXmlPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputMusicXmlPath))
            throw new ArgumentException("MusicXML input path is empty.", nameof(inputMusicXmlPath));

        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("MuseScore output path is empty.", nameof(outputPath));

        if (!File.Exists(inputMusicXmlPath))
            throw new FileNotFoundException("找不到 MusicXML 檔案。", inputMusicXmlPath);

        string museScorePath = ResolveMuseScorePath();

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = museScorePath,
            Arguments = $"-o {Quote(outputPath)} {Quote(inputMusicXmlPath)}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process? process = Process.Start(processStartInfo);
        if (process is null)
            throw new InvalidOperationException("無法啟動 MuseScore。請確認 MuseScore 已安裝，且 appsettings.json 的 MuseScore:ExePath 正確。");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "MuseScore 轉檔失敗。" + Environment.NewLine +
                $"ExitCode: {process.ExitCode}" + Environment.NewLine +
                "STDOUT:" + Environment.NewLine + stdout + Environment.NewLine +
                "STDERR:" + Environment.NewLine + stderr);
        }

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException(
                "MuseScore 執行完成，但找不到輸出檔案。請確認輸出格式是否支援。",
                outputPath);
        }
    }

    private string ResolveMuseScorePath()
    {
        List<string> candidates = new();

        string? envPath = Environment.GetEnvironmentVariable("MUSESCORE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            candidates.Add(envPath);

        if (!string.IsNullOrWhiteSpace(_configuredMuseScorePath))
            candidates.Add(_configuredMuseScorePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add(@"C:\Program Files\MuseScore 4\bin\MuseScore4.exe");
            candidates.Add(@"C:\Program Files\MuseScore 4\bin\MuseScore 4.exe");
            candidates.Add(@"C:\Program Files\MuseScore 4\bin\MuseScore.exe");
            candidates.Add(@"C:\Program Files\MuseScore 3\bin\MuseScore3.exe");
            candidates.Add(@"C:\Program Files (x86)\MuseScore 4\bin\MuseScore4.exe");
        }
        else
        {
            // For Linux container / Azure Container Apps, install MuseScore and set ExePath to one of these commands.
            candidates.Add("musescore4");
            candidates.Add("musescore");
            candidates.Add("mscore");
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            // Full path case.
            if (File.Exists(candidate))
                return candidate;

            // Command name case, e.g. musescore4 in Linux PATH.
            if (!Path.IsPathFullyQualified(candidate) && !candidate.Contains(Path.DirectorySeparatorChar))
                return candidate;
        }

        StringBuilder message = new();
        message.AppendLine("找不到 MuseScore 執行檔。");
        message.AppendLine("請到 MelodyMaker.Web/appsettings.json 設定 MuseScore:ExePath，例如：");
        message.AppendLine(@"C:\Program Files\MuseScore 4\bin\MuseScore4.exe");
        message.AppendLine("也可以設定環境變數 MUSESCORE_PATH。");
        message.AppendLine("目前檢查過的候選路徑：");
        foreach (string candidate in candidates)
            message.AppendLine("- " + candidate);

        throw new FileNotFoundException(message.ToString());
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
