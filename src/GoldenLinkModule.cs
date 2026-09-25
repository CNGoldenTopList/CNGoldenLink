using System.Runtime.CompilerServices;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Monocle;

namespace CNGoldenLink;

public sealed class GoldenLinkSettings : EverestModuleSettings
{
    public bool ConnectionEnabled { get; set; }
    public string ServiceBaseUrl { get; set; } = "https://gist.diving-fish.com";
    public bool DiagnosticsEnabled { get; set; }
    public bool OverlayEnabled { get; set; }
    public int OverlayPort { get; set; } = 32272;
    public bool CheckUpdates { get; set; } = true;
    public bool UpdateDotInObs { get; set; } = true;
}

public sealed class GoldenLinkSaveData : EverestModuleSaveData
{
    public string DatasetId { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, int> NoGoldenBestDeaths { get; set; } = new();
    public Dictionary<string, int> LastTotalDeaths { get; set; } = new();
    public Dictionary<string, bool> CompletedAreas { get; set; } = new();
}

public sealed class GoldenLinkModule : EverestModule
{
    // Idle presence refresh; the server marks a device offline after 60 s without a snapshot.
    private const int HeartbeatSeconds = 25;
    private static readonly string Version = typeof(GoldenLinkModule).Assembly.GetName().Version!.ToString(3);
    public override Type SettingsType => typeof(GoldenLinkSettings);
    public override Type SaveDataType => typeof(GoldenLinkSaveData);
    private GoldenLinkSettings Settings => (GoldenLinkSettings)_Settings;
    private readonly NoGoldenAttempt noGolden = new();
    private readonly ConditionalWeakTable<Strawberry, object> reportedBerries = new();
    private RemoteUploader? uploader;
    private OverlayServer? overlay;
    private UpdateChecker? updates;
    private long nextOverlaySample, nextHistorySample;
    private string? overlayError;
    private DiagnosticWriter? writer;
    private TextMenu.SubHeader? statusLine;
    private Task? forgetting;
    private bool loaded, exiting, attempted, uploadRequested, openHubWhenReady;
    private string? currentUrl, fault, lastRoomKey;
    private long diagnosticSequence;
    private GoldenLinkSaveData? queuedSave;
    private Queue<AreaStatistics> savedAreas = new();

