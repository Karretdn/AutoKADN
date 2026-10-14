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

        // Las secciones van en el orden del trabajo: calcar, dibujar, anotar, armar los anillos, llenar los cuadros del
        // plano y sacar los Excel. Cada una tiene su color en los íconos y en la barra del título.
        Brush calco = Pincel(ColorCalco);
        RibbonPanelSource calcoPanel = AgregarPanel(tab, "CALCO", "Calco", ColorCalco);
        // Un solo panel para importar la hoja y calcarla (a mano o con IA); CALCAR y CALCOIA siguen como comandos.
        calcoPanel.Items.Add(CreateButton("CALCO", "Importar y calcar", "CALCO", CreateCalcarIcon(calco)));
        calcoPanel.Items.Add(CreateButton("CORREGIRIA", "Corregir con IA", "CORREGIRIA", CreateCorregirIaIcon(calco)));
        calcoPanel.Items.Add(CreateButton("DETALLARIA", "Detallar con IA", "DETALLARIA", CreateDetallarIcon(calco)));
        calcoPanel.Items.Add(CreateButton("AJUSTESIA", "Ajustes IA", "AJUSTESIA", CreateAjustesIcon(calco)));

        Brush dibujo = Pincel(ColorDibujo);
        RibbonPanelSource dibujoPanel = AgregarPanel(tab, "DIBUJO", "Dibujo", ColorDibujo);
        dibujoPanel.Items.Add(CreateButton("LINEARAPIDA", "Línea rápida", "LINEARAPIDA", CreateLineaRapidaIcon(dibujo)));
        dibujoPanel.Items.Add(CreateMultiSelectToggle(dibujo));

        Brush anotacion = Pincel(ColorAnotacion);
        RibbonPanelSource anotacionPanel = AgregarPanel(tab, "ANOTACION", "Anotación", ColorAnotacion);
        anotacionPanel.Items.Add(CreateButton("NOMENK", "Nomenclatura", "NOMENK", CreateNomenclaturaIcon(anotacion)));
        anotacionPanel.Items.Add(CreateButton("LIMIK", "Límites", "LIMIK", CreateLimiteIcon(anotacion)));
        anotacionPanel.Items.Add(CreateButton("COTAK", "Cota", "COTAK", CreateCotaIcon(anotacion)));
        anotacionPanel.Items.Add(CreateButton("PEGAS", "Pegas", "PEGAS", CreatePegasIcon(anotacion)));
        anotacionPanel.Items.Add(CreateButton("ANOTACIONES", "Anotaciones", "ANOTACIONES", CreateAnotacionesIcon(anotacion)));

        Brush anillos = Pincel(ColorAnillos);
        RibbonPanelSource anillosPanel = AgregarPanel(tab, "ANILLOS", "Anillos", ColorAnillos);
        anillosPanel.Items.Add(CreateButton("NUEVOANILLO", "Nuevo anillo", "NUEVOANILLO", CreateNuevoAnilloIcon(anillos)));
        anillosPanel.Items.Add(CreateButton("RECORTEANILLOS", "Recorte de anillos", "RECORTEANILLOS", CreateRecorteAnillosIcon(anillos)));
        anillosPanel.Items.Add(CreateButton("CLONARUC", "Clonar a UC", "CLONARUC", CreateClonarUcIcon(anillos)));
        anillosPanel.Items.Add(CreateButton("RELLENARDATOS", "Rellenar datos", "RELLENARDATOS", CreateRellenarDatosIcon(anillos)));
        anillosPanel.Items.Add(CreateButton("TAMANOS", "Tamaños", "TAMANOS", CreateTamanosIcon(anillos)));

        Brush cuadros = Pincel(ColorCuadros);
        RibbonPanelSource cuadrosPanel = AgregarPanel(tab, "CUADROS", "Cuadros", ColorCuadros);
        cuadrosPanel.Items.Add(CreateButton("LISTABLOQUES", "Resumen materiales", "LISTABLOQUES", CreateListaBloquesIcon(cuadros)));
        cuadrosPanel.Items.Add(CreateButton("RESUMENUC", "Resumen UC", "RESUMENUC", CreateResumenUCIcon(cuadros)));
        cuadrosPanel.Items.Add(CreateButton("MATERIALPRUEBA", "Material de prueba", "MATERIALPRUEBA", CreateMaterialPruebaIcon(cuadros)));

        Brush excel = Pincel(ColorExcel);
        RibbonPanelSource excelPanel = AgregarPanel(tab, "EXCEL", "Excel", ColorExcel);
        excelPanel.Items.Add(CreateButton("GENERAREXCEL", "Generar Excel", "GENERAREXCEL", CreateExcelIcon(excel)));
        excelPanel.Items.Add(CreateButton("RESUMENOBRA", "Resumen obra", "RESUMENOBRA", CreateResumenObraIcon(excel)));
        excelPanel.Items.Add(CreateButton("RESUMENPROYECTO", "Resumen proyecto", "RESUMENPROYECTO", CreateResumenProyectoIcon(excel)));

        RibbonPanelSource debugPanel = AgregarPanel(tab, "DEBUG", "Debug", ColorDebug);
        debugPanel.Items.Add(CreateButton("DEBUGDETALLES", "Debug Detalles", "DEBUGDETALLES", CreateDebugIcon(Pincel(ColorDebug))));

        ComponentManager.Ribbon.Tabs.Add(tab);
        ComponentManager.Ribbon.ActiveTab = tab;
        _tab = tab;
    }

    private static readonly Color ColorCalco = Color.FromRgb(0xB3, 0x88, 0xFF);      // violeta
    private static readonly Color ColorDibujo = Color.FromRgb(0x64, 0xB5, 0xF6);     // azul
    private static readonly Color ColorAnotacion = Color.FromRgb(0xFF, 0xC9, 0x4D);  // ámbar
    private static readonly Color ColorAnillos = Color.FromRgb(0x4D, 0xD6, 0xA8);    // verde agua
    private static readonly Color ColorCuadros = Color.FromRgb(0xFF, 0x8A, 0x65);    // coral
    private static readonly Color ColorExcel = Color.FromRgb(0x8B, 0xC3, 0x4A);      // verde
    private static readonly Color ColorDebug = Color.FromRgb(0x9E, 0x9E, 0x9E);      // gris

    private static Brush Pincel(Color color, byte alfa = 0xFF)
    {
        var pincel = new SolidColorBrush(Color.FromArgb(alfa, color.R, color.G, color.B));
        pincel.Freeze();
        return pincel;
    }

    // Panel con la barra del título teñida del color de la sección (translúcida, para que el título se lea en el tema
    // claro y en el oscuro).
    private static RibbonPanelSource AgregarPanel(RibbonTab tab, string id, string titulo, Color color)
    {
        var source = new RibbonPanelSource { Id = $"AutoKADN_PANEL_{id}", Title = titulo };
        tab.Panels.Add(new RibbonPanel { Source = source, CustomPanelTitleBarBackground = Pincel(color, 0x80) });
        return source;
    }

    private static RibbonButton CreateButton(string id, string text, string command, ImageSource icon) => new()
    {
        Id = $"AutoKADN_{id}", Text = text, ShowText = true, ShowImage = true,
        Orientation = System.Windows.Controls.Orientation.Vertical, Size = RibbonItemSize.Large,
        LargeImage = icon, CommandHandler = new RibbonCommandHandler(command)
    };

    // Interruptor persistente (como F8 con el ortogonal): mientras esté activo, LINEARAPIDA va punto a punto (un vértice por clic).
    private static RibbonToggleButton CreateMultiSelectToggle(Brush color)
    {
        var toggle = new RibbonToggleButton
        {
            Id = "AutoKADN_LINEARAPIDA_MULTI", Text = "Selección múltiple", ShowText = true, ShowImage = true,
            Orientation = System.Windows.Controls.Orientation.Vertical, Size = RibbonItemSize.Large,
            LargeImage = CreateSeleccionMultipleIcon(color), IsChecked = LineaRapidaSettings.SeleccionMultiple,
            ToolTip = "Línea rápida punto a punto: cada clic fija un vértice donde haces clic (respeta ortogonal y snaps) y la polilínea sigue. Enter, Espacio o clic derecho terminan."
        };
        toggle.CheckStateChanged += (_, _) => LineaRapidaSettings.SeleccionMultiple = toggle.IsChecked;
        return toggle;
    }

    private static DrawingImage CreateRellenarDatosIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(6, 4, 28, 32));
        dc.DrawLine(p, new System.Windows.Point(11, 13), new System.Windows.Point(20, 13));
        dc.DrawLine(p, new System.Windows.Point(11, 20), new System.Windows.Point(20, 20));
        dc.DrawLine(p, new System.Windows.Point(11, 27), new System.Windows.Point(17, 27));
        dc.DrawLine(p, new System.Windows.Point(22, 25), new System.Windows.Point(26, 29));
        dc.DrawLine(p, new System.Windows.Point(26, 29), new System.Windows.Point(32, 19));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateResumenObraIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(6, 4, 28, 32));
        dc.DrawLine(p, new System.Windows.Point(11, 12), new System.Windows.Point(29, 12));
        dc.DrawLine(p, new System.Windows.Point(11, 19), new System.Windows.Point(29, 19));
        dc.DrawLine(p, new System.Windows.Point(11, 26), new System.Windows.Point(22, 26));
        dc.DrawLine(p, new System.Windows.Point(25, 24), new System.Windows.Point(29, 28));
        dc.DrawLine(p, new System.Windows.Point(29, 24), new System.Windows.Point(25, 28));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateResumenProyectoIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(5, 4, 30, 32));
        dc.DrawLine(p, new System.Windows.Point(5, 13), new System.Windows.Point(35, 13));
        dc.DrawLine(p, new System.Windows.Point(15, 13), new System.Windows.Point(15, 36));
        dc.DrawLine(p, new System.Windows.Point(25, 13), new System.Windows.Point(25, 36));
        dc.DrawLine(p, new System.Windows.Point(5, 22), new System.Windows.Point(35, 22));
        dc.DrawLine(p, new System.Windows.Point(5, 29), new System.Windows.Point(35, 29));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateTamanosIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(5, 22, 12, 12));   // pequeño
        dc.DrawRectangle(null, p, new System.Windows.Rect(17, 6, 18, 28));   // grande
        dc.DrawLine(p, new System.Windows.Point(5, 12), new System.Windows.Point(13, 12));
        dc.DrawLine(p, new System.Windows.Point(10, 7), new System.Windows.Point(13, 12));
        dc.DrawLine(p, new System.Windows.Point(10, 17), new System.Windows.Point(13, 12));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateNuevoAnilloIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(4, 10, 22, 26));
        dc.DrawLine(p, new System.Windows.Point(10, 10), new System.Windows.Point(10, 4));
        dc.DrawLine(p, new System.Windows.Point(10, 4), new System.Windows.Point(32, 4));
        dc.DrawLine(p, new System.Windows.Point(32, 4), new System.Windows.Point(32, 26));
        dc.DrawLine(p, new System.Windows.Point(26, 26), new System.Windows.Point(32, 26));
        dc.DrawLine(p, new System.Windows.Point(15, 17), new System.Windows.Point(15, 29));
        dc.DrawLine(p, new System.Windows.Point(9, 23), new System.Windows.Point(21, 23));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateRecorteAnillosIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(3, 4, 34, 32));                                    // el plano general
        dc.DrawRectangle(null, p, new System.Windows.Rect(14, 13, 20, 14));                                  // el recorte (proporción del marco)
        dc.DrawLine(p, new System.Windows.Point(14, 9), new System.Windows.Point(14, 13));                   // esquinas de corte
        dc.DrawLine(p, new System.Windows.Point(10, 13), new System.Windows.Point(14, 13));
        dc.DrawLine(p, new System.Windows.Point(34, 31), new System.Windows.Point(34, 27));
        dc.DrawLine(p, new System.Windows.Point(34, 31), new System.Windows.Point(38, 31));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateLineaRapidaIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(4, 4, 32, 32));
        dc.DrawLine(p, new System.Windows.Point(10, 20), new System.Windows.Point(36, 20));
        dc.DrawLine(p, new System.Windows.Point(30, 15), new System.Windows.Point(36, 20));
        dc.DrawLine(p, new System.Windows.Point(30, 25), new System.Windows.Point(36, 20));
        dc.DrawEllipse(c, null, new System.Windows.Point(10, 20), 3.0, 3.0);
        return new DrawingImage(g);
    }

    private static DrawingImage CreateCalcarIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(4, 6, 32, 28));                                   // la imagen
        dc.DrawLine(p, new System.Windows.Point(4, 29), new System.Windows.Point(14, 19));                  // montaña
        dc.DrawLine(p, new System.Windows.Point(14, 19), new System.Windows.Point(21, 26));
        dc.DrawLine(p, new System.Windows.Point(21, 26), new System.Windows.Point(27, 20));
        dc.DrawLine(p, new System.Windows.Point(27, 20), new System.Windows.Point(36, 29));
        dc.DrawEllipse(c, null, new System.Windows.Point(28, 13), 2.8, 2.8);                    // sol
        return new DrawingImage(g);
    }

    // «Corregir con IA»: la zona señalada (rectángulo punteado) y un lápiz.
    private static DrawingImage CreateCorregirIaIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6); var punteada = new Pen(c, 1.8) { DashStyle = new DashStyle(new[] { 2.0, 1.5 }, 0) };
        dc.DrawRectangle(null, punteada, new System.Windows.Rect(4, 4, 24, 24));                           // la zona
        dc.DrawLine(new Pen(c, 5), new System.Windows.Point(17, 34), new System.Windows.Point(33, 18));     // el lápiz
        dc.DrawLine(p, new System.Windows.Point(14, 37), new System.Windows.Point(17, 34));                  // su punta
        return new DrawingImage(g);
    }

    // «Detallar con IA»: el anillo en U con una cota encima.
    private static DrawingImage CreateDetallarIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6); var fina = new Pen(c, 1.6);
        dc.DrawLine(p, new System.Windows.Point(9, 14), new System.Windows.Point(9, 34));                    // la U de tubería
        dc.DrawLine(p, new System.Windows.Point(9, 34), new System.Windows.Point(31, 34));
        dc.DrawLine(p, new System.Windows.Point(31, 34), new System.Windows.Point(31, 14));
        dc.DrawLine(fina, new System.Windows.Point(9, 6), new System.Windows.Point(31, 6));                  // la cota
        dc.DrawLine(fina, new System.Windows.Point(9, 3), new System.Windows.Point(9, 10));
        dc.DrawLine(fina, new System.Windows.Point(31, 3), new System.Windows.Point(31, 10));
        dc.DrawLine(fina, new System.Windows.Point(14, 21), new System.Windows.Point(26, 21));               // medida de ubicación
        dc.DrawEllipse(c, null, new System.Windows.Point(20, 27), 2.2, 2.2);
        return new DrawingImage(g);
    }

    // «Ajustes IA»: un engranaje.
    private static DrawingImage CreateAjustesIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawEllipse(null, p, new System.Windows.Point(20, 20), 9, 9);
        dc.DrawEllipse(null, p, new System.Windows.Point(20, 20), 3.5, 3.5);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            dc.DrawLine(new Pen(c, 4), new System.Windows.Point(20 + Math.Cos(a) * 10, 20 + Math.Sin(a) * 10), new System.Windows.Point(20 + Math.Cos(a) * 15, 20 + Math.Sin(a) * 15));
        }
        return new DrawingImage(g);
    }

    private static DrawingImage CreateSeleccionMultipleIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawLine(p, new System.Windows.Point(5, 34), new System.Windows.Point(5, 22));
        dc.DrawLine(p, new System.Windows.Point(5, 22), new System.Windows.Point(18, 22));
        dc.DrawLine(p, new System.Windows.Point(18, 22), new System.Windows.Point(18, 9));
        dc.DrawLine(p, new System.Windows.Point(18, 9), new System.Windows.Point(35, 9));
        dc.DrawEllipse(c, null, new System.Windows.Point(5, 22), 2.8, 2.8);
        dc.DrawEllipse(c, null, new System.Windows.Point(18, 22), 2.8, 2.8);
        dc.DrawEllipse(c, null, new System.Windows.Point(18, 9), 2.8, 2.8);
        return new DrawingImage(g);
    }

    private static DrawingImage CreateDebugIcon(Brush c)
    {
        var group = new DrawingGroup();
        using DrawingContext dc = group.Open();
        var pen = new Pen(c, 2.6);
        dc.DrawEllipse(null, pen, new System.Windows.Point(18, 18), 11, 11);
        dc.DrawEllipse(null, pen, new System.Windows.Point(18, 18), 4, 4);
        dc.DrawLine(pen, new System.Windows.Point(27, 27), new System.Windows.Point(36, 36));
        dc.DrawLine(pen, new System.Windows.Point(18, 7), new System.Windows.Point(18, 2));
        dc.DrawLine(pen, new System.Windows.Point(18, 34), new System.Windows.Point(18, 39));
        return new DrawingImage(group);
    }

    private static DrawingImage CreateClonarUcIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawRectangle(null, p, new System.Windows.Rect(3, 8, 13, 24));
        dc.DrawRectangle(null, p, new System.Windows.Rect(24, 8, 13, 24));
        dc.DrawLine(p, new System.Windows.Point(18, 20), new System.Windows.Point(22, 20));
        dc.DrawLine(p, new System.Windows.Point(19.5, 17), new System.Windows.Point(22, 20));
        dc.DrawLine(p, new System.Windows.Point(19.5, 23), new System.Windows.Point(22, 20));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateExcelIcon(Brush c)
    {
        var group = new DrawingGroup(); using DrawingContext dc = group.Open(); var pen = new Pen(c, 2.6);
        dc.DrawRectangle(null, pen, new System.Windows.Rect(6, 4, 28, 32));
        dc.DrawLine(pen, new System.Windows.Point(12, 14), new System.Windows.Point(28, 14)); dc.DrawLine(pen, new System.Windows.Point(12, 21), new System.Windows.Point(28, 21)); dc.DrawLine(pen, new System.Windows.Point(12, 28), new System.Windows.Point(28, 28)); dc.DrawLine(pen, new System.Windows.Point(20, 8), new System.Windows.Point(20, 33));
        return new DrawingImage(group);
    }

    private static DrawingImage CreateMaterialPruebaIcon(Brush c) { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6); dc.DrawRectangle(null, p, new System.Windows.Rect(4, 7, 32, 27)); dc.DrawLine(p, new System.Windows.Point(8, 15), new System.Windows.Point(32, 15)); dc.DrawLine(p, new System.Windows.Point(8, 22), new System.Windows.Point(27, 22)); dc.DrawEllipse(c, null, new System.Windows.Point(30, 28), 3.2, 3.2); return new DrawingImage(g); }
    private static DrawingImage CreateNomenclaturaIcon(Brush c) { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.8); dc.DrawRoundedRectangle(null, p, new System.Windows.Rect(3, 4, 34, 30), 4.5, 4.5); dc.DrawLine(p, new System.Windows.Point(8, 14), new System.Windows.Point(32, 14)); dc.DrawLine(p, new System.Windows.Point(8, 21), new System.Windows.Point(28, 21)); dc.DrawLine(p, new System.Windows.Point(8, 28), new System.Windows.Point(23, 28)); dc.DrawEllipse(c, null, new System.Windows.Point(30.5, 29), 3.4, 3.4); return new DrawingImage(g); }
    private static DrawingImage CreateLimiteIcon(Brush c) { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.9); dc.DrawLine(p, new System.Windows.Point(4, 34), new System.Windows.Point(36, 6)); dc.DrawLine(p, new System.Windows.Point(6, 10), new System.Windows.Point(34, 36)); dc.DrawEllipse(null, p, new System.Windows.Point(20, 20), 4.8, 4.8); dc.DrawLine(p, new System.Windows.Point(20, 2), new System.Windows.Point(20, 9)); dc.DrawLine(p, new System.Windows.Point(20, 31), new System.Windows.Point(20, 38)); return new DrawingImage(g); }
    private static DrawingImage CreatePegasIcon(Brush c)
    {
        var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6);
        dc.DrawLine(p, new System.Windows.Point(4, 20), new System.Windows.Point(36, 20));
        dc.DrawLine(p, new System.Windows.Point(4, 15), new System.Windows.Point(4, 25));
        dc.DrawLine(p, new System.Windows.Point(13.3, 15), new System.Windows.Point(13.3, 25));
        dc.DrawLine(p, new System.Windows.Point(22.6, 15), new System.Windows.Point(22.6, 25));
        dc.DrawLine(p, new System.Windows.Point(31.9, 15), new System.Windows.Point(31.9, 25));
        dc.DrawLine(p, new System.Windows.Point(36, 15), new System.Windows.Point(36, 25));
        return new DrawingImage(g);
    }

    private static DrawingImage CreateCotaIcon(Brush c) { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.8); dc.DrawLine(p, new System.Windows.Point(5, 8), new System.Windows.Point(35, 8)); dc.DrawLine(p, new System.Windows.Point(5, 32), new System.Windows.Point(35, 32)); dc.DrawLine(p, new System.Windows.Point(5, 5), new System.Windows.Point(5, 35)); dc.DrawLine(p, new System.Windows.Point(35, 5), new System.Windows.Point(35, 35)); dc.DrawLine(p, new System.Windows.Point(9, 20), new System.Windows.Point(31, 20)); dc.DrawLine(p, new System.Windows.Point(9, 20), new System.Windows.Point(15, 16)); dc.DrawLine(p, new System.Windows.Point(9, 20), new System.Windows.Point(15, 24)); dc.DrawLine(p, new System.Windows.Point(31, 20), new System.Windows.Point(25, 16)); dc.DrawLine(p, new System.Windows.Point(31, 20), new System.Windows.Point(25, 24)); return new DrawingImage(g); }
    private static DrawingImage CreateAnotacionesIcon(Brush c) { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.8); dc.DrawLine(p, new System.Windows.Point(4, 35), new System.Windows.Point(36, 5)); dc.DrawLine(p, new System.Windows.Point(7, 9), new System.Windows.Point(33, 9)); dc.DrawLine(p, new System.Windows.Point(7, 16), new System.Windows.Point(29, 16)); dc.DrawLine(p, new System.Windows.Point(7, 23), new System.Windows.Point(26, 23)); dc.DrawLine(p, new System.Windows.Point(7, 30), new System.Windows.Point(23, 30)); return new DrawingImage(g); }
    private static DrawingImage CreateListaBloquesIcon(Brush c) { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.8); dc.DrawRectangle(null, p, new System.Windows.Rect(5, 4, 30, 32)); dc.DrawLine(p, new System.Windows.Point(11, 12), new System.Windows.Point(29, 12)); dc.DrawLine(p, new System.Windows.Point(11, 20), new System.Windows.Point(29, 20)); dc.DrawLine(p, new System.Windows.Point(11, 28), new System.Windows.Point(29, 28)); dc.DrawEllipse(c, null, new System.Windows.Point(7, 12), 1.6, 1.6); dc.DrawEllipse(c, null, new System.Windows.Point(7, 20), 1.6, 1.6); dc.DrawEllipse(c, null, new System.Windows.Point(7, 28), 1.6, 1.6); return new DrawingImage(g); }
    private static DrawingImage CreateResumenUCIcon(Brush c) { var g = new DrawingGroup(); using DrawingContext dc = g.Open(); var p = new Pen(c, 2.6); dc.DrawLine(p, new System.Windows.Point(4, 29), new System.Windows.Point(36, 29)); dc.DrawLine(p, new System.Windows.Point(8, 29), new System.Windows.Point(14, 20)); dc.DrawLine(p, new System.Windows.Point(14, 20), new System.Windows.Point(20, 29)); dc.DrawLine(p, new System.Windows.Point(20, 29), new System.Windows.Point(26, 20)); dc.DrawLine(p, new System.Windows.Point(26, 20), new System.Windows.Point(32, 29)); dc.DrawLine(p, new System.Windows.Point(8, 13), new System.Windows.Point(32, 13)); dc.DrawLine(p, new System.Windows.Point(8, 13), new System.Windows.Point(12, 10)); dc.DrawLine(p, new System.Windows.Point(8, 13), new System.Windows.Point(12, 16)); dc.DrawLine(p, new System.Windows.Point(32, 13), new System.Windows.Point(28, 10)); dc.DrawLine(p, new System.Windows.Point(32, 13), new System.Windows.Point(28, 16)); return new DrawingImage(g); }

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
