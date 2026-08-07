using DWOriginsAssetTool.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Numerics;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Point3D = System.Windows.Media.Media3D.Point3D;
using Vector3D = System.Windows.Media.Media3D.Vector3D;

namespace DWOriginsAssetTool;

public partial class MainWindow : Window
{
    private List<AssetEntry> _all = [], _filtered = [];
    private string _root = "";
    private G1MInfo? _model;
    private G1AClip? _clip;
    private AssetEntry? _animationAsset;
    private IReadOnlyDictionary<string, ActionCameraCatalog.Binding> _cameraCatalog =
        new Dictionary<string, ActionCameraCatalog.Binding>(StringComparer.OrdinalIgnoreCase);
    private ActionCameraClip? _actionCamera;
    private AssetEntry? _modelAsset;
    private readonly DispatcherTimer _animationTimer;
    private DateTime _lastTick;
    private bool _sliderInternal;
    private double _playbackTime;
    private AssetEntry? _selected;
    private string? _previewingPath;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private GameNameCatalog? _nameCatalog;
    private string? _nameCatalogRoot;
    private readonly ObservableCollection<BoneTreeNode> _boneRoots = [];
    private int _renderMode = 1;
    private bool _showGrid = true, _showAxes = true, _showSkeleton;
    private double _yaw = -0.38, _pitch = 0.35, _zoom = 1;
    private Point3D _cameraTarget;
    private Point _dragStart; private bool _dragging, _orbiting, _panning;
    private readonly Model3DGroup _scene = new();
    private readonly Model3DGroup _cameraGizmo = new();
    private readonly Model3DGroup _cameraTrajectory = new();
    private readonly Model3DGroup _cameraTrajectoryCursor = new();
    private readonly Model3DGroup _weaponGroup = new();
    private readonly List<MeshGeometry3D> _weaponGeometries = [];
    private G1MInfo? _weaponModel;
    private AssetEntry? _weaponAsset;
    private ModelAttachmentComposer.Attachment? _weaponAttachment;
    private const int DefaultWeaponAttachmentBoneId = 104;
    private readonly List<MeshGeometry3D> _meshGeometries = [];
    private readonly List<GeometryModel3D> _meshModels = [];
    private readonly List<Material> _texturedMaterials = [];
    private readonly List<Material> _solidMaterials = [];
    private IReadOnlyList<WireframeEdge> _wireframeEdges = [];
    private bool _cameraDirty, _overlayDirty;
    private Point3D _sceneCenter;
    private double _sceneRadius = 1;
    private Vector3[]? _animatedBonePositions;
    private HashSet<int>? _animatedBoneIds;
    private Matrix4x4 _accessoryAdjustment = Matrix4x4.Identity;
    private bool _updatingAccessoryEditor;
    private readonly Dictionary<int, ImageBrush> _characterTextures = [];
    private bool _startupPreviewHandled;
    private bool _exporting;

    private sealed record BoneChoice(int LocalId, int GlobalId)
    {
        public string DisplayName => $"bone_{GlobalId} · L{LocalId}";
    }

    // 与参考 RE 工具统一为 UE 风格世界：X 侧向、Y 向前、Z 向上。
    // G1M 本身是 Y 向上，只在渲染边界换基底，动画/蒙皮计算保持源格式。
    private sealed record QuickCharacterChoice(string Id, string DisplayName, AssetEntry Model);
    private sealed record QuickWeaponChoice(string Id, string DisplayName, AssetEntry Model);
    private sealed record QuickAnimationChoice(string DisplayName, AssetEntry Animation);

    private readonly Dictionary<string, QuickCharacterChoice> _quickCharacters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuickWeaponChoice> _quickWeapons = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingQuickPreview;

