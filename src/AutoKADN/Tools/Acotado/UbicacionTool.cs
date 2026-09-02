using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Acotado;

/// <summary>
/// Cota rápida de ubicación entre dos líneas rectas paralelas.
/// El primer vértice se obtiene con Snap Cercano exactamente como LIMIK.
/// El segundo vértice se busca automáticamente sobre la línea recta de enfrente.
/// </summary>
public sealed class UbicacionTool
{
    private const double PointTolerance = 1e-5;
    private const double GeometryMatchTolerance = 1e-6;
    private const double OverallDimensionScale = 0.05;
    private const short NearestObjectSnap = 512;
    private const short MagentaColorIndex = 6;
    private const string LayerName = "COTAS MAGENTA";

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[UBICACION] Cota rápida de ubicación. Snap Cercano activo. ESC o clic derecho para salir.\n");

        if (!EnsureLayer(document.Database))
        {
            editor.WriteMessage("\nNo fue posible crear o localizar la capa COTAS MAGENTA.\n");
            return;
        }

        object originalOsMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");
        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");

        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", NearestObjectSnap);
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);

            while (CreateUbicacion(document, editor)) { }
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", originalOsMode);
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }
    }

    private static bool CreateUbicacion(Autodesk.AutoCAD.ApplicationServices.Document document, Editor editor)
    {
        var pointOptions = new PromptPointOptions("\nSeleccione un snap sobre la línea: ")
        {
            AllowNone = true
        };

        PromptPointResult pointResult = editor.GetPoint(pointOptions);
        if (pointResult.Status == PromptStatus.Cancel || pointResult.Status == PromptStatus.None)
            return false;
        if (pointResult.Status != PromptStatus.OK)
            return true;

        Point3d sourcePoint = pointResult.Value;
        if (!FindSourceSegment(
                document.Database,
                sourcePoint,
                out Vector3d sourceDirection,
                out ObjectId sourceObjectId))
        {
            editor.WriteMessage("\nEl snap seleccionado no corresponde a una línea o tramo recto válido.\n");
            return true;
        }

        List<StraightSegment> candidates = CollectStraightSegments(document.Database, sourceObjectId);
        if (candidates.Count == 0)
        {
            editor.WriteMessage("\nNo se encontraron líneas rectas para buscar la línea de enfrente.\n");
            return true;
        }

        Vector3d baseNormal = new Vector3d(-sourceDirection.Y, sourceDirection.X, 0.0).GetNormal();
        if (baseNormal.Y < 0.0)
            baseNormal = -baseNormal;

        // La línea de enfrente debe ser paralela a la línea seleccionada,
        // mientras que la búsqueda hacia ella se hace perpendicularmente.
        if (!FindNearestOppositeLine(
                sourcePoint,
                sourceDirection,
                baseNormal,
                candidates,
                out Point3d initialTarget))
        {
            editor.WriteMessage("\nNo se encontró una línea recta paralela de enfrente.\n");
            return true;
        }

        ObjectId dimensionId = CreateTemporaryDimension(
            document.Database,
            sourcePoint,
            initialTarget,
            sourcePoint + (initialTarget - sourcePoint) * 0.5,
            Math.Atan2((initialTarget - sourcePoint).Y, (initialTarget - sourcePoint).X));

        if (dimensionId == ObjectId.Null)
            return true;

        editor.Regen();

        Point3d? targetPoint = PreviewAndSelectTarget(
            document,
            editor,
            dimensionId,
            sourcePoint,
            sourceDirection,
            baseNormal,
            candidates,
            initialTarget);

        if (!targetPoint.HasValue)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        editor.Regen();

        PromptResult textResult = editor.GetString(
            new PromptStringOptions("\nIngrese el valor de la cota: ")
            {
                AllowSpaces = false,
                UseDefaultValue = false
            });

        if (textResult.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(textResult.StringResult))
        {
            EraseDimension(document.Database, dimensionId);
            return textResult.Status != PromptStatus.Cancel && textResult.Status != PromptStatus.None;
        }

        if (!SetFinalDimension(document.Database, dimensionId, textResult.StringResult.Trim()))
        {
            EraseDimension(document.Database, dimensionId);
            return true;
        }

        editor.Regen();
        return true;
    }

    private static ObjectId CreateTemporaryDimension(
        Database database,
        Point3d firstPoint,
        Point3d secondPoint,
        Point3d dimensionLinePoint,
        double rotation)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

        var dimension = new RotatedDimension(
            rotation,
            firstPoint,
            secondPoint,
            dimensionLinePoint,
            string.Empty,
            database.Dimstyle)
        {
            Dimscale = OverallDimensionScale,
            Dimtad = 0,
            Dimjust = 0,
            Layer = LayerName,
            ColorIndex = 256
        };

        currentSpace.AppendEntity(dimension);
        transaction.AddNewlyCreatedDBObject(dimension, true);
        transaction.Commit();
        return dimension.ObjectId;
    }

    private static Point3d? PreviewAndSelectTarget(
        Autodesk.AutoCAD.ApplicationServices.Document document,
        Editor editor,
        ObjectId dimensionId,
        Point3d sourcePoint,
        Vector3d sourceDirection,
        Vector3d baseNormal,
        IReadOnlyList<StraightSegment> candidates,
        Point3d initialTarget)
    {
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        var dimension = transaction.GetObject(dimensionId, OpenMode.ForWrite) as RotatedDimension;
        if (dimension is null)
        {
            transaction.Abort();
            return null;
        }

        var jig = new UbicacionJig(
            dimension,
            sourcePoint,
            sourceDirection,
            baseNormal,
            candidates,
            initialTarget);

        editor.WriteMessage("\nMueva el mouse para previsualizar la línea de enfrente y haga clic para aceptar.\n");

        PromptResult result = editor.Drag(jig);
        if (result.Status != PromptStatus.OK || !jig.HasTarget)
        {
            transaction.Abort();
            return null;
        }

        transaction.Commit();
        return jig.TargetPoint;
    }

    private static bool EnsureLayer(Database database)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);

        if (!layerTable.Has(LayerName))
        {
            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = LayerName,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                    MagentaColorIndex)
            };
            layerTable.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }

        transaction.Commit();
        return true;
    }

    private static bool FindSourceSegment(
        Database database,
        Point3d point,
        out Vector3d direction,
        out ObjectId sourceObjectId)
    {
        direction = Vector3d.XAxis;
        sourceObjectId = ObjectId.Null;
        double bestDistance = double.MaxValue;
        bool found = false;

        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);

        foreach (ObjectId objectId in currentSpace)
        {
            if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
            {
                Vector3d lineVector = line.EndPoint - line.StartPoint;
                if (lineVector.Length <= PointTolerance) continue;

                Point3d closest = line.GetClosestPointTo(point, false);
                double distance = closest.DistanceTo(point);
                if (distance <= GeometryMatchTolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    direction = lineVector.GetNormal();
                    sourceObjectId = objectId;
                    found = true;
                }

                continue;
            }

            if (transaction.GetObject(objectId, OpenMode.ForRead) is not Polyline polyline || polyline.NumberOfVertices < 2)
                continue;

            Point3d closestPoint = polyline.GetClosestPointTo(point, false);
            double polyDistance = closestPoint.DistanceTo(point);
            if (polyDistance > GeometryMatchTolerance || polyDistance >= bestDistance)
                continue;

            double parameter = polyline.GetParameterAtPoint(closestPoint);
            int segmentIndex = (int)Math.Floor(parameter);
            int segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            if (segmentIndex >= segmentCount)
                segmentIndex = segmentCount - 1;
            if (segmentIndex < 0 || polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                continue;

            int nextIndex = (segmentIndex + 1) % polyline.NumberOfVertices;
            Point3d start = polyline.GetPoint3dAt(segmentIndex);
            Point3d end = polyline.GetPoint3dAt(nextIndex);
            Vector3d segmentVector = end - start;
            if (segmentVector.Length <= PointTolerance) continue;

            bestDistance = polyDistance;
            direction = segmentVector.GetNormal();
            sourceObjectId = objectId;
            found = true;
        }

        transaction.Commit();
        return found;
    }

    private static List<StraightSegment> CollectStraightSegments(Database database, ObjectId excludedObjectId)
    {
        var result = new List<StraightSegment>();

        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);

        foreach (ObjectId objectId in currentSpace)
        {
            if (objectId == excludedObjectId)
                continue;

            if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
            {
                AddSegment(result, line.StartPoint, line.EndPoint);
                continue;
            }

            if (transaction.GetObject(objectId, OpenMode.ForRead) is not Polyline polyline || polyline.NumberOfVertices < 2)
                continue;

            int segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                if (polyline.GetSegmentType(i) != SegmentType.Line)
                    continue;

                int nextIndex = (i + 1) % polyline.NumberOfVertices;
                AddSegment(result, polyline.GetPoint3dAt(i), polyline.GetPoint3dAt(nextIndex));
            }
        }

        transaction.Commit();
        return result;
    }

    private static void AddSegment(List<StraightSegment> result, Point3d start, Point3d end)
    {
        Vector3d vector = end - start;
        if (vector.Length <= PointTolerance)
            return;

        result.Add(new StraightSegment(start, end, vector.GetNormal()));
    }

    private static bool FindNearestOppositeLine(
        Point3d sourcePoint,
        Vector3d lineDirection,
        Vector3d searchDirection,
        IReadOnlyList<StraightSegment> candidates,
        out Point3d target)
    {
        target = Point3d.Origin;
        double nearestDistance = double.MaxValue;
        bool found = false;

        foreach (StraightSegment candidate in candidates)
        {
            // La candidata correcta debe ser paralela a la línea de origen.
            if (!AreParallel(candidate.Direction, lineDirection))
                continue;

            // La intersección se busca sobre un rayo perpendicular a la línea de origen.
            if (!TryIntersectRayWithSegment(
                    sourcePoint,
                    searchDirection,
                    candidate.Start,
                    candidate.End,
                    out double distance,
                    out Point3d intersection))
                continue;

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                target = intersection;
                found = true;
            }
        }

        return found;
    }

    private static bool AreParallel(Vector3d first, Vector3d second)
    {
        double dot = Math.Abs(first.GetNormal().DotProduct(second.GetNormal()));
        return dot >= 0.999;
    }

    private static bool TryIntersectRayWithSegment(
        Point3d rayOrigin,
        Vector3d rayDirection,
        Point3d segmentStart,
        Point3d segmentEnd,
        out double rayDistance,
        out Point3d intersection)
    {
        rayDistance = 0.0;
        intersection = Point3d.Origin;

        Vector3d segmentDirection = segmentEnd - segmentStart;
        double denominator = Cross2d(rayDirection, segmentDirection);
        if (Math.Abs(denominator) <= PointTolerance)
            return false;

        Vector3d fromRayToSegment = segmentStart - rayOrigin;
        double t = Cross2d(fromRayToSegment, segmentDirection) / denominator;
        double u = Cross2d(fromRayToSegment, rayDirection) / denominator;

        if (t <= PointTolerance || u < -PointTolerance || u > 1.0 + PointTolerance)
            return false;

        rayDistance = t;
        intersection = rayOrigin + rayDirection * t;
        return true;
    }

    private static double Cross2d(Vector3d first, Vector3d second) =>
        first.X * second.Y - first.Y * second.X;

    private static bool SetFinalDimension(Database database, ObjectId dimensionId, string textValue)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        if (transaction.GetObject(dimensionId, OpenMode.ForWrite) is not Dimension dimension)
        {
            transaction.Abort();
            return false;
        }

        dimension.DimensionText = textValue;
        dimension.Layer = LayerName;
        dimension.ColorIndex = 256;
        transaction.Commit();
        return true;
    }

    private static void EraseDimension(Database database, ObjectId dimensionId)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        if (transaction.GetObject(dimensionId, OpenMode.ForWrite, false) is Entity entity)
            entity.Erase();
        transaction.Commit();
    }

    private sealed class StraightSegment
    {
        public Point3d Start { get; }
        public Point3d End { get; }
        public Vector3d Direction { get; }

        public StraightSegment(Point3d start, Point3d end, Vector3d direction)
        {
            Start = start;
            End = end;
            Direction = direction;
        }
    }

    private sealed class UbicacionJig : EntityJig
    {
        private readonly RotatedDimension _dimension;
        private readonly Point3d _sourcePoint;
        private readonly Vector3d _sourceDirection;
        private readonly Vector3d _baseNormal;
        private readonly IReadOnlyList<StraightSegment> _candidates;
        private Point3d _targetPoint;
        private Point3d _dimensionLinePoint;
        private double _rotation;
        private bool _hasTarget;

        public UbicacionJig(
            RotatedDimension dimension,
            Point3d sourcePoint,
            Vector3d sourceDirection,
            Vector3d baseNormal,
            IReadOnlyList<StraightSegment> candidates,
            Point3d initialTarget)
            : base(dimension)
        {
            _dimension = dimension;
            _sourcePoint = sourcePoint;
            _sourceDirection = sourceDirection.GetNormal();
            _baseNormal = baseNormal.GetNormal();
            _candidates = candidates;
            _targetPoint = initialTarget;
            _dimensionLinePoint = sourcePoint + (initialTarget - sourcePoint) * 0.5;
            _rotation = Math.Atan2((initialTarget - sourcePoint).Y, (initialTarget - sourcePoint).X);
            _hasTarget = true;
        }

        public bool HasTarget => _hasTarget;
        public Point3d TargetPoint => _targetPoint;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nMueva el mouse para elegir la línea de enfrente y haga clic para aceptar: ")
            {
                BasePoint = _sourcePoint,
                UseBasePoint = true,
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted
            };

            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            Vector3d cursorVector = result.Value - _sourcePoint;
            double signedSide = cursorVector.DotProduct(_baseNormal);
            if (Math.Abs(signedSide) <= PointTolerance)
                return SamplerStatus.NoChange;

            Vector3d searchDirection = signedSide >= 0.0 ? _baseNormal : -_baseNormal;
            double cursorDistance = Math.Abs(signedSide);

            if (!TryFindTarget(searchDirection, cursorDistance, out Point3d target))
                return SamplerStatus.NoChange;

            if (target.IsEqualTo(_targetPoint, new Tolerance(PointTolerance, PointTolerance)))
                return SamplerStatus.NoChange;

            _targetPoint = target;
            _dimensionLinePoint = _sourcePoint + (target - _sourcePoint) * 0.5;
            _rotation = Math.Atan2((target - _sourcePoint).Y, (target - _sourcePoint).X);
            _hasTarget = true;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            _dimension.XLine1Point = _sourcePoint;
            _dimension.XLine2Point = _targetPoint;
            _dimension.DimLinePoint = _dimensionLinePoint;
            _dimension.Rotation = _rotation;
            return true;
        }

        private bool TryFindTarget(Vector3d searchDirection, double cursorDistance, out Point3d target)
        {
            target = Point3d.Origin;
            double nearestDistance = double.MaxValue;
            double farthestWithinCursor = -1.0;
            Point3d nearestPoint = Point3d.Origin;
            Point3d farthestPoint = Point3d.Origin;
            bool found = false;
            bool foundWithinCursor = false;

            foreach (StraightSegment candidate in _candidates)
            {
                if (!AreParallel(candidate.Direction, _sourceDirection))
                    continue;

                if (!TryIntersectRayWithSegment(
                        _sourcePoint,
                        searchDirection,
                        candidate.Start,
                        candidate.End,
                        out double distance,
                        out Point3d intersection))
                    continue;

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestPoint = intersection;
                    found = true;
                }

                if (distance <= cursorDistance + PointTolerance && distance > farthestWithinCursor)
                {
                    farthestWithinCursor = distance;
                    farthestPoint = intersection;
                    foundWithinCursor = true;
                }
            }

            if (foundWithinCursor)
            {
                target = farthestPoint;
                return true;
            }

            if (found)
            {
                target = nearestPoint;
                return true;
            }

            return false;
        }
    }
}
