# ============================================================================
#  DSH balance pet  (v2)
#
#  A draggable desktop overlay: the character holds a tablet whose screen stays
#  transparent and shows the live DSH balance. Every time the balance drops by
#  the configured step (0.1 CNY by default) the character flashes red and shakes
#  (Minecraft hurt style) and a red "-0.1" floats up above their head.
#
#  v2:
#    - new artwork; the tablet screen is left fully transparent (no fill)
#    - only the balance label + the number are drawn on the screen
#    - right-click -> per-charge amount (default 0.1); the floating number and
#      the screen readout both follow it
#    - drag with the left button only; on release it snaps to the bottom-left
#    - default size 2cm x 2cm, right-click -> size to change it
#    - "refresh now" reports the outcome instead of silently doing nothing
#
#  Windows PowerShell 5.1 + WinForms. No external packages, no admin rights.
#  ASCII-only: PS 5.1 reads .ps1 without a BOM as ANSI, so all UI text lives in
#  C# \uXXXX escapes inside the here-string below.
# ============================================================================

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ('DshPet' -as [type])) {
Add-Type -ReferencedAssemblies @('System.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll') -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// ------------------------------------------------------------------ utils ---

// Raw ARGB access. Unlock as soon as possible: GDI+ must never draw into a
// bitmap while it is still locked.
public sealed class Buf : IDisposable {
    public readonly Bitmap Bmp;
    public readonly int W, H, Stride;
    public readonly byte[] P;
    readonly BitmapData _d;

    public Buf(Bitmap b) {
        Bmp = b; W = b.Width; H = b.Height;
        _d = b.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        Stride = _d.Stride;
        P = new byte[Stride * H];
        Marshal.Copy(_d.Scan0, P, 0, P.Length);
    }
    public void Flush() { Marshal.Copy(P, 0, _d.Scan0, P.Length); }
    public void Dispose() { Bmp.UnlockBits(_d); }
}

public static class Cs {
    // src over dst with straight (non-premultiplied) alpha
    public static void Blend(byte[] d, int i, int r, int g, int b, double a) {
        if (a <= 0) return;
        if (a > 1) a = 1;
        double da = d[i + 3] / 255.0;
        double oa = a + da * (1 - a);
        if (oa <= 0.0001) { d[i] = 0; d[i+1] = 0; d[i+2] = 0; d[i+3] = 0; return; }
        d[i]     = (byte)Math.Round((b * a + d[i]     * da * (1 - a)) / oa);
        d[i + 1] = (byte)Math.Round((g * a + d[i + 1] * da * (1 - a)) / oa);
        d[i + 2] = (byte)Math.Round((r * a + d[i + 2] * da * (1 - a)) / oa);
        d[i + 3] = (byte)Math.Round(oa * 255);
    }
}

internal static class Native {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObj);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    public const int ULW_ALPHA = 0x02;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
}

// ------------------------------------------------------------- animations ---

sealed class Hit {
    public double T;
    public const double Dur = 0.55;
    public bool Done { get { return T >= Dur; } }
    public double Pulse {
        get {
            if (T < 0.20) return 1.0;                       // solid flash on impact
            double e = Math.Max(0, 1 - (T - 0.20) / (Dur - 0.20));
            return Math.Sin((T - 0.20) * 26) * 0.55 * e * e;
        }
    }
}

sealed class Floater {
    public double T, Dur = 1.05, Jitter;
    public int X, Y;
    public string Text;
    public float Mul = 1f;     // number size multiplier: bigger deduction, bigger text
    public bool Done { get { return T >= Dur; } }
}

// Small modal input dialog. ShowDialog pumps its own loop, so it works on top
// of the layered main window.
sealed class InputDialog : Form {
    readonly TextBox _box;
    public InputDialog(string title, string prompt, string unit, string initial)
        : this(title, prompt, unit, initial, false) { }

    // multi = a big scrollable box instead of the one-line field. The OpenCode
    // credential blob is a few hundred characters of cookie, so the single-line
    // layout would cut it off.
    public InputDialog(string title, string prompt, string unit, string initial, bool multi) {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        TopMost = true;
        int w = multi ? 620 : 330, h = multi ? 300 : 140;
        ClientSize = new Size(w, h);

        Label lab = new Label();
        lab.Text = prompt;
        lab.SetBounds(14, 10, w - 28, 32);
        Controls.Add(lab);

        _box = new TextBox();
        _box.Text = initial;
        if (multi) {
            _box.Multiline = true;
            _box.WordWrap = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.SetBounds(14, 46, w - 28, h - 112);
        } else {
            _box.SetBounds(14, 46, 230, 24);
        }
        Controls.Add(_box);

        if (!multi) {
            Label u = new Label();
            u.Text = unit;
            u.SetBounds(250, 49, 70, 20);
            Controls.Add(u);
        }

        int by = h - 40;
        Button ok = new Button();
        ok.Text = "OK";
        ok.DialogResult = DialogResult.OK;
        ok.SetBounds(w - 170, by, 75, 26);
        Controls.Add(ok);

        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(w - 85, by, 75, 26);
        Controls.Add(cancel);

        AcceptButton = ok; CancelButton = cancel;
        _box.SelectAll();
        _box.Focus();
    }
    public string Value { get { return _box.Text.Trim(); } }
}

// ---------------------------------------------------------------- sound ----
//
// Overlapping hit sounds through the Win32 MCI interface: every cue is played
// on its own alias, so a new hit never cuts off the previous one.
public sealed class SoundPool {
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendStringW(string cmd, StringBuilder ret, int len, IntPtr hwnd);
    static string Send(string cmd) {
        StringBuilder sb = new StringBuilder(256);
        mciSendStringW(cmd, sb, sb.Capacity, IntPtr.Zero);
        return sb.ToString();
    }

    readonly string _path;
    readonly int _slots;
    readonly string[] _alias;
    readonly bool[] _open;
    readonly string _logPath;
    public bool Failed; public string Error = ""; public int Plays;

    public SoundPool(string path, int slots, int volumePercent, string logPath) {
        _path = path; _slots = slots; _logPath = logPath;
        _alias = new string[slots]; _open = new bool[slots];
        try {
            // MCI receives a mangled path when the folder name is not ASCII, so
            // switch the process working directory to the sound's own folder and
            // have MCI open the bare file name instead of a full path.
            try { Directory.SetCurrentDirectory(Path.GetDirectoryName(path)); } catch { }
            string bare = Path.GetFileName(path);
            for (int i = 0; i < slots; i++) {
                _alias[i] = "dsphpet" + i;
                Send("close " + _alias[i]);
                _open[i] = Send("open \"" + bare + "\" type mpegvideo alias " + _alias[i]).Length == 0;
                if (!_open[i]) _open[i] = Send("open \"" + path + "\" type mpegvideo alias " + _alias[i]).Length == 0;
                if (_open[i]) Send("setaudio " + _alias[i] + " volume to " + volumePercent * 10);
            }
            if (!_open[0]) { Failed = true; Error = "cannot open " + path; }
        } catch (Exception ex) { Failed = true; Error = ex.Message; }
    }

    // plays on a free alias and returns its index, or -1 when nothing played
    public int Play() {
        if (Failed) return -1;
        try {
            for (int attempt = 0; attempt < 2; attempt++) {
                for (int i = 0; i < _slots; i++) {
                    if (!_open[i]) continue;
                    if (Send("status " + _alias[i] + " mode").IndexOf("playing") >= 0) continue;
                    Send("seek " + _alias[i] + " to start");
                    Send("play " + _alias[i]);
                    Plays++;
                    return i;
                }
                // every slot is busy: reopen the first so a burst still overlaps
                if (attempt == 0) {
                    Send("close " + _alias[0]);
                    _open[0] = Send("open \"" + _path + "\" type mpegvideo alias " + _alias[0]).Length == 0;
                }
            }
        } catch (Exception ex) {
            Failed = true; Error = ex.Message;
            try { File.AppendAllText(_logPath, DateTime.Now.ToString("s") + " sound: " + ex.Message + "\r\n"); } catch { }
        }
        return -1;
    }

    public void Dispose() {
        try { for (int i = 0; i < _slots; i++) if (_open[i]) Send("close " + _alias[i]); } catch { }
    }
}
// ------------------------------------------------------------------ window ---

public sealed class DshPet : Form {
    const string S_LABEL   = "DSH \u4F59\u989D";                                    // DSH balance
    const string S_HELP    = "\u6F14\u793A\u8FDE\u7EED\u6263\u8D39";                // demo consecutive charges
    const string S_SIZE    = "\u5C3A\u5BF8";                                        // size
    const string S_POLL    = "\u5237\u65B0\u9891\u7387";   // refresh rate
    const string S_POS     = "\u4F4D\u7F6E";                    // position
    const string S_LEFT    = "\u5DE6\u4E0B";                    // bottom-left
    const string S_RIGHT   = "\u53F3\u4E0B";                    // bottom-right
    const string S_REFRESH = "\u7ACB\u5373\u5237\u65B0\u4F59\u989D";                // refresh now
    const string S_TEST    = "\u6D4B\u8BD5\u4E00\u6B21\u6263\u8D39\u6548\u679C";    // test one charge
    const string S_QUIT    = "\u9000\u51FA";                                        // quit
    const string S_CUSTOM  = "\u81EA\u5B9A\u4E49...";                               // custom...
    const string S_HELPT   = "\u6F14\u793A\u6263\u8D39\u91D1\u989D";
    const string S_HELPP   = "\u8981\u6F14\u793A\u6263\u591A\u5C11\u94B1\uFF08\u5143\uFF09";
    const string S_SIZET   = "\u5C3A\u5BF8";
    const string S_SIZEP   = "\u5BBD\u9AD8\uFF08\u5398\u7C73\uFF09";
    const string S_NOKEY   = "\u7F3A\u5C11 API Key";
    const string S_LOADING = "\u8FDE\u63A5\u4E2D...";
    const string S_KEYT    = "API Key";
    const string S_KEYP    = "\u7C98\u8D34 DeepSeek API Key\uFF08\u7559\u7A7A\u5219\u4E0D\u4FEE\u6539\uFF09";
    const string S_SETKEY  = "\u8BBE\u7F6E API Key";

    // OpenCode GO source: quota meters come from the console API, and every
    // model call is itemised in the request log.
    const int SrcDsh = 0, SrcGo = 1;
    const int Win5h = 0, WinWeek = 1, WinMonth = 2;
    const string GoStatusUrl = "https://opencode.ai/console/api/go/status";
    const string GoLogsBase  = "https://opencode.ai/console/api/request-logs?category=inference&limit=100&since=";
    // 100,000,000 micro-cents per US dollar (documented by the budgets API)
    const double MicroPerUsd = 100000000.0;
    const double GoLogOverlapMs = 2000;      // re-read a little, de-duped by id
    const double GoFirstLookbackMs = 600000; // first log fetch: last 10 minutes
    const string S_SRC     = "\u6570\u636E\u6E90";                                  // data source
    const string S_SRCDSH  = "DeepSeek \u4F59\u989D";                               // DeepSeek balance
    const string S_SRCGO   = "OpenCode GO";
    const string S_GOWIN   = "GO \u989D\u5EA6\u7A97\u53E3";                         // GO quota window
    const string S_W5H     = "5 \u5C0F\u65F6";                                      // 5 hours
    const string S_WWEEK   = "\u672C\u5468";                                        // this week
    const string S_WMONTH  = "\u672C\u6708";                                        // this month
    const string S_GOCRED  = "\u8BBE\u7F6E GO \u51ED\u636E";                        // set GO credentials
    const string S_GOCREDT = "OpenCode GO \u51ED\u636E";                            // OpenCode GO credentials
    const string S_GOCREDP = "\u7C98\u8D34\u6293\u5305\u5185\u5BB9\uFF08\u9700\u542B auth\u3001__Host-console_session \u4E0E wrk_ \u5DE5\u4F5C\u533A\uFF09"; // paste: needs auth, session, wrk_
    const string S_GOPASTE = "\u4ECE\u526A\u8D34\u677F\u8BFB\u53D6\u51ED\u636E";   // read credentials from clipboard
    const string S_GOBAD   = "\u8BA4\u4E0D\u51FA\u51ED\u636E";                     // cannot parse credentials
    const string S_GONOCRED= "\u7F3A\u5C11 GO \u51ED\u636E";                       // no GO credentials
    const string S_GOLABEL = "OPENCODE GO";
    const string S_GONOLOG = "GO \u65E5\u5FD7\u4E0D\u53EF\u7528";                  // GO logs unavailable

    readonly string _baseDir;
    readonly string _apiUrl;
    int _pollMs;                       // refresh interval, adjustable from the menu
    string _apiKey;

    // ------------------------------------------------------- opencode go ---
    //
    // Two ways in, both optional and independent:
    //   _goKey   service-account key (oc_sk_...) - Authorization: Bearer.
    //            Enough for the quota meters, and auto-picked from opencode's
    //            own auth.json, so the quota view keeps working with no setup.
    //   cookies  auth + __Host-console_session + x-org-id. Required for
    //            request-logs, i.e. for itemising each model call.
    // With cookies present but expired the meters keep working and the cue
    // slicing silently falls back to whole meter deltas.
    int _src = SrcDsh;                 // SrcDsh | SrcGo
    int _win = Win5h;                  // which GO meter is the big number
    string _goKey = "", _goAuth = "", _goSess = "", _goOrg = "";
    readonly double[] _goLim = new double[3];
    readonly double[] _goUsed = new double[3];
    readonly double[] _goPrev = new double[3];   // used at the previous poll
    readonly List<double> _goCues = new List<double>();   // exact per-call slices
    double _goQueuedSum;               // total of the slices above, never > _pending
    readonly HashSet<string> _goSeen = new HashSet<string>();
    long _goSince;                     // request-logs cursor: newest startedAt seen
    bool _goPrimed;                    // first log fetch only seeds the cursor
    bool _goLogsBlocked;               // cookie rejected (401/403): stop asking
    bool _goLogsWarned;

    Bitmap _flat, _sprNormal, _sprRed, _canvas;
    double _scale = 1.0;
    int _w, _h;
    double[] _fx, _fy;
    double _cm = 8.0;            // widget edge length in centimetres
    int _headX, _headY;

    readonly List<Hit> _hits = new List<Hit>();
    readonly List<Floater> _floaters = new List<Floater>();
    readonly System.Windows.Forms.Timer _timer, _poll;
    volatile bool _dirty = true;
    volatile int _pollWant = 1;      // 0 = idle, 1 = auto (stepwise), 2 = snap to latest

    // Every deduction is exactly one cent, spaced 0.5s apart.
    //
    // The printed number is NOT tracked on its own - it is defined as
    // (_bookedBal - _testOffset), and _bookedBal is only ever moved inside the
    // cue firing code. Everything else works on the pair below, so the number
    // simply cannot drift away from the animation:
    //
    //   _bookedAt  balance the booking corresponds to
    //   _bookedBal number printed when the balance was _bookedAt
    //
    // When the server reports a new balance the whole difference
    // (_bookedAt - bal) is queued; it is then paid off one cent per cue, so a
    // 0.05 jump plays five complete animations. The step is deliberately fixed
    // at one cent: that is the unit the API reports in, so there is never a
    // remainder left over, which is what used to let the number move without an
    // animation.
    const double StepYuan = 0.01;
    // Number sizing: a 0.01 charge keeps the old size, 1.00 yuan (or more)
    // is capped at FloatMaxMul times that size.
    const double FloatCapYuan = 1.0;
    const double FloatMaxMul = 2.0;
    // One complete animation every 0.2s. Cues no longer wait for the previous
    // number to finish flying, so this value IS the rhythm; the numbers stack up
    // as a comet trail instead of repeating in place.
    const double CueGapSec = 0.2;
    const int MaxCuesPerPoll = 40;   // ceiling on catch-up after a big jump

    double _realBal = double.NaN;
    double _bookedAt = double.NaN;
    double _bookedBal = double.NaN;
    double _testOffset = 0;          // display-only offset from test cues
    double _pending = 0;             // booked difference not yet charged
    double _pendingStep = 0;         // amount of the step being paid off
    double _dueGap;                  // seconds until the next cue may fire
    double _lastCueAmount = 0;       // amount of the most recent cue (diagnostics)
    float _floaterStep = 20f;        // vertical spacing of the number trail, in pixels

