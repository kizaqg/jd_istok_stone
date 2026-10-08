namespace JdStones.Core;

public enum StatKind
{
    Flat,
    Percent,
    /// <summary>Бывает и так, и так (только «Снижен. урона»): решаем по величине значения.</summary>
    Both,
}

/// <summary>Названия статов камней истока так, как они пишутся в игре.</summary>
public static class StatCatalog
{
    public static readonly IReadOnlyList<string> Predefined =
    [
        "Здоровье",
        "Атака",
        "Меткость",
        "Метк. нав.",
        "Сопр. Малеусу",
        "Сопр. сонл.",
        "Дух",
        "Снижен. урона",
        "Укл. от нав.",
        "Защита/сниж.ур.",
        "Сопр. параличу",
        "Крит. атака",
        "Сопр. Феору",
        "Воля",
        "Усил. атаки",
        "Защита",
        "Защита/крит.ат.",
        "Защита/крит.ур.",
        "Сопр. слаб.",
        "Крит. урон",
        "Сопр. оглуш.",
        "Сопр. одерж.",
        "Увел. духа",
        "Сопр. Эйдосу",
        "Урон Эйдосу",
        "Урон Малеусу",
        "Урон Феору",
        "Увел. здоровья",
        "Усил. защиты",
    ];

    /// <summary>
    /// Тип значения каждого стата в игре. OCR часто читает «%» как «0» (0.440% → «0.4400»),
    /// поэтому для известных статов тип берётся отсюда, а не из распознанного текста.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, StatKind> Kinds = new Dictionary<string, StatKind>
    {
        ["Здоровье"] = StatKind.Flat,
        ["Атака"] = StatKind.Flat,
        ["Меткость"] = StatKind.Flat,
        ["Метк. нав."] = StatKind.Flat,
        ["Сопр. Малеусу"] = StatKind.Percent,
        ["Сопр. сонл."] = StatKind.Flat,
        ["Дух"] = StatKind.Flat,
        ["Снижен. урона"] = StatKind.Both,
        ["Укл. от нав."] = StatKind.Flat,
        ["Защита/сниж.ур."] = StatKind.Percent,
        ["Сопр. параличу"] = StatKind.Flat,
        ["Крит. атака"] = StatKind.Percent,
        ["Сопр. Феору"] = StatKind.Percent,
        ["Воля"] = StatKind.Flat,
        ["Усил. атаки"] = StatKind.Percent,
        ["Защита"] = StatKind.Flat,
        ["Защита/крит.ат."] = StatKind.Percent,
        ["Защита/крит.ур."] = StatKind.Percent,
        ["Сопр. слаб."] = StatKind.Flat,
        ["Крит. урон"] = StatKind.Percent,
        ["Сопр. оглуш."] = StatKind.Flat,
        ["Сопр. одерж."] = StatKind.Flat,
        ["Увел. духа"] = StatKind.Percent,
        ["Сопр. Эйдосу"] = StatKind.Percent,
        ["Урон Эйдосу"] = StatKind.Percent,
        ["Урон Малеусу"] = StatKind.Percent,
        ["Урон Феору"] = StatKind.Percent,
        ["Увел. здоровья"] = StatKind.Percent,
        ["Усил. защиты"] = StatKind.Percent,
    };

    /// <summary>
    /// Для «Снижен. урона»: проценты всегда меньше 1 (0.048–0.886%) и пишутся с тремя знаками после
    /// точки («0.240%»; если OCR прочитал «%» как «0» — знаков ещё больше: «0.24070»).
    /// Числа — от 1 и с двумя знаками («5.98», «50.70»).
    /// </summary>
    public const double PercentBelow = 1.0;
    public const int PercentDecimals = 3;
    /// <summary>Максимум в колонке «Развитие» («/0.886» у процента, «/27.40» у числа).</summary>
    public const double PercentMaxBelow = 5.0;
}
