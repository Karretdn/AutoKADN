using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoKADN.Tools.Bloques;

public sealed class MaterialPruebaTool
{
    private const double TextHeight = 2.50;
    private const string XDataAppName = "AUTOKADN";
    private const string MaterialTestType = "MATERIAL_PRUEBA";
    private const string UcLayerHalf = "UC_1-2";
    private const string UcLayerThreeQuarter = "UC_3-4";

    private static readonly TestMaterial[] Materials =
    {
        new("UNION", "3/4\"", "UND"),
        new("UNION", "1/2\"", "UND"),
        new("TAPON", "1/2\"", "UND"),
        new("TAPON", "3/4\"", "UND"),
        new("REDUCCION", "3/4x1/2\"", "UND")
    };

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
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        string layoutName = LayoutManager.Current.CurrentLayout;

        if (!TryScanUcs(database, out Dictionary<UcKey, double> availableUcs))
        {
            editor.WriteMessage("\nNo se encontraron cotas UC válidas en el layout actual.\n");
            return;
        }

        var assignments = new List<TestMaterialAssignment>();
        while (true)
        {
            if (!TrySelectUc(editor, availableUcs, out UcKey selectedUc)) return;
            if (!TrySelectMaterial(editor, out TestMaterial material)) return;
            if (!TryReadQuantity(editor, material, out int quantity)) return;

            assignments.Add(new TestMaterialAssignment(selectedUc, material, quantity));
            editor.WriteMessage($"\nAgregado: {quantity} {FormatMaterialName(material.Name, quantity)} DE {material.Diameter} - {ToDisplaySurface(selectedUc.Surface)}.\n");

            string? more = ReadYesNo(editor, "¿Añadir más? [Y/N]: ");
            if (more is null) return;
            if (more.Equals("N", StringComparison.OrdinalIgnoreCase)) break;
        }

        if (assignments.Count == 0) return;

        PromptPointResult pointResult = editor.GetPoint(
            new PromptPointOptions("\nSeleccione el vértice SUPERIOR IZQUIERDO del cuadro de MATERIAL DE PRUEBA: "));
        if (pointResult.Status != PromptStatus.OK) return;

        CreateMaterialTestText(database, pointResult.Value, assignments, layoutName);
        editor.Regen();
        editor.WriteMessage($"\nMaterial de prueba generado en el layout '{layoutName}'.\n");
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

    private static bool TrySelectUc(Editor editor, IReadOnlyDictionary<UcKey, double> quantities, out UcKey selectedUc)
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

    private static bool TrySelectMaterial(Editor editor, out TestMaterial material)
    {
        material = default;
        editor.WriteMessage("\nMaterial de prueba disponible:\n");
        for (int i = 0; i < Materials.Length; i++)
        {
            TestMaterial item = Materials[i];
            editor.WriteMessage($"  {i + 1}. {item.Name} DE {item.Diameter}\n");
        }

        var options = new PromptIntegerOptions("\nEscriba el numero del material: ")
        {
            AllowNone = false, AllowNegative = false, AllowZero = false,
            LowerLimit = 1, UpperLimit = Materials.Length
        };
        PromptIntegerResult result = editor.GetInteger(options);
        if (result.Status != PromptStatus.OK) return false;
        material = Materials[result.Value - 1];
        return true;
    }

    private static bool TryReadQuantity(Editor editor, TestMaterial material, out int quantity)
    {
        quantity = 0;
        var options = new PromptIntegerOptions($"\nCantidad de {material.Name} DE {material.Diameter}: ")
        {
            AllowNone = false, AllowNegative = false, AllowZero = false,
            LowerLimit = 1
        };
        PromptIntegerResult result = editor.GetInteger(options);
        if (result.Status != PromptStatus.OK) return false;
        quantity = result.Value;
        return true;
    }

