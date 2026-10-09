using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Layouts;

/// <summary>Un objeto del plano general ya leído: su id, el objeto abierto (solo mientras dura la transacción) y su extensión.</summary>
internal sealed class RecorteItem
{
    public RecorteItem(ObjectId id, Entity entity, Extents3d extents)
    {
        Id = id;
        Entity = entity;
        Extents = extents;
    }

    public ObjectId Id { get; }
    public Entity Entity { get; }
    public Extents3d Extents { get; }
    public Point3d Center => new Point3d((Extents.MinPoint.X + Extents.MaxPoint.X) / 2.0, (Extents.MinPoint.Y + Extents.MaxPoint.Y) / 2.0, 0.0);
}

/// <summary>Cómo se lleva un punto del plano general al marco del DETALLE: escala uniforme y traslado.</summary>
internal readonly struct RecorteMap
{
    public RecorteMap(RecorteRect rect)
    {
        Scale = (ClonarUcTool.FrameMaxX - ClonarUcTool.FrameMinX) / rect.Width;
        From = rect.Center;
        To = new Point3d((ClonarUcTool.FrameMinX + ClonarUcTool.FrameMaxX) / 2.0, (ClonarUcTool.DetalleFrameMinY + ClonarUcTool.DetalleFrameMaxY) / 2.0, 0.0);
    }

    public double Scale { get; }
    public Point3d From { get; }
    public Point3d To { get; }

    public Point3d Map(Point3d p) => new Point3d(To.X + Scale * (p.X - From.X), To.Y + Scale * (p.Y - From.Y), 0.0);

    public RecorteRect Map(Extents3d e)
    {
        Point3d a = Map(e.MinPoint), b = Map(e.MaxPoint);
        return new RecorteRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>El marco del DETALLE.</summary>
    public static RecorteRect Frame =>
        new RecorteRect(ClonarUcTool.FrameMinX, ClonarUcTool.DetalleFrameMinY, ClonarUcTool.FrameMaxX, ClonarUcTool.DetalleFrameMaxY);
}

/// <summary>
/// Los indicadores de anillo del plano general (un círculo con el número del anillo y el diámetro, más la rayita que los
/// separa) y los textos del recorte.
///  · Si un anillo se alcanza a ver en el recorte, su indicador entra aunque esté afuera, y si queda cortado o fuera del
///    marco se corre hacia adentro (con un margen) para que se sepa de qué anillo es lo que se ve. Un anillo se
///    reconoce por su manzana: las líneas de los lotes (las de la capa que más rodea a los indicadores) que se tocan
///    entre sí; el anillo se ve si de su manzana hay dentro del recorte una longitud de línea apreciable.
///  · Los textos que quedan encima de un indicador corrido, o cruzando el borde del marco, se corren lo mínimo; si no
///    caben, no se copian.
/// </summary>
internal sealed class RecorteEtiquetas
{
    /// <summary>Separación entre un indicador corrido y el borde del marco.</summary>
    public const double IndicatorMargin = 2.5;
    /// <summary>Separación mínima entre un texto y el borde del marco o un indicador.</summary>
    public const double TextMargin = 0.6;
    /// <summary>Las líneas de una misma etiqueta (como "P#" encima de "15 16 Y 17") están a menos de esta cantidad de alturas de texto y se corren juntas.</summary>
    private const double StackGap = 1.6;
    /// <summary>Un texto que estorba se corre de preferencia hacia arriba o abajo (sigue en su lote): el movimiento vertical pesa menos.</summary>
    private const double VerticalPreference = 0.7;

    private static readonly Regex RingNumber = new Regex(@"^\s*(\d{1,3})\s*$");
    private static readonly Regex FormatCodes = new Regex(@"\\[A-Za-z][^;\\]*;|\\[PpNn~]|[{}]");

    private sealed class Indicator
    {
        public RecorteItem Circle = null!;
        public List<RecorteItem> Members = new List<RecorteItem>();
        public int Number;
        public double Radius;
        public Point3d CenterF;      // centro, ya en el marco
        public double RadiusF;
        public bool Included;
        public bool Relocated;
    }

    private sealed class Segment
    {
        public Point3d A;
        public Point3d B;
        public string Layer = string.Empty;
        public double MinX, MinY, MaxX, MaxY;
        public int Parent;
    }

    private readonly RecorteMap _map;
    private readonly List<(Point3d Center, double Radius)> _relocated = new List<(Point3d, double)>();

    public HashSet<ObjectId> Handled { get; } = new HashSet<ObjectId>();

    private RecorteEtiquetas(RecorteMap map)
    {
        _map = map;
    }

    /// <summary>Lee los indicadores, decide cuáles entran, los coloca y los agrega al plan.</summary>
    public static RecorteEtiquetas Build(List<RecorteItem> items, RecorteRect rect, RecorteMap map, RecortePlan plan)
    {
        var labels = new RecorteEtiquetas(map);
        List<Indicator> indicators = FindIndicators(items);
        foreach (Indicator indicator in indicators)
        {
            labels.Handled.Add(indicator.Circle.Id);
            foreach (RecorteItem member in indicator.Members) labels.Handled.Add(member.Id);
            indicator.CenterF = map.Map(indicator.Circle.Center);
            indicator.RadiusF = indicator.Radius * map.Scale;
        }
        if (indicators.Count == 0) return labels;

        MarkVisible(items, indicators, rect);
        labels.Place(indicators, plan);
        return labels;
    }

    // ---------- indicadores ----------

    private static List<Indicator> FindIndicators(List<RecorteItem> items)
    {
        var result = new List<Indicator>();
        foreach (RecorteItem item in items)
        {
            if (item.Entity is not Circle circle) continue;
            var indicator = new Indicator { Circle = item, Radius = circle.Radius };
            foreach (RecorteItem other in items)
            {
                if (ReferenceEquals(other, item) || !IsPart(other, circle)) continue;
                indicator.Members.Add(other);
                if (indicator.Number == 0 && TryRingNumber(other.Entity, out int number)) indicator.Number = number;
            }
            if (indicator.Number > 0) result.Add(indicator);
        }
        return result;
    }

    // Parte de un indicador: texto con el centro dentro del círculo, o una línea/polilínea que cabe entera en su caja.
    private static bool IsPart(RecorteItem item, Circle circle)
    {
        Point3d c = circle.Center;
        double r = circle.Radius;
        if (item.Entity is DBText || item.Entity is MText)
        {
            double dx = item.Center.X - c.X, dy = item.Center.Y - c.Y;
            return dx * dx + dy * dy <= r * r;
        }
        if (item.Entity is Line || item.Entity is Polyline)
        {
            const double tol = 1e-6;
            return item.Extents.MinPoint.X >= c.X - r - tol && item.Extents.MaxPoint.X <= c.X + r + tol
                && item.Extents.MinPoint.Y >= c.Y - r - tol && item.Extents.MaxPoint.Y <= c.Y + r + tol;
        }
        return false;
    }

    private static bool TryRingNumber(Entity entity, out int number)
    {
        number = 0;
        string? text = PlainText(entity);
        if (text is null) return false;
        Match match = RingNumber.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out number) && number > 0;
    }

    /// <summary>El texto sin códigos de formato de un texto o texto múltiple; null si no es un texto.</summary>
    internal static string? PlainText(Entity entity)
    {
        if (entity is DBText text) return text.TextString;
        if (entity is MText mtext) return FormatCodes.Replace(mtext.Contents ?? string.Empty, string.Empty);
        return null;
    }

    // ---------- qué anillos se alcanzan a ver ----------

    // La longitud de línea de la manzana de un anillo que tiene que verse dentro del recorte, en diámetros de indicador.
    private const double VisibleDiameters = 3.0;
    // Las líneas más largas que esto (en radios de indicador) no son de un lote sino de la calle, la quebrada o el límite de
    // todo el plano, y unirían manzanas lejanas: no cuentan para reconocer la manzana de un anillo.
    private const double MaxLotSegmentRadii = 40.0;

    private static void MarkVisible(List<RecorteItem> items, List<Indicator> indicators, RecorteRect rect)
    {
        var skip = new HashSet<ObjectId>();
        foreach (Indicator indicator in indicators)
        {
            skip.Add(indicator.Circle.Id);
            foreach (RecorteItem member in indicator.Members) skip.Add(member.Id);
        }
        List<Segment> segments = ReadSegments(items, skip);
        string? lotLayer = LotLayer(segments, indicators);
        double averageRadius = indicators.Average(i => i.Radius);
        List<Segment> lots = lotLayer == null
            ? new List<Segment>()
            : segments.Where(s => s.Layer == lotLayer && s.A.DistanceTo(s.B) <= MaxLotSegmentRadii * averageRadius).ToList();
        for (int i = 0; i < lots.Count; i++) lots[i].Parent = i; // los índices son los de esta lista
        Cluster(lots, 0.3 * averageRadius);

        foreach (Indicator indicator in indicators)
        {
            bool centerInside = rect.Contains(indicator.Circle.Center, 0.0);
            bool visible = false;
            Segment? home = NearestSegment(lots, indicator.Circle.Center, 2.0 * indicator.Radius);
            if (home != null)
            {
                int root = Find(lots, lots.IndexOf(home));
                double length = 0.0;
                for (int i = 0; i < lots.Count && !visible; i++)
                {
                    if (Find(lots, i) != root) continue;
                    if (RecorteGeometria.ClipSegment(lots[i].A, lots[i].B, rect, out Point3d s, out Point3d e)) length += s.DistanceTo(e);
                    visible = length >= VisibleDiameters * 2.0 * indicator.Radius;
                }
            }
            indicator.Included = centerInside || visible;
        }
    }

    // Los tramos rectos de líneas y polilíneas del plano (los arcos cuentan por su cuerda).
    private static List<Segment> ReadSegments(List<RecorteItem> items, HashSet<ObjectId> skip)
    {
        var segments = new List<Segment>();
        foreach (RecorteItem item in items)
        {
            if (skip.Contains(item.Id)) continue;
            if (item.Entity is Line line) AddSegment(segments, line.StartPoint, line.EndPoint, line.Layer);
            else if (item.Entity is Polyline polyline)
            {
                int count = polyline.NumberOfVertices;
                int last = polyline.Closed ? count : count - 1;
                for (int i = 0; i < last; i++) AddSegment(segments, polyline.GetPoint3dAt(i), polyline.GetPoint3dAt((i + 1) % count), polyline.Layer);
            }
        }
        return segments;
    }

    private static void AddSegment(List<Segment> segments, Point3d a, Point3d b, string layer)
    {
        if (a.DistanceTo(b) < 1e-9) return;
        segments.Add(new Segment
        {
            A = a, B = b, Layer = layer, Parent = segments.Count,
            MinX = Math.Min(a.X, b.X), MaxX = Math.Max(a.X, b.X), MinY = Math.Min(a.Y, b.Y), MaxY = Math.Max(a.Y, b.Y)
        });
    }

    // La capa de las líneas de los lotes: la que más tramos tiene cerca de los indicadores (a menos de 3 radios).
    private static string? LotLayer(List<Segment> segments, List<Indicator> indicators)
    {
        var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Indicator indicator in indicators)
        {
            Point3d c = indicator.Circle.Center;
            double reach = 3.0 * indicator.Radius;
            foreach (Segment s in segments)
            {
                if (DistanceToSegment(c, s) > reach) continue;
                votes[s.Layer] = votes.TryGetValue(s.Layer, out int n) ? n + 1 : 1;
            }
        }
        string? best = null;
        int bestVotes = 0;
        foreach (KeyValuePair<string, int> vote in votes)
            if (vote.Value > bestVotes) { best = vote.Key; bestVotes = vote.Value; }
        return bestVotes >= 4 ? best : null;
    }

    // Une los tramos que se tocan (o están a menos de `tolerance`): cada manzana de un anillo queda en un grupo.
    private static void Cluster(List<Segment> segments, double tolerance)
    {
        if (segments.Count == 0) return;
        double cell = Math.Max(tolerance * 8.0, 1.0);
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < segments.Count; i++)
        {
            Segment s = segments[i];
            int x0 = (int)Math.Floor((s.MinX - tolerance) / cell), x1 = (int)Math.Floor((s.MaxX + tolerance) / cell);
            int y0 = (int)Math.Floor((s.MinY - tolerance) / cell), y1 = (int)Math.Floor((s.MaxY + tolerance) / cell);
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (!grid.TryGetValue((x, y), out List<int>? bucket)) { bucket = new List<int>(); grid[(x, y)] = bucket; }
                    bucket.Add(i);
                }
            }
        }
        foreach (List<int> bucket in grid.Values)
        {
            for (int a = 0; a < bucket.Count; a++)
            {
                Segment sa = segments[bucket[a]];
                for (int b = a + 1; b < bucket.Count; b++)
                {
                    Segment sb = segments[bucket[b]];
                    if (sa.MinX > sb.MaxX + tolerance || sb.MinX > sa.MaxX + tolerance || sa.MinY > sb.MaxY + tolerance || sb.MinY > sa.MaxY + tolerance) continue;
                    int ra = Find(segments, bucket[a]), rb = Find(segments, bucket[b]);
                    if (ra != rb) segments[rb].Parent = ra;
                }
            }
        }
    }

    private static int Find(List<Segment> segments, int index)
    {
        while (segments[index].Parent != index)
        {
            segments[index].Parent = segments[segments[index].Parent].Parent;
            index = segments[index].Parent;
        }
        return index;
    }

    // El tramo más cercano al punto, si está a menos de `reach`.
    private static Segment? NearestSegment(List<Segment> segments, Point3d point, double reach)
    {
        Segment? best = null;
        double bestDistance = reach;
        foreach (Segment s in segments)
        {
            double distance = DistanceToSegment(point, s);
            if (distance < bestDistance) { bestDistance = distance; best = s; }
        }
        return best;
    }

    private static double DistanceToSegment(Point3d p, Segment s)
    {
        double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
        double length2 = dx * dx + dy * dy;
        double t = length2 <= 0.0 ? 0.0 : Math.Max(0.0, Math.Min(1.0, ((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy) / length2));
        double qx = s.A.X + t * dx - p.X, qy = s.A.Y + t * dy - p.Y;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    // ---------- colocar los indicadores ----------

    private void Place(List<Indicator> indicators, RecortePlan plan)
    {
        RecorteRect frame = RecorteMap.Frame;
        var placed = new List<(Point3d Center, double Radius)>();

        // Primero los que ya caben enteros (quedan donde están y estorban a los demás); después los que hay que correr.
        var inside = new List<Indicator>();
        var toMove = new List<Indicator>();
        foreach (Indicator indicator in indicators.Where(i => i.Included).OrderBy(i => i.Number))
        {
            RecorteRect box = CircleRect(indicator.CenterF, indicator.RadiusF + IndicatorMargin);
            if (frame.Encloses(new Extents3d(new Point3d(box.MinX, box.MinY, 0), new Point3d(box.MaxX, box.MaxY, 0)), 1e-6)) inside.Add(indicator);
            else toMove.Add(indicator);
        }
        foreach (Indicator indicator in inside) placed.Add((indicator.CenterF, indicator.RadiusF));

        var finalCenters = new Dictionary<Indicator, Point3d>();
        foreach (Indicator indicator in toMove)
        {
            Point3d target = ClampCenter(indicator.CenterF, indicator.RadiusF, frame);
            Point3d chosen = FreePosition(target, indicator.RadiusF, frame, placed);
            finalCenters[indicator] = chosen;
            placed.Add((chosen, indicator.RadiusF));
            _relocated.Add((chosen, indicator.RadiusF));
            indicator.Relocated = true;
        }

        foreach (Indicator indicator in indicators.Where(i => i.Included).OrderBy(i => i.Number))
        {
            Vector3d shift = finalCenters.TryGetValue(indicator, out Point3d chosen) ? chosen - indicator.CenterF : Vector3d.XAxis * 0.0;
            AddGroup(plan, indicator, shift);
            if (indicator.Relocated) plan.RingsMoved.Add(indicator.Number);
        }
    }

    private static void AddGroup(RecortePlan plan, Indicator indicator, Vector3d shift)
    {
        var all = new List<RecorteItem> { indicator.Circle };
        all.AddRange(indicator.Members);
        foreach (RecorteItem item in all)
        {
            plan.Whole.Add(item.Id);
            if (shift.Length > 1e-9) plan.Shifts[item.Id] = shift;
            if (ClonarUcTool.HasDetalleXData(item.Entity)) plan.PluginData++;
        }
    }

    private static RecorteRect CircleRect(Point3d center, double radius) =>
        new RecorteRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);

    // El centro más cercano al original que deja el círculo entero dentro del marco con su margen.
    private static Point3d ClampCenter(Point3d center, double radius, RecorteRect frame)
    {
        double pad = radius + IndicatorMargin;
        double minX = frame.MinX + pad, maxX = frame.MaxX - pad, minY = frame.MinY + pad, maxY = frame.MaxY - pad;
        double x = minX > maxX ? (frame.MinX + frame.MaxX) / 2.0 : Math.Min(Math.Max(center.X, minX), maxX);
        double y = minY > maxY ? (frame.MinY + frame.MaxY) / 2.0 : Math.Min(Math.Max(center.Y, minY), maxY);
        return new Point3d(x, y, 0.0);
    }

    // `target` si no choca con otro indicador; si no, el lugar libre más cercano dentro del marco.
    private static Point3d FreePosition(Point3d target, double radius, RecorteRect frame, List<(Point3d Center, double Radius)> others)
    {
        if (!Collides(target, radius, others)) return target;

        double pad = radius + IndicatorMargin;
        double minX = frame.MinX + pad, maxX = frame.MaxX - pad, minY = frame.MinY + pad, maxY = frame.MaxY - pad;
        double step = Math.Max(radius / 2.0, 0.5);
        Point3d best = target;
        double bestDistance = double.MaxValue;
        for (double x = minX; x <= maxX + 1e-9; x += step)
        {
            for (double y = minY; y <= maxY + 1e-9; y += step)
            {
                var candidate = new Point3d(x, y, 0.0);
                double distance = candidate.DistanceTo(target);
                if (distance >= bestDistance || Collides(candidate, radius, others)) continue;
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static bool Collides(Point3d center, double radius, List<(Point3d Center, double Radius)> others)
    {
        foreach ((Point3d Center, double Radius) other in others)
            if (center.DistanceTo(other.Center) < radius + other.Radius + 1.0) return true;
        return false;
    }

    // ---------- textos ----------

    /// <summary>
    /// Coloca los textos que entran (con el centro dentro del recorte): las líneas de una misma etiqueta se mueven juntas;
    /// una etiqueta que queda encima de un indicador corrido, o cruzando el borde del marco, se corre lo mínimo, y si no
    /// cabe no se copia. Agrega al plan los que quedan.
    /// </summary>
    public void PlaceTexts(List<RecorteItem> texts, RecortePlan plan)
    {
        if (texts.Count == 0) return;
        var rects = new List<RecorteRect>(texts.Count);
        var heights = new List<double>(texts.Count);   // altura de la letra, ya en el marco
        foreach (RecorteItem text in texts)
        {
            rects.Add(_map.Map(text.Extents));
            heights.Add(LetterHeight(text.Entity) * _map.Scale);
        }

        // Etiquetas de varias líneas: los textos pegados entre sí forman un grupo.
        var parent = new int[texts.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        for (int i = 0; i < texts.Count; i++)
        {
            for (int j = i + 1; j < texts.Count; j++)
            {
                double gap = StackGap * Math.Max(heights[i], heights[j]);
                if (rects[i].MinX > rects[j].MaxX + gap || rects[j].MinX > rects[i].MaxX + gap
                    || rects[i].MinY > rects[j].MaxY + gap || rects[j].MinY > rects[i].MaxY + gap) continue;
                int ri = Root(parent, i), rj = Root(parent, j);
                if (ri != rj) parent[rj] = ri;
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < texts.Count; i++)
        {
            int root = Root(parent, i);
            if (!groups.TryGetValue(root, out List<int>? members)) { members = new List<int>(); groups[root] = members; }
            members.Add(i);
        }

        foreach (List<int> members in groups.Values)
        {
            RecorteRect union = rects[members[0]];
            foreach (int index in members)
                union = new RecorteRect(Math.Min(union.MinX, rects[index].MinX), Math.Min(union.MinY, rects[index].MinY),
                    Math.Max(union.MaxX, rects[index].MaxX), Math.Max(union.MaxY, rects[index].MaxY));

            if (!PlaceRect(union, out Vector3d shift)) { plan.TextsDropped += members.Count; continue; }
            foreach (int index in members)
            {
                RecorteGeometria.Add(plan, texts[index].Id, texts[index].Entity);
                if (shift.Length <= 1e-9) continue;
                plan.Shifts[texts[index].Id] = shift;
                plan.TextsMoved++;
            }
        }
    }

    // La altura de la letra (no la del recuadro: un texto girado tiene un recuadro alto).
    private static double LetterHeight(Entity entity) => entity is DBText text ? text.Height : entity is MText mtext ? mtext.TextHeight : 0.0;

    private static int Root(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }
        return index;
    }

    // El corrimiento mínimo de un rectángulo de texto para que no tape un indicador corrido ni cruce el borde del marco.
    private bool PlaceRect(RecorteRect rect, out Vector3d shift)
    {
        shift = Vector3d.XAxis * 0.0;
        RecorteRect frame = RecorteMap.Frame;
        var inner = new RecorteRect(frame.MinX + TextMargin, frame.MinY + TextMargin, frame.MaxX - TextMargin, frame.MaxY - TextMargin);
        if (rect.Width > inner.Width || rect.Height > inner.Height) return false;

        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            foreach ((Point3d Center, double Radius) circle in _relocated)
            {
                if (!TouchesCircle(rect, circle)) continue;
                if (!EscapeCircle(rect, circle, inner, out Vector3d escape)) return false;
                rect = rect.Translate(escape);
                shift += escape;
                changed = true;
            }
            Vector3d inward = Inward(rect, inner);
            if (inward.Length > 1e-9)
            {
                rect = rect.Translate(inward);
                shift += inward;
                changed = true;
            }
            if (!changed) break;
        }

        foreach ((Point3d Center, double Radius) circle in _relocated)
            if (TouchesCircle(rect, circle)) return false;
        return true;
    }

    private static bool TouchesCircle(RecorteRect rect, (Point3d Center, double Radius) circle)
    {
        double dx = Math.Max(Math.Max(rect.MinX - circle.Center.X, 0.0), circle.Center.X - rect.MaxX);
        double dy = Math.Max(Math.Max(rect.MinY - circle.Center.Y, 0.0), circle.Center.Y - rect.MaxY);
        double clear = circle.Radius + TextMargin;
        return dx * dx + dy * dy < clear * clear - 1e-6; // justo en el borde del margen ya no cuenta como tocar
    }

    // El corrimiento más corto (arriba, abajo, izquierda o derecha) que saca el texto del círculo y lo deja dentro del marco.
    private bool EscapeCircle(RecorteRect rect, (Point3d Center, double Radius) circle, RecorteRect inner, out Vector3d escape)
    {
        double r = circle.Radius + TextMargin;
        double cx = circle.Center.X, cy = circle.Center.Y;
        var candidates = new List<Vector3d>
        {
            new Vector3d(0.0, (cy - r) - rect.MaxY, 0.0),   // abajo
            new Vector3d(0.0, (cy + r) - rect.MinY, 0.0),   // arriba
            new Vector3d((cx - r) - rect.MaxX, 0.0, 0.0),   // izquierda
            new Vector3d((cx + r) - rect.MinX, 0.0, 0.0),   // derecha
        };
        escape = Vector3d.XAxis * 0.0;
        double best = double.MaxValue;
        bool found = false;
        foreach (Vector3d candidate in candidates)
        {
            RecorteRect moved = rect.Translate(candidate);
            if (moved.MinX < inner.MinX - 1e-9 || moved.MaxX > inner.MaxX + 1e-9 || moved.MinY < inner.MinY - 1e-9 || moved.MaxY > inner.MaxY + 1e-9) continue;
            if (_relocated.Any(other => TouchesCircle(moved, other))) continue;
            double cost = Math.Abs(candidate.X) + Math.Abs(candidate.Y) * VerticalPreference;
            if (cost >= best) continue;
            best = cost;
            escape = candidate;
            found = true;
        }
        return found;
    }

    private static Vector3d Inward(RecorteRect rect, RecorteRect inner)
    {
        double dx = Math.Max(inner.MinX - rect.MinX, 0.0) - Math.Max(rect.MaxX - inner.MaxX, 0.0);
        double dy = Math.Max(inner.MinY - rect.MinY, 0.0) - Math.Max(rect.MaxY - inner.MaxY, 0.0);
        return new Vector3d(dx, dy, 0.0);
    }
}
