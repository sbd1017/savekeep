using System.Reflection;
using System.Text.Json.Serialization;
using SaveKeep.Core.Games;
using SaveKeep.Core.Saves;
using SaveKeep.Web;

// The content root is the install directory, so wwwroot and appsettings.json are found from any working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddJsonFile("/etc/savekeep/savekeep.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("SAVEKEEP_");
var options = builder.Configuration.GetSection("SaveKeep").Get<SaveKeepOptions>() ?? new SaveKeepOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new AppState(options));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = SaveReader.MaxSaveSize + 64 * 1024);

var app = builder.Build();
app.UseMiddleware<LanOnlyMiddleware>();
app.UseMiddleware<JksvDavMiddleware>(); // /dav/jksv only
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", (AppState s) =>
{
    var (bytes, over) = s.Vault.Usage();
    return Results.Ok(new
    {
        app = "SaveKeep",
        version = typeof(AppState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
        saves = s.Vault.List().Count,
        totalBytes = bytes,
        overLimit = over,
        webdav = s.Jksv.Enabled,
        ftpPort = s.Options.FtpPort,
    });
});

api.MapGet("/games", () => Results.Ok(KnownGames.All.Select(g => new { g.Id, g.Edition, g.Pair, platform = g.Platform == GamePlatform.Switch ? "Switch" : "3DS", g.TitleId, g.Tool })));

api.MapGet("/audit", (AppState s, int? n) => Results.Ok(s.Audit.Tail(Math.Clamp(n ?? 100, 1, 1000))));

// Inspect an uploaded save, and keep a copy in the vault unless the form says keep=0.
api.MapPost("/inspect", async (HttpRequest http, AppState s, CancellationToken ct) =>
{
    if (!http.HasFormContentType) return Results.BadRequest(new { error = "Attach one save file." });
    var form = await http.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length is 0 or > SaveReader.MaxSaveSize) return Results.BadRequest(new { error = "Attach one save file." });
    var bytes = new byte[file.Length];
    await using (var st = file.OpenReadStream()) await st.ReadExactlyAsync(bytes, ct);
    SaveSummary summary;
    try { summary = SaveInspector.Inspect(bytes, file.FileName); }
    catch (InvalidDataException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
    {
        return Results.BadRequest(new { error = $"PKHeX recognised the save but couldn't read all of it ({e.Message})." });
    }
    var vault = form["keep"] == "0" ? null : VaultApi.StoreUpload(s, bytes, file.FileName);
    return Results.Ok(new
    {
        vault,
        summary = summary with { Slots = [] },
        boxes = summary.Slots.GroupBy(x => x.Box).Select(g => new
        {
            box = g.Key + 1,
            slots = g.Select(v => new { slot = v.Slot + 1, v.Empty, valid = v.ChecksumValid, v.Species, v.SpeciesName, v.Nickname, v.Shiny, v.Level, ot = v.OriginalTrainer, egg = v.IsEgg, v.Form }),
        }),
    });
});

VaultApi.Map(api);
BackupApi.Map(api);
JksvDavApi.Map(api);

app.Run();

public partial class Program;
