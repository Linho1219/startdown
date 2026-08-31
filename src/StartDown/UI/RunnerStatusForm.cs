using StartDown.Infrastructure;
using StartDown.Runner;
using System.ComponentModel;

namespace StartDown.UI;

internal sealed class RunnerStatusForm : Form
{
    private readonly StartupRunner _runner;
    private readonly AppLogger _logger;
    private readonly Action _requestExit;
    private readonly BindingList<StatusRow> _rows = [];
    private readonly Label _summary = new() { AutoSize = true };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Value = 0 };
    private readonly DataGridView _grid = new();
    private readonly TextBox _log = new();
    private readonly Button _closeButton = new() { Text = "取消", AutoSize = true };

    public RunnerStatusForm(StartupRunner runner, AppLogger logger, Action requestExit)
    {
        _runner = runner;
        _logger = logger;
        _requestExit = requestExit;

        if (ApplicationIconProvider.CreateIcon() is { } applicationIcon)
        {
            Icon = applicationIcon;
        }
        Text = "StartDown — 测试运行";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(920, 620);
        MinimumSize = new Size(720, 480);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        _runner.StatusChanged += OnStatusChanged;
        _runner.Completed += OnCompleted;
        _logger.EntryWritten += OnLogEntry;
        ApplyStatus(_runner.CurrentStatus);
    }

    private void BuildLayout()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 66,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 2,
        };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _summary.Font = new Font(SystemFonts.MessageBoxFont ?? Font, FontStyle.Bold);
        _progress.Dock = DockStyle.Fill;
        header.Controls.Add(_summary, 0, 0);
        header.Controls.Add(_progress, 0, 1);
        Controls.Add(header);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoGenerateColumns = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "配置",
            DataPropertyName = nameof(StatusRow.Name),
            Width = 150,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "状态",
            DataPropertyName = nameof(StatusRow.State),
            Width = 120,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "匹配",
            DataPropertyName = nameof(StatusRow.Matches),
            Width = 70,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "剩余",
            DataPropertyName = nameof(StatusRow.Remaining),
            Width = 70,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "详情",
            DataPropertyName = nameof(StatusRow.Detail),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220,
        });
        _grid.DataSource = _rows;

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.Font = new Font(FontFamily.GenericMonospace, 9f);

        var content = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        content.Panel1.Controls.Add(_grid);
        content.Panel2.Controls.Add(_log);
        Controls.Add(content);
        content.Panel1MinSize = 150;
        content.Panel2MinSize = 100;
        content.SplitterDistance = 280;
        content.BringToFront();

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft,
        };
        _closeButton.Click += (_, _) => _requestExit();
        footer.Controls.Add(_closeButton);
        Controls.Add(footer);
    }

    private void OnStatusChanged(object? sender, RunnerStatusChangedEventArgs eventArgs) =>
        ApplyStatus(eventArgs.Status);

    private void OnCompleted(object? sender, RunnerCompletedEventArgs eventArgs)
    {
        ApplyStatus(eventArgs.Status);
        _closeButton.Text = "关闭";
    }

    private void ApplyStatus(RunnerStatus status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ApplyStatus(status));
            return;
        }

        var terminal = status.Entries.Count(entry => entry.IsTerminal);
        var total = status.Entries.Count;
        _summary.Text = $"{PhaseText(status.Phase)} — {terminal}/{total} 项完成" +
                        (status.GlobalRemaining is { } remaining ? $"，总超时剩余 {Math.Ceiling(remaining.TotalSeconds)} 秒" : string.Empty);
        _progress.Value = total == 0 ? 100 : Math.Clamp((int)Math.Round(terminal * 100d / total), 0, 100);

        _rows.RaiseListChangedEvents = false;
        _rows.Clear();
        foreach (var entry in status.Entries)
        {
            _rows.Add(new StatusRow(
                entry.Name,
                EntryStateText(entry.State),
                $"{entry.MatchedWindows}/{entry.ExpectedMatches}",
                entry.Remaining is { } entryRemaining ? $"{Math.Ceiling(entryRemaining.TotalSeconds)}s" : string.Empty,
                entry.Detail ?? string.Empty));
        }
        _rows.RaiseListChangedEvents = true;
        _rows.ResetBindings();
    }

    private void OnLogEntry(object? sender, LogEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => OnLogEntry(sender, entry));
            return;
        }

        _log.AppendText(entry + Environment.NewLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runner.StatusChanged -= OnStatusChanged;
            _runner.Completed -= OnCompleted;
            _logger.EntryWritten -= OnLogEntry;
        }
        base.Dispose(disposing);
    }

    private static string PhaseText(RunnerPhase phase) => phase switch
    {
        RunnerPhase.Created => "准备中",
        RunnerPhase.Starting => "正在启动",
        RunnerPhase.HooksReady => "监听已就绪",
        RunnerPhase.Reconciling => "检查已有进程",
        RunnerPhase.Launching => "正在启动程序",
        RunnerPhase.Monitoring => "正在等待窗口",
        RunnerPhase.Completed => "已完成",
        RunnerPhase.Faulted => "运行失败",
        RunnerPhase.Cancelled => "已取消",
        _ => phase.ToString(),
    };

    private static string EntryStateText(EntryRunState state) => state switch
    {
        EntryRunState.Pending => "等待",
        EntryRunState.Disabled => "未启用",
        EntryRunState.Launching => "正在启动",
        EntryRunState.Watching => "等待窗口",
        EntryRunState.Succeeded => "已处理",
        EntryRunState.SkippedAlreadyRunning => "已运行，跳过",
        EntryRunState.LaunchFailed => "启动失败",
        EntryRunState.TimedOut => "超时",
        EntryRunState.GlobalTimedOut => "总超时",
        EntryRunState.InvalidConfiguration => "配置无效",
        EntryRunState.Aborted => "已中止",
        EntryRunState.Cancelled => "已取消",
        _ => state.ToString(),
    };

    private sealed record StatusRow(string Name, string State, string Matches, string Remaining, string Detail);
}
