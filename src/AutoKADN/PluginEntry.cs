using Autodesk.AutoCAD.Runtime;
using AutoKADN.UI;

namespace AutoKADN;

public class PluginEntry : IExtensionApplication
{
    public void Initialize()
    {
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nAutoKADN cargado.\n");
        AutoKADNRibbon.Initialize();
    }

    public void Terminate()
    {
    }
}
