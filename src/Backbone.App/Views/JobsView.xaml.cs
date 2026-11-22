using Backbone.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Backbone.App.Views;

/// <summary>Página Trabajos: lista y editor. Solo vista; la lógica está en <see cref="JobsViewModel"/>.</summary>
public sealed partial class JobsView : UserControl
{
    internal JobsView(JobsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobsViewModel.Editor))
            {
                ShowEditor();
            }
        };
    }

    internal JobsViewModel ViewModel { get; }

    private void ShowEditor() =>
        EditorHost.Content = ViewModel.Editor is { } editor ? new JobEditorView(editor) : null;

    private void OnJobClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is JobListItem item)
        {
            ViewModel.EditJobCommand.Execute(item);
        }
    }
}
