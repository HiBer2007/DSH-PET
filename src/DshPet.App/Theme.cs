using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshPet.App;

/// <summary>
/// Dark mode for the widget's own windows: the right-click menu, the settings window and
/// the small dialogs.
///
/// WinForms has no dark mode whatsoever - a menu or a form keeps the light system colours
/// no matter what Windows is set to - so the whole palette has to be applied by hand. This
/// is the only place that knows a colour; the menus and forms just ask it.
///
/// "Auto" reads the same registry value the Settings app writes when you switch Windows to
/// dark, so the widget looks right without being told. Applying a theme is also how it is
/// switched back: every control is assigned the light colour explicitly rather than being
/// left alone, because a control that was painted dark once does not forget it.
/// </summary>
internal static class Theme
{
    public enum Mode { Auto = 0, Dark = 1, Light = 2 }

    /// <summary>Tag on a label that should use the quieter text colour.</summary>
    public const string SubtleTag = "subtle";

    public static Mode Current { get; private set; } = Mode.Auto;

    /// <summary>Parses "auto" / "dark" / "light"; anything else keeps the current choice.</summary>
    public static void Use(string name)
    {
        if (name == null) return;
        switch (name.Trim().ToLowerInvariant())
        {
            case "dark": Current = Mode.Dark; break;
            case "light": Current = Mode.Light; break;
            case "auto": Current = Mode.Auto; break;
        }
    }

    public static string Name
    {
        get { return Current == Mode.Dark ? "dark" : Current == Mode.Light ? "light" : "auto"; }
    }

