using System.Security.Cryptography;
using BetterGenshinImpact.Core.Observation;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
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
var cancelFile = Path.Combine(root, "cancel.request");
var uid = "123456789";
File.WriteAllText(scopeFile, JsonConvert.SerializeObject(new {
    schemaVersion = scenario == "invalid-scope" ? 1 : 2, gameId = "Genshin",
    runId = scenario == "invalid-uuid" ? "bad" : "00000000-0000-4000-8000-000000000001",
    runAttemptId = "00000000-0000-4000-8000-000000000002",
    accountId = scenario is "named-without-uid" or "mismatch" ? "00000000-0000-4000-8000-000000000003" : "default",
    expectedUid = scenario == "mismatch" ? "987654321" : (string?)null,
    gameDay = scenario == "invalid-day" ? "2000-02-30" : "2000-01-01", cancelFile,
    taskOperations = new Dictionary<string, string> { ["task"] = "claim-mail", ["daily"] = "claim-daily-rewards", ["pot"] = "claim-serenitea-pot" },
    todoAttemptIds = new Dictionary<string, string> { ["claim-mail"] = "00000000-0000-4000-8000-000000000004",
        ["claim-daily-rewards"] = "00000000-0000-4000-8000-000000000005", ["claim-serenitea-pot"] = "00000000-0000-4000-8000-000000000006" }
}));
Environment.SetEnvironmentVariable("BGI_YEYU_OBSERVATION_DIR", scenario == "off" ? null : root);
Environment.SetEnvironmentVariable("BGI_YEYU_SCOPE_FILE", scenario == "off" ? null : scopeFile);
Environment.SetEnvironmentVariable("BGI_YEYU_CANCEL_FILE", cancelFile);
var assertions = new List<string>();
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
    assertions.Add(message);
}
OcrResult Result(string text) => new(new[] { new OcrResultRegion(
    new RotatedRect(new Point2f(15, 10), new Size2f(10, 5), 0), text, 0.9f) });
