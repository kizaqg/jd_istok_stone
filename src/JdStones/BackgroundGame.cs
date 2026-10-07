using System.Drawing;
using System.Runtime.InteropServices;

namespace JdStones;

/// <summary>
/// Фоновый режим для одного окна игры: съёмка через Windows Graphics Capture, которая сама
/// пересоздаётся при сбоях, и работа со свёрнутой игрой — свёрнутая игра не рисуется, поэтому
/// её окно разворачивается без активации и уводится за край экрана, а после остановки возвращается.
/// </summary>
internal sealed class BackgroundGame(GameWindow window, Action<string> log) : IDisposable
{
    private const int OffscreenX = -20000;
    private const int OffscreenY = -20000;

    private readonly object _lock = new();
    private WgcGameCapture? _capture;
    private Native.WINDOWPLACEMENT? _savedPlacement;

    public GameWindow Window => window;

    public bool IsHidden => _savedPlacement != null;

    public Bitmap Capture(Rectangle clientRegion)
    {
        lock (_lock)
        {
            KeepRenderable();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _capture ??= new WgcGameCapture(window);
                    var bmp = _capture.Capture(clientRegion);
                    if (_capture.SizeChanged)
                    {
                        _capture.Dispose();
                        _capture = null;
                    }
                    return bmp;
                }
                catch (Exception ex) when (attempt == 0 && ex is not InvalidOperationException)
                {
                    // Съёмка сломалась (сворачивание, смена потока и т.п.) — создаём заново и повторяем.
                    _capture?.Dispose();
                    _capture = null;
                }
            }
        }
    }

    public void Click(Point client) => InputSender.BackgroundClick(window, client);

    /// <summary>Сколько миллисекунд картинка игры не обновлялась (-1 — неизвестно).</summary>
    public long FrameAgeMs
    {
        get
        {
            lock (_lock) return _capture?.FrameAgeMs ?? -1;
        }
    }

    /// <summary>Настоящий курсор сейчас над видимой частью окна игры (не над окном поверх неё).</summary>
    public bool IsCursorOverGame()
    {
        if (!window.IsAlive || IsHidden || !Native.GetCursorPos(out var p)) return false;
        var hit = Native.WindowFromPoint(p);
        return hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GA_ROOT) == window.Handle;
    }

    /// <summary>Вернуть окно игры на прежнее место, если программа уводила его за экран.</summary>
    public void RestorePosition()
    {
        lock (_lock)
        {
            if (_savedPlacement is not { } placement || !window.IsAlive) return;
            placement.showCmd = Native.SW_SHOWNOACTIVATE;
            Native.SetWindowPlacement(window.Handle, ref placement);
            _savedPlacement = null;
            log("Окно игры возвращено на место.");
        }
    }

    private void KeepRenderable()
    {
        if (!window.IsAlive) return;

        if (Native.IsIconic(window.Handle))
        {
            var placement = Native.WINDOWPLACEMENT.Create();
            if (!Native.GetWindowPlacement(window.Handle, ref placement)) return;
            _savedPlacement ??= placement;

            // Разворачиваем сразу за краем экрана и без активации — на экране ничего не мелькает.
            var normal = placement.rcNormalPosition;
            var width = normal.Right - normal.Left;
            var height = normal.Bottom - normal.Top;
            placement.rcNormalPosition = new Native.RECT
            {
                Left = OffscreenX, Top = OffscreenY, Right = OffscreenX + width, Bottom = OffscreenY + height,
            };
            placement.showCmd = Native.SW_SHOWNOACTIVATE;
            placement.flags = 0;
            Native.SetWindowPlacement(window.Handle, ref placement);
            log("Игра свёрнута — убрал её за край экрана, чтобы крутить дальше. " +
                "Вернётся сама после остановки, или кликните по ней на панели задач.");
            Thread.Sleep(400); // даём игре отрисовать кадр
            return;
        }

        // Пользователь кликнул по игре на панели задач — она стала активной: возвращаем её на экран.
        if (_savedPlacement != null && Native.GetForegroundWindow() == window.Handle)
            RestorePosition();
    }

    public void Dispose()
    {
        RestorePosition();
        lock (_lock)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }
}
