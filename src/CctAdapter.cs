using Celeste.Mod.ConsistencyTracker;

namespace CNGoldenLink;

/// <summary>Public API adapter compiled against the configured CCT dependency.
/// Called only on the game thread. Returns copied DTOs; never passes live CCT objects to a worker.</summary>
internal static class CctAdapter
{
    public static bool Available => ConsistencyTrackerModule.Instance != null;
    public static bool? TrackingPaused => Available ? ConsistencyTrackerModule.Instance.ModSettings.PauseDeathTracking : null;
    public static CctCapture? Capture(string dataset, string sid, string side)
    {
        var module = ConsistencyTrackerModule.Instance;
        if (module == null) return null;
        var stats = module.CurrentChapterStats;
        // The CCT data can outlive the Level. Do not attribute old data to a newly entered map.
        if (stats == null || stats.ChapterSID != sid || stats.ChapterUID != sid + "/" + side) return null;
        if (stats.Rooms.Count > 2000) throw new InvalidOperationException("cct_room_limit");
        var rooms = stats.Rooms.Select(pair => {
            var r = pair.Value;
            return new CctRoom(pair.Key, r.PreviousAttempts.TakeLast(100).ToArray(), r.SuccessStreak,
                r.SuccessStreakBest, r.GoldenBerryDeaths, r.GoldenBerryDeathsSession, r.DeathsInCurrentRun);
        }).OrderBy(r => r.RoomKey, StringComparer.Ordinal).ToArray();
        CctRoute? route = null;
        var path = module.CurrentChapterPath;
        if (path != null) {
            if (path.ChapterSID != sid) throw new InvalidOperationException("cct_route_mismatch");
            if (path.Checkpoints.Count > 2000 || path.IgnoredRooms.Count > 2000) throw new InvalidOperationException("cct_route_limit");
            var nodes = new List<RouteNode>(); var checkpoints = new List<RouteCheckpoint>();
            for (int i = 0; i < path.Checkpoints.Count; i++) {
                var cp = path.Checkpoints[i]; var key = "cp-" + i;
                checkpoints.Add(new(key, Empty(cp.Name), Empty(cp.Abbreviation)));
                if (nodes.Count + cp.Rooms.Count > 2000) throw new InvalidOperationException("cct_route_limit");
                foreach (var room in cp.Rooms) {
                    if (room.GroupedRooms.Count > 100) throw new InvalidOperationException("cct_group_limit");
                    nodes.Add(new(room.DebugRoomName, key, room.GroupedRooms.ToArray(), room.IsNonGameplayRoom, Empty(room.CustomRoomName)));
                }
            }
            route = new(nodes.ToArray(), path.IgnoredRooms.ToArray(), sid, Empty(path.CampaignName),
                Empty(path.ChapterName), Empty(path.SideName), checkpoints.ToArray());
        }
        var settings = module.ModSettings;
        int window = settings.LiveDataSelectedAttemptCount;
        if (window is not (5 or 10 or 20 or 100)) throw new InvalidOperationException("cct_window_unsupported");
        return new(dataset, sid, side, module.SelectedPathSegmentIndex,
            new(new(module.Metadata.Version.ToString(), typeof(CctAdapter).Assembly.GetName().Version!.ToString(3), stats.SessionStarted.ToString("O"),
                new(settings.TrackNegativeStreaks, window), new(stats.GoldenCollectedCount, stats.GoldenCollectedCountSession), route), rooms));
    }
    /// <summary>Session history for local charts. Same chapter guard as <see cref="Capture"/>; bounded copies only.</summary>
    public static CctHistory? CaptureHistory(string sid, string side)
    {
        var stats = ConsistencyTrackerModule.Instance?.CurrentChapterStats;
        if (stats == null || stats.ChapterSID != sid || stats.ChapterUID != sid + "/" + side) return null;
        static long Ms(long ticks) => ticks / TimeSpan.TicksPerMillisecond;
        var sessions = stats.OldSessions.TakeLast(500).Select(s => new CctPastSession(s.SessionStarted, s.TotalGoldenDeaths,
            s.TotalGoldenDeathsSession, s.TotalSuccessRate, Empty(s.PBRoomName), Empty(s.SessionPBRoomName), s.AverageRunDistance,
            s.AverageRunDistanceSession, s.TotalGoldenCollections, s.TotalGoldenCollectionsSession, s.LastGoldenRuns?.Count ?? 0)).ToArray();
        return new(stats.SessionStarted, stats.LastGoldenRuns.TakeLast(2000).ToArray(), sessions,
            stats.Rooms.Take(2000).ToDictionary(p => p.Key, p => Ms(p.Value.TimeSpentInRoom)),
            stats.Rooms.Take(2000).ToDictionary(p => p.Key, p => Ms(p.Value.TimeSpentInRoomInRuns)));
    }
    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
