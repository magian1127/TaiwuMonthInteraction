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
    /// 看诊施药事件（首页：触发 + 接受）。条件：太吾装备了大夫技能 DoctorSkill0。
    ///
    /// ★ 接入模式（同 GhostwritingEvent）：自写触发和首页，玩家点"为他诊治" → 触发 MOD 自建的
    ///   <see cref="MedicineSelectEvent"/>（选药子事件），由它负责选药界面 + 取消。
    ///   选药确认后，子事件调 ConfirmSkillExecute 接入原版 b6084cf3 治病事件链，原版接管：
    ///     - 播 DoctorSkill0 动画 + 读选中药 + 按药品效果治病
    ///     - 分发到成功/失败结果弹窗（治疗外伤/内伤/解毒/调理内息 等）
    ///   治病 + 结果分发全由原版处理，本 MOD 不写治病逻辑。
    ///
    /// ★ 为什么自建选药子事件（而非直接触发原版 b6084cf3）：
    ///   原版 b6084cf3 的取消(Option_2)硬编码跳 3876fafa 消耗确认框，该框对免消耗的 MOD 看诊
    ///   无意义且体验割裂。自建选药事件后取消由 MOD 控制，直接回本首页，不经过 3876fafa。
    ///
    /// 免消耗（配合后端 ProfessionSkillPatch）：
    ///   DoctorSkill0(=52) 执行时包裹 NoProfessionSkillCost 全局开关，免扣时间/历练/资源。
    ///   SkipConfirm=true（子事件确认时设）跳过原版消耗确认弹窗。
    ///
    /// NPC 筛选：仅选有伤/病/毒/内息紊乱的 NPC（看诊施药的目标），
    /// 否则原版 b6084cf3 的 OnEventEnter 无法匹配用药分支，玩家体验异常。
    /// </summary>
    public class MedicineEvent : MonthInteractionEventBase
    {
        /// <summary>MOD 自建选药子事件 GUID（<see cref="MedicineSelectEvent"/>）。
        /// 取消由 MOD 控制（直接回首页），确认后接入原版 b6084cf3 治病事件链。</summary>
        private const string MedicineSelectEventGuid = "a1b2c3d4-3334-4aaa-9001-000000000013";

        /// <summary>本首页 GUID（字符串形式，注入 MainInteractionHeadEvent 用）。</summary>
        private const string SelfGuid = "a1b2c3d4-3333-4aaa-9001-000000000003";

        /// <summary>ArgBox 键：取消回退标记。触发原版选药事件时提前 Set，
        /// 原版取消链跳回本首页时 ArgBox 仍带此标记，CheckConditionInner 短路放行。</summary>
        private const string KeyFromMedicine = "MI_FromMedicine";

        // 启用取消回退：原版选药/确认框取消跳回本首页时 ArgBox 带此标记
        protected override string CallbackReturnMark => KeyFromMedicine;

        public MedicineEvent()
        {
            Guid = new Guid(SelfGuid);
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
            ModSettings.LogDebug($"Medicine 接受：目标 NPC {targetId}，触发选药子事件 {MedicineSelectEventGuid}");

            // 触发 MOD 自建选药子事件（MedicineSelectEvent），由它负责选药界面 + 取消。
            // 选药界面确认后，子事件调 ConfirmSkillExecute 接入原版 b6084cf3 治病事件链（原版接管治病）。
            // argBox 注入：
            //   - CharacterId：目标 NPC charId（选药筛选 + 右侧显示病人）
            //   - ProfessionSkillTemplateId：DoctorSkill0（子事件确认时技能执行用）
            //   - MainInteractionHeadEvent：原版治病结果窗结束后读此键决定跳回目标（?? fb38f657），
            //     注入本首页 GUID 让结果窗结束回 MOD 首页而非原版 NPC 对话。
            //   - MI_FromMedicine：回退标记。子事件取消回首页、或治病结果窗结束回首页时，
            //     ArgBox 仍带此标记，CheckConditionInner 短路放行（绕过幂等券/计数/概率/NPC重选）。
            var inputArgBox = new EventArgBox();
            inputArgBox.Set("CharacterId", targetId);
            inputArgBox.Set("ProfessionSkillTemplateId", GetDoctorSkillId());
            inputArgBox.Set("MainInteractionHeadEvent", SelfGuid);
            inputArgBox.Set(KeyFromMedicine, true);
            EventHelper.TriggerEvent(MedicineSelectEventGuid, inputArgBox);
        }

        /// <summary>取消回退放行时：从子事件带回的 CharacterId 恢复首次触发时选定的目标 NPC。
        /// 选药子事件取消/治病结果窗结束跳回本首页时 ArgBox 缺少 MI_TargetCharId（被回退短路跳过），
        /// 不恢复会导致再次接受时 ExecuteInteraction 拿到默认 0。看诊的 CharacterId 即目标 NPC，直接复用。</summary>
        protected override bool TryRestoreTargetOnCallback(ref int targetId)
        {
            int charId = -1;
            if (ArgBox.Get("CharacterId", ref charId) && charId >= 0)
            {
                targetId = charId;
                return true;
            }
            ModSettings.LogDebug("Medicine 回退放行但 CharacterId 缺失，无法恢复目标");
            return false;
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