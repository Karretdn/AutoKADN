using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AutoKADN.Tools.Debug;

public sealed class DebugDetallesTool
{
    private const string UcLayoutPattern = @"^ANILLO\s+(\d+)\s+UC$";
    private const string DetailLayoutPattern = @"^ANILLO\s+(\d+)\s+DETALLE$";
    private const string BlocksLayer = "Mat";
    private const string XDataAppName = "AUTOKADN";
    private const string UcSurfaceXDataType = "UC_SURFACE";

    private static DebugWindow _window;

    public void Run()
    {
        if (_window != null && _window.IsLoaded)
        {
            _window.RefreshData();
            _window.Activate();
            return;
        }

        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        _window = new DebugWindow(document.Database);
        _window.Closed += (_, _) => _window = null;
        try { new WindowInteropHelper(_window).Owner = Autodesk.AutoCAD.ApplicationServices.Core.Application.MainWindow.Handle; } catch { }
        _window.Show();
        _window.RefreshData();
    }

    private sealed class DebugWindow : Window
    {
        private readonly Database _database;
        private readonly StackPanel _content;
        private readonly TextBlock _status;

        public DebugWindow(Database database)
        {
            _database = database;
            Title = "AutoKADN - DEBUG DETALLES";
            Width = 1100;
            Height = 700;
            MinWidth = 800;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new DockPanel { Margin = new Thickness(12, 10, 12, 8) };
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray };
            toolbar.Children.Add(_status);
            var refresh = new Button { Content = "Actualizar", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right };
            refresh.Click += (_, _) => RefreshData();
            DockPanel.SetDock(refresh, Dock.Right);
            toolbar.Children.Add(refresh);
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            _content = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
            var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            Content = root;
        }

