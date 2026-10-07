using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Text;
using System.Windows.Forms;
using JdStones.Core;

namespace JdStones;

internal sealed class MainForm : Form
{
    private const int HotkeyStart = 1;
    private const int HotkeyStop = 2;
    private const uint VkF5 = 0x74;
    private const uint VkF6 = 0x75;
    private const string LuckyMessage = "АТЛИЧНАЯ БАЛЕНСА ЩАС ЧО";

    private static readonly string[] UnitTexts = ["любое", "в %", "числом"];
    private static readonly string[] OperatorTexts = ["≥", "≤", "="];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly OcrService? _ocr = OcrService.TryCreate();
    private readonly List<ConditionGroup> _groups;

    private GameWindow? _window;
    private Rectangle? _statsRegion;
    private Point? _rerollClick;
    private Rectangle? _warningRegion;
    private Point? _warningClick;
    private CancellationTokenSource? _cts;
    private BackgroundGame? _bg;
    private GameOverlay? _overlay;

    private readonly CheckBox _backgroundMode = new()
    {
        Text = "Фоновый режим (игра может быть под другими окнами, мышь не трогается)", AutoSize = true, Anchor = AnchorStyles.Left,
    };
    private readonly CheckBox _overlayEnabled = new() { Text = "Панель поверх игры", AutoSize = true, Anchor = AnchorStyles.Left };

