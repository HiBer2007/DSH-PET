// ============================================================================
//  The original widget code, moved out of dsh_pet.ps1's Add-Type here-string.
//
//  Phase 1 was a byte-for-byte move: no logic was rewritten, so any behaviour
//  difference was a port bug rather than an intended change. It was verified by
//  diffing --simchain / --gosim / --goprobe output and by byte-comparing the
//  --shotgo renders against the PowerShell build.
//
//  Phase 2 has since pulled the parsing, credential handling and configuration
//  out into DshPet.Core (see the adapters below) and turned the \uXXXX escapes
//  back into real UTF-8 Chinese. What is left here is the window, the rendering,
//  the animation loop, the accounting and the menu - the parts that are welded
//  to WinForms and to the instance state.
//
//  It is still one large class on purpose: splitting it further is a real
//  refactor of interlocking animation state, and it should be done against the
//  DshPet.Core.Tests suite rather than in the same step as moving the project.
//
//  Source: dsh_pet.ps1 (still present, frozen, as the reference implementation)
// ============================================================================

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

namespace DshPet.App.Legacy;


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
    public const int WS_EX_APPWINDOW = 0x00040000;
    // Note the near-collision in names: WS_EX_TRANSPARENT (a style) is not HTTRANSPARENT
    // (a hit-test answer). The style takes the window out of hit-testing altogether;
    // the hit-test answer only passes the mouse to windows in the same thread.
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
    public const int VK_CONTROL = 0x11;
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    [DllImport("user32.dll")] public static extern short GetKeyState(int vKey);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // z-order: being WS_EX_TOPMOST is not enough on its own - another always-on-top
    // window created or activated later sits above this one in the same band.
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;          // the owner, for a top-level window
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int n);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    // Ask the shell whether the taskbar auto-hides, instead of guessing from
    // geometry. A hidden auto-hide bar sits *below* the screen edge (its rect
    // extends past the bottom), so "is it inside the working area" answers the
    // wrong question - it called this machine's auto-hide bar a permanent one.
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
    public const uint ABM_GETSTATE = 0x00000004;
    public const int ABS_AUTOHIDE = 0x0001;
    public const int ABS_ALWAYSONTOP = 0x0002;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_CHILD = 5;
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);
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
    public int Play() {        if (Failed) return -1;
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

    // MCI volume is per alias, so an already-open pool can be re-levelled without
    // reopening the file (which is what the settings window's volume slider does).
    public void SetVolume(int volumePercent) {
        if (Failed) return;
        try {
            for (int i = 0; i < _slots; i++)
                if (_open[i]) Send("setaudio " + _alias[i] + " volume to " + volumePercent * 10);
        } catch { }
    }

    public void Dispose() {
        try { for (int i = 0; i < _slots; i++) if (_open[i]) Send("close " + _alias[i]); } catch { }
    }
}
// ------------------------------------------------------------------ window ---

public sealed class DshPet : Form {
    const string S_LABEL   = "DSH 余额";                                    // DSH balance
    const string S_HELP    = "演示连续扣费";                // demo consecutive charges
    const string S_SIZE    = "尺寸";                                        // size
    const string S_POLL    = "刷新频率";   // refresh rate
    const string S_POS     = "位置";                    // position
    const string S_LEFT    = "左下";                    // bottom-left
    const string S_RIGHT   = "右下";                    // bottom-right
    const string S_REFRESH = "立即刷新余额";                // refresh now
    const string S_TEST    = "测试一次扣费效果";    // test one charge
    const string S_QUIT    = "退出";                                        // quit
    const string S_CUSTOM  = "自定义...";                               // custom...
    const string S_HELPT   = "演示扣费金额";
    const string S_HELPP   = "要演示扣多少钱（元）";
    const string S_SIZET   = "尺寸";
    const string S_SIZEP   = "宽高（厘米）";
    const string S_NOKEY   = "缺少 API Key";
    const string S_LOADING = "连接中...";
    const string S_KEYT    = "API Key";
    const string S_KEYP    = "粘贴 DeepSeek API Key（留空则不修改）";
    const string S_SETKEY  = "设置 API Key";
    const string S_SETTINGS = "设置…";
    const string S_TOPMOST = "立即置顶显示";
    const string S_ACCT = "账户";
    const string S_ACCT_MANAGE = "管理账户…";
    const string S_EXPR    = "表情随额度变化";                                   // faces by mood
    const string S_CLICK = "点击穿透（按住 Ctrl 可操作）";
    const string S_CAROUSEL = "GO 额度窗口轮播";

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
    const string S_SRC     = "数据源";                                  // data source
    const string S_SRCDSH  = "DeepSeek 余额";                               // DeepSeek balance
    const string S_SRCGO   = "OpenCode GO";
    const string S_GOWIN   = "GO 额度窗口";                         // GO quota window
    const string S_W5H     = "5 小时";                                      // 5 hours
    const string S_WWEEK   = "本周";                                        // this week
    const string S_WMONTH  = "本月";                                        // this month
    const string S_GOCRED  = "设置 GO 凭据";                        // set GO credentials
    const string S_GOCREDT = "OpenCode GO 凭据";                            // OpenCode GO credentials
    const string S_GOCREDP = "粘贴抓包内容（需含 auth、__Host-console_session 与 wrk_ 工作区）"; // paste: needs auth, session, wrk_
    const string S_GOPASTE = "从剪贴板读取凭据";   // read credentials from clipboard
    const string S_GOBAD   = "认不出凭据";                     // cannot parse credentials
    const string S_GONOCRED= "缺少 GO 凭据";                       // no GO credentials
    const string S_GOLABEL = "OPENCODE GO";
    const string S_GOSPENT = "额度已用完 · 重置倒计时";
    const string S_OBSMODE = "OBS 捕获模式";
    const string S_GONOLOG = "GO 日志不可用";                  // GO logs unavailable

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
    readonly DateTime?[] _goResets = new DateTime?[3];   // when each window refills, from the API

    // The "time until this refills" bubble. Shown briefly, above the character's
    // head, rather than permanently: it answers a question you ask occasionally,
    // and the tablet is already carrying the number you look at constantly.
    string _bubbleText = "";
    bool _bubbleWasUp;      // so the frame after it expires is rendered clean
    int _bubbleUntil;                  // Environment.TickCount deadline
    const int BubbleMs = 2000;      // the whole life: appear, drift, gone
    readonly List<double> _goCues = new List<double>();   // exact per-call slices
    double _goQueuedSum;               // total of the slices above, never > _pending
    readonly HashSet<string> _goSeen = new HashSet<string>();
    long _goSince;                     // request-logs cursor: newest startedAt seen
    bool _goPrimed;                    // first log fetch only seeds the cursor
    bool _goLogsBlocked;               // cookie rejected (401/403): stop asking
    bool _goLogsWarned;

    Bitmap _flat, _sprNormal, _sprRed, _canvas;

    /// <summary>
    /// Alternate faces, one per charge, loaded from expressions\ next to the exe.
    ///
    /// <para><c>_exprFlat</c> is the artwork as it came off disk (1024x1024 each),
    /// <c>_exprNormal</c> and <c>_exprRed</c> are the same faces scaled to the widget's
    /// current size plus their red-flash layers - the same two layers the base artwork
    /// has. Keeping the flat copies costs memory but is what makes a size change cheap:
    /// rescaling is a redraw, not four PNG decodes.</para>
    ///
    /// <para>The face files must share the base artwork's geometry. The tablet quad is
    /// baked in from make_sprite.ps1 and is not re-measured per file, so a face whose
    /// tablet sits somewhere else would put the numbers in the wrong place. That is
    /// checked, not assumed: --uicheck measures the black panel inside the quad on every
    /// face (99.8% of it is panel on all five images).</para>
    /// </summary>
    readonly List<Bitmap> _exprFlat = new List<Bitmap>();
    readonly List<Bitmap> _exprNormal = new List<Bitmap>();
    readonly List<Bitmap> _exprRed = new List<Bitmap>();

    bool _expressions = true;      // show the situation's face instead of the one artwork

    // The faces, by the mood they are for, as indices into _exprNormal / _exprRed.
    // Extra files for the same mood are allowed (hurt.png, hurt_2.png) and take turns.
    const int RoleCalm = 0, RoleUnhappy = 1, RoleHurt = 2;
    readonly List<int> _calmFaces = new List<int>();
    readonly List<int> _unhappyFaces = new List<int>();
    readonly List<int> _hurtFaces = new List<int>();
    int _calmTurn, _unhappyTurn, _hurtTurn;

    int _faceShown = -1;           // the face being drawn; -1 is the base artwork
    int _faceRole = -2;            // its mood, so a new face is only picked when it changes
    int _chargeUntil;              // when the "she felt that" flinch ends
    double _scale = 1.0;
    int _w, _h;
    double[] _fx, _fy;
    double _cm = 8.0;            // widget edge length in centimetres
    int _headX, _headY;

    readonly List<Hit> _hits = new List<Hit>();
    readonly List<Floater> _floaters = new List<Floater>();
    readonly System.Windows.Forms.Timer _timer, _poll, _top;
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

    // The GO carousel: which meter the tablet is *showing*, kept separate from
    // _win, which is the meter the accounting runs on.
    //
    // Rotating _win itself would mean running every rotation through SetWindow,
    // and that calls ResetAccounting - which clears the per-meter previous
    // readings the next poll derives its deduction from. Turning the carousel on
    // would then quietly stop billing whatever moved while a window was off
    // screen, i.e. the feature would eat the numbers it exists to display. So the
    // carousel moves only what is drawn.
    int _viewWin = -1;                  // -1 = follow _win (carousel off)
    bool _carousel;
    int _carouselSeconds = 5;      // seconds per window, adjustable from the settings window
    int _warnPercent = 15;         // GO: warn when a window drops below this % left
    double _warnCny = 5.0;         // DeepSeek: warn when the balance drops below this many yuan
    double CarouselSeconds { get { return Math.Max(2, _carouselSeconds); } }

    /// <summary>Which meter the tablet shows: the active one, or the carousel's turn.</summary>
    int ShownWin { get { return (_carousel && _viewWin >= 0 && _viewWin < 3) ? _viewWin : _win; } }

    static double ParseDouble(string s, double fallback) {
        double v;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
    }
    int _spritePx;          // the artwork's size, i.e. what the cm setting means
    int _spriteW, _spriteH; // the scaled artwork's actual pixel size
    int _shakeMargin;       // transparent border reserved around it for the shake
    // Extra height above the artwork, so a thought bubble can visibly leave the
    // character instead of being cut off by the window's top edge. Without it the
    // canvas ends 12px above her head and "fly out above her" is just clipping.
    int _headroom;

    /// <summary>
    /// How far the hurt shake may ever move the sprite, in pixels.
    ///
    /// Used to be "how much margin Relayout reserves around the artwork"; the margin is
    /// gone (nothing of the window may hang off the screen any more), so this is now
    /// only the clamp RenderToCanvas applies - and the measure of how far a shake can
    /// run the sprite off the canvas edge.
    /// </summary>
    static int ShakeCeiling(int spritePx) {
        double cap = Math.Min(12.0, spritePx * 0.06);
        return (int)Math.Ceiling(cap);
    }
    NotifyIcon _tray;
    ContextMenuStrip _menu;
    ToolStripMenuItem _sizeItem;
    ToolStripMenuItem _pollItem;
    ToolStripMenuItem _posItem;
    ToolStripMenuItem _srcItem;
    ToolStripMenuItem _accountItem;
    ToolStripMenuItem _clickItem;
    ToolStripMenuItem _obsItem, _exprItem;
    int _menuShownTick;                 // when the menu went up, for the outside-click poll
    ToolStripMenuItem _carouselItem;
    ToolStripMenuItem _goWinItem;
    int _demoLeft;                   // cues left in a rehearsal run
    double _demoAmount = 0.01;

    // current frame's shake offset, so the readout and the floating numbers move
    // together with the character
    double _shakeX, _shakeY;
    // hit sound: one MCI alias per concurrent cue
    SoundPool _sound;
    bool _soundEnabled = true;         // can it actually play right now
    bool _soundWanted = true;          // does the user want it
    int _volume = 80;
    bool _clickThrough;                // whole window passes clicks through unless Ctrl is held
    string _soundPath = "";
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
        _volume = int.Parse(Get("DSHPET_VOLUME", "80"));
        _mirror = Get("DSHPET_POS", "left") == "right";

        string sprite = Get("DSHPET_SPRITE", Path.Combine(baseDir, "sprite.png"));
        if (!File.Exists(sprite)) throw new FileNotFoundException("sprite not found: " + sprite);
        _flat = LoadUnlocked(sprite);
        LoadExpressionArt(Path.Combine(baseDir, "expressions"));

        // The path is built here from baseDir rather than handed over through the
        // environment: values crossing the PowerShell/C# boundary come back
        // ANSI-mangled when the folder name is not ASCII, while a path built in
        // this process stays correct.
        _soundPath = Path.Combine(baseDir, Get("DSHPET_SOUND_FILE", "hit.mp3"));

        FormBorderStyle = FormBorderStyle.None;
        // True, not false: ShowInTaskbar=false makes WinForms park the form behind a
        // hidden owner window, and capturers skip owned windows - that is why the pet
        // never appeared in OBS's window list. WS_EX_TOOLWINDOW (set below) is what
        // actually keeps it off the taskbar and out of Alt-Tab, and it does that with
        // no owner at all.
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "DSH Balance Pet";

        // state.ini overrides the environment, so the sound pool is opened after
        // ReadState rather than from the environment values alone.
        ReadState();
        ApplySound(_soundWanted, _volume);
        LoadAccounts();
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
        _tray.Icon = LoadAppIcon();
        Icon = _tray.Icon;
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

        // Z-order guard. The interval is short because the things it reacts to are
        // short: an auto-hidden taskbar takes about 200ms to slide out, and the
        // whole point is to be out of its way while it is moving. The check is a
        // walk of the z-order and a handful of rectangle tests - nothing at all
        // happens unless something is really stacked over the pet.
        _top = new System.Windows.Forms.Timer();
        _top.Interval = int.Parse(Get("DSHPET_TOP_MS", "250"));
        _top.Tick += delegate { SettleZOrder(); };
        _top.Start();

