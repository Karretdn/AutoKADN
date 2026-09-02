using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AutoKADN.Tools.NomenclaturaPredial;
using AutoKADN.Tools.NomenclaturaVial;

namespace AutoKADN.Commands;

public class NomenclaturaVialCommand
{
    // Este es el UNICO comando publico de nomenclaturas.
    // La navegacion interna determina que herramienta ejecutar.
    [CommandMethod("KARP_NOMVIAL", CommandFlags.Modal)]
    public void Execute()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;

        // NIVEL 1: categoria principal.
        var categoriaOptions = new PromptKeywordOptions("\nSeleccione una categoria [Nomenclaturas]: ")
        {
            AllowNone = false
        };
        categoriaOptions.Keywords.Add("Nomenclaturas");

        PromptResult categoriaResult = editor.GetKeywords(categoriaOptions);
        if (categoriaResult.Status != PromptStatus.OK)
            return;

        if (!categoriaResult.StringResult.Equals("Nomenclaturas", StringComparison.OrdinalIgnoreCase))
            return;

        // NIVEL 2: tipo de nomenclatura.
        var tipoOptions = new PromptKeywordOptions("\nSeleccione el tipo de nomenclatura [Predial/Vial]: ")
        {
            AllowNone = false
        };
        tipoOptions.Keywords.Add("Predial");
        tipoOptions.Keywords.Add("Vial");

        PromptResult tipoResult = editor.GetKeywords(tipoOptions);
        if (tipoResult.Status != PromptStatus.OK)
            return;

        if (tipoResult.StringResult.Equals("Predial", StringComparison.OrdinalIgnoreCase))
        {
            var tool = new NomenclaturaPredialTool();
            tool.Run();
            return;
        }

        if (tipoResult.StringResult.Equals("Vial", StringComparison.OrdinalIgnoreCase))
        {
            var tool = new NomenclaturaVialTool();
            tool.Run();
        }
    }
}
