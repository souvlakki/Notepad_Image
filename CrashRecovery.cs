using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

partial class ImageNotepad
{
    static readonly Regex CountableWord = new Regex(@"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['’][\p{L}\p{N}][\p{L}\p{M}\p{N}]*)*");
    ToolStripStatusLabel wordCountStatus, crashStatus;
    Timer crashTimer;
    FileStream crashMarker;
    string crashSnapshot;
    bool crashSnapshotPending, crashMarkerHasSnapshot, keepCrashSnapshot;

    static string CrashFolder { get { return Path.Combine(SettingsFolder, "Crash Recovery"); } }

    static int CountWords(string text)
    {
        int count = 0;
        for (Match word = CountableWord.Match(text); word.Success; word = word.NextMatch()) count++;
        return count;
    }

    void UpdateWordCount()
    {
        if (wordCountStatus != null && Cur != null)
            wordCountStatus.Text = "Words: " + CountWords(Cur.Rtb.Text).ToString("N0");
    }

    void InitializeCrashRecovery(bool offerOnStartup)
    {
        crashTimer = new Timer { Interval = 1000 };
        crashTimer.Tick += delegate { WriteCrashSnapshot(); };
        crashTimer.Start();
        FormClosed += delegate
        {
            if (!keepCrashSnapshot && crashMarkerHasSnapshot)
            {
                try { SetCrashMarker(false); }
                catch (IOException) { }
            }
        };
        Disposed += delegate
        {
            crashTimer.Dispose();
            if (crashMarker != null) crashMarker.Dispose();
        };
        if (offerOnStartup) Shown += delegate { BeginInvoke(new Action(OfferCrashRecovery)); };
    }

    void ScheduleCrashSnapshot()
    {
        crashSnapshotPending = true;
    }

    bool WriteCrashSnapshot()
    {
        if (!crashSnapshotPending) return crashMarkerHasSnapshot;
        try
        {
            if (strip.Docs.Any(d => d.Dirty) || NotebookChanged())
            {
                if (crashMarker == null)
                {
                    Directory.CreateDirectory(CrashFolder);
                    string prefix = Path.Combine(CrashFolder, "Session-" + Guid.NewGuid().ToString("N"));
                    crashMarker = new FileStream(prefix + ".state", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    crashSnapshot = prefix + ".inote";
                }
                WriteNotebook(crashSnapshot, NotebookData());
                if (!crashMarkerHasSnapshot) SetCrashMarker(true);
            }
            else if (crashMarkerHasSnapshot) SetCrashMarker(false);
            crashStatus.Visible = false;
            crashSnapshotPending = false;
        }
        catch (IOException) { crashStatus.Visible = true; }
        catch (UnauthorizedAccessException) { crashStatus.Visible = true; }
        return crashMarkerHasSnapshot && !crashSnapshotPending;
    }

    void SetCrashMarker(bool hasSnapshot)
    {
        crashMarker.Position = 0;
        crashMarker.WriteByte(hasSnapshot ? (byte)1 : (byte)0);
        crashMarker.Flush(true);
        crashMarkerHasSnapshot = hasSnapshot;
    }

    static string FindCrashSnapshot(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (string path in Directory.GetFiles(folder, "Session-*.inote")
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                // Live sessions hold an exclusive handle and are skipped.
                using (FileStream marker = File.Open(Path.ChangeExtension(path, ".state"),
                    FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (marker.ReadByte() == 1) return path;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    void OfferCrashRecovery()
    {
        string path;
        try { path = FindCrashSnapshot(CrashFolder); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (path == null || MessageBox.Show(this,
            "Image Notepad found tabs saved before an unexpected shutdown. Restore the latest recovery copy?",
            "Crash Recovery", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        if (!OpenNotebook(path, true) || !WriteCrashSnapshot()) return;
        try
        {
            using (FileStream marker = File.Open(Path.ChangeExtension(path, ".state"),
                FileMode.Open, FileAccess.Write, FileShare.None))
            {
                marker.WriteByte(0);
                marker.Flush(true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void OpenCrashRecovery()
    {
        using (OpenFileDialog dialog = new OpenFileDialog
        {
            Filter = "Crash recovery notebooks (*.inote)|*.inote",
            InitialDirectory = CrashFolder
        })
            if (dialog.ShowDialog(this) == DialogResult.OK) OpenNotebook(dialog.FileName, true);
    }
}
