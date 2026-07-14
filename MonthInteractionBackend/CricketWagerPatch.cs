using System.Collections.Generic;
using System.Reflection;
using GameData.Common;
using GameData.Domains.Item;
using GameData.Domains.Item.Display;
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

        // ——— 反射缓存：原版 ItemDomain 生成 CricketWagerData 各字段的实例方法/字段 ———
        // 原版 SelectCricketWagers 对每个 wager 构造 CricketWagerData 时填了 4 个字段：
        //   Wager / Crickets / MinWagerValue / PreRandomizedShowCricketIndex。
        // 本 patch 追加新项时必须用完全相同的逻辑填满它们，否则前端 CricketCombatBlackBoard.RequestData
        //   访问 reward.Crickets（null）会抛 ArgumentNullException（红字）。

        private static readonly FieldInfo? FiEnemyId =
            AccessTools.Field(typeof(ItemDomain), "_cricketBattleEnemyId");
        private static readonly FieldInfo? FiEnemyCrickets =
            AccessTools.Field(typeof(ItemDomain), "_cricketBattleEnemyCrickets");
        private static readonly MethodInfo? MiGetNpcCricketDisplayData =
            AccessTools.Method(typeof(ItemDomain), "GetNpcCricketDisplayDataListForCricketBattle");
        private static readonly MethodInfo? MiCalcMinWagerValue =
            AccessTools.Method(typeof(ItemDomain), "CalcMinWagerValue");

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemDomain), "SelectCricketWagers")]
        public static void SelectCricketWagersPostfix(
            ItemDomain __instance, DataContext context, ref List<CricketWagerData> __result)
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

            // 原版没生成物品类下注（NPC 无合适物品），追加一项。
            // ★必须用原版同款逻辑填满 Crickets/MinWagerValue/PreRandomizedShowCricketIndex，
            //   否则 Crickets 为 null 会导致前端促织战斗界面红字（CricketCombatBlackBoard.RequestData）。
            if (!replaced)
            {
                var data = BuildCompleteWagerData(__instance, context, PendingItemKey);
                if (data != null)
                {
                    __result.Add(data);
                    AdaptableLog.Info($"[{MonthInteraction.LogTag}] 后端促织：原版无物品下注，已追加 {PendingItemKey}（Crickets={data.Crickets?.Count ?? -1}）");
                }
                else
                {
                    // 反射失败兜底：不追加，放弃注入（避免红字）
                    AdaptableLog.Info($"[{MonthInteraction.LogTag}] 后端促织：追加失败（反射取不到生成方法），跳过注入 {PendingItemKey}");
                }
            }

            // 用完清空，避免污染后续促织决斗
            PendingItemKey = ItemKey.Invalid;
        }

        /// <summary>用原版 SelectCricketWagers 构造 CricketWagerData 的完全相同逻辑，
        /// 生成一个字段完整的赌注项（Wager + Crickets + MinWagerValue + PreRandomizedShowCricketIndex）。
        /// 反射失败返回 null（调用方应跳过追加）。</summary>
        private static CricketWagerData? BuildCompleteWagerData(
            ItemDomain instance, DataContext context, ItemKey itemKey)
        {
            if (FiEnemyId == null || FiEnemyCrickets == null
                || MiGetNpcCricketDisplayData == null || MiCalcMinWagerValue == null)
                return null;

            int enemyId = (int)(FiEnemyId.GetValue(instance) ?? -1);
            var enemyCrickets = (List<ItemKey>?)FiEnemyCrickets.GetValue(instance) ?? new List<ItemKey>();

            Wager wager = Wager.CreateItem(itemKey, 1);

            // 和原版 SelectCricketWagers 内部完全一致的三个调用
            var crickets = (List<ItemDisplayData>)MiGetNpcCricketDisplayData.Invoke(
                instance, new object[] { context, enemyId, enemyCrickets, wager.Grade })!;
            long minWagerValue = (long)(MiCalcMinWagerValue.Invoke(instance, new object[] { wager }) ?? 0L);
            byte preShowIndex = (byte)context.Random.Next(crickets.Count);

            return new CricketWagerData
            {
                Wager = wager,
                Crickets = crickets,
                MinWagerValue = minWagerValue,
                PreRandomizedShowCricketIndex = preShowIndex
            };
        }
    }
}
