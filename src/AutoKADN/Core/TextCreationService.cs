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
            database.CurrentSpaceId, OpenMode.ForWrite);

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
        Point3d centerPosition = ObtenerCentroEntreLineas(initialPosition) ?? initialPosition;
        return CreateTextWithJigAtFixedCenter(centerPosition, content, height);
    }

    public bool CreateTextWithJigAtFixedCenter(Point3d centerPosition, string content, double height = 1.45)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(content))
            return false;

        Database database = document.Database;
        Editor editor = document.Editor;
        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId, OpenMode.ForWrite);

        var text = new DBText
        {
            TextString = content.Trim(),
            Height = height,
            Layer = GetCurrentLayerName(database, transaction),
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = centerPosition,
            Position = centerPosition,
            Rotation = 0.0
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);

        // El centro ya fue calculado antes de entrar al jig.
        // Desde este punto el mouse solamente controla la rotacion.
        var jig = new NomenclaturaTextJig(text, centerPosition, ObtenerModoOrto());
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

    private static Point3d? ObtenerCentroEntreLineas(Point3d clickPoint)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return null;

        Database database = document.Database;
        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId, OpenMode.ForRead);

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

                    AgregarSegmento(
                        candidates,
                        polyline.GetPoint3dAt(i),
                        polyline.GetPoint3dAt(i + 1),
                        clickPoint);
                }

                if (polyline.Closed && polyline.NumberOfVertices > 1)
                {
                    int last = polyline.NumberOfVertices - 1;
                    if (polyline.GetSegmentType(last) == SegmentType.Line)
                    {
                        AgregarSegmento(
                            candidates,
                            polyline.GetPoint3dAt(last),
                            polyline.GetPoint3dAt(0),
                            clickPoint);
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

        transaction.Commit();

        return new Point3d(
            (bestFirst.Projection.X + bestSecond.Projection.X) / 2.0,
            (bestFirst.Projection.Y + bestSecond.Projection.Y) / 2.0,
            (bestFirst.Projection.Z + bestSecond.Projection.Z) / 2.0);
    }

    private static void AgregarSegmento(List<LineCandidate> candidates, Point3d start, Point3d end, Point3d clickPoint)
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

    private static Point3d ProyectarSobreSegmento(Point3d start, Point3d end, Point3d point, Vector3d direction)
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
        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(database.Clayer, OpenMode.ForRead);
        return layer.Name;
    }

    private sealed class LineCandidate
    {
        public Point3d Origin { get; }
        public Vector3d Direction { get; }
        public Point3d Projection { get; }
        public double Distance { get; }

        public LineCandidate(Point3d origin, Vector3d direction, Point3d projection, double distance)
        {
            Origin = origin;
            Direction = direction;
            Projection = projection;
            Distance = distance;
        }
    }

    private sealed class NomenclaturaTextJig : EntityJig
    {
        private readonly DBText _text;
        private readonly Point3d _center;
        private readonly bool _orthoEnabled;
        private double _currentRotation;

        public NomenclaturaTextJig(DBText text, Point3d center, bool orthoEnabled) : base(text)
        {
            _text = text;
            _center = center;
            _orthoEnabled = orthoEnabled;
            _currentRotation = 0.0;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptAngleOptions("\nIndique el angulo: ")
            {
                BasePoint = _center,
                UseBasePoint = true,
                Cursor = CursorType.RubberBand,
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.NoZeroResponseAccepted |
                                    UserInputControls.NoNegativeResponseAccepted
            };

            PromptDoubleResult result = prompts.AcquireAngle(options);

            if (result.Status == PromptStatus.Cancel)
                return SamplerStatus.Cancel;

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.NoChange;

            double angle = result.Value;

            if (_orthoEnabled)
            {
                double quarterTurn = Math.PI / 2.0;
                angle = Math.Round(angle / quarterTurn) * quarterTurn;
            }

            if (Math.Abs(angle - _currentRotation) < 1e-10)
                return SamplerStatus.NoChange;

            _currentRotation = angle;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            _text.Position = _center;
            _text.AlignmentPoint = _center;
            _text.Rotation = _currentRotation;
            return true;
        }
    }
}
