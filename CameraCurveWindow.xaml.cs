using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DWOriginsAssetTool;

public sealed record CameraCurvePoint(double Time, double? ActionFov, double? ActionDistance,
    double? PositionX, double? PositionY, double? PositionZ,
    double? Pitch, double? Yaw, double? Roll);

public sealed record CameraCurveEvent(double Time, string Label, string Kind);

public sealed record CameraCurveSnapshot(string Title, string Subtitle, double Duration,
    double CurrentTime, IReadOnlyList<CameraCurvePoint> Points, IReadOnlyList<CameraCurveEvent> Events,
    IReadOnlyList<double> KeyTimes);

public partial class CameraCurveWindow : Window
{
    private CameraCurveSnapshot? _snapshot;
    private bool _dragging;
    private bool _panning, _boxing;
    private Point _lastMouse, _boxStart;
    private double _timeMin, _timeMax = 1, _valueMin = -1, _valueMax = 1;
    private bool _autoValues = true;
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
        _timeMin = 0;
        _timeMax = Math.Max(.001, snapshot.Duration);
        _autoValues = true;
        Draw();
    }

    public void SetCurrentTime(double time)
    {
        if (_snapshot is null) return;
        _snapshot = _snapshot with { CurrentTime = time };
        Draw();
    }

    private void DisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _autoValues = true;
        Draw();
    }

    private void ChannelTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem item || FovCheck is null) return;
        var tag = item.Tag as string;
        var checks = new[] { PositionXCheck, PositionYCheck, PositionZCheck, PitchCheck, YawCheck, RollCheck, FovCheck, DistanceCheck };
        for (int i = 0; i < checks.Length; i++)
        {
            var id = checks[i].Name.Replace("Check", "");
            checks[i].IsChecked = tag == id || tag == "all" ||
                (tag == "transform" && i < 6) || (tag == "position" && i < 3) ||
                (tag == "rotation" && i >= 3 && i < 6);
        }
        _autoValues = true;
        Draw();
    }

    private void ChannelCheck_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox check) return;
        check.IsChecked = check.IsChecked != true;
        _autoValues = true;
        Draw();
        e.Handled = true;
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _timeMin = 0;
        _timeMax = Math.Max(.001, _snapshot?.Duration ?? 1);
        _autoValues = true;
        Draw();
    }

    private void FitValues_Click(object sender, RoutedEventArgs e) { _autoValues = true; Draw(); }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F) { Fit_Click(sender, e); e.Handled = true; }
    }

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
        var duration = Math.Max(0.001, _timeMax - _timeMin);

        var values = new List<double>();
        if (FovCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.ActionFov).Where(x => x.HasValue).Select(x => x!.Value));
        if (DistanceCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.ActionDistance).Where(x => x.HasValue).Select(x => x!.Value));
        if (PositionXCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.PositionX).Where(x => x.HasValue).Select(x => x!.Value));
        if (PositionYCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.PositionY).Where(x => x.HasValue).Select(x => x!.Value));
        if (PositionZCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.PositionZ).Where(x => x.HasValue).Select(x => x!.Value));
        if (PitchCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.Pitch).Where(x => x.HasValue).Select(x => x!.Value));
        if (YawCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.Yaw).Where(x => x.HasValue).Select(x => x!.Value));
        if (RollCheck.IsChecked == true)
            values.AddRange(_snapshot.Points.Select(x => x.Roll).Where(x => x.HasValue).Select(x => x!.Value));

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
        if (_autoValues)
        {
            var visible = _snapshot.Points.Where(p => p.Time >= _timeMin && p.Time <= _timeMax).ToArray();
            var selectedValues = new List<double>();
            void Add(CheckBox check, Func<CameraCurvePoint, double?> selector)
            {
                if (check.IsChecked == true)
                    selectedValues.AddRange(visible.Select(selector).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value));
            }
            Add(FovCheck, p => p.ActionFov); Add(DistanceCheck, p => p.ActionDistance);
            Add(PositionXCheck, p => p.PositionX); Add(PositionYCheck, p => p.PositionY); Add(PositionZCheck, p => p.PositionZ);
            Add(PitchCheck, p => p.Pitch); Add(YawCheck, p => p.Yaw); Add(RollCheck, p => p.Roll);
            if (selectedValues.Count > 0)
            {
                var low = selectedValues.Min(); var high = selectedValues.Max();
                var padding = Math.Max(1, (high - low) * .08);
                min = low - padding; max = high + padding;
            }
            _valueMin = min;
            _valueMax = max;
            _autoValues = false;
        }
        min = _valueMin;
        max = _valueMax;
        DrawGrid(left, top, plotW, plotH, duration);

        double X(double t) => left + (t - _timeMin) / duration * plotW;
        double Y(double v) => top + (1 - (v - min) / (max - min)) * plotH;

        if (FovCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.ActionFov, X, Y, Color.FromRgb(185, 122, 236), 2.0, _snapshot.KeyTimes);
        if (DistanceCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.ActionDistance, X, Y, Color.FromRgb(88, 210, 236), 2.0, _snapshot.KeyTimes);
        if (PositionXCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.PositionX, X, Y, Color.FromRgb(255, 107, 107), 1.8, _snapshot.KeyTimes);
        if (PositionYCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.PositionY, X, Y, Color.FromRgb(113, 216, 137), 1.8, _snapshot.KeyTimes);
        if (PositionZCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.PositionZ, X, Y, Color.FromRgb(98, 168, 255), 1.8, _snapshot.KeyTimes);
        if (PitchCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.Pitch, X, Y, Color.FromRgb(255, 180, 84), 1.8, _snapshot.KeyTimes);
        if (YawCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.Yaw, X, Y, Color.FromRgb(240, 111, 195), 1.8, _snapshot.KeyTimes);
        if (RollCheck.IsChecked == true)
            DrawCurve(_snapshot.Points, x => x.Roll, X, Y, Color.FromRgb(229, 232, 107), 1.8, _snapshot.KeyTimes);

        foreach (var marker in _snapshot.Events)
        {
            if (marker.Time < _timeMin || marker.Time > _timeMax) continue;
            var markerX = X(marker.Time);
            var line = new Line
            {
                X1 = markerX,
                X2 = markerX,
                Y1 = top,
                Y2 = top + plotH,
                Stroke = new SolidColorBrush(Color.FromRgb(96, 117, 141)),
                StrokeThickness = 1,
                Opacity = .45,
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
            Stroke = Brushes.IndianRed,
            StrokeThickness = 1.6,
            Opacity = .9
        });

        var nearest = _snapshot.Points.MinBy(x => Math.Abs(x.Time - _snapshot.CurrentTime));
        if (nearest is not null)
            DrawCurrentValueLabels(nearest, X, Y);
        InfoText.Text = nearest is null ? "" : BuildCurrentInfo(nearest);
        RangeText.Text = $"数值 {min:0.##} – {max:0.##}";
    }

    private void DrawGrid(double left, double top, double plotW, double plotH, double duration)
    {
        var gridBrush = new SolidColorBrush(Color.FromRgb(33, 47, 64));
        for (var i = 0; i <= 5; i++)
        {
            var y = top + plotH * i / 5.0;
            CurveCanvas.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1, Opacity = .55 });
            var valueLabel = new TextBlock { Text = (_valueMax - (_valueMax - _valueMin) * i / 5).ToString("0.##"), Foreground = Brushes.SlateGray, FontSize = 10 };
            Canvas.SetLeft(valueLabel, 3);
            Canvas.SetTop(valueLabel, y - 6);
            CurveCanvas.Children.Add(valueLabel);
        }
        for (var i = 0; i <= 10; i++)
        {
            var x = left + plotW * i / 10.0;
            CurveCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + plotH, Stroke = gridBrush, StrokeThickness = 1, Opacity = .38 });
            var label = new TextBlock
            {
                Text = FramesCheck.IsChecked == true
                    ? Math.Round((_timeMin + duration * i / 10.0) * 60).ToString("0000")
                    : (_timeMin + duration * i / 10.0).ToString("0.##", CultureInfo.InvariantCulture),
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
        polyline.Clip = new RectangleGeometry(new Rect(PlotLeft, PlotTop,
            Math.Max(1, CurveCanvas.ActualWidth - PlotLeft - PlotRight),
            Math.Max(1, CurveCanvas.ActualHeight - PlotTop - PlotBottom)));
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
            if (keyTime < _timeMin || keyTime > _timeMax) continue;
            var point = points.MinBy(p => Math.Abs(p.Time - keyTime));
            var value = point is null ? null : selector(point);
            if (!value.HasValue || !double.IsFinite(value.Value)) continue;
            if (value.Value < _valueMin || value.Value > _valueMax) continue;
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
            ("视野角度", FovCheck.IsChecked == true ? point.ActionFov : null, Color.FromRgb(185, 122, 236)),
            ("镜头距离", DistanceCheck.IsChecked == true ? point.ActionDistance : null, Color.FromRgb(88, 210, 236)),
            ("位置 X", PositionXCheck.IsChecked == true ? point.PositionX : null, Color.FromRgb(255, 107, 107)),
            ("位置 Y", PositionYCheck.IsChecked == true ? point.PositionY : null, Color.FromRgb(113, 216, 137)),
            ("位置 Z", PositionZCheck.IsChecked == true ? point.PositionZ : null, Color.FromRgb(98, 168, 255)),
            ("Pitch", PitchCheck.IsChecked == true ? point.Pitch : null, Color.FromRgb(255, 180, 84)),
            ("Yaw", YawCheck.IsChecked == true ? point.Yaw : null, Color.FromRgb(240, 111, 195)),
            ("Roll", RollCheck.IsChecked == true ? point.Roll : null, Color.FromRgb(229, 232, 107))
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
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _boxing = true;
            _boxStart = e.GetPosition(CurveCanvas);
            CurveCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        _dragging = true;
        CurveCanvas.CaptureMouse();
        SeekFromPoint(e.GetPosition(CurveCanvas));
        e.Handled = true;
    }

    private void CurveCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var current = e.GetPosition(CurveCanvas);
        if (_panning)
        {
            var dt = (current.X - _lastMouse.X) / Math.Max(1, CurveCanvas.ActualWidth - PlotLeft - PlotRight) * (_timeMax - _timeMin);
            var dv = (current.Y - _lastMouse.Y) / Math.Max(1, CurveCanvas.ActualHeight - PlotTop - PlotBottom) * (_valueMax - _valueMin);
            _timeMin -= dt; _timeMax -= dt;
            _valueMin += dv; _valueMax += dv;
            _lastMouse = current;
            Draw();
            e.Handled = true;
            return;
        }
        if (_boxing)
        {
            Draw();
            var rectangle = new Rectangle { Width = Math.Abs(current.X - _boxStart.X), Height = Math.Abs(current.Y - _boxStart.Y),
                Stroke = Brushes.LightSkyBlue, Fill = new SolidColorBrush(Color.FromArgb(35, 80, 170, 240)), IsHitTestVisible = false };
            Canvas.SetLeft(rectangle, Math.Min(current.X, _boxStart.X));
            Canvas.SetTop(rectangle, Math.Min(current.Y, _boxStart.Y));
            CurveCanvas.Children.Add(rectangle);
            return;
        }
        if (!_dragging) return;
        SeekFromPoint(e.GetPosition(CurveCanvas));
        e.Handled = true;
    }

    private void CurveCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_boxing)
        {
            var end = e.GetPosition(CurveCanvas);
            if (Math.Abs(end.X - _boxStart.X) > 8 && Math.Abs(end.Y - _boxStart.Y) > 8)
            {
                double T(double px) => _timeMin + Math.Clamp((px - PlotLeft) / Math.Max(1, CurveCanvas.ActualWidth - PlotLeft - PlotRight), 0, 1) * (_timeMax - _timeMin);
                double V(double py) => _valueMax - Math.Clamp((py - PlotTop) / Math.Max(1, CurveCanvas.ActualHeight - PlotTop - PlotBottom), 0, 1) * (_valueMax - _valueMin);
                var t1 = T(Math.Min(end.X, _boxStart.X)); var t2 = T(Math.Max(end.X, _boxStart.X));
                var v1 = V(Math.Max(end.Y, _boxStart.Y)); var v2 = V(Math.Min(end.Y, _boxStart.Y));
                if (t2 - t1 > 1e-6 && v2 - v1 > 1e-6) { _timeMin = t1; _timeMax = t2; _valueMin = v1; _valueMax = v2; }
            }
            _boxing = false;
            CurveCanvas.ReleaseMouseCapture();
            Draw();
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        CurveCanvas.ReleaseMouseCapture();
        SeekFromPoint(e.GetPosition(CurveCanvas));
        e.Handled = true;
    }

    private void CurveCanvas_LostMouseCapture(object sender, MouseEventArgs e) { _dragging = false; _panning = false; _boxing = false; }

    private void CurveCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = true;
        _lastMouse = e.GetPosition(CurveCanvas);
        CurveCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void CurveCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = false;
        CurveCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CurveCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var point = e.GetPosition(CurveCanvas);
        var factor = e.Delta > 0 ? .8 : 1.25;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var ratio = Math.Clamp((point.Y - PlotTop) / Math.Max(1, CurveCanvas.ActualHeight - PlotTop - PlotBottom), 0, 1);
            var anchor = _valueMax - ratio * (_valueMax - _valueMin);
            var span = Math.Clamp((_valueMax - _valueMin) * factor, .001, 1e9);
            _valueMax = anchor + ratio * span; _valueMin = _valueMax - span;
        }
        else
        {
            var ratio = Math.Clamp((point.X - PlotLeft) / Math.Max(1, CurveCanvas.ActualWidth - PlotLeft - PlotRight), 0, 1);
            var anchor = _timeMin + ratio * (_timeMax - _timeMin);
            var span = Math.Clamp((_timeMax - _timeMin) * factor, 1.0 / 600, Math.Max(1, _snapshot?.Duration ?? 1) * 20);
            _timeMin = anchor - ratio * span; _timeMax = _timeMin + span;
        }
        Draw();
        e.Handled = true;
    }

    private void SeekFromPoint(Point point)
    {
        if (_snapshot is null || CurveCanvas.ActualWidth <= PlotLeft + PlotRight) return;
        var plotW = CurveCanvas.ActualWidth - PlotLeft - PlotRight;
        var time = _timeMin + Math.Clamp((point.X - PlotLeft) / plotW, 0, 1) * (_timeMax - _timeMin);
        var nearestFrame = Math.Round(time * 60.0) / 60.0;
        SeekRequested?.Invoke(Math.Clamp(nearestFrame, 0, _snapshot.Duration));
    }

    private static string Fmt(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-";

    private string BuildCurrentInfo(CameraCurvePoint point)
    {
        var values = new List<string> { $"t={point.Time:0.000}s" };
        if (FovCheck.IsChecked == true) values.Add($"视野角度={Fmt(point.ActionFov)}");
        if (DistanceCheck.IsChecked == true) values.Add($"镜头距离={Fmt(point.ActionDistance)}");
        if (PositionXCheck.IsChecked == true) values.Add($"X={Fmt(point.PositionX)}");
        if (PositionYCheck.IsChecked == true) values.Add($"Y={Fmt(point.PositionY)}");
        if (PositionZCheck.IsChecked == true) values.Add($"Z={Fmt(point.PositionZ)}");
        if (PitchCheck.IsChecked == true) values.Add($"Pitch={Fmt(point.Pitch)}°");
        if (YawCheck.IsChecked == true) values.Add($"Yaw={Fmt(point.Yaw)}°");
        if (RollCheck.IsChecked == true) values.Add($"Roll={Fmt(point.Roll)}°");
        return string.Join("  ", values);
    }
}
