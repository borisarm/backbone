using Backbone.App.Ipc;
using Backbone.App.Shell;
using Backbone.App.ViewModels;
using Backbone.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Backbone.App;

/// <summary>
/// Ventana principal. Navegación (§9.2): de momento Inicio y Trabajos. Cerrarla la oculta en la bandeja (§9.1);
/// solo "Salir" la cierra de verdad.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private HomeView? _home;
    private JobsView? _jobs;

    public MainWindow(string title)
    {
        InitializeComponent();
        Title = title;
        AppWindow.Closing += OnClosing;
    }

    /// <summary>Crea las páginas con sus view models y muestra Inicio.</summary>
    internal void Initialize(HomeViewModel home, JobsViewModel jobs)
    {
        _home = new HomeView(home);
        _jobs = new JobsView(jobs);
        Host.Children.Add(_home);
        Host.Children.Add(_jobs);
        // "Crear trabajo" del estado vacío de Inicio abre el editor en Trabajos.
        home.CreateJobRequested += (_, _) =>
        {
            Navigation.SelectedItem = JobsItem;
            jobs.NewJobCommand.Execute(null);
        };
        jobs.JobSaved += (_, _) => Navigation.SelectedItem = JobsItem;
        Navigation.SelectedItem = HomeItem;
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (_home is not null)
        {
            _home.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_jobs is not null)
        {
            _jobs.Visibility = tag == "jobs" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Permite el cierre real (al salir de la app).</summary>
    internal void AllowClose() => _allowClose = true;

    /// <summary>Muestra la ventana (si estaba oculta o minimizada) y la trae al frente.</summary>
    internal void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }
}
