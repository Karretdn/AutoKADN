using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AutoKADN.Tools.Anotaciones;
using AutoKADN.Tools.NomenclaturaPredial;
using AutoKADN.Tools.NomenclaturaVial;

namespace AutoKADN.Commands;

public class NomenclaturaVialCommand
{
    // UNICO comando publico de entrada para estas herramientas.
    [CommandMethod("KARP_NOMVIAL", CommandFlags.Modal)]
    public void Execute()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;

        var categoriaOptions = new PromptKeywordOptions(
            "\nSeleccione una categoria [Nomenclaturas/Anotaciones]: ")
        {
            AllowNone = false
        };
        categoriaOptions.Keywords.Add("Nomenclaturas");
        categoriaOptions.Keywords.Add("Anotaciones");

        PromptResult categoriaResult = editor.GetKeywords(categoriaOptions);
        if (categoriaResult.Status != PromptStatus.OK)
            return;

        if (categoriaResult.StringResult.Equals("Nomenclaturas", StringComparison.OrdinalIgnoreCase))
        {
            var tipoOptions = new PromptKeywordOptions(
                "\nSeleccione el tipo de nomenclatura [Predial/Vial]: ")
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
                new NomenclaturaPredialTool().Run();
                return;
            }

            if (tipoResult.StringResult.Equals("Vial", StringComparison.OrdinalIgnoreCase))
            {
                new NomenclaturaVialTool().Run();
            }

            return;
        }

        if (categoriaResult.StringResult.Equals("Anotaciones", StringComparison.OrdinalIgnoreCase))
        {
            var anotacionOptions = new PromptKeywordOptions(
                "\nSeleccione la anotacion [Limite]: ")
            {
                AllowNone = false
            };
            anotacionOptions.Keywords.Add("Limite");

            PromptResult anotacionResult = editor.GetKeywords(anotacionOptions);
            if (anotacionResult.Status != PromptStatus.OK)
                return;

            if (anotacionResult.StringResult.Equals("Limite", StringComparison.OrdinalIgnoreCase))
            {
                new LimiteTool().Run();
            }
        }
    }
}
