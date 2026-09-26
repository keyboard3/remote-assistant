using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RemoteAssistant;

public partial class ActionSettingsWindow : Window, INotifyPropertyChanged
{
    private readonly Dictionary<TextBox, CaptureState> _captureStates = new();
    private ActionEditorRow? _dragCandidate;
    private Point _dragOrigin;
    private bool _manualEntryEnabled;

    public ObservableCollection<ActionEditorRow> Actions { get; } = new();
    public ActionSettingsDocument? SavedSettings { get; private set; }
    public bool ManualEntryEnabled
    {
        get => _manualEntryEnabled;
        set
        {
            if (_manualEntryEnabled == value) return;
            _manualEntryEnabled = value;
            _captureStates.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ManualEntryEnabled)));
            ErrorText.Text = value
                ? "快捷键框已可编辑；可直接输入 LeftWin+LeftShift+S，应用设置时会校验格式。"
                : "已切换为按键录制。";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ActionSettingsWindow(ActionSettingsDocument initialSettings, string? initialError)
    {
        InitializeComponent();
        DataContext = this;
        foreach (var action in initialSettings.Actions) Actions.Add(new ActionEditorRow(action));
        ErrorText.Text = initialError ?? "按钮本身就是触发入口；这里录制的是点击按钮后要执行的组合键。";
        ContentRendered += (_, _) =>
        {
            FitToOwnerMonitor();
            Activate();
        };
    }

    private void FitToOwnerMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ownerHwnd = Owner is null ? hwnd : new WindowInteropHelper(Owner).Handle;
        var monitor = MonitorFromWindow(ownerHwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96d;
        var workWidth = info.Work.Right - info.Work.Left;
        var workHeight = info.Work.Bottom - info.Work.Top;
        var margin = Math.Max(16, (int)Math.Round(20 * scale));
        var availableWidth = Math.Max(320, workWidth - margin * 2);
        var availableHeight = Math.Max(300, workHeight - margin * 2);
        var targetWidth = Math.Min((int)Math.Round(660 * scale), availableWidth);
        var targetHeight = Math.Min((int)Math.Round(680 * scale), availableHeight);

        MinWidth = Math.Min(520, availableWidth / scale);
        MinHeight = Math.Min(380, availableHeight / scale);
        MaxWidth = availableWidth / scale;
        MaxHeight = availableHeight / scale;
        var left = info.Work.Left + (workWidth - targetWidth) / 2;
        var top = info.Work.Top + (workHeight - targetHeight) / 2;
        SetWindowPos(hwnd, IntPtr.Zero, left, top, targetWidth, targetHeight, SwpNoZOrder | SwpNoActivate);
    }

    public ActionSettingsWindow(ActionSettings initialSettings, string? initialError)
        : this(CreateCompatibilityDocument(initialSettings), initialError)
    {
    }

    private static ActionSettingsDocument CreateCompatibilityDocument(ActionSettings settings)
    {
        var actions = ActionSettingsDocument.Default.Actions.ToList();
        actions[^1] = actions[^1] with { Title = settings.Title, Shortcut = settings.Hotkey };
        return new(ActionSettingsDocument.CurrentSchemaVersion, actions);
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        Actions.Add(new ActionEditorRow(new ActionDefinition(
            $"custom.{Guid.NewGuid():N}", ActionKinds.SendKeys, "新快捷操作", null, true)));
        ErrorText.Text = "已新增按钮；请填写名称并录制或手动输入执行快捷键。";
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ActionEditorRow row }) Actions.Remove(row);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int offset)
    {
        if (sender is not Button { Tag: ActionEditorRow row }) return;
        var index = Actions.IndexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Actions.Count) return;
        Actions.Move(index, target);
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ActionEditorRow row } handle) return;
        _dragCandidate = row;
        _dragOrigin = e.GetPosition(this);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border handle || _dragCandidate is not { } row) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = null;
            handle.ReleaseMouseCapture();
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragCandidate = null;
        handle.ReleaseMouseCapture();
        DragDrop.DoDragDrop(handle, new DataObject(typeof(ActionEditorRow), row), DragDropEffects.Move);
        e.Handled = true;
    }

    private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (sender is Border handle) handle.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ActionsScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !TryGetDraggedRow(e, out _)) return;
        var y = e.GetPosition(viewer).Y;
        if (y < 32) viewer.ScrollToVerticalOffset(viewer.VerticalOffset - 18);
        else if (y > viewer.ActualHeight - 32) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + 18);
    }

    private void ActionRow_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ActionEditorRow target ||
            !TryGetDraggedRow(e, out var source) || ReferenceEquals(source, target))
        {
            if (sender is Border invalidTarget) ClearDropIndicator(invalidTarget);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dropAfter = e.GetPosition(border).Y >= border.ActualHeight / 2;
        border.BorderBrush = (Brush)FindResource("Accent");
        border.BorderThickness = dropAfter ? new Thickness(1, 1, 1, 3) : new Thickness(1, 3, 1, 1);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ActionRow_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border) ClearDropIndicator(border);
    }

    private void ActionRow_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border border) return;
        ClearDropIndicator(border);
        if (border.DataContext is not ActionEditorRow target || !TryGetDraggedRow(e, out var source)) return;

        var sourceIndex = Actions.IndexOf(source);
        var targetIndex = Actions.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return;
        var insertIndex = targetIndex + (e.GetPosition(border).Y >= border.ActualHeight / 2 ? 1 : 0);
        if (sourceIndex < insertIndex) insertIndex--;
        if (sourceIndex != insertIndex)
        {
            Actions.Move(sourceIndex, insertIndex);
            ErrorText.Text = "顺序已调整；点击“应用设置”保存。";
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private bool TryGetDraggedRow(DragEventArgs e, out ActionEditorRow row)
    {
        var candidate = e.Data.GetData(typeof(ActionEditorRow)) as ActionEditorRow;
        if (candidate is null || !Actions.Contains(candidate))
        {
            row = null!;
            return false;
        }
        row = candidate;
        return true;
    }

    private static void ClearDropIndicator(Border border)
    {
        border.ClearValue(Border.BorderBrushProperty);
        border.ClearValue(Border.BorderThicknessProperty);
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not ActionEditorRow row) return;
        if (ManualEntryEnabled) return;
        e.Handled = true;
        if (e.IsRepeat) return;

        var key = ResolveKey(e);
        var label = KeyLabel(key);
        if (label is null)
        {
            ErrorText.Text = $"暂不支持按键 {key}；请录制字母、数字、F1–F24、导航键或常用控制键。";
            return;
        }

        if (!_captureStates.TryGetValue(box, out var state) || state.Completed)
        {
            state = new CaptureState();
            _captureStates[box] = state;
        }
        if (!state.Held.Add(key)) return;
        state.Order.Add(key);

        if (!IsModifier(key))
        {
            state.HadMainKey = true;
            state.Completed = true;
            row.Shortcut = BuildChord(state.Order, key);
            ErrorText.Text = $"已录制：{row.Shortcut}";
        }
        else
        {
            row.Shortcut = string.Join("+", state.Order.Select(KeyLabel));
        }
    }

    private void Hotkey_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not ActionEditorRow row || !_captureStates.TryGetValue(box, out var state)) return;
        if (ManualEntryEnabled) return;
        e.Handled = true;
        state.Held.Remove(ResolveKey(e));

        if (!state.HadMainKey && state.Order.Count > 0)
        {
            row.Shortcut = string.Join("+", state.Order.Select(KeyLabel));
            ErrorText.Text = $"已录制：{row.Shortcut}";
        }
        if (state.Held.Count == 0) _captureStates.Remove(box);
    }

    private static string BuildChord(IEnumerable<Key> order, Key mainKey)
    {
        var modifiers = order.Where(IsModifier).Select(KeyLabel);
        return string.Join("+", modifiers.Append(KeyLabel(mainKey)!));
    }

    private static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string? KeyLabel(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key is >= Key.F1 and <= Key.F24) return key.ToString();
        return key switch
        {
            Key.LeftCtrl => "LeftCtrl", Key.RightCtrl => "RightCtrl",
            Key.LeftAlt => "LeftAlt", Key.RightAlt => "RightAlt",
            Key.LeftShift => "LeftShift", Key.RightShift => "RightShift",
            Key.LWin => "LeftWin", Key.RWin => "RightWin",
            Key.Space => "Space", Key.Return => "Enter", Key.Tab => "Tab", Key.Escape => "Escape",
            Key.Back => "Backspace", Key.Delete => "Delete", Key.Insert => "Insert", Key.Home => "Home",
            Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Left => "Left", Key.Up => "Up", Key.Right => "Right", Key.Down => "Down",
            Key.PrintScreen => "PrintScreen",
            _ => null
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var actions = Actions.Select(row => row.ToDefinition()).ToArray();
            SavedSettings = ActionSettingsStore.Prepare(new ActionSettingsDocument(ActionSettingsDocument.CurrentSchemaVersion, actions));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class CaptureState
    {
        public List<Key> Order { get; } = new();
        public HashSet<Key> Held { get; } = new();
        public bool HadMainKey { get; set; }
        public bool Completed { get; set; }
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

public sealed class ActionEditorRow : INotifyPropertyChanged
{
    private string _title;
    private string? _shortcut;
    private bool _enabled;

    public ActionEditorRow(ActionDefinition action)
    {
        Id = action.Id;
        Kind = action.Kind;
        _title = action.Title;
        _shortcut = action.Shortcut;
        _enabled = action.Enabled;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string? Shortcut { get => _shortcut; set => Set(ref _shortcut, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public ActionDefinition ToDefinition() => new(Id, Kind, Title, Shortcut, Enabled);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
