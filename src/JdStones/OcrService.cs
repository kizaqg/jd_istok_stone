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

/// <summary>Распознавание текста встроенным в Windows OCR (без Tesseract и прочих установок).</summary>
internal sealed class OcrService
{
    public const int DefaultScale = 3;
    private readonly OcrEngine _engine;

    private OcrService(OcrEngine engine) => _engine = engine;

    public static OcrService? TryCreate()
    {
        var ru = new Language("ru");
        if (!OcrEngine.IsLanguageSupported(ru)) return null;
        var engine = OcrEngine.TryCreateFromLanguage(ru);
        return engine == null ? null : new OcrService(engine);
    }

    public const string MissingLanguageHelp =
        "В Windows не установлено распознавание текста для русского языка.\n\n" +
        "Как добавить (один раз):\n" +
        "  Параметры → Время и язык → Язык и регион → Добавить язык → Русский\n" +
        "  (достаточно компонента «Оптическое распознавание символов»).\n\n" +
        "Или в PowerShell от администратора:\n" +
        "  Add-WindowsCapability -Online -Name \"Language.OCR~~~ru-RU~0.0.1.0\"\n\n" +
        "После установки перезапустите программу.";

    /// <returns>Слова с координатами в пикселях исходной картинки.</returns>
    /// <param name="scale">Во сколько раз увеличить картинку перед распознаванием.
    /// Разное увеличение даёт независимые чтения — используется для перепроверки находки.</param>
    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(Bitmap source, int scale = DefaultScale)
    {
        while (scale > 1 && Math.Max(source.Width, source.Height) * scale > OcrEngine.MaxImageDimension) scale--;

        using var prepared = Prepare(source, scale);
        using var softwareBitmap = ToSoftwareBitmap(prepared);
        var result = await _engine.RecognizeAsync(softwareBitmap);

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

    public async Task<string> RecognizeTextAsync(Bitmap source) =>
        string.Join("\n", StatParser.GroupIntoRows(await RecognizeAsync(source)));

    /// <summary>Увеличение + ч/б с инверсией: светлый игровой текст на тёмном фоне → тёмный на светлом.</summary>
    private static Bitmap Prepare(Bitmap source, int scale)
    {
        var result = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format32bppArgb);
        var matrix = new ColorMatrix(
        [
            [-0.299f, -0.299f, -0.299f, 0, 0],
            [-0.587f, -0.587f, -0.587f, 0, 0],
            [-0.114f, -0.114f, -0.114f, 0, 0],
            [0, 0, 0, 1, 0],
            [1, 1, 1, 0, 1],
        ]);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
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
