using StartDown.Core;
using StartDown.Infrastructure;
using StartDown.Interop;
using System.Diagnostics;

namespace StartDown.UI;

internal sealed class ConfigurationForm : Form
{
    private readonly ConfigurationStore _store;
    private readonly AppLogger _logger;

    private AppConfiguration _configuration;
    private List<LaunchEntry> _entries = [];
    private LaunchEntry? _currentEntry;
    private bool _loading;
    private bool _dirty;
    private bool _selectionRestoreScheduled;

    private readonly ListView _entryList = new()
    {
        View = View.List,
        MultiSelect = false,
        HideSelection = false,
        FullRowSelect = true,
        UseCompatibleStateImageBehavior = false,
    };
    private readonly NumericUpDown _globalTimeout = NumberBox(1, int.MaxValue, 300);
    private readonly CheckBox _enabled = new() { Text = "启用此配置", AutoSize = true };
    private readonly TextBox _name = new();
    private readonly ComboBox _launchKind = ChoiceBox<LaunchKind>();
    private readonly TextBox _executable = new();
    private readonly TextBox _applicationUserModelId = new();
    private readonly TextBox _arguments = new();
    private readonly TextBox _workingDirectory = new();
    private readonly Button _browseExecutable = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _browseWorkingDirectory = new() { Text = "浏览…", AutoSize = true };
    private readonly ComboBox _processScope = ChoiceBox<ProcessMatchScope>();
    private readonly TextBox _matchPath = new();
    private readonly Button _browseMatchPath = new() { Text = "浏览…", AutoSize = true };
    private readonly ComboBox _existingPolicy = ChoiceBox<ExistingInstancePolicy>();
    private readonly ComboBox _titleMode = ChoiceBox<TitleMatchMode>();
    private readonly TextBox _titlePattern = new();
    private readonly TextBox _className = new();
    private readonly NumericUpDown _minWidth = OptionalNumberBox();
    private readonly NumericUpDown _maxWidth = OptionalNumberBox();
    private readonly NumericUpDown _minHeight = OptionalNumberBox();
    private readonly NumericUpDown _maxHeight = OptionalNumberBox();
    private readonly CheckBox _requireVisible = new() { Text = "必须可见且未被系统隐藏", AutoSize = true };
    private readonly CheckBox _requireTopLevel = new() { Text = "必须是顶层窗口", AutoSize = true };
    private readonly CheckBox _requireUnowned = new() { Text = "必须是无 owner 的主窗口", AutoSize = true };
    private readonly CheckBox _requireNotMinimized = new() { Text = "忽略已经最小化的窗口", AutoSize = true };
    private readonly ComboBox _action = ChoiceBox<WindowAction>();
    private readonly NumericUpDown _expectedMatches = NumberBox(1, int.MaxValue, 1);
    private readonly NumericUpDown _actionDelay = NumberBox(0, int.MaxValue, 250, increment: 50);
    private readonly NumericUpDown _entryTimeout = NumberBox(1, int.MaxValue, 60);
    private readonly CheckBox _autostart = new() { Text = "登录 Windows 后自动运行 StartDown", AutoSize = true };
    private readonly Button _removeButton = new() { Text = "删除", Dock = DockStyle.Fill };
    private readonly Button _duplicateButton = new() { Text = "复制", Dock = DockStyle.Fill };
    private readonly Button _testButton = new() { Text = "测试所选", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly List<Control> _entryOnlyControls = [];

    public ConfigurationForm(ConfigurationStore store, AppLogger logger)
    {
        _store = store;
        _logger = logger;
        _configuration = _store.Load();

        Text = "StartDown";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 680);
        Size = new Size(1120, 780);
        AutoScaleMode = AutoScaleMode.Dpi;

        PopulateChoices();
        BuildLayout();
        WireEvents();
        BindConfiguration();
    }

