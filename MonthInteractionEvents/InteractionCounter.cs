using System;
using System.Collections.Generic;
using GameData.Domains;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 触发计数与限额（后端静态状态）。
    ///
    /// 规则（与 PRD 一致）：
    ///   - 每 NPC 每事件每月最多触发 1 次（避免被同一 NPC 反复打扰）
    ///   - 每事件类型整月最多触发 2 次（避免某类互动刷屏）
    ///   - 弹框即计数（拒绝也消耗）
    ///   - 月初清零
    ///
    /// 月初清零方式：检测当前月份是否变化（从 DomainManager 读取），变了就清空。
    /// 不依赖额外的事件注册，简单可靠。
    /// </summary>
    internal static class InteractionCounter
    {
        /// <summary>重置所有计数 + 轮转队列（读档/进新世界时调用，避免 static 状态跨存档残留卡死触发）。
        /// 轮转队列重置后，下次 CheckCondition 时 EnsureTurnOrderShuffled 会按当前月份重建并打乱。</summary>
        internal static void Reset()
        {
            _npcEventFlags.Clear();
            _eventCounts.Clear();
            _lastCheckedMonth = -1;
            // ★ 同步重置基类的轮转队列状态（防止跨档残留指针卡在某事件）
            MonthInteractionEventBase.ResetTurnState();
            AdaptableLog.Info("[MonthInteraction] 触发计数与轮转队列已重置");
        }
        internal const int MaxPerNpcPerMonth = 1;
        /// <summary>每事件类型整月触发上限（运行时可调，默认 2，由 ModSettings.Apply 推送）。</summary>
        internal static int MaxPerEventPerMonth = 2;

        /// <summary>每 NPC 每事件本月是否已触发。key = (npcCharId, eventType)。</summary>
        private static readonly Dictionary<(int, string), bool> _npcEventFlags = new();

        /// <summary>每事件类型本月已触发次数。</summary>
        private static readonly Dictionary<string, int> _eventCounts = new();

        /// <summary>上次检查时的月份（用于检测月份变化并清零）。</summary>
        private static int _lastCheckedMonth = -1;

        /// <summary>检查月份变化，跨月时自动清零计数。每次判断计数前先调用。</summary>
        private static void CheckMonthRollover()
        {
            try
            {
                int currMonth = DomainManager.World.GetCurrDate() / 30;
                if (_lastCheckedMonth >= 0 && currMonth != _lastCheckedMonth)
                {
                    _npcEventFlags.Clear();
                    _eventCounts.Clear();
                    AdaptableLog.Info($"[MonthInteraction] 触发计数已清空（月份 {_lastCheckedMonth} → {currMonth}）");
                }
                _lastCheckedMonth = currMonth;
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] 月份检查异常: {ex.Message}");
            }
        }

        /// <summary>尝试消耗一次触发次数。满足条件返回 true 并计数；超限或重复返回 false。
        /// 弹框前调用——拒绝也消耗，避免被同一个 NPC 反复打扰。</summary>
        internal static bool TryConsumeCount(int npcId, string eventType)
        {
            CheckMonthRollover();

            // 每 NPC 每事件每月限 1 次
            var key = (npcId, eventType);
            if (_npcEventFlags.TryGetValue(key, out bool triggered) && triggered)
            {
                AdaptableLog.Info($"[MonthInteraction] 计数拦截：NPC {npcId} 事件 {eventType} 本月已触发过");
                return false;
            }

            // 每事件整月上限
            int currentCount = _eventCounts.GetValueOrDefault(eventType, 0);
            if (currentCount >= MaxPerEventPerMonth)
            {
                AdaptableLog.Info($"[MonthInteraction] 计数拦截：事件 {eventType} 本月已达 {MaxPerEventPerMonth} 次上限");
                return false;
            }

            // 消耗
            _npcEventFlags[key] = true;
            _eventCounts[eventType] = currentCount + 1;
            AdaptableLog.Info($"[MonthInteraction] 计数消耗：NPC {npcId} 事件 {eventType}（本月第 {currentCount + 1} 次）");
            return true;
        }
    }
}
