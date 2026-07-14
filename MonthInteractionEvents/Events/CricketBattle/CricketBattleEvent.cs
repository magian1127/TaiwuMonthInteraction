using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameData.Common;
using GameData.Domains;
using GameData.Domains.Character;
using GameData.Domains.Item;
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.EventHelper;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 促织决斗事件。无前置条件（促织数量由原版下注界面内部处理）。
    ///
    /// ★ 本事件特色（相对原版人物互动菜单的促织决斗）：
    ///   - 右侧显示目标 NPC 头像
    ///   - 对话文案用 &lt;Item key=MI_SelectedItemKey str=ColorName/&gt; 标签高亮 NPC 赌注物品
    ///     （鼠标悬停可看物品详情；物品由 NPC 库存品阶最高三档随机一个）
    ///   - 接受后，下注列表里出现该物品（复用原版 SelectCricketWagers 生成，替换其中物品项）
    ///   - 发 ModDisplayEvent 通知 IncreaseDifficulty 屏蔽物品遮蔽（结算后由 CricketBettingResultPatch 恢复）
    /// </summary>
    public class CricketBattleEvent : MonthInteractionEventBase
    {
        // ArgBox 键（KeyTargetCharId 复用基类的同名常量）
        private const string KeySelectedItemKey = "MI_SelectedItemKey";

        /// <summary>MonthInteraction 后端 modId（与 MonthInteractionBackend 的 PluginConfig 一致），
        /// 发 ModDisplayEvent 通知 IncreaseDifficulty 屏蔽用。</summary>
        private const string SelfModId = "MonthInteraction.Backend";

        /// <summary>通知 IncreaseDifficulty 屏蔽促织物品遮蔽。</summary>
        private const string NotifyMaskOff = "CricketMaskOff";
        /// <summary>通知 IncreaseDifficulty 恢复促织物品遮蔽。</summary>
        private const string NotifyMaskOn = "CricketMaskOn";

        // 反射缓存：EventHelper.StartCricketCombat（原版 3 参版本）
        private static readonly MethodInfo? StartCricketCombatMethod =
            typeof(EventHelper).GetMethod("StartCricketCombat",
                BindingFlags.Static | BindingFlags.Public, null,
                new[] { typeof(int), typeof(string), typeof(EventArgBox) }, null);

        // 反射：后端 CricketWagerPatch.PendingItemKey（跨 DLL 传物品给后端 patch）。
        // 延迟初始化：后端插件 DLL 动态加载，Type.GetType 按字符串查不到，需遍历 AppDomain 程序集。
        private static FieldInfo? _pendingItemKeyField;
        private static bool _pendingItemKeyFieldResolved;
        private static FieldInfo? GetPendingItemKeyField()
        {
            if (_pendingItemKeyFieldResolved) return _pendingItemKeyField;
            _pendingItemKeyFieldResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "MonthInteractionBackend") continue;
                    var type = asm.GetType("MonthInteractionBackend.CricketWagerPatch");
                    _pendingItemKeyField = type?.GetField("PendingItemKey", BindingFlags.Public | BindingFlags.Static);
                    break;
                }
            }
            catch { }
            return _pendingItemKeyField;
        }

        public CricketBattleEvent()
        {
            Guid = new Guid("a1b2c3d4-1111-4aaa-9001-000000000001");
            // ★ 右侧显示目标 NPC 头像：TargetRoleKey 指向存 NPC charId 的 ArgBox 键
            TargetRoleKey = KeyTargetCharId;
        }

        protected internal override string EventTag => "CricketBattle";

        protected override bool CanTrigger(int taiwuCharId)
        {
            // 无前置条件：促织数量等由原版下注界面内部处理（不足时前端提示）
            return true;
        }

        /// <summary>从 NPC 库存挑一件物品：筛选标准与原版 <c>CalcEnemyWagers</c> 完全一致。
        /// 这样选出的物品一定在原版下注列表里，后端 patch 走「替换」分支（Crickets 完整）而非「追加」分支。
        ///
        /// 三道关（和原版 CalcEnemyWagers 相同）：
        ///   1. WagerItemMatchers 白名单（物品类型，排除促织等）
        ///   2. CalcWagerGradeRange 品级区间 [min, max]（由 NPC 门派品阶 + 太吾威望决定）
        ///   3. GetValue() >= 1（排除无价值物品）
        /// 合格物品中取最高品级那一档随机一个（和原版 D 步骤一致）。</summary>
        private ItemKey PickItemFromNpc(int npcCharId)
        {
            try
            {
                if (!DomainManager.Character.TryGetElement_Objects(npcCharId, out Character npc))
                    return ItemKey.Invalid;

                var inventory = npc.GetInventory().Items;
                if (inventory == null || inventory.Count == 0) return ItemKey.Invalid;

                // 品级区间（和原版 CalcEnemyWagers 完全一致）
                sbyte charGrade = npc.GetOrganizationInfo().Grade;
                sbyte taiwuFame = DomainManager.Taiwu.GetTaiwu().GetFame();
                var (minGrade, maxGrade) = CricketSpecialConstants.CalcWagerGradeRange(charGrade, taiwuFame);

                // 收集合格物品（三道关全过）
                var candidates = new List<(ItemKey key, sbyte grade)>();
                foreach (var kvp in inventory)
                {
                    ItemKey key = kvp.Key;

                    // 关1：物品类型白名单
                    bool typeMatched = false;
                    foreach (var matcher in CricketSpecialConstants.WagerItemMatchers)
                    {
                        if (matcher(key)) { typeMatched = true; break; }
                    }
                    if (!typeMatched) continue;

                    // 关2：品级区间
                    sbyte grade = ItemTemplateHelper.GetGrade(key.ItemType, key.TemplateId);
                    if (grade < minGrade || grade > maxGrade) continue;

                    // 关3：价值门槛
                    var baseItem = DomainManager.Item.GetBaseItem(key);
                    if (baseItem == null || baseItem.GetValue() < 1) continue;

                    candidates.Add((key, grade));
                }
                if (candidates.Count == 0) return ItemKey.Invalid;

                // 取最高品级那一档，随机一个（和原版 D 步骤一致）
                sbyte highestGrade = candidates.Max(c => c.grade);
                var pool = candidates.Where(c => c.grade == highestGrade).ToList();

                var picked = pool[Rng.Next(pool.Count)].key;
                ModSettings.LogDebug($"CricketBattle 从 NPC {npcCharId} 库存选物品：{picked}（品级区间 [{minGrade},{maxGrade}]，最高品级 {highestGrade}，候选 {pool.Count} 件）");
                return picked;
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] PickItemFromNpc 异常: {ex.Message}");
                return ItemKey.Invalid;
            }
        }

        /// <summary>目标选定后：挑物品并存 ArgBox（文本物品标签 + 注入下注用）。</summary>
        protected override void OnTargetSelected(int targetId)
        {
            ItemKey picked = PickItemFromNpc(targetId);
            // ItemKey 是 ISerializableGameData，用 GenericSet 存储。
            // 语言文件文本用 <Item key=MI_SelectedItemKey str=ColorName/> 引用它（可高亮、鼠标悬停看详情）。
            ArgBox.GenericSet(KeySelectedItemKey, picked);
            ModSettings.LogDebug($"CricketBattle 选定 NPC {targetId}，赌注物品：{picked}");
        }

        /// <summary>文案由语言文件提供（含物品高亮标签）。
        /// 仅当没选到物品（NPC 无库存）时，返回兜底纯文本，避免标签解析失败。</summary>
        public override string GetReplacedContentString()
        {
            ItemKey picked = ArgBox.Get<ItemKey>(KeySelectedItemKey);
            if (!picked.IsValid())
                return "「阁下的促织甚是勇猛，不如来一场促织决斗消遣一番？」";
            return "";  // 用语言文件文本（含 <Item> 高亮标签）
        }

        protected override void ExecuteInteraction(int targetId, EventArgBox argBox)
        {
            // 发 ModDisplayEvent 通知 IncreaseDifficulty 屏蔽物品遮蔽
            try
            {
                DomainManager.Mod.AddModDisplayEvent(SelfModId, NotifyMaskOff);
                ModSettings.LogDebug($"CricketBattle 已发通知 {NotifyMaskOff}");
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] CricketBattle 发屏蔽通知异常: {ex.Message}");
            }

            // 把选中的物品 ItemKey 通过 static 标志位传给后端 patch（CricketWagerPatch.SelectCricketWagersPostfix）
            // 后端 patch 在 SelectCricketWagers 返回后替换其中物品项为指定物品。
            ItemKey pickedKey = ArgBox.Get<ItemKey>(KeySelectedItemKey);
            var pendingField = GetPendingItemKeyField();
            if (pendingField != null && pickedKey.IsValid())
            {
                pendingField.SetValue(null, pickedKey);
                ModSettings.LogDebug($"CricketBattle 已设置后端注入物品 {pickedKey}");
            }

            // 触发促织决斗（不带 specialWagerData，走原版 StartCricketCombat，由后端 Postfix 接管物品替换）
            if (StartCricketCombatMethod == null)
            {
                AdaptableLog.Info("[MonthInteraction] CricketBattle 反射失败：StartCricketCombat 未找到");
                return;
            }
            try
            {
                StartCricketCombatMethod.Invoke(null, new object?[] { targetId, "", argBox });
                ModSettings.LogDebug($"CricketBattle 已触发促织决斗，NPC {targetId}");
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] CricketBattle 触发异常: {ex.Message}");
            }
        }
    }
}
