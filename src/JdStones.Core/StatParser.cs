using System.Globalization;
using System.Text.RegularExpressions;

namespace JdStones.Core;

/// <summary>Одна строка стата камня: «Снижен. урона 0.121%».</summary>
public sealed record StatLine(string Stat, double Value, bool IsPercent, string RawText)
{
    public override string ToString() =>
        $"{Stat} {Value.ToString("0.###", CultureInfo.InvariantCulture)}{(IsPercent ? "%" : "")}";
}

/// <summary>Слово, распознанное OCR, с координатами в пикселях исходной картинки.</summary>
public readonly record struct OcrWord(string Text, double X, double Y, double Width, double Height);

public static partial class StatParser
{
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(%)?")]
    private static partial Regex NumberRegex();

    /// <summary>Собирает слова OCR в строки по вертикальной координате.</summary>
    public static IReadOnlyList<string> GroupIntoRows(IEnumerable<OcrWord> words)
    {
        var rows = new List<(double CenterY, double Height, List<OcrWord> Words)>();
        foreach (var w in words.OrderBy(w => w.Y + w.Height / 2))
        {
            var cy = w.Y + w.Height / 2;
            if (rows.Count > 0)
            {
                var last = rows[^1];
                if (Math.Abs(cy - last.CenterY) <= Math.Max(last.Height, w.Height) * 0.5)
                {
                    last.Words.Add(w);
                    continue;
                }
            }
            rows.Add((cy, w.Height, [w]));
        }
        return rows.Select(r => string.Join(" ", r.Words.OrderBy(w => w.X).Select(w => w.Text))).ToList();
    }

    /// <summary>
    /// Разбирает строки вида «Название значение[%] ...». Всё, что правее первого числа
    /// (например, колонка «Развитие»), игнорируется. Строки без известного стата пропускаются.
    /// </summary>
    public static IReadOnlyList<StatLine> Parse(IEnumerable<string> rows, StatMatcher matcher)
    {
        var result = new List<StatLine>();
        foreach (var row in rows)
        {
            var m = NumberRegex().Match(row);
            if (!m.Success) continue;
            var stat = matcher.Match(row[..m.Index]);
            if (stat == null) continue;
            var value = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            result.Add(new StatLine(stat, value, m.Groups[2].Success, row));
        }
        return result;
    }
}
