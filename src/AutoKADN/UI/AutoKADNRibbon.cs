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
        if (e.Item is RibbonControl)
        {
            ComponentManager.ItemInitialized -= OnRibbonInitialized;
            Instance.CreateTab();
        }
    }

    private void CreateTab()
    {
        if (ComponentManager.Ribbon is null || _tab is not null)
            return;

        RibbonTab? existing = ComponentManager.Ribbon.Tabs
            .FirstOrDefault(tab => string.Equals(tab.Id, "AutoKADN_TAB", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _tab = existing;
            return;
        }

        var tab = new RibbonTab
        {
            Id = "AutoKADN_TAB",
            Title = "AutoKADN"
        };

        var panelSource = new RibbonPanelSource
        {
            Title = "Herramientas"
        };

        var panel = new RibbonPanel
        {
            Source = panelSource
        };

        panelSource.Items.Add(CreateButton("NOMENK", "Nomenclatura", "NOMENK", CreateNomenclaturaIcon()));
        panelSource.Items.Add(CreateButton("LIMIK", "Límites", "LIMIK", CreateLimiteIcon()));

        tab.Panels.Add(panel);
        ComponentManager.Ribbon.Tabs.Add(tab);
        ComponentManager.Ribbon.ActiveTab = tab;
        _tab = tab;
    }

    private static RibbonButton CreateButton(string id, string text, string command, ImageSource icon)
    {
        return new RibbonButton
        {
            Id = $"AutoKADN_{id}",
            Text = text,
            ShowText = true,
            ShowImage = true,
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Size = RibbonItemSize.Large,
            LargeImage = icon,
            CommandHandler = new RibbonCommandHandler(command)
        };
    }

    private static DrawingImage CreateNomenclaturaIcon()
    {
        var group = new DrawingGroup();
        using (DrawingContext dc = group.Open())
        {
            var pen = new Pen(Brushes.White, 2.2);
            dc.DrawRoundedRectangle(null, pen, new System.Windows.Rect(4, 5, 32, 28), 4, 4);
            dc.DrawLine(pen, new System.Windows.Point(9, 15), new System.Windows.Point(31, 15));
            dc.DrawLine(pen, new System.Windows.Point(9, 22), new System.Windows.Point(26, 22));
            dc.DrawEllipse(Brushes.White, null, new System.Windows.Point(30, 29), 3.2, 3.2);
        }

        return new DrawingImage(group);
    }

    private static DrawingImage CreateLimiteIcon()
    {
        var group = new DrawingGroup();
        using (DrawingContext dc = group.Open())
        {
            var pen = new Pen(Brushes.White, 2.5);
            dc.DrawLine(pen, new System.Windows.Point(5, 32), new System.Windows.Point(35, 7));
            dc.DrawLine(pen, new System.Windows.Point(7, 12), new System.Windows.Point(34, 35));
            dc.DrawEllipse(null, pen, new System.Windows.Point(20, 20), 4.5, 4.5);
            dc.DrawLine(pen, new System.Windows.Point(20, 3), new System.Windows.Point(20, 10));
            dc.DrawLine(pen, new System.Windows.Point(20, 30), new System.Windows.Point(20, 37));
        }

        return new DrawingImage(group);
    }

    private sealed class RibbonCommandHandler : ICommand
    {
        private readonly string _command;

        public RibbonCommandHandler(string command) => _command = command;

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            Document? document = Application.DocumentManager.MdiActiveDocument;
            document?.SendStringToExecute($"{_command} ", true, false, false);
        }
    }
}
