using System.Text;

namespace JdStones.Core;

/// <summary>Приводит распознанный текст к виду, удобному для сравнения с названиями статов.</summary>
public static class TextNormalizer
{
    // Латинские буквы, которые OCR часто выдаёт вместо похожих кириллических.
    private static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['A'] = 'а', ['a'] = 'а', ['B'] = 'в', ['b'] = 'ь', ['C'] = 'с', ['c'] = 'с',
        ['E'] = 'е', ['e'] = 'е', ['H'] = 'н', ['K'] = 'к', ['k'] = 'к', ['M'] = 'м',
        ['O'] = 'о', ['o'] = 'о', ['P'] = 'р', ['p'] = 'р', ['T'] = 'т', ['X'] = 'х',
        ['x'] = 'х', ['Y'] = 'у', ['y'] = 'у', ['u'] = 'и', ['n'] = 'п', ['r'] = 'г',
        ['3'] = 'з', ['0'] = 'о', ['6'] = 'б',
    };

    /// <summary>Только строчные кириллические буквы: без пробелов, точек, слэшей и т.п.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var ch = Lookalikes.TryGetValue(raw, out var mapped) ? mapped : raw;
            ch = char.ToLowerInvariant(ch);
            if (ch == 'ё') ch = 'е';
            if (char.IsLetter(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Сколько опечаток OCR прощаем для слова такой длины.</summary>
    public static int AllowedErrors(int length) => Math.Max(1, length / 5);

    /// <summary>Есть ли в тексте подстрока, похожая на <paramref name="needle"/> (оба нормализованы).</summary>
    public static bool ContainsFuzzy(string haystack, string needle)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal)) return true;
        var allowed = AllowedErrors(needle.Length);
        for (var len = needle.Length - allowed; len <= needle.Length + allowed; len++)
        {
            if (len <= 0 || len > haystack.Length) continue;
            for (var start = 0; start + len <= haystack.Length; start++)
            {
                if (Levenshtein(haystack.Substring(start, len), needle) <= allowed) return true;
            }
        }
        return false;
    }
}