    private void PopulateChoices()
    {
        SetChoices(_launchKind,
            new Choice<LaunchKind>(LaunchKind.Executable, "可执行文件"),
            new Choice<LaunchKind>(LaunchKind.ApplicationUserModelId, "微软商店应用"));

        SetChoices(_processScope,
            new Choice<ProcessMatchScope>(ProcessMatchScope.ExactLaunchPath, "启动目标本身"),
            new Choice<ProcessMatchScope>(ProcessMatchScope.ExactPath, "指定可执行文件"),
            new Choice<ProcessMatchScope>(ProcessMatchScope.Directory, "指定目录下的程序"));

        SetChoices(_existingPolicy,
            new Choice<ExistingInstancePolicy>(ExistingInstancePolicy.Skip, "已运行则跳过"),
            new Choice<ExistingInstancePolicy>(ExistingInstancePolicy.Adopt, "接管已运行实例"));

        SetChoices(_titleMode,
            new Choice<TitleMatchMode>(TitleMatchMode.Any, "任意标题"),
            new Choice<TitleMatchMode>(TitleMatchMode.Contains, "包含"),
            new Choice<TitleMatchMode>(TitleMatchMode.Exact, "完全相同"),
            new Choice<TitleMatchMode>(TitleMatchMode.Regex, "正则表达式"));

        SetChoices(_action,
            new Choice<WindowAction>(WindowAction.Close, "关闭窗口"),
            new Choice<WindowAction>(WindowAction.Minimize, "最小化窗口"),
            new Choice<WindowAction>(WindowAction.Hide, "隐藏窗口"));
    }

