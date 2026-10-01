using BetterGenshinImpact.Core.Observation;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using Fischless.GameCapture.BitBlt;
using Fischless.WindowsInput;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using Serilog.Events;
using Serilog.Parsing;
using Vanara.PInvoke;

if (args.Length != 2) return 2;
var scenario = args[0];
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var scopeFile = Path.Combine(root, "scope.json");
File.WriteAllText(scopeFile, JsonConvert.SerializeObject(new {
    schemaVersion = scenario == "invalid-scope" ? 2 : 1, gameId = "Genshin",
    runId = scenario == "invalid-uuid" ? "not-a-manager-uuid" : "00000000-0000-4000-8000-000000000001",
    runAttemptId = "00000000-0000-4000-8000-000000000002",
    accountId = "00000000-0000-4000-8000-000000000003",
    gameDay = scenario == "invalid-day" ? "2000-02-30" : "2000-01-01",
    taskOperations = new Dictionary<string, string> { ["task"] = "claim-mail" }
}));
Environment.SetEnvironmentVariable("BGI_YEYU_OBSERVATION_DIR", scenario == "off" ? null : root);
Environment.SetEnvironmentVariable("BGI_YEYU_SCOPE_FILE", scenario == "off" ? null : scopeFile);
var assertions = new List<string>();
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
    assertions.Add(message);
}
try
{
    YeYuObservation.AttachInputObserver();
    var savedCallback = InputObservation.Observer;
    var protectedInput = new[] { new User32.INPUT { type = User32.INPUTTYPE.INPUT_MOUSE,
        mi = new User32.MOUSEINPUT { dwFlags = User32.MOUSEEVENTF.MOUSEEVENTF_LEFTDOWN } } };
    InputObservation.Observer = (copy, _) => {
        var changedMouse = copy[0].mi;
        changedMouse.dwFlags = User32.MOUSEEVENTF.MOUSEEVENTF_RIGHTDOWN;
        copy[0].mi = changedMouse;
        throw new InvalidOperationException("malicious_observer_failure");
    };
    InputObservation.Notify(protectedInput, false);
    Assert(protectedInput[0].mi.dwFlags == User32.MOUSEEVENTF.MOUSEEVENTF_LEFTDOWN,
        "mutating failing observer cannot modify official input array");
    InputObservation.Observer = savedCallback;
    if (scenario is "off" or "invalid-scope" or "invalid-uuid" or "invalid-day")
    {
        using var task = YeYuObservation.BeginTask("task", "task");
        YeYuObservation.Skipped("sample", "offline");
        Assert(!YeYuObservation.Enabled, "observer disabled without valid explicit scope");
        Assert(InputObservation.Observer == null, "no input callback installed while disabled");
        Assert(!File.Exists(Path.Combine(root, "events.jsonl")), "disabled observer writes no events");
    }
    else if (scenario == "write-failure")
    {
        File.WriteAllText(Path.Combine(root, "events.jsonl"), "");
        using var deny = new FileStream(Path.Combine(root, "events.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var original = new InvalidOperationException("original_exception_reference");
        try
        {
            using var task = YeYuObservation.BeginTask("task", "failure");
            try { throw original; }
            catch (Exception exception) { YeYuObservation.ExceptionObserved(exception, "offline"); throw; }
        }
        catch (Exception exception) { Assert(ReferenceEquals(exception, original), "failed evidence write preserves original exception object"); }
    }
    else if (scenario == "enabled")
    {
        using (YeYuObservation.BeginTask("task", "returned")) { }
        using (YeYuObservation.BeginTask("task", "exception"))
            YeYuObservation.ExceptionObserved(new InvalidOperationException("test_exception"), "offline");
        using (YeYuObservation.BeginTask("task", "recovered-exception"))
            YeYuObservation.ExceptionObserved(new InvalidOperationException("official_retry_recovered"), "offline.recovered_retry");
        using (YeYuObservation.BeginTask("task", "cancelled"))
            YeYuObservation.ExceptionObserved(new OperationCanceledException("test_cancel"), "offline", cancelled: true);
        using (YeYuObservation.BeginTask("task", "skipped")) YeYuObservation.Skipped("official_skip_branch", "offline");
        var sink = new YeYuObservationSink();
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error,
            new InvalidOperationException("raw_log_exception"), new MessageTemplateParser().Parse("raw {Value}"),
            new[] { new LogEventProperty("Value", new ScalarValue("unmodified")) }));
        using (YeYuObservation.BeginTask("task", "click"))
        {
            var click = new[] {
                new User32.INPUT { type = User32.INPUTTYPE.INPUT_MOUSE, mi = new User32.MOUSEINPUT { dwFlags = User32.MOUSEEVENTF.MOUSEEVENTF_LEFTDOWN } },
                new User32.INPUT { type = User32.INPUTTYPE.INPUT_MOUSE, mi = new User32.MOUSEINPUT { dwFlags = User32.MOUSEEVENTF.MOUSEEVENTF_LEFTUP } }
            };
            var originalFlags = click.Select(input => input.mi.dwFlags).ToArray();
            InputObservation.Notify(click, false);
            InputObservation.Notify(click, true);
            Assert(click.Select(input => input.mi.dwFlags).SequenceEqual(originalFlags), "passive callback preserves original input array");
            YeYuObservation.OtherInput("subsequent_keyboard_input");
            InputObservation.Notify(click.Concat(click).ToArray(), false);
            InputObservation.Notify(click.Concat(click).ToArray(), true);
        }
        using (YeYuObservation.BeginTask("task", "drag"))
        {
            YeYuObservation.MouseDown("offline", "left", (nint)17, 10, 20);
            YeYuObservation.MouseMoved("offline");
            YeYuObservation.MouseUp("offline", "left", (nint)17);
        }
        await YeYuObservation.DrainAsync(TimeSpan.FromSeconds(2));
        using (YeYuObservation.BeginTask("task", "capture-failure"))
        {
            BitBltCapture.FailCapture = true;
            YeYuObservation.MouseDown("offline", "left", (nint)17, 10, 20);
            YeYuObservation.MouseUp("offline", "left", (nint)17);
            await YeYuObservation.DrainAsync(TimeSpan.FromSeconds(2));
            BitBltCapture.FailCapture = false;
        }
        using (YeYuObservation.BeginTask("task", "ocr"))
        using (YeYuObservation.BeginRewardEvidence("offline-ocr"))
        using (var original = new Mat(30, 40, MatType.CV_8UC3, new Scalar(10, 20, 30)))
        {
            var recognition = new RecognitionObject();
            recognition.AllContainMatchText.Add("今日奖励已领取");
            YeYuObservation.ObserveOcr(original, recognition,
                new OcrResult(new[] { new OcrResultRegion(new RotatedRect(new Point2f(10, 10), new Size2f(10, 5), 0), "今日奖励已领取", 0.9f) }),
                new Rect(2, 3, 10, 10));
        }
        var events = File.ReadAllLines(Path.Combine(root, "events.jsonl")).Select(JObject.Parse).ToArray();
        JObject[] Events(string type) => events.Where(item => item["type"]?.Value<string>() == type).ToArray();
        Assert(Events("task_ended").Any(item => item["taskName"]?.Value<string>() == "returned" && item["outcome"]?.Value<string>() == "unknown"), "method return remains unknown");
        Assert(Events("task_exception").Length == 2 && Events("task_cancelled").Length == 1 && Events("task_skipped").Length == 1, "exception cancellation and official skip are separate facts");
        Assert(Events("task_ended").Single(item => item["taskName"]?.Value<string>() == "recovered-exception")["outcome"]?.Value<string>() == "unknown",
            "recovered retry exception does not become an official failure terminal");
        Assert(events.All(item => item["outcome"]?.Value<string>() != "failed" && item["outcome"]?.Value<string>() != "succeeded"),
            "exception facts never manufacture success or failure terminals");
        Assert(events.All(item => item["accountIdentityConfirmed"]?.Value<bool>() == false), "declared account never becomes verified identity");
        var clickFrames = Events("action_frame").Where(item => item["taskName"]?.Value<string>() == "click").ToArray();
        Assert(clickFrames.Length == 3, "single click has three independently captured frames");
        Assert(clickFrames.Select(item => item["data"]?["raw"]?["sha256"]?.Value<string>()).Distinct().Count() == 3, "three captures contain independent source pixels");
        Assert(clickFrames.Single(item => item["data"]?["phase"]?.Value<string>() == "click_after_100ms")["data"]?["coveredByLaterInput"]?.Value<bool>() == true, "delayed frame marks later input coverage");
        Assert(Events("action_frame").Count(item => item["taskName"]?.Value<string>() == "drag") == 1, "drag produces only the release frame");
        Assert(Events("action_capture_missing").Any(item => item["data"]?["reason"]?.Value<string>() == "multiple_clicks_in_single_batch"), "batched double click exposes missing physical boundaries");
        Assert(Events("action_frame").Count(item => item["taskName"]?.Value<string>() == "capture-failure" && item["data"]?["available"]?.Value<bool>() == false) == 3, "capture failure preserves all three missing phases");
        Assert(Events("official_ocr").Single()["data"]?["boxes"] is JArray { Count: 1 }, "official OCR exports actual boxes and original raw image");
        Assert(File.ReadAllText(Path.Combine(root, "upstream-log.jsonl")).Contains("raw_log_exception", StringComparison.Ordinal), "complete raw logger exception retained");
        Assert(YeYuObservation.PendingCaptureCount == 0, "drain leaves no untracked delayed capture");
    }
    else return 2;
    File.WriteAllText(Path.Combine(root, "replay-result.json"), JsonConvert.SerializeObject(new {
        schemaVersion = 1, passed = true, scenario, assertions,
        realGameCapturePerformed = false, nativeInputSent = false, captureUsesTestDouble = true
    }, Formatting.Indented));
    return 0;
}
catch (Exception exception)
{
    File.WriteAllText(Path.Combine(root, "replay-result.json"), JsonConvert.SerializeObject(new {
        schemaVersion = 1, passed = false, scenario, assertions, error = exception.ToString()
    }, Formatting.Indented));
    return 1;
}
