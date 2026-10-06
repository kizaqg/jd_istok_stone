using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace JdStones;

/// <summary>
/// Снимает область окна игры. Основной способ — копия пикселей с экрана: игру никак не трогает.
/// Если область чем-то перекрыта (или с экрана пришла чёрная картинка) — просим окно
/// нарисовать себя (PrintWindow). Этот способ работает «сквозь» другие окна, но заставляет
/// DirectX-игру перерисоваться, из-за чего она моргает, поэтому он только запасной.
/// </summary>
internal static class WindowCapture
{
    public static Bitmap CaptureClient(GameWindow window)
    {
        var client = window.ClientScreenRect();
        return Capture(window, new Rectangle(0, 0, client.Width, client.Height));
    }

    /// <param name="region">Область в координатах клиентской части окна.</param>
    public static Bitmap Capture(GameWindow window, Rectangle region)
    {
        if (!window.IsAlive) throw new InvalidOperationException("Окно игры закрыто.");
        if (Native.IsIconic(window.Handle)) throw new InvalidOperationException("Окно игры свернуто.");

        var client = window.ClientScreenRect();
        region.Intersect(new Rectangle(0, 0, client.Width, client.Height));
        if (region.Width <= 0 || region.Height <= 0)
            throw new InvalidOperationException("Выбранная область за пределами окна игры. Выберите её заново.");

        var onScreen = new Rectangle(client.X + region.X, client.Y + region.Y, region.Width, region.Height);
        var fromScreen = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(fromScreen))
        {
            g.CopyFromScreen(onScreen.X, onScreen.Y, 0, 0, region.Size);
        }
        if (IsGameOnTop(window, onScreen) && !IsBlank(fromScreen)) return fromScreen;

        var printed = TryPrintWindow(window, client, region);
        if (printed != null && !IsBlank(printed))
        {
            fromScreen.Dispose();
            return printed;
        }
        printed?.Dispose();
        return fromScreen;
    }

    /// <summary>Видна ли область на экране целиком (углы и центр принадлежат окну игры).</summary>
    private static bool IsGameOnTop(GameWindow window, Rectangle r)
    {
        Point[] probes =
        [
            new(r.Left + 1, r.Top + 1), new(r.Right - 2, r.Top + 1), new(r.Left + 1, r.Bottom - 2),
            new(r.Right - 2, r.Bottom - 2), new(r.Left + r.Width / 2, r.Top + r.Height / 2),
        ];
        foreach (var p in probes)
        {
            var hit = Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y });
            if (Native.GetAncestor(hit, Native.GA_ROOT) != window.Handle) return false;
        }
        return true;
    }

    private static Bitmap? TryPrintWindow(GameWindow window, Rectangle client, Rectangle region)
    {
        if (!Native.GetWindowRect(window.Handle, out var wr)) return null;
        var width = wr.Right - wr.Left;
        var height = wr.Bottom - wr.Top;
        if (width <= 0 || height <= 0) return null;

        using var full = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        bool ok;
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try
            {
                ok = Native.PrintWindow(window.Handle, hdc, Native.PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        if (!ok) return null;

        var crop = new Rectangle(client.X - wr.Left + region.X, client.Y - wr.Top + region.Y, region.Width, region.Height);
        crop.Intersect(new Rectangle(0, 0, width, height));
        if (crop.Width != region.Width || crop.Height != region.Height) return null;
        return full.Clone(crop, PixelFormat.Format32bppRgb);
    }

    /// <summary>Картинка целиком (почти) чёрная — значит, окно не отрисовалось.</summary>
    private static bool IsBlank(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var i = 0; i < bytes.Length; i += 4)
            {
                if (bytes[i] > 24 || bytes[i + 1] > 24 || bytes[i + 2] > 24) return false;
            }
            return true;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
