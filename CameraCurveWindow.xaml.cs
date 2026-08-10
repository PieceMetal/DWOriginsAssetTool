using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DWOriginsAssetTool;

public sealed record CameraCurvePoint(double Time, double? ActionFov, double? ActionDistance,
    double? ConfiguredFov, double? ConfiguredDistance, double? CameraSpeed, double? TargetSpeed);

public sealed record CameraCurveEvent(double Time, string Label, string Kind, bool IsConfiguration);

public sealed record CameraCurveSnapshot(string Title, string Subtitle, double Duration,
    double CurrentTime, IReadOnlyList<CameraCurvePoint> Points, IReadOnlyList<CameraCurveEvent> Events,
    IReadOnlyList<double> KeyTimes);

public partial class CameraCurveWindow : Window
{
    private CameraCurveSnapshot? _snapshot;
    private bool _dragging;
    private const double PlotLeft = 54;
    private const double PlotRight = 18;
    private const double PlotTop = 18;
    private const double PlotBottom = 46;
    public event Action<double>? SeekRequested;

    public CameraCurveWindow()
    {
        InitializeComponent();
    }

    public void SetSnapshot(CameraCurveSnapshot snapshot)
    {
        _snapshot = snapshot;
        TitleText.Text = snapshot.Title;
        SubtitleText.Text = snapshot.Subtitle;
        Draw();
    }

    public void SetCurrentTime(double time)
    {
        if (_snapshot is null) return;
        _snapshot = _snapshot with { CurrentTime = time };
        Draw();
    }

    private void DisplayOption_Changed(object sender, RoutedEventArgs e) => Draw();

