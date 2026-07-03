using System;
using System.Collections.Generic;
using GameData.Domains;
using GameData.Utilities;
using HarmonyLib;
using TaiwuModdingLib.Core.Plugin;

namespace MonthInteractionBackend
{
    /// <summary>
    /// 月中互动 —— 后端插件主类（事件包机制，纯后端 MOD）。
    ///
    /// 架构说明：
    ///   本 MOD 通过 EventPackage + TaiwuEventItem 注册到游戏事件系统，
    ///   触发类型为 TaiwuBlockChanged（太吾移动到新地块时触发）。
    ///   所有逻辑（检测、计数、发起互动）都在后端的 EventItem 里完成。
    ///   无需前端 DLL，无需前后端通信。
    ///
    ///   EventPackage 的注册由游戏扫描 EventPackages（见 Config.lua）自动完成，
    ///   Plugin.Initialize 只负责打日志确认加载 + 挂 Harmony patch。
    /// </summary>
    [PluginConfig("MonthInteraction.Backend", "Magian", "0.1.0.0")]
    public class MonthInteraction : TaiwuRemakePlugin
    {
        public const string LogTag = "MonthInteraction";

        private Harmony? _harmony;

        /// <summary>设置项缓存（键 → int 值）。
        /// 为什么需要缓存（与 AdjustMod 一致）：游戏引擎在存档选单关闭时会清空 ModManager 的
        /// _localMods，之后 DomainManager.Mod.GetSetting 全部返回 defaultValue。所以 Initialize/
        /// OnModSettingUpdate 时（_localMods 可用）读入缓存，运行时只读缓存。</summary>
        private static readonly Dictionary<string, int> _settingsCache = new();

        public override void Initialize()
        {
            AdaptableLog.Info($"[{LogTag}] ★后端 Initialize 开始★ ModIdStr={ModIdStr}");

            try
            {
                _harmony = new Harmony(ModIdStr);
                _harmony.PatchAll(typeof(ProfessionSkillPatch));
                _harmony.PatchAll(typeof(CricketBettingResultPatch));
                _harmony.PatchAll(typeof(CricketWagerPatch));
                AdaptableLog.Info($"[{LogTag}] Harmony patch 已挂载");
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[{LogTag}] Harmony PatchAll 异常: {ex.Message}");
            }

            AdaptableLog.Info($"[{LogTag}] ★后端 Initialize 完成★");

            try { RefreshSettingsCache(); }
            catch (Exception ex) { AdaptableLog.Info($"[{LogTag}] 初始化时 RefreshSettingsCache 异常: {ex.Message}"); }
        }

        public override void Dispose()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>玩家在游戏设置面板改了本 MOD 的设置后触发，重新读取并推送给事件包。</summary>
        public override void OnModSettingUpdate()
        {
            RefreshSettingsCache();
        }

        /// <summary>读档时重置触发计数：InteractionCounter 是 static 状态，不随存档重置，
        /// 读档后会残留"本月已触发N次"导致再也触发不了。读档时清零恢复正常。</summary>
        public override void OnLoadedArchiveData()
        {
            ResetInteractionCounter();
        }

        /// <summary>进入新存档时也重置（保险）。</summary>
        public override void OnEnterNewWorld()
        {
            ResetInteractionCounter();
        }

        private static void ResetInteractionCounter()
        {
            try
            {
                // InteractionCounter 在事件包 DLL（MonthInteractionEvents），事件包用 Assembly.Load 动态加载，
                // TypeGetType 按字符串找不到（程序集名可能带版本/Token 后缀）。遍历已加载程序集查找。
                var counterType = FindEventsType("MonthInteractionEvents.InteractionCounter");
                if (counterType == null)
                {
                    AdaptableLog.Info($"[{LogTag}] ResetInteractionCounter：找不到 InteractionCounter 类型");
                    return;
                }
                var method = counterType.GetMethod("Reset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (method == null)
                {
                    AdaptableLog.Info($"[{LogTag}] ResetInteractionCounter：找不到 Reset 方法");
                    return;
                }
                method.Invoke(null, null);
                AdaptableLog.Info($"[{LogTag}] ResetInteractionCounter 完成");
            }
            catch (System.Exception ex)
            {
                AdaptableLog.Info($"[{LogTag}] ResetInteractionCounter 异常: {ex.Message}");
            }
        }

        /// <summary>刷新设置项缓存，并反射推送给事件包的 ModSettings。
        ///
        /// 调用时机：Initialize()（此时 _localMods 可用）+ OnModSettingUpdate()（改设置后）。
        /// 为什么缓存（与 AdjustMod 一致）：存档选单关闭会清空 _localMods，之后 GetSetting 返回默认值；
        /// 在 _localMods 可用时读入缓存，运行时事件包读的是缓存推送过来的值。
        ///
        /// 事件包 DLL 拿不到 ModIdStr，无法自己读设置，所以由后端读入缓存后反射推送。
        /// 复用与 ResetInteractionCounter 相同的程序集遍历模式（事件包是 Assembly.Load 动态加载的）。</summary>
        private void RefreshSettingsCache()
        {
            _settingsCache.Clear();
            CacheSetting("TriggerChancePercent", 10);
            CacheSetting("MaxPerEventPerMonth", 2);

            // 从缓存取值，反射调 ModSettings.Apply(triggerPercent, maxPerEvent) 推送给事件包
            var modSettingsType = FindEventsType("MonthInteractionEvents.ModSettings");
            if (modSettingsType == null)
            {
                AdaptableLog.Info($"[{LogTag}] RefreshSettingsCache：找不到 ModSettings 类型（事件包尚未加载？用默认值）");
                return;
            }
            var applyMethod = modSettingsType.GetMethod("Apply",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (applyMethod == null)
            {
                AdaptableLog.Info($"[{LogTag}] RefreshSettingsCache：找不到 Apply 方法");
                return;
            }
            int triggerPercent = GetSettingInt("TriggerChancePercent", 10);
            int maxPerEvent = GetSettingInt("MaxPerEventPerMonth", 2);
            applyMethod.Invoke(null, new object[] { triggerPercent, maxPerEvent });
        }

        /// <summary>读取并缓存单个 int 设置项。从 DomainManager.Mod.GetSetting 读取，失败时用 defaultValue。</summary>
        private void CacheSetting(string key, int defaultValue)
        {
            try
            {
                int val = defaultValue;
                _settingsCache[key] = DomainManager.Mod.GetSetting(ModIdStr, key, ref val) ? val : defaultValue;
            }
            catch
            {
                _settingsCache[key] = defaultValue;
            }
        }

        /// <summary>从缓存读取 int 设置项。缓存由 RefreshSettingsCache 在 Initialize/OnModSettingUpdate 填充。</summary>
        internal static int GetSettingInt(string key, int defaultValue)
        {
            return _settingsCache.TryGetValue(key, out int cached) ? cached : defaultValue;
        }

        /// <summary>在已加载程序集中按全名查找事件包类型。事件包是 Assembly.Load 动态加载的，
        /// Type.GetType 按字符串找不到（程序集名可能带版本/Token 后缀），必须遍历 AppDomain。</summary>
        private static System.Type? FindEventsType(string fullTypeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "MonthInteractionEvents")
                    return asm.GetType(fullTypeName);
            }
            return null;
        }
    }
}
