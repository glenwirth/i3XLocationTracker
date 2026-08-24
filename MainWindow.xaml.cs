using System.Windows;
using I3XLocationTracker.ViewModels;

namespace I3XLocationTracker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // PasswordBox.Password can't be data-bound (by design), so seed it from the loaded
        // settings manually. This re-fires PasswordChanged, but that just reassigns the same
        // value back to Token, so it's a harmless no-op save.
        TokenBox.Password = _viewModel.Token;

        Closed += (_, _) => _viewModel.Dispose();
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.Token = TokenBox.Password;
    }
}
