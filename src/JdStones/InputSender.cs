using System.Drawing;
using System.Runtime.InteropServices;

namespace JdStones;

internal static class InputSender
{
    /// <summary>Наводит курсор и делает клик левой кнопкой (как настоящая мышь).</summary>
    public static void LeftClick(Point screen)
    {
        Native.SetCursorPos(screen.X, screen.Y);
        Thread.Sleep(30);
        Send(Native.MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(40);
        Send(Native.MOUSEEVENTF_LEFTUP);
    }

    /// <summary>
    /// Фоновый клик: сообщения мыши отправляются прямо окну игры, настоящий курсор не двигается
    /// и окно не активируется. Работает, только если игра берёт координаты из сообщений.
    /// </summary>
    public static void BackgroundClick(GameWindow window, Point client)
    {
        var target = window.Handle;
        var pt = new Native.POINT { X = client.X, Y = client.Y };
        var child = Native.ChildWindowFromPointEx(window.Handle, pt, Native.CWP_SKIPINVISIBLE | Native.CWP_SKIPDISABLED);
        if (child != IntPtr.Zero && child != window.Handle)
        {
            Native.MapWindowPoints(window.Handle, child, ref pt, 1);
            target = child;
        }
        var lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
        Native.PostMessage(target, Native.WM_MOUSEMOVE, IntPtr.Zero, lParam);
        Thread.Sleep(30);
        Native.PostMessage(target, Native.WM_LBUTTONDOWN, (IntPtr)Native.MK_LBUTTON, lParam);
        Thread.Sleep(50);
        Native.PostMessage(target, Native.WM_LBUTTONUP, IntPtr.Zero, lParam);
    }

    private static void Send(uint flags)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, mi = new Native.MOUSEINPUT { dwFlags = flags } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }
}
