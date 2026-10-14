using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoKADN.CalcoIA;

// Quién lee la hoja. Los dos motores usan las mismas herramientas (Herramientas) y terminan igual: la IA llama a
// «entregar» y queda resultado.json con plano.json al lado.
//   claude-code: Claude Code instalado en el equipo, con la sesión de la suscripción del usuario.
//   api:         la API de Anthropic con una clave (funciona en cualquier equipo).
// El tipo sale de kit\parametros.json (motor.tipo) y se puede cambiar por equipo con %APPDATA%\AutoKADN\calcoia.json
// ({"motor": "api", "modelo": "…"}) o con la variable AUTOKADN_CALCOIA_MOTOR.
public static class Motores
{
    public static int Leer(Trabajo trabajo)
    {
        MotorConfig config = Configuracion.Motor(trabajo.Parametros);
        Paso($"Motor {config.Tipo}{(config.Modelo.Length > 0 ? ", modelo " + config.Modelo : string.Empty)}");
        trabajo.Registrar($"motor {config.Tipo} {config.Modelo}");
        foreach (string viejo in new[] { "resultado.json", "plano.json", "detalle.json" })
            if (File.Exists(trabajo.Archivo(viejo))) File.Delete(trabajo.Archivo(viejo));

        Paso("Preparando la hoja");
        _ = trabajo.Imagen; // extrae la hoja del PDF ahora: los errores de archivo salen antes de llamar a la IA
        if (trabajo.EsDetalle) _ = trabajo.Lectura;

        string? error = config.Tipo == "api" ? MotorApi.Correr(trabajo, config) : MotorClaudeCode.Correr(trabajo, config);
        InformarCosto(trabajo);
        if (error == null && !File.Exists(trabajo.Archivo("resultado.json"))) error = trabajo.EsDetalle ? "la IA terminó sin entregar el detalle" : "la IA terminó sin entregar el plano";
        if (error != null)
        {
            trabajo.Registrar("ERROR " + error);
            Console.WriteLine("ERROR " + error);
            return 1;
        }
        Console.WriteLine("LISTO");
        return 0;
    }

    public static void Paso(string texto) => Console.WriteLine("PASO " + texto.Replace('\n', ' ').Replace('\r', ' '));

    // Lo que costó: una línea «COSTO {json}» para el plugin (lo muestra al entregar) y el campo «costo» de resultado.json.
    private static void InformarCosto(Trabajo trabajo)
    {
        if (trabajo.Costo is not Costo costo) return;
        string json = JsonSerializer.Serialize(costo, Json.Opciones);
        Console.WriteLine("COSTO " + json);
        trabajo.Registrar("costo " + json);
        string archivo = trabajo.Archivo("resultado.json");
        if (!File.Exists(archivo)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(archivo)) is JsonObject resultado)
            {
                resultado["costo"] = JsonSerializer.SerializeToNode(costo, Json.Opciones);
                File.WriteAllText(archivo, resultado.ToJsonString(Json.Indentado));
            }
        }
        catch (JsonException) { /* el resultado sigue valiendo sin el costo */ }
    }

    public static string Instrucciones(Trabajo trabajo) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "kit", trabajo.EsDetalle ? "INSTRUCCIONES-DETALLE.md" : "INSTRUCCIONES.md"));

    // Lo que se le dice a la IA al empezar (los dos motores).
    public static string Pedido(Trabajo trabajo) => (Giro.Normalizar(trabajo.Pedido.Giro) != 0 && !trabajo.EsDetalle
        ? $"La hoja está girada {Giro.Normalizar(trabajo.Pedido.Giro)}° en el dibujo: describa el croquis como se ve en la hoja (derecha, como fue escaneada); el enderezado lo gira solo y la vista previa sale girada. "
        : string.Empty) + (trabajo.Pedido.Diametro.Length > 0
        ? $"El layout dice que el anillo es de Ø{trabajo.Pedido.Diametro}\" (úselo salvo que la hoja diga claramente otro diámetro; si es así, anótelo en dudas). "
        : string.Empty) + (trabajo.EsDetalle
        ? $"Detalle el anillo ya dibujado en el layout {trabajo.Pedido.Layout} con lo que dice la hoja de este trabajo. Empiece con ver_dibujo y ver_hoja, " +
          "compruebe que la hoja corresponda al dibujo, revise con revisar_detalle y termine llamando a entregar_detalle una sola vez."
        : $"Calque la hoja de este trabajo para el layout {trabajo.Pedido.Layout}. Empiece con ver_hoja, mida lo necesario, " +
          "revise la vista previa con enderezar y termine llamando a entregar una sola vez.");

    public static string Describir(string herramienta, JsonNode? entrada) => herramienta switch
    {
        "ver_hoja" => "Viendo la hoja",
        "ampliar" => $"Ampliando la zona x {entrada?["x"]}, y {entrada?["y"]}",
        "medir_lineas" => $"Midiendo líneas {entrada?["direccion"]}",
        "enderezar" => "Enderezando y revisando la vista previa",
        "entregar" => "Entregando el plano",
        "superponer" => "Comparando el croquis con la hoja",
        "ver_paginas" => "Buscando el plano del proyecto en el PDF",
        "ver_pagina" => $"Viendo la página {entrada?["pagina"]} del PDF",
        "ver_dibujo" => "Viendo el anillo dibujado",
        "revisar_detalle" => "Revisando el detalle",
        "entregar_detalle" => "Entregando el detalle",
        _ => herramienta,
    };
}

