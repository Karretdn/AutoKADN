using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using static AutoKADN.Core.Naming;
using System.Globalization;

namespace AutoKADN.Tools.Anotaciones;

public sealed class AnotacionesTool
{
    private const double TextHeight = 2.40;
    private const double TextOffset = 1.00;
    private const string MaterialsLayer = "Mat";
    private const string XDataAppName = "AUTOKADN";
    private const string ActivityType = "ACTIVIDAD";

    private static readonly UcSurface[] Surfaces =
    {
        new("ZONA VERDE", 3, null, null, null), new("ANDEN TABLETA", 1, null, null, null),
        new("CALZADA CONCRETO", 8, null, null, null), new("DESTAPADO", 2, null, null, null),
        new("CUNETA", null, 100, 33, 101), new("ANDEN CONCRETO", 5, null, null, null),
        new("ASFALTO", 30, null, null, null), new("ADOQUIN", 4, null, null, null)
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
                List<ActivityComponent>? activityData = null;
                string? text = BuildAnnotation(editor, document.Database, type, startPoint, endPoint, out spiralData, out activityData);
                if (text is null) { EraseEntity(document.Database, lineId); return; }
                if (!string.IsNullOrWhiteSpace(text))
                    CreateText(document.Database, startPoint, endPoint, text, spiralData, activityData);
                editor.Regen();
            }
        }
        finally { Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu); }
    }

    private static bool GetReferenceLine(Editor editor, out Point3d startPoint, out Point3d endPoint)
    {
        startPoint = Point3d.Origin;
        endPoint = Point3d.Origin;
        var firstOptions = new PromptPointOptions("\nPrimer punto de la línea (ESC o clic derecho para salir): ") { AllowNone = true };
        PromptPointResult first = editor.GetPoint(firstOptions);
        if (first.Status != PromptStatus.OK) return false;
        var secondOptions = new PromptPointOptions("\nSegundo punto de la línea (ESC o clic derecho para salir): ")
        { BasePoint = first.Value, UseBasePoint = true, AllowNone = true };
        PromptPointResult second = editor.GetPoint(secondOptions);
        if (second.Status != PromptStatus.OK) return false;
        if (first.Value.DistanceTo(second.Value) <= Tolerance.Global.EqualPoint)
        {
            editor.WriteMessage("\nLa línea debe tener una longitud mayor que cero.\n");
            return false;
        }
        startPoint = first.Value;
        endPoint = second.Value;
        return true;
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
            "AKCRUCETOPO" => "CRUCE_TOPO", "AKEMPEDRADO" => "EMPEDRADO",
            "AKVIGACONCRETO" => "VIGA_CONCRETO", "AKLIBRE" => "LIBRE", _ => null
        };
    }

    private static string? BuildAnnotation(Editor editor, Database database, string type,
        Point3d startPoint, Point3d endPoint, out SpiralData? spiralData, out List<ActivityComponent>? activityData)
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

        activityData = ReadActivityComponents(editor, label, startPoint.DistanceTo(endPoint));
        if (activityData is null || activityData.Count == 0) return null;

        double totalLength = activityData.Sum(item => item.Quantity);
        editor.WriteMessage($"\nActividad registrada: {label} | {activityData.Count} combinación(es) | LONG.: {FormatQuantity(totalLength)} ML.\n");
        return $"{label}\\PLONG.: {FormatQuantity(totalLength)}ML";
    }

    private static List<ActivityComponent>? ReadActivityComponents(Editor editor, string label, double geometricLength)
    {
        var components = new List<ActivityComponent>();
        bool addMore = true;
        while (addMore)
        {
            if (!TrySelectActivityDiameterTerrain(out string diameter, out string surface, out Color selectedColor)) return null;

            double? quantity = ReadActivityQuantity(editor, geometricLength, components.Count == 0);
            if (!quantity.HasValue) return null;

            components.Add(new ActivityComponent(label, diameter, surface, quantity.Value, selectedColor));
            editor.WriteMessage($"\nComponente registrado: {label} | {diameter}\" | {ToDisplaySurface(surface)} | {FormatQuantity(quantity.Value)} ML.\n");

            addMore = ShowAddAnotherMenu();
        }
        return components;
    }

    // Menú Sí/No con el mismo estilo que el de DIAMETRO/UC, para decidir si se agrega otra
    // combinación de diámetro/terreno/cantidad a la misma actividad.
    private static bool ShowAddAnotherMenu()
    {
        bool addAnother = false;

        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var header = new System.Windows.Controls.MenuItem { Header = "¿AÑADIR OTRO TERRENO?", IsEnabled = false, FontWeight = System.Windows.FontWeights.Bold };
        menu.Items.Add(header);
        menu.Items.Add(new System.Windows.Controls.Separator());

        var yesItem = new System.Windows.Controls.MenuItem { Header = "Sí, otra combinación" };
        yesItem.Click += (_, _) => { addAnother = true; menu.IsOpen = false; };
        menu.Items.Add(yesItem);

        var noItem = new System.Windows.Controls.MenuItem { Header = "No, terminar actividad" };
        noItem.Click += (_, _) => { addAnother = false; menu.IsOpen = false; };
        menu.Items.Add(noItem);

        var frame = new System.Windows.Threading.DispatcherFrame();
        menu.Closed += (_, _) => frame.Continue = false;
        menu.IsOpen = true;
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        return addAnother;
    }

    // Menú en cascada (DIAMETRO -> UC), igual al usado en CotaTool para la cota UC, para elegir
    // diámetro y terreno de cada combinación de actividad en una sola interacción.
    private static bool TrySelectActivityDiameterTerrain(out string diameter, out string surface, out Color color)
    {
        string? selectedDiameter = null;
        string? selectedSurface = null;
        Color? selectedColor = null;

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

            foreach (UcSurface item in Surfaces)
            {
                var terrainItem = new System.Windows.Controls.MenuItem { Header = ToDisplaySurface(item.Name) };
                terrainItem.Click += (_, _) =>
                {
                    selectedDiameter = diameterValue;
                    selectedSurface = item.Name;
                    selectedColor = GetConfiguredTerrainColor(item);
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
        color = selectedColor ?? Color.FromColorIndex(ColorMethod.ByAci, 256);
        return selectedDiameter != null && selectedSurface != null;
    }

    private static double? ReadActivityQuantity(Editor editor, double geometricLength, bool firstComponent)
    {
        string defaultText = firstComponent ? $" [línea: {FormatQuantity(geometricLength)} ML]" : string.Empty;
        var options = new PromptDoubleOptions($"\nValor de LONG. para este terreno{defaultText}: ")
        {
            AllowZero = false, AllowNegative = false, AllowNone = false
        };
        if (firstComponent)
        {
            options.DefaultValue = geometricLength;
            options.UseDefaultValue = true;
        }
        PromptDoubleResult result = editor.GetDouble(options);
        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static Color? GetConfiguredTerrainColor(UcSurface surface)
    {
        if (surface.ColorIndex.HasValue) return Color.FromColorIndex(ColorMethod.ByAci, (short)surface.ColorIndex.Value);
        if (surface.Red.HasValue && surface.Green.HasValue && surface.Blue.HasValue) return Color.FromRgb((byte)surface.Red.Value, (byte)surface.Green.Value, (byte)surface.Blue.Value);
        return null;
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
        if (!TryShowSpiralDialog(out double pipe, out double unions, out double tees, out double valves,
                out double saddles, out string saddleDiameter, out string surface, out Color selectedColor))
            return null;

        // PE.EXT. ya no se pregunta: se genera automáticamente cuando hay SILLETA.
        bool hasSaddle = saddles > 0.0;
        string peExt = hasSaddle ? "Y" : "N";
        var lines = new List<string>();
        if (pipe > 0.0) lines.Add($"{FormatQuantity(pipe)}ML TUBERIA 3/4\"");
        if (unions > 0.0) lines.Add($"{FormatInteger(unions)} UNIONES DE 3/4\"");
        if (tees > 0.0) lines.Add($"{FormatInteger(tees)} TEE DE 3/4\"");
        if (valves > 0.0) lines.Add($"{FormatInteger(valves)} VALVULA DE 3/4\"");
        if (hasSaddle) { lines.Add($"{FormatInteger(saddles)} SILLETA DE {saddleDiameter}"); lines.Add("PE.EXT."); }
        if (lines.Count == 0) { editor.WriteMessage("\nESPIRAL: no se generó ninguna línea porque todas las cantidades fueron cero.\n"); return string.Empty; }
        spiralData = new SpiralData(pipe, unions, tees, valves, saddles, saddleDiameter, peExt, surface, selectedColor);
        editor.WriteMessage($"\nESPIRAL registrado: {ToDisplaySurface(surface)}. Todos sus componentes usarán este terreno.\n");
        return string.Join("\\P", lines);
    }

    // Ventana única con todos los campos numéricos del ESPIRAL editables a la vez (sin salir
    // del menú entre cada uno) más el terreno, en vez de los prompts secuenciales anteriores.
    private static bool TryShowSpiralDialog(out double pipe, out double unions, out double tees, out double valves,
        out double saddles, out string saddleDiameter, out string surface, out Color color)
    {
        var dialog = new SpiralWindow(Surfaces);
        try { new System.Windows.Interop.WindowInteropHelper(dialog).Owner = Application.MainWindow.Handle; } catch { }
        bool? result = dialog.ShowDialog();
        if (result != true)
        {
            pipe = unions = tees = valves = saddles = 0.0;
            saddleDiameter = string.Empty;
            surface = string.Empty;
            color = Color.FromColorIndex(ColorMethod.ByAci, 256);
            return false;
        }

        pipe = dialog.Pipe;
        unions = dialog.Unions;
        tees = dialog.Tees;
        valves = dialog.Valves;
        saddles = dialog.Saddles;
        saddleDiameter = dialog.SaddleDiameter;
        surface = dialog.SelectedSurfaceName;
        color = dialog.SelectedColor;
        return true;
    }

    private static string? ReadYesNo(Editor editor, string prompt)
    {
        var options = new PromptKeywordOptions(prompt) { AllowNone = false }; options.Keywords.Add("Y"); options.Keywords.Add("N");
        PromptResult result = editor.GetKeywords(options); return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private sealed class SpiralWindow : System.Windows.Window
    {
        private readonly UcSurface[] _surfaces;
        private readonly System.Windows.Controls.TextBox _pipeBox;
        private readonly System.Windows.Controls.TextBox _unionsBox;
        private readonly System.Windows.Controls.TextBox _teesBox;
        private readonly System.Windows.Controls.TextBox _valvesBox;
        private readonly System.Windows.Controls.TextBox _saddlesBox;
        private readonly System.Windows.Controls.ComboBox _saddleDiameterCombo;
        private readonly System.Windows.Controls.ComboBox _terrainCombo;

        // Debe coincidir con las SILLETA del catálogo de materiales en GenerarExcelTool
        // (misma lista, para que el texto elegido siempre matchee con el código correcto).
        private static readonly string[] SaddleDiameters = { "2x3/4", "3x3/4", "4x3/4", "6x3/4" };

        public double Pipe { get; private set; }
        public double Unions { get; private set; }
        public double Tees { get; private set; }
        public double Valves { get; private set; }
        public double Saddles { get; private set; }
        public string SaddleDiameter { get; private set; } = string.Empty;
        public string SelectedSurfaceName { get; private set; } = string.Empty;
        public Color SelectedColor { get; private set; } = Color.FromColorIndex(ColorMethod.ByAci, 256);

        public SpiralWindow(UcSurface[] surfaces)
        {
            _surfaces = surfaces;
            Title = "AutoKADN - ESPIRAL";
            Width = 280;
            SizeToContent = System.Windows.SizeToContent.Height;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            ResizeMode = System.Windows.ResizeMode.NoResize;

            var grid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(10) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            for (int i = 0; i < 8; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            _pipeBox = AddField(grid, 0, "TUBERIA 3/4\":", "3");
            _unionsBox = AddField(grid, 1, "UNIONES 3/4\":", "1");
            _teesBox = AddField(grid, 2, "TEE 3/4\":", "1");
            _valvesBox = AddField(grid, 3, "VALVULA 3/4\":", "1");
            _saddlesBox = AddField(grid, 4, "SILLETA:", "1");

            _saddleDiameterCombo = AddCombo(grid, 5, "DIAM. SILLETA:");
            foreach (string diameter in SaddleDiameters) _saddleDiameterCombo.Items.Add(diameter + "\"");
            _saddleDiameterCombo.SelectedIndex = 0;

            _terrainCombo = AddCombo(grid, 6, "TERRENO:");
            foreach (UcSurface item in surfaces) _terrainCombo.Items.Add(ToDisplaySurface(item.Name));
            _terrainCombo.SelectedIndex = 0;

            var buttonsPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 8, 0, 0) };
            var cancelButton = new System.Windows.Controls.Button { Content = "Cancelar", Padding = new System.Windows.Thickness(10, 3, 10, 3), Margin = new System.Windows.Thickness(0, 0, 6, 0) };
            cancelButton.Click += (_, _) => { DialogResult = false; Close(); };
            buttonsPanel.Children.Add(cancelButton);
            var acceptButton = new System.Windows.Controls.Button { Content = "Generar", Padding = new System.Windows.Thickness(10, 3, 10, 3), FontWeight = System.Windows.FontWeights.Bold, IsDefault = true };
            acceptButton.Click += OnAcceptClick;
            buttonsPanel.Children.Add(acceptButton);
            System.Windows.Controls.Grid.SetRow(buttonsPanel, 7);
            System.Windows.Controls.Grid.SetColumn(buttonsPanel, 0);
            System.Windows.Controls.Grid.SetColumnSpan(buttonsPanel, 2);
            grid.Children.Add(buttonsPanel);

            Content = grid;
            _pipeBox.Focus();
        }

        private static System.Windows.Controls.TextBox AddField(System.Windows.Controls.Grid grid, int row, string label, string defaultValue)
        {
            var textBlock = new System.Windows.Controls.TextBlock { Text = label, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0, 0, 6, 3) };
            System.Windows.Controls.Grid.SetRow(textBlock, row);
            System.Windows.Controls.Grid.SetColumn(textBlock, 0);
            grid.Children.Add(textBlock);

            var textBox = new System.Windows.Controls.TextBox { Text = defaultValue, Padding = new System.Windows.Thickness(3, 1, 3, 1), Margin = new System.Windows.Thickness(0, 0, 0, 3) };
            textBox.GotFocus += (_, _) => textBox.SelectAll();
            System.Windows.Controls.Grid.SetRow(textBox, row);
            System.Windows.Controls.Grid.SetColumn(textBox, 1);
            grid.Children.Add(textBox);
            return textBox;
        }

        private static System.Windows.Controls.ComboBox AddCombo(System.Windows.Controls.Grid grid, int row, string label)
        {
            var textBlock = new System.Windows.Controls.TextBlock { Text = label, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0, 0, 6, 3) };
            System.Windows.Controls.Grid.SetRow(textBlock, row);
            System.Windows.Controls.Grid.SetColumn(textBlock, 0);
            grid.Children.Add(textBlock);

            var comboBox = new System.Windows.Controls.ComboBox { Margin = new System.Windows.Thickness(0, 0, 0, 3) };
            System.Windows.Controls.Grid.SetRow(comboBox, row);
            System.Windows.Controls.Grid.SetColumn(comboBox, 1);
            grid.Children.Add(comboBox);
            return comboBox;
        }

        private void OnAcceptClick(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!TryParse(_pipeBox.Text, out double pipe) || !TryParse(_unionsBox.Text, out double unions) ||
                !TryParse(_teesBox.Text, out double tees) || !TryParse(_valvesBox.Text, out double valves) ||
                !TryParse(_saddlesBox.Text, out double saddles) ||
                pipe < 0.0 || unions < 0.0 || tees < 0.0 || valves < 0.0 || saddles < 0.0)
            {
                System.Windows.MessageBox.Show(this, "Ingrese valores numéricos válidos (mayores o iguales a cero).", "AutoKADN", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (saddles > 0.0 && _saddleDiameterCombo.SelectedIndex < 0)
            {
                System.Windows.MessageBox.Show(this, "Seleccione el diámetro de la silleta.", "AutoKADN", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            string saddleDiameter = _saddleDiameterCombo.SelectedIndex >= 0 ? SaddleDiameters[_saddleDiameterCombo.SelectedIndex] : string.Empty;

            if (_terrainCombo.SelectedIndex < 0)
            {
                System.Windows.MessageBox.Show(this, "Seleccione un terreno.", "AutoKADN", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            Pipe = pipe; Unions = unions; Tees = tees; Valves = valves; Saddles = saddles;
            SaddleDiameter = saddleDiameter;
            UcSurface selectedSurface = _surfaces[_terrainCombo.SelectedIndex];
            SelectedSurfaceName = selectedSurface.Name;
            SelectedColor = GetConfiguredTerrainColor(selectedSurface) ?? Color.FromColorIndex(ColorMethod.ByAci, 256);

            DialogResult = true;
            Close();
        }

        private static bool TryParse(string text, out double value) =>
            double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static ObjectId CreateReferenceLine(Database database, Point3d startPoint, Point3d endPoint)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        var line = new Line(startPoint, endPoint) { ColorIndex = 256 };
        currentSpace.AppendEntity(line); transaction.AddNewlyCreatedDBObject(line, true); transaction.Commit(); return line.ObjectId;
    }

    private static void CreateText(Database database, Point3d startPoint, Point3d endPoint, string text,
        SpiralData? spiralData, List<ActivityComponent>? activityData)
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
            new TypedValue((int)DxfCode.ExtendedDataReal, data.Pipe), new TypedValue((int)DxfCode.ExtendedDataReal, data.Unions),
            new TypedValue((int)DxfCode.ExtendedDataReal, data.Tees), new TypedValue((int)DxfCode.ExtendedDataReal, data.Valves),
            new TypedValue((int)DxfCode.ExtendedDataReal, data.Saddles), new TypedValue((int)DxfCode.ExtendedDataAsciiString, data.SaddleDiameter),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, data.PeExt), new TypedValue((int)DxfCode.ExtendedDataAsciiString, data.Surface),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, GetColorToken(data.Color)));
    }

    private static void SetActivityXData(Database database, Transaction transaction, MText mtext, List<ActivityComponent> data)
    {
        EnsureRegApp(database, transaction);
        var values = new List<TypedValue>
        {
            new((int)DxfCode.ExtendedDataRegAppName, XDataAppName),
            new((int)DxfCode.ExtendedDataAsciiString, ActivityType),
            new((int)DxfCode.ExtendedDataAsciiString, data[0].Description)
        };
        foreach (ActivityComponent item in data)
        {
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, item.Diameter));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, item.Surface));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataReal, item.Quantity));
        }
        mtext.XData = new ResultBuffer(values.ToArray());
    }

    private static string GetColorToken(Color color)
    {
        if (color.IsByAci) return "ACI:" + color.ColorIndex.ToString(CultureInfo.InvariantCulture);
        return $"RGB:{color.Red},{color.Green},{color.Blue}";
    }

    private static void EnsureRegApp(Database database, Transaction transaction)
    {
        RegAppTable table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(XDataAppName)) return;
        table.UpgradeOpen(); var record = new RegAppTableRecord { Name = XDataAppName };
        table.Add(record); transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static string GetOrCreateLayer(Database database, Transaction transaction, string layerName)
    {
        LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (table.Has(layerName)) return layerName;
        table.UpgradeOpen(); var layer = new LayerTableRecord { Name = layerName };
        table.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true); return layerName;
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        if (transaction.GetObject(database.Clayer, OpenMode.ForRead) is LayerTableRecord layer) return layer.Name;
        return string.Empty;
    }

    private static string? GetSurface(Color color)
    {
        if (color.IsByLayer || color.IsByBlock || color.IsNone) return null;
        foreach (UcSurface surface in Surfaces)
        {
            if (surface.ColorIndex.HasValue && color.IsByAci && color.ColorIndex == surface.ColorIndex.Value) return surface.Name;
            if (surface.Red.HasValue && IsSameRgb(color, surface.Red.Value, surface.Green!.Value, surface.Blue!.Value)) return surface.Name;
        }
        return null;
    }

    private static bool IsSameRgb(Color color, int red, int green, int blue) => color.Red == red && color.Green == green && color.Blue == blue;


    private static string FormatQuantity(double value) => Math.Abs(value).ToString("0.0##", CultureInfo.InvariantCulture);
    private static string FormatInteger(double value) => Math.Round(Math.Abs(value)).ToString("0", CultureInfo.InvariantCulture);

    private static void EraseEntity(Database database, ObjectId objectId)
    {
        if (objectId == ObjectId.Null) return;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        if (transaction.GetObject(objectId, OpenMode.ForWrite, false) is Entity entity) entity.Erase();
        transaction.Commit();
    }

    private sealed record SpiralData(double Pipe, double Unions, double Tees, double Valves, double Saddles, string SaddleDiameter, string PeExt, string Surface, Color Color);
    private sealed record ActivityComponent(string Description, string Diameter, string Surface, double Quantity, Color Color);
    private readonly record struct UcSurface(string Name, int? ColorIndex, int? Red, int? Green, int? Blue);
}