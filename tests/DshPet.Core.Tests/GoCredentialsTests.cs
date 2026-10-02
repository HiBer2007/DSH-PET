using System.Text;
using DshPet.Core.Credentials;
using Xunit;

namespace DshPet.Core.Tests;

public class GoCredentialScanTests
{
    // Shaped like the real thing, with stand-in values.
    private const string AuthValue = "Fe26.2**deadbeefcafe*abc*1234*5678*zzz";

    // $$ so that single braces below stay literal content.
    private static readonly string PowerShellSnippet = $$"""
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $session.Cookies.Add((New-Object System.Net.Cookie("auth", "{{AuthValue}}", "/", "opencode.ai")))
    $session.Cookies.Add((New-Object System.Net.Cookie("__Host-console_session", "st_9edad063-cc54-49a9-a6a6-3788cf561387", "/", "opencode.ai")))
    Invoke-WebRequest -Uri "https://opencode.ai/console/api/go/status" -Headers @{"x-org-id" = "wrk_01TESTWORKSPACE0000000000"}
    """;

    [Fact]
    public void Scans_the_powerShell_snippet_from_the_docs()
    {
        var patch = GoCredentialPatch.Scan(PowerShellSnippet);
        Assert.Equal(AuthValue, patch.Auth);
        Assert.Equal("st_9edad063-cc54-49a9-a6a6-3788cf561387", patch.Session);
        Assert.Equal("wrk_01TESTWORKSPACE0000000000", patch.Org);
        Assert.Null(patch.Key);
        Assert.True(patch.AnyFound);
    }

    [Fact]
    public void Scans_a_plain_cookie_header()
    {
        var patch = GoCredentialPatch.Scan(
            $"Cookie: oc_locale=zh; auth={AuthValue}; __Host-console_session=st_9edad063-cc54-49a9-a6a6-3788cf561387");
        Assert.Equal(AuthValue, patch.Auth);
        Assert.NotNull(patch.Session);
        Assert.Null(patch.Org);                 // a bare Cookie line carries no workspace id
    }

    [Fact]
    public void Scans_the_auth_assignment_form()
    {
        var patch = GoCredentialPatch.Scan($"auth={AuthValue}; other=1");
        Assert.Equal(AuthValue, patch.Auth);
    }

    [Fact]
    public void Scans_a_bare_service_key()
    {
        var patch = GoCredentialPatch.Scan("oc_sk_ABCdef1234567890abcdefghij");
        Assert.Equal("oc_sk_ABCdef1234567890abcdefghij", patch.Key);
        Assert.Null(patch.Auth);
    }

    // Personal accounts identify themselves with "org_..." where team workspaces
    // use "wrk_...". Recognising only the latter produced a credential set with no
    // org, which reads as "half configured" and silently costs the per-call
    // itemising - the failure is invisible, which is the worst kind.
    [Fact]
    public void Recognises_an_org_prefixed_workspace_id()
    {
        var patch = GoCredentialPatch.Scan(
            $"Cookie: auth={AuthValue}; __Host-console_session=st_69e2f125-255e-4c40-ab67-1a770c10198b\r\n" +
            "x-org-id: org_01M3V8NP5RARPX3TVYXBV3NVTE");
        Assert.Equal(AuthValue, patch.Auth);
        Assert.Equal("st_69e2f125-255e-4c40-ab67-1a770c10198b", patch.Session);
        Assert.Equal("org_01M3V8NP5RARPX3TVYXBV3NVTE", patch.Org);
    }

    [Fact]
    public void Recognises_an_org_id_inside_a_chromium_style_capture()
    {
        string blob =
            "$session.Cookies.Add((New-Object System.Net.Cookie(\"auth\", \"" + AuthValue + "\", \"/\", \"opencode.ai\")))\r\n" +
            "$session.Cookies.Add((New-Object System.Net.Cookie(\"__Host-console_session\", \"st_69e2f125-255e-4c40-ab67-1a770c10198b\", \"/\", \"opencode.ai\")))\r\n" +
            "  \"referer\"=\"https://opencode.ai/console/org_01M3V8NP5RARPX3TVYXBV3NVTE/go\"\r\n" +
            "  \"x-org-id\"=\"org_01M3V8NP5RARPX3TVYXBV3NVTE\"";
        var patch = GoCredentialPatch.Scan(blob);
        Assert.Equal(AuthValue, patch.Auth);
        Assert.Equal("org_01M3V8NP5RARPX3TVYXBV3NVTE", patch.Org);
    }

