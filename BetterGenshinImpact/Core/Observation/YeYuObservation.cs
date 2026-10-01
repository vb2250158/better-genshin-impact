using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask;
using Fischless.GameCapture.BitBlt;
using Fischless.WindowsInput;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using Serilog.Core;
using Serilog.Events;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Observation;

/// <summary>
/// Opt-in passive evidence transport. It does not decide game success, select an
/// account, send input, retry an action, or modify the official task sequence.
/// </summary>
public static class YeYuObservation
{
    private static readonly ObservationState? State = TryCreate();
    private static readonly AsyncLocal<TaskContextSnapshot?> CurrentTask = new();
    private static readonly AsyncLocal<string?> RewardPhase = new();
    private static readonly ConcurrentDictionary<long, Task> PendingCaptures = new();
    private static readonly object CaptureLock = new();
    private static readonly object MouseLock = new();
    private static readonly Dictionary<string, MousePress> MousePresses = new();
    private static BitBltCapture? _capture;
    private static nint _captureWindow;
    private static long _inputSequence;
    private static long _pendingSequence;

    public static bool Enabled => State != null;
    public static int PendingCaptureCount => PendingCaptures.Count;

    public static void AttachInputObserver()
    {
        if (!Enabled) return;
        InputObservation.Observer = ObserveSendInput;
        Write("observer_started", new { pendingCaptures = PendingCaptureCount,
            accountIdentityConfirmed = false,
            limitations = new[] { "account_identity_unconfirmed", "sendinput_double_click_batch",
                "only_daily_commission_ocr_exported", "external_input_paths_not_observed" } });
    }

    public static IDisposable BeginTask(string taskId, string taskName)
    {
        if (!Enabled) return EmptyScope.Instance;
        try
        {
        var previous = CurrentTask.Value;
        var operation = State!.Scope["taskOperations"]?[taskId]?.Value<string>();
        var current = new TaskContextSnapshot(taskId, taskName, operation);
        CurrentTask.Value = current;
        Write("task_started", new { outcome = "unknown" }, current);
        return new CallbackScope(() =>
        {
            Write("task_ended", new { outcome = current.Outcome,
                successConfirmed = false, pendingCaptures = PendingCaptureCount }, current);
            CurrentTask.Value = previous;
        });
        }
        catch { return EmptyScope.Instance; }
    }

    public static void ExceptionObserved(Exception exception, string source, bool cancelled = false)
    {
        if (!Enabled) return;
        Safe(() =>
        {
        var current = CurrentTask.Value;
        if (current != null) current.Outcome = cancelled ? "cancelled" : "exception_observed";
        Write(cancelled ? "task_cancelled" : "task_exception", new {
            source, terminalConfirmed = false, exceptionType = exception.GetType().FullName,
            exception = exception.ToString() }, current);
        });
    }

    public static void Skipped(string reason, string source)
    {
        if (!Enabled) return;
        var current = CurrentTask.Value;
        if (current != null) current.Outcome = "skipped";
        Write("task_skipped", new { reason, source }, current);
    }

    public static void NormalEndObserved(Exception exception, string source)
    {
        if (!Enabled) return;
        Safe(() => Write("task_normal_end", new { source, exception = exception.ToString(), outcome = "unknown" }));
    }

    public static void CancellationObserved(string source)
    {
        if (!Enabled) return;
        var current = CurrentTask.Value;
        if (current != null) current.Outcome = "cancelled";
        Write("task_cancelled", new { source, outcome = "unknown" }, current);
    }

    public static IDisposable BeginRewardEvidence(string phase)
    {
        if (!Enabled) return EmptyScope.Instance;
        var previous = RewardPhase.Value;
        RewardPhase.Value = phase;
        return new CallbackScope(() => RewardPhase.Value = previous);
    }

    public static void ObserveOcr(Mat original, RecognitionObject recognition,
        OcrResult result, Rect regionOfInterest)
    {
        var phase = RewardPhase.Value;
        if (!Enabled || phase == null) return;
        try
        {
            Cv2.ImEncode(".png", original, out var bytes);
            var raw = SaveImage(bytes, "game-ui-reward-verification-raw");
            Write("official_ocr", new {
                phase, raw, source = "ImageRegion.Find.OcrMatch", sourceFrameCapturedAtUtc = (string?)null,
                regionOfInterest = new { x = regionOfInterest.X, y = regionOfInterest.Y,
                    width = regionOfInterest.Width, height = regionOfInterest.Height },
                expectedAll = recognition.AllContainMatchText,
                expectedAny = recognition.OneContainMatchText, expectedRegex = recognition.RegexMatchText,
                text = result.Text, boxes = result.Regions.Select(box => new {
                    text = box.Text, confidence = box.Score,
                    x = box.Rect.BoundingRect().X + regionOfInterest.X,
                    y = box.Rect.BoundingRect().Y + regionOfInterest.Y,
                    width = box.Rect.BoundingRect().Width, height = box.Rect.BoundingRect().Height }),
                accountIdentityConfirmed = false, managerVerification = "not_performed" });
        }
        catch (Exception exception)
        {
            Write("observation_error", new { source = "official_ocr", error = exception.ToString() });
        }
    }

