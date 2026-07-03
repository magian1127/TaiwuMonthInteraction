using System;
using System.Collections.Generic;
using GameData.Domains;
using GameData.Domains.Map;
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.EventHelper;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 代笔改名事件（首页：触发 + 接受）。
    ///
    /// 需求：太吾移动到有未成年 NPC 的地块 + 装备才俊技能 LiteratiSkill0。
    /// ★ 右侧显示孩子的父母之一（随机选成年的血父或血母）—— 父母恳请太吾给孩子赐名。
    /// 玩家点"接受" → 触发 <see cref="GhostwritingNameInputEvent"/>，右侧切换为孩子，弹输入框改名。
    /// </summary>
    public class GhostwritingEvent : MonthInteractionEventBase
    {
        // ArgBox 键
        private const string KeyDialogCharId = "MI_DialogCharId";   // 对话者（父母之一）

        /// <summary>ArgBox 键：取消回退标记。NameInputEvent 的 Cancel 选项设此键，
        /// GhostwritingEvent 的 CheckConditionInner 见此标记直接放行，绕过幂等券。</summary>
        private const string KeyFromNameInput = "MI_FromNameInput";

        /// <summary>MOD 输入名字事件的 GUID（ExecuteInteraction 触发它）。</summary>
        private const string NameInputEventGuid = "a1b2c3d4-2224-4aaa-9001-000000000012";

        // 启用取消回退支持：NameInputEvent Cancel 回退时 ArgBox 带此标记
        protected override string CallbackReturnMark => KeyFromNameInput;

        public GhostwritingEvent()
        {
            Guid = new Guid("a1b2c3d4-2222-4aaa-9001-000000000002");
            // ★ 首页右侧显示父母：游戏会 ArgBox.GetCharacter("MI_DialogCharId") 读 charId
            TargetRoleKey = KeyDialogCharId;
        }

        protected override string EventTag => "Ghostwriting";

        protected override bool CanTrigger(int taiwuCharId)
        {
            return IsSkillEquipped("LiteratiSkill0");
        }

        /// <summary>只选地块内的未成年 NPC（<16 岁），且至少有一个成年的血父或血母。</summary>
        protected override int SelectTargetNpc(EventScriptRuntime scriptRuntime, in Location location)
        {
            if (GetRandomCharMethod == null) return -1;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int candidateId = (int)GetRandomCharMethod.Invoke(
                    DomainManager.Character,
                    new object[] { scriptRuntime.Context, location, NpcSearchRange, false })!;
                if (candidateId < 0) return -1;
                if (!EventHelper.IsCharacterAdult(candidateId)
                    && PickAdultBloodParent(candidateId) >= 0)
                    return candidateId;
            }
            return -1;
        }

        /// <summary>目标选定后：查父母塞 MI_DialogCharId，让首页右侧显示父母。</summary>
        protected override void OnTargetSelected(int targetId)
        {
            int dialogCharId = PickAdultBloodParent(targetId);
            ArgBox.Set(KeyDialogCharId, dialogCharId >= 0 ? dialogCharId : targetId);
            AdaptableLog.Info($"[MonthInteraction] Ghostwriting 选定孩子 {targetId}，对话者(父母) {dialogCharId}");
        }

        protected override void ExecuteInteraction(int targetId, EventArgBox argBox)
        {
            // 从 ArgBox 读对话者（父母），ExecuteInteraction 时 ArgBox 已由 CheckConditionInner 填好
            int dialogCharId = targetId;
            ArgBox.Get(KeyDialogCharId, ref dialogCharId);

            AdaptableLog.Info($"[MonthInteraction] Ghostwriting 接受：孩子 {targetId}，对话者(父母) {dialogCharId}");

            // ★ 触发 MOD 自建的输入名字事件，让玩家输入新名字给孩子改名。
            //   argBox 注入：孩子 charId（改名对象 + 输入页右侧显示）、父母 charId（取消回退用）、技能ID
            int skillId = GetLiteratiSkillId();
            var inputArgBox = new EventArgBox();
            inputArgBox.Set("MI_ChildCharId", targetId);        // 改名对象（孩子）
            inputArgBox.Set("CharacterId", targetId);            // ★ 输入页右侧显示孩子
            inputArgBox.Set(KeyDialogCharId, dialogCharId);      // 父母（取消回退首页时显示）
            inputArgBox.Set("ProfessionSkillTemplateId", skillId); // 才俊技能 ID（确认时播动画+判定用）
            EventHelper.TriggerEvent(NameInputEventGuid, inputArgBox);
        }

        /// <summary>从孩子的血父血母中随机选一个成年的，找不到返回 -1。</summary>
        internal static int PickAdultBloodParent(int childCharId)
        {
            try
            {
                var genealogy = DomainManager.Character.GetGenealogy(childCharId);
                var candidates = new List<int>(2);
                if (genealogy.BloodFatherId >= 0 && EventHelper.IsCharacterAdult(genealogy.BloodFatherId))
                    candidates.Add(genealogy.BloodFatherId);
                if (genealogy.BloodMotherId >= 0 && genealogy.BloodMotherId != genealogy.BloodFatherId
                    && EventHelper.IsCharacterAdult(genealogy.BloodMotherId))
                    candidates.Add(genealogy.BloodMotherId);
                if (candidates.Count == 0) return -1;
                return candidates[Rng.Next(candidates.Count)];
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] PickAdultBloodParent({childCharId}) 异常: {ex.Message}");
                return -1;
            }
        }

        /// <summary>获取才俊 LiteratiSkill0 的 TemplateId（反射 Config.ProfessionSkill.DefKey，带缓存）。</summary>
        private static int? _literatiSkillId;
        internal static int GetLiteratiSkillId()
        {
            if (!_literatiSkillId.HasValue)
            {
                _literatiSkillId = Convert.ToInt32(
                    typeof(Config.ProfessionSkill.DefKey).GetField("LiteratiSkill0")?.GetValue(null));
            }
            return _literatiSkillId.Value;
        }
    }
}
