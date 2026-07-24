using System.Windows;
using System.Windows.Input;

namespace RemotePointer.Client.Views;

public partial class ConnectionApprovalWindow : Window
{
    private readonly ICommand approveCommand;

    public ConnectionApprovalWindow(string presenterName, ICommand approveCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presenterName);
        this.approveCommand = approveCommand ?? throw new ArgumentNullException(nameof(approveCommand));
        InitializeComponent();
        PresenterNameText.Text = $"{presenterName} wants to connect";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;
        Activate();
    }

    private void OnApproveClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (approveCommand.CanExecute(null))
        {
            approveCommand.Execute(null);
        }

        Close();
    }

    private void OnNotNowClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }
}
