using System.Diagnostics;
using System.Drawing;
using JdStones.Core;

namespace JdStones;

internal sealed class RollerConfig
{
    public required GameWindow Window { get; init; }
    public required Rectangle StatsRegion { get; init; }
    public required Point RerollClick { get; init; }
    public Rectangle? WarningRegion { get; init; }
    public Point? WarningClick { get; init; }
    public int DelayMs { get; init; }
    public int MaxAttempts { get; init; }
    public required List<ConditionGroup> Groups { get; init; }
}

internal enum RollOutcome
{
    Found,
    Stopped,
    LimitReached,
}

internal sealed record RollResult(RollOutcome Outcome, int Attempts, IReadOnlyList<StatLine> LastStats, int MatchedGroup = -1);

/// <summary>
/// Цикл перековки: клик → задержка → чтение статов → проверка фильтров.
/// Единственная пауза — задержка из настроек. Если статы не изменились, значит клик
/// не сработал или игра ждёт подтверждения редкого камня: только тогда проверяем предупреждение.
/// </summary>
internal sealed class StoneRoller(OcrService ocr, Action<string> log)
{

    public async Task<RollResult> RunAsync(RollerConfig cfg, CancellationToken ct)
    {
        var customStats = cfg.Groups.SelectMany(g => g.Conditions).Select(c => c.Stat);
        var matcher = new StatMatcher(StatCatalog.Predefined.Concat(customStats));

        string? previous = null;
        IReadOnlyList<StatLine> stats = [];
        var attempts = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (cfg.MaxAttempts > 0 && attempts >= cfg.MaxAttempts)
                    return new RollResult(RollOutcome.LimitReached, attempts, stats);

                InputSender.LeftClick(cfg.Window.ToScreen(cfg.RerollClick));
                await Task.Delay(cfg.DelayMs, ct);

                var started = Stopwatch.StartNew();
                var (rows, signature) = await ReadRowsAsync(cfg);
                var unchanged = signature == previous;

                if (unchanged || previous == null)
                {
                    if (await ConfirmRareWarningAsync(cfg, ct))
                    {
                        (rows, signature) = await ReadRowsAsync(cfg);
                        unchanged = signature == previous;
                    }
                }
                previous = signature;
                attempts++;

                // Старые статы уже проверены и не подошли — просто кликаем дальше, без ожиданий.
                stats = StatParser.Parse(rows, matcher);
                var timing = $"(распознавание {started.ElapsedMilliseconds} мс)";
                log(stats.Count == 0
                    ? $"#{attempts}: статы не распознаны {timing}. Проверьте область кнопкой «Проверить распознавание»."
                    : unchanged
                        ? $"#{attempts}: статы не изменились — клик не сработал? {timing}"
                        : $"#{attempts}: {string.Join(" | ", stats)} {timing}");

                var matched = ConditionEvaluator.MatchingGroupIndex(cfg.Groups, stats);
                if (matched >= 0)
                    return new RollResult(RollOutcome.Found, attempts, stats, matched);
            }
        }
        catch (OperationCanceledException)
        {
            return new RollResult(RollOutcome.Stopped, attempts, stats);
        }
    }

    /// <returns>true, если предупреждение было и мы его подтвердили.</returns>
    private async Task<bool> ConfirmRareWarningAsync(RollerConfig cfg, CancellationToken ct)
    {
        if (cfg.WarningRegion is not { } region || cfg.WarningClick is not { } click) return false;
        using (var shot = WindowCapture.Capture(cfg.Window, region))
        {
            if (!WarningDetector.IsRareStoneWarning(await ocr.RecognizeTextAsync(shot))) return false;
        }
        log("Предупреждение «Такие камни весьма редки» — подтверждаю.");
        InputSender.LeftClick(cfg.Window.ToScreen(click));
        await Task.Delay(cfg.DelayMs, ct);
        return true;
    }

    private async Task<(IReadOnlyList<string> Rows, string Signature)> ReadRowsAsync(RollerConfig cfg)
    {
        using var shot = WindowCapture.Capture(cfg.Window, cfg.StatsRegion);
        var rows = StatParser.GroupIntoRows(await ocr.RecognizeAsync(shot));
        return (rows, string.Join("\n", rows));
    }
}
