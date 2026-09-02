using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;

namespace AutoKADN.Tools.Anotaciones;

public sealed class LimiteTool
{
    private const double OffsetFromLine = 2.00;
    private const double TextHeight = 2.40;
    private const short NearestObjectSnap = 512;
    private const double GeometryMatchTolerance = 1e-6;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        editor.WriteMessage("\n[LIMIK] Límites. Snap Cercano activo. ESC para salir.\n");
        object originalOsMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", NearestObjectSnap);
            while (true)
            {
                var pointOptions = new PromptPointOptions("\nHaga clic sobre la línea (ESC para salir): ");
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status != PromptStatus.OK) return;
                Point3d pointOnLine = pointResult.Value;
                if (!EncontrarSegmentoEnPunto(document.Database, pointOnLine, out Vector3d direction))
                {
                    editor.WriteMessage("\nEl punto no corresponde a una línea o polilínea válida.\n");
                    continue;
                }
                string? limite = SeleccionarLimite(editor);
                if (limite is null) return;
                if (!CrearTextoConJig(document, editor, pointOnLine, direction, limite)) return;
            }
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", originalOsMode);
        }
    }

    private static string? SeleccionarLimite(Editor editor)
    {
        var options = new PromptKeywordOptions("\n¿Qué desea colocar? [LB/LP/LC]: ") { AllowNone = false };
        options.Keywords.Add("LB"); options.Keywords.Add("LP"); options.Keywords.Add("LC");
        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK ? result.StringResult.ToUpperInvariant() : null;
    }

    private static bool EncontrarSegmentoEnPunto(Database database, Point3d point, out Vector3d direction)
    {
        direction = Vector3d.XAxis;
        double bestDistance = double.MaxValue;
        bool found = false;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId objectId in currentSpace)
        {
            if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
            {
                Vector3d lineVector = line.EndPoint - line.StartPoint;
                if (lineVector.Length <= Tolerance.Global.EqualPoint) continue;
                Point3d closest = line.GetClosestPointTo(point, false);
                double distance = closest.DistanceTo(point);
                if (distance <= GeometryMatchTolerance && distance < bestDistance)
                { bestDistance = distance; direction = lineVector.GetNormal(); found = true; }
                continue;
            }
            if (transaction.GetObject(objectId, OpenMode.ForRead) is not Polyline polyline || polyline.NumberOfVertices < 2) continue;
            Point3d closestPoint = polyline.GetClosestPointTo(point, false);
            double polyDistance = closestPoint.DistanceTo(point);
            if (polyDistance > GeometryMatchTolerance || polyDistance >= bestDistance) continue;
            double parameter = polyline.GetParameterAtPoint(closestPoint);
            int segmentIndex = (int)Math.Floor(parameter);
            if (segmentIndex >= polyline.NumberOfVertices - 1) segmentIndex = polyline.Closed ? polyline.NumberOfVertices - 1 : polyline.NumberOfVertices - 2;
            if (segmentIndex < 0 || polyline.GetSegmentType(segmentIndex) != SegmentType.Line) continue;
            Point3d start = polyline.GetPoint3dAt(segmentIndex);
            Point3d end = polyline.GetPoint3dAt((segmentIndex + 1) % polyline.NumberOfVertices);
            Vector3d segmentVector = end - start;
            if (segmentVector.Length <= Tolerance.Global.EqualPoint) continue;
            bestDistance = polyDistance; direction = segmentVector.GetNormal(); found = true;
        }
        transaction.Commit();
        return found;
    }

    private static double CalcularRotacionParalela(Vector3d direction)
    {
        return RotationStandard.MakeReadable(RotationStandard.FromDirection(direction, false));
    }

    private static bool CrearTextoConJig(Autodesk.AutoCAD.ApplicationServices.Document document, Editor editor, Point3d pointOnLine, Vector3d direction, string content)
    {
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        Point3d initialPosition = pointOnLine + normal * OffsetFromLine;
        double rotation = CalcularRotacionParalela(direction);
        ObjectId textId;
        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
        {
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(document.Database.Clayer, OpenMode.ForRead);
            var text = new DBText { TextString = content, Height = TextHeight, Layer = layer.Name, ColorIndex = 256, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = initialPosition, Position = initialPosition, Rotation = rotation };
            currentSpace.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true); textId = text.ObjectId; transaction.Commit();
        }
        editor.Regen();
        editor.WriteMessage("\nMueva el mouse al lado deseado y haga clic para fijar el texto.\n");
        using Transaction jigTransaction = document.Database.TransactionManager.StartTransaction();
        var textForJig = jigTransaction.GetObject(textId, OpenMode.ForWrite) as DBText;
        if (textForJig is null) { jigTransaction.Abort(); return false; }
        var jig = new LimitSideJig(textForJig, pointOnLine, normal, OffsetFromLine, RotationStandard.IsOrthoEnabled());
        PromptResult result = editor.Drag(jig);
        if (result.Status != PromptStatus.OK) { textForJig.Erase(); jigTransaction.Commit(); editor.Regen(); return false; }
        jigTransaction.Commit(); editor.Regen(); return true;
    }

    private sealed class LimitSideJig : EntityJig
    {
        private readonly DBText _text;
        private readonly Point3d _pointOnLine;
        private readonly Vector3d _normal;
        private readonly double _fixedOffset;
        private readonly bool _orthoEnabled;
        private Point3d _lastPoint;
        public LimitSideJig(DBText text, Point3d pointOnLine, Vector3d normal, double fixedOffset, bool orthoEnabled) : base(text)
        { _text = text; _pointOnLine = pointOnLine; _normal = normal.GetNormal(); _fixedOffset = fixedOffset; _orthoEnabled = orthoEnabled; _lastPoint = text.Position; }
        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nMueva el mouse al lado deseado y haga clic para fijar: ") { UseBasePoint = true, BasePoint = _pointOnLine };
            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Cancel || result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            Vector3d fromLine = result.Value - _pointOnLine;
            double signedDistance = fromLine.DotProduct(_normal);
            Vector3d placementNormal = signedDistance >= 0.0 ? _normal : -_normal;
            Point3d projectedPoint = _pointOnLine + placementNormal * _fixedOffset;
            if (projectedPoint.IsEqualTo(_lastPoint)) return SamplerStatus.NoChange;
            _lastPoint = projectedPoint;
            double rotation = RotationStandard.FromPoint(_pointOnLine, result.Value, _orthoEnabled);
            _text.Rotation = RotationStandard.MakeReadable(rotation);
            return SamplerStatus.OK;
        }
        protected override bool Update()
        { _text.Position = _lastPoint; _text.AlignmentPoint = _lastPoint; return true; }
    }
}
