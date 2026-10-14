using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AutoKADN.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// DETALLARIA, «Detallar con IA»: el anillo ya está dibujado en el ANILLO n DETALLE abierto (a mano o con RECORTEANILLOS)
// y la IA lee de la hoja (el croquis) solo los números: cotas de largo, cotas magenta de ubicación y de accesorios. Antes
// comprueba que la hoja sea la de ese anillo; si no, no se pone nada. Lo dibuja CalcoDetallador.
//  · La hoja: la importada con CALCAR en el layout (su marca) o, si no hay, la que elija el usuario (PDF y página).
//  · El trabajo queda en %LOCALAPPDATA%\AutoKADN\CalcoIA\trabajos (pedido, lectura, detalle y registro).
public sealed class CalcoDetallarTool
{
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
                editor.WriteMessage("\n[DETALLARIA] Abra un layout ANILLO n DETALLE (en espacio papel) con el anillo ya dibujado.\n");
                return;
            }

            string lecturaJson;
            int tramos;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CalcoLectura lectura = CalcoLectorLayout.Leer(transaction, (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead));
                tramos = lectura.Tramos.Count(t => t.Nuevo);
                lecturaJson = lectura.AJson(layout);
                transaction.Commit();
            }
            if (tramos == 0)
            {
                editor.WriteMessage("\n[DETALLARIA] No hay tubería nueva dibujada en este layout (líneas azules continuas). Dibuje primero el anillo o use «Calcar con IA».\n");
                return;
            }

            if (!CalcoIaTool.ElegirHoja(editor, database, "DETALLARIA", out string fuente, out int pagina, out double[]? recorte, out int giro)) return;
            Ejecutar(editor, database, layout, fuente, pagina, recorte, lecturaJson, giro);
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en DETALLARIA: {ex.Message}\n");
        }
    }

    private static void Ejecutar(Editor editor, Database database, string layout, string fuente, int pagina, double[]? recorte, string lecturaJson, int giro)
    {
        string? ejecutable = CalcoIaTool.BuscarEjecutable();
        if (ejecutable == null)
        {
            editor.WriteMessage("\n[Detallar con IA] No se encontró AutoKADN.CalcoIA.exe (compile la solución: queda en bin\\CalcoIA).\n");
            return;
        }

        string carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoKADN", "CalcoIA", "trabajos", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(carpeta);
        File.WriteAllText(Path.Combine(carpeta, "pedido.json"), CalcoIaTool.Pedido(fuente, pagina, recorte, layout, Tamanos.Load(), "detallar", CalcoIaTool.DiametroDelLayout(database), giro), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(carpeta, "lectura.json"), lecturaJson, new UTF8Encoding(false));

        var ventana = new VentanaCalcoIa(ejecutable, carpeta, detallar: true);
        bool? poner = Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(ventana);
        if (ventana.Costo != null) editor.WriteMessage($"\n[Detallar con IA] Costo: {ventana.Costo}\n");
        if (ventana.Motivos.Count > 0)
        {
            editor.WriteMessage("\n[Detallar con IA] La hoja no corresponde a este anillo; no se puso nada:\n");
            foreach (string motivo in ventana.Motivos) editor.WriteMessage($"[Detallar con IA]   • {motivo}\n");
            return;
        }
        if (poner != true)
        {
            editor.WriteMessage(ventana.Error != null
                ? $"\n[Detallar con IA] No se pudo detallar: {ventana.Error}. Detalles en {Path.Combine(carpeta, "registro.txt")}\n"
                : "\n[Detallar con IA] Cancelado: no se puso nada.\n");
            return;
        }

        CalcoDetalle detalle = CalcoDetalleJson.Leer(File.ReadAllText(Path.Combine(carpeta, "detalle.json")));
        CalcoResultado resultado = CalcoDetallador.Detallar(database, database.CurrentSpaceId, detalle, Tamanos.Load());
        if (!resultado.Ok)
        {
            editor.WriteMessage($"\n[Detallar con IA] No se puso nada: {resultado.Error}.\n");
            return;
        }
        if (CalcoIaTool.ApartarHoja(database)) editor.WriteMessage("\n[Detallar con IA] La hoja de referencia se movió a la derecha del marco.");
        editor.Regen();
        editor.WriteMessage($"\n[Detallar con IA] {layout}: {resultado.Cotas} cotas y {resultado.Textos} marcas LC. Deshacer (H) lo quita entero.\n");
        foreach (string aviso in resultado.Avisos) editor.WriteMessage($"[Detallar con IA] Aviso: {aviso}.\n");
        foreach (string aviso in ventana.Avisos) editor.WriteMessage($"[Detallar con IA] Aviso: {aviso}.\n");
        foreach (string duda in ventana.Dudas) editor.WriteMessage($"[Detallar con IA] Revise: {duda}\n");
        editor.WriteMessage($"[Detallar con IA] Trabajo guardado en {carpeta}\n");
    }
}
