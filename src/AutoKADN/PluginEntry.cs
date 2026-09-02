using Autodesk.AutoCAD.Runtime;

namespace AutoKADN;

public class PluginEntry : IExtensionApplication
{
    public void Initialize()
    {
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nAutoKADN cargado.\n");
    }

    public void Terminate()
    {
    }
}
