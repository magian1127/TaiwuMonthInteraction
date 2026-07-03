using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 运行时可调设置的中转站。
    ///
    /// 【为什么需要这个类】事件包 DLL 由游戏 Assembly.Load 动态加载，拿不到 ModIdStr，
    /// 无法自己调 DomainManager.Mod.GetSetting。设置由后端插件读入后，通过反射调
    /// <see cref="Apply"/> 推送到这里（复用 MonthInteractionBackend 的反射模式）。
    ///
    /// 【默认值】字段初始值是后端未推送设置前的兜底（与 Config.lua 的 DefaultValue 一致），
    /// 正常运行后会被 Apply 覆盖。事件包代码读这里而不是原来的 const。
    /// </summary>
    internal static class ModSettings
    {
        /// <summary>触发成功率（%）。默认 10。</summary>
        internal static int TriggerChancePercent = 10;

        /// <summary>单事件每月最大触发次数。默认 2。
        /// 实际存取在 <see cref="InteractionCounter.MaxPerEventPerMonth"/>，Apply 时同步过去。</summary>
        internal static int MaxPerEventPerMonth = 2;

        /// <summary>供后端插件反射调用：推送从 Config.lua 读入的设置值。
        /// triggerPercent → <see cref="TriggerChancePercent"/>；
        /// maxPerEvent → <see cref="InteractionCounter.MaxPerEventPerMonth"/>。</summary>
        internal static void Apply(int triggerPercent, int maxPerEvent)
        {
            TriggerChancePercent = triggerPercent;
            InteractionCounter.MaxPerEventPerMonth = maxPerEvent;
            AdaptableLog.Info(
                $"[MonthInteraction] 设置已更新：触发成功率={triggerPercent}%，单事件每月上限={maxPerEvent}");
        }
    }
}
