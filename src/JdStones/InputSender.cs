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

    private static void Send(uint flags)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, mi = new Native.MOUSEINPUT { dwFlags = flags } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }
}
