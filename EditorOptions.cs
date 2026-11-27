using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

partial class ImageNotepad
{
    Panel lineGutter, rightGutter;
    ToolStripMenuItem lineNumbersItem, editorLinesItem;
    Color lineColor = Color.SteelBlue;
    int lineOpacity = 25;

    Color EditorLineColor
    {
        get { return Color.FromArgb(editorLinesItem.Checked ? (lineOpacity * 255 + 50) / 100 : 0, lineColor); }
    }
    Padding editorMargins;
    int autosaveMinutes;
    System.Windows.Forms.Timer autosaveTimer;
    string notebookPath, savedNotebook;
    string recoveryId = Guid.NewGuid().ToString("N");
    string lastRecovery;
    readonly PageSettings printSettings = new PageSettings();

    static string SettingsFolder
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageNotepad"); }
    }

    Control CreateEditorArea()
    {
        Panel area = new EditorFrame { Dock = DockStyle.Fill };
        lineGutter = new Panel { Dock = DockStyle.Left, Width = 52, BackColor = SystemColors.Control, Visible = false };
        Font numberFont = new Font("Arial", 12f);
        lineGutter.Font = numberFont;
        lineGutter.Disposed += delegate { numberFont.Dispose(); };
        lineGutter.Paint += DrawLineNumbers;
        rightGutter = new Panel
        {
            Dock = DockStyle.Right, Width = lineGutter.Width, BackColor = lineGutter.BackColor, Visible = false
        };
        area.Controls.Add(content);
        area.Controls.Add(lineGutter);
        area.Controls.Add(rightGutter);
        return area;
    }

    sealed class EditorFrame : Panel
    {
        public EditorFrame()
        {
            Padding = new Padding(3);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 3 || ClientSize.Height < 3) return;

            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            int radius = Math.Min(5, Math.Min(bounds.Width, bounds.Height) / 2);
            Color edge = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(112, 112, 112);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(edge))
            using (GraphicsPath outline = TabStrip.RoundRect(bounds, radius))
                e.Graphics.DrawPath(pen, outline);
        }
    }

    void SetLineGuttersVisible(bool visible)
    {
        lineGutter.Visible = visible;
        rightGutter.Visible = visible;
    }

    void DrawLineNumbers(object sender, PaintEventArgs e)
    {
        if (Cur == null) return;
        RichTextBox box = Cur.Rtb;
        string note = box.Text;
        int numberHeight = lineGutter.Font.Height;
        int top = Math.Min(note.Length, Math.Max(0, box.GetCharIndexFromPosition(Point.Empty)));
        int start = top == 0 ? 0 : note.LastIndexOf('\n', top - 1) + 1;
        int number = 1;
        for (int i = 0; i < start; i++)
            if (note[i] == '\n') number++;

        for (;;)
        {
            Point pos = box.GetPositionFromCharIndex(start);
            int y = pos.Y + content.Padding.Top;
            if (y >= lineGutter.Height) break;
            if (y + numberHeight > 0)
                TextRenderer.DrawText(e.Graphics, number.ToString(), lineGutter.Font,
                    new Rectangle(0, y, lineGutter.Width - 6, numberHeight),
                    SystemColors.GrayText, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            int next = note.IndexOf('\n', start);
            if (next < 0) break;
            start = next + 1;
            number++;
        }
    }

    sealed class RuledEditor : RichTextBox
    {
        public Color LineColor { get; set; }

        [DllImport("user32.dll")]
        static extern bool GetUpdateRect(IntPtr window, out PrintRect rectangle, bool erase);

        public RuledEditor()
        {
            VScroll += delegate { if (LineColor.A != 0) Invalidate(); };
            TextChanged += delegate { if (LineColor.A != 0) Invalidate(); };
        }

        protected override void WndProc(ref Message message)
        {
            const int Paint = 0x000F;
            PrintRect update = new PrintRect();
            bool draw = message.Msg == Paint && LineColor.A != 0 &&
                Environment.OSVersion.Platform == PlatformID.Win32NT &&
                GetUpdateRect(Handle, out update, false);
            base.WndProc(ref message);
            if (!draw || !IsHandleCreated) return;
            using (Graphics graphics = Graphics.FromHwnd(Handle))
            {
                graphics.SetClip(Rectangle.FromLTRB(update.Left, update.Top, update.Right, update.Bottom));
                int firstChar = GetCharIndexFromPosition(Point.Empty);
                int lineStart = GetFirstCharIndexFromLine(GetLineFromCharIndex(firstChar));
                int top = GetPositionFromCharIndex(Math.Max(0, lineStart)).Y;
                int spacing = Math.Max(1, Font.Height);
                int firstRule = ((top - 1) % spacing + spacing) % spacing;
                using (Pen pen = new Pen(LineColor))
                    for (int y = firstRule; y < ClientSize.Height; y += spacing)
                        graphics.DrawLine(pen, 0, y, ClientSize.Width - 1, y);
            }
        }
    }

    void RefreshEditorLines()
    {
        foreach (Doc d in strip.Docs)
        {
            ((RuledEditor)d.Rtb).LineColor = EditorLineColor;
            d.Rtb.Invalidate();
        }
        SaveEditorSettings();
    }

    void AddEditorSettings(ToolStripMenuItem settings)
    {
        AddItem(settings, "Editor &Margins...", Keys.None, delegate
        {
            int[] values = ReadNumbers("Editor margins (pixels)",
                new[] { "Left", "Top", "Right", "Bottom" },
                new[] { editorMargins.Left, editorMargins.Top, editorMargins.Right, editorMargins.Bottom }, 0, 120);
            if (values == null) return;
            editorMargins = new Padding(values[0], values[1], values[2], values[3]);
            content.Padding = editorMargins;
            lineGutter.Invalidate();
            SaveEditorSettings();
        });
        lineNumbersItem = AddItem(settings, "Show Line &Numbers", Keys.None, null);
        lineNumbersItem.CheckOnClick = true;
        lineNumbersItem.Click += delegate
        {
            SetLineGuttersVisible(lineNumbersItem.Checked);
            SaveEditorSettings();
        };
        ToolStripMenuItem lines = AddItem(settings, "Editor &Lines", Keys.None, null);
        editorLinesItem = AddItem(lines, "&Show Lines", Keys.None, delegate { RefreshEditorLines(); });
        editorLinesItem.CheckOnClick = true;
        AddItem(lines, "Line &Color...", Keys.None, delegate
        {
            using (ColorDialog dialog = new ColorDialog { Color = lineColor, FullOpen = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                lineColor = dialog.Color;
                RefreshEditorLines();
            }
        });
        AddItem(lines, "Line &Opacity...", Keys.None, delegate
        {
            int[] values = ReadNumbers("Editor line opacity", new[] { "Opacity (%)" }, new[] { lineOpacity }, 0, 100);
            if (values == null) return;
            lineOpacity = values[0];
            RefreshEditorLines();
        });
        AddItem(settings, "&Autosave...", Keys.None, delegate
        {
            int[] values = ReadNumbers("Local recovery autosave", new[] { "Minutes (0 = off)" },
                new[] { autosaveMinutes }, 0, 60);
            if (values == null) return;
            autosaveMinutes = values[0];
            ConfigureAutosave();
            SaveEditorSettings();
        });
    }

    int[] ReadNumbers(string title, string[] labels, int[] values, int min, int max)
    {
        using (Form form = new Form())
        {
            form.Text = title;
            form.ClientSize = new Size(310, 48 + 33 * labels.Length);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterParent;
            form.MaximizeBox = form.MinimizeBox = form.ShowInTaskbar = false;
            NumericUpDown[] fields = new NumericUpDown[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                form.Controls.Add(new Label { Text = labels[i], Left = 12, Top = 15 + i * 33, AutoSize = true });
                fields[i] = new NumericUpDown { Left = 190, Top = 11 + i * 33, Minimum = min, Maximum = max, Value = values[i] };
                form.Controls.Add(fields[i]);
            }
            int bottom = 10 + labels.Length * 33;
            form.AcceptButton = new Button { Text = "OK", Left = 148, Top = bottom, DialogResult = DialogResult.OK };
            form.CancelButton = new Button { Text = "Cancel", Left = 228, Top = bottom, DialogResult = DialogResult.Cancel };
            form.Controls.Add((Button)form.AcceptButton);
            form.Controls.Add((Button)form.CancelButton);
            if (form.ShowDialog(this) != DialogResult.OK) return null;
            for (int i = 0; i < fields.Length; i++) values[i] = (int)fields[i].Value;
            return values;
        }
    }

    void LoadEditorSettings()
    {
        try
        {
            string[] settings = File.ReadAllLines(Path.Combine(SettingsFolder, "settings.txt"));
            if (settings.Length != 6 && settings.Length != 9) return;
            int[] values = Array.ConvertAll(settings, x => int.Parse(x, CultureInfo.InvariantCulture));
            for (int i = 0; i < 4; i++) values[i] = Math.Max(0, Math.Min(120, values[i]));
            editorMargins = new Padding(values[0], values[1], values[2], values[3]);
            content.Padding = editorMargins;
            lineNumbersItem.Checked = values[4] != 0;
            SetLineGuttersVisible(lineNumbersItem.Checked);
            autosaveMinutes = Math.Max(0, Math.Min(60, values[5]));
            if (values.Length == 9)
            {
                editorLinesItem.Checked = values[6] != 0;
                lineColor = Color.FromArgb(values[7]);
                lineOpacity = Math.Max(0, Math.Min(100, values[8]));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (FormatException) { }
        catch (OverflowException) { }
    }

    void SaveEditorSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            int[] values = { editorMargins.Left, editorMargins.Top, editorMargins.Right, editorMargins.Bottom,
                lineNumbersItem.Checked ? 1 : 0, autosaveMinutes, editorLinesItem.Checked ? 1 : 0,
                lineColor.ToArgb(), lineOpacity };
            File.WriteAllLines(Path.Combine(SettingsFolder, "settings.txt"),
                Array.ConvertAll(values, value => value.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) { ShowEditorError("Could not save settings", ex); }
    }

    void ShowEditorError(string action, Exception ex)
    {
        MessageBox.Show(this, action + ".\n\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void ChooseSave()
    {
        DialogResult choice = MessageBox.Show(this,
            "Save the entire notebook as one .inote file?\n\nYes: Entire notebook\nNo: Current tab only",
            "Save", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Yes) SaveNotebook();
        if (choice == DialogResult.No) Save(Cur);
    }

    string NotebookData()
    {
        StringBuilder xml = new StringBuilder();
        using (XmlWriter writer = XmlWriter.Create(xml, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("notebook");
            writer.WriteAttributeString("version", "1");
            writer.WriteAttributeString("active", strip.Docs.IndexOf(Cur).ToString(CultureInfo.InvariantCulture));
            foreach (Doc d in strip.Docs)
            {
                writer.WriteStartElement("tab");
                writer.WriteAttributeString("name", d.Name);
                if (d.Language.Length > 0) writer.WriteAttributeString("language", d.Language);
                writer.WriteString(d.Rtb.Rtf);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        return xml.ToString();
    }

    static void WriteNotebook(string path, string data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(data);
            writer.Flush();
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    bool SaveNotebook()
    {
        return notebookPath == null ? SaveNotebookAs() : SaveNotebookTo(notebookPath);
    }

    bool SaveNotebookAs()
    {
        using (SaveFileDialog dialog = new SaveFileDialog
        {
            Filter = "Image Notepad notebook (*.inote)|*.inote",
            DefaultExt = "inote",
            FileName = notebookPath == null ? "Notebook.inote" : Path.GetFileName(notebookPath)
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            string path = dialog.FileName;
            if (!path.EndsWith(".inote", StringComparison.OrdinalIgnoreCase)) path += ".inote";
            if (!string.Equals(path, dialog.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(path) &&
                MessageBox.Show(this, "Replace the existing notebook?", "Save Notebook",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            return SaveNotebookTo(path);
        }
    }

    bool SaveNotebookTo(string path)
    {
        try
        {
            string data = NotebookData();
            WriteNotebook(path, data);
            notebookPath = Path.GetFullPath(path);
            RefreshWorkbookPath();
            savedNotebook = data;
            foreach (Doc d in strip.Docs)
            {
                d.Dirty = false;
                d.WorkbookDirty = false;
            }
            Touch();
            return true;
        }
        catch (Exception ex)
        {
            ShowEditorError("Could not save notebook", ex);
            return false;
        }
    }

    bool NotebookChanged()
    {
        if (savedNotebook == null) return false;
        string data = NotebookData();
        // Switching the active tab does not change notebook content.
        return data.Substring(data.IndexOf('>')) != savedNotebook.Substring(savedNotebook.IndexOf('>'));
    }

    bool ConfirmCloseNotebook()
    {
        if (notebookPath != null)
        {
            if (!NotebookChanged()) return true;
            DialogResult result = MessageBox.Show(this, "Save changes to the notebook?", AppName,
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            return result == DialogResult.No || (result == DialogResult.Yes && SaveNotebook());
        }
        foreach (Doc d in new List<Doc>(strip.Docs))
        {
            if (!d.Dirty) continue;
            SelectDoc(d);
            if (!ConfirmDiscard(d)) return false;
        }
        return true;
    }

    bool OpenNotebook(string path, bool recovery)
    {
        if (!recovery && string.Equals(notebookPath, path, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            XmlDocument xml = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                xml.Load(reader);

            XmlElement root = xml.DocumentElement;
            if (root == null || root.Name != "notebook" || root.GetAttribute("version") != "1")
                throw new InvalidDataException("This notebook format is unsupported.");

            var tabs = new List<Tuple<string, string, string>>();
            using (RichTextBox probe = new RichTextBox())
            {
                foreach (XmlElement tab in root.SelectNodes("tab"))
                {
                    probe.Rtf = tab.InnerText;
                    tabs.Add(Tuple.Create(tab.GetAttribute("name"), probe.Rtf, tab.GetAttribute("language")));
                }
            }
            int active;
            if (!int.TryParse(root.GetAttribute("active"), out active)) active = 0;
            if (!ConfirmCloseNotebook()) return false;
            foreach (Doc d in strip.Docs) d.Rtb.Dispose();
            strip.Docs.Clear();
            strip.Selected = null;
            foreach (var tab in tabs)
            {
                Doc d = NewTab();
                d.Name = tab.Item1;
                d.Language = CodeLanguages.ContainsKey(tab.Item3) ? tab.Item3 : "";
                d.NameCustom = true;
                d.Loading = true;
                d.Rtb.Rtf = tab.Item2;
                d.Loading = false;
                d.Dirty = recovery;
                d.WorkbookDirty = recovery;
            }
            if (strip.Docs.Count == 0) NewTab();
            SelectDoc(strip.Docs[Math.Max(0, Math.Min(active, strip.Docs.Count - 1))]);
            notebookPath = recovery ? null : Path.GetFullPath(path);
            RefreshWorkbookPath();
            savedNotebook = recovery ? null : NotebookData();
            lastRecovery = null;
            recoveryId = Guid.NewGuid().ToString("N");
            ScheduleCrashSnapshot();
            return true;
        }
        catch (Exception ex) { ShowEditorError("Could not open notebook", ex); return false; }
    }

    void InitializeAutosave()
    {
        autosaveTimer = new System.Windows.Forms.Timer();
        autosaveTimer.Tick += delegate { WriteAutosave(); };
        FormClosed += delegate { autosaveTimer.Dispose(); };
        ConfigureAutosave();
    }

    void ConfigureAutosave()
    {
        autosaveTimer.Stop();
        if (autosaveMinutes == 0) return;
        autosaveTimer.Interval = autosaveMinutes * 60000;
        autosaveTimer.Start();
    }

    void WriteAutosave()
    {
        try
        {
            if (!NotebookChanged() && !strip.Docs.Exists(d => d.Dirty)) return;
            string data = NotebookData();
            if (data == lastRecovery) return;
            string folder = RecoveryFolder;
            Directory.CreateDirectory(folder);
            WriteNotebook(Path.Combine(folder, recoveryId + ".inote"), data);
            lastRecovery = data;
        }
        catch (Exception ex)
        {
            autosaveTimer.Stop();
            ShowEditorError("Autosave paused because a recovery copy could not be written. Re-enable it in Settings", ex);
        }
    }

    void RestoreAutosave()
    {
        using (OpenFileDialog dialog = new OpenFileDialog
        {
            Filter = "Recovery notebooks (*.inote)|*.inote",
            InitialDirectory = RecoveryFolder
        })
        {
            if (dialog.ShowDialog(this) == DialogResult.OK) OpenNotebook(dialog.FileName, true);
        }
    }

    sealed class NoteLine
    {
        public int Start;
        public string Text;
        public override string ToString() { return Text; }
    }

    static List<NoteLine> ReadLines(string text)
    {
        List<NoteLine> lines = new List<NoteLine>();
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            int length = end - start;
            if (length > 0 && text[start + length - 1] == '\r') length--;
            lines.Add(new NoteLine { Start = start, Text = text.Substring(start, length) });
            start = end + 1;
        }
        return lines;
    }

    void SortLines(bool descending)
    {
        try { SortNoteLines(Cur, descending); }
        catch (Exception ex) { ShowEditorError("Could not sort lines", ex); }
    }

    void SortNoteLines(Doc doc, bool descending)
    {
        RichTextBox box = doc.Rtb;
        int cursor = box.SelectionStart;
        int selectionLength = box.SelectionLength;
        string text = box.Text;
        List<NoteLine> allLines = ReadLines(text);
        var lines = allLines.Where((line, index) => selectionLength == 0 ||
            (line.Start < cursor + selectionLength &&
             (index + 1 < allLines.Count ? allLines[index + 1].Start : text.Length) > cursor)).ToList();
        if (lines.Count < 2) return;

        NoteLine[] sorted = (descending
            ? lines.OrderByDescending(line => line.Text, StringComparer.CurrentCultureIgnoreCase)
            : lines.OrderBy(line => line.Text, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        if (lines.SequenceEqual(sorted)) return;

        int start = lines[0].Start;
        NoteLine last = lines[lines.Count - 1];
        int length = last.Start + last.Text.Length - start;
        try
        {
            box.Select(start, length);
            string original = box.SelectedRtf;
            if (ContainsRtfImage(original) || Regex.IsMatch(original ?? "", @"\\(?:trowd|intbl|object)\b"))
                throw new InvalidOperationException("Select text lines outside tables and embedded objects to sort.");

            using (RichTextBox scratch = new RichTextBox { Font = box.Font })
            {
                for (int i = 0; i < sorted.Length; i++)
                {
                    NoteLine line = sorted[i];
                    if (i > 0)
                    {
                        scratch.Select(scratch.TextLength, 0);
                        scratch.SelectedText = "\n";
                    }
                    if (line.Text.Length == 0) continue;
                    box.Select(line.Start, line.Text.Length);
                    scratch.Select(scratch.TextLength, 0);
                    scratch.SelectedRtf = box.SelectedRtf;
                }
                string expected = string.Join("\n", sorted.Select(line => line.Text));
                if (scratch.Text.Replace("\r\n", "\n") != expected)
                    throw new InvalidOperationException("The editor could not preserve the selected text while sorting.");
                box.Select(start, length);
                scratch.SelectAll();
                string replacement = scratch.SelectedRtf;
                string expectedNote = text.Substring(0, start) + expected + text.Substring(start + length);
                string originalNote = box.Rtf;
                bool wasDirty = doc.Dirty, workbookWasDirty = doc.WorkbookDirty;
                ApplyFormatting(doc, editor =>
                {
                    editor.SelectedRtf = replacement;
                    // Some RichEdit implementations insert a synthetic final paragraph.
                    int boundary = start + expected.Length;
                    if (editor.Text == expectedNote.Insert(boundary, "\n"))
                    {
                        editor.Select(boundary, 1);
                        editor.SelectedText = "";
                    }
                    if (editor.Text.Replace("\r\n", "\n") != expectedNote.Replace("\r\n", "\n"))
                    {
                        editor.Rtf = originalNote;
                        doc.Dirty = wasDirty;
                        doc.WorkbookDirty = workbookWasDirty;
                        Touch();
                        throw new InvalidOperationException("The editor could not preserve the original line breaks.");
                    }
                });
            }
        }
        finally { box.Select(cursor, selectionLength); }
    }

    static bool MatchesFilter(string line, string query, StringComparison comparison)
    {
        return line.IndexOf(query, comparison) >= 0;
    }

    void FilterLines()
    {
        RichTextBox box = Cur.Rtb;
        List<NoteLine> lines = ReadLines(box.Text);
        using (Form dialog = new Form
        {
            Text = "Filter Lines", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(580, 360), MinimumSize = new Size(420, 250), ShowInTaskbar = false
        })
        {
            var query = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Text to find" };
            var matchCase = new CheckBox { Text = "Match case", AutoSize = true };
            var clear = new Button { Text = "Clear", AutoSize = true };
            var results = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, AccessibleName = "Matching lines" };
            var summary = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
            var jump = new Button { Text = "Go to line", AutoSize = true };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.Controls.Add(new Label { Text = "Contains", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            top.Controls.Add(query, 1, 0);
            top.Controls.Add(matchCase, 2, 0);
            top.Controls.Add(clear, 3, 0);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            bottom.Controls.Add(jump);
            bottom.Controls.Add(close);
            bottom.Controls.Add(summary);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(results, 0, 1);
            layout.Controls.Add(bottom, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = jump;
            dialog.CancelButton = close;
            Action refresh = delegate
            {
                var comparison = matchCase.Checked ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
                results.BeginUpdate();
                try
                {
                    results.Items.Clear();
                    results.Items.AddRange(lines.Where(line => MatchesFilter(line.Text, query.Text, comparison)).ToArray());
                }
                finally { results.EndUpdate(); }
                if (results.Items.Count > 0) results.SelectedIndex = 0;
                jump.Enabled = results.SelectedItem != null;
                summary.Text = results.Items.Count + " of " + lines.Count + " lines";
            };
            query.TextChanged += delegate { refresh(); };
            matchCase.CheckedChanged += delegate { refresh(); };
            clear.Click += delegate { query.Clear(); query.Focus(); };
            jump.Click += delegate { if (results.SelectedItem != null) dialog.DialogResult = DialogResult.OK; };
            results.DoubleClick += delegate { jump.PerformClick(); };
            refresh();
            if (dialog.ShowDialog(this) != DialogResult.OK || box.IsDisposed) return;
            var selected = (NoteLine)results.SelectedItem;
            box.Focus();
            box.Select(selected.Start, selected.Text.Length);
            box.ScrollToCaret();
        }
    }

    void InsertColumns()
    {
        int[] size = ReadNumbers("Insert columns", new[] { "Columns" }, new[] { 2 }, 2, 20);
        if (size != null) InsertGrid(1, size[0], "columns");
    }

    void InsertTable()
    {
        int[] size = ReadNumbers("Insert table", new[] { "Rows", "Columns" }, new[] { 3, 3 }, 1, 20);
        if (size != null) InsertGrid(size[0], size[1], "table");
    }

    void InsertGrid(int rows, int columns, string label)
    {
        try { ApplyFormatting(Cur, box => box.SelectedRtf = TableRtf(rows, columns)); }
        catch (Exception ex) { ShowEditorError("Could not insert " + label, ex); }
    }

    static string TableRtf(int rows, int columns)
    {
        StringBuilder table = new StringBuilder(@"{\rtf1\ansi\pard\par");
        for (int row = 0; row < rows; row++)
        {
            table.AppendLine(@"\trowd\trgaph90");
            for (int col = 1; col <= columns; col++)
                table.Append(@"\clbrdrt\brdrs\clbrdrl\brdrs\clbrdrb\brdrs\clbrdrr\brdrs\cellx")
                    .Append(8640 * col / columns);
            table.AppendLine();
            for (int col = 0; col < columns; col++) table.Append(@"\pard\intbl\cell ");
            table.AppendLine(@"\row");
        }
        return table.Append(@"\pard\par}").ToString();
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct PrintRect
    {
        public int Left, Top, Right, Bottom;

        public PrintRect(Rectangle bounds)
        {
            Left = bounds.Left * 1440 / 100;
            Top = bounds.Top * 1440 / 100;
            Right = bounds.Right * 1440 / 100;
            Bottom = bounds.Bottom * 1440 / 100;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FormatRange
    {
        public IntPtr RenderDc, TargetDc;
        public PrintRect Area, Page;
        public int Start, End;
    }

    void PrintPageSetup()
    {
        try
        {
            using (PageSetupDialog dialog = new PageSetupDialog { PageSettings = printSettings })
                dialog.ShowDialog(this);
        }
        catch (Exception ex) { ShowEditorError("Could not open print page setup", ex); }
    }

    void PrintTab()
    {
        PrintTabs(new[] { Cur }, Cur.Name);
    }

    void PrintTabs(IList<Doc> tabs, string jobName)
    {
        try
        {
            using (PrintDocument document = new PrintDocument())
            using (PrintDialog dialog = new PrintDialog { Document = document, UseEXDialog = true })
            {
                document.DefaultPageSettings = (PageSettings)printSettings.Clone();
                document.DocumentName = jobName;
                int tabIndex = 0;
                int next = 0;
                document.PrintPage += delegate (object sender, PrintPageEventArgs e)
                {
                    RichTextBox box = tabs[tabIndex].Rtb;
                    int end = RenderPrintPage(box, e, next);
                    if (end <= next && next < box.TextLength)
                        throw new InvalidOperationException("The selected margins leave too little room to print.");
                    next = end;
                    if (next >= box.TextLength)
                    {
                        tabIndex++;
                        next = 0;
                    }
                    e.HasMorePages = tabIndex < tabs.Count;
                };
                if (dialog.ShowDialog(this) == DialogResult.OK) document.Print();
            }
        }
        catch (Exception ex) { ShowEditorError("Could not print", ex); }
    }

    static int RenderPrintPage(RichTextBox box, PrintPageEventArgs page, int start)
    {
        const int FormatMessage = 0x0439;
        Rectangle bounds = page.MarginBounds;
        bounds.Offset(-(int)page.PageSettings.HardMarginX, -(int)page.PageSettings.HardMarginY);
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FormatRange)));
        try
        {
            IntPtr dc = page.Graphics.GetHdc();
            try
            {
                FormatRange range = new FormatRange { RenderDc = dc, TargetDc = dc, Area = new PrintRect(bounds),
                    Page = new PrintRect(page.PageBounds), Start = start, End = box.TextLength };
                Marshal.StructureToPtr(range, memory, false);
                return SendMessage(box.Handle, FormatMessage, new IntPtr(1), memory).ToInt32();
            }
            finally { page.Graphics.ReleaseHdc(dc); }
        }
        finally
        {
            SendMessage(box.Handle, FormatMessage, IntPtr.Zero, IntPtr.Zero);
            Marshal.FreeHGlobal(memory);
        }
    }

    void AddOfficeMenu(MenuStrip menu)
    {
        ToolStripMenuItem send = AddTop(menu, "Send &to");
        foreach (string name in new[] { "Word", "Excel", "PowerPoint" })
        {
            var app = new ToolStripMenuItem(name);
            send.DropDownItems.Add(app);
            AddItem(app, "Text only", Keys.None, delegate { SendToOffice(name, true, false); });
            AddItem(app, "Images only", Keys.None, delegate { SendToOffice(name, false, true); });
            AddItem(app, "Text and images", Keys.None, delegate { SendToOffice(name, true, true); });
        }
    }

    static List<string> ExportPictures(string rtf, string folder)
    {
        var paths = new List<string>();
        // RichEdit emits picture data as hex, sometimes after nested picture properties.
        const string picture = @"\{\\pict\b(?<body>(?>\\[{}]|\{(?<depth>)|\}(?<-depth>)|[^{}])+)(?(depth)(?!))\}";
        foreach (Match match in Regex.Matches(rtf, picture, RegexOptions.IgnoreCase))
        {
            string body = match.Groups["body"].Value;
            Match payload = Regex.Match(body, @"(?<=\s)(?<hex>(?:[0-9a-f]{2}[ \r\n\t]*){16,})$", RegexOptions.IgnoreCase);
            if (!payload.Success) continue;
            string hex = Regex.Replace(payload.Groups["hex"].Value, @"\s+", "");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string extension = Regex.IsMatch(body, @"\\pngblip\b") ? ".png" :
                Regex.IsMatch(body, @"\\jpegblip\b") ? ".jpg" :
                body.Contains(@"\emfblip") ? ".emf" :
                body.Contains(@"\wmetafile") ? ".wmf" : null;
            if (extension == null) continue;
            if (extension == ".wmf" && bytes[0] != 0xd7)
                bytes = PlaceableMetafile(bytes, body);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Image-" + paths.Count + extension);
            File.WriteAllBytes(path, bytes);
            paths.Add(path);
        }
        return paths;
    }

    static byte[] PlaceableMetafile(byte[] bytes, string rtf)
    {
        int width = MetafileDimension(rtf, "picwgoal", 6000);
        int height = MetafileDimension(rtf, "pichgoal", 4500);
        ushort[] header = { 0xcdd7, 0x9ac6, 0, 0, 0, (ushort)width, (ushort)height, 1440, 0, 0, 0 };
        for (int i = 0; i < 10; i++) header[10] ^= header[i];
        using (MemoryStream output = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(output))
        {
            foreach (ushort word in header) writer.Write(word);
            writer.Write(bytes);
            return output.ToArray();
        }
    }

    static int MetafileDimension(string rtf, string control, int fallback)
    {
        Match match = Regex.Match(rtf, @"\\" + control + @"(\d+)");
        int value;
        return match.Success && int.TryParse(match.Groups[1].Value, out value)
            ? Math.Max(1, Math.Min(short.MaxValue, value)) : fallback;
    }

    void SendToOffice(string name, bool text, bool images)
    {
        try
        {
            Type officeType = Type.GetTypeFromProgID(name + ".Application");
            if (officeType == null)
                throw new InvalidOperationException("Install the desktop edition of Microsoft " + name + " to use this option.");
            string folder = Path.Combine(Path.GetTempPath(), "ImageNotepad", Guid.NewGuid().ToString("N"));
            List<string> pictures = images && !(name == "Word" && text)
                ? ExportPictures(Cur.Rtb.Rtf, folder) : new List<string>();
            if (images && pictures.Count == 0 && !(name == "Word" && text))
            {
                string note = text ? "No supported embedded images were found. Send the text anyway?" :
                    "No supported embedded images were found in this tab.";
                if (MessageBox.Show(this, note, AppName, text ? MessageBoxButtons.YesNo : MessageBoxButtons.OK,
                    MessageBoxIcon.Information) != DialogResult.Yes) return;
            }
            dynamic app = Activator.CreateInstance(officeType);
            if (name == "Word")
            {
                app.Visible = true;
                dynamic doc = app.Documents.Add();
                if (text && images)
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "Note.rtf");
                    Cur.Rtb.SaveFile(path, RichTextBoxStreamType.RichText);
                    doc.Range().InsertFile(path);
                }
                else if (text) doc.Content.Text = Cur.Rtb.Text.Replace("\n", "\r");
                else
                    foreach (string picture in pictures)
                        doc.InlineShapes.AddPicture(picture, false, true, doc.Range(doc.Content.End - 1, doc.Content.End - 1));
            }
            else if (name == "Excel")
            {
                app.Visible = true;
                dynamic book = app.Workbooks.Add();
                dynamic sheet = book.Worksheets[1];
                int nextRow = 1;
                if (text)
                {
                    string[] lines = Cur.Rtb.Lines;
                    if (lines.Length > 1048576) throw new InvalidOperationException("The note exceeds Excel's row limit.");
                    if (lines.Length > 0)
                    {
                        object[,] values = new object[lines.Length, 1];
                        for (int i = 0; i < lines.Length; i++) values[i, 0] = lines[i];
                        dynamic cells = sheet.Range["A1", "A" + lines.Length];
                        cells.NumberFormat = "@";
                        cells.Value2 = values;
                    }
                    nextRow += lines.Length;
                }
                float top = (float)sheet.Rows[Math.Min(nextRow, 1048576)].Top;
                foreach (string picture in pictures)
                {
                    dynamic shape = sheet.Shapes.AddPicture(picture, 0, -1, 4f, top, -1f, -1f);
                    shape.LockAspectRatio = -1;
                    if ((float)shape.Width > 600f) shape.Width = 600f;
                    top += (float)shape.Height + 12f;
                }
            }
            else
            {
                app.Visible = -1;
                dynamic deck = app.Presentations.Add(-1);
                float width = (float)deck.PageSetup.SlideWidth;
                float height = (float)deck.PageSetup.SlideHeight;
                if (text)
                {
                    string note = Cur.Rtb.Text;
                    // Bound the amount of text per slide so large notes remain readable.
                    for (int start = 0; start < note.Length; )
                    {
                        int end = Math.Min(start + 1800, note.Length);
                        if (end < note.Length)
                        {
                            int newline = note.LastIndexOf('\n', end - 1, end - start);
                            if (newline >= start) end = newline + 1;
                            else if (char.IsHighSurrogate(note[end - 1])) end--;
                        }
                        dynamic slide = deck.Slides.Add(deck.Slides.Count + 1, 12);
                        dynamic shape = slide.Shapes.AddTextbox(1, 32f, 32f, width - 64f, height - 64f);
                        shape.TextFrame.TextRange.Text = note.Substring(start, end - start);
                        shape.TextFrame.TextRange.Font.Size = 20;
                        shape.TextFrame2.AutoSize = 2;
                        start = end;
                    }
                }
                foreach (string picture in pictures)
                {
                    dynamic slide = deck.Slides.Add(deck.Slides.Count + 1, 12);
                    dynamic shape = slide.Shapes.AddPicture(picture, 0, -1, 0f, 0f, -1f, -1f);
                    shape.LockAspectRatio = -1;
                    float scale = Math.Min(1f, Math.Min((width - 64f) / (float)shape.Width, (height - 64f) / (float)shape.Height));
                    shape.Width = (float)shape.Width * scale;
                    shape.Left = (width - (float)shape.Width) / 2;
                    shape.Top = (height - (float)shape.Height) / 2;
                }
            }
        }
        catch (Exception ex) { ShowEditorError("Could not send this tab to " + name, ex); }
    }
}
