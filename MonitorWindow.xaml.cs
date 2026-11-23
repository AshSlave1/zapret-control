using System.Windows;
using System.Windows.Input;

namespace ZapretControl;

public partial class MonitorWindow : Window
{
    public MonitorWindow()
    {
        InitializeComponent();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove может бросить исключение при отпущенной кнопке.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
