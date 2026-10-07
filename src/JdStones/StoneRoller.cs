using System.Diagnostics;
using System.Drawing;
using JdStones.Core;

namespace JdStones;

internal sealed class RollerConfig
{
    /// <summary>Снимок области окна игры (в координатах клиентской части).</summary>
    public required Func<Rectangle, Bitmap> Capture { get; init; }
    /// <summary>Клик в точку окна игры (в координатах клиентской части).</summary>
    public required Action<Point> Click { get; init; }
    public required Rectangle StatsRegion { get; init; }
    public required Point RerollClick { get; init; }
    public Rectangle? WarningRegion { get; init; }
    public Point? WarningClick { get; init; }
    /// <summary>Способы нажать «Да» в окне предупреждения — пробуются по очереди, удачный запоминается.</summary>
    public IReadOnlyList<ConfirmMethod> ConfirmMethods { get; init; } = [];
    /// <summary>Название способа, сработавшего в прошлый раз (из настроек) — пробуется первым.</summary>
    public string? PreferredConfirm { get; init; }
    public int IntervalMs { get; init; }
    public int MaxAttempts { get; init; }
    public required List<ConditionGroup> Groups { get; init; }
}

internal sealed record ConfirmMethod(string Name, Action<Point> Click);

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
internal sealed class StoneRoller(OcrService ocr, Action<string> log, Action<int>? progress = null)
{
    private const int PollMs = 50;
    private const int WarningCheckEveryMs = 300;
    private const int ConfirmTriesPerMethod = 2;
    // Если за интервал статы не пришли — ждём ещё столько, прежде чем кликать снова.
    private const int LateGraceMs = 300;
    private int _confirmsInRow;
    private ConfirmMethod? _pendingMethod;
    private ConfirmMethod? _workingMethod;

    /// <summary>Способ, которым удалось закрыть окно предупреждения (чтобы сохранить в настройках).</summary>
    public string? WorkingConfirmName => _workingMethod?.Name;

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

                // Последняя проверка прямо перед кликом (~0.1 с): если загрузка затянулась и новые
                // статы появились только сейчас, сначала проверяем их. Иначе клик перекрутил бы
                // подходящий камень, который программа ещё не успела посмотреть.
                if (await TryReadNewStatsAsync(cfg, matcher, previous) is { } late)
                {
                    stats = late;
                    previous = Signature(stats);
                    attempts++;
                    progress?.Invoke(attempts);
                    log($"#{attempts}: {string.Join(" | ", stats)} (статы появились с опозданием)");
                    if (await MatchingGroupAsync(cfg, matcher, stats) is var lateGroup && lateGroup >= 0)
                        return new RollResult(RollOutcome.Found, attempts, stats, lateGroup);
                    continue;
                }

                cfg.Click(cfg.RerollClick);
                var clicked = Stopwatch.StartNew();
                var lastWarningCheck = long.MinValue / 2;
                IReadOnlyList<StatLine>? fresh = null;

                // Ждём новые статы до конца интервала (+ чуть-чуть, если загрузка затянулась).
                while (clicked.ElapsedMilliseconds < cfg.IntervalMs + LateGraceMs)
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

