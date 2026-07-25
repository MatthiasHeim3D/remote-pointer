using System.Windows;

namespace RemotePointer.Client.Views;

public partial class ConnectionApprovalWindow : Window
{
    private readonly Func<Task> approveAsync;
    private readonly Func<Task> rejectAsync;

    public ConnectionApprovalWindow(
        string annotatorName,
        byte[]? annotatorProfilePicturePng,
        Func<Task> approveAsync,
        Func<Task> rejectAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotatorName);
        this.approveAsync = approveAsync ?? throw new ArgumentNullException(nameof(approveAsync));
        this.rejectAsync = rejectAsync ?? throw new ArgumentNullException(nameof(rejectAsync));
        AnnotatorProfilePicturePng = annotatorProfilePicturePng is null
            ? null
            : [.. annotatorProfilePicturePng];
        InitializeComponent();
        DataContext = this;
        AnnotatorNameText.Text = $"{annotatorName} wants to connect";
    }

    public byte[]? AnnotatorProfilePicturePng { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;
        Activate();
    }

    private async void OnApproveClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        IsEnabled = false;
        try
        {
            await approveAsync();
            if (IsVisible)
            {
                Close();
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnNotNowClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        IsEnabled = false;
        try
        {
            await rejectAsync();
            if (IsVisible)
            {
                Close();
            }
        }
        catch
        {
            // The view model reports the relay error and keeps the request available to retry.
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
