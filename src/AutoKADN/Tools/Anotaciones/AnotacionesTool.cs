using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoKADN.Tools.Anotaciones;

public sealed class AnotacionesTool
{
    private const double TextHeight = 2.40;
    private const double TextOffset = 1.00;
    private const string MaterialsLayer = "Mat";
    private const string XDataAppName = "AUTOKADN";
    private const string ActivityType = "ACTIVIDAD";
    private const string UcLayerHalf = "UC_1-2";
    private const string UcLayerThreeQuarter = "UC_3-4";

    private static readonly UcSurface[] Surfaces =
    {
        new("ZONA VERDE", 3, null, null, null), new("ANDEN TABLETA", 1, null, null, null),
        new("CALZADA CONCRETO", 8, null, null, null), new("DESTAPADO", 2, null, null, null),
        new("CUNETA", null, 100, 33, 101), new("ANDEN CONCRETO", 5, null, null, null),
        new("ASFALTO", 30, null, null, null), new("ADOQUIN", 4, null, null, null)
    };

    private static readonly string[] SurfaceOrder =
    {
        "ZONA VERDE", "ANDEN CONCRETO", "CALZADA CONCRETO", "ANDEN TABLETA",
        "ADOQUIN", "ASFALTO", "CUNETA", "DESTAPADO"
    };

