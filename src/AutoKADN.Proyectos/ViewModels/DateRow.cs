namespace AutoKADN.Proyectos.ViewModels;

/// <summary>
/// Una fecha del formulario. Mientras el usuario no la toque (IsManual = false) sigue a la fecha anterior,
/// así normalmente solo se eligen una o dos fechas. Borrar una fecha la devuelve al modo automático.
/// </summary>
public sealed class DateRow : ObservableObject
{
    private DateTime? _value;
    private bool _isManual;

    public DateRow(string key, string label, string? header = null, string? autoHint = null)
    {
        Key = key;
        Label = label;
        Header = header;
        AutoHint = autoHint ?? string.Empty;
    }

    public string Key { get; }
    public string Label { get; }
    public string? Header { get; }
    public bool HasHeader => !string.IsNullOrEmpty(Header);
    public string AutoHint { get; }

    public DateTime? Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) Changed?.Invoke(this);
        }
    }

    public bool IsManual
    {
        get => _isManual;
        set => Set(ref _isManual, value);
    }

    /// <summary>Se dispara cuando cambia el valor (por el usuario o por la cadena automática).</summary>
    public event Action<DateRow>? Changed;
}
