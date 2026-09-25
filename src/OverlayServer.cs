using System.Collections.Concurrent;
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
    // Local choices not yet acknowledged by the server, keyed like selections (origin|mapId).
    private readonly Dictionary<string, string> pendingSelections = new();
    private bool selectionSyncUnsupported;
    private long nextSelectionUpload;
    private int uploadSelections;
    /// <summary>Mirror of the connection toggle: selections are only sent to the server while uploading is enabled.</summary>
    public bool UploadSelections { get => Volatile.Read(ref uploadSelections) == 1; set => Volatile.Write(ref uploadSelections, value ? 1 : 0); }
    private readonly Dictionary<string, byte[]> assets = new();
    private readonly HttpClient http;
    private readonly Func<Uri, string?> loadCredential;
    private SyncSnapshot? latest;
    private CctHistory? history;
    private object? settingsView, updateView;
    private long insightsRequestedAt = long.MinValue / 2;
    /// <summary>Validated browser setting changes; the module applies them on the game thread.</summary>
    /// <remarks>Booleans are queued as 0/1; <c>backgroundOpacity</c> is an integer percentage.</remarks>
    public ConcurrentQueue<(string Key, int Value)> SettingChanges { get; } = new();
    /// <summary>Settings the control page may change. Everything else stays in the mod menu / settings file.</summary>
    public static readonly string[] EditableSettings = ["connectionEnabled", "diagnosticsEnabled", "checkUpdates", "updateDotInObs"];
    /// <summary>Integer settings and their inclusive range.</summary>
    public static readonly Dictionary<string, (int Min, int Max)> EditableNumbers = new() { ["backgroundOpacity"] = (0, 100) };
    private int backgroundOpacity = 100;
    /// <summary>OBS overlay panel opacity in percent; 100 is the theme as designed.</summary>
    public void PublishDisplay(int opacity) => Volatile.Write(ref backgroundOpacity, Math.Clamp(opacity, 0, 100));
    public Action? CheckUpdatesRequested { get; set; }
    /// <summary>Warning sink; the module forwards to the Everest log. Kept as a hook so tests run without Celeste.</summary>
    public Action<string>? Log { get; set; }
    /// <summary>True while a control page is polling charts; history capture is skipped otherwise.</summary>
    public bool InsightsWanted => Environment.TickCount64 - Interlocked.Read(ref insightsRequestedAt) < 10000;
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
    public void PublishHistory(CctHistory? value) => Volatile.Write(ref history, value);
    public void PublishSettings(object view) => Volatile.Write(ref settingsView, view);
    public void PublishUpdate(UpdateInfo? info, bool showInObs) => Volatile.Write(ref updateView, info == null ? null : new {
        available = info.Available, current = info.Current, latest = info.Latest, downloadUrl = info.DownloadUrl,
        releaseUrl = info.ReleaseUrl, checkedAt = info.CheckedAt, error = info.Error, showInObs });
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
                            lock (gate) {
                                context = value.Clone(); contextStatus = value.GetProperty("matched").GetBoolean() ? "ready" : "unmatched";
                                ReconcileSelection(value, baseUrl);
                            }
                            nextFetch = Environment.TickCount64 + 300000;
                        }
                    } catch (Exception) when (!stop.IsCancellationRequested) { lock (gate) contextStatus = context == null ? "unavailable" : "cached"; }
                }
                if (UploadSelections && !selectionSyncUnsupported && Environment.TickCount64 >= nextSelectionUpload)
                    await UploadSelection(baseUrl);
                await Task.Delay(500, stop.Token);
            }
        } catch (OperationCanceledException) { }
        finally { http.Dispose(); }
    }
    // The server is the source of truth for the current challenge; a local change that has not been uploaded yet wins.
    // Called under gate after each context fetch.
    private void ReconcileSelection(JsonElement value, string baseUrl) {
        // With uploads off the local file is authoritative; never let a stale server value replace an offline choice.
        if (!UploadSelections || !value.GetProperty("matched").GetBoolean()) return;
        string key = baseUrl + "|" + Id(value.GetProperty("map"));
        if (pendingSelections.ContainsKey(key)) return;
        var ids = value.GetProperty("challenges").EnumerateArray().Select(c => Id(c)).ToHashSet();
        string? remote = Id(value, "selectedChallengeId");
        if (remote != null && ids.Contains(remote)) {
            if (!selections.TryGetValue(key, out var local) || local != remote) { selections[key] = remote; SaveSelections(); }
        } else if (remote == null && selections.TryGetValue(key, out var local) && ids.Contains(local)) {
            // Choices made before the server stored selections: upload once so pushes and the online list use them.
            pendingSelections[key] = local; nextSelectionUpload = 0;
        }
    }
    private async Task UploadSelection(string baseUrl) {
        KeyValuePair<string, string> item;
        lock (gate) item = pendingSelections.FirstOrDefault(p => p.Key.StartsWith(baseUrl + "|", StringComparison.Ordinal));
        if (item.Key == null) return;
        try {
            var uri = RemoteUploader.ValidateOrigin(baseUrl);
            string? token = loadCredential(uri);
            if (token == null) { nextSelectionUpload = Environment.TickCount64 + 30000; return; }
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "api/tracker/challenge-selection")) {
                Content = new StringContent(SyncJson.Serialize(new { mapId = item.Key[(baseUrl.Length + 1)..], challengeId = item.Value }), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, stop.Token);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || status is 400 or 409) {
                // Accepted, or rejected as invalid (e.g. the challenge was removed): either way do not resend it.
                lock (gate) if (pendingSelections.TryGetValue(item.Key, out var current) && current == item.Value) pendingSelections.Remove(item.Key);
                if (!response.IsSuccessStatusCode) Log?.Invoke($"Challenge selection rejected: HTTP {status}");
            } else if (status == 404) {
                // Older server without selection storage: keep choices local only for this session.
                selectionSyncUnsupported = true; lock (gate) pendingSelections.Clear();
            } else nextSelectionUpload = Environment.TickCount64 + (status is 401 or 403 ? 60000 : 30000);
        } catch (Exception) when (!stop.IsCancellationRequested) { nextSelectionUpload = Environment.TickCount64 + 30000; }
    }
    private void SaveSelections() {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        File.WriteAllText(settingsFile + ".tmp", JsonSerializer.Serialize(selections)); File.Move(settingsFile + ".tmp", settingsFile, true);
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
            return Encoding.UTF8.GetBytes(SyncJson.Serialize(OverlayProjection.Build(snapshot, catalog, choices, mapId, selected, contextStatus,
                Volatile.Read(ref updateView), new { backgroundOpacity = Volatile.Read(ref backgroundOpacity) })));
        }
    }
    private byte[] Insights() {
        Interlocked.Exchange(ref insightsRequestedAt, Environment.TickCount64);
        var snapshot = Volatile.Read(ref latest); var past = Volatile.Read(ref history);
        string? mapName = null, campaign = null, challenge = null;
        lock (gate) {
            if (contextKey == origin + "|" + snapshot?.Live.Sid + "|" + snapshot?.Live.Side && context is { } c && c.GetProperty("matched").GetBoolean()) {
                var map = c.GetProperty("map"); var pack = map.GetProperty("campaign"); var selected = Selection(c);
                mapName = Text(map, "cnName") ?? Text(map, "name"); campaign = Text(pack, "cnName") ?? Text(pack, "name");
                var ch = c.GetProperty("challenges").EnumerateArray().FirstOrDefault(x => Id(x) == selected);
                challenge = ch.ValueKind == JsonValueKind.Object ? Text(ch, "name") : null;
            }
        }
        return Encoding.UTF8.GetBytes(SyncJson.Serialize(InsightsProjection.Build(snapshot?.Cct, snapshot?.Cct == null ? null : past, mapName, campaign, challenge)));
    }
    private static bool TryReadSettings(JsonElement body, List<(string, int)> changes) {
        if (body.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in body.EnumerateObject()) {
            if (EditableSettings.Contains(p.Name) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                changes.Add((p.Name, p.Value.GetBoolean() ? 1 : 0));
            else if (EditableNumbers.TryGetValue(p.Name, out var range) && p.Value.ValueKind == JsonValueKind.Number
                && p.Value.TryGetInt32(out int number) && number >= range.Min && number <= range.Max)
                changes.Add((p.Name, number));
            else return false;
        }
        return changes.Count > 0;
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
        if (first[0] == "POST") {
            // Same-origin page requests only: the custom header forces a CORS preflight that this server never grants.
            if (headers.GetValueOrDefault("Origin") != "http://" + host || headers.GetValueOrDefault("X-GoldenLink") != "overlay"
                || !int.TryParse(headers.GetValueOrDefault("Content-Length"), out int length) || length < 1 || length > 4096) {
                await Reply(stream, 403, "text/plain", [] , ct); return;
            }
            int start = end + 4;
            while (count - start < length) { int read = await stream.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) return; count += read; }
            using var doc = JsonDocument.Parse(buffer.AsMemory(start, length));
            bool accepted = false;
            if (path == "/api/overlay/selection") {
                lock (gate) {
                    var snapshot = Volatile.Read(ref latest);
                    if (contextKey == origin + "|" + snapshot?.Live.Sid + "|" + snapshot?.Live.Side && context is { } c && c.GetProperty("matched").GetBoolean()) {
                        string? id = Text(doc.RootElement, "challengeId"), mapId = Id(c.GetProperty("map"));
                        if (mapId == Text(doc.RootElement, "mapId") && c.GetProperty("challenges").EnumerateArray().Any(ch => Id(ch) == id)) {
                            selections[origin + "|" + mapId] = id!; SaveSelections();
                            pendingSelections[origin + "|" + mapId] = id!; nextSelectionUpload = 0; accepted = true;
                        }
                    }
                }
            } else if (path == "/api/overlay/settings") {
                var changes = new List<(string, int)>();
                if (accepted = TryReadSettings(doc.RootElement, changes)) foreach (var change in changes) SettingChanges.Enqueue(change);
            } else if (path == "/api/overlay/update-check") {
                CheckUpdatesRequested?.Invoke(); accepted = true;
            } else { await Reply(stream, 404, "text/plain", [], ct); return; }
            await Reply(stream, accepted ? 200 : 409, "application/json", Encoding.UTF8.GetBytes(accepted ? "{\"ok\":true}" : "{\"ok\":false}"), ct); return;
        }
        if (first[0] != "GET") { await Reply(stream, 405, "text/plain", [], ct); return; }
        if (path is "/api/overlay/state" or "/api/overlay/insights") {
            // A projection failure must reach the page as an error, not as a silently dropped connection.
            byte[] json; int status = 200;
            try { json = path == "/api/overlay/state" ? State() : Insights(); }
            catch (Exception ex) {
                status = 500; json = Encoding.UTF8.GetBytes(SyncJson.Serialize(new { ok = false, error = ex.GetType().Name }));
                Log?.Invoke($"Overlay {path}: {ex.GetType().Name}: {ex.Message}");
            }
            await Reply(stream, status, "application/json", json, ct); return;
        }
        if (path == "/api/overlay/settings") {
            await Reply(stream, 200, "application/json", Encoding.UTF8.GetBytes(SyncJson.Serialize(new {
                settings = Volatile.Read(ref settingsView), update = Volatile.Read(ref updateView) })), ct); return;
        }
        // "/" is the control hub; the fixed 1920x1080 overlays live on their own paths for OBS.
        if (path == "/") path = "/index.html";
        else if (path is "/apex" or "/orbit") path = "/overlay.html";
        if (!assets.TryGetValue(path, out var body) || path == "/demo.mjs") { await Reply(stream, 404, "text/plain", [], ct); return; }
        if (path.EndsWith(".html", StringComparison.Ordinal)) body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("<body>", "<body data-source=\"live\">"));
        string type = Path.GetExtension(path) switch { ".html" => "text/html", ".css" => "text/css", ".mjs" or ".js" => "text/javascript", _ => "application/octet-stream" };
        await Reply(stream, 200, type, body, ct);
    }
    private static async Task Reply(NetworkStream stream, int status, string type, byte[] body, CancellationToken ct) {
        string header = $"HTTP/1.1 {status} Response\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct); await stream.WriteAsync(body, ct);
    }
}
