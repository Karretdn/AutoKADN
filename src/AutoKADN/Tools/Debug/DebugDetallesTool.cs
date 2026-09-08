using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const string UcLayerHalf = "UC_1-2";
    private const string UcLayerThreeQuarter = "UC_3-4";

    private static DebugWindow? _window;

    public void Run()
    {
        if (_window is not null)
        {
            if (_window.IsLoaded)
            {
                _window.RefreshData();
                _window.Activate();
                return;
            }
            _window = null;
        }

        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;

        _window = new DebugWindow(document.Database);
        _window.Closed += (_, _) => _window = null;

        try
        {
            new WindowInteropHelper(_window).Owner = Autodesk.AutoCAD.ApplicationServices.Core.Application.MainWindow.Handle;
        }
        catch
        {
            // El debug sigue funcionando aunque AutoCAD no entregue un propietario WPF válido.
        }

        _window.Show();
        _window.RefreshData();
    }

    private sealed class DebugWindow : Window
    {
        private readonly Database _database;
        private readonly StackPanel _content;
        private readonly TextBlock _status;
        private readonly ScrollViewer _scrollViewer;

        public DebugWindow(Database database)
        {
            _database = database;
            Title = "AutoKADN - DEBUG DETALLES";
            Width = 980;
            Height = 680;
            MinWidth = 760;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new DockPanel { Margin = new Thickness(12, 10, 12, 8) };

            _status = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.DimGray
            };
            DockPanel.SetDock(_status, Dock.Left);
            toolbar.Children.Add(_status);

            var refreshButton = new Button
            {
                Content = "Actualizar",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            refreshButton.Click += (_, _) => RefreshData();
            DockPanel.SetDock(refreshButton, Dock.Right);
            toolbar.Children.Add(refreshButton);

            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            _content = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
            _scrollViewer = new ScrollViewer
            {
                Content = _content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(_scrollViewer, 1);
            root.Children.Add(_scrollViewer);

            Content = root;
        }

        public void RefreshData()
        {
            DebugSnapshot snapshot = Scan(_database);
            _content.Children.Clear();

            _status.Text = $"UC con terreno: {snapshot.UcCount}  |  DETALLES: {snapshot.DetailCount}  |  Registros agrupados: {snapshot.Rows.Count}";

            if (snapshot.Rows.Count == 0)
            {
                _content.Children.Add(new TextBlock
                {
                    Text = "No se encontraron bloques de materiales en los layouts 'ANILLO X DETALLE' asociados a un terreno registrado en los layouts 'ANILLO X UC'.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0)
                });
                return;
            }

            foreach (IGrouping<string, DebugRow> group in snapshot.Rows.GroupBy(x => x.Surface, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => SurfaceOrder(x.Key)))
            {
                AddSurfaceSection(group.Key, group.OrderBy(x => x.Layout, StringComparer.OrdinalIgnoreCase)
                                      .ThenBy(x => x.Description, StringComparer.OrdinalIgnoreCase)
                                      .ThenBy(x => x.Diameter, StringComparer.OrdinalIgnoreCase)
                                      .ToList());
            }
        }

        private void AddSurfaceSection(string surface, List<DebugRow> rows)
        {
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 10, 0, 4),
                CornerRadius = new CornerRadius(4)
            };
            header.Child = new TextBlock
            {
                Text = surface,
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold
            };
            _content.Children.Add(header);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Height = Math.Min(320, Math.Max(72, rows.Count * 28 + 42)),
                Margin = new Thickness(0, 0, 0, 8),
                ItemsSource = rows
            };

            grid.Columns.Add(new DataGridTextColumn { Header = "DESCRIPCION", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "DIAMETRO", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Diameter)), Width = new DataGridLength(120) });
            grid.Columns.Add(new DataGridTextColumn { Header = "CANTIDAD", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Quantity)), Width = new DataGridLength(100) });
            grid.Columns.Add(new DataGridTextColumn { Header = "LAYOUT", Binding = new System.Windows.Data.Binding(nameof(DebugRow.Layout)), Width = new DataGridLength(180) });

            _content.Children.Add(grid);
        }
    }

    private static DebugSnapshot Scan(Database database)
    {
        var terrainByRing = new Dictionary<int, HashSet<string>>();
        var rows = new Dictionary<DebugKey, int>();
        int detailCount = 0;

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);

            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                string layoutName = layout.LayoutName.Trim();
                Match ucMatch = Regex.Match(layoutName, UcLayoutPattern, RegexOptions.IgnoreCase);
                if (!ucMatch.Success) continue;

                int ring = int.Parse(ucMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

                foreach (ObjectId objectId in space)
                {
                    if (transaction.GetObject(objectId, OpenMode.ForRead) is not Dimension dimension) continue;
                    if (GetUcDiameter(dimension.Layer) is null) continue;

                    string? surface = GetSurfaceFromXData(dimension);
                    if (string.IsNullOrWhiteSpace(surface)) continue;

                    if (!terrainByRing.TryGetValue(ring, out HashSet<string>? surfaces))
                    {
                        surfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        terrainByRing[ring] = surfaces;
                    }

                    surfaces.Add(surface);
                }
            }

            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                string layoutName = layout.LayoutName.Trim();
                Match detailMatch = Regex.Match(layoutName, DetailLayoutPattern, RegexOptions.IgnoreCase);
                if (!detailMatch.Success) continue;

                int ring = int.Parse(detailMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                string surface = ResolveSurface(terrainByRing, ring);
                if (string.IsNullOrWhiteSpace(surface)) continue;

                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    if (transaction.GetObject(objectId, OpenMode.ForRead) is not BlockReference blockReference) continue;
                    if (!string.Equals(blockReference.Layer, BlocksLayer, StringComparison.OrdinalIgnoreCase)) continue;

                    detailCount++;

                    string description = GetBlockName(transaction, blockReference);
                    string diameter = GetDiameter(blockReference);
                    DebugKey key = new DebugKey(surface, description, diameter, layoutName);
                    rows.TryGetValue(key, out int current);
                    rows[key] = current + 1;
                }
            }

            transaction.Commit();
        }

        List<DebugRow> result = rows
            .Select(x => new DebugRow(x.Key.Surface, x.Key.Description, x.Key.Diameter, x.Value, x.Key.Layout))
            .ToList();

        return new DebugSnapshot(terrainByRing.Count, detailCount, result);
    }

    private static string ResolveSurface(Dictionary<int, HashSet<string>> terrainByRing, int ring)
    {
        if (!terrainByRing.TryGetValue(ring, out HashSet<string>? surfaces) || surfaces.Count == 0)
            return "SIN TERRENO REGISTRADO";

        if (surfaces.Count == 1)
            return surfaces.First();

        return "TERRENOS MULTIPLES EN UC " + ring + ": " + string.Join(" / ", surfaces.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static string? GetSurfaceFromXData(DBObject entity)
    {
        ResultBuffer? xdata = entity.GetXDataForApplication(XDataAppName);
        if (xdata is null) return null;

        TypedValue[] values = xdata.AsArray();
        for (int i = 0; i < values.Length - 1; i++)
        {
            if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                string.Equals(values[i].Value as string, UcSurfaceXDataType, StringComparison.OrdinalIgnoreCase))
            {
                string? value = values[i + 1].Value as string;
                return NormalizeSurface(value);
            }
        }

        return null;
    }

    private static string? GetUcDiameter(string layer)
    {
        if (string.Equals(layer, UcLayerHalf, StringComparison.OrdinalIgnoreCase)) return "1/2";
        if (string.Equals(layer, UcLayerThreeQuarter, StringComparison.OrdinalIgnoreCase)) return "3/4";
        return null;
    }

    private static string GetBlockName(Transaction transaction, BlockReference blockReference)
    {
        ObjectId definitionId = blockReference.BlockTableRecord;
        if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull)
            definitionId = blockReference.DynamicBlockTableRecord;

        BlockTableRecord? definition = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord;
        return definition?.Name ?? string.Empty;
    }

    private static string GetDiameter(BlockReference blockReference)
    {
        if (!blockReference.IsDynamicBlock) return string.Empty;

        foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
        {
            if (string.Equals(property.PropertyName, "DIAMETRO", StringComparison.OrdinalIgnoreCase))
                return property.Value?.ToString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeSurface(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string normalized = RemoveAccents(value).Trim().ToUpperInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ");

        string compact = normalized.Replace(" ", string.Empty);
        return compact switch
        {
            "ZONAVERDE" => "ZONA VERDE",
            "ANDENTABLETA" => "ANDEN TABLETA",
            "CALZADACONCRETO" => "CALZADA CONCRETO",
            "DESTAPADO" => "DESTAPADO",
            "CUNETA" => "CUNETA",
            "ANDENCONCRETO" => "ANDEN CONCRETO",
            "ASFALTO" => "ASFALTO",
            "ADOQUIN" => "ADOQUIN",
            _ => value.Trim()
        };
    }

    private static string RemoveAccents(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static int SurfaceOrder(string value)
    {
        string[] order =
        {
            "ZONA VERDE", "ANDEN CONCRETO", "CALZADA CONCRETO", "ANDEN TABLETA",
            "ADOQUIN", "ASFALTO", "CUNETA", "DESTAPADO"
        };

        int index = Array.FindIndex(order, x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private sealed record DebugSnapshot(int UcCount, int DetailCount, List<DebugRow> Rows);
    private sealed record DebugRow(string Surface, string Description, string Diameter, int Quantity, string Layout);
    private readonly record struct DebugKey(string Surface, string Description, string Diameter, string Layout);
}
