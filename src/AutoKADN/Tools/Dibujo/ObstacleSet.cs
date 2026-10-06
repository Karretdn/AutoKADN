using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AutoKADN.Tools.Dibujo;

/// <summary>
/// Copia liviana (solo XY) de las curvas visibles del espacio actual. Permite lanzar rayos en cada
/// movimiento del mouse sin abrir objetos de la base de datos.
/// Líneas y tramos rectos de polilíneas se guardan como segmentos (cálculo analítico); arcos, círculos,
/// elipses y splines como copias sueltas de la entidad, que se liberan en Dispose.
/// </summary>
internal sealed class ObstacleSet : IDisposable
{
    /// <summary>Un choque más cerca que esto del origen del rayo se ignora (el origen está sobre la línea).</summary>
    public const double MinHit = 1e-6;

    private const double SegmentTolerance = 1e-7;
    private const double ParallelSine = 1e-9;
    private const double CollinearOffset = 1e-6;
    private const double BoxPadding = 1e-6;

    private static Plane _xyPlane;

    private struct Segment
    {
        public double Ax, Ay, Bx, By;
    }

    private sealed class CurveObstacle
    {
        public Curve Curve;
        public double MinX, MinY, MaxX, MaxY;
    }

    private readonly List<Segment> _segments = new List<Segment>();
    private readonly List<CurveObstacle> _curves = new List<CurveObstacle>();
    private double _minX = double.MaxValue, _minY = double.MaxValue, _maxX = double.MinValue, _maxY = double.MinValue;

    private static Plane XyPlane => _xyPlane ??= new Plane(Point3d.Origin, Vector3d.ZAxis);

