using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AutoKADN.Tools.Resumen;

// RESUMENPROYECTO: genera un Excel con el resumen del proyecto por anillo y consolidado (restas, actividades,
// materiales, verificaciones y comparación con interventoría). Solo lee el dibujo: no cambia nada en él ni en las
// otras herramientas. El libro queda junto al DWG y se abre al terminar.
public sealed class ResumenProyectoTool
{
    private const string Tag = "[RESUMENPROYECTO]";

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        try
        {
            string dwg = database.Filename;
            if (string.IsNullOrWhiteSpace(dwg) || !File.Exists(dwg))
            {
                editor.WriteMessage("\n" + Tag + " Guarda el dibujo primero: el resumen se escribe junto al archivo DWG.\n");
                return;
            }

            editor.WriteMessage("\n" + Tag + " Leyendo el plano...\n");
            string destino = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dwg)) ?? string.Empty,
                "RESUMEN PROYECTO - " + Path.GetFileNameWithoutExtension(dwg) + ".xlsx");
            ResumenProyecto resumen;
            string escrito = Generar(database, dwg, destino, out resumen);

            Informar(editor, resumen, escrito);
            AbrirEnExcel(editor, escrito);
        }
        catch (Exception ex)
        {
            editor.WriteMessage("\n" + Tag + " ERROR: " + ex.Message + "\n");
        }
    }

    // Lee, verifica y escribe el libro. Devuelve la ruta realmente escrita (si el archivo estaba abierto en Excel se
    // guarda con la fecha y la hora en el nombre en vez de fallar). Interno: también lo usan las pruebas sin interfaz.
    internal static string Generar(Database database, string dwg, string destino, out ResumenProyecto resumen)
    {
        resumen = ResumenLector.Leer(database, dwg);
        ResumenVerificaciones.Ejecutar(resumen);
        XlsxLibro libro = ResumenLibro.Construir(resumen);

        string ruta = destino;
        try { libro.Guardar(ruta); }
        catch (IOException)
        {
            ruta = Path.Combine(Path.GetDirectoryName(destino) ?? string.Empty,
                Path.GetFileNameWithoutExtension(destino) + " (" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ").xlsx");
            libro.Guardar(ruta);
        }
        return ruta;
    }

    private static void Informar(Editor editor, ResumenProyecto resumen, string ruta)
    {
        int anillos = resumen.Anillos.Count(a => !a.EsTroncal);
        int errores = resumen.Hallazgos.Count(h => h.Severidad == ResumenSeveridad.Error);
        int avisos = resumen.Hallazgos.Count(h => h.Severidad == ResumenSeveridad.Aviso);
        double tuberia = resumen.Consolidado.Ucs.Sum(u => u.Tuberia);
        editor.WriteMessage("\n" + Tag + " " + ruta + "\n");
        editor.WriteMessage("  Anillos: " + anillos + (resumen.Anillos.Any(a => a.EsTroncal) ? " + troncal" : string.Empty) +
            " · tubería total: " + tuberia.ToString("0.0##", CultureInfo.InvariantCulture) + " ML\n");
        if (errores == 0 && avisos == 0)
            editor.WriteMessage("  Verificaciones: sin errores ni avisos.\n");
        else
            editor.WriteMessage("  Verificaciones: " + errores + " error(es), " + avisos + " aviso(s). Mira la hoja VERIFICACIONES.\n");
        foreach (ResumenHallazgo h in resumen.Hallazgos.Where(x => x.Severidad == ResumenSeveridad.Error).Take(5))
            editor.WriteMessage("   ERROR" + (h.Anillo.Length > 0 ? " [" + h.Anillo + "]" : string.Empty) + ": " + h.Detalle + "\n");
    }

    private static void AbrirEnExcel(Editor editor, string ruta)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            editor.WriteMessage("  No se pudo abrir el libro automáticamente (" + ex.Message + "); ábrelo desde la ruta de arriba.\n");
        }
    }
}
