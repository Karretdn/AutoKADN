using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Core;

public sealed class TextCreationService
{
    public void CreateText(Point3d position, string content, double height = 2.5)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(content))
            return;

        Database database = document.Database;

        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForWrite);

        var text = new DBText
        {
            Position = position,
            TextString = content.Trim(),
            Height = height,
            Layer = database.Clayer.IsNull ? "0" : GetCurrentLayerName(database, transaction)
        };

        modelSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);
        transaction.Commit();
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(database.Clayer, OpenMode.ForRead);
        return layer.Name;
    }
}
