using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AutoKADN.Tools.NomenclaturaPredial;
using AutoKADN.Tools.NomenclaturaVial;

namespace AutoKADN.Commands;

public sealed class KarpNomenclaturaCommand
{
    [CommandMethod("KARP_NOMENCLATURA", CommandFlags.Modal)]
    public void Execute()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        var options = new PromptKeywordOptions("\nSeleccione la herramienta [Vial/Predial]: ")
        {
            AllowNone = false
        };

        options.Keywords.Add("Vial");
        options.Keywords.Add("Predial");

        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK)
            return;

        switch (result.StringResult.ToUpperInvariant())
        {
            case "VIAL":
                new NomenclaturaVialTool().Run();
                break;

            case "PREDIAL":
                new NomenclaturaPredialTool().Run();
                break;
        }
    }
}
