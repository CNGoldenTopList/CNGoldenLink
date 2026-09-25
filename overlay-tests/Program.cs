using CNGoldenLink;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static void Check(bool value, string name) { if (!value) throw new Exception(name); }
var route = new CctRoute([
    new("a", "start", ["a2"], false, "First"), new("b", "end", [], false, null),
    new("a", "end", [], false, null)], [], "Test/Map", "Pack", "Map", "Normal",
    [new("start", "Start", "ST"), new("end", "End", "EN")]);
var state = new CctState(new("test", "test", "test", new(false, 20), new(1, 1), route),
    [new("a", [true,false], 1, 2, 3, 1, 0), new("a2", [], 0, 0, 2, 1, 0), new("b", [true], 1, 1, 4, 2, 0)]);
SyncSnapshot Snapshot(string sid = "Test/Map") => new(new(sid, "Normal", "a2", false, false, true, true, false),
    new("dataset", sid, "Normal", 0, state), new("dataset", sid, "Normal", 12, TotalDeaths:2401), Environment.TickCount64);
var p = SyncJson.Element(OverlayProjection.Build(Snapshot(), new {}, [], null, null, "ready"));
Check(p.GetProperty("cct").GetProperty("roomCount").GetInt32() == 2, "grouped/repeated rooms count once");
Check(p.GetProperty("cct").GetProperty("successRate").GetDouble() == 50, "golden rate uses downstream deaths and wins");
Check(p.GetProperty("cct").GetProperty("checkpointIndex").GetInt32() == 1, "grouped member resolves checkpoint");
Check(p.GetProperty("cct").GetProperty("goldenPb").GetString() == "通关", "collected golden overrides death PB");
JsonElement Pb(CctState s) => SyncJson.Element(OverlayProjection.Build(Snapshot() with {
    Cct = Snapshot().Cct! with { State = s }
}, new {}, [], null, null, "ready")).GetProperty("cct");
var attempts = state with { Metadata = state.Metadata with { Chapter = new(0, 0) },
    Rooms = [new("a2", [], 0, 0, 2, 1, 0), new("b", [], 0, 0, 4, 0, 0)] };
var best = Pb(attempts);
Check(best.GetProperty("goldenPb").GetString() == "b", "total PB uses furthest route room");
Check(best.GetProperty("sessionGoldenPb").GetString() == "First", "session PB uses grouped member and custom name");
Check(best.GetProperty("goldenPbRoomIndex").GetInt32() == 2, "total PB room index counts grouped rooms once");
Check(best.GetProperty("sessionGoldenPbRoomIndex").GetInt32() == 1, "session PB progress is independent");
Check(Pb(attempts with { Rooms = [] }).GetProperty("goldenPbRoomIndex").ValueKind == JsonValueKind.Null, "missing PB has unknown progress");
Check(Pb(attempts with { Metadata = attempts.Metadata with { Route = route with {
    Nodes = [new("a", "start", ["a2"], true, "First"), new("b", "end", [], false, null)]
} } }).GetProperty("goldenPbRoomIndex").GetInt32() == 1, "non-gameplay rooms excluded from PB progress");
Check(p.GetProperty("cct").GetProperty("goldenPbRoomIndex").GetInt32() == 2, "completed PB fills progress");
Check(Pb(attempts with { Rooms = [] }).GetProperty("sessionGoldenPb").ValueKind == JsonValueKind.Null, "empty session has unknown PB");
Check(Pb(attempts with { Metadata = attempts.Metadata with { Route = route with { IgnoredRooms = ["b"] } } })
    .GetProperty("goldenPb").GetString() == "First", "ignored rooms do not contribute PB");
Check(Pb(attempts with { Metadata = attempts.Metadata with { Chapter = new(1, 0) } })
    .GetProperty("sessionGoldenPb").GetString() == "First", "lifetime completion does not replace session PB");

// Chart projection: same route semantics as the overlay, plus funnel, chance and history.
var history = new CctHistory(new DateTime(2026, 9, 26), ["a2", "b", null, "unknown"],
    [new(new DateTime(2026, 9, 25), 8, 3, 0.5f, "b", "a", 1.5f, 1.2f, 0, 0, 3)],
    new Dictionary<string, long> { ["a"] = 60000, ["a2"] = 30000, ["b"] = 1000 }, new Dictionary<string, long> { ["a"] = 6000 });
