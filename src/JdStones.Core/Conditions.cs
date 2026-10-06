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

    /// <summary>Хотя бы одна непустая группа, в которой выполнены все условия.</summary>
    public static bool Matches(IEnumerable<ConditionGroup> groups, IReadOnlyList<StatLine> lines) =>
        groups.Any(g => g.Conditions.Count > 0 && g.Conditions.All(c => IsMet(c, lines)));

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
