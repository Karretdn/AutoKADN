using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Anotaciones;

public sealed class LimiteTool
{
    private const double OffsetFromLine = 1.10;
    private const double TextHeight = 1.45;
    private const short NearestObjectSnap = 512;
    private const double GeometryMatchTolerance = 1e-6;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[LIMIK] Límites. Snap Cercano activo. ESC para salir.\n");

        object originalOsMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");

        try
        {
            // GetPoint permite que AutoCAD muestre el marcador real de OSNAP
            // (Cercano) en lugar del pickbox de GetEntity.
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable(
                "OSMODE", NearestObjectSnap);

            while (true)
            {
                var pointOptions = new PromptPointOptions(
                    "\nHaga clic sobre la línea (ESC para salir): ")
                {
                    AllowNone = false,
                    UseBasePoint = false,
                    UserInputControls = UserInputControls.Accept3dCoordinates
                        | UserInputControls.NoZeroResponseAccepted
                };

                // PRIMER Y ÚNICO CLIC: AutoCAD aplica OSNAP NEAREST y devuelve
                // el punto exacto sobre la línea. No se usa GetEntity, por lo
                // que desaparece el cuadrado de selección.
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status != PromptStatus.OK)
                    return;

                Point3d pointOnLine = pointResult.Value;

                if (!EncontrarSegmentoEnPunto(
                        document.Database,
                        pointOnLine,
                        out Vector3d direction))
                {
                    editor.WriteMessage("\nEl punto no corresponde a una línea o polilínea válida.\n");
                    continue;
                }

                // Se elige el tipo después del clic. Al escogerlo se coloca
                // inmediatamente y se vuelve a pedir el siguiente punto.
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

    private static bool EncontrarSegmentoEnPunto(
        Database database,
        Point3d point,
        out Vector3d direction)
    {
        direction = Vector3d.XAxis;
        double bestDistance = double.MaxValue;
        bool found = false;

        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId,
            OpenMode.ForRead);

        foreach (ObjectId objectId in currentSpace)
        {
            if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
            {
                Vector3d vector = line.EndPoint - line.StartPoint;
                if (vector.Length <= Tolerance.Global.EqualPoint)
                    continue;

                Point3d closest = line.GetClosestPointTo(point, false);
                double distance = closest.DistanceTo(point);

                if (distance <= GeometryMatchTolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    direction = vector.GetNormal();
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

            if (segmentIndex >= polyline.NumberOfVertices - 1)
            {
                segmentIndex = polyline.Closed
                    ? polyline.NumberOfVertices - 1
                    : polyline.NumberOfVertices - 2;
            }

            if (segmentIndex < 0 || polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                continue;

            Point3d start = polyline.GetPoint3dAt(segmentIndex);
            Point3d end = polyline.GetPoint3dAt((segmentIndex + 1) % polyline.NumberOfVertices);
            Vector3d vector = end - start;

            if (vector.Length <= Tolerance.Global.EqualPoint)
                continue;

            bestDistance = polyDistance;
            direction = vector.GetNormal();
            found = true;
        }

        transaction.Commit();
        return found;
    }

    private static Point3d CalcularPosicionTexto(Point3d pointOnLine, Vector3d direction)
    {
        // El clic fija exactamente el punto del eje. El texto queda 1.10
        // unidades por encima/perpendicular a la línea.
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
