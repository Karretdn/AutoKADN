using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;
using AutoKADN.Tools.Anotaciones;
using static AutoKADN.Core.Naming;
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
    private const string XDataAppName = "AUTOKADN";
    private const string UcSurfaceXDataType = "UC_SURFACE";
    private const string SummaryType = "RESUMEN_UC";

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        string layoutName = LayoutManager.Current.CurrentLayout;
        var quantities = new Dictionary<UcKey, double>();
        var crossings = new List<KeyValuePair<UcKey, double>>();

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            ObjectId layoutId = LayoutManager.Current.GetLayoutId(layoutName);
            var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
            var layoutSpace = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId objectId in layoutSpace)
            {
                DBObject entity = transaction.GetObject(objectId, OpenMode.ForRead);
                // CRUCE DE ARROYO anotado en este layout UC: no es una cota, se descuenta más abajo.
                if (entity is MText note) { crossings.AddRange(ActividadXData.ReadQuantities(note, CruceArroyoLabel)); continue; }
                if (entity is not Dimension dimension) continue;
                string? diameter = GetUcDiameter(dimension.Layer);
                if (diameter is null) continue;
                string? surface = GetSurface(dimension);
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
            editor.WriteMessage($"\nNo se encontraron cotas UC válidas en '{string.Join("'/'", UcLayerDiameters.Keys)}' del layout '{layoutName}'.\n");
            return;
        }

        ApplyCruceArroyo(editor, quantities, crossings);

        // El espiral se suma a una UC normal: el CRUCE DE ARROYO (UC aparte) no se ofrece en esa lista.
        List<UcKey> availableUcs = quantities.Keys
            .Where(x => !IsCruceArroyo(x))
            .OrderBy(x => GetSurfaceOrder(x.Surface))
            .ThenBy(x => x.Diameter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dialog = new SpiralWindow(availableUcs.Select(uc => $"{uc.Diameter}\" - {ToDisplaySurface(uc.Surface)} ({FormatQuantity(quantities[uc])} ML)").ToArray());
        try { new System.Windows.Interop.WindowInteropHelper(dialog).Owner = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle; } catch { }
        if (dialog.ShowDialog() != true) return;

        if (dialog.HasSpiral)
        {
            UcKey selectedUc = availableUcs[dialog.SelectedUcIndex];
            quantities[selectedUc] += dialog.Meters;
            editor.WriteMessage($"\nSe sumaron {FormatQuantity(dialog.Meters)} ML a {selectedUc.Diameter} Pulg. - {ToDisplaySurface(selectedUc.Surface)}.\n");
        }

        PromptPointResult pointResult = editor.GetPoint(new PromptPointOptions("\nSeleccione el vértice SUPERIOR IZQUIERDO de la lista de UNIDAD CONSTRUCTIVA: "));
        if (pointResult.Status != PromptStatus.OK) return;
        CreateTexts(database, pointResult.Value, quantities, layoutName);
        editor.Regen();
        editor.WriteMessage($"\nResumen UC generado en el layout '{layoutName}'.\n");
    }

    // CRUCE DE ARROYO: los metros se ven en el plano como parte de su terreno, pero aquí se descuentan de esa UC
    // (diámetro + terreno) y salen en una línea aparte "… En Cruce De Arroyo". Lo anotado manda: si supera lo que
    // hay en la UC, ésta queda en cero y se avisa. Una UC que queda sin metros no sale en la lista.
    private static void ApplyCruceArroyo(Editor editor, Dictionary<UcKey, double> quantities, List<KeyValuePair<UcKey, double>> crossings)
    {
        // Varias anotaciones sobre la misma UC se suman antes de descontar.
        var perUc = new Dictionary<UcKey, double>();
        foreach (KeyValuePair<UcKey, double> crossing in crossings)
        {
            perUc.TryGetValue(crossing.Key, out double sum);
            perUc[crossing.Key] = sum + crossing.Value;
        }

        foreach (KeyValuePair<UcKey, double> crossing in perUc)
        {
            UcKey uc = crossing.Key;
            double meters = crossing.Value;
            if (meters <= 0.0) continue;

            if (!quantities.TryGetValue(uc, out double current))
            {
                editor.WriteMessage($"\nCRUCE DE ARROYO de {FormatQuantity(meters)} ML en {uc.Diameter} Pulg. - {ToDisplaySurface(uc.Surface)}: no hay cotas UC de ese diámetro y terreno en este layout; no se descontó de ninguna UC.\n");
            }
            else
            {
                if (meters > current + 1e-6)
                    editor.WriteMessage($"\nCRUCE DE ARROYO de {FormatQuantity(meters)} ML en {uc.Diameter} Pulg. - {ToDisplaySurface(uc.Surface)}: supera los {FormatQuantity(current)} ML de cotas de esa UC; queda en cero.\n");
                double remaining = Math.Max(0.0, current - meters);
                if (remaining <= 1e-6) quantities.Remove(uc); else quantities[uc] = remaining;
            }

            var arroyo = new UcKey(uc.Diameter, CruceArroyoSurface);
            quantities.TryGetValue(arroyo, out double previous);
            quantities[arroyo] = previous + meters;
            editor.WriteMessage($"\nCRUCE DE ARROYO: {FormatQuantity(meters)} ML de {uc.Diameter} Pulg. pasan de {ToDisplaySurface(uc.Surface)} a su propia línea.\n");
        }
    }

    // Ventana compacta (mismo estilo que la del ESPIRAL en ANOTACIONES) en vez de los prompts de
    // texto: casilla de espiral, metros y la UC donde sumarlos.
    private sealed class SpiralWindow : System.Windows.Window
    {
        private readonly System.Windows.Controls.CheckBox _hasSpiralBox;
        private readonly System.Windows.Controls.TextBox _metersBox;
        private readonly System.Windows.Controls.ComboBox _ucCombo;

        public bool HasSpiral { get; private set; }
        public double Meters { get; private set; }
        public int SelectedUcIndex { get; private set; }

        public SpiralWindow(string[] ucItems)
        {
            Title = "AutoKADN - RESUMEN UC";
            Width = 340;
            SizeToContent = System.Windows.SizeToContent.Height;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            ResizeMode = System.Windows.ResizeMode.NoResize;

            var grid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(10) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            _hasSpiralBox = new System.Windows.Controls.CheckBox { Content = "¿HAY ESPIRAL?", FontWeight = System.Windows.FontWeights.Bold, Margin = new System.Windows.Thickness(0, 0, 0, 6) };
            System.Windows.Controls.Grid.SetRow(_hasSpiralBox, 0);
            System.Windows.Controls.Grid.SetColumnSpan(_hasSpiralBox, 2);
            grid.Children.Add(_hasSpiralBox);

            _metersBox = new System.Windows.Controls.TextBox { Text = "0", IsEnabled = false, Padding = new System.Windows.Thickness(3, 1, 3, 1), Margin = new System.Windows.Thickness(0, 0, 0, 3) };
            _metersBox.GotFocus += (_, _) => _metersBox.SelectAll();
            AddRow(grid, 1, "METROS (ML):", _metersBox);

            _ucCombo = new System.Windows.Controls.ComboBox { IsEnabled = false, Margin = new System.Windows.Thickness(0, 0, 0, 3) };
            foreach (string item in ucItems) _ucCombo.Items.Add(item);
            if (_ucCombo.Items.Count > 0) _ucCombo.SelectedIndex = 0;
            AddRow(grid, 2, "SUMAR EN UC:", _ucCombo);

            _hasSpiralBox.Checked += (_, _) => { _metersBox.IsEnabled = true; _ucCombo.IsEnabled = true; _metersBox.Focus(); };
            _hasSpiralBox.Unchecked += (_, _) => { _metersBox.IsEnabled = false; _ucCombo.IsEnabled = false; };

            var buttonsPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 8, 0, 0) };
            var cancelButton = new System.Windows.Controls.Button { Content = "Cancelar", Padding = new System.Windows.Thickness(10, 3, 10, 3), Margin = new System.Windows.Thickness(0, 0, 6, 0) };
            cancelButton.Click += (_, _) => { DialogResult = false; Close(); };
            buttonsPanel.Children.Add(cancelButton);
            var acceptButton = new System.Windows.Controls.Button { Content = "Generar", Padding = new System.Windows.Thickness(10, 3, 10, 3), FontWeight = System.Windows.FontWeights.Bold, IsDefault = true };
            acceptButton.Click += OnAcceptClick;
            buttonsPanel.Children.Add(acceptButton);
            System.Windows.Controls.Grid.SetRow(buttonsPanel, 3);
            System.Windows.Controls.Grid.SetColumnSpan(buttonsPanel, 2);
            grid.Children.Add(buttonsPanel);

            Content = grid;
        }

        private static void AddRow(System.Windows.Controls.Grid grid, int row, string label, System.Windows.UIElement control)
        {
            var textBlock = new System.Windows.Controls.TextBlock { Text = label, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0, 0, 6, 3) };
            System.Windows.Controls.Grid.SetRow(textBlock, row);
            grid.Children.Add(textBlock);
            System.Windows.Controls.Grid.SetRow(control, row);
            System.Windows.Controls.Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        private void OnAcceptClick(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_hasSpiralBox.IsChecked == true)
            {
                if (!double.TryParse(_metersBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double meters) || meters <= 0.0)
                {
                    System.Windows.MessageBox.Show(this, "Ingrese los metros de espiral (mayor que cero).", "AutoKADN", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (_ucCombo.SelectedIndex < 0)
                {
                    System.Windows.MessageBox.Show(this, "Seleccione la UC donde sumar el espiral.", "AutoKADN", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                HasSpiral = true;
                Meters = meters;
                SelectedUcIndex = _ucCombo.SelectedIndex;
            }
            DialogResult = true;
            Close();
        }
    }

    private static string? GetUcDiameter(string layer)
    {
        return GetUcDiameterFromLayer(layer);
    }

    private static string? GetSurface(Dimension dimension)
    {
        // CotaTool.SetDimensionSurface graba, para cada cota UC, un XData bajo
        // la app "AUTOKADN" con esta forma:
        //   [0] ExtendedDataRegAppName -> "AUTOKADN"
        //   [1] ExtendedDataAsciiString -> "UC_SURFACE"
        //   [2] ExtendedDataAsciiString -> nombre de la superficie, p. ej. "ZONA VERDE"
        // Antes esta función intentaba adivinar la superficie por el color de
        // la cota/capa, pero CotaTool nunca asigna color por superficie (deja
        // ColorIndex = 256, ByLayer), así que esa detección nunca coincidía
        // con lo realmente guardado. Ahora se lee el XData directamente.
        ResultBuffer? xdata = dimension.GetXDataForApplication(XDataAppName);
        if (xdata is null) return null;

        TypedValue[] values = xdata.AsArray();
        if (values.Length < 3) return null;
        if (values[1].Value is not string type ||
            !string.Equals(type, UcSurfaceXDataType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return values[2].Value is string surface && !string.IsNullOrWhiteSpace(surface)
            ? surface.Trim()
            : null;
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

    private static void CreateTexts(Database database, Point3d topLeftPoint, IReadOnlyDictionary<UcKey, double> quantities, string layoutName)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        string layerName = GetCurrentLayerName(database, transaction);
        ObjectId textStyleId = database.Textstyle;
        double firstRowY = topLeftPoint.Y - (RowHeight / 2.0);
        string summaryId = Guid.NewGuid().ToString("D");
        EnsureXDataRegApp(database, transaction);
        IEnumerable<KeyValuePair<UcKey, double>> orderedItems = quantities
            .OrderBy(x => GetSurfaceOrder(x.Key.Surface))
            .ThenBy(x => x.Key.Diameter, StringComparer.OrdinalIgnoreCase);

        int index = 0;
        foreach (var item in orderedItems)
        {
            int column = index / SlotsPerColumn;
            int slot = index % SlotsPerColumn;
            double columnX = topLeftPoint.X + (column * ColumnWidth);
            if (column > 0) columnX += RightColumnShift;
            double y = firstRowY - (slot * RowHeight);
            string rowId = Guid.NewGuid().ToString("D");
            string description = $"Canalizacion Tubería De Polietileno De {item.Key.Diameter} Pulg. En {ToDisplaySurface(item.Key.Surface)}";
            double descriptionX = columnX + DescriptionLeftMargin;
            double unitX = columnX + DescriptionWidth + (UnitWidth / 2.0) + UnitCenterCorrection + UnitHorizontalShift;
            double subtotalX = columnX + DescriptionWidth + UnitWidth + (SubtotalWidth / 2.0) + SubtotalCenterCorrection + SubtotalHorizontalShift;
            AddLeftAlignedText(transaction, currentSpace, description, new Point3d(descriptionX, y, 0), TextHeight, layerName, textStyleId, layoutName, summaryId, rowId, "DESCRIPCION");
            AddCenteredText(transaction, currentSpace, "ML", new Point3d(unitX, y, 0), TextHeight, layerName, textStyleId, layoutName, summaryId, rowId, "UNIDAD");
            AddCenteredText(transaction, currentSpace, FormatQuantity(item.Value), new Point3d(subtotalX, y, 0), TextHeight, layerName, textStyleId, layoutName, summaryId, rowId, "CANTIDAD");
            index++;
        }
        transaction.Commit();
    }


    private static string FormatQuantity(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);

    private static void AddLeftAlignedText(Transaction transaction, BlockTableRecord currentSpace, string value, Point3d position, double height, string layerName, ObjectId textStyleId, string layoutName, string summaryId, string rowId, string field)
    {
        var text = new DBText { TextString = value, Position = position, Height = height, TextStyleId = textStyleId, Layer = layerName, HorizontalMode = TextHorizontalMode.TextLeft, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = position };
        currentSpace.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
        AttachSummaryXData(text, SummaryType, layoutName, summaryId, rowId, field);
    }

    private static void AddCenteredText(Transaction transaction, BlockTableRecord currentSpace, string value, Point3d position, double height, string layerName, ObjectId textStyleId, string layoutName, string summaryId, string rowId, string field)
    {
        var text = new DBText { TextString = value, Position = position, Height = height, TextStyleId = textStyleId, Layer = layerName, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = position };
        currentSpace.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
        AttachSummaryXData(text, SummaryType, layoutName, summaryId, rowId, field);
    }

    private static void AttachSummaryXData(DBObject entity, string summaryType, string layoutName, string summaryId, string rowId, string field)
    {
        entity.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, summaryType),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, layoutName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, summaryId),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, rowId),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, field));
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

}
