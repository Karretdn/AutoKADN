using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Core;

public sealed class TextCreationService
{
    public void CreateText(Point3d position, string content, double height = 1.45)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(content))
            return;

        Database database = document.Database;

        using Transaction transaction = database.TransactionManager.StartTransaction();

        // Use the space currently active in AutoCAD.
        // This allows the command to work in both Model Space and Layouts.
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId,
            OpenMode.ForWrite);

        var text = new DBText
        {
            Position = position,
            TextString = content.Trim(),
            Height = height,
            Layer = GetCurrentLayerName(database, transaction)
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);
        transaction.Commit();
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(
            database.Clayer,
            OpenMode.ForRead);

        return layer.Name;
    }
}
