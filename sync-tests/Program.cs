using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CNGoldenLink;

static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
static async Task Until(Func<bool> condition, string name) {
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while (!condition()) { if (limit.IsCancellationRequested) throw new Exception("Timeout: " + name); await Task.Delay(25); }
}
var state = new CctState(new("2.10.2", "0.2.0", "session", new(true, 20), new(1, 0),
    new(new[] { new RouteNode("房间😀", "cp-0", Array.Empty<string>(), false, "A\u2028B\u2029C") }, Array.Empty<string>(),
        "Test/Map", "Pack", "Map", "A-Side", new[] { new RouteCheckpoint("cp-0", "开始", "ST") })),
    new[] { new CctRoom("房间😀", new[] { true, false }, -1, 12, 3, 1, 0) });
var dataset = Guid.NewGuid().ToString();
SyncSnapshot Snapshot(CctState s) => new(new("Test/Map", "Normal", "房间😀", false, false, false, true, false, dataset),
    new(dataset, "Test/Map", "Normal", 0, s), new(dataset, "Test/Map", "Normal", null, TotalDeaths: 20, Completed: true), Environment.TickCount64);
var fixture = new { state, hash = SyncJson.Hash(SyncJson.Element(state)), canonical = SyncJson.Canonical(SyncJson.Element(state)) };
Check(fixture.hash == "b8498dfec05dace96631d58ea92989f86a43ce8f218f3996f53113c8365584e0", "frozen TypeScript cross-language hash vector");
if (args.Length > 0) await File.WriteAllTextAsync(args[0], SyncJson.Serialize(fixture));
Check(RemoteUploader.ValidateOrigin("https://example.test/").AbsoluteUri == "https://example.test/", "origin canonicalization");
foreach (var bad in new[] { "http://example.test", "https://a:b@example.test", "https://example.test/#x", "file:///x" }) {
    bool rejected = false; try { RemoteUploader.ValidateOrigin(bad); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "unsafe origin: " + bad);
}
Check(LoopbackAuthorization.ParseRequest("GET /cngoldenlink/?state=s&code=c HTTP/1.1\r\nHost: x\r\n\r\n", "s") == "c", "valid callback");
Check(LoopbackAuthorization.ParseRequest("GET /cngoldenlink/?state=x&code=c HTTP/1.1\r\n\r\n", "s") == null, "state mismatch");
Check(LoopbackAuthorization.ParseRequest("GET /cngoldenlink/?state=s&code=c&code=d HTTP/1.1\r\n\r\n", "s") == null, "duplicate code");

// Full native callback and PKCE exchange against an in-memory HTTP service. No real account or browser.
Task? browser = null; string? challenge = null, saved = null;
var handler = new FakeServer();
handler.OnToken = body => {
    var verifier = body.GetProperty("codeVerifier").GetString()!;
    var actual = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    Check(actual == challenge, "PKCE verifier matches browser challenge");
    Check(!body.TryGetProperty("code_verifier", out _), "server field is camelCase");
};
var uploader = new RemoteUploader("https://example.test", 1, handler, _ => null, (_, t) => saved = t, url => {
    var query = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    challenge = query["code_challenge"];
    browser = Task.Run(async () => {
        var redirect = new Uri(query["redirect_uri"]);
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, redirect.Port);
        using var stream = client.GetStream();
        var request = $"GET {redirect.AbsolutePath}?code=synthetic-code&state={query["state"]} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var response = new byte[2048]; int n = await stream.ReadAsync(response);
        Check(Encoding.UTF8.GetString(response, 0, n).StartsWith("HTTP/1.1 200"), "loopback callback completed");
    });
});
uploader.Publish(Snapshot(state));
await Until(() => handler.Baselines == 1, "baseline: " + uploader.Status);
Check(saved == "synthetic-token", "device token persisted through credential adapter");
if (browser != null) await browser;
var changed = state with { Rooms = new[] { state.Rooms[0] with { SuccessStreak = 2, PreviousAttempts = new[] { true, false, true, true } } } };
uploader.Publish(Snapshot(changed));
await Until(() => handler.Changes == 1, "incremental update");
Check(handler.LastPatch.GetProperty("replaceRooms").GetArrayLength() == 1, "only changed room uploaded");
int mutations = handler.Changes;
uploader.Publish(Snapshot(changed)); await Task.Delay(1200);
Check(handler.Changes == mutations, "unchanged data sends no mutation");
Check(handler.AreaWrites == 1, "unchanged area counters not reuploaded");
Check(handler.LastArea.GetProperty("completed").GetBoolean(), "native completion flag uploaded");
var at = Snapshot(state).Live with { HoldingGolden = true };
handler.LosePresenceAck = true;
uploader.ObserveLive(at with { Room = "a" });
uploader.ObserveLive(at with { Room = "b" });
uploader.ObserveLive(at with { Room = "c" });
uploader.ObserveLive(at with { Room = "c", HoldingGolden = false });
await Until(() => handler.LostPresenceBody != null, "rapid room batch submitted");
uploader.ObserveLive(at with { Room = "d" });
await Until(() => handler.PresenceRetried, "identical batch retry after lost ACK");
var lost = JsonDocument.Parse(handler.LostPresenceBody!).RootElement;
Check(lost.GetProperty("transitions").EnumerateArray().Select(x => x.GetProperty("room").GetString()).SequenceEqual(new[] { "a", "b", "c", "c" }), "all rapid transitions remain ordered");
Check(!lost.GetProperty("observation").GetProperty("holdingGolden").GetBoolean(), "final snapshot preserves death");
Check(lost.GetProperty("observation").GetProperty("datasetId").GetString() == dataset, "presence carries dataset identity");
await Until(() => handler.LastPresence.ValueKind == JsonValueKind.Object && handler.LastPresence.GetProperty("observation").GetProperty("room").GetString() == "d", "changes during retry preserved for next batch");

