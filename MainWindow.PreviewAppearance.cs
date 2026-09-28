using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DWOriginsAssetTool.Core;

namespace DWOriginsAssetTool;

public partial class MainWindow
{
    private Color _previewLineColor = Color.FromRgb(200, 174, 114);
    private Color _previewCameraColor = Color.FromRgb(220, 229, 237);
    private Color _previewTargetColor = Color.FromRgb(130, 150, 172);
    private double _previewLineScale = 2;
    private string _previewBackground = "#182030";
    private Brush _originalBackdrop = Brushes.Transparent;

    private void InitializePreviewAppearance()
    {
        _originalBackdrop = PrimaryBackdrop.Background;
        var saved = UserPreferences.GetPreviewAppearance();
        if (TryPreviewColor(saved.LineColor, out var line)) _previewLineColor = line;
        _previewLineScale = double.IsFinite(saved.LineScale) ? Math.Clamp(saved.LineScale, 1, 5) : 2;
        if (TryPreviewColor(saved.CameraColor, out var camera)) _previewCameraColor = camera;
        if (TryPreviewColor(saved.TargetColor, out var target)) _previewTargetColor = target;
        SetPreviewBackground(saved.Background);
    }

    private static bool TryPreviewColor(string text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length != 7 || text[0] != '#') return false;
        try { color = (Color)ColorConverter.ConvertFromString(text); return true; }
        catch { return false; }
    }

    private void SetPreviewBackground(string value)
    {
        Brush brush = _originalBackdrop;
        _previewBackground = "原始渐变";
        if (TryPreviewColor(value, out var color))
        {
            brush = new SolidColorBrush(color);
            _previewBackground = value;
        }
        PrimaryBackdrop.Background = brush;
        ComparisonBackdrop.Background = brush;
    }

    private void PreviewAppearance_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window
        {
            Title = "预览配色", Owner = this, Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            MaxHeight = SystemParameters.WorkArea.Height - 40
        };
        TextBox AddPalette(string title, string current, (string Name, string Value)[] choices)
        {
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            var values = new TextBox { Text = current, Margin = new Thickness(0, 6, 0, 6) };
            var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
            string group = Guid.NewGuid().ToString();
            foreach (var choice in choices)
            {
                Brush brush = choice.Value == "原始渐变" ? _originalBackdrop
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString(choice.Value));
                var content = new StackPanel();
                content.Children.Add(new Border { Width = 54, Height = 28, Background = brush,
                    BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
                content.Children.Add(new TextBlock { Text = choice.Name, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                var radio = new RadioButton { GroupName = group, Content = content,
                    Foreground = Foreground, Margin = new Thickness(0, 0, 10, 6),
                    IsChecked = string.Equals(current, choice.Value, StringComparison.OrdinalIgnoreCase) };
                System.Windows.Automation.AutomationProperties.SetName(radio, title + "：" + choice.Name);
                radio.Checked += (_, _) => values.Text = choice.Value;
                values.TextChanged += (_, _) => radio.IsChecked = string.Equals(values.Text.Trim(), choice.Value, StringComparison.OrdinalIgnoreCase);
                swatches.Children.Add(radio);
            }
            panel.Children.Add(swatches);
            var custom = new StackPanel();
            custom.Children.Add(new TextBlock { Text = "输入颜色值，例如 #DCE5ED", FontSize = 11 });
            custom.Children.Add(values);
            panel.Children.Add(new Expander { Header = "自定义颜色", Content = custom, Margin = new Thickness(0, 0, 0, 16) });
            return values;
        }
        var background = AddPalette("背景", _previewBackground, new[] {
            ("原始渐变", "原始渐变"), ("深蓝灰", "#182030"), ("浅灰", "#DCE2E8"), ("中灰", "#606870") });
        var line = AddPalette("运动轨迹", $"#{_previewLineColor.R:X2}{_previewLineColor.G:X2}{_previewLineColor.B:X2}", new[] {
            ("冷白", "#DCE5ED"), ("白色", "#FFFFFF"), ("暖金", "#C8AE72"), ("灰蓝", "#8296AC"), ("深蓝", "#182030") });
        var camera = AddPalette("摄像机框", $"#{_previewCameraColor.R:X2}{_previewCameraColor.G:X2}{_previewCameraColor.B:X2}", new[] {
            ("冷白", "#DCE5ED"), ("白色", "#FFFFFF"), ("暖金", "#C8AE72"), ("灰蓝", "#8296AC"), ("深蓝", "#182030") });
        var target = AddPalette("注视点连线与标记", $"#{_previewTargetColor.R:X2}{_previewTargetColor.G:X2}{_previewTargetColor.B:X2}", new[] {
            ("冷白", "#DCE5ED"), ("白色", "#FFFFFF"), ("暖金", "#C8AE72"), ("灰蓝", "#8296AC"), ("深蓝", "#182030") });
        panel.Children.Add(new TextBlock { Text = "线条粗细（原始的 1–5 倍，默认 2 倍）" });
        var scale = new Slider { Minimum = 1, Maximum = 5, TickFrequency = 0.5,
            IsSnapToTickEnabled = true, Value = _previewLineScale, Margin = new Thickness(0, 12, 0, 16) };
        panel.Children.Add(scale);
        var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 8, 8, 0) };
        reset.Click += (_, _) => { background.Text = "#182030"; line.Text = "#C8AE72"; camera.Text = "#DCE5ED"; target.Text = "#8296AC"; scale.Value = 2; };
        var apply = new Button { Content = "应用", IsDefault = true, Margin = new Thickness(0, 8, 8, 0) };
        apply.Click += (_, _) =>
        {
            string bg = background.Text.Trim();
            if ((bg != "原始渐变" && !TryPreviewColor(bg, out _)) || !TryPreviewColor(line.Text.Trim(), out var color)
                || !TryPreviewColor(camera.Text.Trim(), out var cameraColor) || !TryPreviewColor(target.Text.Trim(), out var targetColor))
            {
                error.Text = "请输入有效颜色，例如 #DCE5ED。";
                return;
            }
            SetPreviewBackground(bg);
            _previewLineColor = color;
            _previewCameraColor = cameraColor;
            _previewTargetColor = targetColor;
            _previewLineScale = scale.Value;
            DrawCameraTrajectoryProgress((float)_playbackTime, force: true);
            UpdateCameraGizmo((float)_playbackTime);
            UserPreferences.SetPreviewAppearance(bg, line.Text.Trim(), scale.Value, camera.Text.Trim(), target.Text.Trim());
            dialog.Close();
        };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(reset); buttons.Children.Add(apply); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.ShowDialog();
    }
}

