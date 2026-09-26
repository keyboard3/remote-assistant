using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LunaDesktopHelper;

public partial class MainWindow : Window
{
    private bool _expanded;
    private bool _dragging;
    private bool _dragMoved;
    private bool _dragThresholdLogged;
    private bool _shortcutInProgress;
    private Point _dragStart;
    private Point _windowStart;
    private long _windowExStyle;
    private ActionSettingsDocument _settings = ActionSettingsDocument.Default;
    private readonly Dictionary<string, Button> _actionButtons = new(StringComparer.Ordinal);
    private static string PositionFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaDesktopHelper", "position.txt");
    private static string DiagnosticLogPath => Path.Combine(AppContext.BaseDirectory, "luna-ui-diagnostic.log");

    public MainWindow()
    {
        ResetDiagnosticLog();
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        _windowExStyle = exStyle | WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(_windowExStyle));
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        PlaceAtSavedEdge();
        LogDiagnostic($"SendInput INPUT layout size={Marshal.SizeOf<Input>()}; expected={(IntPtr.Size == 8 ? 40 : 28)}; pointerSize={IntPtr.Size}");
        _settings = ActionSettingsStore.LoadOrCreate(out var actionSettingsError, out var migrationBackupPath);
        UpdateActionCards(_settings, actionSettingsError);
        LogDiagnostic($"Action settings loaded path={ActionSettingsStore.SettingsPath} actions={_settings.Actions.Count} migrationBackup={migrationBackupPath} error={actionSettingsError}");
        LogDiagnostic($"RightAlt extended scan code on helper layout=0x{MapVirtualKey(VkRMenu, MapvkVkToVscEx, GetKeyboardLayout(0)):X4} (expected E038)");
        LogDiagnostic($"Ctrl+V scan codes on helper layout ctrl=0x{MapVirtualKey(VkLControl, MapvkVkToVscEx, GetKeyboardLayout(0)):X4} v=0x{MapVirtualKey(VkV, MapvkVkToVscEx, GetKeyboardLayout(0)):X4}");
        LogDiagnostic($"Loaded hwnd=0x{hwnd.ToInt64():X} visible={IsVisible} visibility={Visibility} position=({Left:0.##},{Top:0.##}) size=({Width:0.##},{Height:0.##}) capsule={Capsule.Visibility} menu={Menu.Visibility} exStyle=0x{_windowExStyle:X}");
    }

    private void PlaceAtSavedEdge()
    {
        var primary = SystemParameters.WorkArea;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        try
        {
            var saved = File.ReadAllLines(PositionFile);
            Left = double.Parse(saved[0], System.Globalization.CultureInfo.InvariantCulture);
            Top = double.Parse(saved[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { Left = double.NaN; Top = double.NaN; }
        if (double.IsNaN(Left) || Left < virtualLeft - Width + 3 || Left > virtualRight - 3) Left = primary.Right - 5;
        if (double.IsNaN(Top) || Top < virtualTop || Top > virtualBottom - Height) Top = primary.Top + primary.Height * 0.42;
        SnapToEdge();
    }

    private void Expand()
    {
        if (_expanded) return;
        LogDiagnostic($"Expand begin position=({Left:0.##},{Top:0.##}) size=({Width:0.##},{Height:0.##}) visibility={Visibility} capsule={Capsule.Visibility} menu={Menu.Visibility}");
        _expanded = true;
        Width = 318;
        Shell.CornerRadius = new CornerRadius(22);
        Menu.Visibility = Visibility.Visible;
        Capsule.Visibility = Visibility.Collapsed;
        ResizeExpandedToContent();
        KeepOnScreen();
        LogDiagnostic($"Expand end position=({Left:0.##},{Top:0.##}) size=({Width:0.##},{Height:0.##}) visibility={Visibility} capsule={Capsule.Visibility} menu={Menu.Visibility} actions={_actionButtons.Count}");
    }

    private void ResizeExpandedToContent()
    {
        if (!_expanded) return;
        const double shellVerticalPadding = 26;
        var contentWidth = Math.Max(240, Width - 30);
        MenuContent.Measure(new Size(contentWidth, double.PositiveInfinity));
        var desiredHeight = Math.Ceiling(MenuContent.DesiredSize.Height + shellVerticalPadding);
        var maximumHeight = Math.Max(320, Math.Min(620, SystemParameters.WorkArea.Height - 24));
        Height = Math.Clamp(desiredHeight, 300, maximumHeight);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        LogDiagnostic("Exit requested from menu");
        Close();
    }

    private void ActionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcutInProgress)
        {
            StatusText.Text = "请先完成当前操作，再打开设置。";
            return;
        }

        var restoreExpanded = _expanded;
        var initial = _settings;
        var settingsWindow = new ActionSettingsWindow(initial, null)
        {
            Owner = this,
            ShowActivated = true,
            Topmost = true
        };
        try
        {
            // Text editing needs an activated window. Keep the no-activate helper collapsed
            // and visible as the dialog owner so the settings window has a reliable z-order
            // relationship. Saving never sends a shortcut.
            Collapse();
            LogDiagnostic($"Action settings opening owner=0x{new WindowInteropHelper(this).Handle.ToInt64():X} topmost={settingsWindow.Topmost}");
            var dialogResult = settingsWindow.ShowDialog();
            LogDiagnostic($"Action settings closed result={dialogResult}");
            if (dialogResult == true && settingsWindow.SavedSettings is { } saved)
            {
                _settings = ActionSettingsStore.Save(saved, out var backupPath);
                UpdateActionCards(_settings, null);
                StatusText.Text = backupPath is null ? "设置已保存。" : $"设置已保存；旧文件已备份到 {backupPath}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"无法打开动作设置：{ex.Message}";
            LogDiagnostic($"ActionSettings_Click failed: {ex}");
        }
        finally
        {
            ShowActivated = false;
            if (restoreExpanded) Expand();
            else Collapse();
        }
    }

    private void UpdateActionCards(ActionSettingsDocument settings, string? error)
    {
        ActionsPanel.Children.Clear();
        _actionButtons.Clear();
        foreach (var action in settings.Actions)
        {
            var button = CreateActionButton(action);
            _actionButtons[action.Id] = button;
            ActionsPanel.Children.Add(button);
        }
        ResizeExpandedToContent();
        if (error is not null) StatusText.Text = error;
    }

    private Button CreateActionButton(ActionDefinition action)
    {
        var button = new Button
        {
            Style = (Style)FindResource("MenuActionButtonStyle"),
            Margin = new Thickness(0, 0, 0, 10),
            IsEnabled = action.Enabled,
            ToolTip = $"执行组合键 {action.Shortcut}。"
        };
        AutomationProperties.SetName(button, action.Title);
        button.Click += async (_, _) => await ExecuteShortcutActionAsync(action);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = (Brush)new BrushConverter().ConvertFrom("#30463743")! };
        icon.Child = new TextBlock { Text = "⌨", Foreground = (Brush)FindResource("Accent"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);
        var text = new StackPanel { Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock { Text = action.Title, Foreground = (Brush)FindResource("Ink"), FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = $"执行 · {action.Shortcut}", Foreground = (Brush)FindResource("MutedInk"), FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0) });
        grid.Children.Add(text);
        var arrow = new TextBlock { Text = "›", Foreground = (Brush)new BrushConverter().ConvertFrom("#A8B3C3")!, FontSize = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);
        button.Content = grid;
        return button;
    }

    private void Collapse()
    {
        _expanded = false;
        Width = 88;
        Height = 88;
        Shell.CornerRadius = new CornerRadius(44);
        Menu.Visibility = Visibility.Collapsed;
        Capsule.Visibility = Visibility.Visible;
        KeepOnScreen();
    }

    private static bool TryDescribeShortcutTarget(IntPtr hwnd, out string description, out string reason)
    {
        description = "";
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            reason = "没有可用的前台应用窗口。";
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId)
        {
            reason = "当前前台是助手本身或没有可用应用。";
            return false;
        }

        var classBuffer = new System.Text.StringBuilder(128);
        GetClassName(hwnd, classBuffer, classBuffer.Capacity);
        var className = classBuffer.ToString();
        var processName = "unknown";
        try { processName = Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { }
        description = $"pid={pid} process={processName} class={className}";
        reason = "";
        return true;
    }

    private async Task ExecuteShortcutActionAsync(ActionDefinition settings)
    {
        if (_shortcutInProgress) return;

        ShortcutSpec chord;
        try
        {
            chord = ShortcutParser.Parse(settings.Shortcut ?? throw new InvalidDataException("动作缺少执行快捷键。"));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"动作设置无效，未发送快捷键：{ex.Message}";
            LogDiagnostic($"Configured action rejected without fallback: {ex}");
            return;
        }

        var target = GetForegroundWindow();
        if (!TryDescribeShortcutTarget(target, out var targetDescription, out var reason))
        {
            StatusText.Text = reason;
            LogDiagnostic($"Shortcut action skipped: {reason} hwnd=0x{target.ToInt64():X}");
            return;
        }

        _shortcutInProgress = true;
        if (_actionButtons.TryGetValue(settings.Id, out var actionButton)) actionButton.IsEnabled = false;
        var helperHidden = false;
        LogDiagnostic($"Shortcut action entered id={settings.Id} title={settings.Title} chord={chord.Label} target=0x{target.ToInt64():X} {targetDescription}");
        try
        {
            Hide();
            helperHidden = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var foregroundAfterHide = GetForegroundWindow();
            if (foregroundAfterHide != target)
                throw new InvalidOperationException("前台应用已变化；请返回目标输入框后重试。");
            if (!TryDescribeShortcutTarget(foregroundAfterHide, out var confirmedTarget, out reason))
                throw new InvalidOperationException(reason);

            var inserted = SendShortcutTap(chord, foregroundAfterHide);
            StatusText.Text = $"已执行 {chord.Label}；请确认“{settings.Title}”的结果。";
            LogDiagnostic($"Shortcut action SendInput inserted {inserted} events for {settings.Title}/{chord.Label} to 0x{foregroundAfterHide.ToInt64():X} {confirmedTarget}");
            ShowActivated = false;
            Show();
            helperHidden = false;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Shortcut action failed; restoring helper. Exception={ex}");
            StatusText.Text = ex.Message;
            if (helperHidden)
            {
                ShowActivated = false;
                Show();
                helperHidden = false;
            }
        }
        finally
        {
            if (_actionButtons.TryGetValue(settings.Id, out actionButton)) actionButton.IsEnabled = settings.Enabled;
            _shortcutInProgress = false;
        }
    }

    private static uint SendShortcutTap(ShortcutSpec chord, IntPtr targetWindow)
    {
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;
        var inputSize = Marshal.SizeOf<Input>();
        if (inputSize != expectedSize)
            throw new InvalidOperationException($"INPUT 布局大小异常：{inputSize}，预期 {expectedSize} bytes。");

        var keysDown = chord.Modifiers.Append(chord.Key).ToArray();
        foreach (var key in keysDown)
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new InvalidOperationException($"检测到快捷键中的按键 0x{key:X2} 已按下；请松开后重试。");

        var targetThread = GetWindowThreadProcessId(targetWindow, out _);
        var targetLayout = targetThread == 0 ? IntPtr.Zero : GetKeyboardLayout(targetThread);
        if (targetLayout == IntPtr.Zero) throw new InvalidOperationException("无法读取目标应用的键盘布局，未执行快捷键。");
        var scanCodes = keysDown.Select(key => GetExtendedScanCode(key, targetLayout)).ToArray();
        // PowerToys Keyboard Manager treats 0x101 as its own shortcut input and skips
        // remapping it. This keeps a configured logical chord intact when physical
        // Ctrl/Alt keys are swapped in Keyboard Manager. It is a PowerToys convention,
        // not a Windows SendInput flag.
        var extraInfo = PowerToysKeyboardManagerRunning() ? new UIntPtr(0x101u) : UIntPtr.Zero;
        LogDiagnostic($"Shortcut injection chord={chord.Label} scans={string.Join(',', scanCodes.Select(scan => $"0x{scan:X4}"))} powerToysCompatibility={extraInfo != UIntPtr.Zero}");
        var modifierOnly = IsModifierVirtualKey(chord.Key);
        var heldKeys = modifierOnly ? keysDown : chord.Modifiers;
        var heldScans = modifierOnly ? scanCodes : scanCodes[..^1];
        var modifierDown = heldScans.Select(scan => ScanCodeInput(scan, keyUp: false, extraInfo)).ToArray();
        var modifierUp = heldScans.Reverse().Select(scan => ScanCodeInput(scan, keyUp: true, extraInfo)).ToArray();
        var cleanup = scanCodes.Reverse().Select(scan => ScanCodeInput(scan, keyUp: true, extraInfo)).ToArray();
        uint inserted = 0;

        try
        {
            if (modifierDown.Length > 0)
            {
                var downCount = SendInput((uint)modifierDown.Length, modifierDown, inputSize);
                inserted += downCount;
                if (downCount != modifierDown.Length) ThrowShortcutInsertionFailure("修饰键按下", downCount, modifierDown.Length, cleanup, inputSize);
                Thread.Sleep(20);
                var states = heldKeys.Select(key => new
                {
                    Original = key,
                    Effective = NormalizeVirtualKey(key),
                    Active = (GetAsyncKeyState(key) & 0x8000) != 0 &&
                             (GetAsyncKeyState(NormalizeVirtualKey(key)) & 0x8000) != 0
                }).ToArray();
                LogDiagnostic($"Shortcut modifiers active chord={chord.Label} states={string.Join(',', states.Select(state => $"0x{state.Original:X2}->0x{state.Effective:X2}:{state.Active}"))}");
                var inactive = states.Where(state => !state.Active).Select(state => $"0x{state.Original:X2}").ToArray();
                if (inactive.Length > 0)
                    throw new InvalidOperationException($"快捷键修饰键未生效（{string.Join(", ", inactive)}）；已取消发送主键，避免输入裸字符。可能有键盘增强程序拦截了模拟按键。");
            }

            if (!modifierOnly)
            {
                var mainScan = scanCodes[^1];
                var mainTap = new[] { ScanCodeInput(mainScan, keyUp: false, extraInfo), ScanCodeInput(mainScan, keyUp: true, extraInfo) };
                var mainCount = SendInput((uint)mainTap.Length, mainTap, inputSize);
                inserted += mainCount;
                if (mainCount != mainTap.Length) ThrowShortcutInsertionFailure("主键点击", mainCount, mainTap.Length, cleanup, inputSize);
                Thread.Sleep(20);
            }
            else
            {
                Thread.Sleep(100);
            }
        }
        finally
        {
            if (modifierUp.Length > 0)
            {
                var releaseCount = SendInput((uint)modifierUp.Length, modifierUp, inputSize);
                inserted += releaseCount;
                if (releaseCount != modifierUp.Length)
                {
                    var retryInserted = SendInput((uint)cleanup.Length, cleanup, inputSize);
                    LogDiagnostic($"Shortcut release incomplete inserted={releaseCount}/{modifierUp.Length} cleanup={retryInserted}/{cleanup.Length}");
                }
            }
        }
        return inserted;
    }

    private static void ThrowShortcutInsertionFailure(string stage, uint inserted, int expected, Input[] cleanup, int inputSize)
    {
        var nativeError = Marshal.GetLastWin32Error();
        var cleanupInserted = SendInput((uint)cleanup.Length, cleanup, inputSize);
        var errorDetail = nativeError == 0 ? "Windows 未提供扩展错误码（可能被 UIPI 阻止）" : $"Win32 错误 {nativeError}";
        throw new InvalidOperationException($"{stage}仅插入 {inserted}/{expected} 个事件；释放补救插入 {cleanupInserted}/{cleanup.Length} 个事件。{errorDetail}。");
    }

    private static bool IsModifierVirtualKey(ushort virtualKey)
        => virtualKey is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private static bool PowerToysKeyboardManagerRunning()
    {
        var processes = Process.GetProcessesByName("PowerToys.KeyboardManagerEngine");
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static ushort NormalizeVirtualKey(ushort virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => virtualKey
    };

    private static uint GetExtendedScanCode(ushort virtualKey, IntPtr keyboardLayout)
    {
        var scan = MapVirtualKey(virtualKey, MapvkVkToVscEx, keyboardLayout);
        if (scan == 0) throw new InvalidOperationException($"无法映射虚拟键 0x{virtualKey:X2} 到扫描码。");
        if (virtualKey == VkRMenu && scan != 0xE038)
            throw new InvalidOperationException($"当前键盘布局 Right Alt 映射为 0x{scan:X4}，与预期扩展扫描码 E0 38 不符。");
        return scan;
    }

    private static Input ScanCodeInput(uint extendedScanCode, bool keyUp, UIntPtr extraInfo)
    {
        var prefix = (extendedScanCode >> 8) & 0xFF;
        var flags = KeyeventfScancode;
        if (prefix is 0xE0 or 0xE1) flags |= KeyeventfExtendedKey;
        if (keyUp) flags |= KeyeventfKeyup;
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = 0,
                    ScanCode = (ushort)(extendedScanCode & 0xFF),
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = extraInfo
                }
            }
        };
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        LogMouseEvent("Shell.MouseLeftButtonDown", sender, e);
        if (_expanded && IsButtonSource(e.OriginalSource))
        {
            LogDiagnostic("Shell.MouseLeftButtonDown skipped: source is within a ButtonBase");
            return;
        }
        _dragging = true;
        _dragMoved = false;
        _dragThresholdLogged = false;
        _dragStart = PointToScreen(e.GetPosition(this));
        _windowStart = new Point(Left, Top);
        var captured = Shell.CaptureMouse();
        LogDiagnostic($"MouseCapture requested target=Shell result={captured} shellCaptured={Shell.IsMouseCaptured} actualCapture={Mouse.Captured?.GetType().FullName ?? "null"}");
        e.Handled = true;
    }

    private void Shell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        LogMouseEvent("Shell.MouseLeftButtonUp", sender, e);
        if (!_dragging)
        {
            LogDiagnostic("Shell.MouseLeftButtonUp ignored: _dragging=false");
            return;
        }
        var wasDrag = _dragMoved;
        _dragging = false;
        Shell.ReleaseMouseCapture();
        LogDiagnostic($"MouseRelease target=Shell shellCaptured={Shell.IsMouseCaptured} wasDrag={wasDrag} expanded={_expanded}");

        // CaptureMouse() retargets MouseLeftButtonUp to Shell, so a handler attached
        // to the Capsule child never receives it. Treat a short release on the collapsed
        // shell as the capsule click here; only movement beyond the drag threshold moves it.
        if (!wasDrag && !_expanded)
        {
            LogDiagnostic("MouseUp classified as capsule click");
            Expand();
            e.Handled = true;
            return;
        }

        SnapToEdge();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PositionFile)!);
            File.WriteAllLines(PositionFile, new[] { Left.ToString(System.Globalization.CultureInfo.InvariantCulture), Top.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        catch { }
        e.Handled = true;
    }

    private static bool IsButtonSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase) return true;
            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }
        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = PointToScreen(e.GetPosition(this));
        if (Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) > 4) _dragMoved = true;
        if (_dragMoved && !_dragThresholdLogged)
        {
            _dragThresholdLogged = true;
            LogDiagnostic($"Drag threshold crossed start=({_dragStart.X:0.##},{_dragStart.Y:0.##}) current=({p.X:0.##},{p.Y:0.##})");
        }
        Left = _windowStart.X + p.X - _dragStart.X;
        Top = _windowStart.Y + p.Y - _dragStart.Y;
        KeepOnScreen();
    }

    private void LogMouseEvent(string name, object sender, MouseButtonEventArgs e)
    {
        var local = e.GetPosition(this);
        var screen = PointToScreen(local);
        LogDiagnostic($"{name} sender={sender.GetType().FullName} originalSource={e.OriginalSource?.GetType().FullName ?? "null"} button={e.ChangedButton} state={e.ButtonState} clicks={e.ClickCount} handled={e.Handled} local=({local.X:0.##},{local.Y:0.##}) screen=({screen.X:0.##},{screen.Y:0.##}) actualCapture={Mouse.Captured?.GetType().FullName ?? "null"}");
    }

    private static void ResetDiagnosticLog()
    {
        try
        {
            File.WriteAllText(DiagnosticLogPath, $"Luna UI diagnostic start {DateTimeOffset.Now:O}; pid={Environment.ProcessId}; log={DiagnosticLogPath}{Environment.NewLine}");
        }
        catch { }
    }

    private static void LogDiagnostic(string message)
    {
        try { File.AppendAllText(DiagnosticLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch { }
    }

    private void SnapToEdge()
    {
        // Keep the whole capsule visible with a small inset. The previous implementation
        // intentionally left only five pixels inside the desktop, making the label invisible.
        var left = SystemParameters.VirtualScreenLeft;
        var right = left + SystemParameters.VirtualScreenWidth;
        const double edgeInset = 6;
        Left = Left + Width / 2 < left + (right - left) / 2
            ? left + edgeInset
            : right - Width - edgeInset;
        KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        const double edgeInset = 6;
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
        Left = Math.Clamp(Left, left + edgeInset, right - Width - edgeInset);
    }

    private const int GwlExstyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfScancode = 0x0008;
    private const uint InputKeyboard = 1;
    private const uint MapvkVkToVscEx = 4;
    private const ushort VkV = 0x56, VkLControl = 0xA2, VkRMenu = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParamL, ParamH;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW", ExactSpelling = true, SetLastError = true)] private static extern uint MapVirtualKey(uint code, uint mapType, IntPtr keyboardLayout);
    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout", ExactSpelling = true)] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", EntryPoint = "SendInput", ExactSpelling = true, SetLastError = true)] private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