// Lo que gastó una corrida. Base «plan»: Claude Code con el plan de Claude (el valor es a precio de API; el plan no lo
// cobra aparte, cuenta para su límite de uso). «api»: se cobra a la cuenta de la API. Usd null: modelo sin precio conocido.
public sealed class Costo
{
    public double? Usd { get; set; }
    public string Modelo { get; set; } = string.Empty;
    public int Turnos { get; set; }
    public double Segundos { get; set; }
    public string Base { get; set; } = "plan";
}

public static class Configuracion
{
    public static MotorConfig Motor(Parametros parametros)
    {
        MotorConfig config = parametros.Motor;
        // Lo que el usuario eligió en «Ajustes de la IA» (Ajustes.Archivo) manda sobre el kit.
        JsonObject propio = Ajustes.Leer();
        if (propio["motor"] is JsonValue m && m.TryGetValue(out string? tipo) && tipo.Length > 0) config.Tipo = tipo;
        if (propio["modelo"] is JsonValue mo && mo.TryGetValue(out string? modelo)) config.Modelo = modelo;
        if (propio["esfuerzo"] is JsonValue e && e.TryGetValue(out string? esfuerzo)) config.Esfuerzo = esfuerzo;
        if (propio["minutosMaximos"] is JsonValue mi && mi.TryGetValue(out int minutos) && minutos > 0) config.MinutosMaximos = minutos;
        if (propio["turnosMaximos"] is JsonValue tu && tu.TryGetValue(out int turnos) && turnos > 0) config.TurnosMaximos = turnos;
        if (Environment.GetEnvironmentVariable("AUTOKADN_CALCOIA_MOTOR") is { Length: > 0 } variable) config.Tipo = variable;
        config.Tipo = config.Tipo.Trim().ToLowerInvariant();
        return config;
    }
}

