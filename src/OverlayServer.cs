using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace CNGoldenLink;

internal sealed class OverlayServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly string settingsFile;
    private readonly object gate = new();
    private readonly Dictionary<string, string> selections;
    private readonly Dictionary<string, byte[]> assets = new();
    private readonly HttpClient http;
    private readonly Func<Uri, string?> loadCredential;
    private SyncSnapshot? latest;
    private JsonElement? context;
    private string? contextKey;
    private string contextStatus = "waiting", origin;
    private long nextFetch;
    public int Port { get; }
    public Task Completion { get; }

    public OverlayServer(int port, string baseUrl, string dataPath, HttpMessageHandler? handler = null, Func<Uri, string?>? loadCredential = null) {
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        this.loadCredential = loadCredential ?? CredentialStore.Load;
        Port = Math.Clamp(port, 1024, 65535); origin = baseUrl;
        settingsFile = Path.Combine(dataPath, "overlay-selections.json");
        try { selections = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(settingsFile)) ?? new(); }
        catch { selections = new(); }
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("OverlayAsset.", StringComparison.Ordinal))) {
            using var stream = assembly.GetManifestResourceStream(name)!; using var memory = new MemoryStream();
            stream.CopyTo(memory); assets["/" + name[13..]] = memory.ToArray();
        }
        listener = new(IPAddress.Loopback, Port); listener.Start(8);
        Completion = Task.WhenAll(Task.Run(Listen), Task.Run(FetchContext));
    }
    public void Publish(SyncSnapshot snapshot, string baseUrl) { Volatile.Write(ref latest, snapshot); Volatile.Write(ref origin, baseUrl); }
    public void Dispose() { stop.Cancel(); listener.Stop(); }

    private async Task FetchContext() {
        try {
            while (!stop.IsCancellationRequested) {
                var snapshot = Volatile.Read(ref latest); string baseUrl = Volatile.Read(ref origin);
                string key = baseUrl + "|" + snapshot?.Live.Sid + "|" + snapshot?.Live.Side;
                lock (gate) {
                    if (contextKey != key) { contextKey = key; context = null; contextStatus = "waiting"; nextFetch = 0; }
                }
                if (snapshot?.Live.Sid != null && Environment.TickCount64 >= nextFetch) {
                    nextFetch = Environment.TickCount64 + 30000;
                    try {
                        var uri = RemoteUploader.ValidateOrigin(baseUrl);
                        string? token = loadCredential(uri);
                        if (token == null) { lock (gate) contextStatus = "authorization_required"; }
                        else {
                            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri,
                                "api/tracker/overlay-context?sid=" + Uri.EscapeDataString(snapshot.Live.Sid) + "&side=" + Uri.EscapeDataString(snapshot.Live.Side!)));
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                            response.EnsureSuccessStatusCode();
                            await response.Content.LoadIntoBufferAsync(1024 * 1024);
                            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop.Token));
                            var value = doc.RootElement;
                            if (!value.GetProperty("ok").GetBoolean() || value.GetProperty("schema").GetString() != "goldenlink.context/1"
                                || value.GetProperty("sid").GetString() != snapshot.Live.Sid || value.GetProperty("side").GetString() != snapshot.Live.Side)
                                throw new FormatException("context_mismatch");
                            lock (gate) { context = value.Clone(); contextStatus = value.GetProperty("matched").GetBoolean() ? "ready" : "unmatched"; }
                            nextFetch = Environment.TickCount64 + 300000;
                        }
                    } catch (Exception) when (!stop.IsCancellationRequested) { lock (gate) contextStatus = context == null ? "unavailable" : "cached"; }
                }
                await Task.Delay(500, stop.Token);
            }
        } catch (OperationCanceledException) { }
        finally { http.Dispose(); }
    }
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    // Catalog IDs can be legacy strings or numeric IDs after the server migration.
    // Keep the local overlay/selection contract string-based for both formats.
    private static string? Id(JsonElement element, string name = "id") => element.TryGetProperty(name, out var v)
        ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
        : null;
    private string? Selection(JsonElement value) {
        if (value.GetProperty("map").ValueKind != JsonValueKind.Object) return null;
        string id = Id(value.GetProperty("map"))!;
        var choices = value.GetProperty("challenges");
        selections.TryGetValue(origin + "|" + id, out var selected);
        if (choices.EnumerateArray().Any(c => Id(c) == selected)) return selected;
        return choices.GetArrayLength() == 1 ? Id(choices[0]) : null;
    }
    private byte[] State() {
        lock (gate) {
            var snapshot = Volatile.Read(ref latest);
            var c = contextKey == origin + "|" + snapshot?.Live.Sid + "|" + snapshot?.Live.Side ? context : null;
            object catalog = new { mapName = snapshot?.Cct?.State.Metadata.Route?.ChapterName,
                campaign = snapshot?.Cct?.State.Metadata.Route?.CampaignName, verified = false };
            object[] choices = []; string? mapId = null, selected = null;
            if (c is { } value && value.GetProperty("matched").GetBoolean()) {
                var map = value.GetProperty("map"); var pack = map.GetProperty("campaign");
                mapId = Id(map); selected = Selection(value);
                var list = value.GetProperty("challenges").EnumerateArray().ToArray();
                var challenge = list.FirstOrDefault(ch => Id(ch) == selected);
                string? tier = challenge.ValueKind == JsonValueKind.Object ? Text(challenge, "tier") : null;
                catalog = new { mapName = Text(map, "cnName") ?? Text(map, "name"), mapNameEn = Text(map, "name"),
                    campaign = Text(pack, "cnName") ?? Text(pack, "name"),
                    challenge = challenge.ValueKind == JsonValueKind.Object ? Text(challenge, "name") : null,
                    tier, verified = true };
                choices = list.Select(ch => (object)new { id = Id(ch), name = Text(ch, "name"), tier = Text(ch, "tier") }).ToArray();
            }
            return Encoding.UTF8.GetBytes(SyncJson.Serialize(OverlayProjection.Build(snapshot, catalog, choices, mapId, selected, contextStatus)));
        }
    }
    private async Task Listen() {
        try {
            while (!stop.IsCancellationRequested) {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(2000);
                try { await Handle(client.GetStream(), timeout.Token); }
                catch (Exception) when (!stop.IsCancellationRequested) { }
            }
        } catch (Exception) when (stop.IsCancellationRequested) { }
    }
    private async Task Handle(NetworkStream stream, CancellationToken ct) {
        var buffer = new byte[16384]; int count = 0, end = -1;
        while (count < buffer.Length && end < 0) {
            int read = await stream.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) return;
            count += read; end = Encoding.UTF8.GetString(buffer, 0, count).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        if (end < 0) return;
        var lines = Encoding.UTF8.GetString(buffer, 0, end).Split("\r\n"); var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[1].StartsWith('/')) return;
        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        string host = headers.GetValueOrDefault("Host", "");
        if (host != $"localhost:{Port}" && host != $"127.0.0.1:{Port}") { await Reply(stream, 403, "text/plain", "Invalid host"u8.ToArray(), ct); return; }
        string path = first[1].Split('?')[0];
        if (first[0] == "POST" && path == "/api/overlay/selection") {
            if (headers.GetValueOrDefault("Origin") != "http://" + host || headers.GetValueOrDefault("X-GoldenLink") != "overlay"
                || !int.TryParse(headers.GetValueOrDefault("Content-Length"), out int length) || length < 1 || length > 4096) {
                await Reply(stream, 403, "text/plain", [] , ct); return;
            }
            int start = end + 4;
            while (count - start < length) { int read = await stream.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) return; count += read; }
            using var doc = JsonDocument.Parse(buffer.AsMemory(start, length));
            bool accepted = false;
            lock (gate) {
                var snapshot = Volatile.Read(ref latest);
                if (contextKey == origin + "|" + snapshot?.Live.Sid + "|" + snapshot?.Live.Side && context is { } c && c.GetProperty("matched").GetBoolean()) {
                    string? id = Text(doc.RootElement, "challengeId"), mapId = Id(c.GetProperty("map"));
                    if (mapId == Text(doc.RootElement, "mapId") && c.GetProperty("challenges").EnumerateArray().Any(ch => Id(ch) == id)) {
                        selections[origin + "|" + mapId] = id!;
                        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
                        File.WriteAllText(settingsFile + ".tmp", JsonSerializer.Serialize(selections)); File.Move(settingsFile + ".tmp", settingsFile, true); accepted = true;
                    }
                }
            }
            await Reply(stream, accepted ? 200 : 409, "application/json", Encoding.UTF8.GetBytes(accepted ? "{\"ok\":true}" : "{\"ok\":false}"), ct); return;
        }
        if (first[0] != "GET") { await Reply(stream, 405, "text/plain", [], ct); return; }
        if (path == "/api/overlay/state") { await Reply(stream, 200, "application/json", State(), ct); return; }
        if (path is "/" or "/apex" or "/orbit") path = "/index.html";
        if (!assets.TryGetValue(path, out var body) || path == "/demo.mjs") { await Reply(stream, 404, "text/plain", [], ct); return; }
        if (path == "/index.html") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("<body>", "<body data-source=\"live\">"));
        string type = Path.GetExtension(path) switch { ".html" => "text/html", ".css" => "text/css", ".mjs" => "text/javascript", _ => "application/octet-stream" };
        await Reply(stream, 200, type, body, ct);
    }
    private static async Task Reply(NetworkStream stream, int status, string type, byte[] body, CancellationToken ct) {
        string header = $"HTTP/1.1 {status} Response\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct); await stream.WriteAsync(body, ct);
    }
}
