using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using DWOriginsAssetTool.Core;
using Microsoft.Win32;

namespace DWOriginsAssetTool;

public partial class MainWindow : Window
{
    private sealed record BoneChoice(int LocalId, int GlobalId)
    {
        public string DisplayName => $"bone_{GlobalId} · L{LocalId}";
    }

    private sealed record QuickCharacterChoice(string Id, string DisplayName, AssetEntry Model)
    {
        public override string ToString()
        {
            return DisplayName;
        }
    }

    private sealed record QuickWeaponChoice(string Id, string DisplayName, AssetEntry Model)
    {
        public override string ToString()
        {
            return DisplayName;
        }
    }

    private sealed record QuickAnimationChoice(string DisplayName, AssetEntry Animation)
    {
        public override string ToString()
        {
            return DisplayName;
        }
    }

    private sealed record QuickCameraActionChoice(string DisplayName, AssetEntry Animation, QuickCharacterChoice Character, QuickWeaponChoice? Weapon)
    {
        public override string ToString()
        {
            return DisplayName;
        }
    }

    private List<AssetEntry> _all = new List<AssetEntry>();

    private List<AssetEntry> _filtered = new List<AssetEntry>();

    private string _root = "";

    private G1MInfo? _model;

    private G1AClip? _clip;

    private AssetEntry? _animationAsset;

    private IReadOnlyDictionary<string, ActionCameraCatalog.Binding> _cameraCatalog = new Dictionary<string, ActionCameraCatalog.Binding>(StringComparer.OrdinalIgnoreCase);

    private ActionCameraClip? _actionCamera;

    private IReadOnlyDictionary<string, ConfiguredCameraCatalog.AuthoredActionConfiguration> _authoredCameraEvents = new Dictionary<string, ConfiguredCameraCatalog.AuthoredActionConfiguration>(StringComparer.OrdinalIgnoreCase);

    private AssetEntry? _modelAsset;

    private readonly DispatcherTimer _animationTimer;

    private DateTime _lastTick;

    private bool _sliderInternal;

    private double _playbackTime;

    private AssetEntry? _selected;

    private string? _previewingPath;

    private readonly SemaphoreSlim _previewGate = new SemaphoreSlim(1, 1);

    private GameNameCatalog? _nameCatalog;

    private string? _nameCatalogRoot;

    private readonly ObservableCollection<BoneTreeNode> _boneRoots = new ObservableCollection<BoneTreeNode>();

    private int _renderMode = 1;

    private bool _showGrid = true;

    private bool _showAxes = true;

    private double _yaw = -0.38;

    private double _pitch = 0.35;

    private double _zoom = 1.0;

    private Point3D _cameraTarget;

    private Point _dragStart;

    private bool _dragging;

    private bool _orbiting;

    private bool _panning;

    private bool _dollying;

    private bool _dragViewportRight;

    private bool _timelineDragging;

    private CameraDataWindow? _cameraDataWindow;

    private CameraCurveWindow? _cameraCurveWindow;

    private readonly Model3DGroup _scene = new Model3DGroup();

    private readonly Model3DGroup _comparisonScene = new Model3DGroup();

    private readonly Model3DGroup _backgroundScene = new Model3DGroup();

    private readonly Model3DGroup _comparisonBackgroundScene = new Model3DGroup();

    private readonly Model3DGroup _helperScene = new Model3DGroup();

    private readonly Model3DGroup _comparisonHelperScene = new Model3DGroup();

    private readonly Model3DGroup _cameraGizmo = new Model3DGroup();

    private readonly Model3DGroup _comparisonCameraGizmo = new Model3DGroup();

    private readonly Model3DGroup _cameraTrajectory = new Model3DGroup();

    private readonly Model3DGroup _comparisonCameraTrajectory = new Model3DGroup();

    private readonly Model3DGroup _cameraTrajectoryCursor = new Model3DGroup();

    private readonly Model3DGroup _comparisonCameraTrajectoryCursor = new Model3DGroup();

    private readonly Model3DGroup _weaponGroup = new Model3DGroup();

    private readonly Model3DGroup _comparisonWeaponGroup = new Model3DGroup();

    private readonly Model3DGroup _secondaryWeaponGroup = new Model3DGroup();

    private readonly Model3DGroup _comparisonSecondaryWeaponGroup = new Model3DGroup();

    private readonly List<MeshGeometry3D> _weaponGeometries = new List<MeshGeometry3D>();

    private readonly List<MeshGeometry3D> _secondaryWeaponGeometries = new List<MeshGeometry3D>();

    private G1MInfo? _weaponModel;

    private AssetEntry? _weaponAsset;

    private ModelAttachmentComposer.Attachment? _weaponAttachment;

    private G1MInfo? _secondaryWeaponModel;

    private AssetEntry? _secondaryWeaponAsset;

    private ModelAttachmentComposer.Attachment? _secondaryWeaponAttachment;

    private const int DefaultWeaponAttachmentBoneId = 104;

    private readonly List<MeshGeometry3D> _meshGeometries = new List<MeshGeometry3D>();

    private readonly List<Point3DCollection> _meshPositionBackBuffers = new List<Point3DCollection>();

    private readonly List<GeometryModel3D> _meshModels = new List<GeometryModel3D>();

    private readonly List<GeometryModel3D> _comparisonMeshModels = new List<GeometryModel3D>();

    private readonly List<Material> _texturedMaterials = new List<Material>();

    private readonly List<Material> _solidMaterials = new List<Material>();

    private IReadOnlyList<WireframeEdge> _wireframeEdges = Array.Empty<WireframeEdge>();

    private bool _cameraDirty;

    private bool _overlayDirty;

    private Point3D _sceneCenter;

    private double _sceneRadius = 1.0;

    private Matrix4x4 _accessoryAdjustment = Matrix4x4.Identity;

    private bool _updatingAccessoryEditor;

    private readonly Dictionary<int, ImageBrush> _characterTextures = new Dictionary<int, ImageBrush>();

    private bool _startupPreviewHandled;

    private bool _exporting;

    private AnimationPoseEvaluator.Prepared? _preparedPose;

    private G1MInfo? _preparedPoseModel;

    private G1AClip? _preparedPoseClip;

    private double _lastPlaybackUiUpdate = double.NegativeInfinity;

    private readonly Stopwatch _playbackFpsClock = new Stopwatch();

    private int _playbackFrames;

    private double _comparisonYaw = 0.38;

    private double _comparisonPitch = 0.28;

    private double _comparisonZoom = 1.0;

    private Point3D _comparisonCameraTarget;

    private Point3D[] _cameraTrajectoryPoints = Array.Empty<Point3D>();

    private int _cameraTrajectoryDrawnFrame = -1;

    private float[] _cameraKeyTimes = Array.Empty<float>();

    private float[] _configurationEventTimes = Array.Empty<float>();

    private readonly Dictionary<string, QuickCharacterChoice> _quickCharacters = new Dictionary<string, QuickCharacterChoice>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, QuickWeaponChoice> _quickWeapons = new Dictionary<string, QuickWeaponChoice>(StringComparer.OrdinalIgnoreCase);

    private bool _updatingQuickPreview;

    private int _cameraPreviewRequest;

    private const double ExportFilmbackHeightMm = 24.0;

    private string? _detectedBlenderPath;

    private double CameraViewportAspect
    {
        get
        {
            if (!(Viewport.ActualHeight > 1.0))
            {
                return 1.7777777777777777;
            }
            return Math.Max(0.01, Viewport.ActualWidth / Viewport.ActualHeight);
        }
    }

    private static Point3D ToWorld(float x, float y, float z)
    {
        return new Point3D(0f - x, z, y);
    }

    private static Vector3D ToWorldNormal(float x, float y, float z)
    {
        return new Vector3D(0f - x, z, y);
    }

    private static Vector3 ToWorld(Vector3 v)
    {
        return new Vector3(0f - v.X, v.Z, v.Y);
    }

    private static double VerticalToHorizontalFov(double verticalDegrees, double aspectRatio)
    {
        double num = verticalDegrees * Math.PI / 180.0;
        return 2.0 * Math.Atan(Math.Tan(num * 0.5) * Math.Max(0.01, aspectRatio)) * 180.0 / Math.PI;
    }

