using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Acotado;

/// <summary>
/// Cota rápida de ubicación entre dos líneas rectas paralelas.
/// El primer clic hace Snap Cercano y encuentra automáticamente la línea de enfrente.
/// El segundo paso permite corregir opcionalmente el snap; Enter/clic derecho acepta
/// el resultado automático. La cota final siempre queda perpendicular a las líneas.
/// </summary>
public sealed class UbicacionTool
{
    private const double PointTolerance = 1e-5;
    private const double GeometryMatchTolerance = 1e-4;
    private const double OverallDimensionScale = 0.05;
    private const short NearestObjectSnap = 512;
    private const short MagentaColorIndex = 6;
    private const string LayerName = "COTAS MAGENTA";

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;

        Editor editor = document.Editor;

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

    private static bool CreateUbicacion(
        Autodesk.AutoCAD.ApplicationServices.Document document,
        Editor editor)
    {
        var pointOptions = new PromptPointOptions("\nSeleccione un punto sobre la línea: ")
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
            editor.WriteMessage("\nEl punto seleccionado no corresponde a una línea o tramo recto válido.\n");
            return true;
        }

        List<StraightSegment> candidates = CollectStraightSegments(document.Database, sourceObjectId);
        if (candidates.Count == 0)
        {
            editor.WriteMessage("\nNo se encontraron líneas rectas para buscar la línea de enfrente.\n");
            return true;
        }

        Vector3d normal = new Vector3d(-sourceDirection.Y, sourceDirection.X, 0.0).GetNormal();

        if (!FindNearestOppositeLine(
                sourcePoint,
                sourceDirection,
                normal,
                candidates,
                out Point3d automaticTarget))
        {
            editor.WriteMessage("\nNo se encontró una línea recta paralela de enfrente.\n");
            return true;
        }

        ObjectId dimensionId = CreateDimension(
            document.Database,
            sourcePoint,
            automaticTarget);

        if (dimensionId == ObjectId.Null)
            return true;

        editor.Regen();

        Point3d? finalTarget = CorrectTargetIfNeeded(
            editor,
            document.Database,
            dimensionId,
            sourcePoint,
            sourceDirection,
            candidates,
            automaticTarget);

        if (!finalTarget.HasValue)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        editor.Regen();

        PromptResult textResult = editor.GetString(
            new PromptStringOptions("\nNúmero/texto de cota: ")
            {
                AllowSpaces = false,
                UseDefaultValue = false
            });

        if (textResult.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(textResult.StringResult))
        {
            EraseDimension(document.Database, dimensionId);
            return textResult.Status != PromptStatus.Cancel && textResult.Status != PromptStatus.None;
        }

        if (!SetFinalDimension(
                document.Database,
                dimensionId,
                textResult.StringResult.Trim()))
        {
            EraseDimension(document.Database, dimensionId);
            return true;
        }

