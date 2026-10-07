using JdStones.Core;
using Xunit;

namespace JdStones.Core.Tests;

public class ParserTests
{
    private static readonly StatMatcher Matcher = new(StatCatalog.Predefined);

    // Строки как на скриншоте окна камня: «Текущие» + колонка «Развитие».
    private static readonly string[] ScreenshotRows =
    [
        "Текущие Развитие",
        "Здоровье 86.80 86.80/588.00",
        "Сопр. Эйдосу 0.099% 0.099%/0.630%",
        "Снижен. урона 0.089% 0.089%/0.886%",
        "Увел. здоровья 0.195% 0.195%/0.630%",
        "Снижен. урона 0.121% 0.121%/0.886%",
        "Снижен. урона 5.98 5.98/27.40",
    ];

    [Fact]
    public void ParsesScreenshotRows()
    {
        var lines = StatParser.Parse(ScreenshotRows, Matcher);

        Assert.Equal(6, lines.Count);
        Assert.Equal(new StatLine("Здоровье", 86.8, false, ScreenshotRows[1]), lines[0]);
        Assert.Equal(new StatLine("Сопр. Эйдосу", 0.099, true, ScreenshotRows[2]), lines[1]);
        Assert.Equal(new StatLine("Увел. здоровья", 0.195, true, ScreenshotRows[4]), lines[3]);
        Assert.Equal(new StatLine("Снижен. урона", 5.98, false, ScreenshotRows[6]), lines[5]);
    }

    [Theory]
    [InlineData(StatUnit.Any, 3)]
    [InlineData(StatUnit.Percent, 2)]
    [InlineData(StatUnit.Flat, 1)]
    public void CountsDamageReductionByUnit(StatUnit unit, int expected)
    {
        var lines = StatParser.Parse(ScreenshotRows, Matcher);
        var c = new StatCondition { Stat = "Снижен. урона", Unit = unit };

        Assert.Equal(expected, ConditionEvaluator.CountMatching(c, lines));
    }

    [Fact]
    public void DefenceDoesNotCountCombinedDefenceStats()
    {
        string[] rows = ["Защита 10", "Защита/крит.ат. 0.1%", "Защита/сниж.ур. 0.2%", "Усил. защиты 0.3%"];
        var lines = StatParser.Parse(rows, Matcher);

        Assert.Equal(1, ConditionEvaluator.CountMatching(new StatCondition { Stat = "Защита" }, lines));
        Assert.Equal(1, ConditionEvaluator.CountMatching(new StatCondition { Stat = "Защита/сниж.ур." }, lines));
    }

    [Theory]
    [InlineData("Cнижeн. уpoнa 0,121 %", "Снижен. урона", true)] // латиница вместо кириллицы, запятая
    [InlineData("Снижен урона 0.121%", "Снижен. урона", true)]   // потерянная точка
    [InlineData("Сннжен. урона 5.98", "Снижен. урона", false)]   // одна ошибка в букве
    [InlineData("Дух 12", "Дух", false)]
    [InlineData("Увел. духа 0.5%", "Увел. духа", true)]
    public void ToleratesOcrNoise(string row, string stat, bool percent)
    {
        var line = Assert.Single(StatParser.Parse([row], Matcher));
        Assert.Equal(stat, line.Stat);
        Assert.Equal(percent, line.IsPercent);
    }

    [Theory]
    [InlineData("Защита/крит.ат. 0.1%", "Защита/крит.ат.")]
    [InlineData("Защита/крит.ур. 0.1%", "Защита/крит.ур.")]
    [InlineData("Защита/крит.аг. 0.1%", "Защита/крит.ат.")] // одна ошибка, ближе к «ат»
    public void DistinguishesSimilarDefenceStats(string row, string expected)
    {
        Assert.Equal(expected, Assert.Single(StatParser.Parse([row], Matcher)).Stat);
    }

    [Theory]
    [InlineData("Защита/крит.ут. 0.1%")] // одинаково похоже на «ат» и «ур»
    [InlineData("Защита/крит.ар. 0.1%")]
    public void AmbiguousMisreadIsNotGuessed(string row)
    {
        Assert.Empty(StatParser.Parse([row], Matcher));
    }

