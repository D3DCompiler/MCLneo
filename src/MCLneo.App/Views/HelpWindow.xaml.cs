using System.Windows;

namespace MCLneo.App.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