var ins = SyncJson.Element(InsightsProjection.Build(Snapshot().Cct, history, null, null, "FC"));
var insRooms = ins.GetProperty("rooms");
Check(ins.GetProperty("roomCount").GetInt32() == 2 && ins.GetProperty("winRoom").GetInt32() == 3, "insights count grouped/repeated rooms once");
Check(ins.GetProperty("totals").GetProperty("runs").GetInt64() == 10, "runs are golden deaths plus wins");
Check(insRooms[0].GetProperty("goldenDeaths").GetInt64() == 5 && insRooms[0].GetProperty("timeMs").GetInt64() == 90000, "grouped members merge deaths and time");
Check(insRooms[0].GetProperty("reached").GetInt64() == 10 && insRooms[1].GetProperty("reached").GetInt64() == 5, "funnel counts downstream deaths and wins");
Check(insRooms[0].GetProperty("choke").GetDouble() == 50, "choke rate is deaths over reached");
Check(insRooms[0].GetProperty("successRate").GetDouble() == 50 && insRooms[1].GetProperty("successRate").GetDouble() == 100, "window success rate");
Check(ins.GetProperty("totals").GetProperty("goldenChance").GetDouble() == 50, "golden chance multiplies downstream rates");
Check(ins.GetProperty("sessionRuns").EnumerateArray().Select(r => r.GetProperty("distance").GetInt32()).SequenceEqual([1, 2, 3, 0]), "run distances follow route; win is roomCount+1; off-route is 0");
var sessionsJson = ins.GetProperty("sessions");
Check(sessionsJson.GetArrayLength() == 2 && sessionsJson[0].GetProperty("pb").GetInt32() == 2 && sessionsJson[1].GetProperty("current").GetBoolean(), "history sessions plus current");
Check(sessionsJson[1].GetProperty("pb").GetInt32() == 3, "collected golden makes current PB a win");
Check(ins.GetProperty("checkpoints")[1].GetProperty("firstRoom").GetInt32() == 2, "checkpoint boundaries for charts");
Check(!SyncJson.Element(InsightsProjection.Build(null, null, null, null, null)).GetProperty("available").GetBoolean(), "no CCT, no charts");
// CCT writes "NaN" averages for days without golden runs; System.Text.Json rejects NaN.
var nanHistory = history with { Sessions = [new(new DateTime(2026, 9, 24), 0, 0, float.NaN, null, null, 0, float.NaN, 0, 0, 0)] };
var nan = SyncJson.Element(InsightsProjection.Build(Snapshot().Cct, nanHistory, null, null, null)).GetProperty("sessions")[0];
Check(nan.GetProperty("averageDistanceSession").ValueKind == JsonValueKind.Null && nan.GetProperty("successRate").ValueKind == JsonValueKind.Null, "NaN session values become gaps");

// Update manifest parsing: CDN manifest, GitHub fallback, trusted links only.
var manifest = JsonDocument.Parse("""{"schema":"cngoldenlink.release/1","version":"0.3.1","downloadUrl":"https://aliyun-static.diving-fish.com/cngist/CNGoldenLink-0.3.1.zip"}""").RootElement;
var parsed = UpdateChecker.Parse(manifest, "0.3.0")!;
Check(parsed.Available && parsed.Latest == "0.3.1" && parsed.DownloadUrl!.EndsWith("0.3.1.zip"), "CDN manifest newer version");
Check(!UpdateChecker.Parse(manifest, "0.3.1")!.Available && !UpdateChecker.Parse(manifest, "0.4.0")!.Available, "same or older release is not an update");
var github = JsonDocument.Parse("""{"tag_name":"v0.4.0","draft":false,"prerelease":false,"html_url":"https://github.com/CNGoldenTopList/CNGoldenLink/releases/tag/v0.4.0","assets":[{"browser_download_url":"https://github.com/CNGoldenTopList/CNGoldenLink/releases/download/v0.4.0/CNGoldenLink-0.4.0.zip"}]}""").RootElement;
Check(UpdateChecker.Parse(github, "0.3.0")!.DownloadUrl!.EndsWith("/CNGoldenLink-0.4.0.zip"), "GitHub release asset");
Check(UpdateChecker.Parse(JsonDocument.Parse("""{"tag_name":"v9.0.0","prerelease":true}""").RootElement, "0.3.0") == null, "prereleases ignored");
Check(UpdateChecker.Parse(JsonDocument.Parse("""{"version":"0.3.1","downloadUrl":"https://evil.test/x.zip"}""").RootElement, "0.3.0")!.DownloadUrl == null, "untrusted download host dropped");
using (var checker = new UpdateChecker("0.3.0", new ReleaseHandler(), [new("https://aliyun-static.diving-fish.com/fail.json"), new("https://api.github.com/latest")], TimeSpan.Zero)) {
    for (int i = 0; i < 50 && checker.Info.CheckedAt == null; i++) await Task.Delay(50);
    Check(checker.Info is { Available: true, Latest: "0.4.0" }, "CDN failure falls back to GitHub");
}

