using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using static AutoKADN.Core.Naming;

namespace AutoKADN.Tools.Bloques;

public sealed class MaterialPruebaTool
{
    private const double TextHeight = 2.60;
    private const double VerticalOffset = 4.00;
    private const double HorizontalOffset = 1.00;
    private const string XDataAppName = "AUTOKADN";
    private const string MaterialTestType = "MATERIAL_PRUEBA";

    private static readonly string[] SimpleMaterials = { "UNION", "TAPON" };
    // Diametros propios de REDUCCION (distintos del diametro de la UC "hogar"). Para empezar
    // solo existe 3/4x1/2"; se puede ampliar esta lista sin tocar el resto del flujo.
    private static readonly string[] ReduccionDiameters = { "3/4x1/2" };
    private static readonly string[] TerrainNames =
    {
        "ZONA VERDE", "ANDEN CONCRETO", "ANDEN TABLETA", "CALZADA CONCRETO",
        "ADOQUIN", "ASFALTO", "CUNETA", "DESTAPADO"
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
            if (!TrySelectMaterial(out string materialName, out string materialDiameter)) return;
            if (!TryReadQuantity(editor, materialName, materialDiameter, out int quantity)) return;
            if (!TrySelectDiameterTerrain(out string hogarDiameter, out string hogarSurface)) return;

            string effectiveDiameter = string.IsNullOrWhiteSpace(materialDiameter) ? hogarDiameter : materialDiameter;
            var material = new TestMaterial(materialName, effectiveDiameter, "UND");
            var uc = new UcKey(hogarDiameter, hogarSurface);
            assignments.Add(new TestMaterialAssignment(uc, material, quantity));
            editor.WriteMessage($"\nAgregado: {quantity} {FormatMaterialName(materialName, quantity)} DE {effectiveDiameter}\" - {ToDisplaySurface(hogarSurface)} (UC {hogarDiameter}\").\n");

            if (!ShowAddAnotherMenu()) break;
        }

        if (assignments.Count == 0) return;

        PromptPointResult pointResult = editor.GetPoint(
            new PromptPointOptions("\nSeleccione el vértice SUPERIOR IZQUIERDO del cuadro de MATERIAL DE PRUEBA: "));
        if (pointResult.Status != PromptStatus.OK) return;