    public override void Load() {
        if (loaded) return;
        exiting = false; loaded = true; attempted = false; uploadRequested = true;
        On.Monocle.Engine.Update += Update;
        Everest.Events.Level.OnEnter += Enter;
        Everest.Events.Level.OnExit += Exit;
        Everest.Events.Level.OnComplete += Complete;
        On.Celeste.Strawberry.OnPlayer += StrawberryPlayer;
        On.Celeste.Strawberry.OnCollect += StrawberryCollect;
        Everest.Events.Celeste.OnExiting += Exiting;
    }
    public override void Unload() {
        if (!loaded) return;
        On.Monocle.Engine.Update -= Update;
        Everest.Events.Level.OnEnter -= Enter;
        Everest.Events.Level.OnExit -= Exit;
        Everest.Events.Level.OnComplete -= Complete;
        On.Celeste.Strawberry.OnPlayer -= StrawberryPlayer;
        On.Celeste.Strawberry.OnCollect -= StrawberryCollect;
        Everest.Events.Celeste.OnExiting -= Exiting;
        loaded = false; Exiting(); statusLine = null;
    }
    public override void CreateModMenuSection(TextMenu menu, bool inGame, FMOD.Studio.EventInstance snapshot) {
        menu.Add(new TextMenu.SubHeader("CN Golden Link " + Version));
        if (updates?.Info is { Available: true, DownloadUrl: { } download } update) {
            menu.Add(new TextMenu.SubHeader(Dialog.Clean("CNGOLDENLINK_UPDATE_AVAILABLE").Replace("@VERSION", update.Latest)));
            menu.Add(new TextMenu.Button(Dialog.Clean("CNGOLDENLINK_UPDATE_OPEN")).Pressed(() => OpenBrowser(download)));
        }
        menu.Add(new TextMenu.Button(Dialog.Clean("CNGOLDENLINK_OPEN_HUB")).Pressed(() => {
            // Opening the control page implies the local server; start it instead of silently doing nothing.
            if (!Settings.OverlayEnabled) { Settings.OverlayEnabled = true; overlayError = null; SaveSettings(); }
            if (overlay != null) OpenHub(); else openHubWhenReady = true;
        }));
        menu.Add(new TextMenu.OnOff(Dialog.Clean("CNGOLDENLINK_OVERLAY"), Settings.OverlayEnabled).Change(value => {
            Settings.OverlayEnabled = value; overlayError = null;
        }));
        menu.Add(new TextMenu.OnOff(Dialog.Clean("CNGOLDENLINK_CONNECT"), Settings.ConnectionEnabled).Change(SetConnection));
        menu.Add(new TextMenu.Button(Dialog.Clean("CNGOLDENLINK_REAUTHORIZE")).Pressed(() => {
            Settings.ConnectionEnabled = false; attempted = false; uploader?.Stop();
            var previous = uploader; var url = Settings.ServiceBaseUrl;
            forgetting = Task.Run(async () => {
                if (previous != null) await previous.Completion;
                await RemoteUploader.Forget(url);
            });
        }));
        menu.Add(new TextMenu.OnOff(Dialog.Clean("CNGOLDENLINK_DIAGNOSTICS"), Settings.DiagnosticsEnabled)
            .Change(value => Settings.DiagnosticsEnabled = value));
        menu.Add(new TextMenu.OnOff(Dialog.Clean("CNGOLDENLINK_CHECK_UPDATES"), Settings.CheckUpdates)
            .Change(value => Settings.CheckUpdates = value));
        statusLine = new TextMenu.SubHeader(StatusText()); menu.Add(statusLine);
        menu.Add(new TextMenu.SubHeader(Dialog.Clean("CNGOLDENLINK_SERVER_HINT")));
    }
    private void SetConnection(bool value) {
        Settings.ConnectionEnabled = value; attempted = false; fault = null;
        if (!value) uploader?.Stop();
        uploadRequested = true;
    }
    private static void OpenBrowser(string url) {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Log(LogLevel.Warn, "CNGoldenLink", "Open browser: " + ex.GetType().Name); }
    }
    private void OpenHub() { if (overlay != null) OpenBrowser($"http://localhost:{overlay.Port}/"); }
    private string ConnectionState() => fault ?? (!Settings.ConnectionEnabled ? "off" : uploader?.Status ?? "connecting");
    private string StatusText() {
        string state = ConnectionState();
        string key = "CNGOLDENLINK_STATUS_" + state.ToUpperInvariant();
        return Dialog.Has(key) ? Dialog.Clean(key) : Dialog.Clean("CNGOLDENLINK_STATUS_ERROR") + " " + state;
    }
    // Browser-originated changes are validated by the overlay server and applied here on the game thread.
    private void ApplySetting(string key, bool value) {
        switch (key) {
            case "connectionEnabled": if (value != Settings.ConnectionEnabled) SetConnection(value); break;
            case "diagnosticsEnabled": Settings.DiagnosticsEnabled = value; break;
            case "checkUpdates": Settings.CheckUpdates = value; break;
            case "updateDotInObs": Settings.UpdateDotInObs = value; break;
            default: return;
        }
        SaveSettings();
    }
    private void Update(On.Monocle.Engine.orig_Update orig, Engine self, GameTime time) {
        orig(self, time);
        if (exiting) return;
        try {
            if (overlay != null && (!Settings.OverlayEnabled || overlay.Port != Math.Clamp(Settings.OverlayPort, 1024, 65535))) {
                overlay.Dispose(); overlay = null;
            }
            if (Settings.OverlayEnabled && overlay == null && overlayError == null) {
                try {
                    overlay = new OverlayServer(Settings.OverlayPort, Settings.ServiceBaseUrl, Path.Combine(Everest.PathGame, "CNGoldenLinkData"));
                    overlay.CheckUpdatesRequested = () => updates?.CheckNow();
                    overlay.Log = message => Logger.Log(LogLevel.Warn, "CNGoldenLink", message);
                }
                catch (Exception ex) { overlayError = ex.GetType().Name; openHubWhenReady = false; Logger.Log(LogLevel.Warn, "CNGoldenLink", "Overlay: " + overlayError); }
            }
            if (openHubWhenReady && overlay != null) { openHubWhenReady = false; OpenHub(); }
            if (overlay != null) {
                while (overlay.SettingChanges.TryDequeue(out var change)) ApplySetting(change.Key, change.Value);
                overlay.UploadSelections = Settings.ConnectionEnabled;
            }
            if (Settings.CheckUpdates && updates == null) updates = new UpdateChecker(Version);
            if (!Settings.CheckUpdates && updates != null) { updates.Dispose(); updates = null; }
            if (forgetting is { IsCompleted: true }) {
                fault = forgetting.IsFaulted ? "credential_delete_failed" : null; forgetting = null;
            }
            if (currentUrl != Settings.ServiceBaseUrl) {
                uploader?.Stop(); attempted = false; currentUrl = Settings.ServiceBaseUrl;
            }
            if (!Settings.ConnectionEnabled) { uploader?.Stop(); attempted = false; }
            else if (!attempted && forgetting == null && (uploader == null || uploader.Completion.IsCompleted)) {
                attempted = true; fault = null;
                uploader = new RemoteUploader(Settings.ServiceBaseUrl, HeartbeatSeconds, clientVersion: Version); uploadRequested = true;
                queuedSave = null;
            }
            if (Settings.DiagnosticsEnabled && writer == null)
                writer = new DiagnosticWriter(Path.Combine(Everest.PathGame, "CNGoldenLinkData", "logs"));
            if (!Settings.DiagnosticsEnabled && writer != null) { writer.Stop(); writer = null; }
            var level = Engine.Scene as Level;
            var live = ReadLive(level);
            if (Settings.ConnectionEnabled) uploader?.ObserveLive(live);
            if (level != null) noGolden.Observe(level.Session, level.Session.Deaths,
                level.Session.GrabbedGolden || HoldingGolden(level) == true);
            // Upload the route, not the room: data goes out when the room or golden state changes (and on enter,
            // exit, completion). A transient null while the player respawns counts as "not holding".
            string roomKey = live.DatasetId + "|" + live.Sid + "|" + live.Side + "|" + live.Room + "|" + (live.HoldingGolden == true);
            bool uploadDue = uploadRequested || roomKey != lastRoomKey;
            if (uploadDue || (overlay != null && Environment.TickCount64 >= nextOverlaySample)) {
                Sample(level, uploadDue);
                nextOverlaySample = Environment.TickCount64 + 500;
                if (uploadDue) { lastRoomKey = roomKey; uploadRequested = false; }
            }
            if (overlay != null) {
                if (level != null && overlay.InsightsWanted && Environment.TickCount64 >= nextHistorySample) {
                    nextHistorySample = Environment.TickCount64 + 2000;
                    try { overlay.PublishHistory(CctAdapter.CaptureHistory(level.Session.Area.SID, level.Session.Area.Mode.ToString())); }
                    catch (Exception) { overlay.PublishHistory(null); }
                }
                overlay.PublishSettings(new {
                    version = Version, connectionEnabled = Settings.ConnectionEnabled, connectionStatus = ConnectionState(),
                    diagnosticsEnabled = Settings.DiagnosticsEnabled, checkUpdates = Settings.CheckUpdates,
                    updateDotInObs = Settings.UpdateDotInObs, overlayPort = overlay.Port, serviceBaseUrl = Settings.ServiceBaseUrl,
                    cctAvailable = CctAdapter.Available });
                overlay.PublishUpdate(updates?.Info, Settings.UpdateDotInObs);
            }
            if (Settings.ConnectionEnabled && uploader != null && _SaveData is GoldenLinkSaveData save) {
                if (!ReferenceEquals(queuedSave, save)) {
                    queuedSave = save;
                    savedAreas = new Queue<AreaStatistics>(SavedAreaStatistics.Read(save.DatasetId, save.NoGoldenBestDeaths, save.LastTotalDeaths, save.CompletedAreas));
                }
                if (savedAreas.TryPeek(out var saved) && uploader.QueueSavedArea(saved)) savedAreas.Dequeue();
            }
        } catch (Exception ex) { fault = "sampling_error:" + ex.GetType().Name; }
        if (statusLine != null) statusLine.Title = StatusText();
    }
    private static bool? HoldingGolden(Level level) {
        var player = level.Tracker.GetEntity<Player>();
        return player == null ? null : !player.Dead && player.Leader.Followers.Any(f => f.Entity is Strawberry { Golden: true });
    }
    private LiveObservation ReadLive(Level? level) => new(level?.Session.Area.SID, level?.Session.Area.Mode.ToString(),
        level?.Session.Level, level?.Paused, level?.Transitioning, level == null ? null : HoldingGolden(level),
        CctAdapter.Available, CctAdapter.TrackingPaused, (_SaveData as GoldenLinkSaveData)?.DatasetId);
    private void Sample(Level? level, bool publishRemote = true, bool completedNow = false) {
        var sid = level?.Session.Area.SID; var side = level?.Session.Area.Mode.ToString();
        var live = ReadLive(level);
        CctCapture? cct = null; AreaStatistics? area = null; string? error = null;
        if (level != null && _SaveData is GoldenLinkSaveData save) {
            var key = sid + "|" + side;
            var mode = Celeste.SaveData.Instance?.GetAreaStatsFor(level.Session.Area)?.Modes[(int)level.Session.Area.Mode];
            var total = mode?.Deaths;
            if (total != null) {
                // A restored/reset save must not try to lower the server's cumulative high-water mark.
                if (save.LastTotalDeaths.TryGetValue(key, out int last) && total < last) {
                    save.DatasetId = Guid.NewGuid().ToString(); save.NoGoldenBestDeaths.Clear(); save.LastTotalDeaths.Clear(); save.CompletedAreas.Clear();
                    noGolden.Invalidate();
                }
                save.LastTotalDeaths[key] = total.Value;
            }
            bool? completed = completedNow ? true : mode?.Completed;
            if (completed != null) save.CompletedAreas[key] = completed.Value || save.CompletedAreas.GetValueOrDefault(key);
            area = new(save.DatasetId, sid!, side!, save.NoGoldenBestDeaths.TryGetValue(key, out int best) ? best : null,
                TotalDeaths: total, Completed: save.CompletedAreas.TryGetValue(key, out bool clear) ? clear : null);
            live = live with { DatasetId = save.DatasetId };
            try { if (Settings.ConnectionEnabled || overlay != null) cct = CctAdapter.Capture(save.DatasetId, sid!, side!); }
            catch (Exception ex) { error = ex is InvalidOperationException ? ex.Message : "cct_sampling_error"; }
        }
        var captured = new SyncSnapshot(live, cct, area, Environment.TickCount64, error);
        overlay?.Publish(captured, Settings.ServiceBaseUrl);
        if (Settings.ConnectionEnabled && publishRemote) uploader?.Publish(captured);
        if (writer != null && publishRemote) Diagnose(level, live, "snapshot", error, area);
    }
    private void Diagnose(Level? level, LiveObservation live, string kind, string? detail, AreaStatistics? area = null) {
        if (writer == null) return;
        var player = level?.Tracker.GetEntity<Player>();
        var observation = new Observation(live.Sid, live.Side, live.Room, Engine.Scene?.GetType().Name ?? "none",
            live.Paused, live.Transitioning, level?.Completed, level == null ? null : player != null,
            player?.Dead, live.HoldingGolden, Engine.Instance.IsActive);
        writer.Enqueue(new(++diagnosticSequence, DateTimeOffset.UtcNow, Environment.TickCount64,
            kind, observation, writer.Dropped, detail, area));
    }
    private void Enter(Session session, bool fromSaveData) {
        if (exiting) return;
        noGolden.Start(session, session.StartedFromBeginning, fromSaveData || session.RestartedFromGolden,
            session.Deaths, session.GrabbedGolden); uploadRequested = true;
    }
    // Stats gathered in the last room would otherwise wait for the next level; flush while the Level still exists.
    private void Exit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow) {
        if (exiting) return;
        try { Sample(level); } catch (Exception ex) { fault = "sampling_error:" + ex.GetType().Name; }
    }
    private void Complete(Level level) {
        if (exiting) return;
        try {
            var deaths = noGolden.Complete(level.Session, level.Session.Deaths, level.Session.StartedFromBeginning,
                level.Session.GrabbedGolden || HoldingGolden(level) == true);
            if (deaths != null && _SaveData is GoldenLinkSaveData save) {
                var key = level.Session.Area.SID + "|" + level.Session.Area.Mode;
                if (!save.NoGoldenBestDeaths.TryGetValue(key, out var old) || deaths < old) save.NoGoldenBestDeaths[key] = deaths.Value;
            }
            Sample(level, completedNow: true);
        } catch (Exception ex) { fault = "completion_error:" + ex.GetType().Name; }
    }
    private void StrawberryPlayer(On.Celeste.Strawberry.orig_OnPlayer orig, Strawberry self, Player player) {
        orig(self, player);
        if (self.Golden && self.Follower.Leader?.Entity == player) noGolden.Invalidate();
    }
    private void StrawberryCollect(On.Celeste.Strawberry.orig_OnCollect orig, Strawberry self) {
        orig(self);
        if (exiting) return;
        try {
            // Same classification as CCT: vanilla golden and Collab Utils silver. Speed berries and others are not runs.
            string? berry = !self.Golden ? null : self.GetType().Name switch { "Strawberry" => "golden", "SilverBerry" => "silver", _ => null };
            if (berry == null || Celeste.SaveData.Instance?.Assists.Invincible == true) return;
            if (reportedBerries.TryGetValue(self, out _)) return;
            reportedBerries.Add(self, berry);
            var level = self.Scene as Level ?? Engine.Scene as Level;
            if (level == null) return;
            var live = ReadLive(level);
            if (Settings.ConnectionEnabled) uploader?.QueueBerry(new(Guid.NewGuid().ToString(), live.DatasetId,
                level.Session.Area.SID, level.Session.Area.Mode.ToString(), live.Room, berry));
            Diagnose(level, live, "berry", berry);
            uploadRequested = true;
        } catch (Exception ex) { fault = "berry_error:" + ex.GetType().Name; }
    }
    private void Exiting() {
        exiting = true; noGolden.Invalidate(); uploader?.Stop(); writer?.Stop(); writer = null;
        overlay?.Dispose(); overlay = null; updates?.Dispose(); updates = null;
    }
}
