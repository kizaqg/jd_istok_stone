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
/// Цикл перековки: клик → ждём, пока после загрузки появятся НОВЫЕ статы → проверка фильтров →
/// пауза из настроек → следующий клик. Пока статы не меняются, раз в секунду проверяем,
/// не ждёт ли игра подтверждения «Такие камни весьма редки».
/// </summary>
internal sealed class StoneRoller(OcrService ocr, Action<string> log)
{
    private const int PollMs = 50;
    private const int WarningCheckEveryMs = 1000;
    private const int LoadTimeoutMs = 10_000;

    public async Task<RollResult> RunAsync(RollerConfig cfg, CancellationToken ct)
    {
        var customStats = cfg.Groups.SelectMany(g => g.Conditions).Select(c => c.Stat);
        var matcher = new StatMatcher(StatCatalog.Predefined.Concat(customStats));

        IReadOnlyList<StatLine> stats = [];
        var attempts = 0;

        try
        {
            // Статы, которые на экране до первого клика: новыми считаем только отличающиеся от них.
            var previous = Signature(await ReadStatsAsync(cfg, matcher));

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (cfg.MaxAttempts > 0 && attempts >= cfg.MaxAttempts)
                    return new RollResult(RollOutcome.LimitReached, attempts, stats);

                InputSender.LeftClick(cfg.Window.ToScreen(cfg.RerollClick));
                var clicked = Stopwatch.StartNew();
                var nextWarningCheck = WarningCheckEveryMs;
                var changed = false;

                while (true)
                {
                    await Task.Delay(PollMs, ct);
                    stats = await ReadStatsAsync(cfg, matcher);
                    var current = Signature(stats);
                    if (stats.Count > 0 && current != previous)
                    {
                        // Сразу читаем ещё раз: совпало — статы загрузились полностью,
                        // а не пойманы «наполовину» во время анимации загрузки.
                        var again = await ReadStatsAsync(cfg, matcher);
                        if (Signature(again) == current)
                        {
                            changed = true;
                            break;
                        }
                    }
                    if (clicked.ElapsedMilliseconds >= LoadTimeoutMs) break;
                    if (clicked.ElapsedMilliseconds >= nextWarningCheck)
                    {
                        nextWarningCheck += WarningCheckEveryMs;
                        await ConfirmRareWarningAsync(cfg);
                    }
                }

                attempts++;
                previous = Signature(stats);
                var loadTime = $"(через {clicked.ElapsedMilliseconds / 1000.0:0.0} с после клика)";
                if (!changed)
                {
                    log(stats.Count == 0
                        ? $"#{attempts}: статы не распознаны. Проверьте область кнопкой «Проверить распознавание»."
                        : $"#{attempts}: статы не изменились за {LoadTimeoutMs / 1000} с — клик не сработал? Кликаю ещё раз.");
                    continue;
                }

                log($"#{attempts}: {string.Join(" | ", stats)} {loadTime}");
                var matched = ConditionEvaluator.MatchingGroupIndex(cfg.Groups, stats);
                if (matched >= 0)
                    return new RollResult(RollOutcome.Found, attempts, stats, matched);

                if (cfg.DelayMs > 0) await Task.Delay(cfg.DelayMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return new RollResult(RollOutcome.Stopped, attempts, stats);
        }
    }

    private static string Signature(IReadOnlyList<StatLine> stats) => string.Join("\n", stats);

    /// <returns>true, если предупреждение было и мы его подтвердили.</returns>
    private async Task<bool> ConfirmRareWarningAsync(RollerConfig cfg)
    {
        if (cfg.WarningRegion is not { } region || cfg.WarningClick is not { } click) return false;
        using (var shot = WindowCapture.Capture(cfg.Window, region))
        {
            if (!WarningDetector.IsRareStoneWarning(await ocr.RecognizeTextAsync(shot))) return false;
        }
        log("Предупреждение «Такие камни весьма редки» — подтверждаю.");
        InputSender.LeftClick(cfg.Window.ToScreen(click));
        return true;
    }

    private async Task<IReadOnlyList<StatLine>> ReadStatsAsync(RollerConfig cfg, StatMatcher matcher)
    {
        using var shot = WindowCapture.Capture(cfg.Window, cfg.StatsRegion);
        return StatParser.Parse(StatParser.GroupIntoRows(await ocr.RecognizeAsync(shot)), matcher);
    }
}
