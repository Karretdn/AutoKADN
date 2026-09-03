using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Bloques;

public sealed class ListaBloquesTool
{
    private const double RowHeight = 7.0;
    private const double TextHeight = 2.5;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        Database database = document.Database;
        string layoutName = LayoutManager.Current.CurrentLayout;

        var counts = new Dictionary<BlockKey, int>();

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            ObjectId layoutId = LayoutManager.Current.GetLayoutId(layoutName);
            var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
            var layoutSpace = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

            foreach (ObjectId objectId in layoutSpace)
            {
                if (transaction.GetObject(objectId, OpenMode.ForRead) is not BlockReference blockReference)
                    continue;

                string description = GetBlockName(transaction, blockReference);
                if (string.IsNullOrWhiteSpace(description))
                    continue;

                string diameter = GetDiameter(transaction, blockReference);
                var key = new BlockKey(description, diameter);

                counts.TryGetValue(key, out int currentCount);
                counts[key] = currentCount + 1;
            }

            transaction.Commit();
        }

        if (counts.Count == 0)
        {
            editor.WriteMessage($"\nNo se encontraron bloques en el layout '{layoutName}'.\n");
            return;
        }

        PromptPointResult pointResult = editor.GetPoint("\nIndique el punto de inserción de la lista: ");
        if (pointResult.Status != PromptStatus.OK)
            return;

        CreateTable(database, pointResult.Value, counts);
        editor.Regen();
        editor.WriteMessage($"\nLista de bloques generada en el layout '{layoutName}'.\n");
    }

    private static void CreateTable(
        Database database,
        Point3d insertionPoint,
        IReadOnlyDictionary<BlockKey, int> counts)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace =
            (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

        int rows = counts.Count + 1;
        const int columns = 4;

        var table = new Table
        {
            Position = insertionPoint,
            TableStyle = database.Tablestyle,
            Layer = GetCurrentLayerName(database, transaction)
        };

        table.SetSize(rows, columns);
        table.SetRowHeight(RowHeight);
        table.SetColumnWidth(30.0);
        table.Columns[1].Width = 20.0;
        table.Columns[2].Width = 18.0;
        table.Columns[3].Width = 20.0;

        SetCell(table, 0, 0, "DESCRIPCION");
        SetCell(table, 0, 1, "DIAMETRO");
        SetCell(table, 0, 2, "UNIDAD");
        SetCell(table, 0, 3, "CANTIDAD");

        int row = 1;
        foreach (var item in counts
                     .OrderBy(x => x.Key.Description, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Key.Diameter, StringComparer.OrdinalIgnoreCase))
        {
            SetCell(table, row, 0, item.Key.Description);
            SetCell(table, row, 1, item.Key.Diameter);
            SetCell(table, row, 2, "UND");
            SetCell(table, row, 3, item.Value.ToString());
            row++;
        }

        currentSpace.AppendEntity(table);
        transaction.AddNewlyCreatedDBObject(table, true);
        table.GenerateLayout();
        transaction.Commit();
    }

    private static void SetCell(Table table, int row, int column, string value)
    {
        table.Cells[row, column].TextHeight = TextHeight;
        table.SetTextString(row, column, value);
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        if (transaction.GetObject(database.Clayer, OpenMode.ForRead) is LayerTableRecord layer)
            return layer.Name;

        return string.Empty;
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

    private static string GetDiameter(Transaction transaction, BlockReference blockReference)
    {
        if (!blockReference.AttributeCollection.IsNull)
        {
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                if (transaction.GetObject(attributeId, OpenMode.ForRead) is not AttributeReference attribute)
                    continue;

                if (string.Equals(attribute.Tag, "DIAMETRO", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attribute.Tag, "DIAMETER", StringComparison.OrdinalIgnoreCase))
                    return attribute.TextString.Trim();
            }
        }

        if (blockReference.IsDynamicBlock)
        {
            foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
            {
                if (!string.Equals(property.PropertyName, "DIAMETRO", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(property.PropertyName, "DIAMETER", StringComparison.OrdinalIgnoreCase))
                    continue;

                return property.Value?.ToString()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private readonly record struct BlockKey(string Description, string Diameter);
}
