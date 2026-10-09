using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AutoKADN.Tools.Anotaciones;
using AutoKADN.Tools.Acotado;
using AutoKADN.Tools.Bloques;
using AutoKADN.Tools.Debug;
using AutoKADN.Tools.Dibujo;
using AutoKADN.Tools.Excel;
using AutoKADN.Tools.Layouts;
using AutoKADN.Tools.Medidas;
using AutoKADN.Tools.NomenclaturaPredial;
using AutoKADN.Tools.NomenclaturaVial;

namespace AutoKADN.Commands;

public class NomenclaturaVialCommand
{
    [CommandMethod("NOMENK", CommandFlags.Modal)]
    public void Nomenclaturas()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        var options = new PromptKeywordOptions("\nSeleccione nomenclatura [Predial/Vial]: ") { AllowNone = false };
        options.Keywords.Add("Predial"); options.Keywords.Add("Vial");
        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK) return;
        if (result.StringResult.Equals("Predial", StringComparison.OrdinalIgnoreCase)) { new NomenclaturaPredialTool().Run(); return; }
        if (result.StringResult.Equals("Vial", StringComparison.OrdinalIgnoreCase)) new NomenclaturaVialTool().Run();
    }

    [CommandMethod("LIMIK", CommandFlags.Modal)] public void Limites() => new LimiteTool().Run();
    [CommandMethod("COTAK", CommandFlags.Modal)] public void Acotado() => new CotaTool().Run();
    [CommandMethod("PEGAS", CommandFlags.Modal)] public void Pegas() => new PegasTool().Run();
    [CommandMethod("ANOTACIONES", CommandFlags.Modal)] public void Anotaciones() => new AnotacionesTool().Run();
    [CommandMethod("LISTABLOQUES", CommandFlags.Modal)] public void ListaBloques() => new ListaBloquesTool().Run();
    [CommandMethod("RESUMENUC", CommandFlags.Modal)] public void ResumenUC() => new ResumenUCTool().Run();
    [CommandMethod("MATERIALPRUEBA", CommandFlags.Modal)] public void MaterialPrueba() => new MaterialPruebaTool().Run();
    [CommandMethod("CLONARUC", CommandFlags.Modal)] public void ClonarUc() => new ClonarUcTool().Run();
    [CommandMethod("LINEARAPIDA", CommandFlags.Modal)] public void LineaRapida() => new LineaRapidaTool().Run();
    [CommandMethod("CALCAR", CommandFlags.Modal)] public void Calcar() => new CalcarTool().Run();
    [CommandMethod("RELLENARDATOS", CommandFlags.Modal)] public void RellenarDatos() => new RellenarDatosTool().Run();
    [CommandMethod("NUEVOANILLO", CommandFlags.Modal)] public void NuevoAnillo() => new NuevoAnilloTool().Run();
    [CommandMethod("RECORTEANILLOS", CommandFlags.Modal)] public void RecorteAnillos() => new RecorteAnillosTool().Run();
    [CommandMethod("TAMANOS", CommandFlags.Modal)] public void Tamanos() => new TamanosTool().Run();
    [CommandMethod("RESUMENOBRA", CommandFlags.Modal)] public void ResumenObra() => new ResumenObraTool().Run();
    [CommandMethod("GENERAREXCEL", CommandFlags.Modal)] public void GenerarExcel() => new GenerarExcelTool().Run();
    [CommandMethod("DEBUGDETALLES", CommandFlags.Modal)] public void DebugDetalles() => new DebugDetallesTool().Run();
}
