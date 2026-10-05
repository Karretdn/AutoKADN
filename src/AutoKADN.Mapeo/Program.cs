using System.IO;
using System.Text;
using AutoKADN.Mapeo.Detection;
using AutoKADN.Mapeo.Model;

// Reconocimiento automático de celdas en formatos PDF: genera un <código>.mapa.json por cada PDF.
// Es solo el primer borrador; la revisión y corrección se hace en la app web (carpeta web).
//
//   AutoKADN.Mapeo <pdf | carpeta> [más PDF...] [--salida <carpeta>]
Console.OutputEncoding = new UTF8Encoding(false);

string? outputDir = null;
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--salida" or "-o" && i + 1 < args.Length) outputDir = args[++i];
    else inputs.Add(args[i]);
}
if (inputs.Count == 0)
{
    Console.WriteLine("Uso: AutoKADN.Mapeo <pdf | carpeta> [más PDF...] [--salida <carpeta>]");
    return 1;
}

var pdfs = new List<string>();
foreach (string input in inputs)
{
    if (Directory.Exists(input)) pdfs.AddRange(Directory.GetFiles(input, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    else if (File.Exists(input)) pdfs.Add(input);
    else Console.WriteLine($"No existe: {input}");
}

int failures = 0;
foreach (string pdf in pdfs)
{
    try
    {
        MapaFormato map = await FormDetector.DetectAsync(pdf);
        string folder = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(pdf))!;
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, map.Codigo + ".mapa.json");
        File.WriteAllText(target, map.ToJson(), new UTF8Encoding(false));
        int cells = map.Campos.Count + map.Tablas.Sum(t => t.Filas.Count * t.Columnas.Count);
        Console.WriteLine($"{map.Codigo}: {map.Campos.Count} campos, {map.Tablas.Count} tablas ({cells} celdas) -> {target}");
        foreach (string note in map.Notas) Console.WriteLine($"  aviso: {note}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"{Path.GetFileName(pdf)}: ERROR {ex.Message}");
    }
}
return failures == 0 ? 0 : 2;
