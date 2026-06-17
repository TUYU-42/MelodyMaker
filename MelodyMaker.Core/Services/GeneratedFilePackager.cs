using System.IO.Compression;

namespace MelodyMaker.Core.Services;

public sealed class GeneratedFilePackager
{
    public string CreateZip(string outputZipPath, IEnumerable<string> filePaths)
    {
        if (string.IsNullOrWhiteSpace(outputZipPath))
            throw new ArgumentException("Zip output path is empty.", nameof(outputZipPath));

        string? outputDirectory = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        if (File.Exists(outputZipPath))
            File.Delete(outputZipPath);

        using FileStream zipStream = new(outputZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Create);

        foreach (string filePath in filePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string entryName = Path.GetFileName(filePath);
            archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
        }

        return outputZipPath;
    }
}
