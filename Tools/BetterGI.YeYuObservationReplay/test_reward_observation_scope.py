"""Offline regression: execute exact source method bodies against inert doubles.

No official app is built or started. The temporary .NET fixture links the real
observer and extracts StartOriginalAsync plus the commission section of
StartSummaryAsync from both the pinned baseline and the working tree. Recognition
inputs/results are synthetic; capture, input, notifications and delays are doubles.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET

BASELINE = "5b2d435d668a40d29520702c09142982ed9e429c"
BRIDGE_VERSION = "0.66.0-yeyu.3"
TASK_PATH = "BetterGenshinImpact/GameTask/Common/Job/CheckRewardsTask.cs"
FORK = Path(__file__).resolve().parents[2]
HIDDEN = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def method(source: str, signature: str) -> str:
    start = source.index(signature)
    opening = source.index("{", start)
    depth, quote, line_comment, block_comment, escaped = 0, False, False, False, False
    for index in range(opening, len(source)):
        char = source[index]
        next_char = source[index + 1:index + 2]
        if line_comment:
            line_comment = char != "\n"
            continue
        if block_comment:
            if char == "/" and source[index - 1:index] == "*":
                block_comment = False
            continue
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                quote = False
            continue
        if char == "/" and next_char == "/":
            line_comment = True
        elif char == "/" and next_char == "*":
            block_comment = True
        elif char == '"':
            quote = True
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[start:index + 1]
    raise AssertionError("unbalanced source method")


def generated(source: str, variant: str) -> str:
    original = method(source, "private async Task StartOriginalAsync(CancellationToken ct)")
    summary = method(source, "private async Task StartSummaryAsync(CancellationToken ct)")
    commission = summary[summary.index("// 委托："):summary.index("// 拼接两块截图：")]
    return """using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Recognition;
