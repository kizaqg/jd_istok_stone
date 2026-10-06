using System.Drawing;
using System.Text.Json;
using JdStones.Core;

namespace JdStones;

/// <summary>Настройки сохраняются автоматически в %APPDATA%\JdStones\settings.json.</summary>
internal sealed class AppSettings
{
    public string? WindowProcess { get; set; }
    public string? WindowTitle { get; set; }
    public int[]? StatsRegion { get; set; }
    public int[]? RerollClick { get; set; }
    public int[]? WarningRegion { get; set; }
    public int[]? WarningClick { get; set; }
    public int DelayMs { get; set; } = 1000;
    public int MaxAttempts { get; set; }
    public List<List<ConditionDto>> Groups { get; set; } = [];

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JdStones", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), TemplateSerializer.Options) ?? new();
        }
        catch
        {
            // Повреждённый файл настроек — просто начинаем с чистого листа.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, TemplateSerializer.Options));
        }
        catch
        {
            // Не удалось сохранить — не критично для работы.
        }
    }

    public static int[]? FromRect(Rectangle? r) => r is { } v ? [v.X, v.Y, v.Width, v.Height] : null;
    public static Rectangle? ToRect(int[]? a) => a is { Length: 4 } ? new Rectangle(a[0], a[1], a[2], a[3]) : null;
    public static int[]? FromPoint(Point? p) => p is { } v ? [v.X, v.Y] : null;
    public static Point? ToPoint(int[]? a) => a is { Length: 2 } ? new Point(a[0], a[1]) : null;
}
