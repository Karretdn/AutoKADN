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
        if (document is null) return;
        Editor editor = document.Editor;
        editor.WriteMessage("\n[KARP_NOMPRED] Nomenclatura predial. ESC o clic derecho para salir.\n");
        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        PromptPointResult pointResult;
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);
            var pointOptions = new PromptPointOptions("\nPrimer clic dentro del predio (ESC o clic derecho para cancelar): ") { AllowNone = true };
            pointResult = editor.GetPoint(pointOptions);
        }
        finally { Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu); }
        if (pointResult.Status == PromptStatus.Cancel || pointResult.Status == PromptStatus.None)
        {
            editor.WriteMessage("\n[KARP_NOMPRED] Herramienta finalizada.\n");
            return;
        }
        if (pointResult.Status != PromptStatus.OK) return;
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
        var options = new PromptStringOptions("\nEscriba la nomenclatura predial y presione ENTER (ESC o clic derecho para cancelar): ") { AllowSpaces = true, UseDefaultValue = false };
        PromptResult result = editor.GetString(options);
        if (result.Status != PromptStatus.OK) return null;
        string text = result.StringResult.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Point3d? ObtenerCentroPredial(Editor editor, Point3d clickPoint)
    {
        DBObjectCollection boundary = editor.TraceBoundary(clickPoint, false);
        if (boundary.Count == 0) return null;
        try
        {
            var curves = new DBObjectCollection();
            foreach (DBObject dbObject in boundary) if (dbObject is Curve) curves.Add(dbObject);
            if (curves.Count == 0) return null;
            DBObjectCollection regions = Region.CreateFromCurves(curves);
            Region? region = null;
            foreach (DBObject dbObject in regions) if (dbObject is Region candidate) { region = candidate; break; }
            if (region is null) return null;
            try
            {
                Point3d origin = Point3d.Origin; Vector3d xAxis = Vector3d.XAxis; Vector3d yAxis = Vector3d.YAxis;
                RegionAreaProperties properties = region.AreaProperties(ref origin, ref xAxis, ref yAxis);
                Point2d centroid = properties.Centroid;
                return new Point3d(centroid.X, centroid.Y, clickPoint.Z);
            }
            finally { foreach (DBObject dbObject in regions) dbObject.Dispose(); }
        }
        catch { return null; }
        finally { foreach (DBObject dbObject in boundary) if (!dbObject.IsDisposed) dbObject.Dispose(); }
    }
}
