using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace JdStones;

/// <summary>Окно игры. Все области и точки хранятся относительно его клиентской части.</summary>
internal sealed record GameWindow(IntPtr Handle, string Title, string ProcessName)
{
    /// <summary>Процесс клиента Jade Dynasty (движок Perfect World).</summary>
    public const string DefaultProcess = "elementclient";

    public bool IsAlive => Native.IsWindow(Handle);

    public override string ToString() =>
        string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}   [{ProcessName}.exe]";

    /// <summary>Положение и размер клиентской области окна на экране (в физических пикселях).</summary>
    public Rectangle ClientScreenRect()
    {
        if (!Native.GetClientRect(Handle, out var rc)) throw new InvalidOperationException("Окно игры закрыто.");
        var origin = new Native.POINT();
        Native.ClientToScreen(Handle, ref origin);
        return new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    public Point ToScreen(Point clientPoint)
    {
        var p = new Native.POINT { X = clientPoint.X, Y = clientPoint.Y };
        Native.ClientToScreen(Handle, ref p);
        return new Point(p.X, p.Y);
    }

    public static List<GameWindow> EnumerateVisible()
    {
        var result = new List<GameWindow>();
        var own = Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            var len = Native.GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            Native.GetWindowText(h, sb, sb.Capacity);
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == own) return true;
            result.Add(new GameWindow(h, sb.ToString(), ProcessNameOf(pid)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Ищет окно по сохранённым процессу и заголовку, иначе — первый клиент игры.</summary>
    public static GameWindow? Find(string? processName, string? title)
    {
        var all = EnumerateVisible();
        bool SameProcess(GameWindow w, string? p) =>
            !string.IsNullOrEmpty(p) && string.Equals(w.ProcessName, p, StringComparison.OrdinalIgnoreCase);

        return all.FirstOrDefault(w => SameProcess(w, processName) && w.Title == title)
            ?? all.FirstOrDefault(w => SameProcess(w, processName))
            ?? all.FirstOrDefault(w => !string.IsNullOrEmpty(title) && w.Title == title)
            ?? all.FirstOrDefault(w => SameProcess(w, DefaultProcess));
    }

    private static string ProcessNameOf(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }
}
