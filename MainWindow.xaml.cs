using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LunaDesktopHelper;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private uint _initialClipboardSequence;
    private DateTime _captureStarted;
    private bool _capturing;
    private bool _expanded;
    private bool _dragging;
    private bool _dragMoved;
    private bool _dragThresholdLogged;
    private bool _pasteInProgress;
    private ClipboardImageDropImage? _preparedDropImage;
    private bool _typelessInProgress;
    private Point _dragStart;
    private Point _windowStart;
    private const int CaptureTimeoutSeconds = 120;
    private static string PositionFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaDesktopHelper", "position.txt");
    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaDesktopHelper", "settings.json");
    private static string DiagnosticLogPath => Path.Combine(AppContext.BaseDirectory, "luna-ui-diagnostic.log");

    public MainWindow()
    {
        ResetDiagnosticLog();
        InitializeComponent();
        _clipboardTimer.Tick += ClipboardTimer_Tick;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(exStyle | WsExNoActivate | WsExToolWindow));
        PlaceAtSavedEdge();
        LogDiagnostic($"SendInput INPUT layout size={Marshal.SizeOf<Input>()}; expected={(IntPtr.Size == 8 ? 40 : 28)}; pointerSize={IntPtr.Size}");
        LogDiagnostic($"GUITHREADINFO layout size={Marshal.SizeOf<GuiThreadInfo>()}; expected={(IntPtr.Size == 8 ? 72 : 48)}; pointerSize={IntPtr.Size}");
        var actionSettings = ActionSettingsStore.ReadForDisplay(out var actionSettingsError);
        UpdateActionCard(actionSettings, actionSettingsError);
        LogDiagnostic($"Action settings loaded path={ActionSettingsStore.SettingsPath} title={actionSettings.Title} hotkey={actionSettings.Hotkey} error={actionSettingsError}");
        LogDiagnostic($"RightAlt extended scan code on helper layout=0x{MapVirtualKey(VkRMenu, MapvkVkToVscEx, GetKeyboardLayout(0)):X4} (expected E038)");
        LogDiagnostic($"Ctrl+V scan codes on helper layout ctrl=0x{MapVirtualKey(VkLControl, MapvkVkToVscEx, GetKeyboardLayout(0)):X4} v=0x{MapVirtualKey(VkV, MapvkVkToVscEx, GetKeyboardLayout(0)):X4}");
        LogDiagnostic($"Loaded hwnd=0x{hwnd.ToInt64():X} visible={IsVisible} visibility={Visibility} position=({Left:0.##},{Top:0.##}) size=({Width:0.##},{Height:0.##}) capsule={Capsule.Visibility} menu={Menu.Visibility} exStyle=0x{(exStyle | WsExNoActivate | WsExToolWindow):X}");
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _clipboardTimer.Stop();

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
        Width = 288;
        Height = 424;
        Shell.CornerRadius = new CornerRadius(22);
        Menu.Visibility = Visibility.Visible;
        Capsule.Visibility = Visibility.Collapsed;
        KeepOnScreen();
        LogDiagnostic($"Expand end position=({Left:0.##},{Top:0.##}) size=({Width:0.##},{Height:0.##}) visibility={Visibility} capsule={Capsule.Visibility} menu={Menu.Visibility} pasteEnabled={PasteButton.IsEnabled} pasteVisibility={PasteButton.Visibility} pasteHitTest={PasteButton.IsHitTestVisible} capturing={_capturing}");
        PrepareDropImageIfNeeded();
    }

    private void PrepareDropImageIfNeeded()
    {
        try
        {
            var sequence = GetClipboardSequenceNumber();
            if (_preparedDropImage?.ClipboardSequence == sequence && File.Exists(_preparedDropImage.ImagePath)) return;
            _preparedDropImage = Clipboard.ContainsImage()
                ? ClipboardImageDropService.PrepareImage(sequence)
                : null;
            if (_preparedDropImage is not null)
                LogDiagnostic($"Prepared PNG before click file={_preparedDropImage.ImagePath} clipboardSequence={sequence}");
        }
        catch (Exception ex)
        {
            _preparedDropImage = null;
            LogDiagnostic($"Could not pre-encode clipboard image: {ex}");
        }
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    private void ActionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _pasteInProgress || _typelessInProgress)
        {
            StatusText.Text = "请先完成当前操作，再打开设置。";
            return;
        }

        var restoreExpanded = _expanded;
        var initial = ActionSettingsStore.ReadForDisplay(out var initialError);
        var settingsWindow = new ActionSettingsWindow(initial, initialError);
        try
        {
            // Text editing needs an activated window. Hide the no-activate helper while
            // configuring; saving never sends a shortcut. The user must refocus the target
            // application before invoking the action afterward.
            Collapse();
            Hide();
            if (settingsWindow.ShowDialog() == true && settingsWindow.SavedSettings is { } saved)
            {
                UpdateActionCard(saved, null);
                StatusText.Text = settingsWindow.SaveNotice is { } notice
                    ? $"{notice} 请重新聚焦目标应用后再触发动作。"
                    : "设置已保存。请重新聚焦目标应用后再触发动作。";
                LogDiagnostic($"Action settings saved title={saved.Title} hotkey={saved.Hotkey}");
            }
            else
            {
                var current = ActionSettingsStore.ReadForDisplay(out var error);
                UpdateActionCard(current, error);
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
            Show();
            if (restoreExpanded) Expand();
            else Collapse();
        }
    }

    private void UpdateActionCard(ActionSettings settings, string? error)
    {
        TypelessTitleText.Text = settings.Title;
        TypelessHotkeyText.Text = error is null
            ? $"先聚焦输入框 · {settings.Hotkey}"
            : "设置无效 · 点齿轮修复";
        if (error is not null) StatusText.Text = error;
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

    private void SetCaptureWaitingUi(bool waiting)
    {
        CaptureButton.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        CaptureRecoveryButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        PasteButton.IsEnabled = !waiting;
        TypelessButton.IsEnabled = !waiting;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) return;
        LogDiagnostic($"Capture_Click entered sender={sender.GetType().FullName} expanded={_expanded} visibility={Visibility} position=({Left:0.##},{Top:0.##}) size=({Width:0.##},{Height:0.##})");
        _capturing = true;
        _initialClipboardSequence = GetClipboardSequenceNumber();
        _captureStarted = DateTime.UtcNow;
        StatusText.Text = "截图中…取消后可点‘结束截图等待’。";
        SetCaptureWaitingUi(true);
        try
        {
            // Keep the menu expanded so ending screenshot wait remains directly reachable
            // after Esc, which does not provide a reliable cancel callback here.
            LogDiagnostic("Capture_Click kept helper menu open; waiting for dispatcher idle");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            LogDiagnostic("Capture_Click invoking Win+Shift+S shortcut");
            SendWinShiftS();
            _clipboardTimer.Start();
            LogDiagnostic("Capture_Click shortcut dispatched; clipboard watcher started");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Capture_Click failed; restoring helper. Exception={ex}");
            _clipboardTimer.Stop();
            _capturing = false;
            SetCaptureWaitingUi(false);
            StatusText.Text = "无法启动系统截图，请重试。";
        }
    }

    private void EndCaptureWait_Click(object sender, RoutedEventArgs e)
    {
        if (!_capturing) return;
        LogDiagnostic("EndCaptureWait_Click: user ended screenshot wait; re-enabling screenshot action");
        FinishCapture("已结束截图等待；可重新选择区域截图。", false);
    }

    private void PasteButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        LogMouseEvent("PasteButton.PreviewMouseLeftButtonDown", sender, e);
        LogDiagnostic($"PasteButton down state enabled={PasteButton.IsEnabled} visibility={PasteButton.Visibility} hitTest={PasteButton.IsHitTestVisible} menu={Menu.Visibility} expanded={_expanded} capturing={_capturing} pasteInProgress={_pasteInProgress}");
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        StartClipboardImageDrop();
    }

    private void PasteButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        LogMouseEvent("PasteButton.PreviewMouseLeftButtonUp", sender, e);
        LogDiagnostic($"PasteButton up state enabled={PasteButton.IsEnabled} visibility={PasteButton.Visibility} hitTest={PasteButton.IsHitTestVisible} menu={Menu.Visibility} expanded={_expanded} capturing={_capturing} pasteInProgress={_pasteInProgress}");
    }

    private void StartClipboardImageDrop()
    {
        LogDiagnostic($"StartClipboardImageDrop enabled={PasteButton.IsEnabled} visibility={PasteButton.Visibility} menu={Menu.Visibility} capturing={_capturing} inProgress={_pasteInProgress}");
        if (_pasteInProgress || _capturing)
        {
            LogDiagnostic("Paste_Click ignored because an operation is already in progress");
            return;
        }

        var target = GetForegroundWindow();
        if (!TryDescribePasteTarget(target, out var targetDescription, out var reason))
        {
            StatusText.Text = reason;
            LogDiagnostic($"Paste_Click skipped: {reason} hwnd=0x{target.ToInt64():X}");
            return;
        }

        var helperHidden = false;
        ClipboardImageDropPreparation preparation;
        try
        {
            var sequence = GetClipboardSequenceNumber();
            var image = _preparedDropImage;
            if (image is null || image.ClipboardSequence != sequence || !File.Exists(image.ImagePath))
            {
                image = ClipboardImageDropService.PrepareImage(sequence);
                _preparedDropImage = image;
            }
            preparation = ClipboardImageDropService.Prepare(target, image);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            LogDiagnostic($"Paste_Click preparation failed: {ex}");
            return;
        }

        _pasteInProgress = true;
        PasteButton.IsEnabled = false;
        TypelessButton.IsEnabled = false;

        LogDiagnostic($"Paste_Click entered target=0x{target.ToInt64():X} {targetDescription}; file={preparation.ImagePath}; point=({preparation.ScreenPoint.X:0.##},{preparation.ScreenPoint.Y:0.##}); pointSource={preparation.PointSource}");
        LogTargetThreadGuiInfo("Paste before hide", target);
        try
        {
            // The helper is WS_EX_NOACTIVATE, so the target should remain foreground.
            // Hide the menu before starting the OLE file drop.
            Hide();
            helperHidden = true;
            var foregroundAfterHide = GetForegroundWindow();
            LogTargetThreadGuiInfo("Paste after hide", foregroundAfterHide);
            if (foregroundAfterHide != target)
            {
                StatusText.Text = "前台应用已变化，请先返回目标应用后再试。";
                LogDiagnostic($"Paste_Click aborted: foreground changed from 0x{target.ToInt64():X} to 0x{foregroundAfterHide.ToInt64():X}");
                ShowActivated = false;
                Show();
                helperHidden = false;
                return;
            }

            if (!TryDescribePasteTarget(foregroundAfterHide, out var confirmedTarget, out reason))
            {
                StatusText.Text = reason;
                LogDiagnostic($"Paste_Click aborted: target is no longer eligible; detail={reason}");
                ShowActivated = false;
                Show();
                helperHidden = false;
                return;
            }

            var drop = ClipboardImageDropService.Drop(this, preparation);
            LogDiagnostic($"Paste_Click OLE drop returned {drop.Effect} durationMs={drop.DurationMs} queries={drop.QueryCount} feedback={drop.FeedbackCount} lastFeedback={drop.LastFeedbackEffect} for 0x{foregroundAfterHide.ToInt64():X} {confirmedTarget}; file={preparation.ImagePath}");
            if ((drop.Effect & DragDropEffects.Copy) == 0)
                throw new InvalidOperationException("目标应用没有接受图片文件；请确认输入框仍处于焦点。");
            StatusText.Text = "目标应用已接受 PNG 文件投递。";
            ShowActivated = false;
            Show();
            helperHidden = false;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Paste_Click failed; restoring helper. Exception={ex}");
            StatusText.Text = $"图片投递失败：{ex.Message.Trim()}";
            if (helperHidden)
            {
                ShowActivated = false;
                Show();
                helperHidden = false;
            }
        }
        finally
        {
            PasteButton.IsEnabled = true;
            TypelessButton.IsEnabled = true;
            _pasteInProgress = false;
        }
    }

    private static bool TryDescribePasteTarget(IntPtr hwnd, out string description, out string reason)
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
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            reason = "当前前台是桌面或任务栏，请先返回要粘贴的应用。";
            return false;
        }

        var processName = "unknown";
        try { processName = Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { }
        description = $"pid={pid} process={processName} class={className}";
        reason = "";
        return true;
    }

    private async void Typeless_Click(object sender, RoutedEventArgs e)
    {
        if (_typelessInProgress || _capturing || _pasteInProgress) return;

        ActionSettings settings;
        ShortcutSpec chord;
        try
        {
            settings = ActionSettingsStore.ReadForAction();
            chord = ShortcutParser.Parse(settings.Hotkey);
            UpdateActionCard(settings, null);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"动作设置无效，未发送快捷键：{ex.Message}";
            TypelessHotkeyText.Text = "设置无效 · 点齿轮修复";
            LogDiagnostic($"Configured action rejected without fallback: {ex}");
            return;
        }

        var target = GetForegroundWindow();
        if (!TryDescribePasteTarget(target, out var targetDescription, out var reason))
        {
            StatusText.Text = reason;
            LogDiagnostic($"Typeless_Click skipped: {reason} hwnd=0x{target.ToInt64():X}");
            return;
        }

        _typelessInProgress = true;
        TypelessButton.IsEnabled = false;
        var helperHidden = false;
        LogDiagnostic($"Typeless_Click entered title={settings.Title} chord={chord.Label} target=0x{target.ToInt64():X} {targetDescription}");
        try
        {
            Hide();
            helperHidden = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var foregroundAfterHide = GetForegroundWindow();
            if (foregroundAfterHide != target)
                throw new InvalidOperationException("前台应用已变化；请返回目标输入框后重试。");
            if (!TryDescribePasteTarget(foregroundAfterHide, out var confirmedTarget, out reason))
                throw new InvalidOperationException(reason);

            var inserted = SendTypelessTap(chord, foregroundAfterHide);
            LogDiagnostic($"Typeless_Click SendInput inserted {inserted} events for {settings.Title}/{chord.Label} to 0x{foregroundAfterHide.ToInt64():X} {confirmedTarget}; target action was not observed");
            StatusText.Text = $"已向目标应用发送 {chord.Label}；未确认“{settings.Title}”是否生效。";
            ShowActivated = false;
            Show();
            helperHidden = false;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Typeless_Click failed; restoring helper. Exception={ex}");
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
            TypelessButton.IsEnabled = true;
            _typelessInProgress = false;
        }
    }

    private static void EnsureTypelessSettingsFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            if (!File.Exists(SettingsFile))
                File.WriteAllText(SettingsFile, "{\r\n  \"typelessHotkey\": \"RightAlt\"\r\n}\r\n");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Could not create default Typeless settings file. Exception={ex}");
        }
    }

    private static uint SendTypelessTap(ShortcutSpec chord, IntPtr targetWindow)
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
        if (targetLayout == IntPtr.Zero) throw new InvalidOperationException("无法读取目标应用的键盘布局，未发送 Typeless 快捷键。");
        var scanCodes = keysDown.Select(key => GetExtendedScanCode(key, targetLayout)).ToArray();
        var press = scanCodes.Select(scan => ScanCodeInput(scan, keyUp: false)).ToArray();
        var release = scanCodes.Reverse().Select(scan => ScanCodeInput(scan, keyUp: true)).ToArray();
        var pressed = SendInput((uint)press.Length, press, inputSize);
        if (pressed != press.Length)
        {
            var nativeError = Marshal.GetLastWin32Error();
            var cleanupInserted = SendInput((uint)release.Length, release, inputSize);
            var errorDetail = nativeError == 0 ? "Windows 未提供扩展错误码（可能被 UIPI 阻止）" : $"Win32 错误 {nativeError}";
            throw new InvalidOperationException($"快捷键按下仅插入 {pressed}/{press.Length} 个事件；释放补救插入 {cleanupInserted}/{release.Length} 个事件。{errorDetail}。");
        }

        // A global shortcut monitor may inspect the currently held modifiers when
        // it receives the final key. Give it time to observe the chord before release.
        uint released = 0;
        try
        {
            Thread.Sleep(100);
        }
        finally
        {
            released = SendInput((uint)release.Length, release, inputSize);
        }
        if (released == release.Length) return pressed + released;

        var releaseError = Marshal.GetLastWin32Error();
        var retryInserted = SendInput((uint)release.Length, release, inputSize);
        var releaseDetail = releaseError == 0 ? "Windows 未提供扩展错误码（可能被 UIPI 阻止）" : $"Win32 错误 {releaseError}";
        throw new InvalidOperationException($"快捷键释放仅插入 {released}/{release.Length} 个事件；释放补救插入 {retryInserted}/{release.Length} 个事件。{releaseDetail}。");
    }

    private static uint GetExtendedScanCode(ushort virtualKey, IntPtr keyboardLayout)
    {
        var scan = MapVirtualKey(virtualKey, MapvkVkToVscEx, keyboardLayout);
        if (scan == 0) throw new InvalidOperationException($"无法映射虚拟键 0x{virtualKey:X2} 到扫描码。");
        if (virtualKey == VkRMenu && scan != 0xE038)
            throw new InvalidOperationException($"当前键盘布局 Right Alt 映射为 0x{scan:X4}，与预期扩展扫描码 E0 38 不符。");
        return scan;
    }

    private static Input ScanCodeInput(uint extendedScanCode, bool keyUp)
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
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        if (!_capturing) return;
        if (DateTime.UtcNow - _captureStarted > TimeSpan.FromSeconds(CaptureTimeoutSeconds))
        {
            FinishCapture("等待超时，已恢复截图按钮。系统快捷键模式无法确认是否取消。", false);
            return;
        }
        if (GetClipboardSequenceNumber() != _initialClipboardSequence)
        {
            // Sequence changes can come from any app. Only count it as a completed snip if the clipboard now exposes an image.
            try
            {
                if (Clipboard.ContainsImage()) FinishCapture("截图已复制到剪贴板，可回目标应用粘贴。", true);
            }
            catch (ExternalException) { }
        }
    }

    private void FinishCapture(string message, bool success)
    {
        _clipboardTimer.Stop();
        _capturing = false;
        SetCaptureWaitingUi(false);
        StatusText.Text = message;
        LogDiagnostic($"FinishCapture success={success}; helper remains visible={IsVisible}; message={message}");
        // Only a manual menu-close click changes the expanded state.
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

    private static void SendWinShiftS()
    {
        KeybdEvent(VkLwin, 0, 0, UIntPtr.Zero);
        KeybdEvent(VkShift, 0, 0, UIntPtr.Zero);
        KeybdEvent((byte)'S', 0, 0, UIntPtr.Zero);
        KeybdEvent((byte)'S', 0, KeyeventfKeyup, UIntPtr.Zero);
        KeybdEvent(VkShift, 0, KeyeventfKeyup, UIntPtr.Zero);
        KeybdEvent(VkLwin, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private static uint SendCtrlV(IntPtr targetWindow)
    {
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;
        var inputSize = Marshal.SizeOf<Input>();
        if (inputSize != expectedSize)
            throw new InvalidOperationException($"INPUT 布局大小异常：{inputSize}，预期 {expectedSize} bytes。");

        // SendInput does not clear keys already held down. Avoid combining with an
        // existing physical/remote Ctrl or V hold, which could make the sequence ambiguous.
        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0 || (GetAsyncKeyState(VkV) & 0x8000) != 0)
            throw new InvalidOperationException("检测到 Ctrl 或 V 已处于按下状态；请松开后重试。");

        var targetThread = GetWindowThreadProcessId(targetWindow, out _);
        var targetLayout = targetThread == 0 ? IntPtr.Zero : GetKeyboardLayout(targetThread);
        if (targetLayout == IntPtr.Zero) throw new InvalidOperationException("无法读取目标应用的键盘布局，未发送粘贴快捷键。");
        var ctrlScan = GetExtendedScanCode(VkLControl, targetLayout);
        var vScan = GetExtendedScanCode(VkV, targetLayout);
        var inputs = new[]
        {
            ScanCodeInput(ctrlScan, keyUp: false),
            ScanCodeInput(vScan, keyUp: false),
            ScanCodeInput(vScan, keyUp: true),
            ScanCodeInput(ctrlScan, keyUp: true)
        };
        var inserted = SendInput((uint)inputs.Length, inputs, inputSize);
        if (inserted == inputs.Length) return inserted;

        var nativeError = Marshal.GetLastWin32Error();
        // A short insertion may have left one or more synthetic keys down. Send an
        // explicit cleanup batch; report both counts because cleanup itself can fail.
        var cleanup = new[]
        {
            ScanCodeInput(vScan, keyUp: true),
            ScanCodeInput(ctrlScan, keyUp: true)
        };
        var cleanupInserted = SendInput((uint)cleanup.Length, cleanup, inputSize);
        var errorDetail = nativeError == 0 ? "Windows 未提供扩展错误码（可能被 UIPI 阻止）" : $"Win32 错误 {nativeError}";
        throw new InvalidOperationException($"SendInput 仅插入 {inserted}/4 个事件；释放补救插入 {cleanupInserted}/2 个事件。{errorDetail}。不会报告为粘贴成功。");
    }

    private static void LogTargetThreadGuiInfo(string label, IntPtr hwnd)
    {
        try
        {
            var threadId = GetWindowThreadProcessId(hwnd, out var pid);
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            var ok = threadId != 0 && GetGUIThreadInfo(threadId, ref info);
            GetWindowThreadProcessId(info.Focus, out var focusPid);
            var focusClassBuffer = new System.Text.StringBuilder(128);
            if (info.Focus != IntPtr.Zero) GetClassName(info.Focus, focusClassBuffer, focusClassBuffer.Capacity);
            LogDiagnostic($"{label} GUI thread={threadId} pid={pid} ok={ok} flags=0x{info.Flags:X} active=0x{info.Active.ToInt64():X} focus=0x{info.Focus.ToInt64():X} focusPid={focusPid} focusClass={focusClassBuffer} capture=0x{info.Capture.ToInt64():X} menu=0x{info.MenuOwner.ToInt64():X} moveSize=0x{info.MoveSize.ToInt64():X} caret=0x{info.Caret.ToInt64():X}");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"{label} GetGUIThreadInfo failed: {ex}");
        }
    }

    private const int GwlExstyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfScancode = 0x0008;
    private const uint InputKeyboard = 1;
    private const uint MapvkVkToVscEx = 4;
    private const byte VkLwin = 0x5B, VkShift = 0x10, VkControl = 0x11, VkV = 0x56;
    private const ushort VkLControl = 0xA2, VkRMenu = 0xA5;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW", ExactSpelling = true, SetLastError = true)] private static extern uint MapVirtualKey(uint code, uint mapType, IntPtr keyboardLayout);
    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout", ExactSpelling = true)] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo", ExactSpelling = true, SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "SendInput", ExactSpelling = true, SetLastError = true)] private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "keybd_event", ExactSpelling = true)] private static extern void KeybdEvent(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
