using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace JdStones;

/// <summary>
/// Снимает изображение окна игры напрямую (PrintWindow), даже если его перекрывают другие окна.
/// Если окно так не рисуется (бывает в полноэкранном режиме) — берёт пиксели с экрана в месте окна.
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

        var shot = TryPrintWindow(window, client, region);
        if (shot != null && !IsBlank(shot)) return shot;
        shot?.Dispose();

        var fromScreen = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(fromScreen))
        {
            g.CopyFromScreen(client.X + region.X, client.Y + region.Y, 0, 0, region.Size);
        }
        return fromScreen;
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
