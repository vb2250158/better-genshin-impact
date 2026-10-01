using System.Diagnostics;
using System.Security.Cryptography;
using Fischless.GameCapture;
using Microsoft.ML.OnnxRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

// This process deliberately has no reference to BetterGenshinImpact/App. It
// never starts capture, enumerates windows/processes, or sends keyboard/mouse input.
string? installRoot = null;
string? reportPath = null;
if (args.Length == 2 && args[0] != "--yeyu-probe")
{
    installRoot = args[0];
    reportPath = args[1];
}
else if (args.Length == 4 && args[0] == "--yeyu-probe" && args[2] == "--root")
{
    reportPath = args[1];
    installRoot = args[3];
}
if (installRoot == null || reportPath == null) return 2;

var checks = new List<object>();
var passed = true;
void Check(string name, Func<object> action)
{
    try { checks.Add(new { name, passed = true, evidence = action() }); }
    catch (Exception exception)
    {
        passed = false;
        checks.Add(new { name, passed = false, exceptionType = exception.GetType().FullName,
            error = exception.ToString() });
    }
}
object Resource(string relative)
{
    var path = Path.Combine(installRoot!, relative);
    if (!File.Exists(path)) throw new FileNotFoundException("Required installed resource missing", path);
    using var stream = File.OpenRead(path);
    return new { path = relative, bytes = stream.Length,
        sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
}

installRoot = Path.GetFullPath(installRoot);
reportPath = Path.GetFullPath(reportPath);
Check("source_version", () => {
    var path = Path.Combine(installRoot, "BetterGI.exe");
    var version = FileVersionInfo.GetVersionInfo(path);
    if (version.ProductVersion?.StartsWith("0.66.0", StringComparison.Ordinal) != true)
        throw new InvalidDataException("Expected official 0.66.0 source version");
    return new { fileVersion = version.FileVersion, productVersion = version.ProductVersion,
        executable = Resource("BetterGI.exe") };
});
Check("source_manifest", () => {
    var path = Path.Combine(installRoot, "yeyu-source-manifest.json");
    var manifest = JObject.Parse(File.ReadAllText(path));
    if (manifest["schemaVersion"]?.Value<int>() != 1 ||
        manifest["upstreamRelease"]?.Value<string>() != "0.66.0" ||
        manifest["observationVersion"]?.Value<string>() != "0.66.0-yeyu.2" ||
        manifest["baseCommit"]?.Value<string>() != "0af68c28f6b0e714d5304625ce644736d508022e" ||
        manifest["license"]?.Value<string>() != "GPL-3.0")
        throw new InvalidDataException("Installed source manifest does not match audited source/bridge contract");
    return new { manifest, resource = Resource("yeyu-source-manifest.json"),
        observerBridgeVersion = "0.66.0-yeyu.2",
        note = "Manifest declaration; executable version and resource hashes checked independently" };
});
Check("opencv_native", () => {
    using var image = new Mat(20, 40, MatType.CV_8UC3, new Scalar(30, 60, 90));
    Cv2.ImEncode(".png", image, out var bytes);
    using var decoded = Cv2.ImDecode(bytes, ImreadModes.Color);
    if (decoded.Width != 40 || decoded.Height != 20) throw new InvalidDataException("OpenCV roundtrip failed");
    return new { nativeVersion = Cv2.GetVersionString(), width = decoded.Width,
        height = decoded.Height, encodedBytes = bytes.Length };
});
Check("capture_library", () => {
    var modes = new List<string>();
    foreach (var mode in Enum.GetValues<CaptureModes>())
    {
        using var capture = GameCaptureFactory.Create(mode);
        if (capture.IsCapturing) throw new InvalidOperationException("A new capture must not be started");
        modes.Add(capture.GetType().FullName!);
    }
    return new { modes, captureStarted = false, windowLookupPerformed = false };
});
foreach (var relative in new[] {
    @"GameTask\AutoFight\Assets\combat_avatar.json",
    @"User\AutoFight\群友分享\万能战斗策略（萌新推荐）.txt",
    @"Assets\Model\PaddleOCR\test_pp_ocr.png",
    @"Assets\Model\PaddleOCR\Det\V4\PP-OCRv4_mobile_det_infer\slim.onnx",
    @"Assets\Model\PaddleOCR\Rec\V4\PP-OCRv4_mobile_rec_infer\slim.onnx",
    @"Assets\Model\PaddleOCR\Det\V5\PP-OCRv5_mobile_det_infer\slim.onnx",
    @"Assets\Model\PaddleOCR\Rec\V5\PP-OCRv5_mobile_rec_infer\slim.onnx"
})
{
    Check("resource:" + relative, () => Resource(relative));
    if (!relative.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) continue;
    Check("onnx_load:" + relative, () => {
        using var options = new SessionOptions();
        using var session = new InferenceSession(Path.Combine(installRoot, relative), options);
        return new { model = relative, executionProvider = "CPU", inputNames = session.InputNames,
            outputNames = session.OutputNames,
            inputs = session.InputMetadata.ToDictionary(item => item.Key, item => new {
                type = item.Value.ElementType.FullName, dimensions = item.Value.Dimensions }),
            resource = Resource(relative) };
    });
    if (relative.Contains(@"\Rec\", StringComparison.Ordinal))
    {
        var yaml = Path.Combine(Path.GetDirectoryName(relative)!, "inference.yml");
        Check("resource:" + yaml, () => Resource(yaml));
    }
}
foreach (var directory in new[] { @"Assets\Map", @"Assets\Config", @"GameTask\Common\Element\Assets\1920x1080" })
{
    Check("resource_directory:" + directory, () => {
        var files = Directory.GetFiles(Path.Combine(installRoot, directory), "*", SearchOption.AllDirectories);
        if (files.Length == 0) throw new InvalidDataException("Required resource directory is empty");
        return new { path = directory, fileCount = files.Length,
            bytes = files.Sum(path => new FileInfo(path).Length) };
    });
}
var report = new { schemaVersion = 1, probeKind = "bettergi_installation", passed, success = passed,
    generatedAtUtc = DateTimeOffset.UtcNow, installRoot, processId = Environment.ProcessId,
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    appHostStarted = false, gameLookupPerformed = false, captureStarted = false,
    inputSent = false, protocolRegistered = false, checks };
try
{
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
}
catch { return 3; }
return passed ? 0 : 1;
