namespace JdStones.Core;

/// <summary>Сопоставляет распознанное (возможно, с ошибками) название с известными статами.</summary>
public sealed class StatMatcher
{
    private readonly List<(string Name, string Norm)> _candidates = [];

    public StatMatcher(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var norm = TextNormalizer.Normalize(name);
            if (norm.Length > 0 && _candidates.All(c => c.Norm != norm)) _candidates.Add((name, norm));
        }
    }

    public string? Match(string rawName)
    {
        var norm = TextNormalizer.Normalize(rawName);
        if (norm.Length < 2) return null;

        string? best = null;
        var bestDistance = int.MaxValue;
        var bestNormLength = 0;
        var tie = false;
        foreach (var (name, candidate) in _candidates)
        {
            var d = TextNormalizer.Levenshtein(norm, candidate);
            if (d < bestDistance)
            {
                best = name;
                bestDistance = d;
                bestNormLength = candidate.Length;
                tie = false;
            }
            else if (d == bestDistance)
            {
                tie = true;
            }
        }
        // Текст с ошибкой одинаково похож на два стата (например, «крит.ут» — на «крит.ат» и
        // «крит.ур»): угадывать нельзя, считаем строку нераспознанной.
        if (tie && bestDistance > 0) return null;
        return best != null && bestDistance <= TextNormalizer.AllowedErrors(bestNormLength) ? best : null;
    }

    public static bool SameStat(string a, string b) =>
        TextNormalizer.Normalize(a) == TextNormalizer.Normalize(b);
}
