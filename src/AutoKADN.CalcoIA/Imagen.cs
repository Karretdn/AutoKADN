using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoKADN.CalcoIA;

// La hoja escaneada en memoria (BGR, 3 bytes por píxel) y lo que la IA necesita de ella: ver una zona con su cuadrícula
// de coordenadas y medir líneas por perfiles de píxeles (el método que funcionó en las pruebas, ver CALCO-IA.md §4).
public sealed class Imagen
{
    private readonly byte[] _bgr;
    private readonly BitmapSource _fuente;

    private Imagen(BitmapSource fuente, byte[] bgr)
    {
        _fuente = fuente;
        _bgr = bgr;
        Ancho = fuente.PixelWidth;
        Alto = fuente.PixelHeight;
    }

    public int Ancho { get; }
    public int Alto { get; }

    public static Imagen Cargar(string archivo)
    {
        BitmapFrame marco;
        using (var stream = File.OpenRead(archivo))
            marco = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var bgr = new FormatConvertedBitmap(marco, PixelFormats.Bgr24, null, 0);
        bgr.Freeze();
        var datos = new byte[bgr.PixelWidth * bgr.PixelHeight * 3];
        bgr.CopyPixels(datos, bgr.PixelWidth * 3, 0);
        return new Imagen(bgr, datos);
    }

