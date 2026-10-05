using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AutoKADN.Proyectos.Services;

namespace AutoKADN.Proyectos.Controls;

/// <summary>
/// Fecha de entrada rápida: se escribe con dígitos (2509, 250926, 25/09/2026, hoy) o se elige en el
/// calendario. Debajo del campo se lee en claro (día de la semana) para no tener que interpretar nada.
/// IsManual indica si la persona la tocó; si no, la fecha viene de la anterior y así se muestra.
/// </summary>
public partial class DateField : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate), typeof(DateTime?), typeof(DateField),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDateChanged));

    public static readonly DependencyProperty IsManualProperty = DependencyProperty.Register(
        nameof(IsManual), typeof(bool), typeof(DateField),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((DateField)d).UpdateCaption()));

    public static readonly DependencyProperty AutoHintProperty = DependencyProperty.Register(
        nameof(AutoHint), typeof(string), typeof(DateField),
        new PropertyMetadata(string.Empty, (d, _) => ((DateField)d).UpdateCaption()));

    private bool _invalid;

    public DateField()
    {
        InitializeComponent();
        UpdateCaption();
    }

    public DateTime? SelectedDate { get => (DateTime?)GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }
    public bool IsManual { get => (bool)GetValue(IsManualProperty); set => SetValue(IsManualProperty, value); }
    public string AutoHint { get => (string)GetValue(AutoHintProperty); set => SetValue(AutoHintProperty, value); }

    private static void OnDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (DateField)d;
        // Si la persona está escribiendo no se le pisa el texto.
        if (!field.Input.IsKeyboardFocused || !field._invalid) field.ShowDate();
        field.UpdateCaption();
    }

    private void ShowDate()
    {
        _invalid = false;
        Input.Text = SelectedDate is DateTime date ? DateParser.ForExcel(date) : string.Empty;
    }

    private void UpdateCaption()
    {
        if (Caption is null) return;
        if (_invalid)
        {
            Caption.Text = "Fecha no válida. Prueba con 2509 o 25/09/26";
            Caption.Foreground = (Brush)FindResource("Danger");
            Caption.FontStyle = FontStyles.Normal;
        }
        else if (SelectedDate is DateTime date)
        {
            bool auto = !IsManual && !string.IsNullOrEmpty(AutoHint);
            Caption.Text = auto ? $"{DateParser.Describe(date)}  ·  {AutoHint}" : DateParser.Describe(date);
            Caption.Foreground = (Brush)FindResource(auto ? "TextMuted" : "TextStrong");
            Caption.FontStyle = auto ? FontStyles.Italic : FontStyles.Normal;
        }
        else
        {
            Caption.Text = "Escribe 2509 o elige en el calendario";
            Caption.Foreground = (Brush)FindResource("TextMuted");
            Caption.FontStyle = FontStyles.Normal;
        }
    }

    // ── Escritura ───────────────────────────────────────────────────────────────────────────────
    private void Commit()
    {
        if (!DateParser.TryParse(Input.Text, DateTime.Today, out DateTime? parsed))
        {
            _invalid = true;
            UpdateCaption();
            return;
        }

        _invalid = false;
        if (parsed is null)
        {
            // Vacío: vuelve a seguir a la fecha anterior.
            IsManual = false;
            SelectedDate = null;
        }
        else
        {
            IsManual = true;
            SelectedDate = parsed;
        }
        ShowDate();
        UpdateCaption();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Commit();
        Input.SelectAll();
        e.Handled = true;
    }

    private void OnInputLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    // Al entrar al campo se selecciona todo: escribir reemplaza la fecha.
    private void OnInputFocus(object sender, KeyboardFocusChangedEventArgs e) => Input.SelectAll();

    private void OnInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Input.IsKeyboardFocusWithin) return;
        Input.Focus(); // el foco ya selecciona todo
        e.Handled = true;
    }

    // ── Calendario ──────────────────────────────────────────────────────────────────────────────
    private void OnCalendarClick(object sender, RoutedEventArgs e)
    {
        DateTime start = SelectedDate ?? DateTime.Today;
        Cal.SelectedDate = SelectedDate;
        Cal.DisplayDate = start;
        CalendarPopup.IsOpen = true;
    }

    private void OnCalendarSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Cal.SelectedDate is not DateTime date) return;
        Pick(date);
    }

    private void OnTodayClick(object sender, RoutedEventArgs e) => Pick(DateTime.Today);

    private void Pick(DateTime date)
    {
        CalendarPopup.IsOpen = false;
        IsManual = true;
        SelectedDate = date;
        ShowDate();
        UpdateCaption();
    }

    // El calendario dentro de un popup deja el mouse capturado y bloquea el siguiente clic.
    private void OnCalendarMouseCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured is CalendarItem) Mouse.Capture(null);
    }
}
