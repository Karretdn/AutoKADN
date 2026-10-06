using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AutoKADN.Tools.Dibujo;

namespace AutoKADN.UI;

public sealed class AutoKADNRibbon
{
    private static readonly AutoKADNRibbon Instance = new();
    private RibbonTab? _tab;

    private AutoKADNRibbon() { }

    public static void Initialize()
    {
        if (ComponentManager.Ribbon is null)
        {
            ComponentManager.ItemInitialized += OnRibbonInitialized;
            return;
        }
        Instance.CreateTab();
    }

    private static void OnRibbonInitialized(object? sender, RibbonItemEventArgs e)
    {
        if (ComponentManager.Ribbon is null) return;
        ComponentManager.ItemInitialized -= OnRibbonInitialized;
        Instance.CreateTab();
    }

    private void CreateTab()
    {
        if (ComponentManager.Ribbon is null || _tab is not null) return;
        RibbonTab? existing = ComponentManager.Ribbon.Tabs.FirstOrDefault(x => string.Equals(x.Id, "AutoKADN_TAB", StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { _tab = existing; return; }

        var tab = new RibbonTab { Id = "AutoKADN_TAB", Title = "AutoKADN" };
        var source = new RibbonPanelSource { Title = "Herramientas" };
        var panel = new RibbonPanel { Source = source };

        source.Items.Add(CreateButton("NOMENK", "Nomenclatura", "NOMENK", CreateNomenclaturaIcon()));
        source.Items.Add(CreateButton("LIMIK", "Límites", "LIMIK", CreateLimiteIcon()));
        source.Items.Add(CreateButton("COTAK", "Cota", "COTAK", CreateCotaIcon()));
        source.Items.Add(CreateButton("PEGAS", "Pegas", "PEGAS", CreatePegasIcon()));
        source.Items.Add(CreateButton("ANOTACIONES", "Anotaciones", "ANOTACIONES", CreateAnotacionesIcon()));
        source.Items.Add(CreateButton("LISTABLOQUES", "Resumen materiales", "LISTABLOQUES", CreateListaBloquesIcon()));
        source.Items.Add(CreateButton("RESUMENUC", "Resumen UC", "RESUMENUC", CreateResumenUCIcon()));
        source.Items.Add(CreateButton("MATERIALPRUEBA", "Material de prueba", "MATERIALPRUEBA", CreateMaterialPruebaIcon()));
        source.Items.Add(CreateButton("CLONARUC", "Clonar a UC", "CLONARUC", CreateClonarUcIcon()));
        source.Items.Add(CreateButton("GENERAREXCEL", "Generar Excel", "GENERAREXCEL", CreateExcelIcon()));

        tab.Panels.Add(panel);

        var drawSource = new RibbonPanelSource { Title = "Dibujo" };
        var drawPanel = new RibbonPanel { Source = drawSource };
        drawSource.Items.Add(CreateButton("LINEARAPIDA", "Línea rápida", "LINEARAPIDA", CreateLineaRapidaIcon()));
        drawSource.Items.Add(CreateMultiSelectToggle());
        drawSource.Items.Add(CreateButton("CALCAR", "Importar imagen/PDF", "CALCAR", CreateCalcarIcon()));
        tab.Panels.Add(drawPanel);

        var planSource = new RibbonPanelSource { Title = "Plano" };
        var planPanel = new RibbonPanel { Source = planSource };
        planSource.Items.Add(CreateButton("RELLENARDATOS", "Rellenar datos", "RELLENARDATOS", CreateRellenarDatosIcon()));
        planSource.Items.Add(CreateButton("NUEVOANILLO", "Nuevo anillo", "NUEVOANILLO", CreateNuevoAnilloIcon()));
        planSource.Items.Add(CreateButton("TAMANOS", "Tamaños", "TAMANOS", CreateTamanosIcon()));
        planSource.Items.Add(CreateButton("RESUMENOBRA", "Resumen obra", "RESUMENOBRA", CreateResumenObraIcon()));
        tab.Panels.Add(planPanel);

        var debugSource = new RibbonPanelSource { Title = "DEBUG" };
        var debugPanel = new RibbonPanel { Source = debugSource };
        debugSource.Items.Add(CreateButton("DEBUGDETALLES", "Debug Detalles", "DEBUGDETALLES", CreateDebugIcon()));
        tab.Panels.Add(debugPanel);

        ComponentManager.Ribbon.Tabs.Add(tab);
        ComponentManager.Ribbon.ActiveTab = tab;
        _tab = tab;
    }

    private static RibbonButton CreateButton(string id, string text, string command, ImageSource icon) => new()
    {
        Id = $"AutoKADN_{id}", Text = text, ShowText = true, ShowImage = true,
        Orientation = System.Windows.Controls.Orientation.Vertical, Size = RibbonItemSize.Large,
        LargeImage = icon, CommandHandler = new RibbonCommandHandler(command)
    };

    // Interruptor persistente (como F8 con el ortogonal): mientras esté activo, LINEARAPIDA va punto a punto (un vértice por clic).
    private static RibbonToggleButton CreateMultiSelectToggle()
    {
        var toggle = new RibbonToggleButton
        {
            Id = "AutoKADN_LINEARAPIDA_MULTI", Text = "Selección múltiple", ShowText = true, ShowImage = true,
            Orientation = System.Windows.Controls.Orientation.Vertical, Size = RibbonItemSize.Large,
            LargeImage = CreateSeleccionMultipleIcon(), IsChecked = LineaRapidaSettings.SeleccionMultiple,
            ToolTip = "Línea rápida punto a punto: cada clic fija un vértice donde haces clic (respeta ortogonal y snaps) y la polilínea sigue. Enter, Espacio o clic derecho terminan."
        };
        toggle.CheckStateChanged += (_, _) => LineaRapidaSettings.SeleccionMultiple = toggle.IsChecked;
        return toggle;
    }

    private static DrawingImage CreateRellenarDatosIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(6, 4, 28, 32));
        dc.DrawLine(p, new System.Windows.Point(11, 13), new System.Windows.Point(20, 13));
        dc.DrawLine(p, new System.Windows.Point(11, 20), new System.Windows.Point(20, 20));
        dc.DrawLine(p, new System.Windows.Point(11, 27), new System.Windows.Point(17, 27));
        dc.DrawLine(p, new System.Windows.Point(22, 25), new System.Windows.Point(26, 29));
        dc.DrawLine(p, new System.Windows.Point(26, 29), new System.Windows.Point(32, 19));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateResumenObraIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(6, 4, 28, 32));
        dc.DrawLine(p, new System.Windows.Point(11, 12), new System.Windows.Point(29, 12));
        dc.DrawLine(p, new System.Windows.Point(11, 19), new System.Windows.Point(29, 19));
        dc.DrawLine(p, new System.Windows.Point(11, 26), new System.Windows.Point(22, 26));
        dc.DrawLine(p, new System.Windows.Point(25, 24), new System.Windows.Point(29, 28));
        dc.DrawLine(p, new System.Windows.Point(29, 24), new System.Windows.Point(25, 28));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateTamanosIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(5, 22, 12, 12));   // pequeño
        dc.DrawRectangle(null, p, new System.Windows.Rect(17, 6, 18, 28));   // grande
        dc.DrawLine(p, new System.Windows.Point(5, 12), new System.Windows.Point(13, 12));
        dc.DrawLine(p, new System.Windows.Point(10, 7), new System.Windows.Point(13, 12));
        dc.DrawLine(p, new System.Windows.Point(10, 17), new System.Windows.Point(13, 12));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateNuevoAnilloIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(4, 10, 22, 26));
        dc.DrawLine(p, new System.Windows.Point(10, 10), new System.Windows.Point(10, 4));
        dc.DrawLine(p, new System.Windows.Point(10, 4), new System.Windows.Point(32, 4));
        dc.DrawLine(p, new System.Windows.Point(32, 4), new System.Windows.Point(32, 26));
        dc.DrawLine(p, new System.Windows.Point(26, 26), new System.Windows.Point(32, 26));
        dc.DrawLine(p, new System.Windows.Point(15, 17), new System.Windows.Point(15, 29));
        dc.DrawLine(p, new System.Windows.Point(9, 23), new System.Windows.Point(21, 23));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateLineaRapidaIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(4, 4, 32, 32));
        dc.DrawLine(p, new System.Windows.Point(10, 20), new System.Windows.Point(36, 20));
        dc.DrawLine(p, new System.Windows.Point(30, 15), new System.Windows.Point(36, 20));
        dc.DrawLine(p, new System.Windows.Point(30, 25), new System.Windows.Point(36, 20));
        dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(10, 20), 3.0, 3.0);
        return new DrawingImage(g);
    }

    private static DrawingImage CreateCalcarIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(4, 6, 32, 28));                                   // la imagen
        dc.DrawLine(p, new System.Windows.Point(4, 29), new System.Windows.Point(14, 19));                  // montaña
        dc.DrawLine(p, new System.Windows.Point(14, 19), new System.Windows.Point(21, 26));
        dc.DrawLine(p, new System.Windows.Point(21, 26), new System.Windows.Point(27, 20));
        dc.DrawLine(p, new System.Windows.Point(27, 20), new System.Windows.Point(36, 29));
        dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(28, 13), 2.8, 2.8);                    // sol
        return new DrawingImage(g);
    }

    private static DrawingImage CreateSeleccionMultipleIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawLine(p, new System.Windows.Point(5, 34), new System.Windows.Point(5, 22));
        dc.DrawLine(p, new System.Windows.Point(5, 22), new System.Windows.Point(18, 22));
        dc.DrawLine(p, new System.Windows.Point(18, 22), new System.Windows.Point(18, 9));
        dc.DrawLine(p, new System.Windows.Point(18, 9), new System.Windows.Point(35, 9));
        dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(5, 22), 2.8, 2.8);
        dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(18, 22), 2.8, 2.8);
        dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(18, 9), 2.8, 2.8);
        return new DrawingImage(g);
    }

    private static DrawingImage CreateDebugIcon()
    {
        var group = new DrawingGroup();
        using DrawingContext dc = group.Open();
        var pen = new Pen(Brushes.White, 2.6);
        dc.DrawEllipse(null, pen, new System.Windows.Point(18, 18), 11, 11);
        dc.DrawEllipse(null, pen, new System.Windows.Point(18, 18), 4, 4);
        dc.DrawLine(pen, new System.Windows.Point(27, 27), new System.Windows.Point(36, 36));
        dc.DrawLine(pen, new System.Windows.Point(18, 7), new System.Windows.Point(18, 2));
        dc.DrawLine(pen, new System.Windows.Point(18, 34), new System.Windows.Point(18, 39));
        return new DrawingImage(group);
    }

    private static DrawingImage CreateClonarUcIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(3, 8, 13, 24));
        dc.DrawRectangle(null, p, new System.Windows.Rect(24, 8, 13, 24));
        dc.DrawLine(p, new System.Windows.Point(18, 20), new System.Windows.Point(22, 20));
        dc.DrawLine(p, new System.Windows.Point(19.5, 17), new System.Windows.Point(22, 20));
        dc.DrawLine(p, new System.Windows.Point(19.5, 23), new System.Windows.Point(22, 20));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateExcelIcon()
    {
        var group = new DrawingGroup(); using DrawingContext dc = group.Open(); var pen = new Pen(Brushes.White, 2.6);
        dc.DrawRectangle(null, pen, new System.Windows.Rect(6, 4, 28, 32));
        dc.DrawLine(pen, new System.Windows.Point(12, 14), new System.Windows.Point(28, 14)); dc.DrawLine(pen, new System.Windows.Point(12, 21), new System.Windows.Point(28, 21)); dc.DrawLine(pen, new System.Windows.Point(12, 28), new System.Windows.Point(28, 28)); dc.DrawLine(pen, new System.Windows.Point(20, 8), new System.Windows.Point(20, 33));
        return new DrawingImage(group);
    }

    private static DrawingImage CreateMaterialPruebaIcon() { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6); dc.DrawRectangle(null, p, new System.Windows.Rect(4, 7, 32, 27)); dc.DrawLine(p, new System.Windows.Point(8, 15), new System.Windows.Point(32, 15)); dc.DrawLine(p, new System.Windows.Point(8, 22), new System.Windows.Point(27, 22)); dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(30, 28), 3.2, 3.2); return new DrawingImage(g); }
    private static DrawingImage CreateNomenclaturaIcon() { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.8); dc.DrawRoundedRectangle(null, p, new System.Windows.Rect(3, 4, 34, 30), 4.5, 4.5); dc.DrawLine(p, new System.Windows.Point(8, 14), new System.Windows.Point(32, 14)); dc.DrawLine(p, new System.Windows.Point(8, 21), new System.Windows.Point(28, 21)); dc.DrawLine(p, new System.Windows.Point(8, 28), new System.Windows.Point(23, 28)); dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(30.5, 29), 3.4, 3.4); return new DrawingImage(g); }
    private static DrawingImage CreateLimiteIcon() { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.9); dc.DrawLine(p, new System.Windows.Point(4, 34), new System.Windows.Point(36, 6)); dc.DrawLine(p, new System.Windows.Point(6, 10), new System.Windows.Point(34, 36)); dc.DrawEllipse(null, p, new System.Windows.Point(20, 20), 4.8, 4.8); dc.DrawLine(p, new System.Windows.Point(20, 2), new System.Windows.Point(20, 9)); dc.DrawLine(p, new System.Windows.Point(20, 31), new System.Windows.Point(20, 38)); return new DrawingImage(g); }
    private static DrawingImage CreatePegasIcon()
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6);
        dc.DrawLine(p, new System.Windows.Point(4, 20), new System.Windows.Point(36, 20));
        dc.DrawLine(p, new System.Windows.Point(4, 15), new System.Windows.Point(4, 25));
        dc.DrawLine(p, new System.Windows.Point(13.3, 15), new System.Windows.Point(13.3, 25));
        dc.DrawLine(p, new System.Windows.Point(22.6, 15), new System.Windows.Point(22.6, 25));
        dc.DrawLine(p, new System.Windows.Point(31.9, 15), new System.Windows.Point(31.9, 25));
        dc.DrawLine(p, new System.Windows.Point(36, 15), new System.Windows.Point(36, 25));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateCotaIcon() { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.8); dc.DrawLine(p, new System.Windows.Point(5, 8), new System.Windows.Point(35, 8)); dc.DrawLine(p, new System.Windows.Point(5, 32), new System.Windows.Point(35, 32)); dc.DrawLine(p, new System.Windows.Point(5, 5), new System.Windows.Point(5, 35)); dc.DrawLine(p, new System.Windows.Point(35, 5), new System.Windows.Point(35, 35)); dc.DrawLine(p, new System.Windows.Point(9, 20), new System.Windows.Point(31, 20)); dc.DrawLine(p, new System.Windows.Point(9, 20), new System.Windows.Point(15, 16)); dc.DrawLine(p, new System.Windows.Point(9, 20), new System.Windows.Point(15, 24)); dc.DrawLine(p, new System.Windows.Point(31, 20), new System.Windows.Point(25, 16)); dc.DrawLine(p, new System.Windows.Point(31, 20), new System.Windows.Point(25, 24)); return new DrawingImage(g); }
    private static DrawingImage CreateAnotacionesIcon() { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.8); dc.DrawLine(p, new System.Windows.Point(4, 35), new System.Windows.Point(36, 5)); dc.DrawLine(p, new System.Windows.Point(7, 9), new System.Windows.Point(33, 9)); dc.DrawLine(p, new System.Windows.Point(7, 16), new System.Windows.Point(29, 16)); dc.DrawLine(p, new System.Windows.Point(7, 23), new System.Windows.Point(26, 23)); dc.DrawLine(p, new System.Windows.Point(7, 30), new System.Windows.Point(23, 30)); return new DrawingImage(g); }
    private static DrawingImage CreateListaBloquesIcon() { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.8); dc.DrawRectangle(null, p, new System.Windows.Rect(5, 4, 30, 32)); dc.DrawLine(p, new System.Windows.Point(11, 12), new System.Windows.Point(29, 12)); dc.DrawLine(p, new System.Windows.Point(11, 20), new System.Windows.Point(29, 20)); dc.DrawLine(p, new System.Windows.Point(11, 28), new System.Windows.Point(29, 28)); dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(7, 12), 1.6, 1.6); dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(7, 20), 1.6, 1.6); dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(7, 28), 1.6, 1.6); return new DrawingImage(g); }
    private static DrawingImage CreateResumenUCIcon() { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(Brushes.White, 2.6); dc.DrawLine(p, new System.Windows.Point(4, 29), new System.Windows.Point(36, 29)); dc.DrawLine(p, new System.Windows.Point(8, 29), new System.Windows.Point(14, 20)); dc.DrawLine(p, new System.Windows.Point(14, 20), new System.Windows.Point(20, 29)); dc.DrawLine(p, new System.Windows.Point(20, 29), new System.Windows.Point(26, 20)); dc.DrawLine(p, new System.Windows.Point(26, 20), new System.Windows.Point(32, 29)); dc.DrawLine(p, new System.Windows.Point(8, 13), new System.Windows.Point(32, 13)); dc.DrawLine(p, new System.Windows.Point(8, 13), new System.Windows.Point(12, 10)); dc.DrawLine(p, new System.Windows.Point(8, 13), new System.Windows.Point(12, 16)); dc.DrawLine(p, new System.Windows.Point(32, 13), new System.Windows.Point(28, 10)); dc.DrawLine(p, new System.Windows.Point(32, 13), new System.Windows.Point(28, 16)); return new DrawingImage(g); }

    private sealed class RibbonCommandHandler : ICommand
    {
        private readonly string _command; public RibbonCommandHandler(string command) => _command = command;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            Document? document = Application.DocumentManager.MdiActiveDocument; if (document is null) return;
            document.SendStringToExecute($"\u0003\u0003{_command} ", true, false, false);
        }
    }
}
