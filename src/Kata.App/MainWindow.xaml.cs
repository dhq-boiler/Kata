using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Rendering;
using Kata.App.CodeViewer;
using Kata.App.Diagnostics;
using Kata.App.Dialogs;
using Kata.App.Graph;
using Kata.App.Localization;
using Kata.App.Services;
using Kata.App.ViewModels;
using Kata.App.Views;
using Kata.Core.Diff;
using Kata.Core.Model;
using Microsoft.Win32;

namespace Kata.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        DarkCSharpTheme.ApplyOnce();
        InitializeComponent();
        DataContext = _viewModel;
        RenameSelectedCommand = new AsyncRelayCommand(RenameSelectedAsync);
        ExtractInterfaceSelectedCommand = new AsyncRelayCommand(ExtractInterfaceSelectedAsync);
        ExtractSuperclassSelectedCommand = new AsyncRelayCommand(ExtractSuperclassSelectedAsync);
        ExtractClassSelectedCommand = new AsyncRelayCommand(ExtractClassSelectedAsync);
        AddGhostTypeCommand = new AsyncRelayCommand(AddGhostTypeAsync);
        OpenSolutionCommand = new AsyncRelayCommand(OpenSolutionAsync);
        Loaded += OnLoaded;
        Editor.SelectionChanged += OnEditorSelectionChanged;
        CodeEditor.TextArea.SelectionChanged += OnCodeEditorSelectionChanged;
        CodeEditor.PreviewMouseLeftButtonDown += OnCodeEditorLeftDown;
        CodeEditor.MouseMove += OnCodeEditorMouseMove;
        CodeEditor.MouseLeave += OnCodeEditorMouseLeave;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;
        _hoverRenderer = new HoverBackgroundRenderer(new SolidColorBrush(Color.FromArgb(0x60, 0x56, 0x9c, 0xd6)));
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_hoverRenderer);
        _navFlashRenderer = new HoverBackgroundRenderer(new SolidColorBrush(Color.FromArgb(NavFlashPeakAlpha, 0xff, 0xd7, 0x00)));
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_navFlashRenderer);
        // 重複コード smell が付いているメンバー body を薄い赤で強調表示する。
        // ARGB: A=0x30 で薄く、色は少しトマトっぽい赤 (0xff5a5a) で背景に埋もれない。
        _duplicateRenderer = new HoverBackgroundRenderer(new SolidColorBrush(Color.FromArgb(0x30, 0xff, 0x5a, 0x5a)));
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_duplicateRenderer);
        _navFlashTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30),
        };
        _navFlashTimer.Tick += OnNavFlashTimerTick;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            _viewModel.LayoutChanged -= OnLayoutChanged;
            Editor.SelectionChanged -= OnEditorSelectionChanged;
            CodeEditor.TextArea.SelectionChanged -= OnCodeEditorSelectionChanged;
            CodeEditor.PreviewMouseLeftButtonDown -= OnCodeEditorLeftDown;
            CodeEditor.MouseMove -= OnCodeEditorMouseMove;
            CodeEditor.MouseLeave -= OnCodeEditorMouseLeave;
            PreviewKeyDown -= OnWindowPreviewKeyDown;
            PreviewKeyUp -= OnWindowPreviewKeyUp;
            CodeEditor.TextArea.TextView.BackgroundRenderers.Remove(_hoverRenderer);
            CodeEditor.TextArea.TextView.BackgroundRenderers.Remove(_navFlashRenderer);
            CodeEditor.TextArea.TextView.BackgroundRenderers.Remove(_duplicateRenderer);
            _navFlashTimer.Stop();
            _navFlashTimer.Tick -= OnNavFlashTimerTick;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        };
        _viewModel.LayoutChanged += OnLayoutChanged;
        TrackpadPanZoom.EnableWheelPan(Editor);
        TrackpadPanZoom.EnableHorizontalWheelPan(this, Editor);
    }

    private GridLength _rememberedCodeViewerWidth = new(500, GridUnitType.Pixel);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentMemberSource))
        {
            var mvm = _viewModel.CurrentMemberSource;
            CodeEditor.Text = mvm?.SourceText ?? string.Empty;
            SelectionInfoText.Text = string.Empty;

            // When SourceText is a full file (Cpp fallback path), MemberSpanStart is a
            // valid offset inside SourceText — scroll the editor there. For the C# path
            // SourceText is a member-only excerpt so MemberSpanStart (absolute in file)
            // exceeds SourceText.Length and the guard skips scrolling.
            if (mvm is not null
                && mvm.Source.MemberSpanStart >= 0
                && mvm.Source.MemberSpanStart < CodeEditor.Document.TextLength)
            {
                var offset = mvm.Source.MemberSpanStart;
                var length = mvm.Source.MemberSpanLength;
                CodeEditor.CaretOffset = offset;
                var line = CodeEditor.Document.GetLineByOffset(offset).LineNumber;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CodeEditor.ScrollToLine(line);
                    FlashNavigationTarget(offset, length);
                }), DispatcherPriority.Loaded);
            }

            RefreshDuplicateHighlight();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsCodeViewerVisible))
        {
            if (_viewModel.IsCodeViewerVisible)
            {
                CodeViewerColumn.MinWidth = 240;
                CodeViewerColumn.Width = _rememberedCodeViewerWidth.Value > 0
                    ? _rememberedCodeViewerWidth
                    : new GridLength(500, GridUnitType.Pixel);
            }
            else
            {
                if (CodeViewerColumn.ActualWidth > 0)
                {
                    _rememberedCodeViewerWidth = new GridLength(CodeViewerColumn.ActualWidth, GridUnitType.Pixel);
                }
                CodeViewerColumn.MinWidth = 0;
                CodeViewerColumn.Width = new GridLength(0);
            }
        }
    }

    private void OnCodeEditorSelectionChanged(object? sender, EventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null)
        {
            SelectionInfoText.Text = string.Empty;
            return;
        }

        var area = CodeEditor.TextArea;
        var start = area.Selection.SurroundingSegment?.Offset ?? area.Caret.Offset;
        var length = area.Selection.Length;
        mvm.SelectionStart = start;
        mvm.SelectionLength = length;
        mvm.SelectedText = length > 0 ? CodeEditor.Document.GetText(start, length) : string.Empty;

        SelectionInfoText.Text = length == 0
            ? $"cursor @ {area.Caret.Offset}"
            : $"{length} chars ({start}..{start + length})";
    }

    private void OnEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.FocusedNode = Editor.SelectedItem as TypeNodeViewModel;
    }

    private void OnLayoutChanged()
    {
        Dispatcher.BeginInvoke(new Action(FitToContent), DispatcherPriority.ApplicationIdle);
    }

    private const double DefaultLargeGraphZoom = 0.6;
    private const int LargeGraphThreshold = 80;

    private void FitToContent()
    {
        var nodes = _viewModel.Nodes;
        if (nodes.Count == 0)
        {
            return;
        }

        if (nodes.Count <= LargeGraphThreshold)
        {
            Editor.FitToScreen();
            return;
        }

        // Center on the MEDIAN of user-type positions rather than the bbox
        // center. Sugiyama scatters unrelated components across a very wide row
        // (対象コードベース: 461 types, 167k px wide), so bbox center lands in empty
        // horizontal space between clusters. Median tracks where the mass of
        // nodes actually is, and is robust to a handful of far-flung stubs.
        var xs = new List<double>(nodes.Count);
        var ys = new List<double>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node.IsExternal) continue;
            xs.Add(node.Location.X + node.Size.Width / 2);
            ys.Add(node.Location.Y + node.Size.Height / 2);
        }
        if (xs.Count == 0)
        {
            foreach (var node in nodes)
            {
                xs.Add(node.Location.X + node.Size.Width / 2);
                ys.Add(node.Location.Y + node.Size.Height / 2);
            }
        }
        xs.Sort();
        ys.Sort();
        var centerX = xs[xs.Count / 2];
        var centerY = ys[ys.Count / 2];

        // ViewportSize updates asynchronously after ViewportZoom changes (Nodify
        // recomputes it during its next layout pass), so reading it right after
        // the zoom assignment gives the OLD size — center comes out wrong and
        // the viewport lands nowhere near the nodes. Compute the post-zoom
        // viewport ourselves from the editor's pixel size / zoom.
        var zoom = DefaultLargeGraphZoom;
        Editor.ViewportZoom = zoom;
        var vpW = Editor.ActualWidth > 0 ? Editor.ActualWidth / zoom : 800;
        var vpH = Editor.ActualHeight > 0 ? Editor.ActualHeight / zoom : 600;
        Editor.ViewportLocation = new Point(centerX - vpW / 2, centerY - vpH / 2);
    }

    public AsyncRelayCommand RenameSelectedCommand { get; }
    public AsyncRelayCommand ExtractInterfaceSelectedCommand { get; }
    public AsyncRelayCommand ExtractSuperclassSelectedCommand { get; }
    public AsyncRelayCommand ExtractClassSelectedCommand { get; }
    public AsyncRelayCommand AddGhostTypeCommand { get; }
    public AsyncRelayCommand OpenSolutionCommand { get; }

    private async void OnOpenSolutionClick(object sender, RoutedEventArgs e)
    {
        await OpenSolutionAsync();
    }

    private void OnPreferencesMenuClick(object sender, RoutedEventArgs e)
    {
        var vm = new PreferencesViewModel(App.SettingsStore, App.LanguageService, App.LicenseStore, App.ProFeatures);
        var window = new PreferencesWindow(vm) { Owner = this };
        window.ShowDialog();
    }

    private async Task OpenSolutionAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Solution",
            Filter = "Solution files (*.slnx;*.sln)|*.slnx;*.sln|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        var current = _viewModel.CurrentSolutionPath;
        if (!string.IsNullOrEmpty(current))
        {
            var dir = System.IO.Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                dialog.InitialDirectory = dir;
            }
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await _viewModel.LoadSolutionAsync(dialog.FileName);
    }

    private async Task AddGhostTypeAsync()
    {
        var seed = Editor.SelectedItem as TypeNodeViewModel
                   ?? Editor.SelectedItems?.Cast<object>().OfType<TypeNodeViewModel>().FirstOrDefault()
                   ?? _viewModel.Nodes.FirstOrDefault(n => !n.IsExternal && !n.IsGhost);
        var initialNs = seed?.Namespace.FullName;

        var dialog = new AddGhostTypeDialog(initialNs) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        const string rationale = "User-initiated ghost-type addition via canvas";
        var changeSet = await _viewModel.ProposeAddGhostTypeAsync(
            typeName: dialog.TypeName,
            @namespace: new NamespaceRef(dialog.NamespaceName),
            kind: dialog.Kind,
            rationale: rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Add-ghost-type");
    }

    private async Task ExtractInterfaceSelectedAsync()
    {
        var node = Editor.SelectedItem as TypeNodeViewModel
                   ?? Editor.SelectedItems?.Cast<object>().OfType<TypeNodeViewModel>().FirstOrDefault()
                   ?? _viewModel.Nodes.FirstOrDefault(n => !n.IsExternal && !n.IsGhost);
        if (node is null)
        {
            _viewModel.Status = Strings.Status_NoTypeNodeExtract;
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantExtractInterface;
            return;
        }

        await RunExtractInterfaceFlowAsync(node);
    }

    private async Task ExtractSuperclassSelectedAsync()
    {
        var node = Editor.SelectedItem as TypeNodeViewModel
                   ?? Editor.SelectedItems?.Cast<object>().OfType<TypeNodeViewModel>().FirstOrDefault()
                   ?? _viewModel.Nodes.FirstOrDefault(n => !n.IsExternal && !n.IsGhost);
        if (node is null)
        {
            _viewModel.Status = Strings.Status_NoTypeNodeExtractSuperclass;
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantExtractSuperclass;
            return;
        }

        await RunExtractSuperclassFlowAsync(node);
    }

    private async Task ExtractClassSelectedAsync()
    {
        var node = Editor.SelectedItem as TypeNodeViewModel
                   ?? Editor.SelectedItems?.Cast<object>().OfType<TypeNodeViewModel>().FirstOrDefault()
                   ?? _viewModel.Nodes.FirstOrDefault(n => !n.IsExternal && !n.IsGhost);
        if (node is null)
        {
            _viewModel.Status = Strings.Status_NoTypeNodeExtractClass;
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantExtractClass;
            return;
        }

        await RunExtractClassFlowAsync(node);
    }

    private async Task RenameSelectedAsync()
    {
        var node = Editor.SelectedItem as TypeNodeViewModel
                   ?? Editor.SelectedItems?.Cast<object>().OfType<TypeNodeViewModel>().FirstOrDefault()
                   ?? _viewModel.Nodes.FirstOrDefault();
        if (node is null)
        {
            _viewModel.Status = Strings.Status_NoTypeNodeRename;
            return;
        }

        await RunRenameFlowAsync(node);
    }

    private async Task RunRenameFlowAsync(TypeNodeViewModel node)
    {
        var rename = new RenameDialog(node.Name) { Owner = this };
        if (rename.ShowDialog() != true)
        {
            return;
        }

        if (string.Equals(rename.NewName, node.Name, StringComparison.Ordinal))
        {
            _viewModel.Status = Strings.Status_NameUnchanged;
            return;
        }

        const string rationale = "User-initiated rename via canvas";
        var changeSet = await _viewModel.ProposeRenameAsync(node, rename.NewName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Rename");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var slnPath = App.RequestedSolutionPath;
        if (string.IsNullOrEmpty(slnPath))
        {
            _viewModel.Status = Strings.Status_NoSolutionLoadedHint;
            return;
        }

        await _viewModel.LoadSolutionAsync(slnPath);
    }

    private void OnNodeBorderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.AddHandler(
                UIElement.PreviewMouseRightButtonDownEvent,
                new MouseButtonEventHandler(OnNodePreviewRightDown),
                handledEventsToo: true);
            fe.AddHandler(
                UIElement.PreviewMouseRightButtonUpEvent,
                new MouseButtonEventHandler(OnNodePreviewRightClick),
                handledEventsToo: true);
        }
    }

    private const double RightClickDragThreshold = 4.0;
    private Point _rightDownScreenPoint;
    private FrameworkElement? _rightDownElement;

    private void OnNodePreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }
        _rightDownElement = fe;
        _rightDownScreenPoint = e.GetPosition(this);
    }

    private DispatcherOperation? _pendingClusterRebuild;
    private DispatcherOperation? _pendingEdgeResnap;
    private readonly HashSet<TypeNodeViewModel> _resizedNodes = new();

    private void OnEditorPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(Editor);
        NamespaceClusterViewModel? hitCluster = null;
        VisualTreeHelper.HitTest(
            Editor,
            null,
            result =>
            {
                for (DependencyObject? d = result.VisualHit; d is not null; d = VisualTreeHelper.GetParent(d))
                {
                    if (d is FrameworkElement { Tag: "NsLabel", DataContext: NamespaceClusterViewModel cluster })
                    {
                        hitCluster = cluster;
                        return HitTestResultBehavior.Stop;
                    }
                }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(pos));

        if (hitCluster is not null)
        {
            _viewModel.SelectedNamespace = hitCluster.Namespace;
            e.Handled = true;
        }
    }

    private void OnNodeBorderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TypeNodeViewModel vm)
        {
            return;
        }

        var actual = new Size(fe.ActualWidth, fe.ActualHeight);
        if (Math.Abs(vm.Size.Width - actual.Width) < 0.5 &&
            Math.Abs(vm.Size.Height - actual.Height) < 0.5)
        {
            return;
        }

        vm.Size = actual;
        _resizedNodes.Add(vm);

        _pendingClusterRebuild?.Abort();
        _pendingClusterRebuild = Dispatcher.BeginInvoke(
            new Action(_viewModel.RebuildClusters),
            DispatcherPriority.Background);

        _pendingEdgeResnap?.Abort();
        _pendingEdgeResnap = Dispatcher.BeginInvoke(
            new Action(ResnapEdgesForResizedNodes),
            DispatcherPriority.Background);
    }

    // Layout time uses TypeNodeViewModel.EstimateSize(), which under-counts
    // member row height (~14 px estimate vs. ~19 px actual). MSAGL therefore
    // terminates edges at the estimated node boundary — which lands inside
    // the taller, rendered class. We resize the connections to the actual
    // rectangle in an idempotent rebuild once WPF settles on true sizes.
    private void ResnapEdgesForResizedNodes()
    {
        if (_resizedNodes.Count == 0) return;
        foreach (var conn in _viewModel.Connections)
        {
            if (_resizedNodes.Contains(conn.SourceNode) || _resizedNodes.Contains(conn.TargetNode))
            {
                SugiyamaLayout.RebuildFromRoute(conn);
            }
        }
        _resizedNodes.Clear();
    }

    private void OnNodePreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is null)
        {
            return;
        }

        // Suppress: this up did not follow a down on the same element (Nodify pan may have swallowed the down).
        if (!ReferenceEquals(fe, _rightDownElement))
        {
            return;
        }

        // Suppress: mouse moved far enough between down and up — treat as pan, not click.
        var upPoint = e.GetPosition(this);
        var dx = upPoint.X - _rightDownScreenPoint.X;
        var dy = upPoint.Y - _rightDownScreenPoint.Y;
        _rightDownElement = null;
        if (dx * dx + dy * dy > RightClickDragThreshold * RightClickDragThreshold)
        {
            return;
        }

        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.Placement = PlacementMode.MousePoint;
        fe.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private async void OnRenameMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: TypeNodeViewModel node })
        {
            await RunRenameFlowAsync(node);
        }
    }

    private async void OnExtractInterfaceMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node })
        {
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantExtractInterface;
            return;
        }

        await RunExtractInterfaceFlowAsync(node);
    }

    private async Task RunExtractInterfaceFlowAsync(TypeNodeViewModel node)
    {
        var dialog = new ExtractInterfaceDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var members = dialog.SelectedMembers.Select(m => m.Ref).ToArray();
        var rationale = $"Extracted from {node.Ref.FullyQualifiedName} via canvas";

        var changeSet = await _viewModel.ProposeExtractInterfaceAsync(node, members, dialog.InterfaceName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Extract-interface");
    }

    private async void OnExtractSuperclassMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node })
        {
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantExtractSuperclass;
            return;
        }

        await RunExtractSuperclassFlowAsync(node);
    }

    private async Task RunExtractSuperclassFlowAsync(TypeNodeViewModel node)
    {
        var dialog = new ExtractSuperclassDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var members = dialog.SelectedMembers.Select(m => m.Ref).ToArray();
        var rationale = $"Extracted superclass from {node.Ref.FullyQualifiedName} via canvas";

        var changeSet = await _viewModel.ProposeExtractSuperclassAsync(node, members, dialog.SuperclassName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Extract-superclass");
    }

    private async void OnExtractClassMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node })
        {
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantExtractClass;
            return;
        }

        await RunExtractClassFlowAsync(node);
    }

    private async Task RunExtractClassFlowAsync(TypeNodeViewModel node)
    {
        var dialog = new ExtractClassDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var members = dialog.SelectedMembers.Select(m => m.Ref).ToArray();
        var rationale = $"Extracted class from {node.Ref.FullyQualifiedName} via canvas";

        var changeSet = await _viewModel.ProposeExtractClassAsync(
            node, members, dialog.NewClassName, dialog.DelegatePropertyName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Extract-class");
    }

    private async void OnCollapseHierarchyMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node })
        {
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantCollapse;
            return;
        }

        var parentRef = _viewModel.TryFindReplacementBase(node);
        if (parentRef is null)
        {
            _viewModel.Status = string.Format(Strings.Status_NoBaseToCollapseInto, node.Name);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Confirm_CollapseHierarchy_Body, node.Name, parentRef.Value.FullyQualifiedName),
            Strings.Confirm_CollapseHierarchy_Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var rationale = $"Collapsed {node.Ref.FullyQualifiedName} into {parentRef.Value.FullyQualifiedName}";
        var changeSet = await _viewModel.ProposeCollapseHierarchyAsync(node, parentRef.Value, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Collapse-hierarchy");
    }

    private async void OnRemoveSubclassMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node })
        {
            return;
        }

        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantRemoveSubclass;
            return;
        }

        var baseRef = _viewModel.TryFindReplacementBase(node);
        if (baseRef is null)
        {
            _viewModel.Status = string.Format(Strings.Status_NoBaseForRemoveSubclass, node.Name);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Confirm_RemoveSubclass_Body, node.Name, baseRef.Value.FullyQualifiedName),
            Strings.Confirm_RemoveSubclass_Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var rationale = $"Removed subclass {node.Ref.FullyQualifiedName}, replaced with {baseRef.Value.FullyQualifiedName}";
        var changeSet = await _viewModel.ProposeRemoveSubclassAsync(node, baseRef.Value, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Remove-subclass");
    }

    private async void OnPullUpMethodMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantPullUpMethods;
            return;
        }
        await RunPullUpMemberFlowAsync(node, PullUpMemberDialog.MemberFilter.Methods);
    }

    private async void OnPullUpFieldMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantPullUpFields;
            return;
        }
        await RunPullUpMemberFlowAsync(node, PullUpMemberDialog.MemberFilter.Fields);
    }

    private async Task RunPullUpMemberFlowAsync(TypeNodeViewModel node, PullUpMemberDialog.MemberFilter filter)
    {
        var parentRef = _viewModel.TryFindReplacementBase(node);
        if (parentRef is null)
        {
            _viewModel.Status = string.Format(Strings.Status_NoBaseToPullUp, node.Name);
            return;
        }

        var dialog = new PullUpMemberDialog(node, parentRef.Value, filter) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var members = dialog.SelectedMembers.Select(m => m.Ref).ToArray();
        var rationale = filter == PullUpMemberDialog.MemberFilter.Methods
            ? $"Pulled up methods from {node.Ref.FullyQualifiedName} to {parentRef.Value.FullyQualifiedName}"
            : $"Pulled up fields from {node.Ref.FullyQualifiedName} to {parentRef.Value.FullyQualifiedName}";

        var changeSet = filter == PullUpMemberDialog.MemberFilter.Methods
            ? await _viewModel.ProposePullUpMethodAsync(node, parentRef.Value, members, rationale)
            : await _viewModel.ProposePullUpFieldAsync(node, parentRef.Value, members, rationale);

        await ReviewAndApplyAsync(changeSet, rationale, filter == PullUpMemberDialog.MemberFilter.Methods ? "Pull-up-method" : "Pull-up-field");
    }

    private async void OnPushDownMethodMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantPushDownMethods;
            return;
        }
        await RunPushDownMemberFlowAsync(node, PushDownMemberDialog.MemberFilter.Methods);
    }

    private async void OnPushDownFieldMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantPushDownFields;
            return;
        }
        await RunPushDownMemberFlowAsync(node, PushDownMemberDialog.MemberFilter.Fields);
    }

    private async Task RunPushDownMemberFlowAsync(TypeNodeViewModel node, PushDownMemberDialog.MemberFilter filter)
    {
        var subclasses = _viewModel.FindSubclasses(node);
        if (subclasses.Count == 0)
        {
            _viewModel.Status = string.Format(Strings.Status_NoSubclassesToPushDown, node.Name);
            return;
        }

        var dialog = new PushDownMemberDialog(node, subclasses, filter) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var members = dialog.SelectedMembers.Select(m => m.Ref).ToArray();
        var rationale = filter == PushDownMemberDialog.MemberFilter.Methods
            ? $"Pushed down methods from {node.Ref.FullyQualifiedName} to {dialog.SelectedSubclass.FullyQualifiedName}"
            : $"Pushed down fields from {node.Ref.FullyQualifiedName} to {dialog.SelectedSubclass.FullyQualifiedName}";

        var changeSet = filter == PushDownMemberDialog.MemberFilter.Methods
            ? await _viewModel.ProposePushDownMethodAsync(node, dialog.SelectedSubclass, members, rationale)
            : await _viewModel.ProposePushDownFieldAsync(node, dialog.SelectedSubclass, members, rationale);

        await ReviewAndApplyAsync(changeSet, rationale, filter == PushDownMemberDialog.MemberFilter.Methods ? "Push-down-method" : "Push-down-field");
    }

    private async void OnRemoveSettingMethodMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantRemoveSetter;
            return;
        }

        var dialog = new RemoveSettingMethodDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.SelectedMember is null) return;

        var rationale = $"Removed setter of {node.Name}.{dialog.SelectedMember.Name}";
        var changeSet = await _viewModel.ProposeRemoveSettingMethodAsync(node, dialog.SelectedMember.Ref, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Remove-setting-method");
    }

    private async void OnRenameMemberMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantRenameMembers;
            return;
        }

        var dialog = new Dialogs.RenameMemberDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedMember is null) return;

        var rationale = $"Renamed {node.Name}.{dialog.SelectedMember.Name} → {dialog.NewName}";
        var changeSet = await _viewModel.ProposeRenameMemberAsync(node, dialog.SelectedMember.Ref, dialog.NewName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Rename-member");
    }

    private async void OnRenameFieldMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantRenameFields;
            return;
        }

        var dialog = new RenameFieldDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedField is null) return;

        var rationale = $"Renamed field {node.Name}.{dialog.SelectedField.Name} → {dialog.NewName}";
        var changeSet = await _viewModel.ProposeRenameFieldAsync(node, dialog.SelectedField.Ref, dialog.NewName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Rename-field");
    }

    private async void OnPullUpConstructorBodyMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantPullUpConstructorBody;
            return;
        }

        var parentRef = _viewModel.TryFindReplacementBase(node);
        if (parentRef is null)
        {
            _viewModel.Status = string.Format(Strings.Status_NoBaseToPullUp, node.Name);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Confirm_PullUpCtor_Body, node.Name, parentRef.Value.FullyQualifiedName),
            Strings.Confirm_PullUpCtor_Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var rationale = $"Pulled up constructor body from {node.Ref.FullyQualifiedName} to {parentRef.Value.FullyQualifiedName}";
        var changeSet = await _viewModel.ProposePullUpConstructorBodyAsync(node, parentRef.Value, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Pull-up-constructor-body");
    }

    private async void OnEncapsulateFieldMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantEncapsulateField;
            return;
        }

        var dialog = new EncapsulateFieldDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedField is null) return;

        var rationale = $"Encapsulated field {node.Name}.{dialog.SelectedField.Name}";
        var changeSet = await _viewModel.ProposeEncapsulateFieldAsync(node, dialog.SelectedField.Ref, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Encapsulate-field");
    }

    private async void OnMoveMethodMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantMoveMethods;
            return;
        }
        await RunMoveFlowAsync(node, MoveMemberDialog.MemberFilter.Methods);
    }

    private async void OnMoveFieldMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantMoveFields;
            return;
        }
        await RunMoveFlowAsync(node, MoveMemberDialog.MemberFilter.Fields);
    }

    private async Task RunMoveFlowAsync(TypeNodeViewModel node, MoveMemberDialog.MemberFilter filter)
    {
        var candidates = _viewModel.AllUserTypes();
        if (candidates.Count <= 1)
        {
            _viewModel.Status = Strings.Status_NoOtherClassesAsMoveTarget;
            return;
        }

        var dialog = new MoveMemberDialog(node, candidates, filter) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var members = dialog.SelectedMembers.Select(m => m.Ref).ToArray();
        var kindLabel = filter == MoveMemberDialog.MemberFilter.Methods ? "methods" : "fields";
        var rationale = $"Moved {kindLabel} from {node.Ref.FullyQualifiedName} to {dialog.SelectedTarget.FullyQualifiedName}";

        var changeSet = filter == MoveMemberDialog.MemberFilter.Methods
            ? await _viewModel.ProposeMoveMethodAsync(node, dialog.SelectedTarget, members, rationale)
            : await _viewModel.ProposeMoveFieldAsync(node, dialog.SelectedTarget, members, rationale);

        await ReviewAndApplyAsync(changeSet, rationale, filter == MoveMemberDialog.MemberFilter.Methods ? "Move-method" : "Move-field");
    }

    private async void OnReplaceConstructorWithFactoryMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantConvertConstructor;
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Confirm_ReplaceCtorFactory_Body, node.Name),
            Strings.Confirm_ReplaceCtorFactory_Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var rationale = $"Replaced constructor of {node.Ref.FullyQualifiedName} with static factory Create";
        var changeSet = await _viewModel.ProposeReplaceConstructorWithFactoryAsync(node, "Create", true, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Replace-constructor-with-factory");
    }

    private async void OnReplaceMagicNumberMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantReplaceMagicNumber;
            return;
        }

        var dialog = new ReplaceMagicNumberDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var rationale = $"Replaced magic {dialog.LiteralValue} → {dialog.ConstantName} on {node.Ref.FullyQualifiedName}";
        var changeSet = await _viewModel.ProposeReplaceMagicNumberAsync(
            node, dialog.LiteralValue, dialog.ConstantName, dialog.ConstantType, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Replace-magic-number");
    }

    private async void OnDropBackReferenceMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantChangeAssociation;
            return;
        }

        var dialog = new DropBackReferenceDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedField is null) return;

        var rationale = $"Dropped back-reference {node.Name}.{dialog.SelectedField.Name}";
        var changeSet = await _viewModel.ProposeChangeBidirectionalToUnidirectionalAsync(
            node, dialog.SelectedField.Ref, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Drop-back-reference");
    }

    private async void OnIntroduceParameterObjectMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantIntroduceParamObj;
            return;
        }

        var dialog = new IntroduceParameterObjectDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedMethod is null) return;

        var rationale = $"Bundled parameters of {node.Name}.{dialog.SelectedMethod.Name} into {dialog.ObjectName}";
        var changeSet = await _viewModel.ProposeIntroduceParameterObjectAsync(
            node, dialog.SelectedMethod.Ref, dialog.ObjectName, dialog.ParameterName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Introduce-parameter-object");
    }

    private async void OnAddParameterMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantModifySignature;
            return;
        }

        var dialog = new AddParameterDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedMethod is null) return;

        var rationale = $"Added parameter {dialog.ParameterType} {dialog.ParameterName} to {node.Name}.{dialog.SelectedMethod.Name}";
        var changeSet = await _viewModel.ProposeAddParameterAsync(
            node, dialog.SelectedMethod.Ref, dialog.ParameterType, dialog.ParameterName, dialog.DefaultValue, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Add-parameter");
    }

    private async void OnRemoveParameterMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantModifySignature;
            return;
        }

        var dialog = new RemoveParameterDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedMethod is null) return;

        var rationale = $"Removed parameter {dialog.ParameterName} from {node.Name}.{dialog.SelectedMethod.Name}";
        var changeSet = await _viewModel.ProposeRemoveParameterAsync(
            node, dialog.SelectedMethod.Ref, dialog.ParameterName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Remove-parameter");
    }

    private async void OnRenameParameterMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantRenameParameter;
            return;
        }

        var dialog = new RenameParameterDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedMethod is null) return;

        var rationale = $"Renamed parameter {dialog.OldName} → {dialog.NewName} on {node.Name}.{dialog.SelectedMethod.Name}";
        var changeSet = await _viewModel.ProposeRenameParameterAsync(
            node, dialog.SelectedMethod.Ref, dialog.OldName, dialog.NewName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Rename-parameter");
    }

    private async void OnReplaceDataValueMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantPromoteDataValue;
            return;
        }

        var dialog = new ReplaceDataValueDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedField is null) return;

        var rationale = $"Wrapped {node.Name}.{dialog.SelectedField.Name} in class {dialog.WrapperClassName}";
        var changeSet = await _viewModel.ProposeReplaceDataValueWithObjectAsync(
            node, dialog.SelectedField.Ref, dialog.WrapperClassName, dialog.InnerFieldName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Replace-data-value-with-object");
    }

    private async void OnSelfEncapsulateFieldMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantSelfEncapsulate;
            return;
        }

        var dialog = new SelfEncapsulateFieldDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedField is null) return;

        var rationale = $"Self-encapsulated field {node.Name}.{dialog.SelectedField.Name}";
        var changeSet = await _viewModel.ProposeSelfEncapsulateFieldAsync(node, dialog.SelectedField.Ref, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Self-encapsulate-field");
    }

    private async void OnChangeReferenceToValueMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantLockDown;
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Confirm_RefToValue_Body, node.Name),
            Strings.Confirm_RefToValue_Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var rationale = $"Locked down {node.Ref.FullyQualifiedName} to value semantics (readonly / init-only)";
        var changeSet = await _viewModel.ProposeChangeReferenceToValueAsync(node, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Change-reference-to-value");
    }

    private async void OnChangeValueToReferenceMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantConvertToReference;
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Confirm_ValueToRef_Body, node.Name),
            Strings.Confirm_ValueToRef_Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var rationale = $"Made {node.Ref.FullyQualifiedName} shareable via static registry";
        var changeSet = await _viewModel.ProposeChangeValueToReferenceAsync(node, "string", rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Change-value-to-reference");
    }

    private async void OnReplaceTypeCodeWithClassMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantReplaceTypeCode;
            return;
        }

        var dialog = new ReplaceTypeCodeDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedField is null) return;

        var rationale = $"Replaced type code {node.Name}.{dialog.SelectedField.Name} with class {dialog.NewClassName}";
        var changeSet = await _viewModel.ProposeReplaceTypeCodeWithClassAsync(
            node, dialog.SelectedField.Ref, dialog.NewClassName, dialog.Codes, dialog.InnerType, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Replace-type-code-with-class");
    }

    private async void OnPreserveWholeObjectMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantPreserveWholeObject;
            return;
        }

        var dialog = new PreserveWholeObjectDialog(node, _viewModel.AllUserTypes()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedMethod is null) return;

        var rationale = $"Bundled derived params {string.Join(",", dialog.ReplacedParams)} of {node.Name}.{dialog.SelectedMethod.Name} into {dialog.ObjectType.FullyQualifiedName}";
        var changeSet = await _viewModel.ProposePreserveWholeObjectAsync(
            node, dialog.SelectedMethod.Ref, dialog.ObjectType, dialog.ParameterName, dialog.ReplacedParams, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Preserve-whole-object");
    }

    private async void OnReplaceArrayWithObjectMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantReplaceArray;
            return;
        }

        var dialog = new ReplaceArrayWithObjectDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedField is null) return;

        var rationale = $"Promoted array {node.Name}.{dialog.SelectedField.Name} into class {dialog.NewClassName}";
        var changeSet = await _viewModel.ProposeReplaceArrayWithObjectAsync(
            node, dialog.SelectedField.Ref, dialog.NewClassName, dialog.Mappings, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Replace-array-with-object");
    }

    private async void OnReplaceTypeCodeWithSubclassesMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantSubclass;
            return;
        }

        var dialog = new ReplaceTypeCodeWithSubclassesDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SubclassNames.Count == 0) return;

        var rationale = $"Turned {node.Ref.FullyQualifiedName} into abstract with subclasses {string.Join(", ", dialog.SubclassNames)}";
        var changeSet = await _viewModel.ProposeReplaceTypeCodeWithSubclassesAsync(node, dialog.SubclassNames, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Replace-type-code-with-subclasses");
    }

    private async void OnExtractHierarchyMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantExtractHierarchy;
            return;
        }

        var dialog = new Dialogs.ExtractHierarchyDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SubclassNames.Count == 0) return;

        var rationale = dialog.MethodsToVirtualize.Count == 0
            ? $"Extracted hierarchy under {node.Ref.FullyQualifiedName} → {string.Join(", ", dialog.SubclassNames)}"
            : $"Extracted hierarchy under {node.Ref.FullyQualifiedName} → {string.Join(", ", dialog.SubclassNames)}; virtualized {dialog.MethodsToVirtualize.Count} method(s)";
        var changeSet = await _viewModel.ProposeExtractHierarchyAsync(node, dialog.SubclassNames, dialog.MethodsToVirtualize, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Extract-hierarchy");
    }

    private async void OnTeaseApartInheritanceMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantTeaseApart;
            return;
        }

        var dialog = new Dialogs.TeaseApartInheritanceDialog(node) { Owner = this };
        if (dialog.ShowDialog() != true
            || string.IsNullOrWhiteSpace(dialog.SecondaryHierarchyName)
            || dialog.SecondarySubclassNames.Count == 0
            || string.IsNullOrWhiteSpace(dialog.DelegationFieldName)) return;

        var rationale = $"Teased {node.Ref.FullyQualifiedName} apart: scaffolded {dialog.SecondaryHierarchyName} + {string.Join(", ", dialog.SecondarySubclassNames)}, delegation field {dialog.DelegationFieldName}";
        var changeSet = await _viewModel.ProposeTeaseApartInheritanceAsync(
            node,
            dialog.SecondaryHierarchyName,
            dialog.SecondarySubclassNames,
            dialog.DelegationFieldName,
            rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Tease-apart-inheritance");
    }

    private async void OnExtractMethodMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null)
        {
            _viewModel.Status = Strings.Status_OpenMemberFirst;
            return;
        }
        var selectionLength = CodeEditor.SelectionLength;
        if (selectionLength <= 0)
        {
            _viewModel.Status = Strings.Status_SelectStatementsToExtract;
            return;
        }
        var selectionStart = CodeEditor.SelectionStart;

        var dialog = new Dialogs.ExtractMethodDialog(
            containingMemberLabel: $"{mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}")
        { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.NewMethodName)) return;

        var rationale = $"Extracted {dialog.NewMethodName} from {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeExtractMethodAsync(
            mvm.Source.OwnerType, mvm.Source.Member,
            selectionStart, selectionLength,
            dialog.NewMethodName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Extract-method");
    }

    private async void OnExtractVariableMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        var selectionLength = CodeEditor.SelectionLength;
        if (selectionLength <= 0) { _viewModel.Status = Strings.Status_SelectExprToExtract; return; }
        var selectionStart = CodeEditor.SelectionStart;

        var dialog = new Dialogs.ExtractVariableDialog(
            containingMemberLabel: $"{mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}")
        { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.NewVariableName)) return;

        var rationale = $"Extracted variable {dialog.NewVariableName} in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeExtractVariableAsync(
            mvm.Source.OwnerType, mvm.Source.Member,
            selectionStart, selectionLength,
            dialog.NewVariableName, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Extract-variable");
    }

    private async void OnInlineMethodMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        // Selection can be zero-length — the caret sits inside the invocation.
        var selStart = CodeEditor.SelectionStart;
        var selLen = System.Math.Max(1, CodeEditor.SelectionLength);
        var rationale = $"Inlined method at call site in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeInlineMethodAsync(
            mvm.Source.OwnerType, mvm.Source.Member,
            selStart, selLen, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Inline-method");
    }

    private async void OnInlineVariableMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        var selStart = CodeEditor.SelectionStart;
        var selLen = System.Math.Max(1, CodeEditor.SelectionLength);
        var rationale = $"Inlined variable in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeInlineVariableAsync(
            mvm.Source.OwnerType, mvm.Source.Member,
            selStart, selLen, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Inline-variable");
    }

    private async void OnDecomposeConditionalMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        var selStart = CodeEditor.SelectionStart;
        var selLen = System.Math.Max(1, CodeEditor.SelectionLength);

        var dialog = new Dialogs.DecomposeConditionalDialog(
            containingMemberLabel: $"{mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}")
        { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var rationale = $"Decomposed conditional in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}: {dialog.ConditionMethodName} / {dialog.ThenMethodName}" +
            (dialog.ElseMethodName is null ? "" : $" / {dialog.ElseMethodName}");
        var changeSet = await _viewModel.ProposeDecomposeConditionalAsync(
            mvm.Source.OwnerType, mvm.Source.Member,
            selStart, selLen,
            dialog.ConditionMethodName, dialog.ThenMethodName, dialog.ElseMethodName,
            rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Decompose-conditional");
    }

    private async void OnConsolidateConditionalMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        var selLen = CodeEditor.SelectionLength;
        if (selLen <= 0) { _viewModel.Status = Strings.Status_SelectIfRun; return; }
        var selStart = CodeEditor.SelectionStart;

        var rationale = $"Consolidated conditional in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeConsolidateConditionalExpressionAsync(
            mvm.Source.OwnerType, mvm.Source.Member, selStart, selLen, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Consolidate-conditional");
    }

    private async void OnConsolidateDuplicateFragmentsMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        var selStart = CodeEditor.SelectionStart;
        var selLen = System.Math.Max(1, CodeEditor.SelectionLength);

        var rationale = $"Consolidated duplicate conditional fragments in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeConsolidateDuplicateConditionalFragmentsAsync(
            mvm.Source.OwnerType, mvm.Source.Member, selStart, selLen, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Consolidate-duplicate-fragments");
    }

    private async void OnGuardClausesMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        var selStart = CodeEditor.SelectionStart;
        var selLen = System.Math.Max(1, CodeEditor.SelectionLength);

        var rationale = $"Applied guard clause in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeReplaceNestedConditionalWithGuardClausesAsync(
            mvm.Source.OwnerType, mvm.Source.Member, selStart, selLen, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Guard-clauses");
    }

    private async void OnIntroduceNullObjectMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantIntroduceNullObject;
            return;
        }

        var rationale = $"Scaffolded Null Object for {node.Ref.FullyQualifiedName}";
        var changeSet = await _viewModel.ProposeIntroduceNullObjectAsync(node, nullClassName: null, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Introduce-null-object");
    }

    private async void OnIntroduceAssertionMenuClick(object sender, RoutedEventArgs e)
    {
        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { _viewModel.Status = Strings.Status_OpenMemberFirst; return; }
        var caret = CodeEditor.SelectionStart;

        var dialog = new Dialogs.IntroduceAssertionDialog(
            containingMemberLabel: $"{mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}")
        { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.AssertionExpression)) return;

        var rationale = $"Introduced assertion `{dialog.AssertionExpression}` in {mvm.Source.OwnerType.FullyQualifiedName}.{mvm.Source.Member.Signature}";
        var changeSet = await _viewModel.ProposeIntroduceAssertionAsync(
            mvm.Source.OwnerType, mvm.Source.Member, caret,
            dialog.AssertionExpression, dialog.Message, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Introduce-assertion");
    }

    private async void OnConvertProceduralToObjectsMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantMoveProcedures;
            return;
        }

        // Any user type from the current graph is a possible destination.
        var candidates = _viewModel.Nodes
            .Where(n => !n.IsExternal && !n.IsGhost && n != node)
            .OrderBy(n => n.Name)
            .ToArray();
        if (candidates.Length == 0)
        {
            _viewModel.Status = Strings.Status_NoDataRecordAvailable;
            return;
        }

        var dialog = new Dialogs.ConvertProceduralToObjectsDialog(node, candidates) { Owner = this };
        if (dialog.ShowDialog() != true
            || dialog.SelectedRecord is null
            || dialog.MethodsToMove.Count == 0) return;

        var rationale = $"Moved {dialog.MethodsToMove.Count} procedure(s) from {node.Ref.FullyQualifiedName} onto {dialog.SelectedRecord.Ref.FullyQualifiedName}";
        var changeSet = await _viewModel.ProposeConvertProceduralToObjectsAsync(
            node, dialog.SelectedRecord, dialog.MethodsToMove, rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Convert-procedural-to-objects");
    }

    private async void OnReplaceSubclassWithFieldsMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        if (node.IsExternal || node.IsGhost)
        {
            _viewModel.Status = Strings.Status_CantFlattenHierarchy;
            return;
        }

        var subclasses = _viewModel.FindSubclasses(node);
        if (subclasses.Count == 0)
        {
            _viewModel.Status = string.Format(Strings.Status_NoSubclassesToFoldIn, node.Name);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(
                Strings.Confirm_ReplaceSubclassFields_Body,
                node.Name,
                string.Join("\n", subclasses.Select(s => "- " + s.Ref.FullyQualifiedName))),
            Strings.Confirm_ReplaceSubclassFields_Caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var rationale = $"Flattened hierarchy under {node.Ref.FullyQualifiedName} (removed {subclasses.Count} subclass(es))";
        var changeSet = await _viewModel.ProposeReplaceSubclassWithFieldsAsync(
            node, subclasses.Select(s => s.Ref).ToArray(), rationale);
        await ReviewAndApplyAsync(changeSet, rationale, "Replace-subclass-with-fields");
    }

    private void OnFocusImpactMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TypeNodeViewModel node }) return;
        _viewModel.EnableImpactFocus(new[] { node.Ref }, hops: 1);
    }

    private async void OnNodePreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TypeNodeViewModel typeNode } senderFe)
        {
            return;
        }

        // Popup lives in its own HWND (PopupRoot), but its Popup element is a logical child
        // of the node's DataTemplate — so PreviewMouseLeftButtonDown from a click *inside*
        // the smell Popup still tunnels into this node-border handler. Without the guard the
        // visual walk below finds the popup's inherited MemberItemViewModel DataContext,
        // marks the event handled, and the Button.Click never fires.
        // Different PresentationSource ⇒ the click originated in the popup's HWND.
        if (e.OriginalSource is Visual originVisual &&
            !ReferenceEquals(
                PresentationSource.FromVisual(originVisual),
                PresentationSource.FromVisual(senderFe)))
        {
            return;
        }

        // Belt-and-braces: the 💩 badges themselves live in the node's own HWND so the
        // PresentationSource check above doesn't cover them.
        if (e.OriginalSource is FrameworkElement { Name: "TypeSmellIcon" or "MemberSmellIcon" })
        {
            return;
        }

        MemberItemViewModel? member = null;
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement fe && fe.DataContext is MemberItemViewModel m)
            {
                member = m;
                break;
            }
        }

        if (member is null)
        {
            return;
        }

        e.Handled = true;
        _viewModel.FocusedNode = typeNode;
        await _viewModel.LoadMemberSourceAsync(typeNode.Ref, member.Ref);
        await Dispatcher.InvokeAsync(() => CenterOnNode(typeNode), DispatcherPriority.Loaded);
    }

    private const double FocusZoom = 1.0;

    private void CenterOnNode(TypeNodeViewModel node)
    {
        Editor.ViewportZoom = FocusZoom;
        var vp = Editor.ViewportSize;
        if (vp.Width <= 0 || vp.Height <= 0)
        {
            return;
        }

        var centerX = node.Location.X + node.Size.Width / 2;
        var centerY = node.Location.Y + node.Size.Height / 2;
        Editor.ViewportLocation = new Point(
            centerX - vp.Width / 2,
            centerY - vp.Height / 2);
    }

    private void OnCloseCodeViewerClick(object sender, RoutedEventArgs e)
    {
        _viewModel.CloseCodeViewer();
    }

    private async void OnFindReferencesClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunFindReferencesForCurrentAsync();
    }

    private void OnCloseReferencesPanelClick(object sender, RoutedEventArgs e)
    {
        _viewModel.CloseReferencesPanel();
    }

    private async void OnReferenceRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is ViewModels.ReferenceRowViewModel row)
        {
            await _viewModel.NavigateToReferenceLocationAsync(row.Location);
        }
    }

    private HoverBackgroundRenderer? _hoverRenderer;
    private HoverBackgroundRenderer? _navFlashRenderer;
    private HoverBackgroundRenderer? _duplicateRenderer;
    private System.Windows.Threading.DispatcherTimer _navFlashTimer = null!;
    private DateTime _navFlashStart;
    private (int start, int length)? _lastHoveredWord;

    private const byte NavFlashPeakAlpha = 0x80;
    private static readonly TimeSpan NavFlashHoldDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NavFlashFadeDuration = TimeSpan.FromMilliseconds(1000);

    private void FlashNavigationTarget(int offset, int length)
    {
        if (_navFlashRenderer is null || length <= 0)
        {
            return;
        }
        SetNavFlashAlpha(NavFlashPeakAlpha);
        _navFlashRenderer.SetRange(offset, length);
        _navFlashStart = DateTime.UtcNow;
        _navFlashTimer.Stop();
        _navFlashTimer.Start();
        InvalidateSelectionLayer();
    }

    // 現在 CodeViewer に表示中のメンバーに DuplicatedCode smell が付いていたら
    // その body 範囲を薄い赤で塗って可視化する。付いてなければ renderer をクリア。
    // BodySpanStart/Length が有効なときはそちら (関数本体だけ)、無効な (未取得の)
    // ときは MemberSpan を fallback で使う。
    private void RefreshDuplicateHighlight()
    {
        if (_duplicateRenderer is null) return;
        _duplicateRenderer.SetRange(null, null);

        var mvm = _viewModel.CurrentMemberSource;
        if (mvm is null) { InvalidateSelectionLayer(); return; }

        var hasDup = false;
        foreach (var s in _viewModel.GetCurrentMemberSmells())
        {
            if (s.Category == Kata.Core.Analysis.SmellCategory.DuplicatedCode) { hasDup = true; break; }
        }
        if (!hasDup) { InvalidateSelectionLayer(); return; }

        int start = mvm.Source.BodySpanStart >= 0 && mvm.Source.BodySpanLength > 0
            ? mvm.Source.BodySpanStart
            : mvm.Source.MemberSpanStart;
        int length = mvm.Source.BodySpanStart >= 0 && mvm.Source.BodySpanLength > 0
            ? mvm.Source.BodySpanLength
            : mvm.Source.MemberSpanLength;
        if (start < 0 || length <= 0 || start >= CodeEditor.Document.TextLength)
        {
            InvalidateSelectionLayer();
            return;
        }
        // SourceText を越えないようクリップ
        if (start + length > CodeEditor.Document.TextLength)
            length = CodeEditor.Document.TextLength - start;

        _duplicateRenderer.SetRange(start, length);
        InvalidateSelectionLayer();
    }

    private void OnNavFlashTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _navFlashStart;
        var total = NavFlashHoldDuration + NavFlashFadeDuration;

        if (elapsed >= total)
        {
            _navFlashTimer.Stop();
            _navFlashRenderer?.SetRange(null, null);
            SetNavFlashAlpha(NavFlashPeakAlpha);
            InvalidateSelectionLayer();
            return;
        }

        if (elapsed <= NavFlashHoldDuration)
        {
            // Solid hold — nothing to redraw.
            return;
        }

        // Ease-out cubic on the fade phase so most of the visibility loss happens late.
        var fadeProgress = (elapsed - NavFlashHoldDuration).TotalMilliseconds
                           / NavFlashFadeDuration.TotalMilliseconds;
        var eased = 1.0 - Math.Pow(1.0 - fadeProgress, 3);
        var alpha = (byte)Math.Max(0, NavFlashPeakAlpha * (1.0 - eased));
        SetNavFlashAlpha(alpha);
        InvalidateSelectionLayer();
    }

    private void SetNavFlashAlpha(byte alpha)
    {
        if (_navFlashRenderer?.Brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            solid.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
        }
    }

    private void InvalidateSelectionLayer()
        => CodeEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);

    private async void OnCodeEditorLeftDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }
        var pos = CodeEditor.GetPositionFromPoint(e.GetPosition(CodeEditor));
        if (pos is null)
        {
            return;
        }
        var offset = CodeEditor.Document.GetOffset(pos.Value.Line, pos.Value.Column);
        var word = TryGetIdentifierRange(offset);
        if (word is null)
        {
            return;
        }
        e.Handled = true;
        await _viewModel.NavigateDeeperAtOffsetAsync(offset);
    }

    private void OnCodeEditorMouseMove(object sender, MouseEventArgs e)
    {
        UpdateHoverAt(e.GetPosition(CodeEditor));
    }

    private void OnCodeEditorMouseLeave(object sender, MouseEventArgs e)
    {
        ClearHover();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateHoverAt(Mouse.GetPosition(CodeEditor));
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            ClearHover();
        }
    }

    private void UpdateHoverAt(Point positionInEditor)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control
            || !_viewModel.IsCodeViewerVisible)
        {
            ClearHover();
            return;
        }
        var pos = CodeEditor.GetPositionFromPoint(positionInEditor);
        if (pos is null)
        {
            ClearHover();
            return;
        }
        var offset = CodeEditor.Document.GetOffset(pos.Value.Line, pos.Value.Column);
        var word = TryGetIdentifierRange(offset);
        if (word is null)
        {
            ClearHover();
            return;
        }
        if (_lastHoveredWord?.start == word.Value.start && _lastHoveredWord?.length == word.Value.length)
        {
            return;
        }
        _lastHoveredWord = word;
        _hoverRenderer?.SetRange(word.Value.start, word.Value.length);
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        CodeEditor.Cursor = Cursors.Hand;
    }

    private void ClearHover()
    {
        if (_lastHoveredWord is null && _hoverRenderer?.Start is null)
        {
            return;
        }
        _lastHoveredWord = null;
        _hoverRenderer?.SetRange(null, null);
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        CodeEditor.Cursor = Cursors.IBeam;
    }

    private (int start, int length)? TryGetIdentifierRange(int offset)
    {
        var doc = CodeEditor.Document;
        if (offset < 0 || offset > doc.TextLength)
        {
            return null;
        }
        var probe = offset >= doc.TextLength ? doc.TextLength - 1 : offset;
        if (probe < 0)
        {
            return null;
        }
        if (!IsIdentifierChar(doc.GetCharAt(probe)))
        {
            return null;
        }
        var start = probe;
        while (start > 0 && IsIdentifierChar(doc.GetCharAt(start - 1)))
        {
            start--;
        }
        var end = probe + 1;
        while (end < doc.TextLength && IsIdentifierChar(doc.GetCharAt(end)))
        {
            end++;
        }
        if (char.IsDigit(doc.GetCharAt(start)))
        {
            return null;
        }
        return (start, end - start);
    }

    private static bool IsIdentifierChar(char c) => c == '_' || char.IsLetterOrDigit(c);

    private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BreadcrumbItem crumb })
        {
            return;
        }
        await _viewModel.NavigateBackToBreadcrumbAsync(crumb);
    }

    private async Task ReviewAndApplyAsync(
        ChangeSet? changeSet,
        string rationale,
        string operationLabel,
        IReadOnlyList<TypeRef>? impactSeeds = null)
    {
        if (changeSet is null || changeSet.Changes.Count == 0)
        {
            _viewModel.Status = string.Format(Strings.Status_NoChangesProduced, operationLabel.ToLowerInvariant());
            return;
        }

        var preview = new DiffPreviewDialog(changeSet, rationale, _viewModel.SolutionRootDirectory, _viewModel.LastProposedIntent)
        {
            Owner = this,
        };
        if (preview.ShowDialog() != true)
        {
            _viewModel.Status = string.Format(Strings.Status_OperationRejected, operationLabel);
            return;
        }

        await _viewModel.ApplyAndReloadAsync(changeSet, impactSeeds);
    }

    // Click on the 💩 badge is split across Down and Up:
    //   - Down: swallow so the enclosing Nodify node border does not steal the gesture.
    //   - Up:   open the sibling <Popup>.
    // Opening on Down + StaysOpen="False" is a trap — the corresponding MouseUp lands
    // outside the just-shown Popup and WPF treats it as an outside-click, closing again.
    // Opening on Up avoids that entirely: by the time the Popup mounts the button is
    // already released, so the next MouseDown is the intended dismiss gesture.
    private void OnSmellIconMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement) e.Handled = true;
    }

    // ----- 接続矢印 hover / click ハイライト -----
    //
    // 矢印線の周りに透明太線 (Stroke=Transparent, StrokeThickness=10) を敷いてクリック余裕を確保。
    // Hover 中は IsHighlighted=true にして黄色く太くする。両 endpoint (source/target 型) の
    // IsEdgeHighlighted も同時に立てて外枠を強調。Click で「pin」して手を離しても維持できる。
    // 同じ矢印をもう一度 Click で pin 解除。他の矢印を Click すると前の pin は自動的に外れる。

    private ConnectionViewModel? _pinnedConnection;

    private void OnConnectionMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ConnectionViewModel vm) return;
        ApplyEdgeHighlight(vm, on: true);
    }

    private void OnConnectionMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ConnectionViewModel vm) return;
        if (vm.IsPinned) return; // pin されてる間は hover を離しても外さない
        ApplyEdgeHighlight(vm, on: false);
    }

    private void OnConnectionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ConnectionViewModel vm) return;
        // Nodify のパン/矩形選択と競合しないよう handled にしておく
        e.Handled = true;

        if (vm.IsPinned)
        {
            // 同じ矢印を再クリックで pin 解除
            vm.IsPinned = false;
            ApplyEdgeHighlight(vm, on: false);
            _pinnedConnection = null;
            return;
        }

        // 別の矢印が pin 済みなら先に外す
        if (_pinnedConnection is not null && !ReferenceEquals(_pinnedConnection, vm))
        {
            _pinnedConnection.IsPinned = false;
            ApplyEdgeHighlight(_pinnedConnection, on: false);
        }

        vm.IsPinned = true;
        _pinnedConnection = vm;
        ApplyEdgeHighlight(vm, on: true);
    }

    private static void ApplyEdgeHighlight(ConnectionViewModel vm, bool on)
    {
        vm.IsHighlighted = on;
        vm.SourceNode.IsEdgeHighlighted = on;
        vm.TargetNode.IsEdgeHighlighted = on;
    }

    // The Popup uses StaysOpen=True — WPF's automatic outside-click close was fighting the
    // Button.Click inside the Popup (the Down for "click the button" was being seen as an
    // outside gesture in some cases). We open it here and hand off dismissal to a
    // Window-level PreviewMouseDown that closes the Popup only when the click origin lives
    // outside the Popup's child subtree.
    private void OnSmellIconMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Parent: Panel panel }) return;
        foreach (var child in panel.Children)
        {
            if (child is Popup popup)
            {
                if (_openSmellPopup is not null && !ReferenceEquals(_openSmellPopup, popup))
                    _openSmellPopup.IsOpen = false;

                // Popup 本体は自前 HWND で描画するが、AllowsTransparency=True の Popup が
                // RenderTransform で拡縮された祖先 (NodifyEditor の ViewportTransform) の下に
                // あると、レンダリングパイプラインが親のスケールを引きずり、
                // 中身がキャンバスのズームと同じ倍率で描かれる (低ズームで潰れ、高ズームで巨大化)。
                // ここで開いた瞬間の ViewportZoom の逆数を LayoutTransform に噛ませて
                // 相殺し、ズーム比に依らず一定サイズで見えるようにする。
                if (popup.Child is FrameworkElement popupChild)
                {
                    var zoom = Editor.ViewportZoom;
                    popupChild.LayoutTransform = zoom > 0 && Math.Abs(zoom - 1.0) > 1e-6
                        ? new ScaleTransform(1.0 / zoom, 1.0 / zoom)
                        : Transform.Identity;
                }

                popup.IsOpen = true;
                _openSmellPopup = popup;
                if (popup.Child is IInputElement input) Keyboard.Focus(input);

                if (!_smellOutsideWatcherHooked)
                {
                    AddHandler(PreviewMouseDownEvent,
                        new MouseButtonEventHandler(OnWindowPreviewMouseDown_SmellOutside),
                        handledEventsToo: true);
                    _smellOutsideWatcherHooked = true;
                }

                e.Handled = true;
                return;
            }
        }
    }

    private Popup? _openSmellPopup;
    private bool _smellOutsideWatcherHooked;

    private void OnWindowPreviewMouseDown_SmellOutside(object sender, MouseButtonEventArgs e)
    {
        if (_openSmellPopup is not { IsOpen: true } popup) return;
        if (e.OriginalSource is not DependencyObject src) return;

        // AllowsTransparency=True Popup gets its own HWND, so a click inside the popup
        // reaches its handlers directly and does not fire this Window-level watcher.
        // Anything that DOES reach here originated in the Window's HWND (canvas / other
        // node / toolbar…) — that's an outside click and should dismiss the popup.
        var popupContent = popup.Child;
        if (popupContent is not null && IsInLogicalOrVisualSubtree(src, popupContent)) return;
        if (IsInLogicalOrVisualSubtree(src, popup)) return;
        popup.IsOpen = false;
        _openSmellPopup = null;
    }

    private static bool IsInLogicalOrVisualSubtree(DependencyObject src, DependencyObject ancestor)
    {
        for (DependencyObject? d = src; d is not null; )
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // Escape closes an open smell popup — mirrors the outside-click behavior for keyboard users.
    private void OnSmellPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var el = sender as FrameworkElement;
        while (el is not null)
        {
            if (el is Popup p) { p.IsOpen = false; e.Handled = true; return; }
            el = el.Parent as FrameworkElement
                 ?? VisualTreeHelper.GetParent(el) as FrameworkElement;
        }
    }

    // "これで直す" button in the smell Popup — dispatch to the same handler the context menu uses.
    // Bound directly on the Button (Click="OnSmellFixButtonClick" in XAML); we accept a
    // walked-up fallback path too so a future Border.Button.Click attached listener would
    // keep working.
    private async void OnSmellFixButtonClick(object sender, RoutedEventArgs e)
    {
        CodeSmellViewModel? svm = null;
        if (sender is Button { Tag: CodeSmellViewModel direct })
        {
            svm = direct;
        }
        else
        {
            for (DependencyObject? d = e.OriginalSource as DependencyObject;
                 d is not null;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is Button b && b.Tag is CodeSmellViewModel s) { svm = s; break; }
            }
        }
        if (svm is null)
        {
            _viewModel.Status = "smell-fix: cannot resolve CodeSmellViewModel from Button.Tag";
            return;
        }
        if (svm.PrimaryFix is not { } fix)
        {
            _viewModel.Status = $"smell-fix: no primary fix for {svm.Category}";
            return;
        }

        var node = _viewModel.FindNodeByRef(svm.Smell.Type);
        if (node is null)
        {
            _viewModel.Status = $"smell-fix: node not found for {svm.Smell.Type}";
            return;
        }

        _viewModel.Status = $"smell-fix: dispatching {fix.Kind} on {node.Name}";
        ClosePopupAncestor(sender as FrameworkElement);

        var proxy = new MenuItem { Tag = node };
        switch (fix.Kind)
        {
            case SmellRefactorKind.Rename: OnRenameMenuClick(proxy, e); break;
            case SmellRefactorKind.RenameMember: OnRenameMemberMenuClick(proxy, e); break;
            case SmellRefactorKind.ExtractMethod: OnExtractMethodMenuClick(proxy, e); break;
            case SmellRefactorKind.ExtractClass: OnExtractClassMenuClick(proxy, e); break;
            case SmellRefactorKind.ExtractInterface: OnExtractInterfaceMenuClick(proxy, e); break;
            case SmellRefactorKind.ExtractSuperclass: OnExtractSuperclassMenuClick(proxy, e); break;
            case SmellRefactorKind.IntroduceParameterObject: OnIntroduceParameterObjectMenuClick(proxy, e); break;
            case SmellRefactorKind.EncapsulateField: OnEncapsulateFieldMenuClick(proxy, e); break;
            case SmellRefactorKind.RemoveSettingMethod: OnRemoveSettingMethodMenuClick(proxy, e); break;
            case SmellRefactorKind.MoveMethod: OnMoveMethodMenuClick(proxy, e); break;
            case SmellRefactorKind.ReplaceDataValueWithObject: OnReplaceDataValueMenuClick(proxy, e); break;
            case SmellRefactorKind.ReplaceTypeCodeWithSubclasses: OnReplaceTypeCodeWithSubclassesMenuClick(proxy, e); break;
            case SmellRefactorKind.InlineMethod: OnInlineMethodMenuClick(proxy, e); break;
            case SmellRefactorKind.CollapseHierarchy: OnCollapseHierarchyMenuClick(proxy, e); break;
            case SmellRefactorKind.PushDownMethod: OnPushDownMethodMenuClick(proxy, e); break;
            case SmellRefactorKind.OpenSourceForBodyRefactor:
                await OpenSourceForBodyRefactorAsync(node, svm);
                break;
        }
    }

    // "AI (Claude)" / "AI (Codex)" buttons — invoke the corresponding CLI in headless mode.
    // Prompt is piped via stdin so long context + no shell escaping. Billing delegates to
    // whichever CLI: Claude Code subscription/API, Codex ChatGPT subscription/API.
    // 「AIに提案させる」ボタン: 押すと ContextMenu (Claude / Codex) を開く。
    // ContextMenu の PlacementTarget をボタン自身に固定して、MenuItem 側の Tag binding
    // (PlacementTarget.Tag = CodeSmellViewModel) を確実に効かせる。
    private void OnAskAiSmellFixMenuOpen(object sender, RoutedEventArgs e)
    {
        // Community 版残回数バッジを最新に (直近に AI 呼び出しがあった直後や月境界跨ぎに対応)
        Kata.App.Services.AiQuotaObserver.Instance.Refresh();

        if (sender is not Button btn) return;
        if (btn.ContextMenu is not ContextMenu menu) return;
        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnAskClaudeSmellFixClick(object sender, RoutedEventArgs e)
        => _ = AskAiSmellFixAsync(sender, e, App.ClaudeCli, "Claude", "claude -p");

    private void OnAskCodexSmellFixClick(object sender, RoutedEventArgs e)
        => _ = AskAiSmellFixAsync(sender, e, App.CodexCli, "Codex", "codex exec");

    private async Task AskAiSmellFixAsync(
        object sender,
        RoutedEventArgs e,
        Kata.App.Services.IAiInvoker invoker,
        string backendLabel,
        string backendCommandHint)
    {
        CodeSmellViewModel? svm = null;
        if (sender is MenuItem { Tag: CodeSmellViewModel mi })
        {
            svm = mi;
        }
        else if (sender is Button { Tag: CodeSmellViewModel direct })
        {
            svm = direct;
        }
        else
        {
            for (DependencyObject? d = e.OriginalSource as DependencyObject;
                 d is not null;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is Button b && b.Tag is CodeSmellViewModel s) { svm = s; break; }
            }
        }
        if (svm is null)
        {
            _viewModel.Status = string.Format(Strings.Status_Ai_SvmMissing_Format, backendLabel);
            return;
        }

        var typeRef = svm.Smell.Type;
        var memberSig = svm.Smell.Member?.Signature;

        // ContextMenu 経由の場合 ancestor walk は ContextMenu 自身の Popup で止まるので、
        // smell popup 本体は _openSmellPopup を直接閉じる。
        ClosePopupAncestor(sender as FrameworkElement);
        if (_openSmellPopup is { IsOpen: true } smellPopup)
        {
            smellPopup.IsOpen = false;
            _openSmellPopup = null;
        }
        // Community 版の月次上限を事前チェック。exhaust されていれば prompt build も
        // wait dialog も出さず、その場でアップグレード導線を出す。実際の enforcement は
        // QuotaGatedAiInvoker.AskAsync 側 (先制チェックすり抜け + 直後に別スレッドで
        // 消費されたケース等の保険) にもあり、両方通ることで first-come 保証と UX を両立する。
        if (!App.ProFeatures.IsPro)
        {
            var pre = App.AiUsage.Snapshot();
            if (pre.IsExhausted)
            {
                ShowAiQuotaExceededDialog(backendLabel, pre);
                return;
            }
        }

        _viewModel.Status = string.Format(
            Strings.Status_Ai_Consulting_Format,
            backendCommandHint, svm.DisplayCategory, typeRef.FullyQualifiedName);

        var timeout = TimeSpan.FromMinutes(5);
        using var cts = new CancellationTokenSource(timeout);

        // モードレスの待機ダイアログ。await 中 UI をブロックせず、経過秒表示 + Cancel できる。
        var waitDialog = new Kata.App.Dialogs.AiRequestDialog(
            headline: string.Format(Strings.AiRequest_Headline_Format, backendLabel),
            subtitle: string.Format(Strings.AiRequest_Subtitle_Format,
                backendCommandHint, svm.DisplayCategory, typeRef.FullyQualifiedName),
            cts: cts)
        {
            Owner = this,
        };
        waitDialog.Show();

        try
        {
            Kata.Core.Model.MemberSource? source = null;
            if (svm.Smell.Member is { } memberRef)
            {
                try
                {
                    source = await _viewModel
                        .GetMemberSourceAsync(typeRef, memberRef, cts.Token)
                        .ConfigureAwait(true);
                }
                catch { /* best-effort — omit source if it fails */ }
            }

            // DuplicatedCode の場合、他の複製先の source も引いて prompt に載せる。
            // これが無いと Claude は他ファイルの構造を「予想」で書いて hunk 不一致になる
            // (log #10 で DeviceAudioSource.cpp / FileAudioSource.cpp の include 順を
            //  ProcessAudioSource.cpp の見た目から推測して失敗)。
            var relatedSources = new List<Kata.Core.Model.MemberSource>();
            if (svm.Smell.Category == Kata.Core.Analysis.SmellCategory.DuplicatedCode
                && svm.Smell.RelatedMembers is { Count: > 0 } relateds)
            {
                foreach (var rel in relateds)
                {
                    try
                    {
                        var rs = await _viewModel
                            .GetMemberSourceAsync(rel.DeclaringType, rel, cts.Token)
                            .ConfigureAwait(true);
                        if (rs is not null) relatedSources.Add(rs);
                    }
                    catch { /* skip — 1 個取れなくても致命的ではない */ }
                }
                DiagLog.Line($"[ai] DuplicatedCode: fetched {relatedSources.Count}/{relateds.Count} related sources");
            }

            var prompt = BuildAiSmellPrompt(svm, typeRef, memberSig, source, relatedSources);
            DiagLog.Line($"[ai] request → {backendLabel} ({backendCommandHint}) for {svm.DisplayCategory} @ {typeRef.FullyQualifiedName}::{memberSig ?? "<type>"}");
            DiagLog.Line("[ai] --- prompt (send) begin ---");
            DiagLog.Line(prompt);
            DiagLog.Line("[ai] --- prompt (send) end ---");

            var response = await invoker.AskAsync(prompt, cts.Token).ConfigureAwait(true);
            DiagLog.Line($"[ai] response received ({response?.Length ?? 0} chars)");
            DiagLog.Line("[ai] --- response (raw) begin ---");
            DiagLog.Line(response ?? "(null)");
            DiagLog.Line("[ai] --- response (raw) end ---");

            var changeSet = TryBuildChangeSetFromAiDiff(response ?? string.Empty, source, out var buildError);
            if (changeSet is not null)
            {
                DiagLog.Line($"[ai] parsed ChangeSet with {changeSet.Changes.Count} file change(s):");
                foreach (var c in changeSet.Changes)
                {
                    DiagLog.Line($"[ai]   - {c.Kind} {c.FilePath}");
                }
            }
            else
            {
                DiagLog.Line($"[ai] failed to build ChangeSet: {buildError ?? "(no error msg)"}");
            }

            // 「.h だけの diff」欠落パターンを検知したら、Claude に「.cpp 忘れてるっすよ」と
            // フォローアップして完全版 diff をもらいに行く (LLM がプロンプト強化しても
            // ~50% の確率で片肺 diff を出してくるので、コード側で安全ネットを張る)。
            if (changeSet is not null
                && DetectIncompleteCppExtractDiff(changeSet, out var missingDecls, out var missingStems))
            {
                DiagLog.Line($"[ai] incomplete diff detected: {missingDecls.Count} decls missing .cpp defs in {string.Join(",", missingStems)}; retrying once");
                _viewModel.Status = string.Format(Strings.Status_Ai_FollowupInProgress_Format, backendCommandHint);
                var followup = BuildFollowupCompletionPrompt(missingDecls, missingStems, source);
                var combined = prompt + "\n\n---\nEarlier response:\n" + response + "\n\n---\n" + followup;
                DiagLog.Line("[ai] --- followup prompt (send) begin ---");
                DiagLog.Line(followup);
                DiagLog.Line("[ai] --- followup prompt (send) end ---");
                // followup は同一 transaction 扱いにして quota を消費しない (Fable M3)。
                // 1 クリックで 2 回目の quota 課金 + 途中で AiQuotaExceededException 発生を防ぐ。
                var response2 = await invoker.AskUnmeteredAsync(combined, cts.Token).ConfigureAwait(true);
                DiagLog.Line($"[ai] followup response received ({response2?.Length ?? 0} chars)");
                DiagLog.Line("[ai] --- followup response (raw) begin ---");
                DiagLog.Line(response2 ?? "(null)");
                DiagLog.Line("[ai] --- followup response (raw) end ---");
                var changeSet2 = TryBuildChangeSetFromAiDiff(response2 ?? string.Empty, source, out var buildError2);
                if (changeSet2 is not null
                    && !DetectIncompleteCppExtractDiff(changeSet2, out _, out _))
                {
                    DiagLog.Line("[ai] retry produced complete diff");
                    changeSet = changeSet2;
                    response = response2;
                    buildError = null;
                }
                else
                {
                    DiagLog.Line($"[ai] retry still incomplete or failed: {buildError2 ?? "still missing cpp"}");
                    // 元の incomplete diff は捨てて、fallback パスへ (raw response で MessageBox)。
                    // ユーザーは 2 回目の raw response を見て手動対応する。
                    changeSet = null;
                    response = response2;
                    buildError = Strings.Ai_BuildError_FollowupStillMissing;
                }
            }
            waitDialog.CloseIfOpen();
            if (changeSet is not null)
            {
                _viewModel.Status = string.Format(Strings.Status_Ai_Completed_Format, backendCommandHint, svm.DisplayCategory);
                var rationale = string.Format(Strings.Ai_Rationale_Format, backendLabel, svm.DisplayCategory, svm.Message);
                // The AI-diff path doesn't come through the intent pipeline, so seed
                // Impact Focus explicitly with the smell's own type.
                await ReviewAndApplyAsync(
                        changeSet,
                        rationale,
                        $"AI-{backendLabel}",
                        impactSeeds: new[] { svm.Smell.Type })
                    .ConfigureAwait(true);
            }
            else
            {
                // AI が意図的に diff を返さないケース (false positive として無視すべき、
                // 情報不足で提案不可、等) と、parse/patch 失敗 (LLM 出力がおかしい) は
                // ユーザーへの伝え方を分けたい。前者は「AI の判断としての解説」で、後者は
                // 「機械的な失敗」。
                var isNoOpAnalysis = IsNoOpAiResponse(response);
                var caption = isNoOpAnalysis
                    ? string.Format(Strings.Dialog_Ai_AnalysisCaption_Format, backendLabel, svm.DisplayCategory)
                    : string.Format(Strings.Dialog_Ai_DiffExtractFailedCaption_Format, backendLabel, svm.DisplayCategory);
                if (isNoOpAnalysis)
                {
                    _viewModel.Status = string.Format(Strings.Status_Ai_AnalysisOnly_Format, backendLabel, svm.DisplayCategory);
                }
                else
                {
                    _viewModel.Status = string.Format(Strings.Status_Ai_ApplyFailed_Format, backendLabel, buildError ?? "unknown");
                }
                var body = string.IsNullOrWhiteSpace(response) ? Strings.Dialog_Ai_EmptyResponseBody : response;
                if (!isNoOpAnalysis && buildError is not null) body = $"[{buildError}]\n\n{body}";
                MessageBox.Show(
                    this,
                    body,
                    caption,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // タイムアウトかユーザーの Cancel ボタン。どちらでも同じ扱い。
            _viewModel.Status = string.Format(Strings.Status_Ai_Cancelled_Format, backendLabel, timeout.TotalMinutes.ToString("0"));
        }
        catch (Kata.App.Services.AiQuotaExceededException qex)
        {
            // 事前チェックはすり抜けたが (別スレッド等) invoker 側で拒否された。
            waitDialog.CloseIfOpen();
            ShowAiQuotaExceededDialog(backendLabel, qex.Snapshot);
        }
        catch (Exception ex)
        {
            _viewModel.Status = string.Format(Strings.Status_Ai_Error_Format, backendLabel, ex.GetType().Name, ex.Message);
        }
        finally
        {
            // ダイアログは response 取得直後にも閉じているが、cancel / exception パス経由で
            // まだ開きっぱなしのケースを保険で拾う。
            waitDialog.CloseIfOpen();
            // AI 呼び出し完了後、残回数バッジを更新 (成功 / 失敗どちらでも次回 popup で正しく表示)
            Kata.App.Services.AiQuotaObserver.Instance.Refresh();
        }
    }

    private void ShowAiQuotaExceededDialog(string backendLabel, Kata.App.Services.AiUsageSnapshot snapshot)
    {
        _viewModel.Status = string.Format(
            Strings.AiQuota_ExceededStatus_Format,
            snapshot.UsedCount,
            snapshot.Limit);

        var dialog = new Kata.App.Dialogs.AiQuotaExceededDialog(snapshot)
        {
            Owner = this,
        };
        dialog.ShowDialog();

        // 「キーを持ってる」を選んだら Preferences を開いて Pro タブに直行する。
        // (自然な導線: 上限到達 → 「実は買ってる」→ その場でキー入力可能)
        if (dialog.EnterKeyRequested)
        {
            OpenPreferencesToProTab();
        }
    }

    private void OpenPreferencesToProTab()
    {
        var vm = new PreferencesViewModel(App.SettingsStore, App.LanguageService, App.LicenseStore, App.ProFeatures);
        var proCategory = vm.Categories.FirstOrDefault(c => c.Key == "pro");
        if (proCategory is not null) vm.SelectedCategory = proCategory;
        var window = new Kata.App.Views.PreferencesWindow(vm) { Owner = this };
        window.ShowDialog();
    }

    // Parse the LLM response's diff block, apply it to the target file, wrap the result
    // in a ChangeSet so it can flow through the same review pipeline as any other refactor.
    // Returns null on any parse/patch failure and populates `error` with a short reason —
    // caller falls back to a plain MessageBox in that case.
    private ChangeSet? TryBuildChangeSetFromAiDiff(
        string llmResponse,
        Kata.Core.Model.MemberSource? source,
        out string? error)
    {
        error = null;
        try
        {
            var diffText = UnifiedDiffParser.ExtractDiffBlock(llmResponse);
            if (diffText is null)
            {
                error = Strings.Ai_BuildError_NoDiffBlock;
                return null;
            }
            var parsed = UnifiedDiffParser.Parse(diffText);
            if (parsed.Files.Count == 0)
            {
                error = Strings.Ai_BuildError_NoHunks;
                return null;
            }

            var solutionRoot = _viewModel.SolutionRootDirectory;

            // Step 1: 各 ParsedFileDiff を解決先ファイルパスと「新規/既存」区別付きで束ねる。
            // Claude が 1 ファイルに対して複数の \`\`\`diff ブロックに分けて出したときに、
            // それらを 1 個の ChangeSet エントリにマージしないと、後段の Apply が
            // 「元テキストから独立に patch → 順に書き出し」で最後の書き込みが前のを潰す
            // (image #7 で実際に起きた最大バグ)。
            var groups = new List<(string ResolvedPath, bool IsNew, List<ParsedFileDiff> Diffs)>();
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var fileDiff in parsed.Files)
            {
                var isNew = string.Equals(fileDiff.OldPath, "/dev/null", StringComparison.Ordinal)
                            || string.IsNullOrEmpty(fileDiff.OldPath);
                var resolved = ResolveDiffFilePath(fileDiff, source?.FilePath, solutionRoot, isNew);
                if (resolved is null)
                {
                    error = string.Format(Strings.Ai_BuildError_UnknownTargetFile_Format, fileDiff.NewPath);
                    return null;
                }
                // 新規ファイル判定は「/dev/null 由来」または「NewPath が指す先が disk に無い」の両方。
                // 後者は Claude が /dev/null を書かなかったケース (稀にある) の救済。
                if (!isNew && !System.IO.File.Exists(resolved))
                {
                    isNew = true;
                }
                if (byPath.TryGetValue(resolved, out var idx))
                {
                    var g = groups[idx];
                    g.Diffs.Add(fileDiff);
                    // isNew は 1 つでも新規判定があれば新規のまま (混在は Claude のバグと見なす)
                    groups[idx] = (g.ResolvedPath, g.IsNew || isNew, g.Diffs);
                }
                else
                {
                    byPath[resolved] = groups.Count;
                    groups.Add((resolved, isNew, new List<ParsedFileDiff> { fileDiff }));
                }
            }

            // Step 2: グループごとに、original から始めて全 hunk を累積適用する。
            var changes = new List<DocumentChange>();
            foreach (var (resolvedPath, isNew, diffs) in groups)
            {
                string original;
                if (isNew)
                {
                    original = string.Empty;
                }
                else
                {
                    try { original = System.IO.File.ReadAllText(resolvedPath); }
                    catch (Exception ex) { error = string.Format(Strings.Ai_BuildError_ReadOriginalFailed_Format, System.IO.Path.GetFileName(resolvedPath), ex.Message); return null; }
                }

                var current = original;
                foreach (var fd in diffs)
                {
                    try
                    {
                        current = UnifiedDiffPatcher.Apply(current, fd);
                    }
                    catch (Exception ex)
                    {
                        error = string.Format(Strings.Ai_BuildError_ApplyFailed_Format, System.IO.Path.GetFileName(resolvedPath), ex.Message);
                        return null;
                    }
                }

                if (!isNew && string.Equals(current, original, StringComparison.Ordinal))
                {
                    error = string.Format(Strings.Ai_BuildError_NoChangesProduced_Format, System.IO.Path.GetFileName(resolvedPath));
                    return null;
                }
                changes.Add(new DocumentChange(
                    FilePath: resolvedPath,
                    Kind: isNew ? DocumentChangeKind.Added : DocumentChangeKind.Modified,
                    OldText: isNew ? null : original,
                    NewText: current));
            }
            if (changes.Count == 0)
            {
                error = Strings.Ai_BuildError_NoApplicableChanges;
                return null;
            }
            return new ChangeSet(
                AppliedIntentIds: Array.Empty<Guid>(),
                Changes: changes,
                Summary: string.Format(Strings.Ai_ChangeSetSummary_Format, changes.Count));
        }
        catch (Exception ex)
        {
            error = string.Format(Strings.Ai_BuildError_TransformException_Format, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    // Path resolution priority:
    //   1. Both OldPath/NewPath empty → assume the smell's own source file.
    //   2. Absolute path from the diff → use as-is.
    //   3. Relative path + solution root → combine (新規ファイルなら Exists チェックせず採用)。
    //   4. Relative path but no solution root → fall back to the smell's source file's dir.
    //
    // isNewFile: OldPath が /dev/null の場合など「ディスクにまだ無いファイル」扱いにする指示。
    //            Exists チェックをスキップして solutionRoot + NewPath を素直に採用する。
    private static string? ResolveDiffFilePath(
        ParsedFileDiff fileDiff,
        string? smellSourceFilePath,
        string? solutionRoot,
        bool isNewFile = false)
    {
        var candidate = !string.IsNullOrEmpty(fileDiff.NewPath) ? fileDiff.NewPath : fileDiff.OldPath;
        if (string.IsNullOrEmpty(candidate) || candidate == "/dev/null")
        {
            return smellSourceFilePath;
        }
        if (System.IO.Path.IsPathRooted(candidate)) return candidate;
        if (!string.IsNullOrEmpty(solutionRoot))
        {
            var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionRoot, candidate));
            if (isNewFile || System.IO.File.Exists(combined)) return combined;
        }
        if (!string.IsNullOrEmpty(smellSourceFilePath))
        {
            var dir = System.IO.Path.GetDirectoryName(smellSourceFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, candidate));
                if (isNewFile || System.IO.File.Exists(combined)) return combined;
            }
            // Last resort: 新規ファイル指示があれば smell 元と同じディレクトリに作る、
            // 既存扱いなら fallback として smell 元ファイル自身を返す (旧来動作)。
            if (isNewFile)
            {
                var dir2 = System.IO.Path.GetDirectoryName(smellSourceFilePath);
                if (!string.IsNullOrEmpty(dir2))
                    return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir2, candidate));
            }
            return smellSourceFilePath;
        }
        return null;
    }

    private static string BuildAiSmellPrompt(
        CodeSmellViewModel svm,
        TypeRef typeRef,
        string? memberSignature,
        Kata.Core.Model.MemberSource? source,
        IReadOnlyList<Kata.Core.Model.MemberSource>? relatedSources = null)
    {
        var (languageName, codeFence) = DetectLanguageForPrompt(source?.FilePath);
        var isCppCli = string.Equals(languageName, "C++/CLI", StringComparison.Ordinal);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are a refactoring assistant. Fix the code smell below in the given {languageName} code using a specific Fowler refactoring. Reply with a unified diff — no JSON, no pseudocode.");
        sb.AppendLine();
        sb.AppendLine("## Target");
        sb.AppendLine($"- Type: `{typeRef.FullyQualifiedName}`");
        if (memberSignature is not null) sb.AppendLine($"- Member: `{memberSignature}`");
        sb.AppendLine($"- Smell: `{svm.Category}` ({svm.DisplayCategory})");
        sb.AppendLine($"- Severity: `{svm.Severity}`");
        sb.AppendLine($"- Detector message: {svm.Message}");

        // DuplicatedCode の場合、detector が把握している「他の複製先」を列挙して
        // 「Extract Method で 1 個抽出 + N 箇所全部呼び出しに置換」を明示的に指示する。
        // (これ書いておかないと Claude は 1 箇所 (target) しか置換しない傾向がある)
        if (svm.Smell.Category == Kata.Core.Analysis.SmellCategory.DuplicatedCode
            && svm.Smell.RelatedMembers is { Count: > 0 } related)
        {
            sb.AppendLine();
            sb.AppendLine("### Other duplicate call sites (MUST be replaced by the extracted helper too)");
            sb.AppendLine("The detector found this method body appears verbatim in the following members. Your diff MUST also update EACH of these to call the extracted helper — otherwise the duplication persists.");
            sb.AppendLine();
            foreach (var r in related)
            {
                sb.AppendLine($"- `{r.DeclaringType.FullyQualifiedName}::{r.Signature}`");
            }
        }

        if (source is not null && !string.IsNullOrEmpty(source.SourceText))
        {
            sb.AppendLine();
            sb.AppendLine($"### File: `{source.FilePath}`");
            sb.AppendLine();
            sb.AppendLine("```" + codeFence);
            sb.AppendLine(source.SourceText);
            sb.AppendLine("```");
        }

        // DuplicatedCode 用: 他の複製先の source も並べて出す。Claude が他ファイル構造を
        // 予想で書いて hunk match しなくなる問題への対策 (log #10)。
        if (relatedSources is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Related duplicate sites (source snippets — use these to generate accurate replacement hunks)");
            foreach (var rs in relatedSources)
            {
                if (string.IsNullOrEmpty(rs.SourceText)) continue;
                var (_, relFence) = DetectLanguageForPrompt(rs.FilePath);
                sb.AppendLine();
                sb.AppendLine($"#### `{rs.OwnerType.FullyQualifiedName}::{rs.Member.Signature}`  in `{rs.FilePath}`");
                sb.AppendLine();
                sb.AppendLine("```" + relFence);
                sb.AppendLine(rs.SourceText);
                sb.AppendLine("```");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Reply format (Japanese prose OK, but diff must be plain)");
        sb.AppendLine("1. **One-line summary** — refactoring name + target (e.g. `Extract Method: TryFindTargetDeviceAt`).");
        sb.AppendLine("2. **Unified diff** — `git apply`-able. Example shape:");
        sb.AppendLine("```diff");
        sb.AppendLine("--- a/<path>");
        sb.AppendLine("+++ b/<path>");
        sb.AppendLine("@@ -<oldStart>,<oldLen> +<newStart>,<newLen> @@");
        sb.AppendLine("- removed line");
        sb.AppendLine("+ added line");
        sb.AppendLine("```");
        sb.AppendLine("3. **Follow-up notes** (optional) — related smells, next steps.");
        sb.AppendLine();
        sb.AppendLine("## HARD RULES — the diff MUST leave the codebase compilable");
        sb.AppendLine();
        sb.AppendLine("A partial diff that only declares / only defines / only calls is FORBIDDEN. If the diff would fail to compile or fail to link after apply, the answer is wrong.");
        sb.AppendLine();
        if (isCppCli)
        {
            sb.AppendLine("### C++/CLI: when you add a new member function, ALWAYS emit TWO diff blocks");
            sb.AppendLine();
            sb.AppendLine("For every new member `void Foo::Helper(int x)`, you MUST include:");
            sb.AppendLine();
            sb.AppendLine("  1. Header hunk (`--- a/Foo.h` / `+++ b/Foo.h`) that adds the class-member declaration:");
            sb.AppendLine("     `+   void Helper(int x);`");
            sb.AppendLine();
            sb.AppendLine("  2. Source hunk (`--- a/Foo.cpp` / `+++ b/Foo.cpp`) that adds the definition body:");
            sb.AppendLine("     ```");
            sb.AppendLine("     + void Foo::Helper(int x)");
            sb.AppendLine("     + {");
            sb.AppendLine("     +     // body");
            sb.AppendLine("     + }");
            sb.AppendLine("     ```");
            sb.AppendLine();
            sb.AppendLine("**BAD — header-only diff (linker error, this is the exact failure mode we keep hitting):**");
            sb.AppendLine("```diff");
            sb.AppendLine("--- a/Foo.h");
            sb.AppendLine("+++ b/Foo.h");
            sb.AppendLine("@@ ... @@");
            sb.AppendLine("+   void Helper(int x);   // declaration with no .cpp body → LNK error");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("**BAD — source-only diff (compile error, missing declaration):**");
            sb.AppendLine("```diff");
            sb.AppendLine("--- a/Foo.cpp");
            sb.AppendLine("+++ b/Foo.cpp");
            sb.AppendLine("@@ ... @@");
            sb.AppendLine("+   Helper(x);   // call to something never declared");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("**GOOD — both .h and .cpp (this is the ONLY acceptable shape):**");
            sb.AppendLine("```diff");
            sb.AppendLine("--- a/Foo.h");
            sb.AppendLine("+++ b/Foo.h");
            sb.AppendLine("@@ ... @@");
            sb.AppendLine("+   void Helper(int x);");
            sb.AppendLine("--- a/Foo.cpp");
            sb.AppendLine("+++ b/Foo.cpp");
            sb.AppendLine("@@ ... @@");
            sb.AppendLine("+ void Foo::Helper(int x)");
            sb.AppendLine("+ {");
            sb.AppendLine("+     // body");
            sb.AppendLine("+ }");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Exception — if the helper is a file-scope `static` or lives in an anonymous `namespace { ... }` inside the `.cpp`, no `.h` change is needed. But then it must NOT be a member of any class.");
            sb.AppendLine();
            sb.AppendLine("The source snippet above only shows the `.cpp` side, but the matching header is at the same directory with the same base name (`Foo.cpp` ↔ `Foo.h`). Just write `--- a/…/Foo.h` and the tool will locate it.");
        }
        else
        {
            sb.AppendLine("### Managed languages");
            sb.AppendLine("- If you add a call `+ Foo(x, y);`, the diff MUST also add the `Foo` definition.");
            sb.AppendLine("- New `private` / `internal` methods MUST be defined in the same file that references them.");
        }
        // DuplicatedCode 用の必須追記: 全 site 置換を明文化。
        if (svm.Smell.Category == Kata.Core.Analysis.SmellCategory.DuplicatedCode
            && svm.Smell.RelatedMembers is { Count: > 0 } related2)
        {
            sb.AppendLine();
            sb.AppendLine("### DuplicatedCode-specific rule (this smell)");
            sb.AppendLine("- Extract the shared body into ONE new helper method / free function / static function (whichever fits the language).");
            sb.AppendLine($"- Replace the body of the current target AND ALL {related2.Count} other duplicate site(s) listed above with a call to the helper.");
            sb.AppendLine("- The final diff must include: (1) the helper definition, (2) the current target's replacement hunk, and (3) a replacement hunk for EACH other duplicate site.");
            sb.AppendLine("- Leaving any duplicate site un-replaced means the smell persists — that is a failed refactor.");
        }

        sb.AppendLine();
        sb.AppendLine("### Self-check (do this BEFORE sending your response)");
        sb.AppendLine("- Every identifier on a `+` line (method name, type name, constant) — is it resolvable after apply?");
        sb.AppendLine("- Every declaration has a matching definition? (For C++/CLI: matching hunks in BOTH `.h` and `.cpp`?)");
        sb.AppendLine("- If only one side is present, ADD THE OTHER SIDE before sending. Do not send a half-diff.");
        if (svm.Smell.Category == Kata.Core.Analysis.SmellCategory.DuplicatedCode
            && svm.Smell.RelatedMembers is { Count: > 0 })
        {
            sb.AppendLine("- For DuplicatedCode: did you produce a replacement hunk for every listed duplicate site (not just the target)?");
        }
        sb.AppendLine();
        sb.AppendLine("## Format hygiene (so the patcher can apply cleanly)");
        sb.AppendLine("- **Context and removed lines MUST be copied byte-for-byte from the source snippet above** (indentation, newlines, whitespace — everything).");
        sb.AppendLine("  Do not re-flow line breaks. `Method(\\n    arg1,\\n    arg2)` MUST stay on its original lines.");
        sb.AppendLine("- Removed lines (`-`) must be a contiguous range in the source. A single hunk cannot skip over unchanged lines.");
        sb.AppendLine("- Include ~3 lines of surrounding context to anchor each hunk.");
        sb.AppendLine("- Hunk header line numbers can be approximate — the patcher fuzzy-matches.");
        sb.AppendLine("- **NO placeholder comments** in the diff. Do NOT insert lines like `// (existing include is kept)`, `// ... 省略 ...`, `// rest unchanged`, `// (既存のまま)`. Every context line MUST be a verbatim copy of a line that actually exists in the source. If you don't want to write out an unchanged region, just don't include it — the hunk boundary alone is enough.");
        return sb.ToString();
    }

    // AI が「diff は出せない (false positive / 情報不足 / 意図的な no-op)」と判断した応答か、
    // それとも「diff を出すつもりだったが体裁が崩れて parse できなかった」失敗かを見分ける。
    //
    // 判定は保守的に:
    //   - `---` `+++` `@@` `diff` `patch` `unified` のいずれも登場しない
    //   - かつ ある程度の長さ (200 文字以上) の散文がある
    //   → LLM は意識的に diff を出さない選択をしたと解釈する。
    // どちらか判別できないケースは false 側 (機械的な失敗として扱う) に倒す。
    private static bool IsNoOpAiResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        if (response.Length < 200) return false;
        if (response.Contains("--- ", StringComparison.Ordinal)) return false;
        if (response.Contains("+++ ", StringComparison.Ordinal)) return false;
        if (response.Contains("@@ ", StringComparison.Ordinal)) return false;
        if (response.Contains("```diff", StringComparison.OrdinalIgnoreCase)) return false;
        if (response.Contains("```patch", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // 「.h に宣言を追加したのに .cpp 側のハンクが無い」欠落 diff を検出する。
    // fileDiff.NewPath / OldPath / patched vs original の差分を見て、C++/CLI で
    // header hunks で `+` 行に新規宣言らしき「識別子(引数);」パターンがあり、
    // なおかつ ChangeSet 内に対応する .cpp 変更 (case-insensitive の same base name) が
    // 無ければ true。
    private static bool DetectIncompleteCppExtractDiff(
        Kata.Core.Diff.ChangeSet changeSet,
        out List<string> declaredButUndefined,
        out List<string> missingCppFiles)
    {
        declaredButUndefined = new List<string>();
        missingCppFiles = new List<string>();

        var headerAdds = new List<(string HeaderPath, string Line)>();
        var touchedCppFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in changeSet.Changes)
        {
            var ext = System.IO.Path.GetExtension(c.FilePath).ToLowerInvariant();
            if (ext is ".cpp" or ".cxx" or ".cc")
            {
                touchedCppFiles.Add(System.IO.Path.GetFileNameWithoutExtension(c.FilePath));
                continue;
            }
            if (ext is not ".h" and not ".hpp" and not ".hxx" and not ".hh") continue;
            if (c.OldText is null || c.NewText is null) continue;

            // Find `+` lines (added lines) in the header. Naive: any line in NewText not in OldText.
            var oldSet = new HashSet<string>(
                c.OldText.Replace("\r\n", "\n").Split('\n'), StringComparer.Ordinal);
            foreach (var line in c.NewText.Replace("\r\n", "\n").Split('\n'))
            {
                if (oldSet.Contains(line)) continue;
                var trimmed = line.Trim();
                // Heuristic: looks like a method declaration ending with `);` and no `=` (not init)
                if (!trimmed.EndsWith(");", StringComparison.Ordinal)) continue;
                if (trimmed.Contains('=')) continue;
                // Skip access specifiers and other non-decl-looking lines
                if (trimmed.EndsWith("::") || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                headerAdds.Add((c.FilePath, trimmed));
            }
        }

        if (headerAdds.Count == 0) return false;

        foreach (var (headerPath, line) in headerAdds)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(headerPath);
            if (!touchedCppFiles.Contains(stem))
            {
                declaredButUndefined.Add(line);
                if (!missingCppFiles.Contains(stem)) missingCppFiles.Add(stem);
            }
        }
        return declaredButUndefined.Count > 0;
    }

    // Incomplete-diff 検出後のフォローアップ prompt。Claude に「.cpp が抜けてる」と
    // 明示して、抜けている宣言リストを渡して、cpp 側の定義を含む diff を求める。
    private static string BuildFollowupCompletionPrompt(
        IReadOnlyList<string> declaredButUndefined,
        IReadOnlyList<string> missingCppStems,
        Kata.Core.Model.MemberSource? source)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Your previous diff was INCOMPLETE. You added header declarations but forgot to add the matching `.cpp` definitions. Applying this as-is would break the link step.");
        sb.AppendLine();
        sb.AppendLine("Missing definitions:");
        foreach (var line in declaredButUndefined) sb.AppendLine($"  - `{line}`");
        sb.AppendLine();
        sb.AppendLine("Please re-emit a COMPLETE diff. Include BOTH:");
        sb.AppendLine("- the header hunks you already produced (unchanged), AND");
        foreach (var stem in missingCppStems)
        {
            sb.AppendLine($"- a hunk on `{stem}.cpp` that adds the out-of-class definitions with proper `{stem}::` scope prefix and a real body");
        }
        sb.AppendLine();
        sb.AppendLine("Format reminder:");
        sb.AppendLine("- One `--- a/... / +++ b/...` block per file.");
        sb.AppendLine("- Context lines must exist in the current source (do not re-wrap).");
        sb.AppendLine("- Every declaration must have a matching definition in the same diff.");
        if (source is not null && !string.IsNullOrEmpty(source.FilePath))
        {
            sb.AppendLine();
            sb.AppendLine($"For reference, the smell target's file is: `{source.FilePath}`.");
        }
        return sb.ToString();
    }

    // Prompt に埋め込む言語ラベルと code fence の記法を、対象ファイルの拡張子から決める。
    // ここが csharp 固定だと Cpp/CLI ソースに対して LLM が C# 化しようとして
    // 生成 diff がファイルと合わなくなる。
    private static (string LanguageName, string CodeFence) DetectLanguageForPrompt(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return ("C#", "csharp");
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cpp" or ".cxx" or ".cc" or ".h" or ".hpp" or ".hxx" or ".hh"
                => ("C++/CLI", "cpp"),
            ".fs" or ".fsx" => ("F#", "fsharp"),
            ".vb" => ("Visual Basic", "vbnet"),
            _ => ("C#", "csharp"),
        };
    }

    // Popup fallback for body-level refactors (Extract Method, Inline Method, …). Those need
    // a text-range selection in the code viewer, which the popup can't produce, so we open
    // the member source and prompt the user to finish the operation from there.
    private async Task OpenSourceForBodyRefactorAsync(TypeNodeViewModel node, CodeSmellViewModel svm)
    {
        if (svm.Smell.Member is { } memberRef)
        {
            _viewModel.FocusedNode = node;
            await _viewModel.LoadMemberSourceAsync(node.Ref, memberRef);
            _viewModel.Status = string.Format(Strings.Status_Smell_OpenViewerHint_Format, svm.DisplayCategory);
        }
        else
        {
            _viewModel.Status = string.Format(Strings.Status_Smell_TypeLevelHint_Format, svm.DisplayCategory);
        }
    }

    private void ClosePopupAncestor(FrameworkElement? el)
    {
        while (el is not null)
        {
            if (el is Popup p)
            {
                p.IsOpen = false;
                if (ReferenceEquals(_openSmellPopup, p)) _openSmellPopup = null;
                return;
            }
            var next = el.Parent as FrameworkElement
                       ?? System.Windows.Media.VisualTreeHelper.GetParent(el) as FrameworkElement;
            if (ReferenceEquals(next, el)) return;
            el = next;
        }
    }
}
