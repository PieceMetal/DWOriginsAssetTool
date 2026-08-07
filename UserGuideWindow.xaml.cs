using DWOriginsAssetTool.Core;
using System.ComponentModel;
using System.Windows;

namespace DWOriginsAssetTool;

public partial class UserGuideWindow : Window
{
    public UserGuideWindow()
    {
        InitializeComponent();
        DoNotShowAgainCheck.IsChecked = UserPreferences.HideStartupGuide;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        UserPreferences.SetHideStartupGuide(DoNotShowAgainCheck.IsChecked == true);
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