    public void Run()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        object originalShortcutMenu = Application.GetSystemVariable("SHORTCUTMENU");
        try
        {
            Application.SetSystemVariable("SHORTCUTMENU", 0);
            editor.WriteMessage("\n[ANOTACIONES] ESC o clic derecho para salir.\n");
            while (true)
            {
                if (!GetReferenceLine(editor, out Point3d startPoint, out Point3d endPoint)) return;
                ObjectId lineId = CreateReferenceLine(document.Database, startPoint, endPoint);
                if (lineId == ObjectId.Null) return;
                string? type = SelectAnnotationType(editor);
                if (type is null) { EraseEntity(document.Database, lineId); return; }
                SpiralData? spiralData = null;
                ActivityData? activityData = null;
                string? text = BuildAnnotation(editor, document.Database, type, out spiralData, out activityData);
                if (text is null) { EraseEntity(document.Database, lineId); return; }
                if (!string.IsNullOrWhiteSpace(text)) CreateText(document.Database, startPoint, endPoint, text, type, spiralData, activityData);
                editor.Regen();
            }
        }
        finally { Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu); }
    }

    private static bool GetReferenceLine(Editor editor, out Point3d startPoint, out Point3d endPoint)
    {
        startPoint = Point3d.Origin; endPoint = Point3d.Origin;
        var firstOptions = new PromptPointOptions("\nPrimer punto de la línea (ESC o clic derecho para salir): ") { AllowNone = true };
        PromptPointResult first = editor.GetPoint(firstOptions);
        if (first.Status != PromptStatus.OK) return false;
        var secondOptions = new PromptPointOptions("\nSegundo punto de la línea (ESC o clic derecho para salir): ") { BasePoint = first.Value, UseBasePoint = true, AllowNone = true };
        PromptPointResult second = editor.GetPoint(secondOptions);
        if (second.Status != PromptStatus.OK) return false;
        if (first.Value.DistanceTo(second.Value) <= Tolerance.Global.EqualPoint)
        { editor.WriteMessage("\nLa línea debe tener una longitud mayor que cero.\n"); return true; }
        startPoint = first.Value; endPoint = second.Value; return true;
    }

    private static string? SelectAnnotationType(Editor editor)
    {
        var options = new PromptKeywordOptions("\nSeleccione tipo de anotación: ") { AllowNone = true };
        options.Keywords.Add("AKESPIRAL", "ESPIRAL", "ESPIRAL", true, true);
        options.Keywords.Add("AKCAMISA", "CAMISA", "CAMISA", true, true);
        options.Keywords.Add("AKPANTALLA", "PANTALLA", "PANTALLA", true, true);
        options.Keywords.Add("AKCRUCETOPO", "CRUCETOPO", "CRUCE CON TOPO", true, true);
        options.Keywords.Add("AKEMPEDRADO", "EMPEDRADO", "EMPEDRADO", true, true);
        options.Keywords.Add("AKVIGACONCRETO", "VIGACONCRETO", "VIGA EN CONCRETO", true, true);
        options.Keywords.Add("AKLIBRE", "LIBRE", "LIBRE", true, true);
        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK) return null;
        return result.StringResult switch
        {
            "AKESPIRAL" => "ESPIRAL", "AKCAMISA" => "CAMISA", "AKPANTALLA" => "PANTALLA",
            "AKCRUCETOPO" => "CRUCE_TOPO", "AKEMPEDRADO" => "EMPEDRADO", "AKVIGACONCRETO" => "VIGA_CONCRETO",
            "AKLIBRE" => "LIBRE", _ => null
        };
    }

    private static string? BuildAnnotation(Editor editor, Database database, string type, out SpiralData? spiralData, out ActivityData? activityData)
    {
        spiralData = null;
        activityData = null;
        if (type.Equals("LIBRE", StringComparison.OrdinalIgnoreCase)) return ReadFreeText(editor);
        if (type.Equals("ESPIRAL", StringComparison.OrdinalIgnoreCase)) return ReadSpiral(editor, out spiralData);

        string label = type switch
        {
            "CAMISA" => "CAMISA", "PANTALLA" => "PANTALLA", "CRUCE_TOPO" => "CRUCE CON TOPO",
            "EMPEDRADO" => "EMPEDRADO", "VIGA_CONCRETO" => "VIGA EN CONCRETO", _ => type
        };

        string? baseLengthText = ReadActivityLength(editor, label);
        if (baseLengthText is null) return null;
        double baseLength = ParseNumber(baseLengthText);

        string layoutName = LayoutManager.Current.CurrentLayout;
        if (!TryScanUcs(database, out Dictionary<UcKey, double> availableUcs))
        {
            editor.WriteMessage("\nNo se encontraron cotas UC válidas en el layout actual.\n");
            return null;
        }

        var assignments = new List<UcAssignment>();
        while (true)
        {
            if (!TrySelectActivityUc(editor, availableUcs, out UcKey selectedUc)) return null;
            double? amount = ReadActivityAmount(editor, selectedUc);
            if (!amount.HasValue) return null;

            assignments.Add(new UcAssignment(selectedUc, amount.Value));
            editor.WriteMessage($"\nAsignado {FormatQuantity(amount.Value)} ML a {selectedUc.Diameter} Pulg. - {ToDisplaySurface(selectedUc.Surface)}.\n");

            string? more = ReadYesNo(editor, "¿Ingresar más cantidad? [Y/N]: ");
            if (more is null) return null;
            if (more.Equals("N", StringComparison.OrdinalIgnoreCase)) break;
        }

        double totalLength = assignments.Sum(x => x.Quantity);
        if (totalLength <= 0.0) return null;

        activityData = new ActivityData(type, baseLength, layoutName, assignments);
        return $"{label}\\PLONG.: {FormatQuantity(totalLength)}ML";
    }

    private static string? ReadActivityLength(Editor editor, string label)
    {
        var options = new PromptStringOptions($"\n{label} - ingrese el valor de LONG.: (ENTER para aceptar, ESC o clic derecho para cancelar): ") { AllowSpaces = false };
        while (true)
        {
            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK) return null;
            string value = result.StringResult.Trim();
            if (value.Length == 0) return null;
            if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double length) && length >= 0.0)
                return value;
            editor.WriteMessage("\nIngrese una cantidad numérica mayor o igual a cero.\n");
        }
    }

    private static double? ReadActivityAmount(Editor editor, UcKey selectedUc)
    {
        var options = new PromptDoubleOptions($"\nCantidad para {selectedUc.Diameter} Pulg. - {ToDisplaySurface(selectedUc.Surface)} (ML): ")
        {
            AllowZero = false,
            AllowNegative = false,
            AllowNone = false
        };
        PromptDoubleResult result = editor.GetDouble(options);
        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static bool TryScanUcs(Database database, out Dictionary<UcKey, double> quantities)
    {
        quantities = new Dictionary<UcKey, double>();
        string layoutName = LayoutManager.Current.CurrentLayout;
        using Transaction transaction = database.TransactionManager.StartTransaction();
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
        return quantities.Count > 0;
    }

    private static bool TrySelectActivityUc(Editor editor, IReadOnlyDictionary<UcKey, double> quantities, out UcKey selectedUc)
    {
        selectedUc = default;
        List<UcKey> available = quantities.Keys
            .OrderBy(x => GetSurfaceOrder(x.Surface))
            .ThenBy(x => x.Diameter, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (available.Count == 0) return false;

        editor.WriteMessage("\nSeleccionar UC:\n");
        for (int i = 0; i < available.Count; i++)
        {
            UcKey uc = available[i];
            editor.WriteMessage($"  {i + 1}. {uc.Diameter} Pulg. - {ToDisplaySurface(uc.Surface)} - {FormatQuantity(quantities[uc])} ML\n");
        }

        var options = new PromptIntegerOptions("\nEscriba el numero de la UC: ")
        {
            AllowNone = false, AllowNegative = false, AllowZero = false,
            LowerLimit = 1, UpperLimit = available.Count
        };
        PromptIntegerResult result = editor.GetInteger(options);
        if (result.Status != PromptStatus.OK) return false;
        selectedUc = available[result.Value - 1];
        return true;
    }

    private static string? ReadFreeText(Editor editor)
    {
        var lines = new List<string>();
        editor.WriteMessage("\nLIBRE: escriba una línea y presione ENTER para pasar a la siguiente. Termine con clic derecho o ESC.\n");
        while (true)
        {
            var options = new PromptStringOptions("Texto: ") { AllowSpaces = true };
            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK) return lines.Count == 0 ? null : string.Join("\\P", lines);
            string line = result.StringResult.TrimEnd();
            if (line.Length == 0) lines.Add(string.Empty); else lines.Add(line);
        }
    }

    private static string? ReadSpiral(Editor editor, out SpiralData? spiralData)
    {
        spiralData = null;
        string? pipe = ReadNumber(editor, "METROS DE TUBERIA DE 3/4\"?"); if (pipe is null) return null;
        string? unions = ReadNumber(editor, "CANTIDAD DE UNIONES DE 3/4\"?"); if (unions is null) return null;
        string? tees = ReadNumber(editor, "CANTIDAD DE TEE DE 3/4\"?"); if (tees is null) return null;
        string? valves = ReadNumber(editor, "VALVULA DE 3/4\"?"); if (valves is null) return null;
        string? saddles = ReadNumber(editor, "SILLETA?"); if (saddles is null) return null;
        string saddleDiameter = string.Empty;
        if (!IsZero(saddles))
        {
            var diameterOptions = new PromptStringOptions("DIAMETRO DE SILLETA? (puede escribir signos y números): ") { AllowSpaces = false };
            PromptResult diameterResult = editor.GetString(diameterOptions);
            if (diameterResult.Status != PromptStatus.OK) return null;
            saddleDiameter = diameterResult.StringResult.Trim(); if (saddleDiameter.Length == 0) return null;
        }
        string? peExt = ReadYesNo(editor, "PE.EXT.? [Y/N]: "); if (peExt is null) return null;
        var lines = new List<string>();
        if (!IsZero(pipe)) lines.Add($"{pipe}ML TUBERIA 3/4\"");
        if (!IsZero(unions)) lines.Add($"{unions} UNIONES DE 3/4\"");
        if (!IsZero(tees)) lines.Add($"{tees} TEE DE 3/4\"");
        if (!IsZero(valves)) lines.Add($"{valves} VALVULA DE 3/4\"");
        if (!IsZero(saddles)) lines.Add($"{saddles} SILLETA DE {saddleDiameter}");
        if (peExt.Equals("Y", StringComparison.OrdinalIgnoreCase)) lines.Add("PE.EXT.");
        if (lines.Count == 0) { editor.WriteMessage("\nESPIRAL: no se generó ninguna línea porque todas las cantidades fueron cero y PE.EXT. fue N.\n"); return string.Empty; }
        spiralData = new SpiralData(ParseNumber(pipe), ParseNumber(unions), ParseNumber(tees));
        return string.Join("\\P", lines);
    }

    private static double ParseNumber(string value) => double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0.0;

    private static string? ReadNumber(Editor editor, string prompt)
    {
        var options = new PromptStringOptions($"\n{prompt} (número): ") { AllowSpaces = false };
        while (true)
        {
            PromptResult result = editor.GetString(options); if (result.Status != PromptStatus.OK) return null;
            string normalized = result.StringResult.Trim().Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number >= 0.0) return normalized;
            editor.WriteMessage("\nIngrese una cantidad numérica mayor o igual a cero.\n");
        }
    }

    private static string? ReadYesNo(Editor editor, string prompt)
    {
        var options = new PromptKeywordOptions(prompt) { AllowNone = false }; options.Keywords.Add("Y"); options.Keywords.Add("N");
        PromptResult result = editor.GetKeywords(options); return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static bool IsZero(string value) => double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && Math.Abs(number) <= 1e-12;

    private static ObjectId CreateReferenceLine(Database database, Point3d startPoint, Point3d endPoint)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        var line = new Line(startPoint, endPoint) { ColorIndex = 256 }; currentSpace.AppendEntity(line); transaction.AddNewlyCreatedDBObject(line, true); transaction.Commit(); return line.ObjectId;
    }

    private static void CreateText(Database database, Point3d startPoint, Point3d endPoint, string text, string type, SpiralData? spiralData, ActivityData? activityData)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        Vector3d direction = (endPoint - startPoint).GetNormal();
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        if (normal.Y < 0.0) normal = -normal;
        Point3d textPoint = endPoint + normal * TextOffset;
        AttachmentPoint attachment = direction.X < -Tolerance.Global.EqualPoint ? AttachmentPoint.TopRight : AttachmentPoint.TopLeft;

        var mtext = new MText
        {
            Location = textPoint, Contents = text, TextHeight = TextHeight, Attachment = attachment,
            Rotation = 0.0, ColorIndex = 256,
            Layer = spiralData is not null ? GetOrCreateLayer(database, transaction, MaterialsLayer) : GetCurrentLayerName(database, transaction)
        };
        currentSpace.AppendEntity(mtext); transaction.AddNewlyCreatedDBObject(mtext, true);
        if (spiralData is not null) SetSpiralXData(database, transaction, mtext, spiralData);
        if (activityData is not null) SetActivityXData(database, transaction, mtext, activityData);
        transaction.Commit();
    }

    private static void SetSpiralXData(Database database, Transaction transaction, MText mtext, SpiralData data)
    {
        EnsureRegApp(database, transaction);
        mtext.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "ESPIRAL"),
            new TypedValue((int)DxfCode.ExtendedDataReal, data.Pipe),
            new TypedValue((int)DxfCode.ExtendedDataReal, data.Unions),
            new TypedValue((int)DxfCode.ExtendedDataReal, data.Tees));
    }

    private static void SetActivityXData(Database database, Transaction transaction, MText mtext, ActivityData data)
    {
        EnsureRegApp(database, transaction);
        var values = new List<TypedValue>
        {
            new((int)DxfCode.ExtendedDataRegAppName, XDataAppName),
            new((int)DxfCode.ExtendedDataAsciiString, ActivityType),
            new((int)DxfCode.ExtendedDataAsciiString, data.Type),
            new((int)DxfCode.ExtendedDataReal, data.BaseLength),
            new((int)DxfCode.ExtendedDataAsciiString, data.Layout)
        };

        foreach (UcAssignment assignment in data.Assignments)
        {
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, assignment.Uc.Diameter));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, assignment.Uc.Surface));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataReal, assignment.Quantity));
        }
        mtext.XData = new ResultBuffer(values.ToArray());
    }

    private static void EnsureRegApp(Database database, Transaction transaction)
    {
        RegAppTable table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(XDataAppName)) return;
        table.UpgradeOpen(); var record = new RegAppTableRecord { Name = XDataAppName }; table.Add(record); transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static string GetOrCreateLayer(Database database, Transaction transaction, string layerName)
    {
        LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (table.Has(layerName)) return layerName;
        table.UpgradeOpen(); var layer = new LayerTableRecord { Name = layerName }; table.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true); return layerName;
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        if (transaction.GetObject(database.Clayer, OpenMode.ForRead) is LayerTableRecord layer) return layer.Name;
        return string.Empty;
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

    private static int GetSurfaceOrder(string surface)
    {
        for (int i = 0; i < SurfaceOrder.Length; i++)
            if (string.Equals(surface, SurfaceOrder[i], StringComparison.OrdinalIgnoreCase)) return i;
        return SurfaceOrder.Length;
    }

    private static string ToDisplaySurface(string value) => value.ToLowerInvariant() switch
    {
        "zona verde" => "Zona Verde", "anden tableta" => "Anden Tableta", "calzada concreto" => "Calzada Concreto",
        "destapado" => "Destapado", "cuneta" => "Cuneta", "anden concreto" => "Anden Concreto",
        "asfalto" => "Asfalto", "adoquin" => "Adoquin", _ => value
    };

    private static string FormatQuantity(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);

    private static void EraseEntity(Database database, ObjectId objectId)
    {
        if (objectId == ObjectId.Null) return;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        if (transaction.GetObject(objectId, OpenMode.ForWrite, false) is Entity entity) entity.Erase();
        transaction.Commit();
    }

    private sealed record SpiralData(double Pipe, double Unions, double Tees);
    private sealed record ActivityData(string Type, double BaseLength, string Layout, List<UcAssignment> Assignments);
    private sealed record UcAssignment(UcKey Uc, double Quantity);
    private readonly record struct UcKey(string Diameter, string Surface);
    private readonly record struct UcSurface(string Name, int? ColorIndex, int? Red, int? Green, int? Blue);
}
