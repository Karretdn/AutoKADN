using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using GraphicsWorldDraw = Autodesk.AutoCAD.GraphicsInterface.WorldDraw;

namespace AutoKADN.Tools.Dibujo;

/// <summary>Modo compartido entre el botón de la cinta y el comando.</summary>
public static class LineaRapidaSettings
{
    /// <summary>true: cada clic izquierdo fija un quiebre y la polilínea sigue; false: un solo tramo por polilínea.</summary>
    public static bool SeleccionMultiple { get; set; }
}

// Dibujo rápido de polilíneas: se hace clic en un punto (normalmente sobre una línea), el mouse elige la
// dirección y el tramo se extiende solo hasta el primer choque con otra línea (o el borde del rectángulo).
// ORTHOMODE activo: la dirección se ajusta a los ejes. Desactivado: dirección libre hacia el cursor.
// Modo simple:   clic fija el tramo y pide otro punto de inicio; Enter, Espacio o clic derecho fijan y terminan.
// Modo múltiple: clic fija un quiebre y sigue desde ahí; con el cursor sobre una línea el tramo choca contra
//                esa línea aunque haya otras antes; Enter, Espacio o clic derecho fijan el tramo y terminan.
// ESC cancela la polilínea en curso.
public sealed class LineaRapidaTool
{
    private const short NearestObjectSnap = 512;
    private const int ObjectSnapSuppressed = 16384;
    private const double CollinearTolerance = 1e-9;

    private enum BuildResult { Continue, Exit }

    public void Run()
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        object originalOsMode = AcadApplication.GetSystemVariable("OSMODE");
        object originalShortcutMenu = AcadApplication.GetSystemVariable("SHORTCUTMENU");
        int created = 0;
        try
        {
            // Clic derecho = Enter (sin menú contextual), como en LIMIK.
            AcadApplication.SetSystemVariable("SHORTCUTMENU", 0);
            int startOsMode = (Convert.ToInt32(originalOsMode) & ~ObjectSnapSuppressed) | NearestObjectSnap;

            using (ObstacleSet obstacles = ObstacleSet.Collect(database))
            {
                editor.WriteMessage("\n[LINEARAPIDA] Línea rápida. Enter, Espacio o clic derecho para terminar; ESC cancela.\n");
                while (true)
                {
                    // Para el punto de inicio: snap Cercano activo (además de los del usuario) para caer exacto sobre la línea.
                    AcadApplication.SetSystemVariable("OSMODE", (short)startOsMode);
                    bool multiple = LineaRapidaSettings.SeleccionMultiple;
                    var startOptions = new PromptPointOptions($"\nHaga clic en el punto de inicio, modo {(multiple ? "selección múltiple" : "simple")} (Enter, Espacio o clic derecho para salir): ") { AllowNone = true };
                    PromptPointResult startResult = editor.GetPoint(startOptions);
                    if (startResult.Status != PromptStatus.OK) break;

                    Matrix3d ucs = editor.CurrentUserCoordinateSystem;
                    Point3d start = startResult.Value.TransformBy(ucs);

                    // La dirección la da el cursor libre: sin snaps mientras se muestra el preview.
                    AcadApplication.SetSystemVariable("OSMODE", (short)0);
                    if (BuildPolyline(editor, database, obstacles, start, ucs, ref created) == BuildResult.Exit) break;
                }
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en LINEARAPIDA: {ex.Message}\n");
        }
        finally
        {
            AcadApplication.SetSystemVariable("OSMODE", originalOsMode);
            AcadApplication.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }

        if (created > 0) editor.WriteMessage($"\n[LINEARAPIDA] {created} polilínea(s) creada(s).\n");
    }

    private static BuildResult BuildPolyline(Editor editor, Database database, ObstacleSet obstacles, Point3d start, Matrix3d ucs, ref int created)
    {
        var vertices = new List<Point3d> { start };
        int mark = obstacles.Mark();
        Point3d cursor = start;

        while (true)
        {
            bool multiple = LineaRapidaSettings.SeleccionMultiple;
            Point3d anchor = vertices[vertices.Count - 1];
            var jig = new QuickLineJig(obstacles, vertices, anchor, cursor, ucs, multiple);
            PromptResult drag = editor.Drag(jig);
            cursor = jig.LastCursor;

            if (jig.EnterPressed)
            {
                // Enter, Espacio o clic derecho: lo que se ve en el preview queda como último tramo.
                if (jig.HasSegment) AddVertex(obstacles, vertices, jig.EndPoint);
                if (Commit(editor, database, vertices)) created++;
                return BuildResult.Exit;
            }

            if (drag.Status != PromptStatus.OK)
            {
                obstacles.Rollback(mark);
                editor.WriteMessage("\nCancelado.\n");
                return BuildResult.Exit;
            }

            if (!jig.HasSegment) continue; // clic sin dirección todavía: se ignora

            AddVertex(obstacles, vertices, jig.EndPoint);
            if (multiple) continue; // quiebre fijado: la polilínea sigue desde aquí

            if (Commit(editor, database, vertices)) created++;
            return BuildResult.Continue;
        }
    }

