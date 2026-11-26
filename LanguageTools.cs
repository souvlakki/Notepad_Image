using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

partial class ImageNotepad
{
    // Extensions, reserved words, and common standard-library names.
    static readonly Dictionary<string, string[]> CodeLanguages = new Dictionary<string, string[]>
    {
        { "Python", new[] { ".py .pyw", "False None True and as assert async await break case class continue def del elif else except finally for from global if import in is lambda match nonlocal not or pass raise return try while with yield",
            "abs all any bool dict enumerate filter float input int len list map max min open print range set sorted str sum super tuple type zip" } },
        { "JavaScript", new[] { ".js .jsx .mjs .cjs", "async await break case catch class const continue debugger default delete do else export extends false finally for function if import in instanceof let new null return static super switch this throw true try typeof var void while yield",
            "Array Boolean Date Error JSON Map Math Number Object Promise RegExp Set String Symbol console document fetch parseFloat parseInt setTimeout undefined window" } },
        { "TypeScript", new[] { ".ts .tsx .mts .cts", "abstract any as async await boolean break case catch class const constructor continue declare default else enum export extends false for function if implements import infer interface keyof let namespace never new null number private protected public readonly return satisfies static string super switch this throw true try type typeof unknown void while yield",
            "Array Date Error JSON Map Math Object Omit Partial Pick Promise Readonly Record Set console document fetch window" } },
        { "Java", new[] { ".java", "abstract assert boolean break byte case catch char class const continue default do double else enum extends false final finally float for if implements import instanceof int interface long module native new null package private protected public record return sealed short static super switch synchronized this throw throws transient true try var void volatile while yield",
            "ArrayList Arrays Boolean Collections Double Exception HashMap HashSet Integer List Map Math Object Optional Set String StringBuilder System println" } },
        { "C#", new[] { ".cs", "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit false finally fixed float for foreach if implicit in int interface internal is lock long namespace new null object operator out override params partial private protected public readonly ref return sealed short static string struct switch this throw true try typeof uint ulong unchecked unsafe using var virtual void while yield",
            "Array Console DateTime Dictionary Enumerable Exception Guid IEnumerable List Math Object String StringBuilder Task WriteLine" } }
    };

    static readonly Regex Identifier = new Regex(@"[\p{L}_][\p{L}\p{M}\d_]*");
    static readonly Regex ProseWord = new Regex(@"\p{L}[\p{L}\p{M}]*(?:['’][\p{L}\p{M}]+)*");
    readonly Dictionary<string, Color> languageColors = new Dictionary<string, Color>();
    bool completionEnabled, proseSpelling = true, codeSpelling = true;
    string autosaveFolder;
    ToolStripStatusLabel languageStatus;
    ToolStripDropDownButton spellingStatus;
    Timer spellingTimer;
    IWindowsSpellChecker spellChecker;
    bool checkedSpellSupport;
    ContextMenuStrip completionMenu;

    string RecoveryFolder { get { return autosaveFolder ?? Path.Combine(SettingsFolder, "Recovery"); } }

    static string DetectLanguage(string path)
    {
        string extension = Path.GetExtension(path);
        foreach (var language in CodeLanguages)
            if (language.Value[0].Split(' ').Contains(extension, StringComparer.OrdinalIgnoreCase)) return language.Key;
        return "";
    }

    Color GetLanguageColor(string language)
    {
        Color color;
        return languageColors.TryGetValue(language, out color) ? color : Color.Empty;
    }

