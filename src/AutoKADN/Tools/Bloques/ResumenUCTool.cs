using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoKADN.Tools.Bloques;

public sealed class ResumenUCTool
{
    private const double RowHeight = 5.0;
    private const double TextHeight = 2.5;
    private const int SlotsPerColumn = 5;
    private const double DescriptionWidth = 86.0;
    private const double UnitWidth = 8.0;
    private const double SubtotalWidth = 14.0;
    private const double ColumnWidth = DescriptionWidth + UnitWidth + SubtotalWidth;
    private const double RightColumnShift = -5.0;
    private const double DescriptionLeftMargin = 1.5;
    private const double UnitCenterCorrection = -1.0;
    private const double SubtotalCenterCorrection = -2.0;
    private const double UnitHorizontalShift = 82.0;
    private const double SubtotalHorizontalShift = 95.0;
    private const string UcLayerHalf = "UC_1-2";
    private const string UcLayerThreeQuarter = "UC_3-4";
    private const string XDataAppName = "AUTOKADN";
    private const string SummaryType = "RESUMEN_UC";

    private static readonly UcSurface[] Surfaces =
    {
        new("ZONA VERDE", 3, null, null, null), new("ANDEN TABLETA", 1, null, null, null),
        new("CALZADA CONCRETO", 8, null, null, null), new("DESTAPADO", 2, null, null, null),
        new("CUNETA", null, 100, 33, 101), new("ANDEN CONCRETO", 5, null, null, null),
        new("ASFALTO", 30, null, null, null), new("ADOQUIN", 4, null, null, null)
    };

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        string layoutName = LayoutManager.Current.CurrentLayout;
        var quantities = new Dictionary<UcKey, double>();

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            ObjectId layoutId = LayoutManager.Current.GetLayoutId(layoutName);
            var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
            var layoutSpace = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId objectId in layoutSpace)
            {
                if (transaction.GetObject(objectId, OpenMode.ForRead) is not Dimension dimension) continue;
                string? diameter = GetUcDiameter(dimension.Layer);
                if (diameter is null) continue;
                string? surface = GetSurface(transaction, dimension);
                if (surface is null) continue;
                if (!TryGetDisplayedDimensionValue(dimension, out double value)) continue;
                var key = new UcKey(diameter, surface);
                quantities.TryGetValue(key, out double current);
                quantities[key] = current + Math.Abs(value);
            }
            transaction.Commit();
        }

        if (quantities.Count == 0)
        {
            editor.WriteMessage($"\nNo se encontraron cotas UC válidas en '{UcLayerHalf}'/'{UcLayerThreeQuarter}' del layout '{layoutName}'.\n");
            return;
        }

        PromptPointResult pointResult = editor.GetPoint(new PromptPointOptions("\nSeleccione el vértice SUPERIOR IZQUIERDO de la lista de UNIDAD CONSTRUCTIVA: "));
        if (pointResult.Status != PromptStatus.OK) return;
        CreateTexts(database, pointResult.Value, quantities, layoutName);
        editor.Regen();
        editor.WriteMessage($"\nResumen UC generado en el layout '{layoutName}'.\n");
    }

    private static string? GetUcDiameter(string layer)
    {
        if (string.Equals(layer, UcLayerHalf, StringComparison.OrdinalIgnoreCase)) return "1/2";
        if (string.Equals(layer, UcLayerThreeQuarter, StringComparison.OrdinalIgnoreCase)) return "3/4";
        return null;
    }

    private static string? GetSurface(Transaction transaction, Dimension dimension)
    {
        Color color = dimension.Color;
        if (color.ColorIndex == 256 || color.IsByLayer)
        {
            ObjectId layerId = dimension.LayerId;
            if (!layerId.IsNull && transaction.GetObject(layerId, OpenMode.ForRead) is LayerTableRecord layer) color = layer.Color;
        }
        foreach (UcSurface surface in Surfaces)
        {
            if (surface.ColorIndex.HasValue && color.ColorIndex == surface.ColorIndex.Value) return surface.Name;
            if (surface.Red.HasValue && IsSameRgb(color, surface.Red.Value, surface.Green!.Value, surface.Blue!.Value)) return surface.Name;
        }
        return null;
    }

    private static bool IsSameRgb(Color color, int red, int green, int blue) => color.Red == red && color.Green == green && color.Blue == blue;

    private static bool TryGetDisplayedDimensionValue(Dimension dimension, out double value)
    {
        value = 0.0;
        string text = dimension.DimensionText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Match match = Regex.Match(text, @"[-+]?\d+(?:[\.,]\d+)?");
        if (!match.Success) return false;
        return double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static void CreateTexts(Database database, Point3d topLeftPoint, IReadOnlyDictionary<UcKey, double> quantities, string layoutName)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        string layerName = GetCurrentLayerName(database, transaction);
        ObjectId textStyleId = database.Textstyle;
        double firstRowY = topLeftPoint.Y - (RowHeight / 2.0);
        string summaryId = Guid.NewGuid().ToString("D");
        EnsureXDataRegApp(database, transaction);
        IEnumerable<KeyValuePair<UcKey, double>> orderedItems = quantities.OrderBy(x => x.Key.Diameter, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key.Surface, StringComparer.OrdinalIgnoreCase);

        int index = 0;
        foreach (var item in orderedItems)
        {
            int column = index / SlotsPerColumn;
            int slot = index % SlotsPerColumn;
            double columnX = topLeftPoint.X + (column * ColumnWidth);
            if (column > 0) columnX += RightColumnShift;
            double y = firstRowY - (slot * RowHeight);
            string description = $"Canalizacion Tubería De Polietileno De {item.Key.Diameter} Pulg. En {ToDisplaySurface(item.Key.Surface)}";
            double descriptionX = columnX + DescriptionLeftMargin;
            double unitX = columnX + DescriptionWidth + (UnitWidth / 2.0) + UnitCenterCorrection + UnitHorizontalShift;
            double subtotalX = columnX + DescriptionWidth + UnitWidth + (SubtotalWidth / 2.0) + SubtotalCenterCorrection + SubtotalHorizontalShift;
            AddLeftAlignedText(transaction, currentSpace, description, new Point3d(descriptionX, y, 0), TextHeight, layerName, textStyleId, layoutName, summaryId);
            AddCenteredText(transaction, currentSpace, "ML", new Point3d(unitX, y, 0), TextHeight, layerName, textStyleId, layoutName, summaryId);
            AddCenteredText(transaction, currentSpace, FormatQuantity(item.Value), new Point3d(subtotalX, y, 0), TextHeight, layerName, textStyleId, layoutName, summaryId);
            index++;
        }
        transaction.Commit();
    }

    private static string ToDisplaySurface(string value) => value.ToLowerInvariant() switch
    {
        "zona verde" => "Zona Verde", "anden tableta" => "Anden Tableta", "calzada concreto" => "Calzada Concreto",
        "destapado" => "Destapado", "cuneta" => "Cuneta", "anden concreto" => "Anden Concreto",
        "asfalto" => "Asfalto", "adoquin" => "Adoquin", _ => value
    };

    private static string FormatQuantity(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);

    private static void AddLeftAlignedText(Transaction transaction, BlockTableRecord currentSpace, string value, Point3d position, double height, string layerName, ObjectId textStyleId, string layoutName, string summaryId)
    {
        var text = new DBText { TextString = value, Position = position, Height = height, TextStyleId = textStyleId, Layer = layerName, HorizontalMode = TextHorizontalMode.TextLeft, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = position };
        currentSpace.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
        AttachSummaryXData(text, SummaryType, layoutName, summaryId);
    }

    private static void AddCenteredText(Transaction transaction, BlockTableRecord currentSpace, string value, Point3d position, double height, string layerName, ObjectId textStyleId, string layoutName, string summaryId)
    {
        var text = new DBText { TextString = value, Position = position, Height = height, TextStyleId = textStyleId, Layer = layerName, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = position };
        currentSpace.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
        AttachSummaryXData(text, SummaryType, layoutName, summaryId);
    }

    private static void AttachSummaryXData(DBObject entity, string summaryType, string layoutName, string summaryId)
    {
        entity.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, summaryType),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, layoutName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, summaryId));
    }

    private static void EnsureXDataRegApp(Database database, Transaction transaction)
    {
        RegAppTable table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(XDataAppName)) return;
        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = XDataAppName };
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        if (transaction.GetObject(database.Clayer, OpenMode.ForRead) is LayerTableRecord layer) return layer.Name;
        return string.Empty;
    }

    private readonly record struct UcKey(string Diameter, string Surface);
    private readonly record struct UcSurface(string Name, int? ColorIndex, int? Red, int? Green, int? Blue);
}