    public MainWindow()
    {
        InitializeComponent();
        InitializePreviewAppearance();
        base.Dispatcher.UnhandledException += MainWindow_DispatcherUnhandledException;
        Viewport.Children.Add(new ModelVisual3D
        {
            Content = _scene
        });
        ComparisonViewport.Children.Add(new ModelVisual3D
        {
            Content = _comparisonScene
        });
        WireframeLayer.Camera = ViewportCamera;
        ComparisonWireframeLayer.Camera = ComparisonCamera;
        BoneTree.ItemsSource = _boneRoots;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16.666666666666668)
        };
        _animationTimer.Tick += AnimationTimer_Tick;
        CompositionTarget.Rendering += Viewport_Rendering;
        base.Closed += delegate
        {
            CompositionTarget.Rendering -= Viewport_Rendering;
        };
        base.Loaded += async delegate
        {
            if (!UserPreferences.HideStartupGuide)
            {
                ShowUserGuide();
            }
            string text = FindGameRoot(AppContext.BaseDirectory) ?? FindGameRoot(Environment.CurrentDirectory);
            if (text != null)
            {
                _root = text;
                UpdateGameRootDisplay();
                RefreshBlenderStatus();
                UpdateResourceInitializationState();
                await ScanAsync();
            }
            else
            {
                RefreshBlenderStatus();
            }
        };
    }

    private void MainWindow_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        StopAnimationPlayback();
        PlayButton.IsEnabled = false;
        StatusText.Text = "预览失败：" + e.Exception.Message;
        PreviewSummary.Text = "动画预览遇到异常，模型仍可继续选择";
    }

    private static string? FindGameRoot(string start)
    {
        for (DirectoryInfo directoryInfo = new DirectoryInfo(start); directoryInfo != null; directoryInfo = directoryInfo.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(directoryInfo.FullName, "File")) && File.Exists(System.IO.Path.Combine(directoryInfo.FullName, "DWORIGINS.exe")))
            {
                return directoryInfo.FullName;
            }
        }
        return null;
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
        {
            return;
        }
        base.IsEnabled = false;
        StatusText.Text = "正在扫描资源，请稍候…";
        UpdateProgressPanelPlacement();
        ExportProgressPanel.Visibility = Visibility.Visible;
        ExportProgressBar.IsIndeterminate = true;
        ExportProgressBar.Value = 0.0;
        ExportProgressBar.Opacity = 1.0;
        ExportProgressPercentText.Text = "扫描中";
        ExportProgressText.Text = "正在加载游戏资源";
        ExportProgressDetailText.Text = "正在枚举资源文件，请勿关闭工具";
        try
        {
            string organizedRoot = System.IO.Path.Combine(_root, "DWOrigins_Unpacked");
            string scanRoot = (Directory.Exists(organizedRoot) ? organizedRoot : (Directory.Exists(System.IO.Path.Combine(_root, "File")) ? System.IO.Path.Combine(_root, "File") : _root));
            Progress<(int, string)> progress = new Progress<(int, string)>(delegate ((int Count, string Path) p)
            {
                StatusText.Text = $"已识别 {p.Count:N0} 个资源… {System.IO.Path.GetFileName(p.Path)}";
                ExportProgressText.Text = $"正在扫描资源 · 已识别 {p.Count:N0} 个";
                ExportProgressDetailText.Text = System.IO.Path.GetFileName(p.Path);
            });
            Task<List<AssetEntry>> scanTask = AssetScanner.ScanAsync(scanRoot, progress);
            Task<GameNameCatalog> catalogTask = (Task<GameNameCatalog>)(((object)_nameCatalog != null && _nameCatalogRoot == _root) ? ((Task)Task.FromResult(_nameCatalog)) : ((Task)KidsNameCatalogReader.BuildAsync(_root, new Progress<(int, int, string)>(delegate ((int Done, int Total, string File) p)
            {
                StatusText.Text = $"正在恢复正式名称 {p.Done:N0}/{p.Total:N0} · {p.File}";
                ExportProgressText.Text = "正在恢复正式名称";
                ExportProgressDetailText.Text = $"{p.Done:N0}/{p.Total:N0} · {p.File}";
            }))));
            Task<Dictionary<string, string>> manifestTask = (Task<Dictionary<string, string>>)(scanRoot.Equals(organizedRoot, StringComparison.OrdinalIgnoreCase) ? ((Task)Task.Run(() => OrganizedResourceCatalog.ReadManifestResourceIds(organizedRoot))) : ((Task)Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))));
            await Task.WhenAll(scanTask, catalogTask, manifestTask);
            ExportProgressBar.IsIndeterminate = false;
            ExportProgressBar.Value = 90.0;
            ExportProgressPercentText.Text = "90%";
            ExportProgressText.Text = "正在建立资源关联";
            ExportProgressDetailText.Text = "正在读取动作表和动画镜头索引";
            _cameraCatalog = await Task.Run(() => ActionCameraCatalog.Build(_root));
            _authoredCameraEvents = await Task.Run(() => ConfiguredCameraCatalog.ReadAuthoredActionConfigurations(_root));
            _all = await scanTask;
            _nameCatalog = await catalogTask;
            _nameCatalogRoot = _root;
            int value = 0;
            if (scanRoot.Equals(organizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<AssetEntry> all = _all;
                value = OrganizedResourceCatalog.ApplyResourceIds(all, await manifestTask);
                foreach (AssetEntry item in _all)
                {
                    item.RestoreOfficialNameFromOrganizedPath();
                }
            }
            else
            {
                ApplyOfficialNames(_all, _nameCatalog);
            }
            int value2 = OrganizedResourceCatalog.ApplyGameAnimationCatalog(_all, _root, _cameraCatalog);
            StatusText.Text = $"正在建立目录树 · 已恢复 {value:N0} 个清单资源 ID、{value2:N0} 个游戏本体动作表归属";
            foreach (AssetEntry item2 in _all.Where(delegate (AssetEntry a)
            {
                AssetKind kind = a.Kind;
                return ((uint)kind <= 3u || kind == AssetKind.Package) ? true : false;
            }).Take(100))
            {
                AssetScanner.ValidateMagic(item2.FullPath, item2.Kind);
            }
            DomainBox.Items.Clear();
            DomainBox.Items.Add("全部资源域");
            foreach (string item3 in from x in _all.Select((AssetEntry x) => x.Domain).Distinct<string>(StringComparer.OrdinalIgnoreCase)
                                     orderby x
                                     select x)
            {
                DomainBox.Items.Add(item3);
            }
            DomainBox.SelectedIndex = 0;
            ApplyFilter();
            BuildQuickPreviewCatalog();
            ExportProgressBar.Value = 100.0;
            ExportProgressPercentText.Text = "100%";
            ExportProgressText.Text = "游戏资源加载完成";
            ExportProgressDetailText.Text = $"已加载 {_all.Count:N0} 个资源";
            StatusText.Text = $"扫描完成：{_all.Count:N0} 个资源 · {_nameCatalog.Bindings?.Count ?? 0:N0} 条正式名称映射 · {value2:N0} 个游戏本体动作表归属 · {_cameraCatalog.Count:N0} 组动画镜头。";
            await ApplyStartupPreviewAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Hand);
            StatusText.Text = "扫描失败";
        }
        finally
        {
            base.IsEnabled = true;
            SetProgressIdle(StatusText.Text);
            UpdateResourceInitializationState();
        }
    }

    private void UpdateResourceInitializationState()
    {
        if (InitializeResourcesButton != null)
        {
            bool flag = !string.IsNullOrWhiteSpace(_root) && Directory.Exists(_root);
            bool flag2 = flag && GameResourceInitializer.IsComplete(_root);
            InitializationRequiredPanel.Visibility = (flag2 ? Visibility.Collapsed : Visibility.Visible);
            FirstUseStepsText.Visibility = (flag2 ? Visibility.Collapsed : Visibility.Visible);
            InitializeResourcesButton.IsEnabled = flag && !_exporting;
            InitializeResourcesButton.Content = (flag ? "立即初始化完整游戏资源（必选）" : "先完成第 1 步：选择游戏目录");
            InitializationRequiredText.Text = (flag ? "游戏目录已选择。请继续初始化，完成后才能获得正式名称、角色/武器/动画分类和镜头关联。" : "请先选择包含 DWORIGINS.exe 的游戏目录，然后继续初始化完整游戏资源。");
        }
    }

    private async void InitializeResources_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_root) || _exporting)
        {
            return;
        }
        IReadOnlyList<string> readOnlyList = GameResourceInitializer.FindPackages(_root);
        if (readOnlyList.Count == 0)
        {
            MessageBox.Show(this, "当前目录没有找到游戏 FData 资源包，请重新选择包含 DWORIGINS.exe 的游戏根目录。", "无法初始化", MessageBoxButton.OK, MessageBoxImage.Exclamation);
        }
        else
        {
            if (MessageBox.Show(this, $"将从游戏本体的 {readOnlyList.Count:N0} 个资源包恢复完整目录。\n\n" + "完成后会自动恢复正式名称、角色/武器/动画分类、动作表归属和镜头关联。首次处理耗时较长，并需要额外磁盘空间。\n\n现在开始吗？", "初始化完整游戏资源", MessageBoxButton.YesNo, MessageBoxImage.Asterisk) != MessageBoxResult.Yes)
            {
                return;
            }
            _exporting = true;
            base.IsEnabled = false;
            InitializeResourcesButton.IsEnabled = false;
            UpdateProgressPanelPlacement();
            ExportProgressPanel.Visibility = Visibility.Visible;
            ExportProgressBar.IsIndeterminate = false;
            ExportProgressBar.Value = 0.0;
            ExportProgressBar.Opacity = 1.0;
            ExportProgressPercentText.Text = "0%";
            ExportProgressText.Text = "准备初始化";
            ExportProgressDetailText.Text = $"正在分析 {readOnlyList.Count:N0} 个游戏资源包，请勿关闭工具";
            try
            {
                Progress<GameResourceInitializationProgress> progress = new Progress<GameResourceInitializationProgress>(delegate (GameResourceInitializationProgress value)
                {
                    double value2 = Math.Clamp(value.OverallPercent, 0.0, 100.0);
                    ExportProgressBar.Value = value2;
                    ExportProgressPercentText.Text = $"{value2:0.0}%";
                    ExportProgressText.Text = value.Stage;
                    string value3 = ((value.PackageDone > 0) ? $"资源包 {value.PackageDone:N0}/{value.PackageTotal:N0}" : $"共 {value.PackageTotal:N0} 个资源包");
                    string value4 = ((value.RecordTotal > 1) ? $" · 记录 {value.RecordDone:N0}/{value.RecordTotal:N0}" : "");
                    string value5 = ((value.OutputBytes > 0) ? $" · 已处理 {(double)value.OutputBytes / 1024.0 / 1024.0 / 1024.0:0.0} GB" : "");
                    ExportProgressDetailText.Text = $"{value3}{value4}{value5}\n{value.CurrentFile}";
                    StatusText.Text = $"{value.Stage} {value2:0.0}% · {value3} · {value.CurrentFile}";
                });
                string initializationRoot = _root;
                GameResourceInitializationResult result = await Task.Run(() => GameResourceInitializer.InitializeAsync(initializationRoot, progress));
                ExportProgressBar.Value = 100.0;
                ExportProgressPercentText.Text = "100%";
                ExportProgressText.Text = "完整资源初始化完成，正在建立正式目录";
                ExportProgressDetailText.Text = "正式名称、动作分类和镜头关联均已通过校验";
                _nameCatalog = null;
                _nameCatalogRoot = null;
                _startupPreviewHandled = false;
                await ScanAsync();
                MessageBox.Show(this, $"完整资源已就绪。\n\n资源：{result.ResourceCount:N0}\n正式名称映射：{result.OfficialNameCount:N0}\n动作归属：{result.GameAnimationCount:N0}\n镜头关联：{result.CameraCount:N0}\n动画解码：{result.DecodedAnimationCount:N0}\n续跑跳过资源包：{result.ResumedPackageCount:N0}\n错误：{result.ErrorCount:N0}", "初始化完成", MessageBoxButton.OK, MessageBoxImage.Asterisk);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message + "\n\n已完成的文件会保留，下次点击可继续处理。", "初始化失败", MessageBoxButton.OK, MessageBoxImage.Hand);
                StatusText.Text = "初始化未完成，可再次点击继续";
            }
            finally
            {
                _exporting = false;
                base.IsEnabled = true;
                SetProgressIdle(StatusText.Text);
                UpdateResourceInitializationState();
            }
        }
    }

    private async Task ApplyStartupPreviewAsync()
    {
        if (_startupPreviewHandled)
        {
            return;
        }
        _startupPreviewHandled = true;
        string[] args = Environment.GetCommandLineArgs();
        string modelId = Value(args, "--model");
        string animationId = Value(args, "--animation");
        if (modelId != null)
        {
            AssetEntry assetEntry = _all.FirstOrDefault((AssetEntry x) => x.Kind == AssetKind.Model && x.HashId.Equals(modelId, StringComparison.OrdinalIgnoreCase));
            if (assetEntry != null)
            {
                await PreviewAssetAsync(assetEntry);
            }
        }
        if (animationId != null)
        {
            AssetEntry assetEntry2 = _all.FirstOrDefault((AssetEntry x) => x.Kind == AssetKind.Animation && x.HashId.Equals(animationId, StringComparison.OrdinalIgnoreCase));
            if (assetEntry2 != null)
            {
                await PreviewAssetAsync(assetEntry2);
            }
        }
        else if (animationId == null && StartupHeroCheck.IsChecked == true)
        {
            AssetEntry assetEntry3 = _all.FirstOrDefault((AssetEntry x) => x.Kind == AssetKind.Model && x.HashId.Equals("635c4e68", StringComparison.OrdinalIgnoreCase)) ?? _all.Where((AssetEntry x) => x.Kind == AssetKind.Model && (string.Equals(x.OwnerName, "H0100_Hero0", StringComparison.OrdinalIgnoreCase) || (x.OfficialName?.StartsWith("H0100_Hero0", StringComparison.OrdinalIgnoreCase) ?? false))).OrderBy<AssetEntry, string>((AssetEntry x) => x.HashId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (assetEntry3 != null)
            {
                await PreviewAssetAsync(assetEntry3);
            }
        }
        if (double.TryParse(Value(args, "--time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            SeekPlayback(result);
        }
        if (args.Any((string x) => x.Equals("--play", StringComparison.OrdinalIgnoreCase)) && PlayButton.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            ResetPlaybackFps(playing: true);
            _animationTimer.Start();
            PlayButton.Content = "⏸";
        }
        static string? Value(string[] values, string key)
        {
            int num = Array.FindIndex(values, (string x) => x.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (num < 0 || num + 1 >= values.Length)
            {
                return null;
            }
            return values[num + 1];
        }
    }

    private void ApplyFilter()
    {
        if (!base.IsLoaded)
        {
            return;
        }
        string query = SearchBox.Text.Trim();
        string text = (KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        string domain = DomainBox.SelectedItem?.ToString();
        IEnumerable<AssetEntry> source = _all;
        if (text != "All" && Enum.TryParse<AssetKind>(text, out var kind))
        {
            source = source.Where((AssetEntry x) => x.Kind == kind);
        }
        if (!string.IsNullOrEmpty(domain) && domain != "全部资源域")
        {
            source = source.Where((AssetEntry x) => x.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
        }
        if (query.Length > 0)
        {
            source = source.Where((AssetEntry x) => x.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        _filtered = source.ToList();
        ShowTree();
    }

    private void ShowTree()
    {
        AssetTree.ItemsSource = ResourceTreeNode.CreateRoots(_filtered);
        CountText.Text = $"当前 {_filtered.Count:N0} / 总计 {_all.Count:N0}";
    }

    private static void ApplyOfficialNames(IEnumerable<AssetEntry> assets, GameNameCatalog catalog)
    {
        if (catalog.Bindings == null)
        {
            return;
        }
        Dictionary<string, List<GameResourceBinding>> dictionary = catalog.Bindings.GroupBy<GameResourceBinding, string>((GameResourceBinding x) => x.ResourceId, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, GameResourceBinding>, string, List<GameResourceBinding>>((IGrouping<string, GameResourceBinding> x) => x.Key, (IGrouping<string, GameResourceBinding> x) => x.ToList(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (int, int)> dictionary2 = (from x in catalog.Bindings
                                                      group x by (OfficialName: x.OfficialName, ResourceKind: x.ResourceKind)).SelectMany((IGrouping<(string OfficialName, string ResourceKind), GameResourceBinding> group) => group.Select((GameResourceBinding binding, int index) => (Key: $"{binding.OfficialName}\0{binding.ResourceKind}\0{binding.ResourceId}", Ordinal: index + 1, Total: group.Count()))).ToDictionary<(string, int, int), string, (int, int)>(((string Key, int Ordinal, int Total) x) => x.Key, ((string Key, int Ordinal, int Total) x) => (Ordinal: x.Ordinal, Total: x.Total), StringComparer.OrdinalIgnoreCase);
        foreach (AssetEntry asset in assets)
        {
            if (!dictionary.TryGetValue(asset.HashId, out var value))
            {
                continue;
            }
            List<string> list = new List<string>();
            foreach (GameResourceBinding item3 in value.DistinctBy((GameResourceBinding x) => (OfficialName: x.OfficialName, ResourceKind: x.ResourceKind)))
            {
                string key = $"{item3.OfficialName}\0{item3.ResourceKind}\0{item3.ResourceId}";
                (int, int) tuple = dictionary2[key];
                int item = tuple.Item1;
                int item2 = tuple.Item2;
                string text = EnglishResourceKind(item3.ResourceKind);
                list.Add((item2 > 1) ? $"{item3.OfficialName}_{text}_{item:D3}" : (item3.OfficialName + "_" + text));
            }
            asset.OfficialName = string.Join(" + ", list.OrderBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase));
            asset.OwnerName = value.Select((GameResourceBinding x) => x.OfficialName).FirstOrDefault();
        }
    }

    private static string EnglishResourceKind(string kind)
    {
        return kind switch
        {
            "模型" => "Model",
            "动画" => "Animation",
            "骨骼" => "Skeleton",
            "贴图" => "Texture",
            "材质" => "Material",
            "碰撞" => "Collision",
            _ => "Resource",
        };
    }

    private void BuildQuickPreviewCatalog()
    {
        _quickCharacters.Clear();
        _quickWeapons.Clear();
        List<IGrouping<string, AssetEntry>> list = (from @group in _all.Where(delegate (AssetEntry x)
            {
                if (x.Kind == AssetKind.Model)
                {
                    string ownerName2 = x.OwnerName;
                    if (ownerName2 != null && ownerName2.Length > 0 && ownerName2.StartsWith("H", StringComparison.OrdinalIgnoreCase))
                    {
                        return !ownerName2.StartsWith("HW", StringComparison.OrdinalIgnoreCase);
                    }
                }
                return false;
            }).GroupBy<AssetEntry, string>((AssetEntry x) => x.OwnerName, StringComparer.OrdinalIgnoreCase)
                                                    select @group.OrderByDescending((AssetEntry x) => x.Size).ThenBy<AssetEntry, string>((AssetEntry x) => x.FullPath, StringComparer.OrdinalIgnoreCase).First()).OrderBy<AssetEntry, string>((AssetEntry x) => x.OwnerName, StringComparer.OrdinalIgnoreCase).ToList().GroupBy<AssetEntry, string>((AssetEntry model) => OfficialNameAnnotations.Get(model.OwnerName) ?? "其他角色", StringComparer.OrdinalIgnoreCase)
            .ToList();
        int num = 0;
        foreach (IGrouping<string, AssetEntry> item in list)
        {
            List<AssetEntry> list2 = item.ToList();
            for (int num2 = 0; num2 < list2.Count; num2++)
            {
                AssetEntry assetEntry = list2[num2];
                string ownerName = assetEntry.OwnerName;
                string displayName = ((item.Key == "其他角色") ? $"其他角色 {++num:00}" : ((list2.Count == 1) ? item.Key : $"{item.Key} {num2 + 1}"));
                _quickCharacters[ownerName] = new QuickCharacterChoice(ownerName, displayName, assetEntry);
            }
        }
        foreach (IGrouping<string, AssetEntry> item2 in from x in _all.Where(delegate (AssetEntry x)
            {
                if (x.Kind == AssetKind.Model && x.Size > 0)
                {
                    string ownerName2 = x.OwnerName;
                    if (ownerName2 != null && ownerName2.Length > 0)
                    {
                        return ownerName2.StartsWith("HW", StringComparison.OrdinalIgnoreCase);
                    }
                }
                return false;
            }).OrderBy<AssetEntry, string>((AssetEntry x) => x.OwnerName, StringComparer.OrdinalIgnoreCase).ThenBy<AssetEntry, string>((AssetEntry x) => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList()
                                                        group x by OfficialNameAnnotations.Get(x.OwnerName) ?? "其他武器")
        {
            List<AssetEntry> list3 = item2.ToList();
            for (int num3 = 0; num3 < list3.Count; num3++)
            {
                AssetEntry assetEntry2 = list3[num3];
                string text = assetEntry2.OwnerName + "\0" + assetEntry2.HashId;
                string displayName2 = ((list3.Count == 1) ? item2.Key : $"{item2.Key} · 款式 {num3 + 1:00}");
                _quickWeapons[text] = new QuickWeaponChoice(text, displayName2, assetEntry2);
            }
        }
        _updatingQuickPreview = true;
        QuickCharacterBox.ItemsSource = _quickCharacters.Values.ToList();
        QuickCharacterBox.IsEnabled = _quickCharacters.Count > 0;
        LoadQuickPreviewButton.IsEnabled = _quickCharacters.Count > 0;
        QuickCharacterBox.SelectedItem = _quickCharacters.Values.FirstOrDefault((QuickCharacterChoice x) => x.Id.Equals("H0100_Hero0", StringComparison.OrdinalIgnoreCase)) ?? _quickCharacters.Values.FirstOrDefault();
        BuildQuickCameraActions();
        _updatingQuickPreview = false;
        if (QuickCharacterBox.SelectedItem is QuickCharacterChoice choice)
        {
            ApplyQuickCharacterAsync(choice);
        }
    }

    private void BuildQuickWeapons(string characterId, AssetEntry? preferred = null)
    {
        _updatingQuickPreview = true;
        List<QuickWeaponChoice> list = _quickWeapons.Values.ToList();
        QuickWeaponBox.ItemsSource = list;
        QuickWeaponBox.IsEnabled = list.Count > 0;
        string prefix = CharacterWeaponCatalog.WeaponPrefixForCharacter(characterId);
        QuickWeaponChoice selectedItem = ((preferred != null) ? list.FirstOrDefault((QuickWeaponChoice x) => x.Model == preferred || x.Model.HashId.Equals(preferred.HashId, StringComparison.OrdinalIgnoreCase)) : (string.IsNullOrWhiteSpace(prefix) ? null : list.FirstOrDefault((QuickWeaponChoice x) => x.Model.OwnerName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false)));
        QuickWeaponBox.SelectedItem = selectedItem;
        _updatingQuickPreview = false;
    }

    private void BuildQuickAnimations(string characterId, QuickWeaponChoice? weapon)
    {
        string weaponPrefix = CharacterWeaponCatalog.WeaponPrefixFromModel(weapon?.Model.OwnerName);
        List<AssetEntry> source = _all.Where(delegate (AssetEntry x)
        {
            if (x.Kind == AssetKind.Animation && !x.IsActionCamera)
            {
                if (weaponPrefix != null)
                {
                    return x.GameActionOwners?.Any((string owner) => CharacterWeaponCatalog.UsesWeapon(owner, weaponPrefix)) ?? false;
                }
                IReadOnlyList<string>? gameActionOwners = x.GameActionOwners;
                if (gameActionOwners == null || !gameActionOwners.Any((string owner) => owner.Equals(characterId, StringComparison.OrdinalIgnoreCase)))
                {
                    return string.Equals(x.OwnerName, characterId, StringComparison.OrdinalIgnoreCase);
                }
                return true;
            }
            return false;
        }).OrderBy<AssetEntry, string>((AssetEntry x) => x.TreeDisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        Dictionary<string, int> categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<QuickAnimationChoice> list = source.Select(delegate (AssetEntry asset)
        {
            string text2 = asset.GameActionOwners?.FirstOrDefault((string candidate) => weaponPrefix == null || CharacterWeaponCatalog.UsesWeapon(candidate, weaponPrefix));
            string text3 = ((text2 == null) ? FriendlyActionCategory(asset.GameActionCategory) : CharacterWeaponCatalog.Format(text2));
            categoryCounts.TryGetValue(text3, out var value2);
            value2 = (categoryCounts[text3] = value2 + 1);
            string value3 = (string.IsNullOrWhiteSpace(asset.ActionCameraResourceId) ? "" : "（含镜头）");
            return new QuickAnimationChoice($"{text3} · 动作 {value2:000}{value3}", asset);
        }).ToList();
        QuickAnimationBox.ItemsSource = list;
        QuickAnimationBox.IsEnabled = list.Count > 0;
        QuickAnimationBox.SelectedItem = null;
        string value = OfficialNameAnnotations.Get(characterId) ?? "当前角色";
        string text = (((object)weapon == null) ? "当前角色" : weapon.DisplayName.Split('·')[0].Trim());
        QuickPreviewHint.Text = ((list.Count == 0) ? ("未找到与“" + text + "”可靠对应的动作集。") : $"{value}使用{text}：已找到 {list.Count:N0} 个对应动作。");
    }

    private void BuildQuickCameraActions()
    {
        Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<AssetEntry> list = _all.Where((AssetEntry x) => x.Kind == AssetKind.Animation && !x.IsActionCamera && !string.IsNullOrWhiteSpace(x.ActionCameraResourceId)).OrderBy<AssetEntry, string>((AssetEntry x) => x.GameActionOwners?.FirstOrDefault(), StringComparer.OrdinalIgnoreCase).ThenBy<AssetEntry, string>((AssetEntry x) => x.TreeDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<QuickCameraActionChoice> list2 = new List<QuickCameraActionChoice>();
        foreach (AssetEntry item in list)
        {
            string text = item.GameActionOwners?.FirstOrDefault();
            QuickCharacterChoice quickCharacterChoice = FindQuickCharacterForActionOwner(text);
            if ((object)quickCharacterChoice != null)
            {
                string text2 = (string.IsNullOrWhiteSpace(text) ? FriendlyActionCategory(item.GameActionCategory) : CharacterWeaponCatalog.Format(text));
                dictionary.TryGetValue(text2, out var value);
                value = (dictionary[text2] = value + 1);
                QuickWeaponChoice weapon = FindDefaultQuickWeapon(text ?? quickCharacterChoice.Id);
                list2.Add(new QuickCameraActionChoice($"{text2} · 镜头动作 {value:000}", item, quickCharacterChoice, weapon));
            }
        }
        QuickCameraActionBox.ItemsSource = list2;
        QuickCameraActionBox.IsEnabled = list2.Count > 0;
        QuickCameraActionBox.SelectedItem = null;
        CameraPreviewHint.Text = $"已从游戏本体确认 {_cameraCatalog.Count:N0} 组动作镜头；其中 {list2.Count:N0} 组已恢复固定角色模型，可直接预览。";
    }

    private QuickCharacterChoice? FindQuickCharacterForActionOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return null;
        }
        if (_quickCharacters.TryGetValue(owner, out QuickCharacterChoice value))
        {
            return value;
        }
        int num = owner.IndexOf('_');
        string prefix = ((num >= 0) ? owner.Substring(0, num + 1) : owner);
        return _quickCharacters.Values.FirstOrDefault((QuickCharacterChoice x) => x.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string FriendlyActionCategory(string? category)
    {
        return category switch
        {
            "角色/武将动作表" => "角色动作",
            "共享动作表" => "共享动作",
            "事件动作表" => "事件动作",
            "大地图动作表" => "大地图动作",
            "ACT 动作表" => "战斗动作",
            "游戏动作表" => "通用动作",
            "动画镜头" => "镜头动画",
            _ => "角色动作",
        };
    }

    private QuickWeaponChoice? FindDefaultQuickWeapon(string characterId)
    {
        string prefix = CharacterWeaponCatalog.WeaponPrefixForCharacter(characterId);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            return _quickWeapons.Values.FirstOrDefault((QuickWeaponChoice x) => x.Model.OwnerName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false);
        }
        return null;
    }

    private async void QuickCharacter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingQuickPreview && QuickCharacterBox.SelectedItem is QuickCharacterChoice choice)
        {
            await ApplyQuickCharacterAsync(choice);
        }
    }

    private async Task ApplyQuickCharacterAsync(QuickCharacterChoice choice)
    {
        _updatingQuickPreview = true;
        await PreviewAssetAsync(choice.Model);
        BuildQuickWeapons(choice.Id);
        QuickWeaponChoice defaultWeapon = FindDefaultQuickWeapon(choice.Id);
        if ((object)defaultWeapon != null)
        {
            QuickWeaponBox.SelectedItem = defaultWeapon;
            await LoadAccessoryAssetAsync(defaultWeapon.Model);
        }
        BuildQuickAnimations(choice.Id, defaultWeapon);
        _updatingQuickPreview = false;
        StatusText.Text = "已加载 " + choice.DisplayName + " · 默认武器与角色动作已准备";
    }

    private async void QuickWeapon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingQuickPreview)
        {
            return;
        }
        object selectedItem = QuickWeaponBox.SelectedItem;
        if (!(selectedItem is QuickWeaponChoice choice))
        {
            return;
        }
        _updatingQuickPreview = true;
        try
        {
            await LoadAccessoryAssetAsync(choice.Model);
            if (QuickCharacterBox.SelectedItem is QuickCharacterChoice quickCharacterChoice)
            {
                BuildQuickAnimations(quickCharacterChoice.Id, choice);
            }
        }
        finally
        {
            _updatingQuickPreview = false;
        }
    }

    private async void QuickAnimation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingQuickPreview && QuickAnimationBox.SelectedItem is QuickAnimationChoice quickAnimationChoice)
        {
            await PreviewAssetAsync(quickAnimationChoice.Animation);
            StartAnimationPlaybackFromBeginning();
        }
    }

    private async void QuickCameraAction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QuickCameraActionBox.SelectedItem is QuickCameraActionChoice quickCameraActionChoice)
        {
            CameraPreviewCharacterText.Text = (((object)quickCameraActionChoice.Weapon == null) ? quickCameraActionChoice.Character.DisplayName : (quickCameraActionChoice.Character.DisplayName + " · " + quickCameraActionChoice.Weapon.DisplayName));
            CameraPreviewAnimationText.Text = quickCameraActionChoice.DisplayName;
            CameraPreviewCameraText.Text = quickCameraActionChoice.Animation.ActionCameraResourceId ?? "未找到";
            await PlayFixedCameraPreviewAsync(quickCameraActionChoice, ++_cameraPreviewRequest);
        }
    }

    private async Task PlayFixedCameraPreviewAsync(QuickCameraActionChoice cameraAction, int request)
    {
        _updatingQuickPreview = true;
        try
        {
            await PreviewAssetAsync(cameraAction.Character.Model);
            if (request != _cameraPreviewRequest)
            {
                return;
            }
            if ((object)cameraAction.Weapon != null)
            {
                await LoadAccessoryAssetAsync(cameraAction.Weapon.Model);
            }
            if (request != _cameraPreviewRequest)
            {
                return;
            }
            await PreviewAssetAsync(cameraAction.Animation);
            if (request == _cameraPreviewRequest)
            {
                if ((object)_actionCamera == null)
                {
                    StatusText.Text = "该动作的镜头资源读取失败，未开始播放";
                    return;
                }
                CameraModeCombo.SelectedIndex = 1;
                if (DualViewportCheck.IsChecked == true)
                {
                    ComparisonCameraModeCombo.SelectedIndex = 0;
                }
                SeekPlayback(0.0);
                _lastTick = DateTime.UtcNow;
                ResetPlaybackFps(playing: true);
                _animationTimer.Start();
                PlayButton.Content = "⏸";
                StatusText.Text = "正在播放 " + cameraAction.Character.DisplayName + " · " + cameraAction.DisplayName;
            }
        }
        finally
        {
            _updatingQuickPreview = false;
        }
    }

    private async void LoadQuickPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingQuickPreview)
        {
            return;
        }
        object selectedItem = QuickCharacterBox.SelectedItem;
        if (!(selectedItem is QuickCharacterChoice character))
        {
            return;
        }
        QuickWeaponChoice weapon = QuickWeaponBox.SelectedItem as QuickWeaponChoice;
        QuickAnimationChoice animation = QuickAnimationBox.SelectedItem as QuickAnimationChoice;
        _updatingQuickPreview = true;
        LoadQuickPreviewButton.IsEnabled = false;
        try
        {
            await PreviewAssetAsync(character.Model);
            if ((object)weapon != null)
            {
                await LoadAccessoryAssetAsync(weapon.Model);
            }
            if ((object)animation != null)
            {
                await PreviewAssetAsync(animation.Animation);
                StartAnimationPlaybackFromBeginning();
            }
            StatusText.Text = (((object)animation == null) ? ("已重新加载 " + character.DisplayName + " 与当前武器") : ("已重新加载 " + character.DisplayName + "、当前武器与动画"));
        }
        finally
        {
            _updatingQuickPreview = false;
            LoadQuickPreviewButton.IsEnabled = _quickCharacters.Count > 0;
        }
    }

    private void AssetTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: ResourceTreeNode dataContext })
        {
            dataContext.LoadChildren();
        }
    }

    private async void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!(e.NewValue is ResourceTreeNode resourceTreeNode))
        {
            return;
        }
        AssetEntry asset = resourceTreeNode.Asset;
        if (asset != null)
        {
            _selected = asset;
            DetailTitle.Text = asset.AnnotatedDisplayName;
            DetailPath.Text = asset.RelativePath;
            RelationBody.Text = BuildRelationText(asset);
            UpdateActionCameraInspector(asset);
            DetailBody.Text = "双击资源以加载预览。";
            if (asset.Kind == AssetKind.Animation)
            {
                StatusText.Text = "正在加载动画 " + asset.AnnotatedDisplayName + " · 将叠加到当前模型";
                await PreviewAssetAsync(asset);
            }
            else
            {
                StatusText.Text = "已选择 " + asset.AnnotatedDisplayName + " · 双击加载预览";
            }
        }
    }

    private async void AssetTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AssetTree.SelectedItem is ResourceTreeNode resourceTreeNode)
        {
            AssetEntry asset = resourceTreeNode.Asset;
            if (asset != null)
            {
                await PreviewAssetAsync(asset);
                e.Handled = true;
            }
        }
    }

    private async Task PreviewAssetAsync(AssetEntry asset)
    {
        if (string.Equals(_previewingPath, asset.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _previewingPath = asset.FullPath;
        await _previewGate.WaitAsync();
        _selected = asset;
        AssetKind kind = asset.Kind;
        if ((uint)kind <= 1u)
        {
            StopAnimationPlayback();
        }
        if (asset.Kind == AssetKind.Model)
        {
            _clip = null;
            _actionCamera = null;
            _animationAsset = null;
            WeaponCheck.IsChecked = false;
            WeaponCheck.IsEnabled = false;
            _weaponAsset = null;
            _weaponModel = null;
            _weaponAttachment = null;
            _weaponGeometries.Clear();
            _weaponGroup.Children.Clear();
            _comparisonWeaponGroup.Children.Clear();
            _secondaryWeaponGroup.Children.Clear();
            _comparisonSecondaryWeaponGroup.Children.Clear();
            PlayButton.IsEnabled = false;
            AnimationSlider.IsEnabled = false;
            CameraCurveButton.IsEnabled = false;
            CameraModeCombo.SelectedIndex = 0;
            ComparisonCameraModeCombo.SelectedIndex = 0;
            UpdateCameraModeAvailability(hasActionCamera: false, hasConfiguredCamera: false);
            CameraTrajectoryCheck.IsEnabled = false;
            CameraTrajectoryCheck.IsChecked = false;
            _cameraTrajectory.Children.Clear();
            _cameraTrajectoryCursor.Children.Clear();
        }
        EmptyState.Visibility = Visibility.Collapsed;
        DetailTitle.Text = asset.AnnotatedDisplayName;
        DetailPath.Text = asset.RelativePath;
        RelationBody.Text = BuildRelationText(asset);
        UpdateActionCameraInspector(asset);
        StatusText.Text = "正在读取 " + asset.AnnotatedDisplayName + "…";
        string completionStatus = null;
        try
        {
            if (asset.Kind == AssetKind.Model)
            {
                await LoadModelAssetAsync(asset, updateInspector: true);
            }
            else if (asset.Kind == AssetKind.Animation)
            {
                G1AInfo a = await Task.Run(() => G1Parsers.ReadAnimation(asset.FullPath));
                if (asset.IsActionCamera)
                {
                    DetailBody.Text = $"资源类型：动画镜头\n镜头动画版本：{a.Version}\n文件大小：{asset.SizeText}\n时长：{a.Duration:0.###} 秒\n镜头变换轨道：{a.TrackCount}\n\n该资源由角色动作自动调用，不会作为骨骼动画套到人物上。请选择与它关联的角色动作，再把播放栏切换到“动画镜头”。";
                    PreviewSummary.Text = $"{asset.AnnotatedDisplayName} · 动画镜头资源 · {a.Duration:0.###} 秒";
                    StatusText.Text = "已识别动画镜头 " + asset.AnnotatedDisplayName + "；请选择对应角色动作进行同步播放";
                    return;
                }
                _animationAsset = asset;
                string clipError = null;
                string cameraError = null;
                try
                {
                    _clip = await Task.Run(() => G1Parsers.ReadAnimationClip(asset.FullPath));
                }
                catch (Exception ex)
                {
                    _clip = null;
                    clipError = ex.Message;
                }
                _actionCamera = null;
                if (!string.IsNullOrWhiteSpace(asset.ResourceId) && _cameraCatalog.TryGetValue(asset.ResourceId, out ActionCameraCatalog.Binding cameraBinding))
                {
                    try
                    {
                        _actionCamera = await Task.Run(() => ActionCameraCatalog.Load(cameraBinding));
                    }
                    catch (Exception ex2)
                    {
                        cameraError = ex2.Message;
                    }
                }
                SetCameraKeyframes(_actionCamera);
                UpdateCameraModeAvailability((object)_actionCamera != null, hasConfiguredCamera: false);
                CameraModeCombo.SelectedIndex = ((object)_actionCamera != null ? 1 : 0);
                if (DualViewportCheck.IsChecked == true)
                {
                    ComparisonCameraModeCombo.SelectedIndex = 0;
                }
                CameraGizmoCheck.IsEnabled = (object)_actionCamera != null;
                CameraTrajectoryCheck.IsEnabled = (object)_actionCamera != null;
                CameraDataButton.IsEnabled = (object)_actionCamera != null;
                CameraCurveButton.IsEnabled = (object)_actionCamera != null;
                if ((object)_actionCamera == null)
                {
                    CameraGizmoCheck.IsChecked = false;
                }
                if ((object)_actionCamera == null)
                {
                    CameraTrajectoryCheck.IsChecked = false;
                }
                if ((object)_actionCamera == null)
                {
                    HideCameraDataWindow();
                }
                UpdateActionCameraInspector(asset, cameraError);
                DetailBody.Text = $"动画版本：{a.Version}\n文件大小：{asset.SizeText}\n时长：{a.Duration:0.###} 秒\n估算帧数（60 FPS）：{Math.Round(a.Duration * 60f):N0}\n骨骼轨道：{a.TrackCount}\n最大骨骼 ID：{a.MaxBoneId}\n动画类型：0x{a.AnimationType:X4}";
                int num = (((object)_clip != null && (object)_model != null) ? _clip.Tracks.Count((AnimationTrack t) => (object)FindAnimationBone(_model, _clip, t.GlobalBoneId) != null) : 0);
                IReadOnlyList<string> gameActionOwners = asset.GameActionOwners;
                string value = ((gameActionOwners != null && gameActionOwners.Count > 0) ? (" · 游戏动作表 " + string.Join(" + ", gameActionOwners.Select(OfficialNameAnnotations.Format))) : " · 动作表归属未恢复");
                PreviewSummary.Text = (((object)_model == null) ? $"{asset.AnnotatedDisplayName} · {a.Duration:0.###} 秒 · {a.TrackCount} 条轨道（先选择模型可叠加骨架播放）" : ((clipError != null) ? (asset.AnnotatedDisplayName + " · 动画解码失败：" + clipError) : $"{asset.AnnotatedDisplayName} · {a.Duration:0.###} 秒 · 已映射 {num}/{_clip?.Tracks.Count ?? 0} 条骨架轨道{value}"));
                if (clipError != null)
                {
                    StatusText.Text = "动画无法播放：" + clipError;
                }
                else if (cameraError != null)
                {
                    StatusText.Text = "人物动画可播放，但动画镜头读取失败：" + cameraError;
                }
                _playbackTime = 0.0;
                AnimationSlider.Maximum = Math.Max(0.001, a.Duration);
                AnimationSlider.Value = 0.0;
                Slider animationSlider = AnimationSlider;
                G1AClip? clip = _clip;
                animationSlider.IsEnabled = (object)clip != null && clip.Tracks.Count > 0;
                PlayButton.IsEnabled = AnimationSlider.IsEnabled;
                PlaybackOverlay.Visibility = ((!AnimationSlider.IsEnabled) ? Visibility.Collapsed : Visibility.Visible);
                TimelineText.Text = $"0.00 / {a.Duration:0.00} s";
                if ((object)_model != null)
                {
                    G1AClip? clip2 = _clip;
                    if ((object)clip2 != null && clip2.Tracks.Count > 0)
                    {
                        UpdateCameraTrajectory();
                        RenderAnimatedSkeleton(0f);
                        StatusText.Text = ((num > 0) ? $"已加载动画：{asset.AnnotatedDisplayName} · 匹配 {num}/{_clip.Tracks.Count} 条骨骼轨道" : $"动画与当前角色没有共同骨骼轨道；人物模型保持不变（共 {_clip.Tracks.Count} 条轨道）");
                        StartAnimationPlaybackFromBeginning();
                        goto IL_10a4;
                    }
                }
                RenderTimeline(a);
            }
            else if (asset.Kind == AssetKind.Package)
            {
                string cameraError = ReadMagic(asset.FullPath);
                IReadOnlyList<FDataRecord> readOnlyList = ((!(asset.Extension == "FDATA")) ? Array.Empty<FDataRecord>() : (await Task.Run(() => FDataUnpacker.ReadIndex(asset.FullPath))));
                IReadOnlyList<FDataRecord> readOnlyList2 = readOnlyList;
                DetailBody.Text = $"容器标记：{cameraError}\n文件大小：{asset.SizeText}\nIDRK 资源记录：{readOnlyList2.Count:N0}\n解压后估算：{(double)readOnlyList2.Sum((FDataRecord x) => x.UncompressedSize) / 1048576.0:0.00} MB\n\n可使用“解包选中 FData”按模型、动画、骨架、贴图等类型输出，并生成带资源 ID、源偏移和 SHA-256 的清单。";
                PreviewSummary.Text = asset.Name + " · 原始资源容器";
                RenderPackageCard(cameraError);
            }
            else
            {
                DetailBody.Text = $"类型：{asset.Kind}\n格式：{asset.Extension}\n文件大小：{asset.SizeText}\n文件头：{ReadMagic(asset.FullPath)}";
                PreviewSummary.Text = $"{asset.Name} · {asset.Kind}";
                RenderPackageCard(asset.Kind.ToString());
            }
            goto IL_10a4;
        IL_10a4:
            StatusText.Text = completionStatus ?? ("已加载 " + asset.AnnotatedDisplayName);
        }
        catch (Exception ex3)
        {
            _previewingPath = null;
            DetailBody.Text = "解析失败：" + ex3.Message;
            PreviewSummary.Text = "无法预览此资源";
            RenderPackageCard("解析失败");
            StatusText.Text = ex3.Message;
        }
        finally
        {
            UpdateExportButtons();
            _previewGate.Release();
        }
    }

    private void UpdateExportButtons()
    {
        if (_exporting)
        {
            return;
        }
        G1MInfo model = _model;
        int num;
        if ((object)model != null)
        {
            IReadOnlyList<MeshPart> parts = model.Parts;
            if (parts != null)
            {
                num = ((parts.Count > 0) ? 1 : 0);
                goto IL_0029;
            }
        }
        num = 0;
        goto IL_0029;
    IL_0065:
        object exportAnimationFbxButton;
        int isEnabled;
        ((UIElement)exportAnimationFbxButton).IsEnabled = (byte)isEnabled != 0;
        Button exportCameraFbxButton = ExportCameraFbxButton;
        bool flag;
        int isEnabled2;
        if (flag)
        {
            G1AClip clip = _clip;
            if ((object)clip != null)
            {
                IReadOnlyList<AnimationTrack> tracks = clip.Tracks;
                if (tracks != null && tracks.Count > 0)
                {
                    isEnabled2 = (((object)_actionCamera != null) ? 1 : 0);
                    goto IL_00a2;
                }
            }
        }
        isEnabled2 = 0;
        goto IL_00a2;
    IL_00a2:
        exportCameraFbxButton.IsEnabled = (byte)isEnabled2 != 0;
        return;
    IL_0029:
        flag = (byte)num != 0;
        ExportModelFbxButton.IsEnabled = flag;
        exportAnimationFbxButton = ExportAnimationFbxButton;
        if (flag)
        {
            G1AClip clip = _clip;
            if ((object)clip != null)
            {
                IReadOnlyList<AnimationTrack> tracks = clip.Tracks;
                if (tracks != null)
                {
                    isEnabled = ((tracks.Count > 0) ? 1 : 0);
                    goto IL_0065;
                }
            }
            isEnabled = 0;
        }
        else
        {
            isEnabled = 0;
        }
        goto IL_0065;
    }

    private void RefreshBlenderStatus()
    {
        _detectedBlenderPath = BlenderFbxExporter.FindBlender(_root);
        bool flag = !string.IsNullOrWhiteSpace(_detectedBlenderPath);
        BlenderStatusDot.Fill = (flag ? new SolidColorBrush(Color.FromRgb(88, 198, 139)) : new SolidColorBrush(Color.FromRgb(229, 173, 67)));
        BlenderStatusText.Text = (flag ? "已检测到，可导出 FBX" : "未检测到，FBX 导出不可用");
        BlenderStatusText.Foreground = (flag ? new SolidColorBrush(Color.FromRgb(128, 219, 169)) : new SolidColorBrush(Color.FromRgb(242, 190, 89)));
        BlenderPathText.Text = (flag ? _detectedBlenderPath : "可通过 Steam 或 Blender 官网免费安装");
        BlenderPathText.ToolTip = (flag ? _detectedBlenderPath : null);
        BlenderLocationButton.Content = (flag ? "打开位置" : "选择程序");
        BlenderSteamButton.Content = (flag ? "Steam 页面" : "Steam 安装");
    }

    private bool EnsureBlenderReady()
    {
        RefreshBlenderStatus();
        if (_detectedBlenderPath != null)
        {
            return true;
        }
        MessageBox.Show(this, "FBX 导出需要 Blender。请使用右下角的“Steam 安装”“官网下载”，或手动选择 blender.exe。", "需要 Blender", MessageBoxButton.OK, MessageBoxImage.Asterisk);
        return false;
    }

    private void BlenderLocation_Click(object sender, RoutedEventArgs e)
    {
        RefreshBlenderStatus();
        if (_detectedBlenderPath != null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _detectedBlenderPath + "\"")
            {
                UseShellExecute = true
            });
            return;
        }
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Title = "选择 Blender 程序",
            Filter = "Blender 程序|blender.exe|可执行程序|*.exe"
        };
        if (openFileDialog.ShowDialog(this) == true)
        {
            UserPreferences.SetBlenderPath(openFileDialog.FileName);
            RefreshBlenderStatus();
        }
    }

    private static void OpenExternalLink(string url)
    {
        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    private void OpenBlenderSteam_Click(object sender, RoutedEventArgs e)
    {
        RefreshBlenderStatus();
        string item = ((_detectedBlenderPath == null) ? "steam://install/365670" : "steam://store/365670");
        try
        {
            Process.Start(new ProcessStartInfo(FindSteamExecutable() ?? throw new FileNotFoundException("未找到 Steam 程序"))
            {
                UseShellExecute = true,
                ArgumentList = { item }
            });
            StatusText.Text = ((_detectedBlenderPath == null) ? "已在 Steam 中打开 Blender 安装页面" : "已在 Steam 中打开 Blender 页面（当前已安装）");
        }
        catch
        {
            OpenExternalLink("https://store.steampowered.com/app/365670/Blender/");
            StatusText.Text = "Steam 未能启动，已改为打开 Blender 网页";
        }
    }

    private static string? FindSteamExecutable()
    {
        string text = Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamExe", null)?.ToString();
        return new string[4] { text, "C:\\Program Files (x86)\\Steam\\steam.exe", "C:\\Program Files\\Steam\\steam.exe", "E:\\Steam\\steam.exe" }.FirstOrDefault((string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private void OpenBlenderWebsite_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink("https://www.blender.org/download/");
    }

    private void BeginExportProgress()
    {
        _exporting = true;
        UpdateProgressPanelPlacement();
        ExportProgressPanel.Visibility = Visibility.Visible;
        ExportProgressBar.IsIndeterminate = true;
        ExportProgressBar.Value = 0.0;
        ExportProgressBar.Opacity = 1.0;
        ExportProgressText.Text = "正在准备导出…";
        ExportProgressPercentText.Text = "0%";
        ExportProgressDetailText.Text = "正在启动导出流程，请勿关闭工具";
        ExportModelFbxButton.IsEnabled = false;
        ExportAnimationFbxButton.IsEnabled = false;
        ExportCameraFbxButton.IsEnabled = false;
    }

    private void ReportExportProgress((string Stage, int Percent) value)
    {
        ExportProgressText.Text = value.Stage;
        ProgressBar exportProgressBar = ExportProgressBar;
        int item = value.Percent;
        exportProgressBar.IsIndeterminate = item > 30 && item < 100;
        int num = Math.Clamp(value.Percent, 0, 100);
        ExportProgressBar.Value = num;
        ExportProgressPercentText.Text = $"{num}%";
        ExportProgressDetailText.Text = ((num < 100) ? "正在处理，请勿关闭工具" : "文件已经写入导出目录");
    }

    private void EndExportProgress()
    {
        ExportProgressBar.IsIndeterminate = false;
        ExportProgressBar.Value = 100.0;
        ExportProgressText.Text = "导出完成";
        ExportProgressPercentText.Text = "100%";
        ExportProgressDetailText.Text = "文件已经写入导出目录";
        _exporting = false;
        UpdateExportButtons();
        SetProgressIdle();
    }

    private void SetProgressIdle(string? detail = null)
    {
        ExportProgressBar.IsIndeterminate = false;
        ExportProgressBar.Value = 0.0;
        ExportProgressBar.Opacity = 0.32;
        ExportProgressText.Text = "任务进度";
        ExportProgressPercentText.Text = "";
        ExportProgressDetailText.Text = (string.IsNullOrWhiteSpace(detail) ? "准备就绪 · 初始化和 FBX 导出进度会显示在这里" : detail);
        ExportProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateProgressPanelPlacement()
    {
        Border playbackOverlay = PlaybackOverlay;
        double bottom = ((playbackOverlay != null && playbackOverlay.Visibility == Visibility.Visible) ? 66.0 : 14.0);
        ExportProgressPanel.Margin = new Thickness(108.0, 0.0, 12.0, bottom);
    }

    private static string BuildRelationText(AssetEntry asset)
    {
        object obj;
        if (asset.Kind != AssetKind.Animation)
        {
            obj = "";
        }
        else
        {
            DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(28, 4);
            defaultInterpolatedStringHandler.AppendLiteral("\n游戏动作分类：");
            defaultInterpolatedStringHandler.AppendFormatted(asset.GameActionCategory ?? "未恢复动作表归属");
            defaultInterpolatedStringHandler.AppendLiteral("\n动作表所有者：");
            IReadOnlyList<string> gameActionOwners = asset.GameActionOwners;
            defaultInterpolatedStringHandler.AppendFormatted((gameActionOwners != null && gameActionOwners.Count > 0) ? string.Join(" + ", gameActionOwners) : "未恢复");
            defaultInterpolatedStringHandler.AppendLiteral("\n分类证据：");
            defaultInterpolatedStringHandler.AppendFormatted(asset.ClassificationEvidence ?? "无可证明的 KIDS 正式对象引用链");
            defaultInterpolatedStringHandler.AppendLiteral("\n动画镜头：");
            defaultInterpolatedStringHandler.AppendFormatted(asset.ActionCameraResourceId ?? (asset.IsActionCamera ? "本资源" : "无正式镜头引用"));
            obj = defaultInterpolatedStringHandler.ToStringAndClear() + ((asset.ActionCameraEvidence == null) ? "" : ("\n镜头证据：" + asset.ActionCameraEvidence));
        }
        string value = (string)obj;
        return $"正式名称：{asset.OfficialName ?? "未恢复"}\n中文备注：{asset.NameAnnotation ?? "暂无"}\n分类：{asset.Kind}\n资源域：{asset.Domain}\n底层资源 ID：{asset.HashId}\n格式：{asset.Extension}{value}";
    }

    private void UpdateActionCameraInspector(AssetEntry asset, string? cameraError = null)
    {
        if (asset.Kind != AssetKind.Animation)
        {
            ActionCameraTitle.Text = "未选择骨骼动画";
            ActionCameraStatus.Text = "未检测";
            ActionCameraStatus.Foreground = (Brush)FindResource("MutedBrush");
            ActionCameraDetail.Text = ((_modelAsset == null) ? "先选择一个人物模型，再选择任意角色的骨骼动画。模型只会在你选择另一个模型时更换。" : ("当前模型：" + _modelAsset.AnnotatedDisplayName + "\n继续选择任意角色动作即可套用到这个模型。"));
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
        IReadOnlyList<string> gameActionOwners = asset.GameActionOwners;
        string text = ((gameActionOwners != null && gameActionOwners.Count > 0) ? string.Join(" + ", asset.GameActionOwners.Select(OfficialNameAnnotations.Format)) : "未恢复动作表归属");
        (bool, string) tuple = BuildAuthoredRuntimeCameraSummary(asset);
        if (string.IsNullOrWhiteSpace(asset.ActionCameraResourceId))
        {
            ActionCameraTitle.Text = asset.TreeDisplayName;
            ActionCameraStatus.Text = (tuple.Item1 ? "存在特效事件" : "无独立动画镜头");
            ActionCameraStatus.Foreground = (tuple.Item1 ? new SolidColorBrush(Color.FromRgb(242, 184, 72)) : ((Brush)FindResource("MutedBrush")));
            ActionCameraDetail.Text = "动作表：" + text + "\n游戏动作对象没有引用独立 CameraAnimationData/G1A。\n该动作只能使用自由镜头预览；工具不会生成替代镜头。\n\n" + tuple.Item2;
            return;
        }
        AssetEntry assetEntry = _all.FirstOrDefault((AssetEntry x) => x.Kind == AssetKind.Animation && x.IsActionCamera && NormalizeResourceId(x.ResourceId ?? x.HashId).Equals(NormalizeResourceId(asset.ActionCameraResourceId), StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<string> gameActionOwners2 = asset.GameActionOwners;
        string text2 = ((gameActionOwners2 != null && gameActionOwners2.Count > 0) ? (string.Join(" + ", gameActionOwners2.Select(OfficialNameAnnotations.Format)) + " · 动画镜头") : (assetEntry?.TreeDisplayName ?? "动画镜头"));
        ActionCameraTitle.Text = text2;
        ActionCameraStatus.Text = ((cameraError == null) ? "已关联" : "读取失败");
        ActionCameraStatus.Foreground = ((cameraError == null) ? new SolidColorBrush(Color.FromRgb(116, 214, 143)) : new SolidColorBrush(Color.FromRgb(241, 132, 116)));
        ActionCameraDetail.Text = ((cameraError == null) ? $"当前动作：{asset.TreeDisplayName}\n人物动画：{_clip?.Info.Duration ?? 0f:0.00} 秒 · 动画镜头：{_actionCamera?.Clip.Info.Duration ?? 0f:0.00} 秒\n时间轴以人物动画为准；切换“动画镜头”只改变播放视角，不改变动画时间轴。\n\n{tuple.Item2}" : ("当前动作已有动画镜头，但镜头读取失败：" + cameraError + "\n\n" + tuple.Item2));
        if (cameraError == null && (object)_actionCamera != null)
        {
            TextBlock actionCameraDetail = ActionCameraDetail;
            actionCameraDetail.Text = actionCameraDetail.Text + "\n\n" + BuildCameraLearningSummary(_actionCamera);
        }
    }

    private (bool HasRuntimeCameraCandidate, string Text) BuildAuthoredRuntimeCameraSummary(AssetEntry asset)
    {
        string key = NormalizeResourceId(asset.ResourceId ?? asset.HashId);
        if (!_authoredCameraEvents.TryGetValue(key, out ConfiguredCameraCatalog.AuthoredActionConfiguration value))
        {
            return (HasRuntimeCameraCandidate: false, Text: "动作表事件：未在 KIDS singleton 动作表中找到该动画的事件链。");
        }
        ConfiguredCameraCatalog.AuthoredActionEvent[] array = (from x in value.Events
                                                               orderby (x.PacketFrames.Count <= 0) ? x.StartFrame : x.PacketFrames.Min(), x.ObjectHash
                                                               select x).ToArray();
        ConfiguredCameraCatalog.AuthoredActionEvent[] array2 = array.Where((ConfiguredCameraCatalog.AuthoredActionEvent x) => x.Kind == ConfiguredCameraCatalog.ActionEventKind.Effect).ToArray();
        ConfiguredCameraCatalog.AuthoredActionEvent[] array3 = array2.Where(HasEffectStartOrEndPacket).ToArray();
        ConfiguredCameraCatalog.AuthoredActionEvent[] array4 = array.Where((ConfiguredCameraCatalog.AuthoredActionEvent x) => x.Kind == ConfiguredCameraCatalog.ActionEventKind.Camera).ToArray();
        IReadOnlyDictionary<uint, CameraEventEvidenceCatalog.EffectResourceEvidence> readOnlyDictionary;
        if (array3.Length != 0)
        {
            readOnlyDictionary = CameraEventEvidenceCatalog.Build(_root, array3.Select((ConfiguredCameraCatalog.AuthoredActionEvent x) => x.ResourceHash));
        }
        else
        {
            IReadOnlyDictionary<uint, CameraEventEvidenceCatalog.EffectResourceEvidence> readOnlyDictionary2 = new Dictionary<uint, CameraEventEvidenceCatalog.EffectResourceEvidence>();
            readOnlyDictionary = readOnlyDictionary2;
        }
        IReadOnlyDictionary<uint, CameraEventEvidenceCatalog.EffectResourceEvidence> readOnlyDictionary3 = readOnlyDictionary;
        List<string> list = new List<string>
        {
            $"动作表事件：SequentialPlayer {value.ActionHash:x8}，动画引用 {value.AnimationIds.Count} 个，事件 {array.Length} 个。",
            "来源：" + value.Evidence
        };
        IEnumerable<string> values = from x in array
                                     group x by x.Kind into x
                                     orderby x.Key.ToString()
                                     select $"{ActionEventKindLabel(x.Key)} {x.Count()}";
        list.Add("事件统计：" + string.Join(" · ", values));
        if (array4.Length != 0)
        {
            list.Add($"独立 Camera 事件：{array4.Length} 个。");
            foreach (ConfiguredCameraCatalog.AuthoredActionEvent item in array4.Take(6))
            {
                string value2 = FormatFrames(item);
                list.Add($"  frame[{value2}] camera={item.ResourceHash:x8}");
            }
        }
        if (array3.Length == 0)
        {
            list.Add("特效 Start/End 事件：未发现。");
        }
        else
        {
            list.Add($"特效 Start/End 事件：{array3.Length} 个。");
            foreach (ConfiguredCameraCatalog.AuthoredActionEvent item2 in array3.Take(10))
            {
                string value3 = FormatFrames(item2);
                string value4 = string.Join(",", item2.PacketValues.Select(FormatPacketName).Take(6));
                list.Add($"  frame[{value3}] effect={item2.ResourceHash:x8} packet={value4}");
            }
            if (array3.Length > 10)
            {
                list.Add($"  …另有 {array3.Length - 10} 个事件");
            }
            list.Add("说明：cc6674fe=Start，002f1a65=End。它们是本体特效事件开关，不是 FOV/Distance/震屏参数。");
            if (readOnlyDictionary3.Count > 0)
            {
                list.Add("特效资源链证据：");
                foreach (CameraEventEvidenceCatalog.EffectResourceEvidence item3 in readOnlyDictionary3.Values.Take(5))
                {
                    list.Add("  " + item3.Summary);
                }
                if (readOnlyDictionary3.Count > 5)
                {
                    list.Add($"  …另有 {readOnlyDictionary3.Count - 5} 个 EffectResource");
                }
            }
        }
        if (array2.Length != 0)
        {
            list.Add((array4.Length != 0) ? "结论：该动作存在直接 Camera 事件，同时也存在特效/音效时序。" : "结论：该动作没有直接 Camera 事件；目前只能证明存在特效/音效时序，FOV/距离/震屏还未从本体参数解码。");
        }
        return (HasRuntimeCameraCandidate: array3.Length != 0 || array4.Length != 0, Text: string.Join("\n", list));
    }

    private static bool HasEffectStartOrEndPacket(ConfiguredCameraCatalog.AuthoredActionEvent actionEvent)
    {
        if (actionEvent.Kind == ConfiguredCameraCatalog.ActionEventKind.Effect)
        {
            return actionEvent.PacketValues.Any((uint x) => (x == 3086949 || x == 3429266686u) ? true : false);
        }
        return false;
    }

    private static string FormatFrames(ConfiguredCameraCatalog.AuthoredActionEvent actionEvent)
    {
        if (actionEvent.PacketFrames.Count != 0)
        {
            return string.Join(",", actionEvent.PacketFrames.Select((float x) => x.ToString("0.##", CultureInfo.InvariantCulture)).Take(6));
        }
        return actionEvent.StartFrame.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatPacketName(uint packet)
    {
        return packet switch
        {
            3429266686u => "Start",
            3086949u => "End",
            _ => packet.ToString("x8", CultureInfo.InvariantCulture),
        };
    }

    private static string ActionEventKindLabel(ConfiguredCameraCatalog.ActionEventKind kind)
    {
        return kind switch
        {
            ConfiguredCameraCatalog.ActionEventKind.Effect => "特效",
            ConfiguredCameraCatalog.ActionEventKind.Sound => "音效",
            ConfiguredCameraCatalog.ActionEventKind.Model => "模型",
            ConfiguredCameraCatalog.ActionEventKind.Camera => "相机",
            ConfiguredCameraCatalog.ActionEventKind.MotionSoundInvalidator => "音效失效",
            _ => "其他",
        };
    }

    private static string BuildCameraLearningSummary(ActionCameraClip camera)
    {
        float duration = camera.Clip.Info.Duration;
        int num = Math.Max(1, (int)Math.Ceiling(duration * 60f));
        float num2 = float.PositiveInfinity;
        float num3 = 0f;
        float num4 = float.PositiveInfinity;
        float num5 = 0f;
        float num6 = 0f;
        float num7 = 0f;
        float num8 = 0f;
        float num9 = 0f;
        Vector3? vector = null;
        Vector3? vector2 = null;
        for (int i = 0; i <= num; i++)
        {
            float num10 = Math.Min(duration, (float)i / 60f);
            (Vector3, Vector3, Vector3) tuple = G1Parsers.SampleCameraTrack(camera.TransformTrack, num10);
            ActionCameraFrame actionCameraFrame = ActionCameraCatalog.Sample(camera, num10);
            float val = Vector3.Distance(tuple.Item1, tuple.Item2);
            num2 = Math.Min(num2, val);
            num3 = Math.Max(num3, val);
            num4 = Math.Min(num4, actionCameraFrame.FieldOfView);
            num5 = Math.Max(num5, actionCameraFrame.FieldOfView);
            if (vector.HasValue)
            {
                Vector3 valueOrDefault = vector.GetValueOrDefault();
                if (vector2.HasValue)
                {
                    Vector3 valueOrDefault2 = vector2.GetValueOrDefault();
                    float num11 = Vector3.Distance(valueOrDefault, tuple.Item1);
                    float num12 = Vector3.Distance(valueOrDefault2, tuple.Item2);
                    num6 += num11;
                    num7 += num12;
                    num8 = Math.Max(num8, num11 * 60f);
                    num9 = Math.Max(num9, num12 * 60f);
                }
            }
            vector = tuple.Item1;
            vector2 = tuple.Item2;
        }
        return $"镜头设计参数：\n景别距离 {num2:0.###} – {num3:0.###}\n垂直 FOV {num4:0.###}° – {num5:0.###}°\n镜头移动 {num6:0.###} · 目标移动 {num7:0.###}\n最大速度：镜头 {num8:0.###}/秒 · 目标 {num9:0.###}/秒\n导出时会把当前工具视口比例写入 UE 摄像机 Filmback，保证水平 FOV 与工具预览一致。";
    }

    private static string NormalizeResourceId(string value)
    {
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        return value.Substring(2);
    }

    private async Task LoadModelAssetAsync(AssetEntry asset, bool updateInspector)
    {
        G1MInfo parsed = await Task.Run(() => G1Parsers.ReadModel(asset.FullPath));
        IReadOnlyList<string> texturePaths = FindCharacterTextures(asset.FullPath);
        int[] usedTextureIds = (from x in parsed.Parts
                                select x.DiffuseTextureIndex into x
                                where x >= 0
                                select x).Distinct().ToArray();
        Dictionary<int, G1TextureImage> obj = await Task.Run(() => DecodeCharacterTextures(texturePaths, usedTextureIds));
        _model = parsed;
        _modelAsset = asset;
        _characterTextures.Clear();
        foreach (KeyValuePair<int, G1TextureImage> item in obj)
        {
            _characterTextures[item.Key] = CreateTextureBrush(item.Value);
        }
        G1MInfo model = _model;
        if (updateInspector)
        {
            DetailBody.Text = $"G1M 版本：{model.Version}\n平台标记：{model.Platform}\n文件大小：{asset.SizeText}\n容器分块：{model.ChunkCount}\n网格组：{model.MeshCount}\n子网格：{model.SubmeshCount}\n材质：{model.MaterialCount}\n顶点缓冲：{model.VertexBufferCount}\n索引缓冲：{model.IndexBufferCount}\n包围盒：\n  ({model.MinX:0.###}, {model.MinY:0.###}, {model.MinZ:0.###})\n  ({model.MaxX:0.###}, {model.MaxY:0.###}, {model.MaxZ:0.###})" + ((model.Warning == null) ? "" : ("\n\n提示：" + model.Warning));
            PreviewSummary.Text = $"{asset.AnnotatedDisplayName} · {model.Parts.Count} 个可预览分件 · {model.SubmeshCount} 子网格";
        }
        RenderModel();
        _cameraTarget = _sceneCenter;
        ResetView();
        RefreshBoneTree();
        if ((object)_weaponModel != null)
        {
            PopulateAccessoryBoneChoices();
        }
        UpdateActionCameraInspector(asset);
    }

    private void RenderModel()
    {
        _scene.Children.Clear();
        _comparisonScene.Children.Clear();
        _backgroundScene.Children.Clear();
        _comparisonBackgroundScene.Children.Clear();
        _helperScene.Children.Clear();
        _comparisonHelperScene.Children.Clear();
        _meshGeometries.Clear();
        _meshPositionBackBuffers.Clear();
        _meshModels.Clear();
        _comparisonMeshModels.Clear();
        _texturedMaterials.Clear();
        _solidMaterials.Clear();
        ViewportOverlay.Children.Clear();
        if ((object)_model == null)
        {
            return;
        }
        Point3D point3D = ToWorld(_model.MaxX, _model.MinY, _model.MinZ);
        Point3D point3D2 = ToWorld(_model.MinX, _model.MaxY, _model.MaxZ);
        _sceneCenter = new Point3D((point3D.X + point3D2.X) * 0.5, (point3D.Y + point3D2.Y) * 0.5, (point3D.Z + point3D2.Z) * 0.5);
        _sceneRadius = Math.Max(0.01, (double)Math.Max(_model.MaxX - _model.MinX, Math.Max(_model.MaxY - _model.MinY, _model.MaxZ - _model.MinZ)) * 0.5);
        _scene.Children.Add(new AmbientLight(Color.FromRgb(82, 88, 102)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(244, 236, 220), new Vector3D(-0.45, -0.7, -0.5)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(105, 128, 170), new Vector3D(0.6, -0.15, 0.7)));
        _comparisonScene.Children.Add(new AmbientLight(Color.FromRgb(82, 88, 102)));
        _comparisonScene.Children.Add(new DirectionalLight(Color.FromRgb(244, 236, 220), new Vector3D(-0.45, -0.7, -0.5)));
        _comparisonScene.Children.Add(new DirectionalLight(Color.FromRgb(105, 128, 170), new Vector3D(0.6, -0.15, 0.7)));
        AddWorldBackground(_backgroundScene);
        AddWorldBackground(_comparisonBackgroundScene);
        _scene.Children.Add(_backgroundScene);
        _comparisonScene.Children.Add(_comparisonBackgroundScene);
        // Camera helpers share the model depth buffer: occlusion follows world position.
        _helperScene.Children.Add(_cameraGizmo);
        _helperScene.Children.Add(_cameraTrajectory);
        _helperScene.Children.Add(_cameraTrajectoryCursor);
        _comparisonHelperScene.Children.Add(_comparisonCameraGizmo);
        _comparisonHelperScene.Children.Add(_comparisonCameraTrajectory);
        _comparisonHelperScene.Children.Add(_comparisonCameraTrajectoryCursor);
        _scene.Children.Add(_helperScene);
        _comparisonScene.Children.Add(_comparisonHelperScene);
        _scene.Children.Add(_weaponGroup);
        _comparisonScene.Children.Add(_comparisonWeaponGroup);
        _scene.Children.Add(_secondaryWeaponGroup);
        _comparisonScene.Children.Add(_comparisonSecondaryWeaponGroup);
        if (_showGrid)
        {
            AddGrid3D(_helperScene);
            AddGrid3D(_comparisonHelperScene);
        }
        if (_showAxes)
        {
            AddAxes3D(_helperScene);
            AddAxes3D(_comparisonHelperScene);
        }
        Color[] array = new Color[5]
        {
            Color.FromRgb(190, 157, 104),
            Color.FromRgb(103, 133, 161),
            Color.FromRgb(133, 119, 151),
            Color.FromRgb(112, 145, 127),
            Color.FromRgb(161, 112, 105)
        };
        foreach (MeshPart part in _model.Parts)
        {
            MeshGeometry3D meshGeometry3D = new MeshGeometry3D();
            for (int i = 0; i + 2 < part.Positions.Length; i += 3)
            {
                meshGeometry3D.Positions.Add(ToWorld(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
            }
            if (part.Normals?.Length == part.Positions.Length)
            {
                for (int j = 0; j + 2 < part.Normals.Length; j += 3)
                {
                    meshGeometry3D.Normals.Add(ToWorldNormal(part.Normals[j], part.Normals[j + 1], part.Normals[j + 2]));
                }
            }
            if (part.TextureCoordinates?.Length == meshGeometry3D.Positions.Count * 2)
            {
                for (int k = 0; k + 1 < part.TextureCoordinates.Length; k += 2)
                {
                    meshGeometry3D.TextureCoordinates.Add(new Point(part.TextureCoordinates[k], part.TextureCoordinates[k + 1]));
                }
            }
            AppendTriangles(meshGeometry3D.TriangleIndices, part.Indices, part.TriangleStrip, meshGeometry3D.Positions.Count);
            Color color = array[Math.Abs(part.MaterialIndex) % array.Length];
            MaterialGroup materialGroup = new MaterialGroup();
            ImageBrush value;
            Brush brush = ((part.TextureCoordinates != null && _characterTextures.TryGetValue(part.DiffuseTextureIndex, out value)) ? ((Brush)value) : ((Brush)new SolidColorBrush(color)));
            materialGroup.Children.Add(new DiffuseMaterial(brush));
            materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(115, byte.MaxValue, 244, 220)), 34.0));
            MaterialGroup materialGroup2 = new MaterialGroup();
            materialGroup2.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(168, 174, 184))));
            materialGroup2.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(90, byte.MaxValue, byte.MaxValue, byte.MaxValue)), 24.0));
            GeometryModel3D geometryModel3D = new GeometryModel3D(meshGeometry3D, materialGroup)
            {
                BackMaterial = materialGroup
            };
            GeometryModel3D geometryModel3D2 = new GeometryModel3D(meshGeometry3D, materialGroup)
            {
                BackMaterial = materialGroup
            };
            _meshGeometries.Add(meshGeometry3D);
            _meshPositionBackBuffers.Add(new Point3DCollection(meshGeometry3D.Positions));
            _meshModels.Add(geometryModel3D);
            _comparisonMeshModels.Add(geometryModel3D2);
            _texturedMaterials.Add(materialGroup);
            _solidMaterials.Add(materialGroup2);
            _scene.Children.Add(geometryModel3D);
            _comparisonScene.Children.Add(geometryModel3D2);
        }
        _wireframeEdges = BuildWireframeEdges(_meshGeometries, 20000);
        WireframeLayer.SetGeometry(_meshGeometries, _wireframeEdges);
        ComparisonWireframeLayer.SetGeometry(_meshGeometries, _wireframeEdges);
        ApplyRenderMode();
        UpdateCamera();
        UpdateComparisonCamera();
        ViewportStats.Text = $"{_model.Parts.Count} parts · {_model.SubmeshCount} submeshes · {_model.Bones.Count} bones · GPU 3D";
        ViewportHint.Text = BuildViewportModeHint(CameraModeCombo.SelectedIndex, right: false);
    }

    private static IReadOnlyList<WireframeEdge> BuildWireframeEdges(IReadOnlyList<MeshGeometry3D> meshes, int edgeBudget)
    {
        int num = meshes.Sum((MeshGeometry3D x) => x.TriangleIndices.Count / 3);
        if (num == 0)
        {
            return Array.Empty<WireframeEdge>();
        }
        int num2 = Math.Max(1, (int)Math.Ceiling((double)num * 3.0 / (double)edgeBudget));
        List<WireframeEdge> result = new List<WireframeEdge>(Math.Min(edgeBudget, num * 3));
        HashSet<(int Mesh, int A, int B)> seen = new HashSet<(int, int, int)>();
        int num3 = 0;
        int meshIndex;
        for (meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            Int32Collection triangleIndices = meshes[meshIndex].TriangleIndices;
            int num4 = 0;
            while (num4 + 2 < triangleIndices.Count)
            {
                if (num3 % num2 == 0)
                {
                    Add(triangleIndices[num4], triangleIndices[num4 + 1]);
                    Add(triangleIndices[num4 + 1], triangleIndices[num4 + 2]);
                    Add(triangleIndices[num4 + 2], triangleIndices[num4]);
                    if (result.Count >= edgeBudget)
                    {
                        return result;
                    }
                }
                num4 += 3;
                num3++;
            }
        }
        return result;
        void Add(int a, int b)
        {
            if (a > b)
            {
                int num5 = b;
                b = a;
                a = num5;
            }
            if (seen.Add((meshIndex, a, b)))
            {
                result.Add(new WireframeEdge(meshIndex, a, b));
            }
        }
    }

    private void ApplyRenderMode()
    {
        for (int i = 0; i < _meshModels.Count; i++)
        {
            Material material = _renderMode switch
            {
                0 => _texturedMaterials[i],
                1 => _solidMaterials[i],
                _ => null,
            };
            _meshModels[i].Material = material;
            _meshModels[i].BackMaterial = material;
            _comparisonMeshModels[i].Material = material;
            _comparisonMeshModels[i].BackMaterial = material;
        }
        WireframeLayer.Visibility = ((_renderMode != 2 || (object)_model == null) ? Visibility.Collapsed : Visibility.Visible);
        ComparisonWireframeLayer.Visibility = ((DualViewportCheck.IsChecked != true || _renderMode != 2 || (object)_model == null) ? Visibility.Collapsed : Visibility.Visible);
        if (_renderMode == 2)
        {
            WireframeLayer.RequestRender();
            ComparisonWireframeLayer.RequestRender();
        }
    }

    private static void AppendTriangles(Int32Collection target, uint[] indices, bool strip, int vertexCount)
    {
        if (!strip)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                AddTriangle(target, indices[i], indices[i + 1], indices[i + 2], vertexCount, flip: false);
            }
            return;
        }
        bool flag = false;
        for (int j = 0; j + 2 < indices.Length; j++)
        {
            uint num = indices[j];
            uint num2 = indices[j + 1];
            uint num3 = indices[j + 2];
            bool flag2 = ((num == 65535 || num == uint.MaxValue) ? true : false);
            bool flag3 = flag2;
            if (!flag3)
            {
                bool flag4 = ((num2 == 65535 || num2 == uint.MaxValue) ? true : false);
                flag3 = flag4;
            }
            bool flag5 = flag3;
            if (!flag5)
            {
                bool flag4 = ((num3 == 65535 || num3 == uint.MaxValue) ? true : false);
                flag5 = flag4;
            }
            if (flag5)
            {
                flag = false;
                continue;
            }
            AddTriangle(target, num, num2, num3, vertexCount, flag);
            flag = !flag;
        }
    }

    private static IReadOnlyList<string> FindCharacterTextures(string modelPath)
    {
        DirectoryInfo directoryInfo = Directory.GetParent(modelPath)?.Parent;
        if (directoryInfo == null)
        {
            return Array.Empty<string>();
        }
        string path = System.IO.Path.Combine(directoryInfo.FullName, "texture");
        if (!Directory.Exists(path))
        {
            return new List<string>();
        }
        return Directory.EnumerateFiles(path, "*.g1t", SearchOption.TopDirectoryOnly).OrderBy<string, string>(NaturalTextureOrder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NaturalTextureOrder(string path)
    {
        string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(path);
        if (!int.TryParse(fileNameWithoutExtension.Split('_').LastOrDefault(), out var result))
        {
            return fileNameWithoutExtension;
        }
        return result.ToString("D10");
    }

    private static Dictionary<int, G1TextureImage> DecodeCharacterTextures(IReadOnlyList<string> paths, IReadOnlyList<int> usedIds)
    {
        Dictionary<int, G1TextureImage> dictionary = new Dictionary<int, G1TextureImage>();
        foreach (int usedId in usedIds)
        {
            if ((uint)usedId < paths.Count)
            {
                try
                {
                    dictionary[usedId] = G1TextureDecoder.ReadFirst(paths[usedId]);
                }
                catch
                {
                }
            }
        }
        return dictionary;
    }

    private static ImageBrush CreateTextureBrush(G1TextureImage texture)
    {
        BitmapSource bitmapSource = BitmapSource.Create(texture.Width, texture.Height, 96.0, 96.0, PixelFormats.Bgra32, null, texture.BgraPixels, texture.Width * 4);
        bitmapSource.Freeze();
        ImageBrush imageBrush = new ImageBrush(bitmapSource);
        imageBrush.ViewportUnits = BrushMappingMode.Absolute;
        imageBrush.TileMode = TileMode.Tile;
        imageBrush.Freeze();
        return imageBrush;
    }

    private static void AddTriangle(Int32Collection target, uint a, uint b, uint c, int count, bool flip)
    {
        checked
        {
            if (a != b && b != c && a != c && a < count && b < count && c < count)
            {
                target.Add((int)(flip ? b : a));
                target.Add((int)(flip ? a : b));
                target.Add((int)c);
            }
        }
    }

    private void RenderAnimatedSkeleton(float time)
    {
        if ((object)_model == null || (object)_clip == null)
        {
            if ((object)_model != null)
            {
                RenderModel();
            }
        }
        else if (_model.Bones.Count != 0)
        {
            float num = Math.Clamp(time, 0f, _clip.Info.Duration);
            if ((object)_preparedPoseModel != _model || (object)_preparedPoseClip != _clip || _preparedPose == null)
            {
                _preparedPose = AnimationPoseEvaluator.Prepare(_model, _clip);
                _preparedPoseModel = _model;
                _preparedPoseClip = _clip;
            }
            EvaluatedAnimationPose evaluatedAnimationPose = _preparedPose.Evaluate(num);
            RenderSkinnedModel(evaluatedAnimationPose.SkinMatrices);
            if (!_animationTimer.IsEnabled || Math.Abs((double)num - _lastPlaybackUiUpdate) >= 0.08 || (double)num < _lastPlaybackUiUpdate)
            {
                _lastPlaybackUiUpdate = num;
                ViewportHint.Text = $"同步时间 {time:0.00}s · 人物 {num:0.00}/{_clip.Info.Duration:0.00}s · 黄色为动画轨道";
                TimelineText.Text = $"{num:0.00} / {_clip.Info.Duration:0.00} s";
            }
            if (CameraModeCombo.SelectedIndex == 1 && (object)_actionCamera != null)
            {
                ApplyActionCamera(time);
            }
            if (DualViewportCheck.IsChecked == true)
            {
                UpdateComparisonCamera(time);
            }
            UpdateCameraGizmo(time);
            UpdateCameraTrajectoryCursor(time);
            UpdateRealtimeCameraData(time);
            UpdateWeaponPreview(evaluatedAnimationPose.WorldMatrices);
            RecordPlaybackFrame();
        }
    }

    private void ResetPlaybackFps(bool playing)
    {
        _playbackFrames = 0;
        _playbackFpsClock.Restart();
        PlaybackFpsText.Text = (playing ? "… FPS" : "-- FPS");
        PlaybackFpsText.Foreground = new SolidColorBrush(Color.FromRgb(143, 155, 173));
    }

    private void StopAnimationPlayback()
    {
        _animationTimer.Stop();
        ResetPlaybackFps(playing: false);
        PlayButton.Content = "▶";
    }

    private void StartAnimationPlaybackFromBeginning()
    {
        if ((object)_clip != null && (object)_model != null && PlayButton.IsEnabled)
        {
            ResetCameraTrajectoryProgress();
            SeekPlayback(0.0);
            _lastTick = DateTime.UtcNow;
            ResetPlaybackFps(playing: true);
            _animationTimer.Start();
            PlayButton.Content = "⏸";
        }
    }

    private void RecordPlaybackFrame()
    {
        if (_animationTimer.IsEnabled)
        {
            _playbackFrames++;
            double totalSeconds = _playbackFpsClock.Elapsed.TotalSeconds;
            if (!(totalSeconds < 0.5))
            {
                double num = (double)_playbackFrames / totalSeconds;
                PlaybackFpsText.Text = $"{num:0.0} FPS";
                PlaybackFpsText.Foreground = new SolidColorBrush((num >= 57.0) ? Color.FromRgb(99, 214, 143) : ((num >= 48.0) ? Color.FromRgb(241, 190, 72) : Color.FromRgb(239, 106, 106)));
                _playbackFrames = 0;
                _playbackFpsClock.Restart();
            }
        }
    }

    private void UpdateRealtimeCameraData(float time)
    {
        if (_cameraDataWindow?.IsVisible != true)
        {
            CameraDataHud.Visibility = Visibility.Collapsed;
            return;
        }
        CameraDataHud.Visibility = Visibility.Collapsed;
        if ((object)_actionCamera == null)
        {
            HideCameraDataWindow();
            return;
        }
        ActionCameraFrame actionCameraFrame = ActionCameraCatalog.Sample(_actionCamera, time);
        (Vector3, Vector3, Vector3) tuple = G1Parsers.SampleCameraTrack(_actionCamera.TransformTrack, actionCameraFrame.Time);
        float num = Math.Max(0f, actionCameraFrame.Time - 1f / 60f);
        (Vector3, Vector3, Vector3) tuple2 = G1Parsers.SampleCameraTrack(_actionCamera.TransformTrack, num);
        float num2 = Math.Max(1f / 60f, actionCameraFrame.Time - num);
        float value = Vector3.Distance(tuple.Item1, tuple2.Item1) / num2;
        float value2 = Vector3.Distance(tuple.Item2, tuple2.Item2) / num2;
        float value3 = Vector3.Distance(tuple.Item1, tuple.Item2);
        float value4 = MathF.Atan2(actionCameraFrame.Forward.X, actionCameraFrame.Forward.Z) * 180f / (float)Math.PI;
        float value5 = MathF.Asin(Math.Clamp(actionCameraFrame.Forward.Y, -1f, 1f)) * 180f / (float)Math.PI;
        double value8 = CameraRollDegrees(actionCameraFrame.Forward, actionCameraFrame.Up);
        string text = CameraModeCombo.SelectedIndex == 1
            ? "动画镜头数据"
            : "自由镜头 · 动画摄像机数据";
        CameraHudMode.Text = text;
        _cameraDataWindow?.SetMode(text);
        double value6 = VerticalToHorizontalFov(actionCameraFrame.FieldOfView, CameraViewportAspect);
        double value7 = 24.0 * CameraViewportAspect;
        string text2 = $"帧 {(int)Math.Round(actionCameraFrame.Time * 60f)}  ·  {actionCameraFrame.Time:0.000}s / {_actionCamera.Clip.Info.Duration:0.000}s\n垂直 FOV {actionCameraFrame.FieldOfView:0.00}°\n水平 FOV {value6:0.00}°\n导出背板 {value7:0.00}×{24.0:0.00} mm\n距离 {value3:0.00} cm\n机位 ({tuple.Item1.X:0.00}, {tuple.Item1.Y:0.00}, {tuple.Item1.Z:0.00})\n目标 ({tuple.Item2.X:0.00}, {tuple.Item2.Y:0.00}, {tuple.Item2.Z:0.00})\nPitch {value5:0.00}°  ·  Yaw {value4:0.00}°  ·  Roll {value8:0.00}°\n速度：机位 {value:0.00} · 目标 {value2:0.00} cm/s";
        CameraHudText.Text = text2;
        _cameraDataWindow?.ClearData();
        _cameraDataWindow?.AddLine(text2, "#D6DEE9", newline: false);
    }

    private void ApplyActionCamera(float time)
    {
        ApplyActionCamera(ViewportCamera, Viewport, time);
    }

    private void ApplyActionCamera(PerspectiveCamera camera, Viewport3D viewport, float time)
    {
        if ((object)_actionCamera == null || (object)_model == null || (object)_clip == null)
        {
            return;
        }
        ActionCameraFrame actionCameraFrame = ActionCameraCatalog.Sample(_actionCamera, time);
        Point3D position = ToWorld(actionCameraFrame.Position.X, actionCameraFrame.Position.Y, actionCameraFrame.Position.Z);
        Vector3D lookDirection = ToWorldNormal(actionCameraFrame.Forward.X, actionCameraFrame.Forward.Y, actionCameraFrame.Forward.Z);
        Vector3D upDirection = ToWorldNormal(actionCameraFrame.Up.X, actionCameraFrame.Up.Y, actionCameraFrame.Up.Z);
        if (!(lookDirection.LengthSquared < 1E-08) && !(upDirection.LengthSquared < 1E-08))
        {
            camera.Position = position;
            camera.LookDirection = lookDirection;
            camera.UpDirection = upDirection;
            double aspectRatio = ((viewport.ActualHeight > 1.0) ? Math.Max(0.01, viewport.ActualWidth / viewport.ActualHeight) : 1.7777777777777777);
            camera.FieldOfView = VerticalToHorizontalFov(actionCameraFrame.FieldOfView, aspectRatio);
            camera.NearPlaneDistance = Math.Max(0.001, _sceneRadius / 1000.0);
            camera.FarPlaneDistance = Math.Max(1000.0, _sceneRadius * 20.0);
            UpdateWorldAxisIndicator();
            if (_renderMode == 2)
            {
                WireframeLayer.RequestRender();
            }
        }
    }

    private void EnsureCameraDataWindow()
    {
        if (_cameraDataWindow == null)
        {
            _cameraDataWindow = new CameraDataWindow
            {
                Owner = this
            };
            _cameraDataWindow.Closed += delegate
            {
                _cameraDataWindow = null;
            };
        }
        if (!_cameraDataWindow.IsVisible)
        {
            _cameraDataWindow.Left = base.Left + Math.Max(40.0, base.ActualWidth - _cameraDataWindow.Width - 350.0);
            _cameraDataWindow.Top = base.Top + 110.0;
            _cameraDataWindow.Show();
        }
    }

    private void HideCameraDataWindow()
    {
        CameraDataWindow cameraDataWindow = _cameraDataWindow;
        if (cameraDataWindow != null && cameraDataWindow.IsVisible)
        {
            _cameraDataWindow.Hide();
        }
    }

    private string BuildCurrentActionCameraEvidenceSummary()
    {
        string text = NormalizeResourceId(_animationAsset?.ResourceId ?? _animationAsset?.HashId ?? "");
        if (string.IsNullOrWhiteSpace(text) || !_authoredCameraEvents.TryGetValue(text, out ConfiguredCameraCatalog.AuthoredActionConfiguration value))
        {
            return "未找到该动画的 KIDS 动作链";
        }
        if (value.Events.Any((ConfiguredCameraCatalog.AuthoredActionEvent x) => x.Kind == ConfiguredCameraCatalog.ActionEventKind.Camera))
        {
            return "存在直接 Camera 事件";
        }
        int num = value.Events.Count((ConfiguredCameraCatalog.AuthoredActionEvent x) => x.Kind == ConfiguredCameraCatalog.ActionEventKind.Effect);
        if (num <= 0)
        {
            return "无直接相机字段";
        }
        return $"无直接相机字段；仅发现 {num} 个特效事件";
    }

    private void ShowCameraCurveWindow()
    {
        if ((object)_clip == null)
        {
            MessageBox.Show(this, "请先选择一个角色动画。", "镜头曲线", MessageBoxButton.OK, MessageBoxImage.Asterisk);
            return;
        }
        CameraCurveSnapshot snapshot = BuildCameraCurveSnapshot();
        if (_cameraCurveWindow == null)
        {
            _cameraCurveWindow = new CameraCurveWindow
            {
                Owner = this
            };
            _cameraCurveWindow.SeekRequested += delegate (double time)
            {
                StopAnimationPlayback();
                SeekPlayback(time);
            };
            _cameraCurveWindow.Closed += delegate
            {
                _cameraCurveWindow = null;
            };
        }
        _cameraCurveWindow.Left = base.Left + Math.Max(30.0, base.ActualWidth - _cameraCurveWindow.Width - 360.0);
        _cameraCurveWindow.Top = base.Top + 80.0;
        _cameraCurveWindow.SetSnapshot(snapshot);
        if (!_cameraCurveWindow.IsVisible)
        {
            _cameraCurveWindow.Show();
        }
        else
        {
            _cameraCurveWindow.Activate();
        }
    }

    private CameraCurveSnapshot BuildCameraCurveSnapshot()
    {
        if ((object)_clip == null)
        {
            return new CameraCurveSnapshot("镜头曲线", "未选择动画", 0.0, 0.0, Array.Empty<CameraCurvePoint>(), Array.Empty<CameraCurveEvent>(), Array.Empty<double>());
        }
        double num = Math.Max(0.001, _clip.Info.Duration);
        int num2 = Math.Max(1, (int)Math.Ceiling(num * 60.0));
        List<CameraCurvePoint> list = new List<CameraCurvePoint>(num2 + 1);
        double? previousYaw = null;
        double? previousRoll = null;
        for (int i = 0; i <= num2; i++)
        {
            double num3 = Math.Min(num, (double)i / 60.0);
            double? actionFov = null;
            double? actionDistance = null;
            double? positionX = null;
            double? positionY = null;
            double? positionZ = null;
            double? pitch = null;
            double? yaw = null;
            double? roll = null;
            if ((object)_actionCamera != null)
            {
                ActionCameraFrame actionCameraFrame = ActionCameraCatalog.Sample(_actionCamera, (float)num3);
                (Vector3, Vector3, Vector3) tuple = G1Parsers.SampleCameraTrack(_actionCamera.TransformTrack, actionCameraFrame.Time);
                actionFov = actionCameraFrame.FieldOfView;
                actionDistance = Vector3.Distance(tuple.Item1, tuple.Item2);
                positionX = tuple.Item1.X;
                positionY = tuple.Item1.Y;
                positionZ = tuple.Item1.Z;
                pitch = MathF.Asin(Math.Clamp(actionCameraFrame.Forward.Y, -1f, 1f)) * 180f / MathF.PI;
                yaw = MathF.Atan2(actionCameraFrame.Forward.X, actionCameraFrame.Forward.Z) * 180f / MathF.PI;
                roll = CameraRollDegrees(actionCameraFrame.Forward, actionCameraFrame.Up);
                yaw = UnwrapAngleDegrees(yaw.Value, previousYaw);
                roll = UnwrapAngleDegrees(roll.Value, previousRoll);
                previousYaw = yaw;
                previousRoll = roll;
            }
            list.Add(new CameraCurvePoint(num3, actionFov, actionDistance,
                positionX, positionY, positionZ, pitch, yaw, roll));
        }
        double[] keyTimes = (((object)_actionCamera == null) ? Array.Empty<double>() : (from x in ActionCameraCatalog.KeyframeTimes(_actionCamera).Select((Func<float, double>)((float x) => x)).Distinct()
                                                                                        orderby x
                                                                                        select x).ToArray());
        CameraCurveEvent[] events = BuildCameraCurveEvents().ToArray();
        string title = "镜头曲线 · " + (_animationAsset?.AnnotatedDisplayName ?? "当前动画");
        string subtitle = (object)_actionCamera == null
            ? "该动作没有关联的独立动画镜头。"
            : "镜头曲线来自关联的 Camera G1A。";
        return new CameraCurveSnapshot(title, subtitle, num, _playbackTime, list, events, keyTimes);
    }

    private static double UnwrapAngleDegrees(double angle, double? previous)
    {
        if (!previous.HasValue) return angle;
        while (angle - previous.Value > 180.0) angle -= 360.0;
        while (angle - previous.Value < -180.0) angle += 360.0;
        return angle;
    }

    private static double CameraRollDegrees(Vector3 forward, Vector3 up)
    {
        if (forward.LengthSquared() < 1e-8f || up.LengthSquared() < 1e-8f) return 0.0;
        forward = Vector3.Normalize(forward);
        up -= forward * Vector3.Dot(up, forward);
        if (up.LengthSquared() < 1e-8f) return 0.0;
        up = Vector3.Normalize(up);
        var referenceUp = Vector3.UnitY - forward * Vector3.Dot(Vector3.UnitY, forward);
        if (referenceUp.LengthSquared() < 1e-8f)
            referenceUp = Vector3.UnitZ - forward * Vector3.Dot(Vector3.UnitZ, forward);
        referenceUp = Vector3.Normalize(referenceUp);
        var sine = Vector3.Dot(Vector3.Cross(referenceUp, up), forward);
        var cosine = Math.Clamp(Vector3.Dot(referenceUp, up), -1f, 1f);
        return Math.Atan2(sine, cosine) * 180.0 / Math.PI;
    }

    private IEnumerable<CameraCurveEvent> BuildCameraCurveEvents()
    {
        string text = NormalizeResourceId(_animationAsset?.ResourceId ?? _animationAsset?.HashId ?? "");
        if (string.IsNullOrWhiteSpace(text) || !_authoredCameraEvents.TryGetValue(text, out ConfiguredCameraCatalog.AuthoredActionConfiguration value))
        {
            yield break;
        }
        foreach (ConfiguredCameraCatalog.AuthoredActionEvent item in value.Events)
        {
            IEnumerable<double> enumerable2;
            if (item.PacketFrames.Count <= 0)
            {
                IEnumerable<double> enumerable = new[] { (double)item.StartFrame / 60.0 };
                enumerable2 = enumerable;
            }
            else
            {
                enumerable2 = item.PacketFrames.Select((float x) => (double)x / 60.0);
            }
            IEnumerable<double> enumerable3 = enumerable2;
            foreach (double item2 in enumerable3)
            {
                yield return new CameraCurveEvent(item2, $"{item.Kind} {item.ObjectHash:x8}", "动作表事件");
            }
        }
    }

    private void UpdateCameraGizmo(float time)
    {
        UpdateCameraGizmoForView(_cameraGizmo, CameraModeCombo.SelectedIndex == 0, Viewport, time);
        UpdateCameraGizmoForView(_comparisonCameraGizmo, DualViewportCheck.IsChecked == true && ComparisonCameraModeCombo.SelectedIndex == 0, ComparisonViewport, time);
    }

    private void UpdateCameraGizmoForView(Model3DGroup target, bool freeCamera, Viewport3D viewport, float time)
    {
        target.Children.Clear();
        if (CameraGizmoCheck.IsChecked != true || !freeCamera || (object)_actionCamera == null || (object)_model == null)
        {
            return;
        }
        ActionCameraFrame actionCameraFrame = ActionCameraCatalog.Sample(_actionCamera, time);
        Point3D point3D = ToWorld(actionCameraFrame.Position.X, actionCameraFrame.Position.Y, actionCameraFrame.Position.Z);
        Vector3D vector3D = ToWorldNormal(actionCameraFrame.Forward.X, actionCameraFrame.Forward.Y, actionCameraFrame.Forward.Z);
        Vector3D vector = ToWorldNormal(actionCameraFrame.Up.X, actionCameraFrame.Up.Y, actionCameraFrame.Up.Z);
        if (vector3D.LengthSquared < 1E-08 || vector.LengthSquared < 1E-08)
        {
            return;
        }
        vector3D.Normalize();
        vector.Normalize();
        Vector3D vector3D2 = Vector3D.CrossProduct(vector3D, vector);
        if (!(vector3D2.LengthSquared < 1E-08))
        {
            vector3D2.Normalize();
            vector = Vector3D.CrossProduct(vector3D2, vector3D);
            vector.Normalize();
            double num = Math.Max(_sceneRadius * 0.48, 4.0);
            double num2 = Math.Tan((double)actionCameraFrame.FieldOfView * Math.PI / 360.0) * num;
            double num3 = num2 * ((viewport.ActualHeight > 1.0) ? (viewport.ActualWidth / viewport.ActualHeight) : 1.7777777777777777);
            Point3D point3D2 = point3D + vector3D * num;
            Point3D[] array = new Point3D[4]
            {
                point3D2 + vector * num2 - vector3D2 * num3,
                point3D2 + vector * num2 + vector3D2 * num3,
                point3D2 - vector * num2 + vector3D2 * num3,
                point3D2 - vector * num2 - vector3D2 * num3
            };
            Material material = HelperMaterial(_previewCameraColor);
            Material material2 = HelperMaterial(_previewTargetColor);
            double num4 = Math.Clamp(_previewLineScale, 1.4, 3);
            Point3D[] array2 = array;
            foreach (Point3D end in array2)
            {
                AddHelperLine(viewport, target, point3D, end, num4, material);
            }
            for (int j = 0; j < array.Length; j++)
            {
                AddHelperLine(viewport, target, array[j], array[(j + 1) % array.Length], num4, material);
            }
            double num5 = Math.Max(_sceneRadius * 0.035, 0.25);
            AddHelperLine(viewport, target, point3D - vector3D2 * num5, point3D + vector3D2 * num5, num4 * 1.8, material);
            AddHelperLine(viewport, target, point3D - vector * num5, point3D + vector * num5, num4 * 1.8, material);
            // Use the sampled look-at coordinates, not the display-sized frustum endpoint.
            var lookAt = actionCameraFrame.Target;
            if (float.IsFinite(lookAt.X) && float.IsFinite(lookAt.Y) && float.IsFinite(lookAt.Z))
            {
                Point3D lookAtWorld = ToWorld(lookAt.X, lookAt.Y, lookAt.Z);
                AddHelperDashedLine(viewport, target, point3D, lookAtWorld, Math.Max(1, num4 * 0.65), material2);
                AddHelperDot(viewport, target, lookAtWorld, material2);
            }
        }
    }

    private void UpdateCameraTrajectory()
    {
        _cameraTrajectory.Children.Clear();
        _comparisonCameraTrajectory.Children.Clear();
        _cameraTrajectoryCursor.Children.Clear();
        _comparisonCameraTrajectoryCursor.Children.Clear();
        _cameraTrajectoryPoints = Array.Empty<Point3D>();
        _cameraTrajectoryDrawnFrame = -1;
        if (CameraTrajectoryCheck.IsChecked == true && (object)_actionCamera != null && (object)_model != null)
        {
            float duration = _actionCamera.Clip.Info.Duration;
            int num = Math.Max(1, (int)Math.Ceiling(duration * 60f));
            _cameraTrajectoryPoints = new Point3D[num + 1];
            for (int i = 0; i <= num; i++)
            {
                float actionTime = Math.Min(duration, (float)i / 60f);
                ActionCameraFrame actionCameraFrame = ActionCameraCatalog.Sample(_actionCamera, actionTime);
                _cameraTrajectoryPoints[i] = ToWorld(actionCameraFrame.Position.X, actionCameraFrame.Position.Y, actionCameraFrame.Position.Z);
            }
            DrawCameraTrajectoryProgress((float)_playbackTime, force: true);
        }
    }

    private void UpdateCameraTrajectoryCursor(float time)
    {
        if (CameraTrajectoryCheck.IsChecked != true || _cameraTrajectoryPoints.Length == 0 || (object)_actionCamera == null || (object)_model == null)
        {
            _cameraTrajectoryCursor.Children.Clear();
            _comparisonCameraTrajectoryCursor.Children.Clear();
        }
        else
        {
            DrawCameraTrajectoryProgress(time, force: false);
        }
    }

    private void DrawCameraTrajectoryProgress(float time, bool force)
    {
        if (_cameraTrajectoryPoints.Length == 0 || (object)_actionCamera == null)
        {
            return;
        }
        float max = Math.Max(0.001f, _actionCamera.Clip.Info.Duration);
        int frame = Math.Clamp((int)MathF.Floor(Math.Clamp(time, 0f, max) * 60f + 0.0001f), 0, _cameraTrajectoryPoints.Length - 1);
        Material pathMaterial;
        Material startMaterial;
        Material material;
        double thickness;
        if (force || frame != _cameraTrajectoryDrawnFrame)
        {
            _cameraTrajectoryDrawnFrame = frame;
            _cameraTrajectory.Children.Clear();
            _comparisonCameraTrajectory.Children.Clear();
            _cameraTrajectoryCursor.Children.Clear();
            _comparisonCameraTrajectoryCursor.Children.Clear();
            pathMaterial = HelperMaterial(_previewLineColor);
            startMaterial = HelperMaterial(Color.FromRgb(76, 210, 128));
            material = HelperMaterial(_previewCameraColor);
            thickness = Math.Clamp(_previewLineScale * 0.75, 1, 3);
            if (CameraModeCombo.SelectedIndex == 0)
            {
                Draw(Viewport, _cameraTrajectory, _cameraTrajectoryCursor);
            }
            if (DualViewportCheck.IsChecked == true && ComparisonCameraModeCombo.SelectedIndex == 0)
            {
                Draw(ComparisonViewport, _comparisonCameraTrajectory, _comparisonCameraTrajectoryCursor);
            }
        }
        void Draw(Viewport3D view, Model3DGroup path, Model3DGroup cursor)
        {
            AddHelperMarker(view, path, _cameraTrajectoryPoints[0], thickness, startMaterial);
            for (int i = 1; i <= frame; i++)
            {
                AddHelperLine(view, path, _cameraTrajectoryPoints[i - 1], _cameraTrajectoryPoints[i], thickness, pathMaterial);
            }
            AddHelperMarker(view, cursor, _cameraTrajectoryPoints[frame], thickness, material);
        }
    }

    private bool HasFreeCameraObserver()
    {
        if (CameraModeCombo.SelectedIndex != 0)
        {
            if (DualViewportCheck.IsChecked == true)
            {
                return ComparisonCameraModeCombo.SelectedIndex == 0;
            }
            return false;
        }
        return true;
    }

    private static void AddCameraTrajectoryMarker(Model3DGroup target, Point3D position, double size, Material material)
    {
        double thickness = Math.Max(size * 0.22, 0.012);
        AddLine3D(target, position - new Vector3D(size, 0.0, 0.0), position + new Vector3D(size, 0.0, 0.0), thickness, material);
        AddLine3D(target, position - new Vector3D(0.0, size, 0.0), position + new Vector3D(0.0, size, 0.0), thickness, material);
        AddLine3D(target, position - new Vector3D(0.0, 0.0, size), position + new Vector3D(0.0, 0.0, size), thickness, material);
    }

    private static Matrix4x4[] BuildBindWorld(IReadOnlyList<SkeletonBone> bones)
    {
        Matrix4x4[] array = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            SkeletonBone skeletonBone = bones[i];
            Matrix4x4 matrix4x = Matrix4x4.CreateScale(skeletonBone.ScaleX, skeletonBone.ScaleY, skeletonBone.ScaleZ) * Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(skeletonBone.RotationX, skeletonBone.RotationY, skeletonBone.RotationZ, skeletonBone.RotationW)) * Matrix4x4.CreateTranslation(skeletonBone.PositionX, skeletonBone.PositionY, skeletonBone.PositionZ);
            array[i] = ((skeletonBone.ParentLocalId >= 0 && skeletonBone.ParentLocalId < i) ? (matrix4x * array[skeletonBone.ParentLocalId]) : matrix4x);
        }
        return array;
    }

    private async Task LoadAccessoryAssetAsync(AssetEntry asset)
    {
        if ((object)_model == null || asset.Kind != AssetKind.Model)
        {
            return;
        }
        StatusText.Text = "正在加载配件：" + asset.AnnotatedDisplayName;
        try
        {
            _weaponModel = await Task.Run(() => G1Parsers.ReadModel(asset.FullPath));
            _weaponAsset = asset;
            await LoadPairedWeaponAsync(asset);
            PopulateAccessoryBoneChoices();
            ResolveAccessoryAttachment();
            WeaponCheck.IsEnabled = true;
            WeaponCheck.IsChecked = true;
            AccessoryNameText.Text = asset.AnnotatedDisplayName;
            WeaponCheck.ToolTip = "当前配件：" + asset.AnnotatedDisplayName;
            BuildWeaponPreview();
            UpdateWeaponToCurrentPose();
            StatusText.Text = "已添加独立配件：" + asset.AnnotatedDisplayName;
        }
        catch (Exception ex)
        {
            RemoveAccessory();
            StatusText.Text = "配件模型读取失败：" + ex.Message;
        }
    }

    private async Task LoadPairedWeaponAsync(AssetEntry asset)
    {
        _secondaryWeaponModel = null;
        _secondaryWeaponAsset = null;
        _secondaryWeaponAttachment = null;
        string? pairedOwner = PairedWeaponOwner(asset.OwnerName ?? "");
        if (pairedOwner == null)
        {
            // 孙尚香的 HW0004 飞圈与貂蝉的 HW0011 绳镖没有拆成
            // _L/_R 文件；游戏在两个装备槽中复用同一模型。
            bool sharedDualModel = asset.OwnerName?.StartsWith("HW0004_", StringComparison.OrdinalIgnoreCase) == true ||
                                   asset.OwnerName?.StartsWith("HW0011_", StringComparison.OrdinalIgnoreCase) == true;
            if (!sharedDualModel) return;
            _secondaryWeaponModel = _weaponModel;
            _secondaryWeaponAsset = asset;
            _secondaryWeaponAttachment = ModelAttachmentComposer.Resolve(_secondaryWeaponModel!, 103);
            return;
        }
        AssetEntry? pair = _all.FirstOrDefault(x => x.Kind == AssetKind.Model &&
            string.Equals(x.OwnerName, pairedOwner, StringComparison.OrdinalIgnoreCase));
        if (pair == null) return;
        _secondaryWeaponModel = await Task.Run(() => G1Parsers.ReadModel(pair.FullPath));
        _secondaryWeaponAsset = pair;
        ResolveSecondaryWeaponAttachment();
    }

    private static string? PairedWeaponOwner(string owner)
    {
        int marker = owner.IndexOf("_L", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) return owner[..marker] + "_R" + owner[(marker + 2)..];
        marker = owner.IndexOf("_R", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? owner[..marker] + "_L" + owner[(marker + 2)..] : null;
    }

    private void ResolveSecondaryWeaponAttachment()
    {
        if ((object)_secondaryWeaponModel == null || _secondaryWeaponAsset == null) return;
        string owner = _secondaryWeaponAsset.OwnerName ?? "";
        // Some paired weapons (flying rings / rope darts) reuse one model for both hands
        // and therefore have no _L/_R suffix.  Do not resolve those back onto the
        // primary socket: that makes both copies overlap and look like a single weapon.
        int targetBone = WeaponTargetBone(owner, secondarySharedCopy: true);
        // L/R 文件的骨架布局并不保证装备根固定为 local bone 1。
        // 按实际蒙皮引用求公共祖先，才能正确处理黄盖手甲等多骨骼模型。
        _secondaryWeaponAttachment = ModelAttachmentComposer.Resolve(_secondaryWeaponModel, targetBone);
    }

    private async void AddSelectedAccessory_Click(object sender, RoutedEventArgs e)
    {
        AssetEntry asset = _selected;
        if (asset == null || asset.Kind != AssetKind.Model || (object)_model == null)
        {
            return;
        }
        StatusText.Text = "正在加载配件：" + asset.AnnotatedDisplayName;
        try
        {
            _weaponModel = await Task.Run(() => G1Parsers.ReadModel(asset.FullPath));
            _weaponAsset = asset;
            await LoadPairedWeaponAsync(asset);
            PopulateAccessoryBoneChoices();
            ResolveAccessoryAttachment();
            WeaponCheck.IsEnabled = true;
            WeaponCheck.IsChecked = true;
            AccessoryNameText.Text = asset.AnnotatedDisplayName;
            WeaponCheck.ToolTip = "当前配件：" + asset.AnnotatedDisplayName;
            BuildWeaponPreview();
            UpdateWeaponToCurrentPose();
            StatusText.Text = "已添加独立配件：" + asset.AnnotatedDisplayName;
        }
        catch (Exception ex)
        {
            RemoveAccessory();
            StatusText.Text = "配件模型读取失败：" + ex.Message;
        }
    }

    private void RemoveAccessory_Click(object sender, RoutedEventArgs e)
    {
        RemoveAccessory();
    }

    private void RemoveAccessory()
    {
        _weaponModel = null;
        _weaponAsset = null;
        _weaponAttachment = null;
        _secondaryWeaponModel = null;
        _secondaryWeaponAsset = null;
        _secondaryWeaponAttachment = null;
        _weaponGeometries.Clear();
        _secondaryWeaponGeometries.Clear();
        _weaponGroup.Children.Clear();
        _comparisonWeaponGroup.Children.Clear();
        _secondaryWeaponGroup.Children.Clear();
        _comparisonSecondaryWeaponGroup.Children.Clear();
        WeaponCheck.IsEnabled = false;
        WeaponCheck.IsChecked = false;
        AccessorySourceBoneCombo.ItemsSource = null;
        AccessoryTargetBoneCombo.ItemsSource = null;
        AccessorySourceBoneCombo.IsEnabled = false;
        AccessoryTargetBoneCombo.IsEnabled = false;
        AccessoryNameText.Text = "未添加";
        AccessoryMappingText.Text = "添加配件后显示骨骼对齐关系";
    }

    private void PopulateAccessoryBoneChoices()
    {
        if ((object)_weaponModel != null && (object)_model != null)
        {
            _updatingAccessoryEditor = true;
            BoneChoice[] array = _weaponModel.Bones.Select((SkeletonBone b) => new BoneChoice(b.LocalId, b.GlobalId)).ToArray();
            BoneChoice[] array2 = _model.Bones.Select((SkeletonBone b) => new BoneChoice(b.LocalId, b.GlobalId)).ToArray();
            AccessorySourceBoneCombo.ItemsSource = array;
            AccessoryTargetBoneCombo.ItemsSource = array2;
            AccessorySourceBoneCombo.IsEnabled = array.Length != 0;
            AccessoryTargetBoneCombo.IsEnabled = array2.Length != 0;
            int primaryTarget = WeaponTargetBone(_weaponAsset?.OwnerName, secondarySharedCopy: false);
            ModelAttachmentComposer.Attachment automatic = ModelAttachmentComposer.Resolve(_weaponModel, primaryTarget);
            AccessorySourceBoneCombo.SelectedItem = array.FirstOrDefault((BoneChoice x) => x.LocalId == automatic.SourceLocalBoneId) ?? array.FirstOrDefault();
            AccessoryTargetBoneCombo.SelectedItem = array2.FirstOrDefault((BoneChoice x) => x.GlobalId == primaryTarget) ?? array2.FirstOrDefault();
            _updatingAccessoryEditor = false;
        }
    }

    private void ResolveAccessoryAttachment()
    {
        if ((object)_weaponModel != null && AccessorySourceBoneCombo.SelectedItem is BoneChoice boneChoice && AccessoryTargetBoneCombo.SelectedItem is BoneChoice boneChoice2)
        {
            _weaponAttachment = ModelAttachmentComposer.Resolve(_weaponModel, boneChoice.LocalId, boneChoice2.GlobalId);
            ResolveSecondaryWeaponAttachment();
            AccessoryMappingText.Text = $"独立配件 bone_{boneChoice.GlobalId} → 人物 bone_{boneChoice2.GlobalId}";
        }
    }

    private void AutoFitAccessory_Click(object sender, RoutedEventArgs e)
    {
        if ((object)_weaponModel == null || _weaponAsset == null || (object)_model == null)
        {
            StatusText.Text = "请先添加一个配件模型";
            return;
        }
        IReadOnlyDictionary<uint, IReadOnlyList<string>> officialObjects = KidsNameCatalogReader.BuildOfficialObjectNames(_root);
        AccessoryAutoFitCatalog.Fit fit = AccessoryAutoFitCatalog.Find(_weaponAsset, _weaponModel, officialObjects);
        if ((object)fit == null)
        {
            StatusText.Text = "游戏配置中未找到该配件的装备适配记录";
            AccessoryMappingText.Text = "未找到游戏配置；保留当前手动设置";
            return;
        }
        _updatingAccessoryEditor = true;
        if (AccessorySourceBoneCombo.ItemsSource is IEnumerable<BoneChoice> source)
        {
            AccessorySourceBoneCombo.SelectedItem = source.FirstOrDefault((BoneChoice x) => x.LocalId == fit.SourceLocalBoneId);
        }
        if (AccessoryTargetBoneCombo.ItemsSource is IEnumerable<BoneChoice> source2)
        {
            AccessoryTargetBoneCombo.SelectedItem = source2.FirstOrDefault((BoneChoice x) => x.GlobalId == fit.TargetGlobalBoneId);
        }
        TextBox accessoryPosX = AccessoryPosX;
        TextBox accessoryPosY = AccessoryPosY;
        string text = (AccessoryPosZ.Text = "0");
        string text3 = (accessoryPosY.Text = text);
        accessoryPosX.Text = text3;
        TextBox accessoryRotX = AccessoryRotX;
        TextBox accessoryRotY = AccessoryRotY;
        text = (AccessoryRotZ.Text = "0");
        text3 = (accessoryRotY.Text = text);
        accessoryRotX.Text = text3;
        AccessoryScale.Text = "1";
        _accessoryAdjustment = Matrix4x4.Identity;
        _updatingAccessoryEditor = false;
        ResolveAccessoryAttachment();
        UpdateWeaponToCurrentPose();
        AccessoryMappingText.Text = fit.Evidence;
        StatusText.Text = "已按游戏配置自动适配：" + _weaponAsset.AnnotatedDisplayName;
    }

    private void BuildWeaponPreview()
    {
        _weaponGroup.Children.Clear();
        _comparisonWeaponGroup.Children.Clear();
        _secondaryWeaponGroup.Children.Clear();
        _comparisonSecondaryWeaponGroup.Children.Clear();
        _weaponGeometries.Clear();
        _secondaryWeaponGeometries.Clear();
        _weaponGroup.Transform = Transform3D.Identity;
        _comparisonWeaponGroup.Transform = Transform3D.Identity;
        _secondaryWeaponGroup.Transform = Transform3D.Identity;
        _comparisonSecondaryWeaponGroup.Transform = Transform3D.Identity;
        if (WeaponCheck.IsChecked != true || (object)_weaponModel == null)
        {
            return;
        }
        MaterialGroup materialGroup = new MaterialGroup();
        materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(173, 151, 105))));
        materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(150, byte.MaxValue, 242, 204)), 48.0));
        foreach (MeshPart part in _weaponModel.Parts)
        {
            MeshGeometry3D meshGeometry3D = new MeshGeometry3D();
            for (int i = 0; i + 2 < part.Positions.Length; i += 3)
            {
                meshGeometry3D.Positions.Add(ToWorld(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
            }
            AppendTriangles(meshGeometry3D.TriangleIndices, part.Indices, part.TriangleStrip, meshGeometry3D.Positions.Count);
            _weaponGeometries.Add(meshGeometry3D);
            _weaponGroup.Children.Add(new GeometryModel3D(meshGeometry3D, materialGroup)
            {
                BackMaterial = materialGroup
            });
            _comparisonWeaponGroup.Children.Add(new GeometryModel3D(meshGeometry3D, materialGroup)
            {
                BackMaterial = materialGroup
            });
        }
        if ((object)_secondaryWeaponModel != null)
        {
            foreach (MeshPart part in _secondaryWeaponModel.Parts)
            {
                MeshGeometry3D mesh = new MeshGeometry3D();
                for (int i = 0; i + 2 < part.Positions.Length; i += 3)
                    mesh.Positions.Add(ToWorld(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
                AppendTriangles(mesh.TriangleIndices, part.Indices, part.TriangleStrip, mesh.Positions.Count);
                _secondaryWeaponGeometries.Add(mesh);
                _secondaryWeaponGroup.Children.Add(new GeometryModel3D(mesh, materialGroup) { BackMaterial = materialGroup });
                _comparisonSecondaryWeaponGroup.Children.Add(new GeometryModel3D(mesh, materialGroup) { BackMaterial = materialGroup });
            }
        }
    }

    private void UpdateWeaponPreview(IReadOnlyList<Matrix4x4> poseWorld)
    {
        if (WeaponCheck.IsChecked == true && (object)_weaponModel != null && (object)_weaponAttachment != null && (object)_model != null)
        {
            if (_weaponGeometries.Count != _weaponModel.Parts.Count)
            {
                BuildWeaponPreview();
            }
            SkeletonBone skeletonBone = _model.Bones.FirstOrDefault((SkeletonBone bone) => bone.GlobalId == _weaponAttachment.TargetGlobalBoneId);
            if ((object)skeletonBone != null && skeletonBone.LocalId >= 0 && skeletonBone.LocalId < poseWorld.Count)
            {
                Matrix4x4 matrix4x = ModelAttachmentComposer.Compose(_weaponAttachment,
                    _accessoryAdjustment, poseWorld[skeletonBone.LocalId]);
                Matrix4x4 matrix4x2 = new Matrix4x4(-1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f);
                Matrix4x4 matrix4x3 = matrix4x2 * matrix4x * matrix4x2;
                Matrix3D matrix = new Matrix3D(matrix4x3.M11, matrix4x3.M12, matrix4x3.M13, matrix4x3.M14, matrix4x3.M21, matrix4x3.M22, matrix4x3.M23, matrix4x3.M24, matrix4x3.M31, matrix4x3.M32, matrix4x3.M33, matrix4x3.M34, matrix4x3.M41, matrix4x3.M42, matrix4x3.M43, matrix4x3.M44);
                _weaponGroup.Transform = new MatrixTransform3D(matrix);
                _comparisonWeaponGroup.Transform = new MatrixTransform3D(matrix);
            }
            UpdateSecondaryWeaponPreview(poseWorld);
        }
    }

    private static int WeaponTargetBone(string? ownerName, bool secondarySharedCopy)
    {
        string owner = ownerName ?? "";
        bool right = owner.Contains("_R", StringComparison.OrdinalIgnoreCase);
        // Shared dual models have no side suffix: primary uses the left equipment slot,
        // and the generated second copy uses the right equipment slot.
        if (owner.StartsWith("HW0004_", StringComparison.OrdinalIgnoreCase) ||
            owner.StartsWith("HW0011_", StringComparison.OrdinalIgnoreCase))
            return secondarySharedCopy ? 103 : 104;
        return right ? 103 : 104;
    }

    private void UpdateSecondaryWeaponPreview(IReadOnlyList<Matrix4x4> poseWorld)
    {
        if ((object)_secondaryWeaponModel == null || (object)_secondaryWeaponAttachment == null || (object)_model == null)
            return;
        SkeletonBone? target = _model.Bones.FirstOrDefault(bone => bone.GlobalId == _secondaryWeaponAttachment.TargetGlobalBoneId);
        if (target == null || target.LocalId < 0 || target.LocalId >= poseWorld.Count) return;
        Matrix4x4 composed = ModelAttachmentComposer.Compose(_secondaryWeaponAttachment,
            _accessoryAdjustment, poseWorld[target.LocalId]);
        Matrix4x4 basis = new(-1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f);
        Matrix4x4 world = basis * composed * basis;
        Matrix3D matrix = new(world.M11, world.M12, world.M13, world.M14, world.M21, world.M22, world.M23, world.M24,
            world.M31, world.M32, world.M33, world.M34, world.M41, world.M42, world.M43, world.M44);
        _secondaryWeaponGroup.Transform = new MatrixTransform3D(matrix);
        _comparisonSecondaryWeaponGroup.Transform = new MatrixTransform3D(matrix);
    }

    private void UpdateWeaponToCurrentPose()
    {
        if ((object)_model == null || (object)_weaponModel == null || WeaponCheck.IsChecked != true)
        {
            return;
        }
        if ((object)_clip != null)
        {
            RenderAnimatedSkeleton((float)_playbackTime);
            return;
        }
        Matrix4x4[] array = new Matrix4x4[_model.Bones.Count];
        for (int i = 0; i < _model.Bones.Count; i++)
        {
            SkeletonBone skeletonBone = _model.Bones[i];
            Matrix4x4 matrix4x = Matrix4x4.CreateScale(skeletonBone.ScaleX, skeletonBone.ScaleY, skeletonBone.ScaleZ) * Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(skeletonBone.RotationX, skeletonBone.RotationY, skeletonBone.RotationZ, skeletonBone.RotationW)) * Matrix4x4.CreateTranslation(skeletonBone.PositionX, skeletonBone.PositionY, skeletonBone.PositionZ);
            array[i] = ((skeletonBone.ParentLocalId >= 0 && skeletonBone.ParentLocalId < i) ? (matrix4x * array[skeletonBone.ParentLocalId]) : matrix4x);
        }
        UpdateWeaponPreview(array);
    }

    private void AccessoryBone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingAccessoryEditor && base.IsLoaded && (object)_weaponModel != null)
        {
            ResolveAccessoryAttachment();
            UpdateWeaponToCurrentPose();
        }
    }

    private void AccessoryTransform_Changed(object sender, TextChangedEventArgs e)
    {
        if (base.IsLoaded && !_updatingAccessoryEditor)
        {
            float scale = Math.Clamp(Value(AccessoryScale, 1f), 0.001f, 1000f);
            Matrix4x4 matrix4x = Matrix4x4.CreateFromYawPitchRoll(Value(AccessoryRotY, 0f) * (float)Math.PI / 180f, Value(AccessoryRotX, 0f) * (float)Math.PI / 180f, Value(AccessoryRotZ, 0f) * (float)Math.PI / 180f);
            _accessoryAdjustment = Matrix4x4.CreateScale(scale) * matrix4x * Matrix4x4.CreateTranslation(Value(AccessoryPosX, 0f), Value(AccessoryPosY, 0f), Value(AccessoryPosZ, 0f));
            UpdateWeaponToCurrentPose();
        }
        static float Value(TextBox? box, float fallback)
        {
            if (box == null || !float.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var result) || !float.IsFinite(result))
            {
                return fallback;
            }
            return result;
        }
    }

    private static SkeletonBone? FindAnimationBone(G1MInfo model, G1AClip clip, int animationBoneId)
    {
        return FindAnimationBone(model.Bones, clip, animationBoneId);
    }

    private static SkeletonBone? FindAnimationBone(IReadOnlyList<SkeletonBone> bones, G1AClip clip, int animationBoneId)
    {
        return bones.FirstOrDefault((SkeletonBone b) => b.GlobalId == animationBoneId);
    }

    private void RenderSkinnedModel(Matrix4x4[] skin)
    {
        if ((object)_model == null || _meshGeometries.Count != _model.Parts.Count || _meshPositionBackBuffers.Count != _model.Parts.Count)
        {
            return;
        }
        for (int i = 0; i < _model.Parts.Count; i++)
        {
            MeshPart meshPart = _model.Parts[i];
            float[] positions = meshPart.Positions;
            if (meshPart.BoneIndices != null && meshPart.BoneWeights != null)
            {
                Point3DCollection point3DCollection = _meshPositionBackBuffers[i];
                for (int j = 0; j < positions.Length / 3; j++)
                {
                    Vector3 vector = AnimationPoseEvaluator.SkinVertex(meshPart, j, skin);
                    point3DCollection[j] = ToWorld(vector.X, vector.Y, vector.Z);
                }
                MeshGeometry3D meshGeometry3D = _meshGeometries[i];
                _meshPositionBackBuffers[i] = meshGeometry3D.Positions;
                meshGeometry3D.Positions = point3DCollection;
            }
        }
        if (_renderMode == 2)
        {
            WireframeLayer.RequestRender();
        }
    }

    private void UpdateCamera()
    {
        if ((object)_model == null)
        {
            return;
        }
        if (CameraModeCombo.SelectedIndex == 1 && (object)_actionCamera != null)
        {
            ApplyActionCamera((float)_playbackTime);
            return;
        }
        ViewportCamera.FieldOfView = 42.0;
        double num = _sceneRadius * 3.15 / _zoom;
        Vector3D vector3D = new Vector3D(Math.Cos(_pitch) * Math.Sin(_yaw), Math.Cos(_pitch) * Math.Cos(_yaw), Math.Sin(_pitch));
        Point3D point3D = (((object)_model == null) ? _cameraTarget : _cameraTarget);
        Point3D point3D2 = point3D + vector3D * num;
        ViewportCamera.Position = point3D2;
        ViewportCamera.LookDirection = point3D - point3D2;
        ViewportCamera.UpDirection = ((!(Math.Abs(Math.Cos(_pitch)) < 0.01)) ? new Vector3D(0.0, 0.0, 1.0) : ((_pitch > 0.0) ? new Vector3D(0.0, -1.0, 0.0) : new Vector3D(0.0, 1.0, 0.0)));
        ViewportCamera.NearPlaneDistance = Math.Max(0.001, num / 1000.0);
        ViewportCamera.FarPlaneDistance = Math.Max(1000.0, num + _sceneRadius * 10.0);
        UpdateWorldAxisIndicator();
        if (_renderMode == 2)
        {
            WireframeLayer.RequestRender();
        }
    }

    private void UpdateComparisonCamera(float? playbackTime = null)
    {
        if ((object)_model != null && DualViewportCheck.IsChecked == true)
        {
            float time = playbackTime ?? ((float)_playbackTime);
            if (ComparisonCameraModeCombo.SelectedIndex == 1 && (object)_actionCamera != null)
            {
                ApplyActionCamera(ComparisonCamera, ComparisonViewport, time);
                return;
            }
            ComparisonCamera.FieldOfView = 42.0;
            double num = _sceneRadius * 3.15 / _comparisonZoom;
            Vector3D vector3D = new Vector3D(Math.Cos(_comparisonPitch) * Math.Sin(_comparisonYaw), Math.Cos(_comparisonPitch) * Math.Cos(_comparisonYaw), Math.Sin(_comparisonPitch));
            Point3D point3D = _comparisonCameraTarget + vector3D * num;
            ComparisonCamera.Position = point3D;
            ComparisonCamera.LookDirection = _comparisonCameraTarget - point3D;
            ComparisonCamera.UpDirection = ((!(Math.Abs(Math.Cos(_comparisonPitch)) < 0.01)) ? new Vector3D(0.0, 0.0, 1.0) : ((_comparisonPitch > 0.0) ? new Vector3D(0.0, -1.0, 0.0) : new Vector3D(0.0, 1.0, 0.0)));
            ComparisonCamera.NearPlaneDistance = Math.Max(0.001, num / 1000.0);
            ComparisonCamera.FarPlaneDistance = Math.Max(1000.0, num + _sceneRadius * 10.0);
        }
    }

    private void QueueCameraUpdate()
    {
        _cameraDirty = true;
    }

    private void Viewport_Rendering(object? sender, EventArgs e)
    {
        if (_cameraDirty)
        {
            _cameraDirty = false;
            UpdateCamera();
            UpdateComparisonCamera();
        }
        RefreshHelperView();
        if (_overlayDirty)
        {
            _overlayDirty = false;
            RenderSkeletonOverlay();
        }
    }

    private void UpdateWorldAxisIndicator()
    {
        if (!base.IsLoaded || WorldAxisX == null)
        {
            return;
        }
        Vector3D lookDirection = ViewportCamera.LookDirection;
        if (lookDirection.LengthSquared < 1E-08)
        {
            return;
        }
        lookDirection.Normalize();
        Vector3D vector3D = Vector3D.CrossProduct(lookDirection, ViewportCamera.UpDirection);
        if (vector3D.LengthSquared < 1E-08)
        {
            return;
        }
        vector3D.Normalize();
        Vector3D vector = Vector3D.CrossProduct(vector3D, lookDirection);
        vector.Normalize();
        Vector3D[] array = new Vector3D[3]
        {
            new Vector3D(-1.0, 0.0, 0.0),
            new Vector3D(0.0, 1.0, 0.0),
            new Vector3D(0.0, 0.0, 1.0)
        };
        Line[] array2 = new Line[3] { WorldAxisX, WorldAxisY, WorldAxisZ };
        TextBlock[] array3 = new TextBlock[3] { WorldAxisXLabel, WorldAxisYLabel, WorldAxisZLabel };
        for (int i = 0; i < array.Length; i++)
        {
            double num = Vector3D.DotProduct(array[i], vector3D);
            double num2 = 0.0 - Vector3D.DotProduct(array[i], vector);
            double num3 = Math.Sqrt(num * num + num2 * num2);
            if (num3 > 1E-08)
            {
                num /= num3;
                num2 /= num3;
            }
            double num4 = 45.0 + num * 32.0;
            double num5 = 58.0 + num2 * 32.0;
            array2[i].X1 = 45.0;
            array2[i].Y1 = 58.0;
            array2[i].X2 = num4;
            array2[i].Y2 = num5;
            Canvas.SetLeft(array3[i], Math.Clamp(num4 + (double)((num >= 0.0) ? 3 : (-12)), 2.0, 78.0));
            Canvas.SetTop(array3[i], Math.Clamp(num5 - 9.0, 1.0, 72.0));
        }
    }

    private void AddGrid3D(Model3DGroup target)
    {
        double num = _sceneRadius * 1.8;
        float num2 = _model?.MinY ?? 0f;
        double num3 = num / 10.0;
        DiffuseMaterial material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(45, 54, 66)));
        for (int i = -10; i <= 10; i++)
        {
            AddLine3D(target, new Point3D(_sceneCenter.X - num, _sceneCenter.Y + (double)i * num3, num2), new Point3D(_sceneCenter.X + num, _sceneCenter.Y + (double)i * num3, num2), num3 * 0.012, material);
            AddLine3D(target, new Point3D(_sceneCenter.X + (double)i * num3, _sceneCenter.Y - num, num2), new Point3D(_sceneCenter.X + (double)i * num3, _sceneCenter.Y + num, num2), num3 * 0.012, material);
        }
    }

    private void AddWorldBackground(Model3DGroup target)
    {
        // The training-ground atmosphere is rendered as the viewport backdrop.
        // Keeping an opaque, kilometer-wide 3D floor here creates a hard horizon
        // seam in WPF. The perspective grid remains the true 3D ground reference.
    }

    private static void AddEnvironmentGround(Model3DGroup target, Point3D center, double radius, double sceneRadius)
    {
        // DW Origins training-ground look: one continuous dark floor. Depth is
        // provided by perspective grid and the low atmospheric fog behind it;
        // visible concentric bands read as a target and are intentionally avoided.
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(30, 31, 32)));
        AddGroundDisk(target, center, radius, material);
    }

    private static void AddSkySphere(Model3DGroup target, Point3D center, double radius)
    {
        const int longitudeSegments = 64;
        const int latitudeBands = 32;
        for (int latitude = 0; latitude < latitudeBands; latitude++)
        {
            double v0 = latitude / (double)latitudeBands;
            double v1 = (latitude + 1) / (double)latitudeBands;
            double phi0 = Math.PI * (v0 - 0.5);
            double phi1 = Math.PI * (v1 - 0.5);
            double ring0 = Math.Cos(phi0) * radius;
            double ring1 = Math.Cos(phi1) * radius;
            double z0 = center.Z + Math.Sin(phi0) * radius;
            double z1 = center.Z + Math.Sin(phi1) * radius;
            var mesh = new MeshGeometry3D();
            for (int longitude = 0; longitude <= longitudeSegments; longitude++)
            {
                double u = longitude / (double)longitudeSegments;
                double theta = u * Math.PI * 2.0;
                double cos = Math.Cos(theta);
                double sin = Math.Sin(theta);
                mesh.Positions.Add(new Point3D(center.X + cos * ring0, center.Y + sin * ring0, z0));
                mesh.Positions.Add(new Point3D(center.X + cos * ring1, center.Y + sin * ring1, z1));
            }
            for (int longitude = 0; longitude < longitudeSegments; longitude++)
            {
                int a = longitude * 2;
                int b = a + 1;
                int c = a + 2;
                int d = a + 3;
                foreach (int index in new[] { a, d, b, a, c, d }) mesh.TriangleIndices.Add(index);
            }
            double height = ((v0 + v1) * 0.5 - 0.5) * 2.0;
            Color color = SampleSkyAtmosphere(height);
            var material = new EmissiveMaterial(new SolidColorBrush(color));
            target.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        }
    }

    private static Color SampleSkyAtmosphere(double height)
    {
        // UE-like neutral daylight: a pale atmospheric horizon transitioning
        // into saturated blue overhead; the lower hemisphere blends into ground.
        if (height < -0.12) return Color.FromRgb(45, 50, 49);
        if (height < 0.00) return LerpColor(Color.FromRgb(45, 50, 49), Color.FromRgb(190, 205, 207), (height + 0.12) / 0.12);
        if (height < 0.10) return LerpColor(Color.FromRgb(190, 205, 207), Color.FromRgb(119, 169, 205), height / 0.10);
        return LerpColor(Color.FromRgb(119, 169, 205), Color.FromRgb(28, 76, 137), Math.Min(1.0, (height - 0.10) / 0.90));
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static void AddGroundDisk(Model3DGroup target, Point3D center, double radius, Material material)
    {
        const int segments = 96;
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(center);
        for (int i = 0; i <= segments; i++)
        {
            double angle = i / (double)segments * Math.PI * 2.0;
            mesh.Positions.Add(new Point3D(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius, center.Z));
        }
        for (int i = 0; i < segments; i++)
            foreach (int index in new[] { 0, i + 1, i + 2 }) mesh.TriangleIndices.Add(index);
        target.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private static void AddQuad3D(Model3DGroup target, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Material material)
    {
        MeshGeometry3D meshGeometry3D = new MeshGeometry3D();
        Point3D[] array = new Point3D[4] { p0, p1, p2, p3 };
        foreach (Point3D value in array)
        {
            meshGeometry3D.Positions.Add(value);
        }
        int[] array2 = new int[6] { 0, 1, 2, 0, 2, 3 };
        foreach (int value2 in array2)
        {
            meshGeometry3D.TriangleIndices.Add(value2);
        }
        target.Children.Add(new GeometryModel3D(meshGeometry3D, material)
        {
            BackMaterial = material
        });
    }

    private void AddAxes3D(Model3DGroup target)
    {
        double num = _sceneRadius * 0.45;
        double thickness = Math.Max(0.002, _sceneRadius * 0.008);
        Point3D point3D = new Point3D(0.0, 0.0, 0.0);
        AddLine3D(target, point3D, point3D + new Vector3D(0.0 - num, 0.0, 0.0), thickness, new DiffuseMaterial(Brushes.IndianRed));
        AddLine3D(target, point3D, point3D + new Vector3D(0.0, num, 0.0), thickness, new DiffuseMaterial(Brushes.LightGreen));
        AddLine3D(target, point3D, point3D + new Vector3D(0.0, 0.0, num), thickness, new DiffuseMaterial(Brushes.CornflowerBlue));
    }

    private void AddLine3D(Point3D start, Point3D end, double thickness, Material material)
    {
        AddLine3D(_scene, start, end, thickness, material);
    }

    private static void AddLine3D(Model3DGroup target, Point3D start, Point3D end, double thickness, Material material)
    {
        Vector3D vector = end - start;
        if (!(vector.Length < 1E-08))
        {
            vector.Normalize();
            Vector3D vector2 = ((Math.Abs(Vector3D.DotProduct(vector, new Vector3D(0.0, 1.0, 0.0))) > 0.9) ? new Vector3D(1.0, 0.0, 0.0) : new Vector3D(0.0, 1.0, 0.0));
            Vector3D vector3D = Vector3D.CrossProduct(vector, vector2);
            vector3D.Normalize();
            vector3D *= thickness;
            Vector3D vector3D2 = Vector3D.CrossProduct(vector, vector3D);
            vector3D2.Normalize();
            vector3D2 *= thickness;
            MeshGeometry3D meshGeometry3D = new MeshGeometry3D();
            Point3D[] array = new Point3D[8]
            {
                start - vector3D - vector3D2,
                start + vector3D - vector3D2,
                start + vector3D + vector3D2,
                start - vector3D + vector3D2,
                end - vector3D - vector3D2,
                end + vector3D - vector3D2,
                end + vector3D + vector3D2,
                end - vector3D + vector3D2
            };
            foreach (Point3D value in array)
            {
                meshGeometry3D.Positions.Add(value);
            }
            int[] array2 = new int[36]
            {
                0, 1, 2, 0, 2, 3, 4, 6, 5, 4,
                7, 6, 0, 4, 5, 0, 5, 1, 1, 5,
                6, 1, 6, 2, 2, 6, 7, 2, 7, 3,
                3, 7, 4, 3, 4, 0
            };
            foreach (int value2 in array2)
            {
                meshGeometry3D.TriangleIndices.Add(value2);
            }
            target.Children.Add(new GeometryModel3D(meshGeometry3D, material)
            {
                BackMaterial = material
            });
        }
    }

    private void RenderSkeletonOverlay()
    {
        ViewportOverlay.Children.Clear();
    }

    private static HashSet<int> GetVisibleDeformBoneChain(G1MInfo model)
    {
        HashSet<int> hashSet = (from num2 in model.Parts.Where((MeshPart part) => part.BoneIndices != null).SelectMany((MeshPart part) => part.BoneIndices)
                                where num2 >= 0 && num2 < model.Bones.Count
                                select num2).ToHashSet();
        int[] array = hashSet.ToArray();
        foreach (int index in array)
        {
            int parentLocalId = model.Bones[index].ParentLocalId;
            while (parentLocalId >= 0 && parentLocalId < model.Bones.Count && hashSet.Add(parentLocalId))
            {
                parentLocalId = model.Bones[parentLocalId].ParentLocalId;
            }
        }
        return hashSet;
    }

    private void RefreshBoneTree()
    {
        _boneRoots.Clear();
        if ((object)_model == null)
        {
            BoneSummaryText.Text = "未加载";
            return;
        }
        BoneTreeNode[] array = _model.Bones.Select((SkeletonBone x) => new BoneTreeNode(x.LocalId, x.GlobalId, x.ParentLocalId)).ToArray();
        for (int num = 0; num < array.Length; num++)
        {
            int parentLocalId = array[num].ParentLocalId;
            if (parentLocalId >= 0 && parentLocalId < array.Length)
            {
                array[parentLocalId].Children.Add(array[num]);
            }
            else
            {
                _boneRoots.Add(array[num]);
            }
        }
        BoneSummaryText.Text = $"{array.Length:N0} 根";
    }

    private void RenderTimeline(G1AInfo a)
    {
        _scene.Children.Clear();
        _meshGeometries.Clear();
        _meshModels.Clear();
        _texturedMaterials.Clear();
        _solidMaterials.Clear();
        WireframeLayer.Visibility = Visibility.Collapsed;
        ViewportOverlay.Children.Clear();
        double num = Math.Max(400.0, Viewport.ActualWidth);
        double num2 = Math.Max(300.0, Viewport.ActualHeight);
        int num3 = Math.Min(48, Math.Max(1, a.TrackCount));
        for (int i = 0; i < num3; i++)
        {
            double num4 = 75.0 + (double)i * Math.Min(14.0, (num2 - 130.0) / (double)num3);
            ViewportOverlay.Children.Add(new Line
            {
                X1 = 60.0,
                X2 = num - 50.0,
                Y1 = num4,
                Y2 = num4,
                Stroke = new SolidColorBrush(Color.FromRgb(50, 61, 75)),
                StrokeThickness = 1.0
            });
            for (int j = 0; j < 4 + i % 7; j++)
            {
                double left = 70.0 + (num - 140.0) * (((double)j + (double)(i % 3) * 0.18) / (double)(4 + i % 7));
                ViewportOverlay.Children.Add(new Ellipse
                {
                    Width = 5.0,
                    Height = 5.0,
                    Fill = new SolidColorBrush(Color.FromRgb(215, 168, 74)),
                    Margin = new Thickness(left, num4 - 2.5, 0.0, 0.0)
                });
            }
        }
        TextBlock element = new TextBlock
        {
            Text = $"G1A {a.Version}  ·  {a.Duration:0.###} s  ·  {a.TrackCount} tracks",
            FontSize = 20.0,
            Foreground = Brushes.Gainsboro
        };
        Canvas.SetLeft(element, 60.0);
        Canvas.SetTop(element, 28.0);
        ViewportOverlay.Children.Add(element);
    }

    private void RenderPackageCard(string text)
    {
        _scene.Children.Clear();
        _meshGeometries.Clear();
        _meshModels.Clear();
        _texturedMaterials.Clear();
        _solidMaterials.Clear();
        WireframeLayer.Visibility = Visibility.Collapsed;
        ViewportOverlay.Children.Clear();
        TextBlock element = new TextBlock
        {
            Text = text,
            FontSize = 42.0,
            FontWeight = FontWeights.Light,
            Foreground = new SolidColorBrush(Color.FromRgb(95, 108, 127))
        };
        Canvas.SetLeft(element, Math.Max(30.0, Viewport.ActualWidth / 2.0 - 110.0));
        Canvas.SetTop(element, Math.Max(50.0, Viewport.ActualHeight / 2.0 - 35.0));
        ViewportOverlay.Children.Add(element);
    }

    private static string ReadMagic(string path)
    {
        using FileStream fileStream = File.OpenRead(path);
        byte[] array = new byte[8];
        int num = fileStream.Read(array);
        return (num == 0) ? "空文件" : string.Join(' ', array.Take(num).Select(delegate (byte x)
        {
            if (x < 32 || x > 126)
            {
                return x.ToString("X2");
            }
            char c = (char)x;
            return c.ToString();
        }));
    }

    private void ResetView()
    {
        if ((object)_model != null)
        {
            _cameraTarget = _sceneCenter;
            _comparisonCameraTarget = _sceneCenter;
        }
        _yaw = -0.38;
        _pitch = 0.35;
        _zoom = 1.0;
        _comparisonYaw = 0.38;
        _comparisonPitch = 0.28;
        _comparisonZoom = 1.0;
        UpdateCamera();
        UpdateComparisonCamera();
    }

    private void RenderCurrentView()
    {
        QueueCameraUpdate();
    }

    private void ResetView_Click(object s, RoutedEventArgs e)
    {
        ResetView();
    }

    private double GetPlaybackDuration()
    {
        return _clip?.Info.Duration ?? 0f;
    }

    private void SeekPlayback(double time)
    {
        if ((object)_clip != null)
        {
            _playbackTime = Math.Clamp(time, 0.0, Math.Max(0.001, GetPlaybackDuration()));
            _sliderInternal = true;
            AnimationSlider.Value = _playbackTime;
            _sliderInternal = false;
            if ((object)_model != null)
            {
                RenderAnimatedSkeleton((float)_playbackTime);
            }
            CameraCurveWindow cameraCurveWindow = _cameraCurveWindow;
            if (cameraCurveWindow != null && cameraCurveWindow.IsVisible)
            {
                _cameraCurveWindow.SetCurrentTime(_playbackTime);
            }
        }
    }

    private void CameraMode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!base.IsLoaded)
        {
            return;
        }
        if (s == ComparisonCameraModeCombo)
        {
            UpdateComparisonCamera();
            ComparisonViewportHint.Text = BuildViewportModeHint(ComparisonCameraModeCombo.SelectedIndex, right: true);
            return;
        }
        if (CameraModeCombo.SelectedIndex == 1 && (object)_actionCamera != null)
        {
            RenderAnimatedSkeleton((float)_playbackTime);
            ViewportHint.Text = "动画镜头 · 与人物动作按相同时间同步";
        }
        else
        {
            UpdateCamera();
            if ((object)_clip != null && (object)_model != null)
            {
                RenderAnimatedSkeleton((float)_playbackTime);
            }
        }
        UpdateCameraGizmo((float)_playbackTime);
        UpdateCameraTrajectory();
        UpdateRealtimeCameraData((float)_playbackTime);
    }

    private void UpdateCameraModeAvailability(bool hasActionCamera, bool hasConfiguredCamera)
    {
        LinkedCameraModeItem.Visibility = Visibility.Visible;
        LinkedCameraModeItem.IsEnabled = hasActionCamera;
        LinkedCameraModeItem.ToolTip = hasActionCamera ? "播放动作关联的 Camera G1A" : "当前动作没有关联动画镜头";
        ComparisonActionCameraModeItem.Visibility = Visibility.Visible;
        ComparisonActionCameraModeItem.IsEnabled = hasActionCamera;
        ComparisonActionCameraModeItem.ToolTip = LinkedCameraModeItem.ToolTip;
        ConfiguredCameraModeItem.Visibility = Visibility.Collapsed;
        ConfiguredCameraModeItem.IsEnabled = false;
        ComparisonConfiguredCameraModeItem.Visibility = Visibility.Collapsed;
        ComparisonConfiguredCameraModeItem.IsEnabled = false;
        CompositeCameraModeItem.Visibility = Visibility.Collapsed;
        CompositeCameraModeItem.IsEnabled = false;
        ComparisonCompositeCameraModeItem.Visibility = Visibility.Collapsed;
        ComparisonCompositeCameraModeItem.IsEnabled = false;
        CameraModeCombo.IsEnabled = true;
        ComparisonCameraModeCombo.IsEnabled = true;
        int selectedIndex = CameraModeCombo.SelectedIndex;
        if (selectedIndex != 0 && (selectedIndex != 1 || !hasActionCamera))
        {
            CameraModeCombo.SelectedIndex = 0;
        }
        int selectedIndex2 = ComparisonCameraModeCombo.SelectedIndex;
        if (selectedIndex2 != 0 && (selectedIndex2 != 1 || !hasActionCamera))
        {
            ComparisonCameraModeCombo.SelectedIndex = 0;
        }
    }

    private void DualViewport_Changed(object s, RoutedEventArgs e)
    {
        bool valueOrDefault = DualViewportCheck.IsChecked == true;
        ViewportSplitterColumn.Width = (valueOrDefault ? new GridLength(5.0) : new GridLength(0.0));
        ComparisonViewportColumn.Width = (valueOrDefault ? new GridLength(1.0, GridUnitType.Star) : new GridLength(0.0));
        ComparisonViewportSplitter.Visibility = ((!valueOrDefault) ? Visibility.Collapsed : Visibility.Visible);
        ComparisonViewport.Visibility = ((!valueOrDefault) ? Visibility.Collapsed : Visibility.Visible);
        ComparisonViewportInput.Visibility = ((!valueOrDefault) ? Visibility.Collapsed : Visibility.Visible);
        ComparisonModePanel.Visibility = ((!valueOrDefault) ? Visibility.Collapsed : Visibility.Visible);
        ComparisonHintPanel.Visibility = ((!valueOrDefault) ? Visibility.Collapsed : Visibility.Visible);
        ComparisonWireframeLayer.Visibility = ((!valueOrDefault || _renderMode != 2 || (object)_model == null) ? Visibility.Collapsed : Visibility.Visible);
        if (valueOrDefault)
        {
            _comparisonCameraTarget = (((object)_model == null) ? _cameraTarget : _sceneCenter);
            // 双视口用于直接对照：左侧优先播放动作关联的动画镜头，
            // 右侧保持可操作的自由镜头。没有 Camera G1A 时左侧回退自由镜头。
            CameraModeCombo.SelectedIndex = (object)_actionCamera != null ? 1 : 0;
            ComparisonCameraModeCombo.IsEnabled = true;
            ComparisonCameraModeCombo.SelectedIndex = 0;
            ComparisonViewportHint.Text = BuildViewportModeHint(ComparisonCameraModeCombo.SelectedIndex, right: true);
            UpdateComparisonCamera();
        }
        UpdateCameraGizmo((float)_playbackTime);
        DrawCameraTrajectoryProgress((float)_playbackTime, force: true);
        ViewportHint.Text = BuildViewportModeHint(CameraModeCombo.SelectedIndex, right: false);
    }

    private string BuildViewportModeHint(int mode, bool right)
    {
        string text = ((DualViewportCheck.IsChecked != true) ? "" : (right ? "右视口 · " : "左视口 · "));
        return mode switch
        {
            1 => text + "动画镜头",
            _ when ViewportControlCombo.SelectedIndex == 1 => text + "自由镜头 · 左键旋转 · 右键/中键平移 · 滚轮缩放 · F 聚焦",
            _ => text + "自由镜头 · Alt+左键旋转 · Alt+中键平移 · Alt+右键推拉 · 滚轮缩放 · F 聚焦",
        };
    }

    private void ViewportControl_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!base.IsLoaded)
        {
            return;
        }
        _dragging = false;
        _orbiting = false;
        _panning = false;
        _dollying = false;
        if (ViewportInput.IsMouseCaptured)
        {
            ViewportInput.ReleaseMouseCapture();
        }
        if (ComparisonViewportInput.IsMouseCaptured)
        {
            ComparisonViewportInput.ReleaseMouseCapture();
        }
        ViewportHint.Text = BuildViewportModeHint(CameraModeCombo.SelectedIndex, right: false);
        ComparisonViewportHint.Text = BuildViewportModeHint(ComparisonCameraModeCombo.SelectedIndex, right: true);
        ViewportInput.Focus();
    }

    private void CameraGizmo_Changed(object s, RoutedEventArgs e)
    {
        UpdateCameraGizmo((float)_playbackTime);
    }

    private void CameraTrajectory_Changed(object s, RoutedEventArgs e)
    {
        UpdateCameraTrajectory();
    }

    private void CameraData_Click(object s, RoutedEventArgs e)
    {
        if ((object)_actionCamera == null)
        {
            return;
        }
        EnsureCameraDataWindow();
        UpdateRealtimeCameraData((float)_playbackTime);
    }

    private void CameraCurve_Click(object s, RoutedEventArgs e)
    {
        ShowCameraCurveWindow();
    }

    private void WeaponCheck_Changed(object s, RoutedEventArgs e)
    {
        BuildWeaponPreview();
        UpdateWeaponToCurrentPose();
    }

    private void ViewportView_Changed(object s, SelectionChangedEventArgs e)
    {
        if (base.IsLoaded)
        {
            (_yaw, _pitch) = ViewCombo.SelectedIndex switch
            {
                1 => (0.0, 0.0),
                2 => (Math.PI, 0.0),
                3 => (-Math.PI / 2.0, 0.0),
                4 => (Math.PI / 2.0, 0.0),
                5 => (0.0, 1.5697963267948967),
                6 => (0.0, -1.5697963267948967),
                _ => (-0.38, 0.35),
            };
            QueueCameraUpdate();
        }
    }

    private void RenderMode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (base.IsLoaded)
        {
            _renderMode = RenderModeCombo.SelectedIndex;
            ApplyRenderMode();
        }
    }

    private void ViewportOption_Changed(object s, RoutedEventArgs e)
    {
        _showGrid = GridCheck.IsChecked == true;
        _showAxes = AxesCheck.IsChecked == true;
        if ((object)_model != null)
        {
            RenderModel();
            if ((object)_clip != null)
            {
                RenderAnimatedSkeleton((float)_playbackTime);
            }
        }
    }

    private void Viewport_SizeChanged(object s, SizeChangedEventArgs e)
    {
        QueueCameraUpdate();
        if (_renderMode == 2)
        {
            WireframeLayer.RequestRender();
            ComparisonWireframeLayer.RequestRender();
        }
    }

    private void Viewport_MouseWheel(object s, MouseWheelEventArgs e)
    {
        bool flag = s == ComparisonViewportInput;
        if ((flag ? ComparisonCameraModeCombo.SelectedIndex : CameraModeCombo.SelectedIndex) != 0)
        {
            return;
        }
        if (e.Delta != 0)
        {
            if (flag)
            {
                _comparisonZoom = Math.Clamp(_comparisonZoom * Math.Exp((e.Delta > 0) ? 0.12 : (-0.12)), 0.15, 20.0);
            }
            else
            {
                _zoom = Math.Clamp(_zoom * Math.Exp((e.Delta > 0) ? 0.12 : (-0.12)), 0.15, 20.0);
            }
        }
        QueueCameraUpdate();
        e.Handled = true;
    }

    private void Viewport_MouseDown(object s, MouseButtonEventArgs e)
    {
        bool flag = s == ComparisonViewportInput;
        if ((flag ? ComparisonCameraModeCombo.SelectedIndex : CameraModeCombo.SelectedIndex) == 0)
        {
            bool flag2 = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            bool flag3 = ViewportControlCombo.SelectedIndex == 1;
            _orbiting = flag3 ? e.ChangedButton == MouseButton.Left : flag2 && e.ChangedButton == MouseButton.Left;
            _panning = flag3 ? e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle : flag2 && e.ChangedButton == MouseButton.Middle;
            _dollying = !flag3 && flag2 && e.ChangedButton == MouseButton.Right;
            if (_orbiting || _panning || _dollying)
            {
                Border border = (flag ? ComparisonViewportInput : ViewportInput);
                _dragViewportRight = flag;
                _dragging = true;
                _dragStart = e.GetPosition(border);
                border.Focus();
                border.CaptureMouse();
                e.Handled = true;
            }
        }
    }

    private void Viewport_MouseUp(object s, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _orbiting = false;
            _panning = false;
            _dollying = false;
            ((s == ComparisonViewportInput) ? ComparisonViewportInput : ViewportInput).ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void Viewport_MouseMove(object s, MouseEventArgs e)
    {
        bool flag = s == ComparisonViewportInput;
        if (!_dragging || _dragViewportRight != flag || (flag ? ComparisonCameraModeCombo.SelectedIndex : CameraModeCombo.SelectedIndex) != 0)
        {
            return;
        }
        Border relativeTo = (flag ? ComparisonViewportInput : ViewportInput);
        PerspectiveCamera perspectiveCamera = (flag ? ComparisonCamera : ViewportCamera);
        Point position = e.GetPosition(relativeTo);
        double num = position.X - _dragStart.X;
        double num2 = position.Y - _dragStart.Y;
        _dragStart = position;
        if (_orbiting)
        {
            if (flag)
            {
                _comparisonYaw += num * 0.01;
                _comparisonPitch = Math.Clamp(_comparisonPitch + num2 * 0.01, -1.56, 1.56);
            }
            else
            {
                _yaw += num * 0.01;
                _pitch = Math.Clamp(_pitch + num2 * 0.01, -1.56, 1.56);
            }
        }
        else if (_panning)
        {
            Vector3D lookDirection = perspectiveCamera.LookDirection;
            if (lookDirection.LengthSquared < 1E-08)
            {
                return;
            }
            lookDirection.Normalize();
            Vector3D upDirection = perspectiveCamera.UpDirection;
            upDirection.Normalize();
            Vector3D vector3D = Vector3D.CrossProduct(lookDirection, upDirection);
            if (vector3D.LengthSquared < 1E-08)
            {
                return;
            }
            vector3D.Normalize();
            upDirection = Vector3D.CrossProduct(vector3D, lookDirection);
            upDirection.Normalize();
            double num3 = Math.Max(0.05, perspectiveCamera.LookDirection.Length) * 0.0018;
            if (flag)
            {
                _comparisonCameraTarget += (-vector3D * num + upDirection * num2) * num3;
            }
            else
            {
                _cameraTarget += (-vector3D * num + upDirection * num2) * num3;
            }
        }
        else if (_dollying)
        {
            if (flag)
            {
                _comparisonZoom = Math.Clamp(_comparisonZoom * Math.Exp((0.0 - num) * 0.01), 0.15, 20.0);
            }
            else
            {
                _zoom = Math.Clamp(_zoom * Math.Exp((0.0 - num) * 0.01), 0.15, 20.0);
            }
        }
        QueueCameraUpdate();
        e.Handled = true;
    }

    private void ViewportInput_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.F)
        {
            ResetView();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && PlayButton.IsEnabled)
        {
            PlayAnimation_Click(PlayButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void PlayAnimation_Click(object s, RoutedEventArgs e)
    {
        if ((object)_clip == null)
        {
            return;
        }
        if (_animationTimer.IsEnabled)
        {
            StopAnimationPlayback();
            return;
        }
        if (_playbackTime >= GetPlaybackDuration())
        {
            SeekPlayback(0.0);
        }
        _lastTick = DateTime.UtcNow;
        ResetPlaybackFps(playing: true);
        _animationTimer.Start();
        PlayButton.Content = "⏸";
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        DateTime utcNow = DateTime.UtcNow;
        double totalSeconds = (utcNow - _lastTick).TotalSeconds;
        _lastTick = utcNow;
        double num = _playbackTime + totalSeconds;
        if (num >= GetPlaybackDuration())
        {
            num = 0.0;
            ResetCameraTrajectoryProgress();
        }
        SeekPlayback(num);
    }

    private void ResetCameraTrajectoryProgress()
    {
        _cameraTrajectoryDrawnFrame = -1;
        _cameraTrajectory.Children.Clear();
        _comparisonCameraTrajectory.Children.Clear();
        _cameraTrajectoryCursor.Children.Clear();
        _comparisonCameraTrajectoryCursor.Children.Clear();
    }

    private void AnimationSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (base.IsLoaded && !_sliderInternal && (object)_clip != null && (object)_model != null)
        {
            SeekPlayback(e.NewValue);
        }
    }

    private void AnimationSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { IsEnabled: true, ActualWidth: > 0 } slider)
        {
            StopAnimationPlayback();
            _timelineDragging = true;
            slider.CaptureMouse();
            SeekSliderFromPointer(slider, e.GetPosition(slider));
            e.Handled = true;
        }
    }

    private void AnimationSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_timelineDragging && e.LeftButton == MouseButtonState.Pressed && sender is Slider slider)
        {
            SeekSliderFromPointer(slider, e.GetPosition(slider));
            e.Handled = true;
        }
    }

    private void AnimationSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_timelineDragging && sender is Slider slider)
        {
            SeekSliderFromPointer(slider, e.GetPosition(slider));
            _timelineDragging = false;
            slider.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void AnimationSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _timelineDragging = false;
    }

    private void SeekSliderFromPointer(Slider slider, Point point)
    {
        Track? track = FindVisualChild<Track>(slider);
        if (track is not null && track.ActualWidth > 0.0)
        {
            Point trackPoint = slider.TranslatePoint(point, track);
            double value = track.ValueFromPoint(trackPoint);
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
            return;
        }
        double ratio = Math.Clamp(point.X / Math.Max(1.0, slider.ActualWidth), 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private void SetCameraKeyframes(ActionCameraClip? camera)
    {
        _cameraKeyTimes = (((object)camera == null) ? Array.Empty<float>() : (from x in ActionCameraCatalog.KeyframeTimes(camera).Where(float.IsFinite).Distinct()
                                                                              orderby x
                                                                              select x).ToArray());
        string text = NormalizeResourceId(_animationAsset?.ResourceId ?? _animationAsset?.HashId ?? "");
        _configurationEventTimes = ((!string.IsNullOrWhiteSpace(text) && _authoredCameraEvents.TryGetValue(text, out ConfiguredCameraCatalog.AuthoredActionConfiguration value)) ? (from x in value.Events.SelectMany((ConfiguredCameraCatalog.AuthoredActionEvent x) => (x.PacketFrames.Count <= 0) ? new[] { x.StartFrame / 60f } : x.PacketFrames.Select((float frame) => frame / 60f)).Where(float.IsFinite).Distinct()
                                                                                                                                                                                    orderby x
                                                                                                                                                                                    select x).ToArray() : Array.Empty<float>());
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T result)
            {
                return result;
            }
            T val = FindVisualChild<T>(child);
            if (val != null)
            {
                return val;
            }
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T result)
            {
                return result;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void Filter_Changed(object s, EventArgs e)
    {
        ApplyFilter();
    }

    private async void Rescan_Click(object s, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    private void UpdateGameRootDisplay()
    {
        if (string.IsNullOrWhiteSpace(_root))
        {
            GameRootText.Text = "尚未选择游戏目录";
            GameRootText.ToolTip = null;
        }
        else
        {
            GameRootText.Text = new DirectoryInfo(_root).Name;
            GameRootText.ToolTip = _root;
        }
    }

    private void OpenUserGuide_Click(object s, RoutedEventArgs e)
    {
        ShowUserGuide();
    }

    private void ShowUserGuide()
    {
        UserGuideWindow userGuideWindow = new UserGuideWindow();
        userGuideWindow.Owner = this;
        userGuideWindow.ShowDialog();
    }

    private async void OpenNameCatalog_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_root))
        {
            return;
        }
        if ((object)_nameCatalog == null)
        {
            base.IsEnabled = false;
            try
            {
                Progress<(int, int, string)> progress = new Progress<(int, int, string)>(delegate ((int Done, int Total, string File) p)
                {
                    StatusText.Text = $"正在恢复正式名称 {p.Done:N0}/{p.Total:N0} · {p.File}";
                });
                _nameCatalog = await KidsNameCatalogReader.BuildAsync(_root, progress);
                _nameCatalogRoot = _root;
                ApplyOfficialNames(_all, _nameCatalog);
                ApplyFilter();
                StatusText.Text = $"正式名称目录完成：{_nameCatalog.Entries.Count:N0} 个名称";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "正式名称恢复失败", MessageBoxButton.OK, MessageBoxImage.Hand);
                return;
            }
            finally
            {
                base.IsEnabled = true;
            }
        }
        NameCatalogWindow nameCatalogWindow = new NameCatalogWindow(_nameCatalog);
        nameCatalogWindow.Owner = this;
        nameCatalogWindow.ShowDialog();
    }

    private async void OpenRoot_Click(object s, RoutedEventArgs e)
    {
        OpenFolderDialog openFolderDialog = new OpenFolderDialog
        {
            Title = "选择 DWORIGINS 游戏目录或资源目录",
            InitialDirectory = (Directory.Exists(_root) ? _root : null)
        };
        if (openFolderDialog.ShowDialog(this) == true)
        {
            _root = openFolderDialog.FolderName;
            UpdateGameRootDisplay();
            RefreshBlenderStatus();
            UpdateResourceInitializationState();
            await ScanAsync();
        }
    }

    private async void ExportFiltered_Click(object s, RoutedEventArgs e)
    {
        if (_filtered.Count == 0)
        {
            return;
        }
        string destination = GetExportDirectory("Resources");
        base.IsEnabled = false;
        try
        {
            Progress<(int, int, string)> progress = new Progress<(int, int, string)>(delegate ((int Done, int Total, string Name) p)
            {
                StatusText.Text = $"正在导出 {p.Done:N0}/{p.Total:N0}：{p.Name}";
            });
            int value = await AssetExporter.ExportOrganizedAsync(_filtered, destination, progress);
            MessageBox.Show(this, $"已按类型与资源域导出 {value:N0} 个文件，并生成 manifest.json。\n\n目录：{destination}", "导出完成");
            StatusText.Text = "分类导出完成";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Hand);
        }
        finally
        {
            base.IsEnabled = true;
        }
    }

    private void ExportObj_Click(object s, RoutedEventArgs e)
    {
        if ((object)_model != null && _selected != null)
        {
            string fileName = System.IO.Path.ChangeExtension(_selected.ExportFileName, ".obj");
            string text = ConfirmFixedExportPath("Model", fileName);
            if (text != null)
            {
                AssetExporter.ExportObj(_model, text);
                StatusText.Text = "OBJ 导出完成：" + text;
            }
        }
    }

    private async void ExportModelFbx_Click(object s, RoutedEventArgs e)
    {
        if ((object)_model == null || _exporting || !EnsureBlenderReady())
        {
            return;
        }
        string fileName = System.IO.Path.ChangeExtension(_modelAsset?.ExportFileName ?? "DWOrigins_model.g1m", ".fbx");
        string destination = ConfirmFixedExportPath("Model", fileName);
        if (destination == null)
        {
            return;
        }
        string weaponName = System.IO.Path.GetFileNameWithoutExtension(_weaponAsset?.ExportFileName ?? "weapon");
        string weaponDestination = (((object)_weaponModel == null) ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination), System.IO.Path.GetFileNameWithoutExtension(fileName) + "_" + weaponName + "_weapon.fbx"));
        if (weaponDestination != null && File.Exists(weaponDestination) && MessageBox.Show(this, "武器模型导出文件已存在：\n" + weaponDestination + "\n\n是否一起覆盖？", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
        {
            return;
        }
        G1MInfo model = _model;
        G1MInfo weaponModel = _weaponModel;
        BeginExportProgress();
        try
        {
            Progress<(string Stage, int Percent)> modelProgress = new Progress<(string, int)>(delegate ((string Stage, int Percent) value)
            {
                ReportExportProgress((Stage: "角色模型 · " + value.Stage, Percent: ((object)weaponModel == null) ? value.Percent : (value.Percent * 60 / 100)));
            });
            Progress<(string Stage, int Percent)> weaponProgress = new Progress<(string, int)>(delegate ((string Stage, int Percent) value)
            {
                ReportExportProgress((Stage: "武器模型 · " + value.Stage, Percent: 60 + value.Percent * 40 / 100));
            });
            await Task.Run(delegate
            {
                BlenderFbxExporter.Export(model, null, destination, System.IO.Path.GetFileNameWithoutExtension(fileName), _root, includeGeometry: true, null, modelProgress);
                if ((object)weaponModel != null && weaponDestination != null)
                {
                    BlenderFbxExporter.Export(weaponModel, null, weaponDestination, weaponName, _root, includeGeometry: true, null, weaponProgress, cameraForUnrealSequencer: false, 1.5, null, weaponAttachmentOrientation: true);
                }
            });
            StatusText.Text = ((weaponDestination == null) ? ("角色模型 FBX 导出完成：" + destination) : ("角色模型与独立武器模型 FBX 导出完成：" + System.IO.Path.GetDirectoryName(destination)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FBX 导出失败", MessageBoxButton.OK, MessageBoxImage.Hand);
        }
        finally
        {
            EndExportProgress();
        }
    }

    private async void ExportAnimationFbx_Click(object s, RoutedEventArgs e)
    {
        if ((object)_model == null || (object)_clip == null || _exporting || !EnsureBlenderReady())
        {
            return;
        }
        string? fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(_modelAsset?.ExportFileName ?? "character");
        string animationName = System.IO.Path.GetFileNameWithoutExtension(_animationAsset?.ExportFileName ?? "animation");
        string fileName = fileNameWithoutExtension + "_" + animationName + ".fbx";
        string destination = ConfirmFixedExportPath("Animation", fileName);
        if (destination == null)
        {
            return;
        }
        BeginExportProgress();
        try
        {
            Progress<(string Stage, int Percent)> progress = new Progress<(string, int)>(ReportExportProgress);
            await Task.Run(delegate
            {
                BlenderFbxExporter.Export(_model, _clip, destination, animationName, _root, includeGeometry: false, null, progress);
            });
            StatusText.Text = "角色动画 FBX 导出完成：" + destination;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FBX 导出失败", MessageBoxButton.OK, MessageBoxImage.Hand);
        }
        finally
        {
            EndExportProgress();
        }
    }

    private async void ExportCameraFbx_Click(object s, RoutedEventArgs e)
    {
        if ((object)_actionCamera == null || (object)_model == null || (object)_clip == null || _exporting || !EnsureBlenderReady())
        {
            return;
        }
        string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(_animationAsset?.ExportFileName ?? "action");
        string fileName = fileNameWithoutExtension + "_camera.fbx";
        string destination = ConfirmFixedExportPath("Camera", fileName);
        if (destination == null)
        {
            return;
        }
        double exportAspectRatio = CameraViewportAspect;
        G1MInfo model = _model;
        G1AClip clip = _clip;
        ActionCameraClip camera = _actionCamera;
        BeginExportProgress();
        try
        {
            string reportPath = "";
            Progress<(string Stage, int Percent)> cameraProgress = new Progress<(string, int)>(delegate ((string Stage, int Percent) value)
            {
                ReportExportProgress((Stage: "镜头动画 · " + value.Stage, Percent: value.Percent));
            });
            await Task.Run(delegate
            {
                BlenderFbxExporter.Export(model, clip, destination, System.IO.Path.GetFileNameWithoutExtension(fileName), _root, includeGeometry: false, camera, cameraAspectRatio: exportAspectRatio, progress: cameraProgress, cameraForUnrealSequencer: true);
                reportPath = FbxExporter.ExportActionCameraReport(camera, destination, exportAspectRatio);
            });
            StatusText.Text = "镜头 FBX 与参数报告导出完成：" + destination + " · " + reportPath;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FBX 导出失败", MessageBoxButton.OK, MessageBoxImage.Hand);
        }
        finally
        {
            EndExportProgress();
        }
    }

    private string? ConfirmFixedExportPath(string category, string fileName)
    {
        string text = System.IO.Path.Combine(GetExportDirectory(category), fileName);
        if (!File.Exists(text))
        {
            return text;
        }
        if (MessageBox.Show(this, "导出文件已存在：\n" + text + "\n\n是否覆盖？", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
        {
            return null;
        }
        return text;
    }

    private static string GetExportDirectory(string category)
    {
        string text = System.IO.Path.Combine(AppContext.BaseDirectory, "DWOrigins_Exports", category);
        Directory.CreateDirectory(text);
        return text;
    }

    private void ExportSelected_Click(object s, RoutedEventArgs e)
    {
        if (_selected != null)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                FileName = _selected.ExportFileName,
                Filter = _selected.Extension + " 文件|*." + _selected.Extension.ToLowerInvariant() + "|所有文件|*.*"
            };
            if (saveFileDialog.ShowDialog(this) == true)
            {
                File.Copy(_selected.FullPath, saveFileDialog.FileName, overwrite: true);
            }
        }
    }

    private async void UnpackSelected_Click(object s, RoutedEventArgs e)
    {
        if (_selected != null && !(_selected.Extension != "FDATA"))
        {
            OpenFolderDialog openFolderDialog = new OpenFolderDialog
            {
                Title = "选择 FData 解包输出目录",
                InitialDirectory = GetExportDirectory("Unpacked")
            };
            if (openFolderDialog.ShowDialog(this) == true)
            {
                await UnpackPackagesAsync(new[] { _selected }, openFolderDialog.FolderName);
            }
        }
    }

    private async void UnpackFiltered_Click(object s, RoutedEventArgs e)
    {
        List<AssetEntry> list = _filtered.Where((AssetEntry x) => x.Kind == AssetKind.Package && x.Extension == "FDATA").ToList();
        if (list.Count == 0)
        {
            MessageBox.Show(this, "当前筛选结果中没有 FData 包。", "批量解包");
            return;
        }
        OpenFolderDialog openFolderDialog = new OpenFolderDialog
        {
            Title = $"选择 {list.Count:N0} 个 FData 包的解包输出目录",
            InitialDirectory = GetExportDirectory("Unpacked")
        };
        if (openFolderDialog.ShowDialog(this) == true)
        {
            await UnpackPackagesAsync(list, openFolderDialog.FolderName);
        }
    }

    private async Task UnpackPackagesAsync(IReadOnlyList<AssetEntry> packages, string destination)
    {
        base.IsEnabled = false;
        UpdateProgressPanelPlacement();
        ExportProgressPanel.Visibility = Visibility.Visible;
        ExportProgressBar.IsIndeterminate = true;
        ExportProgressBar.Value = 0.0;
        ExportProgressBar.Opacity = 1.0;
        ExportProgressPercentText.Text = "准备中";
        ExportProgressText.Text = "正在准备加载 PAK 资源";
        ExportProgressDetailText.Text = $"共 {packages.Count:N0} 个 FData 包";
        try
        {
            string expanded = System.IO.Path.Combine(_root, "File", "ASSET", "FDataPackage", "Dev", "fdata_out");
            Dictionary<uint, string> known = await Task.Run(() => FDataUnpacker.BuildKnownExtensionMap(expanded));
            Dictionary<uint, string> knownPaths = await Task.Run(() => FDataUnpacker.BuildKnownRelativePathMap(expanded));
            if ((object)_nameCatalog == null || _nameCatalogRoot != _root)
            {
                _nameCatalog = await KidsNameCatalogReader.BuildAsync(_root);
                _nameCatalogRoot = _root;
            }
            Dictionary<uint, FDataUnpacker.ResourceOutputName> outputNames = FDataUnpacker.BuildOutputNameMap(_nameCatalog, _root);
            FDataUnpacker.AddLogicalFallbackNames(outputNames, knownPaths);
            FDataUnpacker.AddUnresolvedFallbackNames(outputNames, from x in packages.SelectMany((AssetEntry x) => FDataUnpacker.ReadIndex(x.FullPath))
                                                                  select x.ResourceId);
            long bytes = 0L;
            int files = 0;
            int errors = 0;
            for (int p = 0; p < packages.Count; p++)
            {
                AssetEntry assetEntry = packages[p];
                int packageIndex = p;
                Progress<(int, int, string)> progress = new Progress<(int, int, string)>(delegate ((int Done, int Total, string Name) x)
                {
                    double packageFraction = x.Total > 0 ? Math.Clamp((double)x.Done / x.Total, 0.0, 1.0) : 0.0;
                    double overallPercent = (packageIndex + packageFraction) / packages.Count * 100.0;
                    ExportProgressBar.IsIndeterminate = x.Total <= 0;
                    ExportProgressBar.Value = overallPercent;
                    ExportProgressPercentText.Text = $"{overallPercent:0.0}%";
                    ExportProgressText.Text = $"正在加载 PAK · {packageIndex + 1}/{packages.Count}";
                    ExportProgressDetailText.Text = $"记录 {x.Done:N0}/{x.Total:N0} · {x.Name}";
                    StatusText.Text = $"包 {packageIndex + 1}/{packages.Count} · 记录 {x.Done}/{x.Total} · {x.Name}";
                });
                FDataExtractResult fDataExtractResult = await FDataUnpacker.ExtractAsync(assetEntry.FullPath, destination, known, progress, default(CancellationToken), outputNames, knownPaths);
                bytes += fDataExtractResult.OutputBytes;
                files += fDataExtractResult.ExtractedCount;
                errors += fDataExtractResult.Errors.Count;
            }
            ExportProgressBar.IsIndeterminate = false;
            ExportProgressBar.Value = 100.0;
            ExportProgressPercentText.Text = "100%";
            ExportProgressText.Text = "PAK 资源加载完成";
            ExportProgressDetailText.Text = $"{files:N0} 个资源 · {(double)bytes / 1073741824.0:0.00} GB · {errors:N0} 个错误";
            StatusText.Text = $"解包完成：{files:N0} 个资源，{(double)bytes / 1073741824.0:0.00} GB，{errors:N0} 个错误";
            MessageBox.Show(this, $"解包完成。\n资源：{files:N0}\n数据量：{(double)bytes / 1048576.0:0.00} MB\n错误：{errors:N0}\n\n每个包均已生成 manifest.json。", "FData 解包");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FData 解包失败", MessageBoxButton.OK, MessageBoxImage.Hand);
            StatusText.Text = "解包失败";
        }
        finally
        {
            base.IsEnabled = true;
            SetProgressIdle(StatusText.Text);
        }
    }

    private void ShowInExplorer_Click(object s, RoutedEventArgs e)
    {
        if (_selected != null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _selected.FullPath + "\"")
            {
                UseShellExecute = true
            });
        }
    }

    private void OpenExportsFolder_Click(object s, RoutedEventArgs e)
    {
        string exportDirectory = GetExportDirectory("");
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exportDirectory + "\"")
        {
            UseShellExecute = true
        });
    }

