using System.Windows.Input;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.ViewModels;

/// <summary>
/// One row in the host's list of connected annotators. The host acts on an annotator through its
/// client instance id, so a row whose descriptor carries none — the placeholder that stands in
/// for an unnamed annotator — offers no per-annotator actions.
/// </summary>
public sealed class ConnectedAnnotatorViewModel : ObservableObject
{
    private readonly AsyncRelayCommand disconnectCommand;
    private readonly AsyncRelayCommand togglePauseCommand;
    private byte[]? profilePicturePng;
    private string displayName;
    private bool isAnnotating;
    private bool isPaused;

    public ConnectedAnnotatorViewModel(
        ConnectedAnnotatorDescriptor descriptor,
        Func<ConnectedAnnotatorViewModel, Task> togglePause,
        Func<ConnectedAnnotatorViewModel, Task> disconnect)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(togglePause);
        ArgumentNullException.ThrowIfNull(disconnect);

        AnnotatorId = descriptor.AnnotatorId;
        displayName = descriptor.DisplayName;
        profilePicturePng = descriptor.ProfilePicturePng;
        isPaused = descriptor.IsPaused;
        togglePauseCommand = new AsyncRelayCommand(
            _ => togglePause(this),
            _ => CanControl);
        disconnectCommand = new AsyncRelayCommand(
            _ => disconnect(this),
            _ => CanControl);
    }

    public string AnnotatorId { get; }

    public bool CanControl => !string.IsNullOrEmpty(AnnotatorId);

    public string DisplayName
    {
        get => displayName;
        private set
        {
            if (SetProperty(ref displayName, value))
            {
                RaisePropertyChanged(nameof(PauseActionLabel));
                RaisePropertyChanged(nameof(DisconnectActionLabel));
            }
        }
    }

    public byte[]? ProfilePicturePng
    {
        get => profilePicturePng;
        private set => SetProperty(ref profilePicturePng, value);
    }

    public bool IsPaused
    {
        get => isPaused;
        internal set
        {
            if (SetProperty(ref isPaused, value))
            {
                RaisePropertyChanged(nameof(StatusLabel));
                RaisePropertyChanged(nameof(StatusColor));
                RaisePropertyChanged(nameof(PauseActionIcon));
                RaisePropertyChanged(nameof(PauseActionLabel));
                RaisePropertyChanged(nameof(IsAnnotating));
            }
        }
    }

    /// <summary>
    /// True while pointer events from this annotator are still arriving. A paused annotator never
    /// counts as annotating, whatever was still in flight when the pause took effect.
    /// </summary>
    public bool IsAnnotating
    {
        get => isAnnotating && !IsPaused;
        internal set => SetProperty(ref isAnnotating, value);
    }

    public string StatusLabel => IsPaused ? "Paused" : "Connected";

    public string StatusColor => IsPaused ? "#FFB900" : "#6CCB7F";

    // Segoe MDL2 Assets: Play to let a paused annotator draw again, Pause to stop one.
    public string PauseActionIcon => IsPaused ? "" : "";

    public string PauseActionLabel => IsPaused
        ? $"Let {DisplayName} annotate again"
        : $"Pause {DisplayName}";

    public string DisconnectActionLabel => $"Disconnect {DisplayName}";

    public ICommand TogglePauseCommand => togglePauseCommand;

    public ICommand DisconnectCommand => disconnectCommand;

    internal void Update(ConnectedAnnotatorDescriptor descriptor)
    {
        DisplayName = descriptor.DisplayName;
        ProfilePicturePng = descriptor.ProfilePicturePng;
        IsPaused = descriptor.IsPaused;
    }
}
