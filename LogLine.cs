using System.Windows;
using System.Windows.Media;

namespace AdHealthMonitor;

public class LogLine
{
    public string Text { get; set; } = string.Empty;
    public Brush Foreground { get; set; } = Brushes.Black;
    public FontWeight FontWeight { get; set; } = FontWeights.Normal;
}
