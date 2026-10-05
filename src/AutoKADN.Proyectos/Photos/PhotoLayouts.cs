namespace AutoKADN.Proyectos.Photos;

/// <summary>Caja (en píxeles a 96 dpi) donde va una foto, relativa a la esquina superior izquierda de su página.</summary>
public readonly record struct Box(double X, double Y, double W, double H);

/// <summary>
/// Distribución de las fotos en una página del registro fotográfico, según cuántas lleva.
/// Las cajas salen de la guía que se posicionó a mano para 3 a 9 fotos (regularizadas: simétricas respecto al
/// centro de la página y con separaciones iguales). Para 1 y 2 fotos se definió el mismo criterio: una sola
/// foto grande centrada, o dos grandes una sobre otra. Cada foto se ajusta dentro de su caja sin deformarse.
/// </summary>
public static class PhotoLayouts
{
    public const int MaxPerPage = 9;

    /// <summary>Una página con menos fotos que esto se evita cuando hay más de 9 (se reparte con la anterior).</summary>
    public const int MinOnLastPage = 3;

    // Columnas de a 3 (ancho 205) y su separación; centro de la página en x = 352.
    private static readonly double[] ThreeColumns = { 34, 249, 465 };
    private const double CellW = 205;

    private static Box[] Row3(double y, double h) =>
        ThreeColumns.Select(x => new Box(x, y, CellW, h)).ToArray();

    private static readonly Dictionary<int, Box[]> Catalog = BuildCatalog();

    private static Dictionary<int, Box[]> BuildCatalog()
    {
        const double h = 273;
        Box[] top2 = Row3(143, h).Concat(Row3(426, h)).ToArray();
        return new Dictionary<int, Box[]>
        {
            [1] = new[] { new Box(67, 184, 570, 760) },
            [2] = new[] { new Box(202, 150, 300, 400), new Box(202, 570, 300, 400) },
            [3] = new[] { new Box(67, 158, 273, 385), new Box(364, 158, 273, 385), new Box(215.5, 578, 273, 385) },
            [4] = new[] { new Box(67, 158, 273, 385), new Box(364, 158, 273, 385), new Box(67, 551, 273, 385), new Box(364, 551, 273, 385) },
            [5] = Row3(143, 336).Concat(new[] { new Box(62, 503, 277, 370), new Box(365, 503, 277, 370) }).ToArray(),
            [6] = Row3(143, 333).Concat(Row3(516, 333)).ToArray(),
            [7] = top2.Concat(new[] { new Box(249, 709, CellW, h) }).ToArray(),
            [8] = top2.Concat(new[] { new Box(140, 709, CellW, h), new Box(359, 709, CellW, h) }).ToArray(),
            [9] = top2.Concat(Row3(709, h)).ToArray(),
        };
    }

    /// <summary>Cajas de una página con esa cantidad de fotos (1 a 9), en orden de lectura.</summary>
    public static IReadOnlyList<Box> For(int count)
    {
        if (count < 1 || count > MaxPerPage) throw new ArgumentOutOfRangeException(nameof(count), "Una página lleva de 1 a 9 fotos.");
        return Catalog[count];
    }

    /// <summary>
    /// Lado largo (px) con el que conviene guardar cada foto de una página con esa cantidad: unos 290 dpi al
    /// imprimir su caja (tres veces su tamaño en pantalla), entre 800 y 1600. Con 9 fotos pesan menos de la mitad
    /// que con 1 y la calidad impresa es la misma.
    /// </summary>
    public static int SuggestedMaxSide(int photosOnPage)
    {
        double longSide = For(Math.Clamp(photosOnPage, 1, MaxPerPage)).Max(b => Math.Max(b.W, b.H));
        return (int)Math.Clamp(Math.Ceiling(longSide * 3 / 100) * 100, 800, 1600);
    }

    /// <summary>
    /// Cuántas fotos lleva cada página. Máximo 9 por página; las páginas se llenan en orden y, si la última
    /// quedaría con 1 o 2 fotos (habiendo más de 9), se le pasan fotos de la anterior para que tenga 3.
    /// 10 → 7 + 3 · 11 → 8 + 3 · 12 → 9 + 3 · 19 → 9 + 7 + 3 · 20 → 9 + 8 + 3.
    /// </summary>
    public static int[] PlanPages(int total)
    {
        if (total < 1) return Array.Empty<int>();
        if (total <= MaxPerPage) return new[] { total };

        int pages = (total + MaxPerPage - 1) / MaxPerPage;
        var counts = new int[pages];
        for (int i = 0; i < pages - 1; i++) counts[i] = MaxPerPage;
        counts[pages - 1] = total - MaxPerPage * (pages - 1);

        if (counts[pages - 1] < MinOnLastPage)
        {
            int move = MinOnLastPage - counts[pages - 1];
            counts[pages - 2] -= move;
            counts[pages - 1] += move;
        }
        return counts;
    }
}