#if false
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.10.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/DWOriginsAssetTool;V1.0.3.0;component/mainwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.10.0")]
	internal Delegate _CreateDelegate(Type delegateType, string handler)
	{
		return Delegate.CreateDelegate(delegateType, this, handler);
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.10.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			StartupHeroCheck = (CheckBox)target;
			break;
		case 2:
			((Button)target).Click += OpenUserGuide_Click;
			break;
		case 3:
			((Button)target).Click += OpenNameCatalog_Click;
			break;
		case 4:
			CountText = (TextBlock)target;
			break;
		case 5:
			GameRootText = (TextBlock)target;
			break;
		case 6:
			FirstUseStepsText = (TextBlock)target;
			break;
		case 7:
			SelectGameRootButton = (Button)target;
			SelectGameRootButton.Click += OpenRoot_Click;
			break;
		case 8:
			((Button)target).Click += Rescan_Click;
			break;
		case 9:
			InitializationRequiredPanel = (Border)target;
			break;
		case 10:
			InitializationRequiredText = (TextBlock)target;
			break;
		case 11:
			InitializeResourcesButton = (Button)target;
			InitializeResourcesButton.Click += InitializeResources_Click;
			break;
		case 12:
			SearchBox = (TextBox)target;
			SearchBox.TextChanged += Filter_Changed;
			break;
		case 13:
			KindBox = (ComboBox)target;
			KindBox.SelectionChanged += Filter_Changed;
			break;
		case 14:
			DomainBox = (ComboBox)target;
			DomainBox.SelectionChanged += Filter_Changed;
			break;
		case 15:
			AssetTree = (TreeView)target;
			AssetTree.SelectedItemChanged += AssetTree_SelectedItemChanged;
			AssetTree.MouseDoubleClick += AssetTree_MouseDoubleClick;
			AssetTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(AssetTree_Expanded));
			break;
		case 16:
			ViewCombo = (ComboBox)target;
			ViewCombo.SelectionChanged += ViewportView_Changed;
			break;
		case 17:
			CameraCurveButton = (Button)target;
			CameraCurveButton.Click += CameraCurve_Click;
			break;
		case 18:
			RenderModeCombo = (ComboBox)target;
			RenderModeCombo.SelectionChanged += RenderMode_Changed;
			break;
		case 19:
			ViewportStats = (TextBlock)target;
			break;
		case 20:
			DualViewportCheck = (CheckBox)target;
			DualViewportCheck.Click += DualViewport_Changed;
			break;
		case 21:
			GridCheck = (CheckBox)target;
			GridCheck.Click += ViewportOption_Changed;
			break;
		case 22:
			AxesCheck = (CheckBox)target;
			AxesCheck.Click += ViewportOption_Changed;
			break;
		case 23:
			CameraGizmoCheck = (CheckBox)target;
			CameraGizmoCheck.Click += CameraGizmo_Changed;
			break;
		case 24:
			CameraTrajectoryCheck = (CheckBox)target;
			CameraTrajectoryCheck.Click += CameraTrajectory_Changed;
			break;
		case 25:
			CameraDataButton = (Button)target;
			CameraDataButton.Click += CameraData_Click;
			break;
		case 26:
			WeaponCheck = (CheckBox)target;
			WeaponCheck.Click += WeaponCheck_Changed;
			break;
		case 27:
			PreviewSummary = (TextBlock)target;
			break;
		case 28:
			PrimaryViewportColumn = (ColumnDefinition)target;
			break;
		case 29:
			ViewportSplitterColumn = (ColumnDefinition)target;
			break;
		case 30:
			ComparisonViewportColumn = (ColumnDefinition)target;
			break;
		case 31:
			Viewport = (Viewport3D)target;
			Viewport.SizeChanged += Viewport_SizeChanged;
			break;
		case 32:
			ViewportCamera = (PerspectiveCamera)target;
			break;
		case 33:
			WireframeLayer = (WireframeOverlay)target;
			break;
		case 34:
			ViewportOverlay = (Canvas)target;
			break;
		case 35:
			ViewportInput = (Border)target;
			ViewportInput.MouseWheel += Viewport_MouseWheel;
			ViewportInput.MouseMove += Viewport_MouseMove;
			ViewportInput.MouseDown += Viewport_MouseDown;
			ViewportInput.MouseUp += Viewport_MouseUp;
			ViewportInput.KeyDown += ViewportInput_KeyDown;
			break;
		case 36:
			PrimaryModePanel = (Border)target;
			break;
		case 37:
			CameraModeCombo = (ComboBox)target;
			CameraModeCombo.SelectionChanged += CameraMode_Changed;
			break;
		case 38:
			LinkedCameraModeItem = (ComboBoxItem)target;
			break;
		case 39:
			ConfiguredCameraModeItem = (ComboBoxItem)target;
			break;
		case 40:
			CompositeCameraModeItem = (ComboBoxItem)target;
			break;
		case 41:
			ComparisonViewportSplitter = (GridSplitter)target;
			break;
		case 42:
			ComparisonViewport = (Viewport3D)target;
			ComparisonViewport.SizeChanged += Viewport_SizeChanged;
			break;
		case 43:
			ComparisonCamera = (PerspectiveCamera)target;
			break;
		case 44:
			ComparisonWireframeLayer = (WireframeOverlay)target;
			break;
		case 45:
			ComparisonViewportInput = (Border)target;
			ComparisonViewportInput.MouseWheel += Viewport_MouseWheel;
			ComparisonViewportInput.MouseMove += Viewport_MouseMove;
			ComparisonViewportInput.MouseDown += Viewport_MouseDown;
			ComparisonViewportInput.MouseUp += Viewport_MouseUp;
			ComparisonViewportInput.KeyDown += ViewportInput_KeyDown;
			break;
		case 46:
			ComparisonHintPanel = (Border)target;
			break;
		case 47:
			ComparisonViewportHint = (TextBlock)target;
			break;
		case 48:
			ComparisonModePanel = (Border)target;
			break;
		case 49:
			ComparisonCameraModeCombo = (ComboBox)target;
			ComparisonCameraModeCombo.SelectionChanged += CameraMode_Changed;
			break;
		case 50:
			ComparisonActionCameraModeItem = (ComboBoxItem)target;
			break;
		case 51:
			ComparisonConfiguredCameraModeItem = (ComboBoxItem)target;
			break;
		case 52:
			ComparisonCompositeCameraModeItem = (ComboBoxItem)target;
			break;
		case 53:
			EmptyState = (StackPanel)target;
			break;
		case 54:
			ViewportHint = (TextBlock)target;
			break;
		case 55:
			CameraDataHud = (Border)target;
			break;
		case 56:
			CameraHudMode = (TextBlock)target;
			break;
		case 57:
			CameraHudText = (TextBlock)target;
			break;
		case 58:
			WorldAxisX = (Line)target;
			break;
		case 59:
			WorldAxisY = (Line)target;
			break;
		case 60:
			WorldAxisZ = (Line)target;
			break;
		case 61:
			WorldAxisXLabel = (TextBlock)target;
			break;
		case 62:
			WorldAxisYLabel = (TextBlock)target;
			break;
		case 63:
			WorldAxisZLabel = (TextBlock)target;
			break;
		case 64:
			ExportProgressPanel = (Border)target;
			break;
		case 65:
			ExportProgressDot = (Ellipse)target;
			break;
		case 66:
			ExportProgressText = (TextBlock)target;
			break;
		case 67:
			ExportProgressDetailText = (TextBlock)target;
			break;
		case 68:
			ExportProgressPercentText = (TextBlock)target;
			break;
		case 69:
			ExportProgressBar = (ProgressBar)target;
			break;
		case 70:
			PlaybackOverlay = (Border)target;
			break;
		case 71:
			PlayButton = (Button)target;
			PlayButton.Click += PlayAnimation_Click;
			break;
		case 72:
			AnimationSlider = (Slider)target;
			AnimationSlider.ValueChanged += AnimationSlider_ValueChanged;
			AnimationSlider.PreviewMouseLeftButtonDown += AnimationSlider_PreviewMouseLeftButtonDown;
			AnimationSlider.PreviewMouseMove += AnimationSlider_PreviewMouseMove;
			AnimationSlider.PreviewMouseLeftButtonUp += AnimationSlider_PreviewMouseLeftButtonUp;
			AnimationSlider.LostMouseCapture += AnimationSlider_LostMouseCapture;
			break;
		case 73:
			CameraKeyframeCanvas = (Canvas)target;
			break;
		case 74:
			SnapKeyframesCheck = (CheckBox)target;
			break;
		case 75:
			TimelineText = (TextBlock)target;
			break;
		case 76:
			PlaybackFpsText = (TextBlock)target;
			break;
		case 77:
			DetailTitle = (TextBlock)target;
			break;
		case 78:
			DetailPath = (TextBlock)target;
			break;
		case 79:
			QuickCharacterBox = (ComboBox)target;
			QuickCharacterBox.SelectionChanged += QuickCharacter_Changed;
			break;
		case 80:
			QuickWeaponBox = (ComboBox)target;
			QuickWeaponBox.SelectionChanged += QuickWeapon_Changed;
			break;
		case 81:
			QuickAnimationBox = (ComboBox)target;
			QuickAnimationBox.SelectionChanged += QuickAnimation_Changed;
			break;
		case 82:
			LoadQuickPreviewButton = (Button)target;
			LoadQuickPreviewButton.Click += LoadQuickPreview_Click;
			break;
		case 83:
			QuickPreviewHint = (TextBlock)target;
			break;
		case 84:
			QuickCameraActionBox = (ComboBox)target;
			QuickCameraActionBox.SelectionChanged += QuickCameraAction_Changed;
			break;
		case 85:
			CameraPreviewCharacterText = (TextBlock)target;
			break;
		case 86:
			CameraPreviewAnimationText = (TextBlock)target;
			break;
		case 87:
			CameraPreviewCameraText = (TextBlock)target;
			break;
		case 88:
			CameraPreviewHint = (TextBlock)target;
			break;
		case 89:
			ActionCameraCard = (Border)target;
			break;
		case 90:
			ActionCameraTitle = (TextBlock)target;
			break;
		case 91:
			ActionCameraStatus = (TextBlock)target;
			break;
		case 92:
			ActionCameraDetail = (TextBlock)target;
			break;
		case 93:
			DetailBody = (TextBlock)target;
			break;
		case 94:
			RelationBody = (TextBlock)target;
			break;
		case 95:
			BoneSummaryText = (TextBlock)target;
			break;
		case 96:
			BoneTree = (TreeView)target;
			break;
		case 97:
			AccessoryNameText = (TextBlock)target;
			break;
		case 98:
			((Button)target).Click += AddSelectedAccessory_Click;
			break;
		case 99:
			((Button)target).Click += RemoveAccessory_Click;
			break;
		case 100:
			((Button)target).Click += AutoFitAccessory_Click;
			break;
		case 101:
			AccessorySourceBoneCombo = (ComboBox)target;
			AccessorySourceBoneCombo.SelectionChanged += AccessoryBone_Changed;
			break;
		case 102:
			AccessoryTargetBoneCombo = (ComboBox)target;
			AccessoryTargetBoneCombo.SelectionChanged += AccessoryBone_Changed;
			break;
		case 103:
			AccessoryPosX = (TextBox)target;
			AccessoryPosX.TextChanged += AccessoryTransform_Changed;
			break;
		case 104:
			AccessoryPosY = (TextBox)target;
			AccessoryPosY.TextChanged += AccessoryTransform_Changed;
			break;
		case 105:
			AccessoryPosZ = (TextBox)target;
			AccessoryPosZ.TextChanged += AccessoryTransform_Changed;
			break;
		case 106:
			AccessoryRotX = (TextBox)target;
			AccessoryRotX.TextChanged += AccessoryTransform_Changed;
			break;
		case 107:
			AccessoryRotY = (TextBox)target;
			AccessoryRotY.TextChanged += AccessoryTransform_Changed;
			break;
		case 108:
			AccessoryRotZ = (TextBox)target;
			AccessoryRotZ.TextChanged += AccessoryTransform_Changed;
			break;
		case 109:
			AccessoryScale = (TextBox)target;
			AccessoryScale.TextChanged += AccessoryTransform_Changed;
			break;
		case 110:
			AccessoryMappingText = (TextBlock)target;
			break;
		case 111:
			BlenderStatusDot = (Ellipse)target;
			break;
		case 112:
			BlenderStatusText = (TextBlock)target;
			break;
		case 113:
			BlenderPathText = (TextBlock)target;
			break;
		case 114:
			BlenderLocationButton = (Button)target;
			BlenderLocationButton.Click += BlenderLocation_Click;
			break;
		case 115:
			BlenderSteamButton = (Button)target;
			BlenderSteamButton.Click += OpenBlenderSteam_Click;
			break;
		case 116:
			((Button)target).Click += OpenBlenderWebsite_Click;
			break;
		case 117:
			ExportModelFbxButton = (Button)target;
			ExportModelFbxButton.Click += ExportModelFbx_Click;
			break;
		case 118:
			ExportAnimationFbxButton = (Button)target;
			ExportAnimationFbxButton.Click += ExportAnimationFbx_Click;
			break;
		case 119:
			ExportCameraFbxButton = (Button)target;
			ExportCameraFbxButton.Click += ExportCameraFbx_Click;
			break;
		case 120:
			((Button)target).Click += OpenExportsFolder_Click;
			break;
		case 121:
			StatusText = (TextBlock)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
#endif
}
