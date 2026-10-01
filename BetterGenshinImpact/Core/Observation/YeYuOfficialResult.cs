using System;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.Core.Observation;

/// <summary>New opt-in, read-only upstream reporting capability. It neither
/// opens a page nor clicks, waits or retries. Game execution remains official.</summary>
public static class YeYuOfficialResult
{
    public static void RequireValidScopeBeforeStart()
    {
        if (YeYuObservation.Requested && !YeYuObservation.Enabled)
            throw new OperationCanceledException("YeYu authorization scope is invalid; official game startup refused");
    }
    public static void ConfirmAccountBeforeTasks()
    {
        if (!YeYuObservation.Enabled) return;
        using (YeYuObservation.BeginTask("@identity", "Official account identity OCR"))
        using (YeYuObservation.BeginDiagnosticEvidence("account-uid"))
        {
            var uid = Bv.Uid();
            YeYuObservation.ObserveIdentity(uid, "Bv.Uid");
        }
        if (!YeYuObservation.AccountIdentityConfirmed)
        {
            YeYuObservation.FlowTerminal("cancelled", "account_identity_not_confirmed", "YeYuOfficialResult.ConfirmAccountBeforeTasks");
            CancellationContext.Instance.Cancel();
            throw new OperationCanceledException("YeYu account identity gate: official UID was not confirmed");
        }
    }

    /// <summary>One new postcondition OCR, on the page the official task still
    /// has open. No new page/input/delay/retry, and all failures are fail-open.</summary>
    public static bool RewardPage(string phase, string source, params string[] positiveAnchors)
        => Page(phase, source, true, positiveAnchors);
    public static bool KeyPage(string phase, string source, params string[] positiveAnchors)
        => Page(phase, source, false, positiveAnchors);
    private static bool Page(string phase, string source, bool reward, string[] positiveAnchors)
    {
        if (!YeYuObservation.Enabled) return false;
        try
        {
            using var capture = TaskControl.CaptureToRectArea();
            using var evidence = reward ? YeYuObservation.BeginRewardEvidence(phase) : YeYuObservation.BeginKeyStepEvidence(phase);
            var regions = capture.FindMulti(RecognitionObject.Ocr(0, 0, capture.Width, capture.Height));
            var text = string.Join("\n", regions.Select(r => r.Text));
            var negativeAnchors = new[] { "领取失败", "网络错误", "连接超时", "请先登录", "数量不足" };
            var anchorsPresent = positiveAnchors.Length >= 2 && positiveAnchors.All(a => text.Contains(a, StringComparison.Ordinal))
                && !negativeAnchors.Any(a => text.Contains(a, StringComparison.Ordinal));
            YeYuObservation.Fact(phase, anchorsPresent ? "postcondition_anchors_observed" : "postcondition_unconfirmed", source,
                new { text, positiveAnchors, negativeAnchors, anchorsPresent, managerVerification = "not_performed" });
            return anchorsPresent;
        }
        catch (Exception exception)
        {
            YeYuObservation.Fact(phase, "postcondition_observation_failed", source, new { error = exception.ToString() });
            return false;
        }
    }

    public static void Decision(ImageRegion actualInput, string phase, string source, object result, bool reward = false)
    {
        if (!YeYuObservation.Enabled) return;
        using var evidence = reward ? YeYuObservation.BeginRewardEvidence(phase) : YeYuObservation.BeginKeyStepEvidence(phase);
        YeYuObservation.ObserveDecision(actualInput.SrcMat, source, result);
    }
}
