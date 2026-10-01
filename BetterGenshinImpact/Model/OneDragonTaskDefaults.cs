using System.Collections.ObjectModel;
namespace BetterGenshinImpact.Model;
public static class OneDragonTaskDefaults
{
    public static ObservableCollection<OneDragonTaskItem> Create() =>
    [
        new("领取邮件"),
        new("合成树脂"),
        // new ("每日委托"),
        new("自动秘境"),
        new ("自动首领讨伐"),
        new ("自动幽境危战"),
        new ("自动地脉花"),
        new("领取每日奖励"),
        new ("领取尘歌壶奖励"),
        // new ("自动七圣召唤"),
    ];
}
