using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoKADN.CalcoIA;

// «Ajustes de la IA» (AutoKADN.CalcoIA.exe ajustes; botón en la cinta): el motor y el modelo que leen la hoja, la clave
// de la API y las medidas con que el enderezado arma el plano. Muestra el valor de fábrica de cada cosa y guarda en
// Ajustes.Archivo solo lo que el usuario cambió.
public sealed class VentanaAjustes : Window
{
    // Un parámetro del dibujo: su ruta en el JSON (marco.x0), cómo se llama aquí y para qué sirve.
    private sealed record Campo(string Ruta, string Nombre, string Ayuda, bool EsTexto = false);

    private static readonly (string Grupo, Campo[] Campos)[] Grupos =
    [
        ("Tubería y cotas",
        [
            new("separacion", "Separación tubería – línea de propiedad", "Distancia (unidades del dibujo) de la tubería a la cara de la manzana, de una vecina o a una cerca. La misma para la red nueva y la existente."),
            new("offsetCota", "Distancia de la cota de largo", "A cuánto de la tubería va la cota de longitud (como COTAK > Longitud)."),
            new("tramitoFraccion", "Cruce: largo del tramito", "Cada tramito de 0.1 del cruce de vía mide esta fracción del cruce…"),
            new("tramitoMinimo", "Cruce: tramito mínimo", "…pero al menos estas unidades, para que su cota se lea…"),
            new("tramitoMaximo", "Cruce: tramito máximo", "…y como mucho esta fracción del cruce, para que quepa la cota del medio."),
        ]),
        ("Vías y vecinas",
        [
            new("viaMinima", "Ancho mínimo de una vía", "Una vía entre la manzana y una vecina queda al doble de lo que mide en el croquis, pero nunca menos que esto…"),
            new("viaMaxima", "Ancho máximo de una vía", "…ni más que esto."),
            new("simetriaVecinos", "Simetría de vecinas con la vía", "Un lado de una vecina que cae a menos de esta fracción del tamaño de la manzana del anillo se alinea con ella."),
            new("viaSinNombre", "Nombre de una vía sin nombre", "Lo que se escribe en una vía junto a la tubería que el croquis ni el plano del proyecto nombran.", EsTexto: true),
            new("nombreViaLateral", "Nombre de vía: distancia al costado", "Separación del nombre de una vía lateral respecto a la cota de la tubería."),
            new("nombreViaSobreCota", "Nombre de vía: distancia sobre la cota", "Separación del nombre de una vía de arriba o de abajo respecto a la cota de la tubería."),
        ]),
        ("Composición",
        [
            new("margen", "Margen dentro del marco", "Espacio que se deja entre el dibujo y el borde del marco."),
            new("estirarMaximo", "Estirar como máximo", "Cuánto se puede estirar a lo ancho o a lo alto para llenar el marco (el croquis no va a escala). 1 = sin estirar."),
            new("radioIndicador", "Radio del indicador de anillo", "El círculo «n | Ø» de la plantilla."),
            new("separacionViviendas", "Indicador → texto de viviendas", "Del borde del círculo al centro del texto «10 VIVIENDAS»."),
            new("superficie", "Superficie (UC) por defecto", "La UC de los accesorios si el cuadro de la hoja no la dice.", EsTexto: true),
        ]),
        ("Marco del plano (de la plantilla; cambiar solo si cambia la plantilla)",
        [
            new("marco.x0", "Marco: izquierda (x0)", "Borde del recuadro de dibujo del layout ANILLO n DETALLE."),
            new("marco.y0", "Marco: abajo (y0)", ""),
            new("marco.x1", "Marco: derecha (x1)", ""),
            new("marco.y1", "Marco: arriba (y1)", ""),
        ]),
    ];

    private static readonly (string Valor, string Texto)[] Motores =
    [
        ("claude-code", "Claude Code — usa tu plan de Claude (sin clave)"),
        ("api", "API de Anthropic — se paga por uso con tu clave"),
    ];

    private static readonly (string Valor, string Texto)[] Modelos =
    [
        ("", "El predeterminado (Claude Code: el de tu plan; API: claude-opus-5-5)"),
        ("claude-sonnet-5-5", "claude-sonnet-5-5 — el que se ha usado; buen equilibrio"),
        ("claude-opus-5-5", "claude-opus-5-5 — más preciso, cerca del doble de costo"),
        ("claude-haiku-5-5", "claude-haiku-5-5 — muy barato, lee peor la letra a mano"),
    ];

