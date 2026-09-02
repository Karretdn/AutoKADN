using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Acotado;

/// <summary>
/// Cota de ubicación rápida entre dos líneas.
/// El primer punto queda exactamente donde se hace el snap y el segundo
/// se busca automáticamente sobre la línea recta que queda enfrente.
/// </summary>
public sealed class UbicacionTool
{
    private const double PointTolerance = 1e-5;
    private const double OverallDimensionScale = 0.05;
    private const short MagentaColorIndex = 6;
    private const string LayerName = "COTAS MAGENTA";

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[UBICACION] Cota rápida de ubicación. ESC o clic derecho para salir.\n");

        if (!EnsureLayer(document.Database))
        {
            editor.WriteMessage("\nNo fue posible crear o localizar la capa COTAS MAGENTA.\n");
            return;
        }

        while (CreateUbicacion(document, editor)) { }
    }

    private static bool CreateUbicacion(Autodesk.AutoCAD.ApplicationServices.Document document, Editor editor)
    {
        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        PromptEntityResult entityResult;
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);
            var options = new PromptEntityOptions("\nSeleccione un snap sobre la línea (ESC o clic derecho para salir): ")
            {
                AllowNone = true
            };
            options.AddAllowedClass(typeof(Line), false);
            options.AddAllowedClass(typeof(Polyline), false);
            entityResult = editor.GetEntity(options);
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }

        if (entityResult.Status != PromptStatus.OK)
            return false;

        if (!FindSourceSegment(
                document.Database,
                entityResult.ObjectId,
                entityResult.PickedPoint,
                out Point3d sourcePoint,
                out Vector3d sourceDirection))
        {
            editor.WriteMessage("\nNo se encontró un tramo recto válido en el objeto seleccionado.\n");
            return true;
        }

        List<StraightSegment> candidates = CollectStraightSegments(document.Database, entityResult.ObjectId);
        if (candidates.Count == 0)
        {
            editor.WriteMessage("\nNo se encontraron líneas rectas para buscar la línea de enfrente.\n");
            return true;
        }

        Vector3d baseNormal = new Vector3d(-sourceDirection.Y, sourceDirection.X, 0.0).GetNormal();
        Point3d initialTarget = sourcePoint + baseNormal * 10.0;
        Point3d initialDimensionPoint = sourcePoint + (initialTarget - sourcePoint) * 0.5;
        double initialRotation = Math.Atan2((initialTarget - sourcePoint).Y, (initialTarget - sourcePoint).X);

        ObjectId dimensionId = CreateTemporaryDimension(
            document.Database,
            sourcePoint,
            initialTarget,
            initialDimensionPoint,
            initialRotation);

        if (dimensionId == ObjectId.Null)
            return true;

        editor.Regen();

        Point3d? targetPoint = MoveToOppositeLine(
            document,
            editor,
            dimensionId,
            sourcePoint,
            sourceDirection,
            baseNormal,
            candidates);

        if (!targetPoint.HasValue)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        editor.Regen();

        object originalShortcutMenuForText = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        PromptResult textResult;
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);
            var textOptions = new PromptStringOptions("\nIngrese el valor de la cota y presione ENTER (ESC o clic derecho para cancelar): ")
            {
                AllowSpaces = false,
                UseDefaultValue = false
            };
            textResult = editor.GetString(textOptions);
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenuForText);
        }

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

    private static Point3d? MoveToOppositeLine(
        Autodesk.AutoCAD.ApplicationServices.Document document,
        Editor editor,
        ObjectId dimensionId,
        Point3d sourcePoint,
        Vector3d sourceDirection,
        Vector3d baseNormal,
        IReadOnlyList<StraightSegment> candidates)
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
            candidates);

        editor.WriteMessage("\nMueva el mouse para previsualizar la conexión con la línea de enfrente y haga clic para aceptar.\n");

        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        PromptResult result;
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);
            result = editor.Drag(jig);
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }

        if (result.Status != PromptStatus.OK || !jig.HasTarget)
        {
            transaction.Abort();
            return null;
        }

        transaction.Commit();
        return jig.TargetPoint;
    }

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

    private static bool FindSourceSegment(
        Database database,
        ObjectId objectId,
        Point3d pickedPoint,
        out Point3d sourcePoint,
        out Vector3d sourceDirection)
    {
        sourcePoint = Point3d.Origin;
        sourceDirection = Vector3d.XAxis;
        double bestDistance = double.MaxValue;
        bool found = false;

        using Transaction transaction = database.TransactionManager.StartTransaction();
        DBObject? selectedObject = transaction.GetObject(objectId, OpenMode.ForRead);

        if (selectedObject is Line line)
        {
            Vector3d vector = line.EndPoint - line.StartPoint;
            if (vector.Length > PointTolerance)
            {
                sourcePoint = GetClosestPointOnSegment(line.StartPoint, line.EndPoint, pickedPoint);
                sourceDirection = vector.GetNormal();
                transaction.Commit();
                return true;
            }
        }
        else if (selectedObject is Polyline polyline && polyline.NumberOfVertices >= 2)
        {
            int segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;

            for (int i = 0; i < segmentCount; i++)
            {
                if (polyline.GetSegmentType(i) != SegmentType.Line)
                    continue;

                int nextIndex = (i + 1) % polyline.NumberOfVertices;
                Point3d start = polyline.GetPoint3dAt(i);
                Point3d end = polyline.GetPoint3dAt(nextIndex);
                Vector3d vector = end - start;
                if (vector.Length <= PointTolerance)
                    continue;

                Point3d closest = GetClosestPointOnSegment(start, end, pickedPoint);
                double distance = closest.DistanceTo(pickedPoint);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    sourcePoint = closest;
                    sourceDirection = vector.GetNormal();
                    found = true;
                }
            }
        }

        transaction.Commit();
        return found;
    }

    private static Point3d GetClosestPointOnSegment(Point3d start, Point3d end, Point3d point)
    {
        Vector3d segment = end - start;
        double lengthSquared = segment.DotProduct(segment);
        if (lengthSquared <= PointTolerance * PointTolerance)
            return start;

        double parameter = (point - start).DotProduct(segment) / lengthSquared;
        parameter = Math.Max(0.0, Math.Min(1.0, parameter));
        return start + segment * parameter;
    }

    private static void AddSegment(List<StraightSegment> result, Point3d start, Point3d end)
    {
        Vector3d vector = end - start;
        if (vector.Length <= PointTolerance)
            return;

        result.Add(new StraightSegment(start, end, vector.GetNormal()));
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
            IReadOnlyList<StraightSegment> candidates)
            : base(dimension)
        {
            _dimension = dimension;
            _sourcePoint = sourcePoint;
            _sourceDirection = sourceDirection;
            _baseNormal = baseNormal.GetNormal();
            _candidates = candidates;
            _targetPoint = sourcePoint + _baseNormal * 10.0;
            _dimensionLinePoint = sourcePoint + (_targetPoint - sourcePoint) * 0.5;
            _rotation = Math.Atan2((_targetPoint - sourcePoint).Y, (_targetPoint - sourcePoint).X);
            _hasTarget = TryFindTarget(_baseNormal, 10.0, out Point3d initialTarget)
                ? SetTarget(initialTarget)
                : false;
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

            return SetTarget(target) ? SamplerStatus.OK : SamplerStatus.NoChange;
        }

        protected override bool Update()
        {
            _dimension.XLine1Point = _sourcePoint;
            _dimension.XLine2Point = _targetPoint;
            _dimension.DimLinePoint = _dimensionLinePoint;
            _dimension.Rotation = _rotation;
            return true;
        }

        private bool SetTarget(Point3d target)
        {
            Vector3d connection = target - _sourcePoint;
            if (connection.Length <= PointTolerance)
                return false;

            _targetPoint = target;
            _dimensionLinePoint = _sourcePoint + connection * 0.5;
            _rotation = Math.Atan2(connection.Y, connection.X);
            _hasTarget = true;
            return true;
        }

        private bool TryFindTarget(Vector3d searchDirection, double cursorDistance, out Point3d target)
        {
            target = Point3d.Origin;
            double nearestDistance = double.MaxValue;
            double farthestAllowedDistance = -1.0;
            Point3d farthestAllowedPoint = Point3d.Origin;
            bool foundNearest = false;
            bool foundWithinCursor = false;

            foreach (StraightSegment candidate in _candidates)
            {
                if (Math.Abs(candidate.Direction.DotProduct(_sourceDirection)) < 0.999)
                    continue;

                if (!TryIntersectRayWithSegment(_sourcePoint, searchDirection, candidate.Start, candidate.End, out double distance, out Point3d intersection))
                    continue;

                if (distance < PointTolerance)
                    continue;

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    target = intersection;
                    foundNearest = true;
                }

                if (distance <= cursorDistance + PointTolerance && distance > farthestAllowedDistance)
                {
                    farthestAllowedDistance = distance;
                    farthestAllowedPoint = intersection;
                    foundWithinCursor = true;
                }
            }

            if (foundWithinCursor)
            {
                target = farthestAllowedPoint;
                return true;
            }

            return foundNearest;
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
    }
}
