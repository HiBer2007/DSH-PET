using System.Text.Json;
using DshPet.Core.Accounts;
using DshPet.Core.Credentials;
using Xunit;

namespace DshPet.Core.Tests;

public class AccountStoreTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dshpet-acc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Func<string, string?> NoEnv => _ => null;

    // ------------------------------------------------------------- migration --

    [Fact]
    public void Migration_turns_the_single_account_files_into_profiles()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "apikey.txt"), "sk-dsh-key\n");
            File.WriteAllText(Path.Combine(dir, "opencode_go.txt"),
                "key=oc_sk_GO\r\nauth=Fe26.2**a*b\r\nsession=st_9edad063-cc54-49a9-a6a6-3788cf561387\r\norg=wrk_01TEST\r\n");

            var store = AccountStore.Load(dir, "dsh", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));

            Assert.Equal(2, store.Profiles.Count);
            AccountProfile dsh = store.Profiles.Single(p => p.Source == AccountProfile.SourceDeepSeek);
            AccountProfile go = store.Profiles.Single(p => p.Source == AccountProfile.SourceGo);
            Assert.Equal("sk-dsh-key", dsh.DeepSeekKey);
            Assert.Equal("oc_sk_GO", go.GoKey);
            Assert.True(go.HasGoCookie);
            Assert.Equal("DeepSeek", dsh.Name);          // readable default names
            Assert.Equal("OpenCode GO", go.Name);
            Assert.Equal(dsh.Id, store.ActiveId);        // lands on the source in use
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Migration_picks_the_go_profile_when_go_was_the_active_source()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "apikey.txt"), "sk-dsh-key\n");
            File.WriteAllText(Path.Combine(dir, "opencode_go.txt"), "key=oc_sk_GO\r\n");
            var store = AccountStore.Load(dir, "go", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));
            Assert.True(store.Active!.IsGo);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Migration_falls_back_to_one_empty_profile_when_nothing_is_configured()
    {
        string dir = TempDir();
        try
        {
            var store = AccountStore.Load(dir, null, NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));
            Assert.Single(store.Profiles);
            Assert.False(store.Profiles[0].IsUsable);
            Assert.Equal(store.Profiles[0].Id, store.ActiveId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Migration_reads_the_environment_first()
    {
        string dir = TempDir();
        try
        {
            var env = new Dictionary<string, string?> { ["DSHPET_KEY"] = "sk-from-env" };
            var store = AccountStore.Load(dir, "dsh", n => env.TryGetValue(n, out var v) ? v : null,
                                          Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));
            Assert.Equal("sk-from-env", store.Profiles.Single(p => !p.IsGo).DeepSeekKey);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Migration_takes_the_key_out_of_dshs_own_credential_store()
    {
        string dir = TempDir();
        try
        {
            string yaml = Path.Combine(dir, "credentials.yaml");
            File.WriteAllText(yaml, "DEEPSEEK_API_KEY: sk-from-dsh-store\nOTHER: x\n");
            var store = AccountStore.Load(dir, "dsh", NoEnv, yaml, Path.Combine(dir, "no.json"));
            Assert.Equal("sk-from-dsh-store", store.Profiles.Single(p => !p.IsGo).DeepSeekKey);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Migration_leaves_the_old_files_alone_for_the_powerShell_fallback()
    {
        string dir = TempDir();
        try
        {
            string keyFile = Path.Combine(dir, "apikey.txt");
            string credFile = Path.Combine(dir, "opencode_go.txt");
            File.WriteAllText(keyFile, "sk-dsh-key\n");
            File.WriteAllText(credFile, "key=oc_sk_GO\r\n");

            AccountStore.Load(dir, "dsh", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));

            Assert.Equal("sk-dsh-key\n", File.ReadAllText(keyFile));
            Assert.Equal("key=oc_sk_GO\r\n", File.ReadAllText(credFile));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Migration_writes_the_file_it_just_built()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "apikey.txt"), "sk-dsh-key\n");
            AccountStore.Load(dir, "dsh", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));
            Assert.True(File.Exists(Path.Combine(dir, AccountStore.FileName)));

            // and loading again must not re-migrate or duplicate anything
            var again = AccountStore.Load(dir, "dsh", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));
            Assert.Single(again.Profiles);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Migration_does_not_run_again_once_the_file_exists()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "apikey.txt"), "sk-first\n");
            AccountStore.Load(dir, "dsh", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));

            // a credential file appearing later must not silently add an account
            File.WriteAllText(Path.Combine(dir, "apikey.txt"), "sk-second\n");
            var again = AccountStore.Load(dir, "dsh", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));
            Assert.Single(again.Profiles);
            Assert.Equal("sk-first", again.Profiles[0].DeepSeekKey);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ----------------------------------------------------------------- io ----

    [Fact]
    public void Round_trips_through_json_with_every_credential_intact()
    {
        string dir = TempDir();
        try
        {
            var store = new AccountStore();
            AccountProfile go = store.Add("GO 小号", AccountProfile.SourceGo);
            go.GoKey = "oc_sk_X";
            go.GoAuth = "Fe26.2**x*y";
            go.GoSession = "st_abc";
            go.GoOrg = "wrk_abc";
            AccountProfile dsh = store.Add("DeepSeek 备用", AccountProfile.SourceDeepSeek);
            dsh.DeepSeekKey = "sk-xyz";
            store.SetActive(go.Id);
            store.Save(dir);

            var loaded = AccountStore.Load(dir, null, NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));
            Assert.Equal(2, loaded.Profiles.Count);
            Assert.Equal(go.Id, loaded.ActiveId);
            Assert.Equal("GO 小号", loaded.Active!.Name);
            Assert.Equal("oc_sk_X", loaded.Active.GoKey);
            Assert.Equal("Fe26.2**x*y", loaded.Active.GoAuth);
            Assert.Equal("wrk_abc", loaded.Active.GoOrg);
            Assert.Equal("sk-xyz", loaded.Find(dsh.Id)!.DeepSeekKey);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Writes_readable_indented_json()
    {
        string dir = TempDir();
        try
        {
            var store = new AccountStore();
            store.Add("主号", AccountProfile.SourceDeepSeek);
            store.Save(dir);
            string text = File.ReadAllText(Path.Combine(dir, AccountStore.FileName));
            Assert.Contains("\n", text);                       // indented, not one line
            Assert.Contains("\"Profiles\"", text);
            Assert.Contains("主号", text);                      // 中文不转义成 \uXXXX
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_corrupt_file_is_kept_as_bad_instead_of_being_overwritten()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, AccountStore.FileName);
            File.WriteAllText(path, "{ this is not json");
            var store = AccountStore.Load(dir, "dsh", NoEnv, Path.Combine(dir, "no.yaml"), Path.Combine(dir, "no.json"));

            Assert.True(File.Exists(path + ".bad"));
            Assert.Contains("无法解析", store.LoadWarning);
            Assert.Single(store.Profiles);                      // still usable
            Assert.True(File.Exists(path));                     // and a fresh file was written
        }
        finally { Directory.Delete(dir, true); }
    }

    // --------------------------------------------------------------- edits ----

    [Fact]
    public void Active_falls_back_to_the_first_profile_rather_than_none()
    {
        var store = new AccountStore();
        Assert.Null(store.Active);
        AccountProfile a = store.Add("a", AccountProfile.SourceDeepSeek);
        store.ActiveId = "acc_gone";
        Assert.Equal(a.Id, store.Active!.Id);
    }

    [Fact]
    public void Removing_the_active_profile_moves_to_another_one()
    {
        var store = new AccountStore();
        AccountProfile a = store.Add("a", AccountProfile.SourceDeepSeek);
        AccountProfile b = store.Add("b", AccountProfile.SourceGo);
        store.SetActive(b.Id);

        Assert.True(store.Remove(b.Id));
        Assert.Equal(a.Id, store.ActiveId);
        Assert.False(store.Remove("acc_nope"));
    }

    [Fact]
    public void SetActive_ignores_an_unknown_id()
    {
        var store = new AccountStore();
        AccountProfile a = store.Add("a", AccountProfile.SourceDeepSeek);
        store.SetActive("acc_nope");
        Assert.Equal(a.Id, store.ActiveId);
    }

    [Fact]
    public void UniqueName_avoids_collisions_case_insensitively()
    {
        var store = new AccountStore();
        store.Add("小号", AccountProfile.SourceGo);
        store.Add("小号 2", AccountProfile.SourceGo);
        Assert.Equal("小号 3", store.UniqueName("小号"));
        Assert.Equal("别的", store.UniqueName("别的"));
    }

    // ------------------------------------------------------------- profile ----

    [Theory]
    [InlineData(AccountProfile.SourceDeepSeek, "", false)]
    [InlineData(AccountProfile.SourceDeepSeek, "sk-x", true)]
    [InlineData(AccountProfile.SourceGo, "", false)]
    public void Usable_follows_the_source_it_declares(string source, string dshKey, bool usable)
    {
        var p = new AccountProfile { Source = source, DeepSeekKey = dshKey };
        Assert.Equal(usable, p.IsUsable);
    }

    [Fact]
    public void A_go_profile_needs_go_credentials_not_a_deepseek_key()
    {
        var p = new AccountProfile { Source = AccountProfile.SourceGo, DeepSeekKey = "sk-x" };
        Assert.False(p.IsUsable);
        p.GoKey = "oc_sk_y";
        Assert.True(p.IsUsable);
    }

    [Fact]
    public void A_partial_cookie_set_is_not_a_cookie()
    {
        var p = new AccountProfile { Source = AccountProfile.SourceGo, GoAuth = "a", GoSession = "s" };
        Assert.False(p.HasGoCookie);
        p.GoOrg = "wrk_o";
        Assert.True(p.HasGoCookie);
    }

    [Fact]
    public void Describe_says_what_is_missing()
    {
        var dsh = new AccountProfile { Source = AccountProfile.SourceDeepSeek };
        Assert.Contains("未配置", dsh.Describe());
        dsh.DeepSeekKey = "sk-x";
        Assert.Contains("✓", dsh.Describe());

        var go = new AccountProfile { Source = AccountProfile.SourceGo, GoKey = "oc_sk_x" };
        Assert.Contains("无法逐次计费", go.Describe());     // key only: meters yes, itemising no
        go.GoAuth = "a"; go.GoSession = "s"; go.GoOrg = "wrk_o";
        Assert.Contains("Cookie ✓", go.Describe());
    }

    [Fact]
    public void Clone_copies_every_field()
    {
        var p = new AccountProfile
        {
            Id = "acc_1", Name = "n", Source = AccountProfile.SourceGo,
            DeepSeekKey = "d", GoKey = "k", GoAuth = "a", GoSession = "s", GoOrg = "o",
        };
        AccountProfile c = p.Clone();
        Assert.NotSame(p, c);
        Assert.Equal(p.Id, c.Id);
        Assert.Equal(p.Name, c.Name);
        Assert.Equal(p.Source, c.Source);
        Assert.Equal(p.DeepSeekKey, c.DeepSeekKey);
        Assert.Equal(p.GoKey, c.GoKey);
        Assert.Equal(p.GoAuth, c.GoAuth);
        Assert.Equal(p.GoSession, c.GoSession);
        Assert.Equal(p.GoOrg, c.GoOrg);
    }
}

public class DshCredentialsTests
{
    [Fact]
    public void Reads_the_key_out_of_dshs_credential_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dshpet-yaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "credentials.yaml");
            File.WriteAllText(path, "OTHER: 1\r\nDEEPSEEK_API_KEY: sk-abc123\r\nMORE: 2\r\n");
            Assert.Equal("sk-abc123", DshCredentials.LoadKey(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("NOTHING: here")]
    [InlineData("")]
    [InlineData("DEEPSEEK_API_KEY:")]
    public void Missing_key_yields_null(string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dshpet-yaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "credentials.yaml");
            File.WriteAllText(path, content);
            Assert.Null(DshCredentials.LoadKey(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_file_yields_null_rather_than_throwing()
    {
        Assert.Null(DshCredentials.LoadKey(Path.Combine(Path.GetTempPath(), "dshpet-nope-" + Guid.NewGuid().ToString("N"))));
    }
}