    private static string? ReadYesNo(Editor editor, string prompt)
    {
        var options = new PromptKeywordOptions(prompt) { AllowNone = false };
        options.Keywords.Add("Y");
        options.Keywords.Add("N");
        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static void CreateMaterialTestText(Database database, Point3d topLeftPoint,
        IReadOnlyList<TestMaterialAssignment> assignments, string layoutName)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        string layerName = GetCurrentLayerName(database, transaction);
        ObjectId textStyleId = database.Textstyle;
        string materialText = BuildMaterialText(assignments);

        var mtext = new MText
        {
            Location = topLeftPoint,
            Contents = materialText,
            TextHeight = TextHeight,
            Attachment = AttachmentPoint.TopLeft,
            Rotation = 0.0,
            ColorIndex = 256,
            Layer = layerName,
            TextStyleId = textStyleId
        };

        currentSpace.AppendEntity(mtext);
        transaction.AddNewlyCreatedDBObject(mtext, true);
        AttachMaterialTestXData(database, transaction, mtext, assignments, layoutName);
        transaction.Commit();
    }

    private static string BuildMaterialText(IReadOnlyList<TestMaterialAssignment> assignments)
    {
        var parts = assignments.Select(x =>
            $"{x.Quantity} {FormatMaterialName(x.Material.Name, x.Quantity)} DE {x.Material.Diameter}").ToList();

        if (parts.Count == 1)
            return $"MATERIAL DE PRUEBA: {parts[0]}.";
        if (parts.Count == 2)
            return $"MATERIAL DE PRUEBA: {parts[0]} Y {parts[1]}.";

        return $"MATERIAL DE PRUEBA: {string.Join(", ", parts.Take(parts.Count - 1))} Y {parts.Last()}.";
    }

    private static string FormatMaterialName(string name, int quantity)
    {
        if (quantity == 1) return name;
        return name switch
        {
            "UNION" => "UNIONES",
            "TAPON" => "TAPONES",
            "REDUCCION" => "REDUCCIONES",
            _ => name
        };
    }

    private static void AttachMaterialTestXData(Database database, Transaction transaction, MText mtext,
        IReadOnlyList<TestMaterialAssignment> assignments, string layoutName)
    {
        EnsureXDataRegApp(database, transaction);
        var values = new List<TypedValue>
        {
            new((int)DxfCode.ExtendedDataRegAppName, XDataAppName),
            new((int)DxfCode.ExtendedDataAsciiString, MaterialTestType),
            new((int)DxfCode.ExtendedDataAsciiString, layoutName),
            new((int)DxfCode.ExtendedDataAsciiString, Guid.NewGuid().ToString("D"))
        };

        foreach (TestMaterialAssignment assignment in assignments)
        {
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, assignment.Material.Name));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, assignment.Material.Diameter));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, assignment.Material.Unit));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataReal, (double)assignment.Quantity));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, assignment.Uc.Diameter));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, assignment.Uc.Surface));
        }
        mtext.XData = new ResultBuffer(values.ToArray());
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

    private static string? GetUcDiameter(string layer)
    {
        if (string.Equals(layer, UcLayerHalf, StringComparison.OrdinalIgnoreCase)) return "1/2";
        if (string.Equals(layer, UcLayerThreeQuarter, StringComparison.OrdinalIgnoreCase)) return "3/4";
        return null;
    }

    private static string? GetSurface(Transaction transaction, Dimension dimension)
    {
        Autodesk.AutoCAD.Colors.Color color = dimension.Color;
        if (color.ColorIndex == 256 || color.IsByLayer)
        {
            ObjectId layerId = dimension.LayerId;
            if (!layerId.IsNull && transaction.GetObject(layerId, OpenMode.ForRead) is LayerTableRecord layer)
                color = layer.Color;
        }

        foreach (UcSurface surface in Surfaces)
        {
            if (surface.ColorIndex.HasValue && color.ColorIndex == surface.ColorIndex.Value) return surface.Name;
            if (surface.Red.HasValue && color.Red == surface.Red.Value && color.Green == surface.Green!.Value && color.Blue == surface.Blue!.Value)
                return surface.Name;
        }
        return null;
    }

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

    private readonly record struct TestMaterial(string Name, string Diameter, string Unit);
    private readonly record struct UcKey(string Diameter, string Surface);
    private readonly record struct UcSurface(string Name, int? ColorIndex, int? Red, int? Green, int? Blue);
    private sealed record TestMaterialAssignment(UcKey Uc, TestMaterial Material, int Quantity);
}
