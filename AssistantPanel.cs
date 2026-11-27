using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class ImageNotepad
{
    const string AssistantDefaultModel = "gpt-4.1-mini";
    static readonly Uri AssistantEndpoint = new Uri("https://api.openai.com/v1/responses");

    string assistantModel = AssistantDefaultModel;
    string assistantSessionKey = "";

    void ShowAssistantSettings()
    {
        using (var dialog = new Form
        {
            Text = "AI Assistant Settings", ClientSize = new Size(420, 162),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
        })
        {
            var model = new TextBox { Left = 135, Top = 13, Width = 270, Text = assistantModel,
                AccessibleName = "OpenAI model", MaxLength = 120 };
            var key = new TextBox { Left = 135, Top = 45, Width = 270, Text = assistantSessionKey,
                UseSystemPasswordChar = true, MaxLength = 512, AccessibleName = "OpenAI API key for this session" };
            var ok = new Button { Text = "OK", Left = 245, Top = 124, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 330, Top = 124, DialogResult = DialogResult.Cancel };
            dialog.Controls.Add(new Label { Text = "OpenAI model", Left = 12, Top = 16, AutoSize = true });
            dialog.Controls.Add(new Label { Text = "Session API key", Left = 12, Top = 48, AutoSize = true });
            dialog.Controls.Add(new Label { Text = "Leave the key blank to use OPENAI_API_KEY.\nKeys entered here are kept for this session only.",
                Left = 12, Top = 78, AutoSize = true });
            dialog.Controls.Add(model);
            dialog.Controls.Add(key);
            dialog.Controls.Add(ok);
            dialog.Controls.Add(cancel);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            ok.Click += delegate
            {
                if (!string.IsNullOrWhiteSpace(model.Text)) return;
                dialog.DialogResult = DialogResult.None;
                model.Focus();
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            assistantSessionKey = key.Text.Trim();
            string name = model.Text.Trim();
            if (name == assistantModel) return;
            assistantModel = name;
            SaveLanguageSettings();
        }
    }

    sealed class AssistantToggle : Button
    {
        public bool Expanded { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var letter = new GraphicsPath())
            {
                letter.AddString("t", Font.FontFamily, (int)Font.Style,
                    Font.SizeInPoints * e.Graphics.DpiY / 72f, PointF.Empty, StringFormat.GenericTypographic);
                float size = Math.Max(4, letter.GetBounds().Height);
                float left = Padding.Left + 8 + TextRenderer.MeasureText(e.Graphics, Text, Font,
                    Size.Empty, TextFormatFlags.NoPadding).Width;
                float top = (ClientSize.Height - size) / 2;
                float pointY = Expanded ? top + size : top;
                float baseY = Expanded ? top : top + size;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(ForeColor))
                    e.Graphics.FillPolygon(brush, new[] { new PointF(left, baseY),
                        new PointF(left + size, baseY), new PointF(left + size / 2, pointY) });
            }
        }
    }

    sealed class AssistantDivider : Panel
    {
        int anchorY;
        public Action<int> DragBy { get; set; }

        public AssistantDivider()
        {
            Cursor = Cursors.SizeNS;
            BackColor = SystemColors.Control;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int y = ClientSize.Height / 2;
            int x = ClientSize.Width / 2;
            e.Graphics.DrawLine(SystemPens.ControlDark, 0, y, ClientSize.Width, y);
            e.Graphics.DrawLine(SystemPens.ControlDarkDark, x - 15, y - 1, x + 15, y - 1);
            e.Graphics.DrawLine(SystemPens.ControlDarkDark, x - 15, y + 1, x + 15, y + 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            anchorY = PointToScreen(e.Location).Y;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Capture || DragBy == null) return;
            int y = PointToScreen(e.Location).Y;
            int delta = anchorY - y;
            anchorY = y;
            if (delta != 0) DragBy(delta);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Capture = false;
            base.OnMouseUp(e);
        }
    }

    Control CreateAssistantArea(Control editor)
    {
        const int HeaderHeight = 48;
        var workspace = new Panel { Dock = DockStyle.Fill };
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 300 };
        var editorDivider = new AssistantDivider { Dock = DockStyle.Bottom, Height = 10,
            AccessibleName = "Resize AI Assistant" };

        var toggle = new AssistantToggle { Dock = DockStyle.Top, Height = HeaderHeight, Text = "AI Assistant", Expanded = true,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        var promptArea = new Panel { Dock = DockStyle.Top, Height = 112 };
        var promptDivider = new Splitter { Dock = DockStyle.Top, Height = 5, MinSize = 76, MinExtra = 30,
            BackColor = SystemColors.ControlDark, AccessibleName = "Resize prompt and response" };

        var prompt = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            MaxLength = 50000, AccessibleName = "Prompt for the AI Assistant" };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, WrapContents = false };
        var send = new Button { Text = "Send", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
        var includeSelection = new CheckBox { Text = "Include selected text", AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
        var status = new Label { AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
        controls.Controls.Add(send);
        controls.Controls.Add(cancel);
        controls.Controls.Add(includeSelection);
        controls.Controls.Add(status);
        promptArea.Controls.Add(prompt);
        promptArea.Controls.Add(controls);
        promptArea.Controls.Add(new Label { Text = "Prompt", Dock = DockStyle.Top, Height = 18 });

        var answer = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "AI Assistant answer" };
        body.Controls.Add(answer);
        body.Controls.Add(promptDivider);
        body.Controls.Add(promptArea);
        panel.Controls.Add(body);
        panel.Controls.Add(toggle);
        workspace.Controls.Add(editor);
        workspace.Controls.Add(editorDivider);
        workspace.Controls.Add(panel);

        bool expanded = true;
        int expandedHeight = panel.Height;
        Func<int> maximum = () => Math.Max(HeaderHeight, workspace.Height - 85 - editorDivider.Height);
        Action resize = delegate
        {
            if (expanded)
                panel.Height = Math.Min(expandedHeight, maximum());
        };
        workspace.SizeChanged += delegate { resize(); };
        editorDivider.DragBy = delegate (int delta)
        {
            int max = maximum();
            expandedHeight = Math.Max(Math.Min(180, max), Math.Min(panel.Height + delta, max));
            resize();
        };
        body.Resize += delegate
        {
            int limit = body.ClientSize.Height - body.Padding.Vertical - promptDivider.Height - promptDivider.MinExtra;
            if (limit >= promptDivider.MinSize && promptArea.Height > limit) promptArea.Height = limit;
        };
        toggle.Click += delegate
        {
            expanded = !expanded;
            body.Visible = expanded;
            editorDivider.Visible = expanded;
            toggle.Expanded = expanded;
            toggle.Invalidate();
            if (expanded) { resize(); prompt.Focus(); }
            else panel.Height = HeaderHeight;
        };

        var tips = new ToolTip();
        tips.SetToolTip(toggle, "Expand or collapse the AI Assistant.");
        tips.SetToolTip(editorDivider, "Drag to change the height of the AI Assistant.");
        tips.SetToolTip(promptDivider, "Drag to give the prompt or response more space.");
        tips.SetToolTip(prompt, "Type a prompt to send to OpenAI. Press Ctrl+Enter to send.");
        tips.SetToolTip(includeSelection, "Send the text currently selected in the active note with this prompt.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        CancellationTokenSource pending = null;
        panel.Disposed += delegate
        {
            if (pending != null) pending.Cancel();
            client.Dispose();
            tips.Dispose();
        };
        cancel.Click += delegate { if (pending != null) pending.Cancel(); };
        send.Click += async delegate
        {
            if (pending != null) return;
            string request = prompt.Text.Trim();
            string modelName = assistantModel;
            string apiKey = assistantSessionKey;
            if (apiKey.Length == 0) apiKey = (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim();
            if (request.Length == 0) { status.Text = "Enter a prompt."; prompt.Focus(); return; }
            if (apiKey.Length == 0) { status.Text = "Set the API key in Settings > AI Assistant."; return; }

            string selectedText = includeSelection.Checked && Cur != null ? Cur.Rtb.SelectedText : "";
            if (includeSelection.Checked && selectedText.Length == 0)
            {
                status.Text = "Select text in the note or uncheck the option.";
                return;
            }
            if (selectedText.Length > 50000)
            {
                status.Text = "Select up to 50,000 characters.";
                return;
            }

            pending = new CancellationTokenSource();
            send.Enabled = false;
            cancel.Enabled = true;
            status.Text = "Sending to OpenAI…";
            try
            {
                string response = await RequestAssistantAnswer(client, modelName, apiKey, request, selectedText, pending.Token);
                if (!panel.IsDisposed)
                {
                    answer.Text = response.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    answer.SelectionStart = 0;
                    status.Text = "Response received.";
                }
            }
            catch (OperationCanceledException)
            {
                if (!panel.IsDisposed) status.Text = pending.IsCancellationRequested ? "Request canceled." : "Request timed out.";
            }
            catch (Exception ex)
            {
                if (!panel.IsDisposed) status.Text = ex is AssistantApiException ? ex.Message : "Could not reach the API or read its response.";
            }
            finally
            {
                pending.Dispose();
                pending = null;
                if (!panel.IsDisposed) { send.Enabled = true; cancel.Enabled = false; }
            }
        };
        prompt.KeyDown += delegate (object sender, KeyEventArgs e)
        {
            if (!e.Control || e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            send.PerformClick();
        };
        return workspace;
    }

    sealed class AssistantApiException : Exception
    {
        public AssistantApiException(string message) : base(message) { }
    }

    static async Task<string> RequestAssistantAnswer(HttpClient client, string model, string apiKey,
        string prompt, string selection, CancellationToken cancellation)
    {
        var json = new JavaScriptSerializer();
        string input = selection.Length == 0 ? prompt : prompt + "\n\nSelected note text follows:\n" + selection;
        var payload = new { model = model, input = input, store = false, max_output_tokens = 1500 };
        using (var request = new HttpRequestMessage(HttpMethod.Post, AssistantEndpoint))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
            using (HttpResponseMessage response = await client.SendAsync(request, cancellation).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new AssistantApiException(AssistantError(response.StatusCode));
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ReadAssistantAnswer(json.DeserializeObject(body) as IDictionary<string, object>);
            }
        }
    }

    static string AssistantError(HttpStatusCode status)
    {
        switch ((int)status)
        {
            case 400: return "OpenAI rejected the request. Check the model and prompt.";
            case 401: return "OpenAI rejected the API key.";
            case 403: return "The API key does not have access.";
            case 404: return "The model is unavailable for this API key.";
            case 429: return "OpenAI rate limit or quota reached.";
            default: return "OpenAI request failed (HTTP " + (int)status + ").";
        }
    }

    static object AssistantField(IDictionary<string, object> item, string name)
    {
        object value;
        return item != null && item.TryGetValue(name, out value) ? value : null;
    }

    static string ReadAssistantAnswer(IDictionary<string, object> response)
    {
        var pieces = new List<string>();
        foreach (object rawItem in (AssistantField(response, "output") as IEnumerable) ?? new object[0])
        {
            var item = rawItem as IDictionary<string, object>;
            if (!Equals(AssistantField(item, "type"), "message") || !Equals(AssistantField(item, "role"), "assistant")) continue;
            foreach (object rawPart in (AssistantField(item, "content") as IEnumerable) ?? new object[0])
            {
                var part = rawPart as IDictionary<string, object>;
                object type = AssistantField(part, "type");
                string text = Equals(type, "output_text") ? AssistantField(part, "text") as string :
                    Equals(type, "refusal") ? AssistantField(part, "refusal") as string : null;
                if (!string.IsNullOrWhiteSpace(text)) pieces.Add(text);
            }
        }
        if (pieces.Count == 0) throw new AssistantApiException("OpenAI returned no displayable text.");
        if (Equals(AssistantField(response, "status"), "incomplete")) pieces.Add("[Response incomplete]");
        return string.Join("\n\n", pieces);
    }
}
