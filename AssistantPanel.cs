using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
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

    Control CreateAssistantArea(Control editor)
    {
        var workspace = new Panel { Dock = DockStyle.Fill };
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(0, 1, 0, 0) };
        panel.Paint += delegate (object sender, PaintEventArgs e)
        {
            e.Graphics.DrawLine(SystemPens.ControlDark, 0, 0, panel.ClientSize.Width, 0);
        };

        var toggle = new Button { Dock = DockStyle.Top, Height = 29, Text = "AI Assistant  ▸", TextAlign = ContentAlignment.MiddleLeft };
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6), Visible = false };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var model = new TextBox { Text = AssistantDefaultModel, Width = 130, AccessibleName = "OpenAI model" };
        var key = new TextBox { Width = 165, UseSystemPasswordChar = true, MaxLength = 512, AccessibleName = "OpenAI API key for this session" };
        options.Controls.Add(new Label { Text = "OpenAI model", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        options.Controls.Add(model);
        options.Controls.Add(new Label { Text = "Session API key", AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
        options.Controls.Add(key);

        var prompt = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            MaxLength = 50000, AccessibleName = "Prompt for the AI Assistant" };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var send = new Button { Text = "Send", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
        var includeSelection = new CheckBox { Text = "Include selected text", AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
        var status = new Label { AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
        controls.Controls.Add(send);
        controls.Controls.Add(cancel);
        controls.Controls.Add(includeSelection);
        controls.Controls.Add(status);

        var answer = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "AI Assistant answer" };
        body.Controls.Add(options, 0, 0);
        body.Controls.Add(new Label { Text = "Prompt", AutoSize = true }, 0, 1);
        body.Controls.Add(prompt, 0, 2);
        body.Controls.Add(controls, 0, 3);
        body.Controls.Add(answer, 0, 4);
        panel.Controls.Add(body);
        panel.Controls.Add(toggle);
        workspace.Controls.Add(editor);
        workspace.Controls.Add(panel);

        bool expanded = false;
        Action resize = delegate
        {
            panel.Height = expanded ? Math.Min(320, Math.Max(178, workspace.Height / 2)) : 30;
        };
        workspace.SizeChanged += delegate { resize(); };
        toggle.Click += delegate
        {
            expanded = !expanded;
            body.Visible = expanded;
            toggle.Text = expanded ? "AI Assistant  ▾" : "AI Assistant  ▸";
            resize();
            if (expanded) prompt.Focus();
        };

        var tips = new ToolTip();
        tips.SetToolTip(key, "Leave blank to use the OPENAI_API_KEY environment variable. Entered keys are never saved.");
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
            string modelName = model.Text.Trim();
            string apiKey = key.Text.Trim();
            if (apiKey.Length == 0) apiKey = (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim();
            if (request.Length == 0) { status.Text = "Enter a prompt."; prompt.Focus(); return; }
            if (modelName.Length == 0) { status.Text = "Enter a model name."; model.Focus(); return; }
            if (apiKey.Length == 0) { status.Text = "Enter an API key or set OPENAI_API_KEY."; key.Focus(); return; }

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