    [Theory]
    // «%» потерян у значения, но есть в колонке «Развитие».
    [InlineData("Снижен. урона 0.121 0.121%/0.886%", true)]
    [InlineData("Сопр. Феору 1560 0.156%/0.630%", true)]
    // «%» прочитан как «0», а «0.» потерян: 0.054% → 540.
    [InlineData("Снижен. урона 540", true)]
    // Без «%» и без «Развития»: меньше 1 — процент, 1..30 — число.
    [InlineData("Снижен. урона 0.089", true)]
    [InlineData("Снижен. урона 5.98", false)]
    [InlineData("Снижен. урона 2.47 2.47/27.40", false)]
    // Максимум «Развития» выдаёт процент, даже если значение распозналось числом.
    [InlineData("Снижен. урона 12 0.12/0.886", true)]
    // Числовой стат остаётся числом.
    [InlineData("Здоровье 86.80 86.80/588.00", false)]
    public void DetectsPercentEvenWhenOcrLosesTheSign(string row, bool percent)
    {
        Assert.Equal(percent, Assert.Single(StatParser.Parse([row], Matcher)).IsPercent);
    }

    [Fact]
    public void SkipsRowsWithoutKnownStat()
    {
        Assert.Empty(StatParser.Parse(["Текущие Развитие", "Чтото непонятное 5", "Здоровье"], Matcher));
    }

    [Fact]
    public void GroupsWordsIntoRows()
    {
        OcrWord[] words =
        [
            new("0.099%", 100, 21, 40, 10),
            new("Сопр.", 0, 20, 30, 10),
            new("Эйдосу", 35, 20, 40, 10),
            new("Здоровье", 0, 2, 50, 10),
            new("86.80", 100, 3, 30, 10),
        ];

        Assert.Equal(["Здоровье 86.80", "Сопр. Эйдосу 0.099%"], StatParser.GroupIntoRows(words));
    }
}

public class ConditionTests
{
    private static List<StatLine> Lines(params (string Stat, bool Percent)[] items) =>
        items.Select(i => new StatLine(i.Stat, 1, i.Percent, "")).ToList();

    [Fact]
    public void GroupsAreOrAndConditionsAreAnd()
    {
        var groups = new List<ConditionGroup>
        {
            new() { Conditions = [new() { Stat = "Атака", Count = 2 }, new() { Stat = "Дух", Count = 1 }] },
            new() { Conditions = [new() { Stat = "Снижен. урона", Unit = StatUnit.Percent, Count = 3 }] },
        };

        Assert.True(ConditionEvaluator.Matches(groups, Lines(("Атака", false), ("Атака", false), ("Дух", false))));
        Assert.False(ConditionEvaluator.Matches(groups, Lines(("Атака", false), ("Дух", false))));
        Assert.True(ConditionEvaluator.Matches(groups,
            Lines(("Снижен. урона", true), ("Снижен. урона", true), ("Снижен. урона", true))));
        Assert.False(ConditionEvaluator.Matches(groups,
            Lines(("Снижен. урона", true), ("Снижен. урона", true), ("Снижен. урона", false))));
    }

    // Камень со скриншота: «Атаки» нет — фильтр на Атаку срабатывать не должен.
    private static readonly string[] StoneWithoutAttack =
    [
        "Меткость 0.64 0.64/8.40",
        "Дух 41.60 41.60/67.20",
        "Здоровье 33.60 33.60/588.00",
        "Защита/крит.ур. 0.209% 0.209%/0.630%",
        "Меткость 2.36 2.36/8.40",
    ];

    [Fact]
    public void AttackFilterDoesNotMatchStoneWithoutAttack()
    {
        var lines = StatParser.Parse(StoneWithoutAttack, new StatMatcher(StatCatalog.Predefined));
        Assert.Equal(5, lines.Count);
        Assert.Equal(2, lines.Count(l => l.Stat == "Меткость"));

        List<ConditionGroup> groups = [new() { Conditions = [new() { Stat = "Атака", Count = 1 }] }];
        Assert.Equal(-1, ConditionEvaluator.MatchingGroupIndex(groups, lines));
    }

