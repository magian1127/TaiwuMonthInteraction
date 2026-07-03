using GameData.Domains;
using GameData.Domains.Taiwu.Profession;
using GameData.Utilities;
using HarmonyLib;

namespace MonthInteractionBackend
{
    /// <summary>
    /// 月中互动免消耗补丁 —— 让代笔改名（LiteratiSkill0）和看诊施药（DoctorSkill0）执行时不扣时间/历练/资源。
    ///
    /// 原版 <see cref="ProfessionSkillHandle.OnSkillExecuted"/> 在技能执行后扣消耗：
    ///   时间（AdvanceDaysInMonth）、资源（ChangeResource）、历练（ChangeExp）。
    /// 这些扣除被 <c>DomainManager.Extra.NoProfessionSkillCost</c> 全局开关门控——
    /// 开关为 true 时整块跳过。
    ///
    /// 本 patch 用 Prefix/Postfix 精准包裹这个开关：识别到 LiteratiSkill0 或 DoctorSkill0 时
    /// 前置置 true，原方法执行（消耗被跳过），Postfix 恢复原值。
    /// 不影响其他才俊/大夫技能的消耗。
    ///
    /// ★ 与 GhostwritingNameInputEvent / MedicineEvent 配合：
    ///   前者在 ArgBox 设 SkipConfirm=true 跳过确认框（代笔）；后者走原版选药事件天然不经过确认框（看诊）。
    ///   本 patch 负责两侧的免消耗。共同实现"确认后直接进入原版流程，无消耗、无确认弹窗"。
    ///
    /// ⚠️ <c>NoProfessionSkillCost</c> 是全局字段，必须 Prefix/Postfix 精准包裹（置位→原方法→立即恢复），
    ///   否则污染其他技能。日志里能看到成对置位/恢复记录，证明包裹窗口极短。
    ///   识别用 SkillId：两个技能都只通过事件触发，没有独立"手动点击"路径，按 SkillId 识别安全。
    ///   副作用是原版从人物互动菜单触发的同名互动也免消耗——符合"这两个互动本身不扣消耗"的设计意图。
    /// </summary>
    [HarmonyPatch]
    public class ProfessionSkillPatch
    {
        /// <summary>代笔改名技能的 TemplateId（Config.ProfessionSkill.DefKey.LiteratiSkill0 = 16）。</summary>
        private const int LiteratiSkill0 = 16;

        /// <summary>看诊施药技能的 TemplateId（Config.ProfessionSkill.DefKey.DoctorSkill0 = 52）。</summary>
        private const int DoctorSkill0 = 52;

        /// <summary>Prefix 置位前的原值，Postfix 用它恢复。后端事件执行是单线程的，无需加锁。</summary>
        private static bool _originalNoCost;

        /// <summary>当前技能是否应该免消耗（代笔改名 或 看诊施药）。</summary>
        private static bool ShouldSkipCost(int skillId) => skillId == LiteratiSkill0 || skillId == DoctorSkill0;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProfessionSkillHandle), "OnSkillExecuted")]
        public static void OnSkillExecutedPrefix(ref ProfessionSkillArg arg)
        {
            if (!ShouldSkipCost(arg.SkillId))
                return;

            _originalNoCost = DomainManager.Extra.NoProfessionSkillCost;
            DomainManager.Extra.NoProfessionSkillCost = true;
            string tag = arg.SkillId == LiteratiSkill0 ? "代笔改名" : "看诊施药";
            AdaptableLog.Info($"[{MonthInteraction.LogTag}] {tag}免消耗：NoProfessionSkillCost=true（原值 {_originalNoCost}）");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProfessionSkillHandle), "OnSkillExecuted")]
        public static void OnSkillExecutedPostfix(ref ProfessionSkillArg arg)
        {
            if (!ShouldSkipCost(arg.SkillId))
                return;

            DomainManager.Extra.NoProfessionSkillCost = _originalNoCost;
            string tag = arg.SkillId == LiteratiSkill0 ? "代笔改名" : "看诊施药";
            AdaptableLog.Info($"[{MonthInteraction.LogTag}] {tag}免消耗：已恢复 NoProfessionSkillCost={_originalNoCost}");
        }
    }
}