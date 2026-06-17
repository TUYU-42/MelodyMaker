using MelodyMaker.Core.Services;
using MelodyMaker.Web.Services;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// MelodyMaker services
builder.Services.AddSingleton<MelodyGenerator>(serviceProvider =>
{
    IWebHostEnvironment environment = serviceProvider.GetRequiredService<IWebHostEnvironment>();
    string dataFolder = Path.Combine(environment.ContentRootPath, "App_Data");
    return new MelodyGenerator(dataFolder);
});
builder.Services.AddSingleton<MusicXmlExporter>();
builder.Services.AddSingleton<GeneratedFilePackager>();
builder.Services.AddSingleton<JudgeExportWriter>();
builder.Services.AddSingleton<SavedScoreRepository>();
builder.Services.AddSingleton<GeneratedScoreEmailService>();
builder.Services.AddSingleton<MuseScoreConverter>(serviceProvider =>
{
    IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();
    string? museScorePath = configuration["MuseScore:ExePath"];
    return new MuseScoreConverter(museScorePath);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".musicxml"] = "application/vnd.recordare.musicxml+xml";
contentTypeProvider.Mappings[".mxl"] = "application/vnd.recordare.musicxml";
contentTypeProvider.Mappings[".mscz"] = "application/vnd.musescore";
contentTypeProvider.Mappings[".mid"] = "audio/midi";
contentTypeProvider.Mappings[".midi"] = "audio/midi";
contentTypeProvider.Mappings[".mp3"] = "audio/mpeg";
contentTypeProvider.Mappings[".wav"] = "audio/wav";
contentTypeProvider.Mappings[".ogg"] = "audio/ogg";
contentTypeProvider.Mappings[".zip"] = "application/zip";
contentTypeProvider.Mappings[".txt"] = "text/plain";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider
});

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
