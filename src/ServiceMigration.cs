using System.Text.Json;

namespace CNGoldenLink;

/// <summary>One-time move from the old default service address to cngist.com. Both names serve the same
/// backend, so the saved device token stays valid and players do not have to authorize again.</summary>
public static class ServiceMigration
{
    public const string OldDefault = "https://gist.diving-fish.com";
    public const string NewDefault = "https://cngist.com";

    /// <summary>Returns the address to use. Only the untouched old default is rewritten; custom servers are kept.</summary>
    public static string Migrate(string current, string dataPath, Func<Uri, string?> load, Action<Uri, string> save) {
        if (!string.Equals(current?.Trim().TrimEnd('/'), OldDefault, StringComparison.OrdinalIgnoreCase)) return current!;
        try {
            // Copy, never move: an older mod build on the same machine can still use the old name.
            var oldOrigin = RemoteUploader.ValidateOrigin(OldDefault); var newOrigin = RemoteUploader.ValidateOrigin(NewDefault);
            if (load(newOrigin) == null && load(oldOrigin) is { } token) save(newOrigin, token);
        } catch (Exception) { /* Credential store unavailable: the player authorizes once on the new address. */ }
        try { MigrateSelections(Path.Combine(dataPath, "overlay-selections.json")); } catch (Exception) { }
        return NewDefault;
    }

    // Challenge selections are keyed by "service address|map id"; the address is the raw settings value,
    // so match it the same way as above (trailing slash and case do not matter).
    private static void MigrateSelections(string file) {
        if (!File.Exists(file)) return;
        var selections = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
        if (selections == null) return;
        bool changed = false;
        foreach (var (key, value) in selections.ToArray()) {
            int split = key.IndexOf('|');
            if (split < 0 || !string.Equals(key[..split].TrimEnd('/'), OldDefault, StringComparison.OrdinalIgnoreCase)) continue;
            selections.TryAdd(NewDefault + key[split..], value); changed = true;
        }
        if (!changed) return;
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(selections)); File.Move(file + ".tmp", file, true);
    }
}
