using CommunityToolkit.Mvvm.ComponentModel;

namespace Backbone.App.ViewModels;

/// <summary>Una carpeta de origen del editor, con su alias y los mensajes de validación de cada campo.</summary>
internal sealed partial class SourceItemViewModel : ObservableObject
{
    public SourceItemViewModel(string path, string alias)
    {
        Path = path;
        Alias = alias;
        PathError = AliasError = PathWarning = string.Empty;
    }

    /// <summary>Ruta elegida con el selector (no editable).</summary>
    public string Path { get; }

    [ObservableProperty]
    public partial string Alias { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathError))]
    public partial string PathError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAliasError))]
    public partial string AliasError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathWarning))]
    public partial string PathWarning { get; set; }

    /// <summary>Hay un error junto a la ruta.</summary>
    public bool HasPathError => PathError.Length > 0;

    /// <summary>Hay un error junto al alias.</summary>
    public bool HasAliasError => AliasError.Length > 0;

    /// <summary>Hay un aviso junto a la ruta.</summary>
    public bool HasPathWarning => PathWarning.Length > 0;

    internal void ClearMessages()
    {
        PathError = string.Empty;
        AliasError = string.Empty;
        PathWarning = string.Empty;
    }
}