                    fresh = await TryReadNewStatsAsync(cfg, matcher, previous);
                    if (fresh != null) break;
                }

                attempts++;
                progress?.Invoke(attempts);
                if (fresh != null)
                {
                    stats = fresh;
                    previous = Signature(stats);
                    log($"#{attempts}: {string.Join(" | ", stats)} (статы через {clicked.ElapsedMilliseconds / 1000.0:0.0} с)");
                    if (await MatchingGroupAsync(cfg, matcher, stats) is var group && group >= 0)
                        return new RollResult(RollOutcome.Found, attempts, stats, group);
                }
                else
                {
                    log($"#{attempts}: статы не обновились за {(cfg.IntervalMs + LateGraceMs) / 1000.0:0.0} с — кликаю снова. " +
                        "Если часто — проверьте область статов или увеличьте интервал.");
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

    /// <summary>
    /// Новые (отличные от <paramref name="previous"/>) и уже полностью загруженные статы или null.
    /// Не принимает статы, если их перекрыло окно предупреждения.
    /// </summary>
    private async Task<IReadOnlyList<StatLine>?> TryReadNewStatsAsync(RollerConfig cfg, StatMatcher matcher, string previous)
    {
        var stats = await ReadStatsAsync(cfg, matcher);
        var current = Signature(stats);
        if (stats.Count == 0 || current == previous) return null;
        if (await WarningVisibleAsync(cfg)) return null;
        // Читаем ещё раз: совпало — статы загрузились полностью, а не пойманы «наполовину».
        return Signature(await ReadStatsAsync(cfg, matcher)) == current ? stats : null;
    }

    /// <returns>Номер сработавшей группы (после перепроверки) или -1.</returns>
    private async Task<int> MatchingGroupAsync(RollerConfig cfg, StatMatcher matcher, IReadOnlyList<StatLine> stats)
    {
        var group = ConditionEvaluator.MatchingGroupIndex(cfg.Groups, stats);
        return group >= 0 && await ConfirmMatchAsync(cfg, matcher) ? group : -1;
    }

    private async Task<bool> WarningVisibleAsync(RollerConfig cfg)
    {
        if (cfg.WarningRegion is not { } region || cfg.WarningClick == null) return false;
        using var shot = cfg.Capture(region);
        return WarningDetector.IsRareStoneWarning(await ocr.RecognizeTextAsync(shot));
    }

    /// <returns>true, если предупреждение было и мы его подтвердили.</returns>
    private async Task<bool> ConfirmRareWarningAsync(RollerConfig cfg)
    {
        if (cfg.WarningRegion is not { } region || cfg.WarningClick is not { } click) return false;
        using (var shot = cfg.Capture(region))
        {
            if (!WarningDetector.IsRareStoneWarning(await ocr.RecognizeTextAsync(shot)))
            {
                if (_pendingMethod != null && _workingMethod != _pendingMethod)
                {
                    // Окно закрылось после этого способа — дальше сразу используем его.
                    _workingMethod = _pendingMethod;
                    log($"Окно предупреждения закрывается способом: {_workingMethod.Name}.");
                }
                _pendingMethod = null;
                _confirmsInRow = 0;
                return false;
            }
        }

        var methods = cfg.ConfirmMethods.Count > 0 ? cfg.ConfirmMethods : [new ConfirmMethod("клик", cfg.Click)];
        _workingMethod ??= methods.FirstOrDefault(m => m.Name == cfg.PreferredConfirm);
        var attempt = _confirmsInRow++;
        if (attempt >= methods.Count * ConfirmTriesPerMethod)
            throw new InvalidOperationException(
                "Окно «Такие камни весьма редки» не закрывается ни одним способом. " +
                "Заново укажите «Кнопку подтверждения» — точно по кнопке «Да».");

        // Сначала способ, который уже срабатывал; если он перестал — перебираем все по очереди.
        var method = _workingMethod != null && attempt < ConfirmTriesPerMethod
            ? _workingMethod
            : methods[attempt / ConfirmTriesPerMethod % methods.Count];
        _pendingMethod = method;
        log($"Предупреждение «Такие камни весьма редки» — нажимаю «Да» ({method.Name}).");
        method.Click(click);
        await Task.Delay(300); // даём окну закрыться
        return true;
    }

    // Похожие статы («Защита/крит.ат.» и «Защита/крит.ур.») отличаются парой мелких букв,
    // и OCR иногда путает «а/у», «т/р». Находку перепроверяем ещё двумя независимыми
    // чтениями (другое увеличение) и принимаем, если подходят минимум 2 из 3.
    private static readonly int[] VerifyScales = [4, 2];

    private async Task<bool> ConfirmMatchAsync(RollerConfig cfg, StatMatcher matcher)
    {
        var votes = 1;
        var rejected = new List<string>();
        foreach (var scale in VerifyScales)
        {
            var again = await ReadStatsAsync(cfg, matcher, scale);
            if (ConditionEvaluator.MatchingGroupIndex(cfg.Groups, again) >= 0) votes++;
            else rejected.Add(string.Join(" | ", again));
        }
        if (votes >= 2) return true;
        log("   Перепроверка не подтвердила находку (вероятно, ошибка распознавания) — кручу дальше. " +
            "Повторные чтения: " + string.Join("  //  ", rejected));
        return false;
    }

    private async Task<IReadOnlyList<StatLine>> ReadStatsAsync(RollerConfig cfg, StatMatcher matcher,
        int scale = OcrService.DefaultScale)
    {
        using var shot = cfg.Capture(cfg.StatsRegion);
        return StatParser.Parse(StatParser.GroupIntoRows(await ocr.RecognizeAsync(shot, scale)), matcher);
    }
}