var probe = new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
string folder=Path.Combine(Path.GetTempPath(),"GoldenLinkOverlay-"+Guid.NewGuid());
var firstHandler = new ContextHandler();
using var server = new OverlayServer(port,"https://example.test",folder,firstHandler,_=>"test-token");
using var http = new HttpClient(new HttpClientHandler { UseProxy=false }) { BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(4) };
server.Publish(Snapshot(),"https://example.test");
JsonElement data=default;
for(int i=0;i<30;i++) {
    server.Publish(Snapshot(),"https://example.test");
    data=JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/state"));
    if(data.GetProperty("contextStatus").GetString()=="ready") break;
    await Task.Delay(100);
}
Check(data.GetProperty("contextStatus").GetString()=="ready","remote context loaded");
Check(data.GetProperty("selectedChallengeId").ValueKind==JsonValueKind.Null,"multiple challenges are not guessed");
Check((await http.GetStringAsync("/apex")).Contains("data-source=\"live\""),"embedded HTML defaults to actual local data");
var hub = await http.GetStringAsync("/");
Check(hub.Contains("hub.mjs") && hub.Contains("data-source=\"live\""), "root serves the control hub, not a fixed-size overlay");
Check((await http.GetStringAsync("/chart.umd.min.js")).Contains("Chart.js"), "chart library embedded, no CDN request");
Check(!server.InsightsWanted, "history capture idle until the hub asks");
data=JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/insights"));
Check(data.GetProperty("schema").GetString()=="goldenlink.insights/1" && data.GetProperty("rooms").GetArrayLength()==2 && server.InsightsWanted, "insights endpoint");
server.PublishSettings(new { version = "0.3.0" }); server.PublishUpdate(new("0.3.0", "0.3.1", "https://aliyun-static.diving-fish.com/x.zip", null, true), false);
data=JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/settings"));
Check(data.GetProperty("settings").GetProperty("version").GetString()=="0.3.0" && data.GetProperty("update").GetProperty("available").GetBoolean(), "settings view");
data=JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/state"));
Check(data.GetProperty("update").GetProperty("latest").GetString()=="0.3.1" && !data.GetProperty("update").GetProperty("showInObs").GetBoolean(), "overlay sees update flag");
async Task<HttpStatusCode> PostJson(string path, string json, bool trusted = true) {
    using var request=new HttpRequestMessage(HttpMethod.Post,path) {Content=new StringContent(json,Encoding.UTF8,"application/json")};
    request.Headers.Add("Origin",trusted?$"http://127.0.0.1:{port}":"https://example.test"); request.Headers.Add("X-GoldenLink","overlay");
    using var response=await http.SendAsync(request);return response.StatusCode;
}
Check(await PostJson("/api/overlay/settings","{\"connectionEnabled\":true}",false)==HttpStatusCode.Forbidden,"cross-origin settings rejected");
Check(await PostJson("/api/overlay/settings","{\"overlayPort\":1}")==HttpStatusCode.Conflict && server.SettingChanges.IsEmpty,"only whitelisted settings");
Check(await PostJson("/api/overlay/settings","{\"checkUpdates\":\"yes\"}")==HttpStatusCode.Conflict,"settings must be booleans");
Check(await PostJson("/api/overlay/settings","{\"checkUpdates\":false,\"updateDotInObs\":true}")==HttpStatusCode.OK,"settings accepted");
Check(server.SettingChanges.TryDequeue(out var first) && first==("checkUpdates",false) && server.SettingChanges.Count==1,"settings queued for the game thread");
bool checkRequested=false; server.CheckUpdatesRequested=()=>checkRequested=true;
Check(await PostJson("/api/overlay/update-check","{}")==HttpStatusCode.OK && checkRequested,"manual update check");
Check((await http.GetStringAsync("/app.mjs")).Contains("liveMode"),"embedded scripts are served");
async Task<HttpStatusCode> Select(bool trusted) {
    using var request=new HttpRequestMessage(HttpMethod.Post,"/api/overlay/selection") {Content=new StringContent("{\"mapId\":\"map\",\"challengeId\":\"fc\"}",Encoding.UTF8,"application/json")};
    request.Headers.Add("Origin",trusted?$"http://127.0.0.1:{port}":"https://example.test"); request.Headers.Add("X-GoldenLink","overlay");
    using var response=await http.SendAsync(request);return response.StatusCode;
}
Check(await Select(false)==HttpStatusCode.Forbidden,"cross-origin selections rejected");
Check(await Select(true)==HttpStatusCode.OK,"trusted selection accepted");
await Task.Delay(800);
Check(firstHandler.Posts.Count==0,"selection stays local while uploads are off");
server.UploadSelections=true;
for(int i=0;i<40&&firstHandler.Posts.Count==0;i++) await Task.Delay(50);
Check(firstHandler.Posts.Count==1 && firstHandler.Posts[0].GetProperty("mapId").GetString()=="map"
    && firstHandler.Posts[0].GetProperty("challengeId").GetString()=="fc","selection uploaded once uploads are on");