    [Fact]
    public void Does_not_mistake_a_subscriber_user_id_for_a_workspace_id()
    {
        // "user_01M3..." sits in the same response as the org id.
        var patch = GoCredentialPatch.Scan("subscriberUserId=user_01M3V8N7F5CXQFAJPR711EZNSM");
        Assert.Null(patch.Org);
    }

    [Fact]
    public void A_personal_account_capture_is_usable_end_to_end()
    {
        var creds = new GoCredentials();
        creds.Apply(GoCredentialPatch.Scan(
            $"$session.Cookies.Add((New-Object System.Net.Cookie(\"auth\", \"{AuthValue}\", \"/\", \"opencode.ai\")))\r\n" +
            "$session.Cookies.Add((New-Object System.Net.Cookie(\"__Host-console_session\", \"st_69e2f125-255e-4c40-ab67-1a770c10198b\", \"/\", \"opencode.ai\")))\r\n" +
            "  \"x-org-id\"=\"org_01M3V8NP5RARPX3TVYXBV3NVTE\""));
        Assert.True(creds.HasCookie);          // this is what the old regex broke
        Assert.Equal("org_01M3V8NP5RARPX3TVYXBV3NVTE", creds.Org);
    }

    [Fact]
    public void A_partial_paste_only_reports_what_it_found()
    {
        var patch = GoCredentialPatch.Scan("nothing useful here");
        Assert.False(patch.AnyFound);
        Assert.Null(patch.Auth);
        Assert.Null(patch.Session);
        Assert.Null(patch.Org);
        Assert.Null(patch.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_and_empty_input_find_nothing(string? text)
    {
        Assert.False(GoCredentialPatch.Scan(text).AnyFound);
    }
}

public class GoCredentialsTests
{
    private const string AuthValue = "Fe26.2**deadbeefcafe*abc*1234*5678*zzz";

    [Fact]
    public void A_partial_paste_never_wipes_what_is_already_stored()
    {
        var creds = new GoCredentials { Auth = "old-auth", Session = "old-session", Org = "wrk_old" };
        creds.Apply(GoCredentialPatch.Scan("oc_sk_NEWKEY1234567890ab"));   // only a key was pasted

        Assert.Equal("old-auth", creds.Auth);
        Assert.Equal("old-session", creds.Session);
        Assert.Equal("wrk_old", creds.Org);
        Assert.Equal("oc_sk_NEWKEY1234567890ab", creds.Key);
    }

    [Fact]
    public void Readiness_needs_a_key_or_a_full_cookie_set()
    {
        Assert.False(new GoCredentials().Ready);
        Assert.True(new GoCredentials { Key = "oc_sk_x" }.Ready);
        Assert.True(new GoCredentials { Auth = "a", Session = "s", Org = "wrk_o" }.Ready);
        // a cookie set missing one piece is not usable
        Assert.False(new GoCredentials { Auth = "a", Session = "s" }.Ready);
        Assert.False(new GoCredentials { Auth = "a", Org = "wrk_o" }.Ready);
    }

    [Fact]
    public void Parses_every_line_form_of_the_credential_file()
    {
        var creds = new GoCredentials();
        creds.ApplyLine("# a comment");
        creds.ApplyLine("");
        creds.ApplyLine("garbage without an equals sign");
        creds.ApplyLine("key=oc_sk_FROMFILE1234567890");
        creds.ApplyLine("auth=" + AuthValue);
        creds.ApplyLine("session=st_9edad063-cc54-49a9-a6a6-3788cf561387");
        creds.ApplyLine("org=wrk_01TESTWORKSPACE0000000000");

        Assert.True(creds.HasCookie);
        Assert.True(creds.HasKey);
        Assert.Equal("oc_sk_FROMFILE1234567890", creds.Key);
    }

    [Fact]
    public void A_cookie_line_is_scanned_for_the_pieces()
    {
        var creds = new GoCredentials();
        creds.ApplyLine($"cookie=oc_locale=zh; auth={AuthValue}; __Host-console_session=st_9edad063-cc54-49a9-a6a6-3788cf561387; org=wrk_01TESTWORKSPACE0000000000");
        Assert.Equal(AuthValue, creds.Auth);
        Assert.NotNull(creds.Session);
        Assert.Equal("wrk_01TESTWORKSPACE0000000000", creds.Org);
    }

    [Fact]
    public void Empty_values_do_not_erase_a_stored_field()
    {
        var creds = new GoCredentials { Key = "oc_sk_KEEP" };
        creds.ApplyLine("key=");
        Assert.Equal("oc_sk_KEEP", creds.Key);
    }

    [Fact]
    public void Reads_the_key_out_of_opencodes_own_login_store()
    {
        const string auth = """
        {"deepseek":{"type":"api","key":"sk-abcdef"},
         "opencode-go":{"type":"api","key":"oc_sk_d5TESTKEYxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}}
        """;
        Assert.Equal("oc_sk_d5TESTKEYxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                     GoCredentials.KeyFromOpenCodeAuth(auth));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"opencode-go":null}""")]
    [InlineData("""{"opencode-go":{"type":"api"}}""")]                     // no key
    [InlineData("""{"opencode-go":{"key":"sk-wrong-prefix"}}""")]          // wrong kind of key
    public void Refuses_to_guess_a_key_from_a_shape_it_does_not_know(string? json)
    {
        Assert.Null(GoCredentials.KeyFromOpenCodeAuth(json));
    }

    [Fact]
    public void Environment_wins_over_the_file()
    {
        string dir = TempDir();
        try
        {
            new GoCredentials { Org = "wrk_fromfile", Key = "oc_sk_FILE" }.Save(dir);
            var env = new Dictionary<string, string?>
            {
                ["DSHPET_GO_ORG"] = "wrk_fromenv",
                ["DSHPET_GO_AUTH"] = "auth_fromenv",
            };
            var creds = GoCredentials.Load(dir, name => env.TryGetValue(name, out var v) ? v : null,
                                           openCodeAuthPath: Path.Combine(dir, "absent.json"));
            Assert.Equal("wrk_fromenv", creds.Org);
            Assert.Equal("auth_fromenv", creds.Auth);
            Assert.Equal("oc_sk_FILE", creds.Key);        // not overridden, so the file still wins
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Falls_back_to_opencodes_login_store_when_no_key_is_stored()
    {
        string dir = TempDir();
        try
        {
            string auth = Path.Combine(dir, "auth.json");
            File.WriteAllText(auth, """{"opencode-go":{"type":"api","key":"oc_sk_FROMSTORE1234567890"}}""");
            var creds = GoCredentials.Load(dir, _ => null, auth);
            Assert.Equal("oc_sk_FROMSTORE1234567890", creds.Key);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        string dir = TempDir();
        try
        {
            var original = new GoCredentials { Key = "oc_sk_RT", Auth = AuthValue, Session = "st_x", Org = "wrk_y" };
            original.Save(dir);

            var loaded = GoCredentials.Load(dir, _ => null, Path.Combine(dir, "absent.json"));
            Assert.Equal(original.Key, loaded.Key);
            Assert.Equal(original.Auth, loaded.Auth);
            Assert.Equal(original.Session, loaded.Session);
            Assert.Equal(original.Org, loaded.Org);
            Assert.True(loaded.HasCookie && loaded.HasKey);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Saves_ascii_so_the_file_stays_readable_everywhere()
    {
        string dir = TempDir();
        try
        {
            new GoCredentials { Auth = AuthValue }.Save(dir);
            byte[] bytes = File.ReadAllBytes(Path.Combine(dir, GoCredentials.FileName));
            Assert.All(bytes, b => Assert.True(b < 128, "credential file must stay ASCII"));
            Assert.Contains("auth=" + AuthValue, Encoding.ASCII.GetString(bytes));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dshpet-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