    // Una zona de la hoja como PNG, reducida para que el lado largo no pase de maxLado, con una cuadrícula rotulada en
    // coordenadas de la hoja completa (para ubicar cosas; las medidas exactas salen de Perfil).
    public byte[] Zona(Rect zona, int maxLado, out double escala)
    {
        zona = Recortar(zona);
        escala = Math.Min(1.0, (double)maxLado / Math.Max(zona.Ancho, zona.Alto));
        int ancho = Math.Max(1, (int)Math.Round(zona.Ancho * escala)), alto = Math.Max(1, (int)Math.Round(zona.Alto * escala));

        BitmapSource corte = new CroppedBitmap(_fuente, new Int32Rect(zona.X, zona.Y, zona.Ancho, zona.Alto));
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(corte, new System.Windows.Rect(0, 0, ancho, alto));
            double paso = PasoCuadricula(Math.Max(zona.Ancho, zona.Alto));
            var lapiz = new Pen(new SolidColorBrush(Color.FromArgb(110, 230, 30, 30)), 1);
            var fondo = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
            var tinta = new SolidColorBrush(Color.FromRgb(200, 0, 0));
            for (double x = Math.Ceiling(zona.X / paso) * paso; x < zona.X1; x += paso)
            {
                double px = (x - zona.X) * escala;
                dc.DrawLine(lapiz, new Point(px, 0), new Point(px, alto));
                Rotulo(dc, x.ToString("0", CultureInfo.InvariantCulture), px + 2, 2, fondo, tinta);
            }
            for (double y = Math.Ceiling(zona.Y / paso) * paso; y < zona.Y1; y += paso)
            {
                double py = (y - zona.Y) * escala;
                dc.DrawLine(lapiz, new Point(0, py), new Point(ancho, py));
                Rotulo(dc, y.ToString("0", CultureInfo.InvariantCulture), 2, py + 2, fondo, tinta);
            }
        }
        return Png(visual, ancho, alto);
    }

    // Un pedazo de la hoja tal cual (para dibujar encima).
    public BitmapSource Corte(Rect zona)
    {
        zona = Recortar(zona);
        return new CroppedBitmap(_fuente, new Int32Rect(zona.X, zona.Y, zona.Ancho, zona.Alto));
    }

    private static double PasoCuadricula(int lado) => lado > 2400 ? 200 : lado > 1200 ? 100 : lado > 500 ? 50 : 25;

    private static void Rotulo(DrawingContext dc, string texto, double x, double y, Brush fondo, Brush tinta)
    {
        var formato = new FormattedText(texto, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 12, tinta, 1.0);
        dc.DrawRectangle(fondo, null, new System.Windows.Rect(x - 1, y - 1, formato.Width + 2, formato.Height + 2));
        dc.DrawText(formato, new Point(x, y));
    }

    public static byte[] Png(Visual visual, int ancho, int alto)
    {
        var mapa = new RenderTargetBitmap(ancho, alto, 96, 96, PixelFormats.Pbgra32);
        mapa.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(mapa));
        using var memoria = new MemoryStream();
        encoder.Save(memoria);
        return memoria.ToArray();
    }

    public Rect Recortar(Rect zona)
    {
        int x0 = Math.Clamp(zona.X, 0, Ancho - 1), y0 = Math.Clamp(zona.Y, 0, Alto - 1);
        int x1 = Math.Clamp(zona.X1, x0 + 1, Ancho), y1 = Math.Clamp(zona.Y1, y0 + 1, Alto);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // Perfil de una zona: por cada fila (o columna) la fracción de píxeles que son tinta; las seguidas (≤ 3 px) que pasan
    // la fracción pedida forman una línea y se informa su centro ponderado. color: oscuro (lápiz o lapicero), amarillo
    // (resaltador) o azul (tinta azul, la tubería existente punteada).
    public string Perfil(bool filas, Rect zona, double fraccion, string color, int umbral)
    {
        zona = Recortar(zona);
        Func<int, int, bool> tinta = color switch
        {
            "amarillo" => (x, y) => { int k = (y * Ancho + x) * 3; return _bgr[k + 2] > 140 && _bgr[k + 1] > 140 && _bgr[k] < _bgr[k + 1] - 22; },
            "azul" => (x, y) => { int k = (y * Ancho + x) * 3; return _bgr[k] > _bgr[k + 2] + 35 && _bgr[k] < 200; },
            _ => (x, y) => { int k = (y * Ancho + x) * 3; return (_bgr[k + 2] * 299 + _bgr[k + 1] * 587 + _bgr[k] * 114) / 1000 < umbral; },
        };

        var hallados = new List<(int pos, double f)>();
        if (filas)
        {
            for (int y = zona.Y; y < zona.Y1; y++)
            {
                int c = 0;
                for (int x = zona.X; x < zona.X1; x++) if (tinta(x, y)) c++;
                double f = (double)c / zona.Ancho;
                if (f >= fraccion) hallados.Add((y, f));
            }
        }
        else
        {
            for (int x = zona.X; x < zona.X1; x++)
            {
                int c = 0;
                for (int y = zona.Y; y < zona.Y1; y++) if (tinta(x, y)) c++;
                double f = (double)c / zona.Alto;
                if (f >= fraccion) hallados.Add((x, f));
            }
        }

        var texto = new StringBuilder();
        texto.AppendLine($"{(filas ? "Líneas horizontales (filas y)" : "Líneas verticales (columnas x)")} en x {zona.X}-{zona.X1}, y {zona.Y}-{zona.Y1}, color {color}, fracción ≥ {fraccion.ToString(CultureInfo.InvariantCulture)}:");
        int i = 0, lineas = 0;
        while (i < hallados.Count)
        {
            int j = i;
            while (j + 1 < hallados.Count && hallados[j + 1].pos - hallados[j].pos <= 3) j++;
            var grupo = hallados.GetRange(i, j - i + 1);
            double centro = grupo.Sum(h => h.pos * h.f) / grupo.Sum(h => h.f);
            texto.AppendLine(FormattableString.Invariant($"  {grupo[0].pos}-{grupo[^1].pos}  centro={centro:0.0}  máx={grupo.Max(h => h.f):0.00}"));
            lineas++;
            i = j + 1;
        }
        if (lineas == 0) texto.AppendLine("  (ninguna; pruebe con una fracción menor, una franja más angosta o umbral 175 para lápiz claro)");
        return texto.ToString();
    }
}
