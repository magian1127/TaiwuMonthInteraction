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

        /// <summary>单事件每月最大触发次数（= 事件池初始化轮数）。默认 2。
        /// InteractionCounter 的事件池读这个值决定开几轮。</summary>
        internal static int MaxPerEventPerMonth = 2;

        /// <summary>调试模式：开启后输出运行时明细日志（轮转/概率/计数等）。默认 false。
        /// 关键日志（初始化、设置更新、计数重置）不受此开关控制，始终输出。</summary>
        internal static bool DebugMode = false;

        /// <summary>供后端插件反射调用：推送从 Config.lua 读入的设置值。
        /// triggerPercent → <see cref="TriggerChancePercent"/>；
        /// maxPerEvent → <see cref="MaxPerEventPerMonth"/>（事件池轮数）；
        /// debugMode → <see cref="DebugMode"/>。</summary>
        internal static void Apply(int triggerPercent, int maxPerEvent, bool debugMode)
        {
            TriggerChancePercent = triggerPercent;
            MaxPerEventPerMonth = maxPerEvent;
            DebugMode = debugMode;
            // 设置更新属关键里程碑日志，始终输出（不受 DebugMode 控制）
            AdaptableLog.Info(
                $"[MonthInteraction] 设置已更新：触发成功率={triggerPercent}%，单事件每月上限={maxPerEvent}，调试模式={(debugMode ? "开" : "关")}");
        }

        /// <summary>调试日志：仅当 DebugMode 开启时输出。供事件包运行时明细使用
        /// （轮转/概率检查/轮空/去重/计数消耗等高频日志）。</summary>
        internal static void LogDebug(string msg)
        {
            if (DebugMode)
                AdaptableLog.Info($"[MonthInteraction] {msg}");
        }
    }
}
