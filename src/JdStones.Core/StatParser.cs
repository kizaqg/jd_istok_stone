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

    // Максимум из колонки «Развитие»: «0.121%/0.886%» или «5.98/27.40».
    [GeneratedRegex(@"/\s*(\d+(?:[.,]\d+)?)")]
    private static partial Regex MaxRegex();

    /// <summary>Собирает слова OCR в строки по вертикальной координате.</summary>
    public static IReadOnlyList<string> GroupIntoRows(IEnumerable<OcrWord> words) =>
        BuildRows(words).Select(r => string.Join(" ", r.Words.OrderBy(w => w.X).Select(w => w.Text))).ToList();

    private static List<(double CenterY, double Height, List<OcrWord> Words)> BuildRows(IEnumerable<OcrWord> words)
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
        return rows;
    }

    /// <summary>
    /// Разбирает строки вида «Название значение[%] [развитие/максимум[%]]». Строки без известного
    /// стата пропускаются. Проценты определяются не только по «%» после значения (OCR его иногда
    /// теряет или читает как «0»), но и по колонке «Развитие» и по величине значения.
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
            var number = m.Groups[1].Value;
            var value = ParseNumber(number);
            result.Add(new StatLine(stat, value, IsPercent(stat, row[m.Index..], number, value), row));
        }
        return result;
    }

    private static bool IsPercent(string stat, string tail, string number, double value)
    {
        if (StatCatalog.Kinds.TryGetValue(stat, out var kind))
        {
            switch (kind)
            {
                case StatKind.Percent:
                    return true;
                case StatKind.Flat:
                    return false;
            }

            // «Снижен. урона»: процент — меньше 1 или 3+ знака после точки; иначе число.
            if (HasPercentSign(tail, number)) return true;
            if (value < StatCatalog.PercentBelow || Decimals(number) >= StatCatalog.PercentDecimals) return true;
            var max = MaxRegex().Match(tail);
            return max.Success && ParseNumber(max.Groups[1].Value) < StatCatalog.PercentMaxBelow;
        }

        // Свой стат, которого нет в справочнике: только по знаку «%» в строке.
        return tail.Contains('%');
    }

    // «%» сразу после значения (если OCR его не потерял).
    private static bool HasPercentSign(string tail, string number)
    {
        var rest = tail[(tail.IndexOf(number, StringComparison.Ordinal) + number.Length)..].TrimStart();
        return rest.StartsWith('%');
    }

    private static int Decimals(string number)
    {
        var dot = number.IndexOfAny(['.', ',']);
        return dot < 0 ? 0 : number.Length - dot - 1;
    }

    /// <summary>
    /// Склеивает строки из двух чтений одной картинки: названия — из русского OCR, числа — из
    /// английского (он гораздо надёжнее читает цифры, точки и «%»). Строки сопоставляются по высоте.
    /// Если английский ничего не нашёл в строке — она остаётся как прочитал русский.
    /// </summary>
    public static IReadOnlyList<string> MergeRows(IEnumerable<OcrWord> nameWords, IEnumerable<OcrWord> numberWords)
    {
        var numbers = numberWords.Where(w => w.Text.Any(char.IsDigit)).ToList();
        var result = new List<string>();
        foreach (var row in BuildRows(nameWords))
        {
            var ordered = row.Words.OrderBy(w => w.X).ToList();
            var inRow = numbers
                .Where(n => Math.Abs(n.Y + n.Height / 2 - row.CenterY) <= Math.Max(row.Height, n.Height) * 0.6)
                .OrderBy(n => n.X)
                .ToList();
            if (inRow.Count == 0)
            {
                result.Add(string.Join(" ", ordered.Select(w => w.Text)));
                continue;
            }
            // Название — русские слова левее первого числа (и без цифр).
            var firstNumberX = inRow[0].X;
            var name = ordered.Where(w => w.X + w.Width / 2 < firstNumberX && !w.Text.Any(char.IsDigit)).Select(w => w.Text);
            result.Add(string.Join(" ", name.Concat(inRow.Select(n => n.Text))));
        }
        return result;
    }

    private static double ParseNumber(string text) =>
        double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture);
}