await Task.Delay(700);
Check(firstHandler.Posts.Count==1,"acknowledged selection is not resent");
data=JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/state"));
Check(data.GetProperty("catalog").GetProperty("challenge").GetString()=="FC","OBS sees shared selection");
Check(File.ReadAllText(Path.Combine(folder,"overlay-selections.json")).Contains("fc"),"selection persisted");
server.Publish(Snapshot("Other/Map"),"https://example.test");
data=JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/state"));
Check(data.GetProperty("mapId").ValueKind==JsonValueKind.Null,"old catalog not attributed to new map");
server.Dispose();await server.Completion;
using (var numericServer = new OverlayServer(port,"https://example.test",folder,new ContextHandler(true),_=>"test-token")) {
    numericServer.Publish(Snapshot(),"https://example.test");
    for (int i = 0; i < 30; i++) {
        data = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/state"));
        if (data.GetProperty("contextStatus").GetString() == "ready") break;
        await Task.Delay(100);
    }
    Check(data.GetProperty("mapId").GetString() == "676", "numeric remote map ID normalized to string");
    Check(data.GetProperty("choices")[0].GetProperty("id").GetString() == "754", "numeric challenge ID normalized to string");
    using var request = new HttpRequestMessage(HttpMethod.Post,"/api/overlay/selection") {
        Content = new StringContent("{\"mapId\":\"676\",\"challengeId\":\"2141\"}",Encoding.UTF8,"application/json")
    };
    request.Headers.Add("Origin", $"http://127.0.0.1:{port}"); request.Headers.Add("X-GoldenLink","overlay");
    using var response = await http.SendAsync(request);
    Check(response.StatusCode == HttpStatusCode.OK, "numeric remote challenge can be selected");
    data = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/state"));
    Check(data.GetProperty("selectedChallengeId").GetString() == "2141", "numeric selection retained");
    Check(data.GetProperty("catalog").GetProperty("challenge").GetString() == "FC", "numeric selection resolves catalog");
    numericServer.Dispose(); await numericServer.Completion;
}
async Task<JsonElement> Ready() {
    JsonElement state = default;
    for (int i = 0; i < 40; i++) {
        state = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync("/api/overlay/state"));
        if (state.GetProperty("contextStatus").GetString() == "ready") break;
        await Task.Delay(50);
    }
    return state;
}
// Server selection wins over nothing local, and is persisted for offline use.
string freshFolder = Path.Combine(Path.GetTempPath(), "GoldenLinkOverlay-" + Guid.NewGuid());
var remoteHandler = new ContextHandler { Remote = "c" };
using (var remote = new OverlayServer(port, "https://example.test", freshFolder, remoteHandler, _ => "test-token") { UploadSelections = true }) {
    remote.Publish(Snapshot(), "https://example.test");
    data = await Ready();
    Check(data.GetProperty("selectedChallengeId").GetString() == "c" && data.GetProperty("catalog").GetProperty("challenge").GetString() == "C", "server selection adopted");
    Check(File.ReadAllText(Path.Combine(freshFolder, "overlay-selections.json")).Contains("\"c\""), "server selection cached locally");
    await Task.Delay(700);
    Check(remoteHandler.Posts.Count == 0, "adopting the server value does not echo it back");
    remote.Dispose(); await remote.Completion;
}
// A stale server value never replaces a local choice while uploads are off.
using (var offline = new OverlayServer(port, "https://example.test", folder, new ContextHandler { Remote = "c" }, _ => "test-token")) {
    offline.Publish(Snapshot(), "https://example.test");
    data = await Ready();
    Check(data.GetProperty("selectedChallengeId").GetString() == "fc", "local choice kept while uploads are off");
    offline.Dispose(); await offline.Completion;
}
// Choices saved before the server stored selections are backfilled once; an older server (404) stops further attempts.
var backfillHandler = new ContextHandler();
using (var backfill = new OverlayServer(port, "https://example.test", folder, backfillHandler, _ => "test-token") { UploadSelections = true }) {
    backfill.Publish(Snapshot(), "https://example.test");
    await Ready();
    for (int i = 0; i < 40 && backfillHandler.Posts.Count == 0; i++) await Task.Delay(50);
    Check(backfillHandler.Posts.Count == 1 && backfillHandler.Posts[0].GetProperty("challengeId").GetString() == "fc", "existing local choice backfilled");
    backfill.Dispose(); await backfill.Completion;
}
var legacyHandler = new ContextHandler { Legacy = true };
using (var legacy = new OverlayServer(port, "https://example.test", folder, legacyHandler, _ => "test-token") { UploadSelections = true }) {
    legacy.Publish(Snapshot(), "https://example.test");
    await Ready();
    for (int i = 0; i < 40 && legacyHandler.Attempts == 0; i++) await Task.Delay(50);
    Check(await Select(true) == HttpStatusCode.OK, "selection still works locally on an older server");
    await Task.Delay(800);
    Check(legacyHandler.Attempts == 1, "404 stops selection uploads for the session");
    legacy.Dispose(); await legacy.Completion;
}
Console.WriteLine("Overlay projection, insights, update manifest, HTTP hub/assets, selection sync, settings, origin and map-switch checks passed.");