try
{
    YeYuObservation.AttachInputObserver();
    Assert(InputObservation.Observer == null, "v2 installs no native input callback");
    var native = new[] { new User32.INPUT { type = User32.INPUTTYPE.INPUT_MOUSE,
        mi = new User32.MOUSEINPUT { dwFlags = User32.MOUSEEVENTF.MOUSEEVENTF_LEFTDOWN } } };
    InputObservation.Notify(native, false); InputObservation.Notify(native, true);
    Assert(native[0].mi.dwFlags == User32.MOUSEEVENTF.MOUSEEVENTF_LEFTDOWN, "official input array is unchanged");
    if (scenario is "off" or "invalid-scope" or "invalid-uuid" or "invalid-day" or "named-without-uid")
    {
        using var task = YeYuObservation.BeginTask("task", "invalid");
        YeYuObservation.Terminal("completed", "sample", "offline");
        Assert(!YeYuObservation.Enabled, "no observer transport without valid explicit scope");
        Assert(!File.Exists(Path.Combine(root, "events.jsonl")), "invalid or disabled scope writes no events");
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
            catch (Exception ex) { YeYuObservation.ExceptionObserved(ex, "offline"); throw; }
        }
        catch (Exception ex) { Assert(ReferenceEquals(ex, original), "evidence write failure preserves exception identity"); }
    }
    else if (scenario is "enabled" or "mismatch" or "composite")
    {
        using var input = new Mat(30, 40, MatType.CV_8UC3, new Scalar(10, 20, 30));
        var ro = new RecognitionObject { Name = "offline recognition" };
        using (YeYuObservation.BeginTask("@identity", "identity"))
        using (YeYuObservation.BeginDiagnosticEvidence("account-uid"))
        {
            YeYuObservation.ObserveOcr(input, ro, Result(uid), new Rect(100, 200, 40, 30));
            YeYuObservation.ObserveIdentity(int.Parse(uid), "offline official UID result double");
        }
        Assert(YeYuObservation.AccountIdentityConfirmed == (scenario != "mismatch"), "actual UID discovery or named UID mismatch determines identity");
        if (scenario == "composite")
        {
            void Phases(bool includeCatherine)
            {
                foreach (var phase in new[] { "encounter-points", "battle-pass-points", "battle-pass-rewards" })
                    YeYuObservation.Fact(phase, "postcondition_anchors_observed", "offline");
                YeYuObservation.Fact("expeditions", "official_collect_and_redispatch", "offline");
                if (includeCatherine) YeYuObservation.Fact("catherine", "postcondition_anchors_observed", "offline");
            }
            using (YeYuObservation.BeginTask("daily", "daily")) Phases(false);
            using (YeYuObservation.BeginTask("@check-rewards", "check")) YeYuObservation.OfficialRewardResult(true, "offline");
            var partial = File.ReadAllLines(Path.Combine(root, "events.jsonl")).Select(JObject.Parse).ToArray();
            Assert(!partial.Any(e => e["type"]?.Value<string>() == "operation_terminal"), "commission OCR plus five phases cannot complete composite");
            using (YeYuObservation.BeginTask("@check-rewards", "full-check"))
            {
                YeYuObservation.Fact("catherine", "postcondition_anchors_observed", "offline");
                YeYuObservation.OfficialRewardResult(true, "offline");
            }
            var complete = File.ReadAllLines(Path.Combine(root, "events.jsonl")).Select(JObject.Parse).Single(e => e["type"]?.Value<string>() == "operation_terminal");
            Assert(complete["taskId"]?.Value<string>() == "daily" && complete["operation"]?.Value<string>() == "claim-daily-rewards", "late aggregate retains original operation scope");
            Assert(complete["todoAttemptId"]?.Value<string>() == "00000000-0000-4000-8000-000000000005", "late aggregate retains exact Todo attempt");
            Assert(complete["data"]?["requiredPhases"]?.Count() == 6 && complete["data"]?["facts"]?.Count() == 6, "completed composite carries all six source facts");
            Assert(complete["data"]?["terminalConfirmed"]?.Value<bool>() == true, "aggregate completion is explicit");
            using (YeYuObservation.BeginTask("daily", "failed-daily")) YeYuObservation.Terminal("failed", "official_failure", "offline");
            using (YeYuObservation.BeginTask("@check-rewards", "failed-check")) YeYuObservation.OfficialRewardResult(true, "offline");
            Assert(File.ReadAllLines(Path.Combine(root, "events.jsonl")).Select(JObject.Parse).Count(e => e["type"]?.Value<string>() == "operation_terminal") == 1, "later complete facts cannot override original failure");
            using (YeYuObservation.BeginTask("pot", "pot"))
            {
                YeYuObservation.Fact("pot-companionship", "official_no_eligible_companion", "offline");
                YeYuObservation.Fact("pot-currency", "postcondition_anchors_observed", "offline");
                YeYuObservation.CompleteOperationFromFacts("claim-serenitea-pot", "confirmed", "offline",
                    new Dictionary<string, string[]> { ["pot-companionship"] = new[] { "postcondition_anchors_observed", "official_no_eligible_companion" }, ["pot-currency"] = new[] { "postcondition_anchors_observed" } });
            }
            Assert(File.ReadAllLines(Path.Combine(root, "events.jsonl")).Select(JObject.Parse).Single(e => e["taskName"]?.Value<string>() == "pot" && e["type"]?.Value<string>() == "task_terminal")["outcome"]?.Value<string>() == "completed", "pot complete comes from both required source postconditions at task exit");
            File.WriteAllText(Path.Combine(root, "replay-result.json"), JsonConvert.SerializeObject(new { schemaVersion = 2, passed = true, scenario, assertions, realGameCapturePerformed = false, nativeInputSent = false, observationSourceLinked = true, recognitionResultsUseTestDoubles = true }, Formatting.Indented));
            return 0;
        }
        using (YeYuObservation.BeginTask("task", "returned")) { }
        using (YeYuObservation.BeginTask("task", "recovered"))
            YeYuObservation.ExceptionObserved(new InvalidOperationException("retry_recovered"), "offline");
        using (YeYuObservation.BeginTask("task", "failed"))
        {
            YeYuObservation.Terminal("failed", "official_exhausted_exception", "offline");
            YeYuObservation.Terminal("completed", "cleanup_returned", "offline");
        }
        using (YeYuObservation.BeginTask("task", "completed"))
            YeYuObservation.Terminal("completed", "official_count_target_met", "offline", new { current = 3, target = 3 });
        using (YeYuObservation.BeginTask("task", "skipped")) YeYuObservation.Skipped("official_skip_branch", "offline");
        using (YeYuObservation.BeginTask("task", "cancelled")) YeYuObservation.CancellationObserved("offline");
        using (YeYuObservation.BeginTask("task", "actions"))
        {
            YeYuObservation.MouseDown("legacy", "left", (nint)17, 10, 20);
            YeYuObservation.MouseMoved("legacy"); YeYuObservation.MouseUp("legacy", "left", (nint)17);
            await YeYuObservation.DrainAsync(TimeSpan.FromSeconds(1));
        }
        using (YeYuObservation.BeginTask("task", "recognition"))
        {
            for (var i = 0; i < 1000; i++) YeYuObservation.ObserveOcr(input, ro, Result("ordinary" + i), default);
            using (YeYuObservation.BeginRewardEvidence("claimed"))
            {
                YeYuObservation.ObserveOcr(input, ro, Result("before positive confirmation"), default, matched: false);
                YeYuObservation.ObserveOcr(input, ro, Result("claimed confirmed"), default, matched: true);
            }
            for (var i = 0; i < 10; i++)
                using (YeYuObservation.BeginKeyStepEvidence("key" + i)) YeYuObservation.ObserveDecision(input, "offline decision", new { i });
            using var invalid = new Mat();
            YeYuObservation.ObserveDecision(invalid, "invalid-input", new { available = false });
        }
        new YeYuObservationSink().Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error,
            new InvalidOperationException("raw_log_exception"), new MessageTemplateParser().Parse("raw {Value}"),
            new[] { new LogEventProperty("Value", new ScalarValue("unmodified")) }));
        File.WriteAllText(cancelFile, "request");
        var cancelled = 0;
        YeYuObservation.RequestCancellationAtSafePoint(() => cancelled++);
        YeYuObservation.RequestCancellationAtSafePoint(() => cancelled++);
        Assert(cancelled == 2, "cancel file calls supplied official cancellation mechanism at safe points");
        var events = File.ReadAllLines(Path.Combine(root, "events.jsonl")).Select(JObject.Parse).ToArray();
        JObject[] Events(string type) => events.Where(e => e["type"]?.Value<string>() == type).ToArray();
        JObject Terminal(string name) => Events("task_terminal").Single(e => e["taskName"]?.Value<string>() == name);
        Assert(Terminal("returned")["outcome"]?.Value<string>() == "unknown", "plain method return remains unknown");
        Assert(Terminal("recovered")["outcome"]?.Value<string>() == "unknown", "recovered exception is not a failed terminal");
        Assert(Terminal("failed")["outcome"]?.Value<string>() == "failed", "explicit failed terminal survives cleanup success");
        Assert(Terminal("completed")["data"]?["terminalConfirmed"]?.Value<bool>() == true, "count-target completion is an explicit terminal");
        Assert(Terminal("skipped")["outcome"]?.Value<string>() == "skipped", "official skip stays distinct");
        Assert(Terminal("cancelled")["outcome"]?.Value<string>() == "cancelled", "official cancellation stays distinct");
        Assert(!events.Any(e => e["type"]?.Value<string>() is "action_frame" or "action_completed"), "legacy click and drag methods archive no new pictures");
        Assert(Events("recognition").Count(e => e["operation"]?.Value<string>() == "claim-mail") == 8, "1000 polls and extra phases stay within two ordinary plus six dedicated images");
        Assert(Events("recognition").All(e => e["data"]?["frameCaptureTimeKnown"]?.Value<bool>() == false && e["data"]?["frameCapturedAt"]?.Type == JTokenType.Null), "unknown source frame times remain null");
        var identity = Events("recognition").Single(e => e["taskId"]?.Value<string>() == "@identity");
        Assert(identity["data"]?["result"]?["boxes"]?[0]?["x"]?.Value<int>() == Result(uid).Regions[0].Rect.BoundingRect().X, "OCR box coordinates match cropped input pixels without source ROI offset");
        foreach (var item in Events("recognition"))
        {
            var raw = item["data"]!["raw"]!; var bytes = File.ReadAllBytes(Path.Combine(root, raw["path"]!.Value<string>()!));
            Assert(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == raw["sha256"]!.Value<string>(), "archived pixels retain verified SHA256");
            using var decoded = Cv2.ImDecode(bytes, ImreadModes.Color);
            Assert(decoded.Width == 40 && decoded.Height == 30 && decoded.At<Vec3b>(0, 0).Equals(new Vec3b(10, 20, 30)), "archive copies supplied recognition input, not a new frame");
        }
        Assert(Events("recognition_sampling_missing").Length > 0, "missing samples are diagnosed without a game failure");
        Assert(Events("cancellation_request").Length == 1, "one cancel request event per process");
        Assert(File.ReadAllText(Path.Combine(root, "upstream-log.jsonl")).Contains("raw_log_exception"), "raw official logger exception is retained");
    }
    else return 2;
    File.WriteAllText(Path.Combine(root, "replay-result.json"), JsonConvert.SerializeObject(new {
        schemaVersion = 2, passed = true, scenario, assertions, realGameCapturePerformed = false,
        nativeInputSent = false, observationSourceLinked = true, recognitionResultsUseTestDoubles = true
    }, Formatting.Indented));
    return 0;
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(root, "replay-result.json"), JsonConvert.SerializeObject(new { schemaVersion = 2, passed = false, scenario, assertions, error = ex.ToString() }, Formatting.Indented));
    return 1;
}
