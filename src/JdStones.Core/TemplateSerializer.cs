using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace JdStones.Core;

/// <summary>Условие в JSON. Формат совместим со старыми шаблонами (stat/operator/count).</summary>
public sealed class ConditionDto
{
    [JsonPropertyName("stat")] public string Stat { get; set; } = "";
    [JsonPropertyName("operator")] public string Operator { get; set; } = "≥";
    [JsonPropertyName("count")] public JsonElement Count { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
}

public static class TemplateSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Serialize(IEnumerable<ConditionGroup> groups) =>
        JsonSerializer.Serialize(ToDto(groups), Options);

    public static List<ConditionGroup> Deserialize(string json) =>
        FromDto(JsonSerializer.Deserialize<List<List<ConditionDto>>>(json, Options) ?? []);

    public static List<List<ConditionDto>> ToDto(IEnumerable<ConditionGroup> groups) =>
        groups.Select(g => g.Conditions.Select(c => new ConditionDto
        {
            Stat = c.Stat,
            Operator = OperatorToText(c.Operator),
            Count = JsonSerializer.SerializeToElement(c.Count),
            Unit = c.Unit switch { StatUnit.Percent => "percent", StatUnit.Flat => "flat", _ => "any" },
        }).ToList()).ToList();

    public static List<ConditionGroup> FromDto(IEnumerable<List<ConditionDto>> dto) =>
        dto.Select(g => new ConditionGroup
        {
            Conditions = (g ?? []).Select(c => new StatCondition
            {
                Stat = c.Stat ?? "",
                Operator = TextToOperator(c.Operator),
                Count = ParseCount(c.Count),
                Unit = c.Unit switch { "percent" => StatUnit.Percent, "flat" => StatUnit.Flat, _ => StatUnit.Any },
            }).ToList(),
        }).ToList();

    public static string OperatorToText(CompareOp op) => op switch
    {
        CompareOp.AtMost => "≤",
        CompareOp.Equal => "=",
        _ => "≥",
    };

    public static CompareOp TextToOperator(string? text) => text switch
    {
        "≤" or "<=" => CompareOp.AtMost,
        "=" or "==" => CompareOp.Equal,
        _ => CompareOp.AtLeast,
    };

    // Старая программа сохраняла количество строкой ("2"), новая — числом.
    private static int ParseCount(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var n) => n,
        JsonValueKind.String when int.TryParse(e.GetString(), out var s) => s,
        _ => 1,
    };
}
