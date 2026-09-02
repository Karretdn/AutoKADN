using System.Windows.Input;
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

        panelSource.Items.Add(CreateButton("NOMENK", "Nomenclatura", "NOMENK"));
        panelSource.Items.Add(CreateButton("LIMIK", "Límites", "LIMIK"));

        tab.Panels.Add(panel);
        ComponentManager.Ribbon.Tabs.Add(tab);
        ComponentManager.Ribbon.ActiveTab = tab;
        _tab = tab;
    }

    private static RibbonButton CreateButton(string id, string text, string command)
    {
        return new RibbonButton
        {
            Id = $"AutoKADN_{id}",
            Text = text,
            ShowText = true,
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Size = RibbonItemSize.Large,
            CommandHandler = new RibbonCommandHandler(command)
        };
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
