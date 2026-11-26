using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows.Forms;

// One open note (one tab).
class Doc
{
    public RichTextBox Rtb;
    public string FilePath;     // file on disk, if any
    public string Name;         // the name shown on the tab (independent of the file name)
    public bool NameCustom;     // true once the user renamed the tab
    public bool Dirty;
    public bool WorkbookDirty = true;
    public bool Loading;
    public string Language = "";
}

// Windows 11 style tab strip: rounded tabs, hover states, close button, "+" button.
class TabStrip : Control
{
    public List<Doc> Docs = new List<Doc>();
    public Doc Selected;
    public Func<string, Color> LanguageColor;
    public event Action<Doc> SelectRequested;
    public event Action<Doc> CloseRequested;
    public event Action<Doc> RenameRequested;
    public event Action NewRequested;

    public const int StripH = 38, TabTop = 6, PadL = 8, PadR = 8, PlusW = 34;
    const int MinTab = 96, MaxTab = 210, Radius = 8;

    static readonly Color StripBg = Color.FromArgb(243, 243, 243);
    static readonly Color Border = Color.FromArgb(224, 224, 224);
    static readonly Color TextSel = Color.FromArgb(28, 28, 28);
    static readonly Color TextDim = Color.FromArgb(96, 96, 96);

    int hoverIndex = -1;
    bool hoverClose, hoverPlus;
    int scrollX;
    string lastTip;
    readonly Font tabFont = new Font("Segoe UI", 9f);
    readonly ToolTip tip = new ToolTip();

    public TabStrip()
    {
        Height = StripH;
        Dock = DockStyle.Top;
        BackColor = StripBg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    // ----- layout -----
    int Avail() { return Math.Max(40, Width - PadL - PadR - PlusW); }
    int TabW()
    {
        int n = Math.Max(1, Docs.Count);
        return Math.Max(MinTab, Math.Min(MaxTab, Avail() / n));
    }
    bool Overflow() { return TabW() * Docs.Count > Avail(); }
    Rectangle TabRect(int i)
    {
        return new Rectangle(PadL + i * TabW() - scrollX, TabTop, TabW(), StripH - TabTop);
    }
    Rectangle PlusRect()
    {
        int total = TabW() * Docs.Count;
        int x = PadL + (total <= Avail() ? total : Avail());
        return new Rectangle(x + 2, TabTop + 2, 28, 28);
    }
    static Rectangle CloseRect(Rectangle r)
    {
        return new Rectangle(r.Right - 28, r.Y + (r.Height - 20) / 2, 20, 20);
    }
    void ClampScroll()
    {
        int max = Math.Max(0, TabW() * Docs.Count - Avail());
        if (scrollX > max) scrollX = max;
        if (scrollX < 0) scrollX = 0;
    }
    public void EnsureVisible(Doc d)
    {
        int i = Docs.IndexOf(d);
        if (i < 0) return;
        int tw = TabW(), left = i * tw, right = left + tw;
        if (left < scrollX) scrollX = left;
        else if (right > scrollX + Avail()) scrollX = right - Avail();
        ClampScroll();
        Invalidate();
    }

    int HitTab(Point p, out bool onClose)
    {
        onClose = false;
        if (p.Y < TabTop) return -1;
        if (Overflow() && p.X > PadL + Avail()) return -1;
        for (int i = 0; i < Docs.Count; i++)
        {
            Rectangle r = TabRect(i);
            if (r.Contains(p)) { onClose = CloseRect(r).Contains(p); return i; }
        }
        return -1;
    }
    public Doc DocAt(Point p)
    {
        bool oc;
        int i = HitTab(p, out oc);
        return i >= 0 ? Docs[i] : null;
    }

    // ----- shapes -----
    static GraphicsPath RoundRect(Rectangle r, int rad)
    {
        int d = rad * 2;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
    static GraphicsPath TopRound(Rectangle r, int rad)
    {
        int d = rad * 2;
        GraphicsPath p = new GraphicsPath();
        p.AddLine(r.X, r.Bottom, r.X, r.Y + rad);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom);
        return p;
    }

    // ----- painting -----
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(StripBg);
        using (Pen line = new Pen(Border))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        if (Overflow()) g.SetClip(new Rectangle(0, 0, PadL + Avail(), Height));

        int selIndex = -1;
        for (int i = 0; i < Docs.Count; i++)
        {
            if (Docs[i] == Selected) { selIndex = i; continue; }
            DrawTab(g, i, false);
        }
        if (selIndex >= 0) DrawTab(g, selIndex, true); // selected tab on top
        g.ResetClip();

        Rectangle pr = PlusRect();
        if (hoverPlus)
            using (GraphicsPath hp = RoundRect(pr, 6))
            using (Brush hb = new SolidBrush(Color.FromArgb(24, 0, 0, 0)))
                g.FillPath(hb, hp);
        using (Pen pp = new Pen(TextDim, 1.4f))
        {
            int cx = pr.X + pr.Width / 2, cy = pr.Y + pr.Height / 2;
            g.DrawLine(pp, cx - 5, cy, cx + 5, cy);
            g.DrawLine(pp, cx, cy - 5, cx, cy + 5);
        }
    }

