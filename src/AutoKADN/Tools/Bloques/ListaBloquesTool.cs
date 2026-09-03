using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Bloques;

public sealed class ListaBloquesTool
{
    private const double RowHeight = 7.0;
    private const double TextHeight = 2.5;

    private const double DescriptionWidth = 30.0;
    private const double DiameterWidth = 20.0;
    private const double UnitWidth = 18.0;
    private const double QuantityWidth = 20.0;

    private const double ColumnOffset = 5.0;

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

                string diameter = GetDiameter(blockReference);
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

        PromptPointOptions options = new(
            "\nSeleccione el vértice SUPERIOR IZQUIERDO de la lista: ");
        PromptPointResult pointResult = editor.GetPoint(options);

        if (pointResult.Status != PromptStatus.OK)
            return;

        CreateTexts(database, pointResult.Value, counts);
        editor.Regen();

        editor.WriteMessage(
            $"\nLista generada en el layout '{layoutName}': {counts.Count} tipos de bloque.\n");
    }

    private static void CreateTexts(
        Database database,
        Point3d topLeftPoint,
        IReadOnlyDictionary<BlockKey, int> counts)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace =
            (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

        string layerName = GetCurrentLayerName(database, transaction);
        ObjectId textStyleId = database.Textstyle;

        // El punto indicado es el vértice superior izquierdo, justo en la
        // esquina inferior del encabezado. Por tanto, la primera fila de datos
        // queda a media altura de la primera fila disponible.
        double firstRowY = topLeftPoint.Y - (RowHeight * 0.5);

        IEnumerable<KeyValuePair<BlockKey, int>> orderedItems = counts
            .OrderBy(x => x.Key.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.Diameter, StringComparer.OrdinalIgnoreCase);

        int row = 0;
        foreach (var item in orderedItems)
        {
            double y = firstRowY - (row * RowHeight);

            double descriptionX = topLeftPoint.X + ColumnOffset + (DescriptionWidth / 2.0);
            double diameterX = topLeftPoint.X + ColumnOffset + DescriptionWidth + (DiameterWidth / 2.0);
            double unitX = topLeftPoint.X + ColumnOffset + DescriptionWidth + DiameterWidth + (UnitWidth / 2.0);
            double quantityX = topLeftPoint.X + ColumnOffset + DescriptionWidth + DiameterWidth + UnitWidth + (QuantityWidth / 2.0);

            AddCenteredText(transaction, currentSpace, item.Key.Description,
                new Point3d(descriptionX, y, 0), TextHeight, layerName, textStyleId);

            AddCenteredText(transaction, currentSpace, item.Key.Diameter,
                new Point3d(diameterX, y, 0), TextHeight, layerName, textStyleId);

            AddCenteredText(transaction, currentSpace, "UND",
                new Point3d(unitX, y, 0), TextHeight, layerName, textStyleId);

            AddCenteredText(transaction, currentSpace, item.Value.ToString(),
                new Point3d(quantityX, y, 0), TextHeight, layerName, textStyleId);

            row++;
        }

        transaction.Commit();
    }

    private static void AddCenteredText(
        Transaction transaction,
        BlockTableRecord currentSpace,
        string value,
        Point3d position,
        double height,
        string layerName,
        ObjectId textStyleId)
    {
        var text = new DBText
        {
            TextString = value,
            Position = position,
            Height = height,
            TextStyleId = textStyleId,
            Layer = layerName,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = position
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);
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

    private static string GetDiameter(BlockReference blockReference)
    {
        if (!blockReference.IsDynamicBlock)
            return string.Empty;

        foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
        {
            if (string.Equals(property.PropertyName, "DIAMETRO", StringComparison.OrdinalIgnoreCase))
                return property.Value?.ToString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private readonly record struct BlockKey(string Description, string Diameter);
}
