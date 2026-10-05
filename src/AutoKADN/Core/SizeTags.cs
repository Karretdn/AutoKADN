using Autodesk.AutoCAD.DatabaseServices;

namespace AutoKADN.Core;

/// <summary>
/// Marca (XData) los textos de una línea que dibujan Límites, Nomenclatura vial y predial para poder
/// encontrarlos después y cambiarles el tamaño. Usa una aplicación propia (AUTOKADN_TAM) para no tocar
/// el XData "AUTOKADN" que otras herramientas usan para decidir qué se clona o se cuenta.
/// </summary>
public static class SizeTags
{
    public const string AppName = "AUTOKADN_TAM";

    public static void Tag(DBObject target, Database database, Transaction transaction, string kind)
    {
        var regApps = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (!regApps.Has(AppName))
        {
            regApps.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            regApps.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
        target.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind));
    }

    /// <summary>LIMITE, VIAL o PREDIAL si el objeto fue marcado; null si no.</summary>
    public static string? ReadKind(DBObject target)
    {
        using ResultBuffer? buffer = target.GetXDataForApplication(AppName);
        if (buffer is null) return null;
        TypedValue[] values = buffer.AsArray();
        return values.Length > 1 ? values[1].Value as string : null;
    }
}
