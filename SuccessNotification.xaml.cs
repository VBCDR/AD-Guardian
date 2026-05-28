using System.Windows;
using System.Windows.Media;

namespace AdHealthMonitor;

public partial class SuccessNotification : Window
{
    public SuccessNotification(string title, string message, bool isError = false)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        Title = title;

        if (isError)
        {
            IconBorder.Background = new SolidColorBrush(Color.FromRgb(211, 47, 47));
            IconText.Text = "\u2716";
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
