using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CNGoldenLink;

public sealed record LiveObservation(string? Sid, string? Side, string? Room, bool? Paused,
    bool? Transitioning, bool? HoldingGolden, bool CctAvailable, bool? CctTrackingPaused, string? DatasetId = null);
public sealed record CctRoom(string RoomKey, bool[] PreviousAttempts, int SuccessStreak,
    int SuccessStreakBest, int GoldenBerryDeaths, int GoldenBerryDeathsSession, int DeathsInCurrentRun);
public sealed record RouteNode(string RoomKey, string CheckpointKey, string[] GroupedRooms, bool IsNonGameplayRoom, string? CustomRoomName);
public sealed record RouteCheckpoint(string CheckpointKey, string? Name, string? Abbreviation);
public sealed record CctRoute(RouteNode[] Nodes, string[] IgnoredRooms, string? ChapterSID,
    string? CampaignName, string? ChapterName, string? SideName, RouteCheckpoint[] Checkpoints);
public sealed record CctSettings(bool TrackNegativeStreaks, int SelectedAttemptCount);
public sealed record ChapterCounts(int GoldenCollectedCount, int GoldenCollectedCountSession);
public sealed record CctMetadata(string CctVersion, string AdapterVersion, string CctSessionKey,
    CctSettings Settings, ChapterCounts Chapter, CctRoute? Route);
public sealed record CctState(CctMetadata Metadata, CctRoom[] Rooms);
public sealed record CctScope(string DatasetId, string Sid, string Side, string SegmentKey);
public sealed record CctCapture(string DatasetId, string Sid, string Side, int SegmentIndex, CctState State);
public sealed record SyncSnapshot(LiveObservation Live, CctCapture? Cct, AreaStatistics? Area,
    long CapturedAt, string? SamplingError = null);
/// <summary>Actual golden ("golden") or Collab Utils silver ("silver") berry collection, reported once per berry.</summary>
public sealed record BerryCollected(string EventId, string? DatasetId, string Sid, string Side, string? Room, string Berry);

// Local-only CCT history for the control page charts. Never uploaded; the sync protocol above stays unchanged.
public sealed record CctPastSession(DateTime Started, int GoldenDeaths, int GoldenDeathsSession, float SuccessRate,
    string? PbRoom, string? SessionPbRoom, float AverageRunDistance, float AverageRunDistanceSession,
    int Collections, int CollectionsSession, int Runs);
/// <param name="SessionRuns">Room keys where each golden run of the current session ended; null is a win.</param>
/// <param name="TimeSpentMs">Time spent per room key over all play, in milliseconds.</param>
public sealed record CctHistory(DateTime SessionStarted, string?[] SessionRuns, CctPastSession[] Sessions,
    IReadOnlyDictionary<string, long> TimeSpentMs, IReadOnlyDictionary<string, long> TimeSpentInRunsMs);

public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    // Match the server's canonicalCct: ordinal UTF-16 keys, JSON strings, finite integers.
    public static string Canonical(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => Quote(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => Quote(value.GetString()!),
        _ => value.GetRawText()
    };
    // JSON.stringify leaves valid supplementary Unicode and U+2028/U+2029 literal.
    // System.Text.Json's encoder escapes some of these even in UnsafeRelaxed mode.
    private static string Quote(string value) {
        var result = new StringBuilder("\"");
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            switch (c) {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) {
                        result.Append(c).Append(value[++i]);
                    } else if (c < 32 || char.IsSurrogate(c)) result.Append("\\u").Append(((int)c).ToString("x4"));
                    else result.Append(c);
                    break;
            }
        }
        return result.Append('"').ToString();
    }
    public static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value)))).ToLowerInvariant();
    public static CctScope Scope(CctCapture capture) {
        // CCT persists segments by index. Include route topology so deletion/reordering cannot mix different routes.
        // Display names do not define identity; changes to labels update metadata in the same scope.
        var topology = capture.State.Metadata.Route?.Nodes.Select(n => new { n.RoomKey, n.CheckpointKey, n.GroupedRooms, n.IsNonGameplayRoom });
        return new(capture.DatasetId, capture.Sid, capture.Side,
            $"segment-{capture.SegmentIndex}-" + Hash(Element(topology))[..24]);
    }
}