    private void CurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void Draw()
    {
        CurveCanvas.Children.Clear();
        if (_snapshot is null || _snapshot.Points.Count == 0 || CurveCanvas.ActualWidth <= 20 || CurveCanvas.ActualHeight <= 20)
        {
            InfoText.Text = "没有可显示的镜头曲线。";
            RangeText.Text = "";
            return;
        }

        var width = CurveCanvas.ActualWidth;
        var height = CurveCanvas.ActualHeight;
        const double left = PlotLeft;
        const double right = PlotRight;
        const double top = PlotTop;
        const double bottom = PlotBottom;
        var plotW = Math.Max(1, width - left - right);
        var plotH = Math.Max(1, height - top - bottom);
        var duration = Math.Max(0.001, _snapshot.Duration);

        DrawGrid(left, top, plotW, plotH, duration);

        var values = new List<double>();
        if (ActionCameraCheck.IsChecked == true)
        {
            values.AddRange(_snapshot.Points.Select(x => x.ActionFov).Where(x => x.HasValue).Select(x => x!.Value));
            values.AddRange(_snapshot.Points.Select(x => x.ActionDistance).Where(x => x.HasValue).Select(x => x!.Value));
        }
        if (ConfiguredCameraCheck.IsChecked == true)
        {
            values.AddRange(_snapshot.Points.Select(x => x.ConfiguredFov).Where(x => x.HasValue).Select(x => x!.Value));
            values.AddRange(_snapshot.Points.Select(x => x.ConfiguredDistance).Where(x => x.HasValue).Select(x => x!.Value));
        }
        if (SpeedCheck.IsChecked == true)
        {
            values.AddRange(_snapshot.Points.Select(x => x.CameraSpeed).Where(x => x.HasValue).Select(x => x!.Value));
            values.AddRange(_snapshot.Points.Select(x => x.TargetSpeed).Where(x => x.HasValue).Select(x => x!.Value));
        }

        if (values.Count == 0)
        {
            InfoText.Text = "当前显示选项没有有效曲线。";
            RangeText.Text = "";
            return;
        }

        var min = values.Min();
        var max = values.Max();
        if (Math.Abs(max - min) < 1e-6)
        {
            min -= 1;
            max += 1;
        }
        var pad = (max - min) * 0.08;
        min -= pad;
        max += pad;

        double X(double t) => left + Math.Clamp(t / duration, 0, 1) * plotW;
        double Y(double v) => top + (1 - Math.Clamp((v - min) / (max - min), 0, 1)) * plotH;

        if (ActionCameraCheck.IsChecked == true)
        {
            DrawCurve(_snapshot.Points, x => x.ActionFov, X, Y, Color.FromRgb(185, 122, 236), 2.0, _snapshot.KeyTimes);
            DrawCurve(_snapshot.Points, x => x.ActionDistance, X, Y, Color.FromRgb(88, 210, 236), 2.0, _snapshot.KeyTimes);
        }
        if (ConfiguredCameraCheck.IsChecked == true)
        {
            DrawCurve(_snapshot.Points, x => x.ConfiguredFov, X, Y, Color.FromRgb(242, 184, 72), 2.0, _snapshot.Events.Where(x => x.IsConfiguration).Select(x => x.Time));
            DrawCurve(_snapshot.Points, x => x.ConfiguredDistance, X, Y, Color.FromRgb(99, 214, 143), 2.0, _snapshot.Events.Where(x => x.IsConfiguration).Select(x => x.Time));
        }
        if (SpeedCheck.IsChecked == true)
        {
            DrawCurve(_snapshot.Points, x => x.CameraSpeed, X, Y, Color.FromRgb(255, 126, 126), 1.4, _snapshot.KeyTimes);
            DrawCurve(_snapshot.Points, x => x.TargetSpeed, X, Y, Color.FromRgb(126, 162, 255), 1.4, _snapshot.KeyTimes);
        }

        foreach (var marker in _snapshot.Events)
        {
            var markerX = X(marker.Time);
            var line = new Line
            {
                X1 = markerX,
                X2 = markerX,
                Y1 = top,
                Y2 = top + plotH,
                Stroke = new SolidColorBrush(marker.IsConfiguration ? Color.FromRgb(241, 190, 72) : Color.FromRgb(96, 117, 141)),
                StrokeThickness = 1,
                Opacity = marker.IsConfiguration ? .78 : .45,
                ToolTip = $"{marker.Time:0.000}s  {marker.Kind}  {marker.Label}"
            };
            CurveCanvas.Children.Add(line);
            var box = new Rectangle
            {
                Width = 7,
                Height = 7,
                Fill = line.Stroke,
                Stroke = Brushes.Transparent,
                ToolTip = line.ToolTip
            };
            Canvas.SetLeft(box, markerX - 3.5);
            Canvas.SetTop(box, top + plotH + 8);
            CurveCanvas.Children.Add(box);
        }

        var currentX = X(_snapshot.CurrentTime);
        CurveCanvas.Children.Add(new Line
        {
            X1 = currentX,
            X2 = currentX,
            Y1 = top - 4,
            Y2 = top + plotH + 18,
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            Opacity = .9
        });

        var nearest = _snapshot.Points.MinBy(x => Math.Abs(x.Time - _snapshot.CurrentTime));
        if (nearest is not null)
            DrawCurrentValueLabels(nearest, X, Y);
        InfoText.Text = nearest is null
            ? ""
            : $"t={nearest.Time:0.000}s  动画FOV={Fmt(nearest.ActionFov)}  动画Dist={Fmt(nearest.ActionDistance)}  配置FOV={Fmt(nearest.ConfiguredFov)}  配置Dist={Fmt(nearest.ConfiguredDistance)}  CamSpeed={Fmt(nearest.CameraSpeed)}  TargetSpeed={Fmt(nearest.TargetSpeed)}";
        RangeText.Text = $"Y {min:0.##} – {max:0.##}";
    }