    private static readonly IReadOnlyDictionary<string, string> DefaultWeaponPrefixes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["H0100_"] = "HW0002_",
            ["H0000_"] = "HW0006_", ["H0001_"] = "HW0034_", ["H0002_"] = "HW0001_",
            ["H0003_"] = "HW0042_", ["H0004_"] = "HW0032_", ["H0005_"] = "HW0010_",
            ["H0008_"] = "HW0000_", ["H0009_"] = "HW0007_", ["H0010_"] = "HW0008_",
            ["H0012_"] = "HW0042_", ["H0014_"] = "HW0009_", ["H0015_"] = "HW0002_",
            ["H0017_"] = "HW0002_", ["H0022_"] = "HW0031_", ["H0031_"] = "HW0004_",
            ["H0041_"] = "HW0039_"
        };

    private static Point3D ToWorld(float x, float y, float z) => new(-x, z, y);
    private static Vector3D ToWorldNormal(float x, float y, float z) => new(-x, z, y);
    private static Vector3 ToWorld(Vector3 v) => new(-v.X, v.Z, v.Y);
    private const double ExportFilmbackHeightMm = 24d;
    private static double VerticalToHorizontalFov(double verticalDegrees, double aspectRatio)
    {
        var verticalRadians = verticalDegrees * Math.PI / 180d;
        return 2d * Math.Atan(Math.Tan(verticalRadians * .5d) * Math.Max(.01d, aspectRatio)) * 180d / Math.PI;
    }

    private double CameraViewportAspect => Viewport.ActualHeight > 1
        ? Math.Max(.01, Viewport.ActualWidth / Viewport.ActualHeight)
        : 16d / 9d;

    public MainWindow()
    {
        InitializeComponent();
        Dispatcher.UnhandledException += MainWindow_DispatcherUnhandledException;
        Viewport.Children.Add(new ModelVisual3D { Content = _scene });
        WireframeLayer.Camera = ViewportCamera;
        BoneTree.ItemsSource = _boneRoots;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        _animationTimer.Tick += AnimationTimer_Tick;
        CompositionTarget.Rendering += Viewport_Rendering;
        Closed += (_, _) => CompositionTarget.Rendering -= Viewport_Rendering;
        Loaded += async (_, _) =>
        {
            if (!UserPreferences.HideStartupGuide)
                ShowUserGuide();
            var guessed = FindGameRoot(AppContext.BaseDirectory) ?? FindGameRoot(Environment.CurrentDirectory);
            if (guessed is not null) { _root = guessed; await ScanAsync(); }
        };
    }

    private void MainWindow_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _animationTimer.Stop();
        PlayButton.IsEnabled = false;
        StatusText.Text = $"预览失败：{e.Exception.Message}";
        PreviewSummary.Text = "动画预览遇到异常，模型仍可继续选择";
    }

    private static string? FindGameRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "File")) && File.Exists(Path.Combine(dir.FullName, "DWORIGINS.exe"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root)) return;
        IsEnabled = false; StatusText.Text = "正在扫描资源，请稍候…";
        try
        {
            var organizedRoot = Path.Combine(_root, "DWOrigins_Unpacked");
            var scanRoot = Directory.Exists(organizedRoot) ? organizedRoot
                : Directory.Exists(Path.Combine(_root, "File")) ? Path.Combine(_root, "File") : _root;
            var progress = new Progress<(int Count, string Path)>(p => StatusText.Text = $"已识别 {p.Count:N0} 个资源… {Path.GetFileName(p.Path)}");
            var scanTask = AssetScanner.ScanAsync(scanRoot, progress);
            var catalogTask = _nameCatalog is not null && _nameCatalogRoot == _root
                ? Task.FromResult(_nameCatalog)
                : KidsNameCatalogReader.BuildAsync(_root, new Progress<(int Done, int Total, string File)>(p =>
                    StatusText.Text = $"正在恢复正式名称 {p.Done:N0}/{p.Total:N0} · {p.File}"));
            var manifestTask = scanRoot.Equals(organizedRoot, StringComparison.OrdinalIgnoreCase)
                ? Task.Run(() => OrganizedResourceCatalog.ReadManifestResourceIds(organizedRoot))
                : Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            await Task.WhenAll(scanTask, catalogTask, manifestTask);
            _cameraCatalog = await Task.Run(() => ActionCameraCatalog.Build(_root));
            _all = await scanTask;
            _nameCatalog = await catalogTask;
            _nameCatalogRoot = _root;
            var restoredIds = 0;
            if (scanRoot.Equals(organizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                restoredIds = OrganizedResourceCatalog.ApplyResourceIds(_all, await manifestTask);
                foreach (var asset in _all) asset.RestoreOfficialNameFromOrganizedPath();
            }
            else ApplyOfficialNames(_all, _nameCatalog);
            var gameAnimations = OrganizedResourceCatalog.ApplyGameAnimationCatalog(_all, _root, _cameraCatalog);
            StatusText.Text = $"正在建立目录树 · 已恢复 {restoredIds:N0} 个清单资源 ID、{gameAnimations:N0} 个游戏本体动作表归属";
            foreach (var a in _all.Where(a => a.Kind is AssetKind.Model or AssetKind.Animation or AssetKind.Skeleton or AssetKind.Texture or AssetKind.Package).Take(100))
                _ = AssetScanner.ValidateMagic(a.FullPath, a.Kind);
            DomainBox.Items.Clear(); DomainBox.Items.Add("全部资源域");
            foreach (var domain in _all.Select(x => x.Domain).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)) DomainBox.Items.Add(domain);
            DomainBox.SelectedIndex = 0; ApplyFilter();
            BuildQuickPreviewCatalog();
            StatusText.Text = $"扫描完成：{_all.Count:N0} 个资源 · {_nameCatalog.Bindings?.Count ?? 0:N0} 条正式名称映射 · {gameAnimations:N0} 个游戏本体动作表归属 · {_cameraCatalog.Count:N0} 组动画镜头。";
            await ApplyStartupPreviewAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error); StatusText.Text = "扫描失败"; }
        finally { IsEnabled = true; }
    }

    private async Task ApplyStartupPreviewAsync()
    {
        if (_startupPreviewHandled) return;
        _startupPreviewHandled = true;
        var args = Environment.GetCommandLineArgs();
        static string? Value(string[] values, string key)
        {
            var index = Array.FindIndex(values, x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
        }
        var modelId = Value(args, "--model");
        var animationId = Value(args, "--animation");
        if (modelId is not null)
        {
            var model = _all.FirstOrDefault(x => x.Kind == AssetKind.Model &&
                x.HashId.Equals(modelId, StringComparison.OrdinalIgnoreCase));
            if (model is not null) await PreviewAssetAsync(model);
        }
        if (animationId is not null)
        {
            var animation = _all.FirstOrDefault(x => x.Kind == AssetKind.Animation &&
                x.HashId.Equals(animationId, StringComparison.OrdinalIgnoreCase));
            if (animation is not null) await PreviewAssetAsync(animation);
        }
        else if (animationId is null && StartupHeroCheck.IsChecked == true)
        {
            // Hero0's verified body resource is the neutral game skeleton used
            // for all shared character and weapon action previews.
            var hero = _all.FirstOrDefault(x => x.Kind == AssetKind.Model &&
                x.HashId.Equals("635c4e68", StringComparison.OrdinalIgnoreCase))
                ?? _all.Where(x => x.Kind == AssetKind.Model &&
                    (string.Equals(x.OwnerName, "H0100_Hero0", StringComparison.OrdinalIgnoreCase) ||
                     (x.OfficialName?.StartsWith("H0100_Hero0", StringComparison.OrdinalIgnoreCase) ?? false)))
                    .OrderBy(x => x.HashId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            if (hero is not null) await PreviewAssetAsync(hero);
        }
        if (double.TryParse(Value(args, "--time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
            SeekPlayback(time);
        if (args.Any(x => x.Equals("--play", StringComparison.OrdinalIgnoreCase)) && PlayButton.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _animationTimer.Start();
            PlayButton.Content = "⏸";
        }
    }

    private void ApplyFilter()
    {
        if (!IsLoaded) return;
        var query = SearchBox.Text.Trim();
        var tag = (KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var domain = DomainBox.SelectedItem?.ToString();
        IEnumerable<AssetEntry> q = _all;
        if (tag != "All" && Enum.TryParse<AssetKind>(tag, out var kind)) q = q.Where(x => x.Kind == kind);
        if (!string.IsNullOrEmpty(domain) && domain != "全部资源域") q = q.Where(x => x.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
        if (query.Length > 0) q = q.Where(x => x.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        _filtered = q.ToList(); ShowTree();
    }

    private void ShowTree()
    {
        AssetTree.ItemsSource = ResourceTreeNode.CreateRoots(_filtered);
        CountText.Text = $"当前 {_filtered.Count:N0} / 总计 {_all.Count:N0}";
    }

    private static void ApplyOfficialNames(IEnumerable<AssetEntry> assets, GameNameCatalog catalog)
    {
        if (catalog.Bindings is null) return;
        var byResource = catalog.Bindings
            .GroupBy(x => x.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var ordinalByOwnerAndKind = catalog.Bindings
            .GroupBy(x => (x.OfficialName, x.ResourceKind))
            .SelectMany(group => group.Select((binding, index) =>
                (Key: $"{binding.OfficialName}\0{binding.ResourceKind}\0{binding.ResourceId}", Ordinal: index + 1, Total: group.Count())))
            .ToDictionary(x => x.Key, x => (x.Ordinal, x.Total), StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            if (!byResource.TryGetValue(asset.HashId, out var bindings)) continue;
            var labels = new List<string>();
            foreach (var binding in bindings.DistinctBy(x => (x.OfficialName, x.ResourceKind)))
            {
                var key = $"{binding.OfficialName}\0{binding.ResourceKind}\0{binding.ResourceId}";
                var (ordinal, total) = ordinalByOwnerAndKind[key];
                labels.Add(total > 1
                    ? $"{binding.OfficialName} · {binding.ResourceKind} {ordinal:D3}"
                    : $"{binding.OfficialName} · {binding.ResourceKind}");
            }
            asset.OfficialName = string.Join(" + ", labels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            asset.OwnerName = bindings.Select(x => x.OfficialName).FirstOrDefault();
        }
    }

    private void BuildQuickPreviewCatalog()
    {
        _quickCharacters.Clear();
        _quickWeapons.Clear();
        var characterModels = _all.Where(x => x.Kind == AssetKind.Model &&
                x.OwnerName is { Length: > 0 } owner && owner.StartsWith("H", StringComparison.OrdinalIgnoreCase) &&
                !owner.StartsWith("HW", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.OwnerName!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Size).ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(x => x.OwnerName, StringComparer.OrdinalIgnoreCase);
        foreach (var model in characterModels)
        {
            var id = model.OwnerName!;
            var label = OfficialNameAnnotations.Format(id);
            _quickCharacters[id] = new(id, label, model);
        }

        var weapons = _all.Where(x => x.Kind == AssetKind.Model &&
                x.OwnerName is { Length: > 0 } owner && owner.StartsWith("HW", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase);
        foreach (var weapon in weapons)
        {
            var id = weapon.OwnerName! + "\0" + weapon.HashId;
            _quickWeapons[id] = new(id, weapon.AnnotatedDisplayName, weapon);
        }

        _updatingQuickPreview = true;
        QuickCharacterBox.ItemsSource = _quickCharacters.Values.ToList();
        QuickCharacterBox.IsEnabled = _quickCharacters.Count > 0;
        QuickCharacterBox.SelectedItem = _quickCharacters.Values
            .FirstOrDefault(x => x.Id.Equals("H0100_Hero0", StringComparison.OrdinalIgnoreCase))
            ?? _quickCharacters.Values.FirstOrDefault();
        _updatingQuickPreview = false;
        if (QuickCharacterBox.SelectedItem is QuickCharacterChoice selected)
            _ = ApplyQuickCharacterAsync(selected);
    }

    private void BuildQuickWeapons(string characterId, AssetEntry? preferred = null)
    {
        _updatingQuickPreview = true;
        var values = _quickWeapons.Values.ToList();
        QuickWeaponBox.ItemsSource = values;
        QuickWeaponBox.IsEnabled = values.Count > 0;
        var prefix = DefaultWeaponPrefixes.FirstOrDefault(x => characterId.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase)).Value;
        var choice = preferred is not null
            ? values.FirstOrDefault(x => ReferenceEquals(x.Model, preferred) || x.Model.HashId.Equals(preferred.HashId, StringComparison.OrdinalIgnoreCase))
            : string.IsNullOrWhiteSpace(prefix) ? null
                : values.FirstOrDefault(x => x.Model.OwnerName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
        QuickWeaponBox.SelectedItem = choice;
        _updatingQuickPreview = false;
    }

    private void BuildQuickAnimations(string characterId)
    {
        var values = _all.Where(x => x.Kind == AssetKind.Animation && !x.IsActionCamera &&
                ((x.GameActionOwners?.Any(owner => owner.Equals(characterId, StringComparison.OrdinalIgnoreCase)) ?? false) ||
                 string.Equals(x.OwnerName, characterId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.TreeDisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new QuickAnimationChoice(x.AnnotatedDisplayName, x))
            .ToList();
        QuickAnimationBox.ItemsSource = values;
        QuickAnimationBox.IsEnabled = values.Count > 0;
        QuickAnimationBox.SelectedItem = null;
        QuickPreviewHint.Text = values.Count == 0
            ? $"{OfficialNameAnnotations.Format(characterId)} 未找到可直接播放的角色动作。"
            : $"{OfficialNameAnnotations.Format(characterId)}：已找到 {values.Count:N0} 个角色动作；选择动画即可播放。";
    }

    private QuickWeaponChoice? FindDefaultQuickWeapon(string characterId)
    {
        var prefix = DefaultWeaponPrefixes.FirstOrDefault(p => characterId.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(prefix) ? null : _quickWeapons.Values.FirstOrDefault(x =>
            x.Model.OwnerName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
    }

    private async void QuickCharacter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingQuickPreview || QuickCharacterBox.SelectedItem is not QuickCharacterChoice choice) return;
        await ApplyQuickCharacterAsync(choice);
    }

    private async Task ApplyQuickCharacterAsync(QuickCharacterChoice choice)
    {
        _updatingQuickPreview = true;
        await PreviewAssetAsync(choice.Model);
        BuildQuickWeapons(choice.Id);
        BuildQuickAnimations(choice.Id);
        var defaultWeapon = FindDefaultQuickWeapon(choice.Id);
        if (defaultWeapon is not null)
        {
            QuickWeaponBox.SelectedItem = defaultWeapon;
            await LoadAccessoryAssetAsync(defaultWeapon.Model);
        }
        _updatingQuickPreview = false;
        StatusText.Text = $"已加载 {choice.DisplayName} · 默认武器与角色动作已准备";
    }

    private async void QuickWeapon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingQuickPreview || QuickWeaponBox.SelectedItem is not QuickWeaponChoice choice) return;
        await LoadAccessoryAssetAsync(choice.Model);
    }

    private async void QuickAnimation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingQuickPreview || QuickAnimationBox.SelectedItem is not QuickAnimationChoice choice) return;
        await PreviewAssetAsync(choice.Animation);
    }

    private void AssetTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: ResourceTreeNode node }) node.LoadChildren();
    }

    private async void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not ResourceTreeNode { Asset: { } asset }) return;
        _selected = asset;
        DetailTitle.Text = asset.AnnotatedDisplayName; DetailPath.Text = asset.RelativePath;
        RelationBody.Text = BuildRelationText(asset);
        UpdateActionCameraInspector(asset);
        DetailBody.Text = "双击资源以加载预览。";
        if (asset.Kind == AssetKind.Animation)
        {
            StatusText.Text = $"正在加载动画 {asset.AnnotatedDisplayName} · 将叠加到当前模型";
            await PreviewAssetAsync(asset);
        }
        else StatusText.Text = $"已选择 {asset.AnnotatedDisplayName} · 双击加载预览";
    }

    private async void AssetTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AssetTree.SelectedItem is not ResourceTreeNode { Asset: { } asset }) return;
        await PreviewAssetAsync(asset);
        e.Handled = true;
    }

    private async Task PreviewAssetAsync(AssetEntry asset)
    {
        if (string.Equals(_previewingPath, asset.FullPath, StringComparison.OrdinalIgnoreCase)) return;
        _previewingPath = asset.FullPath;
        await _previewGate.WaitAsync();
        _selected = asset;
        // The preview model is user-owned state: only selecting another model may replace it.
        // Choosing animations, camera clips, packages or other resources must not silently clear it.
        if (asset.Kind == AssetKind.Model)
        {
            _animationTimer.Stop(); _clip = null; _actionCamera = null; _animationAsset = null;
            WeaponCheck.IsChecked = false; WeaponCheck.IsEnabled = false;
            _weaponAsset = null; _weaponModel = null; _weaponAttachment = null;
            _weaponGeometries.Clear(); _weaponGroup.Children.Clear();
            PlayButton.IsEnabled = false; AnimationSlider.IsEnabled = false;
            CameraModeCombo.IsEnabled = false; CameraModeCombo.SelectedIndex = 0;
            CameraTrajectoryCheck.IsEnabled = false; CameraTrajectoryCheck.IsChecked = false;
            _cameraTrajectory.Children.Clear(); _cameraTrajectoryCursor.Children.Clear();
            LinkedCameraModeItem.Content = "无动画镜头";
        }
        EmptyState.Visibility = Visibility.Collapsed;
        DetailTitle.Text = asset.AnnotatedDisplayName; DetailPath.Text = asset.RelativePath;
        RelationBody.Text = BuildRelationText(asset);
        UpdateActionCameraInspector(asset);
        StatusText.Text = $"正在读取 {asset.AnnotatedDisplayName}…";
        string? completionStatus = null;
        try
        {
            if (asset.Kind == AssetKind.Model)
            {
                await LoadModelAssetAsync(asset, true);
            }
            else if (asset.Kind == AssetKind.Animation)
            {
                var a = await Task.Run(() => G1Parsers.ReadAnimation(asset.FullPath));
                if (asset.IsActionCamera)
                {
                    _animationTimer.Stop(); PlayButton.Content = "▶";
                    DetailBody.Text = $"资源类型：动画镜头\n镜头动画版本：{a.Version}\n文件大小：{asset.SizeText}\n时长：{a.Duration:0.###} 秒\n镜头变换轨道：{a.TrackCount}\n\n该资源由角色动作自动调用，不会作为骨骼动画套到人物上。请选择与它关联的角色动作，再把播放栏切换到“动画镜头”。";
                    PreviewSummary.Text = $"{asset.AnnotatedDisplayName} · 动画镜头资源 · {a.Duration:0.###} 秒";
                    StatusText.Text = $"已识别动画镜头 {asset.AnnotatedDisplayName}；请选择对应角色动作进行同步播放";
                    return;
                }
                _animationAsset = asset;
                string? clipError = null;
                string? cameraError = null;
                try { _clip = await Task.Run(() => G1Parsers.ReadAnimationClip(asset.FullPath)); }
                catch (Exception clipEx) { _clip = null; clipError = clipEx.Message; }
                _actionCamera = null;
                if (!string.IsNullOrWhiteSpace(asset.ResourceId) && _cameraCatalog.TryGetValue(asset.ResourceId, out var cameraBinding))
                {
                    try { _actionCamera = await Task.Run(() => ActionCameraCatalog.Load(cameraBinding)); }
                    catch (Exception cameraEx) { cameraError = cameraEx.Message; }
                }
                CameraModeCombo.SelectedIndex = 0;
                CameraModeCombo.IsEnabled = _actionCamera is not null;
                CameraGizmoCheck.IsEnabled = _actionCamera is not null;
                CameraTrajectoryCheck.IsEnabled = _actionCamera is not null;
                CameraDataCheck.IsEnabled = _actionCamera is not null;
                if (_actionCamera is null) CameraGizmoCheck.IsChecked = false;
                if (_actionCamera is null) CameraTrajectoryCheck.IsChecked = false;
                if (_actionCamera is null) CameraDataCheck.IsChecked = false;
                LinkedCameraModeItem.Content = _actionCamera is null ? "无动画镜头" : "动画镜头";
                UpdateActionCameraInspector(asset, cameraError);
                DetailBody.Text = $"动画版本：{a.Version}\n文件大小：{asset.SizeText}\n时长：{a.Duration:0.###} 秒\n估算帧数（60 FPS）：{Math.Round(a.Duration * 60):N0}\n骨骼轨道：{a.TrackCount}\n最大骨骼 ID：{a.MaxBoneId}\n动画类型：0x{a.AnimationType:X4}";
                var mapped = _clip is null || _model is null ? 0 : _clip.Tracks.Count(t => FindAnimationBone(_model, _clip, t.GlobalBoneId) is not null);
                var ownershipText = asset.GameActionOwners is { Count: > 0 } owners
                    ? $" · 游戏动作表 {string.Join(" + ", owners.Select(OfficialNameAnnotations.Format))}" : " · 动作表归属未恢复";
                PreviewSummary.Text = _model is null
                    ? $"{asset.AnnotatedDisplayName} · {a.Duration:0.###} 秒 · {a.TrackCount} 条轨道（先选择模型可叠加骨架播放）"
                    : clipError is not null
                        ? $"{asset.AnnotatedDisplayName} · 动画解码失败：{clipError}"
                        : $"{asset.AnnotatedDisplayName} · {a.Duration:0.###} 秒 · 已映射 {mapped}/{_clip?.Tracks.Count ?? 0} 条骨架轨道{ownershipText}";
                if (clipError is not null) StatusText.Text = $"动画无法播放：{clipError}";
                else if (cameraError is not null) StatusText.Text = $"人物动画可播放，但动画镜头读取失败：{cameraError}";
                _playbackTime = 0;
                AnimationSlider.Maximum = Math.Max(.001, a.Duration); AnimationSlider.Value = 0;
                AnimationSlider.IsEnabled = _clip?.Tracks.Count > 0; PlayButton.IsEnabled = AnimationSlider.IsEnabled;
                PlaybackOverlay.Visibility = AnimationSlider.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
                TimelineText.Text = $"0.00 / {a.Duration:0.00} s";
                if (_model is not null && _clip?.Tracks.Count > 0)
                {
                    UpdateCameraTrajectory();
                    RenderAnimatedSkeleton(0);
                    StatusText.Text = mapped > 0
                        ? $"已加载动画：{asset.AnnotatedDisplayName} · 匹配 {mapped}/{_clip.Tracks.Count} 条骨骼轨道"
                        : $"动画与当前角色没有共同骨骼轨道；人物模型保持不变（共 {_clip.Tracks.Count} 条轨道）";
                }
                else RenderTimeline(a);
            }
            else if (asset.Kind == AssetKind.Package)
            {
                var magic = ReadMagic(asset.FullPath);
                var records = asset.Extension == "FDATA" ? await Task.Run(() => FDataUnpacker.ReadIndex(asset.FullPath)) : [];
                DetailBody.Text = $"容器标记：{magic}\n文件大小：{asset.SizeText}\nIDRK 资源记录：{records.Count:N0}\n解压后估算：{records.Sum(x => x.UncompressedSize) / 1048576d:0.00} MB\n\n可使用“解包选中 FData”按模型、动画、骨架、贴图等类型输出，并生成带资源 ID、源偏移和 SHA-256 的清单。";
                PreviewSummary.Text = $"{asset.Name} · 原始资源容器"; RenderPackageCard(magic);
            }
            else
            {
                DetailBody.Text = $"类型：{asset.Kind}\n格式：{asset.Extension}\n文件大小：{asset.SizeText}\n文件头：{ReadMagic(asset.FullPath)}";
                PreviewSummary.Text = $"{asset.Name} · {asset.Kind}"; RenderPackageCard(asset.Kind.ToString());
            }
            StatusText.Text = completionStatus ?? $"已加载 {asset.AnnotatedDisplayName}";
        }
        catch (Exception ex) { _previewingPath = null; DetailBody.Text = "解析失败：" + ex.Message; PreviewSummary.Text = "无法预览此资源"; RenderPackageCard("解析失败"); StatusText.Text = ex.Message; }
        finally { UpdateExportButtons(); _previewGate.Release(); }
    }

    private void UpdateExportButtons()
    {
        if (_exporting) return;
        var hasModel = _model is { Parts.Count: > 0 };
        ExportModelFbxButton.IsEnabled = hasModel;
        ExportAnimationFbxButton.IsEnabled = hasModel && _clip is { Tracks.Count: > 0 };
        ExportCameraFbxButton.IsEnabled = hasModel && _clip is { Tracks.Count: > 0 } && _actionCamera is not null;
    }

    private void BeginExportProgress()
    {
        _exporting = true;
        ExportProgressPanel.Visibility = Visibility.Visible;
        ExportProgressBar.IsIndeterminate = true;
        ExportProgressBar.Value = 0;
        ExportProgressText.Text = "正在准备导出…";
        ExportModelFbxButton.IsEnabled = false;
        ExportAnimationFbxButton.IsEnabled = false;
        ExportCameraFbxButton.IsEnabled = false;
    }

    private void ReportExportProgress((string Stage, int Percent) value)
    {
        ExportProgressText.Text = value.Stage;
        ExportProgressBar.IsIndeterminate = value.Percent is > 30 and < 100;
        ExportProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
    }

    private void EndExportProgress()
    {
        ExportProgressBar.IsIndeterminate = false;
        ExportProgressBar.Value = 100;
        ExportProgressText.Text = "导出完成";
        _exporting = false;
        UpdateExportButtons();
        ExportProgressPanel.Visibility = Visibility.Collapsed;
    }

    private static string BuildRelationText(AssetEntry asset)
    {
        var gameClassification = asset.Kind == AssetKind.Animation
            ? $"\n游戏动作分类：{asset.GameActionCategory ?? "未恢复动作表归属"}" +
              $"\n动作表所有者：{(asset.GameActionOwners is { Count: > 0 } owners ? string.Join(" + ", owners) : "未恢复")}" +
              $"\n分类证据：{asset.ClassificationEvidence ?? "无可证明的 KIDS 正式对象引用链"}" +
              $"\n动画镜头：{asset.ActionCameraResourceId ?? (asset.IsActionCamera ? "本资源" : "无正式镜头引用")}" +
              (asset.ActionCameraEvidence is null ? "" : $"\n镜头证据：{asset.ActionCameraEvidence}")
            : "";
        return $"正式名称：{asset.OfficialName ?? "未恢复"}\n中文备注：{asset.NameAnnotation ?? "暂无"}\n分类：{asset.Kind}\n资源域：{asset.Domain}\n底层资源 ID：{asset.HashId}\n格式：{asset.Extension}{gameClassification}";
    }

    private void UpdateActionCameraInspector(AssetEntry asset, string? cameraError = null)
    {
        if (asset.Kind != AssetKind.Animation)
        {
            ActionCameraTitle.Text = "未选择骨骼动画";
            ActionCameraStatus.Text = "未检测";
            ActionCameraStatus.Foreground = (Brush)FindResource("MutedBrush");
            ActionCameraDetail.Text = _modelAsset is null
                ? "先选择一个人物模型，再选择任意角色的骨骼动画。模型只会在你选择另一个模型时更换。"
                : $"当前模型：{_modelAsset.AnnotatedDisplayName}\n继续选择任意角色动作即可套用到这个模型。";
            return;
        }

        if (asset.IsActionCamera)
        {
            ActionCameraTitle.Text = "这是单独的动画镜头资源";
            ActionCameraStatus.Text = "镜头文件";
            ActionCameraStatus.Foreground = new SolidColorBrush(Color.FromRgb(143, 212, 245));
            ActionCameraDetail.Text = "无需单独加载它。选择与它关联的角色骨骼动画，时间轴会自动出现“动画镜头”。";
            return;
        }

        var owners = asset.GameActionOwners is { Count: > 0 }
            ? string.Join(" + ", asset.GameActionOwners.Select(OfficialNameAnnotations.Format))
            : "未恢复动作表归属";
        if (string.IsNullOrWhiteSpace(asset.ActionCameraResourceId))
        {
            ActionCameraTitle.Text = asset.TreeDisplayName;
            ActionCameraStatus.Text = "无动画镜头";
            ActionCameraStatus.Foreground = (Brush)FindResource("MutedBrush");
            ActionCameraDetail.Text = $"动作表：{owners}\n游戏动作对象没有引用镜头动画，时间轴保持自由镜头。";
            return;
        }

        var cameraAsset = _all.FirstOrDefault(x => x.Kind == AssetKind.Animation && x.IsActionCamera &&
            NormalizeResourceId(x.ResourceId ?? x.HashId).Equals(NormalizeResourceId(asset.ActionCameraResourceId), StringComparison.OrdinalIgnoreCase));
        var cameraName = asset.GameActionOwners is { Count: > 0 } cameraOwners
            ? $"{string.Join(" + ", cameraOwners.Select(OfficialNameAnnotations.Format))} · 动画镜头"
            : cameraAsset?.TreeDisplayName ?? "动画镜头";
        ActionCameraTitle.Text = cameraName;
        ActionCameraStatus.Text = cameraError is null ? "已关联" : "读取失败";
        ActionCameraStatus.Foreground = cameraError is null
            ? new SolidColorBrush(Color.FromRgb(116, 214, 143))
            : new SolidColorBrush(Color.FromRgb(241, 132, 116));
        ActionCameraDetail.Text = cameraError is null
            ? $"当前动作：{asset.TreeDisplayName}\n人物动画：{_clip?.Info.Duration ?? 0:0.00} 秒 · 动画镜头：{_actionCamera?.Clip.Info.Duration ?? 0:0.00} 秒\n时间轴以人物动画为准；切换“动画镜头”只改变播放视角，不改变动画时间轴。"
            : $"当前动作已有动画镜头，但镜头读取失败：{cameraError}";
        if (cameraError is null && _actionCamera is not null)
            ActionCameraDetail.Text += "\n\n" + BuildCameraLearningSummary(_actionCamera);
    }

    private static string BuildCameraLearningSummary(ActionCameraClip camera)
    {
        var duration = camera.Clip.Info.Duration;
        var frames = Math.Max(1, (int)Math.Ceiling(duration * 60));
        var minDistance = float.PositiveInfinity; var maxDistance = 0f; var minFov = float.PositiveInfinity; var maxFov = 0f;
        var cameraPath = 0f; var targetPath = 0f; var maxCameraSpeed = 0f; var maxTargetSpeed = 0f;
        Vector3? previousCamera = null; Vector3? previousTarget = null;
        for (var frame = 0; frame <= frames; frame++)
        {
            var time = Math.Min(duration, frame / 60f);
            var raw = G1Parsers.SampleCameraTrack(camera.TransformTrack, time);
            var sample = ActionCameraCatalog.Sample(camera, time);
            var distance = Vector3.Distance(raw.Position, raw.Target);
            minDistance = Math.Min(minDistance, distance); maxDistance = Math.Max(maxDistance, distance);
            minFov = Math.Min(minFov, sample.FieldOfView); maxFov = Math.Max(maxFov, sample.FieldOfView);
            if (previousCamera is { } oldCamera && previousTarget is { } oldTarget)
            {
                var cameraStep = Vector3.Distance(oldCamera, raw.Position); var targetStep = Vector3.Distance(oldTarget, raw.Target);
                cameraPath += cameraStep; targetPath += targetStep;
                maxCameraSpeed = Math.Max(maxCameraSpeed, cameraStep * 60); maxTargetSpeed = Math.Max(maxTargetSpeed, targetStep * 60);
            }
            previousCamera = raw.Position; previousTarget = raw.Target;
        }
        return $"镜头设计参数：\n景别距离 {minDistance:0.###} – {maxDistance:0.###}\n垂直 FOV {minFov:0.###}° – {maxFov:0.###}°\n镜头移动 {cameraPath:0.###} · 目标移动 {targetPath:0.###}\n最大速度：镜头 {maxCameraSpeed:0.###}/秒 · 目标 {maxTargetSpeed:0.###}/秒\n导出时会把当前工具视口比例写入 UE 摄像机 Filmback，保证水平 FOV 与工具预览一致。";
    }

    private static string NormalizeResourceId(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

    private async Task LoadModelAssetAsync(AssetEntry asset, bool updateInspector)
    {
        var parsed = await Task.Run(() => G1Parsers.ReadModel(asset.FullPath));
        var texturePaths = FindCharacterTextures(asset.FullPath);
        var usedTextureIds = parsed.Parts.Select(x => x.DiffuseTextureIndex).Where(x => x >= 0).Distinct().ToArray();
        var decodedTextures = await Task.Run(() => DecodeCharacterTextures(texturePaths, usedTextureIds));
        _model = parsed; _modelAsset = asset; _characterTextures.Clear();
        foreach (var pair in decodedTextures) _characterTextures[pair.Key] = CreateTextureBrush(pair.Value);
        var m = _model;
        if (updateInspector)
        {
            DetailBody.Text = $"G1M 版本：{m.Version}\n平台标记：{m.Platform}\n文件大小：{asset.SizeText}\n容器分块：{m.ChunkCount}\n网格组：{m.MeshCount}\n子网格：{m.SubmeshCount}\n材质：{m.MaterialCount}\n顶点缓冲：{m.VertexBufferCount}\n索引缓冲：{m.IndexBufferCount}\n包围盒：\n  ({m.MinX:0.###}, {m.MinY:0.###}, {m.MinZ:0.###})\n  ({m.MaxX:0.###}, {m.MaxY:0.###}, {m.MaxZ:0.###})" + (m.Warning is null ? "" : $"\n\n提示：{m.Warning}");
            PreviewSummary.Text = $"{asset.AnnotatedDisplayName} · {m.Parts.Count} 个可预览分件 · {m.SubmeshCount} 子网格";
        }
        RenderModel(); _cameraTarget = _sceneCenter; ResetView(); RefreshBoneTree();
        if (_weaponModel is not null) PopulateAccessoryBoneChoices();
        UpdateActionCameraInspector(asset);
    }

    private void RenderModel()
    {
        _scene.Children.Clear(); _meshGeometries.Clear(); _meshModels.Clear(); _texturedMaterials.Clear(); _solidMaterials.Clear(); ViewportOverlay.Children.Clear();
        if (_model is null) return;
        var minWorld = ToWorld(_model.MaxX, _model.MinY, _model.MinZ);
        var maxWorld = ToWorld(_model.MinX, _model.MaxY, _model.MaxZ);
        _sceneCenter = new Point3D((minWorld.X + maxWorld.X) * .5, (minWorld.Y + maxWorld.Y) * .5, (minWorld.Z + maxWorld.Z) * .5);
        _sceneRadius = Math.Max(.01, Math.Max(_model.MaxX - _model.MinX, Math.Max(_model.MaxY - _model.MinY, _model.MaxZ - _model.MinZ)) * .5);
        _scene.Children.Add(new AmbientLight(Color.FromRgb(82, 88, 102)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(244, 236, 220), new Vector3D(-.45, -.7, -.5)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(105, 128, 170), new Vector3D(.6, -.15, .7)));
        _scene.Children.Add(_cameraGizmo);
        _scene.Children.Add(_cameraTrajectory);
        _scene.Children.Add(_cameraTrajectoryCursor);
        _scene.Children.Add(_weaponGroup);
        if (_showGrid) AddGrid3D();
        if (_showAxes) AddAxes3D();

        var palette = new[] { Color.FromRgb(190, 157, 104), Color.FromRgb(103, 133, 161), Color.FromRgb(133, 119, 151), Color.FromRgb(112, 145, 127), Color.FromRgb(161, 112, 105) };
        foreach (var part in _model.Parts)
        {
            var mesh = new MeshGeometry3D();
            for (var i = 0; i + 2 < part.Positions.Length; i += 3)
                mesh.Positions.Add(ToWorld(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
            if (part.Normals?.Length == part.Positions.Length)
                for (var i = 0; i + 2 < part.Normals.Length; i += 3)
                    mesh.Normals.Add(ToWorldNormal(part.Normals[i], part.Normals[i + 1], part.Normals[i + 2]));
            if (part.TextureCoordinates?.Length == mesh.Positions.Count * 2)
                for (var i = 0; i + 1 < part.TextureCoordinates.Length; i += 2)
                    mesh.TextureCoordinates.Add(new Point(part.TextureCoordinates[i], part.TextureCoordinates[i + 1]));
            AppendTriangles(mesh.TriangleIndices, part.Indices, part.TriangleStrip, mesh.Positions.Count);
            var color = palette[Math.Abs(part.MaterialIndex) % palette.Length];
            var group = new MaterialGroup();
            var diffuseBrush = part.TextureCoordinates is not null && _characterTextures.TryGetValue(part.DiffuseTextureIndex, out var textureBrush)
                ? (Brush)textureBrush : new SolidColorBrush(color);
            group.Children.Add(new DiffuseMaterial(diffuseBrush));
            group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(115, 255, 244, 220)), 34));
            var solid = new MaterialGroup();
            solid.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(168, 174, 184))));
            solid.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 24));
            var geometry = new GeometryModel3D(mesh, group) { BackMaterial = group };
            _meshGeometries.Add(mesh); _meshModels.Add(geometry); _texturedMaterials.Add(group); _solidMaterials.Add(solid); _scene.Children.Add(geometry);
        }
        _wireframeEdges = BuildWireframeEdges(_meshGeometries, 20_000);
        WireframeLayer.SetGeometry(_meshGeometries, _wireframeEdges);
        ApplyRenderMode();
        UpdateCamera();
        ViewportStats.Text = $"{_model.Parts.Count} parts · {_model.SubmeshCount} submeshes · {_model.Bones.Count} bones · GPU 3D";
        ViewportHint.Text = "左键旋转 · 右键平移 · 中键平移 · Alt+中键旋转 · 滚轮缩放 · 持久化 3D 网格";
    }

    private static IReadOnlyList<WireframeEdge> BuildWireframeEdges(IReadOnlyList<MeshGeometry3D> meshes, int edgeBudget)
    {
        var triangleCount = meshes.Sum(x => x.TriangleIndices.Count / 3);
        if (triangleCount == 0) return [];
        var triangleStride = Math.Max(1, (int)Math.Ceiling(triangleCount * 3d / edgeBudget));
        var result = new List<WireframeEdge>(Math.Min(edgeBudget, triangleCount * 3));
        var seen = new HashSet<(int Mesh, int A, int B)>();
        var globalTriangle = 0;
        for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            var indices = meshes[meshIndex].TriangleIndices;
            for (var i = 0; i + 2 < indices.Count; i += 3, globalTriangle++)
            {
                if (globalTriangle % triangleStride != 0) continue;
                Add(indices[i], indices[i + 1]); Add(indices[i + 1], indices[i + 2]); Add(indices[i + 2], indices[i]);
                if (result.Count >= edgeBudget) return result;
            }
            void Add(int a, int b)
            {
                if (a > b) (a, b) = (b, a);
                if (seen.Add((meshIndex, a, b))) result.Add(new WireframeEdge(meshIndex, a, b));
            }
        }
        return result;
    }

    private void ApplyRenderMode()
    {
        for (var i = 0; i < _meshModels.Count; i++)
        {
            var material = _renderMode switch { 0 => _texturedMaterials[i], 1 => _solidMaterials[i], _ => null };
            _meshModels[i].Material = material;
            _meshModels[i].BackMaterial = material;
        }
        WireframeLayer.Visibility = _renderMode == 2 && _model is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_renderMode == 2) WireframeLayer.RequestRender();
    }

    private static void AppendTriangles(Int32Collection target, uint[] indices, bool strip, int vertexCount)
    {
        if (!strip)
        {
            for (var i = 0; i + 2 < indices.Length; i += 3)
                AddTriangle(target, indices[i], indices[i + 1], indices[i + 2], vertexCount, false);
            return;
        }
        var flip = false;
        for (var i = 0; i + 2 < indices.Length; i++)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            if (a is 0xffff or uint.MaxValue || b is 0xffff or uint.MaxValue || c is 0xffff or uint.MaxValue) { flip = false; continue; }
            AddTriangle(target, a, b, c, vertexCount, flip); flip = !flip;
        }
    }

    private static IReadOnlyList<string> FindCharacterTextures(string modelPath)
    {
        var modelDirectory = Directory.GetParent(modelPath); var characterDirectory = modelDirectory?.Parent;
        if (characterDirectory is null) return [];
        var textureDirectory = Path.Combine(characterDirectory.FullName, "texture");
        return Directory.Exists(textureDirectory)
            ? Directory.EnumerateFiles(textureDirectory, "*.g1t", SearchOption.TopDirectoryOnly)
                .OrderBy(NaturalTextureOrder, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    private static string NaturalTextureOrder(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path); var suffix = name.Split('_').LastOrDefault();
        return int.TryParse(suffix, out var value) ? value.ToString("D10") : name;
    }

    private static Dictionary<int, G1TextureImage> DecodeCharacterTextures(IReadOnlyList<string> paths, IReadOnlyList<int> usedIds)
    {
        var result = new Dictionary<int, G1TextureImage>();
        foreach (var id in usedIds)
        {
            if ((uint)id >= paths.Count) continue;
            try { result[id] = G1TextureDecoder.ReadFirst(paths[id]); } catch { }
        }
        return result;
    }

    private static ImageBrush CreateTextureBrush(G1TextureImage texture)
    {
        var bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null,
            texture.BgraPixels, texture.Width * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile };
        brush.Freeze(); return brush;
    }

    private static void AddTriangle(Int32Collection target, uint a, uint b, uint c, int count, bool flip)
    {
        if (a == b || b == c || a == c || a >= count || b >= count || c >= count) return;
        target.Add(checked((int)(flip ? b : a))); target.Add(checked((int)(flip ? a : b))); target.Add(checked((int)c));
    }

    private void RenderAnimatedSkeleton(float time)
    {
        if (_model is null || _clip is null) { if (_model is not null) RenderModel(); return; }
        var bones = _model.Bones; if (bones.Count == 0) return;
        var characterTime = Math.Clamp(time, 0, _clip.Info.Duration);
        // 角色骨骼动画按原始 G1A/G2A 局部轨道求值。
        var pose = AnimationPoseEvaluator.Evaluate(_model, _clip, characterTime);
        RenderSkinnedModel(pose.SkinMatrices);
        _animatedBonePositions = pose.WorldMatrices.Select(x => x.Translation).ToArray();
        _animatedBoneIds = pose.AnimatedLocalBoneIds.Select(i => bones[i].GlobalId).ToHashSet();
        if (_showSkeleton) _overlayDirty = true;
        ViewportHint.Text = $"同步时间 {time:0.00}s · 人物 {characterTime:0.00}/{_clip.Info.Duration:0.00}s · 黄色为动画轨道";
        TimelineText.Text = $"{characterTime:0.00} / {_clip.Info.Duration:0.00} s";
        if (CameraModeCombo.SelectedIndex == 1 && _actionCamera is not null) ApplyActionCamera(time);
        UpdateCameraGizmo(time);
        UpdateCameraTrajectoryCursor(time);
        UpdateRealtimeCameraData(time);
        UpdateWeaponPreview(pose.WorldMatrices);
    }

    private void UpdateRealtimeCameraData(float time)
    {
        if (CameraDataCheck.IsChecked != true || _actionCamera is null)
        {
            CameraDataHud.Visibility = Visibility.Collapsed;
            return;
        }
        var frame = ActionCameraCatalog.Sample(_actionCamera, time);
        var raw = G1Parsers.SampleCameraTrack(_actionCamera.TransformTrack, frame.Time);
        var previousTime = Math.Max(0, frame.Time - 1f / 60f);
        var previous = G1Parsers.SampleCameraTrack(_actionCamera.TransformTrack, previousTime);
        var delta = Math.Max(1f / 60f, frame.Time - previousTime);
        var cameraSpeed = Vector3.Distance(raw.Position, previous.Position) / delta;
        var targetSpeed = Vector3.Distance(raw.Target, previous.Target) / delta;
        var distance = Vector3.Distance(raw.Position, raw.Target);
        var yaw = MathF.Atan2(frame.Forward.X, frame.Forward.Z) * 180f / MathF.PI;
        var pitch = MathF.Asin(Math.Clamp(frame.Forward.Y, -1f, 1f)) * 180f / MathF.PI;
        CameraHudMode.Text = CameraModeCombo.SelectedIndex == 1 ? "最终构图" : "自由视角";
        var viewportHorizontalFov = VerticalToHorizontalFov(frame.FieldOfView, CameraViewportAspect);
        var filmbackWidth = ExportFilmbackHeightMm * CameraViewportAspect;
        CameraHudText.Text = $"帧 {(int)Math.Round(frame.Time * 60)}  ·  {frame.Time:0.000}s / {_actionCamera.Clip.Info.Duration:0.000}s\n" +
                             $"垂直 FOV {frame.FieldOfView:0.00}°\n" +
                             $"水平 FOV {viewportHorizontalFov:0.00}°\n" +
                             $"导出背板 {filmbackWidth:0.00}×{ExportFilmbackHeightMm:0.00} mm\n" +
                             $"距离 {distance:0.00} cm\n" +
                             $"机位 ({raw.Position.X:0.00}, {raw.Position.Y:0.00}, {raw.Position.Z:0.00})\n" +
                             $"目标 ({raw.Target.X:0.00}, {raw.Target.Y:0.00}, {raw.Target.Z:0.00})\n" +
                             $"Yaw {yaw:0.00}°  ·  Pitch {pitch:0.00}°\n" +
                             $"速度：机位 {cameraSpeed:0.00} · 目标 {targetSpeed:0.00} cm/s";
        CameraDataHud.Visibility = Visibility.Visible;
    }

    private void ApplyActionCamera(float time)
    {
        if (_actionCamera is null || _model is null || _clip is null) return;
        // Camera position and look-at are sampled in the coordinate space
        // authored by the shipped 0x66 camera clip. Do not apply a universal
        // root transform: forward-moving sword actions prove that is incorrect.
        var frame = ActionCameraCatalog.Sample(_actionCamera, time);
        var position = ToWorld(frame.Position.X, frame.Position.Y, frame.Position.Z);
        var forward = ToWorldNormal(frame.Forward.X, frame.Forward.Y, frame.Forward.Z);
        var up = ToWorldNormal(frame.Up.X, frame.Up.Y, frame.Up.Z);
        if (forward.LengthSquared < 1e-8 || up.LengthSquared < 1e-8) return;
        ViewportCamera.Position = position;
        ViewportCamera.LookDirection = forward;
        ViewportCamera.UpDirection = up;
        // WPF PerspectiveCamera expects horizontal FOV, so the rendering value
        // must use the resizable viewport aspect.  This is a fitting value, not
        // UE's filmback-based horizontal FOV displayed in the camera data HUD.
        ViewportCamera.FieldOfView = VerticalToHorizontalFov(frame.FieldOfView, CameraViewportAspect);
        ViewportCamera.NearPlaneDistance = Math.Max(.001, _sceneRadius / 1000);
        ViewportCamera.FarPlaneDistance = Math.Max(1000, _sceneRadius * 20);
        UpdateWorldAxisIndicator();
        if (_renderMode == 2) WireframeLayer.RequestRender();
    }

    private void UpdateCameraGizmo(float time)
    {
        _cameraGizmo.Children.Clear();
        if (CameraGizmoCheck.IsChecked != true || CameraModeCombo.SelectedIndex != 0 || _actionCamera is null || _model is null) return;
        // 示意摄像机与实际动画镜头使用相同的本体坐标。
        var frame = ActionCameraCatalog.Sample(_actionCamera, time);
        var position = ToWorld(frame.Position.X, frame.Position.Y, frame.Position.Z);
        var forward = ToWorldNormal(frame.Forward.X, frame.Forward.Y, frame.Forward.Z);
        var up = ToWorldNormal(frame.Up.X, frame.Up.Y, frame.Up.Z);
        if (forward.LengthSquared < 1e-8 || up.LengthSquared < 1e-8) return;
        forward.Normalize(); up.Normalize();
        var right = Vector3D.CrossProduct(forward, up); if (right.LengthSquared < 1e-8) return;
        right.Normalize(); up = Vector3D.CrossProduct(right, forward); up.Normalize();
        var depth = Math.Max(_sceneRadius * .48, 4);
        var halfHeight = Math.Tan(frame.FieldOfView * Math.PI / 360) * depth;
        var halfWidth = halfHeight * (Viewport.ActualHeight > 1 ? Viewport.ActualWidth / Viewport.ActualHeight : 16d / 9d);
        var center = position + forward * depth;
        var corners = new[] { center + up * halfHeight - right * halfWidth, center + up * halfHeight + right * halfWidth,
            center - up * halfHeight + right * halfWidth, center - up * halfHeight - right * halfWidth };
        var cyan = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(76, 208, 238)));
        var yellow = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(241, 190, 72)));
        var thickness = Math.Max(_sceneRadius * .003, .025);
        foreach (var corner in corners) AddLine3D(_cameraGizmo, position, corner, thickness, cyan);
        for (var i = 0; i < corners.Length; i++) AddLine3D(_cameraGizmo, corners[i], corners[(i + 1) % corners.Length], thickness, cyan);
        var body = Math.Max(_sceneRadius * .035, .25);
        AddLine3D(_cameraGizmo, position - right * body, position + right * body, thickness * 1.8, cyan);
        AddLine3D(_cameraGizmo, position - up * body, position + up * body, thickness * 1.8, cyan);
        AddLine3D(_cameraGizmo, position, center, thickness * 1.3, yellow);
    }

    private void UpdateCameraTrajectory()
    {
        _cameraTrajectory.Children.Clear();
        _cameraTrajectoryCursor.Children.Clear();
        if (CameraTrajectoryCheck.IsChecked != true || CameraModeCombo.SelectedIndex != 0 ||
            _actionCamera is null || _model is null) return;

        var duration = _actionCamera.Clip.Info.Duration;
        var frameCount = Math.Max(1, (int)Math.Ceiling(duration * 60));
        var points = new Point3D[frameCount + 1];
        for (var frameIndex = 0; frameIndex <= frameCount; frameIndex++)
        {
            var time = Math.Min(duration, frameIndex / 60f);
            var sample = ActionCameraCatalog.Sample(_actionCamera, time);
            points[frameIndex] = ToWorld(sample.Position.X, sample.Position.Y, sample.Position.Z);
        }

        var pathMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(61, 190, 218)));
        var startMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(76, 210, 128)));
        var endMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(238, 91, 91)));
        var thickness = Math.Max(_sceneRadius * .0016, .018);
        for (var i = 1; i < points.Length; i++)
            AddLine3D(_cameraTrajectory, points[i - 1], points[i], thickness, pathMaterial);
        AddCameraTrajectoryMarker(_cameraTrajectory, points[0], thickness * 4.5, startMaterial);
        AddCameraTrajectoryMarker(_cameraTrajectory, points[^1], thickness * 4.5, endMaterial);
        UpdateCameraTrajectoryCursor((float)_playbackTime);
    }

    private void UpdateCameraTrajectoryCursor(float time)
    {
        _cameraTrajectoryCursor.Children.Clear();
        if (CameraTrajectoryCheck.IsChecked != true || CameraModeCombo.SelectedIndex != 0 ||
            _actionCamera is null || _model is null) return;
        var sample = ActionCameraCatalog.Sample(_actionCamera, time);
        var position = ToWorld(sample.Position.X, sample.Position.Y, sample.Position.Z);
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(246, 190, 67)));
        var thickness = Math.Max(_sceneRadius * .0016, .018);
        AddCameraTrajectoryMarker(_cameraTrajectoryCursor, position, thickness * 5.5, material);
    }

    private static void AddCameraTrajectoryMarker(Model3DGroup target, Point3D position,
        double size, Material material)
    {
        var thickness = Math.Max(size * .22, .012);
        AddLine3D(target, position - new Vector3D(size, 0, 0), position + new Vector3D(size, 0, 0), thickness, material);
        AddLine3D(target, position - new Vector3D(0, size, 0), position + new Vector3D(0, size, 0), thickness, material);
        AddLine3D(target, position - new Vector3D(0, 0, size), position + new Vector3D(0, 0, size), thickness, material);
    }

    private static Matrix4x4[] BuildBindWorld(IReadOnlyList<SkeletonBone> bones)
    {
        var world = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            var local = Matrix4x4.CreateScale(bone.ScaleX, bone.ScaleY, bone.ScaleZ) *
                        Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW)) *
                        Matrix4x4.CreateTranslation(bone.PositionX, bone.PositionY, bone.PositionZ);
            world[i] = bone.ParentLocalId >= 0 && bone.ParentLocalId < i ? local * world[bone.ParentLocalId] : local;
        }
        return world;
    }

    private async Task LoadAccessoryAssetAsync(AssetEntry asset)
    {
        if (_model is null || asset.Kind != AssetKind.Model) return;
        StatusText.Text = $"正在加载配件：{asset.AnnotatedDisplayName}";
        try
        {
            _weaponModel = await Task.Run(() => G1Parsers.ReadModel(asset.FullPath));
            _weaponAsset = asset;
            PopulateAccessoryBoneChoices();
            ResolveAccessoryAttachment();
            WeaponCheck.IsEnabled = true;
            WeaponCheck.IsChecked = true;
            AccessoryNameText.Text = asset.AnnotatedDisplayName;
            WeaponCheck.ToolTip = $"当前配件：{asset.AnnotatedDisplayName}";
            BuildWeaponPreview();
            UpdateWeaponToCurrentPose();
            StatusText.Text = $"已添加独立配件：{asset.AnnotatedDisplayName}";
        }
        catch (Exception ex)
        {
            RemoveAccessory();
            StatusText.Text = $"配件模型读取失败：{ex.Message}";
        }
    }

    private async void AddSelectedAccessory_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { Kind: AssetKind.Model } asset || _model is null) return;
        StatusText.Text = $"正在加载配件：{asset.AnnotatedDisplayName}";
        try
        {
            _weaponModel = await Task.Run(() => G1Parsers.ReadModel(asset.FullPath));
            _weaponAsset = asset;
            PopulateAccessoryBoneChoices();
            ResolveAccessoryAttachment();
            WeaponCheck.IsEnabled = true;
            WeaponCheck.IsChecked = true;
            AccessoryNameText.Text = asset.AnnotatedDisplayName;
            WeaponCheck.ToolTip = $"当前配件：{asset.AnnotatedDisplayName}";
            BuildWeaponPreview();
            UpdateWeaponToCurrentPose();
            StatusText.Text = $"已添加独立配件：{asset.AnnotatedDisplayName}";
        }
        catch (Exception ex)
        {
            RemoveAccessory();
            StatusText.Text = $"配件模型读取失败：{ex.Message}";
        }
    }

    private void RemoveAccessory_Click(object sender, RoutedEventArgs e) => RemoveAccessory();

    private void RemoveAccessory()
    {
        _weaponModel = null; _weaponAsset = null; _weaponAttachment = null;
        _weaponGeometries.Clear(); _weaponGroup.Children.Clear();
        WeaponCheck.IsEnabled = false; WeaponCheck.IsChecked = false;
        AccessorySourceBoneCombo.ItemsSource = null; AccessoryTargetBoneCombo.ItemsSource = null;
        AccessorySourceBoneCombo.IsEnabled = false; AccessoryTargetBoneCombo.IsEnabled = false;
        AccessoryNameText.Text = "未添加"; AccessoryMappingText.Text = "添加配件后显示骨骼对齐关系";
    }

    private void PopulateAccessoryBoneChoices()
    {
        if (_weaponModel is null || _model is null) return;
        _updatingAccessoryEditor = true;
        var source = _weaponModel.Bones.Select(b => new BoneChoice(b.LocalId, b.GlobalId)).ToArray();
        var target = _model.Bones.Select(b => new BoneChoice(b.LocalId, b.GlobalId)).ToArray();
        AccessorySourceBoneCombo.ItemsSource = source; AccessoryTargetBoneCombo.ItemsSource = target;
        AccessorySourceBoneCombo.IsEnabled = source.Length > 0; AccessoryTargetBoneCombo.IsEnabled = target.Length > 0;
        var automatic = ModelAttachmentComposer.Resolve(_weaponModel, DefaultWeaponAttachmentBoneId);
        AccessorySourceBoneCombo.SelectedItem = source.FirstOrDefault(x => x.LocalId == automatic.SourceLocalBoneId) ?? source.FirstOrDefault();
        AccessoryTargetBoneCombo.SelectedItem = target.FirstOrDefault(x => x.GlobalId == DefaultWeaponAttachmentBoneId) ?? target.FirstOrDefault();
        _updatingAccessoryEditor = false;
    }

    private void ResolveAccessoryAttachment()
    {
        if (_weaponModel is null || AccessorySourceBoneCombo.SelectedItem is not BoneChoice source ||
            AccessoryTargetBoneCombo.SelectedItem is not BoneChoice target) return;
        _weaponAttachment = ModelAttachmentComposer.Resolve(_weaponModel, source.LocalId, target.GlobalId);
        AccessoryMappingText.Text = $"独立配件 bone_{source.GlobalId} → 人物 bone_{target.GlobalId}";
    }

    private void AutoFitAccessory_Click(object sender, RoutedEventArgs e)
    {
        if (_weaponModel is null || _weaponAsset is null || _model is null)
        {
            StatusText.Text = "请先添加一个配件模型";
            return;
        }
        var officialObjects = KidsNameCatalogReader.BuildOfficialObjectNames(_root);
        var fit = AccessoryAutoFitCatalog.Find(_weaponAsset, _weaponModel, officialObjects);
        if (fit is null)
        {
            StatusText.Text = "游戏配置中未找到该配件的装备适配记录";
            AccessoryMappingText.Text = "未找到游戏配置；保留当前手动设置";
            return;
        }

        _updatingAccessoryEditor = true;
        if (AccessorySourceBoneCombo.ItemsSource is IEnumerable<BoneChoice> source)
            AccessorySourceBoneCombo.SelectedItem = source.FirstOrDefault(x => x.LocalId == fit.SourceLocalBoneId);
        if (AccessoryTargetBoneCombo.ItemsSource is IEnumerable<BoneChoice> target)
            AccessoryTargetBoneCombo.SelectedItem = target.FirstOrDefault(x => x.GlobalId == fit.TargetGlobalBoneId);
        AccessoryPosX.Text = AccessoryPosY.Text = AccessoryPosZ.Text = "0";
        AccessoryRotX.Text = AccessoryRotY.Text = AccessoryRotZ.Text = "0";
        AccessoryScale.Text = "1";
        _accessoryAdjustment = Matrix4x4.Identity;
        _updatingAccessoryEditor = false;
        ResolveAccessoryAttachment(); UpdateWeaponToCurrentPose();
        AccessoryMappingText.Text = fit.Evidence;
        StatusText.Text = $"已按游戏配置自动适配：{_weaponAsset.AnnotatedDisplayName}";
    }

    private void BuildWeaponPreview()
    {
        _weaponGroup.Children.Clear(); _weaponGeometries.Clear();
        if (WeaponCheck.IsChecked != true || _weaponModel is null) return;
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(173, 151, 105))));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(150, 255, 242, 204)), 48));
        foreach (var part in _weaponModel.Parts)
        {
            var mesh = new MeshGeometry3D();
            for (var i = 0; i + 2 < part.Positions.Length; i += 3)
                mesh.Positions.Add(ToWorld(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
            AppendTriangles(mesh.TriangleIndices, part.Indices, part.TriangleStrip, mesh.Positions.Count);
            _weaponGeometries.Add(mesh);
            _weaponGroup.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        }
    }

    private void UpdateWeaponPreview(IReadOnlyList<Matrix4x4> poseWorld)
    {
        if (WeaponCheck.IsChecked != true || _weaponModel is null || _weaponAttachment is null || _model is null) return;
        if (_weaponGeometries.Count != _weaponModel.Parts.Count) BuildWeaponPreview();
        var slot = _model.Bones.FirstOrDefault(bone => bone.GlobalId == _weaponAttachment.TargetGlobalBoneId);
        if (slot is null || slot.LocalId < 0 || slot.LocalId >= poseWorld.Count) return;
        var transform = ModelAttachmentComposer.Compose(_weaponAttachment, _accessoryAdjustment, poseWorld[slot.LocalId]);
        for (var partIndex = 0; partIndex < _weaponModel.Parts.Count && partIndex < _weaponGeometries.Count; partIndex++)
        {
            var part = _weaponModel.Parts[partIndex];
            var points = new Point3DCollection(part.Positions.Length / 3);
            for (var i = 0; i < part.Positions.Length / 3; i++)
            {
                var source = new Vector3(part.Positions[i * 3], part.Positions[i * 3 + 1], part.Positions[i * 3 + 2]);
                var value = Vector3.Transform(source, transform);
                points.Add(ToWorld(value.X, value.Y, value.Z));
            }
            _weaponGeometries[partIndex].Positions = points;
        }
    }

    private void UpdateWeaponToCurrentPose()
    {
        if (_model is null || _weaponModel is null || WeaponCheck.IsChecked != true) return;
        if (_clip is not null)
        {
            RenderAnimatedSkeleton((float)_playbackTime);
            return;
        }

        var world = new Matrix4x4[_model.Bones.Count];
        for (var i = 0; i < _model.Bones.Count; i++)
        {
            var bone = _model.Bones[i];
            var local = Matrix4x4.CreateScale(bone.ScaleX, bone.ScaleY, bone.ScaleZ) *
                        Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(bone.RotationX, bone.RotationY,
                            bone.RotationZ, bone.RotationW)) *
                        Matrix4x4.CreateTranslation(bone.PositionX, bone.PositionY, bone.PositionZ);
            world[i] = bone.ParentLocalId >= 0 && bone.ParentLocalId < i ? local * world[bone.ParentLocalId] : local;
        }
        UpdateWeaponPreview(world);
    }

    private void AccessoryBone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAccessoryEditor || !IsLoaded || _weaponModel is null) return;
        ResolveAccessoryAttachment(); UpdateWeaponToCurrentPose();
    }

    private void AccessoryTransform_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _updatingAccessoryEditor) return;
        static float Value(TextBox? box, float fallback) => box is not null &&
            float.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && float.IsFinite(value) ? value : fallback;
        var scale = Math.Clamp(Value(AccessoryScale, 1), .001f, 1000);
        var rotation = Matrix4x4.CreateFromYawPitchRoll(
            Value(AccessoryRotY, 0) * MathF.PI / 180,
            Value(AccessoryRotX, 0) * MathF.PI / 180,
            Value(AccessoryRotZ, 0) * MathF.PI / 180);
        _accessoryAdjustment = Matrix4x4.CreateScale(scale) * rotation *
            Matrix4x4.CreateTranslation(Value(AccessoryPosX, 0), Value(AccessoryPosY, 0), Value(AccessoryPosZ, 0));
        UpdateWeaponToCurrentPose();
    }

    private static SkeletonBone? FindAnimationBone(G1MInfo model, G1AClip clip, int animationBoneId)
        => FindAnimationBone(model.Bones, clip, animationBoneId);

    private static SkeletonBone? FindAnimationBone(IReadOnlyList<SkeletonBone> bones, G1AClip clip, int animationBoneId)
    {
          // G2A stores the index used by the G1MS global-to-local bone map.
          // Noesis resolves it as boneIDList[animationBoneId] before looking
          // up the actual joint.  SkeletonBone.GlobalId is that map key and
          // LocalId is the resulting joint-table index.  Treating a G2A ID as
          // LocalId directly is wrong whenever G1MS contains an unused global
          // slot (for example the gap at global ID 6 in Hero0), which applies
          // tracks to unrelated bones and produces severe spikes.
          return bones.FirstOrDefault(b => b.GlobalId == animationBoneId);
    }

    private void RenderSkinnedModel(Matrix4x4[] skin)
    {
        if (_model is null || _meshGeometries.Count != _model.Parts.Count) return;
        for (var partIndex = 0; partIndex < _model.Parts.Count; partIndex++)
        {
            var part = _model.Parts[partIndex]; var p = part.Positions;
            var points = new Point3DCollection(p.Length / 3);
            for (var i = 0; i < p.Length / 3; i++)
            {
                var v = AnimationPoseEvaluator.SkinVertex(part, i, skin);
                points.Add(ToWorld(v.X, v.Y, v.Z));
            }
            _meshGeometries[partIndex].Positions = points;
        }
        if (_renderMode == 2) WireframeLayer.RequestRender();
    }

    private void UpdateCamera()
    {
        if (_model is null) return;
        if (CameraModeCombo.SelectedIndex == 1 && _actionCamera is not null)
        {
            ApplyActionCamera((float)_playbackTime); return;
        }
        ViewportCamera.FieldOfView = 42;
        var distance = _sceneRadius * 3.15 / _zoom;
        // X/Y 是地面，Z 向上；与参考 RE 视口的 orbit 公式一致。
        var direction = new Vector3D(Math.Cos(_pitch) * Math.Sin(_yaw), Math.Cos(_pitch) * Math.Cos(_yaw), Math.Sin(_pitch));
        var target = _model is null ? _cameraTarget : _cameraTarget;
        var position = target + direction * distance;
        ViewportCamera.Position = position;
        ViewportCamera.LookDirection = target - position;
        ViewportCamera.UpDirection = Math.Abs(Math.Cos(_pitch)) < .01
            ? (_pitch > 0 ? new Vector3D(0, -1, 0) : new Vector3D(0, 1, 0))
            : new Vector3D(0, 0, 1);
        ViewportCamera.NearPlaneDistance = Math.Max(.001, distance / 1000);
        ViewportCamera.FarPlaneDistance = Math.Max(1000, distance + _sceneRadius * 10);
        UpdateWorldAxisIndicator();
        if (_renderMode == 2) WireframeLayer.RequestRender();
    }

    private void QueueCameraUpdate()
    {
        _cameraDirty = true;
        if (_showSkeleton) _overlayDirty = true;
    }

    private void Viewport_Rendering(object? sender, EventArgs e)
    {
        if (_cameraDirty)
        {
            _cameraDirty = false;
            UpdateCamera();
        }
        if (_overlayDirty)
        {
            _overlayDirty = false;
            RenderSkeletonOverlay();
        }
    }

    private void UpdateWorldAxisIndicator()
    {
        if (!IsLoaded || WorldAxisX is null) return;
        var forward = ViewportCamera.LookDirection; if (forward.LengthSquared < 1e-8) return; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, ViewportCamera.UpDirection); if (right.LengthSquared < 1e-8) return; right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();
        // 与 RE 工具 GetWorldAxisScreenDirections 完全一致：-X、+Y、+Z。
        var axes = new[] { new Vector3D(-1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1) };
        var lines = new[] { WorldAxisX, WorldAxisY, WorldAxisZ };
        var labels = new[] { WorldAxisXLabel, WorldAxisYLabel, WorldAxisZLabel };
        const double originX = 45, originY = 58, length = 32;
        for (var i = 0; i < axes.Length; i++)
        {
            var sx = Vector3D.DotProduct(axes[i], right); var sy = -Vector3D.DotProduct(axes[i], up);
            var magnitude = Math.Sqrt(sx * sx + sy * sy); if (magnitude > 1e-8) { sx /= magnitude; sy /= magnitude; }
            var x = originX + sx * length; var y = originY + sy * length;
            lines[i].X1 = originX; lines[i].Y1 = originY; lines[i].X2 = x; lines[i].Y2 = y;
            Canvas.SetLeft(labels[i], Math.Clamp(x + (sx >= 0 ? 3 : -12), 2, 78));
            Canvas.SetTop(labels[i], Math.Clamp(y - 9, 1, 72));
        }
    }

    private void AddGrid3D()
    {
        var size = _sceneRadius * 1.8; var z = _model?.MinY ?? 0; var step = size / 10;
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(45, 54, 66)));
        for (var i = -10; i <= 10; i++)
        {
            AddLine3D(new Point3D(_sceneCenter.X - size, _sceneCenter.Y + i * step, z), new Point3D(_sceneCenter.X + size, _sceneCenter.Y + i * step, z), step * .012, material);
            AddLine3D(new Point3D(_sceneCenter.X + i * step, _sceneCenter.Y - size, z), new Point3D(_sceneCenter.X + i * step, _sceneCenter.Y + size, z), step * .012, material);
        }
    }

    private void AddAxes3D()
    {
        var length = _sceneRadius * .45; var thickness = Math.Max(.002, _sceneRadius * .008);
        // 世界轴固定在地面原点，不再插在角色身体中心。
        var origin = new Point3D(0, 0, 0);
        // 与 RE 工具 BuildGridLines 完全一致：红色 X 从原点指向世界 -X。
        AddLine3D(origin, origin + new Vector3D(-length, 0, 0), thickness, new DiffuseMaterial(Brushes.IndianRed));
        AddLine3D(origin, origin + new Vector3D(0, length, 0), thickness, new DiffuseMaterial(Brushes.LightGreen));
        AddLine3D(origin, origin + new Vector3D(0, 0, length), thickness, new DiffuseMaterial(Brushes.CornflowerBlue));
    }

    private void AddLine3D(Point3D start, Point3D end, double thickness, Material material) => AddLine3D(_scene, start, end, thickness, material);

    private static void AddLine3D(Model3DGroup target, Point3D start, Point3D end, double thickness, Material material)
    {
        var direction = end - start; var length = direction.Length; if (length < 1e-8) return; direction.Normalize();
        var reference = Math.Abs(Vector3D.DotProduct(direction, new Vector3D(0, 1, 0))) > .9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var side = Vector3D.CrossProduct(direction, reference); side.Normalize(); side *= thickness;
        var up = Vector3D.CrossProduct(direction, side); up.Normalize(); up *= thickness;
        var mesh = new MeshGeometry3D();
        foreach (var point in new[] { start - side - up, start + side - up, start + side + up, start - side + up, end - side - up, end + side - up, end + side + up, end - side + up }) mesh.Positions.Add(point);
        foreach (var index in new[] { 0,1,2, 0,2,3, 4,6,5, 4,7,6, 0,4,5, 0,5,1, 1,5,6, 1,6,2, 2,6,7, 2,7,3, 3,7,4, 3,4,0 }) mesh.TriangleIndices.Add(index);
        target.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private void RenderSkeletonOverlay()
    {
        ViewportOverlay.Children.Clear();
        if (!_showSkeleton || _model is null || _animatedBonePositions is null || Viewport.ActualWidth <= 1 || Viewport.ActualHeight <= 1) return;
        var aspect = Viewport.ActualWidth / Viewport.ActualHeight;
        var forward = ViewportCamera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, ViewportCamera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();
        var tan = Math.Tan(ViewportCamera.FieldOfView * Math.PI / 360);
        Point? Project(Vector3 point)
        {
            var worldPoint = ToWorld(point); var relative = new Point3D(worldPoint.X, worldPoint.Y, worldPoint.Z) - ViewportCamera.Position;
            var depth = Vector3D.DotProduct(relative, forward); if (depth <= 1e-6) return null;
            var x = Vector3D.DotProduct(relative, right) / (depth * tan * aspect);
            var y = Vector3D.DotProduct(relative, up) / (depth * tan);
            return new Point((x + 1) * Viewport.ActualWidth * .5, (1 - y) * Viewport.ActualHeight * .5);
        }
        var visibleBones = GetVisibleDeformBoneChain(_model);
        for (var i = 0; i < _model.Bones.Count && i < _animatedBonePositions.Length; i++)
        {
            var parent = _model.Bones[i].ParentLocalId; if (parent < 0 || parent >= _animatedBonePositions.Length) continue;
            if (!visibleBones.Contains(i) || !visibleBones.Contains(parent)) continue;
            var a = Project(_animatedBonePositions[parent]); var b = Project(_animatedBonePositions[i]); if (a is null || b is null) continue;
            var animated = _animatedBoneIds?.Contains(_model.Bones[i].GlobalId) == true;
            ViewportOverlay.Children.Add(new Line { X1 = a.Value.X, Y1 = a.Value.Y, X2 = b.Value.X, Y2 = b.Value.Y,
                Stroke = animated ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(190, 100, 190, 235)), StrokeThickness = animated ? 2.2 : 1.1 });
        }
    }

    private static HashSet<int> GetVisibleDeformBoneChain(G1MInfo model)
    {
        var result = model.Parts.Where(part => part.BoneIndices is not null)
            .SelectMany(part => part.BoneIndices!).Where(index => index >= 0 && index < model.Bones.Count)
            .ToHashSet();
        foreach (var local in result.ToArray())
        {
            var current = model.Bones[local].ParentLocalId;
            while (current >= 0 && current < model.Bones.Count && result.Add(current))
                current = model.Bones[current].ParentLocalId;
        }
        return result;
    }

    private void RefreshBoneTree()
    {
        _boneRoots.Clear();
        if (_model is null) { BoneSummaryText.Text = "未加载"; return; }
        var nodes = _model.Bones.Select(x => new BoneTreeNode(x.LocalId, x.GlobalId, x.ParentLocalId)).ToArray();
        for (var i = 0; i < nodes.Length; i++)
        {
            var parent = nodes[i].ParentLocalId;
            if (parent >= 0 && parent < nodes.Length) nodes[parent].Children.Add(nodes[i]);
            else _boneRoots.Add(nodes[i]);
        }
        BoneSummaryText.Text = $"{nodes.Length:N0} 根";
    }

    private void RenderTimeline(G1AInfo a)
    {
        _scene.Children.Clear(); _meshGeometries.Clear(); _meshModels.Clear(); _texturedMaterials.Clear(); _solidMaterials.Clear(); WireframeLayer.Visibility = Visibility.Collapsed; ViewportOverlay.Children.Clear();
        var w = Math.Max(400, Viewport.ActualWidth); var h = Math.Max(300, Viewport.ActualHeight);
        var tracks = Math.Min(48, Math.Max(1, a.TrackCount));
        for (var i = 0; i < tracks; i++)
        {
            var y = 75 + i * Math.Min(14, (h - 130) / tracks);
            ViewportOverlay.Children.Add(new Line { X1 = 60, X2 = w - 50, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(50, 61, 75)), StrokeThickness = 1 });
            for (var k = 0; k < 4 + i % 7; k++)
            {
                var x = 70 + (w - 140) * ((k + (i % 3) * .18) / (4 + i % 7));
                ViewportOverlay.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(215, 168, 74)), Margin = new Thickness(x, y - 2.5, 0, 0) });
            }
        }
        var title = new TextBlock { Text = $"G1A {a.Version}  ·  {a.Duration:0.###} s  ·  {a.TrackCount} tracks", FontSize = 20, Foreground = Brushes.Gainsboro };
        Canvas.SetLeft(title, 60); Canvas.SetTop(title, 28); ViewportOverlay.Children.Add(title);
    }

    private void RenderPackageCard(string text)
    {
        _scene.Children.Clear(); _meshGeometries.Clear(); _meshModels.Clear(); _texturedMaterials.Clear(); _solidMaterials.Clear(); WireframeLayer.Visibility = Visibility.Collapsed; ViewportOverlay.Children.Clear();
        var label = new TextBlock { Text = text, FontSize = 42, FontWeight = FontWeights.Light, Foreground = new SolidColorBrush(Color.FromRgb(95, 108, 127)) };
        Canvas.SetLeft(label, Math.Max(30, Viewport.ActualWidth / 2 - 110)); Canvas.SetTop(label, Math.Max(50, Viewport.ActualHeight / 2 - 35)); ViewportOverlay.Children.Add(label);
    }

    private static string ReadMagic(string path) { using var s = File.OpenRead(path); var b = new byte[8]; var n = s.Read(b); return n == 0 ? "空文件" : string.Join(' ', b.Take(n).Select(x => x is >= 32 and <= 126 ? ((char)x).ToString() : x.ToString("X2"))); }
    private void ResetView() { if (_model is not null) _cameraTarget = _sceneCenter; _yaw = -.38; _pitch = .35; _zoom = 1; UpdateCamera(); }
    private void RenderCurrentView() { QueueCameraUpdate(); }
    private void ResetView_Click(object s, RoutedEventArgs e) { ResetView(); }
    private double GetPlaybackDuration() => _clip?.Info.Duration ?? 0;

    private void SeekPlayback(double time)
    {
        if (_clip is null) return;
        _playbackTime = Math.Clamp(time, 0, Math.Max(.001, GetPlaybackDuration()));
        _sliderInternal = true;
        AnimationSlider.Value = _playbackTime;
        _sliderInternal = false;
        if (_model is not null) RenderAnimatedSkeleton((float)_playbackTime);
    }

    private void CameraMode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (CameraModeCombo.SelectedIndex == 1 && _actionCamera is not null)
        {
            RenderAnimatedSkeleton((float)_playbackTime);
            ViewportHint.Text = "动画镜头 · 与人物动作按相同时间同步";
        }
        else
        {
            UpdateCamera();
            if (_clip is not null && _model is not null) RenderAnimatedSkeleton((float)_playbackTime);
        }
        UpdateCameraGizmo((float)_playbackTime);
        UpdateCameraTrajectory();
        UpdateRealtimeCameraData((float)_playbackTime);
    }
    private void CameraGizmo_Changed(object s, RoutedEventArgs e) => UpdateCameraGizmo((float)_playbackTime);
    private void CameraTrajectory_Changed(object s, RoutedEventArgs e) => UpdateCameraTrajectory();
    private void CameraData_Changed(object s, RoutedEventArgs e) => UpdateRealtimeCameraData((float)_playbackTime);
    private void WeaponCheck_Changed(object s, RoutedEventArgs e)
    {
        BuildWeaponPreview();
        UpdateWeaponToCurrentPose();
    }
    private void ViewportView_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        (_yaw, _pitch) = ViewCombo.SelectedIndex switch
        {
            1 => (Math.PI, 0), 2 => (0, 0), 3 => (Math.PI / 2, 0), 4 => (-Math.PI / 2, 0),
            5 => (0, Math.PI / 2 - .001), 6 => (0, -Math.PI / 2 + .001), _ => (-.38, .35)
        };
        QueueCameraUpdate();
    }
    private void RenderMode_Changed(object s, SelectionChangedEventArgs e) { if (!IsLoaded) return; _renderMode = RenderModeCombo.SelectedIndex; ApplyRenderMode(); }
    private void ViewportOption_Changed(object s, RoutedEventArgs e)
    {
        _showGrid = GridCheck.IsChecked == true; _showAxes = AxesCheck.IsChecked == true; _showSkeleton = SkeletonCheck.IsChecked == true;
        if (_model is not null) { RenderModel(); if (_clip is not null) RenderAnimatedSkeleton((float)_playbackTime); }
    }
    private void Viewport_SizeChanged(object s, SizeChangedEventArgs e) { QueueCameraUpdate(); if (_renderMode == 2) WireframeLayer.RequestRender(); }
    private void Viewport_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (CameraModeCombo.SelectedIndex == 1) return;
        // Same exponential direction as RE: wheel up moves the eye toward the target.
        if (e.Delta != 0) _zoom = Math.Clamp(_zoom * Math.Exp(e.Delta > 0 ? .12 : -.12), .15, 20);
        QueueCameraUpdate(); e.Handled = true;
    }
    private void Viewport_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (CameraModeCombo.SelectedIndex == 1) return;
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        _orbiting = e.ChangedButton == MouseButton.Left || (alt && e.ChangedButton == MouseButton.Middle);
        _panning = e.ChangedButton == MouseButton.Right || (e.ChangedButton == MouseButton.Middle && !alt);
        if (!_orbiting && !_panning) return;
        _dragging = true; _dragStart = e.GetPosition(ViewportInput); ViewportInput.Focus(); ViewportInput.CaptureMouse(); e.Handled = true;
    }
    private void Viewport_MouseUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return; _dragging = false; _orbiting = false; _panning = false;
        ViewportInput.ReleaseMouseCapture(); e.Handled = true;
    }
    private void Viewport_MouseMove(object s, MouseEventArgs e)
    {
        if (!_dragging || CameraModeCombo.SelectedIndex == 1) return;
        var p = e.GetPosition(ViewportInput); var dx = p.X - _dragStart.X; var dy = p.Y - _dragStart.Y; _dragStart = p;
        if (_orbiting)
        {
            _yaw += dx * .01; _pitch = Math.Clamp(_pitch + dy * .01, -1.56, 1.56);
        }
        else if (_panning)
        {
            var forward = ViewportCamera.LookDirection; if (forward.LengthSquared < 1e-8) return; forward.Normalize();
            var up = ViewportCamera.UpDirection; up.Normalize(); var right = Vector3D.CrossProduct(forward, up);
            if (right.LengthSquared < 1e-8) return; right.Normalize(); up = Vector3D.CrossProduct(right, forward); up.Normalize();
            var distance = Math.Max(.05, ViewportCamera.LookDirection.Length); var scale = distance * .0018;
            _cameraTarget += (-right * dx + up * dy) * scale;
        }
        QueueCameraUpdate(); e.Handled = true;
    }
    private void ViewportInput_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.F) return;
        ResetView();
        e.Handled = true;
    }
    private void PlayAnimation_Click(object s, RoutedEventArgs e)
    {
        if (_clip is null) return;
        if (_animationTimer.IsEnabled) { _animationTimer.Stop(); PlayButton.Content = "▶"; }
        else { if (_playbackTime >= GetPlaybackDuration()) SeekPlayback(0); _lastTick = DateTime.UtcNow; _animationTimer.Start(); PlayButton.Content = "⏸"; }
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow; var delta = (now - _lastTick).TotalSeconds; _lastTick = now;
        var value = _playbackTime + delta; if (value >= GetPlaybackDuration()) value = 0;
        SeekPlayback(value);
    }

    private void AnimationSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _sliderInternal || _clip is null || _model is null) return;
        SeekPlayback(e.NewValue);
    }
    private void AnimationSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled || slider.ActualWidth <= 1) return;
        // Let WPF receive a press on the thumb so normal drag capture starts.
        // Depending on the active WPF theme, OriginalSource can be a Border or
        // another element inside the Thumb template. Walk up the visual tree
        // so the normal Thumb mouse capture is never interrupted.
        if (FindVisualParent<System.Windows.Controls.Primitives.Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        var point = e.GetPosition(slider);
        // WPF's default Slider click performs a LargeChange page step. Map the
        // pointer directly to the usable track instead, accounting for the
        // thumb's half-width at both ends so seek time does not lead or lag.
        var thumb = FindVisualChild<System.Windows.Controls.Primitives.Thumb>(slider);
        var thumbWidth = thumb?.ActualWidth > 0 ? thumb.ActualWidth : 10;
        var usable = Math.Max(1, slider.ActualWidth - thumbWidth);
        var ratio = Math.Clamp((point.X - thumbWidth * .5) / usable, 0, 1);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child); if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
    private void Filter_Changed(object s, EventArgs e) => ApplyFilter();
    private async void Rescan_Click(object s, RoutedEventArgs e) => await ScanAsync();

    private void OpenUserGuide_Click(object s, RoutedEventArgs e) => ShowUserGuide();

    private void ShowUserGuide()
    {
        new UserGuideWindow { Owner = this }.ShowDialog();
    }

    private async void OpenNameCatalog_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_root)) return;
        if (_nameCatalog is null)
        {
            IsEnabled = false;
            try
            {
                var progress = new Progress<(int Done, int Total, string File)>(p => StatusText.Text = $"正在恢复正式名称 {p.Done:N0}/{p.Total:N0} · {p.File}");
                _nameCatalog = await KidsNameCatalogReader.BuildAsync(_root, progress);
                _nameCatalogRoot = _root;
                ApplyOfficialNames(_all, _nameCatalog);
                ApplyFilter();
                StatusText.Text = $"正式名称目录完成：{_nameCatalog.Entries.Count:N0} 个名称";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "正式名称恢复失败", MessageBoxButton.OK, MessageBoxImage.Error); return;
            }
            finally { IsEnabled = true; }
        }
        new NameCatalogWindow(_nameCatalog) { Owner = this }.ShowDialog();
    }

    private async void OpenRoot_Click(object s, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 DWORIGINS 游戏目录或资源目录", InitialDirectory = Directory.Exists(_root) ? _root : null };
        if (dialog.ShowDialog(this) == true) { _root = dialog.FolderName; await ScanAsync(); }
    }

    private async void ExportFiltered_Click(object s, RoutedEventArgs e)
    {
        if (_filtered.Count == 0) return;
        var destination = GetExportDirectory("Resources");
        IsEnabled = false;
        try
        {
            var progress = new Progress<(int Done, int Total, string Name)>(p => StatusText.Text = $"正在导出 {p.Done:N0}/{p.Total:N0}：{p.Name}");
            var count = await AssetExporter.ExportOrganizedAsync(_filtered, destination, progress);
            MessageBox.Show(this, $"已按类型与资源域导出 {count:N0} 个文件，并生成 manifest.json。\n\n目录：{destination}", "导出完成"); StatusText.Text = "分类导出完成";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; }
    }

    private void ExportObj_Click(object s, RoutedEventArgs e)
    {
        if (_model is null || _selected is null) return;
        var fileName = Path.ChangeExtension(_selected.ExportFileName, ".obj");
        var destination = ConfirmFixedExportPath("Model", fileName);
        if (destination is not null) { AssetExporter.ExportObj(_model, destination); StatusText.Text = "OBJ 导出完成：" + destination; }
    }

    private async void ExportModelFbx_Click(object s, RoutedEventArgs e)
    {
        if (_model is null || _exporting) return;
        var fileName = Path.ChangeExtension(_modelAsset?.ExportFileName ?? "DWOrigins_model.g1m", ".fbx");
        var destination = ConfirmFixedExportPath("Model", fileName);
        if (destination is null) return;
        BeginExportProgress();
        try
        {
            var progress = new Progress<(string Stage, int Percent)>(ReportExportProgress);
            await Task.Run(() => BlenderFbxExporter.Export(_model, null, destination,
                Path.GetFileNameWithoutExtension(fileName), _root, includeGeometry: true, progress: progress));
            StatusText.Text = "模型 FBX 导出完成：" + destination;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FBX 导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndExportProgress(); }
    }

    private async void ExportAnimationFbx_Click(object s, RoutedEventArgs e)
    {
        if (_model is null || _clip is null || _exporting) return;
        var modelName = Path.GetFileNameWithoutExtension(_modelAsset?.ExportFileName ?? "character");
        var animationName = Path.GetFileNameWithoutExtension(_animationAsset?.ExportFileName ?? "animation");
        var fileName = $"{modelName}_{animationName}.fbx";
        var destination = ConfirmFixedExportPath("Animation", fileName);
        if (destination is null) return;
        BeginExportProgress();
        try
        {
            var progress = new Progress<(string Stage, int Percent)>(ReportExportProgress);
            await Task.Run(() => BlenderFbxExporter.Export(_model, _clip, destination, animationName, _root,
                // UE applies a different root/unit conversion to a skeletal-only FBX.
                // Keep the exact same skinned reference scene as the model export so
                // model and animation always share the same scale and armature root.
                // UE can import this as animation-only by disabling "Import Mesh".
                includeGeometry: true, progress: progress));
            StatusText.Text = "角色动画 FBX 导出完成：" + destination;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FBX 导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndExportProgress(); }
    }

    private async void ExportCameraFbx_Click(object s, RoutedEventArgs e)
    {
        if (_actionCamera is null || _model is null || _clip is null || _exporting) return;
        var fileName = $"{Path.GetFileNameWithoutExtension(_animationAsset?.ExportFileName ?? "action")}_camera.fbx";
        var destination = ConfirmFixedExportPath("Camera", fileName);
        if (destination is null) return;
        // Capture on the UI thread.  The exported CineCamera filmback uses this
        // exact aspect so UE's horizontal FOV matches the visible tool preview.
        var exportAspectRatio = CameraViewportAspect;
        BeginExportProgress();
        try
        {
            string reportPath = "";
            await Task.Run(() =>
            {
                // Export a real Blender FBX scene containing the same reference
                // character, its action, and the animated camera.  UE Sequencer
                // recognizes the camera track from the FBX Camera node; the old
                // minimal ASCII camera-only writer was silently ignored by UE.
                BlenderFbxExporter.Export(_model, _clip, destination,
                    Path.GetFileNameWithoutExtension(fileName), _root,
                    includeGeometry: false, camera: _actionCamera, cameraForUnrealSequencer: true,
                    cameraAspectRatio: exportAspectRatio);
                reportPath = FbxExporter.ExportActionCameraReport(_actionCamera, destination, exportAspectRatio);
            });
            StatusText.Text = $"镜头 FBX 与参数报告导出完成：{destination} · {reportPath}";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FBX 导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndExportProgress(); }
    }

    private string? ConfirmFixedExportPath(string category, string fileName)
    {
        var destination = Path.Combine(GetExportDirectory(category), fileName);
        if (!File.Exists(destination)) return destination;
        var answer = MessageBox.Show(this,
            $"导出文件已存在：\n{destination}\n\n是否覆盖？",
            "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes ? destination : null;
    }

    private static string GetExportDirectory(string category)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "DWOrigins_Exports", category);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void ExportSelected_Click(object s, RoutedEventArgs e)
    {
        if (_selected is null) return; var dialog = new SaveFileDialog { FileName = _selected.ExportFileName, Filter = $"{_selected.Extension} 文件|*.{_selected.Extension.ToLowerInvariant()}|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) File.Copy(_selected.FullPath, dialog.FileName, true);
    }

    private async void UnpackSelected_Click(object s, RoutedEventArgs e)
    {
        if (_selected is null || _selected.Extension != "FDATA") return;
        var dialog = new OpenFolderDialog { Title = "选择 FData 解包输出目录", InitialDirectory = GetExportDirectory("Unpacked") }; if (dialog.ShowDialog(this) != true) return;
        await UnpackPackagesAsync([_selected], dialog.FolderName);
    }

    private async void UnpackFiltered_Click(object s, RoutedEventArgs e)
    {
        var packages = _filtered.Where(x => x.Kind == AssetKind.Package && x.Extension == "FDATA").ToList();
        if (packages.Count == 0) { MessageBox.Show(this, "当前筛选结果中没有 FData 包。", "批量解包"); return; }
        var dialog = new OpenFolderDialog { Title = $"选择 {packages.Count:N0} 个 FData 包的解包输出目录", InitialDirectory = GetExportDirectory("Unpacked") }; if (dialog.ShowDialog(this) != true) return;
        await UnpackPackagesAsync(packages, dialog.FolderName);
    }

    private async Task UnpackPackagesAsync(IReadOnlyList<AssetEntry> packages, string destination)
    {
        IsEnabled = false;
        try
        {
            var expanded = Path.Combine(_root, "File", "ASSET", "FDataPackage", "Dev", "fdata_out");
            var known = await Task.Run(() => FDataUnpacker.BuildKnownExtensionMap(expanded));
            var knownPaths = await Task.Run(() => FDataUnpacker.BuildKnownRelativePathMap(expanded));
            if (_nameCatalog is null || _nameCatalogRoot != _root)
            {
                _nameCatalog = await KidsNameCatalogReader.BuildAsync(_root);
                _nameCatalogRoot = _root;
            }
            var outputNames = FDataUnpacker.BuildOutputNameMap(_nameCatalog, _root);
            FDataUnpacker.AddLogicalFallbackNames(outputNames, knownPaths);
            FDataUnpacker.AddUnresolvedFallbackNames(outputNames,
                packages.SelectMany(x => FDataUnpacker.ReadIndex(x.FullPath)).Select(x => x.ResourceId));
            long bytes = 0; var files = 0; var errors = 0;
            for (var p = 0; p < packages.Count; p++)
            {
                var package = packages[p];
                var packageIndex = p;
                var progress = new Progress<(int Done, int Total, string Name)>(x =>
                    StatusText.Text = $"包 {packageIndex + 1}/{packages.Count} · 记录 {x.Done}/{x.Total} · {x.Name}");
                var result = await FDataUnpacker.ExtractAsync(package.FullPath, destination, known, progress,
                    outputNames: outputNames, knownRelativePaths: knownPaths);
                bytes += result.OutputBytes; files += result.ExtractedCount; errors += result.Errors.Count;
            }
            StatusText.Text = $"解包完成：{files:N0} 个资源，{bytes / 1073741824d:0.00} GB，{errors:N0} 个错误";
            MessageBox.Show(this, $"解包完成。\n资源：{files:N0}\n数据量：{bytes / 1048576d:0.00} MB\n错误：{errors:N0}\n\n每个包均已生成 manifest.json。", "FData 解包");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FData 解包失败", MessageBoxButton.OK, MessageBoxImage.Error); StatusText.Text = "解包失败"; }
        finally { IsEnabled = true; }
    }

    private void ShowInExplorer_Click(object s, RoutedEventArgs e)
    {
        if (_selected is null) return; Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_selected.FullPath}\"") { UseShellExecute = true });
    }

    private void OpenExportsFolder_Click(object s, RoutedEventArgs e)
    {
        var directory = GetExportDirectory("");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
