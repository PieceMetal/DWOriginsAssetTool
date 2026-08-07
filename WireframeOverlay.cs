using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DWOriginsAssetTool;

public readonly record struct WireframeEdge(int MeshIndex, int A, int B);

/// <summary>
/// Draws a sampled, real mesh wireframe as one retained WPF geometry.  This avoids
/// creating thousands of Line controls and keeps orbiting responsive.
/// </summary>
public sealed class WireframeOverlay : FrameworkElement
{
    private static readonly Pen WirePen = CreatePen();
    private IReadOnlyList<MeshGeometry3D> _meshes = Array.Empty<MeshGeometry3D>();
    private IReadOnlyList<WireframeEdge> _edges = Array.Empty<WireframeEdge>();

    public PerspectiveCamera? Camera { get; set; }

    public void SetGeometry(IReadOnlyList<MeshGeometry3D> meshes, IReadOnlyList<WireframeEdge> edges)
    {
        _meshes = meshes;
        _edges = edges;
        InvalidateVisual();
    }

    public void RequestRender() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Camera is null || _edges.Count == 0 || ActualWidth <= 1 || ActualHeight <= 1) return;

        var forward = Camera.LookDirection;
        if (forward.LengthSquared < 1e-10) return;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, Camera.UpDirection);
        if (right.LengthSquared < 1e-10) return;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();
        var aspect = ActualWidth / ActualHeight;
        var tan = Math.Tan(Camera.FieldOfView * Math.PI / 360d);

        bool Project(Point3D world, out Point point)
        {
            var relative = world - Camera.Position;
            var depth = Vector3D.DotProduct(relative, forward);
            if (depth <= Camera.NearPlaneDistance) { point = default; return false; }
            var x = Vector3D.DotProduct(relative, right) / (depth * tan * aspect);
            var y = Vector3D.DotProduct(relative, up) / (depth * tan);
            point = new Point((x + 1) * ActualWidth * .5, (1 - y) * ActualHeight * .5);
            return x is > -1.2 and < 1.2 && y is > -1.2 and < 1.2;
        }

        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
        {
            foreach (var edge in _edges)
            {
                if ((uint)edge.MeshIndex >= (uint)_meshes.Count) continue;
                var positions = _meshes[edge.MeshIndex].Positions;
                if ((uint)edge.A >= (uint)positions.Count || (uint)edge.B >= (uint)positions.Count) continue;
                if (!Project(positions[edge.A], out var a) || !Project(positions[edge.B], out var b)) continue;
                context.BeginFigure(a, false, false);
                context.LineTo(b, true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, WirePen, geometry);
    }

    private static Pen CreatePen()
    {
        var brush = new SolidColorBrush(Color.FromRgb(116, 202, 255));
        brush.Freeze();
        var pen = new Pen(brush, .72) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