        CreateMaterialTestText(database, pointResult.Value, assignments, layoutName);
        editor.Regen();
        editor.WriteMessage($"\nMaterial de prueba generado en el layout '{layoutName}'.\n");
    }

    // Menú MATERIAL: UNION / TAPON (sin submenú, usan el diámetro de la UC "hogar") y
    // REDUCCION (submenú propio con sus diámetros, ej. 3/4x1/2").
    private static bool TrySelectMaterial(out string materialName, out string materialDiameter)
    {
        string? selectedName = null;
        string? selectedDiameter = null;

        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var header = new System.Windows.Controls.MenuItem { Header = "MATERIAL", IsEnabled = false, FontWeight = System.Windows.FontWeights.Bold };
        menu.Items.Add(header);
        menu.Items.Add(new System.Windows.Controls.Separator());

        foreach (string name in SimpleMaterials)
        {
            var item = new System.Windows.Controls.MenuItem { Header = name };
            item.Click += (_, _) => { selectedName = name; selectedDiameter = string.Empty; menu.IsOpen = false; };
            menu.Items.Add(item);
        }

        var reduccionItem = new System.Windows.Controls.MenuItem { Header = "REDUCCION" };
        var reduccionHeader = new System.Windows.Controls.MenuItem { Header = "DIAMETRO REDUCCION", IsEnabled = false, FontWeight = System.Windows.FontWeights.Bold };
        reduccionItem.Items.Add(reduccionHeader);
        reduccionItem.Items.Add(new System.Windows.Controls.Separator());
        foreach (string reduccionDiameter in ReduccionDiameters)
        {
            var diameterItem = new System.Windows.Controls.MenuItem { Header = reduccionDiameter + "\"" };
            diameterItem.Click += (_, _) => { selectedName = "REDUCCION"; selectedDiameter = reduccionDiameter; menu.IsOpen = false; };
            reduccionItem.Items.Add(diameterItem);
        }
        menu.Items.Add(reduccionItem);

        var frame = new System.Windows.Threading.DispatcherFrame();
        menu.Closed += (_, _) => frame.Continue = false;
        menu.IsOpen = true;
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        materialName = selectedName ?? string.Empty;
        materialDiameter = selectedDiameter ?? string.Empty;
        return selectedName != null;
    }

    // Menú en cascada DIAMETRO -> UC (mismo patrón que CotaTool/AnotacionesTool) para elegir
    // la UC "hogar" (diámetro + terreno) a la que pertenece el material de prueba.
    private static bool TrySelectDiameterTerrain(out string diameter, out string surface)
    {
        string? selectedDiameter = null;
        string? selectedSurface = null;

        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var diameterHeader = new System.Windows.Controls.MenuItem { Header = "DIAMETRO", IsEnabled = false, FontWeight = System.Windows.FontWeights.Bold };
        menu.Items.Add(diameterHeader);
        menu.Items.Add(new System.Windows.Controls.Separator());

        foreach ((string label, string diameterValue) in new[] { ("3/4\"", "3/4"), ("1/2\"", "1/2") })
        {
            var diameterItem = new System.Windows.Controls.MenuItem { Header = label };
            var ucHeader = new System.Windows.Controls.MenuItem { Header = "UC", IsEnabled = false, FontWeight = System.Windows.FontWeights.Bold };
            diameterItem.Items.Add(ucHeader);
            diameterItem.Items.Add(new System.Windows.Controls.Separator());

            foreach (string terrainName in TerrainNames)
            {
                var terrainItem = new System.Windows.Controls.MenuItem { Header = ToDisplaySurface(terrainName) };
                terrainItem.Click += (_, _) =>
                {
                    selectedDiameter = diameterValue;
                    selectedSurface = terrainName;
                    menu.IsOpen = false;
                };
                diameterItem.Items.Add(terrainItem);
            }

            menu.Items.Add(diameterItem);
        }

        var frame = new System.Windows.Threading.DispatcherFrame();
        menu.Closed += (_, _) => frame.Continue = false;
        menu.IsOpen = true;
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        diameter = selectedDiameter ?? string.Empty;
        surface = selectedSurface ?? string.Empty;
        return selectedDiameter != null && selectedSurface != null;
    }

    // Menú Sí/No con el mismo estilo, para decidir si se agrega otro material de prueba.
    private static bool ShowAddAnotherMenu()
    {
        bool addAnother = false;

        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var header = new System.Windows.Controls.MenuItem { Header = "¿AÑADIR OTRO MATERIAL?", IsEnabled = false, FontWeight = System.Windows.FontWeights.Bold };
        menu.Items.Add(header);
        menu.Items.Add(new System.Windows.Controls.Separator());

        var yesItem = new System.Windows.Controls.MenuItem { Header = "Sí, otro material" };
        yesItem.Click += (_, _) => { addAnother = true; menu.IsOpen = false; };
        menu.Items.Add(yesItem);

        var noItem = new System.Windows.Controls.MenuItem { Header = "No, generar" };
        noItem.Click += (_, _) => { addAnother = false; menu.IsOpen = false; };
        menu.Items.Add(noItem);

        var frame = new System.Windows.Threading.DispatcherFrame();
        menu.Closed += (_, _) => frame.Continue = false;
        menu.IsOpen = true;
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        return addAnother;
    }

    private static bool TryReadQuantity(Editor editor, string materialName, string materialDiameter, out int quantity)
    {
        quantity = 0;
        string label = string.IsNullOrWhiteSpace(materialDiameter) ? materialName : $"{materialName} DE {materialDiameter}\"";
        var options = new PromptIntegerOptions($"\nCantidad de {label}: ")
        {
            AllowNone = false, AllowNegative = false, AllowZero = false,
            LowerLimit = 1
        };
        PromptIntegerResult result = editor.GetInteger(options);
        if (result.Status != PromptStatus.OK) return false;
        quantity = result.Value;
        return true;
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


    private readonly record struct TestMaterial(string Name, string Diameter, string Unit);
    private readonly record struct UcKey(string Diameter, string Surface);
    private readonly record struct TestMaterialAssignment(UcKey Uc, TestMaterial Material, int Quantity);
}
