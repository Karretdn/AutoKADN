using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace AutoKADN.CalcoIA;

// La API de Anthropic (con clave): el mismo trabajo que hace Claude Code, pero el ciclo de herramientas lo lleva este
// programa. Sirve en cualquier equipo. Modelo por defecto claude-opus-5-5 con esfuerzo alto (leer letra a mano y ubicar
// líneas es trabajo fino); instrucciones y herramientas en caché, y la conversación con caché automática para que cada
// vuelta no vuelva a cobrar las imágenes ya vistas. Si el modelo rechaza la petición, la API la pasa a otro modelo
// (fallbacks "default").
public static class MotorApi
{
    private const string ModeloPorDefecto = "claude-opus-5-5";

    public static string? Correr(Trabajo trabajo, MotorConfig config)
    {
        string? clave = ClaveApi.Leer();
        if (clave == null) return "falta la clave de la API de Anthropic: configúrela con «AutoKADN.CalcoIA.exe clave» o la variable ANTHROPIC_API_KEY";

        var client = new AnthropicClient { ApiKey = clave, MaxRetries = 4 };
        var herramientas = new Herramientas(trabajo);
        string modelo = config.Modelo.Length > 0 ? config.Modelo : ModeloPorDefecto;

        var tools = new List<BetaToolUnion>();
        foreach ((string nombre, string descripcion, string esquema) in Herramientas.Para(trabajo))
        {
            JsonObject json = Herramientas.Esquema(esquema);
            var propiedades = new Dictionary<string, JsonElement>();
            foreach (var p in json["properties"]?.AsObject() ?? new JsonObject()) propiedades[p.Key] = JsonSerializer.SerializeToElement(p.Value);
            var requeridos = json["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
            tools.Add(new BetaTool { Name = nombre, Description = descripcion, InputSchema = new() { Properties = propiedades, Required = requeridos } });
        }

        var mensajes = new List<BetaMessageParam>
        {
            new() { Role = Role.User, Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = Motores.Pedido(trabajo) } } },
        };
        using var limite = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, config.MinutosMaximos)));
        int empujones = 0;
        // Lo que va costando: tokens de cada vuelta por el precio del modelo que respondió.
        double usd = 0;
        bool sinPrecio = false;
        int vueltas = 0;
        string respondio = modelo;
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
        for (int turno = 1; turno <= Math.Max(1, config.TurnosMaximos); turno++)
        {
            BetaMessage respuesta;
            try
            {
                var parametros = new MessageCreateParams
                {
                    Model = modelo,
                    MaxTokens = 16000,
                    System = new List<BetaTextBlockParam> { new() { Text = Motores.Instrucciones(trabajo), CacheControl = new BetaCacheControlEphemeral() } },
                    Tools = tools,
                    Messages = mensajes,
                    CacheControl = new BetaCacheControlEphemeral(),
                    OutputConfig = new BetaOutputConfig { Effort = Esfuerzo(config.Esfuerzo) },
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new Default(),
                };
                respuesta = client.Beta.Messages.Create(parametros, limite.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { return $"se pasó de {config.MinutosMaximos} minutos"; }
            catch (AnthropicUnauthorizedException) { return "la clave de la API no es válida"; }
            catch (AnthropicRateLimitException) { return "se alcanzó el límite de uso de la API; intente más tarde"; }
            catch (AnthropicBadRequestException ex) { return "la API rechazó la petición: " + ex.Message; }
            catch (Anthropic5xxException) { return "el servicio de Anthropic no está disponible ahora; intente más tarde"; }
            catch (AnthropicIOException) { return "no hay conexión con la API de Anthropic"; }
            catch (AnthropicApiException ex) { return "error de la API: " + ex.Message; }

            trabajo.Registrar($"api turno {turno}: stop {respuesta.StopReason}, entrada {respuesta.Usage.InputTokens} (caché leída {respuesta.Usage.CacheReadInputTokens}), salida {respuesta.Usage.OutputTokens}");
            vueltas = turno;
            respondio = respuesta.Model.ToString() is { Length: > 0 } m ? m : modelo;
            if (Precio(respondio) is var (pEntrada, pSalida, pLeida, pEscrita))
                usd += (N(respuesta.Usage.InputTokens) * pEntrada + N(respuesta.Usage.OutputTokens) * pSalida
                        + N(respuesta.Usage.CacheReadInputTokens) * pLeida + N(respuesta.Usage.CacheCreationInputTokens) * pEscrita) / 1_000_000;
            else sinPrecio = true;
            if (respuesta.StopReason == "refusal") return "el modelo no quiso procesar la hoja (rechazo de seguridad)";

            var asistente = new List<BetaContentBlockParam>();
            var usos = new List<BetaToolUseBlock>();
            foreach (BetaContentBlock bloque in respuesta.Content)
            {
                if (bloque.TryPickText(out BetaTextBlock? texto))
                {
                    asistente.Add(new BetaTextBlockParam { Text = texto.Text });
                    if (!string.IsNullOrWhiteSpace(texto.Text)) Motores.Paso(texto.Text.Length > 160 ? texto.Text[..160] + "…" : texto.Text);
                }
                else if (bloque.TryPickThinking(out BetaThinkingBlock? pensamiento))
                    asistente.Add(new BetaThinkingBlockParam { Thinking = pensamiento.Thinking, Signature = pensamiento.Signature });
                else if (bloque.TryPickRedactedThinking(out BetaRedactedThinkingBlock? oculto))
                    asistente.Add(new BetaRedactedThinkingBlockParam { Data = oculto.Data });
                else if (bloque.TryPickToolUse(out BetaToolUseBlock? uso))
                {
                    asistente.Add(new BetaToolUseBlockParam { ID = uso.ID, Name = uso.Name, Input = uso.Input });
                    usos.Add(uso);
                }
            }
            mensajes.Add(new BetaMessageParam { Role = Role.Assistant, Content = asistente });

            if (usos.Count == 0)
            {
                if (respuesta.StopReason == "max_tokens" || ++empujones <= 2)
                {
                    mensajes.Add(new BetaMessageParam { Role = Role.User, Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = trabajo.EsDetalle ? "Continúe con las herramientas y termine llamando a entregar_detalle." : "Continúe con las herramientas y termine llamando a entregar." } } });
                    continue;
                }
                return "la IA dejó de trabajar sin entregar el plano";
            }

            // Todas las respuestas de herramientas en un solo mensaje del usuario.
            var resultados = new List<BetaContentBlockParam>();
            bool entregado = false;
            foreach (BetaToolUseBlock uso in usos)
            {
                JsonElement entrada = JsonSerializer.SerializeToElement(uso.Input);
                Motores.Paso(Motores.Describir(uso.Name, JsonNode.Parse(entrada.GetRawText())));
                ResultadoHerramienta r = herramientas.Ejecutar(uso.Name, entrada);
                entregado |= r.Entregado;
                var contenido = new List<Block> { new BetaTextBlockParam { Text = r.Texto } };
                if (r.Png != null) contenido.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(r.Png), MediaType = MediaType.ImagePng } });
                resultados.Add(new BetaToolResultBlockParam { ToolUseID = uso.ID, Content = contenido, IsError = r.Error });
            }
            mensajes.Add(new BetaMessageParam { Role = Role.User, Content = resultados });
            if (entregado) return null;
        }
        return $"la IA no terminó en {config.TurnosMaximos} vueltas";
        }
        finally
        {
            trabajo.Costo = new Costo { Usd = sinPrecio ? null : Math.Round(usd, 4), Modelo = respondio, Turnos = vueltas, Segundos = Math.Round(reloj.Elapsed.TotalSeconds), Base = "api" };
        }
    }

    private static double N(object? valor) => valor == null ? 0 : Convert.ToDouble(valor, System.Globalization.CultureInfo.InvariantCulture);

    // USD por millón de tokens: entrada, salida, caché leída y caché escrita (5 minutos, la que usa este motor). Los de la
    // familia 5.5; otro modelo queda sin precio (el costo se muestra como desconocido). Haiku: la tarifa de hasta 100K.
    private static (double, double, double, double)? Precio(string modelo) => modelo switch
    {
        _ when modelo.StartsWith("claude-opus-5-5", StringComparison.Ordinal) => (4, 20, 0.20, 5),
        _ when modelo.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal) => (2, 10, 0.20, 2.5),
        _ when modelo.StartsWith("claude-haiku-5-5", StringComparison.Ordinal) => (0.10, 0.50, 0.01, 0.125),
        _ => null,
    };

    private static Effort Esfuerzo(string esfuerzo) => esfuerzo.Trim().ToLowerInvariant() switch
    {
        "low" or "bajo" => Effort.Low,
        "medium" or "medio" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };
}

// La clave de la API: la variable ANTHROPIC_API_KEY o la guardada (cifrada para el usuario de Windows con DPAPI) en
// %APPDATA%\AutoKADN\calcoia-clave.bin. Nunca en el repositorio ni en el dibujo.
public static class ClaveApi
{
    private static string Archivo => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoKADN", "calcoia-clave.bin");

    public static string? Leer()
    {
        string? variable = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(variable)) return variable.Trim();
        if (!File.Exists(Archivo)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Archivo), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public static void Guardar(string clave)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Archivo)!);
        File.WriteAllBytes(Archivo, ProtectedData.Protect(Encoding.UTF8.GetBytes(clave.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public static void Borrar() { if (File.Exists(Archivo)) File.Delete(Archivo); }

    // Para la ventana de ajustes: de dónde saldría la clave (sin mostrarla).
    public static string Estado() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? "Se usa la variable de entorno ANTHROPIC_API_KEY."
        : File.Exists(Archivo) ? "Hay una clave guardada (cifrada para tu usuario de Windows)."
        : "No hay clave guardada.";
}