    double DrawnBalance {
        get {
            if (double.IsNaN(_bookedBal)) return double.NaN;
            double v = _bookedBal - _testOffset;
            // GO needs the extra digits: a single model call costs a fraction of
            // a cent, and at two decimals the printed number would sit still
            // while the cue numbers flew off the head.
            return Math.Round(v < 0 ? 0 : v, _src == SrcGo ? 4 : 2);
        }
    }
    string _status = S_LOADING;
    volatile bool _connected;
    volatile string _lastPollResult = "";

    bool _drag; Point _dragStart, _winStart;
    bool _snapping; double _snapT; Point _snapFrom, _snapTo;
    byte[] _hitMap; int _hitW, _hitH;
    NotifyIcon _tray;
    ContextMenuStrip _menu;
    ToolStripMenuItem _sizeItem;
    ToolStripMenuItem _pollItem;
    ToolStripMenuItem _posItem;
    ToolStripMenuItem _srcItem;
    ToolStripMenuItem _goWinItem;
    int _demoLeft;                   // cues left in a rehearsal run
    double _demoAmount = 0.01;

    // current frame's shake offset, so the readout and the floating numbers move
    // together with the character
    double _shakeX, _shakeY;
    // hit sound: one MCI alias per concurrent cue
    SoundPool _sound;
    bool _soundEnabled = true;
    bool _soundWanted = true;
    bool _mirror;                      // bottom-right mode: flip the art and the panel
    volatile bool _noNetwork;        // set by the offline self-tests
    int _pollInFlight;               // only one balance request at a time
    double _bankedBal; bool _bankedSnap; bool _bankedValid;
    int _bankedKind;                 // 0 = a DeepSeek balance, 1 = a GO reading
    GoReading _bankedGo;

    // One GO poll: three meter windows in dollars plus the raw request log.
    sealed class GoReading {
        public readonly double[] Lim = new double[3];
        public readonly double[] Used = new double[3];
        public string Logs;
        public bool LogsFetched;
    }

    public DshPet(string baseDir, string[] args) {
        _baseDir = baseDir;
        _apiKey  = Get("DSHPET_KEY", "");
        _apiUrl  = Get("DSHPET_API", "https://api.deepseek.com/user/balance");
        _pollMs  = int.Parse(Get("DSHPET_POLL_MS", "2000"));
        _cm      = double.Parse(Get("DSHPET_CM", "8"), CultureInfo.InvariantCulture);
        _soundWanted = Get("DSHPET_SOUND", "1") != "0";
        _mirror = Get("DSHPET_POS", "left") == "right";

        string sprite = Get("DSHPET_SPRITE", Path.Combine(baseDir, "sprite.png"));
        if (!File.Exists(sprite)) throw new FileNotFoundException("sprite not found: " + sprite);
        _flat = new Bitmap(sprite);

        // hit sound (mp3) - opens a small pool of MCI aliases up front so the
        // very first cue has no lag and overlapping cues do not cut each other.
        // The path is built here from baseDir rather than handed over through
        // the environment: values crossing the PowerShell/C# boundary come back
        // ANSI-mangled when the folder name is not ASCII, while a path built in
        // this process stays correct.
        string sndPath = Path.Combine(baseDir, Get("DSHPET_SOUND_FILE", "hit.mp3"));
        if (_soundWanted && File.Exists(sndPath)) {
            int vol = int.Parse(Get("DSHPET_VOLUME", "80"));
            _sound = new SoundPool(sndPath, 4, vol, Path.Combine(baseDir, "pet.log"));
            if (_sound.Failed) Log("sound pool failed: " + _sound.Error);
            else Log("sound ready: " + sndPath + " volume=" + vol);
        } else if (_soundWanted) {
            Log("sound file missing: " + sndPath + " (hit sound disabled)");
        }

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "DSH Balance Pet";

        ReadState();
        LoadGoCreds();
        Relayout();
        SnapToCorner(true);
        BuildMenu();

        // Shared copy, first run: nothing configured a key, so ask once instead
        // of leaving the tablet stuck on "--". Cancelling keeps it offline.
        // Skipped in GO mode: the DeepSeek key is not what that source needs.
        bool quietRun = Array.IndexOf(args, "--selftest") >= 0 || Array.IndexOf(args, "--shot") >= 0 ||
                        Array.IndexOf(args, "--goprobe") >= 0 || Array.IndexOf(args, "--gosim") >= 0 ||
                        Array.IndexOf(args, "--shotgo") >= 0;
        if (string.IsNullOrEmpty(_apiKey) && _src == SrcDsh && !quietRun && !File.Exists(KeyPath)) {
            using (InputDialog d = new InputDialog(S_KEYT, S_KEYP, "", "")) {
                if (d.ShowDialog(this) == DialogResult.OK) {
                    string k = d.Value;
                    if (k.Length > 0) {
                        _apiKey = k;
                        SaveKey(k);
                        Log("api key saved on first run (length " + k.Length + ")");
                    }
                } else {
                    Log("first run: no key entered, staying offline until one is set");
                }
            }
        }

        _tray = new NotifyIcon();
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "DSH - " + (_status.Length > 40 ? _status.Substring(0, 40) : _status);
        _tray.ContextMenuStrip = _menu;
        _tray.Visible = true;
        _tray.DoubleClick += delegate { DemoCharge(0.05); };

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = int.Parse(Get("DSHPET_TICK_MS", "33"));
        _timer.Tick += delegate { OnTick(); };
        _timer.Start();

        _poll = new System.Windows.Forms.Timer();
        _poll.Interval = _pollMs < 1000 ? 1000 : _pollMs;
        _poll.Tick += delegate { if (!_noNetwork) _pollWant = 1; };
        _poll.Start();

        PushLayer();
    }

    static string Get(string n, string f) {
        string v = Environment.GetEnvironmentVariable(n);
        return string.IsNullOrEmpty(v) ? f : v;
    }

    // ----------------------------------------------------- go credentials ----
    //
    // Whatever the user has at hand (the console's network tab, a HAR entry, the
    // PowerShell snippet from the docs, or a plain Cookie: header) is scanned for
    // the three pieces the console API wants, instead of asking for them one by
    // one. Anything not found is left alone so a partial paste cannot wipe the
    // credentials that are already stored.
    static void ParseGoBlob(string text, out string auth, out string sess, out string org, out string key) {
        auth = null; sess = null; org = null; key = null;
        if (string.IsNullOrEmpty(text)) return;
        Match m = Regex.Match(text, "(Fe26\\.2\\*\\*[^\\s\"';,)]+)");
        if (!m.Success) m = Regex.Match(text, "auth\\s*=\\s*([^\\s;\"]+)");
        if (!m.Success) m = Regex.Match(text, "\"\\s*auth\\s*\"\\s*,\\s*\"([^\"]+)\"");
        if (m.Success && m.Groups[1].Success) auth = m.Groups[1].Value;
        m = Regex.Match(text, "(st_[0-9a-fA-F\\-]{16,})");
        if (m.Success) sess = m.Groups[1].Value;
        m = Regex.Match(text, "(wrk_[0-9A-Za-z]+)");
        if (m.Success) org = m.Groups[1].Value;
        m = Regex.Match(text, "(oc_sk_[0-9A-Za-z_\\-]{10,})");
        if (m.Success) key = m.Groups[1].Value;
    }

    string GoCredPath { get { return Path.Combine(_baseDir, "opencode_go.txt"); } }

