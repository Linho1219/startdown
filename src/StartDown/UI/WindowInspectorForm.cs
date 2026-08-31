using StartDown.Core;
using StartDown.Interop;
using System.ComponentModel;

namespace StartDown.UI;

internal sealed class WindowInspectorForm : Form
{
    private readonly WindowSnapshotProvider _snapshotProvider = new();
    private readonly BindingList<WindowRow> _rows = [];
    private readonly DataGridView _grid = new();

    public WindowInspectorForm()
    {
        Text = "选择窗口 — StartDown";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 560);
        MinimumSize = new Size(760, 420);
        AutoScaleMode = AutoScaleMode.Dpi;

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "程序",
            DataPropertyName = nameof(WindowRow.Process),
            Width = 150,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "标题",
            DataPropertyName = nameof(WindowRow.Title),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 180,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "窗口类",
            DataPropertyName = nameof(WindowRow.ClassName),
            Width = 180,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "尺寸",
            DataPropertyName = nameof(WindowRow.Size),
            Width = 100,
        });
        _grid.DataSource = _rows;
        _grid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0)
            {
                AcceptSelection();
            }
        };
        Controls.Add(_grid);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft,
        };
        var select = new Button { Text = "选择", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var refresh = new Button { Text = "刷新", AutoSize = true };
        select.Click += (_, _) => AcceptSelection();
        refresh.Click += (_, _) => RefreshWindows();
        footer.Controls.AddRange([select, cancel, refresh]);
        Controls.Add(footer);

        AcceptButton = select;
        CancelButton = cancel;
        Shown += (_, _) => RefreshWindows();
    }

    public WindowSnapshot? SelectedSnapshot { get; private set; }

    private void RefreshWindows()
    {
        _rows.RaiseListChangedEvents = false;
        _rows.Clear();
        var ownProcessId = Environment.ProcessId;

        foreach (var hwnd in WindowEventSource.EnumerateTopLevelWindows())
        {
            var snapshot = _snapshotProvider.Capture(hwnd);
            if (snapshot is null ||
                snapshot.ProcessId == ownProcessId ||
                !snapshot.IsVisible ||
                snapshot.IsCloaked ||
                snapshot.IsMinimized ||
                string.IsNullOrWhiteSpace(snapshot.Title))
            {
                continue;
            }

            _rows.Add(new WindowRow(snapshot));
        }

        _rows.RaiseListChangedEvents = true;
        _rows.ResetBindings();
        if (_grid.Rows.Count > 0)
        {
            _grid.Rows[0].Selected = true;
        }
    }

    private void AcceptSelection()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WindowRow row)
        {
            return;
        }

        SelectedSnapshot = row.Snapshot;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed class WindowRow
    {
        public WindowRow(WindowSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public WindowSnapshot Snapshot { get; }
        public string Process => Path.GetFileName(Snapshot.ExecutablePath) ?? string.Empty;
        public string Title => Snapshot.Title;
        public string ClassName => Snapshot.ClassName;
        public string Size => $"{Snapshot.Width} × {Snapshot.Height}";
    }
}
