namespace CNGoldenLink;

/// <summary>Pure chart projection for the local control page. Reuses the overlay's route semantics:
/// grouped rooms count once, repeated nodes count at their first position, ignored rooms are skipped.
/// Room numbers are 1-based gameplay positions; a win is <c>roomCount + 1</c>, as in CCT.</summary>
internal static class InsightsProjection
{
    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
    public static object Build(CctCapture? capture, CctHistory? history, string? mapName, string? campaign, string? challenge) {
        var state = capture?.State; var route = state?.Metadata.Route;
        if (state == null || route == null)
            return new { schema = "goldenlink.insights/1", available = false, reason = state == null ? "no_cct" : "no_route", mapName, campaign };
        var nodes = OverlayProjection.Nodes(route).Where(n => !n.Node.IsNonGameplayRoom).ToArray();
        var rooms = state.Rooms.ToDictionary(r => r.RoomKey, StringComparer.Ordinal);
        // Fixed 20-attempt window for success rates and golden chance, independent of CCT's live-data setting.
        const int window = 20;
        int count = nodes.Length, win = count + 1;
        var number = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++) foreach (var key in nodes[i].Members) number.TryAdd(key, i + 1);
        long Sum(string[] members, Func<CctRoom, long> value) => members.Sum(k => rooms.TryGetValue(k, out var r) ? value(r) : 0);
        long Time(IReadOnlyDictionary<string, long>? map, string[] members) => map == null ? 0 : members.Sum(k => map.TryGetValue(k, out var t) ? t : 0);
        var deaths = nodes.Select(n => Sum(n.Members, r => r.GoldenBerryDeaths)).ToArray();
        var deathsSession = nodes.Select(n => Sum(n.Members, r => r.GoldenBerryDeathsSession)).ToArray();
        long wins = state.Metadata.Chapter.GoldenCollectedCount, winsSession = state.Metadata.Chapter.GoldenCollectedCountSession;
        long Reached(long[] d, int i, long w) => d.Skip(i).Sum() + w;
        double? Ratio(long n, long d) => d > 0 ? 100d * n / d : null;
        var rates = nodes.Select(n => {
            long ok = Sum(n.Members, r => r.PreviousAttempts.TakeLast(window).Count(a => a));
            long all = Sum(n.Members, r => Math.Min(r.PreviousAttempts.Length, window));
            return (ok, all, rate: all > 0 ? (double?)ok / all : null);
        }).ToArray();
        // Chance to finish a run that has reached this room, from the recent-window success rates (CCT's golden chance).
        var chance = new double?[count]; double? carry = 1;
        for (int i = count - 1; i >= 0; i--) { carry = carry == null || rates[i].rate == null ? null : carry * rates[i].rate; chance[i] = carry; }
        var cps = route.Checkpoints;
        int Cp(RouteNode n) => Array.FindIndex(cps, c => c.CheckpointKey == n.CheckpointKey);
        var roomRows = nodes.Select((n, i) => new {
            number = i + 1, key = n.Node.RoomKey, name = n.Node.CustomRoomName ?? n.Node.RoomKey, checkpoint = Cp(n.Node) + 1,
            goldenDeaths = deaths[i], goldenDeathsSession = deathsSession[i],
            reached = Reached(deaths, i, wins), reachedSession = Reached(deathsSession, i, winsSession),
            choke = Ratio(deaths[i], Reached(deaths, i, wins)), chokeSession = Ratio(deathsSession[i], Reached(deathsSession, i, winsSession)),
            successes = rates[i].ok, attempts = rates[i].all, successRate = rates[i].rate * 100, goldenChance = chance[i] * 100,
            streak = n.Members.Select(k => rooms.TryGetValue(k, out var r) ? r.SuccessStreak : 0).FirstOrDefault(),
            timeMs = Time(history?.TimeSpentMs, n.Members), timeInRunsMs = Time(history?.TimeSpentInRunsMs, n.Members)
        }).ToArray();
        var checkpointRows = cps.Select((c, ci) => {
            var idx = Enumerable.Range(0, count).Where(i => Cp(nodes[i].Node) == ci).ToArray();
            double? clear = idx.Length == 0 ? null : idx.Aggregate((double?)1, (p, i) => p == null || rates[i].rate == null ? null : p * rates[i].rate) * 100;
            return new { index = ci + 1, name = c.Name ?? c.Abbreviation ?? c.CheckpointKey, abbreviation = c.Abbreviation,
                firstRoom = idx.Length == 0 ? (int?)null : idx[0] + 1, rooms = idx.Length,
                goldenDeaths = idx.Sum(i => deaths[i]), goldenDeathsSession = idx.Sum(i => deathsSession[i]), clearChance = clear };
        }).ToArray();
        int Distance(string? key) => key == null ? win : number.TryGetValue(key, out int n) ? n : 0;
        int? Pb(long[] d, long w) { if (w > 0) return win; int last = Array.FindLastIndex(d, x => x > 0); return last < 0 ? null : last + 1; }
        var sessionRuns = history?.SessionRuns.Select(k => new { distance = Distance(k), won = k == null,
            room = k == null ? null : nodes.FirstOrDefault(n => n.Members.Contains(k)).Node?.CustomRoomName ?? k }).ToArray() ?? [];
        long runs = deaths.Sum() + wins, runsSession = deathsSession.Sum() + winsSession;
        double? AverageDistance(long[] d, long w, long total) => total == 0 ? null : (d.Select((x, i) => x * (i + 1.0)).Sum() + w * win) / total;
        var played = rates.Where(r => r.rate != null).ToArray();
        var sessions = (history?.Sessions ?? []).Select(s => new {
            started = s.Started, current = false,
            pb = s.PbRoom == null ? (int?)null : Distance(s.PbRoom), sessionPb = s.SessionPbRoom == null ? (int?)null : Distance(s.SessionPbRoom),
            // CCT stores NaN for days without golden runs; JSON has no NaN, show a gap instead.
            averageDistance = Finite(s.AverageRunDistance), averageDistanceSession = Finite(s.AverageRunDistanceSession),
            successRate = Finite(s.SuccessRate * 100), runs = (long)s.GoldenDeathsSession + s.CollectionsSession,
            goldenDeaths = (long)s.GoldenDeaths, collectionsSession = (long)s.CollectionsSession
        }).Append(new {
            started = history?.SessionStarted ?? DateTime.MinValue, current = true, pb = Pb(deaths, wins), sessionPb = Pb(deathsSession, winsSession),
            averageDistance = AverageDistance(deaths, wins, runs), averageDistanceSession = AverageDistance(deathsSession, winsSession, runsSession),
            successRate = played.Length == 0 ? null : (double?)played.Average(r => r.rate!.Value) * 100, runs = runsSession,
            goldenDeaths = deaths.Sum(), collectionsSession = winsSession
        }).ToArray();
        return new {
            schema = "goldenlink.insights/1", available = true, mapName = mapName ?? route.ChapterName, campaign = campaign ?? route.CampaignName, challenge,
            window, roomCount = count, winRoom = win,
            totals = new { runs, runsSession, wins, winsSession, goldenDeaths = deaths.Sum(), goldenDeathsSession = deathsSession.Sum(),
                pb = Pb(deaths, wins), sessionPb = Pb(deathsSession, winsSession),
                averageDistance = AverageDistance(deaths, wins, runs), averageDistanceSession = AverageDistance(deathsSession, winsSession, runsSession),
                goldenChance = count == 0 ? null : chance[0] * 100 },
            rooms = roomRows, checkpoints = checkpointRows, sessionRuns, sessions
        };
    }
}
