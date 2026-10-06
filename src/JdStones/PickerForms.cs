using System.Drawing;
using System.Windows.Forms;

namespace JdStones;

/// <summary>
/// Поверх окна игры показывается его «замороженный» снимок 1:1. Пользователь выделяет
/// прямоугольник или кликает точку — получаем координаты относительно окна игры.
/// </summary>
internal sealed class RegionPickerForm : Form
{
    private readonly Bitmap _shot;
    private readonly bool _pickPoint;
    private readonly string _hint;
    private Point? _start;
    private Rectangle _current;

    public Rectangle SelectedRegion { get; private set; }
    public Point SelectedPoint { get; private set; }

    public RegionPickerForm(Bitmap clientShot, Rectangle clientOnScreen, bool pickPoint, string hint)
    {
        _shot = clientShot;
        _pickPoint = pickPoint;
        _hint = hint + "   (Esc — отмена)";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = clientOnScreen;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _start = e.Location;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_start is { } s && !_pickPoint)
        {
            _current = Normalize(s, e.Location);
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _start is not { } s) return;
        _start = null;
        if (_pickPoint)
        {
            SelectedPoint = e.Location;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        var rect = Normalize(s, e.Location);
        if (rect.Width < 5 || rect.Height < 5)
        {
            _current = Rectangle.Empty;
            Invalidate();
            return;
        }
        SelectedRegion = rect;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_shot, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(70, Color.Black))) g.FillRectangle(dim, ClientRectangle);
        if (_current.Width > 0)
        {
            g.DrawImage(_shot, _current, _current, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.Red, 2);
            g.DrawRectangle(pen, _current);
        }
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        var size = g.MeasureString(_hint, font);
        g.FillRectangle(Brushes.Black, 8, 8, size.Width + 12, size.Height + 8);
        g.DrawString(_hint, font, Brushes.Yellow, 14, 12);
    }

    private static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}

internal sealed class WindowPickerForm : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public GameWindow? Selected => _list.SelectedItem as GameWindow;

    public WindowPickerForm()
    {
        Text = "Выберите окно игры";
        Size = new Size(560, 380);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;

        var ok = new Button { Text = "Выбрать", DialogResult = DialogResult.OK, AutoSize = true };
        var refresh = new Button { Text = "Обновить список", AutoSize = true };
        refresh.Click += (_, _) => Fill();
        _list.DoubleClick += (_, _) => { if (Selected != null) { DialogResult = DialogResult.OK; Close(); } };

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bottom.Controls.AddRange([ok, refresh]);
        var hint = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 36, Padding = new Padding(6),
            Text = "Клиент Jade Dynasty обычно называется elementclient.exe. Игра должна быть запущена." };
        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(hint);
        AcceptButton = ok;
        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        var windows = GameWindow.EnumerateVisible()
            .OrderByDescending(w => string.Equals(w.ProcessName, GameWindow.DefaultProcess, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var w in windows) _list.Items.Add(w);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }
}

/// <summary>Показывает, что именно видит программа: снимок области и распознанные статы.</summary>
internal sealed class OcrTestForm : Form
{
    public OcrTestForm(Bitmap image, string report)
    {
        Text = "Проверка распознавания";
        Size = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        var picture = new PictureBox { Dock = DockStyle.Top, Height = 220, Image = image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        var text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10), Text = report.Replace("\n", Environment.NewLine), WordWrap = false,
        };
        Controls.Add(text);
        Controls.Add(picture);
        FormClosed += (_, _) => image.Dispose();
    }
}