// Lost ACK after commit: next cycle reads the committed cursor, without applying data twice.
handler.LoseNextAck = true;
var changedAgain = changed with { Rooms = new[] { changed.Rooms[0] with { SuccessStreak = 3 } } };
uploader.Publish(Snapshot(changedAgain));
await Until(() => handler.Changes == 2, "server committed before disconnect");
await Until(() => uploader.Status == "connected", "recovered connection");
Check(handler.Changes == 2 && handler.StateReads >= 2, "ACK recovery does not double apply");
uploader.Stop(); await uploader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
Check(handler.Stops == 1, "disable sends stop");

var replayServer = new FakeServer();
var replay = new RemoteUploader("https://example.test", 1, replayServer, _ => "synthetic-token", (_, _) => {}, _ => {});
Check(replay.QueueSavedArea(new(dataset, "Offline/Map", "Normal", 8, TotalDeaths: 100)), "offline best queued on reconnect");
replay.Publish(new(new(null, null, null, null, null, null, true, false), null, null, Environment.TickCount64));
await Until(() => replayServer.AreaWrites == 1, "offline best uploaded while in menu");
replay.Stop(); await replay.Completion.WaitAsync(TimeSpan.FromSeconds(5));

// Berry collections, version telemetry and the idle heartbeat. Data uploads are woken by the game, not polled.
var berryServer = new FakeServer();
var events = new RemoteUploader("https://example.test", 1, berryServer, _ => "synthetic-token", (_, _) => {}, _ => {}, "0.3.0");
events.Publish(Snapshot(state));
await Until(() => berryServer.Baselines == 1, "first room sample uploaded");
Check(berryServer.StartVersion == "0.3.0", "presence start reports mod version");
int presences = berryServer.Presences;
await Until(() => berryServer.Presences >= presences + 2, "idle heartbeat keeps presence alive");
var golden = new BerryCollected(Guid.NewGuid().ToString(), dataset, "Test/Map", "Normal", "房间😀", "golden");
events.QueueBerry(golden);
await Until(() => berryServer.Berries.Count == 1, "golden berry delivered");
Check(berryServer.Berries[0].GetProperty("eventId").GetString() == golden.EventId && berryServer.Berries[0].GetProperty("berry").GetString() == "golden", "berry payload");
berryServer.RejectBerry = true;
events.QueueBerry(golden with { EventId = Guid.NewGuid().ToString(), Berry = "silver" });
await Until(() => berryServer.BerryRejects == 1, "old server rejects berry endpoint");
int baselines = berryServer.Baselines + berryServer.Changes;
await Task.Delay(1500);
Check(events.Status == "connected" && berryServer.Berries.Count == 1, "rejected berry dropped without breaking sync");
Check(berryServer.Baselines + berryServer.Changes == baselines, "no CCT upload without a new room sample");
events.Stop(); await events.Completion.WaitAsync(TimeSpan.FromSeconds(5));