    public static void OfficialRewardResult(bool observed, string source)
    {
        Write("official_reward_result", new { observed, source,
            managerVerification = "not_performed", accountIdentityConfirmed = false });
    }

    /// <summary>Called immediately before a real mouse press, without altering it.</summary>
    public static void MouseDown(string source, string button, nint window = 0,
        int? x = null, int? y = null)
    {
        if (!Enabled) return;
        Safe(() =>
        {
            Interlocked.Increment(ref _inputSequence);
            var key = source + ":" + window + ":" + button;
            var target = window != 0 ? window : TaskContext.Instance().GameHandle;
            if ((!x.HasValue || !y.HasValue) && User32.GetCursorPos(out var cursor))
            {
                x = cursor.X;
                y = cursor.Y;
            }
            var press = new MousePress(Guid.NewGuid().ToString("N"), source, button,
                target, x, y, CurrentTask.Value, CaptureRaw(target));
            lock (MouseLock)
            {
                if (MousePresses.Remove(key, out var previous))
                    Write("action_incomplete", new { previous.ActionId, reason = "press_replaced" }, previous.Task);
                MousePresses[key] = press;
            }
        });
    }

    public static void MouseMoved(string source)
    {
        if (!Enabled) return;
        Safe(() =>
        {
            Interlocked.Increment(ref _inputSequence);
            lock (MouseLock)
            {
                foreach (var press in MousePresses.Values)
                {
                    press.Moved = true;
                    // A drag has no before-image artifact; release the temporary pixel copy.
                    press.Before = null;
                }
            }
        });
    }

    /// <summary>Called immediately after a real mouse release, without replaying it.</summary>
    public static void MouseUp(string source, string button, nint window = 0)
    {
        if (!Enabled) return;
        Safe(() =>
        {
            Interlocked.Increment(ref _inputSequence);
            MousePress? press;
            lock (MouseLock) MousePresses.Remove(source + ":" + window + ":" + button, out press);
            if (press == null) return; // Official cleanup releases are not invented clicks.
            press.CompletedAtUtc = DateTimeOffset.UtcNow;
            press.CompletedTimestamp = Stopwatch.GetTimestamp();
            press.CompletedInputSequence = Interlocked.Read(ref _inputSequence);
            var immediate = CaptureRaw(press.Window);
            Write("action_completed", new { press.ActionId, actionType = press.Moved ? "drag" : "click",
                press.Source, press.Button, targetWindow = press.Window.ToString(), press.X, press.Y,
                coordinateSpace = press.Source == "PostMessage" ? "client" : "screen",
                completedAtUtc = press.CompletedAtUtc, accountIdentityConfirmed = false }, press.Task);
            if (press.Moved)
            {
                SaveFrame(press, "drag_end", immediate);
                return;
            }
            ScheduleDelayedCapture(press);
            SaveFrame(press, "click_before", press.Before);
            SaveFrame(press, "click_after_immediate", immediate);
        });
    }

    public static void OtherInput(string source)
    {
        if (Enabled) Interlocked.Increment(ref _inputSequence);
    }

