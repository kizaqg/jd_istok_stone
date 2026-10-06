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
    private const int WarningCheckEveryMs = 300;
    private const int MaxConfirmsInRow = 5;
    private int _confirmsInRow;

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

                // Окно «Такие камни весьма редки» перекрывает статы и кнопки: сначала закрываем его.
                if (await ConfirmRareWarningAsync(cfg)) await Task.Delay(PollMs, ct);

                InputSender.LeftClick(cfg.Window.ToScreen(cfg.RerollClick));
                var clicked = Stopwatch.StartNew();
                var lastWarningCheck = long.MinValue / 2;
                var changed = false;

                // Ждём новые статы, но не дольше интервала.
                while (clicked.ElapsedMilliseconds < cfg.IntervalMs)
                {
                    await Task.Delay(PollMs, ct);

                    if (clicked.ElapsedMilliseconds - lastWarningCheck >= WarningCheckEveryMs)
                    {
                        lastWarningCheck = clicked.ElapsedMilliseconds;
                        if (await ConfirmRareWarningAsync(cfg))
                        {
                            // Нажали «Да» — перековка пошла только сейчас: отсчёт интервала заново.
                            clicked.Restart();
                            lastWarningCheck = 0;
                            continue;
                        }
                    }

                    stats = await ReadStatsAsync(cfg, matcher);
                    var current = Signature(stats);
                    if (stats.Count > 0 && current != previous)
                    {
                        // Перед тем как принять «новые» статы, убеждаемся, что это не окно
                        // предупреждения их закрыло, и читаем ещё раз: совпало — загрузились полностью.
                        if (await ConfirmRareWarningAsync(cfg))
                        {
                            clicked.Restart();
                            lastWarningCheck = 0;
                            continue;
                        }
                        if (Signature(await ReadStatsAsync(cfg, matcher)) == current)
                        {
                            changed = true;
                            break;
                        }
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
            if (!WarningDetector.IsRareStoneWarning(await ocr.RecognizeTextAsync(shot)))
            {
                _confirmsInRow = 0;
                return false;
            }
        }
        if (++_confirmsInRow > MaxConfirmsInRow)
            throw new InvalidOperationException(
                "Окно «Такие камни весьма редки» не закрывается после клика. " +
                "Заново укажите «Кнопку подтверждения» — точно по кнопке «Да».");
        log("Предупреждение «Такие камни весьма редки» — нажимаю «Да».");
        InputSender.LeftClick(cfg.Window.ToScreen(click));
        await Task.Delay(300); // даём окну закрыться
        return true;
    }

    private async Task<IReadOnlyList<StatLine>> ReadStatsAsync(RollerConfig cfg, StatMatcher matcher)
    {
        using var shot = WindowCapture.Capture(cfg.Window, cfg.StatsRegion);
        return StatParser.Parse(StatParser.GroupIntoRows(await ocr.RecognizeAsync(shot)), matcher);
    }
}
