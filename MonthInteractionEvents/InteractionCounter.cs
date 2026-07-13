using System;
using System.Collections.Generic;
using System.Linq;
using GameData.Domains;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 触发计数 + 轮换队列（后端静态状态）。
    ///
    /// 设计（用户定义的"挪队尾"方案）：
    ///   - _queue：有序事件 tag 队列。事件实例被调时，若自己还在队列里（有剩余次数），
    ///     就把自己挪到队尾（让出优先权），然后正常走检查流程。
    ///   - 这样每次移动，排最前的事件优先被检查，检查完自动让位 → 自然形成 A→B→C→A 轮换。
    ///   - 触发一次 → 该事件剩余次数 -1；归零 → 从队列移除（不再参与）。
    ///   - 某事件卡着不触发 → 剩余次数不变，挪到队尾后等下次轮到，不阻塞别的事件。
    ///   - 队列空 → 本月全部达上限，CheckConditionInner 连概率都不掷（O(1) 短路）。
    ///   - 每 NPC 每事件每月限 1 次。
    ///   - 月初清零：重建队列 + 清 NPC 标记。
    ///
    /// 为什么不随机起始索引：挪队尾是确定性轮换，公平无饥饿，且天然适配独立调用架构
    /// （每个事件实例自己挪，不需要总入口协调）。
    /// </summary>
    internal static class InteractionCounter
    {
        /// <summary>每 NPC 每事件每月限触发 1 次。</summary>
        internal const int MaxPerNpcPerMonth = 1;

        /// <summary>事件 tag 有序队列（本月剩余次数 > 0 的事件）。
        /// 排第一的优先被检查；被调时挪到队尾让位。</summary>
        private static List<string>? _queue;

        /// <summary>各事件本月剩余次数（归零则从 _queue 移除）。</summary>
        private static Dictionary<string, int>? _remaining;

        /// <summary>上次检查时的月份（检测跨月重置）。</summary>
        private static int _lastCheckedMonth = -1;

        /// <summary>每 NPC 每事件本月是否已触发。key = (npcCharId, eventType)。</summary>
        private static readonly Dictionary<(int, string), bool> _npcEventFlags = new();

        /// <summary>队列初始化打乱用（月初随机起始顺序）。</summary>
        private static readonly Random _queueRng = new();

        /// <summary>重置队列 + NPC 标记（读档/进新世界时调用，避免 static 跨档残留）。</summary>
        internal static void Reset()
        {
            _queue = null;
            _remaining = null;
            _lastCheckedMonth = -1;
            _npcEventFlags.Clear();
            AdaptableLog.Info("[MonthInteraction] 轮换队列与 NPC 标记已重置");
        }

        /// <summary>检测月份变化，跨月时清空队列 + NPC 标记。</summary>
        private static void CheckMonthRollover()
        {
            try
            {
                int currMonth = DomainManager.World.GetCurrDate() / 30;
                if (_lastCheckedMonth >= 0 && currMonth != _lastCheckedMonth)
                {
                    _queue = null;
                    _remaining = null;
                    _npcEventFlags.Clear();
                    AdaptableLog.Info($"[MonthInteraction] 跨月清零（{_lastCheckedMonth} → {currMonth}）");
                }
                _lastCheckedMonth = currMonth;
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] 月份检查异常: {ex.Message}");
            }
        }

        /// <summary>确保队列已为本月初始化：Fisher-Yates 打乱顺序，每个事件剩余次数 = 月上限。</summary>
        private static void EnsureQueueInitialized()
        {
            if (_queue != null) return;
            int max = ModSettings.MaxPerEventPerMonth;
            _queue = new List<string>(MonthInteractionEventBase.AllEventTags);
            _remaining = new Dictionary<string, int>();
            foreach (var tag in _queue)
                _remaining[tag] = max;
            // 月初打乱起始顺序
            for (int i = _queue.Count - 1; i > 0; i--)
            {
                int j = _queueRng.Next(i + 1);
                (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
            }
            ModSettings.LogDebug($"轮换队列本月初始化（打乱）：[{string.Join(", ", _queue)}]");
        }

        /// <summary>本月是否还有任何事件能触发（队列非空）。供 CheckConditionInner 概率门槛前 O(1) 短路。</summary>
        internal static bool HasAnyChanceThisMonth()
        {
            CheckMonthRollover();
            EnsureQueueInitialized();
            return _queue != null && _queue.Count > 0;
        }

        /// <summary>事件实例被调时：若自己在队列里，挪到队尾让位（让下一次排前面的事件优先）。
        /// 然后返回 true（仍可参与本轮检查）。不在队列（次数用完）→ 返回 false。
        /// 供 CheckConditionInner 在概率通过后调用。</summary>
        internal static bool TryRotateToBack(string eventType)
        {
            CheckMonthRollover();
            EnsureQueueInitialized();
            if (_queue == null || !_queue.Contains(eventType))
                return false;  // 不在队列（本月次数用完）
            // 挪到队尾（除非已经是最后一个）
            if (_queue[_queue.Count - 1] != eventType)
            {
                _queue.Remove(eventType);
                _queue.Add(eventType);
            }
            return true;
        }

        /// <summary>尝试消耗一次触发：剩余次数 > 0 + NPC 本月未触发过 → 扣减 + 记 NPC + 返回 true。
        /// 剩余归零自动从队列移除。弹框前调用——拒绝也消耗，避免被同一 NPC 反复打扰。</summary>
        internal static bool TryConsumeCount(int npcId, string eventType)
        {
            CheckMonthRollover();
            EnsureQueueInitialized();
            if (_remaining == null || !_remaining.TryGetValue(eventType, out int left) || left <= 0)
            {
                ModSettings.LogDebug($"计数拦截：事件 {eventType} 本月已达上限");
                return false;
            }

            var key = (npcId, eventType);
            if (_npcEventFlags.TryGetValue(key, out bool triggered) && triggered)
            {
                ModSettings.LogDebug($"计数拦截：NPC {npcId} 事件 {eventType} 本月已触发过");
                return false;
            }

            // 消耗：扣减剩余次数 + 记 NPC；归零则从队列移除
            int newLeft = left - 1;
            if (newLeft <= 0)
            {
                _remaining.Remove(eventType);
                _queue?.Remove(eventType);
            }
            else
            {
                _remaining[eventType] = newLeft;
            }
            _npcEventFlags[key] = true;
            ModSettings.LogDebug($"计数消耗：NPC {npcId} 事件 {eventType}（本月剩余 {newLeft}）");
            return true;
        }
    }
}