    private static readonly (string Valor, string Texto)[] Esfuerzos =
    [
        ("", "El predeterminado del modelo"),
        ("low", "low — rápido y barato"),
        ("medium", "medium"),
        ("high", "high"),
        ("xhigh", "xhigh"),
        ("max", "max — el más cuidadoso y caro"),
    ];

    private readonly JsonObject _fabrica = Ajustes.DeFabrica();
    private readonly Dictionary<string, TextBox> _campos = [];
    private readonly ComboBox _motor = new(), _modelo = new() { IsEditable = true }, _esfuerzo = new();
    private readonly TextBox _minutos = new(), _turnos = new();
    private readonly PasswordBox _clave = new();
    private readonly TextBlock _estadoClave = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _estado = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };

    public VentanaAjustes()
    {
        Title = "AutoKADN — Ajustes de la IA";
        Width = 760;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var raiz = new DockPanel { Margin = new Thickness(14) };
        var pie = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(pie, Dock.Bottom);
        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        botones.Children.Add(Boton("Restablecer todo", (_, _) => Restablecer()));
        botones.Children.Add(Boton("Guardar", (_, _) => GuardarTodo(), principal: true));
        botones.Children.Add(Boton("Cerrar", (_, _) => Close()));
        DockPanel.SetDock(botones, Dock.Right);
        pie.Children.Add(botones);
        pie.Children.Add(_estado);
        raiz.Children.Add(pie);

        var contenido = new StackPanel();
        contenido.Children.Add(Seccion("Quién lee la hoja", MotorPanel()));
        foreach (var (grupo, campos) in Grupos) contenido.Children.Add(Seccion(grupo, ParametrosPanel(campos)));
        var otros = new StackPanel { Orientation = Orientation.Horizontal };
        otros.Children.Add(Boton("Abrir carpeta de trabajos", (_, _) => Abrir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoKADN", "CalcoIA", "trabajos"))));
        otros.Children.Add(Boton("Abrir instrucciones de la IA (kit)", (_, _) => Abrir(Path.Combine(AppContext.BaseDirectory, "kit"))));
        contenido.Children.Add(Seccion("Más", otros));
        raiz.Children.Add(new ScrollViewer { Content = contenido, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = raiz;

        Cargar();
    }

    private UIElement MotorPanel()
    {
        var grilla = Grilla();
        foreach (var (valor, texto) in Motores) _motor.Items.Add(new ComboBoxItem { Content = texto, Tag = valor });
        foreach (var (valor, texto) in Modelos) _modelo.Items.Add(new ComboBoxItem { Content = texto, Tag = valor });
        foreach (var (valor, texto) in Esfuerzos) _esfuerzo.Items.Add(new ComboBoxItem { Content = texto, Tag = valor });
        Fila(grilla, "Motor", _motor, "Claude Code usa la sesión de tu plan (claude /login). La API cobra por uso y necesita la clave de abajo.");
        Fila(grilla, "Modelo", _modelo, "Elija uno de la lista o escriba el nombre de otro modelo.");
        Fila(grilla, "Esfuerzo", _esfuerzo, "Cuánto razona antes de responder. Más esfuerzo: más cuidadoso, más lento y más caro.");
        Fila(grilla, "Minutos máximos", _minutos, "Si una corrida pasa de esto, se corta.");
        Fila(grilla, "Turnos máximos", _turnos, "Cuántas veces puede usar sus herramientas (ver hoja, medir, enderezar…).");

        var clave = new StackPanel();
        var linea = new DockPanel();
        var guardar = Boton("Guardar clave", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_clave.Password)) { _estado.Text = "Escriba la clave antes de guardarla."; return; }
            ClaveApi.Guardar(_clave.Password);
            _clave.Clear();
            _estadoClave.Text = ClaveApi.Estado();
            _estado.Text = "Clave guardada.";
        });
        var borrar = Boton("Borrar", (_, _) => { ClaveApi.Borrar(); _estadoClave.Text = ClaveApi.Estado(); _estado.Text = "Clave borrada."; });
        DockPanel.SetDock(borrar, Dock.Right);
        DockPanel.SetDock(guardar, Dock.Right);
        linea.Children.Add(borrar);
        linea.Children.Add(guardar);
        linea.Children.Add(_clave);
        clave.Children.Add(linea);
        clave.Children.Add(_estadoClave);
        Fila(grilla, "Clave de la API", clave, "Solo para el motor «API». Se guarda cifrada para tu usuario de Windows, nunca en el plano ni en el repositorio.");
        return grilla;
    }

    private UIElement ParametrosPanel(Campo[] campos)
    {
        var grilla = Grilla();
        foreach (Campo campo in campos)
        {
            var caja = new TextBox { MinWidth = 120 };
            _campos[campo.Ruta] = caja;
            string fabrica = Valor(_fabrica, campo.Ruta) ?? "";
            var linea = new DockPanel();
            var restablecer = new Button { Content = "↺", ToolTip = $"Volver al de fábrica ({fabrica})", Width = 28, Margin = new Thickness(6, 0, 0, 0) };
            restablecer.Click += (_, _) => caja.Text = fabrica;
            var deFabrica = new TextBlock { Text = $"fábrica: {fabrica}", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 120 };
            DockPanel.SetDock(restablecer, Dock.Right);
            DockPanel.SetDock(deFabrica, Dock.Right);
            linea.Children.Add(restablecer);
            linea.Children.Add(deFabrica);
            linea.Children.Add(caja);
            caja.TextChanged += (_, _) => caja.FontWeight = Igual(caja.Text, fabrica, campo.EsTexto) ? FontWeights.Normal : FontWeights.Bold;
            Fila(grilla, campo.Nombre, linea, campo.Ayuda);
        }
        return grilla;
    }

    private void Cargar()
    {
        Parametros p = Parametros.Cargar();
        JsonObject actual = System.Text.Json.JsonSerializer.SerializeToNode(p, Json.Opciones) as JsonObject ?? new JsonObject();
        foreach (var (ruta, caja) in _campos) caja.Text = Valor(actual, ruta) ?? "";
        MotorConfig motor = Configuracion.Motor(p);
        Elegir(_motor, motor.Tipo);
        Elegir(_modelo, motor.Modelo);
        Elegir(_esfuerzo, motor.Esfuerzo);
        _minutos.Text = motor.MinutosMaximos.ToString(CultureInfo.InvariantCulture);
        _turnos.Text = motor.TurnosMaximos.ToString(CultureInfo.InvariantCulture);
        _estadoClave.Text = ClaveApi.Estado();
        _estado.Text = $"Tus ajustes: {Ajustes.Archivo}. Valen desde la próxima corrida. En negrita, lo que cambiaste respecto a fábrica.";
    }

    private void GuardarTodo()
    {
        var ajustes = new JsonObject();
        var parametros = new JsonObject();
        foreach (var grupo in Grupos)
            foreach (Campo campo in grupo.Campos)
            {
                string texto = _campos[campo.Ruta].Text.Trim();
                string fabrica = Valor(_fabrica, campo.Ruta) ?? "";
                if (Igual(texto, fabrica, campo.EsTexto)) continue;
                JsonNode valor;
                if (campo.EsTexto) valor = JsonValue.Create(texto)!;
                else if (double.TryParse(texto.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double numero)) valor = JsonValue.Create(numero)!;
                else { _estado.Text = $"«{campo.Nombre}» debe ser un número."; return; }
                Poner(parametros, campo.Ruta, valor);
            }
        if (parametros.Count > 0) ajustes["parametros"] = parametros;

        JsonObject motorFabrica = _fabrica["motor"] as JsonObject ?? new JsonObject();
        string tipo = Elegido(_motor), modelo = Elegido(_modelo), esfuerzo = Elegido(_esfuerzo);
        if (tipo != (motorFabrica["tipo"]?.GetValue<string>() ?? "claude-code")) ajustes["motor"] = tipo;
        if (modelo != (motorFabrica["modelo"]?.GetValue<string>() ?? "")) ajustes["modelo"] = modelo;
        if (esfuerzo != (motorFabrica["esfuerzo"]?.GetValue<string>() ?? "")) ajustes["esfuerzo"] = esfuerzo;
        foreach (var (caja, clave) in new[] { (_minutos, "minutosMaximos"), (_turnos, "turnosMaximos") })
        {
            if (!int.TryParse(caja.Text.Trim(), out int n) || n <= 0) { _estado.Text = $"«{clave}» debe ser un número entero mayor que cero."; return; }
            if (n != (motorFabrica[clave]?.GetValue<int>() ?? 0)) ajustes[clave] = n;
        }
        if (tipo == "api" && ClaveApi.Leer() == null) _estado.Text = "Guardado. Ojo: el motor «API» necesita una clave y no hay ninguna.";
        Ajustes.Guardar(ajustes);
        if (!(tipo == "api" && ClaveApi.Leer() == null)) _estado.Text = $"Guardado en {Ajustes.Archivo}. Vale desde la próxima corrida.";
    }

    private void Restablecer()
    {
        if (MessageBox.Show(this, "¿Volver todos los ajustes de la IA a los de fábrica? (La clave de la API no se borra.)", "Ajustes de la IA",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Ajustes.Guardar(new JsonObject());
        Cargar();
        _estado.Text = "Todo quedó como de fábrica.";
    }

    // ───────────── ayudas ─────────────

    private static string? Valor(JsonObject o, string ruta)
    {
        JsonNode? nodo = o;
        foreach (string parte in ruta.Split('.')) nodo = nodo is JsonObject j ? j[parte] : null;
        return nodo switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out double d) => d.ToString("0.###", CultureInfo.InvariantCulture),
            JsonValue v when v.TryGetValue(out string? s) => s,
            _ => nodo.ToJsonString(),
        };
    }

    private static void Poner(JsonObject o, string ruta, JsonNode valor)
    {
        string[] partes = ruta.Split('.');
        for (int i = 0; i < partes.Length - 1; i++)
        {
            if (o[partes[i]] is not JsonObject hijo) { hijo = new JsonObject(); o[partes[i]] = hijo; }
            o = hijo;
        }
        o[partes[^1]] = valor;
    }

    private static bool Igual(string texto, string fabrica, bool esTexto)
    {
        if (esTexto) return string.Equals(texto.Trim(), fabrica.Trim(), StringComparison.Ordinal);
        return double.TryParse(texto.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            && double.TryParse(fabrica, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && Math.Abs(a - b) < 1e-9;
    }

    private static void Elegir(ComboBox combo, string valor)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (string.Equals((string)item.Tag, valor, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
        if (combo.IsEditable) combo.Text = valor;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    // El valor elegido: el de la lista o, en el modelo, lo que se escribió (sin la explicación).
    private static string Elegido(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item && (!combo.IsEditable || combo.Text == (string)item.Content)) return (string)item.Tag;
        return combo.Text.Split('—')[0].Trim();
    }

    private static Grid Grilla()
    {
        var grilla = new Grid { Margin = new Thickness(4) };
        grilla.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grilla.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grilla;
    }

    private static void Fila(Grid grilla, string nombre, UIElement control, string ayuda)
    {
        int fila = grilla.RowDefinitions.Count;
        grilla.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var etiqueta = new TextBlock { Text = nombre, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0), TextWrapping = TextWrapping.Wrap };
        if (ayuda.Length > 0) etiqueta.ToolTip = ayuda;
        Grid.SetRow(etiqueta, fila);
        grilla.Children.Add(etiqueta);
        var celda = new StackPanel { Margin = new Thickness(0, 3, 0, 6) };
        celda.Children.Add(control);
        if (ayuda.Length > 0) celda.Children.Add(new TextBlock { Text = ayuda, Foreground = Brushes.Gray, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetRow(celda, fila);
        Grid.SetColumn(celda, 1);
        grilla.Children.Add(celda);
    }

    private static GroupBox Seccion(string titulo, UIElement contenido) =>
        new() { Header = new TextBlock { Text = titulo, FontWeight = FontWeights.SemiBold }, Content = contenido, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(6) };

    private static Button Boton(string texto, RoutedEventHandler clic, bool principal = false)
    {
        var boton = new Button { Content = texto, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 90 };
        if (principal) boton.FontWeight = FontWeights.SemiBold;
        boton.Click += clic;
        return boton;
    }

    private static void Abrir(string carpeta)
    {
        Directory.CreateDirectory(carpeta);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{carpeta}\"") { UseShellExecute = true });
    }
}
