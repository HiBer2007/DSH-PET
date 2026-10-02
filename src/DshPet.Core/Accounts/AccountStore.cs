using System.Text.Json;
using System.Text.Json.Serialization;
using DshPet.Core.Credentials;

namespace DshPet.Core.Accounts;

/// <summary>
/// The set of monitored accounts, stored as <c>accounts.json</c> next to the app.
///
/// This file is the single source of truth for credentials once it exists. It is
/// written indented and with plain property names so it can be hand-edited; the
/// secrets in it are exactly as exposed as the <c>apikey.txt</c> /
/// <c>opencode_go.txt</c> files they replace, and the docs say so.
/// </summary>
public sealed class AccountStore
{
    public const string FileName = "accounts.json";

    /// <summary>Set when the previous file could not be parsed; it is kept aside as .bad.</summary>
    [JsonIgnore]
    public string LoadWarning { get; set; } = "";

    public string ActiveId { get; set; } = "";

    public List<AccountProfile> Profiles { get; set; } = new();

    [JsonIgnore]
    public AccountProfile? Active
    {
        get
        {
            for (int i = 0; i < Profiles.Count; i++)
                if (Profiles[i].Id == ActiveId) return Profiles[i];
            return Profiles.Count > 0 ? Profiles[0] : null;   // never leave the widget with nothing to read
        }
    }

    [JsonIgnore]
    public bool IsEmpty => Profiles.Count == 0;

    // ------------------------------------------------------------------ load --

    /// <summary>
    /// Reads accounts.json, or builds it from the single-account files the
    /// previous version used. Migration never deletes or rewrites those files:
    /// they stay behind as the fallback for the frozen PowerShell build.
    /// </summary>
    public static AccountStore Load(string directory, string? sourceHint = null,
                                    Func<string, string?>? env = null,
                                    string? dshCredentialsPath = null,
                                    string? openCodeAuthPath = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string path = Path.Combine(directory, FileName);

        if (File.Exists(path))
        {
            try
            {
                AccountStore? loaded = JsonSerializer.Deserialize<AccountStore>(File.ReadAllText(path));
                if (loaded != null && loaded.Profiles.Count > 0)
                {
                    loaded.LoadWarning = "";
                    return loaded;
                }
            }
            catch (JsonException) { }
            catch (IOException) { }

            // Unreadable: keep the bytes for inspection instead of silently
            // overwriting whatever the user had, then start from migration.
            try { File.Move(path, path + ".bad"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        AccountStore migrated = Migrate(directory, sourceHint, env, dshCredentialsPath, openCodeAuthPath);
        if (File.Exists(path + ".bad")) migrated.LoadWarning = FileName + " 无法解析，已另存为 .bad 并重新生成";
        try { migrated.Save(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return migrated;
    }

    /// <summary>
    /// Turns the old single-account setup into profiles. Every credential source
    /// the old version knew about is consulted, and each one that yields
    /// something usable becomes its own profile, so an existing install wakes up
    /// with both its DeepSeek and its GO account already listed.
    /// </summary>
    internal static AccountStore Migrate(string directory, string? sourceHint,
                                         Func<string, string?> env, string? dshCredentialsPath,
                                         string? openCodeAuthPath)
    {
        var store = new AccountStore();

        string dshKey = env("DSHPET_KEY") ?? "";
        if (dshKey.Length == 0) dshKey = ReadTrimmed(Path.Combine(directory, "apikey.txt"));
        if (dshKey.Length == 0)
            dshKey = DshCredentials.LoadKey(dshCredentialsPath) ?? "";

        if (dshKey.Length > 0)
        {
            store.Profiles.Add(new AccountProfile
            {
                Id = NewId(),
                Name = "DeepSeek",
                Source = AccountProfile.SourceDeepSeek,
                DeepSeekKey = dshKey,
            });
        }

        GoCredentials go = GoCredentials.Load(directory, env, openCodeAuthPath);
        if (go.Ready)
        {
            store.Profiles.Add(new AccountProfile
            {
                Id = NewId(),
                Name = "OpenCode GO",
                Source = AccountProfile.SourceGo,
                GoKey = go.Key,
                GoAuth = go.Auth,
                GoSession = go.Session,
                GoOrg = go.Org,
            });
        }

        if (store.Profiles.Count == 0)
        {
            store.Profiles.Add(new AccountProfile
            {
                Id = NewId(),
                Name = "默认账户",
                Source = sourceHint == AccountProfile.SourceGo ? AccountProfile.SourceGo
                                                               : AccountProfile.SourceDeepSeek,
            });
        }

        // Land on the source the widget was already showing.
        AccountProfile? wanted = null;
        for (int i = 0; i < store.Profiles.Count && wanted == null; i++)
            if (store.Profiles[i].Source == sourceHint) wanted = store.Profiles[i];
        store.ActiveId = (wanted ?? store.Profiles[0]).Id;
        return store;
    }

    private static string ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    internal static string NewId() => "acc_" + Guid.NewGuid().ToString("N").Substring(0, 8);

    // ------------------------------------------------------------------ save --

    public void Save(string directory)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(Path.Combine(directory, FileName),
                          JsonSerializer.Serialize(this, options),
                          new System.Text.UTF8Encoding(false));
    }

    // ----------------------------------------------------------------- edits --

    public AccountProfile Add(string name, string source)
    {
        var profile = new AccountProfile
        {
            Id = NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? "新账户" : name.Trim(),
            Source = source == AccountProfile.SourceGo ? AccountProfile.SourceGo
                                                       : AccountProfile.SourceDeepSeek,
        };
        Profiles.Add(profile);
        if (ActiveId.Length == 0) ActiveId = profile.Id;
        return profile;
    }

    public AccountProfile? Find(string id)
    {
        for (int i = 0; i < Profiles.Count; i++)
            if (Profiles[i].Id == id) return Profiles[i];
        return null;
    }

    public bool Remove(string id)
    {
        AccountProfile? profile = Find(id);
        if (profile == null) return false;
        Profiles.Remove(profile);
        if (ActiveId == id) ActiveId = Profiles.Count > 0 ? Profiles[0].Id : "";
        return true;
    }

    public void SetActive(string id)
    {
        if (Find(id) != null) ActiveId = id;
    }

    /// <summary>A name that does not collide with an existing one ("小号 2", ...).</summary>
    public string UniqueName(string wanted)
    {
        string baseName = string.IsNullOrWhiteSpace(wanted) ? "账户" : wanted.Trim();
        string candidate = baseName;
        int n = 2;
        while (HasName(candidate)) candidate = baseName + " " + n++;
        return candidate;
    }

    private bool HasName(string name)
    {
        for (int i = 0; i < Profiles.Count; i++)
            if (string.Equals(Profiles[i].Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
