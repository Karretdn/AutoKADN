using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoKADN.CalcoIA;

// Servidor MCP mínimo (JSON-RPC 2.0 por stdin/stdout, un mensaje por línea) con las herramientas del calco, para que
// Claude Code las use sin acceso a nada más. Lo lanza el propio Claude Code con --mcp-config (ver MotorClaudeCode).
public sealed class McpServidor
{
    private readonly Herramientas _herramientas;
    private readonly Trabajo _trabajo;
    private readonly TextWriter _salida;

    public McpServidor(Trabajo trabajo)
    {
        _trabajo = trabajo;
        _herramientas = new Herramientas(trabajo);
        _salida = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public int Correr()
    {
        using var entrada = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? linea;
        while ((linea = entrada.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(linea)) continue;
            JsonNode? mensaje;
            try { mensaje = JsonNode.Parse(linea); }
            catch (JsonException) { continue; }
            if (mensaje is not JsonObject objeto) continue;

            JsonNode? id = objeto["id"]?.DeepClone();
            string metodo = objeto["method"]?.GetValue<string>() ?? string.Empty;
            if (id is null) continue; // notificaciones (notifications/initialized, cancelled…): no llevan respuesta

            try
            {
                JsonNode resultado = metodo switch
                {
                    "initialize" => Inicializar(objeto["params"]),
                    "tools/list" => Listar(_trabajo),
                    "tools/call" => Llamar(objeto["params"]),
                    "ping" => new JsonObject(),
                    _ => throw new MetodoDesconocido(metodo),
                };
                Enviar(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = resultado });
            }
            catch (MetodoDesconocido ex)
            {
                Enviar(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "método desconocido: " + ex.Message } });
            }
            catch (Exception ex)
            {
                // Un fallo inesperado de una herramienta no debe tumbar el servidor: se informa y se sigue.
                _trabajo.Registrar($"fallo en {metodo}: {ex}");
                Enviar(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message } });
            }
        }
        return 0;
    }

    private JsonNode Inicializar(JsonNode? parametros) => new JsonObject
    {
        ["protocolVersion"] = parametros?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "calco", ["version"] = "1.0" },
        ["instructions"] = "Herramientas para calcar o detallar con la hoja de un croquis de red de gas en AutoKADN.",
    };

    private static JsonNode Listar(Trabajo trabajo)
    {
        var lista = new JsonArray();
        foreach ((string nombre, string descripcion, string esquema) in Herramientas.Para(trabajo))
            lista.Add(new JsonObject { ["name"] = nombre, ["description"] = descripcion, ["inputSchema"] = Herramientas.Esquema(esquema) });
        return new JsonObject { ["tools"] = lista };
    }

    private JsonNode Llamar(JsonNode? parametros)
    {
        string nombre = parametros?["name"]?.GetValue<string>() ?? string.Empty;
        JsonNode argumentos = parametros?["arguments"]?.DeepClone() ?? new JsonObject();
        using JsonDocument documento = JsonDocument.Parse(argumentos.ToJsonString());
        ResultadoHerramienta r = _herramientas.Ejecutar(nombre, documento.RootElement);

        var contenido = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = r.Texto } };
        if (r.Png != null) contenido.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(r.Png), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = contenido, ["isError"] = r.Error };
    }

    private void Enviar(JsonObject mensaje) => _salida.WriteLine(mensaje.ToJsonString());

    private sealed class MetodoDesconocido(string metodo) : Exception(metodo);
}