        editor.Regen();
        return true;
    }

    private static Point3d? CorrectTargetIfNeeded(
        Editor editor,
        Database database,
        ObjectId dimensionId,
        Point3d sourcePoint,
        Vector3d sourceDirection,
        IReadOnlyList<StraightSegment> candidates,
        Point3d automaticTarget)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();

        if (transaction.GetObject(dimensionId, OpenMode.ForWrite) is not RotatedDimension dimension)
        {
            transaction.Abort();
            return null;
        }

        ApplyDimensionGeometry(dimension, sourcePoint, automaticTarget);
        editor.Regen();

        var correctionOptions = new PromptPointOptions(
            "\nMueva el cursor para corregir el snap; ENTER/clic derecho para aceptar: ")
        {
            AllowNone = true,
            UseBasePoint = true,
            BasePoint = sourcePoint
        };

        PromptPointResult correctionResult = editor.GetPoint(correctionOptions);

        if (correctionResult.Status == PromptStatus.None)
        {
            transaction.Commit();
            return automaticTarget;
        }

        if (correctionResult.Status != PromptStatus.OK)
        {
            transaction.Commit();
            return automaticTarget;
        }

        if (!FindManualTarget(
                sourcePoint,
                sourceDirection,
                correctionResult.Value,
                candidates,
                out Point3d correctedTarget))
        {
            transaction.Commit();
            return automaticTarget;
        }

        ApplyDimensionGeometry(dimension, sourcePoint, correctedTarget);
        transaction.Commit();
        return correctedTarget;
    }

    private static bool FindManualTarget(
        Point3d sourcePoint,
        Vector3d sourceDirection,
        Point3d cursorPoint,
        IReadOnlyList<StraightSegment> candidates,
        out Point3d target)
    {
        target = Point3d.Origin;
        Vector3d normal = new Vector3d(-sourceDirection.Y, sourceDirection.X, 0.0).GetNormal();

        double cursorSide = (cursorPoint - sourcePoint).DotProduct(normal);
        if (Math.Abs(cursorSide) <= PointTolerance)
            return false;

        Vector3d searchDirection = cursorSide >= 0.0 ? normal : -normal;

        double bestCursorDistance = double.MaxValue;
        double bestAlongRayDistance = double.MaxValue;
        bool found = false;

        foreach (StraightSegment candidate in candidates)
        {
            if (!AreParallel(candidate.Direction, sourceDirection))
                continue;

            if (!TryIntersectRayWithSegment(
                    sourcePoint,
                    searchDirection,
                    candidate.Start,
                    candidate.End,
                    out double rayDistance,
                    out Point3d intersection))
                continue;

            double cursorDistance = intersection.DistanceTo(cursorPoint);

            if (cursorDistance < bestCursorDistance ||
                (Math.Abs(cursorDistance - bestCursorDistance) <= PointTolerance &&
                 rayDistance < bestAlongRayDistance))
            {
                bestCursorDistance = cursorDistance;
                bestAlongRayDistance = rayDistance;
                target = intersection;
                found = true;
            }
        }

        return found;
    }

    private static ObjectId CreateDimension(
        Database database,
        Point3d firstPoint,
        Point3d secondPoint)
    {
        Vector3d dimensionVector = secondPoint - firstPoint;
        if (dimensionVector.Length <= PointTolerance)
            return ObjectId.Null;

        double rotation = Math.Atan2(dimensionVector.Y, dimensionVector.X);
        Point3d dimensionLinePoint = firstPoint + dimensionVector * 0.5;

        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace =
            (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

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
            Dimtih = true,
            Dimtoh = true,
            Layer = LayerName,
            ColorIndex = 256
        };

        currentSpace.AppendEntity(dimension);
        transaction.AddNewlyCreatedDBObject(dimension, true);
        transaction.Commit();
        return dimension.ObjectId;
    }

    private static void ApplyDimensionGeometry(
        RotatedDimension dimension,
        Point3d firstPoint,
        Point3d secondPoint)
    {
        Vector3d dimensionVector = secondPoint - firstPoint;
        if (dimensionVector.Length <= PointTolerance)
            return;

        dimension.XLine1Point = firstPoint;
        dimension.XLine2Point = secondPoint;
        dimension.DimLinePoint = firstPoint + dimensionVector * 0.5;
        dimension.Rotation = Math.Atan2(dimensionVector.Y, dimensionVector.X);
    }

    private static bool EnsureLayer(Database database)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        LayerTable layerTable =
            (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);

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
        BlockTableRecord currentSpace =
            (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);

        foreach (ObjectId objectId in currentSpace)
        {
            if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
            {
                Vector3d lineVector = line.EndPoint - line.StartPoint;
                if (lineVector.Length <= PointTolerance)
                    continue;

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

            if (transaction.GetObject(objectId, OpenMode.ForRead) is not Polyline polyline ||
                polyline.NumberOfVertices < 2)
                continue;

            Point3d closestPoint = polyline.GetClosestPointTo(point, false);
            double polyDistance = closestPoint.DistanceTo(point);

            if (polyDistance > GeometryMatchTolerance || polyDistance >= bestDistance)
                continue;

            double parameter = polyline.GetParameterAtPoint(closestPoint);
            int segmentIndex = (int)Math.Floor(parameter);
            int segmentCount = polyline.Closed
                ? polyline.NumberOfVertices
                : polyline.NumberOfVertices - 1;

            if (segmentIndex >= segmentCount)
                segmentIndex = segmentCount - 1;

            if (segmentIndex < 0 || polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                continue;

            int nextIndex = (segmentIndex + 1) % polyline.NumberOfVertices;
            Point3d start = polyline.GetPoint3dAt(segmentIndex);
            Point3d end = polyline.GetPoint3dAt(nextIndex);
            Vector3d segmentVector = end - start;

            if (segmentVector.Length <= PointTolerance)
                continue;

            bestDistance = polyDistance;
            direction = segmentVector.GetNormal();
            sourceObjectId = objectId;
            found = true;
        }

        transaction.Commit();
        return found;
    }

    private static List<StraightSegment> CollectStraightSegments(
        Database database,
        ObjectId excludedObjectId)
    {
        var result = new List<StraightSegment>();

        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace =
            (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);

        foreach (ObjectId objectId in currentSpace)
        {
            if (objectId == excludedObjectId)
                continue;

            if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
            {
                AddSegment(result, line.StartPoint, line.EndPoint);
                continue;
            }

            if (transaction.GetObject(objectId, OpenMode.ForRead) is not Polyline polyline ||
                polyline.NumberOfVertices < 2)
                continue;

            int segmentCount = polyline.Closed
                ? polyline.NumberOfVertices
                : polyline.NumberOfVertices - 1;

            for (int i = 0; i < segmentCount; i++)
            {
                if (polyline.GetSegmentType(i) != SegmentType.Line)
                    continue;

                int nextIndex = (i + 1) % polyline.NumberOfVertices;
                AddSegment(
                    result,
                    polyline.GetPoint3dAt(i),
                    polyline.GetPoint3dAt(nextIndex));
            }
        }

        transaction.Commit();
        return result;
    }

    private static void AddSegment(
        List<StraightSegment> result,
        Point3d start,
        Point3d end)
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

        foreach (Vector3d direction in new[] { searchDirection, -searchDirection })
        {
            foreach (StraightSegment candidate in candidates)
            {
                if (!AreParallel(candidate.Direction, lineDirection))
                    continue;

                if (!TryIntersectRayWithSegment(
                        sourcePoint,
                        direction,
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

        if (t <= PointTolerance ||
            u < -PointTolerance ||
            u > 1.0 + PointTolerance)
            return false;

        rayDistance = t;
        intersection = rayOrigin + rayDirection * t;
        return true;
    }

    private static double Cross2d(Vector3d first, Vector3d second) =>
        first.X * second.Y - first.Y * second.X;

    private static bool SetFinalDimension(
        Database database,
        ObjectId dimensionId,
        string textValue)
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
}
