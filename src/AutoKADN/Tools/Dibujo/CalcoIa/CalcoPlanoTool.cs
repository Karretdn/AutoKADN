using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AutoKADN.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// CALCOPLANO: dibuja en el layout ANILLO n DETALLE abierto un plano de calco guardado en un archivo .json (ver
// CalcoPlanoJson). Es la pieza que usará «Calcar con IA»; por ahora sirve para probar el dibujante con planos hechos a mano.
// Todo queda en un solo paso: un H (deshacer) lo quita entero.
public sealed class CalcoPlanoTool
{
    private static readonly Regex DetailLayout = new Regex(@"^ANILLO\s+\d+\s+DETALLE$", RegexOptions.IgnoreCase);

    public void Run()
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        try
        {
            if (!TryGetDetailLayout(editor, database, out string layoutName)) return;

            var options = new PromptOpenFileOptions("\nSeleccione el plano de calco (.json): ")
            {
                Filter = "Plano de calco (*.json)|*.json|Todos los archivos (*.*)|*.*",
                DialogCaption = "Dibujar plano de calco",
                PreferCommandLine = false,
            };
            PromptFileNameResult file = editor.GetFileNameForOpen(options);
            if (file.Status != PromptStatus.OK) return;
            if (!File.Exists(file.StringResult))
            {
                editor.WriteMessage("\n[CALCOPLANO] No se encontró el archivo.\n");
                return;
            }

            CalcoPlano plano;
            try { plano = CalcoPlanoJson.Leer(File.ReadAllText(file.StringResult)); }
            catch (FormatException ex)
            {
                editor.WriteMessage($"\n[CALCOPLANO] No se pudo leer «{Path.GetFileName(file.StringResult)}»: {ex.Message}.\n");
                return;
            }

            CalcoResultado resultado = CalcoDibujante.Dibujar(database, database.CurrentSpaceId, layoutName, plano, Tamanos.Load());
            if (!resultado.Ok)
            {
                editor.WriteMessage($"\n[CALCOPLANO] No se dibujó nada: {resultado.Error}.\n");
                return;
            }

            editor.Regen();
            editor.WriteMessage($"\n[CALCOPLANO] Dibujado en {layoutName}: {resultado.Lineas} líneas, {resultado.Textos} textos, {resultado.Cotas} cotas, "
                + $"{resultado.Accesorios} accesorios, {resultado.Indicadores} indicadores.\n");
            foreach (string aviso in resultado.Avisos) editor.WriteMessage($"[CALCOPLANO] Aviso: {aviso}.\n");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en CALCOPLANO: {ex.Message}\n");
        }
    }

    // Solo en el espacio papel de un layout ANILLO n DETALLE (no dentro de un viewport): ahí están el marco y la paleta
    // de la plantilla con la que se dibuja.
    private static bool TryGetDetailLayout(Editor editor, Database database, out string layoutName)
    {
        layoutName = string.Empty;
        if (database.TileMode)
        {
            editor.WriteMessage("\n[CALCOPLANO] Abra un layout ANILLO n DETALLE.\n");
            return false;
        }

        ObjectId layoutSpace;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            ObjectId layoutId = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
            var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
            layoutName = layout.LayoutName.Trim();
            layoutSpace = layout.BlockTableRecordId;
            transaction.Commit();
        }

        if (!DetailLayout.IsMatch(layoutName))
        {
            editor.WriteMessage($"\n[CALCOPLANO] El layout abierto es «{layoutName}»: abra un layout ANILLO n DETALLE.\n");
            return false;
        }
        if (database.CurrentSpaceId != layoutSpace)
        {
            editor.WriteMessage("\n[CALCOPLANO] Está dentro de un viewport (espacio modelo). Salga al espacio papel del layout e intente de nuevo.\n");
            return false;
        }
        return true;
    }
}
