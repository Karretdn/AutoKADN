using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Anotaciones;

public sealed class LimiteTool
{
    private const double OffsetFromLine = 1.10;
    private const double TextHeight = 1.45;
    private const short NearestObjectSnap = 512;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[LIMIK] Límites. Haga clic sobre la línea. ESC para salir.\n");

        object originalOsMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");

        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable(
                "OSMODE", NearestObjectSnap);

            while (true)
            {
                PromptEntityOptions entityOptions = new PromptEntityOptions(
                    "\nHaga clic sobre la línea (ESC para salir): ");
                entityOptions.SetRejectMessage("\nDebe seleccionar una línea o una polilínea.");
                entityOptions.AddAllowedClass(typeof(Line), true);
                entityOptions.AddAllowedClass(typeof(Polyline), true);

                PromptEntityResult entityResult = editor.GetEntity(entityOptions);
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

                string? limite = SeleccionarLimite(editor);
                if (limite is null)
                    return;

                CrearTexto(document.Database, pointOnLine, direction, limite);
            }
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable(
                "OSMODE", originalOsMode);
        }
    }

    private static string? SeleccionarLimite(Editor editor)
    {
        var options = new PromptKeywordOptions(
            "\n¿Qué desea colocar? [LB/LP/LC]: ")
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
            if (vector.Length <= Tolerance.Global.EqualPoint)
                return false;

            direction = vector.GetNormal();
            pointOnLine = line.GetClosestPointTo(pickedPoint, false);
            transaction.Commit();
            return true;
        }

        if (transaction.GetObject(objectId, OpenMode.ForRead) is Polyline polyline)
        {
            if (polyline.NumberOfVertices < 2)
                return false;

            Point3d closestPoint = polyline.GetClosestPointTo(pickedPoint, false);
            double parameter = polyline.GetParameterAtPoint(closestPoint);
            int segmentIndex = (int)Math.Floor(parameter);

            if (segmentIndex >= polyline.NumberOfVertices - 1)
            {
                segmentIndex = polyline.Closed
                    ? polyline.NumberOfVertices - 1
                    : polyline.NumberOfVertices - 2;
            }

            if (segmentIndex < 0 || polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                return false;

            Point3d start = polyline.GetPoint3dAt(segmentIndex);
            Point3d end = polyline.GetPoint3dAt((segmentIndex + 1) % polyline.NumberOfVertices);
            Vector3d vector = end - start;

            if (vector.Length <= Tolerance.Global.EqualPoint)
                return false;

            direction = vector.GetNormal();
            double distanceAlong = Math.Max(
                0.0,
                Math.Min(vector.Length, (closestPoint - start).DotProduct(direction)));

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

    private static void CrearTexto(
        Database database,
        Point3d pointOnLine,
        Vector3d direction,
        string content)
    {
        Point3d textPosition = CalcularPosicionTexto(pointOnLine, direction);
        double rotation = CalcularRotacionParalela(direction);

        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId,
            OpenMode.ForWrite);

        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(
            database.Clayer,
            OpenMode.ForRead);

        var text = new DBText
        {
            TextString = content,
            Height = TextHeight,
            Layer = layer.Name,
            ColorIndex = 256,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = textPosition,
            Position = textPosition,
            Rotation = rotation
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);
        transaction.Commit();
    }
}
