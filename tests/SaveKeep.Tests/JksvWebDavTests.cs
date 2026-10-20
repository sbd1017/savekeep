using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SaveKeep.Core.Saves;
using SaveKeep.Core.Vault;
using SaveKeep.Sim;
using SaveKeep.Web;

namespace SaveKeep.Tests;

/// <summary>
/// The JKSV WebDAV receiver, served by a real Kestrel on loopback with the service's own middleware, and driven by a
/// simulated JKSV that sends the requests JKSV's source sends (J-D-K/JKSV: master src/webdav.cpp + src/fs/remote.cpp
/// + src/ui/fld.cpp for the 11/05/2024 release; rewrite source/remote/WebDav.cpp + source/appstates/BackupMenuState.cpp
/// for the 2025 releases).
/// </summary>
public class JksvWebDavTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- host

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required AppState State { get; init; }
        public required ManualClock Clock { get; init; }
        public required Uri Origin { get; init; }
        public HttpClient Http { get; } = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero }) { Timeout = TimeSpan.FromSeconds(30) };

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    /// <summary>The service's pipeline: LAN guard, then the receiver, then the API, on 127.0.0.1 with the same body limit.</summary>
    private static async Task<Host> StartAsync(string? dataDir = null)
    {
        var clock = new ManualClock(T0);
        var options = new SaveKeepOptions { DataDir = dataDir ?? TestEnv.TempDir() };
        var state = new AppState(options, clock);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = options.DataDir });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(state);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, 0);
            k.Limits.MaxRequestBodySize = SaveReader.MaxSaveSize + 64 * 1024;
        });
        var app = builder.Build();
        app.UseMiddleware<LanOnlyMiddleware>();
        app.UseMiddleware<JksvDavMiddleware>();
        var api = app.MapGroup("/api");
        VaultApi.Map(api);
        JksvDavApi.Map(api);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Host { App = app, State = state, Clock = clock, Origin = new Uri(address) };
    }

    /// <summary>Turns the receiver on through the API, as the web page does, and returns the webdav.json it shows.</summary>
    private static async Task<WebDavConfig> TurnOnAsync(Host host, bool replace = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(host.Origin, "/api/webdav/login"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { confirmReplace = replace }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(LanOnlyMiddleware.CsrfHeader, "1");
        var res = await host.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        var json = JsonDocument.Parse(body.GetProperty("webdavJson").GetString()!).RootElement;
        return new WebDavConfig(json.GetProperty("origin").GetString()!, json.GetProperty("basepath").GetString()!,
            json.GetProperty("username").GetString()!, json.GetProperty("password").GetString()!);
    }

    public sealed record WebDavConfig(string Origin, string BasePath, string Username, string Password);

    // ---------------------------------------------------------------- simulated JKSV

    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod MkCol = new("MKCOL");

    /// <summary>
    /// libcurl with CURLOPT_USERNAME/PASSWORD and the default CURLAUTH_BASIC sends Basic credentials up front.
    /// <paramref name="origin"/> is the Kestrel address (on a real server webdav.json's origin is its LAN address).
    /// </summary>
    private sealed class Jksv(HttpClient http, Uri origin, WebDavConfig cfg)
    {
        public List<string> Sent { get; } = [];
        private string Root => "/" + cfg.BasePath + "/";

        private async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? content = null, string? depth = null, bool chunked = false, bool expect = false)
        {
            var req = new HttpRequestMessage(method, origin.GetLeftPart(UriPartial.Authority) + path) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Username}:{cfg.Password}")));
            req.Headers.UserAgent.ParseAdd("JKSV");
            if (depth is not null) req.Headers.Add("Depth", depth);
            if (chunked) req.Headers.TransferEncodingChunked = true;
            if (expect) req.Headers.ExpectContinue = true;
            Sent.Add($"{method} {path}{(depth is null ? "" : " Depth:" + depth)}");
            return await http.SendAsync(req);
        }

        private static string Esc(string s) => Uri.EscapeDataString(s); // curl_easy_escape

        // ----- JKSV 11/05/2024 (master): src/fs/remote.cpp webDavInit, src/ui/fld.cpp

        /// <summary>webDavInit: dirExists("JKSV") is PROPFIND Depth 0 (CURLOPT_NOBODY) wanting 207; otherwise MKCOL, judged on curl success alone.</summary>
        public async Task<bool> ClassicInitAsync()
        {
            var root = Root + "JKSV/";
            if ((int)(await Send(PropFind, root, depth: "0")).StatusCode == 207) return true;
            await Send(MkCol, root);
            return true;
        }

        /// <summary>Folder menu for a title: create the title folder if PROPFIND says it is missing, then list it (Depth 1).</summary>
        public async Task<List<string>> ClassicOpenTitleAsync(string safeTitle)
        {
            var dir = Root + "JKSV/" + Esc(safeTitle) + "/";
            if ((int)(await Send(PropFind, dir, depth: "0")).StatusCode != 207) await Send(MkCol, dir);
            var list = await Send(PropFind, dir, depth: "1");
            return (int)list.StatusCode == 207 ? ChildNames(await list.Content.ReadAsStringAsync(), skipFirst: true) : [];
        }

        /// <summary>ZR: fileExists (PROPFIND Depth 0) decides update vs upload; both are a PUT with no length (chunked).</summary>
        public async Task<HttpResponseMessage> ClassicUploadAsync(string safeTitle, string fileName, byte[] zip)
        {
            var file = Root + "JKSV/" + Esc(safeTitle) + "/" + Esc(fileName);
            await Send(PropFind, file, depth: "0");
            return await Send(HttpMethod.Put, file, new StreamContent(new NonSeekable(zip)), chunked: true);
        }

        // ----- JKSV 2025 rewrite: source/remote/WebDav.cpp, source/appstates/BackupMenuState.cpp

        public sealed record Item(string Name, string Id, string Parent, bool IsDir);
        public List<Item> Listing { get; } = [];
        private string _parent = "";

        /// <summary>Constructor: PROPFIND Depth 1 of the basepath, then of every folder in it, recursively. Initialised only if the XML parses.</summary>
        public async Task<bool> RewriteInitAsync()
        {
            Listing.Clear();
            _parent = Root;
            return await ProcessListingAsync(Root);
        }

        private async Task<bool> ProcessListingAsync(string path)
        {
            var res = await Send(PropFind, path, depth: "1");
            XElement root;
            try { root = XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!; }
            catch (System.Xml.XmlException) { return false; }
            var responses = root.Elements().ToList();
            var parentHref = Find(responses.FirstOrDefault(), "href")?.Value;
            if (parentHref is null) return false;
            var parentId = parentHref.EndsWith('/') ? parentHref : parentHref + "/";
            foreach (var r in responses.Skip(1))
            {
                var href = Find(r, "href")?.Value;
                var resourceType = Find(Find(Find(r, "propstat"), "prop"), "resourcetype");
                if (href is null || resourceType is null) continue;
                var name = Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);
                if (Find(resourceType, "collection") is not null)
                {
                    Listing.Add(new Item(name, href.EndsWith('/') ? href : href + "/", parentId, true));
                    await ProcessListingAsync(href);
                }
                else Listing.Add(new Item(name, href, parentId, false));
            }
            return true;
        }

        /// <summary>BackupMenuState::initialize_remote_storage: find or MKCOL (must be 201) the title's folder, then change into it.</summary>
        public async Task OpenTitleAsync(string safeTitle)
        {
            _parent = Root;
            var dir = Listing.FirstOrDefault(i => i.IsDir && i.Parent == _parent && i.Name == safeTitle);
            if (dir is null)
            {
                var res = await Send(MkCol, _parent + Esc(safeTitle) + "/");
                if ((int)res.StatusCode != 201) return;
                // create_directory's id is m_parent + "/" + name + "/": a double slash the server must take.
                dir = new Item(safeTitle, _parent + "/" + Esc(safeTitle) + "/", _parent, true);
                Listing.Add(dir);
            }
            _parent = dir.Id;
        }

        public bool FileExists(string name) => Listing.Any(i => !i.IsDir && i.Parent == _parent && i.Name == name);

        /// <summary>ZR on a zip: upload_file is a PUT with CURLOPT_INFILESIZE_LARGE (Content-Length); curl asks Expect: 100-continue for large bodies.</summary>
        public async Task<HttpResponseMessage> UploadAsync(string fileName, byte[] zip)
        {
            Assert.False(FileExists(fileName)); // an existing name would be a patch_file (overwrite) instead
            var content = new ByteArrayContent(zip);
            var res = await Send(HttpMethod.Put, _parent + Esc(fileName), content, expect: true);
            Listing.Add(new Item(fileName, _parent + "/" + Esc(fileName), _parent, false));
            return res;
        }

        public IEnumerable<Item> Files => Listing.Where(i => !i.IsDir);

        private static XElement? Find(XElement? parent, string localName) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

        private static List<string> ChildNames(string xml, bool skipFirst) =>
            XDocument.Parse(xml).Root!.Elements().Skip(skipFirst ? 1 : 0)
                .Select(r => Uri.UnescapeDataString(Find(r, "href")!.Value.TrimEnd('/').Split('/')[^1])).ToList();
    }

    /// <summary>A body with no known length, so HttpClient sends it chunked as libcurl does without CURLOPT_INFILESIZE.</summary>
    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => base.Position; set => throw new NotSupportedException(); }
    }

    // ---------------------------------------------------------------- backups

    private static readonly byte[] MetaBin = Enumerable.Range(0, 0x4D).Select(i => (byte)i).ToArray();

    /// <summary>The rewrite's zip: <c>.nx_save_meta.bin</c> (write_meta_zip) then the save root, so <c>main</c> at the top.</summary>
    private static byte[] RewriteZip(byte[] main) => Zip(("main", main), (".nx_save_meta.bin", MetaBin));

    private static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static byte[] SwordMain(string ot = "SimTrainer") => SampleSaves.MainBytes(SampleSaves.SwordShield(ot));

    private static Dictionary<string, string> TreeExceptAudit(string dataDir) =>
        TestEnv.HashTree(dataDir).Where(kv => kv.Key != "audit.jsonl" && !kv.Key.StartsWith("webdav/uploads.jsonl")).ToDictionary();

    // ---------------------------------------------------------------- the request sequences

    [Fact]
    public async Task Rewrite_jksv_sequence_files_the_upload_into_the_vault()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        Assert.Equal("dav/jksv", cfg.BasePath);
        Assert.StartsWith("http://127.0.0.1:", cfg.Origin);
        Assert.False(cfg.Origin.EndsWith('/')); // JKSV: no trailing slash

        var jksv = new Jksv(host.Http, host.Origin, cfg);
        Assert.True(await jksv.RewriteInitAsync());
        Assert.Empty(jksv.Listing);
        await jksv.OpenTitleAsync("Pokemon Sword");
        var main = SwordMain();
        var zip = RewriteZip(main);
        var res = await jksv.UploadAsync("SimTrainer - 2026-10-20_12-00-00.zip", zip);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        Assert.Equal(
        [
            "PROPFIND /dav/jksv/ Depth:1",
            "MKCOL /dav/jksv/Pokemon%20Sword/",
            "PUT /dav/jksv//Pokemon%20Sword/SimTrainer%20-%202026-10-20_12-00-00.zip",
        ], jksv.Sent);

        var entry = Assert.Single(host.State.Vault.List());
        Assert.Equal(VaultSources.JksvWebDav, entry.Record.Source);
        Assert.Equal("sw", entry.Record.Game);
        Assert.Equal(SaveKeep.Core.Util.Hex.Sha256(main), entry.Record.Sha256);
        Assert.Equal(zip, host.State.Vault.ReadOriginalZip(entry.Ref)); // the exact JKSV zip, for restoring
        Assert.Contains("Pokemon Sword/SimTrainer - 2026-10-20_12-00-00.zip", entry.Record.SourceDetail);

        var upload = Assert.Single(host.State.Jksv.RecentUploads());
        Assert.Equal("added", upload.Outcome);
        Assert.Equal(entry.Ref.ToString(), upload.Entry);
        Assert.Empty(Directory.GetFiles(host.State.Jksv.IncomingDir)); // staging emptied

        // Next JKSV start: the folder is listed (clean href, no double slash), files never are.
        var again = new Jksv(host.Http, host.Origin, cfg);
        Assert.True(await again.RewriteInitAsync());
        var folder = Assert.Single(again.Listing);
        Assert.Equal(("Pokemon Sword", "/dav/jksv/Pokemon%20Sword/", true), (folder.Name, folder.Id, folder.IsDir));
        await again.OpenTitleAsync("Pokemon Sword");
        Assert.DoesNotContain(again.Sent, s => s.StartsWith("MKCOL"));

        // The same save again (new name, same main): not stored twice, noted as seen.
        var dup = await again.UploadAsync("SimTrainer - 2026-10-20_13-00-00.zip", RewriteZip(main));
        Assert.Equal(HttpStatusCode.NoContent, dup.StatusCode);
        Assert.Equal("PUT /dav/jksv/Pokemon%20Sword/SimTrainer%20-%202026-10-20_13-00-00.zip", again.Sent[^1]);
        var only = Assert.Single(host.State.Vault.List());
        Assert.Contains(only.Events, e => e.Kind == "seen" && e.Source == VaultSources.JksvWebDav);
        Assert.Equal("duplicate", host.State.Jksv.RecentUploads()[0].Outcome);

        // A different save is a new entry.
        Assert.Equal(HttpStatusCode.Created, (await again.UploadAsync("Rival.zip", RewriteZip(SwordMain("Rival")))).StatusCode);
        Assert.Equal(2, host.State.Vault.List().Count);
    }

    [Fact]
    public async Task Classic_jksv_sequence_with_a_chunked_upload_files_the_save()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        var jksv = new Jksv(host.Http, host.Origin, cfg);

        Assert.True(await jksv.ClassicInitAsync());
        var children = await jksv.ClassicOpenTitleAsync("Pokemon Shield");
        Assert.Empty(children);
        // The 2024 JKSV zips a folder backup itself, main at the root.
        var main = SampleSaves.MainBytes(SampleSaves.SwordShield());
        var res = await jksv.ClassicUploadAsync("Pokemon Shield", "SimTrainer - 2026-10-20_12-00-00.zip", Zip(("main", main)));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        Assert.Equal(
        [
            "PROPFIND /dav/jksv/JKSV/ Depth:0",
            "MKCOL /dav/jksv/JKSV/",
            "PROPFIND /dav/jksv/JKSV/Pokemon%20Shield/ Depth:0",
            "MKCOL /dav/jksv/JKSV/Pokemon%20Shield/",
            "PROPFIND /dav/jksv/JKSV/Pokemon%20Shield/ Depth:1",
            "PROPFIND /dav/jksv/JKSV/Pokemon%20Shield/SimTrainer%20-%202026-10-20_12-00-00.zip Depth:0",
            "PUT /dav/jksv/JKSV/Pokemon%20Shield/SimTrainer%20-%202026-10-20_12-00-00.zip",
        ], jksv.Sent);
        Assert.Single(host.State.Vault.List());

        // The next start finds both folders, and the uploaded file still does not exist as far as WebDAV goes.
        var again = new Jksv(host.Http, host.Origin, cfg);
        await again.ClassicInitAsync();
        Assert.Empty(await again.ClassicOpenTitleAsync("Pokemon Shield"));
        Assert.DoesNotContain(again.Sent, s => s.StartsWith("MKCOL"));

        // Folders survive a restart of the service (names only).
        var reopened = new JksvReceiver(host.State.Options.DataDir, host.State.Vault, host.State.Audit);
        Assert.True(reopened.FolderExists(["JKSV", "Pokemon Shield"]));
        Assert.Equal(["Pokemon Shield"], reopened.ChildFolders(["JKSV"]));
    }

    [Fact]
    public async Task A_zip_with_main_one_folder_down_is_accepted()
    {
        await using var host = await StartAsync();
        var jksv = new Jksv(host.Http, host.Origin, await TurnOnAsync(host));
        await jksv.RewriteInitAsync();
        await jksv.OpenTitleAsync("Pokemon Sword");
        var res = await jksv.UploadAsync("x.zip", Zip(("backup/main", SwordMain())));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Single(host.State.Vault.List());
    }

    // ---------------------------------------------------------------- login

    private async Task<HttpStatusCode> Status(Host host, HttpMethod method, string path, string? user, string? pass, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, new Uri(host.Origin, path)) { Content = content };
        if (user is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        return (await host.Http.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Requests_need_the_generated_login_and_the_receiver_on()
    {
        await using var host = await StartAsync();
        var zip = new ByteArrayContent(RewriteZip(SwordMain()));

        // Off: refused before any login is looked at.
        Assert.Equal(HttpStatusCode.Forbidden, await Status(host, PropFind, "/dav/jksv/", "jksv-x", "y"));
        Assert.False(host.State.Jksv.Enabled);

        var cfg = await TurnOnAsync(host);
        Assert.Matches("^jksv-[a-z2-9]{8}$", cfg.Username);
        Assert.Equal(24, cfg.Password.Length);

        var noAuth = await host.Http.SendAsync(new HttpRequestMessage(PropFind, new Uri(host.Origin, "/dav/jksv/")));
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        Assert.StartsWith("Basic", noAuth.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(host, PropFind, "/dav/jksv/", cfg.Username, "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(host, PropFind, "/dav/jksv/", "someone", cfg.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(host, HttpMethod.Put, "/dav/jksv/a.zip", cfg.Username, cfg.Password + "x", zip));
        var bearer = new HttpRequestMessage(PropFind, new Uri(host.Origin, "/dav/jksv/"));
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Http.SendAsync(bearer)).StatusCode);
        Assert.Equal((HttpStatusCode)207, await Status(host, PropFind, "/dav/jksv/", cfg.Username, cfg.Password));
        Assert.Empty(host.State.Vault.List());

        // Only a hash of the password is on disk.
        var stored = File.ReadAllText(Path.Combine(host.State.Jksv.Dir, "credentials.json"));
        Assert.DoesNotContain(cfg.Password, stored);
        Assert.Contains(cfg.Username, stored);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(host.State.Jksv.Dir, "credentials.json")));

        // A new login needs confirming and cuts off the old one.
        var post = new HttpRequestMessage(HttpMethod.Post, new Uri(host.Origin, "/api/webdav/login")) { Content = new StringContent("{\"confirmReplace\":false}", Encoding.UTF8, "application/json") };
        post.Headers.Add(LanOnlyMiddleware.CsrfHeader, "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Http.SendAsync(post)).StatusCode);
        var cfg2 = await TurnOnAsync(host, replace: true);
        Assert.NotEqual(cfg.Password, cfg2.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(host, PropFind, "/dav/jksv/", cfg.Username, cfg.Password));
        Assert.Equal((HttpStatusCode)207, await Status(host, PropFind, "/dav/jksv/", cfg2.Username, cfg2.Password));

        // The login survives a restart; turning off makes it useless.
        Assert.True(new JksvReceiver(host.State.Options.DataDir, host.State.Vault, host.State.Audit).CheckBasic(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg2.Username}:{cfg2.Password}"))));
        var off = new HttpRequestMessage(HttpMethod.Post, new Uri(host.Origin, "/api/webdav/off")) { Content = new StringContent("{\"confirm\":true}", Encoding.UTF8, "application/json") };
        off.Headers.Add(LanOnlyMiddleware.CsrfHeader, "1");
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(off)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(host, PropFind, "/dav/jksv/", cfg2.Username, cfg2.Password));
        Assert.False(File.Exists(Path.Combine(host.State.Jksv.Dir, "credentials.json")));
    }

    [Fact]
    public async Task Repeated_wrong_logins_pause_the_receiver()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        for (int i = 0; i < JksvReceiver.MaxAuthFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await Status(host, PropFind, "/dav/jksv/", cfg.Username, "guess" + i));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Status(host, PropFind, "/dav/jksv/", cfg.Username, cfg.Password));
        Assert.Contains(host.State.Audit.Tail(100), a => a.GetProperty("Event").GetString() == "webdav-locked");
        host.Clock.Advance(JksvReceiver.FailureWindow + TimeSpan.FromSeconds(1));
        Assert.Equal((HttpStatusCode)207, await Status(host, PropFind, "/dav/jksv/", cfg.Username, cfg.Password));
    }

    [Fact]
    public async Task Only_the_dav_path_skips_the_custom_header_and_the_lan_checks_still_apply()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        // The API still needs X-SaveKeep for anything but GET, even with the receiver's login.
        Assert.Equal(HttpStatusCode.Forbidden, await Status(host, HttpMethod.Post, "/api/webdav/off", cfg.Username, cfg.Password, new StringContent("{\"confirm\":true}", Encoding.UTF8, "application/json")));
        Assert.True(host.State.Jksv.Enabled);
        // Look-alike paths are not the receiver (case and suffix), so they are not exempt.
        Assert.Equal(HttpStatusCode.Forbidden, await Status(host, HttpMethod.Put, "/dav/jksvx/a.zip", cfg.Username, cfg.Password, new ByteArrayContent([1])));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(host, HttpMethod.Put, "/DAV/jksv/a.zip", cfg.Username, cfg.Password, new ByteArrayContent([1])));

        static async Task<int> Guard(string remote, string host, string method, string? forwarded = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            ctx.Request.Host = new HostString(host);
            ctx.Request.Method = method;
            ctx.Request.Path = "/dav/jksv/Pokemon Sword/a.zip";
            if (forwarded is not null) ctx.Request.Headers["X-Forwarded-For"] = forwarded;
            ctx.Response.Body = new MemoryStream();
            bool reached = false;
            await new LanOnlyMiddleware(_ => { reached = true; return Task.CompletedTask; }, new SaveKeepOptions(), NullLogger<LanOnlyMiddleware>.Instance).InvokeAsync(ctx);
            return reached ? 200 : ctx.Response.StatusCode;
        }
        Assert.Equal(200, await Guard("192.168.1.50", "192.168.1.20:8797", "PUT"));      // the Switch on the LAN, no custom header
        Assert.Equal(200, await Guard("192.168.1.50", "192.168.1.20:8797", "MKCOL"));
        Assert.Equal(403, await Guard("8.8.8.8", "192.168.1.20:8797", "PUT"));          // not on the LAN
        Assert.Equal(421, await Guard("192.168.1.50", "evil.example:8797", "PUT"));      // DNS rebinding
        Assert.Equal(403, await Guard("192.168.1.50", "192.168.1.20:8797", "PUT", forwarded: "8.8.8.8")); // proxied
    }

    // ---------------------------------------------------------------- upload only

    [Fact]
    public async Task Nothing_can_be_read_back_overwritten_or_deleted()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        var jksv = new Jksv(host.Http, host.Origin, cfg);
        await jksv.RewriteInitAsync();
        await jksv.OpenTitleAsync("Pokemon Sword");
        Assert.Equal(HttpStatusCode.Created, (await jksv.UploadAsync("save.zip", RewriteZip(SwordMain()))).StatusCode);
        var before = TreeExceptAudit(host.State.Options.DataDir);
        string file = "/dav/jksv/Pokemon%20Sword/save.zip";

        foreach (var m in new[] { HttpMethod.Get, HttpMethod.Head, new HttpMethod("MOVE"), new HttpMethod("COPY"), new HttpMethod("PROPPATCH"), new HttpMethod("LOCK"), HttpMethod.Post, HttpMethod.Patch })
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, await Status(host, m, file, cfg.Username, cfg.Password));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, await Status(host, m, "/dav/jksv/", cfg.Username, cfg.Password));
        }
        Assert.Equal(HttpStatusCode.Forbidden, await Status(host, HttpMethod.Delete, file, cfg.Username, cfg.Password));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(host, HttpMethod.Delete, "/dav/jksv/Pokemon%20Sword/", cfg.Username, cfg.Password));
        Assert.Equal(HttpStatusCode.NotFound, await Status(host, PropFind, file, cfg.Username, cfg.Password));
        var listing = await host.Http.SendAsync(Auth(new HttpRequestMessage(PropFind, new Uri(host.Origin, "/dav/jksv/Pokemon%20Sword/")), cfg));
        Assert.DoesNotContain("save.zip", await listing.Content.ReadAsStringAsync());

        var options = await host.Http.SendAsync(Auth(new HttpRequestMessage(HttpMethod.Options, new Uri(host.Origin, "/dav/jksv/")), cfg));
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.Equal("OPTIONS, PROPFIND, MKCOL, PUT", string.Join(", ", options.Content.Headers.Allow));

        Assert.Equal(before, TreeExceptAudit(host.State.Options.DataDir));

        // The same name again with a different save is a new vault entry; the first is untouched.
        var first = Assert.Single(host.State.Vault.List());
        var res = await Status(host, HttpMethod.Put, file, cfg.Username, cfg.Password, new ByteArrayContent(RewriteZip(SwordMain("Other"))));
        Assert.Equal(HttpStatusCode.Created, res);
        Assert.Equal(2, host.State.Vault.List().Count);
        Assert.Equal(first.Record, host.State.Vault.Load(first.Ref)!.Record);
        foreach (var (path, hash) in before) Assert.Equal(hash, TestEnv.HashTree(host.State.Options.DataDir)[path]);
    }

    private static HttpRequestMessage Auth(HttpRequestMessage req, WebDavConfig cfg)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Username}:{cfg.Password}")));
        req.Headers.Add("Depth", "1");
        return req;
    }

    // ---------------------------------------------------------------- traversal

    /// <summary>Sends a request exactly as written (HttpClient would tidy the path) and returns the status code.</summary>
    private static async Task<int> Raw(Host host, string method, string rawPath, WebDavConfig cfg, byte[]? body = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.Origin.Host, host.Origin.Port);
        var stream = tcp.GetStream();
        body ??= [];
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Username}:{cfg.Password}"));
        var head = $"{method} {rawPath} HTTP/1.1\r\nHost: {host.Origin.Authority}\r\nAuthorization: Basic {auth}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        return await ReadStatus(stream);
    }

    private static async Task<int> ReadStatus(NetworkStream stream)
    {
        var buf = new byte[64];
        int got = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (got < 12) got += await stream.ReadAsync(buf.AsMemory(got), cts.Token);
        return int.Parse(Encoding.ASCII.GetString(buf, 9, 3));
    }

    [Theory]
    [InlineData("PUT", "/dav/jksv/../../api/x.zip")]
    [InlineData("PUT", "/dav/jksv/%2e%2e/%2e%2e/x.zip")]
    [InlineData("PUT", "/dav/jksv/a/%2E%2E/%2E%2E/%2E%2E/x.zip")]
    [InlineData("PUT", "/dav/jksv/a%2F..%2F..%2Fx.zip")]
    [InlineData("PUT", "/dav/jksv/..%5C..%5Cx.zip")]
    [InlineData("PUT", "/dav/jksv/a/b/c/d/x.zip")]
    [InlineData("MKCOL", "/dav/jksv/%2e%2e/")]
    [InlineData("MKCOL", "/dav/jksv/a%2F..%2F..%2F/")]
    [InlineData("MKCOL", "/dav/jksv/a%00b/")]
    [InlineData("PROPFIND", "/dav/jksv/%2e%2e/%2e%2e/")]
    public async Task Traversal_attempts_store_nothing(string method, string path)
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        var before = TreeExceptAudit(host.State.Options.DataDir);
        var status = await Raw(host, method, path, cfg, method == "PUT" ? RewriteZip(SwordMain()) : null);

        Assert.Contains(status, new[] { 400, 403, 404, 405, 409 });
        Assert.Empty(host.State.Vault.List());
        Assert.Equal(before, TreeExceptAudit(host.State.Options.DataDir));
        Assert.Empty(host.State.Jksv.ChildFolders([]));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(host.State.Options.DataDir)!), f => f.EndsWith("x.zip"));
    }

    [Fact]
    public async Task Unsafe_names_are_refused_by_the_path_parser()
    {
        Assert.NotNull(JksvReceiver.ParsePath("/../x.zip", out _, out _));
        Assert.NotNull(JksvReceiver.ParsePath("/a/./x.zip", out _, out _));
        Assert.NotNull(JksvReceiver.ParsePath("/a\\b.zip", out _, out _));
        Assert.NotNull(JksvReceiver.ParsePath("/a%2Fb.zip", out _, out _));
        Assert.NotNull(JksvReceiver.ParsePath("/a\u0001.zip", out _, out _));
        Assert.NotNull(JksvReceiver.ParsePath("/" + new string('a', 256) + ".zip", out _, out _));
        Assert.NotNull(JksvReceiver.ParsePath("/a/b/c/d/", out _, out _));
        Assert.Null(JksvReceiver.ParsePath("//Pokemon Sword/SimTrainer - 2026-10-20_12-00-00.zip", out var segs, out var dir));
        Assert.Equal(["Pokemon Sword", "SimTrainer - 2026-10-20_12-00-00.zip"], segs);
        Assert.False(dir);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_zip_with_unsafe_entry_names_is_refused()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        var bad = Zip(("../main", SwordMain()));
        var status = await Status(host, HttpMethod.Put, "/dav/jksv/x.zip", cfg.Username, cfg.Password, new ByteArrayContent(bad));
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Empty(host.State.Vault.List());
        Assert.Contains("unsafe path", host.State.Jksv.RecentUploads()[0].Message);
    }

    // ---------------------------------------------------------------- size and content

    [Fact]
    public async Task Oversize_uploads_are_refused_with_or_without_a_length()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);

        // Declared too large: answered from the headers, before the body is read.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(host.Origin.Host, host.Origin.Port);
            var s = tcp.GetStream();
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Username}:{cfg.Password}"));
            await s.WriteAsync(Encoding.ASCII.GetBytes($"PUT /dav/jksv/big.zip HTTP/1.1\r\nHost: {host.Origin.Authority}\r\nAuthorization: Basic {auth}\r\nContent-Length: {JksvReceiver.MaxUploadBytes + 1}\r\nExpect: 100-continue\r\n\r\n"));
            Assert.Equal(413, await ReadStatus(s));
        }

        // Chunked, with no length: cut off once the limit is passed.
        var big = new byte[JksvReceiver.MaxUploadBytes + 100_000];
        big[0] = (byte)'P'; big[1] = (byte)'K'; big[2] = 3; big[3] = 4;
        var req = new HttpRequestMessage(HttpMethod.Put, new Uri(host.Origin, "/dav/jksv/big2.zip")) { Content = new StreamContent(new NonSeekable(big)) };
        req.Headers.TransferEncodingChunked = true;
        Auth(req, cfg);
        HttpStatusCode? code = null;
        try { code = (await host.Http.SendAsync(req)).StatusCode; }
        catch (HttpRequestException) { } // the server may close the connection while the client is still sending
        if (code is not null) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, code);

        Assert.Empty(host.State.Vault.List());
        Assert.Empty(Directory.GetFiles(host.State.Jksv.IncomingDir));
        Assert.All(host.State.Jksv.RecentUploads(), u => Assert.Equal("refused", u.Outcome));
        Assert.Contains(host.State.Jksv.RecentUploads(), u => u.Path == "big.zip");
    }

    [Fact]
    public async Task Files_that_are_not_save_backups_are_refused_and_listed()
    {
        await using var host = await StartAsync();
        var cfg = await TurnOnAsync(host);
        var main = SwordMain();
        var cases = new (string Name, byte[] Body, int Status, string Why)[]
        {
            ("nomain.zip", Zip(("readme.txt", "hello"u8.ToArray())), 422, "no 'main'"),
            ("junk.zip", Zip(("main", Enumerable.Repeat((byte)0x5A, 100_000).ToArray())), 422, "PKHeX does not recognise"),
            ("two.zip", Zip(("main", main), ("x/main", main)), 422, "more than one 'main'"),
            ("raw.zip", main, 415, "Not a zip"),
            ("main", RewriteZip(main), 415, "Only JKSV zip backups"),
            ("empty.zip", [], 400, "Empty"),
        };
        foreach (var (name, body, status, why) in cases)
        {
            Assert.Equal((HttpStatusCode)status, await Status(host, HttpMethod.Put, "/dav/jksv/" + name, cfg.Username, cfg.Password, new ByteArrayContent(body)));
            var u = host.State.Jksv.RecentUploads()[0];
            Assert.Equal(("refused", name), (u.Outcome, u.Path));
            Assert.Contains(why, u.Message);
        }
        Assert.Empty(host.State.Vault.List());
        Assert.Empty(Directory.GetFiles(host.State.Jksv.IncomingDir));
        Assert.Equal(cases.Length, host.State.Audit.Tail(200).Count(a => a.GetProperty("Event").GetString() == "webdav-upload-refused"));
    }

    [Fact]
    public async Task Leftover_staging_files_are_removed_at_start()
    {
        var dir = TestEnv.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "webdav", "incoming"));
        File.WriteAllBytes(Path.Combine(dir, "webdav", "incoming", "abc.part"), [1, 2, 3]);
        await using var host = await StartAsync(dir);
        Assert.Empty(Directory.GetFiles(host.State.Jksv.IncomingDir));
    }

    // ---------------------------------------------------------------- status for the web page

    [Fact]
    public async Task Status_shows_the_login_without_its_password_and_the_last_uploads()
    {
        await using var host = await StartAsync();
        var off = JsonDocument.Parse(await host.Http.GetStringAsync(new Uri(host.Origin, "/api/webdav"))).RootElement;
        Assert.False(off.GetProperty("enabled").GetBoolean());

        var cfg = await TurnOnAsync(host);
        var jksv = new Jksv(host.Http, host.Origin, cfg);
        await jksv.RewriteInitAsync();
        await jksv.OpenTitleAsync("Pokemon Sword");
        await jksv.UploadAsync("a.zip", RewriteZip(SwordMain()));
        await Status(host, HttpMethod.Put, "/dav/jksv/b.zip", cfg.Username, cfg.Password, new ByteArrayContent([1, 2, 3]));

        var text = await host.Http.GetStringAsync(new Uri(host.Origin, "/api/webdav"));
        Assert.DoesNotContain(cfg.Password, text);
        var on = JsonDocument.Parse(text).RootElement;
        Assert.True(on.GetProperty("enabled").GetBoolean());
        Assert.Equal(cfg.Username, on.GetProperty("username").GetString());
        Assert.Equal("dav/jksv", on.GetProperty("basepath").GetString());
        Assert.Equal(cfg.Origin, on.GetProperty("origin").GetString());
        Assert.False(string.IsNullOrEmpty(on.GetProperty("originWarning").GetString())); // loopback here; a real server shows its LAN address
        var uploads = on.GetProperty("uploads").EnumerateArray().ToList();
        Assert.Equal(["refused", "added"], uploads.Select(u => u.GetProperty("outcome").GetString()));
        Assert.Equal("Sword", uploads[1].GetProperty("edition").GetString());

        var vault = JsonDocument.Parse(await host.Http.GetStringAsync(new Uri(host.Origin, "/api/vault"))).RootElement;
        Assert.Equal("jksv-webdav", vault.GetProperty("games")[0].GetProperty("entries")[0].GetProperty("source").GetString());
    }

    [Fact]
    public void Webdav_json_has_the_keys_jksv_reads()
    {
        var json = JsonDocument.Parse(JksvDavApi.WebDavJson("http://192.168.1.20:8797", "jksv-abcdefgh", "pw")).RootElement;
        Assert.Equal(["origin", "basepath", "username", "password"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("http://192.168.1.20:8797", json.GetProperty("origin").GetString());
        Assert.Equal("dav/jksv", json.GetProperty("basepath").GetString()); // no leading or trailing slash
    }

    [Fact]
    public void Origin_uses_the_servers_own_lan_address()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalIpAddress = IPAddress.Parse("::ffff:192.168.1.20");
        ctx.Connection.LocalPort = 8797;
        ctx.Request.Host = new HostString("raspberrypi.local:8797");
        Assert.Equal(("http://192.168.1.20:8797", (string?)null), JksvDavApi.Origin(ctx));

        var local = new DefaultHttpContext();
        local.Connection.LocalIpAddress = IPAddress.Loopback;
        local.Connection.LocalPort = 8797;
        local.Request.Host = new HostString("localhost:8797");
        var (origin, warning) = JksvDavApi.Origin(local);
        Assert.Equal("http://localhost:8797", origin);
        Assert.NotNull(warning);
    }
}
