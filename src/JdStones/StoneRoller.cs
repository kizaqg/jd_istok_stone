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

internal sealed record RollResult(RollOutcome Outcome, int Attempts, IReadOnlyList<StatLine> LastStats);

/// <summary>Цикл перековки: клик → (подтверждение редкого камня) → чтение статов → проверка фильтров.</summary>
internal sealed class StoneRoller(OcrService ocr, Action<string> log)
{
    // Если после клика статы те же, ждём обновления окна ещё немного, а не читаем старый результат.
    private const int ChangeWaitStepMs = 200;
    private const int ChangeWaitSteps = 10;

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

                if (cfg.WarningRegion is { } warnRegion && cfg.WarningClick is { } warnClick)
                {
                    using var warnShot = WindowCapture.Capture(cfg.Window, warnRegion);
                    if (WarningDetector.IsRareStoneWarning(await ocr.RecognizeTextAsync(warnShot)))
                    {
                        log("Предупреждение «Такие камни весьма редки» — подтверждаю.");
                        InputSender.LeftClick(cfg.Window.ToScreen(warnClick));
                        await Task.Delay(cfg.DelayMs, ct);
                    }
                }

                var (rows, signature) = await ReadRowsAsync(cfg);
                for (var i = 0; i < ChangeWaitSteps && signature == previous; i++)
                {
                    await Task.Delay(ChangeWaitStepMs, ct);
                    (rows, signature) = await ReadRowsAsync(cfg);
                }
                previous = signature;
                attempts++;

                stats = StatParser.Parse(rows, matcher);
                log(stats.Count > 0
                    ? $"#{attempts}: {string.Join(" | ", stats)}"
                    : $"#{attempts}: статы не распознаны. Проверьте область кнопкой «Проверить распознавание».");

                if (ConditionEvaluator.Matches(cfg.Groups, stats))
                    return new RollResult(RollOutcome.Found, attempts, stats);
            }
        }
        catch (OperationCanceledException)
        {
            return new RollResult(RollOutcome.Stopped, attempts, stats);
        }
    }

    private async Task<(IReadOnlyList<string> Rows, string Signature)> ReadRowsAsync(RollerConfig cfg)
    {
        using var shot = WindowCapture.Capture(cfg.Window, cfg.StatsRegion);
        var rows = StatParser.GroupIntoRows(await ocr.RecognizeAsync(shot));
        return (rows, string.Join("\n", rows));
    }
}
