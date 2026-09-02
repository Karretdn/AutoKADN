using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Anotaciones;

public sealed class LimiteTool
{
    private const double OffsetFromLine = 1.10;
    private const double TextHeight = 1.45;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[LIMIK] Límites. ESC para salir. Clic derecho para cambiar LB/LP/LC.\n");

        string? limite = SeleccionarLimite(editor);
        if (limite is null)
            return;

        while (true)
        {
            PromptEntityOptions entityOptions = new PromptEntityOptions(
                $"\nSeleccione la línea para colocar {limite} (clic derecho = cambiar tipo, ESC = salir): ");
            entityOptions.SetRejectMessage("\nDebe seleccionar una línea o una polilínea.");
            entityOptions.AddAllowedClass(typeof(Line), true);
            entityOptions.AddAllowedClass(typeof(Polyline), true);

            PromptEntityResult entityResult = editor.GetEntity(entityOptions);

            // Con el clic derecho configurado como ENTER, AutoCAD devuelve None.
            // Lo usamos como acceso rápido para cambiar LB/LP/LC sin salir.
            if (entityResult.Status == PromptStatus.None)
            {
                limite = SeleccionarLimite(editor, limite);
                if (limite is null)
                    return;
                continue;
            }

            if (entityResult.Status != PromptStatus.OK)
                return;

            if (!ObtenerSegmentoSeleccionado(
                    document.Database,
                    entityResult.ObjectId,
                    entityResult.PickedPoint,
                    out Point3d pointOnLine,
                    out Vector3d direction))
            {
                editor.WriteMessage("\nNo se pudo determinar el segmento seleccionado.\n");
                continue;
            }

            Point3d textPosition = CalcularPosicionTexto(pointOnLine, direction);
            double rotation = CalcularRotacionParalela(direction);

            if (!CrearTextoConGiro(editor, document.Database, textPosition, rotation, limite))
                return;
        }
    }

    private static string? SeleccionarLimite(Editor editor, string? actual = null)
    {
        string mensaje = actual is null
            ? "\nSeleccione límite [LB/LP/LC]: "
            : $"\nCambiar límite [LB/LP/LC] (actual: {actual}): ";

        var options = new PromptKeywordOptions(mensaje)
        {
            AllowNone = false
        };
        options.Keywords.Add("LB");
        options.Keywords.Add("LP");
        options.Keywords.Add("LC");

        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK
            ? result.StringResult.ToUpperInvariant()
            : null;
    }

    private static bool ObtenerSegmentoSeleccionado(
        Database database,
        ObjectId objectId,
        Point3d pickedPoint,
        out Point3d pointOnLine,
        out Vector3d direction)
    {
        pointOnLine = Point3d.Origin;
        direction = Vector3d.XAxis;
        using Transaction transaction = database.TransactionManager.StartTransaction();

        if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
        {
            Vector3d vector = line.EndPoint - line.StartPoint;
            if (vector.Length <= Tolerance.Global.EqualPoint) return false;
            direction = vector.GetNormal();
            pointOnLine = line.GetClosestPointTo(pickedPoint, false);
            transaction.Commit();
            return true;
        }

        if (transaction.GetObject(objectId, OpenMode.ForRead) is Polyline polyline)
        {
            if (polyline.NumberOfVertices < 2) return false;
            Point3d closestPoint = polyline.GetClosestPointTo(pickedPoint, false);
            double parameter = polyline.GetParameterAtPoint(closestPoint);
            int segmentIndex = (int)Math.Floor(parameter);

            if (segmentIndex >= polyline.NumberOfVertices - 1)
                segmentIndex = polyline.Closed ? polyline.NumberOfVertices - 1 : polyline.NumberOfVertices - 2;

            if (segmentIndex < 0 || polyline.GetSegmentType(segmentIndex) != SegmentType.Line) return false;

            Point3d start = polyline.GetPoint3dAt(segmentIndex);
            Point3d end = polyline.GetPoint3dAt((segmentIndex + 1) % polyline.NumberOfVertices);
            Vector3d vector = end - start;
            if (vector.Length <= Tolerance.Global.EqualPoint) return false;

            direction = vector.GetNormal();
            double distanceAlong = Math.Max(0.0, Math.Min(vector.Length,
                (closestPoint - start).DotProduct(direction)));
            pointOnLine = start + direction * distanceAlong;
            transaction.Commit();
            return true;
        }

        return false;
    }

    private static Point3d CalcularPosicionTexto(Point3d pointOnLine, Vector3d direction)
    {
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        return pointOnLine + normal * OffsetFromLine;
    }

    private static double CalcularRotacionParalela(Vector3d direction)
    {
        double rotation = Math.Atan2(direction.Y, direction.X);
        if (rotation > Math.PI / 2.0 || rotation <= -Math.PI / 2.0)
            rotation += rotation > 0.0 ? -Math.PI : Math.PI;
        return rotation;
    }

    private static bool CrearTextoConGiro(
        Editor editor,
        Database database,
        Point3d position,
        double rotation,
        string content)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(database.Clayer, OpenMode.ForRead);

        var text = new DBText
        {
            TextString = content,
            Height = TextHeight,
            Layer = layer.Name,
            ColorIndex = 4,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = position,
            Position = position,
            Rotation = rotation
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);

        var jig = new LimiteTextJig(text, position, rotation);
        PromptResult result = editor.Drag(jig);

        if (result.Status != PromptStatus.OK && result.Status != PromptStatus.None)
        {
            text.Erase();
            transaction.Commit();
            return false;
        }

        transaction.Commit();
        return true;
    }

    private sealed class LimiteTextJig : EntityJig
    {
        private readonly DBText _text;
        private readonly Point3d _position;
        private readonly double _rotation;

        public LimiteTextJig(DBText text, Point3d position, double rotation) : base(text)
        {
            _text = text;
            _position = position;
            _rotation = rotation;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(
                "\nHaga clic o clic derecho para confirmar (ESC para salir): ")
            {
                UseBasePoint = true,
                BasePoint = _position,
                UserInputControls = UserInputControls.Accept3dCoordinates
                    | UserInputControls.NoZeroResponseAccepted
            };

            PromptPointResult result = prompts.AcquirePoint(options);

            if (result.Status == PromptStatus.Cancel)
                return SamplerStatus.Cancel;

            if (result.Status == PromptStatus.None)
                return SamplerStatus.OK;

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            _text.Position = _position;
            _text.AlignmentPoint = _position;
            _text.Rotation = _rotation;
            return true;
        }
    }
}