    void DrawTab(Graphics g, int i, bool selected)
    {
        Doc d = Docs[i];
        Rectangle r = TabRect(i);
        if (r.Right < 0 || r.Left > Width) return;
        bool hover = (i == hoverIndex);
        Rectangle rr = new Rectangle(r.X, r.Y, r.Width - 3, r.Height + 3);

        if (selected)
        {
            using (GraphicsPath path = TopRound(rr, Radius))
            using (Brush b = new SolidBrush(Color.White))
            using (Pen p = new Pen(Border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }
        else if (hover)
        {
            Rectangle hr = new Rectangle(rr.X, rr.Y + 2, rr.Width, r.Height - 6);
            using (GraphicsPath path = RoundRect(hr, Radius))
            using (Brush b = new SolidBrush(Color.FromArgb(20, 0, 0, 0)))
                g.FillPath(b, path);
        }

        Color accent = LanguageColor == null ? Color.Empty : LanguageColor(d.Language);
        if (!accent.IsEmpty)
            using (Brush brush = new SolidBrush(accent))
                g.FillRectangle(brush, r.X + 5, r.Y + 9, 3, r.Height - 17);

        int indicators = (d.Dirty ? 1 : 0) + (d.WorkbookDirty ? 1 : 0);
        Rectangle tr = new Rectangle(r.X + 12, r.Y, r.Width - 12 - 34 - 10 * indicators, r.Height - 2);
        TextRenderer.DrawText(g, d.Name, tabFont, tr, selected ? TextSel : TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.SingleLine);

        Rectangle c = CloseRect(new Rectangle(r.X, r.Y, r.Width, r.Height));
        int dotX = c.X - 10 * indicators;
        int dotY = c.Y + (c.Height - 7) / 2;
        if (d.Dirty)
        {
            g.FillEllipse(Brushes.Firebrick, dotX, dotY, 7, 7);
            dotX += 10;
        }
        if (d.WorkbookDirty)
            g.FillEllipse(Brushes.Black, dotX, dotY, 7, 7);

        if (selected || hover)
        {
            if (hover && hoverClose)
                using (GraphicsPath cp = RoundRect(c, 5))
                using (Brush cb = new SolidBrush(Color.FromArgb(32, 0, 0, 0)))
                    g.FillPath(cb, cp);
            using (Pen xp = new Pen(TextDim, 1.4f))
            {
                g.DrawLine(xp, c.X + 6, c.Y + 6, c.X + 14, c.Y + 14);
                g.DrawLine(xp, c.X + 14, c.Y + 6, c.X + 6, c.Y + 14);
            }
        }
    }

    // ----- mouse -----
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool oc;
        int i = HitTab(e.Location, out oc);
        bool hp = PlusRect().Contains(e.Location);
        if (i != hoverIndex || oc != hoverClose || hp != hoverPlus)
        {
            hoverIndex = i; hoverClose = oc; hoverPlus = hp;
            Invalidate();
        }
        string t = null;
        if (i >= 0)
        {
            Doc d = Docs[i];
            t = d.Name + (d.FilePath != null ? "\n" + d.FilePath : "");
            if (d.Dirty) t += "\nRed dot: unsaved tab edits";
            if (d.WorkbookDirty) t += "\nBlack dot: current tab is not saved in the workbook";
        }
        else if (hp) t = "New tab (Ctrl+N)";
        if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hoverIndex = -1; hoverClose = false; hoverPlus = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        bool onClose;
        int i = HitTab(e.Location, out onClose);
        if (e.Button == MouseButtons.Left)
        {
            if (PlusRect().Contains(e.Location)) { if (NewRequested != null) NewRequested(); return; }
            if (i >= 0)
            {
                Doc d = Docs[i];
                if (onClose) { if (e.Clicks == 1 && CloseRequested != null) CloseRequested(d); }
                else if (SelectRequested != null) SelectRequested(d);
            }
        }
        else if (e.Button == MouseButtons.Middle)
        {
            if (i >= 0 && CloseRequested != null) CloseRequested(Docs[i]);
        }
        else if (e.Button == MouseButtons.Right)
        {
            if (i >= 0 && SelectRequested != null) SelectRequested(Docs[i]);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            bool onClose;
            int i = HitTab(e.Location, out onClose);
            if (i >= 0 && !onClose) { if (RenameRequested != null) RenameRequested(Docs[i]); }
            else if (i < 0 && !PlusRect().Contains(e.Location)) { if (NewRequested != null) NewRequested(); }
        }
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scrollX -= e.Delta / 2;
        ClampScroll();
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnResize(EventArgs e)
    {
        ClampScroll();
        Invalidate();
        base.OnResize(e);
    }
}

partial class ImageNotepad : Form
{
    const string AppName = "Image Notepad";
    const int MaxImageDimension = 600;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    TabStrip strip;
    Panel content;
    bool wordWrap = true;
    int untitledCount = 0;
    ContextMenuStrip editMenu, tabMenu;
    Doc tabMenuTarget;

    // Paint hand-off state
    System.Windows.Forms.Timer paintTimer;
    List<int> paintBefore;
    int paintTicks, paintStage, paintMark;
    IntPtr paintHwnd;
    uint paintClipboardSequence;

    static string PipeName() { return "ImageNotepad_" + Environment.UserName; }

    // ---------------------------------------------------------------
    // Startup / single instance: a second launch hands its files to the
    // running window, which opens them in new tabs.
    // ---------------------------------------------------------------
    [STAThread]
    static void Main(string[] args)
    {
        List<string> files = new List<string>();
        foreach (string a in args)
        {
            try { files.Add(Path.GetFullPath(a)); } catch (Exception) { }
        }

        bool first;
        Mutex mutex = new Mutex(true, "Local\\ImageNotepad_" + Environment.UserName, out first);

        if (!first && SendToRunningInstance(files))
            return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ImageNotepad(files, first));
        GC.KeepAlive(mutex);
    }

    static bool SendToRunningInstance(List<string> files)
    {
        try
        {
            using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName(), PipeDirection.Out))
            {
                c.Connect(2000);
                StreamWriter w = new StreamWriter(c);
                foreach (string f in files) w.WriteLine(f);
                w.WriteLine();
                w.Flush();
            }
            return true;
        }
        catch (Exception)
        {
            return false; // could not reach it; just open normally
        }
    }

    void PipeLoop()
    {
        while (!IsDisposed)
        {
            try
            {
                using (NamedPipeServerStream s = new NamedPipeServerStream(PipeName(), PipeDirection.In, 1))
                {
                    s.WaitForConnection();
                    List<string> paths = new List<string>();
                    StreamReader r = new StreamReader(s);
                    string line;
                    while ((line = r.ReadLine()) != null)
                        if (line.Length > 0) paths.Add(line);

                    while (!IsHandleCreated && !IsDisposed) Thread.Sleep(50);
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate { OpenExternal(paths); }));
                }
            }
            catch (Exception)
            {
                Thread.Sleep(200);
            }
        }
    }

    void OpenExternal(List<string> paths)
    {
        foreach (string p in paths)
            if (File.Exists(p)) OpenPath(p);

        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        TopMost = true;
        TopMost = false;
        Activate();
    }

    // ---------------------------------------------------------------
    // UI
    // ---------------------------------------------------------------
    ImageNotepad(List<string> files, bool listen)
    {
        Width = 950; Height = 680;
        StartPosition = FormStartPosition.CenterScreen;

        content = new Panel();
        content.Dock = DockStyle.Fill;
        content.BackColor = Color.White;

        strip = new TabStrip { LanguageColor = GetLanguageColor };
        strip.AllowDrop = true;
        strip.SelectRequested += delegate (Doc d) { SelectDoc(d); };
        strip.CloseRequested += delegate (Doc d) { CloseTab(d); };
        strip.RenameRequested += delegate (Doc d) { RenameTab(d); };
        strip.NewRequested += delegate { NewTab(); };
        strip.DragEnter += delegate (object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        };
        strip.DragDrop += delegate (object s, DragEventArgs e)
        {
            string[] fs = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string f in fs)
                if (!IsImage(f) && File.Exists(f)) OpenPath(f);
        };

        MenuStrip menu = new MenuStrip();
        MainMenuStrip = menu;
        Controls.Add(CreateEditorArea());
        Controls.Add(strip);
        Controls.Add(menu);

        // ----- File -----
        ToolStripMenuItem mFile = AddTop(menu, "&File");
        AddItem(mFile, "&New Tab", Keys.Control | Keys.N, delegate { NewTab(); });
        AddItem(mFile, "&Open... (new tab)", Keys.Control | Keys.O, delegate { OpenDialog(); });
        mFile.DropDownItems.Add(new ToolStripSeparator());
        AddItem(mFile, "&Save...", Keys.Control | Keys.S, delegate { ChooseSave(); });
        AddItem(mFile, "Save Notebook As...", Keys.None, delegate { SaveNotebookAs(); });
        AddItem(mFile, "Move Saved File to Recycle Bin...", Keys.None, delegate { RecycleSavedFile(); });
        AddItem(mFile, "Open Crash Recovery...", Keys.None, delegate { OpenCrashRecovery(); });
        AddItem(mFile, "&Restore Autosave...", Keys.None, delegate { RestoreAutosave(); });
        AddItem(mFile, "Print Page Setup...", Keys.None, delegate { PrintPageSetup(); });
        AddItem(mFile, "Save Individual Tab &As...", Keys.Control | Keys.Shift | Keys.S, delegate { SaveAs(Cur); });
        AddItem(mFile, "Save A&ll Tabs in Notebook", Keys.Control | Keys.Alt | Keys.S, delegate { SaveNotebook(); });
        AddItem(mFile, "Save Modified Tabs as Separate Files", Keys.None, delegate { SaveAll(); });
        mFile.DropDownItems.Add(new ToolStripSeparator());
        AddItem(mFile, "&Rename Tab...", Keys.F2, delegate { RenameTab(Cur); });
        AddItem(mFile, "&Close Tab", Keys.Control | Keys.W, delegate { CloseTab(Cur); });
        mFile.DropDownItems.Add(new ToolStripSeparator());
        AddItem(mFile, "E&xit", Keys.None, delegate { Close(); });

        // ----- Edit -----
        ToolStripMenuItem mEdit = AddTop(menu, "&Edit");
        AddItem(mEdit, "&Undo", Keys.Control | Keys.Z, delegate { if (Cur.Rtb.CanUndo) Cur.Rtb.Undo(); });
        AddItem(mEdit, "&Redo", Keys.Control | Keys.Y, delegate { if (Cur.Rtb.CanRedo) Cur.Rtb.Redo(); });
        mEdit.DropDownItems.Add(new ToolStripSeparator());
        AddItem(mEdit, "Cu&t", Keys.Control | Keys.X, delegate { Cur.Rtb.Cut(); });
        AddItem(mEdit, "&Copy", Keys.Control | Keys.C, delegate { Cur.Rtb.Copy(); });
        AddItem(mEdit, "&Paste (text or image)", Keys.Control | Keys.V, delegate { PasteIntoTab(); });
        AddItem(mEdit, "Select &All", Keys.Control | Keys.A, delegate { Cur.Rtb.SelectAll(); });

        // ----- Insert -----
        ToolStripMenuItem mIns = AddTop(menu, "&Insert");
        AddItem(mIns, "&Image from file...", Keys.Control | Keys.Shift | Keys.I, delegate { InsertImageDialog(); });
        AddItem(mIns, "Send to &Paint", Keys.Control | Keys.Shift | Keys.P, delegate { SendToPaint(); });

        AddItem(mIns, "&Table...", Keys.None, delegate { InsertTable(); });
        AddOfficeMenu(menu);

        // ----- Format -----
        ToolStripMenuItem mFmt = AddTop(menu, "F&ormat");
        AddItem(mFmt, "&Font...", Keys.None, delegate { PickFont(); });
        AddItem(mFmt, "Text &Color...", Keys.None, delegate
        {
            ColorDialog d = new ColorDialog();
            if (d.ShowDialog() == DialogResult.OK)
                ApplyFormatting(Cur, delegate (RichTextBox r) { r.SelectionColor = d.Color; });
        });
        AddItem(mFmt, "&Bullet list (toggle)", Keys.None, delegate
        { ApplyFormatting(Cur, delegate (RichTextBox r) { r.SelectionBullet = !r.SelectionBullet; }); });
        ToolStripMenuItem wrap = AddItem(mFmt, "&Word Wrap", Keys.None, null);
        wrap.CheckOnClick = true;
        wrap.Checked = true;
        wrap.Click += delegate
        {
            wordWrap = wrap.Checked;
            foreach (Doc d in strip.Docs) d.Rtb.WordWrap = wordWrap;
        };
        // Bold / italic / underline: Ctrl+B / Ctrl+I / Ctrl+U are built in.

        ToolStripMenuItem printing = AddTop(menu, "&Print");
        AddItem(printing, "Print Current &Tab...", Keys.Control | Keys.P, delegate { PrintTab(); });
        AddItem(printing, "Print &All Tabs...", Keys.None, delegate { PrintTabs(new List<Doc>(strip.Docs), AppName + " - All Tabs"); });
        AddItem(printing, "Page &Setup...", Keys.None, delegate { PrintPageSetup(); });

        // ----- Help -----
        ToolStripMenuItem mHelp = AddTop(menu, "\u2699 &Settings");
        AddItem(mHelp, "&How to Use Image Notepad", Keys.F1, delegate { ShowHelp(this); });
        AddEditorSettings(mHelp);
        AddLanguageSettings(mHelp);
        InitializeLanguageTools();
        mHelp.DropDownItems.Add(new ToolStripSeparator());
        AddItem(mHelp, "&About", Keys.None, delegate
        {
            MessageBox.Show(AppName + "\nA simple notepad that keeps pictures together with your text.\n\nNotes are saved as .rtf files, so they also open in WordPad and Word.",
                "About " + AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        LoadEditorSettings();
        BuildContextMenus();

        FormClosing += delegate (object s, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                keepCrashSnapshot = true;
                WriteCrashSnapshot();
                return;
            }
            if (!ConfirmCloseNotebook()) e.Cancel = true;
        };

        foreach (string f in files)
            if (File.Exists(f)) OpenPath(f);
        if (strip.Docs.Count == 0) NewTab();
        UpdateFormTitle();
        InitializeAutosave();
        InitializeCrashRecovery(listen);

        if (listen)
        {
            Thread t = new Thread(PipeLoop);
            t.IsBackground = true;
            t.Start();
        }
    }

    static ToolStripMenuItem AddTop(MenuStrip m, string text)
    {
        ToolStripMenuItem i = new ToolStripMenuItem(text);
        m.Items.Add(i);
        return i;
    }

    static ToolStripMenuItem AddItem(ToolStripMenuItem parent, string text, Keys keys, EventHandler click)
    {
        ToolStripMenuItem i = new ToolStripMenuItem(text);
        if (keys != Keys.None) i.ShortcutKeys = keys;
        if (click != null) i.Click += click;
        parent.DropDownItems.Add(i);
        return i;
    }

    static ToolStripMenuItem CtxItem(ContextMenuStrip m, string text, string shortcutText, EventHandler click)
    {
        ToolStripMenuItem i = new ToolStripMenuItem(text);
        if (shortcutText != null) i.ShortcutKeyDisplayString = shortcutText;
        if (click != null) i.Click += click;
        m.Items.Add(i);
        return i;
    }

    // Right-click menus: one for the text area, one for the tab bar.
    void BuildContextMenus()
    {
        editMenu = new ContextMenuStrip();
        ToolStripMenuItem miUndo = CtxItem(editMenu, "Undo", "Ctrl+Z", delegate { if (Cur.Rtb.CanUndo) Cur.Rtb.Undo(); });
        ToolStripMenuItem miRedo = CtxItem(editMenu, "Redo", "Ctrl+Y", delegate { if (Cur.Rtb.CanRedo) Cur.Rtb.Redo(); });
        editMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem miCut = CtxItem(editMenu, "Cut", "Ctrl+X", delegate { Cur.Rtb.Cut(); });
        ToolStripMenuItem miCopy = CtxItem(editMenu, "Copy", "Ctrl+C", delegate { Cur.Rtb.Copy(); });
        ToolStripMenuItem miPaste = CtxItem(editMenu, "Paste", "Ctrl+V", delegate { PasteIntoTab(); });
        ToolStripMenuItem miDelete = CtxItem(editMenu, "Delete", "Del", delegate { if (Cur.Rtb.SelectionLength > 0) Cur.Rtb.SelectedText = ""; });
        editMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem miAll = CtxItem(editMenu, "Select All", "Ctrl+A", delegate { Cur.Rtb.SelectAll(); });
        editMenu.Items.Add(new ToolStripSeparator());
        CtxItem(editMenu, "Insert Image...", "Ctrl+Shift+I", delegate { InsertImageDialog(); });
        ToolStripMenuItem miPaint = CtxItem(editMenu, "Send to Paint", "Ctrl+Shift+P", delegate { SendToPaint(); });
        editMenu.Opening += delegate
        {
            RichTextBox r = Cur.Rtb;
            bool sel = r.SelectionLength > 0;
            miUndo.Enabled = r.CanUndo;
            miRedo.Enabled = r.CanRedo;
            miCut.Enabled = sel;
            miCopy.Enabled = sel;
            miDelete.Enabled = sel;
            miAll.Enabled = r.TextLength > 0;
            bool canPaste = false;
            try { canPaste = Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsData(DataFormats.Rtf); }
            catch (Exception) { }
            miPaste.Enabled = canPaste;
            miPaint.Enabled = SelectionHasImage(r);
        };

        tabMenu = new ContextMenuStrip();
        ToolStripMenuItem tRename = CtxItem(tabMenu, "Rename Tab...", "F2", delegate { if (tabMenuTarget != null) RenameTab(tabMenuTarget); });
        ToolStripMenuItem tSave = CtxItem(tabMenu, "Save Individual Tab", "Ctrl+S", delegate { if (tabMenuTarget != null) Save(tabMenuTarget); });
        tabMenu.Items.Add(new ToolStripSeparator());
        CtxItem(tabMenu, "New Tab", "Ctrl+N", delegate { NewTab(); });
        ToolStripMenuItem tClose = CtxItem(tabMenu, "Close Tab", "Ctrl+W", delegate { if (tabMenuTarget != null) CloseTab(tabMenuTarget); });
        ToolStripMenuItem tOthers = CtxItem(tabMenu, "Close Other Tabs", null, delegate { if (tabMenuTarget != null) CloseOthers(tabMenuTarget); });
        tabMenu.Opening += delegate
        {
            tabMenuTarget = strip.DocAt(strip.PointToClient(Control.MousePosition));
            bool on = tabMenuTarget != null;
            tRename.Enabled = on;
            tSave.Enabled = on;
            tClose.Enabled = on;
            tOthers.Enabled = on && strip.Docs.Count > 1;
        };
        strip.ContextMenuStrip = tabMenu;
    }

    // ---------------------------------------------------------------
    // Keyboard: Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+PageDown / Ctrl+PageUp
    // ---------------------------------------------------------------
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert)) &&
            Cur != null && Cur.Rtb.Focused)
        { PasteIntoTab(); return true; }
        if (keyData == (Keys.Control | Keys.Space) && completionEnabled && Cur != null && Cur.Language.Length > 0)
        { ShowCompletion(); return true; }
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.PageDown))
        { CycleTab(1); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab) || keyData == (Keys.Control | Keys.PageUp))
        { CycleTab(-1); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void CycleTab(int dir)
    {
        int n = strip.Docs.Count;
        if (n < 2) return;
        int i = strip.Docs.IndexOf(strip.Selected);
        SelectDoc(strip.Docs[(i + dir + n) % n]);
    }

    // ---------------------------------------------------------------
    // Tabs
    // ---------------------------------------------------------------
    Doc Cur { get { return strip.Selected; } }

    Doc NewTab()
    {
        Doc d = new Doc();
        d.Name = "Untitled " + (++untitledCount);
        d.Rtb = new RichTextBox();
        d.Rtb.Dock = DockStyle.Fill;
        d.Rtb.Font = new Font("Arial", 12);
        d.Rtb.AcceptsTab = true;
        d.Rtb.AllowDrop = true;
        d.Rtb.HideSelection = false;
        d.Rtb.BorderStyle = BorderStyle.None;
        d.Rtb.WordWrap = wordWrap;
        d.Rtb.Visible = false;
        d.Rtb.ContextMenuStrip = editMenu;   // standard right-click menu
        d.Rtb.SelectionChanged += delegate { ScheduleSpelling(); };
        d.Rtb.VScroll += delegate { lineGutter.Invalidate(); };
        d.Rtb.Resize += delegate { lineGutter.Invalidate(); };
        d.Rtb.TextChanged += delegate
        {
            lineGutter.Invalidate();
            if (Cur == d) UpdateWordCount();
            if (!d.Loading) ScheduleCrashSnapshot();
            ScheduleSpelling();
            if (!d.Loading && (!d.Dirty || !d.WorkbookDirty))
            {
                d.Dirty = true;
                d.WorkbookDirty = true;
                Touch();
            }
        };
        d.Rtb.DragEnter += delegate (object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        };
        d.Rtb.DragDrop += delegate (object s, DragEventArgs e)
        {
            string[] fs = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string f in fs)
            {
                if (IsImage(f)) InsertImage(d, f);
                else if (File.Exists(f)) OpenPath(f); // dropped notes open in their own tab
            }
        };

        content.Controls.Add(d.Rtb);
        strip.Docs.Add(d);
        ScheduleCrashSnapshot();
        SelectDoc(d);
        return d;
    }

    void SelectDoc(Doc d)
    {
        strip.Selected = d;
        foreach (Doc x in strip.Docs) x.Rtb.Visible = (x == d);
        strip.EnsureVisible(d);
        strip.Invalidate();
        lineGutter.Invalidate();
        UpdateFormTitle();
        RefreshLanguageStatus();
        UpdateWordCount();
        d.Rtb.Focus();
    }

    void Touch()
    {
        ScheduleCrashSnapshot();
        strip.Invalidate();
        lineGutter.Invalidate();
        UpdateFormTitle();
    }

    void UpdateFormTitle()
    {
        Doc d = Cur;
        Text = (d == null ? "" : (d.Dirty ? "*" : "") + d.Name + " - ") + AppName;
    }

    bool CloseTab(Doc d)
    {
        if (d.Dirty) SelectDoc(d);
        if (!ConfirmDiscard(d)) return false;
        int idx = strip.Docs.IndexOf(d);
        bool wasSelected = (strip.Selected == d);
        strip.Docs.Remove(d);
        ScheduleCrashSnapshot();
        content.Controls.Remove(d.Rtb);
        d.Rtb.Dispose();
        if (strip.Docs.Count == 0) NewTab();
        else if (wasSelected) SelectDoc(strip.Docs[Math.Min(idx, strip.Docs.Count - 1)]);
        else { strip.Invalidate(); UpdateFormTitle(); }
        return true;
    }

    void CloseOthers(Doc keep)
    {
        List<Doc> others = new List<Doc>(strip.Docs);
        others.Remove(keep);
        foreach (Doc d in others)
            if (!CloseTab(d)) break;
        if (strip.Docs.Contains(keep)) SelectDoc(keep);
    }

    void RenameTab(Doc d)
    {
        string n = Prompt(this, "Rename Tab", "Tab name:", d.Name);
        if (n != null && n.Length > 0)
        {
            if (d.Name != n) d.WorkbookDirty = true;
            d.Name = n;
            d.NameCustom = true;
            Touch();
        }
    }

    static string Prompt(IWin32Window owner, string title, string label, string value)
    {
        Form f = new Form();
        f.Text = title;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.StartPosition = FormStartPosition.CenterParent;
        f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
        f.ClientSize = new Size(360, 118);
        Label l = new Label(); l.Text = label; l.Left = 14; l.Top = 14; l.AutoSize = true;
        TextBox t = new TextBox(); t.Text = value; t.Left = 14; t.Top = 36; t.Width = 332;
        Button ok = new Button(); ok.Text = "OK"; ok.DialogResult = DialogResult.OK; ok.Left = 190; ok.Top = 78; ok.Width = 75;
        Button cancel = new Button(); cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel; cancel.Left = 271; cancel.Top = 78; cancel.Width = 75;
        f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        t.SelectAll();
        string result = f.ShowDialog(owner) == DialogResult.OK ? t.Text.Trim() : null;
        f.Dispose();
        return result;
    }

    // ---------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------
    static bool IsImage(string f)
    {
        string e = Path.GetExtension(f).ToLowerInvariant();
        return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".bmp";
    }

    static string SafeFileName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    bool ConfirmDiscard(Doc d)
    {
        if (!d.Dirty) return true;
        DialogResult r = MessageBox.Show("Save changes to \"" + d.Name + "\"?", AppName,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return Save(d);
        return true;
    }

    void OpenDialog()
    {
        OpenFileDialog d = new OpenFileDialog();
        d.Filter = "Notes and source files|*.rtf;*.txt;*.inote;*.py;*.pyw;*.js;*.jsx;*.mjs;*.cjs;*.ts;*.tsx;*.mts;*.cts;*.java;*.cs|All files (*.*)|*.*";
        d.Multiselect = true;
        if (d.ShowDialog() == DialogResult.OK)
            foreach (string f in d.FileNames) OpenPath(f);
    }

    // Opens the file in a NEW tab (or switches to it if it is already open).
    void OpenPath(string p)
    {
        if (p.EndsWith(".inote", StringComparison.OrdinalIgnoreCase))
        {
            OpenNotebook(p, false);
            return;
        }
        foreach (Doc existing in strip.Docs)
        {
            if (existing.FilePath != null &&
                string.Equals(existing.FilePath, p, StringComparison.OrdinalIgnoreCase))
            {
                SelectDoc(existing);
                return;
            }
        }

        // Reuse a pristine blank tab instead of leaving it open next to the new one.
        Doc d = Cur;
        if (d == null || d.FilePath != null || d.NameCustom || d.Dirty || d.Rtb.TextLength > 0)
            d = NewTab();

        try
        {
            d.Loading = true;
            bool rich = p.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase);
            if (rich) d.Rtb.LoadFile(p, RichTextBoxStreamType.RichText);
            else d.Rtb.Text = File.ReadAllText(p);
            d.WorkbookDirty = true;
            d.Language = DetectLanguage(p);
            RefreshLanguageStatus();
            d.FilePath = p;
            d.Name = Path.GetFileNameWithoutExtension(p); // tab shows the name, not the file name
            d.NameCustom = false;
            d.Dirty = false;
            d.Loading = false;
            Touch();
        }
        catch (Exception ex)
        {
            d.Loading = false;
            MessageBox.Show("Could not open file:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (d.FilePath == null && d.Rtb.TextLength == 0 && strip.Docs.Count > 1) CloseTab(d);
        }
    }

    bool Save(Doc d)
    {
        if (d.FilePath != null && (d.FilePath.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) || DetectLanguage(d.FilePath).Length > 0))
            return SaveTo(d, d.FilePath);
        return SaveAs(d);
    }

    bool SaveAs(Doc d)
    {
        SaveFileDialog dlg = new SaveFileDialog();
        dlg.Filter = "Rich Text with images (*.rtf)|*.rtf|Plain text, no images (*.txt)|*.txt";
        dlg.DefaultExt = "rtf";
        if (d.Language.Length > 0)
        {
            string extension = CodeLanguages[d.Language][0].Split(' ')[0];
            dlg.Filter = d.Language + " source (*" + extension + ")|*" + extension + "|" + dlg.Filter;
            dlg.DefaultExt = extension.Substring(1);
        }
        dlg.FileName = SafeFileName(d.Name);
        if (d.FilePath != null) dlg.InitialDirectory = Path.GetDirectoryName(d.FilePath);
        return dlg.ShowDialog() == DialogResult.OK && SaveTo(d, dlg.FileName);
    }

    bool SaveAll()
    {
        List<Doc> docs = new List<Doc>(strip.Docs);
        foreach (Doc d in docs)
        {
            if (!d.Dirty) continue;
            SelectDoc(d);
            if (!Save(d)) return false;
        }
        return true;
    }

    bool SaveTo(Doc d, string p)
    {
        try
        {
            if (DetectLanguage(p).Length > 0)
                File.WriteAllText(p, d.Rtb.Text);
            else if (p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                if (MessageBox.Show("Saving as .txt discards images and formatting. Continue?", AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
                d.Rtb.SaveFile(p, RichTextBoxStreamType.PlainText);
            }
            else d.Rtb.SaveFile(p, RichTextBoxStreamType.RichText);
            d.FilePath = p;
            if (!d.NameCustom)
            {
                string name = Path.GetFileNameWithoutExtension(p);
                if (d.Name != name) d.WorkbookDirty = true;
                d.Name = name;
            }
            d.Dirty = false;
            Touch();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save file:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    void ApplyFormatting(Doc d, Action<RichTextBox> apply)
    {
        string before = d.Dirty && d.WorkbookDirty ? null : d.Rtb.Rtf;
        apply(d.Rtb);
        ScheduleCrashSnapshot();
        if (before != null && before != d.Rtb.Rtf)
        {
            d.Dirty = true;
            d.WorkbookDirty = true;
            Touch();
        }
    }

    void PickFont()
    {
        FontDialog d = new FontDialog();
        if (Cur.Rtb.SelectionFont != null) d.Font = Cur.Rtb.SelectionFont;
        if (d.ShowDialog() == DialogResult.OK)
            ApplyFormatting(Cur, delegate (RichTextBox r) { r.SelectionFont = d.Font; });
    }

    // ---------------------------------------------------------------
    // Images
    // ---------------------------------------------------------------
    void InsertImageDialog()
    {
        OpenFileDialog d = new OpenFileDialog();
        d.Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp";
        d.Multiselect = true;
        if (d.ShowDialog() == DialogResult.OK)
            foreach (string f in d.FileNames) InsertImage(Cur, f);
    }

    static Size ImageInsertSize(int width, int height)
    {
        int largest = Math.Max(width, height);
        if (largest <= MaxImageDimension) return new Size(width, height);
        return new Size(Math.Max(1, (int)((long)width * MaxImageDimension / largest)),
            Math.Max(1, (int)((long)height * MaxImageDimension / largest)));
    }

    void PasteIntoTab()
    {
        try
        {
            if (!Clipboard.ContainsData(DataFormats.Rtf) && Clipboard.ContainsImage())
            {
                using (Image image = Clipboard.GetImage())
                    if (image != null) InsertImageContent(Cur, image);
                return;
            }
            Cur.Rtb.Paste();
        }
        catch (Exception ex) { ShowEditorError("Could not paste from the clipboard", ex); }
    }

    void InsertImage(Doc d, string file)
    {
        try
        {
            using (Image src = Image.FromFile(file)) InsertImageContent(d, src);
        }
        catch (Exception ex) { ShowEditorError("Could not insert image", ex); }
    }

    void InsertImageContent(Doc d, Image src)
    {
        Size size = ImageInsertSize(src.Width, src.Height);
        int w = size.Width, h = size.Height;
        if ((w != src.Width || h != src.Height) &&
            MessageBox.Show(this, string.Format("This image is {0} × {1} pixels. The maximum is {2} × {2} pixels.\n\nResize to {3} × {4} pixels and insert it?",
                src.Width, src.Height, MaxImageDimension, w, h), "Image exceeds size limit",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        using (Bitmap bmp = new Bitmap(w, h))
        using (MemoryStream png = new MemoryStream())
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);

            StringBuilder rtf = new StringBuilder(@"{\rtf1\ansi{\pict\pngblip");
            rtf.Append(@"\picw").Append(w).Append(@"\pich").Append(h);
            rtf.Append(@"\picwgoal").Append(w * 15).Append(@"\pichgoal").Append((long)h * 15);
            rtf.Append(' ');
            rtf.Append(BitConverter.ToString(png.ToArray()).Replace("-", ""));
            rtf.Append("}}");
            string pictureRtf = rtf.ToString();
            using (RichTextBox probe = new RichTextBox())
            {
                probe.Rtf = pictureRtf;
                if (!ContainsRtfImage(probe.Rtf))
                    throw new InvalidOperationException("The rich text editor could not load this image.");
            }
            ApplyFormatting(d, delegate (RichTextBox r) { r.SelectedRtf = pictureRtf; });
        }
    }

    // ---------------------------------------------------------------
    // Send to Paint: image -> memory (clipboard) -> open Paint -> paste
    // ---------------------------------------------------------------
    static bool SelectionHasImage(RichTextBox r)
    {
        if (r.SelectionLength == 0) return false;
        return ContainsRtfImage(r.SelectedRtf);
    }

    static bool ContainsRtfImage(string rtf)
    {
        return rtf != null && (rtf.Contains("\\pict") || rtf.Contains("\\objdata"));
    }

    static Bitmap GetSelectedImage(RichTextBox r)
    {
        if (!SelectionHasImage(r)) return null;
        r.Copy();
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!Clipboard.ContainsImage()) return null;
                Image img = Clipboard.GetImage();
                if (img == null) return null;
                Bitmap copy = new Bitmap(img);
                img.Dispose();
                return copy;
            }
            catch (Exception) { Thread.Sleep(100); }
        }
        return null;
    }

    static bool SetClipboardImage(Bitmap bmp)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetImage(bmp); return true; }
            catch (Exception) { Thread.Sleep(100); }
        }
        return false;
    }

    void SendToPaint()
    {
        using (Bitmap bmp = GetSelectedImage(Cur.Rtb))
        {
            if (bmp == null)
            {
                MessageBox.Show("Click a picture in the note so it is highlighted, then choose Insert > Send to Paint.\n\nIf the picture is selected together with text, select just the picture.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!SetClipboardImage(bmp))
            {
                MessageBox.Show("Could not place the image in memory (the clipboard is busy). Please try again.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        paintClipboardSequence = GetClipboardSequenceNumber();
        StartPasteIntoPaint();
    }

    void StartPasteIntoPaint()
    {
        paintBefore = new List<int>();
        foreach (Process p in Process.GetProcessesByName("mspaint")) paintBefore.Add(p.Id);
        try { Process.Start("mspaint.exe"); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start Paint:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        paintTicks = 0; paintStage = 0; paintMark = 0; paintHwnd = IntPtr.Zero;
        if (paintTimer == null)
        {
            paintTimer = new System.Windows.Forms.Timer();
            paintTimer.Interval = 250;
            paintTimer.Tick += PaintTick;
        }
        paintTimer.Start();
    }

    void PaintTick(object sender, EventArgs e)
    {
        paintTicks++;
        if (paintStage == 0)
        {
            // Wait for Paint's window to appear.
            IntPtr h = IntPtr.Zero;
            foreach (Process p in Process.GetProcessesByName("mspaint"))
            {
                try
                {
                    p.Refresh();
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    bool isNew = !paintBefore.Contains(p.Id);
                    if (isNew || paintTicks > 16) { h = p.MainWindowHandle; break; }
                }
                catch (Exception) { }
            }
            if (h != IntPtr.Zero) { paintHwnd = h; paintStage = 1; paintMark = paintTicks; }
            else if (paintTicks > 60)
            {
                paintTimer.Stop();
                MessageBox.Show("Paint opened, but it could not be detected automatically.\nThe image is in memory: click in Paint and press Ctrl+V.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        else if (paintStage == 1)
        {
            // Give Paint about a second to finish loading, then bring it to the front.
            if (paintTicks - paintMark >= 4)
            {
                ShowWindow(paintHwnd, 9); // SW_RESTORE
                SetForegroundWindow(paintHwnd);
                paintStage = 2; paintMark = paintTicks;
            }
        }
        else
        {
            if (paintTicks - paintMark >= 2)
            {
                paintTimer.Stop();
                if (GetClipboardSequenceNumber() != paintClipboardSequence)
                {
                    MessageBox.Show("The clipboard changed before Paint was ready. Choose Send to Paint again.",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    if (GetForegroundWindow() == paintHwnd)
                    {
                        SendKeys.SendWait("^v");
                        return;
                    }
                }
                catch (Exception) { }
                MessageBox.Show("Switch to Paint and press Ctrl+V to paste the image.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    // ---------------------------------------------------------------
    // Help
    // ---------------------------------------------------------------
    static void HelpHeading(RichTextBox r, string text)
    {
        r.SelectionStart = r.TextLength; r.SelectionLength = 0;
        r.SelectionFont = new Font("Segoe UI", 12f, FontStyle.Bold);
        r.SelectionColor = Color.FromArgb(0, 90, 158);
        r.SelectedText = text + "\n";
    }

    static void HelpText(RichTextBox r, string text)
    {
        r.SelectionStart = r.TextLength; r.SelectionLength = 0;
        r.SelectionFont = new Font("Segoe UI", 10f);
        r.SelectionColor = Color.FromArgb(30, 30, 30);
        r.SelectedText = text + "\n";
    }

    static void ShowHelp(IWin32Window owner)
    {
        Form f = new Form();
        f.Text = "How to Use Image Notepad";
        f.Size = new Size(700, 640);
        f.StartPosition = FormStartPosition.CenterParent;
        f.ShowInTaskbar = false;
        f.MinimizeBox = false;

        Panel pad = new Panel();
        pad.Dock = DockStyle.Fill;
        pad.Padding = new Padding(16);
        pad.BackColor = Color.White;

        RichTextBox r = new RichTextBox();
        r.Dock = DockStyle.Fill;
        r.ReadOnly = true;
        r.BorderStyle = BorderStyle.None;
        r.BackColor = Color.White;
        r.TabStop = false;

        HelpHeading(r, "Image Notepad - Quick Guide");
        HelpText(r, "Write notes in tabs and drop pictures right into the text. Everything in a tab, text and pictures, is kept together when you save it.\n");

        HelpHeading(r, "Working with tabs");
        HelpText(r,
            "\u2022  New tab: click the + button, press Ctrl+N, or double-click an empty spot on the tab bar.\n" +
            "\u2022  Switch tabs: click a tab, or press Ctrl+Tab (next) and Ctrl+Shift+Tab (previous).\n" +
            "\u2022  Rename a tab: double-click it, right-click > Rename Tab, or press F2. A tab shows its own name, not the full file name.\n" +
            "\u2022  Close a tab: click the x, middle-click the tab, or press Ctrl+W. A red dot means unsaved edits. A black dot means the current tab is not saved in the workbook.\n" +
            "\u2022  Opening files: File > Open (Ctrl+O), dragging a file onto the window, or double-clicking a file in Explorer. Each file opens in a new tab in this same window.\n");

        HelpHeading(r, "Saving");
        HelpText(r,
            "\u2022  File > Save (Ctrl+S) lets you save the current tab or all tabs in one .inote file.\n" +
            "\u2022  File > Save Individual Tab As (Ctrl+Shift+S) saves it under a new file name.\n" +
            "\u2022  File > Save All Tabs in Notebook (Ctrl+Alt+S) saves every tab under one notebook name.\n" +
            "\u2022  Save as .rtf to keep the pictures (these files also open in WordPad and Word). Saving as .txt keeps only the text.\n");

        HelpHeading(r, "Editor and output");
        HelpText(r, "Settings has margins, line numbers, autosave and this help. File > Restore Autosave opens local recovery copies. After a crash, the app offers to restore the latest snapshot. The status bar counts words in the active tab. Insert > Table adds a grid. Use Send to for Word, Excel or PowerPoint, or use the Print menu to print the active tab or all tabs.\n");

        HelpText(r, "Set the current tab language under Settings > Tab Language. Ctrl+Space opens code suggestions when enabled. Click the spelling message for corrections. Set the local recovery path under Settings > Autosave Folder.\n");

        HelpHeading(r, "Pictures");
        HelpText(r,
            "\u2022  Add a picture: Insert > Image from file (Ctrl+Shift+I), paste with Ctrl+V (for example a screenshot taken with Win+Shift+S), or drag an image file into the note.\n" +
            "\u2022  The maximum is 600 × 600 pixels for inserted image files and pasted bitmap images. You can resize an oversized image or cancel.\n" +
            "\u2022  Send to Paint: click a picture so it is highlighted, then choose Insert > Send to Paint (Ctrl+Shift+P) or right-click > Send to Paint. The picture is copied to memory, Paint opens, and the picture is pasted in. If it does not paste by itself, click in Paint and press Ctrl+V.\n");

        HelpHeading(r, "Formatting");
        HelpText(r,
            "\u2022  Bold, italic, underline: Ctrl+B, Ctrl+I, Ctrl+U.\n" +
            "\u2022  Format menu: font, text color, bullet list, and word wrap.\n");

        HelpHeading(r, "Right-click menus");
        HelpText(r,
            "\u2022  In the note: Undo, Redo, Cut, Copy, Paste, Delete, Select All, Insert Image and Send to Paint.\n" +
            "\u2022  On a tab: Rename, Save Individual Tab, New Tab, Close Tab and Close Other Tabs.\n");

        HelpHeading(r, "Keyboard shortcuts");
        HelpText(r,
            "Ctrl+N  New tab                    Ctrl+O  Open\n" +
            "Ctrl+S  Choose what to save         Ctrl+Shift+S  Save as\n" +
            "Ctrl+Alt+S  Save all tabs           Ctrl+W  Close tab\n" +
            "F2  Rename tab                      Ctrl+Tab  Next tab\n" +
            "Ctrl+Shift+I  Insert image          Ctrl+Shift+P  Send to Paint\n" +
            "Ctrl+Z / Ctrl+Y  Undo / Redo        F1  This help\n");

        r.SelectionStart = 0;
        r.SelectionLength = 0;
        r.ScrollToCaret();

        Button close = new Button();
        close.Text = "Close";
        close.Dock = DockStyle.Bottom;
        close.Height = 34;
        close.DialogResult = DialogResult.OK;
        f.AcceptButton = close;
        f.CancelButton = close;

        pad.Controls.Add(r);
        f.Controls.Add(pad);
        f.Controls.Add(close);
        f.ShowDialog(owner);
        f.Dispose();
    }
}
