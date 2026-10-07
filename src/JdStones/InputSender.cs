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

    /// <summary>Фоновый клик с «наведением»: курсор задерживается над кнопкой перед нажатием.</summary>
    public static void BackgroundHoverClick(GameWindow window, Point client)
    {
        var lParam = (IntPtr)((client.Y << 16) | (client.X & 0xFFFF));
        for (var i = 0; i < 3; i++)
        {
            Native.PostMessage(window.Handle, Native.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            Thread.Sleep(60);
        }
        Native.PostMessage(window.Handle, Native.WM_LBUTTONDOWN, (IntPtr)Native.MK_LBUTTON, lParam);
        Thread.Sleep(120);
        Native.PostMessage(window.Handle, Native.WM_LBUTTONUP, IntPtr.Zero, lParam);
    }

    /// <summary>Клавиша Enter, отправленная окну игры (без активации окна).</summary>
    public static void BackgroundEnter(GameWindow window)
    {
        const int scan = 0x1C;
        var down = (IntPtr)(1 | (scan << 16));
        var up = (IntPtr)unchecked((int)(1 | (scan << 16) | (1u << 30) | (1u << 31)));
        Native.PostMessage(window.Handle, Native.WM_KEYDOWN, (IntPtr)Native.VK_RETURN, down);
        Native.PostMessage(window.Handle, Native.WM_CHAR, (IntPtr)'\r', down);
        Thread.Sleep(50);
        Native.PostMessage(window.Handle, Native.WM_KEYUP, (IntPtr)Native.VK_RETURN, up);
    }

    /// <summary>
    /// Сообщает игре «твоё окно активно», не активируя его на самом деле (браузер остаётся сверху,
    /// фокус и курсор не трогаются). Некоторые игры, считая себя неактивными, игнорируют клики
    /// по модальным окнам.
    /// </summary>
    public static void PretendActive(GameWindow window)
    {
        Native.PostMessage(window.Handle, Native.WM_ACTIVATEAPP, (IntPtr)1, IntPtr.Zero);
        Native.PostMessage(window.Handle, Native.WM_ACTIVATE, (IntPtr)Native.WA_ACTIVE, IntPtr.Zero);
        Native.PostMessage(window.Handle, Native.WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(80);
    }

    /// <summary>
    /// Запасной вариант: на долю секунды выводит игру на передний план, кликает настоящей мышью
    /// и возвращает обратно активное окно и курсор.
    /// </summary>
    public static void ForegroundClickAndRestore(GameWindow window, Point client)
    {
        var previous = Native.GetForegroundWindow();
        Native.GetCursorPos(out var cursor);
        // Нажатие Alt снимает запрет Windows на смену активного окна из фоновой программы.
        Native.keybd_event(Native.VK_MENU, 0, 0, IntPtr.Zero);
        Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, IntPtr.Zero);
        Native.SetForegroundWindow(window.Handle);
        Thread.Sleep(80);
        LeftClick(window.ToScreen(client));
        Thread.Sleep(40);
        Native.SetCursorPos(cursor.X, cursor.Y);
        if (previous != IntPtr.Zero && previous != window.Handle) Native.SetForegroundWindow(previous);
    }

    private static void Send(uint flags)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, mi = new Native.MOUSEINPUT { dwFlags = flags } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }
}