    private readonly Label _windowLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _statsLabel = StatusLabel();
    private readonly Label _rerollLabel = StatusLabel();
    private readonly Label _warningRegionLabel = StatusLabel();
    private readonly Label _warningClickLabel = StatusLabel();
    private readonly NumericUpDown _delay = new() { Minimum = 500, Maximum = 30000, Increment = 100, Width = 80 };
    private readonly NumericUpDown _maxAttempts = new() { Minimum = 0, Maximum = 1_000_000, Width = 80 };
    private readonly FlowLayoutPanel _groupsPanel = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8),
    };
    private readonly ComboBox _presetBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 3, 4, 0) };
    private readonly Button _startButton = new() { Text = "Запустить (F5)", AutoSize = true };
    private readonly Button _stopButton = new() { Text = "Остановить (F6)", AutoSize = true, Enabled = false };
    private readonly Label _statusLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "Остановлено" };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9),
    };

    public MainForm()
    {
        Text = "Камни истока";
        Font = new Font("Segoe UI", 9);
        Size = new Size(860, 760);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;

        _groups = TemplateSerializer.FromDto(_settings.Groups);
        _statsRegion = AppSettings.ToRect(_settings.StatsRegion);
        _rerollClick = AppSettings.ToPoint(_settings.RerollClick);
        _warningRegion = AppSettings.ToRect(_settings.WarningRegion);
        _warningClick = AppSettings.ToPoint(_settings.WarningClick);
        _delay.Value = Math.Clamp(_settings.ClickIntervalMs, (int)_delay.Minimum, (int)_delay.Maximum);
        _maxAttempts.Value = Math.Clamp(_settings.MaxAttempts, 0, (int)_maxAttempts.Maximum);
        _window = GameWindow.Find(_settings.WindowProcess, _settings.WindowTitle);
        _backgroundMode.Checked = _settings.BackgroundMode;
        _overlayEnabled.Checked = _settings.OverlayEnabled;

        BuildLayout();
        RefreshSetupLabels();
        RebuildGroups();
        _backgroundMode.CheckedChanged += (_, _) =>
        {
            ResetBackgroundCapture();
            SaveSettings();
            Log(_backgroundMode.Checked
                ? "Фоновый режим включён: окно игры можно закрыть другими окнами (но не сворачивать)."
                : "Фоновый режим выключен: игра должна быть видна, клики двигают курсор.");
        };
        _overlayEnabled.CheckedChanged += (_, _) => { UpdateOverlay(); SaveSettings(); };
        UpdateOverlay();

        Log(_ocr == null
            ? "ВНИМАНИЕ: нет русского OCR в Windows. Нажмите «Проверить распознавание» для инструкции."
            : "Готово. Настройте окно, области и фильтры, затем F5.");
    }

    private static Label StatusLabel() => new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.DimGray };

    // ---------- Разметка ----------

    private void BuildLayout()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("Файл");
        file.DropDownItems.Add("Сохранить шаблон…", null, (_, _) => SaveTemplate());
        file.DropDownItems.Add("Загрузить шаблон…", null, (_, _) => LoadTemplate());
        menu.Items.Add(file);
        MainMenuStrip = menu;

        var setup = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(8, 4, 8, 4) };
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        setup.Controls.Add(MakeButton("Окно игры…", PickWindow), 0, 0);
        setup.Controls.Add(_windowLabel, 1, 0);
        setup.SetColumnSpan(_windowLabel, 3);

        setup.Controls.Add(MakeButton("Область статов", PickStatsRegion), 0, 1);
        setup.Controls.Add(_statsLabel, 1, 1);
        setup.Controls.Add(MakeButton("Кнопка перековки", PickRerollClick), 2, 1);
        setup.Controls.Add(_rerollLabel, 3, 1);

        setup.Controls.Add(MakeButton("Область предупреждения", PickWarningRegion), 0, 2);
        setup.Controls.Add(_warningRegionLabel, 1, 2);
        setup.Controls.Add(MakeButton("Кнопка подтверждения", PickWarningClick), 2, 2);
        setup.Controls.Add(_warningClickLabel, 3, 2);

        setup.Controls.Add(new Label { Text = "Интервал между кликами, мс:", AutoSize = true, Anchor = AnchorStyles.Right }, 0, 3);
        setup.Controls.Add(_delay, 1, 3);
        setup.Controls.Add(new Label { Text = "Лимит попыток (0 — без лимита):", AutoSize = true, Anchor = AnchorStyles.Right }, 2, 3);
        setup.Controls.Add(_maxAttempts, 3, 3);

        var test = MakeButton("Проверить распознавание", TestRecognition);
        setup.Controls.Add(test, 0, 4);
        setup.Controls.Add(MakeButton("Сбросить предупреждение", () =>
        {
            _warningRegion = null;
            _warningClick = null;
            AfterPick();
        }), 2, 4);
        setup.Controls.Add(new Label
        {
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.DimGray,
            Text = "необязательно: обе настройки или ни одной",
        }, 3, 4);

        setup.Controls.Add(_backgroundMode, 0, 5);
        setup.SetColumnSpan(_backgroundMode, 2);
        setup.Controls.Add(MakeButton("Проверить фоновый клик", TestBackgroundClick), 2, 5);
        setup.Controls.Add(_overlayEnabled, 3, 5);

        var filtersHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 4, 8, 0) };
        filtersHeader.Controls.Add(new Label { Text = "Фильтры для поиска", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 12, 0) });
        filtersHeader.Controls.Add(MakeButton("+ Группа (ИЛИ)", () =>
        {
            _groups.Add(new ConditionGroup { Conditions = [new StatCondition()] });
            RebuildGroups();
        }));
        filtersHeader.Controls.Add(new Label { Text = "Набор:", AutoSize = true, Margin = new Padding(24, 6, 4, 0) });
        _presetBox.SelectionChangeCommitted += (_, _) => LoadPreset();
        NoWheel(_presetBox);
        filtersHeader.Controls.Add(_presetBox);
        filtersHeader.Controls.Add(MakeButton("Сохранить набор…", SavePreset));
        filtersHeader.Controls.Add(MakeButton("Удалить набор", DeletePreset));
        RefreshPresets();

        _startButton.Click += (_, _) => StartRolling();
        _stopButton.Click += (_, _) => StopRolling();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        buttons.Controls.AddRange([_startButton, _stopButton, _statusLabel]);
        _statusLabel.Margin = new Padding(12, 8, 0, 0);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 190, Padding = new Padding(8) };
        bottom.Controls.Add(_log);
        bottom.Controls.Add(buttons);

        Controls.Add(_groupsPanel);
        Controls.Add(bottom);
        Controls.Add(filtersHeader);
        Controls.Add(setup);
        Controls.Add(menu);
    }

    private static Button MakeButton(string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        b.Click += (_, _) => action();
        return b;
    }

    private void RefreshSetupLabels()
    {
        _windowLabel.Text = _window is { IsAlive: true } ? _window.ToString() : "окно не выбрано (запустите игру и нажмите «Окно игры…»)";
        _windowLabel.ForeColor = _window is { IsAlive: true } ? Color.DarkGreen : Color.Firebrick;
        _statsLabel.Text = Describe(_statsRegion);
        _rerollLabel.Text = Describe(_rerollClick);
        _warningRegionLabel.Text = Describe(_warningRegion);
        _warningClickLabel.Text = Describe(_warningClick);
    }

    private static string Describe(Rectangle? r) => r is { } v ? $"x={v.X} y={v.Y}, {v.Width}×{v.Height}" : "не задана";
    private static string Describe(Point? p) => p is { } v ? $"x={v.X} y={v.Y}" : "не задана";

    // ---------- Фильтры ----------

    private void RebuildGroups()
    {
        _groupsPanel.SuspendLayout();
        foreach (var c in _groupsPanel.Controls.Cast<Control>().ToList()) c.Dispose();
        _groupsPanel.Controls.Clear();

        if (_groups.Count == 0)
            _groupsPanel.Controls.Add(new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Фильтров нет. Нажмите «+ Группа (ИЛИ)»." });

        for (var i = 0; i < _groups.Count; i++)
        {
            if (i > 0)
                _groupsPanel.Controls.Add(new Label { Text = "ИЛИ", AutoSize = true, Font = new Font(Font.FontFamily, 11, FontStyle.Bold), Margin = new Padding(12, 2, 0, 2) });
            _groupsPanel.Controls.Add(BuildGroup(_groups[i], i));
        }
        _groupsPanel.ResumeLayout();
    }

    private Control BuildGroup(ConditionGroup group, int index)
    {
        var box = new GroupBox
        {
            Text = $"Группа {index + 1}: должны выполниться все условия (И)",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6),
        };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var (text, width) in new[] { ("Стат", 186), ("Значение", 96), ("", 56), ("Строк", 66) })
            header.Controls.Add(new Label { Text = text, Width = width, ForeColor = Color.DimGray });
        flow.Controls.Add(header);

        foreach (var condition in group.Conditions) flow.Controls.Add(BuildConditionRow(group, condition));

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var add = new Button { Text = "+ условие", AutoSize = true };
        add.Click += (_, _) => { group.Conditions.Add(new StatCondition()); BeginInvoke(RebuildGroups); };
        var delete = new Button { Text = "Удалить группу", AutoSize = true };
        delete.Click += (_, _) => { _groups.Remove(group); BeginInvoke(RebuildGroups); };
        actions.Controls.AddRange([add, delete]);
        flow.Controls.Add(actions);

        box.Controls.Add(flow);
        return box;
    }

    private Control BuildConditionRow(ConditionGroup group, StatCondition condition)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };

        // Можно выбрать из списка или вписать свой вариант прямо в поле.
        var stat = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown, Width = 180, MaxDropDownItems = 20,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        stat.Items.AddRange(StatCatalog.Predefined.Cast<object>().ToArray());
        stat.Text = condition.Stat;
        stat.TextChanged += (_, _) => condition.Stat = stat.Text.Trim();

        var unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        unit.Items.AddRange(UnitTexts);
        unit.SelectedIndex = (int)condition.Unit;
        unit.SelectedIndexChanged += (_, _) => condition.Unit = (StatUnit)unit.SelectedIndex;

        var op = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 50 };
        op.Items.AddRange(OperatorTexts);
        op.SelectedIndex = (int)condition.Operator;
        op.SelectedIndexChanged += (_, _) => condition.Operator = (CompareOp)op.SelectedIndex;

        var count = new NumericUpDown { Minimum = 0, Maximum = 10, Width = 60, Value = Math.Clamp(condition.Count, 0, 10) };
        count.ValueChanged += (_, _) => condition.Count = (int)count.Value;

        var delete = new Button { Text = "✕", Width = 30, Height = stat.Height + 2 };
        delete.Click += (_, _) => { group.Conditions.Remove(condition); BeginInvoke(RebuildGroups); };

        foreach (Control c in new Control[] { stat, unit, op, count }) NoWheel(c);
        row.Controls.AddRange([stat, unit, op, count, delete]);
        return row;
    }

    /// <summary>
    /// Колёсико мыши над списком/числом не меняет значение (раньше статы случайно
    /// перелистывались при прокрутке) — вместо этого прокручивается список фильтров.
    /// </summary>
    private void NoWheel(Control control)
    {
        control.MouseWheel += (_, e) =>
        {
            if (e is HandledMouseEventArgs h) h.Handled = true;
            if (control is ComboBox { DroppedDown: true }) return;
            var y = -_groupsPanel.AutoScrollPosition.Y - e.Delta;
            _groupsPanel.AutoScrollPosition = new Point(0, Math.Max(0, y));
        };
    }

    // ---------- Наборы фильтров ----------

    private void RefreshPresets(string? select = null)
    {
        _presetBox.Items.Clear();
        foreach (var name in _settings.Presets.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            _presetBox.Items.Add(name);
        if (select != null) _presetBox.SelectedItem = select;
        _overlay?.SetPresets(_presetBox.Items.Cast<string>());
        _overlay?.SelectPreset(select);
    }

    private void LoadPreset()
    {
        if (_cts != null || _presetBox.SelectedItem is not string name || !_settings.Presets.TryGetValue(name, out var dto)) return;
        _groups.Clear();
        _groups.AddRange(TemplateSerializer.FromDto(dto));
        RebuildGroups();
        SaveSettings();
        Log($"Загружен набор «{name}».");
    }

    private void SavePreset()
    {
        CommitPendingEdits();
        if (!_groups.Any(g => g.Conditions.Count > 0))
        {
            MessageBox.Show(this, "Нет фильтров для сохранения.", "Набор", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var name = PromptForm.Ask(this, "Сохранить набор", "Название набора:", _presetBox.SelectedItem as string ?? "")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (_settings.Presets.ContainsKey(name) &&
            MessageBox.Show(this, $"Набор «{name}» уже есть. Заменить?", "Набор", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _settings.Presets[name] = TemplateSerializer.ToDto(_groups);
        SaveSettings();
        RefreshPresets(name);
        Log($"Набор «{name}» сохранён.");
    }

    private void DeletePreset()
    {
        if (_presetBox.SelectedItem is not string name) return;
        if (MessageBox.Show(this, $"Удалить набор «{name}»?", "Набор", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _settings.Presets.Remove(name);
        SaveSettings();
        RefreshPresets();
        Log($"Набор «{name}» удалён.");
    }

    // ---------- Выбор окна, областей и точек ----------

    private GameWindow? RequireWindow()
    {
        if (_window is { IsAlive: true }) return _window;
        _window = GameWindow.Find(_settings.WindowProcess, _settings.WindowTitle);
        RefreshSetupLabels();
        if (_window != null) return _window;
        MessageBox.Show(this, "Окно игры не найдено. Запустите игру и выберите окно кнопкой «Окно игры…».",
            "Нет окна игры", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return null;
    }

    private void PickWindow()
    {
        using var picker = new WindowPickerForm();
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
        _window = picker.Selected;
        SaveSettings();
        RefreshSetupLabels();
    }

    private void PickStatsRegion()
    {
        if (PickOnGame(false, "Выделите мышью строки статов: названия и значения (колонку «Текущие»)") is { } r)
            _statsRegion = r.Region;
        AfterPick();
    }

    private void PickRerollClick()
    {
        if (PickOnGame(true, "Кликните по кнопке перековки камня") is { } r) _rerollClick = r.Point;
        AfterPick();
    }

    private void PickWarningRegion()
    {
        if (PickOnGame(false, "Выделите текст окна «Такие камни весьма редки…»") is { } r) _warningRegion = r.Region;
        AfterPick();
    }

    private void PickWarningClick()
    {
        if (PickOnGame(true, "Кликните по кнопке подтверждения в окне предупреждения") is { } r) _warningClick = r.Point;
        AfterPick();
    }

    private void AfterPick()
    {
        SaveSettings();
        RefreshSetupLabels();
    }

    private (Rectangle Region, Point Point)? PickOnGame(bool pickPoint, string hint)
    {
        if (_cts != null || RequireWindow() is not { } window) return null;

        Hide();
        if (_overlay != null)
        {
            _overlay.Suspended = true;
            _overlay.Hide();
        }
        try
        {
            Application.DoEvents();
            Thread.Sleep(200);
            using var shot = CaptureGame(window, new Rectangle(Point.Empty, window.ClientScreenRect().Size));
            using var picker = new RegionPickerForm(shot, window.ClientScreenRect(), pickPoint, hint);
            if (picker.ShowDialog() != DialogResult.OK) return null;
            return (picker.SelectedRegion, picker.SelectedPoint);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        finally
        {
            if (_overlay != null) _overlay.Suspended = false;
            Show();
            Activate();
        }
    }

    // ---------- Проверка распознавания ----------

    private async void TestRecognition()
    {
        if (_ocr == null)
        {
            MessageBox.Show(this, OcrService.MissingLanguageHelp, "Нет русского OCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (RequireWindow() is not { } window) return;
        if (_statsRegion is not { } region)
        {
            MessageBox.Show(this, "Сначала выберите область статов.", "Нет области", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        CommitPendingEdits();
        try
        {
            var shot = CaptureGame(window, region);
            var rows = StatParser.GroupIntoRows(await _ocr.RecognizeAsync(shot));
            var conditions = _groups.SelectMany(g => g.Conditions).ToList();
            var matcher = new StatMatcher(StatCatalog.Predefined.Concat(conditions.Select(c => c.Stat)));
            var lines = StatParser.Parse(rows, matcher);

            var report = new StringBuilder();
            report.AppendLine("Распознанный текст:");
            foreach (var row in rows) report.AppendLine("  " + row);
            report.AppendLine().AppendLine($"Найдено статов: {lines.Count}");
            foreach (var line in lines) report.AppendLine($"  {line.Stat,-18} {line.Value:0.###}{(line.IsPercent ? "%" : "")}   ({(line.IsPercent ? "в %" : "числом")})");
            report.AppendLine().AppendLine($"Фильтры сейчас: {(ConditionEvaluator.Matches(_groups, lines) ? "ПОДХОДИТ" : "не подходит")}");
            foreach (var c in conditions.Where(c => c.Stat.Length > 0))
                report.AppendLine($"  {DescribeCondition(c)}: найдено {ConditionEvaluator.CountMatching(c, lines)}");

            if (_warningRegion is { } warnRegion)
            {
                using var warnShot = CaptureGame(window, warnRegion);
                var warnText = await _ocr.RecognizeTextAsync(warnShot);
                report.AppendLine().AppendLine("Область предупреждения: " +
                    (WarningDetector.IsRareStoneWarning(warnText) ? "ПРЕДУПРЕЖДЕНИЕ ВИДНО" : "предупреждения нет"));
                report.AppendLine("  " + warnText.Replace("\n", " / "));
            }

            using var form = new OcrTestForm(shot, report.ToString());
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- Запуск / остановка ----------

    /// <summary>
    /// Число, введённое с клавиатуры в поле «Строк», попадает в Value только после выхода из поля.
    /// Если сразу нажать F5 — старое значение. Чтение Value принудительно применяет введённое.
    /// </summary>
    private void CommitPendingEdits()
    {
        void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is NumericUpDown n) _ = n.Value;
                Walk(c);
            }
        }
        Walk(_groupsPanel);
    }

    private RollerConfig? BuildConfig()
    {
        CommitPendingEdits();
        string? error = null;
        if (_ocr == null) error = OcrService.MissingLanguageHelp;
        else if (_statsRegion == null) error = "Выберите область статов.";
        else if (_rerollClick == null) error = "Выберите кнопку перековки.";
        else if ((_warningRegion == null) != (_warningClick == null))
            error = "Для предупреждения о редком камне задайте и область, и кнопку подтверждения (или нажмите «Сбросить предупреждение»).";
        else if (!_groups.Any(g => g.Conditions.Count > 0)) error = "Добавьте хотя бы один фильтр.";
        else if (_groups.SelectMany(g => g.Conditions).Any(c => string.IsNullOrWhiteSpace(c.Stat)))
            error = "В одном из условий не указан стат.";
        else if (_groups.SelectMany(g => g.Conditions).FirstOrDefault(ConditionEvaluator.IsAlwaysTrue) is { } always)
            error = $"Условие «{always.Stat} ≥ 0» выполняется на любом камне. Укажите количество строк от 1.";

        if (error != null)
        {
            MessageBox.Show(this, error, "Нельзя запустить", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        var loose = _groups.Select((g, i) => (g, i)).Where(x => ConditionEvaluator.MatchesWithoutStats(x.g)).ToList();
        if (loose.Count > 0)
        {
            var text = string.Join("\n", loose.Select(x => $"Группа {x.i + 1}: {DescribeGroup(x.g)}"));
            var answer = MessageBox.Show(this,
                "Эта группа сработает на камне, где этих статов нет вообще:\n\n" + text +
                "\n\n«≤» и «=» означают «не больше» и «ровно». Чтобы искать камень С нужными статами, " +
                "используйте «≥» и количество от 1.\n\nВсё равно запустить?",
                "Проверьте фильтры", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return null;
        }
        if (RequireWindow() is not { } window) return null;

        Func<Rectangle, Bitmap> capture;
        Action<Point> click;
        ConfirmMethod[] confirmMethods;
        if (_backgroundMode.Checked)
        {
            BackgroundGame bg;
            try
            {
                bg = BackgroundCapture(window);
                bg.Capture(new Rectangle(0, 0, 1, 1)).Dispose(); // проверяем, что съёмка запускается
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Фоновая съёмка окна не запустилась:\n" + ex.Message +
                    "\n\nВыключите «Фоновый режим», чтобы работать как раньше.", "Фоновый режим",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            capture = bg.Capture;
            click = bg.Click;
            confirmMethods =
            [
                new("фоновый клик", click),
                new("фоновый клик с наведением", p => InputSender.BackgroundHoverClick(window, p)),
                new("клавиша Enter", _ => InputSender.BackgroundEnter(window)),
                new("фоновый клик с активацией окна", p =>
                {
                    InputSender.PretendActive(window);
                    InputSender.BackgroundHoverClick(window, p);
                }),
                new("Enter с активацией окна", _ =>
                {
                    InputSender.PretendActive(window);
                    InputSender.BackgroundEnter(window);
                }),
                new("клик с переключением на игру", p => InputSender.ForegroundClickAndRestore(window, p)),
            ];
        }
        else
        {
            capture = r => WindowCapture.Capture(window, r);
            click = p => InputSender.LeftClick(window.ToScreen(p));
            confirmMethods = [new("клик", click)];
        }

        return new RollerConfig
        {
            Capture = capture,
            Click = click,
            StatsRegion = _statsRegion!.Value,
            RerollClick = _rerollClick!.Value,
            WarningRegion = _warningRegion,
            WarningClick = _warningClick,
            ConfirmMethods = confirmMethods,
            Background = _backgroundMode.Checked,
            IsCursorOverGame = _backgroundMode.Checked ? _bg!.IsCursorOverGame : null,
            PreferredConfirm = _backgroundMode.Checked ? _settings.ConfirmMethod : null,
            IntervalMs = (int)_delay.Value,
            MaxAttempts = (int)_maxAttempts.Value,
            Groups = _groups.Select(g => g.Clone()).ToList(),
        };
    }

    private async void StartRolling()
    {
        if (_cts != null) return;
        if (BuildConfig() is not { } cfg) return;
        SaveSettings();

        _cts = new CancellationTokenSource();
        SetRunning(true);
        Log("Запуск. Остановить — F6.");
        var roller = new StoneRoller(_ocr!, msg => SafeInvoke(() => Log(msg)),
            n => SafeInvoke(() => _overlay?.SetStatus($"Попытка {n}")));
        var token = _cts.Token;
        try
        {
            var result = await Task.Run(() => roller.RunAsync(cfg, token));
            if (_backgroundMode.Checked && roller.WorkingConfirmName is { } confirmName && confirmName != _settings.ConfirmMethod)
            {
                _settings.ConfirmMethod = confirmName;
                _settings.Save();
            }
            switch (result.Outcome)
            {
                case RollOutcome.Found:
                    var group = cfg.Groups[result.MatchedGroup];
                    var why = string.Join("\n", group.Conditions.Select(c =>
                        $"{DescribeCondition(c)}: найдено {ConditionEvaluator.CountMatching(c, result.LastStats)}"));
                    Log($"НАЙДЕНО за {result.Attempts} попыток (группа {result.MatchedGroup + 1}): {string.Join(" | ", result.LastStats)}");
                    SystemSounds.Exclamation.Play();
                    _overlay?.SetRunning(false);
                    _overlay?.SetStatus($"НАЙДЕНО ({result.Attempts})");
                    _bg?.RestorePosition();
                    ShowMain();
                    MessageBox.Show(this,
                        $"{LuckyMessage}\n\nПопыток: {result.Attempts}\n{string.Join("\n", result.LastStats)}" +
                        $"\n\nСработала группа {result.MatchedGroup + 1}:\n{why}" +
                        $"\n\nКак прочитан текст:\n{string.Join("\n", result.LastStats.Select(l => l.RawText))}",
                        "Камень найден", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case RollOutcome.LimitReached:
                    Log($"Достигнут лимит попыток ({result.Attempts}).");
                    SystemSounds.Asterisk.Play();
                    break;
                default:
                    Log($"Остановлено. Попыток: {result.Attempts}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _bg?.RestorePosition();
            SetRunning(false);
        }
    }

    private void StopRolling() => _cts?.Cancel();

    private static string DescribeCondition(StatCondition c) =>
        $"{c.Stat} ({UnitTexts[(int)c.Unit]}) {OperatorTexts[(int)c.Operator]} {c.Count}";

    private static string DescribeGroup(ConditionGroup g) => string.Join(" И ", g.Conditions.Select(DescribeCondition));

    private void SetRunning(bool running)
    {
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _statusLabel.Text = running ? "Работает…" : "Остановлено";
        _statusLabel.ForeColor = running ? Color.DarkGreen : SystemColors.ControlText;
        _backgroundMode.Enabled = !running;
        _overlay?.SetRunning(running);
    }

    private void ShowMain()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    // ---------- Фоновый режим ----------

    private Bitmap CaptureGame(GameWindow window, Rectangle region) =>
        _backgroundMode.Checked ? BackgroundCapture(window).Capture(region) : WindowCapture.Capture(window, region);

    /// <summary>Фоновая работа с окном игры (создаётся один раз на окно).</summary>
    private BackgroundGame BackgroundCapture(GameWindow window)
    {
        if (_bg != null && _bg.Window.Handle == window.Handle) return _bg;
        ResetBackgroundCapture();
        _bg = new BackgroundGame(window, msg => SafeInvoke(() => Log(msg)));
        return _bg;
    }

    private void ResetBackgroundCapture()
    {
        _bg?.Dispose();
        _bg = null;
    }

    /// <summary>
    /// Одна перековка фоновым кликом: курсор не двигается, игра не активируется.
    /// Если после этого статы поменялись — игра принимает фоновые клики.
    /// </summary>
    private async void TestBackgroundClick()
    {
        if (_ocr == null)
        {
            MessageBox.Show(this, OcrService.MissingLanguageHelp, "Нет русского OCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_cts != null || RequireWindow() is not { } window) return;
        if (_statsRegion is not { } region || _rerollClick is not { } point)
        {
            MessageBox.Show(this, "Сначала выберите область статов и кнопку перековки.", "Проверка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this,
                "Программа сделает ОДНУ перековку фоновым кликом: курсор не сдвинется, игра не станет активной.\n" +
                "Затем проверит, обновились ли статы.\n\nОкно игры не сворачивайте. Продолжить?",
                "Проверка фонового клика", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            var wgc = BackgroundCapture(window);
            if (Native.IsIconic(window.Handle))
            {
                MessageBox.Show(this, "Разверните окно игры для проверки.", "Проверка фонового клика", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var matcher = new StatMatcher(StatCatalog.Predefined);
            async Task<string> ReadAsync()
            {
                using var shot = wgc.Capture(region);
                var lines = StatParser.Parse(StatParser.GroupIntoRows(await _ocr.RecognizeAsync(shot)), matcher);
                return string.Join(" | ", lines);
            }

            var before = await ReadAsync();
            if (before.Length == 0)
            {
                MessageBox.Show(this, "Фоновая съёмка не видит статы. Откройте окно камня в игре и проверьте область статов.",
                    "Проверка фонового клика", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Log("Проверка фонового клика: статы до клика — " + before);
            await Task.Run(() => InputSender.BackgroundClick(window, point));

            var after = before;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000 && (after == before || after.Length == 0))
            {
                await Task.Delay(200);
                after = await ReadAsync();
            }

            if (after != before && after.Length > 0)
            {
                Log("Фоновый клик работает. Статы после клика — " + after);
                MessageBox.Show(this, "Фоновый клик РАБОТАЕТ — статы обновились.\n\nВключите галочку «Фоновый режим».",
                    "Проверка фонового клика", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Log("Фоновый клик: статы не изменились за 6 с.");
                MessageBox.Show(this,
                    "Статы не изменились за 6 секунд — игра, похоже, не принимает фоновые клики.\n\n" +
                    "Если в игре появилось окно «Такие камни весьма редки» — закройте его и повторите проверку.\n" +
                    "Иначе оставьте «Фоновый режим» выключенным и напишите разработчику.",
                    "Проверка фонового клика", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- Панель поверх игры ----------

    private void UpdateOverlay()
    {
        if (!_overlayEnabled.Checked)
        {
            _overlay?.Dispose();
            _overlay = null;
            return;
        }
        if (_overlay != null) return;

        _overlay = new GameOverlay(() => _window, AppSettings.ToPoint(_settings.OverlayOffset));
        _overlay.RunClicked += () =>
        {
            if (_cts != null) StopRolling();
            else StartRolling();
        };
        _overlay.ShowMainClicked += ShowMain;
        _overlay.PresetChosen += name =>
        {
            _presetBox.SelectedItem = name;
            LoadPreset();
        };
        _overlay.OffsetChanged += () =>
        {
            _settings.OverlayOffset = AppSettings.FromPoint(_overlay?.Offset);
            _settings.Save();
        };
        _overlay.SetPresets(_settings.Presets.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        _overlay.SelectPreset(_presetBox.SelectedItem as string);
        _overlay.SetRunning(_cts != null);
        _overlay.Start();
    }

    // ---------- Горячие клавиши F5 / F6 (работают и когда активна игра) ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var ok = Native.RegisterHotKey(Handle, HotkeyStart, Native.MOD_NOREPEAT, VkF5)
               & Native.RegisterHotKey(Handle, HotkeyStop, Native.MOD_NOREPEAT, VkF6);
        if (!ok) Log("Не удалось занять F5/F6 (их использует другая программа). Работают кнопки в окне.");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotkeyStart);
        Native.UnregisterHotKey(Handle, HotkeyStop);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            if ((int)m.WParam == HotkeyStart) StartRolling();
            else if ((int)m.WParam == HotkeyStop) StopRolling();
        }
        base.WndProc(ref m);
    }

    // ---------- Шаблоны и настройки ----------

    private void SaveTemplate()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON файлы|*.json", DefaultExt = "json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, TemplateSerializer.Serialize(_groups));
            Log("Шаблон сохранён: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить шаблон:\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadTemplate()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON файлы|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var groups = TemplateSerializer.Deserialize(File.ReadAllText(dialog.FileName));
            _groups.Clear();
            _groups.AddRange(groups);
            RebuildGroups();
            SaveSettings();
            Log("Шаблон загружен: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось загрузить шаблон:\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveSettings()
    {
        if (_window != null)
        {
            _settings.WindowProcess = _window.ProcessName;
            _settings.WindowTitle = _window.Title;
        }
        _settings.StatsRegion = AppSettings.FromRect(_statsRegion);
        _settings.RerollClick = AppSettings.FromPoint(_rerollClick);
        _settings.WarningRegion = AppSettings.FromRect(_warningRegion);
        _settings.WarningClick = AppSettings.FromPoint(_warningClick);
        _settings.ClickIntervalMs = (int)_delay.Value;
        _settings.MaxAttempts = (int)_maxAttempts.Value;
        _settings.Groups = TemplateSerializer.ToDto(_groups);
        _settings.BackgroundMode = _backgroundMode.Checked;
        _settings.OverlayEnabled = _overlayEnabled.Checked;
        _settings.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        SaveSettings();
        _overlay?.Dispose();
        ResetBackgroundCapture();
        base.OnFormClosing(e);
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Окно закрывается.
        }
    }

    private void Log(string message)
    {
        if (_log.TextLength > 200_000) _log.Text = _log.Text[^100_000..];
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