        public void RefreshData()
        {
            DebugSnapshot snapshot = Scan(_database);
            _content.Children.Clear();
            _status.Text = "UC: " + snapshot.UcCount + " | DETALLES: " + snapshot.DetailCount + " | MATERIALES: " + snapshot.Rows.Count;

            foreach (IGrouping<string, DebugRow> group in snapshot.Rows.GroupBy(x => x.Surface, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                AddSurfaceSection(group.Key, group.OrderBy(x => x.Diameter, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Description, StringComparer.OrdinalIgnoreCase).ToList());
            }

            if (snapshot.Rows.Count == 0)
            {
                _content.Children.Add(new TextBlock { Text = "No se encontraron bloques Mat con DIAMETRO y UC válidos en los layouts ANILLO X DETALLE.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            }
        }

        private void AddSurfaceSection(string surface, List<DebugRow> rows)
        {
            var header = new Border { Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 4), CornerRadius = new CornerRadius(4) };
            header.Child = new TextBlock { Text = "TERRENO: " + surface, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold };
            _content.Children.Add(header);

            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Height = Math.Min(360, Math.Max(72, rows.Count * 28 + 42)), Margin = new Thickness(0, 0, 0, 8), ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "DESCRIPCION", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "DIAMETRO", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Diameter)), Width = new DataGridLength(120) });
            grid.Columns.Add(new DataGridTextColumn { Header = "CANTIDAD", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Quantity)), Width = new DataGridLength(100) });
            grid.Columns.Add(new DataGridTextColumn { Header = "ORIGEN", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Source)), Width = new DataGridLength(130) });
            grid.Columns.Add(new DataGridTextColumn { Header = "LAYOUT", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Layout)), Width = new DataGridLength(190) });
            _content.Children.Add(grid);
        }
    }

    private static DebugSnapshot Scan(Database database)
    {
        var rows = new Dictionary<DebugKey, int>();
        var ucSurfaces = new Dictionary<int, HashSet<string>>();
        int detailCount = 0;

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);

            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;
                Match match = Regex.Match(layout.LayoutName.Trim(), UcLayoutPattern, RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                int ring = int.Parse(match.Groups[1].Value);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    Dimension dimension = transaction.GetObject(objectId, OpenMode.ForRead) as Dimension;
                    if (dimension == null) continue;
                    if (GetUcDiameter(dimension.Layer) == null) continue;
                    string surface = GetSurfaceFromXData(dimension);
                    if (string.IsNullOrWhiteSpace(surface)) continue;
                    if (!ucSurfaces.TryGetValue(ring, out HashSet<string> set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); ucSurfaces[ring] = set; }
                    set.Add(surface);
                }
            }

            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;
                Match match = Regex.Match(layout.LayoutName.Trim(), DetailLayoutPattern, RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                int ring = int.Parse(match.Groups[1].Value);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    BlockReference block = transaction.GetObject(objectId, OpenMode.ForRead) as BlockReference;
                    if (block == null || !string.Equals(block.Layer, BlocksLayer, StringComparison.OrdinalIgnoreCase)) continue;
                    detailCount++;

                    string description = GetBlockName(transaction, block);
                    string diameter = GetDynamicProperty(block, "DIAMETRO");
                    string surface = GetDynamicProperty(block, "UC");
                    string source = "UC/DIAMETRO";

                    string xdataDescription = description;
                    string xdataDiameter = diameter;
                    string xdataSurface = surface;
                    if (GetMaterialXData(block, ref xdataDescription, ref xdataDiameter, ref xdataSurface))
                    {
                        if (string.IsNullOrWhiteSpace(description)) description = xdataDescription;
                        if (string.IsNullOrWhiteSpace(diameter)) diameter = xdataDiameter;
                        if (string.IsNullOrWhiteSpace(surface)) surface = xdataSurface;
                        source = "XDATA MATERIAL";
                    }

                    surface = NormalizeSurface(surface);
                    if (string.IsNullOrWhiteSpace(surface) && ucSurfaces.TryGetValue(ring, out HashSet<string> surfaces) && surfaces.Count == 1)
                    {
                        surface = surfaces.First();
                        source = "UC DEL ANILLO";
                    }
                    if (string.IsNullOrWhiteSpace(surface)) surface = "SIN TERRENO";
                    if (string.IsNullOrWhiteSpace(diameter)) diameter = "SIN DIAMETRO";
                    if (string.IsNullOrWhiteSpace(description)) description = "SIN DESCRIPCION";

                    DebugKey key = new DebugKey(surface, description, diameter, source, layout.LayoutName);
                    rows.TryGetValue(key, out int current);
                    rows[key] = current + 1;
                }
            }
            transaction.Commit();
        }

        return new DebugSnapshot(ucSurfaces.Count, detailCount, rows.Select(x => new DebugRow(x.Key.Surface, x.Key.Description, x.Key.Diameter, x.Value, x.Key.Source, x.Key.Layout)).ToList());
    }

    private static string GetBlockName(Transaction transaction, BlockReference block)
    {
        ObjectId definitionId = block.BlockTableRecord;
        if (block.IsDynamicBlock && !block.DynamicBlockTableRecord.IsNull) definitionId = block.DynamicBlockTableRecord;
        BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord;
        return definition == null ? string.Empty : definition.Name;
    }

    private static bool GetMaterialXData(BlockReference block, ref string description, ref string diameter, ref string surface)
    {
        ResultBuffer xdata = block.GetXDataForApplication(XDataAppName);
        if (xdata == null) return false;
        TypedValue[] values = xdata.AsArray();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].TypeCode != (int)DxfCode.ExtendedDataAsciiString || !string.Equals(values[i].Value as string, "MATERIAL", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < values.Length) description = values[i + 1].Value == null ? description : values[i + 1].Value.ToString().Trim();
            if (i + 2 < values.Length) diameter = values[i + 2].Value == null ? diameter : values[i + 2].Value.ToString().Trim();
            if (i + 6 < values.Length) surface = values[i + 6].Value == null ? surface : values[i + 6].Value.ToString().Trim();
            return true;
        }
        return false;
    }

    private static string GetDynamicProperty(BlockReference block, string propertyName)
    {
        if (!block.IsDynamicBlock) return string.Empty;
        foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
            if (string.Equals(property.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase)) return property.Value == null ? string.Empty : property.Value.ToString().Trim();
        return string.Empty;
    }

    private static string GetSurfaceFromXData(DBObject entity)
    {
        ResultBuffer xdata = entity.GetXDataForApplication(XDataAppName);
        if (xdata == null) return string.Empty;
        TypedValue[] values = xdata.AsArray();
        for (int i = 0; i < values.Length - 1; i++)
            if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, UcSurfaceXDataType, StringComparison.OrdinalIgnoreCase)) return NormalizeSurface(values[i + 1].Value as string);
        return string.Empty;
    }

    private static string GetUcDiameter(string layer)
    {
        if (string.Equals(layer, "UC_1-2", StringComparison.OrdinalIgnoreCase)) return "1/2";
        if (string.Equals(layer, "UC_3-4", StringComparison.OrdinalIgnoreCase)) return "3/4";
        return null;
    }

    private static string NormalizeSurface(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string normalized = value.Trim().ToUpperInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        if (normalized == "CALZADA ASFALTO") normalized = "ASFALTO";
        return normalized;
    }

    private sealed record DebugSnapshot(int UcCount, int DetailCount, List<DebugRow> Rows);
    private sealed record DebugRow(string Surface, string Description, string Diameter, int Quantity, string Source, string Layout);
    private readonly record struct DebugKey(string Surface, string Description, string Diameter, string Source, string Layout);
}
