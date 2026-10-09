using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using GraphicsWorldDraw = Autodesk.AutoCAD.GraphicsInterface.WorldDraw;

namespace AutoKADN.Tools.Layouts;

// RECORTEANILLOS: saca los anillos del plano general, uno tras otro. Desde el layout del plano general se marcan dos
// extremos "a ojo" del área de un anillo: el primer punto es una esquina fija y el rectángulo crece hacia el cursor con la
// proporción del marco del DETALLE (siempre la misma), en vivo mientras se mueve el mouse. Con Enter el dibujo que cae
// dentro del rectángulo se copia al DETALLE, ampliado o reducido para que encaje en el marco y cortado en el borde. El
// plano general no se toca (se copia, no se corta). El UC se llena después con CLONARUC.
// A dónde va: al primer ANILLO N DETALLE que ya existe y sigue siendo una base sin dibujo del plano (el ANILLO 1 que se
// deja hecho al empezar un proyecto; no se le borra nada). Si no hay, se crea el par de layouts (como NUEVOANILLO) con el
// primer número que falte: el 1 si se borró, si no el siguiente. Después pregunta si se hace otro recorte.
public sealed class RecorteAnillosTool
{
    private const string Tag = "[RECORTEANILLOS]";
    private static readonly Regex RingLayout = new Regex(@"^(?:ANILLO\s+\d+|TRONCAL)\s+(?:DETALLE|UC)$", RegexOptions.IgnoreCase);

    public void Run()
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        if (database.TileMode)
        {
            editor.WriteMessage($"\n{Tag} Este comando funciona en el layout del plano general (espacio papel).\n");
            return;
        }

        LayoutManager layoutManager = LayoutManager.Current;
        string sourceName = layoutManager.CurrentLayout;
        if (RingLayout.IsMatch(sourceName.Trim()))
        {
            editor.WriteMessage($"\n{Tag} Ubícate en el layout del plano general, de donde se recortan los anillos. Layout actual: '{sourceName}'.\n");
            return;
        }

