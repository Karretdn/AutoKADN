using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Content;

namespace AutoKADN.Mapeo.Detection;

// Rectángulo en puntos PDF con el origen ARRIBA a la izquierda.
public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    public bool ContainsPoint(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool Intersects(RectD o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public static RectD Union(RectD a, RectD b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new RectD(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}

// Segmento horizontal (Pos = y, A..B = x) o vertical (Pos = x, A..B = y).
public sealed record Seg(double Pos, double A, double B);

// Una palabra (o un tramo de rayas "____") del PDF, con su caja y su línea base.
public sealed class Token
{
    public string Text { get; init; } = "";
    public RectD Box { get; init; }
    public double Baseline { get; init; }
    public double FontSize { get; init; }
    // Tramo de guiones bajos (un espacio para escribir dentro de un párrafo).
    public bool IsBlank { get; init; }
}

// Todo lo que el detector necesita saber de una página: rayas, textos e imágenes.
public sealed class PageLayout
{
    public int Number { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public List<Seg> Horizontals { get; } = new();
    public List<Seg> Verticals { get; } = new();
    public List<Token> Tokens { get; } = new();
    public List<RectD> Images { get; } = new();
    // Dibujos pequeños que no son rayas (letras convertidas a curvas, símbolos como Ø): sirven para saber
    // que una celda NO está vacía aunque no tenga texto que leer.
    public List<RectD> Ink { get; } = new();

    private const double MinLine = 8.0;     // una raya más corta es el trazo de una letra (I, l, T...), no un borde
    private const double ThinLine = 2.5;   // un relleno más fino que esto es una raya, no un recuadro
    private const double FlatTolerance = 0.8;

    public int ReadableWordCount => Tokens.Count(t => !t.IsBlank && t.Text.Any(char.IsLetter));

    public static List<PageLayout> Read(string pdfPath)
    {
        var pages = new List<PageLayout>();
        using PdfDocument document = PdfDocument.Open(pdfPath);
        foreach (Page page in document.GetPages())
        {
            var layout = new PageLayout { Number = page.Number, Width = page.Width, Height = page.Height };
            layout.ReadPaths(page);
            layout.ReadTokens(page);
            foreach (var image in page.GetImages())
            {
                var b = image.Bounds;
                layout.Images.Add(new RectD(b.Left, layout.Height - b.Top, b.Width, b.Height));
            }
            layout.Horizontals.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            layout.Verticals.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            pages.Add(layout);
        }
        return pages;
    }

    // ---- rayas ----

    private void ReadPaths(Page page)
    {
        foreach (PdfPath path in page.Paths)
        {
            // Sin relleno ni trazo es un recorte (clip): no se dibuja.
            if (!path.IsFilled && !path.IsStroked) continue;
            foreach (PdfSubpath sub in path)
            {
                var points = new List<(double X, double Y)>();
                bool curved = false;
                foreach (var command in sub.Commands)
                {
                    switch (command)
                    {
                        case PdfSubpath.Move m: points.Add((m.Location.X, m.Location.Y)); break;
                        case PdfSubpath.Line l:
                            if (points.Count == 0) points.Add((l.From.X, l.From.Y));
                            points.Add((l.To.X, l.To.Y));
                            break;
                        case PdfSubpath.Close: break;
                        default: curved = true; break;
                    }
                }
                if (curved)
                {
                    var bounds = sub.GetBoundingRectangle();
                    if (bounds.HasValue) AddInk(bounds.Value.Left, bounds.Value.Bottom, bounds.Value.Right, bounds.Value.Top);
                    continue;
                }
                if (points.Count < 2) continue;

                double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
                double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
                double w = maxX - minX, h = maxY - minY;
                if (w < 0.001 && h < 0.001) continue;

                if (points.Count == 2)
                {
                    // Raya simple (trazo): solo vale si es horizontal o vertical.
                    if (h <= FlatTolerance) AddHorizontal((minY + maxY) / 2, minX, maxX);
                    else if (w <= FlatTolerance) AddVertical((minX + maxX) / 2, minY, maxY);
                    continue;
                }

                if (h <= ThinLine && w > h * 2) AddHorizontal((minY + maxY) / 2, minX, maxX);
                else if (w <= ThinLine && h > w * 2) AddVertical((minX + maxX) / 2, minY, maxY);
                else if (path.IsStroked && IsAxisAlignedBox(points))
                {
                    // Recuadro dibujado con trazo: sus cuatro lados son rayas.
                    AddHorizontal(minY, minX, maxX); AddHorizontal(maxY, minX, maxX);
                    AddVertical(minX, minY, maxY); AddVertical(maxX, minY, maxY);
                }
                // Un relleno grande (sombreado de un encabezado) no es una raya: se ignora.
            }
        }
    }

    private static bool IsAxisAlignedBox(List<(double X, double Y)> points)
    {
        for (int i = 1; i < points.Count; i++)
        {
            bool flat = Math.Abs(points[i].X - points[i - 1].X) < 0.01 || Math.Abs(points[i].Y - points[i - 1].Y) < 0.01;
            if (!flat) return false;
        }
        return true;
    }

    // PdfPig entrega Y desde abajo; aquí se guarda desde arriba.
    private void AddHorizontal(double pdfY, double x1, double x2)
    {
        if (x2 - x1 < MinLine) { AddInk(x1, pdfY - 0.5, x2, pdfY + 0.5); return; }
        Horizontals.Add(new Seg(Height - pdfY, x1, x2));
    }

    private void AddVertical(double x, double pdfY1, double pdfY2)
    {
        double bottom = Math.Min(pdfY1, pdfY2), top = Math.Max(pdfY1, pdfY2);
        if (top - bottom < MinLine) { AddInk(x - 0.5, bottom, x + 0.5, top); return; }
        Verticals.Add(new Seg(x, Height - top, Height - bottom));
    }

    private void AddInk(double left, double pdfBottom, double right, double pdfTop)
    {
        double w = right - left, h = pdfTop - pdfBottom;
        if (w > 40 || h > 40 || (w < 0.3 && h < 0.3)) return;   // los sombreados y logos grandes no cuentan
        if (h < 2.0) return;                                    // un guion o una rayita decorativa no es contenido
        Ink.Add(new RectD(left, Height - pdfTop, Math.Max(w, 0.1), Math.Max(h, 0.1)));
    }

    // ---- textos ----

    private void ReadTokens(Page page)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrEmpty(l.Value)).ToList();
        // Agrupar por renglón (misma línea base).
        var lines = new List<List<Letter>>();
        foreach (Letter letter in letters.OrderByDescending(l => l.StartBaseLine.Y))
        {
            List<Letter>? line = lines.FirstOrDefault(l => Math.Abs(l[0].StartBaseLine.Y - letter.StartBaseLine.Y) <= 2.0);
            if (line is null) lines.Add(new List<Letter> { letter }); else line.Add(letter);
        }

        foreach (var line in lines)
        {
            line.Sort((a, b) => a.StartBaseLine.X.CompareTo(b.StartBaseLine.X));
            var current = new List<Letter>();
            bool currentUnderscore = false;
            void Flush()
            {
                if (current.Count > 0) AddToken(current, currentUnderscore);
                current = new List<Letter>();
            }

            Letter? previous = null;
            foreach (Letter letter in line)
            {
                string value = letter.Value;
                if (string.IsNullOrWhiteSpace(value)) { Flush(); previous = null; continue; }
                bool underscore = value == "_";
                double fontSize = Math.Max(letter.FontSize, 1);
                bool gap = previous is not null && letter.StartBaseLine.X - previous.EndBaseLine.X > fontSize * 0.28;
                if (current.Count > 0 && (underscore != currentUnderscore || gap)) Flush();
                currentUnderscore = underscore;
                current.Add(letter);
                previous = letter;
            }
            Flush();
        }
        Tokens.Sort((a, b) =>
        {
            int c = Math.Abs(a.Baseline - b.Baseline) <= 2.0 ? 0 : a.Baseline.CompareTo(b.Baseline);
            return c != 0 ? c : a.Box.X.CompareTo(b.Box.X);
        });
    }

    private void AddToken(List<Letter> letters, bool underscore)
    {
        double x0 = letters.Min(l => l.StartBaseLine.X);
        double x1 = letters.Max(l => l.EndBaseLine.X);
        double fontSize = Math.Max(1, letters.Max(l => l.FontSize));
        double baseline = Height - letters[0].StartBaseLine.Y;
        double top = baseline - fontSize * 0.78, bottom = baseline + fontSize * 0.22;
        string text = string.Concat(letters.Select(l => l.Value));
        Tokens.Add(new Token
        {
            Text = text,
            Box = new RectD(x0, top, Math.Max(x1 - x0, 0.5), bottom - top),
            Baseline = baseline,
            FontSize = fontSize,
            IsBlank = underscore && letters.Count >= 3,
        });
    }
}
