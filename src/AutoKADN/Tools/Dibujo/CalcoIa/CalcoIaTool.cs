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

// «Calcar con IA» (botón, comando CALCOIA): le pasa la hoja a AutoKADN.CalcoIA.exe (que la lee con Claude Code o con la
// API de Anthropic, ver src/AutoKADN.CalcoIA) y dibuja la geometría que devuelve en el layout ANILLO n DETALLE abierto
// (las cotas las pone «Detallar con IA»). La hoja: la importada con CALCAR en ese layout (su marca) o, si no hay, la que
// elija el usuario (PDF y página).
// Cada corrida deja su carpeta en %LOCALAPPDATA%\AutoKADN\CalcoIA\trabajos (pedido, croquis, plano, vista y registro).
public sealed class CalcoIaTool
{
    private const string MarcaApp = "AUTOKADN_CALCO";
    private static readonly Regex AnilloDetalle = new Regex(@"^ANILLO\s+\d+\s+DETALLE$", RegexOptions.IgnoreCase);
    private static readonly Regex TituloAnillo = new Regex(@"ANILLO\s+\d+\s*-\s*(?<d>\d+\s*/\s*\d+)", RegexOptions.IgnoreCase);

    public void Run()
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        try
        {
            if (!EnAnilloDetalle(database, out string layout))
            {
                editor.WriteMessage("\n[CALCOIA] Abra un layout ANILLO n DETALLE (en espacio papel) con la hoja importada con CALCAR.\n");
                return;
            }
            if (!ElegirHoja(editor, database, "CALCOIA", out string fuente, out int pagina, out double[]? recorte, out int giro)) return;
            Ejecutar(editor, database, layout, fuente, pagina, recorte, giro);
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en CALCOIA: {ex.Message}\n");
        }
    }

    // «Ajustes IA» (botón, comando AJUSTESIA): la ventana de ajustes de AutoKADN.CalcoIA.exe (motor, modelo, clave de la
    // API y parámetros del dibujo). Corre aparte: no bloquea AutoCAD.
    internal static void AbrirAjustes()
    {
        Editor? editor = AcadApplication.DocumentManager.MdiActiveDocument?.Editor;
        string? ejecutable = BuscarEjecutable();
        if (ejecutable == null)
        {
            editor?.WriteMessage("\n[Ajustes IA] No se encontró AutoKADN.CalcoIA.exe (compile la solución: queda en bin\\CalcoIA).\n");
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ejecutable, "ajustes") { UseShellExecute = false, CreateNoWindow = true });
    }

    internal static bool EsAnilloDetalle(string layout) => AnilloDetalle.IsMatch(layout.Trim());

    // La hoja importada con CALCAR en este layout o, si no hay, la que elija el usuario (PDF o imagen, y la página).
    internal static bool ElegirHoja(Editor editor, Database database, string etiqueta, out string fuente, out int pagina, out double[]? recorte, out int giro)
    {
        if (BuscarMarca(database, out fuente, out pagina, out recorte, out giro) && File.Exists(fuente))
        {
            editor.WriteMessage($"\n[{etiqueta}] Hoja: «{Path.GetFileName(fuente)}»{(CalcoImporter.IsPdf(fuente) ? ", página " + pagina : string.Empty)}{(giro != 0 ? $", girada {giro}°" : string.Empty)} (importada con CALCAR).\n");
            return true;
        }
        recorte = null;
        giro = 0;
        pagina = 1;
        var archivo = new PromptOpenFileOptions("\nElija la hoja (PDF escaneado o imagen) con el croquis del anillo")
        {
            Filter = "Hoja escaneada (*.pdf;*.jpg;*.jpeg;*.png)|*.pdf;*.jpg;*.jpeg;*.png",
        };
        PromptFileNameResult elegido = editor.GetFileNameForOpen(archivo);
        if (elegido.Status != PromptStatus.OK) { fuente = string.Empty; return false; }
        fuente = elegido.StringResult;
        if (!CalcoImporter.IsPdf(fuente)) return true;
        var numero = new PromptIntegerOptions("\nPágina del PDF con el croquis del anillo") { AllowZero = false, AllowNegative = false, DefaultValue = 1, UseDefaultValue = true };
        PromptIntegerResult r = editor.GetInteger(numero);
        if (r.Status != PromptStatus.OK) return false;
        pagina = r.Value;
        return true;
    }

    // Recorte en fracción de la hoja (0..1, origen arriba a la izquierda) a partir de lo importado y el rectángulo elegido.
    internal static double[] Fracciones(CalcoFrame contenido, CalcoFrame recorte)
    {
        CalcoFrame visible = recorte.Intersect(contenido);
        double F(double v) => Math.Round(Math.Max(0.0, Math.Min(1.0, v)), 4);
        return new[]
        {
            F((visible.MinX - contenido.MinX) / contenido.Width), F((contenido.MaxY - visible.MaxY) / contenido.Height),
            F((visible.MaxX - contenido.MinX) / contenido.Width), F((contenido.MaxY - visible.MinY) / contenido.Height),
        };
    }

    // Marca la imagen o el PDF importado con su archivo, página y recorte, para poder calcarlo después con CALCOIA.
    internal static void Marcar(Database database, ObjectId entidad, string fuente, int pagina, double[]? recorte)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var regApps = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (!regApps.Has(MarcaApp))
        {
            regApps.UpgradeOpen();
            var registro = new RegAppTableRecord { Name = MarcaApp };
            regApps.Add(registro);
            transaction.AddNewlyCreatedDBObject(registro, true);
        }
        var objeto = (Entity)transaction.GetObject(entidad, OpenMode.ForWrite);
        // El recorte se guarda en la hoja sin girar: así sirve aunque después la giren (CALCAR > Girar o ROTATE).
        if (recorte != null) recorte = ANatural(recorte, CalcoImporter.RotationOf(objeto));
        string textoRecorte = recorte == null ? string.Empty : string.Join(";", recorte.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
        objeto.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, MarcaApp),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, fuente),
            new TypedValue((int)DxfCode.ExtendedDataInteger32, pagina),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, textoRecorte));
        transaction.Commit();
    }

    // Recorte [x0, y0, x1, y1] (fracciones, origen arriba a la izquierda) de la hoja vista con un giro (grados
    // antihorarios) a la hoja sin girar.
    internal static double[] ANatural(double[] r, int giro)
    {
        (double, double) P(double u, double v) => ((giro + 360) % 360) switch
        {
            90 => (1 - v, u),
            180 => (1 - u, 1 - v),
            270 => (v, 1 - u),
            _ => (u, v),
        };
        var esquinas = new[] { P(r[0], r[1]), P(r[2], r[3]) };
        return new[] { esquinas.Min(e => e.Item1), esquinas.Min(e => e.Item2), esquinas.Max(e => e.Item1), esquinas.Max(e => e.Item2) };
    }

    // El flujo completo: pedido → AutoKADN.CalcoIA.exe (ventana de avance) → dibujar el plano.
    internal static void Ejecutar(Editor editor, Database database, string layout, string fuente, int pagina, double[]? recorte, int giro = 0)
    {
        string? ejecutable = BuscarEjecutable();
        if (ejecutable == null)
        {
            editor.WriteMessage("\n[Calcar con IA] No se encontró AutoKADN.CalcoIA.exe (compile la solución: queda en bin\\CalcoIA).\n");
            return;
        }

        string carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoKADN", "CalcoIA", "trabajos", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(carpeta);
        File.WriteAllText(Path.Combine(carpeta, "pedido.json"), Pedido(fuente, pagina, recorte, layout, Tamanos.Load(), "calcar", DiametroDelLayout(database), giro), new UTF8Encoding(false));

        var ventana = new VentanaCalcoIa(ejecutable, carpeta);
        bool? dibujar = Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(ventana);
        if (ventana.Costo != null) editor.WriteMessage($"\n[Calcar con IA] Costo: {ventana.Costo}\n");
        if (dibujar != true)
        {
            editor.WriteMessage(ventana.Error != null
                ? $"\n[Calcar con IA] No se pudo calcar: {ventana.Error}. Detalles en {Path.Combine(carpeta, "registro.txt")}\n"
                : "\n[Calcar con IA] Cancelado: no se dibujó nada.\n");
            return;
        }

        CalcoPlano plano = CalcoPlanoJson.Leer(File.ReadAllText(Path.Combine(carpeta, "plano.json")));
        CalcoResultado resultado = CalcoDibujante.Dibujar(database, database.CurrentSpaceId, layout, plano, Tamanos.Load(), conCotas: false);
        if (!resultado.Ok)
        {
            editor.WriteMessage($"\n[Calcar con IA] No se dibujó nada: {resultado.Error}.\n");
            return;
        }
        if (ApartarHoja(database)) editor.WriteMessage("\n[Calcar con IA] La hoja de referencia se movió a la derecha del marco (sigue marcada para «Detallar con IA»).");
        editor.Regen();
        editor.WriteMessage($"\n[Calcar con IA] Dibujado en {layout}: {resultado.Lineas} líneas, {resultado.Textos} textos, "
            + $"{resultado.Accesorios} accesorios, {resultado.Indicadores} indicadores. Deshacer (H) lo quita entero. Las cotas las pone «Detallar con IA».\n");
        foreach (string aviso in resultado.Avisos) editor.WriteMessage($"[Calcar con IA] Aviso: {aviso}.\n");
        foreach (string duda in ventana.Dudas) editor.WriteMessage($"[Calcar con IA] Revise: {duda}\n");
        editor.WriteMessage($"[Calcar con IA] Trabajo guardado en {carpeta}\n");
    }

    // AUTOKADN_CALCOIA (ruta del .exe) o, subiendo desde la DLL del plugin, bin\CalcoIA\AutoKADN.CalcoIA.exe.
    internal static string? BuscarEjecutable()
    {
        string? propio = Environment.GetEnvironmentVariable("AUTOKADN_CALCOIA");
        if (!string.IsNullOrWhiteSpace(propio) && File.Exists(propio)) return propio;
        string? carpeta = Path.GetDirectoryName(typeof(CalcoIaTool).Assembly.Location);
        for (int i = 0; carpeta != null && i < 8; i++)
        {
            foreach (string candidato in new[] { Path.Combine(carpeta, "CalcoIA", "AutoKADN.CalcoIA.exe"), Path.Combine(carpeta, "bin", "CalcoIA", "AutoKADN.CalcoIA.exe") })
                if (File.Exists(candidato)) return candidato;
            carpeta = Path.GetDirectoryName(carpeta);
        }
        return null;
    }

    // El diámetro del título del cajetín del layout abierto («ANILLO 1 - 3/4"»), o vacío si no lo tiene.
    internal static string DiametroDelLayout(Database database)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var espacio = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        string diametro = string.Empty;
        foreach (ObjectId id in espacio)
        {
            DBObject objeto = transaction.GetObject(id, OpenMode.ForRead);
            string texto = objeto is MText m ? m.Text : objeto is DBText d ? d.TextString : string.Empty;
            Match coincide = TituloAnillo.Match(texto);
            if (coincide.Success) { diametro = coincide.Groups["d"].Value.Replace(" ", string.Empty); break; }
        }
        transaction.Commit();
        return diametro;
    }

    internal static string Pedido(string fuente, int pagina, double[]? recorte, string layout, TamanoValores t, string modo = "calcar", string diametro = "", int giro = 0)
    {
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"fuente\": ").Append(Texto(fuente)).Append(",\n");
        json.Append("  \"pagina\": ").Append(pagina.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        if (recorte != null) json.Append("  \"recorte\": [").Append(string.Join(", ", recorte.Select(N))).Append("],\n");
        json.Append("  \"layout\": ").Append(Texto(layout)).Append(",\n");
        json.Append("  \"modo\": ").Append(Texto(modo)).Append(",\n");
        if (diametro.Length > 0) json.Append("  \"diametro\": ").Append(Texto(diametro)).Append(",\n");
        if (giro != 0) json.Append("  \"giro\": ").Append(giro.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        json.Append($"  \"tamanos\": {{ \"vial\": {N(t.Vial)}, \"predial\": {N(t.Predial)}, \"cotas\": {N(t.Cotas)}, \"bloques\": {N(t.Bloques)} }}\n");
        json.Append("}\n");
        return json.ToString();
    }

    internal static string JsonTexto(string valor) => Texto(valor);

    private static string Texto(string valor)
    {
        var s = new StringBuilder("\"");
        foreach (char c in valor)
        {
            switch (c)
            {
                case '"': s.Append("\\\""); break;
                case '\\': s.Append("\\\\"); break;
                case '\n': s.Append("\\n"); break;
                case '\r': s.Append("\\r"); break;
                case '\t': s.Append("\\t"); break;
                default:
                    if (c < ' ') s.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else s.Append(c);
                    break;
            }
        }
        return s.Append('"').ToString();
    }

    internal static bool EnAnilloDetalle(Database database, out string layout)
    {
        layout = string.Empty;
        if (database.TileMode) return false;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var actual = (Layout)transaction.GetObject(LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout), OpenMode.ForRead);
        layout = actual.LayoutName.Trim();
        bool espacioPapel = database.CurrentSpaceId == actual.BlockTableRecordId;
        transaction.Commit();
        return espacioPapel && EsAnilloDetalle(layout);
    }

    // La última hoja importada con CALCAR en el layout abierto (imagen o PDF de la capa CALCO con la marca).
    // El giro sale de cómo está la hoja ahora (CALCAR > Girar o ROTATE); el recorte va en la hoja derecha (la IA la lee
    // derecha y el enderezado gira lo que leyó).
    // La hoja de referencia (la imagen o el PDF marcado) se lleva a la derecha del marco del plano, alineada con su borde
    // de arriba, para que no tape el resultado. Solo si está encima del marco (si el usuario ya la movió, se deja). La
    // marca va con ella: «Detallar con IA» la sigue encontrando.
    internal static bool ApartarHoja(Database database)
    {
        bool movida = false;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var espacio = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in espacio)
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entidad) || !string.Equals(entidad.Layer, CalcoImporter.LayerName, StringComparison.OrdinalIgnoreCase)) continue;
            using ResultBuffer? marca = entidad.GetXDataForApplication(MarcaApp);
            if (marca == null) continue;
            Extents3d caja;
            try { caja = entidad.GeometricExtents; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
            bool encima = caja.MinPoint.X < ClonarUcTool.FrameMaxX && caja.MaxPoint.X > ClonarUcTool.FrameMinX
                && caja.MinPoint.Y < ClonarUcTool.DetalleFrameMaxY && caja.MaxPoint.Y > ClonarUcTool.DetalleFrameMinY;
            if (!encima) continue;
            const double separacion = 10.0;
            var mover = new Vector3d(ClonarUcTool.FrameMaxX + separacion - caja.MinPoint.X, ClonarUcTool.DetalleFrameMaxY - caja.MaxPoint.Y, 0.0);
            entidad.UpgradeOpen();
            entidad.TransformBy(Matrix3d.Displacement(mover));
            movida = true;
        }
        transaction.Commit();
        return movida;
    }

    internal static bool BuscarMarca(Database database, out string fuente, out int pagina, out double[]? recorte, out int giro)
    {
        fuente = string.Empty;
        pagina = 1;
        recorte = null;
        giro = 0;
        bool hallada = false;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var espacio = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in espacio)
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entidad) || !string.Equals(entidad.Layer, CalcoImporter.LayerName, StringComparison.OrdinalIgnoreCase)) continue;
            using ResultBuffer? marca = entidad.GetXDataForApplication(MarcaApp);
            TypedValue[]? valores = marca?.AsArray();
            if (valores == null || valores.Length < 4 || !(valores[1].Value is string archivo)) continue;
            fuente = archivo;
            pagina = valores[2].Value is int p ? p : 1;
            recorte = null;
            if (valores[3].Value is string texto && texto.Length > 0)
            {
                double[] partes = texto.Split(';').Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN).ToArray();
                if (partes.Length == 4 && partes.All(v => !double.IsNaN(v))) recorte = partes;
            }
            giro = CalcoImporter.RotationOf(entidad);
            hallada = true; // se queda con la última (la más reciente)
        }
        transaction.Commit();
        return hallada;
    }
}