        ObjectId sourceSpaceId;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var layout = (Layout)transaction.GetObject(layoutManager.GetLayoutId(sourceName), OpenMode.ForRead);
            sourceSpaceId = layout.BlockTableRecordId;
            transaction.Commit();
        }
        if (database.CurrentSpaceId != sourceSpaceId)
        {
            editor.WriteMessage($"\n{Tag} Está dentro de un viewport (espacio modelo). Salga al espacio papel del layout e intente de nuevo.\n");
            return;
        }
        if (NuevoAnilloTool.RingLayouts(database).Count == 0)
        {
            editor.WriteMessage($"\n{Tag} No hay ningún layout de anillo. Deja hecho el ANILLO 1 DETALLE (y su UC) como base del proyecto y vuelve a intentar.\n");
            return;
        }

        object originalShortcutMenu = AcadApplication.GetSystemVariable("SHORTCUTMENU");
        int made = 0;
        string? firstDetalle = null;
        var usedNow = new HashSet<int>(); // anillos que ya recibieron un recorte en esta sesión
        try
        {
            // Clic derecho = Enter (sin menú contextual), como en las demás herramientas.
            AcadApplication.SetSystemVariable("SHORTCUTMENU", 0);
            editor.WriteMessage($"\n{Tag} Marque dos extremos opuestos del área de cada anillo (con eso queda el tamaño); después mueva el rectángulo y haga clic para ubicarlo. Enter o clic derecho para terminar.\n");

            while (true)
            {
                int number = TargetNumber(database, usedNow, out bool reuse);

                var firstOptions = new PromptPointOptions($"\nANILLO {number}: primer extremo del área (Enter o clic derecho para terminar): ") { AllowNone = true };
                PromptPointResult first = editor.GetPoint(firstOptions);
                if (first.Status != PromptStatus.OK) break;

                Matrix3d ucs = editor.CurrentUserCoordinateSystem;
                var jig = new AreaJig(first.Value.TransformBy(ucs), ucs);
                PromptResult drag = editor.Drag(jig);
                if (drag.Status != PromptStatus.OK || !jig.HasRect)
                {
                    editor.WriteMessage($"\n{Tag} Recorte descartado.\n");
                    continue;
                }

                // El tamaño (y con él la escala) ya quedó fijo con los dos extremos; ahora el rectángulo se mueve con el mouse
                // para ubicarlo con precisión, y el clic lo deja ahí. Enter lo deja donde está; ESC descarta.
                RecorteRect rect = jig.Rect;
                var moveJig = new PlaceJig(rect, jig.LastCursor, ucs);
                PromptResult placed = editor.Drag(moveJig);
                if (moveJig.EnterPressed) { /* se queda donde está */ }
                else if (placed.Status != PromptStatus.OK)
                {
                    editor.WriteMessage($"\n{Tag} Recorte descartado.\n");
                    continue;
                }
                else rect = rect.Translate(moveJig.Offset);

                double scale = (ClonarUcTool.FrameMaxX - ClonarUcTool.FrameMinX) / rect.Width;
                editor.WriteMessage($"\n{Tag} Área de {rect.Width:0.##} x {rect.Height:0.##} en el plano general; se ajusta al marco con escala ×{scale:0.###}.\n");

                if (!Confirm(editor, rect, number)) { editor.WriteMessage($"\n{Tag} Recorte descartado.\n"); continue; }

                if (CutInto(editor, database, layoutManager, sourceName, sourceSpaceId, rect, scale, number, reuse, out string detalle))
                {
                    made++;
                    usedNow.Add(number);
                    firstDetalle ??= detalle;
                }

                if (!AskAnother(editor)) break;
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en RECORTEANILLOS: {ex.Message}\n");
        }
        finally
        {
            AcadApplication.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }

        if (made == 0 || firstDetalle is null) return;
        try
        {
            layoutManager.CurrentLayout = firstDetalle;
            editor.Regen();
        }
        catch (System.Exception) { /* si no se puede cambiar de layout, el usuario ya sabe dónde están */ }
        editor.WriteMessage($"\n{Tag} {made} recorte(s) hechos. Ya estás en '{firstDetalle}'; después usa CLONARUC para pasar el detalle al UC.\n");
    }

    // Un DETALLE se considera una base libre si trae el formato (marco, cajetín y lo que el formato lleve dentro del
    // marco: el norte, textos fijos…) pero casi ningún dibujo del plano: a lo sumo esta cantidad de líneas, polilíneas,
    // cotas, sombreados u objetos con datos del plugin. Un anillo ya dibujado tiene decenas.
    private const int MaxFormatDrawing = 3;

    // A dónde va el siguiente recorte: el primer DETALLE que ya existe y es una base libre (que no recibió un recorte
    // en esta sesión), sin crear nada; si no hay, el primer número que falta en la secuencia (se crea).
    private static int TargetNumber(Database database, ICollection<int> usedNow, out bool reuse)
    {
        SortedDictionary<int, NuevoAnilloTool.RingPair> rings = NuevoAnilloTool.RingLayouts(database);
        foreach (KeyValuePair<int, NuevoAnilloTool.RingPair> ring in rings)
        {
            if (!ring.Value.Detalle || usedNow.Contains(ring.Key)) continue;
            if (!IsFreeBase(database, ring.Key)) continue;
            reuse = true;
            return ring.Key;
        }
        reuse = false;
        int number = 1;
        while (rings.ContainsKey(number)) number++;
        return number;
    }

    private static bool IsFreeBase(Database database, int number)
    {
        string name = $"ANILLO {number} DETALLE";
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
            if (!string.Equals(layout.LayoutName.Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
            var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            int drawing = 0;
            foreach (ObjectId id in space)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                bool hasData = ClonarUcTool.HasDetalleXData(entity);
                if (!hasData && !ClonarUcTool.IsInsideFrame(entity, ClonarUcTool.DetalleFrameMinY, ClonarUcTool.DetalleFrameMaxY)) continue;
                if (hasData || entity is Line || entity is Autodesk.AutoCAD.DatabaseServices.Polyline || entity is Polyline2d || entity is Polyline3d
                    || entity is Arc || entity is Ellipse || entity is Spline || entity is Hatch || entity is Dimension) drawing++;
                if (drawing > MaxFormatDrawing) return false;
            }
            return true;
        }
        return false;
    }

    // Deja el rectángulo a la vista y espera Enter (crear) o ESC / Descartar.
    private static bool Confirm(Editor editor, RecorteRect rect, int number)
    {
        using var preview = new RectPreview(rect);
        var options = new PromptKeywordOptions($"\nEnter para crear el recorte de ANILLO {number} [Crear/Descartar] <Crear>: ") { AllowNone = true };
        options.Keywords.Add("Crear");
        options.Keywords.Add("Descartar");
        options.Keywords.Default = "Crear";
        PromptResult answer = editor.GetKeywords(options);
        if (answer.Status == PromptStatus.None) return true;
        return answer.Status == PromptStatus.OK && answer.StringResult.Equals("Crear", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AskAnother(Editor editor)
    {
        var options = new PromptKeywordOptions("\n¿Hacer otro recorte? [Si/No] <Si>: ") { AllowNone = true };
        options.Keywords.Add("Si");
        options.Keywords.Add("No");
        options.Keywords.Default = "Si";
        PromptResult answer = editor.GetKeywords(options);
        if (answer.Status == PromptStatus.None) return true;
        return answer.Status == PromptStatus.OK && answer.StringResult.Equals("Si", StringComparison.OrdinalIgnoreCase);
    }

    // Copia al DETALLE (creando antes el par de layouts si hace falta) lo que cae dentro del rectángulo.
    private static bool CutInto(Editor editor, Database database, LayoutManager layoutManager, string sourceName, ObjectId sourceSpaceId,
        RecorteRect rect, double scale, int number, bool reuse, out string detalleName)
    {
        detalleName = $"ANILLO {number} DETALLE";

        RecortePlan plan;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var source = (BlockTableRecord)transaction.GetObject(sourceSpaceId, OpenMode.ForRead);
            plan = RecorteGeometria.Collect(transaction, source, rect);
            transaction.Commit();
        }

        using (plan)
        {
            if (plan.Total == 0)
            {
                editor.WriteMessage($"\n{Tag} No hay dibujo dentro del rectángulo; no se creó nada.\n");
                return false;
            }

            if (!reuse)
            {
                NuevoAnilloTool.RingCreation? created = NuevoAnilloTool.CreateRing(editor, database, Tag, 0, number);
                if (created is null) return false;
                detalleName = created.NewDetalle;
                // Crear el layout puede dejarlo como actual: se vuelve al plano general para seguir marcando ahí.
                if (!string.Equals(layoutManager.CurrentLayout, sourceName, StringComparison.OrdinalIgnoreCase)) layoutManager.CurrentLayout = sourceName;
            }

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var layout = (Layout)transaction.GetObject(layoutManager.GetLayoutId(detalleName), OpenMode.ForRead);
                var target = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                RecorteGeometria.Apply(transaction, database, plan, target, RecorteGeometria.ToDetalleFrame(rect));
                transaction.Commit();
            }

            string message = $"\n{Tag} {detalleName}{(reuse ? " (ya existía; se pegó ahí sin borrar nada)" : string.Empty)}: {plan.Total} objeto(s) copiados del plano general, con escala ×{scale:0.###} al marco "
                + $"({plan.CutCurves} línea(s) o curva(s) cortadas en el borde; {plan.Blocks} bloque(s)).";
            if (plan.Overflow > 0) message += $" {plan.Overflow} objeto(s) que no son líneas (textos, bloques, sombreados) cruzan el borde y se copiaron enteros.";
            if (plan.PluginData > 0) message += $" {plan.PluginData} de los copiados llevan datos del plugin (materiales, anotaciones) y cuentan en el Excel de este anillo.";
            if (plan.RingsMoved.Count > 0) message += $" Indicador(es) de anillo corridos al marco: {string.Join(", ", plan.RingsMoved)} (se alcanza a ver parte de ese anillo).";
            if (plan.TextsMoved > 0) message += $" {plan.TextsMoved} texto(s) se corrieron para que no tapen un indicador ni queden cortados por el borde.";
            if (plan.TextsDropped > 0) message += $" {plan.TextsDropped} texto(s) no cabían en el espacio y no se copiaron.";
            if (plan.Unreadable > 0) message += $" {plan.Unreadable} objeto(s) no se pudieron leer y no se copiaron.";
            editor.WriteMessage(message + "\n");
        }
        return true;
    }

    // Segundo extremo: el rectángulo con la proporción del marco, en vivo.
    private sealed class AreaJig : DrawJig
    {
        private readonly Point3d _first;
        private readonly Matrix3d _ucs;
        private Point3d _cursor;
        private bool _drawn;

        public AreaJig(Point3d first, Matrix3d ucs)
        {
            _first = first;
            _cursor = first;
            _ucs = ucs;
        }

        public bool HasRect { get; private set; }
        public RecorteRect Rect { get; private set; }
        /// <summary>El punto donde se hizo clic para el segundo extremo.</summary>
        public Point3d LastCursor => _cursor;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nSegundo extremo del área (el rectángulo mantiene la proporción del formato): ")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates
            };
            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;

            Point3d cursor = result.Value.TransformBy(_ucs);
            if (_drawn && cursor.IsEqualTo(_cursor)) return SamplerStatus.NoChange;
            _drawn = true;
            _cursor = cursor;

            HasRect = Math.Abs(cursor.X - _first.X) > 1e-9 || Math.Abs(cursor.Y - _first.Y) > 1e-9;
            if (HasRect) Rect = RecorteRect.FromAnchor(_first, cursor, RecorteGeometria.FrameAspect);
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(GraphicsWorldDraw draw)
        {
            if (!HasRect) return true;
            RecorteRect r = Rect;
            var geometry = draw.Geometry;
            draw.SubEntityTraits.Color = 3;
            geometry.WorldLine(new Point3d(r.MinX, r.MinY, 0.0), new Point3d(r.MaxX, r.MinY, 0.0));
            geometry.WorldLine(new Point3d(r.MaxX, r.MinY, 0.0), new Point3d(r.MaxX, r.MaxY, 0.0));
            geometry.WorldLine(new Point3d(r.MaxX, r.MaxY, 0.0), new Point3d(r.MinX, r.MaxY, 0.0));
            geometry.WorldLine(new Point3d(r.MinX, r.MaxY, 0.0), new Point3d(r.MinX, r.MinY, 0.0));
            return true;
        }
    }

    // Ubicar el rectángulo ya dimensionado: se mueve con el mouse (como MOVER, desde el punto del segundo clic).
    private sealed class PlaceJig : DrawJig
    {
        private readonly RecorteRect _rect;
        private readonly Point3d _origin;
        private readonly Matrix3d _ucs;
        private Point3d _cursor;
        private bool _drawn;

        public PlaceJig(RecorteRect rect, Point3d origin, Matrix3d ucs)
        {
            _rect = rect;
            _origin = origin;
            _cursor = origin;
            _ucs = ucs;
        }

        public bool EnterPressed { get; private set; }
        public Vector3d Offset => _cursor - _origin;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nMueva el rectángulo y haga clic para ubicarlo (Enter lo deja donde está, ESC descarta): ")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted
            };
            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.None)
            {
                EnterPressed = true; // Enter, Espacio o clic derecho
                return SamplerStatus.Cancel;
            }
            if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;

            Point3d cursor = result.Value.TransformBy(_ucs);
            if (_drawn && cursor.IsEqualTo(_cursor)) return SamplerStatus.NoChange;
            _drawn = true;
            _cursor = cursor;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(GraphicsWorldDraw draw)
        {
            RecorteRect r = _rect.Translate(Offset);
            var geometry = draw.Geometry;
            draw.SubEntityTraits.Color = 3;
            geometry.WorldLine(new Point3d(r.MinX, r.MinY, 0.0), new Point3d(r.MaxX, r.MinY, 0.0));
            geometry.WorldLine(new Point3d(r.MaxX, r.MinY, 0.0), new Point3d(r.MaxX, r.MaxY, 0.0));
            geometry.WorldLine(new Point3d(r.MaxX, r.MaxY, 0.0), new Point3d(r.MinX, r.MaxY, 0.0));
            geometry.WorldLine(new Point3d(r.MinX, r.MaxY, 0.0), new Point3d(r.MinX, r.MinY, 0.0));
            return true;
        }
    }

    // El rectángulo elegido, a la vista mientras se espera la confirmación.
    private sealed class RectPreview : IDisposable
    {
        private readonly Polyline? _polyline;
        private readonly Autodesk.AutoCAD.GraphicsInterface.TransientManager? _manager;

        public RectPreview(RecorteRect rect)
        {
            try
            {
                var polyline = new Polyline();
                polyline.AddVertexAt(0, new Point2d(rect.MinX, rect.MinY), 0.0, 0.0, 0.0);
                polyline.AddVertexAt(1, new Point2d(rect.MaxX, rect.MinY), 0.0, 0.0, 0.0);
                polyline.AddVertexAt(2, new Point2d(rect.MaxX, rect.MaxY), 0.0, 0.0, 0.0);
                polyline.AddVertexAt(3, new Point2d(rect.MinX, rect.MaxY), 0.0, 0.0, 0.0);
                polyline.Closed = true;
                polyline.ColorIndex = 3;
                _manager = Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager;
                _manager.AddTransient(polyline, Autodesk.AutoCAD.GraphicsInterface.TransientDrawingMode.Main, 128, new IntegerCollection());
                _polyline = polyline;
            }
            catch (System.Exception) { /* sin el dibujo transitorio la herramienta sigue funcionando */ }
        }

        public void Dispose()
        {
            if (_polyline is null) return;
            try { _manager?.EraseTransient(_polyline, new IntegerCollection()); }
            catch (System.Exception) { }
            _polyline.Dispose();
        }
    }
}