    /// <summary>
    /// HKCU\...\Themes\Personalize\AppsUseLightTheme is 0 when the user picked dark for
    /// apps. A missing key means an older Windows, which only had light.
    /// </summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (key == null) return false;
                object value = key.GetValue("AppsUseLightTheme");
                if (value == null) return false;
                return Convert.ToInt32(value) == 0;
            }
        }
        catch { return false; }
    }

    public static bool Dark
    {
        get { return Current == Mode.Dark || (Current == Mode.Auto && SystemPrefersDark()); }
    }

    // ---------------------------------------------------------------- palette ---

    public static Color Window { get { return Dark ? Color.FromArgb(32, 32, 36) : SystemColors.Control; } }
    public static Color Surface { get { return Dark ? Color.FromArgb(45, 45, 53) : SystemColors.Window; } }
    public static Color Border { get { return Dark ? Color.FromArgb(74, 74, 86) : SystemColors.ControlDark; } }
    public static Color Text { get { return Dark ? Color.FromArgb(236, 236, 242) : SystemColors.ControlText; } }
    public static Color Subtle { get { return Dark ? Color.FromArgb(152, 154, 166) : SystemColors.GrayText; } }
    public static Color Accent { get { return Dark ? Color.FromArgb(102, 156, 232) : SystemColors.Highlight; } }
    public static Color MenuBack { get { return Dark ? Color.FromArgb(38, 38, 45) : SystemColors.Control; } }
    public static Color MenuSelect { get { return Dark ? Color.FromArgb(62, 68, 86) : SystemColors.Highlight; } }

    // ----------------------------------------------------------------- forms ---

    /// <summary>Applies the palette to a control and everything under it.</summary>
    public static void Apply(Control root)
    {
        if (root == null) return;
        Style(root);
        foreach (Control child in root.Controls) Apply(child);
    }

    /// <summary>Marks a label as secondary text (the grey hints).</summary>
    public static void MarkSubtle(Control label)
    {
        if (label != null) label.Tag = SubtleTag;
    }

    static void Style(Control c)
    {
        bool subtle = (c.Tag as string) == SubtleTag;

        // Containers first: a GroupBox or TabPage paints its own background, and the
        // children inherit whatever it leaves behind. The TabControl is tested before the
        // others on purpose - it needs its headers taken over as well, and an "else if"
        // chain that lists it second never reaches that code.
        if (c is TabControl)
        {
            c.BackColor = Window;
            c.ForeColor = Text;
            TabControl tabs = (TabControl)c;
            if (tabs.DrawMode != TabDrawMode.OwnerDrawFixed)
            {
                tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                tabs.DrawItem += DrawTab;
            }
            tabs.Invalidate();
        }
        else if (c is Form || c is GroupBox || c is TabPage || c is Panel)
        {
            c.BackColor = Window;
            c.ForeColor = Text;
        }

        if (c is Label || c is LinkLabel)
        {
            c.BackColor = Color.Transparent;
            c.ForeColor = subtle ? Subtle : Text;
        }
        else if (c is Button)
        {
            Button b = (Button)c;
            // Flat is what makes BackColor mean anything on a button; the stock look
            // ignores it entirely, which is why dark themes usually skip buttons.
            b.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
            b.UseVisualStyleBackColor = !Dark;
            if (Dark) b.FlatAppearance.BorderColor = Border;
            b.BackColor = Dark ? Surface : SystemColors.Control;
            b.ForeColor = Text;
        }
        else if (c is CheckBox || c is RadioButton)
        {
            c.BackColor = Color.Transparent;
            c.ForeColor = Text;
        }
        else if (c is TextBox || c is ListBox || c is RichTextBox || c is CheckedListBox)
        {
            c.BackColor = Surface;
            c.ForeColor = Text;
            if (c is TextBox) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
        }
        else if (c is UpDownBase)                       // NumericUpDown / DomainUpDown
        {
            c.BackColor = Surface;
            c.ForeColor = Text;
            // Its inner edit box is themed by the recursion; the spinner buttons are drawn
            // by the system and stay light, which is the one thing WinForms will not give
            // up without a custom control.
        }
    }

    /// <summary>
    /// A TabControl whose strip belongs to the theme.
    ///
    /// The tabs themselves can be owner-drawn, but the strip *around* them - the whole
    /// width to the right of the last tab, and a sliver at the left - is painted by the
    /// system in the light control colour, and no property changes it. Answering
    /// WM_ERASEBKGND with the theme's colour is the documented way around that; the page
    /// still paints itself over the top afterwards.
    /// </summary>
    public sealed class DarkTabControl : TabControl
    {
        const int WM_ERASEBKGND = 0x0014;

        protected override void WndProc(ref Message m)
        {
            if (Dark && m.Msg == WM_ERASEBKGND && m.WParam != IntPtr.Zero)
            {
                using (Graphics g = Graphics.FromHdc(m.WParam))
                using (Brush brush = new SolidBrush(Window))
                    g.FillRectangle(brush, ClientRectangle);
                m.Result = (IntPtr)1;               // handled: do not let the system erase it
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// Tab headers, drawn by hand: the stock ones ignore every colour you set and would
    /// stay light grey on a dark page.
    /// </summary>
    static void DrawTab(object sender, DrawItemEventArgs e)
    {
        TabControl tabs = (TabControl)sender;
        bool selected = e.Index == tabs.SelectedIndex;
        Rectangle r = e.Bounds;
        using (Brush b = new SolidBrush(selected ? Surface : Window))
            e.Graphics.FillRectangle(b, r);
        if (selected)
        {
            using (Pen p = new Pen(Accent, 2f))
                e.Graphics.DrawLine(p, r.Left + 2, r.Bottom - 2, r.Right - 2, r.Bottom - 2);
        }
        TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, r,
                              selected ? Text : Subtle,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // ----------------------------------------------------------------- menus ---

    /// <summary>
    /// Themes a context menu - and the tray menu, which shares this one instance.
    /// A menu is a control tree as well, so the colours go on every item, not just the
    /// strip: an unstyled item draws its own light background over the dark one.
    /// </summary>
    public static void ApplyMenu(ContextMenuStrip menu)
    {
        if (menu == null) return;
        menu.Renderer = Dark ? (ToolStripRenderer)new DarkMenuRenderer() : null;
        menu.BackColor = MenuBack;
        menu.ForeColor = Text;
        foreach (ToolStripItem item in menu.Items) StyleItem(item);
    }

    static void StyleItem(ToolStripItem item)
    {
        item.BackColor = MenuBack;
        item.ForeColor = Text;
        ToolStripMenuItem parent = item as ToolStripMenuItem;
        if (parent == null) return;
        foreach (ToolStripItem child in parent.DropDownItems) StyleItem(child);
    }

    /// <summary>
    /// Whether a menu is wearing the dark renderer.
    ///
    /// Asked through a helper because the obvious test is a trap: <c>ToolStrip.Renderer</c>
    /// never returns null - assigning null is how the stock renderer is restored, but the
    /// getter falls back to <c>ToolStripManager.Renderer</c> - so "Renderer == null" reads
    /// as "still themed" and cannot tell the two apart.
    /// </summary>
    public static bool MenuIsDark(ContextMenuStrip menu)
    {
        return menu != null && menu.Renderer is DarkMenuRenderer;
    }

    /// <summary>
    /// The menu palette. Only the entries that actually show up on a context menu are
    /// overridden; anything left alone (the image margin, for instance, which this menu
    /// never uses) keeps the stock colour rather than turning into a guess.
    /// </summary>
    sealed class DarkMenuTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return MenuBack; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemBorder { get { return MenuSelect; } }
        public override Color MenuItemSelected { get { return MenuSelect; } }
        public override Color MenuItemSelectedGradientBegin { get { return MenuSelect; } }
        public override Color MenuItemSelectedGradientEnd { get { return MenuSelect; } }
        public override Color MenuItemPressedGradientBegin { get { return MenuBack; } }
        public override Color MenuItemPressedGradientEnd { get { return MenuBack; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return MenuBack; } }
        public override Color CheckBackground { get { return MenuSelect; } }
        public override Color CheckSelectedBackground { get { return MenuSelect; } }
        public override Color ImageMarginGradientBegin { get { return MenuBack; } }
        public override Color ImageMarginGradientMiddle { get { return MenuBack; } }
        public override Color ImageMarginGradientEnd { get { return MenuBack; } }
    }

    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuTable()) { RoundedEdges = false; }

        /// <summary>
        /// The tick, drawn by hand. The stock mark is a dark bitmap, which is invisible on
        /// a dark menu - and a check mark you cannot see is worse than no menu.
        /// </summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (Brush b = new SolidBrush(MenuBack)) e.Graphics.FillRectangle(b, r);
            using (Pen p = new Pen(Accent, 2f))
            {
                e.Graphics.DrawLines(p, new Point[] {
                    new Point(r.Left + 3, r.Top + r.Height / 2),
                    new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4),
                    new Point(r.Right - 3, r.Top + 3)
                });
            }
        }
    }
}