sealed class ReleaseHandler : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        if (request.RequestUri!.AbsolutePath == "/fail.json") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
            """{"tag_name":"v0.4.0","draft":false,"prerelease":false,"html_url":"https://github.com/CNGoldenTopList/CNGoldenLink/releases/tag/v0.4.0","assets":[]}""") });
    }
}
sealed class ContextHandler(bool numericIds = false):HttpMessageHandler {
    /// <summary>selectedChallengeId returned by the fake server; null means none.</summary>
    public string? Remote { get; init; }
    /// <summary>Older server: no selectedChallengeId field and 404 for the selection endpoint.</summary>
    public bool Legacy { get; init; }
    public List<JsonElement> Posts { get; } = new();
    public volatile int Attempts;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        if(request.Headers.Authorization?.Parameter!="test-token")throw new Exception("missing device auth");
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/tracker/challenge-selection") {
            Attempts++;
            if (Legacy) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (Posts) Posts.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
        string json = """
        {"ok":true,"schema":"goldenlink.context/1","sid":"Test/Map","side":"Normal","matched":true,"map":{"id":"map","name":"Map","cnName":null,"campaign":{"id":"pack","name":"Pack","cnName":null}},"challenges":[{"id":"c","name":"C","tier":null},{"id":"fc","name":"FC","tier":"h3"}],"selectedChallengeId":SELECTED}
        """.Replace("SELECTED", Remote == null ? "null" : "\"" + Remote + "\"");
        if (Legacy) json = json.Replace(",\"selectedChallengeId\":null", "");
        if (numericIds) json = json.Replace("\"id\":\"map\"", "\"id\":676").Replace("\"id\":\"pack\"", "\"id\":33")
            .Replace("\"id\":\"c\"", "\"id\":754").Replace("\"id\":\"fc\"", "\"id\":2141");
        return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)};
    }
}
