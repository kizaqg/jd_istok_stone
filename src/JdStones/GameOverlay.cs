using System.Drawing;
using System.Windows.Forms;

namespace JdStones;

/// <summary>
/// Маленькая панель поверх окна игры: старт/стоп, выбор набора фильтров, счётчик попыток.
/// Окно игры назначается её владельцем, поэтому панель держится над игрой, но не над
/// другими программами, и вместе с игрой уходит на задний план. Панель можно перетаскивать.
/// </summary>
internal sealed class GameOverlay : Form
{
    private static readonly Color Back = Color.FromArgb(18, 32, 48);
    private static readonly Color Accent = Color.FromArgb(70, 190, 230);

    private readonly Func<GameWindow?> _window;
    private readonly System.Windows.Forms.Timer _follow = new() { Interval = 250 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Gainsboro, Text = "Остановлено" };
    private readonly Button _run = OverlayButton("▶ Старт (F5)");
    private readonly ComboBox _presets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, FlatStyle = FlatStyle.Flat };
    private IntPtr _owner;
    private Point? _dragFrom;

    /// <summary>Смещение панели относительно левого верхнего угла окна игры (null — правый верхний угол).</summary>
    public Point? Offset { get; private set; }

    /// <summary>Временно спрятать (например, пока выбирается область на снимке игры).</summary>
    public bool Suspended { get; set; }

    public event Action? RunClicked;
    public event Action? ShowMainClicked;
    public event Action<string>? PresetChosen;
    public event Action? OffsetChanged;

    public GameOverlay(Func<GameWindow?> window, Point? offset)
    {
        _window = window;
        Offset = offset;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Back;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9);
        Opacity = 0.93;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(8, 6, 8, 8);

        var title = new Label { Text = "Камни истока", AutoSize = true, ForeColor = Accent, Font = new Font(Font, FontStyle.Bold), Cursor = Cursors.SizeAll };
        var main = OverlayButton("⚙");
        main.Width = 28;
        main.AutoSize = false;
        main.Height = _run.Height;
        main.Click += (_, _) => ShowMainClicked?.Invoke();
        _run.Click += (_, _) => RunClicked?.Invoke();
        _presets.SelectionChangeCommitted += (_, _) => { if (_presets.SelectedItem is string s) PresetChosen?.Invoke(s); };
        _presets.MouseWheel += (_, e) => { if (e is HandledMouseEventArgs h && !_presets.DroppedDown) h.Handled = true; };

        var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Cursor = Cursors.SizeAll };
        top.Controls.AddRange([title, _status]);
        _status.Margin = new Padding(8, 0, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        buttons.Controls.AddRange([_run, main]);
        var presetRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        presetRow.Controls.AddRange([new Label { Text = "Набор:", AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(0, 4, 4, 0) }, _presets]);
        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Cursor = Cursors.SizeAll };
        layout.Controls.AddRange([top, buttons, presetRow]);
        Controls.Add(layout);

        // Перетаскивание за любое «пустое» место панели.
        foreach (Control c in new Control[] { this, layout, top, title, _status, buttons, presetRow })
        {
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _dragFrom = Cursor.Position; };
            c.MouseMove += (_, _) => Drag();
            c.MouseUp += (_, _) => { if (_dragFrom != null) { _dragFrom = null; OffsetChanged?.Invoke(); } };
        }

        _follow.Tick += (_, _) => Follow();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public void Start() => _follow.Start();

    public void SetRunning(bool running)
    {
        _run.Text = running ? "■ Стоп (F6)" : "▶ Старт (F5)";
        _run.BackColor = running ? Color.FromArgb(150, 50, 50) : Color.FromArgb(30, 90, 120);
        if (!running) _status.Text = "Остановлено";
    }

    public void SetStatus(string text) => _status.Text = text;

    public void SetPresets(IEnumerable<string> names)
    {
        var selected = _presets.SelectedItem as string;
        _presets.Items.Clear();
        foreach (var n in names) _presets.Items.Add(n);
        if (selected != null && _presets.Items.Contains(selected)) _presets.SelectedItem = selected;
    }

    public void SelectPreset(string? name)
    {
        if (name != null && _presets.Items.Contains(name)) _presets.SelectedItem = name;
    }

    private void Drag()
    {
        if (_dragFrom is not { } from || _window() is not { IsAlive: true } w) return;
        var now = Cursor.Position;
        Location = new Point(Location.X + now.X - from.X, Location.Y + now.Y - from.Y);
        _dragFrom = now;
        var client = w.ClientScreenRect();
        Offset = new Point(Location.X - client.X, Location.Y - client.Y);
    }

    private void Follow()
    {
        if (_dragFrom != null) return;
        var w = _window();
        if (Suspended || w is not { IsAlive: true } || Native.IsIconic(w.Handle) || !Native.IsWindowVisible(w.Handle))
        {
            if (Visible) Hide();
            return;
        }

        if (_owner != w.Handle)
        {
            // Окно игры — владелец панели: панель всегда над игрой, но не над другими программами.
            Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, w.Handle);
            _owner = w.Handle;
        }

        var client = w.ClientScreenRect();
        var offset = Offset ?? new Point(client.Width - Width - 12, 48);
        var target = new Point(client.X + offset.X, client.Y + offset.Y);
        if (Location != target) Location = target;
        if (!Visible) Show();
    }

    private static Button OverlayButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(30, 90, 120),
        ForeColor = Color.White,
        Margin = new Padding(0, 0, 4, 0),
        FlatAppearance = { BorderColor = Color.FromArgb(70, 190, 230) },
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _follow.Dispose();
        base.Dispose(disposing);
    }
}
