using System.IO;
using System.Text;
using System.Text.Json;

namespace AutoKADN.CalcoIA;

// AutoKADN.CalcoIA.exe <comando> <carpeta del trabajo> …
//   leer <trabajo>                      lee la hoja con el motor configurado (claude-code o api) y deja plano.json
//   mcp <trabajo>                       servidor MCP (stdio) con las herramientas, para Claude Code
//   herramienta <trabajo> <nombre> [json | @archivo]   corre una herramienta a mano (pruebas)
//   clave                               guarda la clave de la API de Anthropic (cifrada para el usuario de Windows)
//   ajustes                             ventana «Ajustes de la IA» (motor, modelo, clave y parámetros del dibujo)
// El plugin escribe pedido.json en la carpeta del trabajo, corre «leer» y lee su salida: líneas «PASO …» con el avance,
// «COSTO {json}» y, al final, «LISTO» o «ERROR …». El modo (calcar, detallar o corregir) va en pedido.json.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length >= 1 && args[0] == "clave")
        {
            // Guarda la clave de la API cifrada para este usuario de Windows (se escribe por la entrada estándar).
            Console.Error.Write("Clave de la API de Anthropic: ");
            string? clave = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(clave)) { Console.Error.WriteLine("sin clave: no se guardó nada"); return 1; }
            ClaveApi.Guardar(clave);
            Console.WriteLine("clave guardada (cifrada para este usuario de Windows)");
            return 0;
        }
        if (args.Length >= 1 && args[0] == "ajustes")
        {
            // Ventana «Ajustes de la IA» (la abre el botón de la cinta).
            new System.Windows.Application().Run(new VentanaAjustes());
            return 0;
        }
        if (args.Length < 2)
        {
            Console.Error.WriteLine("uso: AutoKADN.CalcoIA.exe leer|mcp|herramienta <carpeta del trabajo> …");
            return 2;
        }

        try
        {
            Trabajo trabajo = Trabajo.Abrir(args[1]);
            switch (args[0])
            {
                case "leer":
                    return Motores.Leer(trabajo);
                case "mcp":
                    return new McpServidor(trabajo).Correr();
                case "herramienta":
                    return Herramienta(trabajo, args);
                default:
                    Console.Error.WriteLine($"comando desconocido: {args[0]}");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine("ERROR " + ex.Message);
            return 1;
        }
    }

    private static int Herramienta(Trabajo trabajo, string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("falta el nombre de la herramienta"); return 2; }
        string json = args.Length > 3 ? args[3] : "{}";
        if (json.StartsWith('@')) json = File.ReadAllText(json[1..]);
        using JsonDocument entrada = JsonDocument.Parse(json);
        ResultadoHerramienta r = new Herramientas(trabajo).Ejecutar(args[2], entrada.RootElement);
        Console.WriteLine(r.Texto);
        if (r.Png != null)
        {
            string archivo = trabajo.Archivo($"salida_{args[2]}.png");
            File.WriteAllBytes(archivo, r.Png);
            Console.WriteLine("imagen: " + archivo);
        }
        return r.Error ? 1 : 0;
    }
}
