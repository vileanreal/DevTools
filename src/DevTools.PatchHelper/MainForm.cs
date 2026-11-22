using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DevTools.PatchHelper
{
    /// <summary>
    /// The helper window: the patch, the module folders on this server, every step with its status, a progress bar and a
    /// log. Work runs on a background thread; the window only shows progress.
    /// </summary>
    public sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(23, 32, 51);
        private static readonly Color Muted = Color.FromArgb(102, 112, 133);
        private static readonly Color Green = Color.FromArgb(6, 118, 71);
        private static readonly Color Blue = Color.FromArgb(37, 99, 235);
        private static readonly Color Red = Color.FromArgb(180, 35, 24);
        private static readonly Color Amber = Color.FromArgb(181, 71, 8);

        private readonly string _patchFolder;
        private readonly PatchManifest _manifest;
        private readonly PatchEngine _engine;
        private readonly FileLogReporter _fileLog;
        private readonly Dictionary<string, TextBox> _folders = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ListViewItem> _rows = new Dictionary<string, ListViewItem>();
        private readonly ListView _steps = new ListView();
        private readonly TextBox _log = new TextBox();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _progressText = new Label();
        private readonly Label _status = new Label();
        private readonly Button _check = new Button(), _apply = new Button(), _rollback = new Button(), _openFolder = new Button(), _copyReport = new Button(), _close = new Button();
        private ApplyPlan? _plan;
        private CancellationTokenSource? _running;

        public MainForm(string patchFolder, PatchManifest manifest)
        {
            _patchFolder = patchFolder;
            _manifest = manifest;
            _fileLog = new FileLogReporter(Path.Combine(patchFolder, $"apply-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
            _engine = new PatchEngine(patchFolder, manifest, new WindowsServiceControl(), new UiReporter(this));
            Text = $"{manifest.Title} — {manifest.PatchName}";
            Font = new Font("Segoe UI", 9f);
            MinimumSize = new Size(820, 640);
            Size = new Size(980, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            BuildLayout();
            Shown += (_, __) => OfferResume();
            FormClosing += OnClosing;
        }

        // ---------- Layout ----------

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // Header: title, subtitle and what this patch is.
            var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            header.Controls.Add(new Label { Text = _manifest.Title, AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), ForeColor = Ink });
            if (!string.IsNullOrWhiteSpace(_manifest.Subtitle)) header.Controls.Add(new Label { Text = _manifest.Subtitle, AutoSize = true, ForeColor = Muted });
            var modules = string.Join(", ", _manifest.Modules.Select(module => $"{module.Name} ({module.Files.Count})"));
            header.Controls.Add(new Label
            {
                Text = $"{_manifest.PatchName}{(string.IsNullOrWhiteSpace(_manifest.Description) ? "" : " — " + _manifest.Description)}\r\nModules: {modules} · compiled {_manifest.CreatedAt}{(string.IsNullOrWhiteSpace(_manifest.CreatedBy) ? "" : " by " + _manifest.CreatedBy)}",
                AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Muted, Margin = new Padding(3, 4, 3, 0)
            });
            root.Controls.Add(header);

            // Module folders on this server.
            var group = new GroupBox { Text = "Module folders on this server", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var remembered = TargetMemory.Load(_manifest.System);
            foreach (var module in _manifest.Modules)
            {
                var label = new Label { Text = module.Name, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 7, 10, 3) };
                var box = new TextBox { Dock = DockStyle.Fill, Text = remembered.TryGetValue(module.Name, out var folder) ? folder : module.ClientFolder ?? "" };
                box.TextChanged += (_, __) => { _plan = null; UpdateButtons(); };
                var browse = new Button { Text = "Browse…", AutoSize = true };
                browse.Click += (_, __) =>
                {
                    using (var dialog = new FolderBrowserDialog { Description = $"Folder of {module.Name} on this server", SelectedPath = box.Text })
                        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                };
                var services = new[] { string.IsNullOrWhiteSpace(module.AppPool) ? null : "app pool " + module.AppPool, string.IsNullOrWhiteSpace(module.Service) ? null : "service " + module.Service }.Where(text => text != null);
                var note = new Label { Text = services.Any() ? "Stops " + string.Join(" and ", services) : "No app pool or service", AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left, Margin = new Padding(8, 7, 3, 3) };
                _folders[module.Name] = box;
                grid.Controls.AddRange(new Control[] { label, box, browse, note });
            }
            group.Controls.Add(grid);
            root.Controls.Add(group);

            // Steps.
            _steps.Dock = DockStyle.Fill;
            _steps.View = View.Details;
            _steps.FullRowSelect = true;
            _steps.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _steps.Columns.Add("", 34);
            _steps.Columns.Add("Step", 260);
            _steps.Columns.Add("Status", 110);
            _steps.Columns.Add("Detail", 480);
            foreach (var step in _engine.Steps()) AddStepRow(step.Key, step.Value);
            _steps.Resize += (_, __) => _steps.Columns[3].Width = Math.Max(200, _steps.ClientSize.Width - 34 - 260 - 110 - 4);
            root.Controls.Add(_steps);

            // Log.
            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font("Consolas", 9f);
            _log.BackColor = Color.FromArgb(247, 250, 251);
            root.Controls.Add(_log);

            // Progress, status and buttons.
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            _progress.Dock = DockStyle.Fill;
            _progress.Height = 18;
            bottom.Controls.Add(_progress);
            _progressText.AutoSize = true;
            _progressText.ForeColor = Muted;
            bottom.Controls.Add(_progressText);
            _status.AutoSize = true;
            _status.MaximumSize = new Size(920, 0);
            _status.Font = new Font("Segoe UI Semibold", 10f);
            _status.Margin = new Padding(3, 6, 3, 6);
            bottom.Controls.Add(_status);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            Setup(_check, "1. Check", (_, __) => RunCheck());
            Setup(_apply, "2. Apply", (_, __) => RunApply(resume: null));
            Setup(_rollback, "Roll back", (_, __) => RunRollback());
            Setup(_openFolder, "Open patch folder", (_, __) => Process.Start("explorer.exe", $"\"{_patchFolder}\""));
            Setup(_copyReport, "Copy report", (_, __) => { Clipboard.SetText(Report()); SetStatus("The report was copied. Paste it into an email or ticket.", Blue); });
            Setup(_close, "Close", (_, __) => Close());
            _apply.Font = new Font(Font, FontStyle.Bold);
            buttons.Controls.AddRange(new Control[] { _check, _apply, _rollback, _openFolder, _copyReport, _close });
            bottom.Controls.Add(buttons);
            root.Controls.Add(bottom);

            SetStatus("Check the module folders, then press “1. Check”. Nothing is changed until you press “2. Apply”.", Muted);
            UpdateButtons();
        }

        private static void Setup(Button button, string text, EventHandler click)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Padding = new Padding(10, 4, 10, 4);
            button.Click += click;
        }

        private void AddStepRow(string key, string label)
        {
            var row = new ListViewItem(new[] { "○", label, "Not started", "" }) { ForeColor = Muted, UseItemStyleForSubItems = true };
            _rows[key] = row;
            _steps.Items.Add(row);
        }

        // ---------- Actions ----------

        private Dictionary<string, string> Targets() =>
            _folders.ToDictionary(item => item.Key, item => item.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);

        private void RunCheck() => RunBackground("Checking", token =>
        {
            ResetRows();
            _engine.CheckPatch(token);
            var plan = _engine.Compare(Targets(), token);
            TargetMemory.Save(_manifest.System, plan.Targets);
            Invoke(new Action(() =>
            {
                _plan = plan;
                var warnings = plan.Warnings.Count();
                if (warnings > 0) SetStatus($"Ready to apply, with {warnings} warning(s): some files on this server aren't the version the patch was made from. See the log, then press “2. Apply” if that's expected.", Amber);
                else if (plan.Files.All(file => file.Action == FileAction.Skip)) SetStatus("This server already has every file of this patch. Nothing needs to be applied.", Green);
                else SetStatus($"Ready to apply: {plan.Count(FileAction.Add)} to add, {plan.Count(FileAction.Replace)} to replace, {plan.Count(FileAction.Delete)} to delete. Press “2. Apply”.", Blue);
            }));
        });

        private void RunApply(ApplyState? resume)
        {
            if (_plan == null) { MessageBox.Show(this, "Press “1. Check” first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var warnings = _plan.Warnings.ToList();
            if (warnings.Count > 0)
            {
                var list = string.Join("\r\n", warnings.Take(15).Select(item => $"• {item.Module}/{item.File.Path} — {item.Note}")) + (warnings.Count > 15 ? $"\r\n…and {warnings.Count - 15} more (see the log)" : "");
                if (MessageBox.Show(this, $"{warnings.Count} file(s) on this server aren't the version this patch was made from:\r\n\r\n{list}\r\n\r\nApply anyway? Everything is backed up first.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            var stops = _manifest.Modules.SelectMany(module => new[] { module.AppPool, module.Service }).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
            if (resume == null && MessageBox.Show(this, $"Apply {_manifest.PatchName} to {string.Join(", ", _manifest.Modules.Select(module => module.Name))}?{(stops.Count > 0 ? $"\r\n\r\n{string.Join(", ", stops)} will be stopped while the files are replaced, then started again." : "")}", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var state = resume ?? new ApplyState();
            var plan = _plan;
            RunBackground("Applying", token => _engine.Apply(plan, state, token), onSuccess: () =>
            {
                SetStatus($"Done. The patch was applied and verified. Backup: {Path.GetFileName(state.BackupFolder)}. Use “Copy report” to send a summary.", Green);
                _plan = null;
            }, onFailure: message => AfterFailure(state, message));
        }

        private void AfterFailure(ApplyState state, string message)
        {
            SetStatus($"Stopped: {message}{(string.IsNullOrWhiteSpace(_manifest.SupportContact) ? "" : "\r\n" + _manifest.SupportContact)}", Red);
            var choice = MessageBox.Show(this,
                $"The patch was not fully applied:\r\n{message}\r\n\r\nYes — retry (for example after closing a program that locks a file)\r\nNo — roll back: restore the backup and start the services again\r\nCancel — leave everything as it is for now",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Error);
            if (choice == DialogResult.Yes) RunBackground("Retrying", token =>
            {
                var plan = _engine.Compare(state.Targets, token);
                Invoke(new Action(() => _plan = plan));
                _engine.Apply(plan, state, token);
            }, onSuccess: () => SetStatus("Done. The patch was applied and verified after retrying.", Green), onFailure: retry => AfterFailure(state, retry));
            else if (choice == DialogResult.No) RunRollback(state);
        }

        private void RunRollback(ApplyState? state = null)
        {
            state = state ?? ApplyState.TryLoad(_patchFolder);
            if (state == null || string.IsNullOrEmpty(state.BackupFolder)) { MessageBox.Show(this, "There is nothing to roll back: this patch hasn't been applied from this folder.", Text); return; }
            if (state.Status == "rolled-back") { MessageBox.Show(this, "This patch was already rolled back.", Text); return; }
            if (MessageBox.Show(this, $"Restore the files from {Path.GetFileName(state.BackupFolder)} and remove the files this patch added?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            RunBackground("Rolling back", token => _engine.Rollback(state, token),
                onSuccess: () => { SetStatus("Rolled back. The server has the files it had before this patch.", Amber); _plan = null; },
                onFailure: message => SetStatus($"The rollback failed: {message}. The backup is in {state.BackupFolder}.", Red));
        }

        /// <summary>A previous run of this patch that didn't finish can be continued or rolled back.</summary>
        private void OfferResume()
        {
            var state = ApplyState.TryLoad(_patchFolder);
            if (state == null)
            {
                if (_folders.Values.All(box => box.Text.Length > 0)) RunCheck();
                return;
            }
            foreach (var target in state.Targets) if (_folders.TryGetValue(target.Key, out var box)) box.Text = target.Value;
            if (state.Status == "applied") { SetStatus($"This patch was already applied from this folder on {state.StartedAt}. You can roll it back, or check again.", Green); UpdateButtons(); return; }
            if (!state.IsUnfinished) { UpdateButtons(); return; }
            var done = state.CompletedSteps.Count == 0 ? "no step finished" : "finished: " + string.Join(", ", state.CompletedSteps);
            var choice = MessageBox.Show(this,
                $"This patch was started on {state.StartedAt} but not finished ({done}).\r\n\r\nYes — continue where it stopped\r\nNo — roll back to the backup\r\nCancel — decide later",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (choice == DialogResult.Yes) RunBackground("Continuing", token =>
            {
                _engine.CheckPatch(token);
                var plan = _engine.Compare(state.Targets, token);
                Invoke(new Action(() => _plan = plan));
                _engine.Apply(plan, state, token);
            }, onSuccess: () => SetStatus("Done. The patch was applied and verified.", Green), onFailure: message => AfterFailure(state, message));
            else if (choice == DialogResult.No) RunRollback(state);
            else UpdateButtons();
        }

        private void RunBackground(string label, Action<CancellationToken> work, Action? onSuccess = null, Action<string>? onFailure = null)
        {
            _running = new CancellationTokenSource();
            var token = _running.Token;
            SetStatus(label + "…", Blue);
            UpdateButtons();
            Task.Run(() => work(token), token).ContinueWith(task =>
            {
                BeginInvoke(new Action(() =>
                {
                    _running = null;
                    _progress.Value = 0;
                    _progressText.Text = "";
                    if (task.IsFaulted)
                    {
                        var error = task.Exception!.GetBaseException();
                        var message = error is PatchHelperException || error is IOException || error is UnauthorizedAccessException ? error.Message : $"{error.GetType().Name}: {error.Message}";
                        Log("Error: " + message);
                        if (onFailure != null) onFailure(message); else SetStatus(message, Red);
                    }
                    else if (task.IsCanceled) SetStatus("Cancelled.", Amber);
                    else onSuccess?.Invoke();
                    UpdateButtons();
                }));
            });
        }

        private void UpdateButtons()
        {
            var busy = _running != null;
            var state = ApplyState.TryLoad(_patchFolder);
            _check.Enabled = !busy;
            _apply.Enabled = !busy && _plan != null && _plan.Files.Any(file => file.Action != FileAction.Skip);
            _rollback.Enabled = !busy && state != null && !string.IsNullOrEmpty(state.BackupFolder) && state.Status != "rolled-back";
            _close.Text = busy ? "Cancel" : "Close";
            foreach (var box in _folders.Values) box.ReadOnly = busy;
        }

        private void OnClosing(object? sender, FormClosingEventArgs e)
        {
            if (_running == null) return;
            if (MessageBox.Show(this, "A step is still running. Cancel it? Progress is saved, so you can continue or roll back when you open the helper again.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                _running.Cancel();
            e.Cancel = true;
        }

        // ---------- Progress ----------

        private void ResetRows() => Invoke(new Action(() =>
        {
            foreach (var row in _rows) SetRow(row.Key, StepStatus.Pending, "");
            _log.Clear();
        }));

        private void SetRow(string key, StepStatus status, string? detail)
        {
            if (!_rows.TryGetValue(key, out var row))
            {
                AddStepRow(key, key == PatchEngine.StepRollback ? "Roll back" : key);
                row = _rows[key];
            }
            row.SubItems[0].Text = status == StepStatus.Done ? "✔" : status == StepStatus.Running ? "●" : status == StepStatus.Failed ? "✖" : status == StepStatus.Skipped ? "⤼" : "○";
            row.SubItems[2].Text = status == StepStatus.Done ? "Done" : status == StepStatus.Running ? "Running" : status == StepStatus.Failed ? "Failed" : status == StepStatus.Skipped ? "Skipped" : "Not started";
            if (detail != null) row.SubItems[3].Text = detail;
            row.ForeColor = status == StepStatus.Done ? Green : status == StepStatus.Running ? Blue : status == StepStatus.Failed ? Red : status == StepStatus.Skipped ? Muted : Muted;
            row.Font = new Font(Font, status == StepStatus.Running ? FontStyle.Bold : FontStyle.Regular);
            if (status == StepStatus.Running || status == StepStatus.Failed) row.EnsureVisible();
        }

        private void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss}  {message}";
            _log.AppendText(line + Environment.NewLine);
        }

        private void SetStatus(string text, Color color) { _status.Text = text; _status.ForeColor = color; }

        private string Report()
        {
            var text = new StringBuilder();
            text.AppendLine($"{_manifest.Title} — {_manifest.PatchName}");
            if (!string.IsNullOrWhiteSpace(_manifest.Description)) text.AppendLine(_manifest.Description);
            text.AppendLine($"Server: {Environment.MachineName} · {DateTime.Now:yyyy-MM-dd HH:mm} · by {Environment.UserName}");
            foreach (var folder in _folders) text.AppendLine($"{folder.Key}: {folder.Value.Text}");
            text.AppendLine();
            foreach (ListViewItem row in _steps.Items) text.AppendLine($"{row.SubItems[0].Text} {row.SubItems[1].Text}: {row.SubItems[2].Text}{(row.SubItems[3].Text.Length > 0 ? " — " + row.SubItems[3].Text : "")}");
            text.AppendLine();
            text.AppendLine(_status.Text);
            return text.ToString();
        }

        /// <summary>Forwards engine progress to the window (on the UI thread) and to the log file.</summary>
        private sealed class UiReporter : IHelperReporter
        {
            private readonly MainForm _form;
            public UiReporter(MainForm form) { _form = form; }

            public void Step(string key, StepStatus status, string? detail = null)
            {
                _form._fileLog.Step(key, status, detail);
                _form.BeginInvoke(new Action(() => _form.SetRow(key, status, detail)));
            }

            public void Log(string message)
            {
                _form._fileLog.Log(message);
                _form.BeginInvoke(new Action(() => _form.Log(message)));
            }

            public void Progress(int done, int total, string text) => _form.BeginInvoke(new Action(() =>
            {
                _form._progress.Maximum = Math.Max(1, total);
                _form._progress.Value = Math.Min(done, _form._progress.Maximum);
                _form._progressText.Text = $"{text}: {done:N0} of {total:N0}";
            }));
        }
    }
}
