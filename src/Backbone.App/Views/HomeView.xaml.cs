using Backbone.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Backbone.App.Views;

/// <summary>Página Inicio. Solo vista; la lógica está en <see cref="HomeViewModel"/>.</summary>
public sealed partial class HomeView : UserControl
{
    internal HomeView(HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    internal HomeViewModel ViewModel { get; }
}
