using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;
using AutoKADN.Tools.Layouts;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// CORREGIRIA, «Corregir con IA»: el calco de la IA ya está dibujado en el ANILLO n DETALLE abierto y algo quedó mal. El
// usuario encierra la zona (en el dibujo o en la hoja de referencia; Enter = todo el calco) y escribe qué hay que hacer.
// AutoKADN.CalcoIA.exe (modo corregir) parte del croquis de ese calco y lo corrige; aquí se dibuja el corregido y se
// borra el anterior. Va antes de retocar a mano y de «Detallar con IA»: lo cambiado a mano en el calco se pierde.
//  · El calco anterior: el trabajo más reciente (calcar o corregir) de este layout y esta hoja cuyo plano sigue dibujado
//    (Anterior). Se borra lo que coincide con su plano; lo que el usuario movió o cambió a mano se queda y se avisa.
//  · El trabajo queda en %LOCALAPPDATA%\AutoKADN\CalcoIA\trabajos (pedido, corrección, croquis, plano y registro), y la
//    corrección se anota en correcciones.jsonl junto a esa carpeta.
public sealed class CalcoCorregirTool
{
    private const double Tolerancia = 1e-3;
    private static readonly Regex Modo = new Regex("\"modo\"\\s*:\\s*\"[^\"]*\"");

