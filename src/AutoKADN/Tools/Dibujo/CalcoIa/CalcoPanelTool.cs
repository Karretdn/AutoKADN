using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// CALCO, «Importar y calcar»: un solo panel (VentanaCalco) para la hoja, el plano del proyecto y qué hacer, y al aceptar
// hace todo seguido sin más preguntas que el recorte: importa la hoja (CalcarTool; si ya está importada en el layout, no),
// calca con la IA lo elegido (CalcoIaTool) y, si se pidieron cotas magenta, detalla con la IA lo que acaba de dibujar
// (CalcoDetallarTool), que pone también las verdes. Los botones sueltos (CALCAR, CALCOIA) siguen como comandos.
public sealed class CalcoPanelTool
{
    public void Run()
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        try
        {
            if (database.TileMode)
            {
                editor.WriteMessage("\n[CALCO] Abra el layout ANILLO n DETALLE donde va el anillo (o el layout donde va a calcar a mano).\n");
                return;
            }
            bool enAnillo = CalcoIaTool.EnAnilloDetalle(database, out string layout);
            CalcoMemoria memoria = CalcoMemoria.Leer(database);
            bool marcada = CalcoIaTool.BuscarMarca(database, out string importada, out int paginaImportada, out _, out _) && File.Exists(importada);

            var ventana = new VentanaCalco(memoria, marcada ? importada : string.Empty, paginaImportada, enAnillo);
            if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(ventana) != true) return;

            memoria.Hoja = ventana.Hoja;
            memoria.Pagina = ventana.Pagina;
            memoria.Plano = ventana.Plano;
            memoria.SoloImportar = ventana.SoloImportar;
            memoria.Bloques = ventana.Bloques;
            memoria.TextosPredio = ventana.TextosPredio;
            memoria.CotasVerdes = ventana.CotasVerdes;
            memoria.CotasMagenta = ventana.CotasMagenta;
            memoria.Aledanas = ventana.Aledanas;
            memoria.Guardar(database);

            int pagina = CalcoImporter.IsPdf(ventana.Hoja) ? ventana.Pagina : 1;
            if (ventana.SoloImportar || !ventana.YaImportada)
            {
                if (!CalcarTool.ImportarEnLayout(editor, database, ventana.Hoja, pagina)) return;
                if (ventana.SoloImportar) return;
            }
            if (!enAnillo) return;

            if (!CalcoIaTool.BuscarMarca(database, out string fuente, out int paginaMarca, out double[]? recorte, out int giro)) return;
            // Las cotas verdes las pone el calco si no hay otra pasada; con cotas magenta las pone el detallado (las dos
            // salen de lo que quedó dibujado, con sus ids).
            var calco = new OpcionesCalco
            {
                Bloques = ventana.Bloques,
                TextosPredio = ventana.TextosPredio,
                CotasVerdes = ventana.CotasVerdes && !ventana.CotasMagenta,
            };
            if (!CalcoIaTool.Ejecutar(editor, database, layout, fuente, paginaMarca, recorte, giro, calco)) return;
            if (!ventana.CotasMagenta) return;

            string? lectura = CalcoDetallarTool.LeerDibujo(database, layout);
            if (lectura == null)
            {
                editor.WriteMessage("\n[Detallar con IA] El calco no dejó tubería nueva: no hay qué acotar.\n");
                return;
            }
            CalcoDetallarTool.Ejecutar(editor, database, layout, fuente, paginaMarca, recorte, lectura, giro,
                new OpcionesDetalle { CotasVerdes = ventana.CotasVerdes, CotasMagenta = true });
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en CALCO: {ex.Message}\n");
        }
    }
}
