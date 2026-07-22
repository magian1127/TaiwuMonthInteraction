using System;
using Config.EventConfig;
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.Enum;
using GameData.Domains.TaiwuEvent.EventHelper;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 看诊施药 —— 选药子事件（自写选药界面，确认后接入原版事件链）。
    ///
    /// ★ 设计原则（同 <see cref="Ghostwriting.GhostwritingNameInputEvent"/>）：
    ///   - 选药界面（装配药品列表、玩家选药、点确认/取消）全部由本 MOD 自写。
    ///   - 玩家点确认后，<b>必须调用原版事件链</b>，不允许自写治病/结果逻辑。
    ///
    /// 选药界面照抄原版 <c>TaiwuEvent_b6084cf3</c> 的 OnEventEnter（else 分支）：
    ///   <c>EventHelper.FilterItemForCharacterByType</c> 在 ArgBox 装配药品列表，
    ///   游戏的 ViewEventWindow 见 <c>SelectItemInfo</c> 自动渲染选药网格（与输入框同机制）。
    ///
    /// 确认按钮照抄原版 b6084cf3.OnOption1Select：
    ///   调 <see cref="EventHelper.ConfirmSkillExecute(string, EventArgBox)"/>，afterEvent 指向原版
    ///   <c>b6084cf3</c>。技能执行后原版 b6084cf3 重新进 OnEventEnter，见
    ///   <c>ConchShip_PresetKey_FinishSkillExecute=true</c> 走治病分支（读 SelectItemKey、按药品效果
    ///   ToEvent 到结果窗）。治病 + 结果分发全由原版处理，本 MOD 不写任何治病逻辑。
    ///
    /// 取消按钮回到 MOD 自写的看诊首页 <see cref="MedicineEvent"/>（带标记，绕过幂等券）。
    /// ★ 与直接触发原版 b6084cf3 的差异：原版 b6084cf3 的取消(Option_2)硬跳 3876fafa 确认框，
    ///   自建事件后取消由 MOD 控制，直接回首页，不经过 3876fafa。
    /// </summary>
    public class MedicineSelectEvent : TaiwuEventItem
    {
        private const string SelfGuid = "a1b2c3d4-3334-4aaa-9001-000000000013";
        private const string MedicineEventGuid = "a1b2c3d4-3333-4aaa-9001-000000000003";

        /// <summary>原版看诊施药·选药+治病事件 GUID（确认后 ConfirmSkillExecute 的 afterEvent 指向它，
        /// 原版接管治病 + 结果分发）。同 <see cref="MedicineEvent"/> 的 SelectMedicineEventGuid。</summary>
        private const string OriginalMedicineEventGuid = "b6084cf3-7fd3-4ed9-8ddc-144f17c00dc5";

        public MedicineSelectEvent()
        {
            Guid = new Guid(SelfGuid);
            IsHeadEvent = false;
            TriggerType = 0;
            EventType = EEventType.ModEvent;
            EventGroup = "MonthInteraction";
            ForceSingle = false;
            EventSortingOrder = 510;
            MainRoleKey = "RoleTaiwu";
            TargetRoleKey = "CharacterId";   // 右侧显示病人（看诊对象）
            EventBackground = "";            // 留空：原版的 tex_profession_doctor_0 背景图 MOD 无法加载
            MaskControl = EventMaskControl.NoChange;
            MaskTweenTime = 0f;
            EscOptionKey = "Cancel";

            EventOptions = new[]
            {
                new TaiwuEventOption
                {
                    OptionKey = "Confirm",
                    Behavior = 0,
                    OnOptionAvailableCheck = () => true,
                    OnOptionSelect = () =>
                    {
                        // ★ 照抄原版 b6084cf3 的 OnOption1Select：
                        //   调原版 ConfirmSkillExecute，afterEvent 指向原版 b6084cf3。
                        //   原版接管后自动执行：播动画 + 读 SelectItemKey + 按药品效果治病 + 结果窗。
                        //   本 MOD 不写任何治病/结果逻辑。
                        //
                        // 额外（MOD 需求）：跳过原版 ConfirmSkillExecute 弹出的"消耗确认"弹窗。
                        //   SkipConfirm=true 让 HandleDisplayEvent_ConfirmProfessionSkillExecute
                        //   直接 StartShowSkillAnim，不弹 UI_ProfessionSkillConfirm 框。
                        //   配合后端 ProfessionSkillPatch 包裹 OnSkillExecuted，免扣时间/历练/资源。
                        ArgBox.Set("SkipConfirm", true);
                        ModSettings.LogDebug("MedicineSelect 确认：接入原版治病事件链（跳过确认框+免消耗）" + OriginalMedicineEventGuid);
                        EventHelper.ConfirmSkillExecute(OriginalMedicineEventGuid, ArgBox);
                        return "";
                    }
                },
                new TaiwuEventOption
                {
                    OptionKey = "Cancel",
                    Behavior = 0,
                    OnOptionAvailableCheck = () => true,
                    OnOptionSelect = () =>
                    {
                        // 取消：带标记回首页（绕过幂等券 + 恢复目标）。
                        // ★ 由 MOD 控制取消去向，直接回看诊首页，不经过原版 3876fafa 确认框。
                        ArgBox.Set("MI_FromMedicine", true);
                        return MedicineEventGuid;
                    }
                }
            };
        }

        public override bool OnCheckEventCondition() => true;

        /// <summary>装配选药面板。照抄原版 b6084cf3 的 OnEventEnter（else 分支，非 FinishSkillExecute 时）：
        /// 设右侧显示伤势 + FilterItemForCharacterByType 筛选太吾库存药品写进 SelectItemKey。
        /// 游戏见 ArgBox 的 SelectItemInfo 自动渲染选药网格。</summary>
        public override void OnEventEnter()
        {
            try
            {
                // 右侧显示病人伤势信息（原版同款）
                ArgBox.Set("ConchShip_PresetKey_RightRoleShowInjuryInfo", true);
                // 筛选太吾库存药品（subType=16=药品，maxMoneyValue=800，与原版一致）
                EventHelper.FilterItemForCharacterByType(
                    EventArgBox.TaiwuCharacterId, "SelectItemKey", ArgBox,
                    (sbyte)(-1), (short)800, false, null, (sbyte)16);
                ModSettings.LogDebug("MedicineSelect OnEventEnter：已装配选药面板");
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] MedicineSelect OnEventEnter 异常: {ex.Message}");
            }
        }

        public override void OnEventExit() { }

        /// <summary>事件正文由语言文件提供。</summary>
        public override string GetReplacedContentString() => "";
    }
}
