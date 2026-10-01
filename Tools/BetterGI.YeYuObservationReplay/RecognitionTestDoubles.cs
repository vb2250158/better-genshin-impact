using OpenCvSharp;
// Explicit offline recognition result doubles. No capture or game App type is
// present; the linked production observer cannot obtain a fresh window frame.
namespace BetterGenshinImpact.Core.Recognition
{
    public sealed class RecognitionObject
    {
        public string? Name { get; set; }
        public double Threshold { get; set; } = 0.8;
        public TemplateMatchModes TemplateMatchMode { get; set; } = TemplateMatchModes.CCoeffNormed;
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
