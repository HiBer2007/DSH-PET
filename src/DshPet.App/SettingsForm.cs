using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using DshPet.App.Legacy;
// The form class shares its name with the root namespace, so it needs an alias
// to be usable as a type from inside that namespace.
using PetForm = DshPet.App.Legacy.DshPet;

namespace DshPet.App;

/// <summary>
/// The settings window: accounts on one tab, looks and behaviour on the other.
///
/// Everything applies immediately. The pet sits behind the dialog, so a size or
/// position change is visible while you drag the control, and switching the active
/// account changes the tablet on the spot - which is why the button is "done"
/// rather than OK/Cancel. There is no pending state to cancel, and pretending
/// otherwise would be a lie about what already happened.
///
/// The test and login buttons touch the network. The probes run on a worker thread
/// and post their result back, so a fifteen second timeout cannot freeze the
/// window.
///
/// Hand-laid-out with explicit pixel bounds rather than a designer file: the
/// layout is small enough to read in one screen, and a generated .Designer.cs
/// would be a third file to keep in sync for no benefit.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly PetForm _pet;

    // ---- tab 1: accounts
    private readonly ListBox _accountList = new ListBox();
    private readonly Button _addAccount = new Button();
    private readonly Button _renameAccount = new Button();
    private readonly Button _deleteAccount = new Button();
    private readonly Button _useAccount = new Button();
    private readonly TextBox _accountName = new TextBox();
    private readonly Button _applyName = new Button();
    private readonly RadioButton _typeDsh = new RadioButton();
    private readonly RadioButton _typeGo = new RadioButton();
    private readonly Label _accountStatus = new Label();
    private readonly Label _dshKeyLabel = new Label();
    private readonly TextBox _dshKey = new TextBox();
    private readonly CheckBox _dshReveal = new CheckBox();
    private readonly Button _dshSave = new Button();
    private readonly Label _goBlobLabel = new Label();
    private readonly TextBox _goBlob = new TextBox();
    private readonly Button _goSave = new Button();
    private readonly Button _goLogin = new Button();
    private readonly Button _test = new Button();
    private readonly Label _testResult = new Label();

    private string _selectedId = "";

    // ---- tab 2: looks and behaviour
    private readonly NumericUpDown _cm = new NumericUpDown();
    private readonly RadioButton _posLeft = new RadioButton();
    private readonly RadioButton _posRight = new RadioButton();
    private readonly NumericUpDown _pollSeconds = new NumericUpDown();
    private readonly CheckBox _sound = new CheckBox();
    private readonly CheckBox _clickThrough = new CheckBox();
    private readonly CheckBox _carousel = new CheckBox();
    private readonly NumericUpDown _carouselSeconds = new NumericUpDown();
    private readonly NumericUpDown _warnPercent = new NumericUpDown();
    private readonly NumericUpDown _warnCny = new NumericUpDown();
    private readonly TrackBar _volume = new TrackBar();
    private readonly Label _volumeLabel = new Label();
    private readonly Button _preview = new Button();

    private readonly Label _paths = new Label();

    public SettingsForm(PetForm pet)
    {
        _pet = pet;
        PetForm.SettingsView view = pet.ViewSettings();

        Text = "DSH 余额宠物 — 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;

        TabControl tabs = new TabControl();
        tabs.SetBounds(8, 8, 624, 520);
        TabPage accountPage = new TabPage("账户");
        TabPage lookPage = new TabPage("外观与行为");
        tabs.TabPages.Add(accountPage);
        tabs.TabPages.Add(lookPage);
        Controls.Add(tabs);

        BuildAccountPage(accountPage);
        BuildLookPage(lookPage);

        ClientSize = new Size(640, 596);
        BuildFooter(536);

        LoadFromView(view);
        RefreshAccountList();
    }

    // ------------------------------------------------------------- accounts --

    private void BuildAccountPage(TabPage page)
    {
        GroupBox list = Group("账户列表（右键菜单里也能直接切换）", 8, 6, 596, 144);
        page.Controls.Add(list);

        _accountList.SetBounds(14, 22, 400, 108);
        _accountList.IntegralHeight = false;
        _accountList.SelectedIndexChanged += delegate { OnAccountSelected(); };
        list.Controls.Add(_accountList);

        // A 2x2 grid inside the group: the group box's client area is ~590 wide
        // (borders), and a button at x=516 with width 86 pokes 12px out of it.
        Buttonize(list, _addAccount, "新增账户", 420, 22, AddAccountClicked);
        Buttonize(list, _useAccount, "设为当前", 420, 54, UseAccountClicked);
        Buttonize(list, _renameAccount, "重命名", 420, 86, RenameAccountClicked);
        Buttonize(list, _deleteAccount, "删除", 500, 86, DeleteAccountClicked);

        GroupBox detail = Group("选中账户", 8, 156, 596, 330);
        page.Controls.Add(detail);

        LabelRef(detail, "名称", 14, 27, 44);
        _accountName.SetBounds(60, 24, 240, 24);
        detail.Controls.Add(_accountName);
        Buttonize(detail, _applyName, "改名字", 308, 22, ApplyNameClicked);

        _typeDsh.Text = "DeepSeek 余额";
        _typeDsh.SetBounds(14, 52, 150, 22);
        _typeDsh.CheckedChanged += delegate { if (_typeDsh.Checked) TypeChanged("dsh"); };
        detail.Controls.Add(_typeDsh);

        _typeGo.Text = "OpenCode GO 额度";
        _typeGo.SetBounds(170, 52, 180, 22);
        _typeGo.CheckedChanged += delegate { if (_typeGo.Checked) TypeChanged("go"); };
        detail.Controls.Add(_typeGo);

        _accountStatus.SetBounds(14, 78, 570, 20);
        _accountStatus.ForeColor = SystemColors.GrayText;
        detail.Controls.Add(_accountStatus);

        _dshKeyLabel.Text = "API Key";
        _dshKeyLabel.SetBounds(14, 106, 62, 20);
        detail.Controls.Add(_dshKeyLabel);

        _dshKey.SetBounds(80, 103, 330, 24);
        _dshKey.UseSystemPasswordChar = true;
        detail.Controls.Add(_dshKey);

        _dshReveal.Text = "显示";
        _dshReveal.SetBounds(416, 104, 56, 22);
        _dshReveal.CheckedChanged += delegate { _dshKey.UseSystemPasswordChar = !_dshReveal.Checked; };
        detail.Controls.Add(_dshReveal);

        Buttonize(detail, _dshSave, "保存", 478, 102, SaveCredentialsClicked);

        _goBlobLabel.Text = "在下面登录，或者把浏览器里抓到的内容整段粘到这里（含 auth、__Host-console_session 与 wrk_/org_ 工作区）";
        _goBlobLabel.SetBounds(14, 106, 570, 20);
        detail.Controls.Add(_goBlobLabel);

        _goBlob.Multiline = true;
        _goBlob.WordWrap = true;
        _goBlob.ScrollBars = ScrollBars.Vertical;
        _goBlob.SetBounds(14, 128, 560, 88);
        detail.Controls.Add(_goBlob);

        Buttonize(detail, _goSave, "解析并保存", 14, 224, SaveCredentialsClicked);
        Buttonize(detail, _goLogin, "登录 OpenCode…", 108, 224, LoginClicked);
        Buttonize(detail, _test, "测试连接", 202, 224, TestClicked);

        _testResult.SetBounds(300, 222, 276, 54);
        detail.Controls.Add(_testResult);
    }

    private void AddAccountClicked(object sender, EventArgs e)
    {
        string chosen = Ask("新增账户", "账户名称", "新账户");
        if (chosen == null) return;
        _selectedId = _pet.AddAccount(chosen, _typeGo.Checked ? "go" : "dsh");
        RefreshAccountList();
    }

    private void RenameAccountClicked(object sender, EventArgs e)
    {
        PetForm.AccountView current = Selected();
        if (current == null) return;
        string chosen = Ask("重命名账户", "账户名称", current.Name);
        if (chosen == null) return;
        _pet.RenameAccount(_selectedId, chosen);
        RefreshAccountList();
    }

    private void DeleteAccountClicked(object sender, EventArgs e)
    {
        PetForm.AccountView current = Selected();
        if (current == null) return;
        if (_pet.ViewAccounts().Count <= 1)
        {
            MessageBox.Show(this, "至少要保留一个账户。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, "删除账户「" + current.Name + "」？\n它的凭据会一起删掉。", Text,
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _pet.DeleteAccount(_selectedId);
        _selectedId = "";
        RefreshAccountList();
    }

    private void UseAccountClicked(object sender, EventArgs e)
    {
        if (_selectedId.Length == 0) return;
        _pet.ActivateAccount(_selectedId);
        RefreshAccountList();
    }

    private void ApplyNameClicked(object sender, EventArgs e)
    {
        if (_selectedId.Length == 0) return;
        _pet.RenameAccount(_selectedId, _accountName.Text);
        RefreshAccountList();
    }

    private void TypeChanged(string source)
    {
        if (_selectedId.Length == 0) return;
        _pet.SetAccountSource(_selectedId, source);
        RefreshAccountDetail();
        RefreshAccountList();
    }

    private void SaveCredentialsClicked(object sender, EventArgs e)
    {
        if (_selectedId.Length == 0) return;
        string key = _typeDsh.Checked ? _dshKey.Text : "";
        string blob = _typeGo.Checked ? _goBlob.Text : "";
        if (key.Trim().Length == 0 && blob.Trim().Length == 0)
        {
            _testResult.ForeColor = SystemColors.GrayText;
            _testResult.Text = "没填内容，未做修改";
            return;
        }
        string summary = _pet.SaveAccountCredentials(_selectedId, key, blob);
        _dshKey.Clear();
        _goBlob.Clear();
        _testResult.ForeColor = Color.FromArgb(0, 120, 60);
        _testResult.Text = "已保存：" + summary;
        RefreshAccountList();
        RefreshAccountDetail();
    }

    /// <summary>
    /// Sign in inside the app and take the cookies straight out of the browser's
    /// jar, so copying a capture out of devtools is only the fallback path.
    /// </summary>
    private void LoginClicked(object sender, EventArgs e)
    {
        if (_selectedId.Length == 0) return;
        if (_typeDsh.Checked)
        {
            _testResult.ForeColor = SystemColors.GrayText;
            _testResult.Text = "DeepSeek 账户不需要登录，填 API Key 就行。";
            return;
        }

        using (LoginForm login = new LoginForm(Path.Combine(_pet.ViewSettings().BaseDir, "webview2")))
        {
            if (login.ShowDialog(this) != DialogResult.OK) return;
            string summary = _pet.SaveAccountCredentials(_selectedId, "", login.CapturedBlob);
            _testResult.ForeColor = Color.FromArgb(0, 120, 60);
            _testResult.Text = "登录成功，已保存：" + summary;
            RefreshAccountList();
            RefreshAccountDetail();
        }
    }

    private void TestClicked(object sender, EventArgs e)
    {
        if (_selectedId.Length == 0) return;
        // Testing means testing the account you are looking at, so make it the live
        // one first - otherwise the result would describe a profile the widget is
        // not actually reading.
        _pet.ActivateAccount(_selectedId);
        RunTest(_test, _testResult, _typeGo.Checked ? (Func<string>)_pet.TestGoConnection
                                                   : (Func<string>)_pet.TestDeepSeek);
    }

    private PetForm.AccountView Selected()
    {
        return _selectedId.Length == 0 ? null : _pet.ViewAccount(_selectedId);
    }

    private void OnAccountSelected()
    {
        ListBoxItem item = _accountList.SelectedItem as ListBoxItem;
        if (item == null) return;
        _selectedId = item.Id;
        RefreshAccountDetail();
    }

    private void RefreshAccountList()
    {
        _accountList.BeginUpdate();
        _accountList.Items.Clear();
        int select = -1;
        var accounts = _pet.ViewAccounts();
        for (int i = 0; i < accounts.Count; i++)
        {
            PetForm.AccountView a = accounts[i];
            _accountList.Items.Add(new ListBoxItem(a.Id, (a.IsActive ? "● " : "    ") + a.Name + " — " + a.Describe));
            if (a.Id == _selectedId) select = i;
        }
        if (select < 0 && _accountList.Items.Count > 0) select = 0;
        _accountList.SelectedIndex = select;
        _accountList.EndUpdate();
        RefreshAccountDetail();
    }

    private void RefreshAccountDetail()
    {
        PetForm.AccountView a = Selected();
        bool has = a != null;

        _accountName.Enabled = has;
        _applyName.Enabled = has;
        _typeDsh.Enabled = has;
        _typeGo.Enabled = has;
        _dshSave.Enabled = has;
        _goSave.Enabled = has;
        _goLogin.Enabled = has;
        _test.Enabled = has;
        _renameAccount.Enabled = has;
        _deleteAccount.Enabled = has;
        _useAccount.Enabled = has && !a.IsActive;

        if (!has)
        {
            _accountStatus.Text = "先在上面选一个账户。";
            _dshKeyLabel.Visible = _dshKey.Visible = _dshReveal.Visible = _dshSave.Visible = false;
            _goBlobLabel.Visible = _goBlob.Visible = _goSave.Visible = _goLogin.Visible = false;
            return;
        }

        _accountName.Text = a.Name;
        _typeDsh.Checked = !a.IsGo;
        _typeGo.Checked = a.IsGo;
        _accountStatus.Text = (a.IsActive ? "当前正在监控。" : "未监控（右键菜单或「设为当前」可切换）。")
                            + "  " + a.Describe;

        // Only the fields this account's type actually uses are shown, so a GO
        // cookie cannot be pasted into a DeepSeek account by accident.
        _dshKeyLabel.Visible = _dshKey.Visible = _dshReveal.Visible = _dshSave.Visible = !a.IsGo;
        _goBlobLabel.Visible = _goBlob.Visible = _goSave.Visible = _goLogin.Visible = a.IsGo;
        _test.Text = a.IsGo ? "测试 GO" : "测试余额";
    }

    // ------------------------------------------------------- looks/behaviour --

    private void BuildLookPage(TabPage page)
    {
        GroupBox look = Group("外观", 8, 6, 596, 92);
        page.Controls.Add(look);

        LabelRef(look, "尺寸", 14, 32, 40);

        _cm.DecimalPlaces = 1;
        _cm.Increment = 0.5m;
        _cm.Minimum = 0.8m;
        _cm.Maximum = 40m;
        _cm.SetBounds(58, 29, 74, 24);
        _cm.ValueChanged += delegate { _pet.UiSetSize((double)_cm.Value); };
        look.Controls.Add(_cm);

        LabelRef(look, "厘米（正方形）", 140, 32, 110);

        _posLeft.Text = "左下角";
        _posLeft.SetBounds(300, 31, 84, 22);
        _posLeft.CheckedChanged += delegate { if (_posLeft.Checked) _pet.UiSetMirror(false); };
        look.Controls.Add(_posLeft);

        _posRight.Text = "右下角（镜像）";
        _posRight.SetBounds(392, 31, 140, 22);
        _posRight.CheckedChanged += delegate { if (_posRight.Checked) _pet.UiSetMirror(true); };
        look.Controls.Add(_posRight);

        Label hint = new Label();
        hint.Text = "拖动挂件松手后会自动回到选定的角落。被别的置顶窗口盖住时，右键菜单第一项可以立刻抢回顶层。";
        hint.SetBounds(14, 60, 570, 20);
        hint.ForeColor = SystemColors.GrayText;
        look.Controls.Add(hint);

        GroupBox interact = Group("交互", 8, 106, 596, 76);
        page.Controls.Add(interact);

        _clickThrough.Text = "点击穿透（鼠标点上去会穿到下面的窗口）";
        _clickThrough.SetBounds(14, 26, 330, 22);
        _clickThrough.CheckedChanged += delegate { _pet.UiSetClickThrough(_clickThrough.Checked); };
        interact.Controls.Add(_clickThrough);

        _carousel.Text = "GO 额度窗口轮播（5 秒换一个）";
        _carousel.SetBounds(360, 26, 230, 22);
        _carousel.CheckedChanged += delegate { _pet.UiSetCarousel(_carousel.Checked); };
        interact.Controls.Add(_carousel);

        Label ctrlHint = new Label();
        ctrlHint.Text = "点击穿透开启后按住 Ctrl 就能照常拖动它、右键出菜单（和 Rainmeter 一样）。" +
                        "轮播只换平板上显示哪个额度窗口，不影响记账；额度低于 30% 转黄、低于 10% 转红。";
        ctrlHint.SetBounds(14, 48, 570, 20);
        ctrlHint.ForeColor = SystemColors.GrayText;
        interact.Controls.Add(ctrlHint);

        GroupBox behaviour = Group("刷新与音效", 8, 190, 596, 128);
        page.Controls.Add(behaviour);

        LabelRef(behaviour, "刷新频率", 14, 32, 70);

        _pollSeconds.DecimalPlaces = 1;
        _pollSeconds.Increment = 1m;
        _pollSeconds.Minimum = 1m;
        _pollSeconds.Maximum = 600m;
        _pollSeconds.SetBounds(90, 29, 74, 24);
        _pollSeconds.ValueChanged += delegate { _pet.UiSetPollSeconds((double)_pollSeconds.Value); };
        behaviour.Controls.Add(_pollSeconds);

        LabelRef(behaviour, "秒（最小 1 秒）", 170, 32, 120);

        _sound.Text = "扣血音效";
        _sound.SetBounds(300, 31, 100, 22);
        _sound.CheckedChanged += delegate
        {
            _volume.Enabled = _sound.Checked;
            _preview.Enabled = _sound.Checked;
            _pet.UiSetSound(_sound.Checked, _volume.Value);
        };
        behaviour.Controls.Add(_sound);

        _volume.Minimum = 0;
        _volume.Maximum = 100;
        _volume.TickFrequency = 10;
        _volume.SetBounds(14, 62, 420, 40);
        _volume.ValueChanged += delegate
        {
            _volumeLabel.Text = _volume.Value + "%";
            _pet.UiSetSound(_sound.Checked, _volume.Value);
        };
        behaviour.Controls.Add(_volume);

        _volumeLabel.SetBounds(440, 72, 50, 20);
        behaviour.Controls.Add(_volumeLabel);

        Buttonize(behaviour, _preview, "试听", 500, 68, delegate { _pet.UiPreviewSound(); });

        GroupBox alerts = Group("提醒与轮播", 8, 326, 596, 128);
        page.Controls.Add(alerts);

        LabelRef(alerts, "轮播速率", 14, 30, 70);
        _carouselSeconds.DecimalPlaces = 0;
        _carouselSeconds.Increment = 1m;
        _carouselSeconds.Minimum = 2m;
        _carouselSeconds.Maximum = 3600m;
        _carouselSeconds.SetBounds(90, 27, 74, 24);
        _carouselSeconds.ValueChanged += delegate { _pet.UiSetCarouselSeconds((int)_carouselSeconds.Value); };
        alerts.Controls.Add(_carouselSeconds);
        LabelRef(alerts, "秒 / 个窗口（2–3600）", 170, 30, 170);

        LabelRef(alerts, "GO 提醒阈值", 14, 62, 84);
        _warnPercent.DecimalPlaces = 0;
        _warnPercent.Increment = 5m;
        _warnPercent.Minimum = 0m;
        _warnPercent.Maximum = 100m;
        _warnPercent.SetBounds(102, 59, 58, 24);
        _warnPercent.ValueChanged += delegate { _pet.UiSetWarnPercent((int)_warnPercent.Value); };
        alerts.Controls.Add(_warnPercent);
        LabelRef(alerts, "% 剩余（0 = 只在用完时提醒）", 168, 62, 210);

        LabelRef(alerts, "余额阈值", 384, 62, 60);
        _warnCny.DecimalPlaces = 1;
        _warnCny.Increment = 1m;
        _warnCny.Minimum = 0m;
        _warnCny.Maximum = 100000m;
        _warnCny.SetBounds(446, 59, 74, 24);
        _warnCny.ValueChanged += delegate { _pet.UiSetWarnCny((double)_warnCny.Value); };
        alerts.Controls.Add(_warnCny);
        LabelRef(alerts, "元", 524, 62, 24);

        Label alertHint = new Label();
        alertHint.Text = "气泡只在「刚用完 / 刚跌破阈值 / 刚重置」时各弹一次，不会反复提醒；" +
                         "用完的那个窗口，平板上的数字会自动换成重置倒计时。双击角色可随时召唤一次。";
        alertHint.SetBounds(14, 90, 570, 32);
        alertHint.ForeColor = SystemColors.GrayText;
        alerts.Controls.Add(alertHint);
    }

    // -------------------------------------------------------------- chrome ----

    private void BuildFooter(int y)
    {
        _paths.Text = "配置目录：" + _pet.ViewSettings().BaseDir + "    账户与凭据存在 accounts.json";
        _paths.SetBounds(20, y, 600, 20);
        _paths.ForeColor = SystemColors.GrayText;
        Controls.Add(_paths);

        Button openLog = new Button();
        openLog.Text = "查看日志";
        openLog.SetBounds(14, y + 28, 90, 28);
        openLog.Click += delegate { OpenPath(_pet.ViewSettings().LogPath); };
        Controls.Add(openLog);

        Button openDir = new Button();
        openDir.Text = "打开目录";
        openDir.SetBounds(112, y + 28, 90, 28);
        openDir.Click += delegate { OpenPath(_pet.ViewSettings().BaseDir); };
        Controls.Add(openDir);

        Button done = new Button();
        done.Text = "完成";
        done.SetBounds(542, y + 28, 90, 28);
        done.DialogResult = DialogResult.OK;
        Controls.Add(done);

        AcceptButton = done;
        CancelButton = done;
    }

    private static GroupBox Group(string title, int x, int y, int width, int height)
    {
        GroupBox box = new GroupBox();
        box.Text = title;
        box.SetBounds(x, y, width, height);
        return box;
    }

    private static void LabelRef(Control parent, string text, int x, int y, int width)
    {
        Label label = new Label();
        label.Text = text;
        label.SetBounds(x, y, width, 20);
        parent.Controls.Add(label);
    }

    private static void Buttonize(Control parent, Button button, string text, int x, int y, EventHandler onClick)
    {
        button.Text = text;
        button.SetBounds(x, y, 86, 28);
        button.Click += onClick;
        parent.Controls.Add(button);
    }

    private void LoadFromView(PetForm.SettingsView view)
    {
        _cm.Value = Clamp((decimal)view.Cm, _cm.Minimum, _cm.Maximum);
        _posRight.Checked = view.Mirror;
        _posLeft.Checked = !view.Mirror;
        _pollSeconds.Value = Clamp((decimal)(view.PollMs / 1000.0), _pollSeconds.Minimum, _pollSeconds.Maximum);

        _sound.Checked = view.SoundEnabled;
        _volume.Value = view.Volume < 0 ? 0 : (view.Volume > 100 ? 100 : view.Volume);
        _volumeLabel.Text = _volume.Value + "%";
        _volume.Enabled = view.SoundEnabled;
        _preview.Enabled = view.SoundEnabled;
        _clickThrough.Checked = view.ClickThrough;
        _carousel.Checked = view.Carousel;
        _carouselSeconds.Value = Clamp(view.CarouselSeconds, _carouselSeconds.Minimum, _carouselSeconds.Maximum);
        _warnPercent.Value = Clamp(view.WarnPercent, _warnPercent.Minimum, _warnPercent.Maximum);
        _warnCny.Value = Clamp((decimal)view.WarnCny, _warnCny.Minimum, _warnCny.Maximum);
    }

    private static decimal Clamp(decimal value, decimal min, decimal max) =>
        value < min ? min : (value > max ? max : value);

    private string Ask(string title, string prompt, string initial)
    {
        using (InputDialog d = new InputDialog(title, prompt, "", initial))
        {
            return d.ShowDialog(this) == DialogResult.OK ? d.Value : null;
        }
    }

    // -------------------------------------------------------- test plumbing --

    /// <summary>
    /// Runs a blocking probe off the UI thread: the button stays disabled and the
    /// result lands in the label when it arrives.
    /// </summary>
    private void RunTest(Button button, Label result, Func<string> probe)
    {
        button.Enabled = false;
        result.ForeColor = SystemColors.GrayText;
        result.Text = "测试中…";

        ThreadPool.QueueUserWorkItem(delegate
        {
            string text;
            bool ok;
            try
            {
                text = probe();
                ok = text.IndexOf('✓') >= 0;
            }
            catch (Exception ex)
            {
                text = "失败：" + ex.Message;
                ok = false;
            }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    result.ForeColor = ok ? Color.FromArgb(0, 120, 60) : Color.FromArgb(170, 40, 30);
                    result.Text = text;
                    button.Enabled = true;
                });
            }
            catch (InvalidOperationException)
            {
                // the window was closed while the probe was in flight
            }
        });
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                MessageBox.Show(this, "还没有这个文件：\r\n" + path, Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打不开：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>A list box entry that remembers which account it stands for.</summary>
    private sealed class ListBoxItem
    {
        public readonly string Id;
        private readonly string _text;
        public ListBoxItem(string id, string text) { Id = id; _text = text; }
        public override string ToString() { return _text; }
    }

    // ------------------------------------------------------ self-check hooks --

    /// <summary>
    /// What every control currently shows, for the --uicheck mode. The wiring
    /// between this window and the widget is the part most likely to break
    /// silently - a wrong index, a control nobody reads - and the part a human
    /// cannot reasonably re-check after every change.
    /// </summary>
    internal string ControlSummary() =>
        "accounts=" + _accountList.Items.Count +
        " selected=" + _selectedId +
        " type=" + (_typeGo.Checked ? "go" : "dsh") +
        " cm=" + _cm.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
        " poll=" + _pollSeconds.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
        " mirror=" + _posRight.Checked +
        " sound=" + _sound.Checked +
        " vol=" + _volume.Value +
        " click=" + _clickThrough.Checked +
        " carousel=" + _carousel.Checked +
        " carousel_s=" + _carouselSeconds.Value +
        " warn_pct=" + _warnPercent.Value +
        " warn_cny=" + _warnCny.Value;

    internal string SelectedAccountName() { return _accountName.Text; }

    /// <summary>
    /// Bounds check, per parent: a control outside its container is a layout bug
    /// (and with a TabControl in play the container is the page, not the form).
    /// </summary>
    internal string OutOfBounds() { return OutOfBounds(this); }

    private static string OutOfBounds(Control parent)
    {
        string bad = "";
        foreach (Control c in parent.Controls)
        {
            if (c.Left < 0 || c.Top < 0 || c.Right > parent.ClientSize.Width || c.Bottom > parent.ClientSize.Height)
                bad += (bad.Length > 0 ? ", " : "") + c.GetType().Name + "(\"" + c.Text + "\") " +
                       "[" + c.Left + "," + c.Top + " " + c.Width + "x" + c.Height + "] in " +
                       parent.GetType().Name + "(" + parent.ClientSize.Width + "x" + parent.ClientSize.Height + ")";
            if (c.Controls.Count > 0)
            {
                string nested = OutOfBounds(c);
                if (nested.Length > 0) bad += (bad.Length > 0 ? ", " : "") + nested;
            }
        }
        return bad;
    }
}
