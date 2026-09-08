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
    private const string SpiralXDataType = "ESPIRAL";
    private const string SpiralDiameter = "3/4";
    private const string ActivityXDataType = "ACTIVIDAD";
    private const string PlanosAsBuiltCode = "100005412";
    private const string PantallaCode = "100006014";
    private const string VigaConcretoCode = "100006013";
    private const string EmpedradoCode = "100006010";
    private const string CruceTopoCode = "100005403";
    private const double EmpedradoFactor = 0.4;

    private static readonly Dictionary<string, string> CanalizacionCodeBySurface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ZONA VERDE"] = "100005377",
        ["ANDEN CONCRETO"] = "100005387",
        ["CALZADA CONCRETO"] = "100005379",
        ["ANDEN TABLETA"] = "100005385",
        ["ADOQUIN"] = "100006913",
        ["ASFALTO"] = "100005386",
        ["CUNETA"] = "100006912",
        ["DESTAPADO"] = "100006911",
    };

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
        private readonly List<Expander> _ucExpanders = new List<Expander>();

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
            var toggleAll = new Button { Content = "Colapsar todo", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Right };
            toggleAll.Click += (_, _) =>
            {
                bool collapse = _ucExpanders.Any(x => x.IsExpanded);
                foreach (Expander expander in _ucExpanders) expander.IsExpanded = !collapse;
                toggleAll.Content = collapse ? "Expandir todo" : "Colapsar todo";
            };
            DockPanel.SetDock(toggleAll, Dock.Right);
            toolbar.Children.Add(toggleAll);
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
            _ucExpanders.Clear();
            int surfaceTypeCount = snapshot.UcRows.Select(x => x.Surface).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _status.Text = "TERRENOS (UC): " + surfaceTypeCount + " | DETALLES: " + snapshot.DetailCount + " | MATERIALES: " + snapshot.Rows.Count + " | ESPIRALES: " + snapshot.SpiralByGroup.Count;

            if (snapshot.UcRows.Count > 0)
            {
                AddUcSection(snapshot.UcRows);
            }
            else
            {
                _content.Children.Add(new TextBlock { Text = "No se encontraron cotas UC válidas (capas UC_1-2 / UC_3-4 con XData UC_SURFACE) en los layouts ANILLO X UC.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold });
            }
            AddDiagnosticsSection(snapshot.Diagnostics);

            Dictionary<UcGroupKey, double> ucTotals = snapshot.UcRows
                .GroupBy(x => new UcGroupKey(x.Surface, x.Diameter))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.LengthValue));

            Dictionary<UcGroupKey, List<DebugRow>> materialsByGroup = snapshot.Rows
                .GroupBy(x => new UcGroupKey(x.Surface, GetUcDiameterFromMaterial(x.Diameter) ?? "SIN CLASIFICAR"))
                .ToDictionary(g => g.Key, g => g.ToList());

            IEnumerable<UcGroupKey> allGroupKeys = ucTotals.Keys.Union(materialsByGroup.Keys).Union(snapshot.SpiralByGroup.Keys).Union(snapshot.ActivityByGroup.Keys)
                .OrderBy(x => x.Surface, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Diameter, StringComparer.OrdinalIgnoreCase);

            foreach (UcGroupKey key in allGroupKeys)
            {
                bool found = ucTotals.TryGetValue(key, out double length);
                List<DebugRow> materials = materialsByGroup.TryGetValue(key, out List<DebugRow> list) ? list : new List<DebugRow>();
                SpiralAgg spiral = snapshot.SpiralByGroup.TryGetValue(key, out SpiralAgg agg) ? agg : null;
                ActivityAgg activity = snapshot.ActivityByGroup.TryGetValue(key, out ActivityAgg act) ? act : null;
                AddUnidadConstructivaSection(key.Surface, key.Diameter, found, length, materials, spiral, activity);
            }

            if (snapshot.Rows.Count == 0 && ucTotals.Count == 0 && snapshot.SpiralByGroup.Count == 0)
            {
                _content.Children.Add(new TextBlock { Text = "No se encontraron bloques Mat con DIAMETRO y UC válidos en los layouts ANILLO X DETALLE.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            }
        }

        private void AddUnidadConstructivaSection(string surface, string diameter, bool found, double length, List<DebugRow> materials, SpiralAgg spiral, ActivityAgg activity)
        {
            double spiralPipe = spiral?.Pipe ?? 0.0;
            double pipeTotal = (found ? length : 0.0) + spiralPipe;

            string headerText = found
                ? "UNIDAD CONSTRUCTIVA: " + surface + " " + diameter + "\" = " + length.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML"
                    + (spiralPipe > 0 ? " (+ " + spiralPipe.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML ESPIRAL)" : string.Empty)
                : "FALTA INGRESAR UC — " + surface + " " + diameter + "\"";

            var headerBorder = new Border
            {
                Background = new SolidColorBrush(found ? Color.FromRgb(20, 90, 40) : Color.FromRgb(150, 30, 30)),
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(4)
            };
            headerBorder.Child = new TextBlock { Text = headerText, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };

            var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (!found)
            {
                string reason = spiral != null
                    ? "Estos materiales (incluyendo el ESPIRAL) no tienen una cota UC (capa UC_1-2/UC_3-4 con terreno " + surface + ") que los reciba. Sin esa cota, NO se generarán en el Excel. Ingrese la cota UC correspondiente en el layout ANILLO X UC."
                    : "Estos materiales no tienen una cota UC (capa UC_1-2/UC_3-4 con terreno " + surface + ") que los reciba. Ingrese la cota UC correspondiente en el layout ANILLO X UC.";
                body.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(150, 30, 30)) });
            }

            var rows = new List<UcMaterialRow>();
            if (pipeTotal > 0)
            {
                string pipeSource = found
                    ? (spiralPipe > 0 ? "UC (METROS) + ESPIRAL" : "UC (METROS)")
                    : "ESPIRAL";
                rows.Add(new UcMaterialRow("TUBERIA", diameter, pipeTotal.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML", pipeSource, spiral != null ? string.Join(", ", spiral.Layouts) : string.Empty));
            }
            if (spiral != null)
            {
                if (spiral.Unions > 0) rows.Add(new UcMaterialRow("UNION", diameter, spiral.Unions.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture), "ESPIRAL", string.Join(", ", spiral.Layouts)));
                if (spiral.Tees > 0) rows.Add(new UcMaterialRow("TEE", diameter, spiral.Tees.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture), "ESPIRAL", string.Join(", ", spiral.Layouts)));
                if (spiral.Valves > 0) rows.Add(new UcMaterialRow("VALVULA", diameter, spiral.Valves.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture), "ESPIRAL", string.Join(", ", spiral.Layouts)));
                foreach ((string saddleDiameter, double qty) in spiral.Saddles)
                {
                    rows.Add(new UcMaterialRow("SILLETA", saddleDiameter, qty.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture), "ESPIRAL", string.Join(", ", spiral.Layouts)));
                }
            }
            rows.AddRange(materials.OrderBy(x => x.Diameter, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Description, StringComparer.OrdinalIgnoreCase)
                .Select(x => new UcMaterialRow(x.Description, x.Diameter, x.Quantity.ToString(), x.Source, x.Layout)));

            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Height = Math.Min(280, Math.Max(60, rows.Count * 24 + 36)), RowHeight = 22, Margin = new Thickness(0, 0, 0, 6), ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "DESCRIPCION", Binding = new System.Windows.Data.Binding(nameof(UcMaterialRow.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "DIAMETRO", Binding = new System.Windows.Data.Binding(nameof(UcMaterialRow.Diameter)), Width = new DataGridLength(120) });
            grid.Columns.Add(new DataGridTextColumn { Header = "CANTIDAD", Binding = new System.Windows.Data.Binding(nameof(UcMaterialRow.Quantity)), Width = new DataGridLength(100) });
            grid.Columns.Add(new DataGridTextColumn { Header = "ORIGEN", Binding = new System.Windows.Data.Binding(nameof(UcMaterialRow.Source)), Width = new DataGridLength(130) });
            grid.Columns.Add(new DataGridTextColumn { Header = "LAYOUT", Binding = new System.Windows.Data.Binding(nameof(UcMaterialRow.Layout)), Width = new DataGridLength(190) });
            body.Children.Add(grid);

            AddActividadesSection(body, surface, diameter, found, length, pipeTotal, activity);

            var expander = new Expander
            {
                Header = headerBorder,
                Content = body,
                IsExpanded = true,
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(0, 4, 0, 0)
            };
            _ucExpanders.Add(expander);
            _content.Children.Add(expander);
        }

        private void AddActividadesSection(Panel target, string surface, string diameter, bool found, double length, double pipeTotal, ActivityAgg activity)
        {
            double camisa = activity?.Camisa ?? 0.0;
            double cruceTopo = activity?.CruceTopo ?? 0.0;
            double pantalla = activity?.Pantalla ?? 0.0;
            double vigaConcreto = activity?.VigaConcreto ?? 0.0;
            double empedradoMl = activity?.Empedrado ?? 0.0;

            var rows = new List<ActivityRow>();

            if (found)
            {
                double canalizacion = length - camisa - cruceTopo;
                string canalizacionCode = CanalizacionCodeBySurface.TryGetValue(surface, out string code) ? code : "SIN CODIGO";
                string canalizacionNota = (camisa > 0 || cruceTopo > 0)
                    ? "UC (" + length.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + ") − CAMISA (" + camisa.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + ") − CRUCE TOPO (" + cruceTopo.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + ")"
                    : "UC";
                rows.Add(new ActivityRow(canalizacionCode, "CANALIZACION ANILLO", canalizacion.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML", canalizacionNota));
                rows.Add(new ActivityRow(PlanosAsBuiltCode, "PLANOS AS-BUILT", pipeTotal.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML", "= TUBERIA (UC + ESPIRAL)"));
            }

            if (cruceTopo > 0) rows.Add(new ActivityRow(CruceTopoCode, "CRUCE CON TOPO", cruceTopo.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML", found ? "También resta en Canalización" : "Sin UC: no se generará"));
            if (pantalla > 0) rows.Add(new ActivityRow(PantallaCode, "PANTALLA", pantalla.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML", found ? string.Empty : "Sin UC: no se generará"));
            if (vigaConcreto > 0) rows.Add(new ActivityRow(VigaConcretoCode, "VIGA EN CONCRETO", vigaConcreto.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML", found ? string.Empty : "Sin UC: no se generará"));
            if (empedradoMl > 0) rows.Add(new ActivityRow(EmpedradoCode, "EMPEDRADO", (empedradoMl * EmpedradoFactor).ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " m²", (found ? string.Empty : "Sin UC: no se generará. ") + empedradoMl.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML × 0.4"));
            if (camisa > 0) rows.Add(new ActivityRow("(sin línea propia)", "CAMISA", camisa.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " ML", found ? "Solo resta en Canalización" : "Sin UC: no resta nada"));

            if (rows.Count == 0) return;

            var header = new Border { Background = new SolidColorBrush(Color.FromRgb(90, 60, 20)), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(4) };
            header.Child = new TextBlock { Text = "ACTIVIDADES — " + surface + " " + diameter + "\"", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold };
            target.Children.Add(header);

            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Height = Math.Min(220, Math.Max(60, rows.Count * 24 + 36)), RowHeight = 22, Margin = new Thickness(0, 0, 0, 4), ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "CODIGO", Binding = new System.Windows.Data.Binding(nameof(ActivityRow.Codigo)), Width = new DataGridLength(130) });
            grid.Columns.Add(new DataGridTextColumn { Header = "DESCRIPCION", Binding = new System.Windows.Data.Binding(nameof(ActivityRow.Descripcion)), Width = new DataGridLength(180) });
            grid.Columns.Add(new DataGridTextColumn { Header = "CANTIDAD", Binding = new System.Windows.Data.Binding(nameof(ActivityRow.Cantidad)), Width = new DataGridLength(110) });
            grid.Columns.Add(new DataGridTextColumn { Header = "NOTA", Binding = new System.Windows.Data.Binding(nameof(ActivityRow.Nota)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            target.Children.Add(grid);
        }

        private void AddUcSection(List<UcRow> rows)
        {
            var header = new Border { Background = new SolidColorBrush(Color.FromRgb(20, 60, 110)), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(4) };
            header.Child = new TextBlock { Text = "UNIDADES CONSTRUCTIVAS (UC)", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold };
            _content.Children.Add(header);

            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Height = Math.Min(360, Math.Max(72, rows.Count * 28 + 42)), Margin = new Thickness(0, 0, 0, 8), ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "ANILLO", Binding = new System.Windows.Data.Binding(nameof(UcRow.Ring)), Width = new DataGridLength(80) });
            grid.Columns.Add(new DataGridTextColumn { Header = "TERRENO", Binding = new System.Windows.Data.Binding(nameof(UcRow.Surface)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "DIAMETRO", Binding = new System.Windows.Data.Binding(nameof(UcRow.Diameter)), Width = new DataGridLength(120) });
            grid.Columns.Add(new DataGridTextColumn { Header = "METROS", Binding = new System.Windows.Data.Binding(nameof(UcRow.Length)), Width = new DataGridLength(100) });
            grid.Columns.Add(new DataGridTextColumn { Header = "TUBERIA (ML)", Binding = new System.Windows.Data.Binding(nameof(UcRow.Length)), Width = new DataGridLength(120) });
            _content.Children.Add(grid);
        }

        private void AddDiagnosticsSection(List<string> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                _content.Children.Add(new TextBlock { Text = "No se encontró ningún layout con nombre 'ANILLO X UC' en este dibujo.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = Brushes.DarkRed });
                return;
            }

            var box = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "Diagnóstico por layout ANILLO X UC:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            foreach (string line in diagnostics)
            {
                stack.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
            }
            box.Child = stack;
            _content.Children.Add(box);
        }

    }

    private static DebugSnapshot Scan(Database database)
    {
        var rows = new Dictionary<DebugKey, int>();
        var ucSurfaces = new Dictionary<int, HashSet<string>>();
        var ucLengths = new Dictionary<UcRowKey, double>();
        var diagnostics = new List<string>();
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

                int dimensionTotal = 0, dimensionOnUcLayer = 0, dimensionWithSurface = 0;
                foreach (ObjectId objectId in space)
                {
                    Dimension dimension = transaction.GetObject(objectId, OpenMode.ForRead) as Dimension;
                    if (dimension == null) continue;
                    dimensionTotal++;
                    string diameter = GetUcDiameter(dimension.Layer);
                    if (diameter == null) continue;
                    dimensionOnUcLayer++;
                    string surface = GetSurfaceFromXData(dimension);
                    if (string.IsNullOrWhiteSpace(surface)) continue;
                    dimensionWithSurface++;
                    if (!ucSurfaces.TryGetValue(ring, out HashSet<string> set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); ucSurfaces[ring] = set; }
                    set.Add(surface);

                    if (TryGetDisplayedDimensionValue(dimension, out double length))
                    {
                        UcRowKey key = new UcRowKey(ring, surface, diameter);
                        ucLengths.TryGetValue(key, out double current);
                        ucLengths[key] = current + Math.Abs(length);
                    }
                }

                int dimensionWithoutSurface = dimensionOnUcLayer - dimensionWithSurface;
                string detectedSurfacesText = ucSurfaces.TryGetValue(ring, out HashSet<string> detectedSurfaces) && detectedSurfaces.Count > 0
                    ? string.Join(", ", detectedSurfaces.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    : "ninguno";
                string line = "ANILLO " + ring + " UC: " + dimensionTotal + " cotas totales, " + dimensionOnUcLayer + " en capa UC_1-2/UC_3-4, " + dimensionWithSurface + " con XData UC_SURFACE válido"
                    + (dimensionWithoutSurface > 0 ? " (" + dimensionWithoutSurface + " en capa UC pero SIN XData UC_SURFACE — probablemente creadas antes de asignar terreno, o con COTAK antiguo: bórrelas y vuelva a crearlas)" : string.Empty)
                    + ". Terrenos detectados: " + detectedSurfacesText + ".";
                diagnostics.Add(line);
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

            var spiralByGroup = new Dictionary<UcGroupKey, SpiralAgg>();
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    MText mtext = transaction.GetObject(objectId, OpenMode.ForRead) as MText;
                    if (mtext == null) continue;
                    ResultBuffer xdata = mtext.XData;
                    if (xdata == null) continue;
                    TypedValue[] values = xdata.AsArray();
                    int typeIndex = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, SpiralXDataType, StringComparison.OrdinalIgnoreCase))
                        {
                            typeIndex = i;
                            break;
                        }
                    }
                    if (typeIndex < 0) continue;
                    int index = typeIndex + 1;
                    if (index + 7 >= values.Length) continue;
                    if (!TryReadXDataDouble(values[index].Value, out double pipe) ||
                        !TryReadXDataDouble(values[index + 1].Value, out double unions) ||
                        !TryReadXDataDouble(values[index + 2].Value, out double tees) ||
                        !TryReadXDataDouble(values[index + 3].Value, out double valves) ||
                        !TryReadXDataDouble(values[index + 4].Value, out double saddles)) continue;
                    string saddleDiameter = values[index + 5].Value == null ? string.Empty : values[index + 5].Value.ToString().Trim();
                    string surface = values[index + 7].Value == null ? string.Empty : values[index + 7].Value.ToString().Trim();
                    surface = NormalizeSurface(surface);
                    if (string.IsNullOrWhiteSpace(surface)) continue;

                    UcGroupKey groupKey = new UcGroupKey(surface, SpiralDiameter);
                    if (!spiralByGroup.TryGetValue(groupKey, out SpiralAgg agg)) { agg = new SpiralAgg(); spiralByGroup[groupKey] = agg; }
                    agg.Pipe += Math.Abs(pipe);
                    agg.Unions += Math.Abs(unions);
                    agg.Tees += Math.Abs(tees);
                    agg.Valves += Math.Abs(valves);
                    if (Math.Abs(saddles) > 0.0 && !string.IsNullOrWhiteSpace(saddleDiameter))
                    {
                        int saddleIndex = agg.Saddles.FindIndex(x => string.Equals(x.Diameter, saddleDiameter, StringComparison.OrdinalIgnoreCase));
                        if (saddleIndex < 0) agg.Saddles.Add((saddleDiameter, Math.Abs(saddles)));
                        else agg.Saddles[saddleIndex] = (agg.Saddles[saddleIndex].Diameter, agg.Saddles[saddleIndex].Qty + Math.Abs(saddles));
                    }
                    agg.Layouts.Add(layout.LayoutName);
                }
            }

            var activityByGroup = new Dictionary<UcGroupKey, ActivityAgg>();
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    MText mtext = transaction.GetObject(objectId, OpenMode.ForRead) as MText;
                    if (mtext == null) continue;
                    ResultBuffer xdata = mtext.XData;
                    if (xdata == null) continue;
                    TypedValue[] values = xdata.AsArray();
                    int typeIndex = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, ActivityXDataType, StringComparison.OrdinalIgnoreCase))
                        {
                            typeIndex = i;
                            break;
                        }
                    }
                    if (typeIndex < 0) continue;
                    if (typeIndex + 1 >= values.Length) continue;
                    string label = values[typeIndex + 1].Value == null ? string.Empty : values[typeIndex + 1].Value.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(label)) continue;

                    int index = typeIndex + 2;
                    while (index + 2 < values.Length)
                    {
                        string diameter = values[index].Value == null ? string.Empty : values[index].Value.ToString().Trim();
                        string surface = values[index + 1].Value == null ? string.Empty : values[index + 1].Value.ToString().Trim();
                        surface = NormalizeSurface(surface);
                        diameter = GetUcDiameterFromMaterial(diameter) ?? diameter;
                        if (!TryReadXDataDouble(values[index + 2].Value, out double quantity)) break;
                        index += 3;
                        if (string.IsNullOrWhiteSpace(surface)) continue;

                        UcGroupKey groupKey = new UcGroupKey(surface, diameter);
                        if (!activityByGroup.TryGetValue(groupKey, out ActivityAgg agg)) { agg = new ActivityAgg(); activityByGroup[groupKey] = agg; }
                        agg.Add(label, Math.Abs(quantity));
                        agg.Layouts.Add(layout.LayoutName);
                    }
                }
            }

            transaction.Commit();

            List<UcRow> ucRows = ucLengths
                .Select(x => new UcRow(x.Key.Ring, x.Key.Surface, x.Key.Diameter, x.Value))
                .OrderBy(x => x.Ring).ThenBy(x => x.Surface, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Diameter, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DebugSnapshot(ucSurfaces.Count, detailCount, ucRows, diagnostics, rows.Select(x => new DebugRow(x.Key.Surface, x.Key.Description, x.Key.Diameter, x.Value, x.Key.Source, x.Key.Layout)).ToList(), spiralByGroup, activityByGroup);
        }
    }

    private static bool TryGetDisplayedDimensionValue(Dimension dimension, out double value)
    {
        value = 0.0;
        string text = dimension.DimensionText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Match match = Regex.Match(text, @"[-+]?\d+(?:[\.,]\d+)?");
        return match.Success && double.TryParse(match.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadXDataDouble(object value, out double result)
    {
        result = 0.0;
        if (value == null) return false;
        if (value is double d) { result = d; return true; }
        if (value is float f) { result = f; return true; }
        if (value is int i) { result = i; return true; }
        if (value is short s) { result = s; return true; }
        return double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
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

    private static string GetUcDiameterFromMaterial(string diameter)
    {
        if (string.IsNullOrWhiteSpace(diameter)) return null;
        string normalized = Regex.Replace(diameter.Trim().ToUpperInvariant().Replace("\"", string.Empty), @"\s+", string.Empty);
        if (normalized == "1/2") return "1/2";
        if (normalized == "3/4" || normalized.EndsWith("X3/4", StringComparison.OrdinalIgnoreCase)) return "3/4";
        if (normalized == "3/4X1/2") return "3/4";
        return null;
    }

    private static string NormalizeSurface(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string normalized = NormalizeWhitespace(value).Trim().ToUpperInvariant();
        if (normalized.Length == 0) return string.Empty;
        normalized = RemoveAccents(normalized);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        if (normalized == "CALZADA ASFALTO") normalized = "ASFALTO";
        return normalized;
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }
        return builder.ToString();
    }

    private static string RemoveAccents(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value
            .Replace('Á', 'A').Replace('É', 'E').Replace('Í', 'I').Replace('Ó', 'O').Replace('Ú', 'U').Replace('Ü', 'U')
            .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u').Replace('ü', 'u');
    }

    private sealed record DebugSnapshot(int UcCount, int DetailCount, List<UcRow> UcRows, List<string> Diagnostics, List<DebugRow> Rows, Dictionary<UcGroupKey, SpiralAgg> SpiralByGroup, Dictionary<UcGroupKey, ActivityAgg> ActivityByGroup);
    private sealed record DebugRow(string Surface, string Description, string Diameter, int Quantity, string Source, string Layout);
    private readonly record struct DebugKey(string Surface, string Description, string Diameter, string Source, string Layout);
    private sealed record UcRow(int Ring, string Surface, string Diameter, double LengthValue)
    {
        public string Length => LengthValue.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
    }
    private readonly record struct UcRowKey(int Ring, string Surface, string Diameter);
    private readonly record struct UcGroupKey(string Surface, string Diameter);
    private sealed record UcMaterialRow(string Description, string Diameter, string Quantity, string Source, string Layout);
    private sealed record ActivityRow(string Codigo, string Descripcion, string Cantidad, string Nota);
    private sealed class SpiralAgg
    {
        public double Pipe;
        public double Unions;
        public double Tees;
        public double Valves;
        public List<(string Diameter, double Qty)> Saddles = new List<(string Diameter, double Qty)>();
        public HashSet<string> Layouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class ActivityAgg
    {
        public double Camisa;
        public double Pantalla;
        public double CruceTopo;
        public double Empedrado;
        public double VigaConcreto;
        public HashSet<string> Layouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Add(string label, double quantity)
        {
            string normalized = label.Trim().ToUpperInvariant();
            if (normalized == "CAMISA") Camisa += quantity;
            else if (normalized == "PANTALLA") Pantalla += quantity;
            else if (normalized == "CRUCE CON TOPO") CruceTopo += quantity;
            else if (normalized == "EMPEDRADO") Empedrado += quantity;
            else if (normalized == "VIGA EN CONCRETO") VigaConcreto += quantity;
        }
    }
}
