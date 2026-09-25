using System.Text.Json;

namespace CNGoldenLink;

public sealed record UpdateInfo(string Current, string? Latest = null, string? DownloadUrl = null, string? ReleaseUrl = null,
    bool Available = false, DateTimeOffset? CheckedAt = null, string? Error = null);

/// <summary>Background release check. Reads the CDN manifest written by CI, then GitHub as a fallback.
/// Never downloads or installs anything; the player opens the returned link themselves.</summary>
internal sealed class UpdateChecker : IDisposable
{
    public static readonly Uri[] DefaultSources = [
        new("https://aliyun-static.diving-fish.com/cngist/CNGoldenLink-latest.json"),
        new("https://api.github.com/repos/CNGoldenTopList/CNGoldenLink/releases/latest")];
    private static readonly string[] TrustedHosts = ["aliyun-static.diving-fish.com", "github.com"];
    private readonly HttpClient http;
    private readonly IReadOnlyList<Uri> sources;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private UpdateInfo info;
    public UpdateInfo Info => Volatile.Read(ref info);
    public Task Completion { get; }

    public UpdateChecker(string current, HttpMessageHandler? handler = null, IReadOnlyList<Uri>? sources = null,
        TimeSpan? initialDelay = null, TimeSpan? period = null) {
        info = new(current);
        this.sources = sources ?? DefaultSources;
        http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CNGoldenLink/" + current);
        Completion = Task.Run(() => Run(initialDelay ?? TimeSpan.FromSeconds(15), period ?? TimeSpan.FromHours(6)));
    }
    public void CheckNow() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    public void Dispose() { stop.Cancel(); }

    private async Task Run(TimeSpan initialDelay, TimeSpan period) {
        try {
            // Do not compete with game startup; one request per period is plenty for release cadence.
            await Wait(initialDelay);
            while (!stop.IsCancellationRequested) {
                Volatile.Write(ref info, await Check(Info.Current));
                await Wait(period);
            }
        } catch (OperationCanceledException) { }
        finally { http.Dispose(); }
    }
    private async Task Wait(TimeSpan delay) {
        try { await wake.WaitAsync(delay, stop.Token); } catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
    }
    private async Task<UpdateInfo> Check(string current) {
        string? error = null;
        foreach (var source in sources) {
            try {
                using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(256 * 1024);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop.Token));
                if (Parse(doc.RootElement, current) is { } result) return result;
                error = "invalid_manifest";
            } catch (Exception ex) when (!stop.IsCancellationRequested) { error = ex is HttpRequestException or TaskCanceledException ? "unreachable" : "invalid_manifest"; }
        }
        // Keep the last good answer; an offline check must not hide a known update.
        var previous = Info;
        return previous with { CheckedAt = DateTimeOffset.UtcNow, Error = error };
    }
    /// <summary>Accepts the CI manifest (<c>cngoldenlink.release/1</c>) or a GitHub release object.</summary>
    public static UpdateInfo? Parse(JsonElement value, string current) {
        string? version = Text(value, "version") ?? Text(value, "tag_name")?.TrimStart('v');
        if (version == null || !Version.TryParse(version, out var latest) || latest.Build < 0) return null;
        if (value.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (value.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        string? download = Trusted(Text(value, "downloadUrl"));
        if (download == null && value.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            download = assets.EnumerateArray().Select(a => Trusted(Text(a, "browser_download_url")))
                .FirstOrDefault(u => u != null && u.EndsWith($"/CNGoldenLink-{version}.zip", StringComparison.Ordinal));
        string? release = Trusted(Text(value, "releaseUrl") ?? Text(value, "html_url"));
        bool newer = Version.TryParse(current, out var installed) && latest > installed;
        return new(current, latest.ToString(3), download ?? release, release, newer, DateTimeOffset.UtcNow);
    }
    // Links are opened in the player's browser; only allow our CDN and GitHub over HTTPS.
    private static string? Trusted(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && TrustedHosts.Contains(uri.Host) ? uri.AbsoluteUri : null;
    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