    public static ObstacleSet Collect(Database database)
    {
        var set = new ObstacleSet();
        try
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var layerVisibility = new Dictionary<ObjectId, bool>();
                RXClass curveClass = RXObject.GetClass(typeof(Curve));
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (!id.ObjectClass.IsDerivedFrom(curveClass)) continue;
                    if (transaction.GetObject(id, OpenMode.ForRead) is not Curve curve || !curve.Visible) continue;
                    if (!IsLayerVisible(transaction, curve.LayerId, layerVisibility)) continue;
                    try { set.AddCurve(curve); }
                    catch { /* entidad no soportada: se ignora como obstáculo */ }
                }
                transaction.Commit();
            }
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (CurveObstacle obstacle in _curves) obstacle.Curve.Dispose();
        _curves.Clear();
    }

    /// <summary>Marca la cantidad actual de segmentos para poder deshacer los que se agreguen después.</summary>
    public int Mark() => _segments.Count;

    public void Rollback(int mark)
    {
        if (mark >= 0 && mark < _segments.Count) _segments.RemoveRange(mark, _segments.Count - mark);
    }

    public void AddSegment(Point3d a, Point3d b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (dx * dx + dy * dy <= 1e-18) return;
        _segments.Add(new Segment { Ax = a.X, Ay = a.Y, Bx = b.X, By = b.Y });
        Grow(a.X, a.Y);
        Grow(b.X, b.Y);
    }

    /// <summary>Primer choque del rayo con cualquier obstáculo. t = distancia desde el origen.</summary>
    public bool TryRayFirst(Point3d origin, Vector3d direction, out double t)
    {
        t = double.MaxValue;
        bool found = false;
        for (int i = 0; i < _segments.Count; i++)
        {
            if (RaySegment(origin.X, origin.Y, direction.X, direction.Y, _segments[i], out double hit) && hit < t)
            {
                t = hit;
                found = true;
            }
        }
        if (_curves.Count > 0)
        {
            double rayLength = RayLength(origin);
            for (int i = 0; i < _curves.Count; i++)
            {
                if (RayCurve(_curves[i], origin, direction, rayLength, out double hit) && hit < t)
                {
                    t = hit;
                    found = true;
                }
            }
        }
        if (!found) t = 0.0;
        return found;
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
        catch { /* capa ilegible: se asume visible */ }
        cache[layerId] = visible;
        return visible;
    }

    private void AddCurve(Curve curve)
    {
        switch (curve)
        {
            case Line line:
                AddSegment(line.StartPoint, line.EndPoint);
                break;
            case Polyline polyline:
                AddPolyline(polyline);
                break;
            case Polyline2d _:
                AddExploded(curve);
                break;
            case Polyline3d _:
            case Xline _:
            case Ray _:
                break; // 3D o infinitas: no sirven como tope en el plano
            default:
                AddCopy(curve);
                break;
        }
    }

    private void AddPolyline(Polyline polyline)
    {
        int vertexCount = polyline.NumberOfVertices;
        if (vertexCount < 2) return;
        int segmentCount = polyline.Closed ? vertexCount : vertexCount - 1;

        for (int i = 0; i < segmentCount; i++)
        {
            if (polyline.GetSegmentType(i) != SegmentType.Arc) continue;
            AddExploded(polyline); // con arcos: cada tramo pasa a ser línea o arco independiente
            return;
        }

        for (int i = 0; i < segmentCount; i++)
        {
            if (polyline.GetSegmentType(i) != SegmentType.Line) continue;
            AddSegment(polyline.GetPoint3dAt(i), polyline.GetPoint3dAt((i + 1) % vertexCount));
        }
    }

    private void AddExploded(Entity entity)
    {
        // No se hace Dispose de la colección: se conservan algunas de sus piezas.
        var pieces = new DBObjectCollection();
        try { entity.Explode(pieces); }
        catch
        {
            foreach (DBObject piece in pieces) piece.Dispose();
            return;
        }

        foreach (DBObject piece in pieces)
        {
            if (piece is Line line)
            {
                AddSegment(line.StartPoint, line.EndPoint);
                piece.Dispose();
            }
            else if (piece is Curve curve && TryGetExtents(curve, out Extents3d extents))
            {
                AddCurveObstacle(curve, extents);
            }
            else
            {
                piece.Dispose();
            }
        }
    }

    private void AddCopy(Curve curve)
    {
        if (!TryGetExtents(curve, out Extents3d extents)) return;
        if (curve.Clone() is Curve copy) AddCurveObstacle(copy, extents);
    }

    private void AddCurveObstacle(Curve owned, Extents3d extents)
    {
        _curves.Add(new CurveObstacle
        {
            Curve = owned,
            MinX = extents.MinPoint.X - BoxPadding,
            MinY = extents.MinPoint.Y - BoxPadding,
            MaxX = extents.MaxPoint.X + BoxPadding,
            MaxY = extents.MaxPoint.Y + BoxPadding
        });
        Grow(extents.MinPoint.X, extents.MinPoint.Y);
        Grow(extents.MaxPoint.X, extents.MaxPoint.Y);
    }

    private static bool TryGetExtents(Curve curve, out Extents3d extents)
    {
        try
        {
            extents = curve.GeometricExtents;
            return true;
        }
        catch
        {
            extents = default;
            return false;
        }
    }

    private void Grow(double x, double y)
    {
        if (x < _minX) _minX = x;
        if (y < _minY) _minY = y;
        if (x > _maxX) _maxX = x;
        if (y > _maxY) _maxY = y;
    }

    /// <summary>Largo suficiente para que un rayo atraviese todo lo recopilado.</summary>
    private double RayLength(Point3d origin)
    {
        if (_minX > _maxX) return 1.0;
        double dx = Math.Max(Math.Abs(origin.X - _minX), Math.Abs(origin.X - _maxX));
        double dy = Math.Max(Math.Abs(origin.Y - _minY), Math.Abs(origin.Y - _maxY));
        return Math.Sqrt(dx * dx + dy * dy) * 1.01 + 1.0;
    }

    private static bool RaySegment(double ox, double oy, double ux, double uy, Segment s, out double t)
    {
        t = 0.0;
        double vx = s.Bx - s.Ax, vy = s.By - s.Ay;
        double length = Math.Sqrt(vx * vx + vy * vy);
        if (length <= 0.0) return false;

        double denominator = ux * vy - uy * vx; // u x v  (u es unitario: |denominador| / largo = seno del ángulo)
        double wx = s.Ax - ox, wy = s.Ay - oy;
        if (Math.Abs(denominator) <= ParallelSine * length)
        {
            // Paralelo: solo choca si es colineal (el rayo corre por su misma recta); entonces choca con el extremo más cercano por delante.
            if (Math.Abs(wx * vy - wy * vx) / length > CollinearOffset) return false;
            double toA = wx * ux + wy * uy;
            double toB = (s.Bx - ox) * ux + (s.By - oy) * uy;
            double nearest = double.MaxValue;
            if (toA > MinHit) nearest = toA;
            if (toB > MinHit && toB < nearest) nearest = toB;
            if (nearest == double.MaxValue) return false;
            t = nearest;
            return true;
        }

        double distance = (wx * vy - wy * vx) / denominator;
        double along = (wx * uy - wy * ux) / denominator;
        if (distance <= MinHit || along < -SegmentTolerance || along > 1.0 + SegmentTolerance) return false;

        t = distance;
        return true;
    }

    private static bool RayCurve(CurveObstacle obstacle, Point3d origin, Vector3d direction, double rayLength, out double t)
    {
        t = 0.0;
        if (!RayHitsBox(origin.X, origin.Y, direction.X, direction.Y, rayLength, obstacle)) return false;

        using (var ray = new Line(new Point3d(origin.X, origin.Y, 0.0),
                   new Point3d(origin.X + direction.X * rayLength, origin.Y + direction.Y * rayLength, 0.0)))
        {
            var points = new Point3dCollection();
            try { obstacle.Curve.IntersectWith(ray, Intersect.OnBothOperands, XyPlane, points, IntPtr.Zero, IntPtr.Zero); }
            catch { return false; }

            bool found = false;
            double best = double.MaxValue;
            foreach (Point3d point in points)
            {
                double distance = (point.X - origin.X) * direction.X + (point.Y - origin.Y) * direction.Y;
                if (distance <= MinHit || distance >= best) continue;
                best = distance;
                found = true;
            }
            if (found) t = best;
            return found;
        }
    }

    private static bool RayHitsBox(double ox, double oy, double ux, double uy, double length, CurveObstacle box)
    {
        double tMin = 0.0, tMax = length;
        return Slab(ox, ux, box.MinX, box.MaxX, ref tMin, ref tMax) && Slab(oy, uy, box.MinY, box.MaxY, ref tMin, ref tMax);
    }

    private static bool Slab(double origin, double direction, double low, double high, ref double tMin, ref double tMax)
    {
        if (Math.Abs(direction) < 1e-12) return origin >= low && origin <= high;
        double t1 = (low - origin) / direction;
        double t2 = (high - origin) / direction;
        if (t1 > t2)
        {
            double swap = t1;
            t1 = t2;
            t2 = swap;
        }
        if (t1 > tMin) tMin = t1;
        if (t2 < tMax) tMax = t2;
        return tMin <= tMax;
    }
}
