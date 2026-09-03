using Autodesk.AutoCAD.Colors;
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
    private const double VertexMatchTolerance = 2.00;
    private const string MagentaLayer = "COTAS MAGENTA";
    private const string DashedLinetype = "DASHED";
    private const double DashedLinetypeScale = 5.0;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        editor.WriteMessage("\n[LIMIK] Límites. Snap Cercano activo. ESC o clic derecho para salir.\n");
        object originalOsMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");
        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", NearestObjectSnap);
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);
            while (true)
            {
                var pointOptions = new PromptPointOptions("\nHaga clic sobre la línea (ESC o clic derecho para salir): ") { AllowNone = true };
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status == PromptStatus.Cancel || pointResult.Status == PromptStatus.None) return;
                if (pointResult.Status != PromptStatus.OK) continue;
                Point3d pointOnLine = pointResult.Value;
                if (!EncontrarSegmentoEnPunto(document.Database, pointOnLine, out Vector3d direction))
                {
                    editor.WriteMessage("\nEl punto no corresponde a una línea o polilínea válida.\n");
                    continue;
                }
                string? limite = SeleccionarLimite(editor);
                if (limite is null) return;
                if (limite == "0.0")
                {
                    string? limiteCero = SeleccionarLimiteCero(editor);
                    if (limiteCero is null) return;
                    if (!CrearTextoCeroConJig(document, editor, pointOnLine, direction, limiteCero)) return;
                    continue;
                }
                if (!CrearTextoConJig(document, editor, pointOnLine, direction, limite)) return;
            }
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", originalOsMode);
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }
    }

    private static string? SeleccionarLimite(Editor editor)
    {
        var options = new PromptKeywordOptions("\n¿Qué desea colocar? [LB/LP/LC/0.0]: ") { AllowNone = true };
        options.Keywords.Add("LB"); options.Keywords.Add("LP"); options.Keywords.Add("LC"); options.Keywords.Add("0.0");
        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK ? result.StringResult.ToUpperInvariant() : null;
    }

    private static string? SeleccionarLimiteCero(Editor editor)
    {
        var options = new PromptKeywordOptions("\n¿Qué límite 0.0 desea colocar? [LC0.0/LP0.0/LB0.0]: ") { AllowNone = true };
        options.Keywords.Add("LC0.0"); options.Keywords.Add("LP0.0"); options.Keywords.Add("LB0.0");
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
                {
                    bestDistance = distance;
                    direction = lineVector.GetNormal();
                    found = true;
                }
                continue;
            }
            if (transaction.GetObject(objectId, OpenMode.ForRead) is not Polyline polyline || polyline.NumberOfVertices < 2) continue;
            Point3d closestPoint = polyline.GetClosestPointTo(point, false);
            double polyDistance = closestPoint.DistanceTo(point);
            if (polyDistance > GeometryMatchTolerance || polyDistance >= bestDistance) continue;
            double parameter = polyline.GetParameterAtPoint(closestPoint);
            int segmentIndex = (int)Math.Floor(parameter);
            if (segmentIndex >= polyline.NumberOfVertices - 1)
                segmentIndex = polyline.Closed ? polyline.NumberOfVertices - 1 : polyline.NumberOfVertices - 2;
            if (segmentIndex < 0 || polyline.GetSegmentType(segmentIndex) != SegmentType.Line) continue;
            Point3d start = polyline.GetPoint3dAt(segmentIndex);
            Point3d end = polyline.GetPoint3dAt((segmentIndex + 1) % polyline.NumberOfVertices);
            Vector3d segmentVector = end - start;
            if (segmentVector.Length <= Tolerance.Global.EqualPoint) continue;
            bestDistance = polyDistance;
            direction = segmentVector.GetNormal();
            found = true;
        }
        transaction.Commit();
        return found;
    }

    private static double CalcularRotacionParalela(Vector3d direction)
    {
        double rotation = Math.Atan2(direction.Y, direction.X);
        if (rotation > Math.PI / 2.0 || rotation <= -Math.PI / 2.0)
            rotation += rotation > 0.0 ? -Math.PI : Math.PI;
        return rotation;
    }

    private static bool CrearTextoConJig(Autodesk.AutoCAD.ApplicationServices.Document document, Editor editor, Point3d pointOnLine, Vector3d direction, string content)
    {
        return CrearTextoConJigEnCapa(document, editor, pointOnLine, direction, content, null);
    }

    private static bool CrearTextoConJigEnCapa(Autodesk.AutoCAD.ApplicationServices.Document document, Editor editor, Point3d pointOnLine, Vector3d direction, string content, string? forcedLayer)
    {
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        Point3d initialPosition = pointOnLine + normal * OffsetFromLine;
        double rotation = CalcularRotacionParalela(direction);
        ObjectId textId;
        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
        {
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            string layerName = forcedLayer ?? ((LayerTableRecord)transaction.GetObject(document.Database.Clayer, OpenMode.ForRead)).Name;
            var text = new DBText { TextString = content, Height = TextHeight, Layer = layerName, ColorIndex = 256, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = initialPosition, Position = initialPosition, Rotation = rotation };
            currentSpace.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true); textId = text.ObjectId; transaction.Commit();
        }
        editor.Regen();
        editor.WriteMessage("\nMueva el mouse al lado deseado y haga clic para fijar el texto. ESC o clic derecho cancela.\n");
        using Transaction jigTransaction = document.Database.TransactionManager.StartTransaction();
        var textForJig = jigTransaction.GetObject(textId, OpenMode.ForWrite) as DBText;
        if (textForJig is null) { jigTransaction.Abort(); return false; }
        var jig = new LimitSideJig(textForJig, pointOnLine, normal, OffsetFromLine, rotation);
        PromptResult result = editor.Drag(jig);
        if (result.Status != PromptStatus.OK) { textForJig.Erase(); jigTransaction.Commit(); editor.Regen(); return false; }
        jigTransaction.Commit(); editor.Regen(); return true;
    }

    private static bool CrearTextoCeroConJig(Autodesk.AutoCAD.ApplicationServices.Document document, Editor editor, Point3d pointOnLine, Vector3d direction, string content)
    {
        Point3d safePoint = AjustarPuntoAlInteriorDelSegmento(document.Database, pointOnLine, direction);
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        Point3d initialPosition = safePoint + normal * OffsetFromLine;
        double rotation = CalcularRotacionParalela(direction);
        ObjectId textId;
        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
        {
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            string layerName = EnsureMagentaLayer(document.Database, transaction);
            var text = new DBText { TextString = content, Height = TextHeight, Layer = layerName, ColorIndex = 256, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = initialPosition, Position = initialPosition, Rotation = rotation };
            currentSpace.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true); textId = text.ObjectId; transaction.Commit();
        }

        editor.Regen();
        editor.WriteMessage("\nMueva el mouse al lado deseado y haga clic para fijar el texto. ESC o clic derecho cancela.\n");
        using (Transaction jigTransaction = document.Database.TransactionManager.StartTransaction())
        {
            var textForJig = jigTransaction.GetObject(textId, OpenMode.ForWrite) as DBText;
            if (textForJig is null) { jigTransaction.Abort(); return false; }
            var jig = new LimitSideJig(textForJig, safePoint, normal, OffsetFromLine, rotation);
            PromptResult result = editor.Drag(jig);
            if (result.Status != PromptStatus.OK)
            {
                textForJig.Erase();
                jigTransaction.Commit();
                editor.Regen();
                return false;
            }
            jigTransaction.Commit();
        }

        editor.Regen();
        PromptPointResult first = ObtenerVerticeConSnap(editor, "\nPrimer vértice de la lineta: ");
        if (first.Status != PromptStatus.OK) return EliminarTexto(document, textId);
        Point3d firstVertex = first.Value;

        PromptPointResult second = ObtenerVerticeConSnap(editor, "\nSegundo vértice de la lineta: ", firstVertex);
        if (second.Status != PromptStatus.OK) return EliminarTexto(document, textId);
        Point3d secondVertex = second.Value;

        if (!EsVerticeValido(document.Database, firstVertex) || !EsVerticeValido(document.Database, secondVertex))
        {
            editor.WriteMessage("\nLos puntos seleccionados deben corresponder a vértices. No se creó la anotación 0.0.\n");
            EliminarTexto(document, textId);
            return true;
        }

        if (firstVertex.DistanceTo(secondVertex) <= (OffsetFromLine * 2.0) + GeometryMatchTolerance)
        {
            editor.WriteMessage("\nLos vértices seleccionados están demasiado cerca para generar la lineta. No se creó la anotación 0.0.\n");
            EliminarTexto(document, textId);
            return true;
        }

        if (!CrearLinetaMagenta(document, editor, firstVertex, secondVertex))
        {
            editor.WriteMessage("\nNo se pudo crear la lineta. No se creó la anotación 0.0.\n");
            EliminarTexto(document, textId);
            return true;
        }

        return true;
    }

    private static PromptPointResult ObtenerVerticeConSnap(Editor editor, string message, Point3d? basePoint = null)
    {
        object originalOsMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", 1);
            var options = new PromptPointOptions(message) { AllowNone = true };
            if (basePoint.HasValue)
            {
                options.UseBasePoint = true;
                options.BasePoint = basePoint.Value;
            }
            return editor.GetPoint(options);
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", originalOsMode);
        }
    }

    private static bool EsVerticeValido(Database database, Point3d point)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId objectId in currentSpace)
        {
            Entity? entity = transaction.GetObject(objectId, OpenMode.ForRead) as Entity;
            if (entity is Line line && (line.StartPoint.DistanceTo(point) <= VertexMatchTolerance || line.EndPoint.DistanceTo(point) <= VertexMatchTolerance)) return true;
            if (entity is Polyline polyline)
            {
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                    if (polyline.GetPoint3dAt(i).DistanceTo(point) <= VertexMatchTolerance) return true;
            }
        }
        transaction.Commit();
        return false;
    }

    private static bool EliminarTexto(Autodesk.AutoCAD.ApplicationServices.Document document, ObjectId textId)
    {
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        if (!textId.IsNull && !textId.IsErased)
        {
            Entity? text = transaction.GetObject(textId, OpenMode.ForWrite, false) as Entity;
            text?.Erase();
        }
        transaction.Commit();
        document.Editor.Regen();
        return false;
    }

    private static Point3d AjustarPuntoAlInteriorDelSegmento(Database database, Point3d point, Vector3d direction)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        double bestDistance = double.MaxValue;
        Point3d safePoint = point;
        foreach (ObjectId objectId in currentSpace)
        {
            Entity? entity = transaction.GetObject(objectId, OpenMode.ForRead) as Entity;
            if (entity is Line line)
            {
                Point3d closest = line.GetClosestPointTo(point, false);
                double distance = closest.DistanceTo(point);
                if (distance <= GeometryMatchTolerance && distance < bestDistance)
                {
                    Vector3d axis = (line.EndPoint - line.StartPoint).GetNormal();
                    double length = line.StartPoint.DistanceTo(line.EndPoint);
                    double projection = (closest - line.StartPoint).DotProduct(axis);
                    double margin = Math.Min(OffsetFromLine, length / 3.0);
                    double clamped = Math.Max(margin, Math.Min(length - margin, projection));
                    safePoint = line.StartPoint + axis * clamped;
                    bestDistance = distance;
                }
                continue;
            }
            if (entity is not Polyline polyline || polyline.NumberOfVertices < 2) continue;
            Point3d closestPoint = polyline.GetClosestPointTo(point, false);
            double polyDistance = closestPoint.DistanceTo(point);
            if (polyDistance > GeometryMatchTolerance || polyDistance >= bestDistance) continue;
            double parameter = polyline.GetParameterAtPoint(closestPoint);
            int index = (int)Math.Floor(parameter);
            if (index >= polyline.NumberOfVertices - 1) index = polyline.Closed ? polyline.NumberOfVertices - 1 : polyline.NumberOfVertices - 2;
            if (index < 0 || polyline.GetSegmentType(index) != SegmentType.Line) continue;
            Point3d start = polyline.GetPoint3dAt(index);
            Point3d end = polyline.GetPoint3dAt((index + 1) % polyline.NumberOfVertices);
            Vector3d axis = (end - start).GetNormal();
            double length = start.DistanceTo(end);
            double projection = (closestPoint - start).DotProduct(axis);
            double margin = Math.Min(OffsetFromLine, length / 3.0);
            double clamped = Math.Max(margin, Math.Min(length - margin, projection));
            safePoint = start + axis * clamped;
            bestDistance = polyDistance;
        }
        transaction.Commit();
        return safePoint;
    }

    private static bool CrearLinetaMagenta(Autodesk.AutoCAD.ApplicationServices.Document document, Editor editor, Point3d firstVertex, Point3d secondVertex)
    {
        Vector3d axis = (secondVertex - firstVertex).GetNormal();
        if (axis.Length <= GeometryMatchTolerance) return false;
        Point3d start = firstVertex + axis * OffsetFromLine;
        Point3d end = secondVertex - axis * OffsetFromLine;
        if (start.DistanceTo(end) <= GeometryMatchTolerance) return true;

        using Transaction transaction = document.Database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
        string layerName = EnsureMagentaLayer(document.Database, transaction);
        ObjectId linetypeId = EnsureDashedLinetype(document.Database, transaction);
        var line = new Line
        {
            StartPoint = start,
            EndPoint = end,
            Layer = layerName,
            ColorIndex = 256,
            LinetypeId = linetypeId,
            LinetypeScale = DashedLinetypeScale
        };
        currentSpace.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
        transaction.Commit();
        editor.Regen();
        return true;
    }

    private static string EnsureMagentaLayer(Database database, Transaction transaction)
    {
        LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(MagentaLayer)) return MagentaLayer;
        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord { Name = MagentaLayer, Color = Color.FromColorIndex(ColorMethod.ByAci, 6) };
        layerTable.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
        return MagentaLayer;
    }

    private static ObjectId EnsureDashedLinetype(Database database, Transaction transaction)
    {
        LinetypeTable table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(DashedLinetype)) return table[DashedLinetype];
        try
        {
            database.LoadLineTypeFile(DashedLinetype, "acad.lin");
            table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            return table.Has(DashedLinetype) ? table[DashedLinetype] : ObjectId.Null;
        }
        catch
        {
            return ObjectId.Null;
        }
    }

    private sealed class LimitSideJig : EntityJig
    {
        private readonly DBText _text;
        private readonly Point3d _pointOnLine;
        private readonly Vector3d _normal;
        private readonly double _fixedOffset;
        private readonly double _fixedRotation;
        private Point3d _lastPoint;

        public LimitSideJig(DBText text, Point3d pointOnLine, Vector3d normal, double fixedOffset, double fixedRotation) : base(text)
        {
            _text = text;
            _pointOnLine = pointOnLine;
            _normal = normal.GetNormal();
            _fixedOffset = fixedOffset;
            _fixedRotation = fixedRotation;
            _lastPoint = text.Position;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nMueva el mouse al lado deseado y haga clic para fijar: ")
            {
                UseBasePoint = true,
                BasePoint = _pointOnLine,
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted
            };
            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.None) return SamplerStatus.Cancel;
            if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            Vector3d fromLine = result.Value - _pointOnLine;
            double signedDistance = fromLine.DotProduct(_normal);
            Vector3d placementNormal = signedDistance >= 0.0 ? _normal : -_normal;
            Point3d projectedPoint = _pointOnLine + placementNormal * _fixedOffset;
            if (projectedPoint.IsEqualTo(_lastPoint)) return SamplerStatus.NoChange;
            _lastPoint = projectedPoint;
            _text.Rotation = _fixedRotation;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            _text.Position = _lastPoint;
            _text.AlignmentPoint = _lastPoint;
            _text.Rotation = _fixedRotation;
            return true;
        }
    }
}