    [Fact]
    public void NothingRecognizedNeverMatches()
    {
        List<ConditionGroup> groups = [new() { Conditions = [new() { Stat = "Атака", Operator = CompareOp.AtMost, Count = 1 }] }];
        Assert.False(ConditionEvaluator.Matches(groups, []));
    }

    [Fact]
    public void DetectsFiltersThatMatchWithoutStats()
    {
        Assert.True(ConditionEvaluator.IsAlwaysTrue(new StatCondition { Stat = "Атака", Count = 0 }));
        Assert.False(ConditionEvaluator.IsAlwaysTrue(new StatCondition { Stat = "Атака", Count = 1 }));
        Assert.True(ConditionEvaluator.MatchesWithoutStats(new ConditionGroup
            { Conditions = [new() { Stat = "Атака", Operator = CompareOp.AtMost, Count = 1 }] }));
        Assert.False(ConditionEvaluator.MatchesWithoutStats(new ConditionGroup
            { Conditions = [new() { Stat = "Атака", Operator = CompareOp.AtMost, Count = 1 }, new() { Stat = "Дух", Count = 1 }] }));
    }

    [Fact]
    public void EmptyGroupNeverMatches()
    {
        Assert.False(ConditionEvaluator.Matches([new ConditionGroup()], Lines(("Атака", false))));
    }

    [Theory]
    [InlineData(CompareOp.AtMost, 1, true)]
    [InlineData(CompareOp.AtMost, 0, false)]
    [InlineData(CompareOp.Equal, 1, true)]
    [InlineData(CompareOp.Equal, 2, false)]
    public void Operators(CompareOp op, int count, bool expected)
    {
        var c = new StatCondition { Stat = "Воля", Operator = op, Count = count };
        Assert.Equal(expected, ConditionEvaluator.IsMet(c, Lines(("Воля", false))));
    }

    [Theory]
    [InlineData("Такие камни весьма редки. Вы уверены?", true)]
    [InlineData("Такие камии весьма редкн", true)]
    [InlineData("Количество эффектов у вашего камня истока - 7. Такие камни весьма редки. Вы уверены, что хотите провести ритуал обновления?", true)]
    [InlineData("Перековать камень?", false)]
    public void DetectsRareWarning(string text, bool expected)
    {
        Assert.Equal(expected, WarningDetector.IsRareStoneWarning(text));
    }
}

public class TemplateTests
{
    [Fact]
    public void ReadsOldTemplateFormat()
    {
        const string json = """
            [
                [ { "stat": "Атака", "operator": "≥", "count": "2" },
                  { "stat": "Мой стат", "operator": "=", "count": "1" } ],
                [ { "stat": "Дух", "operator": "≤", "count": "0" } ]
            ]
            """;

        var groups = TemplateSerializer.Deserialize(json);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Conditions[0].Count);
        Assert.Equal("Мой стат", groups[0].Conditions[1].Stat);
        Assert.Equal(CompareOp.Equal, groups[0].Conditions[1].Operator);
        Assert.Equal(CompareOp.AtMost, groups[1].Conditions[0].Operator);
        Assert.Equal(StatUnit.Any, groups[1].Conditions[0].Unit);
    }

    [Fact]
    public void RoundTrips()
    {
        var groups = new List<ConditionGroup>
        {
            new() { Conditions = [new() { Stat = "Снижен. урона", Unit = StatUnit.Percent, Operator = CompareOp.Equal, Count = 3 }] },
        };

        var json = TemplateSerializer.Serialize(groups);
        var back = TemplateSerializer.Deserialize(json);

        Assert.Contains("Снижен. урона", json);
        var c = Assert.Single(Assert.Single(back).Conditions);
        Assert.Equal((StatUnit.Percent, CompareOp.Equal, 3), (c.Unit, c.Operator, c.Count));
    }
}
