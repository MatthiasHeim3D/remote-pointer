using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.ViewModels;

/// <summary>
/// One preset swatch in the annotation colour picker.
/// </summary>
public sealed class AnnotationColorOption : ObservableObject
{
    private bool isSelected;

    public AnnotationColorOption(string name, string color)
    {
        Name = name;
        Color = AnnotationColors.Normalize(color);
    }

    /// <summary>Shown as the swatch's tooltip; the swatch itself is the colour.</summary>
    public string Name { get; }

    public string Color { get; }

    public bool IsSelected
    {
        get => isSelected;
        internal set => SetProperty(ref isSelected, value);
    }
}
