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
        { "C++", new[] { ".cpp .cc .cxx .hpp .hh .hxx .h .ipp .tpp .ixx .cppm", "alignas alignof asm auto bool break case catch char class concept const consteval constexpr constinit continue co_await co_return co_yield decltype default delete do double else enum explicit export extern false float for friend if import inline int long module mutable namespace new noexcept nullptr operator override private protected public requires return short signed sizeof static static_assert struct switch template this throw true try typedef typename union unsigned using virtual void volatile while",
            "array begin cerr cin cout deque endl end exception filesystem find forward getline iostream iterator list make_shared make_unique map move optional pair printf queue ranges set shared_ptr size_t sort span stack std string string_view tuple unique_ptr unordered_map unordered_set variant vector" } },
        { "HTML", new[] { ".html .htm .xhtml", "a abbr address article aside audio b blockquote body br button canvas caption code col colgroup data datalist dd details dialog div dl dt em fieldset figcaption figure footer form h1 h2 h3 h4 h5 h6 head header hr html i iframe img input label legend li link main mark menu meta meter nav ol optgroup option output p picture pre progress script section select small source span strong style summary table tbody td template textarea tfoot th thead time title tr track ul video",
            "action alt aria-label aria-labelledby aria-hidden async autocomplete charset class content data- defer disabled for height hidden href id lang loading method name placeholder rel required role src srcset tabindex target type value width" } },
        { "PHP", new[] { ".php .phtml .php3 .php4 .php5 .php7 .phps", "abstract and array as break callable case catch class clone const continue declare default die do echo else elseif empty endfor endforeach endif endswitch endwhile enum eval exit extends final finally fn for foreach function global if implements include include_once instanceof interface isset list match namespace new or print private protected public readonly require require_once return static switch throw trait try unset use var while xor yield",
            "_COOKIE _ENV _FILES _GET _POST _REQUEST _SERVER _SESSION array_filter array_keys array_map array_merge array_reduce array_values count date explode file_get_contents file_put_contents filter_var htmlspecialchars implode json_decode json_encode mysqli_connect preg_match printf sprintf str_contains str_replace strlen strpos substr trim var_dump" } },
        { "CSS", new[] { ".css", "align-content align-items animation aspect-ratio background background-color background-image border border-color border-radius border-width box-shadow box-sizing color column-count content cursor display fill filter flex flex-direction flex-grow flex-wrap font font-family font-size font-weight gap grid grid-area grid-template-columns grid-template-rows height justify-content left letter-spacing line-height margin margin-bottom margin-left margin-right margin-top max-height max-width min-height min-width object-fit opacity order outline overflow padding padding-bottom padding-left padding-right padding-top position right text-align text-decoration text-transform top transform transition user-select visibility white-space width z-index",
            "absolute auto block bold border-box calc center clamp currentColor fixed flex grid inherit initial inline inline-block minmax none relative repeat rgba solid sticky transparent unset var" } },
        { "SQL", new[] { ".sql", "ADD ALL ALTER AND AS ASC BETWEEN BY CASE CHECK COLUMN CREATE CROSS DATABASE DEFAULT DELETE DESC DISTINCT DROP ELSE END EXISTS FETCH FOREIGN FROM FULL GRANT GROUP HAVING IN INDEX INNER INSERT INTERSECT INTO IS JOIN LEFT LIKE LIMIT NOT NULL OFFSET ON OR ORDER OUTER OVER PARTITION PRIMARY REFERENCES RIGHT ROLLBACK SELECT SET TABLE THEN TRANSACTION TRUNCATE UNION UNIQUE UPDATE USING VALUES VIEW WHEN WHERE WITH",
            "ABS AVG CAST COALESCE CONCAT COUNT CURRENT_DATE CURRENT_TIMESTAMP DENSE_RANK LENGTH LOWER MAX MIN NULLIF RANK ROUND ROW_NUMBER SUBSTRING SUM TRIM UPPER VARCHAR INTEGER DECIMAL" } }
    };

    static readonly Regex Identifier = new Regex(@"[\p{L}_][\p{L}\p{M}\d_]*");
    static readonly Regex HyphenatedIdentifier = new Regex(@"[-\p{L}_][-\p{L}\p{M}\d_]*");
    static readonly Regex ProseWord = new Regex(@"\p{L}[\p{L}\p{M}]*(?:['’][\p{L}\p{M}]+)*");
    readonly Dictionary<string, Color> languageColors = new Dictionary<string, Color>();
    bool completionEnabled, proseSpelling = true, codeSpelling = true;
    string autosaveFolder;
    ToolStripStatusLabel languageStatus, workbookPathStatus;
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

    static bool IsSourcePath(string path)
    {
        return DetectLanguage(path).Length > 0 ||
            string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);
    }

    static Regex CodeToken(string language)
    {
        return language == "CSS" || language == "HTML" ? HyphenatedIdentifier : Identifier;
    }

    static StringComparer CodeComparer(string language)
    {
        return language == "HTML" || language == "CSS" || language == "SQL" || language == "PHP"
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
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
        StatusStrip bar = new StatusStrip { SizingGrip = false, ShowItemToolTips = true };
        wordCountStatus = new ToolStripStatusLabel("Words: 0");
        languageStatus = new ToolStripStatusLabel("Plain text");
        spellingStatus = new ToolStripDropDownButton { Visible = false };
        bar.Items.Add(wordCountStatus);
        bar.Items.Add(languageStatus);
        workbookPathStatus = new ToolStripStatusLabel
        {
            Spring = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(12, 3, 0, 2), Visible = false
        };
        bar.Items.Add(workbookPathStatus);
        bar.Items.Add(spellingStatus);
        crashStatus = new ToolStripStatusLabel("Crash recovery unavailable") { Visible = false };
        bar.Items.Add(crashStatus);
        Controls.Add(bar);
        spellingTimer = new Timer { Interval = 450 };
        spellingTimer.Tick += delegate { spellingTimer.Stop(); CheckSpelling(); };
        FormClosed += delegate
        {
            spellingTimer.Dispose();
            if (completionMenu != null) completionMenu.Dispose();
            if (spellChecker != null) Marshal.ReleaseComObject(spellChecker);
        };
    }

    void RefreshWorkbookPath()
    {
        if (workbookPathStatus == null) return;
        workbookPathStatus.Text = notebookPath ?? "";
        workbookPathStatus.ToolTipText = workbookPathStatus.Text;
        workbookPathStatus.Visible = notebookPath != null;
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
            (pattern == HyphenatedIdentifier && text[start - 1] == '-') ||
            CharUnicodeInfo.GetUnicodeCategory(text[start - 1]) == UnicodeCategory.NonSpacingMark))
            start--;
        Match match = pattern.Match(text, start);
        return match.Success && match.Index <= caret && match.Index + match.Length >= caret ? match : null;
    }

    static string[] Complete(string language, string prefix, string text)
    {
        string[] data = CodeLanguages[language];
        StringComparer comparer = CodeComparer(language);
        StringComparison comparison = comparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var local = CodeToken(language).Matches(text).Cast<Match>()
            .Select(match => match.Value).Where(value => value.Length > prefix.Length);
        return data[1].Split(' ').Concat(data[2].Split(' ')).Concat(local)
            .Where(value => value.StartsWith(prefix, comparison) && !comparer.Equals(value, prefix))
            .Distinct(comparer).Take(16).ToArray();
    }

    void ShowCompletion()
    {
        RichTextBox box = Cur.Rtb;
        int caret = box.SelectionStart;
        Match word = WordAt(box.Text, caret, CodeToken(Cur.Language), false);
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
        StringComparer comparer = CodeComparer(language);
        if (word.Length < 3 || words.Contains(word, comparer) || CodeLanguages[language][2].Split(' ').Contains(word, comparer))
            return new string[0];
        // Repeated identifiers are likely intentional project names.
        if (Regex.Matches(source, @"(?<![\w])" + Regex.Escape(word) + @"(?![\w])").Count > 1)
            return new string[0];
        return words.Where(keyword => comparer == StringComparer.OrdinalIgnoreCase
            ? OneEditApart(word.ToUpperInvariant(), keyword.ToUpperInvariant()) : OneEditApart(word, keyword)).ToArray();
    }

    void CheckSpelling()
    {
        if (Cur == null || Cur.Loading) return;
        RichTextBox box = Cur.Rtb;
        bool code = Cur.Language.Length > 0;
        if (code ? !codeSpelling : !proseSpelling) return;
        Match word = WordAt(box.Text, box.SelectionStart, code ? CodeToken(Cur.Language) : ProseWord, true);
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
            string model = root.GetAttribute("assistantModel").Trim();
            if (model.Length > 0) assistantModel = model;
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
                writer.WriteAttributeString("assistantModel", assistantModel);
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
                RefreshWorkbookPath();
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
