using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoKADN.Proyectos.Photos;

/// <summary>Busca las fotos de una carpeta y las deja listas para el Excel: giradas según su orientación, reducidas y en JPEG.</summary>
public static class PhotoLoader
{
    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Lado más largo (px) con el que se guardan las fotos en el Excel: nítidas al imprimir y livianas.</summary>
    public const int MaxSide = 1600;
    public const int JpegQuality = 85;

    /// <summary>Fotos de la carpeta (sin entrar a subcarpetas), ordenadas por nombre con los números en orden natural.</summary>
    public static List<string> ListPhotos(string folder)
    {
        if (!Directory.Exists(folder)) return new List<string>();
        return Directory.GetFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), new NaturalComparer())
            .ToList();
    }

    /// <summary>Miniatura ya girada según su orientación, para mostrar en la lista.</summary>
    public static BitmapSource Thumbnail(string path, int width = 180)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();

        int orientation;
        using (FileStream stream = File.OpenRead(path))
            orientation = ReadOrientation(BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0]);
        return ApplyOrientation(image, orientation);
    }

    public static PreparedPhoto Prepare(string path, int maxSide = MaxSide)
    {
        int width, height, orientation;
        using (FileStream stream = File.OpenRead(path))
        {
            BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            orientation = ReadOrientation(frame);
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // no deja el archivo bloqueado
        if (Math.Max(width, height) > maxSide)
        {
            if (width >= height) image.DecodePixelWidth = maxSide; else image.DecodePixelHeight = maxSide;
        }
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();

        BitmapSource oriented = ApplyOrientation(image, orientation);
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(oriented));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new PreparedPhoto(Path.GetFileName(path), output.ToArray(), oriented.PixelWidth, oriented.PixelHeight);
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
                return Convert.ToInt32(metadata.GetQuery("/app1/ifd/{ushort=274}"));
        }
        catch { /* formato sin metadatos EXIF */ }
        return 1;
    }

    // EXIF: 2 espejo horizontal · 3 giro 180 · 4 espejo vertical · 5 transpuesta · 6 giro 90 · 7 transversa · 8 giro 270
    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        BitmapSource result = source;
        if (orientation is 2 or 5 or 7) result = Transform(result, new ScaleTransform(-1, 1));
        if (orientation == 4) result = Transform(result, new ScaleTransform(1, -1));
        if (orientation == 3) result = Transform(result, new RotateTransform(180));
        if (orientation is 6 or 7) result = Transform(result, new RotateTransform(90));
        if (orientation is 5 or 8) result = Transform(result, new RotateTransform(270));
        return result;
    }

    private static BitmapSource Transform(BitmapSource source, System.Windows.Media.Transform transform)
    {
        var transformed = new TransformedBitmap(source, transform);
        transformed.Freeze();
        return transformed;
    }

    /// <summary>Orden de nombres con los números comparados como números (foto2 antes que foto10).</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string a = x.Substring(si, i - si).TrimStart('0'), b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
                    int number = string.CompareOrdinal(a, b);
                    if (number != 0) return number;
                }
                else
                {
                    int letters = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (letters != 0) return letters;
                    i++;
                    j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
