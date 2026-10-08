using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using JdStones.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrWord = JdStones.Core.OcrWord;

namespace JdStones;

/// <summary>Как подготовить картинку перед распознаванием.</summary>
internal enum OcrFilter
{
    /// <summary>Серый с инверсией: светлый текст на тёмном фоне → тёмный на светлом.</summary>
    GrayInvert,
    /// <summary>Чёрно-белый с порогом (Оцу): яркий текст → чисто чёрный, фон → чисто белый.</summary>
    BlackWhite,
}

/// <summary>
/// Распознавание текста встроенным в Windows OCR (без Tesseract и прочих установок).
/// Названия читает русский движок, числа — английский (он заметно надёжнее с цифрами, точкой и «%»).
/// </summary>
internal sealed class OcrService
{
    public const int DefaultScale = 3;
    private readonly OcrEngine _engine;
    private readonly OcrEngine? _digits;

    private OcrService(OcrEngine engine, OcrEngine? digits)
    {
        _engine = engine;
        _digits = digits;
    }

    /// <summary>Обработка картинки (настройка пользователя).</summary>
    public OcrFilter Filter { get; set; } = OcrFilter.GrayInvert;

    /// <summary>Есть ли английский движок для чисел.</summary>
    public bool HasDigitsEngine => _digits != null;

    public static OcrService? TryCreate()
    {
        var ru = new Language("ru");
        if (!OcrEngine.IsLanguageSupported(ru)) return null;
        var engine = OcrEngine.TryCreateFromLanguage(ru);
        if (engine == null) return null;
        OcrEngine? digits = null;
        foreach (var tag in new[] { "en-US", "en-GB", "en" })
        {
            var lang = new Language(tag);
            if (!OcrEngine.IsLanguageSupported(lang)) continue;
            digits = OcrEngine.TryCreateFromLanguage(lang);
            if (digits != null) break;
        }
        return new OcrService(engine, digits);
    }

    public const string MissingLanguageHelp =
        "В Windows не установлено распознавание текста для русского языка.\n\n" +
        "Как добавить (один раз):\n" +
        "  Параметры → Время и язык → Язык и регион → Добавить язык → Русский\n" +
        "  (достаточно компонента «Оптическое распознавание символов»).\n\n" +
        "Или в PowerShell от администратора:\n" +
        "  Add-WindowsCapability -Online -Name \"Language.OCR~~~ru-RU~0.0.1.0\"\n\n" +
        "После установки перезапустите программу.";

    public const string MissingDigitsHelp =
        "Для более точного чтения чисел и «%» добавьте английское распознавание (один раз), " +
        "в PowerShell от администратора:\n" +
        "  Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"";

    /// <summary>
    /// Строки статов: названия от русского движка, числа от английского (если он есть).
    /// </summary>
    /// <param name="scale">Во сколько раз увеличить картинку. Разное увеличение даёт независимые
    /// чтения — используется для перепроверки находки.</param>
    public async Task<IReadOnlyList<string>> RecognizeRowsAsync(Bitmap source, int scale = DefaultScale, OcrFilter? filter = null)
    {
        scale = FitScale(source, scale);
        using var prepared = Prepare(source, scale, filter ?? Filter);
        using var softwareBitmap = ToSoftwareBitmap(prepared);
        var names = Words(await _engine.RecognizeAsync(softwareBitmap), scale);
        if (_digits == null) return StatParser.GroupIntoRows(names);
        var numbers = Words(await _digits.RecognizeAsync(softwareBitmap), scale);
        return StatParser.MergeRows(names, numbers);
    }

    /// <summary>Просто текст (русский движок) — для окна предупреждения.</summary>
    public async Task<string> RecognizeTextAsync(Bitmap source)
    {
        var scale = FitScale(source, DefaultScale);
        using var prepared = Prepare(source, scale, Filter);
        using var softwareBitmap = ToSoftwareBitmap(prepared);
        return string.Join("\n", StatParser.GroupIntoRows(Words(await _engine.RecognizeAsync(softwareBitmap), scale)));
    }

    /// <summary>Картинка после обработки — показать в «Проверке распознавания».</summary>
    public static Bitmap Preview(Bitmap source, OcrFilter filter) => Prepare(source, FitScale(source, DefaultScale), filter);

    private static int FitScale(Bitmap source, int scale)
    {
        while (scale > 1 && Math.Max(source.Width, source.Height) * scale > OcrEngine.MaxImageDimension) scale--;
        return scale;
    }

    private static List<OcrWord> Words(OcrResult result, int scale)
    {
        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
        {
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWord(w.Text, r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale));
            }
        }
        return words;
    }

    private static Bitmap Prepare(Bitmap source, int scale, OcrFilter filter)
    {
        var result = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }

        var data = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);

            if (filter == OcrFilter.GrayInvert)
            {
                for (var i = 0; i < px.Length; i += 4)
                {
                    var gray = (byte)(255 - (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000);
                    px[i] = px[i + 1] = px[i + 2] = gray;
                    px[i + 3] = 255;
                }
            }
            else
            {
                // Яркость = максимум каналов: и бежевые названия, и бирюзовые значения на тёмно-синем фоне
                // получаются яркими. Порог выбираем автоматически (метод Оцу) по гистограмме.
                var hist = new int[256];
                for (var i = 0; i < px.Length; i += 4) hist[Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]))]++;
                var threshold = OtsuThreshold(hist, px.Length / 4);
                for (var i = 0; i < px.Length; i += 4)
                {
                    var bright = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                    var v = (byte)(bright > threshold ? 0 : 255);
                    px[i] = px[i + 1] = px[i + 2] = v;
                    px[i + 3] = 255;
                }
            }
            Marshal.Copy(px, 0, data.Scan0, px.Length);
        }
        finally
        {
            result.UnlockBits(data);
        }
        return result;
    }

    private static int OtsuThreshold(int[] hist, int total)
    {
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1;
        int wB = 0, threshold = 128;
        for (var t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height,
                BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
