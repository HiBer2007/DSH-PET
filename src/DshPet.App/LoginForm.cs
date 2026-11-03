using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshPet.App;

/// <summary>
/// Log in to OpenCode in a real browser view, then lift the console cookies out
/// of its cookie jar.
///
/// This exists because the alternative was copying a capture out of the browser
/// devtools every time the cookies expired - which is fiddly, easy to get wrong,
/// and was the single most common way for the GO side to end up half configured.
///
/// The browser profile lives in its own folder next to the app rather than in the
/// user's Edge profile, for two reasons: the user's own browsing is none of this
/// app's business, and a dedicated profile is what makes the login *persist* -
/// sign in once and the next launch is already authenticated. Being a real
/// Chromium view also means whatever the console does (email, OAuth, a redirect
/// chain) simply works, and it keeps working when they change it.
///
/// Cookies, not a saved password: nothing here ever sees the credentials, only
/// the session the server handed back.
/// </summary>
internal sealed class LoginForm : Form
{
    private const string ConsoleUrl = "https://opencode.ai/console";

    private static readonly Regex OrgInUrl =
        new(@"/console/((?:wrk|org)_[0-9A-Za-z]+)", RegexOptions.Compiled);

    /// <summary>
    /// The workspace id, anywhere in the page.
    ///
    /// The console's landing page is just <c>/console</c> - the id only shows up in the URL
    /// once you are deep inside a workspace, and the new login flow does not always land you
    /// there. The console ships the id in its server-rendered data, so reading the page
    /// finds it either way; this is what stops the save button from sitting disabled while
    /// the status line says "已拿到 Cookie，但还没看到工作区 ID".
    /// </summary>
    private static readonly Regex OrgInPage =
        new(@"((?:wrk|org)_[0-9A-Za-z]{8,})", RegexOptions.Compiled);

    private readonly string _profileFolder;
    private readonly WebView2 _web = new WebView2();
    private readonly Label _status = new Label();
    private readonly Label _hint = new Label();
    private readonly Button _finish = new Button();
    private readonly Button _cancel = new Button();
    private readonly Timer _poll = new Timer();

    private string _org = "";
    private string _auth = "";
    private string _session = "";
    private bool _ready;
    private bool _failed;

    /// <summary>
    /// Every cookie the console has set, by name.
    ///
    /// The whole jar is kept rather than just the two names the app was written around:
    /// the console renamed its login page once already, and hard-coding "auth" and
    /// "__Host-console_session" meant the save button silently never lit up. What the
    /// server hands back is what gets sent back.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<string, CoreWebView2Cookie> _jar =
        new System.Collections.Generic.Dictionary<string, CoreWebView2Cookie>();

    /// <summary>The credential lines to feed the account, once logged in.</summary>
    public string CapturedBlob { get; private set; } = "";

    public LoginForm(string profileFolder)
    {
        _profileFolder = profileFolder;

        Text = "登录 OpenCode";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(980, 720);
        Font = SystemFonts.MessageBoxFont;
        Icon = null;

        _web.SetBounds(12, 12, ClientSize.Width - 24, ClientSize.Height - 116);
        _web.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_web);

        _status.SetBounds(14, ClientSize.Height - 96, ClientSize.Width - 28, 22);
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _status.Text = "正在准备浏览器…";
        Controls.Add(_status);

        _hint.SetBounds(14, ClientSize.Height - 74, ClientSize.Width - 28, 20);
        _hint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _hint.ForeColor = SystemColors.GrayText;
        _hint.Text = "登录一次就会记住：浏览器配置存在 " + profileFolder + "，删掉它就等于退出登录。";
        Controls.Add(_hint);

        _finish.Text = "完成，保存到这个账户";
        _finish.SetBounds(ClientSize.Width - 194, ClientSize.Height - 46, 180, 30);
        _finish.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _finish.Enabled = false;
        _finish.Click += FinishClicked;
        Controls.Add(_finish);

