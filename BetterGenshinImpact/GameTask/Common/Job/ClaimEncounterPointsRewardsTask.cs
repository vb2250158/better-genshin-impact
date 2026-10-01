using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.Common.Job;

/// <summary>
/// 领取长效历练点奖励
/// </summary>
public class ClaimEncounterPointsRewardsTask
{
    private readonly ReturnMainUiTask _returnMainUiTask = new();

    private readonly string commissionsLocalizedString;

    public ClaimEncounterPointsRewardsTask()
    {
        IStringLocalizer<ClaimEncounterPointsRewardsTask> stringLocalizer = App.GetService<IStringLocalizer<ClaimEncounterPointsRewardsTask>>() ?? throw new NullReferenceException();
        CultureInfo cultureInfo = new CultureInfo(TaskContext.Instance().Config.OtherConfig.GameCultureInfoName);
        this.commissionsLocalizedString = stringLocalizer.WithCultureGet(cultureInfo, "委托");
    }

    public async Task Start(CancellationToken ct)
    {
        try
        {
            await DoOnce(ct);
        }
        catch (Exception e)
        {
            BetterGenshinImpact.Core.Observation.YeYuObservation.ExceptionObserved(e, "ClaimEncounterPointsRewardsTask.Start", e is OperationCanceledException);
            BetterGenshinImpact.Core.Observation.YeYuObservation.Terminal(e is OperationCanceledException || ct.IsCancellationRequested ? "cancelled" : "failed", "official_unrecovered_task_exception", "ClaimEncounterPointsRewardsTask.Start", new { exception = e.ToString() });
            Logger.LogDebug(e, "领取长效历练点奖励异常");
            Logger.LogError("领取长效历练点奖励异常: {Msg}", e.Message);
        }
    }

    public async Task DoOnce(CancellationToken ct)
    {
        await _returnMainUiTask.Start(ct);

        await Delay(200, ct);

        TaskContext.Instance().PostMessageSimulator.SimulateAction(GIActions.OpenAdventurerHandbook); // F1 开书

        await Delay(1000, ct);

        var assetScale = TaskContext.Instance().SystemInfo.AssetScale;

        var earlyClaim = false; // 无需点击委托直接领取
        // 找委托按钮
        var f1Success = await NewRetry.WaitForAction(() =>
        {
            using var ra = CaptureToRectArea();

            var ocrList = ra.FindMulti(RecognitionObject.Ocr(0, 0, 380 * assetScale, ra.Height));

            var wt = ocrList.FirstOrDefault(txt => Regex.IsMatch(txt.Text, this.commissionsLocalizedString));

            if (wt != null)
            {
                using var claimCapture = CaptureToRectArea();
                if (ClickClaimBtn(claimCapture))
                {
                    earlyClaim = true;
                    return true;
                }

                wt.Click();
                return true;
            }

            return false;
        }, ct, 5);

        if (!f1Success)
        {
            BetterGenshinImpact.Core.Observation.YeYuObservation.Fact("encounter-points", "official_page_open_failed", "ClaimEncounterPointsRewardsTask.DoOnce");
            Logger.LogError("{F}未找到委托按钮,F1打开冒险之证失败", "历练点：");
            return;
        }

        if (earlyClaim)
        {
            // 已经领取过
            await Delay(1000, ct);

            BetterGenshinImpact.Core.Observation.YeYuOfficialResult.RewardPage("encounter-points", "ClaimEncounterPointsRewardsTask.DoOnce", "历练点", "已领取");
            return;
        }

        await Delay(1000, ct);

        // 领取
        using var claimCapture = CaptureToRectArea();
        if (ClickClaimBtn(claimCapture))
        {
            await Delay(1000, ct);

            BetterGenshinImpact.Core.Observation.YeYuOfficialResult.RewardPage("encounter-points", "ClaimEncounterPointsRewardsTask.DoOnce", "历练点", "已领取");
        }

        // 关闭
        await _returnMainUiTask.Start(ct);
    }

    private static bool ClickClaimBtn(ImageRegion ra2)
    {
        var claimBtn = ra2.Find(ElementRecognition.Get("BtnClaimEncounterPointsRewards", ra2));
        if (claimBtn.IsExist())
        {
            claimBtn.Click();
            Logger.LogInformation("{F}领取长效历练点奖励", "历练点：");


            return true;
        }
        else
        {
            BetterGenshinImpact.Core.Observation.YeYuObservation.Fact("encounter-points", "official_claim_button_missing_ambiguous", "ClaimEncounterPointsRewardsTask.ClickClaimBtn");
            Logger.LogInformation("{F}未找到领取历练点奖励按钮", "历练点：");
            return false;
        }
    }
}
