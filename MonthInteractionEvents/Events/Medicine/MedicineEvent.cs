using System;
using GameData.Domains;
using GameData.Domains.Character;
using GameData.Domains.Map;
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.EventHelper;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 看诊施药事件。条件：太吾装备了大夫技能 DoctorSkill0。
    ///
    /// ★ 接入原版事件链（同 GhostwritingEvent 模式）：
    ///   玩家点"为他诊治" → TriggerEvent("b6084cf3-...") 进入原版选药事件，原版自动接管：
    ///     - 选药界面（FilterItemForCharacterByType 筛选太吾库存药品）
    ///     - 确认后 ConfirmSkillExecute 执行 DoctorSkill0（播动画 + 用药判定）
    ///     - 根据药品效果分发到成功/失败结果弹窗（治疗外伤/内伤/解毒/调理内息 等）
    ///
    /// 与代笔改名的差异：原版 b6084cf3 一个事件同时是选药页+提交派发页
    /// （OnEventEnter 按 ConchShip_PresetKey_FinishSkillExecute 分两路），无需中间事件。
    /// 确认按钮直接调 ConfirmSkillExecute("b6084cf3-...", ArgBox) 不经过 3876fafa 确认框，
    /// 所以 SkipConfirm 跳过确认框天然实现。
    ///
    /// 免消耗（配合后端 ProfessionSkillPatch）：
    ///   DoctorSkill0(=52) 执行时同样包裹 NoProfessionSkillCost 全局开关，免扣时间/历练/资源。
    ///
    /// NPC 筛选：仅选有伤/病/毒/内息紊乱的 NPC（看诊施药的目标），
    /// 否则原版 b6084cf3 的 OnEventEnter 无法匹配用药分支，玩家体验异常。
    /// </summary>
    public class MedicineEvent : MonthInteractionEventBase
    {
        /// <summary>原版看诊施药·选药事件 GUID（Profession3 包）。</summary>
        private const string SelectMedicineEventGuid = "b6084cf3-7fd3-4ed9-8ddc-144f17c00dc5";

        public MedicineEvent()
        {
            Guid = new Guid("a1b2c3d4-3333-4aaa-9001-000000000003");
            // 右侧显示目标 NPC 头像
            TargetRoleKey = KeyTargetCharId;
        }

        protected internal override string EventTag => "Medicine";

        protected override bool CanTrigger(int taiwuCharId)
        {
            return IsSkillEquipped("DoctorSkill0");
        }

        /// <summary>筛选有伤/病/毒/内息紊乱的 NPC（看诊施药的目标）。
        /// 覆盖原版 b6084cf3.OnEventEnter 里所有用药分支适用的状态。</summary>
        protected override int SelectTargetNpc(EventScriptRuntime scriptRuntime, in Location location)
        {
            if (GetRandomCharMethod == null) return -1;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int candidateId = (int)GetRandomCharMethod.Invoke(
                    DomainManager.Character,
                    new object[] { scriptRuntime.Context, location, NpcSearchRange, false })!;
                if (candidateId < 0) return -1;
                if (!DomainManager.Character.TryGetElement_Objects(candidateId, out Character npc))
                    continue;
                // 外伤 / 内伤 / 中毒 / 内息紊乱 任一为 true 即为目标
                if (EventHelper.CheckRoleWounded(npc)
                    || EventHelper.CheckRoleInnerInjured(npc)
                    || EventHelper.CheckCharacterIsPoisoning(npc)
                    || npc.GetDisorderOfQi() > 0)
                    return candidateId;
            }
            return -1;
        }

        protected override void ExecuteInteraction(int targetId, EventArgBox argBox)
        {
            ModSettings.LogDebug($"Medicine 接受：目标 NPC {targetId}，接入原版选药事件 {SelectMedicineEventGuid}");

            // 触发原版选药事件（b6084cf3），原版自动接管选药 + ConfirmSkillExecute + 结果分发。
            // argBox 注入：
            //   - CharacterId：目标 NPC charId（选药筛选 + 右侧显示）
            //   - ProfessionSkillTemplateId：DoctorSkill0（技能执行用）
            //   - SkipConfirm：true ★跳过原版 ConfirmSkillExecute 弹出的 UI_ProfessionSkillConfirm
            //     "消耗时间确认"弹窗。b6084cf3 的 OnOption1Select 直接调 ConfirmSkillExecute，
            //     不经过 3876fafa 确认框，但仍会触发 UI_ProfessionSkillConfirm——必须设此 flag。
            var inputArgBox = new EventArgBox();
            inputArgBox.Set("CharacterId", targetId);
            inputArgBox.Set("ProfessionSkillTemplateId", GetDoctorSkillId());
            inputArgBox.Set("SkipConfirm", true);
            EventHelper.TriggerEvent(SelectMedicineEventGuid, inputArgBox);
        }

        /// <summary>获取大夫 DoctorSkill0 的 TemplateId（反射 Config.ProfessionSkill.DefKey，带缓存）。</summary>
        private static int? _doctorSkillId;
        internal static int GetDoctorSkillId()
        {
            if (!_doctorSkillId.HasValue)
            {
                _doctorSkillId = Convert.ToInt32(
                    typeof(Config.ProfessionSkill.DefKey).GetField("DoctorSkill0")?.GetValue(null));
            }
            return _doctorSkillId.Value;
        }
    }
}