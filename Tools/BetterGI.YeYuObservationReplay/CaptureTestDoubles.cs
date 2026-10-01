using OpenCvSharp;

// Explicit offline test doubles. This executable never references the game App
// or the native input dispatcher; production builds never compile this file.
namespace Fischless.GameCapture.BitBlt
{
    public sealed class BitBltCapture : IDisposable
    {
        private static int _frameNumber;
        public static bool FailCapture { get; set; }
        public void Start(nint window, Dictionary<string, object> settings) { }
        public CapturedFrame Capture()
        {
            if (FailCapture) throw new InvalidOperationException("offline_capture_failure");
            return new CapturedFrame(new Mat(30, 40, MatType.CV_8UC3,
                new Scalar(Interlocked.Increment(ref _frameNumber), 30, 40)));
        }
        public void Dispose() { }
        public sealed class CapturedFrame(Mat frame) : IDisposable
        {
            public Mat FramePixels { get; } = frame;
            public Mat Frame => FramePixels;
            public void Dispose() => FramePixels.Dispose();
        }
    }
}
namespace BetterGenshinImpact.GameTask
{
    public sealed class TaskContext
    {
        private static readonly TaskContext Context = new();
        public static TaskContext Instance() => Context;
        public nint GameHandle => (nint)17;
    }
}
namespace BetterGenshinImpact.Core.Recognition
{
    public sealed class RecognitionObject
    {
        public List<string> AllContainMatchText { get; } = new();
        public List<string> OneContainMatchText { get; } = new();
        public List<string> RegexMatchText { get; } = new();
    }
}
namespace BetterGenshinImpact.Core.Recognition.OCR
{
    public sealed record OcrResultRegion(RotatedRect Rect, string Text, float Score);
    public sealed record OcrResult(OcrResultRegion[] Regions)
    {
        public string Text => string.Join("\n", Regions.Select(region => region.Text));
    }
}
