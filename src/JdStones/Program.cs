using System.Windows.Forms;

namespace JdStones;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, "JdStones.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Программа уже запущена.", "Камни истока", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);
        Application.Run(new MainForm());
    }

    private static void ShowError(Exception? ex) =>
        MessageBox.Show(ex?.Message ?? "Неизвестная ошибка", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
