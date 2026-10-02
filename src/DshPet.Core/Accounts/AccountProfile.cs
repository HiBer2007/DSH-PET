namespace DshPet.Core.Accounts;

/// <summary>
/// One monitored account: an identity plus the source it is read from.
///
/// A profile is deliberately one kind of account, not a bag of both. A DeepSeek
/// key and an OpenCode GO subscription are different things with different
/// numbers on the tablet, so "which account am I looking at" and "which source
/// am I reading" are the same question - answering them separately is what made
/// the single-account version confusing once there was more than one of either.
/// </summary>
public sealed class AccountProfile
{
    public const string SourceDeepSeek = "dsh";
    public const string SourceGo = "go";

    /// <summary>Stable internal id. Never shown; the name is what the user sees.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><see cref="SourceDeepSeek"/> or <see cref="SourceGo"/>.</summary>
    public string Source { get; set; } = SourceDeepSeek;

    /// <summary>DeepSeek API key (<c>sk-...</c>).</summary>
    public string DeepSeekKey { get; set; } = "";

    /// <summary>OpenCode console service key (<c>oc_sk_...</c>) - enough for the meters.</summary>
    public string GoKey { get; set; } = "";

    /// <summary>Console session cookie; with the other two, this unlocks per-call itemising.</summary>
    public string GoAuth { get; set; } = "";

    public string GoSession { get; set; } = "";

    /// <summary>Workspace id (<c>wrk_...</c>), sent as <c>x-org-id</c>.</summary>
    public string GoOrg { get; set; } = "";

    public bool IsGo => Source == SourceGo;
    public bool HasDeepSeekKey => DeepSeekKey.Length > 0;
    public bool HasGoKey => GoKey.Length > 0;
    public bool HasGoCookie => GoAuth.Length > 0 && GoSession.Length > 0 && GoOrg.Length > 0;
    public bool HasAnyGo => HasGoKey || HasGoCookie;

    /// <summary>Whether the source this profile declares actually has credentials.</summary>
    public bool IsUsable => IsGo ? HasAnyGo : HasDeepSeekKey;

    public AccountProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Source = Source,
        DeepSeekKey = DeepSeekKey,
        GoKey = GoKey,
        GoAuth = GoAuth,
        GoSession = GoSession,
        GoOrg = GoOrg,
    };

    /// <summary>One line for the account list, e.g. "OpenCode GO · Key ✓ Cookie ✓".</summary>
    public string Describe()
    {
        if (IsGo)
        {
            string state = HasGoCookie ? "Key" + (HasGoKey ? " ✓" : " ✗") + " Cookie ✓"
                                       : (HasGoKey ? "仅 Key（无法逐次计费）" : "未配置");
            return "OpenCode GO · " + state;
        }
        return HasDeepSeekKey ? "DeepSeek 余额 · Key ✓" : "DeepSeek 余额 · 未配置";
    }
}
