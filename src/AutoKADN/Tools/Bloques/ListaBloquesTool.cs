using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Bloques;

public sealed class ListaBloquesTool
{
    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        Database database = document.Database;

        string layoutName = LayoutManager.Current.CurrentLayout;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using Transaction transaction = database.TransactionManager.StartTransaction();

        ObjectId layoutId = LayoutManager.Current.GetLayoutId(layoutName);
        var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
        var layoutSpace = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

        foreach (ObjectId objectId in layoutSpace)
        {
            if (transaction.GetObject(objectId, OpenMode.ForRead) is not BlockReference blockReference)
                continue;

            string blockName = GetBlockName(transaction, blockReference);
            if (string.IsNullOrWhiteSpace(blockName))
                continue;

            counts.TryGetValue(blockName, out int currentCount);
            counts[blockName] = currentCount + 1;
        }

        transaction.Commit();

        editor.WriteMessage($"\n\n=== AUTOKADN - BLOQUES DEL LAYOUT: {layoutName} ===\n");

        if (counts.Count == 0)
        {
            editor.WriteMessage("No se encontraron bloques colocados directamente en este layout.\n");
            return;
        }

        int total = counts.Values.Sum();
        int index = 1;

        foreach (var item in counts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            editor.WriteMessage($"{index,3}. {item.Key,-40} Cantidad: {item.Value}\n");
            index++;
        }

        editor.WriteMessage($"\nTipos de bloque: {counts.Count} | Total de referencias: {total}\n");
        editor.WriteMessage("=== FIN DE LISTA ===\n");
    }

    private static string GetBlockName(Transaction transaction, BlockReference blockReference)
    {
        ObjectId definitionId = blockReference.BlockTableRecord;

        if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull)
            definitionId = blockReference.DynamicBlockTableRecord;

        if (transaction.GetObject(definitionId, OpenMode.ForRead) is BlockTableRecord definition)
            return definition.Name;

        return string.Empty;
    }
}
