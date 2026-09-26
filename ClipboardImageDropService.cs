using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LunaDesktopHelper;

internal sealed record ClipboardImageDropImage(DataObject Data, string ImagePath, uint ClipboardSequence);
internal sealed record ClipboardImageDropPreparation(DataObject Data, string ImagePath, System.Windows.Point ScreenPoint, string PointSource);
internal sealed record ClipboardImageDropResult(DragDropEffects Effect, long DurationMs, int QueryCount, int FeedbackCount, DragDropEffects LastFeedbackEffect);

internal static class ClipboardImageDropService
{
    public static ClipboardImageDropImage PrepareImage(uint clipboardSequence)
    {
        BitmapSource image;
        try
        {
            image = Clipboard.GetImage() ?? throw new InvalidOperationException("剪贴板里没有可投递的图片，请先截图。");
            image.Freeze();
        }
        catch (ExternalException ex)
        {
            throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后再试。", ex);
        }

        var imagePath = SaveTemporaryPng(image);
        var data = new DataObject();
        var files = new StringCollection { imagePath };
        data.SetFileDropList(files);
        data.SetImage(image);

        return new ClipboardImageDropImage(data, imagePath, clipboardSequence);
    }

    public static ClipboardImageDropPreparation Prepare(IntPtr targetWindow, ClipboardImageDropImage image)
    {
        var (point, source) = FindDropPoint(targetWindow);
        return new ClipboardImageDropPreparation(image.Data, image.ImagePath, point, source);
    }

    public static ClipboardImageDropResult Drop(DependencyObject dragSource, ClipboardImageDropPreparation preparation)
    {
        if (!GetCursorPos(out var originalCursor))
            throw new InvalidOperationException("无法读取鼠标位置，未执行文件投递。");

        var dropX = checked((int)Math.Round(preparation.ScreenPoint.X));
        var dropY = checked((int)Math.Round(preparation.ScreenPoint.Y));
        if (!SetCursorPos(dropX, dropY))
            throw new InvalidOperationException("无法将鼠标定位到目标输入区域，未执行文件投递。");

        var clock = Stopwatch.StartNew();
        long? releasedAtMs = null;
        var queryCount = 0;
        var feedbackCount = 0;
        var lastFeedbackEffect = DragDropEffects.None;
        var nudge = false;
        var nudgeTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        nudgeTimer.Tick += (_, _) =>
        {
            nudge = !nudge;
            SetCursorPos(dropX + (nudge ? 1 : 0), dropY);
        };
        QueryContinueDragEventHandler onQuery = (_, args) =>
        {
            queryCount++;
            if (args.EscapePressed || clock.ElapsedMilliseconds > 2000)
            {
                args.Action = DragAction.Cancel;
            }
            else if ((args.KeyStates & DragDropKeyStates.LeftMouseButton) != 0)
            {
                args.Action = DragAction.Continue;
            }
            else
            {
                releasedAtMs ??= clock.ElapsedMilliseconds;
                if ((lastFeedbackEffect & DragDropEffects.Copy) != 0)
                    args.Action = DragAction.Drop;
                else if (clock.ElapsedMilliseconds - releasedAtMs.Value < 350)
                    args.Action = DragAction.Continue;
                else
                    args.Action = DragAction.Cancel;
            }
            args.Handled = true;
        };
        GiveFeedbackEventHandler onFeedback = (_, args) =>
        {
            feedbackCount++;
            lastFeedbackEffect = args.Effects;
        };
        System.Windows.DragDrop.AddQueryContinueDragHandler(dragSource, onQuery);
        System.Windows.DragDrop.AddGiveFeedbackHandler(dragSource, onFeedback);
        try
        {
            // Fast clicks can release before Chromium has accepted DragEnter. Keep the
            // OLE drag alive briefly after release and drop only after Copy feedback.
            nudgeTimer.Start();
            var effect = System.Windows.DragDrop.DoDragDrop(dragSource, preparation.Data, DragDropEffects.Copy);
            return new ClipboardImageDropResult(effect, clock.ElapsedMilliseconds, queryCount, feedbackCount, lastFeedbackEffect);
        }
        finally
        {
            nudgeTimer.Stop();
            System.Windows.DragDrop.RemoveQueryContinueDragHandler(dragSource, onQuery);
            System.Windows.DragDrop.RemoveGiveFeedbackHandler(dragSource, onFeedback);
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static string SaveTemporaryPng(BitmapSource image)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LunaDesktopHelper",
            "drops");
        Directory.CreateDirectory(directory);
        CleanupOldFiles(directory);

        var path = Path.Combine(directory, $"Luna-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
        return path;
    }

    private static void CleanupOldFiles(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "Luna-*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    private static (System.Windows.Point Point, string Source) FindDropPoint(IntPtr targetWindow)
    {
        if (!GetWindowRect(targetWindow, out var targetRect))
            throw new InvalidOperationException("无法读取目标窗口位置，未执行文件投递。");

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null)
            {
                var rect = focused.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width >= 8 && rect.Height >= 8)
                {
                    var x = rect.Left + rect.Width / 2;
                    var y = rect.Top + rect.Height / 2;
                    if (x >= targetRect.Left && x < targetRect.Right && y >= targetRect.Top && y < targetRect.Bottom)
                    {
                        var controlType = focused.Current.ControlType?.ProgrammaticName ?? "unknown";
                        return (new System.Windows.Point(x, y), $"UIAutomation:{controlType}");
                    }
                }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }

        // Chromium apps generally register the whole web contents as an OLE drop
        // target. Use its lower-center area when accessibility does not expose a
        // focused editor rectangle.
        var width = targetRect.Right - targetRect.Left;
        var height = targetRect.Bottom - targetRect.Top;
        var fallback = new System.Windows.Point(
            targetRect.Left + width / 2.0,
            targetRect.Bottom - Math.Clamp(height * 0.16, 72, 160));
        return (fallback, "window-lower-center-fallback");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

}
