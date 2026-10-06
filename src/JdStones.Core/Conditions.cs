namespace JdStones.Core;

public enum StatUnit
{
    Any,
    Percent,
    Flat,
}

public enum CompareOp
{
    AtLeast,
    AtMost,
    Equal,
}

/// <summary>«Стат [в %/числом] ≥ N раз».</summary>
public sealed class StatCondition
{
    public string Stat { get; set; } = "";
    public StatUnit Unit { get; set; } = StatUnit.Any;
    public CompareOp Operator { get; set; } = CompareOp.AtLeast;
    public int Count { get; set; } = 1;

    public StatCondition Clone() => (StatCondition)MemberwiseClone();
}

/// <summary>Группа условий, объединённых через «И». Группы между собой — через «ИЛИ».</summary>
public sealed class ConditionGroup
{
    public List<StatCondition> Conditions { get; set; } = [];

    public ConditionGroup Clone() => new() { Conditions = Conditions.Select(c => c.Clone()).ToList() };
}

public static class ConditionEvaluator
{
    public static int CountMatching(StatCondition c, IEnumerable<StatLine> lines) =>
        lines.Count(l => StatMatcher.SameStat(l.Stat, c.Stat) && UnitMatches(c.Unit, l));

    public static bool IsMet(StatCondition c, IReadOnlyList<StatLine> lines)
    {
        var count = CountMatching(c, lines);
        return c.Operator switch
        {
            CompareOp.AtLeast => count >= c.Count,
            CompareOp.AtMost => count <= c.Count,
            CompareOp.Equal => count == c.Count,
            _ => false,
        };
    }

    /// <summary>
    /// Хотя бы одна непустая группа, в которой выполнены все условия.
    /// Если статы не распознаны вовсе — никогда не «находим» камень.
    /// </summary>
    public static bool Matches(IReadOnlyList<ConditionGroup> groups, IReadOnlyList<StatLine> lines) =>
        MatchingGroupIndex(groups, lines) >= 0;

    /// <returns>Номер (с 0) первой сработавшей группы или -1.</returns>
    public static int MatchingGroupIndex(IReadOnlyList<ConditionGroup> groups, IReadOnlyList<StatLine> lines)
    {
        if (lines.Count == 0) return -1;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g.Conditions.Count > 0 && g.Conditions.All(c => IsMet(c, lines))) return i;
        }
        return -1;
    }

    /// <summary>Условие «≥ 0» выполняется всегда и ничего не фильтрует.</summary>
    public static bool IsAlwaysTrue(StatCondition c) => c.Operator == CompareOp.AtLeast && c.Count <= 0;

    /// <summary>Группа срабатывает на камне, где нет ни одного из указанных статов (например, «Атака ≤ 1»).</summary>
    public static bool MatchesWithoutStats(ConditionGroup g) =>
        g.Conditions.Count > 0 && g.Conditions.All(c => IsMet(c, []));

    private static bool UnitMatches(StatUnit unit, StatLine line) => unit switch
    {
        StatUnit.Percent => line.IsPercent,
        StatUnit.Flat => !line.IsPercent,
        _ => true,
    };
}

public static class WarningDetector
{
    private static readonly string[] Phrases =
        ["такиекамни", "весьмаредки"];

    /// <summary>Есть ли в тексте предупреждение «Такие камни весьма редки».</summary>
    public static bool IsRareStoneWarning(string text)
    {
        var norm = TextNormalizer.Normalize(text);
        return Phrases.Any(p => TextNormalizer.ContainsFuzzy(norm, p));
    }
}