var unauthorized = new FakeServer { RejectConfig = true };
var denied = new RemoteUploader("https://example.test", 1, unauthorized, _ => "synthetic-token", (_, _) => {}, _ => throw new Exception("must not open browser"));
await denied.Completion.WaitAsync(TimeSpan.FromSeconds(5));
Check(denied.Status == "authorization_required" && unauthorized.Presences == 0, "401 stops without repeated authorization");
Console.WriteLine("PASS: loopback/PKCE, origin validation, first baseline, incremental sync, unchanged suppression, lost ACK recovery, berries, heartbeat, version, stop and revoked token.");

sealed class FakeServer : HttpMessageHandler
{
    public Action<JsonElement>? OnToken;
    public volatile int Baselines, Changes, StateReads, AreaWrites, Stops, Presences;
    public volatile bool LoseNextAck, RejectConfig, RejectBerry;
    public volatile int BerryRejects;
    public string? StartVersion;
    public readonly List<JsonElement> Berries = new();
    public volatile bool LosePresenceAck, PresenceRetried;
    public string? LostPresenceBody;
    public JsonElement LastArea, LastPresence;
    public JsonElement LastPatch;
    private JsonElement? state, cursor;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) {
        string path = request.RequestUri!.AbsolutePath;
        JsonElement body = request.Content == null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellation)).RootElement.Clone();
        if (path != "/api/tracker/devices/token" && request.Headers.Authorization?.Parameter != "synthetic-token") throw new Exception("missing device token");
        object result = new { ok = true };
        switch (path) {
            case "/api/tracker/devices/token":
                OnToken?.Invoke(body); result = new { ok = true, accessToken = "synthetic-token", deviceId = Guid.NewGuid().ToString() }; break;
            case "/api/tracker/config":
                if (RejectConfig) return Response(new { ok = false, code = "device_unauthorized" }, HttpStatusCode.Unauthorized);
                break;
            case "/api/tracker/presence":
                Presences++;
                if (body.GetProperty("action").GetString() == "snapshot") {
                    LastPresence = body;
                    if (LosePresenceAck) { LosePresenceAck = false; LostPresenceBody = body.GetRawText(); throw new HttpRequestException("lost presence ACK"); }
                    if (LostPresenceBody == body.GetRawText()) PresenceRetried = true;
                }
                if (body.GetProperty("action").GetString() == "start") {
                    StartVersion = body.TryGetProperty("clientVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    result = new { ok = true, connectionId = Guid.NewGuid().ToString() };
                }
                if (body.GetProperty("action").GetString() == "stop") Stops++;
                break;
            case "/api/tracker/area-stats": LastArea = body; AreaWrites++; break;
            case "/api/tracker/berry":
                if (RejectBerry) { BerryRejects++; return Response(new { ok = false, code = "not_found" }, HttpStatusCode.NotFound); }
                lock (Berries) Berries.Add(body); break;
            case "/api/tracker/cct/state":
                StateReads++;
                result = cursor == null ? new { ok = true, current = (object?)null } : new { ok = true, current = (object?)new {
                    streamEpoch = cursor.Value.GetProperty("streamEpoch").GetString(), revision = cursor.Value.GetProperty("revision").GetInt64(),
                    stateHash = cursor.Value.GetProperty("stateHash").GetString(), state } }; break;
            case "/api/tracker/cct/baseline":
                state = body.GetProperty("state");
                cursor = SyncJson.Element(new { ok = true, streamEpoch = body.GetProperty("streamEpoch").GetString(), revision = 0, stateHash = SyncJson.Hash(state.Value) });
                result = cursor.Value; Baselines++; break;
            case "/api/tracker/cct/change":
                LastPatch = body.GetProperty("patch");
                state = SyncJson.Element(new { metadata = LastPatch.GetProperty("metadata"), rooms = LastPatch.GetProperty("replaceRooms") });
                if (SyncJson.Hash(state.Value) != body.GetProperty("afterStateHash").GetString()) throw new Exception("invalid after-state hash");
                cursor = SyncJson.Element(new { ok = true, streamEpoch = body.GetProperty("expected").GetProperty("streamEpoch").GetString(),
                    revision = body.GetProperty("revision").GetInt64(), stateHash = body.GetProperty("afterStateHash").GetString() });
                result = cursor.Value; Changes++;
                if (LoseNextAck) { LoseNextAck = false; throw new HttpRequestException("synthetic lost response"); }
                break;
            default: throw new Exception("Unexpected endpoint " + path);
        }
        return Response(result);
    }
    private static HttpResponseMessage Response(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(SyncJson.Serialize(body), Encoding.UTF8, "application/json") };
}
