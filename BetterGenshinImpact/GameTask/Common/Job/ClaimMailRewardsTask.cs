using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.Common.Job;

/// <summary>
/// 领取邮件奖励
/// </summary>
public class ClaimMailRewardsTask
{
    private readonly ReturnMainUiTask _returnMainUiTask = new();

    public async Task Start(CancellationToken ct)
    {
        try
        {
            await DoOnce(ct);
        }
        catch (Exception e)
        {
            BetterGenshinImpact.Core.Observation.YeYuObservation.ExceptionObserved(e, "ClaimMailRewardsTask.Start", e is OperationCanceledException);
            BetterGenshinImpact.Core.Observation.YeYuObservation.Terminal(e is OperationCanceledException || ct.IsCancellationRequested ? "cancelled" : "failed", "official_unrecovered_task_exception", "ClaimMailRewardsTask.Start", new { exception = e.ToString() });
            Logger.LogDebug(e, "领取邮件奖励异常");
            Logger.LogError("领取邮件奖励异常: {Msg}", e.Message);
        }
    }

    public async Task DoOnce(CancellationToken ct)
    {
        await _returnMainUiTask.Start(ct);

        await Delay(200, ct);

        TaskContext.Instance().PostMessageSimulator.SimulateAction(GIActions.OpenPaimonMenu); // ESC 

        await Delay(1300, ct);

        using (var ra = CaptureToRectArea())
        {
            var mailIcon = ra.Find(ElementRecognition.Get("EscMailReward", ra));
            if (mailIcon.IsExist())
            {
                mailIcon.Click();
                await Delay(1000, ct);

                using var claimArea = CaptureToRectArea();
                var claimAll = claimArea.Find(ElementRecognition.Get("Collect", claimArea));
                if (claimAll.IsExist())
                {
                    claimAll.Click();
                    Logger.LogInformation("邮件：{Text}", "全部领取");
                    await Delay(200, ct);
                    if (BetterGenshinImpact.Core.Observation.YeYuOfficialResult.RewardPage("mail-all-claimed", "ClaimMailRewardsTask.DoOnce", "获得", "点击空白区域继续"))
                        BetterGenshinImpact.Core.Observation.YeYuObservation.Terminal("completed", "mail_reward_popup_confirmed", "ClaimMailRewardsTask.DoOnce");
                    else
                        BetterGenshinImpact.Core.Observation.YeYuObservation.Fact("mail-all-claimed", "action_attempted_postcondition_unconfirmed", "ClaimMailRewardsTask.DoOnce");

                    TaskContext.Instance().PostMessageSimulator.KeyPress(User32.VK.VK_ESCAPE); // ESC 
                }
            }
            else
            {
                BetterGenshinImpact.Core.Observation.YeYuOfficialResult.Decision(ra, "mail-no-eligible-reward", "ClaimMailRewardsTask.DoOnce", new { mailRewardIconExists = false }, reward: true);
                BetterGenshinImpact.Core.Observation.YeYuObservation.Skipped("official_no_mail_reward_icon", "ClaimMailRewardsTask.DoOnce");
                Logger.LogInformation("邮件：{Text}", "没有邮件奖励");
            }
        }

        // 关闭
        await _returnMainUiTask.Start(ct);
    }
}
