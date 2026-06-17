using System.Net;
using System.Net.Mail;

namespace MelodyMaker.Web.Services;

public sealed class GeneratedScoreEmailService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public GeneratedScoreEmailService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public async Task SendScoreAsync(string jobId, string recipientEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Missing generated score id.", nameof(jobId));

        if (string.IsNullOrWhiteSpace(recipientEmail))
            throw new ArgumentException("Please enter an email address.", nameof(recipientEmail));

        string? host = _configuration["Email:SmtpHost"];
        string? from = _configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
        {
            throw new InvalidOperationException(
                "Email is not configured. Set Email:SmtpHost and Email:From in appsettings.json or user secrets.");
        }

        string outputFolder = GetOutputFolder(jobId);
        if (!Directory.Exists(outputFolder))
            throw new DirectoryNotFoundException("Cannot find the generated score folder.");

        string pdfPath = Path.Combine(outputFolder, "score.pdf");
        string midiPath = Path.Combine(outputFolder, "score.mid");
        List<string> missingFiles = new();
        if (!File.Exists(pdfPath))
            missingFiles.Add("score.pdf");

        if (!File.Exists(midiPath))
            missingFiles.Add("score.mid");

        if (missingFiles.Count > 0)
        {
            throw new FileNotFoundException(
                "Cannot email the score yet because these files were not generated: " +
                string.Join(", ", missingFiles) +
                ". Check the MuseScore warning above and confirm MuseScore:ExePath is correct.");
        }

        using MailMessage message = new(from, recipientEmail)
        {
            Subject = "Your MelodyMaker score",
            Body = "Attached are your generated MelodyMaker score files."
        };

        message.Attachments.Add(new Attachment(pdfPath));
        message.Attachments.Add(new Attachment(midiPath));

        using SmtpClient smtpClient = CreateClient(host);
        await smtpClient.SendMailAsync(message, cancellationToken);
    }

    private SmtpClient CreateClient(string host)
    {
        int port = int.TryParse(_configuration["Email:SmtpPort"], out int parsedPort)
            ? parsedPort
            : 587;

        SmtpClient client = new(host, port)
        {
            EnableSsl = bool.TryParse(_configuration["Email:EnableSsl"], out bool enableSsl)
                ? enableSsl
                : true
        };

        string? userName = _configuration["Email:UserName"];
        string? password = _configuration["Email:Password"];
        if (!string.IsNullOrWhiteSpace(userName))
            client.Credentials = new NetworkCredential(userName, password);

        return client;
    }

    private string GetOutputFolder(string jobId)
    {
        string safeJobId = Path.GetFileName(jobId);
        string generatedRoot = Path.GetFullPath(Path.Combine(_environment.WebRootPath, "generated"));
        string outputFolder = Path.GetFullPath(Path.Combine(generatedRoot, safeJobId));

        if (!outputFolder.StartsWith(generatedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid generated score id.");

        return outputFolder;
    }
}