        _cancel.Text = "取消";
        _cancel.SetBounds(ClientSize.Width - 284, ClientSize.Height - 46, 82, 30);
        _cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_cancel);

        CancelButton = _cancel;

        _poll.Interval = 1200;
        _poll.Tick += PollTick;
        Theme.Apply(this);              // the window around the browser follows the theme
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            Directory.CreateDirectory(_profileFolder);
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, _profileFolder);
            await _web.EnsureCoreWebView2Async(env);

            // The page itself decides its colours from prefers-color-scheme, so the widget's
            // theme is forwarded to the browser rather than fighting it with CSS.
            try
            {
                _web.CoreWebView2.Profile.PreferredColorScheme = Theme.Dark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
            catch { }

            // A sign-in flow that wants a popup gets the same view instead of the
            // system browser: the point is to end up logged in *here*, where the
            // cookie jar can be read.
            _web.CoreWebView2.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs a)
            {
                a.NewWindow = _web.CoreWebView2;
            };
            _web.CoreWebView2.SourceChanged += delegate { TrackUrl(); };
            _web.CoreWebView2.NavigationCompleted += delegate { TrackUrl(); UpdateStatus(); };
            _web.CoreWebView2.Navigate(ConsoleUrl);

            _poll.Start();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            ShowUnavailable(ex);
        }
    }

    /// <summary>
    /// The runtime is not always installed (it normally arrives with Edge). Say so
    /// plainly and point at the box that still works, rather than letting a
    /// COMException surface as a crash.
    /// </summary>
    private void ShowUnavailable(Exception ex)
    {
        _failed = true;
        _poll.Stop();
        _web.Visible = false;
        _status.Text = "打不开内置浏览器：" + ex.Message;
        _hint.Text = "这台机器缺少 WebView2 运行时（通常随 Edge 安装）。装上它就能用这个窗口，" +
                     "或者继续用「粘贴凭据」的方式登录 —— 功能完全一样，只是要手动复制。";
        _finish.Enabled = false;
    }

    /// <summary>
    /// What the browser currently sees, for the --loginprobe mode: the page the console
    /// redirected to and every cookie it has set, by name. Values are never printed, only
    /// their length - this is a diagnostic, not a credential dump.
    ///
    /// It exists because the console changed its login page once and the only symptom was
    /// "the save button never lights up": the app was looking for two cookie names that the
    /// new page no longer sets. Printing what is actually there turns that guessing game
    /// into a measurement.
    /// </summary>
    internal string Debug() {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string url = _web.CoreWebView2 == null ? "(no core)" : (_web.CoreWebView2.Source ?? "");
        sb.AppendLine("url=" + url);
        sb.AppendLine("cookies=" + _jar.Count + "  saveButton=" + (_ready ? "enabled" : "disabled") +
                      "  org=" + (_org.Length > 0 ? _org : "(none)"));
        foreach (System.Collections.Generic.KeyValuePair<string, CoreWebView2Cookie> kv in _jar) {
            CoreWebView2Cookie c = kv.Value;
            sb.AppendLine("  " + c.Name + "  len=" + c.Value.Length +
                          "  domain=" + c.Domain + "  path=" + c.Path +
                          "  httpOnly=" + c.IsHttpOnly + "  secure=" + c.IsSecure +
                          "  expires=" + (c.IsSession ? "session" : c.Expires.ToString("u")));
        }
        return sb.ToString().TrimEnd();
    }

    private void TrackUrl()
    {
        try
        {
            string url = _web.CoreWebView2 == null ? "" : (_web.CoreWebView2.Source ?? "");
            Match m = OrgInUrl.Match(url);
            if (m.Success) _org = m.Groups[1].Value;
        }
        catch (Exception) { }
    }

    private async void PollTick(object sender, EventArgs e)
    {
        if (_failed || _web.CoreWebView2 == null) return;
        try
        {
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://opencode.ai");
            string auth = "", session = "";
            _jar.Clear();
            for (int i = 0; i < cookies.Count; i++)
            {
                CoreWebView2Cookie c = cookies[i];
                _jar[c.Name] = c;
                if (c.Name == "auth") auth = c.Value;
                else if (c.Name == "__Host-console_session") session = c.Value;
            }
            if (auth != _auth || session != _session)
            {
                _auth = auth;
                _session = session;
            }
            TrackUrl();
            if (_org.Length == 0 && _ready && _web.CoreWebView2 != null)
            {
                string html = await _web.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement ? document.documentElement.outerHTML : ''");
                Match m = OrgInPage.Match(html ?? "");
                if (m.Success)
                {
                    _org = m.Groups[1].Value;
                    TrackUrl();                      // let the URL path keep precedence
                }
            }
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _status.Text = "读取 Cookie 失败：" + ex.Message;
        }
    }

    private void UpdateStatus()
    {
        string url = "";
        try { url = _web.CoreWebView2 == null ? "" : (_web.CoreWebView2.Source ?? ""); } catch { }
        // On the login page itself nothing counts yet. Off it, any cookie at all is enough:
        // which names the console uses is its business, and pinning them was what broke
        // the last time it changed.
        bool onLoginPage = url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0;
        bool haveJar = _jar.Count > 0 && !onLoginPage;
        bool haveNamed = _auth.Length > 0 && _session.Length > 0;
        _ready = haveJar || haveNamed;
        _finish.Enabled = _ready;

        if (_ready && _org.Length > 0)
            _status.Text = "已登录 ✓  工作区 " + _org + " —— 点右下角保存。";
        else if (_ready)
            _status.Text = "已拿到 " + _jar.Count + " 个 Cookie，但还没看到工作区 ID。请在页面里进一次 GO 控制台。";
        else if (onLoginPage)
            _status.Text = "请在窗口里登录 OpenCode…（页面在 " + ShortUrl(url) + "）";
        else
            _status.Text = _org.Length > 0
                ? "工作区 " + _org + "，等待登录…（" + ShortUrl(url) + "）"
                : "请在窗口里登录 OpenCode…（" + ShortUrl(url) + "）";
    }

    private static string ShortUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "(加载中)";
        try
        {
            Uri u = new Uri(url);
            return u.Host + u.AbsolutePath;
        }
        catch { return url; }
    }

    private void FinishClicked(object sender, EventArgs e)
    {
        if (!_ready) return;
        // Shaped like the credential file so the same scanner can read it back - one
        // parser for pasted captures and for logins, not two. The whole jar goes in as one
        // Cookie header value, which is what makes this survive a rename of the session
        // cookie; the two historical names are still written when they exist, because the
        // manual-paste path and older state.ini files speak that language.
        System.Text.StringBuilder jar = new System.Text.StringBuilder();
        foreach (System.Collections.Generic.KeyValuePair<string, CoreWebView2Cookie> kv in _jar)
        {
            if (jar.Length > 0) jar.Append("; ");
            jar.Append(kv.Key).Append('=').Append(kv.Value.Value);
        }
        CapturedBlob = "cookies=" + jar + "\r\n";
        if (_auth.Length > 0) CapturedBlob += "auth=" + _auth + "\r\n";
        if (_session.Length > 0) CapturedBlob += "session=" + _session + "\r\n";
        if (_org.Length > 0) CapturedBlob += "org=" + _org + "\r\n";
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _poll.Dispose();
        base.OnFormClosed(e);
    }
}
