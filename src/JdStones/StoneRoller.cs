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
    public int IntervalMs { get; init; }
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
/// Цикл перековки с фиксированным интервалом: клик → ждём новые статы и сразу проверяем →
/// следующий клик ровно через «интервал» после предыдущего (игра не принимает клик,
/// пока не закончилась загрузка, поэтому кликать сразу после появления статов нельзя).
/// </summary>
internal sealed class StoneRoller(OcrService ocr, Action<string> log)
{
    private const int PollMs = 50;
    private const int WarningCheckEveryMs = 1000;

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

                // Ждём новые статы, но не дольше интервала.
                while (clicked.ElapsedMilliseconds < cfg.IntervalMs)
                {
                    await Task.Delay(PollMs, ct);
                    stats = await ReadStatsAsync(cfg, matcher);
                    var current = Signature(stats);
                    if (stats.Count > 0 && current != previous)
                    {
                        // Сразу читаем ещё раз: совпало — статы загрузились полностью,
                        // а не пойманы «наполовину» во время анимации загрузки.
                        if (Signature(await ReadStatsAsync(cfg, matcher)) == current)
                        {
                            changed = true;
                            break;
                        }
                    }
                    else if (clicked.ElapsedMilliseconds >= nextWarningCheck)
                    {
                        nextWarningCheck += WarningCheckEveryMs;
                        await ConfirmRareWarningAsync(cfg);
                    }
                }

                attempts++;
                if (changed)
                {
                    previous = Signature(stats);
                    log($"#{attempts}: {string.Join(" | ", stats)} (статы через {clicked.ElapsedMilliseconds / 1000.0:0.0} с)");
                    var matched = ConditionEvaluator.MatchingGroupIndex(cfg.Groups, stats);
                    if (matched >= 0)
                        return new RollResult(RollOutcome.Found, attempts, stats, matched);
                }
                else
                {
                    log(stats.Count == 0
                        ? $"#{attempts}: статы не распознаны. Проверьте область кнопкой «Проверить распознавание»."
                        : $"#{attempts}: статы не обновились за {cfg.IntervalMs / 1000.0:0.0} с — кликаю снова. Если часто — увеличьте интервал.");
                }

                var left = cfg.IntervalMs - (int)clicked.ElapsedMilliseconds;
                if (left > 0) await Task.Delay(left, ct);
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
