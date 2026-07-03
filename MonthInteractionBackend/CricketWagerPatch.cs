using System.Collections.Generic;
using GameData.Domains.Item;
using GameData.Utilities;
using HarmonyLib;

namespace MonthInteractionBackend
{
    /// <summary>
    /// 促织决斗 —— 后端注入指定物品到下注奖励列表。
    ///
    /// <see cref="SelectCricketWagersPostfix"/>：把原版生成的 BetRewards 中第一个物品类（Type==1）
    /// 的 Wager 替换为 <see cref="PendingItemKey"/> 指定的物品。
    ///
    /// ★ 为什么用 Postfix 改 __result 而非 specialWagerData？
    ///   原版 AddCricketBattleDisplayEvent 里 data.BetRewards = specialWagerData ?? SelectCricketWagers(context)。
    ///   自己构造 specialWagerData 会导致 SelectCricketWagers 被调两次（一次自己调、一次原版在 ?? 里调），
    ///   且后端结算 SetCricketBettingResult 用自己的 BetRewards 副本按索引查奖励，
    ///   前后端索引错位导致胜利拿不到正确物品。
    ///   Postfix 改 __result 则数据源头就改好，前后端索引天然一致（和 IncreaseDifficulty 同模式）。
    ///
    /// <see cref="PendingItemKey"/> 由 CricketBattleEvent.ExecuteInteraction（事件包）通过反射设置，
    /// 每次促织事件前置位、本 Postfix 用完清空。
    /// </summary>
    [HarmonyPatch]
    public class CricketWagerPatch
    {
        /// <summary>本次促织决斗要注入下注列表的物品 ItemKey。
        /// 事件包代码（CricketBattleEvent）通过反射设置；本 Postfix 读取后清空。
        /// ItemKey.Invalid 表示不注入（用原版默认）。</summary>
        public static ItemKey PendingItemKey = ItemKey.Invalid;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemDomain), "SelectCricketWagers")]
        public static void SelectCricketWagersPostfix(ref List<CricketWagerData> __result)
        {
            if (!PendingItemKey.IsValid()) return;
            if (__result == null || __result.Count == 0) return;

            // 找第一个物品类（Type==1）替换为指定物品
            bool replaced = false;
            for (int i = 0; i < __result.Count; i++)
            {
                if (__result[i]?.Wager.Type == 1)
                {
                    __result[i].Wager = Wager.CreateItem(PendingItemKey, 1);
                    replaced = true;
                    AdaptableLog.Info($"[{MonthInteraction.LogTag}] 后端促织：已替换下注列表[{i}]为 {PendingItemKey}");
                    break;
                }
            }

            // 原版没生成物品类下注（NPC 无合适物品），追加一项
            if (!replaced)
            {
                __result.Add(new CricketWagerData
                {
                    Wager = Wager.CreateItem(PendingItemKey, 1)
                });
                AdaptableLog.Info($"[{MonthInteraction.LogTag}] 后端促织：原版无物品下注，已追加 {PendingItemKey}");
            }

            // 用完清空，避免污染后续促织决斗
            PendingItemKey = ItemKey.Invalid;
        }
    }
}
