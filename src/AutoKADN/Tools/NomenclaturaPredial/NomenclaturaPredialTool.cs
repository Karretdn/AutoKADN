using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AutoKADN.Core;

namespace AutoKADN.Tools.NomenclaturaPredial;

public sealed class NomenclaturaPredialTool
{
    private readonly TextCreationService _textCreationService = new();

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[KARP_NOMPRED] Nomenclatura predial. ESC para salir.\n");

        PromptPointResult pointResult = editor.GetPoint("\nPrimer clic dentro del predio: ");
        if (pointResult.Status != PromptStatus.OK)
        {
            editor.WriteMessage("\n[KARP_NOMPRED] Herramienta finalizada.\n");
            return;
        }

        Point3d clickPoint = pointResult.Value;
        string? content = ObtenerTexto(editor);
        if (content is null)
        {
            editor.WriteMessage("\n[KARP_NOMPRED] Herramienta finalizada.\n");
            return;
        }

        Point3d center = ObtenerCentroPredial(document.Database, clickPoint) ?? clickPoint;

        if (!_textCreationService.CreateTextWithJigAtFixedCenter(center, content))
        {
            editor.WriteMessage("\n[KARP_NOMPRED] Herramienta cancelada.\n");
            return;
        }

        editor.WriteMessage($"\nTexto predial creado: {content}\n");
        editor.WriteMessage("\n[KARP_NOMPRED] Herramienta finalizada.\n");
    }

    private static string? ObtenerTexto(Editor editor)
    {
        var options = new PromptStringOptions("\nEscriba la nomenclatura predial y presione ENTER: ")
        {
            AllowSpaces = true,
            UseDefaultValue = false
        };

        PromptResult result = editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return null;

        string text = result.StringResult.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Point3d? ObtenerCentroPredial(Database database, Point3d clickPoint)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId, OpenMode.ForRead);

        // Caso 1: una polilínea cerrada representa directamente el predio.
        foreach (ObjectId objectId in currentSpace)
        {
            if (!objectId.ObjectClass.IsDerivedFrom(RXClass.GetClass(typeof(Polyline))))
                continue;

            var polyline = transaction.GetObject(objectId, OpenMode.ForRead) as Polyline;
            if (polyline is null || !polyline.Closed || polyline.NumberOfVertices < 3)
                continue;

            if (!PuntoDentroDePoligono(polyline, clickPoint))
                continue;

            Point3d center = CalcularCentroide(polyline);
            transaction.Commit();
            return center;
        }

        // Caso 2: el predio está construido con líneas/segmentos independientes.
        // Se crea una región temporal a partir de los contornos cercanos al clic.
        var curves = new DBObjectCollection();

        try
        {
            foreach (ObjectId objectId in currentSpace)
            {
                if (objectId.ObjectClass.IsDerivedFrom(RXClass.GetClass(typeof(Line))))
                {
                    var line = transaction.GetObject(objectId, OpenMode.ForRead) as Line;
                    if (line is null || !EstaCercaDelPunto(line.GeometricExtents, clickPoint))
                        continue;

                    curves.Add(new Line(line.StartPoint, line.EndPoint));
                }
                else if (objectId.ObjectClass.IsDerivedFrom(RXClass.GetClass(typeof(Polyline))))
                {
                    var polyline = transaction.GetObject(objectId, OpenMode.ForRead) as Polyline;
                    if (polyline is null || polyline.Closed || !EstaCercaDelPunto(polyline.GeometricExtents, clickPoint))
                        continue;

                    curves.Add(polyline.Clone());
                }
            }

            if (curves.Count == 0)
                return null;

            DBObjectCollection regions = Region.CreateFromCurves(curves);
            Region? bestRegion = null;
            double bestArea = double.MaxValue;

            foreach (DBObject dbObject in regions)
            {
                if (dbObject is not Region region)
                {
                    dbObject.Dispose();
                    continue;
                }

                if (!PuntoDentroDeExtensiones(region.GeometricExtents, clickPoint))
                {
                    region.Dispose();
                    continue;
                }

                Point3d origin = Point3d.Origin;
                Vector3d xAxis = Vector3d.XAxis;
                Vector3d yAxis = Vector3d.YAxis;
                RegionAreaProperties properties = region.AreaProperties(ref origin, ref xAxis, ref yAxis);

                if (properties.ImageArea <= 0.0)
                {
                    region.Dispose();
                    continue;
                }

                if (properties.ImageArea < bestArea)
                {
                    bestRegion?.Dispose();
                    bestRegion = region;
                    bestArea = properties.ImageArea;
                }
                else
                {
                    region.Dispose();
                }
            }

            if (bestRegion is null)
                return null;

            Point3d centroidOrigin = Point3d.Origin;
            Vector3d centroidXAxis = Vector3d.XAxis;
            Vector3d centroidYAxis = Vector3d.YAxis;
            RegionAreaProperties centroidProperties = bestRegion.AreaProperties(
                ref centroidOrigin,
                ref centroidXAxis,
                ref centroidYAxis);

            Point2d centroid = centroidProperties.Centroid;
            Point3d centerPoint = new Point3d(centroid.X, centroid.Y, clickPoint.Z);
            bestRegion.Dispose();
            transaction.Commit();
            return centerPoint;
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (DBObject curve in curves)
                curve.Dispose();
        }
    }

    private static bool EstaCercaDelPunto(Extents3d extents, Point3d point)
    {
        const double tolerance = 1000.0;
        return point.X >= extents.MinPoint.X - tolerance
            && point.X <= extents.MaxPoint.X + tolerance
            && point.Y >= extents.MinPoint.Y - tolerance
            && point.Y <= extents.MaxPoint.Y + tolerance;
    }

    private static bool PuntoDentroDeExtensiones(Extents3d extents, Point3d point)
    {
        const double tolerance = 1e-6;
        return point.X >= extents.MinPoint.X - tolerance
            && point.X <= extents.MaxPoint.X + tolerance
            && point.Y >= extents.MinPoint.Y - tolerance
            && point.Y <= extents.MaxPoint.Y + tolerance;
    }

    private static bool PuntoDentroDePoligono(Polyline polyline, Point3d point)
    {
        int count = polyline.NumberOfVertices;
        bool inside = false;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Point2d current = polyline.GetPoint2dAt(i);
            Point2d previous = polyline.GetPoint2dAt(j);

            bool intersects = ((current.Y > point.Y) != (previous.Y > point.Y))
                && point.X < (previous.X - current.X)
                    * (point.Y - current.Y)
                    / (previous.Y - current.Y)
                    + current.X;

            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    private static Point3d CalcularCentroide(Polyline polyline)
    {
        double areaDoble = 0.0;
        double centroX = 0.0;
        double centroY = 0.0;
        int count = polyline.NumberOfVertices;

        for (int i = 0; i < count; i++)
        {
            Point2d current = polyline.GetPoint2dAt(i);
            Point2d next = polyline.GetPoint2dAt((i + 1) % count);
            double cross = current.X * next.Y - next.X * current.Y;
            areaDoble += cross;
            centroX += (current.X + next.X) * cross;
            centroY += (current.Y + next.Y) * cross;
        }

        if (Math.Abs(areaDoble) < 1e-9)
            return polyline.GeometricExtents.MinPoint;

        return new Point3d(
            centroX / (3.0 * areaDoble),
            centroY / (3.0 * areaDoble),
            polyline.Elevation);
    }
}
