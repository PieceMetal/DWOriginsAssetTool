using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DWOriginsAssetTool;

public partial class MainWindow
{
    // Unlit ribbons keep a constant width in WPF DIPs while sharing scene depth with the character.
    private static Material HelperMaterial(Color color)
    {
        // WPF emissive-only geometry does not establish opaque depth. The black
        // diffuse pass writes depth without lighting the annotation; emission adds color.
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Black));
        material.Children.Add(new EmissiveMaterial(Brushes.White) { Color = color });
        material.Freeze();
        return material;
    }

    private static void AddHelperLine(Viewport3D view, Model3DGroup group, Point3D a, Point3D b, double width, Material material)
    {
        if (view.Camera is not PerspectiveCamera camera || view.ActualWidth <= 1) return;
        var forward = camera.LookDirection;
        if (forward.LengthSquared < 1e-12) return;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection);
        if (right.LengthSquared < 1e-12) return;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var da = Vector3D.DotProduct(a - camera.Position, forward);
        var db = Vector3D.DotProduct(b - camera.Position, forward);
        double near = Math.Max(camera.NearPlaneDistance, 0.0001);
        if (da < near && db < near) return;
        if (da < near) { a += (b - a) * ((near - da) / (db - da)); da = near; }
        if (db < near) { b += (a - b) * ((near - db) / (da - db)); db = near; }
        double dx = Vector3D.DotProduct(b - camera.Position, right) / db - Vector3D.DotProduct(a - camera.Position, right) / da;
        double dy = Vector3D.DotProduct(b - camera.Position, up) / db - Vector3D.DotProduct(a - camera.Position, up) / da;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-12) return;
        var side = right * (-dy / length) + up * (dx / length);
        double unit = Math.Tan(camera.FieldOfView * Math.PI / 360) / view.ActualWidth * width;
        var sa = side * (da * unit);
        var sb = side * (db * unit);
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { a - sa, a + sa, b + sb, b - sb },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        mesh.Freeze();
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private static void AddHelperMarker(Viewport3D view, Model3DGroup group, Point3D position, double width, Material material)
    {
        if (view.Camera is not PerspectiveCamera camera || view.ActualWidth <= 1) return;
        var forward = camera.LookDirection;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection);
        if (right.LengthSquared < 1e-12) return;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double depth = Vector3D.DotProduct(position - camera.Position, forward);
        if (depth <= camera.NearPlaneDistance) return;
        double radius = depth * 2 * Math.Tan(camera.FieldOfView * Math.PI / 360) / view.ActualWidth * 4;
        AddHelperLine(view, group, position - right * radius, position + right * radius, width, material);
        AddHelperLine(view, group, position - up * radius, position + up * radius, width, material);
    }

    private static void AddHelperDashedLine(Viewport3D view, Model3DGroup group, Point3D a, Point3D b, double width, Material material)
    {
        if (view.Camera is not PerspectiveCamera camera || view.ActualWidth <= 1) return;
        var forward = camera.LookDirection;
        forward.Normalize();
        double da = Vector3D.DotProduct(a - camera.Position, forward);
        double db = Vector3D.DotProduct(b - camera.Position, forward);
        double near = Math.Max(camera.NearPlaneDistance, 0.0001);
        if (da < near && db < near) return;
        if (da < near) { a += (b - a) * ((near - da) / (db - da)); da = near; }
        if (db < near) { b += (a - b) * ((near - db) / (da - db)); db = near; }
        var right = Vector3D.CrossProduct(forward, camera.UpDirection);
        if (right.LengthSquared < 1e-12) return;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double dx = Vector3D.DotProduct(b - camera.Position, right) / db - Vector3D.DotProduct(a - camera.Position, right) / da;
        double dy = Vector3D.DotProduct(b - camera.Position, up) / db - Vector3D.DotProduct(a - camera.Position, up) / da;
        double pixels = Math.Sqrt(dx * dx + dy * dy) * view.ActualWidth / (2 * Math.Tan(camera.FieldOfView * Math.PI / 360));
        if (pixels < 0.01 || !double.IsFinite(pixels)) return;
        // Perspective-correct interpolation keeps dash lengths constant on screen.
        Point3D At(double t) => a + (b - a) * (t * da / ((1 - t) * db + t * da));
        double step = Math.Max(11, pixels / 512);
        for (double offset = 0; offset < pixels; offset += step)
            AddHelperLine(view, group, At(offset / pixels), At(Math.Min(offset + step * 0.6, pixels) / pixels), width, material);
    }

    private static void AddHelperDot(Viewport3D view, Model3DGroup group, Point3D position, Material material)
    {
        if (view.Camera is not PerspectiveCamera camera || view.ActualWidth <= 1) return;
        var forward = camera.LookDirection;
        forward.Normalize();
        double depth = Vector3D.DotProduct(position - camera.Position, forward);
        if (depth <= camera.NearPlaneDistance) return;
        var right = Vector3D.CrossProduct(forward, camera.UpDirection);
        if (right.LengthSquared < 1e-12) return;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double radius = depth * 2 * Math.Tan(camera.FieldOfView * Math.PI / 360) / view.ActualWidth * 3.5;
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(position);
        for (int i = 0; i < 20; i++)
        {
            double angle = i * Math.PI * 2 / 20;
            mesh.Positions.Add(position + radius * (right * Math.Cos(angle) + up * Math.Sin(angle)));
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(i + 1); mesh.TriangleIndices.Add((i + 1) % 20 + 1);
        }
        mesh.Freeze();
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private object? _helperViewState;
    private void RefreshHelperView()
    {
        object state = (ViewportCamera.Position, ViewportCamera.LookDirection, ViewportCamera.UpDirection, ViewportCamera.FieldOfView,
            Viewport.ActualWidth, Viewport.ActualHeight, ComparisonCamera.Position, ComparisonCamera.LookDirection,
            ComparisonCamera.UpDirection, ComparisonCamera.FieldOfView, ComparisonViewport.ActualWidth, ComparisonViewport.ActualHeight);
        if (state.Equals(_helperViewState)) return;
        _helperViewState = state;
        UpdateCameraGizmo((float)_playbackTime);
        DrawCameraTrajectoryProgress((float)_playbackTime, force: true);
    }
}
