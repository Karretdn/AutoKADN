using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AutoKADN.Core;

public sealed class TextCreationService
{
    private const double LineSearchTolerance = 20.0;
    private const double ParallelAngleTolerance = 5.0 * Math.PI / 180.0;

    public void CreateText(Point3d position, string content, double height = 1.45)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(content))
            return;

        Database database = document.Database;

        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId,
            OpenMode.ForWrite);

        var text = new DBText
        {
            Position = position,
            TextString = content.Trim(),
            Height = height,
            Layer = GetCurrentLayerName(database, transaction)
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);
        transaction.Commit();
    }

    public bool CreateTextWithJig(Point3d initialPosition, string content, double height = 1.45)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(content))
            return false;

        Database database = document.Database;
        Editor editor = document.Editor;

        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId,
            OpenMode.ForWrite);

        Point3d textPosition = ObtenerCentroEntreLineas(transaction, currentSpace, initialPosition)
            ?? initialPosition;

        var text = new DBText
        {
            TextString = content.Trim(),
            Height = height,
            Layer = GetCurrentLayerName(database, transaction),
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = textPosition,
            Position = textPosition
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);

        var jig = new NomenclaturaTextJig(text, textPosition, ObtenerModoOrto());
        PromptResult result = editor.Drag(jig);

        if (result.Status != PromptStatus.OK)
        {
            text.Erase();
            transaction.Commit();
            return false;
        }

        transaction.Commit();
        return true;
    }

    private static Point3d? ObtenerCentroEntreLineas(
        Transaction transaction,
        BlockTableRecord currentSpace,
        Point3d clickPoint)
    {
        var candidates = new List<LineCandidate>();

        foreach (ObjectId objectId in currentSpace)
        {
            if (objectId.ObjectClass.IsDerivedFrom(RXClass.GetClass(typeof(Line))))
            {
                var line = transaction.GetObject(objectId, OpenMode.ForRead) as Line;
                if (line is not null)
                    AgregarSegmento(candidates, line.StartPoint, line.EndPoint, clickPoint);
            }
            else if (objectId.ObjectClass.IsDerivedFrom(RXClass.GetClass(typeof(Polyline))))
            {
                var polyline = transaction.GetObject(objectId, OpenMode.ForRead) as Polyline;
                if (polyline is null)
                    continue;

                for (int i = 0; i < polyline.NumberOfVertices - 1; i++)
                {
                    if (polyline.GetSegmentType(i) != SegmentType.Line)
                        continue;

                    Point3d start = polyline.GetPoint3dAt(i);
                    Point3d end = polyline.GetPoint3dAt(i + 1);
                    AgregarSegmento(candidates, start, end, clickPoint);
                }

                if (polyline.Closed && polyline.NumberOfVertices > 1)
                {
                    int last = polyline.NumberOfVertices - 1;
                    if (polyline.GetSegmentType(last) == SegmentType.Line)
                    {
                        Point3d start = polyline.GetPoint3dAt(last);
                        Point3d end = polyline.GetPoint3dAt(0);
                        AgregarSegmento(candidates, start, end, clickPoint);
                    }
                }
            }
        }

        if (candidates.Count < 2)
            return null;

        LineCandidate? bestFirst = null;
        LineCandidate? bestSecond = null;
        double bestScore = double.MaxValue;

        for (int i = 0; i < candidates.Count - 1; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                LineCandidate first = candidates[i];
                LineCandidate second = candidates[j];

                if (!SonParalelas(first.Direction, second.Direction))
                    continue;

                double sideFirst = ObtenerDistanciaFirmada(first.Origin, first.Direction, clickPoint);
                double sideSecond = ObtenerDistanciaFirmada(second.Origin, second.Direction, clickPoint);

                if (Math.Sign(sideFirst) == Math.Sign(sideSecond))
                    continue;

                double score = first.Distance + second.Distance;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestFirst = first;
                    bestSecond = second;
                }
            }
        }

        if (bestFirst is null || bestSecond is null)
            return null;

        return new Point3d(
            (bestFirst.Projection.X + bestSecond.Projection.X) / 2.0,
            (bestFirst.Projection.Y + bestSecond.Projection.Y) / 2.0,
            (bestFirst.Projection.Z + bestSecond.Projection.Z) / 2.0);
    }

    private static void AgregarSegmento(
        List<LineCandidate> candidates,
        Point3d start,
        Point3d end,
        Point3d clickPoint)
    {
        Vector3d vector = end - start;
        if (vector.Length <= Tolerance.Global.EqualPoint)
            return;

        Vector3d direction = vector.GetNormal();
        Point3d projection = ProyectarSobreSegmento(start, end, clickPoint, direction);
        double distance = projection.DistanceTo(clickPoint);

        if (distance <= LineSearchTolerance)
            candidates.Add(new LineCandidate(start, direction, projection, distance));
    }

    private static Point3d ProyectarSobreSegmento(
        Point3d start,
        Point3d end,
        Point3d point,
        Vector3d direction)
    {
        double length = start.DistanceTo(end);
        double parameter = (point - start).DotProduct(direction);
        parameter = Math.Max(0.0, Math.Min(length, parameter));
        return start + direction * parameter;
    }

    private static double ObtenerDistanciaFirmada(Point3d origin, Vector3d direction, Point3d point)
    {
        Vector3d toPoint = point - origin;
        return direction.X * toPoint.Y - direction.Y * toPoint.X;
    }

    private static bool SonParalelas(Vector3d first, Vector3d second)
    {
        double cross = Math.Abs(first.X * second.Y - first.Y * second.X);
        double angle = Math.Asin(Math.Min(1.0, cross));
        return angle <= ParallelAngleTolerance;
    }

    private static bool ObtenerModoOrto()
    {
        try
        {
            object? value = Application.GetSystemVariable("ORTHOMODE");
            return Convert.ToInt32(value) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(
            database.Clayer,
            OpenMode.ForRead);

        return layer.Name;
    }

    private sealed record LineCandidate(
        Point3d Origin,
        Vector3d Direction,
        Point3d Projection,
        double Distance);

    private sealed class NomenclaturaTextJig : EntityJig
    {
        private readonly DBText _text;
        private readonly Point3d _initialPosition;
        private readonly bool _orthoEnabled;
        private Point3d _currentPosition;
        private double _currentRotation;

        public NomenclaturaTextJig(DBText text, Point3d initialPosition, bool orthoEnabled)
            : base(text)
        {
            _text = text;
            _initialPosition = initialPosition;
            _orthoEnabled = orthoEnabled;
            _currentPosition = initialPosition;
            _currentRotation = 0.0;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nMueva el texto y haga clic para terminar: ")
            {
                UseBasePoint = true,
                BasePoint = _initialPosition,
                UserInputControls = UserInputControls.Accept3dCoordinates
                    | UserInputControls.NoZeroResponseAccepted
            };

            PromptPointResult result = prompts.AcquirePoint(options);

            if (result.Status == PromptStatus.Cancel)
                return SamplerStatus.Cancel;

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            Point3d point = result.Value;
            double rotation = CalcularRotacion(_initialPosition, point, _orthoEnabled);

            if (point.DistanceTo(_currentPosition) < Tolerance.Global.EqualPoint
                && Math.Abs(rotation - _currentRotation) < 1e-9)
                return SamplerStatus.NoChange;

            _currentPosition = point;
            _currentRotation = rotation;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            _text.Position = _currentPosition;
            _text.AlignmentPoint = _currentPosition;
            _text.Rotation = _currentRotation;
            return true;
        }

        private static double CalcularRotacion(Point3d origin, Point3d point, bool orthoEnabled)
        {
            double dx = point.X - origin.X;
            double dy = point.Y - origin.Y;

            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
                return 0.0;

            double angle = Math.Atan2(dy, dx);

            if (orthoEnabled)
                angle = Math.Round(angle / (Math.PI / 2.0)) * (Math.PI / 2.0);

            return angle;
        }
    }
}
