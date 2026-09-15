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

var probe = new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
string folder=Path.Combine(Path.GetTempPath(),"GoldenLinkOverlay-"+Guid.NewGuid());
using var server = new OverlayServer(port,"https://example.test",folder,new ContextHandler(),_=>"test-token");
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
Check((await http.GetStringAsync("/app.mjs")).Contains("liveMode"),"embedded scripts are served");
async Task<HttpStatusCode> Select(bool trusted) {
    using var request=new HttpRequestMessage(HttpMethod.Post,"/api/overlay/selection") {Content=new StringContent("{\"mapId\":\"map\",\"challengeId\":\"fc\"}",Encoding.UTF8,"application/json")};
    request.Headers.Add("Origin",trusted?$"http://127.0.0.1:{port}":"https://example.test"); request.Headers.Add("X-GoldenLink","overlay");
    using var response=await http.SendAsync(request);return response.StatusCode;
}
Check(await Select(false)==HttpStatusCode.Forbidden,"cross-origin selections rejected");
Check(await Select(true)==HttpStatusCode.OK,"trusted selection accepted");
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
Console.WriteLine("Overlay projection, HTTP assets, selection, origin and map-switch checks passed.");

sealed class ContextHandler(bool numericIds = false):HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        if(request.Headers.Authorization?.Parameter!="test-token")throw new Exception("missing device auth");
        string json = """
        {"ok":true,"schema":"goldenlink.context/1","sid":"Test/Map","side":"Normal","matched":true,"map":{"id":"map","name":"Map","cnName":null,"campaign":{"id":"pack","name":"Pack","cnName":null}},"challenges":[{"id":"c","name":"C","tier":null},{"id":"fc","name":"FC","tier":"h3"}]}
        """;
        if (numericIds) json = json.Replace("\"id\":\"map\"", "\"id\":676").Replace("\"id\":\"pack\"", "\"id\":33")
            .Replace("\"id\":\"c\"", "\"id\":754").Replace("\"id\":\"fc\"", "\"id\":2141");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
    }
}
