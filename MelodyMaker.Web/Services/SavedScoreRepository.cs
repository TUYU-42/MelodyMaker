using System.Security.Cryptography;
using System.Text.Json;

namespace MelodyMaker.Web.Services;

public sealed class SavedScoreRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IWebHostEnvironment _environment;
    private readonly object _sync = new();

    public SavedScoreRepository(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public SavedScoreRecord Save(SavedScoreRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.JobId))
            throw new ArgumentException("Cannot save a score without a job id.", nameof(record));

        lock (_sync)
        {
            List<SavedScoreRecord> records = ReadAllUnsafe();
            record.Password = GenerateUniquePassword(records);
            record.CreatedUtc = DateTime.UtcNow;
            records.Add(record);
            WriteAllUnsafe(records);
            return record;
        }
    }

    public SavedScoreRecord? FindByPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return null;

        string normalized = NormalizePassword(password);

        lock (_sync)
        {
            return ReadAllUnsafe()
                .LastOrDefault(r => string.Equals(r.Password, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    private string StorePath
    {
        get
        {
            string dataFolder = Path.Combine(_environment.ContentRootPath, "App_Data");
            Directory.CreateDirectory(dataFolder);
            return Path.Combine(dataFolder, "SavedScores.json");
        }
    }

    private List<SavedScoreRecord> ReadAllUnsafe()
    {
        string path = StorePath;
        if (!File.Exists(path))
            return new List<SavedScoreRecord>();

        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return new List<SavedScoreRecord>();

        return JsonSerializer.Deserialize<List<SavedScoreRecord>>(json, JsonOptions)
            ?? new List<SavedScoreRecord>();
    }

    private void WriteAllUnsafe(List<SavedScoreRecord> records)
    {
        File.WriteAllText(StorePath, JsonSerializer.Serialize(records, JsonOptions));
    }

    private static string GenerateUniquePassword(IReadOnlyCollection<SavedScoreRecord> records)
    {
        HashSet<string> used = records
            .Select(r => r.Password)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string password;
        do
        {
            password = RandomNumberGenerator.GetHexString(8);
        }
        while (used.Contains(password));

        return password;
    }

    private static string NormalizePassword(string password)
    {
        return new string(password.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }
}