    private void DrawGrid(double left, double top, double plotW, double plotH, double duration)
    {
        var gridBrush = new SolidColorBrush(Color.FromRgb(33, 47, 64));
        for (var i = 0; i <= 5; i++)
        {
            var y = top + plotH * i / 5.0;
            CurveCanvas.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1, Opacity = .55 });
        }
        for (var i = 0; i <= 10; i++)
        {
            var x = left + plotW * i / 10.0;
            CurveCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + plotH, Stroke = gridBrush, StrokeThickness = 1, Opacity = .38 });
            var label = new TextBlock
            {
                Text = (duration * i / 10.0).ToString("0.##", CultureInfo.InvariantCulture),
                Foreground = new SolidColorBrush(Color.FromRgb(143, 160, 180)),
                FontSize = 10
            };
            Canvas.SetLeft(label, x - 10);
            Canvas.SetTop(label, top + plotH + 22);
            CurveCanvas.Children.Add(label);
        }
    }

    private void DrawCurve(IReadOnlyList<CameraCurvePoint> points, Func<CameraCurvePoint, double?> selector,
        Func<double, double> x, Func<double, double> y, Color color, double thickness, IEnumerable<double> keyTimes)
    {
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            Opacity = .96
        };
        foreach (var point in points)
        {
            var value = selector(point);
            if (!value.HasValue || !double.IsFinite(value.Value)) continue;
            polyline.Points.Add(new Point(x(point.Time), y(value.Value)));
        }
        if (polyline.Points.Count > 1) CurveCanvas.Children.Add(polyline);

        var brush = new SolidColorBrush(color);
        foreach (var keyTime in keyTimes.Distinct().OrderBy(t => t))
        {
            var point = points.MinBy(p => Math.Abs(p.Time - keyTime));
            var value = point is null ? null : selector(point);
            if (!value.HasValue || !double.IsFinite(value.Value)) continue;
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = brush,
                Stroke = Brushes.White,
                StrokeThickness = .8,
                ToolTip = $"{keyTime:0.000}s  {value.Value:0.###}"
            };
            Canvas.SetLeft(dot, x(keyTime) - 3.5);
            Canvas.SetTop(dot, y(value.Value) - 3.5);
            CurveCanvas.Children.Add(dot);
        }
    }

    private void DrawCurrentValueLabels(CameraCurvePoint point, Func<double, double> x, Func<double, double> y)
    {
        var items = new (string Label, double? Value, Color Color)[]
        {
            ("动画FOV", point.ActionFov, Color.FromRgb(185, 122, 236)),
            ("动画Dist", point.ActionDistance, Color.FromRgb(88, 210, 236)),
            ("配置FOV", point.ConfiguredFov, Color.FromRgb(242, 184, 72)),
            ("配置Dist", point.ConfiguredDistance, Color.FromRgb(99, 214, 143)),
            ("CamSpeed", point.CameraSpeed, Color.FromRgb(255, 126, 126)),
            ("TargetSpeed", point.TargetSpeed, Color.FromRgb(126, 162, 255))
        };
        var visible = items.Where(i => i.Value.HasValue && double.IsFinite(i.Value.Value)).ToArray();
        var baseX = Math.Min(CurveCanvas.ActualWidth - 150, x(point.Time) + 8);
        var row = 0;
        foreach (var item in visible)
        {
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(218, 13, 18, 26)),
                BorderBrush = new SolidColorBrush(item.Color),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                Child = new TextBlock
                {
                    Text = $"{item.Label} {item.Value!.Value:0.###}",
                    Foreground = new SolidColorBrush(item.Color),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas")
                }
            };
            Canvas.SetLeft(label, Math.Max(PlotLeft + 3, baseX));
            Canvas.SetTop(label, PlotTop + 6 + row * 22);
            CurveCanvas.Children.Add(label);
            row++;
        }
    }

    private void CurveCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_snapshot is null) return;
        _dragging = true;
        CurveCanvas.CaptureMouse();
        SeekFromPoint(e.GetPosition(CurveCanvas));
        e.Handled = true;
    }

    private void CurveCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        SeekFromPoint(e.GetPosition(CurveCanvas));
        e.Handled = true;
    }

    private void CurveCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        CurveCanvas.ReleaseMouseCapture();
        SeekFromPoint(e.GetPosition(CurveCanvas));
        e.Handled = true;
    }

    private void CurveCanvas_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void SeekFromPoint(Point point)
    {
        if (_snapshot is null || CurveCanvas.ActualWidth <= PlotLeft + PlotRight) return;
        var plotW = CurveCanvas.ActualWidth - PlotLeft - PlotRight;
        var time = Math.Clamp((point.X - PlotLeft) / plotW, 0, 1) * Math.Max(.001, _snapshot.Duration);
        var nearestFrame = Math.Round(time * 60.0) / 60.0;
        SeekRequested?.Invoke(Math.Clamp(nearestFrame, 0, _snapshot.Duration));
    }

    private static string Fmt(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-";
}