    public void Run()
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        try
        {
            if (!CalcoIaTool.EnAnilloDetalle(database, out string layout))
            {
                editor.WriteMessage("\n[CORREGIRIA] Abra el layout ANILLO n DETALLE (en espacio papel) que calcó con «Calcar con IA».\n");
                return;
            }
            if (!CalcoIaTool.BuscarMarca(database, out string fuente, out int pagina, out double[]? recorte, out int giro))
            {
                editor.WriteMessage("\n[CORREGIRIA] Este layout no tiene la hoja importada con CALCAR: «Corregir con IA» corrige un calco hecho con «Calcar con IA».\n");
                return;
            }
            Anterior? anterior = BuscarAnterior(database, layout, fuente, pagina);
            if (anterior == null)
            {
                editor.WriteMessage("\n[CORREGIRIA] No encontré en este layout el calco de la IA de esta hoja (¿se borró, se deshizo o se calcó en otro equipo?). Use «Calcar con IA».\n");
                return;
            }

            if (!PedirZona(editor, database, recorte, giro, out string zona, out double[]? caja, out string descripcion)) return;
            var nota = new VentanaNotaCorreccion(descripcion);
            if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(nota) != true || nota.Nota.Length == 0)
            {
                editor.WriteMessage("\n[Corregir con IA] Cancelado: no se cambió nada.\n");
                return;
            }
            Ejecutar(editor, database, layout, anterior, zona, caja, nota.Nota);
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en CORREGIRIA: {ex.Message}\n");
        }
    }

    private static void Ejecutar(Editor editor, Database database, string layout, Anterior anterior, string zona, double[]? caja, string nota)
    {
        string? ejecutable = CalcoIaTool.BuscarEjecutable();
        if (ejecutable == null)
        {
            editor.WriteMessage("\n[Corregir con IA] No se encontró AutoKADN.CalcoIA.exe (compile la solución: queda en bin\\CalcoIA).\n");
            return;
        }

        string carpeta = Path.Combine(Trabajos(), DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(carpeta);
        // El mismo pedido del calco anterior (hoja, página, recorte, giro, diámetro): así la zona se ubica en lo que dibujó.
        string pedido = Modo.Replace(File.ReadAllText(Path.Combine(anterior.Carpeta, "pedido.json")), "\"modo\": \"corregir\"", 1);
        File.WriteAllText(Path.Combine(carpeta, "pedido.json"), pedido, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(carpeta, "correccion.json"), Correccion(anterior.Carpeta, nota, zona, caja), new UTF8Encoding(false));

        var ventana = new VentanaCalcoIa(ejecutable, carpeta, corregir: true);
        bool? dibujar = Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(ventana);
        if (ventana.Costo != null) editor.WriteMessage($"\n[Corregir con IA] Costo: {ventana.Costo}\n");
        if (dibujar != true)
        {
            editor.WriteMessage(ventana.Error != null
                ? $"\n[Corregir con IA] No se pudo corregir: {ventana.Error}. Detalles en {Path.Combine(carpeta, "registro.txt")}\n"
                : "\n[Corregir con IA] Cancelado: el calco anterior sigue igual.\n");
            return;
        }

        CalcoPlano plano = CalcoPlanoJson.Leer(File.ReadAllText(Path.Combine(carpeta, "plano.json")));
        CalcoResultado resultado = CalcoDibujante.Dibujar(database, database.CurrentSpaceId, layout, plano, Tamanos.Load(), conCotas: false);
        if (!resultado.Ok)
        {
            editor.WriteMessage($"\n[Corregir con IA] No se dibujó nada (el calco anterior sigue igual): {resultado.Error}.\n");
            return;
        }
        int borrados = Borrar(database, anterior.Entidades);
        editor.Regen();
        editor.WriteMessage($"\n[Corregir con IA] {layout}: se reemplazó el calco anterior ({borrados} objetos) por el corregido: {resultado.Lineas} líneas, "
            + $"{resultado.Textos} textos, {resultado.Accesorios} accesorios, {resultado.Indicadores} indicadores. Deshacer (H) vuelve al anterior.\n");
        if (anterior.Faltan > 0)
            editor.WriteMessage($"[Corregir con IA] Aviso: {anterior.Faltan} cosas del calco anterior no estaban como las dejó la IA (movidas o cambiadas a mano): no se borraron, revíselas.\n");
        if (HayCotas(database))
            editor.WriteMessage("[Corregir con IA] Aviso: el layout ya tiene cotas (de «Detallar con IA» o a mano) y la corrección no las mueve: revíselas, o bórrelas y vuelva a detallar.\n");
        foreach (string aviso in resultado.Avisos) editor.WriteMessage($"[Corregir con IA] Aviso: {aviso}.\n");
        foreach (string duda in ventana.Dudas) editor.WriteMessage($"[Corregir con IA] Revise: {duda}\n");
        editor.WriteMessage($"[Corregir con IA] Trabajo guardado en {carpeta}\n");
    }

    private static string Trabajos() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoKADN", "CalcoIA", "trabajos");

    private static string Correccion(string anterior, string nota, string zona, double[]? caja)
    {
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var json = new StringBuilder("{\n");
        json.Append("  \"anterior\": ").Append(CalcoIaTool.JsonTexto(anterior)).Append(",\n");
        json.Append("  \"nota\": ").Append(CalcoIaTool.JsonTexto(nota)).Append(",\n");
        if (caja != null) json.Append("  \"caja\": [").Append(string.Join(", ", caja.Select(N))).Append("],\n");
        json.Append("  \"zona\": ").Append(CalcoIaTool.JsonTexto(zona)).Append('\n');
        return json.Append("}\n").ToString();
    }

    // La zona: dos esquinas (Enter en la primera = todo el calco). Si cae sobre la hoja de referencia, en fracción de la
    // hoja derecha; si no, en coordenadas del dibujo. Clic derecho = Enter (sin menú contextual mientras se pide).
    private static bool PedirZona(Editor editor, Database database, double[]? recorte, int giro, out string zona, out double[]? caja, out string descripcion)
    {
        zona = "todo";
        caja = null;
        descripcion = "Corrección de todo el calco.";
        object menu = AcadApplication.GetSystemVariable("SHORTCUTMENU");
        PromptPointResult primera, segunda;
        try
        {
            AcadApplication.SetSystemVariable("SHORTCUTMENU", 0);
            var opciones = new PromptPointOptions("\nEsquina de la zona a corregir, en el dibujo o en la hoja de referencia (Enter: todo el calco): ") { AllowNone = true };
            primera = editor.GetPoint(opciones);
            if (primera.Status == PromptStatus.None) return true;
            if (primera.Status != PromptStatus.OK) return false;
            segunda = editor.GetCorner(new PromptCornerOptions("\nEsquina opuesta: ", primera.Value));
            if (segunda.Status != PromptStatus.OK) return false;
        }
        finally
        {
            AcadApplication.SetSystemVariable("SHORTCUTMENU", menu);
        }

        Matrix3d ucs = editor.CurrentUserCoordinateSystem;
        Point3d a = primera.Value.TransformBy(ucs), b = segunda.Value.TransformBy(ucs);
        CalcoFrame rect = CalcoFrame.FromCorners(a, b);
        if (rect.IsEmpty(1e-6)) return true;

        if (ExtensionHoja(database) is CalcoFrame hoja && rect.CenterX > hoja.MinX && rect.CenterX < hoja.MaxX && rect.CenterY > hoja.MinY && rect.CenterY < hoja.MaxY)
        {
            // Fracciones de lo que se ve de la hoja (con su giro) → de la hoja derecha, dentro del recorte.
            double[] vista = CalcoIaTool.Fracciones(hoja, rect);
            double[] natural = CalcoIaTool.ANatural(vista, giro);
            double[] r = recorte ?? new[] { 0.0, 0.0, 1.0, 1.0 };
            double X(double u) => Math.Round(r[0] + u * (r[2] - r[0]), 5);
            double Y(double v) => Math.Round(r[1] + v * (r[3] - r[1]), 5);
            zona = "hoja";
            caja = new[] { X(natural[0]), Y(natural[1]), X(natural[2]), Y(natural[3]) };
            descripcion = "Zona señalada en la hoja de referencia.";
            return true;
        }
        zona = "plano";
        caja = new[] { Math.Round(rect.MinX, 3), Math.Round(rect.MinY, 3), Math.Round(rect.MaxX, 3), Math.Round(rect.MaxY, 3) };
        descripcion = "Zona señalada en el dibujo.";
        return true;
    }

    // Lo que se ve de la hoja de referencia (la imagen o el PDF marcado con CALCAR, ya recortado), o null.
    private static CalcoFrame? ExtensionHoja(Database database)
    {
        CalcoFrame? hallada = null;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var espacio = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in espacio)
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entidad) || !string.Equals(entidad.Layer, CalcoImporter.LayerName, StringComparison.OrdinalIgnoreCase)) continue;
            using ResultBuffer? marca = entidad.GetXDataForApplication(CalcoIaTool.MarcaApp);
            if (marca == null) continue;
            try
            {
                Extents3d e = entidad.GeometricExtents;
                hallada = new CalcoFrame(e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y); // la última, como BuscarMarca
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
        }
        transaction.Commit();
        return hallada;
    }

    // ── El calco anterior ──

    private sealed class Anterior
    {
        public string Carpeta = string.Empty;
        public List<ObjectId> Entidades = new List<ObjectId>();
        public int Faltan;   // cosas de su plano que ya no están como las dejó
    }

    // El trabajo más reciente de este layout y esta hoja (calcar o corregir, terminado) cuyo plano sigue dibujado: al
    // menos la mitad de sus cosas están en el layout tal como las dejó.
    private static Anterior? BuscarAnterior(Database database, string layout, string fuente, int pagina)
    {
        string raiz = Trabajos();
        if (!Directory.Exists(raiz)) return null;
        string hoja = Normalizar(fuente);
        foreach (string carpeta in Directory.GetDirectories(raiz).OrderByDescending(c => Path.GetFileName(c), StringComparer.Ordinal).Take(60))
        {
            string archivoPedido = Path.Combine(carpeta, "pedido.json"), archivoPlano = Path.Combine(carpeta, "plano.json");
            if (!File.Exists(archivoPedido) || !File.Exists(archivoPlano) || !File.Exists(Path.Combine(carpeta, "resultado.json")) || !File.Exists(Path.Combine(carpeta, "croquis.json"))) continue;
            try
            {
                if (!(MiniJson.Parse(File.ReadAllText(archivoPedido)) is Dictionary<string, object> pedido)) continue;
                string modo = pedido.TryGetValue("modo", out object m) && m is string sm ? sm : "calcar";
                if (modo != "calcar" && modo != "corregir") continue;
                if (!(pedido.TryGetValue("layout", out object l) && l is string sl && string.Equals(sl.Trim(), layout, StringComparison.OrdinalIgnoreCase))) continue;
                if (!(pedido.TryGetValue("fuente", out object f) && f is string sf && Normalizar(sf) == hoja)) continue;
                int suPagina = pedido.TryGetValue("pagina", out object p) && p is double dp ? (int)dp : 1;
                if (CalcoImporter.IsPdf(fuente) && suPagina != pagina) continue;

                CalcoPlano plano = CalcoPlanoJson.Leer(File.ReadAllText(archivoPlano));
                Anterior? anterior = Coincidencias(database, plano);
                if (anterior == null) continue;
                anterior.Carpeta = carpeta;
                return anterior;
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string Normalizar(string ruta)
    {
        try { return Path.GetFullPath(ruta).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return ruta.Replace('/', '\\').ToUpperInvariant(); }
    }

    // Las entidades del layout que son las del plano tal como las dibujó CalcoDibujante (mismas coordenadas): cada lado
    // suelto, cada texto, cada accesorio y cada indicador (su círculo con lo de adentro y el texto de viviendas). Null si
    // está menos de la mitad.
    private static Anterior? Coincidencias(Database database, CalcoPlano plano)
    {
        var segmentos = new List<(CalcoPunto A, CalcoPunto B)>();
        foreach (CalcoLinea linea in plano.Lineas)
        {
            int n = linea.Cerrada ? linea.Puntos.Count : linea.Puntos.Count - 1;
            for (int s = 0; s < n; s++)
            {
                CalcoPunto a = linea.Puntos[s], b = linea.Puntos[(s + 1) % linea.Puntos.Count];
                if (Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9) continue;
                segmentos.Add((a, b));
            }
        }
        var textos = plano.Textos.Where(t => !string.IsNullOrWhiteSpace(t.Texto)).ToList();
        int esperadas = segmentos.Count + textos.Count + plano.Accesorios.Count + plano.Indicadores.Count
            + plano.Indicadores.Count(i => !string.IsNullOrWhiteSpace(i.Viviendas)) + 2 * plano.Notas.Count;
        if (esperadas == 0) return null;

        var anterior = new Anterior();
        bool[] segUsado = new bool[segmentos.Count], textoUsado = new bool[textos.Count], accUsado = new bool[plano.Accesorios.Count], indUsado = new bool[plano.Indicadores.Count], notaUsada = new bool[plano.Notas.Count], flechaUsada = new bool[plano.Notas.Count];
        int halladas = 0;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var espacio = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
            var circulos = new List<Circle>();
            var sueltas = new List<Entity>();   // lo que puede ser parte de un indicador (textos y rayas)
            foreach (ObjectId id in espacio)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entidad)) continue;
                if (entidad is Polyline pl && pl.NumberOfVertices == 2 && !pl.Closed)
                {
                    Point2d a = pl.GetPoint2dAt(0), b = pl.GetPoint2dAt(1);
                    int i = Buscar(segUsado, k => (Igual(a, segmentos[k].A) && Igual(b, segmentos[k].B)) || (Igual(a, segmentos[k].B) && Igual(b, segmentos[k].A)));
                    if (i >= 0) { segUsado[i] = true; anterior.Entidades.Add(id); halladas++; continue; }
                    // La flecha de una nota: de lo que señala a la esquina de su texto.
                    int f = Buscar(flechaUsada, k => Igual(a, plano.Notas[k].Flecha) && b.GetDistanceTo(new Point2d(plano.Notas[k].Posicion.X, plano.Notas[k].Posicion.Y)) < 5);
                    if (f >= 0) { flechaUsada[f] = true; anterior.Entidades.Add(id); halladas++; continue; }
                }
                else if (entidad is MText nota && Buscar(notaUsada, k => Igual(nota.Location, plano.Notas[k].Posicion)) is int n && n >= 0)
                {
                    notaUsada[n] = true; anterior.Entidades.Add(id); halladas++; continue;
                }
                else if (entidad is DBText texto)
                {
                    Point3d centro = texto.AlignmentPoint;
                    int i = Buscar(textoUsado, k => textos[k].Texto.Trim() == texto.TextString && Igual(centro, textos[k].Centro));
                    if (i >= 0) { textoUsado[i] = true; anterior.Entidades.Add(id); halladas++; continue; }
                }
                else if (entidad is BlockReference bloque)
                {
                    string nombre = NombreEfectivo(transaction, bloque);
                    int i = Buscar(accUsado, k => string.Equals(plano.Accesorios[k].Nombre, nombre, StringComparison.OrdinalIgnoreCase) && Igual(bloque.Position, plano.Accesorios[k].Posicion));
                    if (i >= 0) { accUsado[i] = true; anterior.Entidades.Add(id); halladas++; continue; }
                }
                if (entidad is Circle circulo) circulos.Add(circulo);
                if (entidad is MText || entidad is DBText || entidad is Line || entidad is Polyline) sueltas.Add(entidad);
            }

            foreach (Circle circulo in circulos)
            {
                int i = Buscar(indUsado, k => Igual(circulo.Center, plano.Indicadores[k].Centro));
                if (i < 0) continue;
                indUsado[i] = true;
                halladas++;
                anterior.Entidades.Add(circulo.ObjectId);
                double r = circulo.Radius;
                foreach (Entity parte in sueltas)
                {
                    bool adentro = parte is MText mt ? mt.Location.DistanceTo(circulo.Center) < r
                        : parte is Curve curva && !(parte is Polyline pl2 && pl2.NumberOfVertices != 2)
                          && curva.StartPoint.DistanceTo(circulo.Center) <= r + 0.01 && curva.EndPoint.DistanceTo(circulo.Center) <= r + 0.01;
                    if (adentro) anterior.Entidades.Add(parte.ObjectId);
                }
                string viviendas = plano.Indicadores[i].Viviendas.Trim();
                if (viviendas.Length == 0) continue;
                DBText? rotulo = sueltas.OfType<DBText>().FirstOrDefault(t => t.TextString == viviendas
                    && Math.Abs(t.AlignmentPoint.X - circulo.Center.X) < Tolerancia && t.AlignmentPoint.Y < circulo.Center.Y && circulo.Center.Y - t.AlignmentPoint.Y < r + 5);
                if (rotulo != null) { anterior.Entidades.Add(rotulo.ObjectId); halladas++; }
            }
            transaction.Commit();
        }
        if (halladas * 2 < esperadas) return null;
        anterior.Entidades = anterior.Entidades.Distinct().ToList();
        anterior.Faltan = esperadas - halladas;
        return anterior;
    }

    private static int Buscar(bool[] usados, Func<int, bool> coincide)
    {
        for (int k = 0; k < usados.Length; k++) if (!usados[k] && coincide(k)) return k;
        return -1;
    }

    private static bool Igual(Point2d a, CalcoPunto b) => Math.Abs(a.X - b.X) < Tolerancia && Math.Abs(a.Y - b.Y) < Tolerancia;

    private static bool Igual(Point3d a, CalcoPunto b) => Math.Abs(a.X - b.X) < Tolerancia && Math.Abs(a.Y - b.Y) < Tolerancia;

    private static string NombreEfectivo(Transaction transaction, BlockReference bloque)
    {
        ObjectId id = bloque.IsDynamicBlock ? bloque.DynamicBlockTableRecord : bloque.BlockTableRecord;
        return ((BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name;
    }

    private static int Borrar(Database database, List<ObjectId> entidades)
    {
        int borrados = 0;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        foreach (ObjectId id in entidades)
        {
            if (id.IsNull || id.IsErased || !id.IsValid) continue;
            transaction.GetObject(id, OpenMode.ForWrite).Erase();
            borrados++;
        }
        transaction.Commit();
        return borrados;
    }

    // Cotas dentro del marco (de «Detallar con IA» o puestas a mano).
    private static bool HayCotas(Database database)
    {
        bool hay = false;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var espacio = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in espacio)
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead) is Dimension cota)) continue;
            Point3d p = cota.TextPosition;
            if (p.X > ClonarUcTool.FrameMinX && p.X < ClonarUcTool.FrameMaxX && p.Y > ClonarUcTool.DetalleFrameMinY && p.Y < ClonarUcTool.DetalleFrameMaxY) { hay = true; break; }
        }
        transaction.Commit();
        return hay;
    }
}