// Claude Code sin ventana (claude -p). Solo ve las herramientas del calco (servidor MCP de este mismo ejecutable):
// sin las herramientas propias (--tools ""), sin pedir permisos (--permission-mode dontAsk) y sin guardar la sesión.
public static class MotorClaudeCode
{
    public static string? Correr(Trabajo trabajo, MotorConfig config)
    {
        string? claude = BuscarClaude();
        if (claude == null) return "no se encontró Claude Code (claude.exe). Instálelo e inicie sesión una vez con «claude» en una terminal";

        string mcp = trabajo.Archivo("mcp.json");
        var servidor = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["calco"] = new JsonObject { ["type"] = "stdio", ["command"] = Environment.ProcessPath, ["args"] = new JsonArray("mcp", trabajo.Carpeta) },
            },
        };
        File.WriteAllText(mcp, servidor.ToJsonString());

        var inicio = new ProcessStartInfo(claude)
        {
            WorkingDirectory = trabajo.Carpeta,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string a in new[]
                 {
                     "-p", Motores.Pedido(trabajo), "--output-format", "stream-json", "--verbose",
                     "--append-system-prompt", Motores.Instrucciones(trabajo),
                     "--mcp-config", mcp, "--strict-mcp-config", "--tools", "",
                     "--allowedTools", string.Join(",", Herramientas.Para(trabajo).Select(d => "mcp__calco__" + d.Nombre)),
                     "--permission-mode", "dontAsk", "--no-session-persistence",
                 })
            inicio.ArgumentList.Add(a);
        if (config.Modelo.Length > 0) { inicio.ArgumentList.Add("--model"); inicio.ArgumentList.Add(config.Modelo); }
        if (config.Esfuerzo.Length > 0) { inicio.ArgumentList.Add("--effort"); inicio.ArgumentList.Add(config.Esfuerzo); }

        // Que corra como lo lanzaría AutoCAD: sin las variables de una sesión de Claude Code que esté abierta.
        foreach (string clave in inicio.Environment.Keys.ToList())
            if (clave.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) && clave != "CLAUDE_CODE_OAUTH_TOKEN" && clave != "CLAUDE_CONFIG_DIR")
                inicio.Environment.Remove(clave);

        using var proceso = Process.Start(inicio)!;
        proceso.StandardInput.Close();
        string? error = null;
        var errores = new System.Text.StringBuilder();
        proceso.ErrorDataReceived += (_, e) => { if (e.Data != null) { errores.AppendLine(e.Data); trabajo.Registrar("stderr " + e.Data); } };
        proceso.BeginErrorReadLine();

        var reloj = Stopwatch.StartNew();   // lo que espera el usuario (incluye arrancar Claude Code)
        var lector = Task.Run(() =>
        {
            string? linea;
            while ((linea = proceso.StandardOutput.ReadLine()) != null)
            {
                trabajo.Registrar("claude " + (linea.Length > 2000 ? linea[..2000] + "…" : linea));
                JsonNode? evento;
                try { evento = JsonNode.Parse(linea); } catch (JsonException) { continue; }
                switch (evento?["type"]?.GetValue<string>())
                {
                    case "assistant":
                        foreach (JsonNode? bloque in evento["message"]?["content"]?.AsArray() ?? [])
                        {
                            string? tipo = bloque?["type"]?.GetValue<string>();
                            if (tipo == "tool_use") Motores.Paso(Motores.Describir((bloque!["name"]?.GetValue<string>() ?? "").Replace("mcp__calco__", ""), bloque["input"]));
                            else if (tipo == "text" && bloque!["text"]?.GetValue<string>() is { Length: > 0 } texto) Motores.Paso(texto.Length > 160 ? texto[..160] + "…" : texto);
                        }
                        break;
                    case "result":
                        if (evento["is_error"]?.GetValue<bool>() == true) error = "Claude Code: " + (evento["result"]?.ToString() ?? evento["subtype"]?.ToString());
                        trabajo.Registrar($"fin: turnos {evento["num_turns"]}, {evento["duration_ms"]} ms, costo {evento["total_cost_usd"]} USD");
                        // El modelo que más gastó (Claude Code puede usar otro chico para tareas sueltas).
                        string? usado = (evento["modelUsage"] as JsonObject)?.OrderByDescending(m => m.Value?["costUSD"]?.GetValue<double>() ?? 0).Select(m => m.Key).FirstOrDefault();
                        trabajo.Costo = new Costo
                        {
                            Usd = evento["total_cost_usd"] is JsonValue usd && usd.TryGetValue(out double d) ? Math.Round(d, 4) : null,
                            Modelo = usado ?? config.Modelo,
                            Turnos = evento["num_turns"] is JsonValue t && t.TryGetValue(out int n) ? n : 0,
                            Segundos = Math.Round(reloj.Elapsed.TotalSeconds),
                            Base = "plan",
                        };
                        break;
                }
            }
        });

        if (!proceso.WaitForExit(TimeSpan.FromMinutes(Math.Max(1, config.MinutosMaximos))))
        {
            try { proceso.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return $"se pasó de {config.MinutosMaximos} minutos";
        }
        lector.Wait(TimeSpan.FromSeconds(10));
        if (error == null && proceso.ExitCode != 0)
        {
            string detalle = errores.ToString().Trim();
            error = $"Claude Code terminó con código {proceso.ExitCode}" + (detalle.Length > 0 ? ": " + (detalle.Length > 400 ? detalle[..400] : detalle) : string.Empty);
        }
        return error;
    }

    private static string? BuscarClaude()
    {
        string? propio = Environment.GetEnvironmentVariable("AUTOKADN_CLAUDE");
        if (!string.IsNullOrWhiteSpace(propio) && File.Exists(propio)) return propio;
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (string carpeta in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            foreach (string nombre in new[] { "claude.exe", "claude.cmd" })
            {
                try { string ruta = Path.Combine(carpeta.Trim(), nombre); if (File.Exists(ruta)) return ruta; }
                catch (ArgumentException) { }
            }
        return null;
    }
}
