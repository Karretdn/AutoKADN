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

        Point3d center = ObtenerCentroPredial(editor, clickPoint) ?? clickPoint;

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

    private static Point3d? ObtenerCentroPredial(Editor editor, Point3d clickPoint)
    {
        // TraceBoundary usa el primer clic como punto semilla y obtiene
        // exclusivamente el recinto cerrado que contiene ese punto.
        // Esto evita confundir el predio con los espacios vecinos.
        DBObjectCollection boundary = editor.TraceBoundary(clickPoint, false);

        if (boundary.Count == 0)
            return null;

        try
        {
            var curves = new DBObjectCollection();

            foreach (DBObject dbObject in boundary)
            {
                if (dbObject is Curve)
                    curves.Add(dbObject);
            }

            if (curves.Count == 0)
                return null;

            DBObjectCollection regions = Region.CreateFromCurves(curves);
            Region? region = null;

            foreach (DBObject dbObject in regions)
            {
                if (dbObject is Region candidate)
                {
                    region = candidate;
                    break;
                }
            }

            if (region is null)
                return null;

            try
            {
                Point3d origin = Point3d.Origin;
                Vector3d xAxis = Vector3d.XAxis;
                Vector3d yAxis = Vector3d.YAxis;

                RegionAreaProperties properties = region.AreaProperties(
                    ref origin,
                    ref xAxis,
                    ref yAxis);

                Point2d centroid = properties.Centroid;

                return new Point3d(
                    centroid.X,
                    centroid.Y,
                    clickPoint.Z);
            }
            finally
            {
                foreach (DBObject dbObject in regions)
                    dbObject.Dispose();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (DBObject dbObject in boundary)
            {
                if (!dbObject.IsDisposed)
                    dbObject.Dispose();
            }
        }
    }
}
