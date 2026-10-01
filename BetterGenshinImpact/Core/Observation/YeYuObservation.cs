using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using Serilog.Core;
using Serilog.Events;

namespace BetterGenshinImpact.Core.Observation;

/// <summary>Fail-open transport. It never captures a window, sends input, waits,
/// retries, or infers success from return. V1 action artifacts stay historical.</summary>
public static class YeYuObservation
{
    private static readonly ObservationState? State = TryCreate();
    private static readonly AsyncLocal<TaskContextSnapshot?> CurrentTask = new();
    private static readonly AsyncLocal<string?> EvidencePhase = new();
    private static readonly AsyncLocal<string?> EvidenceKind = new();
    private static readonly AsyncLocal<bool> ImageRegionOcr = new();
    private static readonly object IdentityLock = new();
    private static bool _identityConfirmed;
    private static string? _observedUid;
    private static int _taskSequence;
    private static int _cancellationRecorded;
    public static bool Enabled => State != null;
    public static bool Requested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_YEYU_OBSERVATION_DIR"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_YEYU_SCOPE_FILE"));
    public static bool AccountIdentityConfirmed { get { lock (IdentityLock) return _identityConfirmed; } }
    public static int PendingCaptureCount => 0;

    // ABI compatibility: v2 deliberately installs no input observer.
    public static void AttachInputObserver() => Write("observer_started", new {
        observationVersion = "0.66.0-yeyu.2", capturePolicy = "official_recognition_input_only",
        actionCaptureEnabled = false, delayedCaptureEnabled = false,
        normalImageLimitPerOperation = 2, dedicatedPhaseLimitPerOperation = 6 });
    public static void MouseDown(string source, string button, nint window = 0, int? x = null, int? y = null) { }
    public static void MouseUp(string source, string button, nint window = 0) { }
    public static void MouseMoved(string source) { }
    public static void OtherInput(string source) { }
    public static Task DrainAsync(TimeSpan timeout) { Write("observer_drain", new { pendingCaptures = 0 }); return Task.CompletedTask; }

    public static IDisposable BeginTask(string taskId, string taskName)
    {
        if (!Enabled) return EmptyScope.Instance;
        try
        {
            var previous = CurrentTask.Value;
            var operation = State!.Scope["taskOperations"]?[taskId]?.Value<string>();
            if (taskId == "@check-rewards" && State.Scope["taskOperations"] is JObject mappings &&
                mappings.Properties().Any(p => p.Value.Value<string>() == "claim-daily-rewards")) operation = "claim-daily-rewards";
            var current = new TaskContextSnapshot(taskId, taskName, operation, Interlocked.Increment(ref _taskSequence));
            CurrentTask.Value = current;
            if (operation != null && taskId != "@check-rewards") State.RegisterOperationTask(current);
            Write("task_started", new { outcome = "unknown", officialOrder = current.Sequence }, current);
            return new CallbackScope(() => {
                Write("task_terminal", new { outcome = current.Outcome, terminalConfirmed = current.TerminalConfirmed,
                    reason = current.Reason, officialSource = current.Source, facts = current.Facts,
                    recognitionIds = current.RecognitionIds.ToArray(), successConfirmed = current.Outcome == "completed" && current.TerminalConfirmed }, current);
                Write("task_ended", new { outcome = current.Outcome, terminalConfirmed = current.TerminalConfirmed,
                    successConfirmed = current.Outcome == "completed" && current.TerminalConfirmed }, current);
                CurrentTask.Value = previous;
            });
        }
        catch { return EmptyScope.Instance; }
    }
    public static void Fact(string phase, string status, string source, object? details = null)
    {
        Safe(() => {
            var current = CurrentTask.Value;
            var fact = new { phase, status, officialSource = source, details };
            if (current != null) lock (current.Sync) current.Facts[phase] = JToken.FromObject(fact);
            if (current?.Operation != null) State!.RecordOperationFact(current.Operation, phase, JToken.FromObject(fact));
            Write("official_task_fact", fact);
        });
    }
    /// <summary>Use only at an explicit official completion/failure/skip branch.</summary>
    public static void Terminal(string outcome, string reason, string source, object? details = null)
    {
        Safe(() => {
            if (outcome is not ("completed" or "failed" or "skipped" or "cancelled" or "unknown")) return;
            var current = CurrentTask.Value;
            if (current == null) return;
            lock (current.Sync) {
                if (current.Outcome is "failed" or "cancelled") return;
                current.Outcome = outcome; current.TerminalConfirmed = outcome != "unknown";
                current.Reason = reason; current.Source = source;
            }
            Fact("terminal", outcome, source, new { reason, details });
        });
    }
    public static void ExceptionObserved(Exception exception, string source, bool cancelled = false)
        => Write(cancelled ? "task_cancelled" : "task_exception", new { source, terminalConfirmed = false,
            recoveredOrTerminalUnknown = true, exceptionType = exception.GetType().FullName, exception = exception.ToString() });
    public static void Skipped(string reason, string source) => Terminal("skipped", reason, source);
    public static void NormalEndObserved(Exception exception, string source)
        => Write("task_normal_end", new { source, exception = exception.ToString(), outcome = "unknown" });
    public static void CancellationObserved(string source) => Terminal("cancelled", "official_cancellation_requested", source);
    public static void FlowTerminal(string outcome, string reason, string source)
        => Write("flow_terminal", new { outcome, reason, officialSource = source });
    public static void RequestCancellationAtSafePoint(Action cancel)
    {
        if (!Enabled) return;
        Safe(() => {
            var path = State!.Scope["cancelFile"]?.Value<string>();
            var envPath = Environment.GetEnvironmentVariable("BGI_YEYU_CANCEL_FILE");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                (!string.IsNullOrWhiteSpace(envPath) && Path.GetFullPath(envPath) != Path.GetFullPath(path))) return;
            if (!File.Exists(path)) return;
            if (Interlocked.Exchange(ref _cancellationRecorded, 1) == 0)
                Write("cancellation_request", new { source = "runner_cancel_file", path, mechanism = "official_CancellationContext.Cancel" });
            cancel();
        });
    }
    public static IDisposable BeginRewardEvidence(string phase) => BeginEvidence(phase, "game-ui-reward-verification-raw");
    public static IDisposable BeginKeyStepEvidence(string phase) => BeginEvidence(phase, "game-ui-key-step-verification-raw");
    public static IDisposable BeginDiagnosticEvidence(string phase) => BeginEvidence(phase, "game-ui-recognition-raw");
    private static IDisposable BeginEvidence(string phase, string kind)
    {
        if (!Enabled) return EmptyScope.Instance;
        var previousPhase = EvidencePhase.Value; var previousKind = EvidenceKind.Value;
        EvidencePhase.Value = phase; EvidenceKind.Value = kind;
        return new CallbackScope(() => { EvidencePhase.Value = previousPhase; EvidenceKind.Value = previousKind; });
    }
    public static IDisposable BeginImageRegionOcr()
    {
        if (!Enabled) return EmptyScope.Instance;
        var previous = ImageRegionOcr.Value; ImageRegionOcr.Value = true;
        return new CallbackScope(() => ImageRegionOcr.Value = previous);
    }
    public static void ObserveOcr(Mat input, RecognitionObject recognition, OcrResult result, Rect roi, bool? matched = null)
        => Safe(() => ObserveRecognition(input, "ImageRegion.Ocr", new { recognitionName = recognition.Name,
            text = result.Text, boxes = Boxes(result, default), regionOfInterest = RectValue(new Rect(0, 0, input.Width, input.Height)),
            sourceRegionOfInterest = RectValue(roi), matched,
            expectedAll = recognition.AllContainMatchText, expectedAny = recognition.OneContainMatchText,
            expectedRegex = recognition.RegexMatchText }, "actual_ocr_input_pixels", matched));
    public static void ObserveOcrPrimitive(Mat input, OcrResult result, string source)
    {
        if (!Enabled || ImageRegionOcr.Value) return;
        Safe(() => ObserveRecognition(input, source, new { text = result.Text, boxes = Boxes(result, default),
            regionOfInterest = RectValue(new Rect(0, 0, input.Width, input.Height)) }, "actual_ocr_input_pixels_original_frame_unavailable"));
    }
    public static void ObserveTextPrimitive(Mat input, string text, string source)
        => Safe(() => ObserveRecognition(input, source, new { text, boxes = Array.Empty<object>(), bboxUnavailable = true }, "actual_recognizer_input_pixels_original_frame_unavailable"));
    public static void ObserveTemplate(Mat input, RecognitionObject ro, Rect roi, int width, int height,
        IEnumerable<(Rect Box, double Score)> matches)
        => Safe(() => ObserveRecognition(input, "ImageRegion.TemplateMatch", new { recognitionName = ro.Name,
            threshold = ro.Threshold, mode = ro.TemplateMatchMode.ToString(), regionOfInterest = RectValue(roi),
            templateSize = new { width, height }, boxes = matches.Select(m => new { rect = RectValue(m.Box), score = m.Score }).ToArray() }, "actual_template_input_pixels"));
    public static void ObserveDecision(Mat input, string source, object result)
        => Safe(() => ObserveRecognition(input, source, result, "official_step_decision_input_pixels"));
    public static void ObserveIdentity(int uid, string source)
    {
        Safe(() => {
            var observed = uid > 0 ? uid.ToString(CultureInfo.InvariantCulture) : null;
            var expected = State!.Scope["expectedUid"]?.Value<string>();
            var discovery = State.Scope["accountId"]?.Value<string>() == "default" && string.IsNullOrWhiteSpace(expected);
            var actualInputArchived = CurrentTask.Value?.RecognitionIds.Count > 0;
            lock (IdentityLock) { _observedUid = observed; _identityConfirmed = actualInputArchived && observed != null && (discovery || expected == observed); }
            Write("account_identity", new { observedUid = observed, expectedUid = expected,
                identityConfirmed = AccountIdentityConfirmed, discovery, actualInputArchived, source,
                recognitionIds = CurrentTask.Value?.RecognitionIds.ToArray(),
                reason = observed == null ? "official_uid_ocr_failed" : AccountIdentityConfirmed ? "official_uid_ocr_confirmed" : "account_uid_mismatch" });
        });
    }
    public static void OfficialRewardResult(bool observed, string source)
    {
        Fact("daily-commission-reward", observed ? "claimed_confirmed_by_official_ocr" : "not_claimed_confirmed_by_official_ocr", source);
        Write("official_reward_result", new { observed, source, phase = "daily-commission-reward", managerVerification = "not_performed", compositeSuccessConfirmed = false });
        if (observed) CompleteOperationFromFacts("claim-daily-rewards", "official_composite_postconditions_confirmed", source,
            new Dictionary<string, string[]> {
                ["encounter-points"] = new[] { "postcondition_anchors_observed" },
                ["catherine"] = new[] { "postcondition_anchors_observed" },
                ["expeditions"] = new[] { "official_collect_and_redispatch" },
                ["battle-pass-points"] = new[] { "postcondition_anchors_observed" },
                ["battle-pass-rewards"] = new[] { "postcondition_anchors_observed" },
                ["daily-commission-reward"] = new[] { "claimed_confirmed_by_official_ocr" }
            });
    }
    public static void CompleteOperationFromFacts(string operation, string reason, string source,
        IReadOnlyDictionary<string, string[]> required)
    {
        Safe(() => {
            var (task, facts) = State!.OperationSnapshot(operation);
            if (task == null || !AccountIdentityConfirmed) return;
            var missing = required.Where(item => !facts.TryGetValue(item.Key, out var fact) ||
                !item.Value.Contains(fact["status"]?.Value<string>())).Select(item => item.Key).ToArray();
            if (missing.Length > 0) {
                Write("official_composite_result", new { operation, confirmed = false, missingPhases = missing, facts }, task);
                return;
            }
            lock (task.Sync) {
                if (task.Outcome is "failed" or "cancelled") return;
                task.Outcome = "completed"; task.TerminalConfirmed = true;
                task.Source = source; task.Reason = reason;
            }
            // Composite facts can arrive at the later official report scope.
            // Keep that operation report distinct from an individual task exit.
            if (operation == "claim-daily-rewards")
                Write("operation_terminal", new { outcome = "completed", terminalConfirmed = true,
                    reason, officialSource = source, facts, requiredPhases = required.Keys.ToArray(),
                    recognitionIds = task.RecognitionIds.Concat(CurrentTask.Value?.RecognitionIds ?? new List<string>()).Distinct().ToArray(),
                    successConfirmed = true, reportPhase = "final_official_postcondition_report",
                    managerVerification = "not_performed" }, task);
        });
    }
    private static object[] Boxes(OcrResult result, Rect roi) => result.Regions.Select(box => {
        var rect = box.Rect.BoundingRect();
        return (object)new { text = box.Text, confidence = box.Score, x = rect.X + roi.X, y = rect.Y + roi.Y, width = rect.Width, height = rect.Height };
    }).ToArray();
    private static object RectValue(Rect rect) => new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
    private static void ObserveRecognition(Mat input, string method, object result, string coordinates, bool? matched = null)
    {
        if (!Enabled || CurrentTask.Value == null) return;
        var observedAt = DateTimeOffset.UtcNow;
        Safe(() => {
            var current = CurrentTask.Value!; var phase = matched == false ? null : EvidencePhase.Value;
            var kind = phase == null ? "game-ui-recognition-raw" : EvidenceKind.Value ?? "game-ui-recognition-raw";
            var resultToken = JToken.FromObject(result);
            var signature = method + ":" + phase + ":" + resultToken.ToString(Formatting.None);
            var budget = State!.Budget(current.Operation ?? current.TaskId);
            lock (budget.Sync) {
                var dedicated = phase != null;
                if ((dedicated && (budget.DedicatedCount >= 6 || budget.Phases.Contains(phase!))) || (!dedicated && budget.NormalCount >= 2)) {
                    if (budget.Missing.Add(method + ":" + phase)) Write("recognition_sampling_missing", new { method, phase, reason = "bounded_sampling_limit", normalLimit = 2, dedicatedLimit = 6 });
                    return;
                }
                if (!budget.Signatures.Add(signature)) return;
                var id = Guid.NewGuid().ToString("D");
                Cv2.ImEncode(".png", input, out var bytes);
                var raw = SaveImage(bytes, kind, id);
                if (dedicated) { budget.DedicatedCount++; budget.Phases.Add(phase!); } else budget.NormalCount++;
                lock (current.Sync) current.RecognitionIds.Add(id);
                Write("recognition", new { recognitionId = id, raw, method, phase, coordinateSpace = coordinates,
                    width = input.Width, height = input.Height, observedAtUtc = observedAt, capturedAtUtc = observedAt,
                    capturedAtMeaning = "official_recognition_input_observed", frameCapturedAt = (string?)null,
                    frameCaptureTimeKnown = false, sealedAtUtc = DateTimeOffset.UtcNow,
                    source = "official_input_copy", result = resultToken, managerVerification = "not_performed" });
            }
        });
    }
    private static object SaveImage(byte[] bytes, string kind, string id)
    {
        var relative = "frames/" + id + ".png";
        Directory.CreateDirectory(Path.Combine(State!.Root, "frames"));
        File.WriteAllBytes(Path.Combine(State.Root, relative), bytes);
        return new { kind, path = relative, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }
    internal static void WriteLog(LogEvent value)
    {
        if (!Enabled) return;
        Safe(() => State!.Append("upstream-log.jsonl", Envelope("upstream_log", new {
            timestampUtc = value.Timestamp.ToUniversalTime(), level = value.Level.ToString(), messageTemplate = value.MessageTemplate.Text,
            message = value.RenderMessage(), exception = value.Exception?.ToString(),
            properties = value.Properties.ToDictionary(p => p.Key, p => p.Value.ToString()) }, CurrentTask.Value)));
    }
    private static JObject Envelope(string type, object data, TaskContextSnapshot? task)
    {
        string? uid; bool identity; lock (IdentityLock) { uid = _observedUid; identity = _identityConfirmed; }
        return new JObject { ["schemaVersion"] = 2, ["observerVersion"] = "0.66.0-yeyu.2", ["type"] = type, ["eventType"] = type,
            ["observedAtUtc"] = DateTimeOffset.UtcNow, ["processId"] = Environment.ProcessId,
            ["runId"] = State!.Scope["runId"], ["runAttemptId"] = State.Scope["runAttemptId"], ["accountId"] = State.Scope["accountId"],
            ["gameDay"] = State.Scope["gameDay"], ["gameId"] = "Genshin", ["accountIdentityConfirmed"] = identity, ["observedUid"] = uid,
            ["taskId"] = task?.TaskId, ["taskName"] = task?.TaskName, ["operation"] = task?.Operation,
            ["todoAttemptId"] = task?.Operation == null ? null : State.Scope["todoAttemptIds"]?[task.Operation],
            ["outcome"] = task?.Outcome ?? "unknown", ["data"] = JToken.FromObject(data) };
    }
    private static void Write(string type, object data, TaskContextSnapshot? task = null)
    {
        if (!Enabled) return;
        try { State!.Append("events.jsonl", Envelope(type, data, task ?? CurrentTask.Value)); } catch { }
    }
    private static void Safe(Action action)
    {
        if (!Enabled) return;
        try { action(); } catch (Exception exception) { Write("observation_error", new { error = exception.ToString() }); }
    }
    private static ObservationState? TryCreate()
    {
        try {
            var root = Environment.GetEnvironmentVariable("BGI_YEYU_OBSERVATION_DIR");
            var path = Environment.GetEnvironmentVariable("BGI_YEYU_SCOPE_FILE");
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return null;
            var scope = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (scope["schemaVersion"]?.Value<int>() != 2 || scope["gameId"]?.Value<string>() != "Genshin") return null;
            foreach (var key in new[] { "runId", "runAttemptId" }) if (!IsGuid(scope[key]?.Value<string>())) return null;
            var account = scope["accountId"]?.Value<string>();
            if (account != "default" && !IsGuid(account)) return null;
            if (account != "default" && string.IsNullOrWhiteSpace(scope["expectedUid"]?.Value<string>())) return null;
            if (!DateOnly.TryParseExact(scope["gameDay"]?.Value<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
            if (scope["taskOperations"] is not JObject || scope["todoAttemptIds"] is not JObject attempts) return null;
            foreach (var attempt in attempts.Properties()) if (!IsGuid(attempt.Value.Value<string>())) return null;
            var directory = Path.GetFullPath(root); Directory.CreateDirectory(directory);
            return new ObservationState(directory, scope);
        } catch { return null; }
    }
    private static bool IsGuid(string? value) => value != null && Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;
    private sealed class ObservationState(string root, JObject scope)
    {
        private readonly object _writeLock = new(); private readonly Dictionary<string, SamplingBudget> _budgets = new();
        private readonly Dictionary<string, TaskContextSnapshot> _operationTasks = new();
        private readonly Dictionary<string, Dictionary<string, JToken>> _operationFacts = new();
        public string Root { get; } = root; public JObject Scope { get; } = scope;
        public SamplingBudget Budget(string key) { lock (_writeLock) { if (!_budgets.TryGetValue(key, out var budget)) _budgets[key] = budget = new(); return budget; } }
        public void RegisterOperationTask(TaskContextSnapshot task) { lock (_writeLock) _operationTasks[task.Operation!] = task; }
        public void RecordOperationFact(string operation, string phase, JToken fact) {
            lock (_writeLock) { if (!_operationFacts.TryGetValue(operation, out var facts)) _operationFacts[operation] = facts = new(); facts[phase] = fact; }
        }
        public (TaskContextSnapshot?, Dictionary<string, JToken>) OperationSnapshot(string operation) {
            lock (_writeLock) { _operationTasks.TryGetValue(operation, out var task); _operationFacts.TryGetValue(operation, out var facts);
                return (task, facts == null ? new() : new(facts)); }
        }
        public void Append(string name, JObject value) { lock (_writeLock) File.AppendAllText(Path.Combine(Root, name), value.ToString(Formatting.None) + "\n", new UTF8Encoding(false)); }
    }
    private sealed class SamplingBudget
    {
        public readonly object Sync = new(); public readonly HashSet<string> Signatures = new(), Phases = new(), Missing = new();
        public int NormalCount; public int DedicatedCount;
    }
    private sealed class TaskContextSnapshot(string id, string name, string? operation, int sequence)
    {
        public readonly object Sync = new(); public string TaskId { get; } = id; public string TaskName { get; } = name;
        public string? Operation { get; } = operation; public int Sequence { get; } = sequence;
        public string Outcome = "unknown"; public bool TerminalConfirmed;
        public string Reason = "returned_without_explicit_official_terminal"; public string? Source;
        public readonly Dictionary<string, JToken> Facts = new(); public readonly List<string> RecognitionIds = new();
    }
    private sealed class CallbackScope(Action callback) : IDisposable
    {
        private Action? _callback = callback;
        public void Dispose() { var action = Interlocked.Exchange(ref _callback, null); if (action != null) Safe(action); }
    }
    private sealed class EmptyScope : IDisposable { public static EmptyScope Instance { get; } = new(); public void Dispose() { } }
}
public sealed class YeYuObservationSink : ILogEventSink { public void Emit(LogEvent value) => YeYuObservation.WriteLog(value); }
