using Autodesk.AutoCAD.Runtime;
using AutoKADN.Tools.NomenclaturaVial;

namespace AutoKADN.Commands;

public class NomenclaturaVialCommand
{
    [CommandMethod("KARP_NOMVIAL", CommandFlags.Modal)]
    public void Execute()
    {
        var tool = new NomenclaturaVialTool();
        tool.Run();
    }
}