    void AddLanguageSettings(ToolStripMenuItem settings)
    {
        ToolStripMenuItem mode = AddItem(settings, "Tab &Language", Keys.None, null);
        foreach (string language in new[] { "" }.Concat(CodeLanguages.Keys))
        {
            var option = AddItem(mode, language.Length == 0 ? "Plain text" : language, Keys.None,
                delegate
                {
                    if (Cur == null || Cur.Language == language) return;
                    Cur.Language = language;
                    Cur.Dirty = true;
                    Cur.WorkbookDirty = true;
                    RefreshLanguageStatus();
                    Touch();
                });
            mode.DropDownOpening += delegate { option.Checked = Cur != null && Cur.Language == language; };
        }

        ToolStripMenuItem colors = AddItem(settings, "Language Tab Colors", Keys.None, null);
        foreach (string language in CodeLanguages.Keys)
        {
            ToolStripMenuItem colorMenu = AddItem(colors, language, Keys.None, null);
            AddItem(colorMenu, "Choose color...", Keys.None, delegate
            {
                Color current = GetLanguageColor(language);
                using (ColorDialog dialog = new ColorDialog { Color = current.IsEmpty ? Color.RoyalBlue : current, FullOpen = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        languageColors[language] = dialog.Color;
                        strip.Invalidate();
                        SaveLanguageSettings();
                    }
            });
            AddItem(colorMenu, "No color", Keys.None, delegate
            {
                languageColors.Remove(language);
                strip.Invalidate();
                SaveLanguageSettings();
            });
        }

        AddLanguageToggle(settings, "Code Completion (Ctrl+Space)", () => completionEnabled, value => completionEnabled = value);
        AddLanguageToggle(settings, "Check Spelling in Plain Text", () => proseSpelling, value => proseSpelling = value);
        AddLanguageToggle(settings, "Suggest Corrections for Code Keywords", () => codeSpelling, value => codeSpelling = value);
        AddItem(settings, "Autosave &Folder...", Keys.None, delegate
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog
            {
                Description = "Choose where Image Notepad writes automatic recovery copies.",
                SelectedPath = RecoveryFolder,
                ShowNewFolderButton = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                autosaveFolder = dialog.SelectedPath;
                lastRecovery = null;
                SaveLanguageSettings();
                ConfigureAutosave();
                if (autosaveMinutes > 0) WriteAutosave();
            }
        });
        AddItem(settings, "Use Default Autosave Folder", Keys.None, delegate
        {
            autosaveFolder = null;
            lastRecovery = null;
            SaveLanguageSettings();
            ConfigureAutosave();
            if (autosaveMinutes > 0) WriteAutosave();
        });
    }

    void AddLanguageToggle(ToolStripMenuItem parent, string label, Func<bool> current, Action<bool> update)
    {
        ToolStripMenuItem item = AddItem(parent, label, Keys.None, null);
        item.CheckOnClick = true;
        parent.DropDownOpening += delegate { item.Checked = current(); };
        item.Click += delegate
        {
            update(item.Checked);
            SaveLanguageSettings();
            ScheduleSpelling();
        };
    }

    void InitializeLanguageTools()
    {
        LoadLanguageSettings();
        StatusStrip bar = new StatusStrip { SizingGrip = false };
        languageStatus = new ToolStripStatusLabel("Plain text");
        spellingStatus = new ToolStripDropDownButton { Visible = false };
        bar.Items.Add(languageStatus);
        bar.Items.Add(spellingStatus);
        Controls.Add(bar);
        bar.BringToFront();
        spellingTimer = new Timer { Interval = 450 };
        spellingTimer.Tick += delegate { spellingTimer.Stop(); CheckSpelling(); };
        FormClosed += delegate
        {
            spellingTimer.Dispose();
            if (completionMenu != null) completionMenu.Dispose();
            if (spellChecker != null) Marshal.ReleaseComObject(spellChecker);
        };
    }

    void RefreshLanguageStatus()
    {
        if (languageStatus == null || Cur == null) return;
        languageStatus.Text = Cur.Language.Length == 0 ? "Plain text" : Cur.Language;
        ScheduleSpelling();
    }

    void ScheduleSpelling()
    {
        if (spellingTimer == null) return;
        spellingStatus.Visible = false;
        spellingTimer.Stop();
        spellingTimer.Start();
    }

