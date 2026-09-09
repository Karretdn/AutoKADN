using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Bloques;

public sealed class MaterialPruebaTool
{
    private const double TextHeight = 2.00;
    private const double VerticalOffset = 2.50;
    private const double HorizontalOffset = 1.00;
    private const string XDataAppName = "AUTOKADN";
    private const string MaterialTestType = "MATERIAL_PRUEBA";

    // Los keywords de AutoCAD con espacios (incluso NBSP) se truncan a la primera palabra
    // y siempre terminan seleccionando la primera opción (ver fix histórico en CotaTool.SelectLayer).
    // Por eso el keyword real es un código corto sin espacios; el texto completo va en el mensaje.
    private static readonly (string Code, string Name)[] TerrainCodes =
    {
        ("ZV", "ZONA VERDE"), ("AC", "ANDEN CONCRETO"), ("AT", "ANDEN TABLETA"), ("CC", "CALZADA CONCRETO"),
        ("AD", "ADOQUIN"), ("AS", "ASFALTO"), ("CU", "CUNETA"), ("DE", "DESTAPADO")
    };

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        string layoutName = LayoutManager.Current.CurrentLayout;

        var assignments = new List<TestMaterialAssignment>();
        while (true)
        {
            if (!TrySelectMaterialName(editor, out string materialName)) return;
            if (!TrySelectDiameter(editor, out string diameter)) return;
            if (!TrySelectTerrain(editor, out string surface)) return;
            if (!TryReadQuantity(editor, materialName, diameter, out int quantity)) return;

            var material = new TestMaterial(materialName, diameter, "UND");
            var uc = new UcKey(diameter, surface);
            assignments.Add(new TestMaterialAssignment(uc, material, quantity));
            editor.WriteMessage($"\nAgregado: {quantity} {FormatMaterialName(materialName, quantity)} DE {diameter}\" - {ToDisplaySurface(surface)}.\n");

            string? more = ReadYesNo(editor, "\n¿Añadir más? [Y/N]: ");
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

    private static bool TrySelectMaterialName(Editor editor, out string materialName)
    {
        materialName = string.Empty;
        var options = new PromptKeywordOptions("\nMaterial: ") { AllowNone = false };
        options.Keywords.Add("UNION");
        options.Keywords.Add("TAPON");
        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK) return false;
        materialName = result.StringResult;
        return true;
    }

    private static bool TrySelectDiameter(Editor editor, out string diameter)
    {
        diameter = string.Empty;
        // Ojo: el "/" dentro de un mismo texto entre corchetes se interpreta como separador de
        // opciones en el tooltip dinámico de AutoCAD (parte "1/2" en dos líneas). Se usa "1-2" / "3-4",
        // igual que las capas UC_1-2 / UC_3-4 y las etiquetas ya corregidas en CotaTool.
        var options = new PromptKeywordOptions("\nDiametro [D12=1-2\" / D34=3-4\"]: ") { AllowNone = false };
        options.Keywords.Add("D12");
        options.Keywords.Add("D34");
        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK) return false;
        diameter = result.StringResult.Equals("D34", StringComparison.OrdinalIgnoreCase) ? "3/4" : "1/2";
        return true;
    }

    private static bool TrySelectTerrain(Editor editor, out string surface)
    {
        surface = string.Empty;
        var options = new PromptKeywordOptions(
            "\nTerreno [ZV=ZonaVerde/AC=AndenConcreto/AT=AndenTableta/CC=CalzadaConcreto/AD=Adoquin/AS=Asfalto/CU=Cuneta/DE=Destapado]: ")
        { AllowNone = false };
        foreach ((string code, string _) in TerrainCodes) options.Keywords.Add(code);
        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK) return false;
        foreach ((string code, string name) in TerrainCodes)
        {
            if (string.Equals(result.StringResult, code, StringComparison.OrdinalIgnoreCase)) { surface = name; return true; }
        }
        return false;
    }

    private static bool TryReadQuantity(Editor editor, string materialName, string diameter, out int quantity)
    {
        quantity = 0;
        var options = new PromptIntegerOptions($"\nCantidad de {materialName} DE {diameter}\": ")
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
        Point3d insertionPoint = topLeftPoint
            + Vector3d.YAxis * VerticalOffset
            + Vector3d.XAxis * HorizontalOffset;

        var mtext = new MText
        {
            Location = insertionPoint,
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
            $"{x.Quantity} {FormatMaterialName(x.Material.Name, x.Quantity)} DE {x.Material.Diameter}\"").ToList();

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

    private static string ToDisplaySurface(string value) => value.ToLowerInvariant() switch
    {
        "zona verde" => "Zona Verde", "anden tableta" => "Anden Tableta", "calzada concreto" => "Calzada Concreto",
        "destapado" => "Destapado", "cuneta" => "Cuneta", "anden concreto" => "Anden Concreto",
        "asfalto" => "Asfalto", "adoquin" => "Adoquin", _ => value
    };

    private readonly record struct TestMaterial(string Name, string Diameter, string Unit);
    private readonly record struct UcKey(string Diameter, string Surface);
    private readonly record struct TestMaterialAssignment(UcKey Uc, TestMaterial Material, int Quantity);
}
