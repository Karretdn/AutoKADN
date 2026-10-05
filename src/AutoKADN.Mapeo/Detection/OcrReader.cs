using System.IO;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AutoKADN.Mapeo.Detection;

// Respaldo para PDF cuyo texto está convertido a curvas (no hay letras que leer): se dibuja la página y se
// reconoce con el OCR que trae Windows. Devuelve palabras con su caja en puntos PDF.
public static class OcrReader
{
    private const double PixelsPerPoint = 3.0;

    public static bool IsAvailable => CreateEngine() is not null;

    private static OcrEngine? CreateEngine()
    {
        try
        {
            foreach (string tag in new[] { "es-CO", "es-ES", "es-MX", "es" })
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language)) return OcrEngine.TryCreateFromLanguage(language);
            }
        }
        catch { }
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public static async Task<List<Token>> ReadAsync(string pdfPath, int pageNumber)
    {
        var tokens = new List<Token>();
        OcrEngine? engine = CreateEngine();
        if (engine is null) return tokens;

        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
        using PdfPage page = document.GetPage((uint)(pageNumber - 1));
        const double PointsPerDip = 72.0 / 96.0;
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Round(page.Size.Width * PointsPerDip * PixelsPerPoint),
            DestinationHeight = (uint)Math.Round(page.Size.Height * PointsPerDip * PixelsPerPoint),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        OcrResult result = await engine.RecognizeAsync(bitmap);

        foreach (OcrLine line in result.Lines)
        {
            foreach (OcrWord word in line.Words)
            {
                var r = word.BoundingRect;
                var box = new RectD(r.X / PixelsPerPoint, r.Y / PixelsPerPoint, r.Width / PixelsPerPoint, r.Height / PixelsPerPoint);
                tokens.Add(new Token { Text = word.Text, Box = box, Baseline = box.Bottom - box.H * 0.2, FontSize = Math.Max(box.H * 0.85, 4) });
            }
        }
        return tokens;
    }
}
