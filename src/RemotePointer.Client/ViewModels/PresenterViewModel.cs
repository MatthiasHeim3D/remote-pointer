using System.Globalization;
using System.Windows.Input;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.ViewModels;

public sealed class PresenterViewModel : ObservableObject, IDisposable
{
    private readonly ITargetRegionService targetRegionService;
    private readonly RelayCommand togglePointingCommand;
    private bool aspectRatioLockEnabled = true;
    private int capturedPointerCount;
    private bool disposed;
    private double expectedHeightPixels = 1_080d;
    private double expectedWidthPixels = 1_920d;
    private bool isError;
    private string lastPointer = "No local pointers captured yet.";
    private TargetRegionState state = TargetRegionState.Inactive;
    private string statusMessage = "Calibrate the target area to begin.";

    public PresenterViewModel(ITargetRegionService targetRegionService)
    {
        this.targetRegionService = targetRegionService
            ?? throw new ArgumentNullException(nameof(targetRegionService));
        this.targetRegionService.StateChanged += OnStateChanged;
        this.targetRegionService.PointerCaptured += OnPointerCaptured;

        CalibrateCommand = new RelayCommand(_ => BeginCalibration());
        togglePointingCommand = new RelayCommand(
            _ => TogglePointingMode(),
            _ => State is TargetRegionState.Ready or TargetRegionState.Pointing);
        ExitPointingCommand = new RelayCommand(_ => targetRegionService.ExitPointingMode());
    }

    public double ExpectedWidthPixels
    {
        get => expectedWidthPixels;
        set => SetProperty(ref expectedWidthPixels, value);
    }

    public double ExpectedHeightPixels
    {
        get => expectedHeightPixels;
        set => SetProperty(ref expectedHeightPixels, value);
    }

    public bool AspectRatioLockEnabled
    {
        get => aspectRatioLockEnabled;
        set => SetProperty(ref aspectRatioLockEnabled, value);
    }

    public TargetRegionState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
            {
                togglePointingCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(IsPointing));
                RaisePropertyChanged(nameof(StateLabel));
            }
        }
    }

    public bool IsPointing => State == TargetRegionState.Pointing;

    public string StateLabel => State.ToString();

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsError
    {
        get => isError;
        private set => SetProperty(ref isError, value);
    }

    public int CapturedPointerCount
    {
        get => capturedPointerCount;
        private set => SetProperty(ref capturedPointerCount, value);
    }

    public string LastPointer
    {
        get => lastPointer;
        private set => SetProperty(ref lastPointer, value);
    }

    public ICommand CalibrateCommand { get; }

    public ICommand TogglePointingCommand => togglePointingCommand;

    public ICommand ExitPointingCommand { get; }

    public void TogglePointingMode()
    {
        if (State is not (TargetRegionState.Ready or TargetRegionState.Pointing))
        {
            SetStatus("Calibrate and lock a target region before enabling pointing.", isError: true);
            return;
        }

        targetRegionService.TogglePointingMode();
    }

    public void ReportHotKeyRegistrationFailure(string message) =>
        SetStatus(message, isError: true);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        targetRegionService.StateChanged -= OnStateChanged;
        targetRegionService.PointerCaptured -= OnPointerCaptured;
        targetRegionService.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private void BeginCalibration()
    {
        if (!double.IsFinite(ExpectedWidthPixels)
            || !double.IsFinite(ExpectedHeightPixels)
            || ExpectedWidthPixels <= 0d
            || ExpectedHeightPixels <= 0d)
        {
            SetStatus("Expected receiver dimensions must be positive numbers.", isError: true);
            return;
        }

        var expectedRatio = AspectRatio.Calculate(
            ExpectedWidthPixels,
            ExpectedHeightPixels);
        targetRegionService.BeginCalibration(expectedRatio, AspectRatioLockEnabled);
    }

    private void OnStateChanged(object? sender, TargetRegionStateChangedEventArgs e)
    {
        State = e.State;
        SetStatus(e.Message, e.IsError);
    }

    private void OnPointerCaptured(object? sender, PointerCapturedEventArgs e)
    {
        CapturedPointerCount++;
        LastPointer = string.Create(
            CultureInfo.InvariantCulture,
            $"Normalized X {e.Point.X:0.0000}, Y {e.Point.Y:0.0000}");
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsError = isError;
    }
}
