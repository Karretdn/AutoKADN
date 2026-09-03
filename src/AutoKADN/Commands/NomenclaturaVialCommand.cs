using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AutoKADN.Tools.Anotaciones;
using AutoKADN.Tools.Acotado;
using AutoKADN.Tools.Bloques;
using AutoKADN.Tools.NomenclaturaPredial;
using AutoKADN.Tools.NomenclaturaVial;

namespace AutoKADN.Commands;

public class NomenclaturaVialCommand
{
    [CommandMethod("NOMENK", CommandFlags.Modal)]
    public void Nomenclaturas()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        var options = new PromptKeywordOptions("\nSeleccione nomenclatura [Predial/Vial]: ")
        {
            AllowNone = false
        };
        options.Keywords.Add("Predial");
        options.Keywords.Add("Vial");

        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK)
            return;

        if (result.StringResult.Equals("Predial", StringComparison.OrdinalIgnoreCase))
        {
            new NomenclaturaPredialTool().Run();
            return;
        }

        if (result.StringResult.Equals("Vial", StringComparison.OrdinalIgnoreCase))
            new NomenclaturaVialTool().Run();
    }

    [CommandMethod("LIMIK", CommandFlags.Modal)]
    public void Limites()
    {
        new LimiteTool().Run();
    }

    [CommandMethod("COTAK", CommandFlags.Modal)]
    public void Acotado()
    {
        new CotaTool().Run();
    }

    [CommandMethod("ANOTACIONES", CommandFlags.Modal)]
    public void Anotaciones()
    {
        new AnotacionesTool().Run();
    }

    [CommandMethod("LISTABLOQUES", CommandFlags.Modal)]
    public void ListaBloques()
    {
        new ListaBloquesTool().Run();
    }
}