    private static void AddVertex(ObstacleSet obstacles, List<Point3d> vertices, Point3d point)
    {
        obstacles.AddSegment(vertices[vertices.Count - 1], point); // los tramos ya fijados también cuentan como tope
        vertices.Add(point);
    }

    private static bool Commit(Editor editor, Database database, List<Point3d> vertices)
    {
        List<Point3d> points = Simplify(vertices);
        if (points.Count < 2) return false;

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
            var polyline = new Polyline();
            polyline.SetDatabaseDefaults(database);
            polyline.Elevation = points[0].Z;
            for (int i = 0; i < points.Count; i++)
                polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0.0, 0.0, 0.0);
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            transaction.Commit();
        }
        editor.UpdateScreen(); // el preview desaparece: que el trazo real se vea de inmediato
        return true;
    }

    // Quita vértices repetidos y los intermedios que quedan alineados con el mismo sentido
    // (p. ej. un quiebre fijado sobre una línea que se atraviesa sin cambiar de dirección).
    private static List<Point3d> Simplify(List<Point3d> vertices)
    {
        var result = new List<Point3d>();
        foreach (Point3d point in vertices)
        {
            if (result.Count > 0 && result[result.Count - 1].DistanceTo(point) <= ObstacleSet.MinHit) continue;
            while (result.Count >= 2 && IsStraightThrough(result[result.Count - 2], result[result.Count - 1], point))
                result.RemoveAt(result.Count - 1);
            result.Add(point);
        }
        return result;
    }

    private static bool IsStraightThrough(Point3d a, Point3d b, Point3d c)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, bcx = c.X - b.X, bcy = c.Y - b.Y;
        double scale = Math.Sqrt(abx * abx + aby * aby) * Math.Sqrt(bcx * bcx + bcy * bcy);
        if (scale <= 0.0) return false;
        double cross = abx * bcy - aby * bcx;
        double dot = abx * bcx + aby * bcy;
        return dot > 0.0 && Math.Abs(cross) <= CollinearTolerance * scale;
    }

    // Tamaño de un píxel en unidades de dibujo y tolerancia para "tocar" una línea (APERTURE).
    private readonly struct ViewScale
    {
        private ViewScale(double unitsPerPixel, double hoverTolerance)
        {
            UnitsPerPixel = unitsPerPixel;
            HoverTolerance = hoverTolerance;
        }

        public double UnitsPerPixel { get; }
        public double HoverTolerance { get; }

        public static ViewScale Read()
        {
            double unitsPerPixel = 1.0;
            double aperture = 10.0;
            try
            {
                double viewSize = Convert.ToDouble(AcadApplication.GetSystemVariable("VIEWSIZE"));
                object screen = AcadApplication.GetSystemVariable("SCREENSIZE");
                if (screen is Point2d size && size.Y > 0.0 && viewSize > 0.0) unitsPerPixel = viewSize / size.Y;
                aperture = Math.Max(Convert.ToDouble(AcadApplication.GetSystemVariable("APERTURE")), 5.0);
            }
            catch { /* se usan valores por defecto */ }
            return new ViewScale(unitsPerPixel, aperture * unitsPerPixel);
        }
    }

    private sealed class QuickLineJig : DrawJig
    {
        private const string SimpleMessage = "\nDirección con el mouse. Clic: fijar y seguir. Enter, Espacio o clic derecho: fijar y terminar: ";
        private const string MultipleMessage = "\nClic: fijar quiebre. Enter, Espacio o clic derecho: fijar el tramo y terminar: ";

        private readonly ObstacleSet _obstacles;
        private readonly List<Point3d> _vertices;
        private readonly Point3d _anchor;
        private readonly Matrix3d _ucs;
        private readonly bool _multiple;
        private Point3d _lastCursor;
        private double _pixel = 1.0;
        private bool _drawn;

        public QuickLineJig(ObstacleSet obstacles, List<Point3d> vertices, Point3d anchor, Point3d initialCursor, Matrix3d ucs, bool multiple)
        {
            _obstacles = obstacles;
            _vertices = vertices;
            _anchor = anchor;
            _lastCursor = initialCursor; // el cursor aún no se movió: no hay preview hasta que se mueva
            _ucs = ucs;
            _multiple = multiple;
            EndPoint = anchor;
        }

        public bool EnterPressed { get; private set; }
        public bool HasSegment { get; private set; }
        public Point3d EndPoint { get; private set; }
        public Point3d LastCursor => _lastCursor;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(_multiple ? MultipleMessage : SimpleMessage)
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
            if (cursor.IsEqualTo(_lastCursor))
            {
                // Sin movimiento no hay preview, pero la primera vez se pide un dibujo para que se vea la cadena ya fijada.
                if (_drawn) return SamplerStatus.NoChange;
                _drawn = true;
                return SamplerStatus.OK;
            }
            _drawn = true;
            _lastCursor = cursor;
            Resolve(cursor);
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(GraphicsWorldDraw draw)
        {
            draw.SubEntityTraits.Color = 7;
            var geometry = draw.Geometry;
            for (int i = 0; i + 1 < _vertices.Count; i++) geometry.WorldLine(_vertices[i], _vertices[i + 1]);
            if (!HasSegment) return true;

            geometry.WorldLine(_anchor, EndPoint);
            double r = _pixel * 5.0; // cruz en el punto donde choca
            geometry.WorldLine(new Point3d(EndPoint.X - r, EndPoint.Y - r, EndPoint.Z), new Point3d(EndPoint.X + r, EndPoint.Y + r, EndPoint.Z));
            geometry.WorldLine(new Point3d(EndPoint.X - r, EndPoint.Y + r, EndPoint.Z), new Point3d(EndPoint.X + r, EndPoint.Y - r, EndPoint.Z));
            return true;
        }

        // Dirección: ejes del UCS más cercanos al cursor con ORTHOMODE activo; libre hacia el cursor si no.
        // El tramo llega hasta el primer choque. En modo múltiple, si el cursor toca una línea y el rayo
        // la alcanza, el tramo choca contra esa línea aunque haya otras antes.
        private void Resolve(Point3d cursor)
        {
            HasSegment = false;
            EndPoint = _anchor;

            var toCursor = new Vector3d(cursor.X - _anchor.X, cursor.Y - _anchor.Y, 0.0);
            if (toCursor.Length <= ObstacleSet.MinHit) return;

            ViewScale scale = ViewScale.Read();
            _pixel = scale.UnitsPerPixel;

            Vector3d[] candidates = RotationStandard.IsOrthoEnabled() ? OrthoCandidates(toCursor) : new[] { toCursor.GetNormal() };
            Vector3d direction = candidates[0];
            double distance = 0.0;
            bool hit = false;

            if (_multiple && _obstacles.TryFindNear(cursor, scale.HoverTolerance, out ObstacleSet.Ref target))
            {
                foreach (Vector3d candidate in candidates)
                {
                    if (candidate.DotProduct(toCursor) <= ObstacleSet.MinHit) continue; // solo hacia donde apunta el cursor
                    if (!_obstacles.TryRayTarget(target, _anchor, candidate, out double targetDistance)) continue;
                    direction = candidate;
                    distance = targetDistance;
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                direction = candidates[0];
                hit = _obstacles.TryRayFirst(_anchor, direction, out distance);
                if (!hit) distance = Math.Max(toCursor.DotProduct(direction), 0.0); // sin nada que chocar: hasta el cursor
            }

            if (distance <= ObstacleSet.MinHit) return;
            EndPoint = new Point3d(_anchor.X + direction.X * distance, _anchor.Y + direction.Y * distance, _anchor.Z);
            HasSegment = true;
        }

        // Los cuatro sentidos de los ejes del UCS, del más cercano al cursor al más lejano.
        private Vector3d[] OrthoCandidates(Vector3d toCursor)
        {
            CoordinateSystem3d system = _ucs.CoordinateSystem3d;
            Vector3d x = FlatAxis(system.Xaxis, Vector3d.XAxis);
            Vector3d y = FlatAxis(system.Yaxis, Vector3d.YAxis);
            Vector3d[] axes = { x, x.Negate(), y, y.Negate() };
            return axes.OrderByDescending(axis => axis.DotProduct(toCursor)).ToArray();
        }

        private static Vector3d FlatAxis(Vector3d axis, Vector3d fallback)
        {
            var flat = new Vector3d(axis.X, axis.Y, 0.0);
            return flat.Length > 1e-9 ? flat.GetNormal() : fallback;
        }
    }
}