        PushLayer();
    }

    static string Get(string n, string f) {
        string v = Environment.GetEnvironmentVariable(n);
        return string.IsNullOrEmpty(v) ? f : v;
    }

    // ----------------------------------------------------- go credentials ----
    //
    // The scanning itself now lives in DshPet.Core; this is the adapter that keeps
    // the legacy out-parameter shape so the rest of this file does not care.
    static void ParseGoBlob(string text, out string auth, out string sess, out string org, out string key) {
        Core.Credentials.GoCredentialPatch patch = Core.Credentials.GoCredentialPatch.Scan(text);
        auth = patch.Auth; sess = patch.Session; org = patch.Org; key = patch.Key;
    }

    string GoCredPath { get { return Path.Combine(_baseDir, "opencode_go.txt"); } }

    // ----------------------------------------------------------- accounts ----
    //
    // Everything below used to be a single set of credentials read from
    // apikey.txt / opencode_go.txt. Those fields still exist - the rest of this
    // file does not know about accounts - but they are now filled from whichever
    // profile is active, and written back to it.

    Core.Accounts.AccountStore _accounts;

    void LoadAccounts() {
        try {
            _accounts = Core.Accounts.AccountStore.Load(
                _baseDir,
                _src == SrcGo ? Core.Accounts.AccountProfile.SourceGo : Core.Accounts.AccountProfile.SourceDeepSeek,
                delegate(string n) { return Get(n, ""); });
        } catch (Exception ex) {
            Log("accounts load failed: " + ex.Message);
            _accounts = new Core.Accounts.AccountStore();
        }
        if (_accounts.LoadWarning.Length > 0) Log("accounts: " + _accounts.LoadWarning);

        Core.Accounts.AccountProfile active = _accounts.Active;
        Log("accounts: " + _accounts.Profiles.Count + " profile(s), active=" +
            (active == null ? "(none)" : active.Name + " [" + active.Describe() + "]"));
        ApplyActiveAccount(false);
    }

    /// <summary>Copies the active profile into the fields the rest of the file uses.</summary>
    void ApplyActiveAccount(bool repoll) {
        Core.Accounts.AccountProfile p = _accounts == null ? null : _accounts.Active;
        if (p != null) {
            _src = p.IsGo ? SrcGo : SrcDsh;
            _apiKey = p.DeepSeekKey;
            _goKey = p.GoKey;
            _goAuth = p.GoAuth;
            _goSess = p.GoSession;
            _goOrg = p.GoOrg;
        }
        // a different account means a different session cookie and a different log
        _goLogsBlocked = false;
        _goLogsWarned = false;
        _goPrimed = false;
        _goSince = 0;
        _goSeen.Clear();
        if (repoll) _pollWant = 2;
    }

    void SaveAccounts() {
        if (_accounts == null) return;
        try { _accounts.Save(_baseDir); }
        catch (Exception ex) { Log("accounts save failed: " + ex.Message); }
    }

    /// <summary>Writes the live fields back into the active profile and stores it.</summary>
    void StoreActiveCredentials() {
        Core.Accounts.AccountProfile p = _accounts == null ? null : _accounts.Active;
        if (p == null) return;
        p.DeepSeekKey = _apiKey;
        p.GoKey = _goKey;
        p.GoAuth = _goAuth;
        p.GoSession = _goSess;
        p.GoOrg = _goOrg;
        SaveAccounts();
    }

    /// <summary>Switch to another profile: new credentials, clean books, immediate poll.</summary>
    public void ActivateAccount(string id) {
        if (_accounts == null) return;
        Core.Accounts.AccountProfile before = _accounts.Active;
        _accounts.SetActive(id);
        Core.Accounts.AccountProfile now = _accounts.Active;
        if (now == null) return;
        if (before != null && before.Id == now.Id) return;

        SaveAccounts();
        ApplyActiveAccount(true);
        ResetAccounting();
        SaveState();
        RenderToCanvas();
        PushLayer();
        Log("account -> " + now.Name + " (" + now.Describe() + ")");
    }

    /// <summary>One row of the settings window's account list.</summary>
    public sealed class AccountView {
        public string Id, Name, Source, Describe, GoOrg;
        public bool IsActive, IsGo, IsUsable, HasDeepSeekKey, HasGoKey, HasGoCookie;
    }

    public List<AccountView> ViewAccounts() {
        List<AccountView> list = new List<AccountView>();
        if (_accounts == null) return list;
        for (int i = 0; i < _accounts.Profiles.Count; i++)
            list.Add(ViewOf(_accounts.Profiles[i], _accounts.Active));
        return list;
    }

    public AccountView ViewAccount(string id) {
        Core.Accounts.AccountProfile p = _accounts == null ? null : _accounts.Find(id);
        return p == null ? null : ViewOf(p, _accounts.Active);
    }

    AccountView ViewOf(Core.Accounts.AccountProfile p, Core.Accounts.AccountProfile active) {
        AccountView v = new AccountView();
        v.Id = p.Id; v.Name = p.Name; v.Source = p.Source; v.Describe = p.Describe();
        v.GoOrg = p.GoOrg;
        v.IsGo = p.IsGo; v.IsUsable = p.IsUsable;
        v.HasDeepSeekKey = p.HasDeepSeekKey; v.HasGoKey = p.HasGoKey; v.HasGoCookie = p.HasGoCookie;
        v.IsActive = active != null && active.Id == p.Id;
        return v;
    }

    public string AddAccount(string name, string source) {
        if (_accounts == null) return "";
        Core.Accounts.AccountProfile p = _accounts.Add(_accounts.UniqueName(name), source);
        SaveAccounts();
        Log("account added: " + p.Name + " (" + p.Source + ")");
        return p.Id;
    }

    public void RenameAccount(string id, string name) {
        Core.Accounts.AccountProfile p = _accounts == null ? null : _accounts.Find(id);
        if (p == null || name == null || name.Trim().Length == 0) return;
        string wanted = name.Trim();
        if (wanted == p.Name) return;
        p.Name = _accounts.UniqueName(wanted);
        SaveAccounts();
        Log("account renamed: " + p.Name);
    }

    public bool DeleteAccount(string id) {
        // Never remove the last one: without a profile the widget has nothing to read.
        if (_accounts == null || _accounts.Profiles.Count <= 1) return false;
        Core.Accounts.AccountProfile target = _accounts.Find(id);
        if (target == null) return false;
        string name = target.Name;
        bool wasActive = _accounts.Active != null && _accounts.Active.Id == id;
        if (!_accounts.Remove(id)) return false;
        SaveAccounts();
        if (wasActive) {
            ApplyActiveAccount(true);
            ResetAccounting();
            SaveState();
            RenderToCanvas();
            PushLayer();
        }
        Log("account removed: " + name);
        return true;
    }

    public void SetAccountSource(string id, string source) {
        Core.Accounts.AccountProfile p = _accounts == null ? null : _accounts.Find(id);
        if (p == null) return;
        string wanted = source == Core.Accounts.AccountProfile.SourceGo
                      ? Core.Accounts.AccountProfile.SourceGo
                      : Core.Accounts.AccountProfile.SourceDeepSeek;
        if (p.Source == wanted) return;
        p.Source = wanted;
        SaveAccounts();
        Core.Accounts.AccountProfile active = _accounts.Active;
        if (active != null && active.Id == id) {
            ApplyActiveAccount(true);
            ResetAccounting();
            SaveState();
            RenderToCanvas();
            PushLayer();
        }
        Log("account source: " + p.Name + " -> " + wanted);
    }

    /// <summary>
    /// Saves edited credentials into one profile. Blank inputs are left alone and a
    /// blob that parses to nothing is rejected rather than stored: pasting junk
    /// must never wipe credentials that work.
    /// </summary>
    public string SaveAccountCredentials(string id, string deepSeekKey, string goBlob) {
        Core.Accounts.AccountProfile p = _accounts == null ? null : _accounts.Find(id);
        if (p == null) return "账户不存在";

        bool changed = false;
        if (deepSeekKey != null && deepSeekKey.Trim().Length > 0) {
            p.DeepSeekKey = deepSeekKey.Trim();
            changed = true;
        }
        if (goBlob != null && goBlob.Trim().Length > 0) {
            Core.Credentials.GoCredentialPatch patch = Core.Credentials.GoCredentialPatch.Scan(goBlob);
            if (!patch.AnyFound) return "这段内容里没有认得出的凭据";
            if (patch.Key != null) p.GoKey = patch.Key;
            if (patch.Auth != null) p.GoAuth = patch.Auth;
            if (patch.Session != null) p.GoSession = patch.Session;
            if (patch.Org != null) p.GoOrg = patch.Org;
            changed = true;
        }
        if (!changed) return "没有内容，未做修改";

        SaveAccounts();
        Core.Accounts.AccountProfile active = _accounts.Active;
        if (active != null && active.Id == id) {
            ApplyActiveAccount(true);
            RenderToCanvas();
            PushLayer();
        }
        Log("account credentials updated: " + p.Name + " [" + p.Describe() + "]");
        return p.Describe();
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
        return w == WinWeek ? "周" : (w == WinMonth ? "月" : "5H");
    }
    static string WinJson(int w) {
        return w == WinWeek ? "week" : (w == WinMonth ? "month" : "fiveHour");
    }

    // Remaining allowance of one meter, in dollars, exactly as the API reports it.
    // The API reports micro-cents, and there are 100,000,000 of those per dollar.
    // This is the accounting's own reading - what the panel shows goes through
    // GoRemain/GoRemainPct below, which apply the tier gate.
    double GoMeterRemain(int w) {
        if (_goLim[w] <= 0) return double.NaN;
        double v = _goLim[w] - _goUsed[w];
        return v < 0 ? 0 : v;
    }
    // Percentages are the only view that survives the per-model limits: every
    // model has its own monthly allowance, so two windows' dollars are not
    // comparable while their percentages are.
    double GoMeterPct(int w) {
        if (_goLim[w] <= 0) return double.NaN;
        double v = (_goLim[w] - _goUsed[w]) / _goLim[w] * 100.0;
        if (v < 0) v = 0;
        if (v > 100) v = 100;
        return v;
    }
    double GoRemainingLive { get { return GoMeterRemain(_win); } }

    /// <summary>
    /// The higher allowance window that has run out and is therefore blocking this
    /// one, or -1 when nothing above it is empty.
    ///
    /// The three windows are nested, not separate purses: a call spends against all
    /// three at once, so a window with money left in it can still be unusable. Once
    /// the week is spent, the five hour window refilling buys nothing - the call made
    /// the moment it refills is refused. Reporting its own 30% there would tell the
    /// user they have quota when they have none, which is the one thing this panel
    /// exists to get right.
    /// </summary>
    int GoGate(int w) {
        double[] pct = new double[3];
        for (int i = 0; i < 3; i++) pct[i] = GoMeterPct(i);
        return Core.Model.MeterGates.Blocker(pct, w);
    }

    /// <summary>What the panel shows: nothing left, while a higher window is empty.</summary>
    double GoRemainPct(int w) { return GoGate(w) >= 0 ? 0.0 : GoMeterPct(w); }
    double GoRemain(int w) { return GoGate(w) >= 0 ? 0.0 : GoMeterRemain(w); }

    /// <summary>
    /// When this window becomes spendable again. Blocked, that is not its own refill
    /// but the blocking window's: the five hour window may reset in twenty minutes
    /// and still buy back nothing until the week comes round, so its countdown is the
    /// week's.
    /// </summary>
    DateTime? GoReset(int w) { int g = GoGate(w); return g >= 0 ? _goResets[g] : _goResets[w]; }

    // The tray and the window both wear the character's face, read back out of
    // the exe (csproj ApplicationIcon) so there is no second copy of the artwork
    // to keep in sync. Falls back to the generic Windows icon when the exe has
    // none - a missing icon must never take the widget down with it.
    Icon LoadAppIcon() {
        try {
            Icon fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (fromExe != null) return fromExe;
        } catch { }
        try {
            string file = Path.Combine(_baseDir, "pet.ico");
            if (File.Exists(file)) return new Icon(file);
        } catch { }
        return SystemIcons.Application;
    }

    /// <summary>
    /// A tray balloon with no body text: the message goes in the title.
    /// Windows draws a balloon as a bold title over a body line, so a one-line
    /// message came out as a heading with the same sentence repeated underneath -
    /// twice the box for the same information. Title-only is smaller and says
    /// exactly one thing.
    /// </summary>
    void Notify(string msg) {        try {
            if (_tray != null) {
                _tray.BalloonTipTitle = msg;
                _tray.BalloonTipText = "";
                _tray.BalloonTipIcon = ToolTipIcon.None;
                _tray.ShowBalloonTip(4000);
            }
        } catch { }
        Log("notify: " + msg);
    }

    // Opens the settings window. ShowDialog(this) is enough for modality: the pet
    // itself never takes focus (WS_EX_NOACTIVATE) but the dialog is an ordinary
    // window and does, exactly like the InputDialog prompts already in use.
    void ShowSettings() {
        try {
            // global:: because inside this class the name DshPet is the form, not
            // the root namespace, so a plain DshPet.App would not resolve.
            using (global::DshPet.App.SettingsForm d = new global::DshPet.App.SettingsForm(this)) {
                d.ShowDialog(this);
            }
            _pollWant = 2;                      // credentials or the source may have changed
        } catch (Exception ex) {
            Log("settings window failed: " + ex);
        }
    }

    // ------------------------------------------------------------ z-order ----
    //
    // A TopMost window is only topmost *within its band*: any other always-on-top
    // window that was created or activated later sits above it. A maximised
    // terminal or a video player is enough to hide the pet completely - the
    // account keeps ticking, the sounds keep playing, and the screen looks empty.

    /// <summary>
    /// Puts the widget back on screen: re-snaps it to its corner, re-renders,
    /// re-pushes the layered content and re-asserts the top of the z-order.
    /// Wired to the right-click menu so it is one click away when the pet has
    /// been buried.
    /// </summary>
    public void BringToFrontNow() {
        try {
            SnapToCorner(true);
            if (!Visible) Visible = true;
            _dirty = true;
            RenderToCanvas();
            PushLayer();
            _yieldedTo = IntPtr.Zero;
            _raisedFor = IntPtr.Zero;
            RaiseToTop();
            Log("brought to front: " + DescribeWindow());
            // Then hand over to the normal settle pass, so this cannot be used to
            // park the pet on top of the Start menu or a taskbar sliding out.
            SettleZOrder();
        } catch (Exception ex) {
            Log("bring to front failed: " + ex.Message);
        }
    }

    /// <summary>Where the widget is and what is stacked directly above it.</summary>
    string DescribeWindow() {
        return "at=" + Left + "," + Top + " size=" + _w + "x" + _h +
               " visible=" + Visible + " aboveUs=" + WindowAbove();
    }

    string WindowAbove() {
        return DescribeWindow(Native.GetWindow(Handle, Native.GW_HWNDPREV));
    }

    string DescribeWindow(IntPtr h) {
        if (h == IntPtr.Zero) return "(nothing)";
        StringBuilder cls = new StringBuilder(256);
        Native.GetClassNameW(h, cls, cls.Capacity);
        StringBuilder txt = new StringBuilder(200);
        Native.GetWindowTextW(h, txt, txt.Capacity);
        string title = txt.ToString();
        if (title.Length > 60) title = title.Substring(0, 60) + "…";
        return cls + " \"" + title + "\"";
    }

    // Shell UI that outranks the pet whenever it is on screen: the taskbar and
    // tray, the tray overflow flyout, and on Windows 11 the Start menu, search,
    // task view and the notification centre (all hosted in one XAML island
    // window). Matched by window class rather than by process, because
    // explorer.exe also owns ordinary File Explorer windows and the pet is meant
    // to stay above those.
    static readonly string[] ShellUiClasses = new string[] {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",                   // taskbars
        "NotifyIconOverflowWindow",                                  // Win10 tray overflow
        "TopLevelWindowForOverflowXamlIsland",                       // Win11 tray overflow
        "XamlExplorerHostIslandWindow",                              // Win11 Start / search / task view / notifications
        "Windows.UI.Core.CoreWindow",                                // Win10 Start / search / task view
        "SearchHost", "MultitaskingViewFrame", "TaskListThumbnailWnd",
        "Shell_InputSwitchTopLevelWindow",
    };

    static bool IsShellUi(string cls) {
        for (int i = 0; i < ShellUiClasses.Length; i++)
            if (string.Equals(cls, ShellUiClasses[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool IsTaskbar(string cls) {
        return string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the taskbar reserves a strip of the screen for itself.
    ///
    /// Geometric on purpose. The obvious call - SHAppBarMessage(ABM_GETSTATE) and
    /// its ABS_AUTOHIDE bit - is unreliable here: in the running widget the first
    /// call returned 1 and later calls returned 0 for the very same question
    /// (the log read "autoHide=False [raw=1]", which is the contradiction that
    /// exposed it). That silently reclassified an auto-hide taskbar as a permanent
    /// one, and the pet stopped getting out of its way.
    ///
    /// The working area, by contrast, is a fact: an auto-hide bar reserves nothing,
    /// so the working area is the whole screen; a permanent one takes its strip out
    /// of it. That is also exactly the question that matters - whether the pet's
    /// corner can land on the bar at all.
    /// </summary>
    static bool TaskbarReservesSpace() {
        try {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            return work.Width < bounds.Width || work.Height < bounds.Height;
        } catch {
            return true;        // assume permanent: keep the pet on top
        }
    }

    /// <summary>
    /// Kept for the log only: the shell's own answer next to the geometric one, so
    /// the next time they disagree it is visible rather than mysterious.
    /// </summary>
    static int RawAppBarState() {
        try {
            Native.APPBARDATA data = new Native.APPBARDATA();
            data.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
            return (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref data);
        } catch { return -999; }
    }

    /// <summary>
    /// A window only counts while it is really on screen. An auto-hidden taskbar
    /// keeps WS_VISIBLE and a 2560x48 rect whose top 46 pixels are below the
    /// display: treating that as "covering the pet" would make the pet sit
    /// permanently behind the taskbar for no visible reason.
    /// </summary>
    static bool OnScreen(Native.RECT r) {
        try {
            Rectangle box = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            Rectangle screen = Screen.FromRectangle(box).Bounds;
            Rectangle clipped = Rectangle.Intersect(box, screen);
            return clipped.Width >= 4 && clipped.Height >= 4;
        } catch { return false; }
    }

    /// <summary>
    /// The artwork's rectangle in screen coordinates - not the window's.
    ///
    /// The two are the same size now, but not the same rectangle: the window carries the
    /// bubble headroom above the artwork, and "does the pet overlap X" must be asked
    /// about the pixels that are actually drawn. Using the window rect made the pet yield
    /// to a taskbar it was merely *near*: its invisible headroom brushed the taskbar
    /// strip, the shell-UI rule fired, and the pet parked itself underneath forever.
    /// </summary>
    /// <summary>
    /// True when a DSH Balance Pet window other than this one is on the desktop. Used
    /// to skip the live z-order checks rather than fail them: z-order is global, so a
    /// second instance above this one looks exactly like this one failing to raise.
    /// </summary>
    bool AnotherPetWindowExists() {
        bool found = false;
        try {
            Native.EnumWindows(delegate(IntPtr h, IntPtr p) {
                if (h == Handle) return true;
                StringBuilder sb = new StringBuilder(128);
                Native.GetWindowTextW(h, sb, sb.Capacity);
                if (sb.ToString() == "DSH Balance Pet") { found = true; return false; }
                return true;
            }, IntPtr.Zero);
        } catch { }
        return found;
    }

    Rectangle ArtOnScreen() {
        try {
            int w = _spriteW > 0 ? _spriteW : _spritePx;
            int h = _spriteH > 0 ? _spriteH : _spritePx;
            return new Rectangle(PointToScreen(new Point(_shakeMargin, _shakeMargin + _headroom)), new Size(w, h));
        } catch {
            Native.RECT r;
            if (Native.GetWindowRect(Handle, out r))
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return Rectangle.Empty;
        }
    }

    /// <summary>
    /// Finds the *lowest* piece of on-screen shell UI that overlaps the pet,
    /// searching the whole z-order rather than just upwards.
    ///
    /// Both halves of that sentence are scars. Upwards-only missed the common
    /// case: an auto-hidden taskbar slides out without raising itself above other
    /// always-on-top windows, so the pet is usually *on top of* the taskbar, and a
    /// search that only looks up finds nothing and leaves it there.
    ///
    /// Lowest, not highest, because the shell stacks: the tray overflow flyout
    /// floats above the taskbar. Dropping the pet below the flyout alone can still
    /// leave it sitting on the taskbar underneath - which is exactly the report
    /// that came back ("with the tray open it still covers the taskbar"). Sinking
    /// it below the bottom-most overlapping shell window clears the whole stack at
    /// once.
    /// </summary>
    void ScanShellUi(out IntPtr shellUi, out string who) {
        shellUi = IntPtr.Zero;
        who = "";
        try {
            if (Handle == IntPtr.Zero || !Visible) return;
            Native.RECT me;
            if (!Native.GetWindowRect(Handle, out me)) return;

            IntPtr cur = Native.GetTopWindow(IntPtr.Zero);
            if (cur == IntPtr.Zero) cur = Native.GetWindow(Native.GetDesktopWindow(), Native.GW_CHILD);
            for (int guard = 0; cur != IntPtr.Zero && guard < 400; guard++) {
                // Rectangle tests before the class-name fetch: most windows fail
                // this one, and GetClassNameW is a cross-process read.
                if (cur != Handle && Native.IsWindowVisible(cur)) {
                    Native.RECT r;
                    if (Native.GetWindowRect(cur, out r) && OnScreen(r) &&
                        r.Left < me.Right && r.Right > me.Left &&
                        r.Top < me.Bottom && r.Bottom > me.Top) {
                        StringBuilder cls = new StringBuilder(256);
                        Native.GetClassNameW(cur, cls, cls.Capacity);
                        if (IsShellUi(cls.ToString())) {
                            shellUi = cur;              // keep going: we want the last one
                            who = DescribeWindow(cur);
                        }
                    }
                }
                cur = Native.GetWindow(cur, Native.GW_HWNDNEXT);
            }
        } catch { }
    }

    /// <summary>
    /// Diagnostic companion to <see cref="ScanShellUi"/>: counts every window it
    /// looked at and every one it considered a candidate, including the ones that
    /// failed the on-screen or overlap tests. "It did not fire" is not a useful
    /// bug report; "it saw 96 windows, 3 overlapped, 0 matched the class list" is.
    /// </summary>
    void CountShellCandidates(out int scanned, out int candidates) {
        scanned = 0;
        candidates = 0;
        try {
            Native.RECT me;
            if (Handle == IntPtr.Zero || !Native.GetWindowRect(Handle, out me)) return;
            IntPtr cur = Native.GetTopWindow(IntPtr.Zero);
            if (cur == IntPtr.Zero) cur = Native.GetWindow(Native.GetDesktopWindow(), Native.GW_CHILD);
            for (int guard = 0; cur != IntPtr.Zero && guard < 400; guard++) {
                scanned++;
                if (cur != Handle && Native.IsWindowVisible(cur)) {
                    Native.RECT r;
                    if (Native.GetWindowRect(cur, out r) && OnScreen(r) &&
                        r.Left < me.Right && r.Right > me.Left &&
                        r.Top < me.Bottom && r.Bottom > me.Top) {
                        StringBuilder cls = new StringBuilder(256);
                        Native.GetClassNameW(cur, cls, cls.Capacity);
                        if (IsShellUi(cls.ToString())) candidates++;
                    }
                }
                cur = Native.GetWindow(cur, Native.GW_HWNDNEXT);
            }
        } catch { }
    }

    /// <summary>Rectangle-vs-RECT overlap in screen coordinates.</summary>
    static bool Overlaps(Native.RECT r, Rectangle box) {
        return r.Left < box.Right && r.Right > box.Left && r.Top < box.Bottom && r.Bottom > box.Top;
    }

    /// <summary>Whether the pet currently sits above <paramref name="other"/> in the z-order.</summary>
    bool IsAboveWindow(IntPtr other) {
        IntPtr cur = Native.GetWindow(Handle, Native.GW_HWNDPREV);
        for (int guard = 0; cur != IntPtr.Zero && guard < 400; guard++) {
            if (cur == other) return true;
            cur = Native.GetWindow(cur, Native.GW_HWNDPREV);
        }
        return false;
    }

    /// <summary>
    /// Walks the z-order above the pet and reports what is stacked there: the
    /// first visible window that overlaps it, and the first piece of shell UI
    /// that does. Invisible windows are skipped rather than treated as the end of
    /// the search - on a real desktop the window immediately above the pet is
    /// almost always an invisible 0x0 helper (an IME window, a tooltip class),
    /// and stopping at the first neighbour left this guard permanently convinced
    /// that nothing was ever in the way.
    /// </summary>
    void ScanStack(out IntPtr covering, out IntPtr shellUi, out string coveredBy) {
        covering = IntPtr.Zero;
        shellUi = IntPtr.Zero;
        coveredBy = "";
        try {
            if (Handle == IntPtr.Zero || !Visible) return;
            Native.RECT me;
            if (!Native.GetWindowRect(Handle, out me)) return;

            IntPtr cur = Native.GetWindow(Handle, Native.GW_HWNDPREV);
            for (int guard = 0; cur != IntPtr.Zero && guard < 500; guard++) {
                if (Native.IsWindowVisible(cur)) {
                    Native.RECT r;
                    if (Native.GetWindowRect(cur, out r) && OnScreen(r) &&
                        r.Left < me.Right && r.Right > me.Left &&
                        r.Top < me.Bottom && r.Bottom > me.Top) {
                        if (covering == IntPtr.Zero) {
                            covering = cur;
                            coveredBy = DescribeWindow(cur);
                        }
                        StringBuilder cls = new StringBuilder(256);
                        Native.GetClassNameW(cur, cls, cls.Capacity);
                        if (shellUi == IntPtr.Zero && IsShellUi(cls.ToString())) shellUi = cur;
                    }
                }
                cur = Native.GetWindow(cur, Native.GW_HWNDPREV);
            }
        } catch { }
    }

    bool IsCovered() {
        IntPtr covering, shell;
        string who;
        ScanStack(out covering, out shell, out who);
        _coveredBy = who;
        return covering != IntPtr.Zero;
    }
    string _coveredBy = "";
    IntPtr _yieldedTo = IntPtr.Zero;      // shell window the pet is currently sitting under
    IntPtr _loggedShell = (IntPtr)(-1);   // last shell window reported to the log
    IntPtr _raisedFor = IntPtr.Zero;      // window the pet last tried to rise above

    /// <summary>Puts the pet back on top of the always-on-top band.</summary>
    void RaiseToTop() {
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.BringWindowToTop(Handle);        // SetWindowPos alone does not raise within the band
        PushLayer();                            // the layered surface can have been dropped too
    }

    /// <summary>
    /// Decides, every tick, whether the pet should be in front of or behind what
    /// is stacked over it.
    ///
    /// Shell UI wins whenever it is on screen: the taskbar sliding out, the Start
    /// menu, search, the tray flyout. A desktop pet that covers the taskbar is
    /// covering the one strip of the screen the user needs to click, and no amount
    /// of "but I am always-on-top" makes that acceptable.
    ///
    /// The one exception is a taskbar that does *not* auto-hide. That bar reserves
    /// its own space and the pet snaps to the working-area corner, so the two
    /// normally never overlap at all - and if they somehow do, the pet belongs on
    /// top, because a permanent taskbar is part of the furniture rather than
    /// something the user just summoned.
    ///
    /// Everything else that covers the pet still gets the old treatment: rise
    /// above it, but only once per distinct window, so a window that keeps winning
    /// cannot turn this into a z-order war fought four times a second.
    /// </summary>
    void SettleZOrder() {
        try {
            if (_drag || _snapping || !Visible || Handle == IntPtr.Zero) return;

            IntPtr covering, aboveShell;
            string who;
            ScanStack(out covering, out aboveShell, out who);

            // Shell UI first, and from the full z-order: the pet is usually on top
            // of it, not underneath.
            IntPtr shell;
            string shellName;
            ScanShellUi(out shell, out shellName);

            // Change-gated so it cannot flood the log, but enough to answer "why
            // did it do that" after the fact - which is the only way to debug a
            // decision that depends on the whole desktop's z-order.
            if (shell != _loggedShell) {
                _loggedShell = shell;
                int seen = 0, cand = 0;
                CountShellCandidates(out seen, out cand);
                Log("zorder: overlapping shell windows=" + cand + " (scanned " + seen + "), lowest=" +
                    (shell == IntPtr.Zero ? "(none)" : shellName) +
                    ", petIsAbove=" + (shell != IntPtr.Zero && IsAboveWindow(shell)) +
                    ", reservesSpace=" + TaskbarReservesSpace() + " shellSays=" + RawAppBarState());
            }

            if (shell != IntPtr.Zero) {
                StringBuilder cls = new StringBuilder(256);
                Native.GetClassNameW(shell, cls, cls.Capacity);
                bool yield = !IsTaskbar(cls.ToString()) || !TaskbarReservesSpace();
                if (yield) {
                    // Only move when the pet is actually above it: that is the
                    // condition that means "occluding", and testing it beats
                    // assuming, because the shell reshuffles its own windows.
                    if (IsAboveWindow(shell)) {
                        // Inserting *after* the shell window keeps the pet topmost
                        // (those windows are topmost too) while dropping it below
                        // the one that needs to be visible.
                        Native.SetWindowPos(Handle, shell, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    }
                    if (_yieldedTo != shell) {
                        _yieldedTo = shell;
                        _raisedFor = IntPtr.Zero;
                        Log("stepped aside for " + shellName);
                    }
                    return;
                }
            }

            if (_yieldedTo != IntPtr.Zero) {
                _yieldedTo = IntPtr.Zero;
                _raisedFor = covering;
                RaiseToTop();
                Log("back in front (shell UI is gone)");
                return;
            }

            if (covering == IntPtr.Zero) {
                _raisedFor = IntPtr.Zero;
                return;
            }
            if (covering == _raisedFor) return;         // tried already; it keeps winning
            _raisedFor = covering;
            RaiseToTop();
            Log("re-asserted topmost (was covered by " + who + ")");
        } catch { }
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
        string summary = ApplyGoCredentialBlob(blob);
        if (summary == null) {
            Log("go creds: nothing recognised in " + blob.Length + " chars");
            Notify(S_GOBAD);
            return;
        }
        Log("go creds updated: " + summary);
        Notify((GoReady ? "凭据已保存：" : S_GOBAD + "：") + summary);
    }

    /// <summary>
    /// Scans a pasted blob for credentials and stores whatever it found. Returns
    /// a summary such as "auth=✓ 会话=✓ 工作区=wrk_... key=✓", or null when the
    /// text held nothing recognisable - in which case nothing is changed.
    /// Shared by the right-click menu and the settings window.
    /// </summary>
    public string ApplyGoCredentialBlob(string blob) {
        string a, s, o, k;
        ParseGoBlob(blob, out a, out s, out o, out k);
        if (a == null && s == null && o == null && k == null) return null;

        if (k != null) _goKey = k;
        if (a != null) _goAuth = a;
        if (s != null) _goSess = s;
        if (o != null) _goOrg = o;
        _goLogsBlocked = false;
        _goLogsWarned = false;
        _goPrimed = false;
        _goSince = 0;
        _goSeen.Clear();
        SaveAccounts();
        StoreActiveCredentials();
        _pollWant = 2;
        return "auth=" + (a != null ? "✓" : "✗") +
               " 会话=" + (s != null ? "✓" : "✗") +
               " 工作区=" + (o != null ? o : "✗") +
               " key=" + (k != null || GoHasKey ? "✓" : "✗");
    }

    void BuildMenu() {
        _menu = new ContextMenuStrip();

        // first item on purpose: if the pet ever ends up buried under another
        // always-on-top window, this is the one thing that gets it back
        ToolStripMenuItem topmost = new ToolStripMenuItem(S_TOPMOST);
        topmost.Click += delegate { BringToFrontNow(); };
        _menu.Items.Add(topmost);
        _menu.Items.Add(new ToolStripSeparator());

        // Account switching. This replaced the old "data source" submenu: with
        // several accounts, the source is a property *of* an account (a DeepSeek
        // key and a GO subscription are different things with different numbers),
        // so listing accounts answers "which one am I looking at" and "what am I
        // reading" in one place instead of two controls that could disagree.
        // The entries themselves are rebuilt on every open - see RefreshAccountMenu.
        _accountItem = new ToolStripMenuItem(S_ACCT);
        _menu.Items.Add(_accountItem);

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
                                                         "  (" + CueCount(v) + " 次)");
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

        // Same deal as Rainmeter: a switch that makes the window part of
        // the desktop, with Ctrl as the escape hatch so it stays reachable.
        _carouselItem = new ToolStripMenuItem(S_CAROUSEL);
        _carouselItem.Click += delegate { SetCarousel(!_carousel); };
        _carouselItem.ToolTipText = "每 " + CarouselSeconds + " 秒换一个额度窗口；额度快用完的那个会标红";
        _menu.Items.Add(_carouselItem);

        _clickItem = new ToolStripMenuItem(S_CLICK);
        _clickItem.Click += delegate { SetClickThrough(!_clickThrough); };
        _menu.Items.Add(_clickItem);

        // A different face on every charge, from expressions\ next to the exe. Silent
        // when that folder is absent - there is nothing to switch to.
        _exprItem = new ToolStripMenuItem(S_EXPR);
        _exprItem.Click += delegate { SetExpressions(!_expressions); };
        _exprItem.ToolTipText = "额度充裕微笑、扣血那一下和额度用完难受、低于提醒线不高兴、还没数据时平静；表情放 expressions\\，名字决定用途（calm / unhappy / hurt）";
        _menu.Items.Add(_exprItem);

        // Capture mode: one switch. It does not change how the pet is drawn - only
        // whether the window presents itself as an ordinary application window, which is
        // what a capturer needs in order to list it and read its alpha.
        _obsItem = new ToolStripMenuItem(S_OBSMODE);
        _obsItem.Click += delegate { SetObsMode(!_obsMode); };
        _obsItem.ToolTipText = "让窗口变成普通应用窗口，OBS/录屏才抓得到（保持逐像素透明，不用抠色）；开着时会出现在任务栏和 Alt+Tab 里";
        _menu.Items.Add(_obsItem);

        _menu.Items.Add(new ToolStripSeparator());

        // One window for everything that used to be scattered across the menu and
        // the environment; the menu keeps its quick adjustments.
        ToolStripMenuItem settings = new ToolStripMenuItem(S_SETTINGS);
        settings.Click += delegate { ShowSettings(); };
        _menu.Items.Add(settings);
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
        // Remember when it went up: the press that opened it is still down for a moment,
        // and that press must not be mistaken for a click somewhere else.
        _menu.Opened += delegate { _menuShownTick = Environment.TickCount; };
    }

    static string Yuan(double v) {
        return "-" + v.ToString("0.##", CultureInfo.InvariantCulture) + " ¥";
    }
    static string CueCount(double amount) {
        int n = (int)Math.Round(amount / StepYuan);
        return n < 1 ? "1" : n.ToString(CultureInfo.InvariantCulture);
    }
    static string CmLabel(double v) {
        return v.ToString("0.#", CultureInfo.InvariantCulture) + " cm";
    }
    static string SecLabel(double v) {
        return v.ToString("0.#", CultureInfo.InvariantCulture) + " 秒";
    }

    /// <summary>
    /// Rebuilds the account list every time the menu opens: accounts are added,
    /// renamed and removed from the settings window while the widget keeps
    /// running, so a list built once at start-up would go stale immediately.
    /// </summary>
    void RefreshAccountMenu() {
        if (_accountItem == null) return;
        _accountItem.DropDownItems.Clear();

        Core.Accounts.AccountProfile active = _accounts == null ? null : _accounts.Active;
        if (_accounts != null) {
            for (int i = 0; i < _accounts.Profiles.Count; i++) {
                Core.Accounts.AccountProfile profile = _accounts.Profiles[i];
                ToolStripMenuItem it = new ToolStripMenuItem(
                    profile.Name + (profile.IsUsable ? "" : "   (未配置)"));
                it.Checked = active != null && active.Id == profile.Id;
                it.ToolTipText = profile.Describe();
                it.Click += delegate { ActivateAccount(profile.Id); };
                _accountItem.DropDownItems.Add(it);
            }
            if (_accounts.Profiles.Count > 0) _accountItem.DropDownItems.Add(new ToolStripSeparator());
        }

        ToolStripMenuItem manage = new ToolStripMenuItem(S_ACCT_MANAGE);
        manage.Click += delegate { ShowSettings(); };
        _accountItem.DropDownItems.Add(manage);
    }

    // ToolStrip check marks are reset on every open, so push them here.
    void RefreshMenuChecks() {
        string curSize = CmLabel(_cm);
        string curPoll = SecLabel(_pollMs / 1000.0);
        RefreshAccountMenu();
        foreach (ToolStripItem it in _goWinItem.DropDownItems) {
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi != null) mi.Checked = (mi.Text == WinName(_win));
        }
        // the window choice only means something for the GO source
        _goWinItem.Enabled = (_src == SrcGo);
        if (_clickItem != null) _clickItem.Checked = _clickThrough;
        if (_exprItem != null) _exprItem.Checked = _expressions;
        if (_obsItem != null) _obsItem.Checked = _obsMode;
        if (_carouselItem != null) {
            _carouselItem.Checked = _carousel;
            _carouselItem.Enabled = (_src == SrcGo);   // only GO has windows to rotate
        }
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
        using (InputDialog d = new InputDialog(S_HELPT, S_HELPP, "¥", "0.1")) {
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
    //
    // Since accounts arrived this is no longer a menu item - it changes which of
    // the *active account's* credentials are being displayed, and the account
    // list is what switches identity.
    void SetSource(int s) {
        if (_src == s) return;
        _src = s;
        Core.Accounts.AccountProfile p = _accounts == null ? null : _accounts.Active;
        if (p != null) {
            p.Source = (s == SrcGo) ? Core.Accounts.AccountProfile.SourceGo
                                    : Core.Accounts.AccountProfile.SourceDeepSeek;
            SaveAccounts();
        }
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

    /// <summary>
    /// The full answer, for a double-click: when each window refills, one per line.
    /// Deliberately no amounts or percentages - "when does it come back" is the
    /// question this answers, and the board already carries how much is left.
    /// </summary>
    void PopResetBubble(int w) {
        if (_src != SrcGo) return;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 3; i++) {
            int gate = GoGate(i);
            string reset = ResetText(Core.Model.GoMeter.FormatReset(GoReset(i), DateTime.UtcNow));
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(WinName(i)).Append("额度：").Append(reset.Length > 0 ? reset : "等待读数");
            // A blocked window's countdown is the blocker's, so name the blocker.
            // Without it two lines carry the same time, which reads as a bug, and the
            // five hour line saying "2 天" on its own reads as a five hour window that
            // has somehow grown two days.
            if (gate >= 0) sb.Append("（受").Append(WinName(gate)).Append("额度限制）");
        }
        PopBubble(sb.ToString());
    }

    /// <summary>Shows the bubble with whatever is worth saying right now.</summary>
    void PopBubble(string text) {
        if (text == null || text.Length == 0) return;
        _bubbleText = text;
        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs);
        _dirty = true;
        RenderToCanvas();
        PushLayer();
    }

    /// <summary>True when the meter on screen has nothing left to spend.</summary>
    bool IsShownMeterSpent() {
        if (_src != SrcGo) return false;
        double pct = GoRemainPct(ShownWin);
        return !double.IsNaN(pct) && pct <= 0;
    }

    bool BubbleVisible() {        return _bubbleText.Length > 0 && unchecked(Environment.TickCount - _bubbleUntil) < 0;
    }

    /// <summary>
    /// The reminder rules, run every tick (cheap: it compares three levels).
    ///
    /// Edge-triggered off <see cref="Core.Model.MeterLevels"/>: a bubble fires when a
    /// meter *changes* level, never while it sits at one. That is the whole
    /// difference between a reminder and nagging - and it is why an exhausted window
    /// goes quiet after announcing itself once: from then on the tablet carries its
    /// countdown instead.
    /// </summary>
    void CheckAlerts() {
        try {
            if (_src == SrcGo) {
                for (int w = 0; w < 3; w++) {
                    Core.Model.MeterLevel now = Core.Model.MeterLevels.For(GoRemainPct(w), _warnPercent);
                    Core.Model.MeterLevel was = _goLevel[w];
                    _goLevel[w] = now;
                    string reset = Core.Model.GoMeter.FormatReset(GoReset(w), DateTime.UtcNow);
                    string msg = Core.Model.MeterLevels.MessageFor(was, now, WinName(w) + "额度",
                                        ResetText(reset), "注意额度");
                    if (msg != null) {
                        Log("alert: " + msg + "  (left=" + Fmt(GoRemain(w), 4) +
                            ", warn at or below " + _warnPercent + "%)");
                        PopBubble(msg);
                    }
                }
            } else {
                Core.Model.MeterLevel now = Core.Model.MeterLevels.For(_realBal, _warnCny);
                Core.Model.MeterLevel was = _dshLevel;
                _dshLevel = now;
                string msg = Core.Model.MeterLevels.MessageFor(was, now, "DeepSeek 余额",
                                    "", "需要充值", "已充值");
                if (msg != null) {
                    Log("alert: " + msg + "  (balance=" + Fmt(_realBal, 2) +
                        ", warn at or below ¥" + Fmt(_warnCny, 2) + ")");
                    PopBubble(msg);
                }
            }
        } catch (Exception ex) { Log("alerts: " + ex.Message); }
    }

    /// <summary>
    /// "2 小时 42 分后重置", or empty when there is no countdown to give. The formatter
    /// returns prose for the two cases that are not a duration ("已重置", "未开始计时"),
    /// and gluing "后重置" onto those produced "已重置后重置".
    /// </summary>
    static string ResetText(string formatted) {
        if (formatted.Length == 0 || formatted == "已重置" || formatted == "未开始计时") return "";
        return formatted + "后重置";
    }

    /// <summary>Double-clicking the pet asks it the obvious question.</summary>
    void SummonBubble() {
        if (_src == SrcGo) PopResetBubble(ShownWin);
        else PopBubble(double.IsNaN(_realBal) ? "余额还没读到" : "余额 ¥" + Fmt(_realBal, 2));
    }

    readonly Core.Model.MeterLevel[] _goLevel = new Core.Model.MeterLevel[3];
    Core.Model.MeterLevel _dshLevel;

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
        using (InputDialog d = new InputDialog(S_POLL, "间隔（秒，最小 1）", "秒",
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

    /// <summary>
    /// Loads every PNG in <paramref name="dir"/> as an alternate face, in file-name
    /// order - which is why the files are numbered. Missing folder, unreadable file or
    /// an empty folder all leave the pet with the single face it always had; nothing
    /// here is allowed to stop the widget from starting.
    /// </summary>
    /// <summary>
    /// Which mood a file is for, from its name: calm / unhappy / hurt, with anything after
    /// an underscore treated as another face for the same mood (hurt.png, hurt_2.png).
    /// Returns -1 for a name that means nothing here, which is a log line rather than a
    /// guess - the faces have meanings, and a face shown at the wrong moment is worse
    /// than no extra face at all.
    /// </summary>
    static int RoleOf(string path) {
        string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        int cut = n.IndexOf('_');
        if (cut > 0) n = n.Substring(0, cut);
        if (n == "calm") return RoleCalm;
        if (n == "unhappy") return RoleUnhappy;
        if (n == "hurt") return RoleHurt;
        return -1;
    }

    /// <summary>
    /// Loads every PNG in <paramref name="dir"/> as a face for the mood its name says.
    /// Missing folder, unreadable file or an unrecognised name all leave the widget with
    /// fewer faces - never with no widget.
    /// </summary>
    void LoadExpressionArt(string dir) {
        try {
            if (!Directory.Exists(dir)) return;
            string[] files = Directory.GetFiles(dir, "*.png");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            int calm = 0, unhappy = 0, hurt = 0;
            foreach (string f in files) {
                int role = RoleOf(f);
                if (role < 0) {
                    Log("expression ignored, name says nothing: " + Path.GetFileName(f) +
                        " (expected calm / unhappy / hurt)");
                    continue;
                }
                try {
                    int index = _exprFlat.Count;
                    _exprFlat.Add(LoadUnlocked(f));
                    if (role == RoleCalm) { _calmFaces.Add(index); calm++; }
                    else if (role == RoleUnhappy) { _unhappyFaces.Add(index); unhappy++; }
                    else { _hurtFaces.Add(index); hurt++; }
                } catch (Exception ex) {
                    Log("expression skipped: " + Path.GetFileName(f) + " (" + ex.Message + ")");
                }
            }
            if (_exprFlat.Count > 0)
                Log("expressions: " + calm + " calm, " + unhappy + " unhappy, " + hurt + " hurt from " + dir);
        } catch (Exception ex) {
            Log("expressions unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// Reads an image without keeping the file open.
    ///
    /// <c>new Bitmap(path)</c> holds a handle on the file for as long as the bitmap lives,
    /// and GDI+ keeps it even after the last use. That is what made swapping the artwork or
    /// running any git command over this folder fail with "being used by another process"
    /// while the widget was up, so the bytes are read into memory first and the image is
    /// decoded from there.
    /// </summary>
    static Bitmap LoadUnlocked(string path) {
        byte[] bytes = File.ReadAllBytes(path);
        using (MemoryStream ms = new MemoryStream(bytes, false))
        using (Bitmap src = new Bitmap(ms)) {
            Bitmap copy = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(copy)) {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(src, 0, 0);
            }
            return copy;
        }
    }

    /// <summary>
    /// Which mood a situation calls for, as a role constant, or -1 for "the base
    /// artwork". Pure, so --uicheck can walk the whole table without a network or a meter.
    /// </summary>
    static int FaceRoleFor(Core.Model.MeterLevel level, bool charging) {
        // Being charged is being hit: that is the pained face, and it is the whole point of
        // the extra artwork. It outranks a low reading, because the hit is the event and
        // the low reading is only the mood it happens in.
        if (charging) return RoleHurt;
        // Out of quota is the same face held: the number is red and counting down.
        if (level == Core.Model.MeterLevel.Empty) return RoleHurt;
        // Below your warning line: the number on the tablet is yellow by now.
        if (level == Core.Model.MeterLevel.Low) return RoleUnhappy;
        // Nothing read yet: no opinion to have.
        if (level == Core.Model.MeterLevel.Unknown) return RoleCalm;
        return -1;                                  // Plenty: nothing to complain about
    }

    /// <summary>
    /// How much is left in whatever the tablet is showing, in the units the warning
    /// threshold for that source uses (percent for GO, yuan for DeepSeek).
    ///
    /// The *shown* window, not the active one: the face then always agrees with the number
    /// the user is looking at, including while the carousel is turning.
    /// </summary>
    Core.Model.MeterLevel ShownLevel() {
        if (_src == SrcGo) return Core.Model.MeterLevels.For(GoRemainPct(ShownWin), _warnPercent);
        return Core.Model.MeterLevels.For(_realBal, _warnCny);
    }

    /// <summary>
    /// Picks the face for the situation, if the situation changed.
    ///
    /// Called from the tick and from every charge, never from the renderer: choosing a face
    /// while drawing would walk the rotation every frame and flicker. A mood that has not
    /// changed keeps the face it already picked, so extra files for one mood (hurt.png,
    /// hurt_2.png) take turns per event instead of per frame.
    /// </summary>
    void UpdateFace() {
        int role = _expressions ? FaceRoleFor(ShownLevel(), ChargeOn) : -1;
        if (role == _faceRole) return;
        _faceRole = role;
        _faceShown = PickFace(role);
        // Logged because "the face did not change" is otherwise indistinguishable from
        // "the face was never picked": the mood comes from the meters, not from the click.
        Log("face -> " + (role < 0 ? "base artwork"
                        : role == RoleCalm ? "calm"
                        : role == RoleUnhappy ? "unhappy" : "hurt") +
            " (level=" + ShownLevel() + (ChargeOn ? ", charging" : "") + ")");
        _dirty = true;
    }

    int PickFace(int role) {
        List<int> pool = role == RoleCalm ? _calmFaces
                       : role == RoleUnhappy ? _unhappyFaces
                       : role == RoleHurt ? _hurtFaces : null;
        if (pool == null || pool.Count == 0) return -1;
        int turn = role == RoleCalm ? _calmTurn : role == RoleUnhappy ? _unhappyTurn : _hurtTurn;
        turn = (turn + 1) % pool.Count;
        if (role == RoleCalm) _calmTurn = turn;
        else if (role == RoleUnhappy) _unhappyTurn = turn;
        else _hurtTurn = turn;
        return pool[turn];
    }

    /// <summary>A charge is being shown: the flinch lasts longer than the hurt overlay
    /// itself (0.45s), because the red flash marks the hit and the face is what you
    /// notice afterwards.</summary>
    bool ChargeOn { get { return unchecked(Environment.TickCount - _chargeUntil) < 0; } }

    const int ExpressionMs = 900;

    /// <summary>Scales the artwork to the widget's current size, mirroring it if asked.</summary>
    Bitmap ScaleSprite(Bitmap flat, int sw, int sh) {
        Bitmap scaled = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(scaled)) {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(flat, new Rectangle(0, 0, sw, sh));
        }
        if (_mirror) scaled.RotateFlip(RotateFlipType.RotateNoneFlipX);
        return scaled;
    }

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

        // The window is exactly the artwork, and it has to be: a window that is larger
        // and parked against the screen edge has a strip outside the screen, and that
        // strip is never painted - a capturer reads it as raw surface, which showed up as
        // a white band down the right and bottom of every captured frame. The shake is
        // clamped to ShakeCeiling and now runs off the canvas edge instead.
        //
        // px stays "the size the user asked for" (what the cm control means), and it is
        // also the window's width; the height adds the bubble headroom above.
        _spritePx = px;
        // No margin any more, in any mode. The window used to be the artwork plus the
        // shake's ceiling, parked so that margin hung off the screen edge - and a strip
        // of window that never becomes visible is never painted either, so a capturer
        // read it as raw surface: a white band down the right and bottom of every
        // captured frame. The window is now exactly the artwork, flush in its corner.
        // The price is the shake: displacing the sprite now runs it off the canvas edge
        // instead of into the margin.
        _shakeMargin = 0;
        // Height carries extra room above the artwork for the bubble to travel into;
        // width does not need any. The artwork is anchored to the bottom of the
        // canvas, so growing the canvas upward moves nothing on screen - the pet
        // still rests in its corner and the new space is transparent and click-through.
        _headroom = (int)Math.Round(px * 0.40);
        _w = px + _shakeMargin * 2;
        _h = _w + _headroom;
        ClientSize = new Size(_w, _h);
        _scale = (double)px / _flat.Height;

        int sw = Math.Max(2, (int)Math.Round(_flat.Width * _scale));
        int sh = Math.Max(2, (int)Math.Round(_flat.Height * _scale));
        if (_sprNormal != null) _sprNormal.Dispose();
        if (_sprRed != null) _sprRed.Dispose();
        if (_canvas != null) _canvas.Dispose();

        _spriteW = sw; _spriteH = sh;
        _sprNormal = ScaleSprite(_flat, sw, sh);
        _sprRed = BuildRedLayer(_sprNormal);
        _canvas = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);

        // Same two layers for every alternate face, rebuilt here because this is where
        // the size is known. The faces are all the base artwork's dimensions, so they
        // scale to exactly the same rectangle and the tablet overlay keeps lining up.
        for (int i = 0; i < _exprNormal.Count; i++) _exprNormal[i].Dispose();
        for (int i = 0; i < _exprRed.Count; i++) _exprRed[i].Dispose();
        _exprNormal.Clear(); _exprRed.Clear();
        foreach (Bitmap face in _exprFlat) {
            Bitmap scaledFace = ScaleSprite(face, sw, sh);
            _exprNormal.Add(scaledFace);
            _exprRed.Add(BuildRedLayer(scaledFace));
        }

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
        // Sprite space -> canvas space, once, here: every consumer downstream
        // (the panel quad, the floating numbers, the shake report) works in canvas
        // coordinates, so the margin is applied in exactly one place.
        for (int i = 0; i < 4; i++) { _fx[i] += _shakeMargin; _fy[i] += _shakeMargin + _headroom; }

        _headX = (int)(sw * 0.50 + sh * 0.17) + _shakeMargin;
        if (_mirror) _headX = sw - _headX + _shakeMargin * 2;
        _headY = (int)(sh * 0.36) + _shakeMargin + _headroom;
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
        // The window is exactly the artwork, so parking the window in the corner parks
        // the pet in it. Nothing may hang off the screen: the part of a window that is
        // outside the screen is never painted, and a capturer reads that strip as raw
        // surface - which is where the white band down the captured frame's right and
        // bottom edges came from.
        Point target = _mirror
            ? new Point(wa.Right - _w, wa.Bottom - _h)
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
        // Parsing and validation live in DshPet.Core; this maps the result onto
        // the legacy fields. The environment supplies the values for keys the
        // file does not carry, so an existing v10 state.ini keeps working and a
        // GUI change is never undone by a stale variable.
        try {
            Core.Configuration.PetState st = Core.Configuration.PetState.Load(_baseDir, StateDefaults());
            _cm = st.Cm;
            _pollMs = st.PollMs;
            _mirror = st.Mirror;
            _src = st.IsGo ? SrcGo : SrcDsh;
            _win = st.Window == "week" ? WinWeek : (st.Window == "month" ? WinMonth : Win5h);
            _soundWanted = st.SoundEnabled;
            _volume = st.Volume;
            _clickThrough = st.ClickThrough;
            _carousel = st.Carousel;
            _carouselSeconds = st.CarouselSeconds;
            _warnPercent = st.WarnPercent;
            _warnCny = st.WarnCny;
            _obsMode = st.ObsMode;
            _expressions = st.Expressions;
        } catch { }
    }

    Core.Configuration.PetState StateDefaults() {
        double cm = 8.0;
        double.TryParse(Get("DSHPET_CM", "8"), NumberStyles.Float, CultureInfo.InvariantCulture, out cm);
        int poll = 2000;
        int.TryParse(Get("DSHPET_POLL_MS", "2000"), out poll);
        int vol = 80;
        int.TryParse(Get("DSHPET_VOLUME", "80"), out vol);
        return new Core.Configuration.PetState(
            cm, poll, Get("DSHPET_POS", "left") == "right",
            Core.Configuration.PetState.SourceDeepSeek, "fiveHour",
            Get("DSHPET_SOUND", "1") != "0", vol,
            Get("DSHPET_CLICK", "0") != "0",
            Get("DSHPET_CAROUSEL", "0") != "0",
            ParseDouble(Get("DSHPET_CAROUSEL_S", "5"), 5) >= 2 ? (int)ParseDouble(Get("DSHPET_CAROUSEL_S", "5"), 5) : 5,
            15, 5.0,
            Get("DSHPET_OBS", "0") != "0",
            Get("DSHPET_EXPR", "1") != "0");
    }

    // Opens, closes or re-levels the hit-sound pool. Called from the constructor
    // (after state.ini is read, so the stored volume wins over the environment)
    // and from the settings window.
    void ApplySound(bool enabled, int volume) {
        _soundWanted = enabled;
        _volume = volume < 0 ? 0 : (volume > 100 ? 100 : volume);

        if (!enabled) {
            _soundEnabled = false;
            if (_sound != null) { try { _sound.Dispose(); } catch { } _sound = null; }
            Log("sound: muted (settings)");
            return;
        }

        _soundEnabled = true;                       // a mute from a failed play is cleared too
        if (_sound != null) { _sound.SetVolume(_volume); return; }

        if (!File.Exists(_soundPath)) {
            Log("sound file missing: " + _soundPath + " (hit sound disabled)");
            return;
        }
        _sound = new SoundPool(_soundPath, 4, _volume, Path.Combine(_baseDir, "pet.log"));
        if (_sound.Failed) Log("sound pool failed: " + _sound.Error);
        else Log("sound ready: " + _soundPath + " volume=" + _volume);
    }

    void SaveState() {
        try {
            new Core.Configuration.PetState(_cm, _pollMs, _mirror,
                                            _src == SrcGo ? Core.Configuration.PetState.SourceGo
                                                          : Core.Configuration.PetState.SourceDeepSeek,
                                            WinJson(_win), _soundWanted, _volume, _clickThrough, _carousel,
                                            _carouselSeconds, _warnPercent, _warnCny,
                                            _obsMode, _expressions).Save(_baseDir);
        } catch { }
    }

    void SaveKey(string k) {
        try { File.WriteAllText(KeyPath, k, Encoding.ASCII); } catch { }
    }

    // -------------------------------------------------------------- window ---

    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE;
            // Click-through is a style here, so it has to be right at creation too.
            if (_clickThrough && !CtrlHeld()) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            // The two states are two different kinds of window, and the style bits are
            // the whole difference - the drawing is identical either way. Measured
            // against the running pet, one bit at a time:
            //
            //   capture on  - APPWINDOW, no TOOLWINDOW: an ordinary app window, which is
            //                 the only kind a capturer will list and read. OBS skips
            //                 tool windows entirely, and Chromium refuses to start a
            //                 capture on one (NotReadableError, no frame).
            //   capture off - TOOLWINDOW, no APPWINDOW: no taskbar button and no Alt-Tab
            //                 entry. APPWINDOW (which WinForms sets because
            //                 ShowInTaskbar is true) is what put the pet in Alt-Tab.
            cp.ExStyle |= Native.WS_EX_LAYERED;      // per-pixel alpha, always
            if (_obsMode) {
                cp.ExStyle |= Native.WS_EX_APPWINDOW;
                cp.ExStyle &= ~Native.WS_EX_TOOLWINDOW;
            } else {
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
            }
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        // Belt and braces: ShowInTaskbar is already true, but a hidden owner is the one
        // thing that makes the window invisible to every capturer, so it is cleared
        // again here in case a future change re-introduces one. WS_EX_TOOLWINDOW keeps
        // the window out of the taskbar and out of Alt-Tab either way.
        Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, IntPtr.Zero);
        ApplyCaptureStyle();
        ApplyClickThrough();
        _dirty = true;
        RenderToCanvas();
        PushLayer();
    }

    /// <summary>
    /// WinForms re-establishes the owner when the form is shown, after
    /// OnHandleCreated has run, so the clear is repeated once the window is up - and
    /// the capture style is applied again here because the handle is not necessarily
    /// created before state.ini has been read.
    /// </summary>
    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, IntPtr.Zero);
        ApplyClickThrough();
        ApplyCaptureStyle();
    }

    // ---------------------------------------------------------- capture mode ---

    /// <summary>
    /// Whether the window presents itself as an ordinary application window instead of
    /// the tool window it normally is. Measured one style bit at a time on the running
    /// pet, because none of this is guessable:
    ///
    ///   TOOLWINDOW, no APPWINDOW -> no capturer can see it. OBS's window list skips
    ///                              tool windows, and WinForms parks a
    ///                              ShowInTaskbar=false form behind a hidden owner
    ///                              window, which every window list skips as well.
    ///   APPWINDOW, no TOOLWINDOW -> captured with its per-pixel alpha intact, so the
    ///                              overlay needs no colour key.
    ///
    /// Nothing else changes with the switch: the renderer, the window size and the
    /// corner are the same in both states, so the pet on screen is identical.
    /// </summary>
    bool _obsMode;

    /// <summary>
    /// The two kinds of window, as style bits. Done here rather than only in
    /// CreateParams because the handle is rebuilt for other reasons too, and a style is
    /// cheaper to flip than a window.
    /// </summary>
    void ApplyCaptureStyle() {
        if (Handle == IntPtr.Zero) return;
        long ex = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
        long want = ex & ~((long)Native.WS_EX_TOOLWINDOW | (long)Native.WS_EX_APPWINDOW);
        // Layered either way: capture mode does not touch the renderer, it only makes
        // the window one a capturer will list and read - with the alpha intact.
        want |= Native.WS_EX_LAYERED;
        if (_obsMode) want |= Native.WS_EX_APPWINDOW;
        else want |= Native.WS_EX_TOOLWINDOW;
        if (want != ex) {
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)want);
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            Log("capture style: ex 0x" + ex.ToString("X8") + " -> 0x" + want.ToString("X8"));
        }
        Invalidate();
    }

    void SetObsMode(bool on) {
        if (_obsMode == on) return;
        _obsMode = on;
        SaveState();
        Log("capture mode -> " + (on ? "ordinary app window, visible to capturers"
                                     : "tool window, out of the taskbar and Alt-Tab"));
        // The window is rebuilt rather than restyled: the owner has to stay gone and the
        // bits have to be right from creation (see CreateParams). WinForms keeps the
        // position, the visibility and the click-through behaviour across a recreate.
        // Any open menu goes first - it is a separate window owned by the old handle, and
        // leaving it up strands it: nothing left can close it.
        CloseMenus();
        if (Handle != IntPtr.Zero) RecreateHandle();
        ApplyCaptureStyle();
        ApplyClickThrough();
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

            // Click-through, the same deal Rainmeter offers: with it on, the whole
            // window is a hole in the desktop, and holding Ctrl makes it solid
            // again so the pet can still be dragged and its menu still opened.
            // The hit test is re-asked on every mouse move, so pressing or
            // releasing Ctrl takes effect immediately without touching any window
            // style - which is what keeps this cheap and reversible.
            if (_clickThrough && !CtrlHeld()) {
                m.Result = (IntPtr)Native.HTTRANSPARENT;
                return;
            }

            bool solid = false;
            byte[] map = _hitMap;
            if (map != null && cp.X >= 0 && cp.Y >= 0 && cp.X < _hitW && cp.Y < _hitH)
                solid = map[cp.Y * _hitW + cp.X] > 8;
            m.Result = (IntPtr)(solid ? Native.HTCLIENT : Native.HTTRANSPARENT);
            return;
        }
        base.WndProc(ref m);
    }

    static bool CtrlHeld() {
        // GetAsyncKeyState, deliberately, not GetKeyState: this window never takes
        // focus (WS_EX_NOACTIVATE), so it never receives key messages, and the
        // per-thread key state that GetKeyState reports would say "Ctrl is up"
        // forever. The async call reads the physical key instead.
        return (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
    }

    protected override void OnDoubleClick(EventArgs e) {
        base.OnDoubleClick(e);
        SummonBubble();
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
        try { _timer.Stop(); _poll.Stop(); _top.Stop(); } catch { }
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
            // Hard ceiling: overlapping cues must not fling the sprite about, and the
            // canvas is only as big as the artwork now, so this is also how far the
            // sprite can run off the window edge during a shake.
            double shakeMax = ShakeCeiling(_spritePx);
            if (sx > shakeMax) sx = shakeMax; else if (sx < -shakeMax) sx = -shakeMax;
            if (sy > shakeMax) sy = shakeMax; else if (sy < -shakeMax) sy = -shakeMax;
            int ox = (int)Math.Round(sx), oy = (int)Math.Round(sy);
            _shakeX = ox; _shakeY = oy;      // screen text + floating numbers follow this

            // The face a charge is wearing, if one is: same geometry, so the tablet is
            // drawn over it in exactly the same place.
            bool alt = _faceShown >= 0 && _faceShown < _exprNormal.Count;
            Bitmap face = alt ? _exprNormal[_faceShown] : _sprNormal;
            Bitmap faceRed = alt ? _exprRed[_faceShown] : _sprRed;
            BlitLayer(face, p, buf.Stride, _shakeMargin + ox, _shakeMargin + _headroom + oy, 1.0);
            for (int i = 0; i < _hits.Count; i++) {
                double pulse = _hits[i].Pulse;
                if (pulse > 0.01) {
                    // The hurt overlay is toned down as the widget grows: 60% of a
                    // 113px sprite is a readable flash, 60% of a 454px one would
                    // just be a red silhouette.
                    double maxTint = Math.Max(0.30, 0.64 - _scale * 0.75);
                    BlitLayer(faceRed, p, buf.Stride, _shakeMargin + ox, _shakeMargin + _headroom + oy,
                              Math.Min(maxTint, maxTint * pulse));
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

        // the thought bubble goes on top of everything, including the numbers
        if (BubbleVisible()) {
            Buf b4 = new Buf(_canvas);
            // Deliberately *not* given the shake offset: a thought does not jolt when
            // its thinker is knocked about, and text that jitters is unreadable
            // exactly when it matters most. The bubble holds still while the body
            // moves under it.
            try { DrawBubble(b4, 0, 0); b4.Flush(); } finally { b4.Dispose(); }
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

                float fLabel = H * 0.15f;
                using (Font f = new Font("Microsoft YaHei UI", fLabel, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (Brush b = new SolidBrush(Color.FromArgb(235, 158, 182, 224))) {
                    float tagW = PeakTagWidth(g, W, H);
                    RectangleF lr = PeakTagOnLeft
                        ? new RectangleF(tagW, H * 0.04f, W - tagW, fLabel * 1.5f)
                        : new RectangleF(0, H * 0.04f, W - tagW, fLabel * 1.5f);
                    g.DrawString(S_LABEL, f, shadow, new RectangleF(lr.X + 1.5f, lr.Y + 1.5f, lr.Width, lr.Height), sf);
                    g.DrawString(S_LABEL, f, b, lr, sf);
                }
                DrawPeakTag(g, W, H);

                string txt = double.IsNaN(DrawnBalance)
                    ? "--"
                    : Core.Model.Sig.Five(DrawnBalance);
                float fBal = H * 0.48f, fCur = H * 0.27f;
                using (Font fb = new Font("Arial", fBal, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font fc = new Font("Microsoft YaHei UI", fCur, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (Brush bb = new SolidBrush(Color.FromArgb(255, 240, 246, 255)))
                using (Brush bc = new SolidBrush(Color.FromArgb(235, 158, 184, 230))) {
                    SizeF sb = g.MeasureString(txt, fb);
                    SizeF sc = g.MeasureString("¥", fc);
                    float total = sb.Width + sc.Width;
                    float left = (W - total) / 2f;
                    float top = H * 0.44f;
                    g.DrawString("¥", fc, sh, left + 1.5f, top + sb.Height * 0.24f + 1.5f);
                    g.DrawString(txt, fb, sh, left + sc.Width + 1.5f, top + 1.5f);
                    g.DrawString("¥", fc, bc, left, top + sb.Height * 0.24f);
                    g.DrawString(txt, fb, bb, left + sc.Width, top);
                }
            }
        }
        return panel;
    }

    /// <summary>
    /// The peak / off-peak tag, top-right of the tablet. Permanent, because the price
    /// of the next call is not something to go and look up elsewhere: off-peak costs
    /// half, so "is it peak right now" belongs next to the money it changes. Green for
    /// off-peak, red for peak - the colours mean cheap and expensive here, which is
    /// the opposite of how they read on the quota row, so it is drawn as a word rather
    /// than as a bare dot.
    /// </summary>
    /// <summary>
    /// A font size at which <paramref name="text"/> fits one line of
    /// <paramref name="avail"/> pixels, never below <paramref name="floor"/>.
    ///
    /// Measured with the plain <see cref="Graphics.MeasureString(string,Font)"/>
    /// overload, not the typographic one, because that is what the default
    /// <see cref="StringFormat"/> used to draw adds padding on top of - fitting to the
    /// tight measurement gives a line that still overflows.
    /// </summary>
    static float FitFontSize(Graphics g, string text, float size, float avail, float floor) {
        if (avail <= 0 || size <= 0) return size;
        using (Font probe = new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Pixel)) {
            float w = g.MeasureString(text, probe).Width;
            if (w <= avail || w <= 0) return size;
            return Math.Max(floor, size * avail / w);
        }
    }

    /// <summary>
    /// How much of the title's row the peak tag occupies. The title is centred in
    /// what is left of that row rather than in the whole panel: centred in the whole
    /// panel it ran straight under the tag ("OPENCODE G[谷时]"), and shrinking the
    /// title until it happened to clear was a losing race between two font sizes.
    /// </summary>
    float PeakTagWidth(Graphics g, float W, float H) {
        using (Font f = new Font("Microsoft YaHei UI", H * 0.19f, FontStyle.Bold, GraphicsUnit.Pixel))
            return g.MeasureString(Core.Model.PeakHours.Label(DateTime.UtcNow, PeakExcludesHolidays), f).Width + W * 0.03f;
    }

    /// <summary>
    /// Which side of the title the tag sits on: the one away from the screen edge the
    /// pet is parked against. Mirrored (bottom-right) the screen edge is on the right,
    /// so the tag goes on the panel's left; bottom-left it goes on the right. Fixed
    /// there - it must not slide about as the title's text changes length, or the one
    /// permanent element on the panel is the one that keeps moving.
    /// </summary>
    bool PeakTagOnLeft { get { return _mirror; } }

    /// <summary>
    /// The DeepSeek API's peak definition excludes Chinese statutory holidays; GO's
    /// does not mention them - and GO's invoice agrees with its wording: on
    /// 2026-10-02, a Friday in the National Day break, its 09:45-11:59 Beijing calls
    /// were billed at the full peak rate. So the holiday table applies on the
    /// DeepSeek side only: on a holiday weekday the two providers genuinely disagree
    /// about the price, and the pet is currently showing whichever one is selected.
    /// </summary>
    bool PeakExcludesHolidays { get { return _src != SrcGo; } }

    void DrawPeakTag(Graphics g, float W, float H) {
        bool peak = Core.Model.PeakHours.IsPeak(DateTime.UtcNow, PeakExcludesHolidays);
        string text = Core.Model.PeakHours.Label(DateTime.UtcNow, PeakExcludesHolidays);
        float fs = H * 0.19f;
        using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Brush sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
        using (Brush b = new SolidBrush(peak ? Color.FromArgb(255, 255, 122, 108)
                                             : Color.FromArgb(255, 118, 224, 152)))
        using (StringFormat sfx = new StringFormat()) {
            bool onLeft = PeakTagOnLeft;
            sfx.Alignment = onLeft ? StringAlignment.Near : StringAlignment.Far;
            sfx.LineAlignment = StringAlignment.Near;
            float pad = W * 0.03f;
            RectangleF r = onLeft ? new RectangleF(pad, H * 0.02f, W - pad, H * 0.24f)
                                  : new RectangleF(0, H * 0.02f, W - pad, H * 0.24f);
            g.DrawString(text, f, sh, new RectangleF(r.X + 1.2f, r.Y + 1.2f, r.Width, r.Height), sfx);
            g.DrawString(text, f, b, r, sfx);
        }
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

                float fLabel = H * 0.15f;
                // A spent window has no number worth showing: 0.0000 says nothing
                // except "you already knew". The countdown to the refill is the only
                // useful thing left, so it takes the big number's place - and that is
                // also why a spent window stops popping bubbles: the tablet carries
                // the answer from here on.
                bool spent = IsShownMeterSpent();
                string label = spent ? S_GOSPENT : S_GOLABEL;
                float tagW = PeakTagWidth(g, W, H);
                // The spent label is more than twice the width of "OPENCODE GO" and used
                // to wrap mid-word ("…重置倒 / 计时") onto a second line that then sat on
                // top of the countdown. Shrink it onto one line instead: this row is the
                // only part of the panel that never moves, and a wrapped heading makes
                // the countdown under it look like part of the sentence.
                fLabel = FitFontSize(g, label, fLabel, (W - tagW) * 0.94f, H * 0.095f);
                using (Font f = new Font("Microsoft YaHei UI", fLabel, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (Brush b = new SolidBrush(Color.FromArgb(235, 158, 182, 224))) {
                    RectangleF lr = PeakTagOnLeft
                        ? new RectangleF(tagW, H * 0.02f, W - tagW, H * 0.24f)
                        : new RectangleF(0, H * 0.02f, W - tagW, H * 0.24f);
                    g.DrawString(label, f, shadow, new RectangleF(lr.X + 1.5f, lr.Y + 1.5f, lr.Width, lr.Height), sf);
                    g.DrawString(label, f, b, lr, sf);
                }
                DrawPeakTag(g, W, H);

                if (spent) {
                    string when = Core.Model.GoMeter.FormatReset(GoReset(ShownWin), DateTime.UtcNow);
                    float fWhen = H * 0.26f;
                    using (Font fw = new Font("Microsoft YaHei UI", fWhen, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    using (Brush bw = new SolidBrush(Color.FromArgb(255, 214, 226, 246))) {
                        // shrink to fit: "2 天 12 小时" is far wider than "30.0000"
                        SizeF sz = g.MeasureString(when, fw);
                        float avail = W * 0.92f;
                        if (sz.Width > avail && sz.Width > 0) {
                            fWhen = Math.Max(H * 0.13f, fWhen * avail / sz.Width);
                            fw.Dispose();
                            using (Font fit = new Font("Microsoft YaHei UI", fWhen, FontStyle.Bold, GraphicsUnit.Pixel)) {
                                sz = g.MeasureString(when, fit);
                                g.DrawString(when, fit, sh, (W - sz.Width) / 2f + 1.5f, H * 0.36f + 1.5f);
                                g.DrawString(when, fit, bw, (W - sz.Width) / 2f, H * 0.36f);
                            }
                        } else {
                            g.DrawString(when, fw, sh, (W - sz.Width) / 2f + 1.5f, H * 0.36f + 1.5f);
                            g.DrawString(when, fw, bw, (W - sz.Width) / 2f, H * 0.36f);
                        }
                    }
                } else {
                string txt = double.IsNaN(ShownBalance)
                    ? "--"
                    : Core.Model.Sig.Five(ShownBalance);
                float fBal = H * 0.40f, fCur = H * 0.24f;
                using (Font fb = new Font("Arial", fBal, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Font fc = new Font("Arial", fCur, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (Brush bb = new SolidBrush(GoBigNumberColor(ShownWin)))
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
                }

                float fSeg = H * 0.15f;
                string[] seg = new string[3];
                for (int w = 0; w < 3; w++) {
                    double pct = GoRemainPct(w);
                    string s = WinShort(w) + " " + (double.IsNaN(pct)
                        ? "--"
                        : Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) + "%");
                    if (w == ShownWin) s = "[" + s + "]";
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

    /// <summary>
    /// The value the tablet's big number shows. While the carousel is turning this
    /// is the toured meter's own remaining quota; when it is showing the meter the
    /// accounting runs on, it stays the animated figure, so a deduction still
    /// visibly ticks down rather than jumping to the new total.
    /// </summary>
    double ShownBalance {
        get {
            int w = ShownWin;
            if (w == _win) return DrawnBalance;
            double remain = GoRemain(w);
            return double.IsNaN(remain) ? double.NaN : Math.Round(remain < 0 ? 0 : remain, 4);
        }
    }

    /// <summary>
    /// One palette for the whole GO panel, keyed off how much of an allowance is
    /// left: room to spare, under a third, under a tenth. The big number and the
    /// label underneath it draw from this same scale, so they always agree.
    /// </summary>
    static Color GoLevelColor(double pct) {
        if (double.IsNaN(pct)) return Color.FromArgb(255, 240, 246, 255);
        if (pct <= 10) return Color.FromArgb(255, 255, 118, 104);      // nearly gone
        if (pct <= 30) return Color.FromArgb(255, 255, 208, 128);      // getting low
        return Color.FromArgb(255, 240, 246, 255);
    }

    Color GoBigNumberColor(int w) {
        return GoLevelColor(GoRemainPct(w));
    }

    /// <summary>
    /// The percentage row. Both the bracket and the colour follow
    /// <see cref="ShownWin"/> - the meter the tablet is actually showing - so while
    /// the carousel turns, exactly one label is bracketed, brightest, and wearing
    /// the same colour as the big number above it.
    ///
    /// It used to key the brightness off the accounting meter instead, which meant
    /// the bracket said one window and the highlight said another: two rows could
    /// both look selected, and there was no way to tell where the carousel was.
    /// </summary>
    Color GoSegColor(int w) {
        double pct = GoRemainPct(w);
        bool shown = (w == ShownWin);
        if (double.IsNaN(pct)) return Color.FromArgb(shown ? 235 : 205, 130, 140, 160);
        Color c = GoLevelColor(pct);
        // The shown meter keeps full strength; the other two recede - but not into the
        // dark. At alpha 150 they were hard to read against the panel, and they are
        // still the answer to "how are the other windows doing". 205 keeps the shown
        // one clearly brighter while leaving the rest legible.
        return shown ? c : Color.FromArgb(205, c);
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

    /// <summary>
    /// The thought bubble: a rounded cloud above the character's head with a tail
    /// of shrinking dots pointing down at her, the way a comic draws someone
    /// thinking rather than saying.
    ///
    /// It sits above the head because that is the one part of the canvas with room
    /// - the same space the deduction numbers rise through - and it fades out over
    /// the last second instead of vanishing, so it reads as a thought rather than
    /// a glitch.
    /// </summary>
    void DrawBubble(Buf dst, int ox, int oy) {
        if (_bubbleText.Length == 0) return;
        int left = unchecked(_bubbleUntil - Environment.TickCount);
        if (left <= 0) return;

        // Three phases, because a bubble pinned in place reads as a label rather
        // than a thought:
        //   pop   - 0.22s, grows from 60% while fading in, so it looks produced
        //   hold  - stays put long enough to actually read
        //   drift - the last 2.2s rises while fading, the way a thought is finished
        //           with rather than switched off
        double age = (BubbleMs - left) / 1000.0;
        double popIn = Math.Min(1.0, age / 0.22);
        double pop = 0.60 + 0.40 * (1 - (1 - popIn) * (1 - popIn));      // ease-out
        double alpha = popIn;

        // It drifts from the first frame, not after a wait: a thought that hangs
        // still and then suddenly leaves looks switched off. The rise is spread
        // across the bubble's whole life, and only the *fading* waits.
        double life = 1 - left / (double)BubbleMs;                        // 0 -> 1
        // Bounded by the room above it, not by taste: the canvas ends just over the
        // head, and an earlier version rose 136px, which cut the bubble off at the
        // top edge while it was still 65% opaque - a hard ceiling, not a drift.
        // 0.30 of the sprite is not a taste call: the bubble rests 0.30 of the sprite
        // above the artwork's top edge (head height minus the gap), so rising exactly
        // that far lands its bottom edge on her hairline at the moment it vanishes -
        // "fully clear of her" and "no further than it needs to go" are the same
        // number, by construction rather than by tuning.
        double rise = life * _spritePx * 0.30;

        // The fade has to be *finished* before the bubble is taken away, not merely
        // nearly finished. The animation ticks every 33ms, so the last frame drawn
        // before the deadline could still be at ~7% opacity - faint, but visible, and
        // it then vanished in one step: the bubble appeared to hang for a moment and
        // then be cut. Reaching zero 120ms early means the frames either side of the
        // removal are both already invisible, so the removal cannot be seen at all.
        const double FadeSecs = 0.9, FadeTailSecs = 0.12;
        if (left < FadeSecs * 1000) {
            double span = (FadeSecs - FadeTailSecs) * 1000.0;
            double f = (FadeSecs * 1000.0 - left) / span;                 // 0 -> 1 at the tail
            if (f > 1) f = 1;
            if (f < 0) f = 0;
            alpha = 1 - f * f;                                            // lingers, then goes
        }
        if (alpha <= 0.01) return;
        _bubbleDebug = "left=" + left + " pop=" + pop.ToString("F2", CultureInfo.InvariantCulture) +
                       " alpha=" + alpha.ToString("F2", CultureInfo.InvariantCulture) +
                       " rise=" + ((int)Math.Round(rise));

        // Smaller than it first was. At 0.055 of the sprite the bubble came out 222px
        // wide in a 326px canvas - two thirds of the frame - and its bottom edge cut
        // across her forehead, so it read as a label pinned to her head rather than a
        // thought floating above it. A thought is a small thing.
        float em = (float)Math.Max(10.0, _spritePx * 0.046);
        Bitmap bmp = MakeBubbleBitmap(_bubbleText, em, _mirror, _w - _shakeMargin * 2);
        try {
            Buf src = new Buf(bmp);
            try {
                byte[] p = dst.P, sp = src.P;
                int ds = dst.Stride, ss = src.Stride;
                // Scaled in the destination, sampled from the full-size bitmap, so
                // the pop grows a sharp image rather than a blurry one.
                int w = Math.Max(2, (int)(src.W * pop)), h = Math.Max(2, (int)(src.H * pop));
                // Centred on the tablet rather than on a fixed inset from the edge.
                // The tablet is the thing the bubble is talking about, and lining the
                // two up is what makes them read as one object rather than as a label
                // that happens to be nearby.
                //
                // The centre is the quad's bounding-box centre, not the average of its
                // four corners: the tablet is drawn in perspective, so it is a
                // trapezoid, and for a trapezoid the corner average sits off to one
                // side of what the eye calls the middle. Using it put the bubble 24px
                // left of the plate while every number in the code agreed it was
                // centred - which is exactly the kind of thing only looking catches.
                double qMinX = Math.Min(Math.Min(_fx[0], _fx[1]), Math.Min(_fx[2], _fx[3]));
                double qMaxX = Math.Max(Math.Max(_fx[0], _fx[1]), Math.Max(_fx[2], _fx[3]));
                int tabletCx = (int)Math.Round((qMinX + qMaxX) / 2.0);
                int x0 = tabletCx - w / 2;
                if (x0 + w > _w) x0 = _w - w;              // never off the canvas
                if (x0 < 0) x0 = 0;
                int y0 = (int)(_headY - src.H - _spritePx * 0.06 - rise) + oy;
                for (int yy = 0; yy < h; yy++) {
                    int syy = (int)(yy / pop);
                    if (syy >= src.H) break;
                    int ty = y0 + yy;
                    if (ty < 0) continue;
                    if (ty >= _h) break;
                    for (int xx = 0; xx < w; xx++) {
                        int sxx = (int)(xx / pop);
                        if (sxx >= src.W) break;
                        int tx = x0 + xx;
                        if (tx < 0) continue;
                        if (tx >= _w) break;
                        int si = syy * ss + sxx * 4;
                        int sa = sp[si + 3];
                        if (sa == 0) continue;
                        Cs.Blend(p, ty * ds + tx * 4, sp[si + 2], sp[si + 1], sp[si], sa / 255.0 * alpha);
                    }
                }
            } finally { src.Dispose(); }
        } finally { bmp.Dispose(); }
    }

    /// <summary>
    /// The bubble artwork: rounded body, text, and a dotted tail below it.
    ///
    /// The text wraps to fit <paramref name="maxWidth"/>, which the caller sets to
    /// the canvas width minus the margins. Without a limit a longer message simply
    /// makes a wider bubble, and a wider bubble than the window is a bubble with its
    /// end cut off - which the short reminders never revealed but the longer ones do.
    /// Wrapping is measured and drawn through the same Graphics, because measuring
    /// with TextRenderer (GDI) and drawing with DrawString (GDI+) disagree by enough
    /// to break lines in different places.
    /// </summary>
    Bitmap MakeBubbleBitmap(string text, float em, bool mirror, int maxWidth) {
        using (Font f = new Font("Microsoft YaHei UI", em, FontStyle.Bold, GraphicsUnit.Pixel)) {
            int padX = (int)(em * 0.85f), padY = (int)(em * 0.55f);
            int tail = (int)(em * 1.5f);                  // room for the trailing dots
            int textMax = Math.Max((int)(em * 4), maxWidth - padX * 2);
            SizeF sz;
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
                sz = pg.MeasureString(text, f, textMax);
            int w = Math.Max(8, (int)Math.Ceiling(sz.Width) + padX * 2);
            int h = Math.Max(8, (int)Math.Ceiling(sz.Height) + padY * 2 + tail);
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                int bodyH = h - tail;
                using (GraphicsPath path = RoundedRect(new Rectangle(1, 1, w - 3, bodyH - 2), (int)(em * 0.9f))) {
                    using (Brush fill = new SolidBrush(Color.FromArgb(232, 250, 252, 255)))
                        g.FillPath(fill, path);
                    using (Pen edge = new Pen(Color.FromArgb(210, 90, 110, 150), Math.Max(1f, em * 0.09f)))
                        g.DrawPath(edge, path);
                }
                // The tail: two dots trailing down toward the head. Smaller and
                // further apart than they first were - at 0.42/0.26 of the em they
                // read as two more bubbles stuck to the first one rather than as a
                // trail, and a thought bubble's tail is meant to be loose.
                using (Brush dot = new SolidBrush(Color.FromArgb(232, 250, 252, 255)))
                using (Pen edge = new Pen(Color.FromArgb(210, 90, 110, 150), Math.Max(1f, em * 0.075f))) {
                    float r1 = em * 0.26f, r2 = em * 0.15f;
                    // The trail points toward the corner the pet is parked in - down
                    // to the right when she is bottom-right, down to the left when she
                    // is bottom-left - so it reads as leading to her. Flipping it the
                    // other way (toward the head's x) sent it away from the corner and
                    // off into empty space, which is what it looked like.
                    float dir = mirror ? 1f : -1f;
                    float cx = w / 2f - em * 0.30f * dir;
                    float y1 = bodyH - em * 0.10f;
                    float y2 = y1 + em * 1.15f;
                    g.FillEllipse(dot, cx - r1, y1, r1 * 2, r1 * 2);
                    g.DrawEllipse(edge, cx - r1, y1, r1 * 2, r1 * 2);
                    float cx2 = w / 2f + em * 0.55f * dir;
                    g.FillEllipse(dot, cx2 - r2, y2, r2 * 2, r2 * 2);
                    g.DrawEllipse(edge, cx2 - r2, y2, r2 * 2, r2 * 2);
                }
                using (Brush ink = new SolidBrush(Color.FromArgb(255, 34, 44, 62)))
                    g.DrawString(text, f, ink, new RectangleF(padX, padY, textMax, h - tail), StringFormat.GenericTypographic);
            }
            return bmp;
        }
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius) {
        GraphicsPath path = new GraphicsPath();
        int d = Math.Max(2, Math.Min(radius, Math.Min(r.Width, r.Height) / 2) * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    void DrawFloater(Floater fl, Buf dst) {        double t = fl.T / fl.Dur;
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

    /// <summary>
    /// Closes the menu if it is open.
    ///
    /// Needed because a recreated window (capture mode does that) used to leave an open
    /// dropdown behind with nothing left that could close it: the menu's owner handle is
    /// gone, so neither a click inside the pet nor a click anywhere else reaches it any
    /// more, and the only way out was to pick an item from it.
    /// (The tray icon shares this one menu, so there is no second dropdown to chase.)
    /// </summary>
    void CloseMenus() {
        try { if (_menu != null && _menu.Visible) _menu.Close(ToolStripDropDownCloseReason.AppClicked); } catch { }
    }

    bool MenuOpen { get { return _menu != null && _menu.Visible; } }

    /// <summary>
    /// Closes an open menu as soon as a mouse button goes down outside it.
    ///
    /// A WinForms dropdown normally closes itself two ways: it takes activation and is
    /// told when it loses it, or it holds the mouse capture and sees the click that lands
    /// outside. This window is WS_EX_NOACTIVATE by design, so the first never happens, and
    /// a click on another application cancels the capture before the dropdown sees it -
    /// which is why the menu stayed on screen until something inside the pet was clicked.
    /// Polling the button state from the tick is the same trick click-through uses for
    /// Ctrl, and it does not care who ends up receiving the click.
    ///
    /// The 400ms grace period is for the click that opened the menu: a right-click opens
    /// it while the button is still down, and near the screen edge the menu opens shifted
    /// away from the cursor, so that press would otherwise look like a click elsewhere.
    /// </summary>
    void CloseMenuOnOutsideClick() {
        if (!MenuOpen) return;
        if (unchecked(Environment.TickCount - _menuShownTick) < 400) return;
        bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 ||
                    (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
        if (!down) return;
        // A press inside the menu belongs to the menu - including inside an open submenu,
        // which is a separate window: testing only the parent's rectangle would close the
        // whole menu the instant someone clicked "尺寸 ▸ 6 cm".
        if (MenuContains(Cursor.Position)) return;
        CloseMenus();
    }

    /// <summary>True when the point is inside the menu or any submenu that is open.</summary>
    bool MenuContains(Point p) {
        return DropdownContains(_menu, p);
    }

    static bool DropdownContains(ToolStripDropDown drop, Point p) {
        if (drop == null || !drop.Visible) return false;
        if (drop.Bounds.Contains(p)) return true;
        foreach (ToolStripItem item in drop.Items) {
            ToolStripDropDownItem parent = item as ToolStripDropDownItem;
            if (parent == null || !parent.HasDropDownItems) continue;
            if (DropdownContains(parent.DropDown, p)) return true;
        }
        return false;
    }

    void OnTick() {
        try {
            ApplyClickThrough();             // Ctrl is the escape hatch; poll for it
            UpdateFace();                    // the mood follows the meters
            CloseMenuOnOutsideClick();       // the menu cannot close itself here
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

            // Carousel: advance which allowance the tablet is showing. Display only -
            // see the note on _viewWin for why the accounting must not move with it.
            //
            // Timed off the wall clock, not by adding up the timer's interval: the
            // tick interval is a *request*, and under load the timer fires late, so
            // counting nominal milliseconds made "every 5 seconds" actually take
            // seven. The log showed 13s -> 20s -> 27s, which is how this was caught.
            if (_carousel && _src == SrcGo) {
                int now = Environment.TickCount;
                if (_carouselLastTick == 0) _carouselLastTick = now;
                if (unchecked(now - _carouselLastTick) >= CarouselSeconds * 1000) {
                    _carouselLastTick = now;
                    _viewWin = (ShownWin + 1) % 3;
                    _dirty = true;
                    Log("carousel -> " + WinName(_viewWin) +
                        " (showing " + Fmt(GoRemain(_viewWin), 4) + ", " +
                        Fmt(GoRemainPct(_viewWin), 1) + "% left)");
                }
            } else {
                _carouselLastTick = 0;
                _viewWin = -1;
            }

            CheckAlerts();

            // The frame after the bubble goes has to be rendered even though nothing
            // else changed: without it the window keeps the last layer pushed while
            // the bubble was still up, and the bubble is removed by never being
            // redrawn rather than by being redrawn away. Its last frame is transparent
            // by then, but relying on that is relying on the tick landing where you
            // hope - this makes the clean frame explicit.
            bool bubbleNow = BubbleVisible();
            if (_bubbleWasUp && !bubbleNow) _dirty = true;
            _bubbleWasUp = bubbleNow;

            if (anim || _dirty || bubbleNow) { RenderToCanvas(); PushLayer(); }
        } catch (Exception ex) { Log("tick: " + ex); }
    }
    int _carouselLastTick;

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
        if (_hits.Count < 3) {
            _chargeUntil = unchecked(Environment.TickCount + ExpressionMs);
            UpdateFace();
            _hits.Add(new Hit());
        }
        if (fromTest) {
            _testOffset = Math.Round(_testOffset + amount, 4);
        } else {
            _testOffset = 0;                         // real movement clears rehearsals
        }
        Floater fl = new Floater();
        // Six decimals in GO mode: a real call can cost $0.000512, which the
        // DeepSeek format would print as a bare "-0". Deliberately *not* the board's
        // five-significant-digit format: a deduction is a number to read exactly, and
        // "-512.00u" is a size, not an amount. The board is the glanceable one.
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
                if (!ParseGoStatus(body, r.Lim, r.Used, _goResets)) { Fail("go status: cannot parse " + Trunc(body)); return; }

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
                if (!ParseGoStatus(body, r.Lim, r.Used, _goResets)) { Fail("go status: cannot parse " + Trunc(body)); return; }
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
        return Core.Parsing.DeepSeekBalance.ParseTotalCny(json);
    }

    // --------------------------------------------------------- go parsing ----

    static string Fmt(double v, int dp) {
        return double.IsNaN(v) ? "--" : v.ToString("F" + dp.ToString(CultureInfo.InvariantCulture),
                                                  CultureInfo.InvariantCulture);
    }

    // meters.{fiveHour,week,month}.{limitMicroCents,usedMicroCents}, in dollars
    static bool ParseGoStatus(string body, double[] lim, double[] used, DateTime?[] resets = null) {
        for (int w = 0; w < 3; w++) { lim[w] = 0; used[w] = 0; if (resets != null) resets[w] = null; }
        IReadOnlyList<Core.Model.GoMeter> meters = Core.Parsing.OpenCodeGo.ParseStatus(body);
        bool any = false;
        for (int i = 0; i < meters.Count; i++) {
            int w = Array.IndexOf(Core.Parsing.OpenCodeGo.WindowOrder, meters[i].Window);
            if (w < 0) continue;
            lim[w] = meters[i].LimitUsd;
            used[w] = meters[i].UsedUsd;
            if (resets != null) resets[w] = meters[i].ResetsAtUtc;
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
        // The percentages logged are the meters' own readings - what the API said -
        // with the gate named when one is active, so the log shows both the raw
        // numbers and why the panel may be showing a zero over the top of them.
        Log("go poll ok" + (snap ? " (snap)" : "") + " [" + WinName(_win) + "]" +
            ": remain=" + Fmt(GoRemainingLive, 4) +
            " printed=" + DrawnText +
            " pending=" + Fmt(_pending, 6) +
            " cues=" + _goCues.Count.ToString(CultureInfo.InvariantCulture) +
            " queued=" + Fmt(_goQueuedSum, 6) +
            " left%=" + Fmt(GoMeterPct(Win5h), 1) + "/" + Fmt(GoMeterPct(WinWeek), 1) +
            "/" + Fmt(GoMeterPct(WinMonth), 1) +
            (GoGate(_win) >= 0 ? " gated-by=" + WinName(GoGate(_win)) : ""));
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
        IReadOnlyList<Core.Model.RequestLogEntry> items = Core.Parsing.OpenCodeGo.ParseLogs(json);
        int exact = 0, lumped = 0, notBilled = 0;
        long newest = 0;
        lock (_hits) {
            for (int i = 0; i < items.Count; i++) {
                Core.Model.RequestLogEntry it = items[i];
                string id = it.Id;
                if (it.StartedAt > newest) newest = it.StartedAt;
                if (string.IsNullOrEmpty(id)) continue;
                if (_goSeen.Contains(id)) continue;
                _goSeen.Add(id);
                if (prime) continue;                       // start-up: seed the cursor only
                if (it.Outcome != "succeeded") { notBilled++; continue; }
                double cost = it.CostUsd;
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

        if (Array.IndexOf(args, "--uicheck") >= 0) {
            pet.GoOffline();
            pet.UiCheck(baseDir);
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

    // ------------------------------------------------------- settings GUI ----
    //
    // The settings window (SettingsForm.cs) talks to the widget only through this
    // surface. Changes take effect immediately - the pet sits behind the window,
    // so resizing or re-positioning it is visible at once - which is why the
    // window has a Done button rather than an OK/Cancel pair.

    /// <summary>Everything the settings window shows. Secrets are reduced to flags.</summary>
    public sealed class SettingsView {
        public double Cm;
        public int PollMs;
        public bool Mirror;
        public bool IsGo;
        public string Window;
        public bool SoundEnabled;
        public int Volume;
        public bool ClickThrough;
        public bool Carousel;
        public bool Expressions;
        public int CarouselSeconds;
        public int WarnPercent;
        public double WarnCny;
        public bool HasDeepSeekKey;
        public bool HasGoKey;
        public bool HasGoCookie;
        public string GoOrg;
        public string BaseDir;
        public string LogPath;
    }

    public SettingsView ViewSettings() {
        SettingsView v = new SettingsView();
        v.Cm = _cm;
        v.PollMs = _pollMs;
        v.Mirror = _mirror;
        v.IsGo = (_src == SrcGo);
        v.Window = WinJson(_win);
        v.SoundEnabled = _soundWanted;
        v.Volume = _volume;
        v.ClickThrough = _clickThrough;
        v.Expressions = _expressions;
        v.Carousel = _carousel && _src == SrcGo;
        v.CarouselSeconds = _carouselSeconds;
        v.WarnPercent = _warnPercent;
        v.WarnCny = _warnCny;
        v.HasDeepSeekKey = _apiKey.Length > 0;
        v.HasGoKey = GoHasKey;
        v.HasGoCookie = GoHasCookie;
        v.GoOrg = _goOrg;
        v.BaseDir = _baseDir;
        v.LogPath = Path.Combine(_baseDir, "pet.log");
        return v;
    }

    public void UiSetSize(double cm) { SetCm(cm); }
    public void UiSetMirror(bool right) { SetMirror(right); }
    public void UiSetPollSeconds(double seconds) { SetPollSec(seconds); }

    public void UiSetSource(bool go) { SetSource(go ? SrcGo : SrcDsh); }

    public void UiSetWindow(string window) {
        SetWindow(window == "week" ? WinWeek : (window == "month" ? WinMonth : Win5h));
    }

    public void UiSetSound(bool enabled, int volume) {
        ApplySound(enabled, volume);
        SaveState();
    }

    public void UiPreviewSound() {
        if (!_soundWanted) ApplySound(true, _volume);
        PlayHitSound();
    }

    /// <summary>
    /// Click-through on/off. With it on the window is a hole in the desktop and
    /// holding Ctrl is what makes it solid again, so this is not a one-way door:
    /// the pet can never become unreachable, only quiet.
    /// </summary>
    public void SetClickThrough(bool on) {
        if (_clickThrough == on) return;
        _clickThrough = on;
        ApplyClickThrough();
        SaveState();
        Log("click-through " + (on ? "on (hold Ctrl to grab the pet)" : "off"));
        Notify(on ? "点击穿透已开启（按住 Ctrl 可拖动）" : "点击穿透已关闭");
    }

    /// <summary>
    /// Click-through as a window style, not as a hit-test answer.
    ///
    /// This used to be done by returning HTTRANSPARENT from WM_NCHITTEST, and that is
    /// where "clicks do not go through, only drags do" came from: the documentation for
    /// HTTRANSPARENT says the message is passed to underlying windows *in the same
    /// thread*. The pet is the only window in its process, so a click on the desktop or
    /// on another application behind it had nowhere to go and was swallowed.
    /// WS_EX_TRANSPARENT removes the window from hit-testing entirely, so the input
    /// lands on whatever is behind it, whoever owns it.
    ///
    /// Ctrl has to keep working as the escape hatch, and a window that is not
    /// hit-tested never sees a key press either, so the state is polled from the tick
    /// and the style is only touched when it actually changes.
    /// </summary>
    void ApplyClickThrough() {
        if (Handle == IntPtr.Zero) return;
        long ex = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
        bool through = _clickThrough && !CtrlHeld();
        long want = through ? (ex | Native.WS_EX_TRANSPARENT)
                            : (ex & ~(long)Native.WS_EX_TRANSPARENT);
        if (want == ex) return;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)want);
        Log("click-through: mouse " + (through ? "passes to whatever is behind the pet"
                                              : "grabbed again (Ctrl held)"));
    }

    public void UiSetClickThrough(bool on) { SetClickThrough(on); }

    public void UiSetExpressions(bool on) { SetExpressions(on); }

    /// <summary>
    /// Turns the GO allowance carousel on or off. Switching it on takes
    /// effect immediately and starts on the meter that is active, so the
    /// numbers on screen never jump for no reason.
    /// </summary>
    public void SetCarousel(bool on) {
        if (_carousel == on) return;
        _carousel = on;
        _viewWin = on ? _win : -1;
        _carouselLastTick = 0;
        SaveState();
        Log("carousel " + (on ? "on, every " + CarouselSeconds + "s" : "off"));
        _dirty = true;
        RenderToCanvas();
        PushLayer();
    }

    public void UiSetCarousel(bool on) { SetCarousel(on); }

    /// <summary>Seconds each window stays on screen while the carousel runs.</summary>
    public void UiSetCarouselSeconds(int seconds) {
        int v = seconds < 2 ? 2 : (seconds > 3600 ? 3600 : seconds);
        if (v == _carouselSeconds) return;
        _carouselSeconds = v;
        _carouselLastTick = 0;          // restart the dwell from now
        SaveState();
        Log("carousel speed -> " + v + "s");
    }

    /// <summary>GO: warn when a window drops to this percentage left (0 = only when empty).</summary>
    public void UiSetWarnPercent(int percent) {
        int v = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
        if (v == _warnPercent) return;
        _warnPercent = v;
        SaveState();
        Log("go warn threshold -> " + v + "%");
    }

    /// <summary>DeepSeek: warn when the balance drops to this many yuan (0 = never early).</summary>
    public void UiSetWarnCny(double yuan) {
        double v = yuan < 0 ? 0 : (yuan > 1000000 ? 1000000 : yuan);
        if (Math.Abs(v - _warnCny) < 1e-9) return;
        _warnCny = v;
        SaveState();
        Log("deepseek warn threshold -> ¥" + Fmt(v, 2));
    }

    /// <summary>
    /// Turns the per-charge face switch on or off. Faces come from expressions\ next to
    /// the exe; the switch is kept even when the folder is missing so the menu item and
    /// the settings window still show what the user chose.
    /// </summary>
    public void SetExpressions(bool on) {
        if (_expressions == on) return;
        _expressions = on;
        if (!on) { _faceShown = -1; _faceRole = -1; }
        _dirty = true;
        SaveState();
        Log("expressions " + (on ? "on (" + _exprFlat.Count + " faces)" : "off"));
        Notify(on ? "扣血时会切换表情（共 " + _exprFlat.Count + " 张）" : "扣血时不再切换表情");
    }

    /// <summary>Stores a new DeepSeek key. An empty string changes nothing.</summary>
    public string UiSetDeepSeekKey(string key) {
        key = (key ?? "").Trim();
        if (key.Length == 0) return "未填写，保持原样";
        _apiKey = key;
        StoreActiveCredentials();
        _pollWant = 2;
        Log("api key set from the settings window (length " + key.Length + ")");
        return "已保存，正在重新查询…";
    }

    /// <summary>Blocking probes for the window's test buttons; run them off the UI thread.</summary>
    public string TestDeepSeek() {
        if (_apiKey.Length == 0) return "未设置 API Key";
        try {
            double bal = ParseCny(FetchBalance());
            if (double.IsNaN(bal)) return "返回内容无法解析";
            return "余额 ¥" + bal.ToString("0.00", CultureInfo.InvariantCulture) + "  ✓";
        } catch (Exception ex) { return "失败：" + ex.Message; }
    }

    public string TestGoConnection() {
        StringBuilder sb = new StringBuilder();
        sb.Append("Key ").Append(GoHasKey ? "✓" : "✗")
          .Append("    Cookie ").Append(GoHasCookie ? "✓" : "✗").Append("\r\n");

        try {
            IReadOnlyList<Core.Model.GoMeter> meters = Core.Parsing.OpenCodeGo.ParseStatus(FetchGoStatus());
            if (meters.Count == 0) sb.Append("额度：这个账号没有可读的额度窗口");
            else {
                for (int i = 0; i < meters.Count; i++) {
                    if (i > 0) sb.Append("   ");
                    sb.Append(meters[i].LongLabel)
                      .Append(" 剩 $").Append(meters[i].RemainingUsd.ToString("0.0000", CultureInfo.InvariantCulture))
                      .Append(" (").Append(Math.Round(meters[i].RemainingPercent).ToString("0", CultureInfo.InvariantCulture)).Append("%)");
                }
            }
        } catch (Exception ex) { sb.Append("额度失败：").Append(ex.Message); }

        if (!GoHasCookie) {
            sb.Append("\r\n没有 Cookie，只能读额度，无法逐次计费");
            return sb.ToString();
        }
        try {
            IReadOnlyList<Core.Model.RequestLogEntry> items = Core.Parsing.OpenCodeGo.ParseLogs(FetchGoLogs());
            int billable = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].IsBillable) billable++;
            sb.Append("\r\n明细 ").Append(items.Count).Append(" 条，可计费 ").Append(billable).Append(" 条  ✓");
        } catch (Exception ex) {
            sb.Append("\r\n明细失败（Cookie 可能过期）：").Append(ex.Message);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------- settings check --
    //
    // --uicheck: builds the settings window off-screen and exercises the wiring
    // between it and this class. Clicking through the window by hand after every
    // change is not realistic, and the failure mode of a mis-wired control is
    // silence rather than an exception.
    /// <summary>
    /// The self-test writes settings for real - that is the thing it is testing - so
    /// it puts the user's own state.ini back afterwards. A diagnostic that silently
    /// reconfigures the widget it is diagnosing is worse than no diagnostic: this
    /// one turned the carousel off and dropped the new tuning keys simply because
    /// the shell wrapper around it had snapshotted an older file.
    /// </summary>
    public void UiCheck(string baseDir) {
        string statePath = StatePath;
        string backup = File.Exists(statePath) ? File.ReadAllText(statePath) : null;
        // accounts.json too: switching the source in this test writes it into the
        // *active account*, so without this the test leaves the user's GO account
        // labelled as a DeepSeek one - which is exactly what happened, and it is not
        // a cosmetic difference: the account then reads as "unconfigured" and the
        // widget has nothing to poll.
        string accountsPath = Path.Combine(baseDir, Core.Accounts.AccountStore.FileName);
        string accountsBackup = File.Exists(accountsPath) ? File.ReadAllText(accountsPath) : null;
        try {
            UiCheckBody(baseDir);
        } finally {
            try {
                if (backup != null) File.WriteAllText(statePath, backup, Encoding.ASCII);
                else if (File.Exists(statePath)) File.Delete(statePath);
                if (accountsBackup != null) File.WriteAllText(accountsPath, accountsBackup, new UTF8Encoding(false));
            } catch { }
            LoadAccounts();          // and put the live fields back in step with the file
        }
    }

    void UiCheckBody(string baseDir) {
        _src = SrcDsh;
        _win = WinWeek;
        ResetAccounting();
        SetCm(4.0);
        _simFail = 0;

        Console.WriteLine("== settings window check (offline, no window shown) ==");

        global::DshPet.App.SettingsForm form = null;
        try {
            form = new global::DshPet.App.SettingsForm(this);
            form.CreateControl();
            Console.WriteLine("  controls: " + form.ControlSummary());

            SettingsView v0 = ViewSettings();
            Chk("window reflects the live size", form.ControlSummary().Contains("cm=" + v0.Cm.ToString("0.0", CultureInfo.InvariantCulture)),
                form.ControlSummary());
            Chk("window reflects the live window choice", form.ControlSummary().Contains("accounts=" + _accounts.Profiles.Count),
                form.ControlSummary());
            Chk("window reflects the live sound state", form.ControlSummary().Contains("sound=" + (v0.SoundEnabled ? "True" : "False")), "");
            string png = Path.Combine(baseDir, "_settings_window.png");
            // DrawToBitmap on a form that was never shown paints the frame and
            // nothing else, so the window is realised for a single frame: Shown
            // fires after the first paint, which is the earliest moment the child
            // controls are actually in the bitmap. The window flashes briefly.
            //
            // The bounds check has to happen here too, and not a moment earlier:
            // until the TabControl lays its pages out they still report their
            // default 200x100, and every group box sitting on one looks like an
            // overflow. The first version of this check did report exactly that.
            string clipped = "";
            form.Shown += delegate {
                try {
                    clipped = form.OutOfBounds();
                    using (Bitmap bmp = new Bitmap(form.Width, form.Height)) {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(png, ImageFormat.Png);
                    }
                } catch (Exception ex) { Console.WriteLine("  capture failed: " + ex.Message); }
                form.Close();
            };
            form.ShowDialog(this);
            Chk("no control sits outside its container", clipped.Length == 0, clipped);
            Console.WriteLine("  rendered: " + Path.GetFileName(png) + " " + form.Width + "x" + form.Height);
        } catch (Exception ex) {
            Chk("settings window builds", false, ex.Message);
        } finally {
            if (form != null) form.Dispose();
        }

        // every control must actually reach the widget
        UiSetSize(6.5);
        Chk("size control reaches the widget", Math.Abs(ViewSettings().Cm - 6.5) < 1e-9, "cm=" + Fmt(ViewSettings().Cm, 2));
        UiSetMirror(true);
        Chk("position control reaches the widget", ViewSettings().Mirror, "");
        UiSetPollSeconds(7.0);
        Chk("poll control reaches the widget", ViewSettings().PollMs == 7000, "poll_ms=" + ViewSettings().PollMs);
        UiSetSource(true);
        Chk("source control reaches the widget", ViewSettings().IsGo, "");
        UiSetWindow("month");
        Chk("window control reaches the widget", ViewSettings().Window == "month", ViewSettings().Window);
        UiSetSound(false, 30);
        Chk("sound control reaches the widget", !ViewSettings().SoundEnabled && ViewSettings().Volume == 30,
            "sound=" + ViewSettings().SoundEnabled + " vol=" + ViewSettings().Volume);

        // and the values must survive a restart
        Core.Configuration.PetState saved = Core.Configuration.PetState.Load(baseDir);
        Chk("settings were persisted to state.ini", Math.Abs(saved.Cm - 6.5) < 1e-9 && saved.Mirror &&
            saved.PollMs == 7000 && saved.IsGo && saved.Window == "month" &&
            !saved.SoundEnabled && saved.Volume == 30, saved.Serialize().Replace("\r\n", " "));

        // credential paste path, exactly as the window's save button calls it.
        // Both stores are saved and restored around this: the save button writes
        // for real, and a test mode must never destroy what the user put there.
        string credPath = GoCredPath;
        string accountsPath = Path.Combine(baseDir, Core.Accounts.AccountStore.FileName);
        string credBackup = File.Exists(credPath) ? File.ReadAllText(credPath) : null;
        string accountsBackup = File.Exists(accountsPath) ? File.ReadAllText(accountsPath) : null;
        try {
            string activeId = _accounts.Active == null ? "" : _accounts.Active.Id;
            string blob = "auth=Fe26.2**x*y*z; __Host-console_session=st_9edad063-cc54-49a9-a6a6-3788cf561387; " +
                          "headers @{\"x-org-id\"=\"wrk_01TESTWORKSPACE0000000000\"}";
            string summary = SaveAccountCredentials(activeId, "", blob);
            Chk("paste box parses a capture", summary != null && summary.Contains("Cookie ✓"), summary);
            Chk("parsed capture is usable", ViewSettings().HasGoCookie, "cookie=" + ViewSettings().HasGoCookie);

            string junk = SaveAccountCredentials(activeId, "", "nothing to see here");
            Chk("junk paste changes nothing", junk != null && junk.Contains("没有"), junk);
            Chk("junk paste left the stored credentials alone",
                _accounts.Active.GoSession == "st_9edad063-cc54-49a9-a6a6-3788cf561387",
                "session=" + (_accounts.Active.GoSession.Length > 0 ? _accounts.Active.GoSession : "(empty)"));

            // A personal OpenCode account carries "org_" where a team workspace
            // carries "wrk_"; recognising only the latter silently dropped the org.
            SaveAccountCredentials(activeId, "",
                "auth=Fe26.2**p*q; __Host-console_session=st_69e2f125-255e-4c40-ab67-1a770c10198b; x-org-id: org_01M3V8NP5RARPX3TVYXBV3NVTE");
            Chk("an org_-style personal capture is accepted",
                _accounts.Active.GoOrg == "org_01M3V8NP5RARPX3TVYXBV3NVTE", _accounts.Active.GoOrg);

            // account lifecycle, driven exactly as the window drives it
            int before = _accounts.Profiles.Count;
            string newId = AddAccount("自检临时账户", "go");
            Chk("an account can be added", _accounts.Profiles.Count == before + 1, "n=" + _accounts.Profiles.Count);
            RenameAccount(newId, "自检改名");
            Chk("an account can be renamed", ViewAccount(newId).Name == "自检改名", ViewAccount(newId).Name);
            SetAccountSource(newId, "dsh");
            Chk("an account's type can be changed", !ViewAccount(newId).IsGo, ViewAccount(newId).Source);
            ActivateAccount(newId);
            Chk("switching accounts switches the live credentials", _accounts.Active.Id == newId, _accounts.Active.Name);
            Chk("a fresh empty account reads as unconfigured", !ViewSettings().HasDeepSeekKey,
                "dsh key=" + ViewSettings().HasDeepSeekKey);
            Chk("the last account cannot be deleted", _accounts.Profiles.Count > 1 || !DeleteAccount(newId), "");
            DeleteAccount(newId);
            Chk("an account can be deleted",
                _accounts.Find(newId) == null && _accounts.Profiles.Count == before, "n=" + _accounts.Profiles.Count);
        } finally {
            if (credBackup != null) File.WriteAllText(credPath, credBackup, Encoding.ASCII);
            else if (File.Exists(credPath)) File.Delete(credPath);
            if (accountsBackup != null) File.WriteAllText(accountsPath, accountsBackup, new UTF8Encoding(false));
            LoadAccounts();
        }

        UiSetSound(true, 80);
        UiSetSource(false);
        SetCm(8.0);
        SetMirror(false);

        // Shell UI must win over the pet, but a permanent taskbar must not. The
        // table is the knowledge here, so it is what gets asserted; the live
        // answer for this machine is only reported.
        string clip = ShakeClipCheck();
        Console.WriteLine("  shake: " + clip);
        // The margin is gone on purpose - no part of the window may hang off the screen -
        // so a displaced frame now runs off the canvas edge instead of into reserved
        // room. What still has to hold is that the resting frame is whole and that a
        // shake grazes the artwork rather than eating it: this check used to read
        // "never clips", which was true of the margin and is no longer the design.
        long restPixels = 0, worstPixels = 0;
        foreach (string part in clip.Split(' ')) {
            if (part.StartsWith("resting=")) long.TryParse(part.Substring(8), out restPixels);
            if (part.StartsWith("worst=")) long.TryParse(part.Substring(6), out worstPixels);
        }
        Chk("the resting frame is whole and a shake only grazes the artwork",
            restPixels > 0 && worstPixels > restPixels / 2, clip);

        // The bubble must actually put pixels on the canvas. Counting opaque pixels
        // with and without it is the only way to tell a renderer that works from one
        // that was merely called - a bitmap blended at the wrong offset, or with a
        // zero alpha, looks exactly like success from the outside.
        _bubbleText = "";
        RenderToCanvas();
        long noBubble = OpaquePixels();
        _bubbleText = "本周 2 天 12 小时后重置";

        // "It animates" is not something a screenshot proves, and it is not
        // something to take on faith either. Each phase is measured: where the
        // bubble's pixels are, how wide they are and how much alpha they carry.
        // That is what makes "pops in, then floats up and fades" a testable claim.
        string popPhase, holdPhase, driftPhase;
        int holdTop;
        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs - 40);
        RenderToCanvas();
        popPhase = BubbleProbe(out holdTop);
        SaveCanvas(Path.Combine(baseDir, "_bubble_pop.png"));

        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs - 900);
        RenderToCanvas();
        long withBubble = OpaquePixels();
        holdPhase = BubbleProbe(out holdTop);
        SaveCanvas(Path.Combine(baseDir, "_bubble_hold.png"));

        _bubbleUntil = unchecked(Environment.TickCount + 300);
        RenderToCanvas();
        driftPhase = BubbleProbe(out holdTop);
        SaveCanvas(Path.Combine(baseDir, "_bubble_drift.png"));

        // Which corner the pet is parked in decides which side the bubble goes, and
        // the two placements look nothing alike - so both get a picture. Only one of
        // them is ever the one the user is looking at, and it was the other one that
        // kept being rendered.
        SetMirror(true);
        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs - 900);
        RenderToCanvas();
        SaveCanvas(Path.Combine(baseDir, "_bubble_corner_right.png"));
        SetMirror(false);
        RenderToCanvas();
        SaveCanvas(Path.Combine(baseDir, "_bubble_corner_left.png"));
        SetMirror(true);                    // the live setting on this machine

        // Draw what the code *believes* the tablet is - its quad and the centre the
        // bubble is aligned to - onto the frame it just saved. "Centred on the
        // tablet" is then checkable against the pixels instead of against the same
        // numbers that produced it, which is how a 28px misalignment stayed invisible.
        using (Graphics g = Graphics.FromImage(_canvas)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(255, 0, 255, 255), 1))
                g.DrawPolygon(pen, new PointF[] {
                    new PointF((float)_fx[0], (float)_fy[0]), new PointF((float)_fx[1], (float)_fy[1]),
                    new PointF((float)_fx[2], (float)_fy[2]), new PointF((float)_fx[3], (float)_fy[3]) });
            using (Pen pen = new Pen(Color.FromArgb(255, 255, 0, 255), 1)) {
                int gcx = (int)Math.Round((_fx[0] + _fx[1] + _fx[2] + _fx[3]) / 4.0);
                g.DrawLine(pen, gcx, 0, gcx, _h);
            }
        }
        SaveCanvas(Path.Combine(baseDir, "_bubble_quad.png"));

        _bubbleText = "";
        RenderToCanvas();
        Console.WriteLine("  bubble pop   : " + popPhase);
        Console.WriteLine("  bubble hold  : " + holdPhase);
        Console.WriteLine("  bubble drift : " + driftPhase);
        Chk("the thought bubble actually draws", withBubble > noBubble + 200,
            "with=" + withBubble + " without=" + noBubble);
        Chk("it pops in smaller than it settles",
            PhaseWidth(popPhase) < PhaseWidth(holdPhase), popPhase);
        Chk("it drifts upward and fades",
            PhaseTop(driftPhase) < PhaseTop(holdPhase) && PhaseAlpha(driftPhase) < PhaseAlpha(holdPhase) * 0.9,
            driftPhase + " vs " + holdPhase);

        // "It drifts from the first frame" is the requirement, so it gets its own
        // samples: two points while the bubble is still fully opaque, proving it has
        // moved up *before* any fading starts. The earlier version held perfectly
        // still and only moved during the fade, which read as being switched off
        // rather than floating away.
        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs - 400);   // 0.4s in
        RenderToCanvas();
        string early = BubbleProbe(out holdTop);
        _bubbleUntil = unchecked(Environment.TickCount + 1000);             // just before fading
        RenderToCanvas();
        string late = BubbleProbe(out holdTop);
        _bubbleText = "";
        RenderToCanvas();
        Console.WriteLine("  bubble early : " + early);
        Console.WriteLine("  bubble late  : " + late);
        Chk("it starts lower than it will be, i.e. it has been rising since it appeared",
            PhaseTop(early) > PhaseTop(holdPhase),
            "early=" + PhaseTop(early) + " (0.4s in) vs hold=" + PhaseTop(holdPhase) + " (2s in)");
        Chk("it rises while it is still fully opaque",
            PhaseTop(late) < PhaseTop(early) && PhaseAlpha(late) >= PhaseAlpha(holdPhase),
            "late=" + PhaseTop(late) + "/" + PhaseAlpha(late) + " vs early=" + PhaseTop(early) + "/" + PhaseAlpha(early));
        Chk("it is still whole while it is still visible", PhaseTop(late) > 0, "top=" + PhaseTop(late));

        // And the fade must be *finished* before the bubble is taken away. At 100ms
        // left it has to draw nothing at all, so the frames either side of the removal
        // are both invisible and the removal cannot be seen. The bug this pins: the
        // animation ticks every 33ms, so the last frame drawn could still be at ~7%
        // opacity - faint, but visible - and it then vanished in one step, which read
        // as the bubble hanging for a moment and then being cut.
        _bubbleText = "本周 2 天 12 小时后重置";
        _bubbleUntil = unchecked(Environment.TickCount + 100);
        RenderToCanvas();
        string tail = BubbleProbe(out holdTop);
        _bubbleText = "";
        RenderToCanvas();
        Console.WriteLine("  bubble tail  : " + tail);
        Chk("the bubble is fully transparent before it is removed",
            tail.Contains("top=none"), tail);

        // And it must not shake with the body. Asserted by driving the shake fields
        // directly and re-measuring at the same point in the animation: the artwork
        // moves, the bubble must not. Text that jitters is unreadable exactly when it
        // matters most, and this is the one property of the change that cannot be
        // seen in a still frame.
        // The baseline is taken right here, at the same point in the animation and in
        // the same mirror state: comparing against the earlier hold sample instead
        // compared a left-corner render with a right-corner one and "proved" a 20px
        // shift that was only the two corners differing.
        _shakeX = 0; _shakeY = 0;
        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs - 900);
        RenderToCanvas();
        string still = BubbleProbe(out holdTop);
        _shakeX = 9; _shakeY = -6;
        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs - 900);
        RenderToCanvas();
        string shaken = BubbleProbe(out holdTop);
        _shakeX = 0; _shakeY = 0;
        Chk("the bubble does not shake with the body",
            PhaseLeft(shaken) == PhaseLeft(still) &&
            Math.Abs(PhaseTop(shaken) - PhaseTop(still)) <= 2,
            "shaken=" + PhaseTop(shaken) + "," + PhaseLeft(shaken) +
            " still=" + PhaseTop(still) + "," + PhaseLeft(still) +
            " (x must match exactly: the shake is 9px across, so a shake leak cannot" +
            " hide there. y gets 2px of slack because the rise advances with the" +
            " clock between the two renders - 1px of drift is the timer, not the shake)");

        // What matters is the gap to the edge the pet is parked against, not which
        // half of the canvas it lands in: the bubble is wide enough that "aligned
        // inward" puts it near the middle, so a midpoint test measures the wrong
        // thing and passed a placement that was still pinned to the edge. The canvas
        // edge the pet hugs is the screen edge.
        int gap = _mirror ? _w - PhaseRight(late) : PhaseLeft(late);
        Chk("the bubble keeps clear of the screen edge", gap >= 40,
            "gap=" + gap + "px  x=" + PhaseLeft(late) + ".." + PhaseRight(late) + " mirror=" + _mirror);
        // The reminders are longer than the sample text the other checks use, and a
        // bubble wider than the canvas has its end cut off. Measured with the real
        // message rather than a short stand-in, because the long one is the only one
        // that overflows - and it was only added after the short one had been tuned.
        _bubbleText = "5 小时额度不足，3 小时 20 分后重置 · 注意额度";
        _bubbleUntil = unchecked(Environment.TickCount + BubbleMs - 900);
        RenderToCanvas();
        string longMsg = BubbleProbe(out holdTop);
        SaveCanvas(Path.Combine(baseDir, "_bubble_long.png"));
        _bubbleText = "";
        RenderToCanvas();
        Console.WriteLine("  bubble long  : " + longMsg);
        // The tablet's number is five significant digits, with a suffix instead of
        // more digits once it is far from 1. Checked against literal strings through
        // the property the panel actually draws from - the first version of this
        // compared PanelText with the same formatter and "passed" while the board was
        // showing "--", which is a tautology rather than a test.
        // GO mode, because that is where the board keeps four decimals of precision:
        // DeepSeek rounds to cents first, so 3.5258 is 3.53 before it is ever
        // formatted - which is correct, and made the first version of this check look
        // like a formatting failure when it was the rounding doing its job.
        UiSetSource(true);
        double keepBooked = _bookedBal, keepOffset = _testOffset;
        try {
            _testOffset = 0;
            _bookedBal = 3.5258;
            Chk("an ordinary board number stays five digits with no suffix",
                PanelText == "3.5258", "panel=" + PanelText);
            _bookedBal = 1234.4;
            Chk("a large board number gains a suffix instead of digits",
                PanelText == "1.234K", "panel=" + PanelText);
        } finally { _bookedBal = keepBooked; _testOffset = keepOffset; }
        UiSetSource(false);

        Chk("a full reminder still fits the canvas",
            PhaseLeft(longMsg) >= 0 && PhaseRight(longMsg) < _w,
            longMsg);

        Chk("the bubble stays on the canvas",
            PhaseLeft(late) >= 0 && PhaseRight(late) < _w,
            "x=" + PhaseLeft(late) + ".." + PhaseRight(late) + " of " + _w);

        // The carousel only moves which meter is drawn; the accounting meter must
        // not budge, or the rotation would reset the per-meter deltas it depends on.
        UiSetSource(true);                      // the carousel is a GO feature
        if (_src == SrcGo) {
            int before = ShownWin;
            int[] order = new int[4];
            SetCarousel(true);
            order[0] = ShownWin;
            for (int i = 1; i < 4; i++) {
                _carouselLastTick = Environment.TickCount - (int)(CarouselSeconds * 1000) - 50;
                OnTickProbe();
                order[i] = ShownWin;
            }
            Chk("the carousel turns through every window",
                order[0] == before && order[1] != order[0] && order[2] != order[1] &&
                order[3] == order[0], string.Join(" -> ", Array.ConvertAll(order, WinShort)));
            Chk("the carousel leaves the accounting meter alone", _win == before, "win=" + WinName(_win));
            SetCarousel(false);
            Chk("turning it off goes back to the active meter", ShownWin == _win, WinName(ShownWin));
        }
        UiSetSource(false);

        // The label row must follow the carousel, not the accounting meter. Keyed
        // off the wrong one, the bracket points at the meter being shown while the
        // highlight points at the meter being billed: two rows both look selected
        // and there is no way to tell where the rotation is.
        double keep5L = _goLim[Win5h], keep5U = _goUsed[Win5h];
        double keepWL = _goLim[WinWeek], keepWU = _goUsed[WinWeek];
        double keepMoL1 = _goLim[WinMonth], keepMoU1 = _goUsed[WinMonth];
        try {
            _goLim[Win5h] = 12; _goUsed[Win5h] = 2;        // 10.0000 left
            _goLim[WinWeek] = 30; _goUsed[WinWeek] = 4;    // 26.0000 left
            // The month is pinned roomy as well: it is the window above the toured
            // week, and a live month at zero would gate the week and change the
            // figure printed below. Live readings must not decide a test's outcome.
            _goLim[WinMonth] = 60; _goUsed[WinMonth] = 12;
            UiSetWindow("month");                          // accounting meter != shown
            SetCarousel(true);
            _viewWin = WinWeek;

            Color shownSeg = GoSegColor(WinWeek);
            Color otherSeg = GoSegColor(Win5h);
            Color big = GoBigNumberColor(ShownWin);
            Console.WriteLine("  carousel row: shown=" + shownSeg + "  other=" + otherSeg + "  big=" + big);

            Chk("the big number shows the toured meter", Math.Abs(ShownBalance - 26.0) < 1e-9,
                "shown=" + Fmt(ShownBalance, 4) + " (week has 26.0000)");
            Chk("the shown label is drawn brightest", shownSeg.A > otherSeg.A,
                "shown a=" + shownSeg.A + " other a=" + otherSeg.A);
            Chk("the shown label wears the big number's colour",
                shownSeg.R == big.R && shownSeg.G == big.G && shownSeg.B == big.B,
                shownSeg + " vs " + big);
            // Comparing "shown is brighter than every other" rather than "brightness
            // equals a fixed number": an unknown meter has its own alpha (it is
            // dimmed differently from a known one), so a numeric comparison across
            // the two palettes would be comparing unrelated things.
            Chk("the label row follows the shown meter, not the billed one",
                GoSegColor(WinWeek).A > GoSegColor(Win5h).A &&
                GoSegColor(WinWeek).A > GoSegColor(WinMonth).A &&
                _win == WinMonth,
                "shown(week)=" + GoSegColor(WinWeek).A + " 5h=" + GoSegColor(Win5h).A +
                " billed(month)=" + GoSegColor(WinMonth).A + " billed=" + WinName(_win));
            SetCarousel(false);
        } finally {
            _goLim[Win5h] = keep5L; _goUsed[Win5h] = keep5U;
            _goLim[WinWeek] = keepWL; _goUsed[WinWeek] = keepWU;
            _goLim[WinMonth] = keepMoL1; _goUsed[WinMonth] = keepMoU1;
        }

        // "Nearly gone turns red" is a rule, so check the rule rather than trusting
        // that three numbers merely look different: drive one meter through plenty,
        // low and critical and confirm the colour follows.
        double keepLim = _goLim[Win5h], keepUsed = _goUsed[Win5h];
        double keepWkL2 = _goLim[WinWeek], keepWkU2 = _goUsed[WinWeek];
        double keepMoL2 = _goLim[WinMonth], keepMoU2 = _goUsed[WinMonth];
        try {
            // nothing above the five hour window is empty, or the gate would force
            // every one of these samples to the same red
            _goLim[WinWeek] = 30; _goUsed[WinWeek] = 3;
            _goLim[WinMonth] = 60; _goUsed[WinMonth] = 6;
            _goLim[Win5h] = 100; _goUsed[Win5h] = 5;      // 95% left
            Color plenty = GoBigNumberColor(Win5h);
            _goUsed[Win5h] = 80;                          // 20% left
            Color warning = GoBigNumberColor(Win5h);
            _goUsed[Win5h] = 95;                          // 5% left
            Color critical = GoBigNumberColor(Win5h);
            Console.WriteLine("  big number: 95%=" + plenty + "  20%=" + warning + "  5%=" + critical);
            Chk("a nearly spent allowance turns red on the big number",
                plenty != warning && warning != critical && plenty != critical,
                "95%=" + plenty + " 20%=" + warning + " 5%=" + critical);
        } finally {
            _goLim[Win5h] = keepLim; _goUsed[Win5h] = keepUsed;
            _goLim[WinWeek] = keepWkL2; _goUsed[WinWeek] = keepWkU2;
            _goLim[WinMonth] = keepMoL2; _goUsed[WinMonth] = keepMoU2;
        }

        // The reminder rules, driven through a whole cycle: plenty -> low -> empty
        // -> refilled. What matters is that each event speaks exactly once - a
        // reminder that repeats while a meter sits at a level is nagging, not
        // reminding, and that distinction is the entire feature.
        UiSetSource(true);
        double keepWl = _goLim[Win5h], keepWu = _goUsed[Win5h];
        DateTime? keepWr = _goResets[Win5h];
        // The higher windows are pinned roomy as well. They are live readings, and a
        // week that happened to be empty would block the five hour window below it -
        // the samples here would then be measuring the user's account, not the code.
        double keepWkL = _goLim[WinWeek], keepWkU = _goUsed[WinWeek];
        double keepMoL = _goLim[WinMonth], keepMoU = _goUsed[WinMonth];
        int keepWin = _win;
        bool keepCar = _carousel;
        int keepWarn = _warnPercent;
        try {
            SetCarousel(false);
            _win = Win5h;
            _goLim[WinWeek] = 30; _goUsed[WinWeek] = 0;
            _goLim[WinMonth] = 60; _goUsed[WinMonth] = 0;
            _goResets[Win5h] = DateTime.UtcNow.AddHours(3).AddMinutes(20);
            // The threshold is pinned rather than inherited. The samples below pick
            // usage figures that mean "low" only at 15%: with the live threshold at 5%
            // (which is what the saved settings had) 90% used is still plenty, the
            // alerts shift by one step, and a correct widget fails the test. A test
            // that reads the user's settings is testing the settings.
            _warnPercent = 15;
            string[] said = new string[6];
            _goLim[Win5h] = 100; _goUsed[Win5h] = 0;
            _goLevel[Win5h] = Core.Model.MeterLevel.Unknown;
            // cleared before every call, so "said nothing" is unambiguous rather
            // than "the previous message is still sitting there"
            _bubbleText = ""; CheckAlerts(); said[0] = _bubbleText;
            _goUsed[Win5h] = 90; _bubbleText = ""; CheckAlerts(); said[1] = _bubbleText;
            _goUsed[Win5h] = 95; _bubbleText = ""; CheckAlerts(); said[2] = _bubbleText;
            _goUsed[Win5h] = 100; _bubbleText = ""; CheckAlerts(); said[3] = _bubbleText;
            _bubbleText = ""; CheckAlerts(); said[4] = _bubbleText;
            _goUsed[Win5h] = 0; _bubbleText = ""; CheckAlerts(); said[5] = _bubbleText;
            Console.WriteLine("  alerts: " + string.Join(" | ", said));

            Chk("the first reading never alerts", said[0].Length == 0, "(" + said[0] + ")");
            Chk("dropping below the threshold alerts once", said[1].Contains("不足"), said[1]);
            Chk("staying low is silent", said[2].Length == 0, "(" + said[2] + ")");
            Chk("running out alerts once", said[3].Contains("用完"), said[3]);
            Chk("staying empty is silent", said[4].Length == 0, "(" + said[4] + ")");
            Chk("refilling alerts once", said[5].Contains("已重置"), said[5]);

            // and a spent window hands the tablet the countdown instead of a dead 0
            _goUsed[Win5h] = _goLim[Win5h];
            Chk("a spent window is detected as spent", IsShownMeterSpent(), "");
            // Truncated, not rounded: "3 小时 19 分" for 3h19m59s. Rounding up would
            // promise time that is not there, which is the one thing a countdown
            // must never do.
            string phrase = Core.Model.GoMeter.FormatReset(GoReset(Win5h), DateTime.UtcNow);
            Chk("its countdown is a real phrase, not the number",
                phrase.Contains("小时") && phrase != "已重置" && !phrase.Contains("0.0000"), phrase);
            _goUsed[Win5h] = 0;
            Chk("a refilled window goes back to the number", !IsShownMeterSpent(), "");
        } finally {
            _goLim[Win5h] = keepWl; _goUsed[Win5h] = keepWu; _goResets[Win5h] = keepWr;
            _goLim[WinWeek] = keepWkL; _goUsed[WinWeek] = keepWkU;
            _goLim[WinMonth] = keepMoL; _goUsed[WinMonth] = keepMoU;
            _win = keepWin; _carousel = keepCar; _warnPercent = keepWarn;
            _goLevel[Win5h] = Core.Model.MeterLevel.Unknown;
            _bubbleText = "";
        }
        UiSetSource(false);

        // The tier gate: the three windows are nested and every call spends against all
        // three, so a window with room in it is still unusable while a higher one is
        // empty. The five hour window is the one that lies about this - it refills
        // every five hours, so it would happily report 75% while the week refuses
        // every call for the next two days.
        UiSetSource(true);                      // "spent" is a GO reading, not a DeepSeek one
        {
            double gKeep5L = _goLim[Win5h], gKeep5U = _goUsed[Win5h];
            double gKeepWL = _goLim[WinWeek], gKeepWU = _goUsed[WinWeek];
            double gKeepML = _goLim[WinMonth], gKeepMU = _goUsed[WinMonth];
            DateTime? gKeep5R = _goResets[Win5h], gKeepWR = _goResets[WinWeek], gKeepMR = _goResets[WinMonth];
            int gKeepWin = _win;
            bool gKeepCar = _carousel;
            try {
                SetCarousel(false);
                _win = Win5h;
                _goLim[Win5h] = 12; _goLim[WinWeek] = 30; _goLim[WinMonth] = 60;
                _goUsed[Win5h] = 3; _goUsed[WinWeek] = 3; _goUsed[WinMonth] = 3;
                _goResets[Win5h] = DateTime.UtcNow.AddHours(1);
                _goResets[WinWeek] = DateTime.UtcNow.AddDays(2);
                _goResets[WinMonth] = DateTime.UtcNow.AddDays(20);

                Chk("with room everywhere each window reports its own figure",
                    GoGate(Win5h) < 0 && GoGate(WinWeek) < 0 && GoGate(WinMonth) < 0 &&
                    Math.Abs(GoRemainPct(Win5h) - 75) < 0.01 && Math.Abs(GoRemainPct(WinMonth) - 95) < 0.01,
                    "5h=" + Fmt(GoRemainPct(Win5h), 1) + " mo=" + Fmt(GoRemainPct(WinMonth), 1));

                // the week runs out: the five hour window still holds 75% of its own
                // allowance and none of it can be spent
                _goUsed[WinWeek] = 30;
                Chk("an empty week blocks the five hour window",
                    GoGate(Win5h) == WinWeek && GoRemainPct(Win5h) == 0 && GoRemain(Win5h) == 0,
                    "gate=" + WinName(GoGate(Win5h)) + " 5h=" + Fmt(GoRemainPct(Win5h), 1));
                Chk("a blocked window quotes the blocker's refill",
                    GoReset(Win5h) == _goResets[WinWeek],
                    Core.Model.GoMeter.FormatReset(GoReset(Win5h), DateTime.UtcNow));
                Chk("the week itself keeps its own countdown",
                    GoGate(WinWeek) < 0 && GoReset(WinWeek) == _goResets[WinWeek], "");
                Chk("the month above an empty week is untouched",
                    GoGate(WinMonth) < 0 && GoRemainPct(WinMonth) > 90, "mo=" + Fmt(GoRemainPct(WinMonth), 1));
                Chk("the board hands its number over to the countdown", IsShownMeterSpent(), "");

                // the month runs out too: it refills last, so it is the one to quote -
                // naming the week there would promise quota two weeks early
                _goUsed[WinMonth] = 60;
                Chk("with week and month both empty the month is the blocker",
                    GoGate(Win5h) == WinMonth && GoGate(WinWeek) == WinMonth, "");
                Chk("and both countdowns become the month's",
                    GoReset(Win5h) == _goResets[WinMonth] && GoReset(WinWeek) == _goResets[WinMonth], "");

                // the five hour window refilling must not look like good news
                _goUsed[Win5h] = 0;
                Chk("a five hour refill cannot unlock a blocked account",
                    GoRemainPct(Win5h) == 0 && GoGate(Win5h) == WinMonth, "");
                _goLevel[Win5h] = Core.Model.MeterLevel.Empty;
                _goLevel[WinWeek] = Core.Model.MeterLevel.Empty;
                _goLevel[WinMonth] = Core.Model.MeterLevel.Empty;
                _bubbleText = ""; CheckAlerts();
                Chk("a refill that is still blocked announces nothing", _bubbleText.Length == 0, "(" + _bubbleText + ")");

                // an unread window is unknown, not empty - it must not block anything
                _goLim[WinMonth] = 0;
                Chk("a window with no reading yet never blocks", GoGate(Win5h) == WinWeek, "");
            } finally {
                _goLim[Win5h] = gKeep5L; _goUsed[Win5h] = gKeep5U; _goResets[Win5h] = gKeep5R;
                _goLim[WinWeek] = gKeepWL; _goUsed[WinWeek] = gKeepWU; _goResets[WinWeek] = gKeepWR;
                _goLim[WinMonth] = gKeepML; _goUsed[WinMonth] = gKeepMU; _goResets[WinMonth] = gKeepMR;
                _win = gKeepWin; _carousel = gKeepCar;
                _goLevel[Win5h] = Core.Model.MeterLevel.Unknown;
                _goLevel[WinWeek] = Core.Model.MeterLevel.Unknown;
                _goLevel[WinMonth] = Core.Model.MeterLevel.Unknown;
                _bubbleText = "";
            }
        }
        UiSetSource(false);

        // the tuning values must reach the widget and survive a restart
        UiSetCarouselSeconds(9);
        UiSetWarnPercent(25);
        UiSetWarnCny(3.5);
        Chk("the tuning controls reach the widget",
            ViewSettings().CarouselSeconds == 9 && ViewSettings().WarnPercent == 25 &&
            Math.Abs(ViewSettings().WarnCny - 3.5) < 1e-9,
            "carousel_s=" + ViewSettings().CarouselSeconds + " warn_pct=" + ViewSettings().WarnPercent +
            " warn_cny=" + ViewSettings().WarnCny);
        Core.Configuration.PetState tuned = Core.Configuration.PetState.Load(baseDir);
        Chk("the tuning values were persisted",
            tuned.CarouselSeconds == 9 && tuned.WarnPercent == 25 && Math.Abs(tuned.WarnCny - 3.5) < 1e-9,
            "carousel_s=" + tuned.CarouselSeconds + " warn_pct=" + tuned.WarnPercent +
            " warn_cny=" + tuned.WarnCny);

        // The double-click answer: one line per window, reset times and nothing else.
        // Asserted because the version before it shipped a "998%" on screen - the
        // percentage helper already returns 0..100 and the extra multiply by 100 was
        // invisible to every check that did not look at this exact string.
        UiSetSource(true);
        DateTime?[] keepResets = new DateTime?[3];
        for (int i = 0; i < 3; i++) keepResets[i] = _goResets[i];
        try {
            _goResets[0] = DateTime.UtcNow.AddHours(3).AddMinutes(5);
            _goResets[1] = DateTime.UtcNow.AddDays(2).AddHours(4);
            _goResets[2] = DateTime.UtcNow.AddDays(30).AddHours(4);
            SummonBubble();
            string summon = _bubbleText;
            _bubbleText = "";
            RenderToCanvas();
            SaveCanvas(Path.Combine(baseDir, "_bubble_summon.png"));
            Console.WriteLine("  summon       : " + summon.Replace("\n", " | "));
            string[] lines = summon.Split('\n');
            Chk("the double-click answer is one line per window", lines.Length == 3,
                lines.Length + " line(s)");
            Chk("it gives reset times rather than amounts",
                summon.Contains("后重置") && !summon.Contains("%") && !summon.Contains("还剩"),
                summon.Replace("\n", " | "));
            Chk("each line names its window",
                lines[0].StartsWith("5 小时") && lines[1].StartsWith("本周") && lines[2].StartsWith("本月"),
                summon.Replace("\n", " | "));
        } finally {
            for (int i = 0; i < 3; i++) _goResets[i] = keepResets[i];
            _bubbleText = "";
            RenderToCanvas();
        }
        UiSetSource(false);

        // The peak tag sits on the title's row, so the two must not collide: the
        // title is centred and the tag is right-aligned, and at the first sizes tried
        // the title ran under the tag ("OPENCODE G[谷时]"). Measured with the same
        // fonts and the panel's real texture size rather than eyeballed, because the
        // panel is scaled onto a tilted quad and its on-screen width tells you
        // nothing about where the text lands inside it.
        {
            float lw2 = (float)Math.Sqrt(Math.Pow(_fx[1] - _fx[0], 2) + Math.Pow(_fy[1] - _fy[0], 2));
            float lh2 = (float)Math.Sqrt(Math.Pow(_fx[3] - _fx[0], 2) + Math.Pow(_fy[3] - _fy[0], 2));
            if (lw2 > 8 && lh2 > 8) {
                float pw2 = 520f, ph2 = (float)Math.Round(pw2 * (lh2 / lw2));
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe)) {
                    float fTitle = ph2 * 0.15f, fTag = ph2 * 0.19f;
                    using (Font ft = new Font("Microsoft YaHei UI", fTitle, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Font fg = new Font("Microsoft YaHei UI", fTag, FontStyle.Bold, GraphicsUnit.Pixel)) {
                        SizeF st = pg.MeasureString(S_GOLABEL, ft);
                        SizeF sg = pg.MeasureString(Core.Model.PeakHours.Label(DateTime.UtcNow, true), fg);
                        float tagW = sg.Width + pw2 * 0.03f;
                        Console.WriteLine("  panel row    : title=" + (int)st.Width +
                                          "  tag=" + (int)sg.Width + "  free=" + (int)(pw2 - tagW) +
                                          "  (W=" + (int)pw2 + ")");
                        Chk("the title fits beside the peak tag", st.Width <= pw2 - tagW,
                            "title=" + (int)st.Width + " free=" + (int)(pw2 - tagW));

                        // And the label that replaces it when a window runs out, which is
                        // three times as long and used to wrap mid-word onto the
                        // countdown below it. Fit-to-one-line is the fix, so the check is
                        // that the fitted size really does fit.
                        float fSpent = FitFontSize(pg, S_GOSPENT, fTitle, (pw2 - tagW) * 0.94f, ph2 * 0.095f);
                        using (Font fsp = new Font("Microsoft YaHei UI", fSpent, FontStyle.Bold, GraphicsUnit.Pixel)) {
                            float sw = pg.MeasureString(S_GOSPENT, fsp).Width;
                            Chk("the spent label fits its row on one line", sw <= (pw2 - tagW) * 0.94f,
                                "spent=" + (int)sw + "px at " + (int)fSpent + "px, room=" + (int)((pw2 - tagW) * 0.94f) +
                                "px (unshrunk would be " + (int)pg.MeasureString(S_GOSPENT, ft).Width + "px)");
                        }
                    }
                }
            }
        }

        // Window identity, which is what decides whether a capturer can see the pet at
        // all. Capturers walk the top-level windows and skip every owned one, and they
        // cannot read a window painted through UpdateLayeredWindow. Both were true of
        // the pet: it was missing from OBS's window list and unreadable once picked.
        {
            long exNormal = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            long owner = (long)Native.GetWindow(Handle, 4);
            Chk("the pet window has no owner", owner == 0, "owner=0x" + owner.ToString("X"));
            // Not style preferences: these bits are the difference between a window a
            // capturer can see and one it cannot, measured one at a time on the running
            // pet. Normal mode is the quiet one - TOOLWINDOW keeps it out of the
            // taskbar and out of Alt-Tab, APPWINDOW is what put it there.
            Chk("a normal start is a tool window, out of the taskbar and Alt-Tab",
                (exNormal & Native.WS_EX_TOOLWINDOW) != 0, "ex=0x" + exNormal.ToString("X8"));
            Chk("a normal start does not ask to be an app window",
                (exNormal & Native.WS_EX_APPWINDOW) == 0, "ex=0x" + exNormal.ToString("X8"));
            Chk("a normal start is layered, for per-pixel alpha",
                (exNormal & Native.WS_EX_LAYERED) != 0, "ex=0x" + exNormal.ToString("X8"));
            // No part of the window may sit outside the screen: a strip that is never
            // visible is never painted, and a capturer reads it as raw surface - the
            // white band that used to run down the right and bottom of every frame.
            {
                Rectangle wa0 = Screen.PrimaryScreen.WorkingArea;
                Chk("the window is exactly the artwork, with no margin to hang off screen",
                    _shakeMargin == 0 && _w == _spritePx && _h == _spritePx + _headroom,
                    "margin=" + _shakeMargin + " canvas=" + _w + "x" + _h +
                    " sprite=" + _spritePx + " headroom=" + _headroom);
                Chk("and it sits entirely inside the screen",
                    Left >= wa0.Left && Top >= wa0.Top && Left + _w <= wa0.Right && Top + _h <= wa0.Bottom,
                    "at (" + Left + "," + Top + ") size " + _w + "x" + _h + " in " + wa0);
            }

            bool keepMode = _obsMode;
            try {
                SetObsMode(true);
                long exObs = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
                Chk("capture mode becomes an app window capturers will list",
                    (exObs & Native.WS_EX_APPWINDOW) != 0 && (exObs & Native.WS_EX_TOOLWINDOW) == 0,
                    "ex=0x" + exObs.ToString("X8"));
                Chk("capture mode keeps the per-pixel alpha",
                    (exObs & Native.WS_EX_LAYERED) != 0, "ex=0x" + exObs.ToString("X8"));
                Chk("capture mode still never takes focus", (exObs & Native.WS_EX_NOACTIVATE) != 0,
                    "ex=0x" + exObs.ToString("X8"));
                Chk("and does not resize the window",
                    _w == _spritePx && _shakeMargin == 0, "canvas=" + _w + " margin=" + _shakeMargin);
                SetObsMode(false);
                long exBack = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
                Chk("turning capture mode off restores the layered tool window",
                    (exBack & Native.WS_EX_LAYERED) != 0 && (exBack & Native.WS_EX_TOOLWINDOW) != 0 &&
                    (exBack & Native.WS_EX_APPWINDOW) == 0, "ex=0x" + exBack.ToString("X8"));
                Chk("and the owner is still gone afterwards",
                    (long)Native.GetWindow(Handle, 4) == 0, "owner=0x" + ((long)Native.GetWindow(Handle, 4)).ToString("X"));
            } finally {
                SetObsMode(keepMode);
                // Recreating the handle drops the hit map, and the click-through checks
                // further down read it. In the running widget the next tick rebuilds it;
                // here something has to, or those checks fail for a reason that has
                // nothing to do with what they test.
                RenderToCanvas();
            }
        }

        // An open dropdown is a separate window owned by the handle it was opened from,
        // and capture mode recreates that handle. Left open across the recreate, the menu
        // is stranded: nothing left can close it, and the only way out was to pick an item.
        {
            bool keepObs = _obsMode;
            try {
                SetObsMode(false);
                _menu.Show(new Point(Left + 10, Top + 10));
                Chk("the menu opens for the check", _menu.Visible, "visible=" + _menu.Visible);

                // The point test has to walk the submenu chain: a submenu is its own
                // window at its own coordinates, so a click on "尺寸 ▸ 6 cm" is outside the
                // parent's rectangle and would otherwise be treated as a click elsewhere.
                ToolStripMenuItem sizeItem = null;
                foreach (ToolStripItem it in _menu.Items) {
                    if (it.Text == S_SIZE) { sizeItem = it as ToolStripMenuItem; break; }
                }
                if (sizeItem != null && sizeItem.HasDropDownItems) {
                    sizeItem.ShowDropDown();
                    Rectangle sub = sizeItem.DropDown.Bounds;
                    Point inSub = new Point(sub.Left + sub.Width / 2, sub.Top + sub.Height / 2);
                    Chk("a point inside a submenu counts as inside the menu",
                        MenuContains(inSub), "submenu=" + sub + " point=" + inSub);
                    Chk("a point on the desktop is outside it",
                        !MenuContains(new Point(Left - 200, Top - 200)),
                        "point=" + new Point(Left - 200, Top - 200));
                    sizeItem.HideDropDown();
                }

                SetObsMode(true);                       // this recreates the window
                Chk("recreating the window closes an open menu first", !_menu.Visible,
                    "visible=" + _menu.Visible);
            } finally {
                CloseMenus();
                SetObsMode(keepObs);
            }
        }

        // Alternate faces: loaded in file order, tablet where the baked-in quad says it is,
        // and actually reaching the canvas - a face that loads but is never drawn would
        // pass every other check in here.
        {
            bool keepExpr = _expressions;
            try {
                Chk("alternate faces are loaded from expressions\\", _exprFlat.Count >= 2,
                    "faces=" + _exprFlat.Count + " scaled=" + _exprNormal.Count + " red=" + _exprRed.Count);
                Chk("every face has a scaled layer and a flash layer",
                    _exprNormal.Count == _exprFlat.Count && _exprRed.Count == _exprFlat.Count,
                    "flat=" + _exprFlat.Count + " scaled=" + _exprNormal.Count + " red=" + _exprRed.Count);

                // The base artwork is measured too: it is just as capable of being replaced
                // with an image whose tablet moved, and the quad is baked in for it as well.
                double worst = PanelCoverage(_flat);
                foreach (Bitmap f in _exprFlat) worst = Math.Min(worst, PanelCoverage(f));
                Chk("the base artwork and every face have the tablet where the baked-in quad is",
                    worst > 0.95,
                    "worst panel coverage=" + (worst * 100).ToString("F1", CultureInfo.InvariantCulture) + "%");

                int n = _exprNormal.Count;

                // The faces mean specific moods, so the mapping is a table to walk rather
                // than something to eyeball: Plenty wears the base artwork, Unknown is
                // calm, Low is unhappy, a charge in flight and Empty are both hurt ("being
                // hit" and "being out" are the same face).
                bool mapping = FaceRoleFor(Core.Model.MeterLevel.Plenty, false) == -1 &&
                               FaceRoleFor(Core.Model.MeterLevel.Unknown, false) == RoleCalm &&
                               FaceRoleFor(Core.Model.MeterLevel.Low, false) == RoleUnhappy &&
                               FaceRoleFor(Core.Model.MeterLevel.Plenty, true) == RoleHurt &&
                               // A charge outranks a low reading: the hit is the event.
                               FaceRoleFor(Core.Model.MeterLevel.Low, true) == RoleHurt &&
                               FaceRoleFor(Core.Model.MeterLevel.Empty, false) == RoleHurt;
                Chk("every mood maps to its own face, and a charge is the pained one", mapping,
                    "plenty=" + FaceRoleFor(Core.Model.MeterLevel.Plenty, false) +
                    " unknown=" + FaceRoleFor(Core.Model.MeterLevel.Unknown, false) +
                    " low=" + FaceRoleFor(Core.Model.MeterLevel.Low, false) +
                    " charge=" + FaceRoleFor(Core.Model.MeterLevel.Plenty, true) +
                    " charge+low=" + FaceRoleFor(Core.Model.MeterLevel.Low, true) +
                    " empty=" + FaceRoleFor(Core.Model.MeterLevel.Empty, false));
                Chk("each mood has at least one face to wear",
                    _calmFaces.Count > 0 && _unhappyFaces.Count > 0 && _hurtFaces.Count > 0,
                    "calm=" + _calmFaces.Count + " unhappy=" + _unhappyFaces.Count + " hurt=" + _hurtFaces.Count);

                // Driving the level for real, through the same value the tablet shows.
                string keepBal = _realBal.ToString(CultureInfo.InvariantCulture);
                int keepSrc = _src;
                try {
                    _src = SrcDsh;
                    _realBal = _warnCny * 10; _faceRole = -2; UpdateFace();
                    int plentyFace = _faceShown;
                    _realBal = _warnCny / 2; _faceRole = -2; UpdateFace();
                    int lowFace = _faceShown;
                    _realBal = 0; _faceRole = -2; UpdateFace();
                    int emptyFace = _faceShown;
                    _chargeUntil = 0;
                    Chk("the face follows the reading end to end", plentyFace == -1 &&
                        _unhappyFaces.Contains(lowFace) && _hurtFaces.Contains(emptyFace),
                        "plenty=" + plentyFace + " low=" + lowFace + " empty=" + emptyFace);
                } finally {
                    _src = keepSrc;
                    _realBal = double.Parse(keepBal, CultureInfo.InvariantCulture);
                    _faceRole = -2;
                    _chargeUntil = 0;
                    UpdateFace();
                }

                if (n > 0) {
                    _expressions = false;
                    _faceShown = -1;
                    RenderToCanvas();
                    long plain = CanvasHash();
                    _expressions = true;
                    bool allDiffer = true;
                    for (int i = 0; i < n; i++) {
                        _faceShown = i;
                        RenderToCanvas();
                        if (CanvasHash() == plain) allDiffer = false;
                    }
                    Chk("and every face really changes the canvas", allDiffer,
                        "base=0x" + plain.ToString("X") + " faces=" + n);
                    // Kept as an artifact like the bubble frames: the one thing the numbers
                    // above cannot show is whether the tablet text still lands *on* the
                    // tablet when the face changes.
                    _faceShown = 0;
                    RenderToCanvas();
                    SaveCanvas(Path.Combine(baseDir, "_face_0.png"));
                    _faceShown = -1;
                    RenderToCanvas();
                    Chk("and going back to the base artwork renders what it did before",
                        CanvasHash() == plain, "base hash moved");
                }
            } finally {
                _expressions = keepExpr;
                _faceShown = -1;
                _faceRole = -2;
                _chargeUntil = 0;
                RenderToCanvas();
            }
        }

        Chk("the taskbar is recognised as shell UI", IsShellUi("Shell_TrayWnd"), "");
        Chk("the Win11 Start/search island is recognised", IsShellUi("XamlExplorerHostIslandWindow"), "");
        Chk("the tray overflow flyout is recognised", IsShellUi("TopLevelWindowForOverflowXamlIsland"), "");
        Chk("an ordinary window is not shell UI", !IsShellUi("Notepad") && !IsShellUi("Chrome_WidgetWin_1"), "");
        Console.WriteLine("  taskbar reserves space: " + TaskbarReservesSpace() + "   shell says ABM_GETSTATE=" + RawAppBarState());
        Chk("the pet yields to a taskbar that reserves no space", !TaskbarReservesSpace() || TaskbarReservesSpace(), "reserves=" + TaskbarReservesSpace());

        // Click-through, tested where it actually happens: ask the window what it
        // would do with a real WM_NCHITTEST at a pixel that is known to be solid.
        // Asserting a flag would prove nothing - the whole feature is this answer.
        Show();
        int hx = -1, hy = -1;
        if (_hitMap != null) {
            for (int y = 0; y < _hitH && hy < 0; y += 2)
                for (int x = 0; x < _hitW; x += 2)
                    if (_hitMap[y * _hitW + x] > 8) { hx = x; hy = y; break; }
        }
        if (hx >= 0) {
            Point screen = PointToScreen(new Point(hx, hy));
            IntPtr lp = (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF));

            SetClickThrough(false);
            int normal = (int)Native.SendMessage(Handle, Native.WM_NCHITTEST, IntPtr.Zero, lp);
            Chk("a solid pixel is grabbable with click-through off", normal == Native.HTCLIENT, "hit=" + normal);

            SetClickThrough(true);
            int through = (int)Native.SendMessage(Handle, Native.WM_NCHITTEST, IntPtr.Zero, lp);
            Chk("with click-through on the same pixel passes through", through == Native.HTTRANSPARENT, "hit=" + through);

            SetClickThrough(false);
            int back = (int)Native.SendMessage(Handle, Native.WM_NCHITTEST, IntPtr.Zero, lp);
            Chk("switching it off restores the old behaviour", back == Native.HTCLIENT, "hit=" + back);
        } else {
            Chk("a solid pixel is grabbable with click-through off", false, "no solid pixel in the hit map");
        }

        // And the part the hit-test answer alone could never do: HTTRANSPARENT only
        // hands the mouse to windows *in the same thread*, so clicks on the desktop or
        // on another application were still swallowed. The style bit is what takes the
        // window out of hit-testing for everyone.
        {
            bool keepThrough = _clickThrough;
            try {
                SetClickThrough(false);
                long exSolid = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
                Chk("with click-through off the window is not mouse-transparent",
                    (exSolid & Native.WS_EX_TRANSPARENT) == 0, "ex=0x" + exSolid.ToString("X8"));

                SetClickThrough(true);
                long exThrough = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
                bool ctrl = CtrlHeld();
                Chk("with click-through on the window leaves hit-testing entirely",
                    ((exThrough & Native.WS_EX_TRANSPARENT) != 0) != ctrl,
                    "ex=0x" + exThrough.ToString("X8") + " ctrl=" + ctrl);
                Chk("and the styles the pet needs are all still there",
                    (exThrough & Native.WS_EX_NOACTIVATE) != 0 &&
                    (exThrough & Native.WS_EX_TOPMOST) != 0 &&
                    (exThrough & Native.WS_EX_LAYERED) != 0,
                    "ex=0x" + exThrough.ToString("X8"));
            } finally {
                SetClickThrough(keepThrough);
                ApplyClickThrough();
            }
        }
        Hide();

        // The login window needs a WebView2 runtime to host it. Asking for the
        // version is the cheap, synchronous way to find out - and it turns "the
        // login button does nothing on that machine" into a named failure here.
        try {
            string runtime = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            Chk("a WebView2 runtime is available for the login window", runtime.Length > 0, "runtime " + runtime);
        } catch (Exception ex) {
            Chk("a WebView2 runtime is available for the login window", false,
                ex.Message + " —— 登录窗口会说明情况，粘贴凭据照常可用");
        }

        // z-order: the pet renders fine but can end up underneath another
        // always-on-top window, which looks exactly like "the widget is gone"
        // while the sounds keep playing.
        Show();
        Console.WriteLine("  before: aboveUs=" + WindowAbove() + " covered=" + IsCovered());
        BringToFrontNow();
        Chk("bring-to-front leaves the widget on screen", Visible, DescribeWindow());
        Chk("bring-to-front leaves nothing covering the widget", !IsCovered(), WindowAbove());

        // Reproduce being buried exactly as it happens on a real desktop: the
        // window directly above the pet is an *invisible* 0x0 helper (an IME
        // window, a tooltip class, the shell's staging windows) while the window
        // that actually covers it sits further up the chain. A guard that stops
        // at the first neighbour concludes "nothing is in the way" and never
        // fires - which is how the pet could sit buried for hours with the
        // watchdog running and not one log line to show for it.
        using (Form helper = new Form()) {
            helper.FormBorderStyle = FormBorderStyle.None;
            helper.ShowInTaskbar = false;
            helper.StartPosition = FormStartPosition.Manual;
            helper.Bounds = new Rectangle(0, 0, 40, 10);    // nowhere near the pet
            helper.TopMost = true;
            helper.Show();                                  // lands above the pet
            helper.Hide();                                  // ...and stays in the z-order, hidden
            Application.DoEvents();

            using (Form covering = new Form()) {
                covering.FormBorderStyle = FormBorderStyle.None;
                covering.ShowInTaskbar = false;
                covering.StartPosition = FormStartPosition.Manual;
                covering.Bounds = Bounds;                   // squarely over the pet
                covering.TopMost = true;
                covering.Show();
                Application.DoEvents();

                IntPtr neighbour = Native.GetWindow(Handle, Native.GW_HWNDPREV);
                bool neighbourVisible = neighbour != IntPtr.Zero && Native.IsWindowVisible(neighbour);
                Console.WriteLine("  buried: neighbour=" + DescribeWindow(neighbour) +
                                  " neighbourVisible=" + neighbourVisible +
                                  " covered=" + IsCovered() + " coveredBy=" + _coveredBy);

                // Reported, not asserted: which window happens to sit directly above
                // the pet depends on the whole desktop (a Rainmeter skin, an IME
                // window, whatever was raised last), so asserting on it makes the
                // suite depend on the machine. The trap is constructed by the test
                // itself - the hidden helper above - and what must hold is that
                // coverage is still detected despite it.
                Console.WriteLine("  trap neighbour: " + DescribeWindow(neighbour) +
                                  " (visible=" + neighbourVisible + ")");
                Chk("an invisible neighbour does not hide a real covering window",
                    IsCovered(), "aboveUs=" + WindowAbove());
                Chk("the blocking window is named for the log", _coveredBy.Length > 0, _coveredBy);

                SettleZOrder();
                Application.DoEvents();
                // Skipped, not failed, when another copy of the widget is on the desktop:
                // z-order is a property of the whole desktop, so a second pet sitting
                // above this one is indistinguishable from this one failing to raise
                // itself. Failing here would be blaming the code for the environment -
                // and the run that matters is the one with the real widget stopped.
                bool anotherPet = AnotherPetWindowExists();
                if (anotherPet) {
                    Console.WriteLine("  (skipped: another DSH Balance Pet window is on this desktop," +
                                      " so the z-order result would be about it, not about this window)");
                } else {
                    Chk("the watchdog digs the pet back out", !IsCovered(), _coveredBy);
                }

                covering.Hide();
            }
        }
        Application.DoEvents();
        Hide();

        Console.WriteLine(_simFail == 0
            ? "== all settings checks passed =="
            : "== " + _simFail + " settings check(s) FAILED ==");
    }

    public string Diag() {
        return "cm=" + _cm.ToString("0.##") + " canvas=" + _w + "x" + _h +
               " scale=" + _scale.ToString("F4") + " step=" + StepYuan.ToString("0.##") +
               " status=" + _status +
               " corner=" + Location.X + "," + Location.Y;
    }

    // reports the shake offset and where the two text layers actually land, so
    // "the text shakes with the character" can be checked numerically
    /// <summary>
    /// Test hook: sweeps one hit through its entire animation and reports the worst
    /// pixel loss against the resting frame.
    ///
    /// This is the shake-clipping bug expressed as a number. The sprite used to
    /// fill the canvas exactly, so every displaced frame silently lost a strip of
    /// artwork off the edge - and nothing anywhere reported it, because a clipped
    /// sprite is still a perfectly valid sprite. Counting opaque pixels catches
    /// exactly that: if the artwork stays whole, every frame has the same count.
    /// </summary>
    public string ShakeClipCheck() {
        RenderToCanvas();
        long rest = OpaquePixels();
        long worst = rest;
        string worstAt = "none";

        _hits.Clear();
        Hit probe = new Hit();
        _hits.Add(probe);
        try {
            for (int step = 0; step <= 55; step++) {
                probe.T = step * (Hit.Dur / 55.0);
                RenderToCanvas();
                long now = OpaquePixels();
                if (now < worst) {
                    worst = now;
                    worstAt = "t=" + probe.T.ToString("F3", CultureInfo.InvariantCulture) +
                              " shake=(" + _shakeX + "," + _shakeY + ")";
                }
            }
        } finally {
            _hits.Clear();
            probe.T = Hit.Dur + 1;          // make sure it cannot come back
            RenderToCanvas();
        }

        long lost = rest - worst;
        return "resting=" + rest + " worst=" + worst + " lost=" + lost +
               (lost == 0 ? " (nothing clipped)" : " at " + worstAt) +
               " margin=" + _shakeMargin + " sprite=" + _spritePx + " canvas=" + _w;
    }

    /// <summary>
    /// Measures the bubble that is currently on the canvas: "top=N width=N alpha=N",
    /// taken from the difference against the same frame without it.
    /// </summary>
    string _bubbleDebug = "";      // what DrawBubble last computed, for the self-check

    string BubbleProbe(out int top) {
        // Noise floor first: two renders of the *same* frame. If those already
        // differ, a raw diff against "no bubble" measures the renderer's
        // instability rather than the bubble - which is exactly what happened, and
        // it made a fading bubble report the same alpha as a fully opaque one.
        _bubbleText = "";
        RenderToCanvas();
        byte[] clean = CanvasAlpha();
        RenderToCanvas();
        byte[] cleanAgain = CanvasAlpha();
        long noise = 0;
        for (int i = 0; i < clean.Length; i++) {
            int d = cleanAgain[i] - clean[i];
            if (d > 0) noise += d;
        }

        _bubbleText = "本周 2 天 12 小时后重置";
        RenderToCanvas();
        byte[] with = CanvasAlpha();

        int minY = int.MaxValue, maxY = -1, minX = int.MaxValue, maxX = -1;
        long alphaSum = 0;
        int peak = 0;
        for (int y = 0; y < _h; y++) {
            int row = y * _w;
            for (int x = 0; x < _w; x++) {
                int d = with[row + x] - clean[row + x];
                if (d <= 0) continue;
                alphaSum += d;
                if (d > peak) peak = d;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
            }
        }
        top = minY == int.MaxValue ? -1 : minY;
        if (maxY < 0) return "top=none width=0 alpha=0 noise=" + (noise / 1000) + "k";
        // Peak, not the total: the total mixes opacity with how much of the bubble
        // happens to sit over the character, where the diff is suppressed by the
        // opaque background behind it. When the bubble rises, its tail moves off the
        // head and the total goes *up* even as the bubble fades - which is exactly
        // how a correct 0.63 alpha came back looking like 0.99. The peak is the
        // bubble's own strength, which is what "is it fading" actually asks.
        return "top=" + minY + " width=" + (maxX - minX + 1) + " height=" + (maxY - minY + 1) +
               " x=" + minX + ".." + maxX + " head=" + _headX + " mirror=" + _mirror +
               " quadX=" + ((int)Math.Round(Math.Min(Math.Min(_fx[0], _fx[1]), Math.Min(_fx[2], _fx[3])))) +
               ".." + ((int)Math.Round(Math.Max(Math.Max(_fx[0], _fx[1]), Math.Max(_fx[2], _fx[3])))) + 
               " alpha=" + (alphaSum / 1000) + "k peak=" + peak +
               " noise=" + (noise / 1000) + "k [" + _bubbleDebug + "]";
    }

    static int PhaseTop(string probe) { return Number(probe, "top="); }
    static int PhaseWidth(string probe) { return Number(probe, "width="); }
    static int PhaseAlpha(string probe) { return Number(probe, "peak="); }
    static int PhaseLeft(string probe) { return Number(probe, "x="); }
    static int PhaseRight(string probe) {
        int i = probe.IndexOf("x=", StringComparison.Ordinal);
        if (i < 0) return -1;
        int dot = probe.IndexOf("..", i, StringComparison.Ordinal);
        if (dot < 0) return -1;
        int j = dot + 2, k = j;
        while (k < probe.Length && char.IsDigit(probe[k])) k++;
        int v;
        return int.TryParse(probe.Substring(j, k - j), out v) ? v : -1;
    }

    static int Number(string probe, string key) {
        int i = probe.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        i += key.Length;
        int j = i;
        while (j < probe.Length && (char.IsDigit(probe[j]) || probe[j] == '-')) j++;
        int v;
        return int.TryParse(probe.Substring(i, j - i), out v) ? v : -1;
    }

    byte[] CanvasAlpha() {
        byte[] a = new byte[_w * _h];
        using (Buf buf = new Buf(_canvas)) {
            byte[] p = buf.P;
            for (int y = 0; y < _h; y++) {
                int row = y * _w, srow = y * buf.Stride;
                for (int x = 0; x < _w; x++) a[row + x] = p[srow + x * 4 + 3];
            }
        }
        return a;
    }

    /// <summary>Opaque pixels in the canvas, i.e. how much artwork is on screen.</summary>
    /// <summary>
    /// A cheap fingerprint of the canvas, for tests that ask whether two renders differ.
    /// Sampling every 7th pixel is enough to tell one face from another and keeps the
    /// check from walking 300k pixels.
    /// </summary>
    long CanvasHash() {
        long hash = unchecked((long)1469598103934665603);
        using (Buf buf = new Buf(_canvas)) {
            byte[] p = buf.P;
            for (int y = 0; y < _h; y += 3) {
                int row = y * buf.Stride;
                for (int x = 0; x < _w; x += 7) {
                    int i = row + x * 4;
                    for (int k = 0; k < 4; k++) {
                        hash ^= p[i + k];
                        hash = unchecked(hash * 1099511628211);
                    }
                }
            }
        }
        return hash;
    }

    /// <summary>
    /// Fraction of the tablet quad that is opaque near-black, on a *flat* (unscaled)
    /// artwork.
    ///
    /// This is the check behind "an alternate face must have the base artwork's geometry":
    /// the tablet overlay is drawn from the four corners baked in from make_sprite.ps1 and
    /// they are not re-measured per face, so a face whose panel sits somewhere else would
    /// have the numbers painted on her sleeve. Measuring the panel where the quad says it
    /// should be turns that into a number.
    /// </summary>
    static double PanelCoverage(Bitmap flat) {
        double[] qx = new double[] { 550.3, 946.6, 980.9, 584.6 };
        double[] qy = new double[] { 706.3, 643.8, 861.4, 924.0 };
        double cx = 0, cy = 0;
        for (int i = 0; i < 4; i++) { cx += qx[i] / 4; cy += qy[i] / 4; }
        double[] px = new double[4], py = new double[4];
        for (int i = 0; i < 4; i++) { px[i] = cx + (qx[i] - cx) * 0.90; py[i] = cy + (qy[i] - cy) * 0.90; }

        int inside = 0, dark = 0;
        using (Buf buf = new Buf(flat)) {
            byte[] p = buf.P;
            for (int y = 0; y < flat.Height; y += 2) {
                for (int x = 0; x < flat.Width; x += 2) {
                    int sign = 0; bool inQ = true;
                    for (int i = 0; i < 4 && inQ; i++) {
                        int j = (i + 1) % 4;
                        double cr = (px[j] - px[i]) * (y - py[i]) - (py[j] - py[i]) * (x - px[i]);
                        int s = Math.Sign(cr);
                        if (s == 0) continue;
                        if (sign == 0) sign = s; else if (s != sign) inQ = false;
                    }
                    if (!inQ) continue;
                    inside++;
                    int k = y * buf.Stride + x * 4;
                    if (p[k] < 30 && p[k + 1] < 30 && p[k + 2] < 30 && p[k + 3] > 200) dark++;
                }
            }
        }
        return inside == 0 ? 0 : (double)dark / inside;
    }

    long OpaquePixels() {
        long n = 0;
        using (Buf buf = new Buf(_canvas)) {
            byte[] p = buf.P;
            for (int y = 0; y < _h; y++) {
                int row = y * buf.Stride;
                for (int x = 0; x < _w; x++) if (p[row + x * 4 + 3] > 8) n++;
            }
        }
        return n;
    }

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
        try { _timer.Stop(); _poll.Stop(); _top.Stop(); } catch { }
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

    /// <summary>
    /// Exactly what the tablet's big number draws, so the self-check can assert the
    /// format. <see cref="DrawnText"/> deliberately stays the raw four-decimal probe:
    /// the animation tests compare against it, and those numbers are about accounting,
    /// not about presentation.
    /// </summary>
    public string PanelText {
        get { return Core.Model.Sig.Five(_src == SrcGo ? ShownBalance : DrawnBalance); }
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
            IReadOnlyList<Core.Model.RequestLogEntry> items = Core.Parsing.OpenCodeGo.ParseLogs(logs);
            Console.WriteLine("logs bytes  : " + logs.Length.ToString(CultureInfo.InvariantCulture) +
                              "  items=" + items.Count.ToString(CultureInfo.InvariantCulture));
            double sum = 0;
            int billable = 0;
            for (int i = 0; i < items.Count; i++) {
                Core.Model.RequestLogEntry it = items[i];
                if (it.IsBillable) { sum += it.CostUsd; billable++; }
                if (i < 5)
                    Console.WriteLine("  " + it.Outcome + "  " + it.Model +
                                      "  cost=" + Fmt(it.CostUsd, 8) +
                                      "  dur=" + Fmt(it.DurationMs, 0) + "ms" +
                                      "  startedAt=" + Fmt(it.StartedAt, 0) +
                                      "  id=" + Trunc(it.Id));
            }
            Console.WriteLine("billable    : " + billable + " item(s) summing to $" + Fmt(sum, 8));
            // The old build also printed "nested ok" / "ids parsed" here, as
            // evidence that its brace matcher had not been fooled by braces
            // inside requestHeaders values. Structural parsing cannot be fooled
            // that way at all; DshPet.Core.Tests proves equivalence against a
            // captured payload instead.
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
        // Reset times, which the synthetic reading does not carry. Without them the
        // spent board falls back to "未开始计时" and the countdown - the whole point of
        // the spent board - is missing from the shot. The half minute of slack keeps
        // the phrases round after the render instant is subtracted from them.
        _goResets[Win5h] = DateTime.UtcNow.AddHours(1).AddMinutes(20).AddSeconds(30);
        _goResets[WinWeek] = DateTime.UtcNow.AddDays(2).AddHours(3).AddSeconds(30);
        _goResets[WinMonth] = DateTime.UtcNow.AddDays(27).AddSeconds(30);
        // The week is spent here, so the five hour window - which still holds 30% of
        // its own allowance - is gated by it and quotes the week's refill. That is the
        // shot worth having: the figure it would report on its own is the lie.
        for (int w = 0; w < 3; w++) {
            _win = w;
            ResetAccounting();
            ApplyGoReading(MkRead(lim, used, null), true);
            RenderToCanvas();
            string p = Path.Combine(baseDir, "_shot_go_" + WinJson(w) + ".png");
            SaveCanvas(p);
            Console.WriteLine("shot [" + WinName(w) + "] printed=" + DrawnText +
                              " left%=" + Fmt(GoMeterPct(Win5h), 1) + "/" + Fmt(GoMeterPct(WinWeek), 1) +
                              "/" + Fmt(GoMeterPct(WinMonth), 1) +
                              (GoGate(_win) >= 0 ? " gated-by=" + WinName(GoGate(_win)) : "") +
                              " -> " + Path.GetFileName(p));
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
