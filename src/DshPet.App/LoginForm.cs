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
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            Directory.CreateDirectory(_profileFolder);
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, _profileFolder);
            await _web.EnsureCoreWebView2Async(env);

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
            for (int i = 0; i < cookies.Count; i++)
            {
                CoreWebView2Cookie c = cookies[i];
                if (c.Name == "auth") auth = c.Value;
                else if (c.Name == "__Host-console_session") session = c.Value;
            }
            if (auth != _auth || session != _session)
            {
                _auth = auth;
                _session = session;
            }
            TrackUrl();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _status.Text = "读取 Cookie 失败：" + ex.Message;
        }
    }

    private void UpdateStatus()
    {
        bool haveCookies = _auth.Length > 0 && _session.Length > 0;
        _ready = haveCookies;
        _finish.Enabled = haveCookies;

        if (haveCookies && _org.Length > 0)
            _status.Text = "已登录 ✓  工作区 " + _org + " —— 点右下角保存。";
        else if (haveCookies)
            _status.Text = "已拿到登录 Cookie，但还没看到工作区 ID。请在页面里进一次 GO 控制台。";
        else
            _status.Text = _org.Length > 0
                ? "工作区 " + _org + "，等待登录…"
                : "请在窗口里登录 OpenCode…";
    }

    private void FinishClicked(object sender, EventArgs e)
    {
        if (!_ready) return;
        // Shaped like the credential file so the same scanner can read it back -
        // one parser for pasted captures and for logins, not two.
        CapturedBlob = "auth=" + _auth + "\r\nsession=" + _session + "\r\n";
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
