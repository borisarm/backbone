using Backbone.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Backbone.App.Views;

/// <summary>Editor de un trabajo. Solo vista: toda la lógica está en <see cref="JobEditorViewModel"/>.</summary>
public sealed partial class JobEditorView : UserControl
{
    internal JobEditorView(JobEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus(FocusState.Programmatic);
            _ready = true;
        };
    }

    // Los cambios que hace el enlace de datos al abrir el editor no deben lanzar una validación.
    private bool _ready;

    internal JobEditorViewModel ViewModel { get; }

    // Programación: cada cambio pide al servicio validar la sección y muestra el error junto al campo.
    private void OnScheduleChanged(object sender, RoutedEventArgs e) => RequestScheduleValidation();

    private void OnScheduleKindChanged(object sender, SelectionChangedEventArgs e) => RequestScheduleValidation();

    private void OnScheduleTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs e) => RequestScheduleValidation();

    private void OnRemoveTimeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduleTimeItem item })
        {
            ViewModel.RemoveTimeCommand.Execute(item);
        }
    }

    private void RequestScheduleValidation()
    {
        if (_ready)
        {
            _ = ViewModel.ValidateScheduleAsync();
        }
    }

    // Al salir de un alias o del destino se pide la validación al servicio para mostrar el error en línea.
    private void OnSourceFieldLostFocus(object sender, RoutedEventArgs e) => _ = ViewModel.ValidateAsync();

    private void OnDestinationLostFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.Destination))
        {
            _ = ViewModel.ValidateAsync();
        }
    }

    private void OnRemoveSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SourceItemViewModel source })
        {
            ViewModel.RemoveSourceCommand.Execute(source);
        }
    }
}
