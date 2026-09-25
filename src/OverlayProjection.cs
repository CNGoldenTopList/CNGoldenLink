namespace CNGoldenLink;

/// <summary>Pure presentation projection. No game objects or credentials cross this boundary.</summary>
internal static class OverlayProjection
{
    /// <summary>Route nodes in order. Grouped rooms join their node, repeats count once, ignored rooms are skipped.</summary>
    public static List<(RouteNode Node, string[] Members)> Nodes(CctRoute? route) {
        var ignored = route?.IgnoredRooms.ToHashSet(StringComparer.Ordinal) ?? new();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<(RouteNode Node, string[] Members)>();
        foreach (var node in route?.Nodes ?? []) {
            if (ignored.Contains(node.RoomKey) || seen.Contains(node.RoomKey)) continue;
            var members = new[] { node.RoomKey }.Concat(node.GroupedRooms).Where(k => !ignored.Contains(k) && seen.Add(k)).ToArray();
            nodes.Add((node, members));
        }
        return nodes;
    }
    public static object Build(SyncSnapshot? snapshot, object catalog, object[] choices, string? mapId, string? selected, string contextStatus,
        object? update = null, object? display = null) {
        var state = snapshot?.Cct?.State; var route = state?.Metadata.Route;
        var nodes = Nodes(route);
        int index = nodes.FindIndex(n => n.Members.Contains(snapshot?.Live.Room));
        var room = state?.Rooms.FirstOrDefault(r => r.RoomKey == snapshot?.Live.Room);
        var cps = route?.Checkpoints ?? [];
        int cp = index < 0 ? -1 : Array.FindIndex(cps, c => c.CheckpointKey == nodes[index].Node.CheckpointKey);
        var rooms = state?.Rooms.ToDictionary(r => r.RoomKey) ?? new();
        long Deaths(string[] members, bool session) => members.Sum(k => rooms.TryGetValue(k, out var r) ? (long)(session ? r.GoldenBerryDeathsSession : r.GoldenBerryDeaths) : 0);
        var total = nodes.Select(n => Deaths(n.Members, false)).ToArray();
        var session = nodes.Select(n => Deaths(n.Members, true)).ToArray();
        long wins = state?.Metadata.Chapter.GoldenCollectedCount ?? 0, sessionWins = state?.Metadata.Chapter.GoldenCollectedCountSession ?? 0;
        string? Best(long[] deaths, long collected) {
            if (state == null) return null;
            if (collected > 0) return "通关";
            int best = Array.FindLastIndex(deaths, d => d > 0);
            if (best < 0) return null;
            return nodes[best].Node.CustomRoomName ?? nodes[best].Node.RoomKey;
        }
        long entries = index < 0 ? 0 : total.Skip(index).Sum() + wins;
        int? BestRoomIndex(long[] deaths, long collected) {
            if (state == null || route == null) return null;
            if (collected > 0) return nodes.Count(n => !n.Node.IsNonGameplayRoom);
            int best = Array.FindLastIndex(deaths, d => d > 0);
            return best < 0 ? null : nodes.Take(best + 1).Count(n => !n.Node.IsNonGameplayRoom);
        }
        long sessionEntries = index < 0 ? 0 : session.Skip(index).Sum() + sessionWins;
        long passes = index < 0 ? 0 : entries - total[index];
        double? Ratio(long n, long d) => d > 0 ? 100d * n / d : null;
        bool valid = snapshot != null && Environment.TickCount64 - snapshot.CapturedAt < 3000;
        return new {
            schema = "goldenlink.overlay/1", source = "live", connected = valid && snapshot!.Live.Sid != null,
            catalog, choices, mapId, selectedChallengeId = selected, contextStatus, update, display,
            live = new { room = index >= 0 ? nodes[index].Node.CustomRoomName ?? snapshot?.Live.Room : snapshot?.Live.Room,
                holdingGolden = snapshot?.Live.HoldingGolden == true, paused = snapshot?.Live.Paused == true || snapshot?.Live.CctTrackingPaused == true },
            cct = new {
                roomIndex = index < 0 || nodes[index].Node.IsNonGameplayRoom ? (int?)null : nodes.Take(index + 1).Count(n => !n.Node.IsNonGameplayRoom),
                roomCount = route == null ? (int?)null : nodes.Count(n => !n.Node.IsNonGameplayRoom), checkpointIndex = cp < 0 ? (int?)null : cp + 1,
                checkpoints = cps.Select(c => new { name = c.Name ?? c.Abbreviation ?? c.CheckpointKey, shortName = c.Abbreviation,
                    rooms = nodes.Count(n => n.Node.CheckpointKey == c.CheckpointKey && !n.Node.IsNonGameplayRoom) }).ToArray(),
                streak = room?.SuccessStreak, bestStreak = room?.SuccessStreakBest,
                goldenPb = Best(total, wins), sessionGoldenPb = Best(session, sessionWins),
                goldenPbRoomIndex = BestRoomIndex(total, wins), sessionGoldenPbRoomIndex = BestRoomIndex(session, sessionWins),
                successRate = index < 0 ? null : Ratio(passes, entries), successes = index < 0 ? (long?)null : passes, attempts = index < 0 ? (long?)null : entries,
                entryRate = index < 0 ? null : Ratio(entries, total.Sum() + wins),
                sessionEntryRate = index < 0 ? null : Ratio(sessionEntries, session.Sum() + sessionWins),
                goldenDeaths = state == null ? (long?)null : state.Rooms.Sum(r => (long)r.GoldenBerryDeaths),
                sessionGoldenDeaths = state == null ? (long?)null : state.Rooms.Sum(r => (long)r.GoldenBerryDeathsSession),
                recent = room?.PreviousAttempts.TakeLast(20).ToArray() ?? [] },
            area = new { noGoldenBestDeaths = snapshot?.Area?.NoGoldenBestDeaths, totalDeaths = snapshot?.Area?.TotalDeaths }
        };
    }
}