    public static async Task DrainAsync(TimeSpan timeout)
    {
        if (!Enabled) return;
        try
        {
            var pending = PendingCaptures.Values.ToArray();
            if (pending.Length != 0)
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout)).ConfigureAwait(false);
            Write("observer_drain", new { pendingCaptures = PendingCaptureCount,
                incompletePresses = MousePresses.Count });
            if (PendingCaptureCount == 0)
            {
                lock (CaptureLock)
                {
                    _capture?.Dispose();
                    _capture = null;
                    _captureWindow = 0;
                }
            }
        }
        catch { }
    }

    internal static void WriteLog(LogEvent logEvent)
    {
        if (!Enabled) return;
        try
        {
            State!.Append("upstream-log.jsonl", Envelope("upstream_log", new {
                timestampUtc = logEvent.Timestamp.ToUniversalTime(), level = logEvent.Level.ToString(),
                messageTemplate = logEvent.MessageTemplate.Text, message = logEvent.RenderMessage(),
                exception = logEvent.Exception?.ToString(),
                properties = logEvent.Properties.ToDictionary(item => item.Key, item => item.Value.ToString())
            }, CurrentTask.Value));
        }
        catch { }
    }

    private static void ObserveSendInput(User32.INPUT[] inputs, bool completed)
    {
        if (!Enabled) return;
        Safe(() =>
        {
            if (!completed) OtherInput("SendInput.dispatch");
            var mouse = inputs.Where(input => input.type == User32.INPUTTYPE.INPUT_MOUSE).ToArray();
            var downCount = mouse.Count(input => ((uint)input.mi.dwFlags & 0x2A) != 0);
            if (downCount > 1)
            {
                if (!completed)
                {
                    OtherInput("SendInput.batch");
                    Write("action_capture_missing", new { source = "SendInput", reason = "multiple_clicks_in_single_batch",
                        downCount, inputCount = inputs.Length });
                }
                return;
            }
            foreach (var input in mouse)
            {
                var flags = (uint)input.mi.dwFlags;
                if (!completed && (flags & 1) != 0) MouseMoved("SendInput");
                foreach (var button in new[] { (Name: "left", Down: 2u, Up: 4u),
                    (Name: "right", Down: 8u, Up: 16u), (Name: "middle", Down: 32u, Up: 64u) })
                {
                    if (!completed && (flags & button.Down) != 0) MouseDown("SendInput", button.Name);
                    if (completed && (flags & button.Up) != 0) MouseUp("SendInput", button.Name);
                }
                if (!completed && (flags & (128u | 256u)) != 0)
                    Write("action_capture_missing", new { source = "SendInput", reason = "xbutton_not_covered" });
            }
            if (!completed && mouse.Length == 0) OtherInput("SendInput.keyboard");
        });
    }

    private static void ScheduleDelayedCapture(MousePress press)
    {
        var id = Interlocked.Increment(ref _pendingSequence);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = CaptureAfterDelayAsync();
        PendingCaptures[id] = task;
        start.SetResult(true);
        async Task CaptureAfterDelayAsync()
        {
            try
            {
                await start.Task.ConfigureAwait(false);
                var remainingMs = 100 - Stopwatch.GetElapsedTime(press.CompletedTimestamp).TotalMilliseconds;
                if (remainingMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(remainingMs)).ConfigureAwait(false);
                SaveFrame(press, "click_after_100ms", CaptureRaw(press.Window));
            }
            catch (Exception exception)
            {
                Write("observation_error", new { press.ActionId, error = exception.ToString() }, press.Task);
            }
            finally { PendingCaptures.TryRemove(id, out _); }
        }
    }

    private static FramePixels? CaptureRaw(nint requestedWindow)
    {
        try
        {
            var target = requestedWindow != 0 ? requestedWindow : TaskContext.Instance().GameHandle;
            if (target == 0) throw new InvalidOperationException("game_window_unconfirmed");
            var requestedAt = DateTimeOffset.UtcNow;
            lock (CaptureLock)
            {
                if (_capture == null || target != _captureWindow)
                {
                    _capture?.Dispose();
                    _capture = new BitBltCapture();
                    _capture.Start(target, new Dictionary<string, object> { ["autoFixWin11BitBlt"] = false });
                    _captureWindow = target;
                }
                using var frame = _capture.Capture();
                if (frame == null || frame.Frame.Empty()) throw new InvalidOperationException("capture_returned_no_frame");
                var capturedAt = DateTimeOffset.UtcNow;
                var capturedTimestamp = Stopwatch.GetTimestamp();
                var capturedInputSequence = Interlocked.Read(ref _inputSequence);
                Cv2.ImEncode(".png", frame.Frame, out var bytes);
                return new FramePixels(bytes, requestedAt, capturedAt, target, frame.Frame.Width,
                    frame.Frame.Height, capturedTimestamp, capturedInputSequence);
            }
        }
        catch (Exception exception)
        {
            Write("capture_error", new { requestedWindow = requestedWindow.ToString(), error = exception.ToString() });
            return null;
        }
    }

    private static void SaveFrame(MousePress press, string phase, FramePixels? frame)
    {
        try
        {
            var subsequentInputs = phase == "click_before" ? 0 :
                Math.Max(0, (frame?.CapturedInputSequence ?? Interlocked.Read(ref _inputSequence)) - press.CompletedInputSequence);
            Write("action_frame", new { press.ActionId, phase, available = frame != null,
                raw = frame == null ? null : SaveImage(frame.Bytes, "game-ui-action-raw"),
                captureRequestedAtUtc = frame?.RequestedAtUtc, capturedAtUtc = frame?.CapturedAtUtc,
                targetWindow = frame?.Window.ToString(), width = frame?.Width, height = frame?.Height,
                captureSource = "independent_BitBlt", freshnessConfirmed = frame != null,
                completedAtUtc = press.CompletedAtUtc,
                delayMs = frame == null || phase == "click_before" ? (double?)null :
                    Stopwatch.GetElapsedTime(press.CompletedTimestamp, frame.CapturedTimestamp).TotalMilliseconds,
                targetDelayMs = phase == "click_after_100ms" ? 100 : (int?)null,
                subsequentInputs, coveredByLaterInput = subsequentInputs > 0,
                accountIdentityConfirmed = false }, press.Task);
        }
        catch (Exception exception)
        {
            Write("observation_error", new { press.ActionId, phase, error = exception.ToString() }, press.Task);
        }
    }

    private static object SaveImage(byte[] bytes, string kind)
    {
        var name = Guid.NewGuid().ToString("N") + ".png";
        var relativePath = "frames/" + name;
        var path = Path.Combine(State!.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new { kind, path = relativePath, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }

    private static JObject Envelope(string type, object data, TaskContextSnapshot? task)
    {
        return new JObject {
            ["schemaVersion"] = 1, ["observerVersion"] = "1", ["type"] = type,
            ["eventType"] = type, ["observedAtUtc"] = DateTimeOffset.UtcNow,
            ["processId"] = Environment.ProcessId, ["runId"] = State!.Scope["runId"],
            ["runAttemptId"] = State.Scope["runAttemptId"], ["accountId"] = State.Scope["accountId"],
            ["gameDay"] = State.Scope["gameDay"], ["gameId"] = "Genshin",
            ["accountIdentityConfirmed"] = false, ["taskId"] = task?.TaskId,
            ["taskName"] = task?.TaskName, ["operation"] = task?.Operation,
            ["outcome"] = task?.Outcome == "skipped" ? "skipped" : "unknown",
            ["data"] = JToken.FromObject(data)
        };
    }

    private static void Write(string type, object data, TaskContextSnapshot? task = null)
    {
        if (!Enabled) return;
        try { State!.Append("events.jsonl", Envelope(type, data, task ?? CurrentTask.Value)); }
        catch { }
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception exception) { Write("observation_error", new { error = exception.ToString() }); }
    }

    private static ObservationState? TryCreate()
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("BGI_YEYU_OBSERVATION_DIR");
            var scopePath = Environment.GetEnvironmentVariable("BGI_YEYU_SCOPE_FILE");
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(scopePath)) return null;
            var scope = JObject.Parse(File.ReadAllText(scopePath, Encoding.UTF8));
            if (scope["schemaVersion"]?.Value<int>() != 1 || scope["gameId"]?.Value<string>() != "Genshin") return null;
            foreach (var key in new[] { "runId", "runAttemptId", "accountId", "gameDay" })
                if (string.IsNullOrWhiteSpace(scope[key]?.Value<string>())) return null;
            if (!IsCanonicalGuid(scope["runId"]!.Value<string>()!) ||
                !IsCanonicalGuid(scope["runAttemptId"]!.Value<string>()!)) return null;
            var account = scope["accountId"]!.Value<string>()!;
            if (account != "default" && !IsCanonicalGuid(account)) return null;
            if (!DateOnly.TryParseExact(scope["gameDay"]!.Value<string>(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
            var root = Path.GetFullPath(directory);
            Directory.CreateDirectory(root);
            return new ObservationState(root, scope);
        }
        catch { return null; }
    }

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;

    private sealed class ObservationState(string root, JObject scope)
    {
        private readonly object _writeLock = new();
        public string Root { get; } = root;
        public JObject Scope { get; } = scope;
        public void Append(string fileName, JObject item)
        {
            lock (_writeLock)
                File.AppendAllText(Path.Combine(Root, fileName), item.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
        }
    }

    private sealed class TaskContextSnapshot(string taskId, string taskName, string? operation)
    {
        public string TaskId { get; } = taskId;
        public string TaskName { get; } = taskName;
        public string? Operation { get; } = operation;
        public string Outcome { get; set; } = "returned_without_terminal";
    }

    private sealed record FramePixels(byte[] Bytes, DateTimeOffset RequestedAtUtc,
        DateTimeOffset CapturedAtUtc, nint Window, int Width, int Height,
        long CapturedTimestamp, long CapturedInputSequence);

    private sealed class MousePress(string actionId, string source, string button, nint window,
        int? x, int? y, TaskContextSnapshot? task, FramePixels? before)
    {
        public string ActionId { get; } = actionId;
        public string Source { get; } = source;
        public string Button { get; } = button;
        public nint Window { get; } = window;
        public int? X { get; } = x;
        public int? Y { get; } = y;
        public TaskContextSnapshot? Task { get; } = task;
        public FramePixels? Before { get; set; } = before;
        public bool Moved { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public long CompletedTimestamp { get; set; }
        public long CompletedInputSequence { get; set; }
    }

    private sealed class CallbackScope(Action callback) : IDisposable
    {
        private Action? _callback = callback;
        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _callback, null);
            if (action != null) Safe(action);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}

public sealed class YeYuObservationSink : ILogEventSink
{
    public void Emit(LogEvent logEvent) => YeYuObservation.WriteLog(logEvent);
}
