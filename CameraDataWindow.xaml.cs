using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DWOriginsAssetTool;

public partial class CameraDataWindow : Window
{
    public CameraDataWindow()
    {
        InitializeComponent();
    }

    public void SetMode(string mode) => ModeText.Text = mode;

    public void ClearData() => DataText.Inlines.Clear();

    public void AddLine(string text, string color, bool newline = true)
    {
        DataText.Inlines.Add(new Run(text)
        {
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!
        });
        if (newline) DataText.Inlines.Add(new LineBreak());
    }
}