    private void BuildLayout()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
        };
        Controls.Add(split);
        split.Panel1MinSize = 220;
        split.Panel2MinSize = 500;
        split.SplitterDistance = 260;

        var intro = new Label
        {
            Text = "StartDown 会先监听窗口，再依次启动这些程序。每项达到预期匹配次数后即完成；全部完成或总超时后退出。",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 64,
            Padding = new Padding(10),
        };
        split.Panel1.Controls.Add(intro);

        _entryList.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_entryList);
        _entryList.BringToFront();

        var leftButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            Padding = new Padding(6),
            ColumnCount = 1,
            RowCount = 4,
        };
        leftButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
        {
            leftButtons.RowStyles.Add(
                new RowStyle(SizeType.Percent, 25));
        }

        var addButton = new Button { Text = "添加", Dock = DockStyle.Fill };
        var importShortcutButton = new Button { Text = "导入", Dock = DockStyle.Fill };
        importShortcutButton.AccessibleDescription = "从 Windows 快捷方式导入";
        addButton.Click += (_, _) => AddEntry();
        importShortcutButton.Click += (_, _) => ImportShortcut();
        _duplicateButton.Click += (_, _) => DuplicateEntry();
        _removeButton.Click += (_, _) => RemoveEntry();
        leftButtons.Controls.AddRange([addButton, importShortcutButton, _duplicateButton, _removeButton]);
        split.Panel1.Controls.Add(leftButtons);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        split.Panel2.Controls.Add(scroll);

        var editor = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Dock = DockStyle.Top,
            Padding = new Padding(14),
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scroll.Controls.Add(editor);

        AddSection(editor, "全局");
        AddRow(editor, "总超时 (s)", _globalTimeout);

        AddSection(editor, "程序");
        AddWideRow(editor, _enabled);
        AddRow(editor, "名称", _name);
        AddRow(editor, "启动类型", _launchKind);
        _entryOnlyControls.Add(_browseExecutable);
        _browseExecutable.Click += (_, _) => BrowseExecutable();
        AddRow(editor, "启动程序", _executable, _browseExecutable);
        AddRow(editor, "应用 AUMID", _applicationUserModelId);
        AddRow(editor, "命令行参数", _arguments);
        _entryOnlyControls.Add(_browseWorkingDirectory);
        _browseWorkingDirectory.Click += (_, _) => BrowseWorkingDirectory();
        AddRow(editor, "工作目录", _workingDirectory, _browseWorkingDirectory);
        AddRow(editor, "窗口所属程序", _processScope);
        _browseMatchPath.Click += (_, _) => BrowseMatchPath();
        AddRow(editor, "匹配路径/目录", _matchPath, _browseMatchPath);
        AddRow(editor, "程序已在运行", _existingPolicy);

        AddSection(editor, "关闭条件 (全部条件同时满足)");
        var inspectWindow = new Button { Text = "从当前窗口读取标题、窗口类和程序路径…", AutoSize = true };
        _entryOnlyControls.Add(inspectWindow);
        inspectWindow.Click += (_, _) => InspectWindow();
        AddWideRow(editor, inspectWindow);
        AddRow(editor, "标题匹配", _titleMode);
        AddRow(editor, "标题内容", _titlePattern);
        AddRow(editor, "窗口类 (可选)", _className);
        AddRow(editor, "最小宽度 (-1 不限)", _minWidth);
        AddRow(editor, "最大宽度 (-1 不限)", _maxWidth);
        AddRow(editor, "最小高度 (-1 不限)", _minHeight);
        AddRow(editor, "最大高度 (-1 不限)", _maxHeight);
        AddWideRow(editor, _requireVisible);
        AddWideRow(editor, _requireTopLevel);
        AddWideRow(editor, _requireUnowned);
        AddWideRow(editor, _requireNotMinimized);

        AddSection(editor, "动作与完成条件");
        AddRow(editor, "动作", _action);
        AddRow(editor, "预期处理窗口数", _expectedMatches);
        AddRow(editor, "窗口出现后延迟 (ms)", _actionDelay);
        AddRow(editor, "此项超时 (s)", _entryTimeout);

        AddSection(editor, "运行");
        AddWideRow(editor, _autostart);
        var openStartupSettings = new Button { Text = "打开 Windows“启动应用”设置", AutoSize = true };
        openStartupSettings.Click += (_, _) => TryAction(AutostartManager.OpenWindowsStartupSettings);
        AddWideRow(editor, openStartupSettings);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 18, 0, 6),
        };
        var saveButton = new Button { Text = "保存", AutoSize = true };
        var runAllButton = new Button { Text = "运行全部", AutoSize = true };
        saveButton.Click += (_, _) => SaveConfiguration(showSuccess: true);
        _testButton.Click += (_, _) => TestSelected();
        runAllButton.Click += (_, _) => RunAll();
        actions.Controls.AddRange([saveButton, _testButton, runAllButton]);
        AddWideRow(editor, actions);

        _status.Text = $"配置文件：{_store.FilePath}";
        AddWideRow(editor, _status);
    }

    private void WireEvents()
    {
        _entryList.SelectedIndexChanged += (_, _) => ChangeSelection();
        _launchKind.SelectedIndexChanged += (_, _) =>
        {
            UpdateConditionalControls();
            MarkDirty();
        };
        _processScope.SelectedIndexChanged += (_, _) =>
        {
            UpdateConditionalControls();
            MarkDirty();
        };
        _titleMode.SelectedIndexChanged += (_, _) =>
        {
            UpdateConditionalControls();
            MarkDirty();
        };
        _name.TextChanged += (_, _) =>
        {
            if (!_loading && _currentEntry is not null)
            {
                _currentEntry.Name = _name.Text;
                var item = _entryList.Items[EntryKey(_currentEntry.Id)];
                if (item is not null)
                {
                    item.Text = DisplayName(_currentEntry.Name);
                }
            }
            MarkDirty();
        };

        foreach (var textBox in new[]
                 {
                     _executable, _applicationUserModelId, _arguments, _workingDirectory,
                     _matchPath, _titlePattern, _className,
                 })
        {
            textBox.TextChanged += (_, _) => MarkDirty();
        }

        foreach (var number in new[] { _globalTimeout, _minWidth, _maxWidth, _minHeight, _maxHeight, _expectedMatches, _actionDelay, _entryTimeout })
        {
            number.ValueChanged += (_, _) => MarkDirty();
        }

        foreach (var checkBox in new[] { _enabled, _requireVisible, _requireTopLevel, _requireUnowned, _requireNotMinimized })
        {
            checkBox.CheckedChanged += (_, _) => MarkDirty();
        }

        _existingPolicy.SelectedIndexChanged += (_, _) => MarkDirty();
        _action.SelectedIndexChanged += (_, _) => MarkDirty();
        _autostart.CheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                HandleAutostartChanged();
            }
        };

        FormClosing += OnFormClosing;
    }

    private void BindConfiguration(Guid? selectedId = null)
    {
        _loading = true;
        try
        {
            _entries = _configuration.Entries;
            _entryList.BeginUpdate();
            var index = -1;
            try
            {
                _entryList.Items.Clear();
                foreach (var entry in _entries)
                {
                    _entryList.Items.Add(CreateEntryItem(entry));
                }

                index = selectedId is null
                    ? (_entries.Count > 0 ? 0 : -1)
                    : _entries.FindIndex(entry => entry.Id == selectedId);
                _currentEntry = index >= 0 ? _entries[index] : null;
                if (index >= 0)
                {
                    _entryList.Items[index].Selected = true;
                    _entryList.Items[index].Focused = true;
                }
            }
            finally
            {
                _entryList.EndUpdate();
            }
            _globalTimeout.Value = Math.Max(_configuration.GlobalTimeoutSeconds, 1);
            _autostart.Checked = AutostartManager.IsEnabled(_store.FilePath);
            LoadEntry(_currentEntry);
        }
        finally
        {
            _loading = false;
            _dirty = false;
            UpdateButtons();
        }
    }

    private void ChangeSelection()
    {
        if (_loading)
        {
            return;
        }

        if (_entryList.SelectedItems.Count == 0)
        {
            ScheduleSelectionRestore();
            return;
        }

        var selectedEntry = _entryList.SelectedItems[0].Tag as LaunchEntry;
        if (selectedEntry?.Id == _currentEntry?.Id)
        {
            return;
        }

        ApplyCurrentEntry();
        _currentEntry = selectedEntry;
        LoadEntry(_currentEntry);
        UpdateButtons();
    }

    private void LoadEntry(LaunchEntry? entry)
    {
        var previousLoading = _loading;
        _loading = true;
        try
        {
            var enabled = entry is not null;
            SetEditorEnabled(enabled);
            if (entry is null)
            {
                return;
            }

            _enabled.Checked = entry.Enabled;
            _name.Text = entry.Name;
            SelectChoice(_launchKind, entry.LaunchKind);
            _executable.Text = entry.ExecutablePath;
            _applicationUserModelId.Text = entry.ApplicationUserModelId ?? string.Empty;
            _arguments.Text = entry.Arguments ?? string.Empty;
            _workingDirectory.Text = entry.WorkingDirectory ?? string.Empty;
            SelectChoice(_processScope, entry.ProcessMatchScope);
            _matchPath.Text = entry.MatchPath ?? string.Empty;
            SelectChoice(_existingPolicy, entry.ExistingInstancePolicy);
            SelectChoice(_titleMode, entry.WindowRule.TitleMatch);
            _titlePattern.Text = entry.WindowRule.TitlePattern ?? string.Empty;
            _className.Text = entry.WindowRule.ClassName ?? string.Empty;
            SetOptional(_minWidth, entry.WindowRule.MinWidth);
            SetOptional(_maxWidth, entry.WindowRule.MaxWidth);
            SetOptional(_minHeight, entry.WindowRule.MinHeight);
            SetOptional(_maxHeight, entry.WindowRule.MaxHeight);
            _requireVisible.Checked = entry.WindowRule.RequireVisible;
            _requireTopLevel.Checked = entry.WindowRule.RequireTopLevel;
            _requireUnowned.Checked = entry.WindowRule.RequireUnowned;
            _requireNotMinimized.Checked = entry.WindowRule.RequireNotMinimized;
            SelectChoice(_action, entry.Action);
            _expectedMatches.Value = Math.Max(entry.ExpectedMatches, 1);
            _actionDelay.Value = Math.Max(entry.ActionDelayMilliseconds, 0);
            _entryTimeout.Value = Math.Max(entry.TimeoutSeconds, 1);
            UpdateConditionalControls();
        }
        finally
        {
            _loading = previousLoading;
        }
    }

    private void ApplyCurrentEntry()
    {
        if (_loading || _currentEntry is null)
        {
            return;
        }

        _configuration.GlobalTimeoutSeconds = Decimal.ToInt32(_globalTimeout.Value);
        _currentEntry.Enabled = _enabled.Checked;
        _currentEntry.Name = _name.Text;
        _currentEntry.LaunchKind = SelectedChoice(_launchKind, LaunchKind.Executable);
        _currentEntry.ExecutablePath = _executable.Text;
        _currentEntry.ApplicationUserModelId = EmptyToNull(_applicationUserModelId.Text);
        _currentEntry.Arguments = EmptyToNull(_arguments.Text);
        _currentEntry.WorkingDirectory = EmptyToNull(_workingDirectory.Text);
        _currentEntry.ProcessMatchScope = SelectedChoice(_processScope, ProcessMatchScope.ExactLaunchPath);
        _currentEntry.MatchPath = EmptyToNull(_matchPath.Text);
        _currentEntry.ExistingInstancePolicy = SelectedChoice(_existingPolicy, ExistingInstancePolicy.Skip);
        _currentEntry.WindowRule.TitleMatch = SelectedChoice(_titleMode, TitleMatchMode.Any);
        _currentEntry.WindowRule.TitlePattern = EmptyToNull(_titlePattern.Text);
        _currentEntry.WindowRule.ClassName = EmptyToNull(_className.Text);
        _currentEntry.WindowRule.MinWidth = GetOptional(_minWidth);
        _currentEntry.WindowRule.MaxWidth = GetOptional(_maxWidth);
        _currentEntry.WindowRule.MinHeight = GetOptional(_minHeight);
        _currentEntry.WindowRule.MaxHeight = GetOptional(_maxHeight);
        _currentEntry.WindowRule.RequireVisible = _requireVisible.Checked;
        _currentEntry.WindowRule.RequireTopLevel = _requireTopLevel.Checked;
        _currentEntry.WindowRule.RequireUnowned = _requireUnowned.Checked;
        _currentEntry.WindowRule.RequireNotMinimized = _requireNotMinimized.Checked;
        _currentEntry.Action = SelectedChoice(_action, WindowAction.Close);
        _currentEntry.ExpectedMatches = Decimal.ToInt32(_expectedMatches.Value);
        _currentEntry.ActionDelayMilliseconds = Decimal.ToInt32(_actionDelay.Value);
        _currentEntry.TimeoutSeconds = Decimal.ToInt32(_entryTimeout.Value);
    }

    private bool SaveConfiguration(bool showSuccess)
    {
        ApplyCurrentEntry();
        _configuration.GlobalTimeoutSeconds = Decimal.ToInt32(_globalTimeout.Value);
        var selectedId = _currentEntry?.Id;

        try
        {
            _configuration = _store.Save(_configuration);
            BindConfiguration(selectedId);
            _status.Text = $"已保存：{_store.FilePath}";
            if (showSuccess)
            {
                MessageBox.Show(this, "配置已保存。", "StartDown", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }
        catch (ConfigurationValidationException exception)
        {
            MessageBox.Show(
                this,
                "请修正以下配置：\n\n" + exception.Message,
                "配置无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception exception)
        {
            _logger.Error($"保存配置失败：{exception.Message}");
            MessageBox.Show(this, exception.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void AddEntry()
    {
        ApplyCurrentEntry();
        var entry = new LaunchEntry { Name = $"程序 {_entries.Count + 1}" };
        _entries.Add(entry);
        var item = CreateEntryItem(entry);
        _entryList.Items.Add(item);
        SelectEntry(item);
        _dirty = true;
    }

    private void DuplicateEntry()
    {
        ApplyCurrentEntry();
        if (_currentEntry is null)
        {
            return;
        }

        var source = _currentEntry;
        var copy = new LaunchEntry
        {
            Name = source.Name + " (副本)",
            Enabled = source.Enabled,
            LaunchKind = source.LaunchKind,
            ExecutablePath = source.ExecutablePath,
            ApplicationUserModelId = source.ApplicationUserModelId,
            Arguments = source.Arguments,
            WorkingDirectory = source.WorkingDirectory,
            ProcessMatchScope = source.ProcessMatchScope,
            MatchPath = source.MatchPath,
            ExistingInstancePolicy = source.ExistingInstancePolicy,
            TimeoutSeconds = source.TimeoutSeconds,
            ExpectedMatches = source.ExpectedMatches,
            ActionDelayMilliseconds = source.ActionDelayMilliseconds,
            Action = source.Action,
            WindowRule = new WindowRule
            {
                TitleMatch = source.WindowRule.TitleMatch,
                TitlePattern = source.WindowRule.TitlePattern,
                ClassName = source.WindowRule.ClassName,
                MinWidth = source.WindowRule.MinWidth,
                MaxWidth = source.WindowRule.MaxWidth,
                MinHeight = source.WindowRule.MinHeight,
                MaxHeight = source.WindowRule.MaxHeight,
                RequireVisible = source.WindowRule.RequireVisible,
                RequireTopLevel = source.WindowRule.RequireTopLevel,
                RequireUnowned = source.WindowRule.RequireUnowned,
                RequireNotMinimized = source.WindowRule.RequireNotMinimized,
            }
        };
        _entries.Add(copy);
        var item = CreateEntryItem(copy);
        _entryList.Items.Add(item);
        SelectEntry(item);
        _dirty = true;
    }

    private void RemoveEntry()
    {
        if (_currentEntry is null || MessageBox.Show(
                this,
                $"删除“{_currentEntry.Name}”？",
                "StartDown",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var item = _entryList.Items[EntryKey(_currentEntry.Id)];
        var removedIndex = item?.Index ?? -1;
        var previousLoading = _loading;
        _loading = true;
        try
        {
            _entries.Remove(_currentEntry);
            item?.Remove();
            var nextIndex = _entryList.Items.Count == 0
                ? -1
                : Math.Min(Math.Max(removedIndex, 0), _entryList.Items.Count - 1);
            if (nextIndex >= 0)
            {
                SelectEntry(_entryList.Items[nextIndex]);
            }
            else
            {
                _currentEntry = null;
                LoadEntry(null);
            }
        }
        finally
        {
            _loading = previousLoading;
        }
        _dirty = true;
        UpdateButtons();
    }

    private void TestSelected()
    {
        var id = _currentEntry?.Id;
        if (id is null || !SaveConfiguration(showSuccess: false))
        {
            return;
        }

        LaunchRunner(["--run", "--entry", id.Value.ToString("D"), "--show-status"]);
    }

    private void RunAll()
    {
        if (!SaveConfiguration(showSuccess: false))
        {
            return;
        }

        if (!_configuration.Entries.Any(entry => entry.Enabled))
        {
            MessageBox.Show(this, "没有启用的配置。", "StartDown", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        LaunchRunner(["--run", "--show-status"]);
    }

    private void LaunchRunner(IEnumerable<string> arguments)
    {
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定 StartDown 可执行文件路径。");
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(_store.FilePath);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "运行失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportShortcut()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Windows 快捷方式 (*.lnk)|*.lnk",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ApplyCurrentEntry();
            var shortcut = ShortcutResolver.Resolve(dialog.FileName);
            var entry = new LaunchEntry
            {
                Name = Path.GetFileNameWithoutExtension(shortcut.ShortcutPath),
                Arguments = shortcut.Arguments,
                ProcessMatchScope = ProcessMatchScope.ExactLaunchPath,
            };

            if (shortcut.TargetKind == ShortcutTargetKind.Executable)
            {
                entry.LaunchKind = LaunchKind.Executable;
                entry.ExecutablePath = shortcut.ExecutablePath ?? string.Empty;
                entry.WorkingDirectory = shortcut.WorkingDirectory
                    ?? Path.GetDirectoryName(entry.ExecutablePath);
            }
            else
            {
                entry.LaunchKind = LaunchKind.ApplicationUserModelId;
                entry.ApplicationUserModelId = shortcut.AppUserModelId;
                entry.ActionDelayMilliseconds = 1_000;
            }

            _entries.Add(entry);
            var item = CreateEntryItem(entry);
            _entryList.Items.Add(item);
            SelectEntry(item);
            _dirty = true;
            _status.Text = shortcut.TargetKind == ShortcutTargetKind.Executable
                ? $"已从快捷方式导入：{entry.ExecutablePath}"
                : $"已从快捷方式导入打包应用：{entry.ApplicationUserModelId}。已将动作延迟设为 1 秒以避开启动画面；建议再读取主窗口补充标题或尺寸条件。";
        }
        catch (Exception exception)
        {
            _logger.Error($"导入快捷方式失败：{exception.Message}");
            MessageBox.Show(
                this,
                exception.Message,
                "无法导入快捷方式",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void BrowseExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SelectChoice(_launchKind, LaunchKind.Executable);
        _executable.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.StartsWith("程序 ", StringComparison.Ordinal))
        {
            _name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
        if (string.IsNullOrWhiteSpace(_workingDirectory.Text))
        {
            _workingDirectory.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        }
    }

    private void BrowseWorkingDirectory()
    {
        using var dialog = new FolderBrowserDialog { InitialDirectory = _workingDirectory.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workingDirectory.Text = dialog.SelectedPath;
        }
    }

    private void BrowseMatchPath()
    {
        var scope = SelectedChoice(_processScope, ProcessMatchScope.ExactLaunchPath);
        if (scope == ProcessMatchScope.Directory)
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = _matchPath.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _matchPath.Text = dialog.SelectedPath;
            }
            return;
        }

        using var fileDialog = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (fileDialog.ShowDialog(this) == DialogResult.OK)
        {
            _matchPath.Text = fileDialog.FileName;
        }
    }

    private void InspectWindow()
    {
        using var dialog = new WindowInspectorForm();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedSnapshot is not { } snapshot)
        {
            return;
        }

        var selectedLaunchKind = SelectedChoice(_launchKind, LaunchKind.Executable);
        if (!string.IsNullOrWhiteSpace(snapshot.ApplicationUserModelId) &&
            (selectedLaunchKind == LaunchKind.ApplicationUserModelId || string.IsNullOrWhiteSpace(_executable.Text)))
        {
            SelectChoice(_launchKind, LaunchKind.ApplicationUserModelId);
            _applicationUserModelId.Text = snapshot.ApplicationUserModelId;
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.StartsWith("程序 ", StringComparison.Ordinal))
            {
                _name.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "打包应用" : snapshot.Title;
            }
        }
        else if (string.IsNullOrWhiteSpace(_executable.Text) && !string.IsNullOrWhiteSpace(snapshot.ExecutablePath))
        {
            SelectChoice(_launchKind, LaunchKind.Executable);
            _executable.Text = snapshot.ExecutablePath;
            _workingDirectory.Text = Path.GetDirectoryName(snapshot.ExecutablePath) ?? string.Empty;
            _name.Text = Path.GetFileNameWithoutExtension(snapshot.ExecutablePath);
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.ExecutablePath) &&
                 !PathsEqual(_executable.Text, snapshot.ExecutablePath))
        {
            SelectChoice(_processScope, ProcessMatchScope.ExactPath);
            _matchPath.Text = snapshot.ExecutablePath;
        }

        SelectChoice(_titleMode, string.IsNullOrEmpty(snapshot.Title) ? TitleMatchMode.Any : TitleMatchMode.Exact);
        _titlePattern.Text = snapshot.Title;
        _className.Text = snapshot.ClassName;
        _status.Text = $"已读取窗口：{snapshot.Width} × {snapshot.Height} 像素" +
                       (string.IsNullOrWhiteSpace(snapshot.ApplicationUserModelId)
                           ? ""
                           : $"；AUMID：{snapshot.ApplicationUserModelId}") +
                       "；尺寸阈值仍由你决定。";
    }

    private void UpdateConditionalControls()
    {
        var hasEntry = _currentEntry is not null;
        var executableLaunch = SelectedChoice(_launchKind, LaunchKind.Executable) == LaunchKind.Executable;
        var scope = SelectedChoice(_processScope, ProcessMatchScope.ExactLaunchPath);
        _executable.Enabled = hasEntry && executableLaunch;
        _browseExecutable.Enabled = _executable.Enabled;
        _workingDirectory.Enabled = hasEntry && executableLaunch;
        _browseWorkingDirectory.Enabled = _workingDirectory.Enabled;
        _applicationUserModelId.Enabled = hasEntry && !executableLaunch;
        _matchPath.Enabled = hasEntry && scope != ProcessMatchScope.ExactLaunchPath;
        _browseMatchPath.Enabled = _matchPath.Enabled;
        _titlePattern.Enabled = hasEntry && SelectedChoice(_titleMode, TitleMatchMode.Any) != TitleMatchMode.Any;
    }

    private void SetEditorEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _enabled, _name, _launchKind, _executable, _applicationUserModelId,
            _arguments, _workingDirectory, _processScope,
            _matchPath, _browseMatchPath, _existingPolicy, _titleMode, _titlePattern,
            _className, _minWidth, _maxWidth, _minHeight, _maxHeight, _requireVisible,
            _requireTopLevel, _requireUnowned, _requireNotMinimized, _action,
            _expectedMatches, _actionDelay, _entryTimeout,
        })
        {
            control.Enabled = enabled;
        }

        foreach (var control in _entryOnlyControls)
        {
            control.Enabled = enabled;
        }
    }

    private void UpdateButtons()
    {
        var hasSelection = _currentEntry is not null;
        _removeButton.Enabled = hasSelection;
        _duplicateButton.Enabled = hasSelection;
        _testButton.Enabled = hasSelection;
    }

    private void SelectEntry(ListViewItem item)
    {
        var previousLoading = _loading;
        _loading = true;
        try
        {
            if (_entryList.SelectedItems.Count > 0 && _entryList.SelectedItems[0] != item)
            {
                _entryList.SelectedItems[0].Selected = false;
            }
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
            _currentEntry = item.Tag as LaunchEntry;
            LoadEntry(_currentEntry);
            UpdateButtons();
        }
        finally
        {
            _loading = previousLoading;
        }
    }

    private void ScheduleSelectionRestore()
    {
        if (_selectionRestoreScheduled || !IsHandleCreated || IsDisposed)
        {
            return;
        }

        _selectionRestoreScheduled = true;
        BeginInvoke(() =>
        {
            _selectionRestoreScheduled = false;
            if (_loading || IsDisposed || _entryList.SelectedItems.Count > 0)
            {
                return;
            }

            var currentItem = _currentEntry is null
                ? null
                : _entryList.Items[EntryKey(_currentEntry.Id)];
            if (currentItem is not null)
            {
                SelectEntry(currentItem);
                return;
            }

            _currentEntry = null;
            LoadEntry(null);
            UpdateButtons();
        });
    }

    private static ListViewItem CreateEntryItem(LaunchEntry entry) => new(DisplayName(entry.Name))
    {
        Name = EntryKey(entry.Id),
        Tag = entry,
    };

    private static string EntryKey(Guid id) => id.ToString("D");

    private static string DisplayName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? " (未命名)" : name;

    private void MarkDirty()
    {
        if (!_loading)
        {
            _dirty = true;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!_dirty)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "配置尚未保存。现在保存吗？",
            "StartDown",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (result == DialogResult.Cancel || result == DialogResult.Yes && !SaveConfiguration(showSuccess: false))
        {
            eventArgs.Cancel = true;
        }
    }

    private void TryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "StartDown", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleAutostartChanged()
    {
        var enable = _autostart.Checked;
        if (enable && (_dirty || !File.Exists(_store.FilePath)) && !SaveConfiguration(showSuccess: false))
        {
            SetAutostartChecked(false);
            return;
        }

        try
        {
            AutostartManager.SetEnabled(enable, _store.FilePath);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "StartDown", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAutostartChecked(AutostartManager.IsEnabled(_store.FilePath));
        }
    }

    private void SetAutostartChecked(bool value)
    {
        _loading = true;
        try
        {
            _autostart.Checked = value;
        }
        finally
        {
            _loading = false;
        }
    }

    private static void AddSection(TableLayoutPanel table, string title)
    {
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 18, 0, 8),
        };
        AddWideRow(table, label);
    }

    private static void AddRow(TableLayoutPanel table, string labelText, Control editor, Control? trailing = null)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = labelText,
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(0, 7, 10, 7),
        };
        editor.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        editor.Margin = new Padding(0, 4, 8, 4);
        table.Controls.Add(label, 0, row);
        table.Controls.Add(editor, 1, row);
        if (trailing is not null)
        {
            trailing.Anchor = AnchorStyles.Left;
            table.Controls.Add(trailing, 2, row);
        }
    }

    private static void AddWideRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 5, 0, 5);
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 3);
    }

    private static NumericUpDown NumberBox(int minimum, int maximum, int value, int increment = 1) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Increment = increment,
        Width = 130,
        ThousandsSeparator = true,
    };

    private static NumericUpDown OptionalNumberBox() => NumberBox(-1, int.MaxValue, -1, 10);

    private static ComboBox ChoiceBox<T>() where T : struct, Enum => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        IntegralHeight = false,
    };

    private static void SetChoices<T>(ComboBox comboBox, params Choice<T>[] choices) where T : struct, Enum
    {
        comboBox.Items.Clear();
        comboBox.Items.AddRange(choices);
        if (choices.Length > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static T SelectedChoice<T>(ComboBox comboBox, T fallback) where T : struct, Enum =>
        comboBox.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private static void SelectChoice<T>(ComboBox comboBox, T value) where T : struct, Enum
    {
        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }
        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private static int? GetOptional(NumericUpDown number) => number.Value < 0 ? null : Decimal.ToInt32(number.Value);

    private static void SetOptional(NumericUpDown number, int? value) =>
        number.Value = value is null ? -1 : Math.Clamp(value.Value, 0, Decimal.ToInt32(number.Maximum));

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record Choice<T>(T Value, string Text) where T : struct, Enum
    {
        public override string ToString() => Text;
    }
}
