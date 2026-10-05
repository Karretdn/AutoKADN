namespace AutoKADN.Mapeo.Detection;

// Una celda delimitada por rayas por sus cuatro lados, con el texto que trae impreso.
public sealed class Cell
{
    public RectD Box { get; init; }
    public List<Token> Tokens { get; } = new();
    public bool HasImage { get; set; }
    // Dibujo dentro de la celda que no se pudo leer como texto (letras en curvas, el símbolo Ø).
    public bool HasInk { get; set; }
    public double InkWidth { get; set; }
    internal double InkMinX { get; set; }
    internal double InkMaxX { get; set; }
    // Solo tinta, sin texto legible: se asume el símbolo de diámetro.
    public bool InkOnly => Tokens.Count == 0 && HasInk && !HasImage;
    public string Text => Tokens.Count > 0 ? string.Join(" ", Tokens.Select(t => t.Text)) : InkOnly ? "Ø" : "";
    public bool IsEmpty => Tokens.Count == 0 && !HasImage && !HasInk;
}

// Convierte las rayas sueltas de la página en celdas: junta las que están alineadas y busca los rectángulos
// que quedan cerrados por las cuatro bandas. Una celda "combinada" (que abarca varias) se encuentra sola
// porque no tiene raya en medio.
public static class CellGrid
{
    private const double Snap = 1.2;      // rayas más cerca que esto son la misma
    private const double Gap = 1.8;       // hueco permitido al unir tramos de una misma raya
    private const double EdgeTol = 1.8;   // tolerancia para decir que una raya cubre un lado

