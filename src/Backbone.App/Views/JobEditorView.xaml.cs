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
        Loaded += (_, _) => NameBox.Focus(FocusState.Programmatic);
    }

    internal JobEditorViewModel ViewModel { get; }

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