using OpenCvSharp;
using static FixtureApi;
public sealed class %sTask
{
    private readonly string _dailyRewardsClaimedLocalizedString = "claimed";
    private readonly string _dailyCommissionRewardsString = "commission";
    private readonly string _commissionsButtonString = "commissionsButton";
    private static RecognitionObject GetConfirmRa(bool isOcrMatch = false, params string[] text)
        => new() { Name = text.FirstOrDefault() ?? "buttons" };
    public Task Original(CancellationToken ct) => StartOriginalAsync(ct);
    public async Task Summary(CancellationToken ct)
    {
        var assetScale = 1.0;
        Mat? commissionStrip = null;
        %s
        commissionStrip?.Dispose();
    }
    private static async Task SafeReturnMainUiAsync(CancellationToken ct)
    {
        try { await new ReturnMainUiTask().Start(ct); }
        catch (Exception e) when (e is not NormalEndException and not OperationCanceledException)
        { Logger.LogDebug(e, "汇总通知：回到主界面失败"); }
    }
    %s
}
""" % (variant, commission, original)


HARNESS = r'''using System.Reflection;
using BetterGenshinImpact.Core.Observation;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

if (args.Length != 4) return 2;
var variant = args[0]; var route = args[1]; var mode = args[2];
var root = Path.GetFullPath(args[3]); Directory.CreateDirectory(root);
var scopePath = Path.Combine(root, "scope.json");
File.WriteAllText(scopePath, JsonConvert.SerializeObject(new {
    schemaVersion = 2, gameId = "Genshin", accountId = "default", expectedUid = (string?)null,
    runId = "00000000-0000-4000-8000-000000000001", runAttemptId = "00000000-0000-4000-8000-000000000002",
    gameDay = "2000-01-01", taskOperations = new Dictionary<string,string> { ["daily"] = "claim-daily-rewards" },
    todoAttemptIds = new Dictionary<string,string> { ["claim-daily-rewards"] = "00000000-0000-4000-8000-000000000003" }
}));
Environment.SetEnvironmentVariable("BGI_YEYU_OBSERVATION_DIR", root);
Environment.SetEnvironmentVariable("BGI_YEYU_SCOPE_FILE", scopePath);
using var cts = new CancellationTokenSource();
FixtureApi.Token = cts.Token; FixtureApi.Mode = mode;
FixtureApi.OriginalException = mode == "ocr_cancel" ? new OperationCanceledException("inert original cancellation", cts.Token)
    : new InvalidOperationException("inert original exception");
FileStream? denied = null;
YeYuObservation.AttachInputObserver();
if (mode == "write_failure") denied = new FileStream(Path.Combine(root, "events.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
Exception? caught = null;
using (YeYuObservation.BeginTask("@check-rewards", "offline real source body"))
{
    try
    {
        if (variant == "Baseline") {
            var task = new BaselineTask();
            if (route == "original") await task.Original(cts.Token); else await task.Summary(cts.Token);
        } else {
            var task = new CandidateTask();
            if (route == "original") await task.Original(cts.Token); else await task.Summary(cts.Token);
        }
    }
    catch (Exception ex) { caught = ex; }
}
denied?.Dispose();
File.WriteAllText(Path.Combine(root, "fixture-result.json"), JsonConvert.SerializeObject(new {
    schemaVersion = 1, variant, route, mode, calls = FixtureApi.Calls, phases = FixtureApi.Phases,
    exceptionType = caught?.GetType().FullName, exceptionReferencePreserved = caught == null || ReferenceEquals(caught, FixtureApi.OriginalException),
    ambientPhaseRestored = FixtureApi.Phase() == null,
    realSourceBodyExtracted = true, productionObserverLinked = true, captureInputAndNotificationsUseInertDoubles = true,
    realGameCapturePerformed = false, nativeInputSent = false, officialToolStarted = false
}, Formatting.Indented));
return caught != null && !ReferenceEquals(caught, FixtureApi.OriginalException) ? 1 : 0;

public static class FixtureApi
{
    public static readonly List<string> Calls = new();
    public static readonly List<object> Phases = new();
    public static string Mode = ""; public static CancellationToken Token;
    public static Exception OriginalException = new InvalidOperationException();
    public static int WaitCalls, ReturnCalls, CaptureCalls;
    public static string? Phase() => ((AsyncLocal<string?>)typeof(YeYuObservation)
        .GetField("EvidencePhase", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Value;
    public static void Probe(string source) => Phases.Add(new { source, phase = Phase() });
    public static void CheckToken(CancellationToken ct) { if (ct != Token) throw new Exception("changed cancellation token"); }
    public static Task Delay(int milliseconds, CancellationToken ct) {
        CheckToken(ct); Calls.Add("delay:" + milliseconds); Probe("delay:" + milliseconds); return Task.CompletedTask;
    }
    public static FakeRegion CaptureToRectArea() {
        Calls.Add("capture:" + ++CaptureCalls); Probe("capture:" + CaptureCalls); return new FakeRegion();
    }
    public static OcrResult Ocr(string text) => new(new[] { new OcrResultRegion(new RotatedRect(new Point2f(15,10),new Size2f(10,5),0),text,0.9f) });
}
public static class NewRetry
{
    public static async Task<bool> WaitForElementAppear(RecognitionObject ro, Action? action, CancellationToken ct, int attempts, int delay)
    {
        FixtureApi.CheckToken(ct); FixtureApi.Calls.Add($"wait:{ro.Name}:{attempts}:{delay}");
        var index = ++FixtureApi.WaitCalls;
        action?.Invoke(); await Task.Yield(); // exercise the real AsyncLocal async boundary
        if (index == 1) return FixtureApi.Mode != "opening_false";
        if (FixtureApi.Mode is "ocr_throw" or "ocr_cancel") throw FixtureApi.OriginalException;
        var matched = FixtureApi.Mode == "claimed";
        using var pixels = new Mat(30,40,MatType.CV_8UC3,new Scalar(10,20,30));
        YeYuObservation.ObserveOcr(pixels,ro,FixtureApi.Ocr(matched ? "claimed" : "not claimed"),new Rect(192,108,576,756),matched);
        FixtureApi.Calls.Add("ocr-result:" + matched.ToString().ToLowerInvariant());
        return matched;
    }
}
public sealed class ReturnMainUiTask
{
    public async Task Start(CancellationToken ct)
    {
        FixtureApi.CheckToken(ct); var index = ++FixtureApi.ReturnCalls; FixtureApi.Calls.Add("return-main:" + index);
        if (index == 1 && FixtureApi.Mode == "initial_return_throw") throw FixtureApi.OriginalException;
        await Task.Yield();
        if (index > 1) {
            using var pixels = new Mat(270,480,MatType.CV_8UC3,new Scalar(40,50,60));
            YeYuObservation.ObserveTemplate(pixels,new RecognitionObject { Name = "paimon_menu" },new Rect(0,0,480,270),20,15,Array.Empty<(Rect,double)>());
            if (FixtureApi.Mode == "final_return_throw") throw FixtureApi.OriginalException;
        }
    }
}
public sealed class FakeRegion : IDisposable
{
    private readonly Mat _pixels = new(30,40,MatType.CV_8UC3,new Scalar(70,80,90));
    public string Text => "commissionsButton";
    public Mat SrcMat { get { FixtureApi.Calls.Add("crop-src-mat"); return _pixels; } }
    public IEnumerable<FakeRegion> FindMulti(RecognitionObject ro) {
        FixtureApi.Calls.Add("find-multi:" + ro.Name); return new[] { this };
    }
    public void Click() => FixtureApi.Calls.Add("click-button");
    public FakeRegion DeriveCrop(Rect rect) {
        FixtureApi.Calls.Add($"crop:{rect.X}:{rect.Y}:{rect.Width}:{rect.Height}"); return new FakeRegion();
    }
    public void Dispose() { FixtureApi.Calls.Add("region-dispose"); _pixels.Dispose(); }
}
public static class Simulation { public static FakeInput SendInput { get; } = new(); }
public sealed class FakeInput { public void SimulateAction(GIActions action) => FixtureApi.Calls.Add("simulate:" + action); }
public enum GIActions { OpenAdventurerHandbook }
public enum NotificationEvent { DailyReward }
public static class Notify { public static FakeNotification Event(NotificationEvent value) { FixtureApi.Calls.Add("notify:" + value); return new(); } }
public sealed class FakeNotification
{
    public void Success(string message) => FixtureApi.Calls.Add("notify-success:" + message);
    public void Error(string message) => FixtureApi.Calls.Add("notify-error:" + message);
}
public static class Logger
{
    public static void LogInformation(string message, params object[] args) => FixtureApi.Calls.Add("info:" + message + ":" + JsonConvert.SerializeObject(args));
    public static void LogWarning(string message, params object[] args) => FixtureApi.Calls.Add("warning:" + message + ":" + JsonConvert.SerializeObject(args));
    public static void LogDebug(Exception ex, string message) => FixtureApi.Calls.Add("debug:" + message + ":" + ex.GetType().FullName);
}
public sealed class NormalEndException : Exception { }
'''


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--package-cache", type=Path)
    args = parser.parse_args()
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    ledger: list[dict] = []

    def run(argv: list[str], purpose: str, log: Path, cwd: Path = FORK) -> None:
        with log.open("wb") as output:
            process = subprocess.Popen(argv, cwd=cwd, stdout=output, stderr=subprocess.STDOUT, creationflags=HIDDEN)
            ledger.append({"purpose": purpose, "pid": process.pid, "parentPid": os.getpid(), "startedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                           "launchSource": "/root/bettergi_shipping/test_reward_observation_scope.py", "sourceScript": str(Path(__file__).resolve()), "arguments": argv,
                           "logPath": str(log), "consoleHidden": bool(HIDDEN)})
            (evidence / "processes.json").write_text(json.dumps(ledger, ensure_ascii=False, indent=2), encoding="utf-8")
            try:
                code = process.wait(timeout=180)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
                raise
            ledger[-1]["exitCode"] = code
            (evidence / "processes.json").write_text(json.dumps(ledger, ensure_ascii=False, indent=2), encoding="utf-8")
            if code:
                raise AssertionError(f"{purpose} exit={code}; log={log}")

    baseline = subprocess.check_output(["git", "show", f"{BASELINE}:{TASK_PATH}"], cwd=FORK).decode("utf-8")
    candidate = (FORK / TASK_PATH).read_bytes().decode("utf-8")
    app_metadata = ET.parse(FORK / "BetterGenshinImpact/BetterGenshinImpact.csproj").getroot()
    assert app_metadata.findtext(".//Version") == "0.66.0", "official version changed"
    bridge = next(item for item in app_metadata.findall(".//AssemblyAttribute") if item.findtext("_Parameter1") == "YeYuBridgeVersion")
    assert bridge.findtext("_Parameter2") == BRIDGE_VERSION, "bridge assembly metadata mismatch"
    probe_source = (FORK / "Tools/BetterGI.YeYuProbe/Program.cs").read_text(encoding="utf-8")
    assert probe_source.count(BRIDGE_VERSION) == 2 and "0.66.0-yeyu.2" not in probe_source, "installation probe version mismatch"
    project = evidence / "fixture"
    project.mkdir()
    (project / "Program.cs").write_text(HARNESS, encoding="utf-8")
    for variant, source in (("Baseline", baseline), ("Candidate", candidate)):
        (project / f"{variant}Task.cs").write_text(generated(source, variant), encoding="utf-8")
    xml = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(xml, "PropertyGroup")
    for key, value in {"OutputType": "Exe", "TargetFramework": "net8.0-windows10.0.22621.0", "PlatformTarget": "x64", "ImplicitUsings": "enable", "Nullable": "enable"}.items():
        ET.SubElement(properties, key).text = value
    if args.package_cache:
        ET.SubElement(properties, "RestorePackagesPath").text = str(args.package_cache.resolve())
    items = ET.SubElement(xml, "ItemGroup")
    for path in [FORK / "BetterGenshinImpact/Core/Observation/YeYuObservation.cs", Path(__file__).with_name("RecognitionTestDoubles.cs")]:
        ET.SubElement(items, "Compile", Include=str(path), Link=path.name)
    references = ET.parse(Path(__file__).with_name("BetterGI.YeYuObservationReplay.csproj")).getroot().findall(".//PackageReference")
    for reference in references:
        if reference.attrib["Include"] != "Vanara.PInvoke.User32":
            ET.SubElement(items, "PackageReference", **reference.attrib)
    project_file = project / "RewardScopeReplay.csproj"
    ET.ElementTree(xml).write(project_file, encoding="utf-8", xml_declaration=True)
    run([args.dotnet, "build", str(project_file), "-c", "Release", "--disable-build-servers", "--nologo", "-p:NuGetAudit=false"], "compile inert fixture, never official tool", evidence / "fixture-build.log")
    assembly = project / "bin/Release/net8.0-windows10.0.22621.0/RewardScopeReplay.dll"
    cases = []
    for route in ("original", "summary"):
        for mode in ("not_claimed", "claimed", "opening_false", "ocr_throw", "ocr_cancel", "initial_return_throw", "final_return_throw", "write_failure"):
            results = {}
            event_sets = {}
            for variant in ("Baseline", "Candidate"):
                output = evidence / f"{route}-{mode}-{variant.lower()}"
                run([args.dotnet, str(assembly), variant, route, mode, str(output)], f"inert {route}/{mode}/{variant} real source body replay", evidence / f"{output.name}.log")
                results[variant] = json.loads((output / "fixture-result.json").read_text(encoding="utf-8"))
                event_sets[variant] = [json.loads(line) for line in (output / "events.jsonl").read_text(encoding="utf-8").splitlines()]
                assert all(event["schemaVersion"] == 2 and event["observerVersion"] == BRIDGE_VERSION for event in event_sets[variant]), "event schema/bridge version mismatch"
                assert event_sets[variant][0]["data"]["observationVersion"] == BRIDGE_VERSION, "observer startup version mismatch"
            before, after = results["Baseline"], results["Candidate"]
            assert before["calls"] == after["calls"], f"official call/parameter/order/branch changed: {route}/{mode}"
            assert before["exceptionType"] == after["exceptionType"], f"official exception policy changed: {route}/{mode}"
            assert before["exceptionReferencePreserved"] and after["exceptionReferencePreserved"], f"original exception changed: {route}/{mode}"
            assert after["ambientPhaseRestored"], f"phase not restored on return/exception: {route}/{mode}"
            captures = [item for item in after["phases"] if item["source"].startswith("capture:")]
            if route == "summary" and len(captures) > 1:
                assert captures[-1]["phase"] is None, "summary observation leaks into handbook capture"
            recognition = [event for event in event_sets["Candidate"] if event["type"] == "recognition"]
            returned = [event for event in recognition if event["data"]["method"] == "ImageRegion.TemplateMatch"]
            assert all(event["data"]["phase"] is None and event["data"]["raw"]["kind"] == "game-ui-recognition-raw" for event in returned), "return template falsely tagged reward evidence"
            if mode in ("not_claimed", "claimed", "opening_false", "final_return_throw") and (route == "original" or mode != "opening_false"):
                assert len(returned) == 1, f"ordinary return template not archived in fixture: {route}/{mode}"
                assert returned[0]["data"]["width"] == 480 and returned[0]["data"]["height"] == 270 and returned[0]["data"]["result"]["boxes"] == []
            if mode == "claimed":
                reward = [event for event in recognition if event["data"]["phase"] == "daily-commission-reward"]
                assert len(reward) == 1 and reward[0]["data"]["raw"]["kind"] == "game-ui-reward-verification-raw" and reward[0]["data"]["result"]["matched"] is True, "successful official OCR lost reward attribution"
            if route == "original" and mode == "not_claimed":
                old_return = next(event for event in event_sets["Baseline"] if event["type"] == "recognition" and event["data"]["method"] == "ImageRegion.TemplateMatch")
                assert old_return["data"]["raw"]["kind"] == "game-ui-reward-verification-raw" and old_return["data"]["phase"] == "daily-commission-reward", "pinned baseline did not reproduce actual defect"
            facts_before = [event["data"] for event in event_sets["Baseline"] if event["type"] == "official_reward_result"]
            facts_after = [event["data"] for event in event_sets["Candidate"] if event["type"] == "official_reward_result"]
            assert facts_before == facts_after, "official reward result transport changed"
            assert not any(event["type"] == "operation_terminal" for event in event_sets["Candidate"]), "fixture made completion without composite official facts"
            for event in recognition:
                raw = event["data"]["raw"]
                assert sha((evidence / f"{route}-{mode}-candidate" / raw["path"]).read_bytes()) == raw["sha256"], "archived input hash changed"
            cases.append({"route": route, "mode": mode, "passed": True, "officialTraceEqual": True, "exceptionPolicyAndIdentityEqual": True,
                          "candidateReturnTemplateCount": len(returned), "candidateRecognitionCount": len(recognition), "candidateRewardResultCount": len(facts_after)})
    report = {"schemaVersion": 1, "passed": True, "baselineCommit": BASELINE, "bridgeVersion": BRIDGE_VERSION,
              "baselineTaskSourceSha256": sha(baseline.encode()), "candidateTaskSourceSha256": sha(candidate.encode()),
              "observerSourceSha256": sha((FORK / "BetterGenshinImpact/Core/Observation/YeYuObservation.cs").read_bytes()),
              "testSourceSha256": sha(Path(__file__).read_bytes()), "fixtureAssemblySha256": sha(assembly.read_bytes()),
              "cases": cases, "caseCount": len(cases), "compiledSourceBodies": ["StartOriginalAsync (full)", "StartSummaryAsync (actual commission try/catch/finally section)"],
              "baselineDefectReproduced": True, "productionObservationSourceLinked": True,
              "officialToolBuilt": False, "officialToolStarted": False, "realGameCapturePerformed": False,
              "nativeInputSent": False, "notificationSent": False, "promoted": False, "evidenceRoot": str(evidence)}
    (evidence / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"passed": True, "caseCount": len(cases), "report": str(evidence / "report.json")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