    static Match WordAt(string text, int caret, Regex pattern, bool previous)
    {
        if (caret < 0 || caret > text.Length) return null;
        if (previous)
            while (caret > 0 && (text[caret - 1] == ' ' || text[caret - 1] == '\t')) caret--;
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || "_'’".IndexOf(text[start - 1]) >= 0 ||
            CharUnicodeInfo.GetUnicodeCategory(text[start - 1]) == UnicodeCategory.NonSpacingMark))
            start--;
        Match match = pattern.Match(text, start);
        return match.Success && match.Index <= caret && match.Index + match.Length >= caret ? match : null;
    }

    static string[] Complete(string language, string prefix, string text)
    {
        string[] data = CodeLanguages[language];
        var local = Identifier.Matches(text).Cast<Match>()
            .Select(match => match.Value).Where(value => value.Length > prefix.Length);
        return data[1].Split(' ').Concat(data[2].Split(' ')).Concat(local)
            .Where(value => value.StartsWith(prefix, StringComparison.Ordinal) && value != prefix)
            .Distinct(StringComparer.Ordinal).Take(16).ToArray();
    }

    void ShowCompletion()
    {
        RichTextBox box = Cur.Rtb;
        int caret = box.SelectionStart;
        Match word = WordAt(box.Text, caret, Identifier, false);
        int start = word == null ? caret : word.Index;
        int length = word == null ? box.SelectionLength : word.Length;
        string prefix = box.Text.Substring(start, caret - start);
        string[] items = Complete(Cur.Language, prefix, box.Text);
        if (completionMenu != null) completionMenu.Dispose();
        completionMenu = new ContextMenuStrip();
        if (items.Length == 0) completionMenu.Items.Add("No matching suggestions").Enabled = false;
        foreach (string item in items)
            completionMenu.Items.Add(item, null, delegate
            {
                box.Select(start, length);
                box.SelectedText = item;
                box.Focus();
            });
        Point position = box.GetPositionFromCharIndex(caret);
        completionMenu.Show(box, new Point(position.X, position.Y + box.Font.Height));
        if (items.Length > 0) completionMenu.Items[0].Select();
    }

    static bool OneEditApart(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1 || left == right) return false;
        if (left.Length > right.Length) return OneEditApart(right, left);
        int i = 0;
        while (i < left.Length && left[i] == right[i]) i++;
        if (left.Length < right.Length) return left.Substring(i) == right.Substring(i + 1);
        if (left.Substring(i + 1) == right.Substring(i + 1)) return true;
        return i + 1 < left.Length && left[i] == right[i + 1] && left[i + 1] == right[i] &&
            left.Substring(i + 2) == right.Substring(i + 2);
    }

    static string[] CorrectCode(string language, string word, string source)
    {
        string[] words = CodeLanguages[language][1].Split(' ');
        if (word.Length < 3 || words.Contains(word) || CodeLanguages[language][2].Split(' ').Contains(word))
            return new string[0];
        // Repeated identifiers are likely intentional project names.
        if (Regex.Matches(source, @"(?<![\w])" + Regex.Escape(word) + @"(?![\w])").Count > 1)
            return new string[0];
        return words.Where(keyword => OneEditApart(word, keyword)).ToArray();
    }

    void CheckSpelling()
    {
        if (Cur == null || Cur.Loading) return;
        RichTextBox box = Cur.Rtb;
        bool code = Cur.Language.Length > 0;
        if (code ? !codeSpelling : !proseSpelling) return;
        Match word = WordAt(box.Text, box.SelectionStart, code ? Identifier : ProseWord, true);
        if (word == null || word.Length > 70) return;
        string[] suggestions = code ? CorrectCode(Cur.Language, word.Value, box.Text) : SuggestSpelling(word.Value);
        if (suggestions.Length == 0) return;
        spellingStatus.DropDownItems.Clear();
        spellingStatus.Text = (code ? "Possible keyword typo: " : "Spelling: ") + word.Value;
        spellingStatus.Visible = true;
        foreach (string replacement in suggestions)
            spellingStatus.DropDownItems.Add(replacement, null, delegate
            {
                if (box.IsDisposed || Cur == null || box != Cur.Rtb || box.TextLength < word.Index + word.Length ||
                    box.Text.Substring(word.Index, word.Length) != word.Value) return;
                box.Select(word.Index, word.Length);
                box.SelectedText = replacement;
                box.Focus();
            });
    }

    void LoadLanguageSettings()
    {
        try
        {
            var settings = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(Path.Combine(SettingsFolder, "language-settings.xml"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                settings.Load(reader);
            XmlElement root = settings.DocumentElement;
            if (root == null) return;
            completionEnabled = root.GetAttribute("completion") == "true";
            proseSpelling = root.GetAttribute("spelling") != "false";
            codeSpelling = root.GetAttribute("codeSpelling") != "false";
            string folder = root.GetAttribute("autosaveFolder");
            if (folder.Length > 0 && Path.IsPathRooted(folder)) autosaveFolder = folder;
            foreach (XmlElement item in root.SelectNodes("color"))
            {
                string language = item.GetAttribute("language");
                int rgb;
                if (CodeLanguages.ContainsKey(language) && int.TryParse(item.InnerText, out rgb))
                    languageColors[language] = Color.FromArgb(rgb);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (XmlException) { }
    }

    void SaveLanguageSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            using (XmlWriter writer = XmlWriter.Create(Path.Combine(SettingsFolder, "language-settings.xml")))
            {
                writer.WriteStartElement("settings");
                writer.WriteAttributeString("completion", XmlConvert.ToString(completionEnabled));
                writer.WriteAttributeString("spelling", XmlConvert.ToString(proseSpelling));
                writer.WriteAttributeString("codeSpelling", XmlConvert.ToString(codeSpelling));
                writer.WriteAttributeString("autosaveFolder", autosaveFolder);
                foreach (var pair in languageColors)
                {
                    writer.WriteStartElement("color");
                    writer.WriteAttributeString("language", pair.Key);
                    writer.WriteString(pair.Value.ToArgb().ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }
        catch (Exception ex) { ShowEditorError("Could not save language settings", ex); }
    }

    string[] SuggestSpelling(string word)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return new string[0];
        try
        {
            if (!checkedSpellSupport)
            {
                checkedSpellSupport = true;
                var factoryClass = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"));
                var factory = (IWindowsSpellFactory)Activator.CreateInstance(factoryClass);
                try
                {
                    string culture = CultureInfo.CurrentUICulture.Name;
                    if (factory.IsSupported(culture)) spellChecker = factory.CreateSpellChecker(culture);
                    else if (factory.IsSupported("en-US")) spellChecker = factory.CreateSpellChecker("en-US");
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
            if (spellChecker == null) return new string[0];
            IWindowsSpellingErrors errors = spellChecker.Check(word);
            IntPtr error = IntPtr.Zero;
            try
            {
                if (errors.Next(out error) != 0 || error == IntPtr.Zero) return new string[0];
            }
            finally
            {
                if (error != IntPtr.Zero) Marshal.Release(error);
                Marshal.ReleaseComObject(errors);
            }

            IEnumString results = spellChecker.Suggest(word);
            try
            {
                var suggestions = new List<string>();
                string[] value = new string[1];
                while (suggestions.Count < 8 && results.Next(1, value, IntPtr.Zero) == 0)
                    suggestions.Add(value[0]);
                return suggestions.ToArray();
            }
            finally { Marshal.ReleaseComObject(results); }
        }
        catch (COMException) { return new string[0]; }
        catch (InvalidCastException) { return new string[0]; }
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWindowsSpellFactory
    {
        IEnumString GetSupportedLanguages();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language);
        IWindowsSpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWindowsSpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetLanguageTag();
        IWindowsSpellingErrors Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWindowsSpellingErrors
    {
        [PreserveSig] int Next(out IntPtr error);
    }

    void RecycleSavedFile()
    {
        string tabPath = Cur == null ? null : Cur.FilePath;
        string path = tabPath ?? notebookPath;
        if (path == null || !File.Exists(path))
        {
            MessageBox.Show(this, "There is no saved file for this tab or notebook.", AppName);
            return;
        }
        if (MessageBox.Show(this, "Move this file to the Windows Recycle Bin?\n\n" + path +
            "\n\nThe open tabs will stay in the editor.", AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("The Windows Recycle Bin is required.");
            if (new DriveInfo(Path.GetPathRoot(path)).DriveType != DriveType.Fixed)
                throw new IOException("This command requires a local fixed drive with a Windows Recycle Bin.");
            RecycleThroughShell(path);
            if (File.Exists(path)) throw new IOException("The Windows shell did not move the file.");
            if (tabPath != null)
            {
                Cur.FilePath = null;
                Cur.Dirty = true;
            }
            else
            {
                notebookPath = null;
                savedNotebook = null;
                foreach (Doc d in strip.Docs)
                {
                    d.Dirty = true;
                    d.WorkbookDirty = true;
                }
            }
            Touch();
        }
        catch (Exception ex) { ShowEditorError("Could not move the file to the Recycle Bin", ex); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IRecycleShellItem item);

    void RecycleThroughShell(string path)
    {
        const uint AllowUndo = 0x40, WarnIfPermanent = 0x4000;
        const uint RecycleOnDelete = 0x80000, EarlyFailure = 0x100000;
        Guid itemInterface = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        IRecycleShellItem item;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemInterface, out item);
        try
        {
            var operation = (IRecycleOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
            try
            {
                operation.SetOperationFlags(AllowUndo | WarnIfPermanent | RecycleOnDelete | EarlyFailure);
                operation.SetOwnerWindow(Handle);
                operation.DeleteItem(item, IntPtr.Zero);
                operation.PerformOperations();
                if (operation.GetAnyOperationsAborted())
                    throw new IOException("The Windows shell did not complete the move to the Recycle Bin.");
            }
            finally { Marshal.ReleaseComObject(operation); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRecycleShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRecycleOperation
    {
        // Preserve the native IFileOperation vtable order.
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(IntPtr message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, IntPtr name, IntPtr sink);
        void RenameItems(IntPtr items, IntPtr name);
        void MoveItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IRecycleShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }
}
