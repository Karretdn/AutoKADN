using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Layouts;

/// <summary>Rectángulo alineado a los ejes, en coordenadas de papel.</summary>
internal readonly struct RecorteRect
{
    public RecorteRect(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Point3d Center => new Point3d((MinX + MaxX) / 2.0, (MinY + MaxY) / 2.0, 0.0);

    public RecorteRect Translate(Vector3d v) => new RecorteRect(MinX + v.X, MinY + v.Y, MaxX + v.X, MaxY + v.Y);

    public bool Contains(Point3d point, double tolerance) =>
        point.X >= MinX - tolerance && point.X <= MaxX + tolerance && point.Y >= MinY - tolerance && point.Y <= MaxY + tolerance;

    public bool Overlaps(Extents3d extents) =>
        extents.MaxPoint.X >= MinX && extents.MinPoint.X <= MaxX && extents.MaxPoint.Y >= MinY && extents.MinPoint.Y <= MaxY;

    public bool Encloses(Extents3d extents, double tolerance) =>
        extents.MinPoint.X >= MinX - tolerance && extents.MaxPoint.X <= MaxX + tolerance
        && extents.MinPoint.Y >= MinY - tolerance && extents.MaxPoint.Y <= MaxY + tolerance;

    /// <summary>
    /// El rectángulo de la proporción dada (ancho / alto) con una esquina FIJA en `anchor` (el primer punto) que crece
    /// hacia el cursor: cubre al menos el recuadro anchor-cursor, y el lado que falte para la proporción se extiende en
    /// el mismo sentido (el cursor queda dentro o sobre el borde, nunca se mueve la esquina fija).
    /// </summary>
    public static RecorteRect FromAnchor(Point3d anchor, Point3d cursor, double aspect)
    {
        double dx = cursor.X - anchor.X, dy = cursor.Y - anchor.Y;
        double width = Math.Max(Math.Abs(dx), Math.Abs(dy) * aspect);
        double height = width / aspect;
        double x2 = anchor.X + (dx < 0.0 ? -width : width);
        double y2 = anchor.Y + (dy < 0.0 ? -height : height);
        return new RecorteRect(Math.Min(anchor.X, x2), Math.Min(anchor.Y, y2), Math.Max(anchor.X, x2), Math.Max(anchor.Y, y2));
    }
}

/// <summary>Lo que se va a copiar del plano general: objetos completos (ya existen) y trozos nuevos de curvas cortadas en el borde.</summary>
internal sealed class RecortePlan : IDisposable
{
    internal readonly ObjectIdCollection Whole = new ObjectIdCollection();
    internal readonly List<Entity> Pieces = new List<Entity>();

    /// <summary>Objetos que entraron enteros (por dentro, o los textos y bloques cuyo centro cae dentro).</summary>
    public int WholeCount => Whole.Count;
    /// <summary>Curvas que cruzaban el borde y se cortaron.</summary>
    public int CutCurves;
    /// <summary>Trozos que quedaron de esas curvas.</summary>
    public int PieceCount => Pieces.Count;
    /// <summary>Objetos que no son curvas (textos, bloques, sombreados…) y se copiaron enteros aunque sobresalen del borde.</summary>
    public int Overflow;
    /// <summary>De los copiados, cuántos son bloques.</summary>
    public int Blocks;
    /// <summary>De los copiados, cuántos llevan datos del plugin (materiales, anotaciones): vienen con sus datos y cuentan en el Excel de ese anillo.</summary>
    public int PluginData;
    /// <summary>Objetos de los que no se pudo leer la extensión ni la posición: no se copiaron.</summary>
    public int Unreadable;
    /// <summary>Corrimiento extra (ya en el marco) de los objetos enteros que se mueven: indicadores de anillo y textos.</summary>
    internal readonly Dictionary<ObjectId, Vector3d> Shifts = new Dictionary<ObjectId, Vector3d>();
    /// <summary>Números de los anillos cuyo indicador se corrió hacia adentro del marco.</summary>
    public readonly List<int> RingsMoved = new List<int>();
    /// <summary>Textos que se corrieron para que no tapen un indicador ni queden cortados por el borde.</summary>
    public int TextsMoved;
    /// <summary>Textos que no cabían en el espacio y no se copiaron.</summary>
    public int TextsDropped;

    public int Total => WholeCount + PieceCount;
    public bool Applied { get; internal set; }

    public void Dispose()
    {
        if (Applied) return;
        foreach (Entity piece in Pieces) piece.Dispose();
        Pieces.Clear();
    }
}

internal static class RecorteGeometria
{
    private const double Tolerance = 1e-6;

    /// <summary>La proporción (ancho / alto) del marco del DETALLE: la del rectángulo de recorte.</summary>
    public static double FrameAspect =>
        (ClonarUcTool.FrameMaxX - ClonarUcTool.FrameMinX) / (ClonarUcTool.DetalleFrameMaxY - ClonarUcTool.DetalleFrameMinY);

    /// <summary>Escala uniforme + traslado que llevan el rectángulo de recorte exactamente al marco.</summary>
    public static Matrix3d ToFrame(RecorteRect rect, double frameMinX, double frameMinY, double frameMaxX, double frameMaxY)
    {
        double scale = (frameMaxX - frameMinX) / rect.Width;
        Point3d from = rect.Center;
        var to = new Point3d((frameMinX + frameMaxX) / 2.0, (frameMinY + frameMaxY) / 2.0, 0.0);
        return Matrix3d.Displacement(to - from) * Matrix3d.Scaling(scale, from);
    }

    public static Matrix3d ToDetalleFrame(RecorteRect rect) =>
        ToFrame(rect, ClonarUcTool.FrameMinX, ClonarUcTool.DetalleFrameMinY, ClonarUcTool.FrameMaxX, ClonarUcTool.DetalleFrameMaxY);

    /// <summary>
    /// Recorre el dibujo del plano general (solo lectura) y decide qué entra: las curvas dentro, enteras; las que cruzan el
    /// borde, cortadas en el borde; bloques, textos y demás, enteros si el punto de inserción o el centro de su extensión
    /// cae dentro. Los bloques y demás objetos con datos del plugin entran con sus datos. Los indicadores de anillo
    /// (círculo con número y diámetro) de los anillos que se alcanzan a ver entran y se corren dentro del marco; los textos
    /// se corren si tapan un indicador o cruzan el borde, y no se copian si no caben (ver RecorteEtiquetas). Se omiten
    /// viewports, el logo, el membrete y lo que está en capas apagadas o congeladas.
    /// </summary>
    public static RecortePlan Collect(Transaction transaction, BlockTableRecord source, RecorteRect rect)
    {
        var plan = new RecortePlan();
        var map = new RecorteMap(rect);
        var layerVisible = new Dictionary<ObjectId, bool>();
        var items = new List<RecorteItem>();
        foreach (ObjectId id in source)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
            if (entity is Viewport || entity is Ole2Frame || !entity.Visible) continue; // el logo (OLE) es del formato, no del plano
            if (ClonarUcTool.IsTitleBlockLayer(entity.Layer)) continue;
            if (!IsLayerVisible(transaction, entity.LayerId, layerVisible)) continue;
            if (!TryExtents(entity, out Extents3d extents)) { plan.Unreadable++; continue; }
            items.Add(new RecorteItem(id, entity, extents));
        }

        RecorteEtiquetas labels = RecorteEtiquetas.Build(items, rect, map, plan);
        var texts = new List<RecorteItem>();

        foreach (RecorteItem item in items)
        {
            if (labels.Handled.Contains(item.Id)) continue; // indicadores de anillo: ya decididos
            ObjectId id = item.Id;
            Entity entity = item.Entity;
            Extents3d extents = item.Extents;

            if (entity is Curve curve && IsClippable(curve))
            {
                if (!rect.Overlaps(extents)) continue;
                if (rect.Encloses(extents, Tolerance)) { Add(plan, id, entity); continue; }
                List<Entity> parts = ClipCurve(curve, rect);
                if (parts.Count > 0)
                {
                    plan.CutCurves++;
                    plan.Pieces.AddRange(parts);
                    if (ClonarUcTool.HasDetalleXData(entity)) plan.PluginData += parts.Count;
                }
                continue;
            }

            // Bloques, textos, sombreados…: enteros si el punto de inserción (bloques) o el centro de su extensión cae dentro.
            var block = entity as BlockReference;
            bool inside = rect.Contains(item.Center, 0.0) || (block != null && rect.Contains(block.Position, 0.0));
            if (!inside) continue;

            if (entity is DBText || entity is MText) { texts.Add(item); continue; } // se colocan juntos al final (etiquetas de varias líneas)

            Add(plan, id, entity);
            if (!rect.Encloses(extents, Tolerance)) plan.Overflow++;
        }
        labels.PlaceTexts(texts, plan);
        return plan;
    }

    internal static void Add(RecortePlan plan, ObjectId id, Entity entity)
    {
        plan.Whole.Add(id);
        if (entity is BlockReference) plan.Blocks++;
        if (ClonarUcTool.HasDetalleXData(entity)) plan.PluginData++;
    }

    // La extensión del objeto; en un bloque cuya extensión no se puede leer (vacío, sin geometría) vale su punto de inserción.
    private static bool TryExtents(Entity entity, out Extents3d extents)
    {
        try
        {
            extents = entity.GeometricExtents;
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            if (entity is BlockReference block)
            {
                extents = new Extents3d(block.Position, block.Position);
                return true;
            }
            extents = default;
            return false;
        }
    }

    /// <summary>Copia el plan al espacio destino (abierto para escribir) ya escalado y trasladado.</summary>
    public static void Apply(Transaction transaction, Database database, RecortePlan plan, BlockTableRecord target, Matrix3d transform)
    {
        if (plan.Whole.Count > 0)
        {
            var mapping = new IdMapping();
            database.DeepCloneObjects(plan.Whole, target.ObjectId, mapping, false);
            foreach (IdPair pair in mapping)
            {
                if (!pair.IsPrimary || !pair.IsCloned) continue;
                if (transaction.GetObject(pair.Value, OpenMode.ForWrite) is not Entity clone) continue;
                clone.TransformBy(transform);
                if (plan.Shifts.TryGetValue(pair.Key, out Vector3d shift)) clone.TransformBy(Matrix3d.Displacement(shift));
            }
        }
        foreach (Entity piece in plan.Pieces)
        {
            piece.TransformBy(transform);
            target.AppendEntity(piece);
            transaction.AddNewlyCreatedDBObject(piece, true);
        }
        plan.Applied = true;
    }

    // ---------- recorte de curvas ----------

    private static bool IsClippable(Curve curve) =>
        curve is Line || curve is Arc || curve is Circle || curve is Ellipse || curve is Polyline || curve is Polyline2d || curve is Spline;

    private static List<Entity> ClipCurve(Curve curve, RecorteRect rect)
    {
        var parts = new List<Entity>();
        if (curve is Line line)
        {
            if (ClipSegment(line.StartPoint, line.EndPoint, rect, out Point3d start, out Point3d end))
            {
                var piece = (Line)line.Clone();
                piece.StartPoint = start;
                piece.EndPoint = end;
                parts.Add(piece);
            }
            return parts;
        }

        var parameters = new List<double>();
        using (Polyline boundary = Boundary(rect))
        {
            var points = new Point3dCollection();
            try { curve.IntersectWith(boundary, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { return KeepWholeIfInside(curve, rect); }
            foreach (Point3d point in points)
            {
                try { parameters.Add(curve.GetParameterAtPoint(curve.GetClosestPointTo(point, false))); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { /* un punto que no cae en la curva no sirve para cortar */ }
            }
        }

        parameters.Sort();
        var cuts = new DoubleCollection();
        double last = double.NaN;
        foreach (double p in parameters)
        {
            double eps = 1e-9 * (1.0 + Math.Abs(p));
            if (!double.IsNaN(last) && p - last <= eps) continue;
            if (!curve.Closed && (p <= curve.StartParam + eps || p >= curve.EndParam - eps)) continue; // en un extremo no corta nada
            cuts.Add(p);
            last = p;
        }

        if (cuts.Count == 0) return KeepWholeIfInside(curve, rect); // no cruza el borde: o está dentro o está fuera

        DBObjectCollection split;
        try { split = curve.GetSplitCurves(cuts); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return KeepWholeIfInside(curve, rect); }

        foreach (DBObject item in split)
        {
            if (item is not Curve piece) { item.Dispose(); continue; }
            Point3d middle;
            try { middle = piece.GetPointAtParameter((piece.StartParam + piece.EndParam) / 2.0); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { piece.Dispose(); continue; }
            if (rect.Contains(middle, Tolerance))
            {
                piece.SetPropertiesFrom(curve);
                parts.Add(piece);
            }
            else piece.Dispose();
        }
        return parts;
    }

    // Una curva que no cruza el borde: entera si un punto suyo está dentro, nada si está fuera.
    private static List<Entity> KeepWholeIfInside(Curve curve, RecorteRect rect)
    {
        var parts = new List<Entity>();
        Point3d sample;
        try { sample = curve.GetPointAtParameter((curve.StartParam + curve.EndParam) / 2.0); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return parts; }
        if (rect.Contains(sample, Tolerance)) parts.Add((Entity)curve.Clone());
        return parts;
    }

    private static Polyline Boundary(RecorteRect rect)
    {
        var boundary = new Polyline();
        boundary.AddVertexAt(0, new Point2d(rect.MinX, rect.MinY), 0.0, 0.0, 0.0);
        boundary.AddVertexAt(1, new Point2d(rect.MaxX, rect.MinY), 0.0, 0.0, 0.0);
        boundary.AddVertexAt(2, new Point2d(rect.MaxX, rect.MaxY), 0.0, 0.0, 0.0);
        boundary.AddVertexAt(3, new Point2d(rect.MinX, rect.MaxY), 0.0, 0.0, 0.0);
        boundary.Closed = true;
        return boundary;
    }

    // Liang–Barsky: el tramo del segmento que cae dentro del rectángulo.
    internal static bool ClipSegment(Point3d a, Point3d b, RecorteRect rect, out Point3d start, out Point3d end)
    {
        start = a; end = b;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t0 = 0.0, t1 = 1.0;
        double[] p = { -dx, dx, -dy, dy };
        double[] q = { a.X - rect.MinX, rect.MaxX - a.X, a.Y - rect.MinY, rect.MaxY - a.Y };
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-15)
            {
                if (q[i] < 0.0) return false; // paralelo y fuera
                continue;
            }
            double t = q[i] / p[i];
            if (p[i] < 0.0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        if (t1 - t0 <= 1e-12) return false;
        start = new Point3d(a.X + t0 * dx, a.Y + t0 * dy, a.Z);
        end = new Point3d(a.X + t1 * dx, a.Y + t1 * dy, a.Z);
        return start.DistanceTo(end) > Tolerance;
    }

    private static bool IsLayerVisible(Transaction transaction, ObjectId layerId, Dictionary<ObjectId, bool> cache)
    {
        if (cache.TryGetValue(layerId, out bool visible)) return visible;
        visible = true;
        try
        {
            var layer = (LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForRead);
            visible = !layer.IsOff && !layer.IsFrozen;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { /* capa ilegible: se asume visible */ }
        cache[layerId] = visible;
        return visible;
    }
}