    public static List<Cell> Find(PageLayout page)
    {
        List<Seg> hs = Merge(page.Horizontals), vs = Merge(page.Verticals);
        double[] ys = Cluster(hs.Select(s => s.Pos)), xs = Cluster(vs.Select(s => s.Pos));
        if (xs.Length < 2 || ys.Length < 2) return new List<Cell>();

        // Intervalos cubiertos por cada raya agrupada.
        var hCover = new List<(double A, double B)>[ys.Length];
        var vCover = new List<(double A, double B)>[xs.Length];
        for (int i = 0; i < ys.Length; i++) hCover[i] = new();
        for (int i = 0; i < xs.Length; i++) vCover[i] = new();
        foreach (Seg s in hs) hCover[Nearest(ys, s.Pos)].Add((s.A, s.B));
        foreach (Seg s in vs) vCover[Nearest(xs, s.Pos)].Add((s.A, s.B));
        for (int i = 0; i < ys.Length; i++) hCover[i] = Union(hCover[i]);
        for (int i = 0; i < xs.Length; i++) vCover[i] = Union(vCover[i]);

        bool HLine(int yi, int xi, int xj) => Covers(hCover[yi], xs[xi], xs[xj]);
        bool VLine(int xi, int yi, int yj) => Covers(vCover[xi], ys[yi], ys[yj]);

        int nx = xs.Length, ny = ys.Length;
        var used = new bool[nx - 1, ny - 1];
        var cells = new List<Cell>();

        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                if (used[i, j]) continue;
                if (!HLine(j, i, i + 1) || !VLine(i, j, j + 1)) continue;

                int i2 = i + 1, j2 = j + 1;
                bool closed = false;
                while (true)
                {
                    bool rightClosed = VLine(i2, j, j2);
                    bool bottomClosed = HLine(j2, i, i2);
                    if (rightClosed && bottomClosed) { closed = true; break; }
                    if (!rightClosed)
                    {
                        // Seguir hacia la derecha solo si la raya de arriba continúa.
                        if (i2 + 1 < nx && HLine(j, i2, i2 + 1)) { i2++; continue; }
                        break;
                    }
                    if (j2 + 1 < ny && VLine(i, j2, j2 + 1)) { j2++; continue; }
                    break;
                }
                if (!closed) continue;

                for (int a = i; a < i2; a++) for (int b = j; b < j2; b++) used[a, b] = true;
                cells.Add(new Cell { Box = new RectD(xs[i], ys[j], xs[i2] - xs[i], ys[j2] - ys[j]) });
            }
        }

        AssignContent(cells, page);
        return cells;
    }

    // Une rayas del mismo nivel que se tocan o están separadas por un hueco mínimo.
    public static List<Seg> Merge(List<Seg> segments)
    {
        var result = new List<Seg>();
        foreach (var group in segments.OrderBy(s => s.Pos).ToList().GroupAdjacent(Snap))
        {
            double pos = group.Average(s => s.Pos);
            double a = 0, b = 0; bool open = false;
            foreach (Seg s in group.OrderBy(s => s.A))
            {
                if (!open) { a = s.A; b = s.B; open = true; }
                else if (s.A <= b + Gap) b = Math.Max(b, s.B);
                else { result.Add(new Seg(pos, a, b)); a = s.A; b = s.B; }
            }
            if (open) result.Add(new Seg(pos, a, b));
        }
        return result;
    }

    private static double[] Cluster(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var result = new List<double>();
        var run = new List<double>();
        foreach (double v in sorted)
        {
            if (run.Count > 0 && v - run[^1] > Snap) { result.Add(run.Average()); run.Clear(); }
            run.Add(v);
        }
        if (run.Count > 0) result.Add(run.Average());
        return result.ToArray();
    }

    private static int Nearest(double[] sorted, double value)
    {
        int best = 0; double distance = double.MaxValue;
        for (int i = 0; i < sorted.Length; i++)
        {
            double d = Math.Abs(sorted[i] - value);
            if (d < distance) { distance = d; best = i; }
        }
        return best;
    }

    private static List<(double A, double B)> Union(List<(double A, double B)> intervals)
    {
        var result = new List<(double A, double B)>();
        foreach (var iv in intervals.OrderBy(i => i.A))
        {
            if (result.Count > 0 && iv.A <= result[^1].B + Gap) result[^1] = (result[^1].A, Math.Max(result[^1].B, iv.B));
            else result.Add(iv);
        }
        return result;
    }

    private static bool Covers(List<(double A, double B)> intervals, double from, double to)
    {
        foreach (var iv in intervals)
            if (iv.A <= from + EdgeTol && iv.B >= to - EdgeTol) return true;
        return false;
    }

    private static void AssignContent(List<Cell> cells, PageLayout page)
    {
        foreach (Token token in page.Tokens)
        {
            Cell? owner = null;
            foreach (Cell cell in cells)
            {
                if (!cell.Box.ContainsPoint(token.Box.CenterX, token.Box.CenterY)) continue;
                // Si hay celdas anidadas, gana la más pequeña.
                if (owner is null || cell.Box.W * cell.Box.H < owner.Box.W * owner.Box.H) owner = cell;
            }
            owner?.Tokens.Add(token);
        }
        foreach (RectD ink in page.Ink)
        {
            Cell? owner = null;
            foreach (Cell cell in cells)
            {
                if (!cell.Box.ContainsPoint(ink.CenterX, ink.CenterY)) continue;
                if (owner is null || cell.Box.W * cell.Box.H < owner.Box.W * owner.Box.H) owner = cell;
            }
            if (owner is null) continue;
            if (!owner.HasInk) { owner.HasInk = true; owner.InkMinX = ink.X; owner.InkMaxX = ink.Right; }
            else { owner.InkMinX = Math.Min(owner.InkMinX, ink.X); owner.InkMaxX = Math.Max(owner.InkMaxX, ink.Right); }
            owner.InkWidth = owner.InkMaxX - owner.InkMinX;
        }
        foreach (RectD image in page.Images)
            foreach (Cell cell in cells)
                if (cell.Box.Intersects(image) && Overlap(cell.Box, image) > 0.3 * Math.Min(cell.Box.W * cell.Box.H, image.W * image.H)) cell.HasImage = true;
    }

    private static double Overlap(RectD a, RectD b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static IEnumerable<List<Seg>> GroupAdjacent(this List<Seg> sorted, double tolerance)
    {
        var group = new List<Seg>();
        foreach (Seg s in sorted)
        {
            if (group.Count > 0 && s.Pos - group[^1].Pos > tolerance) { yield return group; group = new List<Seg>(); }
            group.Add(s);
        }
        if (group.Count > 0) yield return group;
    }
}
