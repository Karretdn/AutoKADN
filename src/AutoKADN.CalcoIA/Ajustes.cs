using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoKADN.CalcoIA;

// Los ajustes del usuario (ventana «Ajustes de la IA»): calcoia.json en %APPDATA%\AutoKADN (o en la carpeta de
// AUTOKADN_SETTINGS, que usan las pruebas para no tocar los reales). Solo guarda lo que el usuario cambió; lo demás sale
// del kit (kit\parametros.json, junto al ejecutable), así una mejora de fábrica le llega sin pisar lo suyo.
//   { "motor": "claude-code" | "api", "modelo": "...", "esfuerzo": "...", "minutosMaximos": 20, "turnosMaximos": 60,
//     "parametros": { "separacion": 9.0, "marco": { "x0": 4.5 }, ... } }
public static class Ajustes
{
    public static string Carpeta => Environment.GetEnvironmentVariable("AUTOKADN_SETTINGS") is { Length: > 0 } carpeta
        ? carpeta
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoKADN");

    public static string Archivo => Path.Combine(Carpeta, "calcoia.json");

    public static string ArchivoDeFabrica => Path.Combine(AppContext.BaseDirectory, "kit", "parametros.json");

    public static JsonObject Leer()
    {
        try { return File.Exists(Archivo) && JsonNode.Parse(File.ReadAllText(Archivo)) is JsonObject o ? o : new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public static void Guardar(JsonObject ajustes)
    {
        Directory.CreateDirectory(Carpeta);
        File.WriteAllText(Archivo, ajustes.ToJsonString(new JsonSerializerOptions(Json.Indentado)));
    }

    // Los parámetros de fábrica tal como están en el kit (JSON).
    public static JsonObject DeFabrica()
    {
        try { return File.Exists(ArchivoDeFabrica) && JsonNode.Parse(File.ReadAllText(ArchivoDeFabrica), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o ? o : new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    // Copia los valores de «encima» sobre «base» (los objetos anidados, como el marco, valor por valor).
    public static void Mezclar(JsonObject @base, JsonObject encima)
    {
        foreach (var (clave, valor) in encima)
        {
            if (valor is JsonObject hijo && @base[clave] is JsonObject propio) Mezclar(propio, hijo);
            else @base[clave] = valor?.DeepClone();
        }
    }
}
