using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;

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

        source.Items.Add(CreateButton("NOMENK", "Nomenclatura", "NOMENK"));
        source.Items.Add(CreateButton("LIMIK", "Límites", "LIMIK"));
        source.Items.Add(CreateButton("COTAK", "Cota", "COTAK"));
        source.Items.Add(CreateButton("ANOTACIONES", "Anotaciones", "ANOTACIONES"));
        source.Items.Add(CreateButton("LISTABLOQUES", "Resumen materiales", "LISTABLOQUES"));
        source.Items.Add(CreateButton("RESUMENUC", "Resumen UC", "RESUMENUC"));

        tab.Panels.Add(panel);
        ComponentManager.Ribbon.Tabs.Add(tab);
        ComponentManager.Ribbon.ActiveTab = tab;
        _tab = tab;
    }

    private static RibbonButton CreateButton(string id, string text, string command) => new()
    {
        Id = $"AutoKADN_{id}", Text = text, ShowText = true, ShowImage = true,
        Orientation = System.Windows.Controls.Orientation.Vertical,
        Size = RibbonItemSize.Large, LargeImage = CreateIcon(),
        CommandHandler = new RibbonCommandHandler(command)
    };

    private static DrawingImage CreateIcon()
    {
        var group = new DrawingGroup();
        using DrawingContext dc = group.Open();
        var pen = new Pen(Brushes.White, 2.8);
        dc.DrawRectangle(null, pen, new System.Windows.Rect(4, 4, 32, 32));
        dc.DrawLine(pen, new System.Windows.Point(10, 13), new System.Windows.Point(30, 13));
        dc.DrawLine(pen, new System.Windows.Point(10, 20), new System.Windows.Point(30, 20));
        dc.DrawLine(pen, new System.Windows.Point(10, 27), new System.Windows.Point(30, 27));
        return new DrawingImage(group);
    }

    private sealed class RibbonCommandHandler : ICommand
    {
        private readonly string _command;
        public RibbonCommandHandler(string command) => _command = command;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            Document? document = Application.DocumentManager.MdiActiveDocument;
            if (document is null) return;
            document.SendStringToExecute($"\u0003\u0003{_command} ", true, false, false);
        }
    }
}
