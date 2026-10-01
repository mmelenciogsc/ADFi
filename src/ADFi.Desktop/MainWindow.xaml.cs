using System.Windows;
using System.Windows.Controls;

namespace ADFi.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void PrimaryNavigation_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, PrimaryNavigation))
        {
            return;
        }

        StatusText.Text = PrimaryNavigation.SelectedIndex == 0
            ? "MY ADFi selected. Sign in to access private account features."
            : "DISCOVER selected. Public ADFied works do not require sign-in.";
    }
}

