using GameData.Domains;
using GameData.Domains.Item;
using GameData.Domains.TaiwuEvent;
using GameData.Utilities;
using HarmonyLib;

namespace MonthInteractionBackend
{
    /// <summary>
    /// 促织决斗结算恢复补丁 —— 战斗结算后发 ModDisplayEvent 通知 IncreaseDifficulty 恢复物品遮蔽。
    ///
    /// <see cref="CricketBattleEvent"/> 触发促织时发了 "CricketMaskOff" 通知屏蔽 IncreaseDifficulty 的物品遮蔽。
    /// 本 patch 在 <see cref="TaiwuEventDomain.SetCricketBettingResult"/>（促织战斗结算）的 Postfix 发
    /// "CricketMaskOn" 通知，让 IncreaseDifficulty 恢复正常遮蔽。
    ///
    /// 无论玩家是赢是输、确认还是取消，SetCricketBettingResult 都会被调用（结算），保证恢复通知一定发出。
    /// </summary>
    [HarmonyPatch]
    public class CricketBettingResultPatch
    {
        private const string SelfModId = "MonthInteraction.Backend";
        private const string NotifyMaskOn = "CricketMaskOn";

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TaiwuEventDomain), "SetCricketBettingResult")]
        public static void SetCricketBettingResultPostfix(bool ok, Wager wager, int index)
        {
            try
            {
                DomainManager.Mod.AddModDisplayEvent(SelfModId, NotifyMaskOn);
                AdaptableLog.Info($"[{MonthInteraction.LogTag}] 促织结算完成，已发恢复通知 {NotifyMaskOn}（ok={ok}）");
            }
            catch (System.Exception ex)
            {
                AdaptableLog.Info($"[{MonthInteraction.LogTag}] 促织结算恢复通知异常: {ex.Message}");
            }
        }
    }
}
