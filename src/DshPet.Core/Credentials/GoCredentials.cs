using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshPet.Core.Credentials;

/// <summary>
/// What a scan of some pasted text managed to recognise. Null means "not found",
/// which is deliberately different from "found and empty": the caller only
/// overwrites the pieces that were actually present, so a partial paste cannot
/// wipe working credentials.
/// </summary>
public sealed record GoCredentialPatch(string? Auth, string? Session, string? Org, string? Key)
{
    public bool AnyFound => Auth is not null || Session is not null || Org is not null || Key is not null;

    private static readonly Regex AuthCookie =
        new(@"(Fe26\.2\*\*[^\s""';,)]+)", RegexOptions.Compiled);

    private static readonly Regex AuthAssignment =
        new(@"auth\s*=\s*([^\s;""]+)", RegexOptions.Compiled);

    private static readonly Regex AuthPair =
        new(@"""\s*auth\s*""\s*,\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex SessionCookie =
        new(@"(st_[0-9a-fA-F\-]{16,})", RegexOptions.Compiled);

    // The workspace id comes in two flavours: team workspaces carry "wrk_...",
    // personal accounts carry "org_...". Matching only the first recognised half
    // of a personal capture - no org meant HasCookie stayed false, which looks
    // like "half configured" and silently costs the per-call itemising.
    private static readonly Regex Workspace =
        new(@"((?:wrk|org)_[0-9A-Za-z]+)", RegexOptions.Compiled);

    private static readonly Regex ServiceKey =
        new(@"(oc_sk_[0-9A-Za-z_\-]{10,})", RegexOptions.Compiled);

    /// <summary>
    /// Pulls credentials out of whatever the user had at hand: a HAR entry, the
    /// console's network tab, the PowerShell snippet from the docs, a plain
    /// <c>Cookie:</c> header, or a bare key.
    /// </summary>
    public static GoCredentialPatch Scan(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new GoCredentialPatch(null, null, null, null);

        string blob = text!;                              // net48 refs lack [NotNullWhen]
        string? auth = FirstGroup(AuthCookie, blob)
                    ?? FirstGroup(AuthAssignment, blob)
                    ?? FirstGroup(AuthPair, blob);

        return new GoCredentialPatch(
            auth,
            FirstGroup(SessionCookie, blob),
            FirstGroup(Workspace, blob),
            FirstGroup(ServiceKey, blob));
    }

    private static string? FirstGroup(Regex re, string text)
    {
        Match m = re.Match(text);
        return m.Success && m.Groups[1].Success ? m.Groups[1].Value : null;
    }
}

/// <summary>Everything needed to talk to the OpenCode console API.</summary>
public sealed class GoCredentials
{
    public const string FileName = "opencode_go.txt";

    /// <summary>Console session cookie. Needed together with <see cref="Session"/> and <see cref="Org"/>.</summary>
    public string Auth { get; set; } = "";

    /// <summary><c>__Host-console_session</c> cookie.</summary>
    public string Session { get; set; } = "";

    /// <summary>Workspace id (<c>wrk_...</c>), sent as <c>x-org-id</c>.</summary>
    public string Org { get; set; } = "";

    /// <summary>Service-account key (<c>oc_sk_...</c>). Enough for the meters, all the logs need the cookie.</summary>
    public string Key { get; set; } = "";

    /// <summary>The three cookie pieces; only then does the request log answer.</summary>
    public bool HasCookie => Auth.Length > 0 && Session.Length > 0 && Org.Length > 0;

    /// <summary>The bearer key; survives the cookie expiring.</summary>
    public bool HasKey => Key.Length > 0;

    /// <summary>At least one usable way in.</summary>
    public bool Ready => HasKey || HasCookie;

    public void Apply(GoCredentialPatch patch)
    {
        if (patch.Key is not null) Key = patch.Key;
        if (patch.Auth is not null) Auth = patch.Auth;
        if (patch.Session is not null) Session = patch.Session;
        if (patch.Org is not null) Org = patch.Org;
    }

    public string Describe() =>
        $"key={(HasKey ? "yes" : "no")} cookie={(HasCookie ? "yes" : "no")} " +
        $"org={(Org.Length > 0 ? Org : "(none)")}";

    // ------------------------------------------------------------------ load --

    /// <summary>
    /// The credential file next to the app, then explicit environment variables on
    /// top of it, then opencode's own login store for a key if there still is
    /// none.
    ///
    /// Environment beats the file, the same way DSHPET_KEY beats apikey.txt on the
    /// DeepSeek side: an override that a stored file could silently win against
    /// would be worse than no override at all.
    /// </summary>
    public static GoCredentials Load(string directory, Func<string, string?>? env = null,
                                     string? openCodeAuthPath = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var creds = new GoCredentials();

        creds.ApplyFile(Path.Combine(directory, FileName));

        creds.Key = Override(env("DSHPET_GO_KEY"), creds.Key);
        creds.Auth = Override(env("DSHPET_GO_AUTH"), creds.Auth);
        creds.Session = Override(env("DSHPET_GO_SESSION"), creds.Session);
        creds.Org = Override(env("DSHPET_GO_ORG"), creds.Org);

        if (creds.Key.Length == 0)
        {
            string path = openCodeAuthPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "opencode", "auth.json");
            try
            {
                if (File.Exists(path)) creds.Key = KeyFromOpenCodeAuth(File.ReadAllText(path)) ?? "";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return creds;
    }

    private static string Override(string? fromEnvironment, string stored) =>
        string.IsNullOrEmpty(fromEnvironment) ? stored : fromEnvironment!;   // net48 refs lack [NotNullWhen]

    /// <summary>Reads the <c>key=</c>/<c>auth=</c>/... credential file.</summary>
    public void ApplyFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadAllLines(path)) ApplyLine(raw);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void ApplyLine(string raw)
    {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') return;
        int i = line.IndexOf('=');
        if (i <= 0) return;
        string name = line.Substring(0, i).Trim().ToLowerInvariant();   // no range syntax on net48
        string value = line.Substring(i + 1).Trim();
        if (value.Length == 0) return;

        switch (name)
        {
            case "auth": Auth = value; break;
            case "session": Session = value; break;
            case "org": Org = value; break;
            case "key": Key = value; break;
            case "cookie": Apply(GoCredentialPatch.Scan(value)); break;
        }
    }

    /// <summary>
    /// Pulls the OpenCode Go key out of opencode's own login store, shaped like
    /// <c>{"opencode-go":{"type":"api","key":"oc_sk_..."}}</c>.
    /// </summary>
    public static string? KeyFromOpenCodeAuth(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            // The `!` is needed on net48: its reference assemblies have no
            // [NotNullWhen] attributes, so IsNullOrWhiteSpace does not narrow.
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("opencode-go", out var entry)) return null;
            if (entry.ValueKind != JsonValueKind.Object) return null;
            if (!entry.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String) return null;
            string value = key.GetString() ?? "";
            return value.StartsWith("oc_sk_", StringComparison.Ordinal) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ save --

    public void Save(string directory)
    {
        var sb = new StringBuilder();
        if (Key.Length > 0) sb.Append("key=").Append(Key).Append("\r\n");
        if (Auth.Length > 0) sb.Append("auth=").Append(Auth).Append("\r\n");
        if (Session.Length > 0) sb.Append("session=").Append(Session).Append("\r\n");
        if (Org.Length > 0) sb.Append("org=").Append(Org).Append("\r\n");
        File.WriteAllText(Path.Combine(directory, FileName), sb.ToString(), Encoding.ASCII);
    }
}