    void LoadGoCreds() {
        _goKey  = Get("DSHPET_GO_KEY", "");
        _goAuth = Get("DSHPET_GO_AUTH", "");
        _goSess = Get("DSHPET_GO_SESSION", "");
        _goOrg  = Get("DSHPET_GO_ORG", "");

        try {
            if (File.Exists(GoCredPath)) {
                foreach (string raw in File.ReadAllLines(GoCredPath)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim().ToLowerInvariant();
                    string v = line.Substring(i + 1).Trim();
                    if (v.Length == 0) continue;
                    if (k == "auth") _goAuth = v;
                    else if (k == "session") _goSess = v;
                    else if (k == "org") _goOrg = v;
                    else if (k == "key") _goKey = v;
                    else if (k == "cookie") {
                        string a, s, o, kk;
                        ParseGoBlob(v, out a, out s, out o, out kk);
                        if (a != null) _goAuth = a;
                        if (s != null) _goSess = s;
                        if (o != null) _goOrg = o;
                    }
                }
            }
        } catch (Exception ex) { Log("go creds file: " + ex.Message); }

        // opencode's own login store: an oc_sk_ service key is enough for the
        // quota meters, and unlike a cookie it does not expire.
        if (_goKey.Length == 0) {
            try {
                string ap = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                         ".local", "share", "opencode", "auth.json");
                if (File.Exists(ap)) {
                    string txt = File.ReadAllText(ap);
                    int i = txt.IndexOf("\"opencode-go\"");
                    if (i >= 0) {
                        int k = txt.IndexOf("\"key\"", i);
                        int c = k < 0 ? -1 : txt.IndexOf(':', k);
                        int q1 = c < 0 ? -1 : txt.IndexOf('"', c + 1);
                        int q2 = q1 < 0 ? -1 : txt.IndexOf('"', q1 + 1);
                        if (q2 > q1) {
                            string v = txt.Substring(q1 + 1, q2 - q1 - 1);
                            if (v.StartsWith("oc_sk_")) _goKey = v;
                        }
                    }
                }
            } catch (Exception ex) { Log("go auth.json: " + ex.Message); }
        }
        Log("go credentials: key=" + (_goKey.Length > 0 ? "yes" : "no") +
            " cookie=" + (GoHasCookie ? "yes" : "no") + " org=" + (_goOrg.Length > 0 ? _goOrg : "(none)"));
    }

    void SaveGoCreds() {
        try {
            StringBuilder sb = new StringBuilder();
            if (_goKey.Length > 0)  sb.Append("key=" + _goKey + "\r\n");
            if (_goAuth.Length > 0) sb.Append("auth=" + _goAuth + "\r\n");
            if (_goSess.Length > 0) sb.Append("session=" + _goSess + "\r\n");
            if (_goOrg.Length > 0)  sb.Append("org=" + _goOrg + "\r\n");
            File.WriteAllText(GoCredPath, sb.ToString(), Encoding.ASCII);
        } catch (Exception ex) { Log("go creds save: " + ex.Message); }
    }

    bool GoHasCookie { get { return _goAuth.Length > 0 && _goSess.Length > 0 && _goOrg.Length > 0; } }
    bool GoHasKey { get { return _goKey.Length > 0; } }
    bool GoReady { get { return GoHasKey || GoHasCookie; } }

    // The bearer key is preferred for the meters: it survives the cookie
    // expiring. request-logs only answers to the console session cookie.
    string[] GoHeaders(bool cookie) {
        List<string> h = new List<string>();
        h.Add("Accept: application/json");
        h.Add("Accept-Encoding: gzip, deflate");
        if (cookie) {
            h.Add("Cookie: oc_locale=zh; auth=" + _goAuth + "; __Host-console_session=" + _goSess);
            h.Add("x-org-id: " + _goOrg);
            h.Add("Referer: https://opencode.ai/console/" + _goOrg + "/logs");
        } else {
            h.Add("Authorization: Bearer " + _goKey);
        }
        h.Add("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:157.0) Gecko/20100101 Firefox/157.0");
        return h.ToArray();
    }

    static string WinName(int w) {
        return w == WinWeek ? S_WWEEK : (w == WinMonth ? S_WMONTH : S_W5H);
    }
    static string WinShort(int w) {
        return w == WinWeek ? "\u5468" : (w == WinMonth ? "\u6708" : "5H");
    }
    static string WinJson(int w) {
        return w == WinWeek ? "week" : (w == WinMonth ? "month" : "fiveHour");
    }

    // Remaining allowance of one meter, in dollars. The API reports micro-cents,
    // and there are 100,000,000 of those per dollar.
    double GoRemain(int w) {
        if (_goLim[w] <= 0) return double.NaN;
        double v = _goLim[w] - _goUsed[w];
        return v < 0 ? 0 : v;
    }
    // Percentages are the only view that survives the per-model limits: every
    // model has its own monthly allowance, so two windows' dollars are not
    // comparable while their percentages are.
    double GoRemainPct(int w) {
        if (_goLim[w] <= 0) return double.NaN;
        double v = (_goLim[w] - _goUsed[w]) / _goLim[w] * 100.0;
        if (v < 0) v = 0;
        if (v > 100) v = 100;
        return v;
    }
    double GoRemainingLive { get { return GoRemain(_win); } }

    void Notify(string msg) {
        try {
            if (_tray != null) {
                _tray.BalloonTipTitle = "DSH";
                _tray.BalloonTipText = msg;
                _tray.ShowBalloonTip(4000);
            }
        } catch { }
        Log("notify: " + msg);
    }

    void AskGoCred() {
        using (InputDialog d = new InputDialog(S_GOCREDT, S_GOCREDP, "", _goAuth, true)) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            ApplyGoCredBlob(d.Value);
        }
    }

    void PasteGoCred() {
        string txt = "";
        try { if (Clipboard.ContainsText()) txt = Clipboard.GetText(); } catch (Exception ex) { Log("clipboard: " + ex.Message); }
        if (txt.Trim().Length == 0) { Notify(S_GOBAD); return; }
        ApplyGoCredBlob(txt);
    }

    void ApplyGoCredBlob(string blob) {
        string a, s, o, k;
        ParseGoBlob(blob, out a, out s, out o, out k);
        if (a == null && s == null && o == null && k == null) {
            Log("go creds: nothing recognised in " + blob.Length + " chars");
            Notify(S_GOBAD);
            return;
        }
        if (k != null) _goKey = k;
        if (a != null) _goAuth = a;
        if (s != null) _goSess = s;
        if (o != null) _goOrg = o;
        _goLogsBlocked = false;
        _goLogsWarned = false;
        _goPrimed = false;
        _goSince = 0;
        _goSeen.Clear();
        SaveGoCreds();
        string found = "auth=" + (a != null ? "\u2713" : "\u2717") +
                       " \u4F1A\u8BDD=" + (s != null ? "\u2713" : "\u2717") +
                       " \u5DE5\u4F5C\u533A=" + (o != null ? "\u2713" : "\u2717") +
                       " key=" + (k != null || GoHasKey ? "\u2713" : "\u2717");
        Log("go creds updated: " + found);
        Notify((GoReady ? "\u51ED\u636E\u5DF2\u4FDD\u5B58\uFF1A" : S_GOBAD + "\uFF1A") + found);
        _pollWant = 2;
    }

    void BuildMenu() {
        _menu = new ContextMenuStrip();

        // data source: the DeepSeek balance, or the OpenCode GO quota meters
        _srcItem = new ToolStripMenuItem(S_SRC);
        ToolStripMenuItem srcDsh = new ToolStripMenuItem(S_SRCDSH);
        srcDsh.Click += delegate { SetSource(SrcDsh); };
        _srcItem.DropDownItems.Add(srcDsh);
        ToolStripMenuItem srcGo = new ToolStripMenuItem(S_SRCGO);
        srcGo.Click += delegate { SetSource(SrcGo); };
        _srcItem.DropDownItems.Add(srcGo);
        _menu.Items.Add(_srcItem);

        // which GO meter is the big number (the other two stay visible as
        // percentages, so all three allowances are on screen at once)
        _goWinItem = new ToolStripMenuItem(S_GOWIN);
        int[] wins = new int[] { Win5h, WinWeek, WinMonth };
        foreach (int w in wins) {
            int v = w;
            ToolStripMenuItem it = new ToolStripMenuItem(WinName(v));
            it.Click += delegate { SetWindow(v); };
            _goWinItem.DropDownItems.Add(it);
        }
        _menu.Items.Add(_goWinItem);

        _menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem refresh = new ToolStripMenuItem(S_REFRESH);
        refresh.Click += delegate { _pollWant = 2; };
        _menu.Items.Add(refresh);

        ToolStripMenuItem test = new ToolStripMenuItem(S_TEST);
        test.Click += delegate { DemoCharge(StepYuan); };
        _menu.Items.Add(test);

        // submenu: rehearse a bigger deduction to watch a run of consecutive cues
        ToolStripMenuItem demoItem = new ToolStripMenuItem(S_HELP);
        double[] demos = new double[] { 0.05, 0.1, 0.2, 0.5, 1.0 };
        foreach (double d in demos) {
            double v = d;
            ToolStripMenuItem it = new ToolStripMenuItem("-" + v.ToString("0.##", CultureInfo.InvariantCulture) +
                                                         "  (" + CueCount(v) + " \u6B21)");
            it.Click += delegate { DemoCharge(v); };
            demoItem.DropDownItems.Add(it);
        }
        demoItem.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem demoCustom = new ToolStripMenuItem(S_CUSTOM);
        demoCustom.Click += delegate { AskDemo(); };
        demoItem.DropDownItems.Add(demoCustom);
        _menu.Items.Add(demoItem);

        _menu.Items.Add(new ToolStripSeparator());

        _sizeItem = new ToolStripMenuItem(S_SIZE);
        double[] sizes = new double[] { 1.5, 2.0, 3.0, 4.0, 6.0, 8.0 };
        foreach (double s in sizes) {
            double v = s;
            ToolStripMenuItem it = new ToolStripMenuItem(CmLabel(v));
            it.Click += delegate { SetCm(v); };
            _sizeItem.DropDownItems.Add(it);
        }
        _sizeItem.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem sizeCustom = new ToolStripMenuItem(S_CUSTOM);
        sizeCustom.Click += delegate { AskCm(); };
        _sizeItem.DropDownItems.Add(sizeCustom);
        _menu.Items.Add(_sizeItem);

        _pollItem = new ToolStripMenuItem(S_POLL);
        double[] polls = new double[] { 1.0, 2.0, 3.0, 5.0, 10.0, 30.0 };
        foreach (double s in polls) {
            double v = s;
            ToolStripMenuItem it = new ToolStripMenuItem(SecLabel(v));
            it.Click += delegate { SetPollSec(v); };
            _pollItem.DropDownItems.Add(it);
        }
        _pollItem.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem pollCustom = new ToolStripMenuItem(S_CUSTOM);
        pollCustom.Click += delegate { AskPollSec(); };
        _pollItem.DropDownItems.Add(pollCustom);
        _menu.Items.Add(_pollItem);

        _posItem = new ToolStripMenuItem(S_POS);
        ToolStripMenuItem left = new ToolStripMenuItem(S_LEFT);
        left.Click += delegate { SetMirror(false); };
        _posItem.DropDownItems.Add(left);
        ToolStripMenuItem right = new ToolStripMenuItem(S_RIGHT);
        right.Click += delegate { SetMirror(true); };
        _posItem.DropDownItems.Add(right);
        _menu.Items.Add(_posItem);

        _menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem key = new ToolStripMenuItem(S_SETKEY);
        key.Click += delegate { AskKey(); };
        _menu.Items.Add(key);

        ToolStripMenuItem gocred = new ToolStripMenuItem(S_GOCRED);
        gocred.Click += delegate { AskGoCred(); };
        _menu.Items.Add(gocred);

        ToolStripMenuItem gopaste = new ToolStripMenuItem(S_GOPASTE);
        gopaste.Click += delegate { PasteGoCred(); };
        _menu.Items.Add(gopaste);

        _menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem quit = new ToolStripMenuItem(S_QUIT);
        quit.Click += delegate { Quit(); };
        _menu.Items.Add(quit);

        _menu.Opening += delegate { RefreshMenuChecks(); };
    }

    static string Yuan(double v) {
        return "-" + v.ToString("0.##", CultureInfo.InvariantCulture) + " \u00A5";
    }
    static string CueCount(double amount) {
        int n = (int)Math.Round(amount / StepYuan);
        return n < 1 ? "1" : n.ToString(CultureInfo.InvariantCulture);
    }
    static string CmLabel(double v) {
        return v.ToString("0.#", CultureInfo.InvariantCulture) + " cm";
    }
    static string SecLabel(double v) {
        return v.ToString("0.#", CultureInfo.InvariantCulture) + " \u79D2";
    }

    // ToolStrip check marks are reset on every open, so push them here.
    void RefreshMenuChecks() {
        string curSize = CmLabel(_cm);
        string curPoll = SecLabel(_pollMs / 1000.0);
        foreach (ToolStripItem it in _srcItem.DropDownItems) {
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null) mi.Checked = (mi.Text == (_src == SrcGo ? S_SRCGO : S_SRCDSH));
        }
        foreach (ToolStripItem it in _goWinItem.DropDownItems) {
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null) mi.Checked = (mi.Text == WinName(_win));
        }
        // the window choice only means something for the GO source
        _goWinItem.Enabled = (_src == SrcGo);
        foreach (ToolStripItem it in _posItem.DropDownItems) {
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null) mi.Checked = (mi.Text == (_mirror ? S_RIGHT : S_LEFT));
        }
        foreach (ToolStripItem it in _pollItem.DropDownItems) {
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null && mi.Text != S_CUSTOM) mi.Checked = (mi.Text == curPoll);
        }
        foreach (ToolStripItem it in _sizeItem.DropDownItems) {
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null && mi.Text != S_CUSTOM) mi.Checked = (mi.Text == curSize);
        }
    }

    // ------------------------------------------------------------- settings ---

    // Rehearsal: queue ceil(amount / 0.01) cues. They walk the printed number
    // down through _testOffset, leaving the real balance and the booking alone,
    // so a refresh afterwards simply restores the true value.
    void DemoCharge(double amount) {
        if (amount < StepYuan) amount = StepYuan;
        int cues = (int)Math.Round(amount / StepYuan);
        if (cues < 1) cues = 1;
        if (cues > 500) cues = 500;
        _demoLeft = cues;
        _demoAmount = StepYuan;
        Log("demo charge: " + Yuan(amount) + " -> " + cues + " cue(s), " +
            CueGapSec.ToString("0.#") + "s apart");
        _dirty = true;
    }

    void AskDemo() {
        using (InputDialog d = new InputDialog(S_HELPT, S_HELPP, "\u00A5", "0.1")) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            double v;
            if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                DemoCharge(v);
            else
                Log("demo amount rejected: '" + d.Value + "'");
        }
    }

    // Switching source throws away the accounting on purpose: the two sources
    // measure unrelated quantities (yuan of prepaid credit vs dollars of
    // subscription quota), so carrying a booking across would print a number
    // that never existed. The first reading of the new source re-anchors.
    void SetSource(int s) {
        if (_src == s) return;
        _src = s;
        ResetAccounting();
        SaveState();
        _pollWant = 2;
        Log("data source -> " + (_src == SrcGo ? "OpenCode GO" : "DeepSeek balance") +
            (_src == SrcGo && !GoReady ? " (no GO credentials yet)" : ""));
        RenderToCanvas();
        PushLayer();
    }

    void SetWindow(int w) {
        if (_win == w) return;
        _win = w;
        ResetAccounting();
        SaveState();
        _pollWant = 2;
        Log("go window -> " + WinName(w));
        RenderToCanvas();
        PushLayer();
    }

    void ResetAccounting() {
        _realBal = double.NaN;
        _bookedAt = double.NaN;
        _bookedBal = double.NaN;
        _pending = 0;
        _pendingStep = 0;
        _testOffset = 0;
        _dueGap = 0;
        _demoLeft = 0;
        _goCues.Clear();
        _goQueuedSum = 0;
        for (int i = 0; i < 3; i++) _goPrev[i] = double.NaN;
        _connected = false;
        _status = S_LOADING;
        _lastPollResult = "";
        _dirty = true;
    }

    // Bottom-right mode mirrors the artwork. The tablet quad is mirrored and its
    // left/right corners are swapped, so the glyphs stay readable while the tilt
    // flips with the art.
    void SetMirror(bool on) {
        if (_mirror == on) { SnapToCorner(true); return; }
        _mirror = on;
        Relayout();
        SnapToCorner(true);
        SaveState();
        RenderToCanvas();
        PushLayer();
        Log("position set to " + (on ? "bottom-right (mirrored)" : "bottom-left"));
    }

    // Refresh rate. The floor is one second: the balance API is a network
    // round trip, and polling faster than that only burns quota.
    void SetPollSec(double sec) {
        if (sec < 1.0) sec = 1.0;
        if (sec > 600.0) sec = 600.0;
        _pollMs = (int)Math.Round(sec * 1000.0);
        try { _poll.Stop(); _poll.Interval = _pollMs < 1000 ? 1000 : _pollMs; _poll.Start(); } catch { }
        _pollWant = 2;                    // refresh right away, so the change is visible
        SaveState();
        Log("poll interval set to " + SecLabel(sec));
    }

    void AskPollSec() {
        using (InputDialog d = new InputDialog(S_POLL, "\u95F4\u9694\uFF08\u79D2\uFF0C\u6700\u5C0F 1\uFF09", "\u79D2",
                                               (_pollMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture))) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            double v;
            if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 1.0)
                SetPollSec(v);
            else
                Log("custom poll interval rejected: '" + d.Value + "'");
        }
    }

    void SetCm(double v) {
        if (v < 0.8) v = 0.8;
        if (v > 40) v = 40;
        _cm = v;
        Relayout();
        SnapToCorner(true);               // keep it pinned to the corner
        SaveState();
        RenderToCanvas();
        PushLayer();
        Log("size set to " + CmLabel(v) + " -> " + _w + "x" + _h + " px");
    }

    void AskCm() {
        using (InputDialog d = new InputDialog(S_SIZET, S_SIZEP, "cm",
                                               _cm.ToString("0.##", CultureInfo.InvariantCulture))) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            double v;
            if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                SetCm(v);
            else
                Log("custom size rejected: '" + d.Value + "'");
        }
    }

    void AskKey() {
        using (InputDialog d = new InputDialog(S_KEYT, S_KEYP, "",
                                               _apiKey)) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string k = d.Value;
            if (k.Length > 0 && k != _apiKey) {
                _apiKey = k;
                SaveKey(k);
                _pollWant = 2;
                Log("api key set manually (length " + k.Length + ")");
            }
        }
    }

    // ------------------------------------------------------------ geometry ---

    void Relayout() {
        // Physical size has to come from the real monitor DPI. If the process
        // could not be made DPI aware, Windows lies and reports 96, which would
        // shrink the widget on a scaled display - so fall back to the monitor's
        // actual DPI instead of trusting a 96 that was never real.
        double dpiY = 96;
        try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiY = g.DpiY; } catch { }
        if (dpiY <= 96.5) {
            try {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) {
                    IntPtr hdc = g.GetHdc();
                    int raw = Native.GetDeviceCaps(hdc, 90);          // LOGPIXELSY
                    g.ReleaseHdc(hdc);
                    if (raw > 0) dpiY = raw;
                }
            } catch { }
        }
        if (dpiY < 72) dpiY = 96;
        int px = (int)Math.Round(_cm / 2.54 * dpiY);
        if (px < 40) px = 40;
        _w = px; _h = px;
        ClientSize = new Size(_w, _h);
        _scale = (double)_h / _flat.Height;

        int sw = Math.Max(2, (int)Math.Round(_flat.Width * _scale));
        int sh = Math.Max(2, (int)Math.Round(_flat.Height * _scale));
        Bitmap scaled = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(scaled)) {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_flat, new Rectangle(0, 0, sw, sh));
            // the flip has to happen on the scaled copy, so it is applied below
        }
        if (_sprNormal != null) _sprNormal.Dispose();
        if (_sprRed != null) _sprRed.Dispose();
        if (_canvas != null) _canvas.Dispose();

        if (_mirror) scaled.RotateFlip(RotateFlipType.RotateNoneFlipX);
        _sprNormal = scaled;
        _sprRed = BuildRedLayer(scaled);
        _canvas = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);

        // tablet screen quad in sprite pixels, from make_sprite.ps1 (min-area
        // rotated rectangle around the opaque black screen)
        double[] qx = new double[] { 550.3, 946.6, 980.9, 584.6 };
        double[] qy = new double[] { 706.3, 643.8, 861.4, 924.0 };
        _fx = new double[4]; _fy = new double[4];
        for (int i = 0; i < 4; i++) { _fx[i] = qx[i] * _scale; _fy[i] = qy[i] * _scale; }
        if (_mirror) {
            for (int i = 0; i < 4; i++) _fx[i] = sw - _fx[i];   // mirror the tablet quad
            // Swap whole corners (x AND y), not just the x values: swapping only x
            // pairs the x of one corner with the y of another and the text tilts wrong.
            for (int i = 0; i < 4; i += 2) {
                double tx = _fx[i], ty = _fy[i];
                _fx[i] = _fx[i + 1]; _fy[i] = _fy[i + 1];
                _fx[i + 1] = tx; _fy[i + 1] = ty;
            }
        }

        _headX = (int)(sw * 0.50 + sh * 0.17);
        if (_mirror) _headX = sw - _headX;
        _headY = (int)(sh * 0.36);
        // one text line high, so a fast run of numbers cascades without overlapping
        _floaterStep = (float)Math.Max(18.0, 34.0 * _scale * 1.7);
        _hitMap = null;
        _dirty = true;
    }

    // Flat red copy of the art for the hurt flash (alpha preserved).
    static Bitmap BuildRedLayer(Bitmap src) {
        Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        Buf s = new Buf(src);
        try {
            Buf d = new Buf(dst);
            try {
                for (int y = 0; y < s.H; y++) {
                    for (int x = 0; x < s.W; x++) {
                        int si = y * s.Stride + x * 4, di = y * d.Stride + x * 4;
                        d.P[di]     = 34;
                        d.P[di + 1] = 48;
                        d.P[di + 2] = 255;
                        d.P[di + 3] = s.P[si + 3];
                    }
                }
                d.Flush();
            } finally { d.Dispose(); }
        } finally { s.Dispose(); }
        return dst;
    }

    void SnapToCorner(bool immediate) {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Point target = _mirror ? new Point(wa.Right - _w, wa.Bottom - _h)
                                : new Point(wa.Left, wa.Bottom - _h);
        if (immediate) { Location = target; _snapping = false; return; }
        _snapFrom = Location;
        _snapTo = target;
        _snapT = 0;
        _snapping = true;
    }

    // --------------------------------------------------------------- state ---

    string StatePath { get { return Path.Combine(_baseDir, "state.ini"); } }
    string KeyPath { get { return Path.Combine(_baseDir, "apikey.txt"); } }

    void ReadState() {
        try {
            if (!File.Exists(StatePath)) return;
            foreach (string line in File.ReadAllLines(StatePath)) {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                double d;
                int n;
                if (k == "pos") { _mirror = (v == "right"); continue; }
                if (k == "src") { _src = (v == "go") ? SrcGo : SrcDsh; continue; }
                if (k == "win") {
                    _win = (v == "week") ? WinWeek : (v == "month" ? WinMonth : Win5h);
                    continue;
                }
                if (k == "poll_ms" && int.TryParse(v, out n) && n >= 1000 && n <= 600000) {
                    _pollMs = n;
                    continue;
                }
                if (k == "cm" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.8)
                    _cm = d;
            }
        } catch { }
    }

    void SaveState() {
        try {
            File.WriteAllText(StatePath,
                "cm=" + _cm.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n" +
                "poll_ms=" + _pollMs.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "pos=" + (_mirror ? "right" : "left") + "\r\n" +
                "src=" + (_src == SrcGo ? "go" : "dsh") + "\r\n" +
                "win=" + WinJson(_win) + "\r\n",
                Encoding.ASCII);
        } catch { }
    }

    void SaveKey(string k) {
        try { File.WriteAllText(KeyPath, k, Encoding.ASCII); } catch { }
    }

    // -------------------------------------------------------------- window ---

    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        _dirty = true;
        RenderToCanvas();
        PushLayer();
    }

    void PushLayer() {
        if (_canvas == null || Handle == IntPtr.Zero) return;
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try {
            hBmp = _canvas.GetHbitmap(Color.FromArgb(0));
            old = Native.SelectObject(memDc, hBmp);
            Native.SIZE size = new Native.SIZE(); size.cx = _canvas.Width; size.cy = _canvas.Height;
            Native.POINT src = new Native.POINT(); src.X = 0; src.Y = 0;
            Native.POINT dst = new Native.POINT(); dst.X = Left; dst.Y = Top;
            Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
            bf.BlendOp = Native.AC_SRC_OVER; bf.BlendFlags = 0;
            bf.SourceConstantAlpha = 255; bf.AlphaFormat = Native.AC_SRC_ALPHA;
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, Native.ULW_ALPHA);
        } finally {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == Native.WM_NCHITTEST) {
            int lp = (int)m.LParam;
            int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
            Point cp = PointToClient(new Point(x, y));
            bool solid = false;
            byte[] map = _hitMap;
            if (map != null && cp.X >= 0 && cp.Y >= 0 && cp.X < _hitW && cp.Y < _hitH)
                solid = map[cp.Y * _hitW + cp.X] > 8;
            m.Result = (IntPtr)(solid ? Native.HTCLIENT : Native.HTTRANSPARENT);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            _drag = true;
            _snapping = false;
            _dragStart = Cursor.Position;
            _winStart = Location;
        } else if (e.Button == MouseButtons.Right) {
            RefreshMenuChecks();
            _menu.Show(Cursor.Position);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (_drag) {
            Point p = Cursor.Position;
            Location = new Point(_winStart.X + (p.X - _dragStart.X), _winStart.Y + (p.Y - _dragStart.Y));
            PushLayer();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        if (_drag) {
            _drag = false;
            SnapToCorner(false);        // release -> fly back to the bottom-left
        }
        base.OnMouseUp(e);
    }

    void Quit() {
        try { _timer.Stop(); _poll.Stop(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_sound != null) { try { _sound.Dispose(); } catch { } }
        SaveState();
        Application.Exit();
    }

    // -------------------------------------------------------------- render ---

    public void RenderToCanvas() {
        if (_canvas == null) return;

        Buf buf = new Buf(_canvas);
        try {
            byte[] p = buf.P;
            Array.Clear(p, 0, p.Length);

            double sx = 0, sy = 0;
            for (int i = 0; i < _hits.Count; i++) {
                Hit h = _hits[i];
                double e = Math.Max(0, 1 - h.T / Hit.Dur);
                // Fixed pixel amplitude instead of one scaled by the sprite: at
                // the old 2cm default the widget was ~113px, so a scale
                // proportional shake rounded away to zero and nothing moved.
                // ~24 rad/s keeps several samples per cycle at 30fps; the old
                // 44 rad/s was undersampled and looked like random jumping.
                sx += Math.Sin(h.T * 24) * 3.2 * e;
                sy += Math.Cos(h.T * 19) * 2.8 * e;
            }
            // hard ceiling: overlapping cues must not fling the sprite off-screen
            double shakeMax = Math.Min(12.0, _w * 0.06);
            if (sx > shakeMax) sx = shakeMax; else if (sx < -shakeMax) sx = -shakeMax;
            if (sy > shakeMax) sy = shakeMax; else if (sy < -shakeMax) sy = -shakeMax;
            int ox = (int)Math.Round(sx), oy = (int)Math.Round(sy);
            _shakeX = ox; _shakeY = oy;      // screen text + floating numbers follow this

            BlitLayer(_sprNormal, p, buf.Stride, ox, oy, 1.0);
            for (int i = 0; i < _hits.Count; i++) {
                double pulse = _hits[i].Pulse;
                if (pulse > 0.01) {
                    // The hurt overlay is toned down as the widget grows: 60% of a
                    // 113px sprite is a readable flash, 60% of a 454px one would
                    // just be a red silhouette.
                    double maxTint = Math.Max(0.30, 0.64 - _scale * 0.75);
                    BlitLayer(_sprRed, p, buf.Stride, ox, oy, Math.Min(maxTint, maxTint * pulse));
                }
            }
            buf.Flush();
        } finally { buf.Dispose(); }

        DrawScreen();                                  // transparent screen + text only
        if (!_connected && _lastPollResult.Length > 0) DrawAlert();

        if (_floaters.Count > 0) {
            Buf b2 = new Buf(_canvas);
            try {
                for (int i = 0; i < _floaters.Count; i++) DrawFloater(_floaters[i], b2);
                b2.Flush();
            } finally { b2.Dispose(); }
        }

        Buf b3 = new Buf(_canvas);
        try { BuildHitMap(b3); } finally { b3.Dispose(); }
        _dirty = false;
    }

    void BlitLayer(Bitmap layer, byte[] dst, int dstStride, int ox, int oy, double alpha) {
        if (layer == null) return;
        Buf src = new Buf(layer);
        try {
            byte[] sp = src.P; int ss = src.Stride;
            for (int y = 0; y < src.H; y++) {
                int ty = y + oy;
                if (ty < 0 || ty >= _h) continue;
                int srow = y * ss, drow = ty * dstStride;
                for (int x = 0; x < src.W; x++) {
                    int tx = x + ox;
                    if (tx < 0 || tx >= _w) continue;
                    int si = srow + x * 4;
                    int sa = sp[si + 3];
                    if (sa == 0) continue;
                    Cs.Blend(dst, drow + tx * 4, sp[si + 2], sp[si + 1], sp[si], sa / 255.0 * alpha);
                }
            }
        } finally { src.Dispose(); }
    }

    void BuildHitMap(Buf buf) {
        if (_hitMap == null || _hitW != _w || _hitH != _h) {
            _hitMap = new byte[_w * _h]; _hitW = _w; _hitH = _h;
        }
        byte[] p = buf.P;
        for (int y = 0; y < _h; y++) {
            int row = y * _w, srow = y * buf.Stride;
            for (int x = 0; x < _w; x++) _hitMap[row + x] = p[srow + x * 4 + 3];
        }
    }

    // The panel bitmap carries ONLY the label and the number. The tablet screen
    // is an opaque black surface in the artwork, so the text is drawn light.
    Bitmap BuildPanel(int pw, int ph) {
        if (_src == SrcGo) return BuildPanelGo(pw, ph);
        Bitmap panel = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(panel)) {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            float W = pw, H = ph;
            using (StringFormat sf = new StringFormat()) {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                float fLabel = H * 0.21f;
                using (Font f = new Font("Microsoft YaHei UI", fLabel, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (Brush b = new SolidBrush(Color.FromArgb(235, 158, 182, 224))) {
                    RectangleF lr = new RectangleF(0, H * 0.04f, W, fLabel * 1.5f);
                    g.DrawString(S_LABEL, f, shadow, new RectangleF(lr.X + 1.5f, lr.Y + 1.5f, lr.Width, lr.Height), sf);
                    g.DrawString(S_LABEL, f, b, lr, sf);
                }

                string txt = double.IsNaN(DrawnBalance)
                    ? "--"
                    : (DrawnBalance).ToString("0.00", CultureInfo.InvariantCulture);
                float fBal = H * 0.48f, fCur = H * 0.27f;
                using (Font fb = new Font("Arial", fBal, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font fc = new Font("Microsoft YaHei UI", fCur, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (Brush bb = new SolidBrush(Color.FromArgb(255, 240, 246, 255)))
                using (Brush bc = new SolidBrush(Color.FromArgb(235, 158, 184, 230))) {
                    SizeF sb = g.MeasureString(txt, fb);
                    SizeF sc = g.MeasureString("\u00A5", fc);
                    float total = sb.Width + sc.Width;
                    float left = (W - total) / 2f;
                    float top = H * 0.44f;
                    g.DrawString("\u00A5", fc, sh, left + 1.5f, top + sb.Height * 0.24f + 1.5f);
                    g.DrawString(txt, fb, sh, left + sc.Width + 1.5f, top + 1.5f);
                    g.DrawString("\u00A5", fc, bc, left, top + sb.Height * 0.24f);
                    g.DrawString(txt, fb, bb, left + sc.Width, top);
                }
            }
        }
        return panel;
    }

    // GO panel: the product name, the active meter's remaining dollars, and all
    // three allowances as percentages underneath. The percentages earn their row
    // because the per-model limits differ: two windows' dollars are not
    // comparable across models, while their percentages are. The active meter is
    // bracketed and drawn brighter, so "which one is the big number" is obvious.
    Bitmap BuildPanelGo(int pw, int ph) {
        Bitmap panel = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(panel)) {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            float W = pw, H = ph;

            using (StringFormat sf = new StringFormat()) {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                float fLabel = H * 0.17f;
                using (Font f = new Font("Microsoft YaHei UI", fLabel, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (Brush b = new SolidBrush(Color.FromArgb(235, 158, 182, 224))) {
                    RectangleF lr = new RectangleF(0, H * 0.02f, W, H * 0.24f);
                    g.DrawString(S_GOLABEL, f, shadow, new RectangleF(lr.X + 1.5f, lr.Y + 1.5f, lr.Width, lr.Height), sf);
                    g.DrawString(S_GOLABEL, f, b, lr, sf);
                }

                string txt = double.IsNaN(DrawnBalance)
                    ? "--"
                    : DrawnBalance.ToString("0.0000", CultureInfo.InvariantCulture);
                float fBal = H * 0.40f, fCur = H * 0.24f;
                using (Font fb = new Font("Arial", fBal, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font fc = new Font("Arial", fCur, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (Brush bb = new SolidBrush(Color.FromArgb(255, 240, 246, 255)))
                using (Brush bc = new SolidBrush(Color.FromArgb(235, 158, 184, 230))) {
                    SizeF sb = g.MeasureString(txt, fb);
                    SizeF sc = g.MeasureString("$", fc);
                    float total = sb.Width + sc.Width;
                    float left = (W - total) / 2f;
                    float top = H * 0.30f;
                    g.DrawString("$", fc, sh, left + 1.5f, top + sb.Height * 0.22f + 1.5f);
                    g.DrawString(txt, fb, sh, left + sc.Width + 1.5f, top + 1.5f);
                    g.DrawString("$", fc, bc, left, top + sb.Height * 0.22f);
                    g.DrawString(txt, fb, bb, left + sc.Width, top);
                }

                float fSeg = H * 0.15f;
                string[] seg = new string[3];
                for (int w = 0; w < 3; w++) {
                    double pct = GoRemainPct(w);
                    string s = WinShort(w) + " " + (double.IsNaN(pct)
                        ? "--"
                        : Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) + "%");
                    if (w == _win) s = "[" + s + "]";
                    seg[w] = s;
                }
                using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0))) {
                    SizeF[] sz = new SizeF[3];
                    float gap = W * 0.03f;
                    float avail = W * 0.96f - gap * 2;
                    Font fs = new Font("Microsoft YaHei UI", fSeg, FontStyle.Bold, GraphicsUnit.Pixel);
                    try {
                        float textW = 0;
                        for (int w = 0; w < 3; w++) { sz[w] = g.MeasureString(seg[w], fs); textW += sz[w].Width; }
                        // Shrink to fit instead of clipping: the active meter is
                        // bracketed, and a wide row would otherwise push the
                        // opening bracket off the edge of the screen.
                        if (textW > avail && textW > 0) {
                            fSeg = Math.Max(H * 0.09f, fSeg * avail / textW);
                            fs.Dispose();
                            fs = new Font("Microsoft YaHei UI", fSeg, FontStyle.Bold, GraphicsUnit.Pixel);
                            for (int w = 0; w < 3; w++) sz[w] = g.MeasureString(seg[w], fs);
                        }
                        float tot = 0;
                        for (int w = 0; w < 3; w++) tot += sz[w].Width;
                        float x = (W - (tot + gap * 2)) / 2f;
                        float y = H * 0.78f + (H * 0.15f - fSeg) * 0.5f;
                        for (int w = 0; w < 3; w++) {
                            using (Brush br = new SolidBrush(GoSegColor(w))) {
                                g.DrawString(seg[w], fs, sh, x + 1.2f, y + 1.2f);
                                g.DrawString(seg[w], fs, br, x, y);
                            }
                            x += sz[w].Width + gap;
                        }
                    } finally { fs.Dispose(); }
                }
            }
        }
        return panel;
    }

    // Green-ish while there is room, amber under a third, red under a tenth.
    Color GoSegColor(int w) {
        double pct = GoRemainPct(w);
        bool active = (w == _win);
        if (double.IsNaN(pct)) return Color.FromArgb(200, 130, 140, 160);
        if (pct <= 10) return Color.FromArgb(active ? 255 : 225, 244, 110, 100);
        if (pct <= 30) return Color.FromArgb(active ? 255 : 225, 245, 200, 120);
        if (active)    return Color.FromArgb(255, 235, 245, 255);
        return Color.FromArgb(225, 150, 195, 235);
    }

    void DrawScreen() {
        float lw = (float)Math.Sqrt(Math.Pow(_fx[1] - _fx[0], 2) + Math.Pow(_fy[1] - _fy[0], 2));
        float lh = (float)Math.Sqrt(Math.Pow(_fx[3] - _fx[0], 2) + Math.Pow(_fy[3] - _fy[0], 2));
        if (lw < 8 || lh < 8) return;

        int pw = 520;
        int ph = (int)Math.Round(pw * (lh / lw));
        if (ph < 24) { ph = 24; pw = (int)Math.Round(ph * (lw / lh)); }

        Bitmap panel = BuildPanel(pw, ph);
        try {
            using (Graphics g = Graphics.FromImage(_canvas)) {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                PointF[] dest = new PointF[3];
                dest[0] = new PointF((float)(_fx[0] + _shakeX), (float)(_fy[0] + _shakeY));
                dest[1] = new PointF((float)(_fx[1] + _shakeX), (float)(_fy[1] + _shakeY));
                dest[2] = new PointF((float)(_fx[3] + _shakeX), (float)(_fy[3] + _shakeY));
                g.DrawImage(panel, dest);
            }
        } finally { panel.Dispose(); }
    }

    // offline indicator: a small dot, so the screen keeps showing only the
    // label and the number
    void DrawAlert() {
        using (Graphics g = Graphics.FromImage(_canvas)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = Math.Max(2.5f, (float)(_h * 0.032));
            float cx = (float)(_fx[1] + _fx[2]) / 2f + (float)_shakeX;
            float cy = (float)(_fy[1] + _fy[2]) / 2f + (float)(_h * 0.05) + (float)_shakeY;
            using (Brush b = new SolidBrush(Color.FromArgb(235, 228, 62, 52)))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (Pen p = new Pen(Color.FromArgb(210, 255, 255, 255), Math.Max(1f, r * 0.3f)))
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
        }
    }

    void DrawFloater(Floater fl, Buf dst) {
        double t = fl.T / fl.Dur;
        float em = (float)Math.Max(13.0, 34.0 * _scale) * fl.Mul;
        em = Math.Min(em, _h * 0.38f);      // keep a big number inside the window
        float pop = t < 0.18 ? (float)(0.62 + 0.38 * (t / 0.18)) : 1f;
        double alpha = t < 0.55 ? 1.0 : (1 - (t - 0.55) / 0.45);
        if (alpha <= 0) return;

        Bitmap bmp = MakeFloaterBitmap(fl.Text, em);
        try {
            Buf src = new Buf(bmp);
            try {
                byte[] p = dst.P, sp = src.P;
                int ds = dst.Stride, ss = src.Stride;
                int w = (int)(src.W * pop), h = (int)(src.H * pop);
                // the shake offset is applied here so the number shakes together
                // with the character instead of hanging in place
                int x0 = fl.X + (int)Math.Round(fl.Jitter * Math.Sin(t * 9)) + (int)Math.Round(_shakeX);
                int y0 = fl.Y - (int)Math.Round(t * em * 2.7) + (int)Math.Round(_shakeY);
                for (int yy = 0; yy < h; yy++) {
                    int syy = (int)(yy / pop); if (syy >= src.H) break;
                    int ty = y0 + yy; if (ty < 0 || ty >= _h) continue;
                    for (int xx = 0; xx < w; xx++) {
                        int sxx = (int)(xx / pop); if (sxx >= src.W) break;
                        int tx = x0 + xx; if (tx < 0 || tx >= _w) continue;
                        int si = syy * ss + sxx * 4;
                        int sa = sp[si + 3]; if (sa == 0) continue;
                        Cs.Blend(p, ty * ds + tx * 4, sp[si + 2], sp[si + 1], sp[si], sa / 255.0 * alpha);
                    }
                }
            } finally { src.Dispose(); }
        } finally { bmp.Dispose(); }
    }

    Bitmap MakeFloaterBitmap(string text, float em) {
        using (Font f = new Font("Arial", em, FontStyle.Bold, GraphicsUnit.Pixel)) {
            Size sz = TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int pad = (int)(em * 0.45f);
            Bitmap bmp = new Bitmap(Math.Max(4, sz.Width + pad * 2), Math.Max(4, sz.Height + pad * 2),
                                    PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (GraphicsPath path = new GraphicsPath()) {
                    path.AddString(text, f.FontFamily, (int)FontStyle.Bold, em, new PointF(pad, pad),
                                   StringFormat.GenericTypographic);
                    using (Pen outline = new Pen(Color.FromArgb(255, 92, 8, 8), em * 0.16f)) {
                        outline.LineJoin = LineJoin.Round;
                        g.DrawPath(outline, path);
                    }
                    using (Brush fill = new SolidBrush(Color.FromArgb(255, 255, 72, 60)))
                        g.FillPath(fill, path);
                }
            }
            return bmp;
        }
    }

    // ---------------------------------------------------------------- tick ---

    void OnTick() {
        try {
            if (_pollWant != 0) { int want = _pollWant; _pollWant = 0; PollNow(want == 2); }

            double dt = _timer.Interval / 1000.0;
            bool anim = false;

            if (_snapping) {
                _snapT += dt / 0.16;
                if (_snapT >= 1) { Location = _snapTo; _snapping = false; }
                else {
                    double e = 1 - Math.Pow(1 - _snapT, 3);
                    Location = new Point(
                        (int)Math.Round(_snapFrom.X + (_snapTo.X - _snapFrom.X) * e),
                        (int)Math.Round(_snapFrom.Y + (_snapTo.Y - _snapFrom.Y) * e));
                }
                PushLayer();
            }

            _lastCueAmount = 0;              // diagnostics: what fired this frame
            DrainBankedBalance();            // apply any reading that arrived

            if (_dueGap > 0) _dueGap -= dt;

            // The ONLY place the printed number moves: one cent per cue, never
            // more, and never without a cue. The debt is re-derived every time a
            // reading lands, so a reading arriving mid-cue can never strand it.
            double take = 0;
            if (_dueGap <= 0 && _pendingStep <= 0 && _pending > 1e-9) {
                // DeepSeek: charge the whole observed drop in one go. GO: one
                // real model call per cue when the request log can supply it.
                take = (_src == SrcGo) ? TakeGo() : Math.Round(_pending, 2);
            }
            if (take > 0) {
                // GO costs are a hundred times smaller than a cent, so its books
                // are kept to eight decimals; at four, every cue would round the
                // remainder and the printed number would slowly drift off the
                // meter.
                int dp = (_src == SrcGo) ? 8 : 4;
                _pending = Math.Round(_pending - take, dp);
                if (_pending < 1e-9) _pending = 0;
                _bookedAt = Math.Round(_bookedAt - take, dp);
                _bookedBal = Math.Round(_bookedBal - take, dp);
                _pendingStep = take;
                _dueGap = CueGapSec;
            }

            // The cue itself fires once the previous floater has finished rising:
            // The cue fires as soon as its slot is due. It deliberately does NOT
            // wait for the previous number to finish flying: with a 0.2s rhythm
            // that wait would stretch every cue to over a second. The numbers
            // stack upwards instead, comet-style, so a run of cues still reads
            // clearly.
            if (_pendingStep > 0) {
                _lastCueAmount = _pendingStep;
                MakeTick(_pendingStep, false);
                _pendingStep = 0;
                anim = true;
            }

            // Rehearsal run: same rhythm as real charges, so a custom amount shows
            // exactly what a burst of real spending looks like. The printed number
            // walks down; the books stay untouched.
            if (_demoLeft > 0 && _pendingStep <= 0 && _dueGap <= 0) {
                _lastCueAmount = _demoAmount;
                MakeTick(_demoAmount, true);
                _demoLeft--;
                _dueGap = CueGapSec;
                anim = true;
            }
            for (int i = _hits.Count - 1; i >= 0; i--) {
                _hits[i].T += dt; anim = true;
                if (_hits[i].Done) _hits.RemoveAt(i);
            }
            for (int i = _floaters.Count - 1; i >= 0; i--) {
                _floaters[i].T += dt; anim = true;
                if (_floaters[i].Done) _floaters.RemoveAt(i);
            }

            if (anim || _dirty) { RenderToCanvas(); PushLayer(); }
        } catch (Exception ex) { Log("tick: " + ex); }
    }

    // What the next cue charges, in GO mode. With the request log in hand this is
    // one real call's exact cost; without it (no cookie, or the log lagging a
    // meter already booked) the whole observed drop goes in one cue, which is
    // still exact to a hundredth of a cent because the meters report micro-cents.
    double TakeGo() {
        if (_goCues.Count > 0) {
            double c = _goCues[0];
            _goCues.RemoveAt(0);
            _goQueuedSum -= c;
            if (_goQueuedSum < 1e-12) _goQueuedSum = 0;
            if (c > _pending) c = _pending;
            if (c > 0) return c;
        }
        return _pending;
    }

    // fromTest: the menu / tray "test one charge" cue only pretends to spend.
    // It plays the animation and drops the printed number through _testOffset
    // while leaving _realBal and the step yardstick alone, so a later balance
    // refresh neither corrects the number back up nor mistakes the rehearsal for
    // money actually spent. There is deliberately no cap on the offset: capping
    // it used to freeze the number after ten cues. The printed value clamps at
    // zero instead, and any real spending clears the offset.
    void MakeTick(double amount, bool fromTest) {
        // Cap the simultaneous hurt overlays. Real charges are already capped by
        // the tick loop, but repeatedly clicking "test one charge" used to stack
        // an unbounded number of them and each one added its own shake, which
        // added up to a sprite flying across the screen.
        if (_hits.Count < 3) _hits.Add(new Hit());
        if (fromTest) {
            _testOffset = Math.Round(_testOffset + amount, 4);
        } else {
            _testOffset = 0;                         // real movement clears rehearsals
        }
        Floater fl = new Floater();
        // Six decimals in GO mode: a real call can cost $0.000512, which the
        // DeepSeek format would print as a bare "-0".
        fl.Text = "-" + amount.ToString(_src == SrcGo ? "0.######" : "0.####",
                                        CultureInfo.InvariantCulture);
        // One charge prints in full; the text grows with the amount, capped at 1 yuan.
        double ratio = Math.Min(Math.Max(amount, 0.0), FloatCapYuan) / FloatCapYuan;
        fl.Mul = (float)(1.0 + (FloatMaxMul - 1.0) * ratio);
        if (_mirror) {
            // mirror the offset: the number's right edge sits where the left edge did
            float fem = (float)Math.Max(13.0, 34.0 * _scale) * fl.Mul;
            using (Font ff = new Font("Arial", fem, FontStyle.Bold, GraphicsUnit.Pixel)) {
                Size fsz = TextRenderer.MeasureText(fl.Text, ff);
                fl.X = _headX + (int)(_w * 0.06) - (fsz.Width + (int)(fem * 0.9f));
            }
        } else {
            fl.X = _headX - (int)(_w * 0.06);
        }
        // Cascade: each number that is still in the air pushes the next one up, so
        // a fast run reads as a comet trail instead of a stack printed in place.
        // Counted over a short window, otherwise a long flight would fling a later
        // cue far above the head.
        int trail = 0;
        for (int i = 0; i < _floaters.Count; i++) if (_floaters[i].T < 0.5) trail++;
        if (trail > 3) trail = 3;
        fl.Y = _headY - (int)Math.Round(trail * _floaterStep);
        fl.Jitter = 7 * _scale;
        _floaters.Add(fl);
        if (_floaters.Count > 20) _floaters.RemoveAt(0);
        PlayHitSound();          // every pop plays, even on top of the last one
        _dirty = true;
    }

    void MakeTick(double amount) { MakeTick(amount, false); }

    // Every damage number plays the cue on its own MCI alias, so a new hit never
    // cuts off the previous one.
    void PlayHitSound() {
        if (_sound == null || !_soundEnabled) return;
        int slot = _sound.Play();
        if (slot < 0) {
            Log("sound: nothing played (failed=" + _sound.Failed + " err=" + _sound.Error + ")");
            _soundEnabled = false;                       // do not spam the log
        }
    }

    // ------------------------------------------------------------- polling ---
    //
    // Two transports: .NET HttpClient first, then the local Node runtime. Some
    // locked-down Windows setups make schannel refuse to acquire TLS credentials
    // for .NET while Node ships its own OpenSSL stack and still works.

    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient() {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        HttpClient c;
        try {
            // opencode.ai answers through Cloudflare, which compresses even when
            // it was not asked to. Only advertise what .NET can undo.
            HttpClientHandler h = new HttpClientHandler();
            h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            // The OpenCode console session is sent as a hand-built Cookie header.
            // With the default UseCookies = true the .NET Framework handler
            // replaces that header with its own (empty) cookie container, and the
            // console answers 401 - verified against 4.8, where false returns 200.
            h.UseCookies = false;
            c = new HttpClient(h);
        } catch { c = new HttpClient(); }
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    static long NowMs() {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    static string _nodePath;
    static bool _nodeSearched;
    static string FindNode() {
        if (_nodeSearched) return _nodePath;
        _nodeSearched = true;

        string explicitPath = Environment.GetEnvironmentVariable("DSHPET_NODE");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) { _nodePath = explicitPath; return _nodePath; }

        string fromPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(fromPath)) {
            foreach (string dir in fromPath.Split(';')) {
                if (dir.Trim().Length == 0) continue;
                try {
                    string cand = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(cand)) { _nodePath = cand; return _nodePath; }
                } catch { }
            }
        }
        string[] roots = new string[] {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetEnvironmentVariable("APPDATA")
        };
        foreach (string r in roots) {
            if (string.IsNullOrEmpty(r)) continue;
            try {
                foreach (string sub in new string[] { "nodejs\\node.exe", "Programs\\nodejs\\node.exe",
                                                      "nvm\\current\\node.exe" }) {
                    string cand = Path.Combine(r, sub);
                    if (File.Exists(cand)) { _nodePath = cand; return _nodePath; }
                }
                if (Directory.Exists(r)) {
                    foreach (string d in Directory.GetDirectories(r, "node-v*")) {
                        string cand = Path.Combine(d, "node.exe");
                        if (File.Exists(cand)) { _nodePath = cand; return _nodePath; }
                    }
                }
            } catch { }
        }
        return _nodePath;
    }

    // One GET with an explicit header list: .NET first, the local Node runtime as
    // the fallback transport. marker is a substring the body must contain, which
    // is how a 200 that is really a login page gets caught.
    string GetJson(string url, string[] headers, string marker) {
        try {
            return GetWithHttp(url, headers, marker);
        } catch (Exception ex) {
            string m = ex.Message;
            // The transport itself worked (or answered with a real HTTP status):
            // re-running the exact request through Node would only repeat it.
            if (m.StartsWith("HTTP ") || m.StartsWith("unexpected response")) throw;
            Log("http transport failed (" + ex.GetType().Name + ": " + m + "), trying node");
        }
        return GetWithNode(url, headers, marker);
    }

    string GetWithHttp(string url, string[] headers, string marker) {
        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url)) {
            for (int i = 0; i < headers.Length; i++) {
                int c = headers[i].IndexOf(':');
                if (c <= 0) continue;
                req.Headers.TryAddWithoutValidation(headers[i].Substring(0, c).Trim(),
                                                    headers[i].Substring(c + 1).Trim());
            }
            using (HttpResponseMessage resp = Http.SendAsync(req).GetAwaiter().GetResult()) {
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)resp.StatusCode + " " + Trunc(body));
                if (marker != null && body.IndexOf(marker) < 0)
                    throw new Exception("unexpected response: " + Trunc(body));
                return body;
            }
        }
    }

    string GetWithNode(string url, string[] headers, string marker) {
        string node = FindNode();
        if (node == null) throw new Exception("node not found");
        string script =
            "const h=require('https');const u=process.env.DSHPET_URL;const hd={};" +
            "(process.env.DSHPET_HEADERS||'').split('\\n').forEach(function(l){" +
            "var i=l.indexOf(':');if(i>0)hd[l.slice(0,i)]=l.slice(i+1)});" +
            "const r=h.get(u,{headers:hd},function(s){var c=[];s.on('data',function(d){c.push(d)});" +
            "s.on('end',function(){process.stdout.write(Buffer.concat(c).toString('utf8'))})});" +
            "r.on('error',function(e){console.error(String(e.message||e));process.exit(2)});" +
            "r.setTimeout(15000,function(){console.error('timeout');process.exit(3)});";

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = node;
        psi.Arguments = "-e \"" + script.Replace("\"", "\\\"") + "\"";
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.EnvironmentVariables["DSHPET_URL"] = url;
        psi.EnvironmentVariables["DSHPET_HEADERS"] = string.Join("\n", headers);
        using (Process p = Process.Start(psi)) {
            string outp = p.StandardOutput.ReadToEnd();
            string errp = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } throw new Exception("node timeout"); }
            if (p.ExitCode != 0 || outp.Trim().Length == 0)
                throw new Exception("node exit " + p.ExitCode + " " + Trunc(errp));
            if (marker != null && outp.IndexOf(marker) < 0) throw new Exception("node: " + Trunc(outp));
            return outp;
        }
    }

    // ---------------------------------------------------- deepseek balance ---

    string[] DshHeaders() {
        return new string[] {
            "Authorization: Bearer " + _apiKey,
            "Accept: application/json",
            "Accept-Encoding: gzip, deflate"
        };
    }

    string FetchBalance() {
        return GetJson(_apiUrl, DshHeaders(), "is_available");
    }

    // ------------------------------------------------------- opencode go -----

    // The bearer key is tried first because it does not expire; if it is stale
    // the console cookie gets a turn before the poll is declared failed.
    string FetchGoStatus() {
        if (GoHasKey) {
            try { return GetJson(GoStatusUrl, GoHeaders(false), "fiveHour"); }
            catch (Exception ex) {
                if (!GoHasCookie) throw;
                Log("go status via key failed (" + ex.Message + "), trying console cookie");
            }
        }
        return GetJson(GoStatusUrl, GoHeaders(true), "fiveHour");
    }

    // request-logs only answers to the console session cookie; the bearer key
    // gets a 403, so this is never called without one.
    string FetchGoLogs() { return FetchGoLogs(0); }

    // forceSince (ms) is only used by --goprobe --since=... to look further back
    // than a live poll ever would.
    string FetchGoLogs(long forceSince) {
        long since = forceSince > 0 ? forceSince
                   : (_goSince > 0 ? _goSince - (long)GoLogOverlapMs
                                   : NowMs() - (long)GoFirstLookbackMs);
        if (since < 0) since = 0;
        return GetJson(GoLogsBase + since.ToString(CultureInfo.InvariantCulture), GoHeaders(true), "\"items\"");
    }

    static string Trunc(string s) {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 200 ? s.Substring(0, 200) : s;
    }

    // snap = the user asked for it (the menu's "refresh now", or --shot): the
    // tablet jumps straight to the value just read. snap = false is the
    // background poll, which keeps walking down in whole steps so every step
    // still gets its hurt animation. Synchronous either way, so a menu click is
    // answered on the spot and always logged.
    // The fetch runs on a worker thread and the result is banked for the next
    // frame: with the poll now every couple of seconds, doing the HTTP call on
    // the UI thread would visibly freeze the widget. The banked reading is
    // applied by OnTick, so the tablet still reacts within one frame of it
    // arriving. Only one request is ever in flight.
    void PollNow(bool snap) {
        if (_src == SrcGo) { PollNowGo(snap); return; }
        if (string.IsNullOrEmpty(_apiKey)) {
            _connected = false;
            _status = S_NOKEY;
            _lastPollResult = "no api key";
            _dirty = true;
            Log("poll skipped: no api key (menu: " + S_SETKEY + ")");
            return;
        }
        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0) return;
        bool wantSnap = snap;
        ThreadPool.QueueUserWorkItem(delegate {
            try {
                string body = FetchBalance();
                double bal = ParseCny(body);
                if (double.IsNaN(bal)) { Fail("cannot parse response: " + Trunc(body)); return; }
                lock (_hits) { _bankedBal = bal; _bankedSnap = wantSnap; _bankedKind = 0; _bankedValid = true; }
            } catch (Exception ex) {
                Fail(ex.Message);
            } finally {
                Interlocked.Exchange(ref _pollInFlight, 0);
            }
        });
    }

    // The GO poll. The meters come back every time; the request log is only
    // asked for when a meter actually moved (or the user forced a refresh),
    // because the log can only ever explain money the meters have already
    // booked - and because that keeps the extra request down to the moments
    // where a call really happened.
    void PollNowGo(bool snap) {
        if (!GoReady) {
            _connected = false;
            _status = S_GONOCRED;
            _lastPollResult = "no go credentials";
            _dirty = true;
            Log("go poll skipped: no credentials (menu: " + S_GOCRED + ")");
            return;
        }
        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0) return;
        bool wantSnap = snap;
        ThreadPool.QueueUserWorkItem(delegate {
            try {
                string body = FetchGoStatus();
                GoReading r = new GoReading();
                if (!ParseGoStatus(body, r.Lim, r.Used)) { Fail("go status: cannot parse " + Trunc(body)); return; }

                bool needLogs = wantSnap || !_goPrimed;
                if (!needLogs) {
                    lock (_hits) {
                        for (int w = 0; w < 3; w++)
                            if (!double.IsNaN(_goPrev[w]) && Math.Abs(r.Used[w] - _goPrev[w]) > 1e-9) needLogs = true;
                    }
                }
                if (needLogs && GoHasCookie && !_goLogsBlocked) {
                    try {
                        r.Logs = FetchGoLogs();
                        r.LogsFetched = true;
                    } catch (Exception lex) {
                        string lm = lex.Message;
                        // A dead session cookie must not take the meters down
                        // with it: the quotal view keeps working, only the
                        // per-call itemising is lost.
                        if (lm.StartsWith("HTTP 401") || lm.StartsWith("HTTP 403")) _goLogsBlocked = true;
                        Log("go logs failed (" + lm + ")" + (_goLogsBlocked ? " - itemising disabled" : ""));
                    }
                }
                lock (_hits) { _bankedGo = r; _bankedSnap = wantSnap; _bankedKind = 1; _bankedValid = true; }
            } catch (Exception ex) {
                Fail(ex.Message);
            } finally {
                Interlocked.Exchange(ref _pollInFlight, 0);
            }
        });
    }

    // applies a banked reading; called from the render tick
    void DrainBankedBalance() {
        // Offline self-tests must ignore even a reading that was already
        // fetched: a request issued during start-up can land in the middle of a
        // simulated sequence and wipe the pending amount, which made the test
        // (and this bug hunt) lie.
        if (_noNetwork) { lock (_hits) { _bankedValid = false; } return; }
        double bal; bool snap; int kind; GoReading gr;
        lock (_hits) {
            if (!_bankedValid) return;
            bal = _bankedBal; snap = _bankedSnap; kind = _bankedKind;
            gr = _bankedGo; _bankedGo = null; _bankedValid = false;
        }
        if (kind == 1) {
            // Drop a reading whose source is no longer the active one: the user
            // can switch while a request is in flight, and re-anchoring the new
            // source on the old source's number would print a value that never
            // existed.
            if (gr == null || _src != SrcGo) return;
            ApplyGoReading(gr, snap);
            return;
        }
        if (_src != SrcDsh) return;
        ApplyBalance(bal, snap);
        Log("poll ok" + (snap ? " (snap)" : "") +
            ": balance=" + bal.ToString("0.00", CultureInfo.InvariantCulture) +
            " printed=" + DrawnText +
            " bookedAt=" + (double.IsNaN(_bookedAt) ? -1 : _bookedAt) +
            " pending=" + _pending.ToString("0.####", CultureInfo.InvariantCulture));
    }

    // blocking variants, used by the offline self-tests and by --goprobe
    void PollNowSync(bool snap) {
        if (_src == SrcGo) {
            try {
                string body = FetchGoStatus();
                GoReading r = new GoReading();
                if (!ParseGoStatus(body, r.Lim, r.Used)) { Fail("go status: cannot parse " + Trunc(body)); return; }
                if (GoHasCookie && !_goLogsBlocked) {
                    try { r.Logs = FetchGoLogs(); r.LogsFetched = true; }
                    catch (Exception lex) { Log("go logs failed: " + lex.Message); }
                }
                ApplyGoReading(r, snap);
            } catch (Exception ex) { Fail(ex.Message); }
            return;
        }
        try {
            string body = FetchBalance();
            double bal = ParseCny(body);
            if (double.IsNaN(bal)) { Fail("cannot parse response: " + Trunc(body)); return; }
            ApplyBalance(bal, snap);
        } catch (Exception ex) { Fail(ex.Message); }
    }

    // The whole balance bookkeeping lives here. Nothing else may move the printed
    // number: the total still owed is always re-derived from
    // (_bookedAt - bal), so a reading that arrives at any moment can never make
    // the display drift on its own.
    void ApplyBalance(double bal, bool snap) {
        lock (_hits) {
            _connected = true;
            _lastPollResult = "";
            bool firstReading = double.IsNaN(_realBal);
            _realBal = bal;
            if (firstReading) {
                _bookedAt = bal;
                _bookedBal = bal;
                _pending = 0;
            }
            if (snap) {
                // "refresh now": believe the server exactly, drop rehearsals and
                // anything still queued
                _bookedAt = bal;
                _bookedBal = bal;
                _testOffset = 0;
                _pending = 0;
                _pendingStep = 0;
                _dueGap = 0;
            } else if (!firstReading) {
                // The whole difference becomes the debt. A cue already committed
                // but not yet charged counts as part of it, so it is subtracted
                // rather than overwritten - overwriting it is what used to make
                // the number move by less than one animation.
                double owed = Math.Round(_bookedAt - bal, 4);
                if (owed < -1e-9) {
                    // the server walked the balance back up (a correction, not a
                    // top-up): re-anchor so the debt is only what is uncharged
                    owed = _pending;
                    _bookedAt = Math.Round(bal + owed, 4);
                    _bookedBal = Math.Round(bal + owed, 4);
                }
                if (owed >= _pendingStep - 1e-9) owed = Math.Round(owed - _pendingStep, 4);
                else owed = 0;                    // the cue in flight already covers it
                // one cue is one cent, so cap the queue to keep a huge jump from
                // turning into minutes of animation
                double cap = StepYuan * MaxCuesPerPoll;
                if (owed > cap) owed = cap;
                _pending = owed < 1e-9 ? 0 : owed;
                if (_pending > 1e-9 && _dueGap <= 0) _dueGap = 0;   // first cue now
            }
        }
        _dirty = true;
    }

    void Fail(string why) {
        lock (_hits) { _connected = false; _status = S_LOADING; _lastPollResult = why; }
        Log("poll FAILED: " + why);
        _dirty = true;
    }

    static double ParseCny(string json) {
        int i = json.IndexOf("\"balance_infos\"");
        string scope = i >= 0 ? json.Substring(i) : json;
        int c = scope.IndexOf("\"CNY\"");
        if (c < 0) return double.NaN;
        int j = scope.IndexOf("total_balance", c);
        if (j < 0) return double.NaN;
        int col = scope.IndexOf(':', j);
        if (col < 0) return double.NaN;
        int q1 = scope.IndexOf('"', col + 1);
        if (q1 < 0) return double.NaN;
        int q2 = scope.IndexOf('"', q1 + 1);
        if (q2 < 0) return double.NaN;
        double v;
        if (double.TryParse(scope.Substring(q1 + 1, q2 - q1 - 1), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out v)) return v;
        return double.NaN;
    }

    // --------------------------------------------------------- go parsing ----
    //
    // Hand-rolled for the same reason ParseCny is: the widget ships with no JSON
    // dependency, and it only ever needs a handful of flat fields.

    static string Fmt(double v, int dp) {
        return double.IsNaN(v) ? "--" : v.ToString("F" + dp.ToString(CultureInfo.InvariantCulture),
                                                  CultureInfo.InvariantCulture);
    }

    // Value of "key" in a scope: quoted string or bare number. NaN when the key
    // is missing or its value is null.
    static double JsonNum(string s, string key) {
        if (s == null) return double.NaN;
        int i = s.IndexOf("\"" + key + "\"");
        if (i < 0) return double.NaN;
        int c = s.IndexOf(':', i);
        if (c < 0) return double.NaN;
        int p = c + 1;
        while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++;
        if (p >= s.Length || s[p] == 'n') return double.NaN;      // null
        int q1, q2;
        if (s[p] == '"') {
            q1 = p + 1;
            q2 = s.IndexOf('"', q1);
        } else {
            q1 = p;
            q2 = p;
            while (q2 < s.Length && (char.IsDigit(s[q2]) || s[q2] == '-' || s[q2] == '+' ||
                                     s[q2] == '.' || s[q2] == 'e' || s[q2] == 'E')) q2++;
        }
        if (q1 < 0 || q2 <= q1) return double.NaN;
        double v;
        if (double.TryParse(s.Substring(q1, q2 - q1), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out v)) return v;
        return double.NaN;
    }

    static string JsonStr(string s, string key) {
        if (s == null) return null;
        int i = s.IndexOf("\"" + key + "\"");
        if (i < 0) return null;
        int c = s.IndexOf(':', i);
        if (c < 0) return null;
        int q1 = s.IndexOf('"', c + 1);
        if (q1 < 0) return null;
        int q2 = q1 + 1;
        while (q2 < s.Length) {
            if (s[q2] == '\\') { q2 += 2; continue; }
            if (s[q2] == '"') break;
            q2++;
        }
        if (q2 >= s.Length) return null;
        return s.Substring(q1 + 1, q2 - q1 - 1);
    }

    // The objects of the top-level array under arrayKey. Braces are matched with
    // string literals skipped, because requestHeaders values contain both braces
    // and quoted commas.
    static List<string> JsonArray(string json, string arrayKey) {
        List<string> res = new List<string>();
        if (json == null) return res;
        int k = json.IndexOf("\"" + arrayKey + "\"");
        if (k < 0) return res;
        int c = json.IndexOf(':', k);
        if (c < 0) return res;
        int p = c + 1;
        while (p < json.Length && (json[p] == ' ' || json[p] == '\t' || json[p] == '\r' || json[p] == '\n')) p++;
        if (p >= json.Length || json[p] != '[') return res;      // null or not an array

        int depth = 0, start = -1;
        bool inStr = false;
        for (int i = p; i < json.Length; i++) {
            char ch = json[i];
            if (inStr) {
                if (ch == '\\') { i++; continue; }
                if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{') { if (depth == 0) start = i; depth++; continue; }
            if (ch == '}') {
                depth--;
                if (depth <= 0 && start >= 0) { res.Add(json.Substring(start, i - start + 1)); start = -1; }
                if (depth < 0) depth = 0;
                continue;
            }
            if (ch == ']' && depth == 0) break;
        }
        return res;
    }

    // meters.{fiveHour,week,month}.{limitMicroCents,usedMicroCents}, in dollars
    static bool ParseGoStatus(string body, double[] lim, double[] used) {
        bool any = false;
        for (int w = 0; w < 3; w++) {
            lim[w] = 0; used[w] = 0;
            if (body == null) continue;
            int i = body.IndexOf("\"" + WinJson(w) + "\"");
            if (i < 0) continue;
            int close = body.IndexOf('}', i);            // meters hold no nested object
            string scope = close > i ? body.Substring(i, close - i) : body.Substring(i);
            double l = JsonNum(scope, "limitMicroCents");
            double u = JsonNum(scope, "usedMicroCents");
            if (double.IsNaN(l) || double.IsNaN(u)) continue;
            lim[w] = l / MicroPerUsd;
            used[w] = u / MicroPerUsd;
            any = true;
        }
        return any;
    }

    // ------------------------------------------------------ go bookkeeping ---

    void ApplyGoReading(GoReading r, bool snap) {
        // The meters go in first: they are the authority on how much money moved,
        // and a log slice is only allowed to spend the debt they just booked.
        bool prime = !_goPrimed;
        ApplyGoStatus(r, snap);
        if (r.LogsFetched) {
            HarvestGoLogs(r.Logs, prime);
            _goPrimed = true;
        }
        // Say it once, on the UI thread: from here on the meters still work but
        // calls are no longer itemised, and a silent downgrade would look like a
        // regression.
        if (_goLogsBlocked && !_goLogsWarned) {
            _goLogsWarned = true;
            Notify(S_GONOLOG);
        }
        Log("go poll ok" + (snap ? " (snap)" : "") + " [" + WinName(_win) + "]" +
            ": remain=" + Fmt(GoRemainingLive, 4) +
            " printed=" + DrawnText +
            " pending=" + Fmt(_pending, 6) +
            " cues=" + _goCues.Count.ToString(CultureInfo.InvariantCulture) +
            " queued=" + Fmt(_goQueuedSum, 6) +
            " left%=" + Fmt(GoRemainPct(Win5h), 1) + "/" + Fmt(GoRemainPct(WinWeek), 1) +
            "/" + Fmt(GoRemainPct(WinMonth), 1));
    }

    // Same shape as ApplyBalance: the printed number is re-derived from
    // (_bookedAt - remain), so a reading that lands at any moment cannot make it
    // drift. Only the way the debt is sliced into cues differs.
    void ApplyGoStatus(GoReading r, bool snap) {
        lock (_hits) {
            _connected = true;
            _lastPollResult = "";
            for (int w = 0; w < 3; w++) { _goLim[w] = r.Lim[w]; _goUsed[w] = r.Used[w]; }
            for (int w = 0; w < 3; w++) _goPrev[w] = r.Used[w];

            double cur = GoRemainingLive;
            if (double.IsNaN(cur)) {
                // no subscription, or the meters are missing: say so instead of
                // printing a number nobody can trust
                _connected = false;
                _lastPollResult = "no go meter";
                Log("go: no meter for " + WinName(_win) + " (limit=" + Fmt(_goLim[_win], 2) + ")");
                _dirty = true;
                return;
            }
            bool first = double.IsNaN(_realBal);
            double prev = _realBal;
            _realBal = cur;
            bool refill = !first && !snap && cur > prev + 1e-9;

            if (first || snap || refill) {
                // first reading, a forced refresh, or the allowance resetting:
                // believe it exactly and drop anything still queued
                _bookedAt = cur;
                _bookedBal = cur;
                _testOffset = 0;
                _pending = 0;
                _pendingStep = 0;
                _dueGap = 0;
                _goCues.Clear();
                _goQueuedSum = 0;
                if (refill) Log("go: allowance reset/refilled [" + WinName(_win) + "] -> " + Fmt(cur, 4));
            } else {
                double owed = Math.Round(_bookedAt - cur, 8);
                if (owed < -1e-9) {
                    // the allowance moved up without a reset (a correction, not a
                    // refill): re-anchor rather than play a refund animation
                    _bookedAt = cur;
                    _bookedBal = cur;
                    _pending = 0;
                    _pendingStep = 0;
                    _goCues.Clear();
                    _goQueuedSum = 0;
                    Log("go: allowance corrected up -> " + Fmt(cur, 4));
                } else {
                    // a cue already committed but not yet charged is part of the
                    // debt, so it is subtracted rather than overwritten
                    if (owed >= _pendingStep - 1e-9) owed = Math.Round(owed - _pendingStep, 8);
                    else owed = 0;
                    _pending = owed < 1e-9 ? 0 : owed;
                    // never keep a planned slice that lost its debt behind it
                    while (_goCues.Count > 0 && _goQueuedSum > _pending + 1e-9) {
                        double last = _goCues[_goCues.Count - 1];
                        _goCues.RemoveAt(_goCues.Count - 1);
                        _goQueuedSum -= last;
                    }
                    if (_pending > 1e-9 && _dueGap <= 0) _dueGap = 0;
                }
            }
        }
        _dirty = true;
    }

    // Every succeeded call in the request log becomes one cue carrying its exact
    // cost, so a burst of parallel calls is itemised instead of collapsing into
    // one lump. A slice is only planned while the meters still show that much
    // uncharged spend behind it, which is what keeps the itemised cues and the
    // meter total from ever disagreeing.
    void HarvestGoLogs(string json, bool prime) {
        List<string> items = JsonArray(json, "items");
        int exact = 0, lumped = 0, notBilled = 0;
        long newest = 0;
        lock (_hits) {
            for (int i = 0; i < items.Count; i++) {
                string it = items[i];
                string id = JsonStr(it, "id");
                double st = JsonNum(it, "startedAt");
                if (!double.IsNaN(st) && (long)st > newest) newest = (long)st;
                if (string.IsNullOrEmpty(id)) continue;
                if (_goSeen.Contains(id)) continue;
                _goSeen.Add(id);
                if (prime) continue;                       // start-up: seed the cursor only
                if (JsonStr(it, "outcome") != "succeeded") { notBilled++; continue; }
                double cost = JsonNum(it, "cost");
                if (double.IsNaN(cost) || cost <= 0) continue;   // free or fully cached
                if (_goQueuedSum + cost <= _pending + 1e-9) {
                    _goCues.Add(cost);
                    _goQueuedSum += cost;
                    exact++;
                } else {
                    lumped++;                              // meters already booked it
                }
            }
            if (newest > _goSince) _goSince = newest;
            if (_goSeen.Count > 1200) _goSeen.Clear();
            if (_dueGap <= 0 && _pending > 1e-9) _dueGap = 0;
        }
        if (exact > 0 || lumped > 0)
            Log("go logs: " + items.Count.ToString(CultureInfo.InvariantCulture) + " item(s), " +
                exact + " exact cue(s), " + lumped + " already lumped, " +
                notBilled + " not billable, cursor=" + _goSince);
    }

    void Log(string msg) {
        try {
            File.AppendAllText(Path.Combine(_baseDir, "pet.log"),
                DateTime.Now.ToString("s") + " " + msg + "\r\n");
        } catch { }
    }

    // ---------------------------------------------------------------- entry --

    [STAThread]
    public static void Run(string baseDir, string[] args) {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { try { Native.SetProcessDPIAware(); } catch { } }

        bool selftest = Array.IndexOf(args, "--selftest") >= 0;
        bool shot = Array.IndexOf(args, "--shot") >= 0;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        DshPet pet = new DshPet(baseDir, args);

        if (shot) {
            System.Windows.Forms.Timer shotTimer = new System.Windows.Forms.Timer();
            shotTimer.Interval = 2500;
            shotTimer.Tick += delegate {
                shotTimer.Stop();
                pet.PollNowSyncPublic(false);
                Console.WriteLine("rest  : " + pet.ShakeReport());

                // menu "refresh now" must print the latest value immediately
                Console.WriteLine("shown after auto poll     = " + pet.DrawnText);
                pet.PollNowSyncPublic(true);
                Console.WriteLine("shown after refresh now   = " + pet.DrawnText +
                                  "   (must equal the live balance)");

                // regression: the number must keep stepping for EVERY rehearsal.
                // It used to freeze after ten because the offset was capped.
                pet.PollNowSyncPublic(true);
                string before = pet.DrawnText;
                for (int i = 1; i <= 15; i++) pet.MakeTick(0.03, true);
                Console.WriteLine("printed before 15 cues    = " + before);
                Console.WriteLine("printed after 15 cues     = " + pet.DrawnText +
                                  "   (must be 0.45 lower, not frozen)");

                // and a rehearsal must not survive a refresh as phantom spend
                for (int i = 1; i <= 3; i++) pet.MakeTick(pet.StepValue, true);
                Console.WriteLine("shown after 3 test cues   = " + pet.DrawnText);
                pet.PollNowSyncPublic(true);
                Console.WriteLine("shown after refresh again = " + pet.DrawnText +
                                  "   <- back to the live balance, no drift");

                // visual check of a fast run: five cues at the real 0.2s rhythm,
                // captured mid-cascade so the trail spacing can be inspected
                pet.ResetDemoForTest();
                pet.PollNowSyncPublic(true);
                pet.DemoCharge(0.05);
                pet.PumpTicks(6);                    // first cue lands
                pet.PumpTicks(6);                    // second, 0.2s later
                pet.PumpTicks(6);                    // third
                pet.SaveCanvas(Path.Combine(baseDir, "_shot_cascade.png"));
                Console.WriteLine("cascade: " + pet.ShakeReport() +
                                  "  floaters=" + pet.FloaterCountPublic +
                                  "  printed=" + pet.DrawnText);
                pet.PumpTicks(360);                  // let the run finish
                Console.WriteLine("cascade settled -> printed " + pet.DrawnText);

                pet.ResetDemoForTest();
                pet.MakeTick(pet.StepValue, true);   // rehearsal for the screenshot
                pet.BurnFrames(2);
                Console.WriteLine("shake1: " + pet.ShakeReport());
                pet.SaveCanvas(Path.Combine(baseDir, "_shot_flash.png"));
                pet.BurnFrames(3);
                Console.WriteLine("shake2: " + pet.ShakeReport());
                pet.BurnFrames(9);
                Console.WriteLine("after : " + pet.ShakeReport());
                pet.SaveCanvas(Path.Combine(baseDir, "shot.png"));
                Console.WriteLine("shot ok: connected=" + pet._connected +
                                  " drawn=" + pet.DrawnText);
                Application.Exit();
            };
            shotTimer.Start();
            Application.Run(pet);
            return;
        }

        if (selftest) {
            Console.WriteLine("diag: " + pet.Diag());
            pet.GoOffline();                  // deterministic: no background poll
            pet.RunSelfTest(baseDir);
            Application.Exit();
            return;
        }

        // --goprobe talks to the real console API; --gosim and --shotgo are
        // offline and deterministic.
        if (Array.IndexOf(args, "--goprobe") >= 0) {
            pet.GoOffline();
            long since = 0;
            foreach (string a in args) {
                if (a.StartsWith("--since=")) {
                    long v;
                    if (long.TryParse(a.Substring(8), out v)) since = v;
                }
            }
            pet.GoProbe(since);
            Application.Exit();
            return;
        }

        if (Array.IndexOf(args, "--gosim") >= 0) {
            pet.GoOffline();
            pet.GoSim();
            Application.Exit();
            return;
        }

        foreach (string a in args) {
            if (a.StartsWith("--goblob=")) {
                pet.GoOffline();
                pet.GoBlobTest(a.Substring(9));
                Application.Exit();
                return;
            }
        }

        if (Array.IndexOf(args, "--shotgo") >= 0) {
            pet.GoOffline();
            pet.GoShot(baseDir);
            Application.Exit();
            return;
        }

        if (Array.IndexOf(args, "--simchain") >= 0) {
            pet.GoOffline();                  // deterministic: no background poll/ticks
            // Feeds a chain of balance readings through the accounting layer and
            // prints what the tablet would show. The number must only fall in
            // whole steps, and a reading that crosses a step must be charged on
            // the spot (not held until the next poll).
            Console.WriteLine("step=" + pet.StepValue.ToString("0.##") +
                              " per cue, " + pet.CueGapText + "s apart" +
                              "   (one cent per animation)");
            // single reading, then watch every tick
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(30.00, true);
            Console.WriteLine("  reset            : " + pet.StateText);
            pet.ApplyBalancePublic(29.99, false);   // exactly one cent
            Console.WriteLine("  server -> 29.99  : " + pet.StateText);
            for (int i = 1; i <= 6; i++) {
                pet.PumpTicks(1);
                Console.WriteLine("    tick " + i + "         : " + pet.StateText +
                                  "  printed=" + pet.DrawnText);
            }
            Console.WriteLine();

            Console.WriteLine("the reported case: a 0.05 drop becomes FIVE animations");
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(30.00, true);
            Console.WriteLine("  reset -> printed " + pet.DrawnText + "  (bookedAt " +
                              pet.BookedAtText + ")");
            pet.ApplyBalancePublic(29.95, false);        // 0.05 down
            Console.WriteLine("  server -> 29.95 : owed " +
                              pet.PendingCount.ToString("0.####") +
                              "  -> printed still " + pet.DrawnText);
            for (int i = 1; i <= 8; i++) {
                pet.PumpTicks(9);                        // ~0.3s per sample
                Console.WriteLine("    +0.3s -> printed " + pet.DrawnText +
                                  "  cue " + pet.LastCueText +
                                  "  owed " + pet.PendingCount.ToString("0.####"));
            }
            pet.PumpTicks(120);
            Console.WriteLine("  settled -> printed " + pet.DrawnText +
                              "  (30.00 - 0.05 = 29.95, 5 animations)");
            Console.WriteLine();

            double[] chain = new double[] { 30.00, 29.99, 29.98, 29.97, 29.98, 29.95, 29.90 };
            pet.ResetDemoForTest();
            for (int i = 0; i < chain.Length; i++) {
                pet.ApplyBalancePublic(chain[i], false);
                string line = "  read " + chain[i].ToString("0.00") +
                              " -> printed " + pet.DrawnText;
                pet.PumpTicks(60);                  // let queued cues land
                Console.WriteLine(line + " -> after cues " + pet.DrawnText +
                                  (Math.Abs(pet.PendingCount) > 1e-9
                                     ? "  (still owed " + pet.PendingCount.ToString("0.####") + ")"
                                     : ""));
            }

            Console.WriteLine();
            Console.WriteLine("a big jump is paid off one cent at a time, never merged:");
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(30.00, true);
            Console.WriteLine("  reset -> printed " + pet.DrawnText);
            pet.ApplyBalancePublic(29.75, false);   // 0.25 down = 25 animations
            for (int i = 1; i <= 6; i++) {
                pet.PumpTicks(24);                  // ~0.8s per sample
                Console.WriteLine("    +0.8s -> printed " + pet.DrawnText +
                                  "  cue " + pet.LastCueText +
                                  "  owed " + pet.PendingCount.ToString("0.####"));
            }
            pet.PumpTicks(900);                     // let the whole run finish
            Console.WriteLine("  settled -> printed " + pet.DrawnText +
                              "  (30.00 - 0.25 = 29.75)");

            Console.WriteLine();
            Console.WriteLine("demo cues only offset what is printed:");
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(30.00, true);
            Console.WriteLine("  reset -> printed " + pet.DrawnText);
            pet.DemoCharge(0.05);                   // same rhythm as a real burst
            for (int i = 1; i <= 5; i++) {
                pet.PumpTicks(16);
                Console.WriteLine("    +0.53s -> printed " + pet.DrawnText +
                                  "  cuesLeft " + pet.DemoLeftText);
            }
            pet.PumpTicks(200);
            Console.WriteLine("  all demo cues done -> printed " + pet.DrawnText +
                              "  cuesLeft " + pet.DemoLeftText);
            pet.ApplyBalancePublic(29.70, false);   // 0.25 of real spending = 25 cues
            pet.PumpTicks(900);
            Console.WriteLine("  real spend to 29.70 -> printed " + pet.DrawnText +
                              "  (demo offset cleared, real cues paid off)");
            Application.Exit();
            return;
        }

        Application.Run(pet);
    }

    public string Diag() {
        return "cm=" + _cm.ToString("0.##") + " canvas=" + _w + "x" + _h +
               " scale=" + _scale.ToString("F4") + " step=" + StepYuan.ToString("0.##") +
               " status=" + _status +
               " corner=" + Location.X + "," + Location.Y;
    }

    // reports the shake offset and where the two text layers actually land, so
    // "the text shakes with the character" can be checked numerically
    public string ShakeReport() {
        double panelCx = (_fx[1] + _fx[2]) / 2 + _shakeX;
        double panelCy = (_fy[1] + _fy[2]) / 2 + _shakeY;
        string f = "none";
        if (_floaters.Count > 0) {
            Floater fl = _floaters[_floaters.Count - 1];
            double t = fl.T / fl.Dur;
            float em = (float)Math.Max(13.0, 34.0 * _scale);
            f = "(" + (fl.X + (int)Math.Round(fl.Jitter * Math.Sin(t * 9)) + (int)Math.Round(_shakeX)) +
                "," + (fl.Y - (int)Math.Round(t * em * 2.7) + (int)Math.Round(_shakeY)) + ")";
        }
        return "shake=(" + _shakeX.ToString("F1") + "," + _shakeY.ToString("F1") + ")" +
               " screenText=(" + panelCx.ToString("F1") + "," + panelCy.ToString("F1") + ")" +
               " floater=" + f;
    }

    public bool ConnectedFlag { get { return _connected; } }
    public double RealBalance { get { return _realBal; } }
    public double PendingCount { get { return _pending; } }
    public string BookedAtText { get { return double.IsNaN(_bookedAt) ? "--" : _bookedAt.ToString("0.00", CultureInfo.InvariantCulture); } }
    public string DemoLeftText { get { return _demoLeft.ToString(CultureInfo.InvariantCulture); } }
    public string CueGapText { get { return CueGapSec.ToString("0.#", CultureInfo.InvariantCulture); } }
    public int FloaterCountPublic { get { return _floaters.Count; } }
    // clears in-flight animation state so self-test scenarios stay independent
    public void ResetDemoForTest() {
        _hits.Clear(); _floaters.Clear(); _pendingStep = 0; _demoLeft = 0; _dueGap = 0;
    }
    public double StepValue { get { return StepYuan; } }
    public string LastCueText {
        get { return _lastCueAmount > 0 ? "-" + _lastCueAmount.ToString("0.####", CultureInfo.InvariantCulture) : "(none)"; }
    }
    public void ApplyBalancePublic(double bal, bool snap) { ApplyBalance(bal, snap); }
    // drives the real tick loop without the UI timer, for --simchain
    public void PumpTicks(int n) { for (int i = 0; i < n; i++) OnTickProbe(); }
    // tests must never talk to the network: that races with the simulated readings
    public void PollNowSyncPublic(bool snap) { PollNowSync(snap); }
    public void GoOffline() {
        _noNetwork = true;
        _pollWant = 0;
        try { _timer.Stop(); _poll.Stop(); } catch { }
    }
    public string StateText { get { return "real=" + _realBal.ToString("0.####") + " printed=" + _bookedBal.ToString("0.####") + " bookedAt=" + _bookedAt.ToString("0.####") + " pend=" + _pending.ToString("0.####") + " step=" + _pendingStep.ToString("0.####") + " gap=" + _dueGap.ToString("0.###") + " hits=" + _hits.Count + " floaters=" + _floaters.Count + " dt=" + (_timer.Interval/1000.0).ToString("0.###"); } }
    void OnTickProbe() { OnTick(); }
    public string DrawnText {
        get {
            return double.IsNaN(DrawnBalance)
                ? "--"
                : DrawnBalance.ToString(_src == SrcGo ? "0.0000" : "0.00", CultureInfo.InvariantCulture);
        }
    }

    // advance animation state without the timer, used by --shot
    public void BurnFrames(int n) {
        for (int i = 0; i < n; i++) {
            double dt = 0.033;
            for (int k = _hits.Count - 1; k >= 0; k--) { _hits[k].T += dt; if (_hits[k].Done) _hits.RemoveAt(k); }
            for (int k = _floaters.Count - 1; k >= 0; k--) { _floaters[k].T += dt; if (_floaters[k].Done) _floaters.RemoveAt(k); }
        }
        RenderToCanvas();
        PushLayer();
    }

    // ---------------------------------------------------------- go probe ----
    //
    // --goprobe: talk to the real console API and print what was understood.
    // Bypasses the poll/bank machinery on purpose, so a failure is reported with
    // its raw reason instead of a status line.
    public void GoProbe() { GoProbe(0); }

    public void GoProbe(long sinceOverride) {
        Console.WriteLine("credentials : key=" + (GoHasKey ? "yes" : "no") +
                          "  cookie=" + (GoHasCookie ? "yes" : "no") +
                          "  org=" + (_goOrg.Length > 0 ? _goOrg : "(none)"));
        Console.WriteLine("status auth : " + (GoHasKey ? "bearer key" : (GoHasCookie ? "console cookie" : "none")));
        try {
            string body = FetchGoStatus();
            Console.WriteLine("status bytes: " + body.Length.ToString(CultureInfo.InvariantCulture));
            double[] lim = new double[3], used = new double[3];
            bool ok = ParseGoStatus(body, lim, used);
            Console.WriteLine("status parse: " + ok);
            if (ok) {
                for (int w = 0; w < 3; w++) {
                    double left = lim[w] - used[w];
                    Console.WriteLine("  " + WinJson(w).PadRight(9) +
                                      " limit=$" + Fmt(lim[w], 6) +
                                      "  used=$" + Fmt(used[w], 6) +
                                      "  remain=$" + Fmt(left, 6) +
                                      "  (" + Fmt(lim[w] > 0 ? left / lim[w] * 100.0 : double.NaN, 2) + "% left)");
                }
            }
        } catch (Exception ex) { Console.WriteLine("status FAILED: " + ex.Message); }

        if (!GoHasCookie) {
            Console.WriteLine("logs skipped: no console cookie (the bearer key answers 403 here)");
            return;
        }
        try {
            string logs = FetchGoLogs(sinceOverride);
            List<string> items = JsonArray(logs, "items");
            Console.WriteLine("logs bytes  : " + logs.Length.ToString(CultureInfo.InvariantCulture) +
                              "  items=" + items.Count.ToString(CultureInfo.InvariantCulture));
            double sum = 0;
            int billable = 0, withHeaders = 0;
            for (int i = 0; i < items.Count; i++) {
                string it = items[i];
                double c = JsonNum(it, "cost");
                if (JsonStr(it, "outcome") == "succeeded" && !double.IsNaN(c) && c > 0) { sum += c; billable++; }
                // the nested objects are the reason this parser exists: count them
                if (it.IndexOf("\"requestHeaders\"") >= 0) withHeaders++;
                if (i < 5)
                    Console.WriteLine("  " + JsonStr(it, "outcome") + "  " + JsonStr(it, "model") +
                                      "  cost=" + Fmt(c, 8) +
                                      "  dur=" + Fmt(JsonNum(it, "durationMs"), 0) + "ms" +
                                      "  startedAt=" + Fmt(JsonNum(it, "startedAt"), 0) +
                                      "  id=" + Trunc(JsonStr(it, "id")));
            }
            Console.WriteLine("billable    : " + billable + " item(s) summing to $" + Fmt(sum, 8));
            Console.WriteLine("nested ok   : " + withHeaders + "/" + items.Count + " item(s) carried a nested requestHeaders object");
            // An item must never be split by a brace inside a header value: if it
            // were, the piece would have no id and this count would be short.
            int ids = 0;
            for (int i = 0; i < items.Count; i++) if (!string.IsNullOrEmpty(JsonStr(items[i], "id"))) ids++;
            Console.WriteLine("ids parsed  : " + ids + "/" + items.Count);
        } catch (Exception ex) { Console.WriteLine("logs FAILED: " + ex.Message); }
    }

    static string Mask(string v) {
        if (v == null) return "(not found)";
        if (v.Length <= 10) return v;
        return v.Substring(0, 6) + "..." + v.Substring(v.Length - 4) +
               " (len " + v.Length.ToString(CultureInfo.InvariantCulture) + ")";
    }

    // --goblob=<file>: run the credential scanner over a file, exactly the way the
    // paste dialog does. Values are masked; the point is to prove the three pieces
    // are found in a real capture without printing the secrets.
    public void GoBlobTest(string path) {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { Console.WriteLine("cannot read " + path + ": " + ex.Message); return; }
        string a, s, o, k;
        ParseGoBlob(text, out a, out s, out o, out k);
        Console.WriteLine("blob chars : " + text.Length.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("auth       : " + Mask(a));
        Console.WriteLine("session    : " + Mask(s));
        Console.WriteLine("org        : " + (o == null ? "(not found)" : o));
        Console.WriteLine("key        : " + Mask(k));
        Console.WriteLine("verdict    : " + (a != null && s != null && o != null
                            ? "cookie credentials complete (itemising enabled)"
                            : (k != null ? "service key only (quota works, no itemising)"
                                         : "incomplete - paste more of the capture")));
    }

    // ------------------------------------------------------------ go sim ----
    //
    // --gosim: the accounting, offline and deterministic. Feeds synthetic meter
    // readings plus request logs through exactly the code the widget runs and
    // checks the two properties that matter:
    //   * the printed number only ever lands on the meter's own value, and
    //   * the itemised cues never bill the same money twice.
    GoReading MkRead(double[] lim, double[] used, string logs) {
        GoReading r = new GoReading();
        for (int w = 0; w < 3; w++) { r.Lim[w] = lim[w]; r.Used[w] = used[w]; }
        r.Logs = logs;
        r.LogsFetched = logs != null;
        return r;
    }

    static string LogItem(string id, long startedAt, string outcome, double cost) {
        return "{\"id\":\"" + id + "\",\"requestID\":\"" + id + "\",\"startedAt\":" +
               startedAt.ToString(CultureInfo.InvariantCulture) +
               ",\"outcome\":\"" + outcome + "\",\"cost\":" +
               cost.ToString("0.########", CultureInfo.InvariantCulture) +
               ",\"model\":\"deepseek-v4.1-flash\",\"thinking\":{\"a\":\"}{\"}}";
    }
    static string LogBody(string items) {
        return "{\"items\":[" + items + "],\"until\":" + NowMs().ToString(CultureInfo.InvariantCulture) +
               ",\"retentionDays\":30}";
    }

    int _simFail;
    void Chk(string what, bool ok, string detail) {
        if (!ok) _simFail++;
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
    }

    // pumps the real tick loop, collecting what each cue charged
    List<double> PumpCues(int ticks) {
        List<double> got = new List<double>();
        for (int i = 0; i < ticks; i++) {
            PumpTicks(1);
            if (_lastCueAmount > 0) got.Add(_lastCueAmount);
        }
        return got;
    }
    static string CueList(List<double> v) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < v.Count; i++) {
            if (i > 0) sb.Append(" + ");
            sb.Append(v[i].ToString("0.########", CultureInfo.InvariantCulture));
        }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }
    static double Sum(List<double> v) {
        double s = 0;
        for (int i = 0; i < v.Count; i++) s += v[i];
        return s;
    }

    public void GoSim() {
        _src = SrcGo;
        _win = Win5h;
        ResetAccounting();
        _simFail = 0;
        double[] lim = new double[] { 12.0, 30.0, 60.0 };
        long t0 = NowMs() - 60000;

        Console.WriteLine("== GO accounting simulation (offline, 5h window, $12 limit) ==");

        // 1. first reading: anchors, plays nothing
        double[] used = new double[] { 8.0, 30.0, 30.0 };
        ApplyGoReading(MkRead(lim, used, LogBody("")), true);
        Console.WriteLine("anchor: meter=$" + Fmt(12.0 - used[0], 6) + " printed=" + DrawnText + " pending=" + Fmt(_pending, 8));
        Chk("first reading anchors without a cue", DrawnText == "4.0000" && _pending == 0, "printed=" + DrawnText);

        // 2. one real call, itemised
        double cA = 0.0024109;
        used[0] += cA;
        ApplyGoReading(MkRead(lim, used, LogBody(LogItem("a1", t0 + 1000, "succeeded", cA))), false);
        Chk("one call plans exactly one slice", _goCues.Count == 1 && Math.Abs(_goQueuedSum - cA) < 1e-12,
            "queued=" + _goQueuedSum.ToString("0.########") + " n=" + _goCues.Count);
        List<double> got = new List<double>();
        got.AddRange(PumpCues(40));
        Chk("the cue charges the call's exact cost", got.Count == 1 && Math.Abs(got[0] - cA) < 1e-12, CueList(got));
        Chk("printed lands on the meter", DrawnText == Fmt(Math.Round(12.0 - used[0], 4), 4),
            "printed=" + DrawnText + " meter=" + Fmt(12.0 - used[0], 6));

        // 3. two calls inside one poll interval: two cues, not one lump
        double cB1 = 0.00239846, cB2 = 0.00305062;
        used[0] += cB1 + cB2;
        ApplyGoReading(MkRead(lim, used, LogBody(
            LogItem("b1", t0 + 2000, "succeeded", cB1) + "," +
            LogItem("b2", t0 + 2001, "succeeded", cB2))), false);
        Chk("a burst of two plans two slices", _goCues.Count == 2, "n=" + _goCues.Count);
        got = PumpCues(40);
        Chk("both slices bill their own cost", got.Count == 2 &&
            Math.Abs(got[0] - cB1) < 1e-12 && Math.Abs(got[1] - cB2) < 1e-12, CueList(got));
        Chk("printed still lands on the meter", DrawnText == Fmt(Math.Round(12.0 - used[0], 4), 4),
            "printed=" + DrawnText + " meter=" + Fmt(12.0 - used[0], 6));

        // 4. meter moves before the log does: one lump, and the late log entry
        //    must not bill the same money a second time
        double cC = 0.000512;
        used[0] += cC;
        ApplyGoReading(MkRead(lim, used, LogBody("")), false);
        Chk("a meter move with no log yet still owes money", _pending > 0, "pending=" + Fmt(_pending, 8));
        got = PumpCues(40);
        Chk("it is charged as one lump of the meter delta", got.Count == 1 && Math.Abs(got[0] - cC) < 1e-12, CueList(got));
        ApplyGoReading(MkRead(lim, used, LogBody(LogItem("c1", t0 + 3000, "succeeded", cC))), false);
        got = PumpCues(40);
        Chk("the same call arriving late is not billed twice",
            got.Count == 0 && _pending == 0 && _goCues.Count == 0,
            "cues=" + CueList(got) + " pending=" + Fmt(_pending, 8));
        Chk("printed is unchanged by the late log", DrawnText == Fmt(Math.Round(12.0 - used[0], 4), 4),
            "printed=" + DrawnText + " meter=" + Fmt(12.0 - used[0], 6));

        // 5. rejected calls cost nothing
        ApplyGoReading(MkRead(lim, used, LogBody(LogItem("d1", t0 + 4000, "rejected", 0.0))), false);
        got = PumpCues(40);
        Chk("a rejected call never bills", got.Count == 0 && DrawnText == Fmt(Math.Round(12.0 - used[0], 4), 4),
            "cues=" + CueList(got));

        // 6. a free call (cost 0) is not a cue either
        used[0] += 0.0;
        ApplyGoReading(MkRead(lim, used, LogBody(LogItem("e1", t0 + 5000, "succeeded", 0.0))), false);
        got = PumpCues(40);
        Chk("a zero-cost call never bills", got.Count == 0, "cues=" + CueList(got));

        // 7. the window resets: the number refills instead of playing a refund
        used[0] = 0;
        ApplyGoReading(MkRead(lim, used, null), false);
        Chk("a reset refills the printed number", DrawnText == "12.0000" && _pending == 0 && _goCues.Count == 0,
            "printed=" + DrawnText);

        // 8. the invariant the whole design rests on: cues never exceed the
        //    meter's own debt, and once settled the two agree exactly
        double[] drop = new double[] { 0.00432123, 0.00000001, 0.15, 0.0009 };
        bool ok = true;
        for (int i = 0; i < drop.Length; i++) {
            used[0] += drop[i];
            ApplyGoReading(MkRead(lim, used, LogBody(LogItem("f" + i, t0 + 6000 + i, "succeeded", drop[i]))), false);
            if (_goQueuedSum > _pending + 1e-9) ok = false;
            PumpCues(120);
            string want = Fmt(Math.Round(12.0 - used[0], 4), 4);
            if (DrawnText != want) { ok = false; Console.WriteLine("      drift at " + i + ": " + DrawnText + " != " + want); }
        }
        Chk("a mixed run of costs never drifts or over-books", ok, "printed=" + DrawnText);

        // 9. the tray/menu rehearsal must not touch the GO books: it is allowed
        //    to offset the printed number, never to move what is booked
        _testOffset = 0;
        double booksBefore = _bookedBal;
        MakeTick(StepYuan, true);
        PumpCues(40);
        Chk("a rehearsal only offsets the display", _pending == 0 &&
            Math.Abs(_testOffset - StepYuan) < 1e-9 && Math.Abs(_bookedBal - booksBefore) < 1e-12,
            "printed=" + DrawnText + " offset=" + Fmt(_testOffset, 4) + " books=" + Fmt(_bookedBal, 4));

        // 10. window switching and source switching must re-anchor, not carry over
        _win = WinMonth;
        ResetAccounting();
        ApplyGoReading(MkRead(lim, new double[] { 8.0, 30.0, 30.0 }, null), true);
        Chk("switching window re-anchors on the new meter", DrawnText == "30.0000" && _pending == 0, "printed=" + DrawnText);

        // 11. the worker->UI handoff: a banked GO reading must be applied, and a
        //     reading left over from the other source must be dropped
        _noNetwork = false;                       // exercise the bank path itself
        _win = Win5h;
        ResetAccounting();
        lock (_hits) {
            _bankedGo = MkRead(lim, new double[] { 8.0, 30.0, 30.0 }, null);
            _bankedSnap = false; _bankedKind = 1; _bankedValid = true;
        }
        PumpTicks(1);
        Chk("a banked GO reading is applied by the tick", DrawnText == "4.0000", "printed=" + DrawnText);
        lock (_hits) {
            _bankedBal = 123.45;
            _bankedSnap = false; _bankedKind = 0; _bankedValid = true;
        }
        PumpTicks(1);
        Chk("a stale DeepSeek reading cannot hijack the GO display", DrawnText == "4.0000", "printed=" + DrawnText);
        _noNetwork = true;

        Console.WriteLine(_simFail == 0
            ? "== all GO accounting checks passed =="
            : "== " + _simFail + " GO accounting check(s) FAILED ==");
    }

    public void GoShot(string baseDir) {
        _src = SrcGo;
        ResetAccounting();
        double[] lim = new double[] { 12.0, 30.0, 60.0 };
        double[] used = new double[] { 8.34450239, 30.0, 30.0 };
        for (int w = 0; w < 3; w++) {
            _win = w;
            ResetAccounting();
            ApplyGoReading(MkRead(lim, used, null), true);
            RenderToCanvas();
            string p = Path.Combine(baseDir, "_shot_go_" + WinJson(w) + ".png");
            SaveCanvas(p);
            Console.WriteLine("shot [" + WinName(w) + "] printed=" + DrawnText +
                              " left%=" + Fmt(GoRemainPct(Win5h), 1) + "/" + Fmt(GoRemainPct(WinWeek), 1) +
                              "/" + Fmt(GoRemainPct(WinMonth), 1) + " -> " + Path.GetFileName(p));
        }
        // and with a cue in the air, to check the floater's tiny-number format
        _win = Win5h;
        ResetAccounting();
        ApplyGoReading(MkRead(lim, used, null), true);
        MakeTick(0.0024109, false);
        BurnFrames(2);
        RenderToCanvas();
        SaveCanvas(Path.Combine(baseDir, "_shot_go_cue.png"));
        Console.WriteLine("shot [cue] floaters=" + _floaters.Count +
                          " text=" + (_floaters.Count > 0 ? _floaters[_floaters.Count - 1].Text : "(none)") +
                          " printed=" + DrawnText);
    }

    public void RunSelfTest(string baseDir) {
        PollNowSync(true);                // start from the live value
        MakeTick(StepYuan, false);
        for (int i = 0; i < 6; i++) OnTick();
        RenderToCanvas();
        SaveCanvas(Path.Combine(baseDir, "selftest.png"));
        Console.WriteLine("selftest.png written; connected=" + _connected +
                          " drawn=" + DrawnText + " lastPoll=" + _lastPollResult);
    }

    public void SaveCanvas(string path) {
        using (Bitmap copy = new Bitmap(_canvas.Width, _canvas.Height, PixelFormat.Format32bppArgb)) {
            using (Graphics g = Graphics.FromImage(copy)) g.DrawImageUnscaled(_canvas, 0, 0);
            using (Bitmap flat = new Bitmap(copy.Width, copy.Height)) {
                using (Graphics g = Graphics.FromImage(flat)) {
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                            new Rectangle(0, 0, flat.Width, flat.Height),
                            Color.FromArgb(255, 28, 32, 46), Color.FromArgb(255, 62, 42, 58),
                            LinearGradientMode.ForwardDiagonal))
                        g.FillRectangle(lg, 0, 0, flat.Width, flat.Height);
                    g.DrawImageUnscaled(copy, 0, 0);
                }
                flat.Save(path, ImageFormat.Png);
            }
        }
    }
}
'@
}

# ------------------------------------------------------------------ startup --

$here = $PSScriptRoot
if (-not $here) { $here = (Get-Location).Path }

# API key: local override, then environment, then DSH's own credential store.
$keyFile = Join-Path $here 'apikey.txt'
if (-not $env:DSHPET_KEY -and (Test-Path $keyFile)) {
    $k = (Get-Content $keyFile -Raw).Trim()
    if ($k) { $env:DSHPET_KEY = $k }
}
if (-not $env:DSHPET_KEY) {
    $cred = Join-Path $env:USERPROFILE '.dsh\.credentials.yaml'
    if (Test-Path $cred) {
        $m = [regex]::Match((Get-Content $cred -Raw), 'DEEPSEEK_API_KEY:\s*(\S+)')
        if ($m.Success) { $env:DSHPET_KEY = $m.Groups[1].Value }
    }
}
if (-not $env:DSHPET_SPRITE) { $env:DSHPET_SPRITE = Join-Path $here 'sprite.png' }


try {
    [DshPet]::Run($here, $args)
} catch {
    $_ | Out-String | Set-Content (Join-Path $here 'error.log') -Encoding UTF8
    throw
}





























