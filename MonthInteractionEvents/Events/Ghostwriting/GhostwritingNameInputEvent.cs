using System;
using Config.EventConfig;
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.DisplayEvent;
using GameData.Domains.TaiwuEvent.Enum;
using GameData.Domains.TaiwuEvent.EventHelper;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 代笔改名 —— 输入名字事件（自写输入界面，确认后接入原版事件链）。
    ///
    /// ★ 设计原则（用户硬要求）：
    ///   - 输入界面（装配输入框、玩家输入名字、点确认）全部由本 MOD 自写。
    ///   - 玩家点确认后，<b>必须调用原版事件链</b>，不允许自写改名/结果逻辑。
    ///
    /// 确认按钮的实现完全照抄原版输入事件 <c>TaiwuEvent_4347b51a-5ebd-49f1-a6e0-cf4229fcb4ce</c>
    /// 的 Option_1：调用 <see cref="EventHelper.ConfirmSkillExecute(string, EventArgBox)"/>，
    /// afterEvent 指向原版结果分发事件 <c>d6587697-8896-458e-ae9e-4b56dc54b022</c>。
    /// 之后原版自动接管：执行 LiteratiSkill0 改名 → 敏感词/资历判定 → 弹出成功/失败结果窗。
    ///
    /// 取消按钮回到 MOD 自写的首页 <see cref="GhostwritingEvent"/>（父母对话）。
    /// </summary>
    public class GhostwritingNameInputEvent : TaiwuEventItem
    {
        // ArgBox 键（与原版输入事件一致）
        private const string KeyInputResult = "InputResult";
        private const string KeyInputRequestData = "InputRequestData";
        private const string KeyChildCharId = "MI_ChildCharId";

        private const string SelfGuid = "a1b2c3d4-2224-4aaa-9001-000000000012";
        private const string GhostwritingEventGuid = "a1b2c3d4-2222-4aaa-9001-000000000002";

        /// <summary>原版代笔改名结果分发事件 GUID（技能执行完毕后进入，分发到成功/失败结果窗）。</summary>
        private const string OriginalResultEventGuid = "d6587697-8896-458e-ae9e-4b56dc54b022";

        public GhostwritingNameInputEvent()
        {
            Guid = new Guid(SelfGuid);
            IsHeadEvent = false;
            TriggerType = 0;
            EventType = EEventType.ModEvent;
            EventGroup = "MonthInteraction";
            ForceSingle = false;
            EventSortingOrder = 510;
            MainRoleKey = "RoleTaiwu";
            TargetRoleKey = "CharacterId";   // 右侧显示孩子（改名对象）
            EventBackground = "";            // 留空：原版的 tex_profession_literati_0 背景图 MOD 无法加载
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
                        // ★ 照抄原版 TaiwuEvent_4347b51a 的 OnOption1Select：
                        //   调用原版 ConfirmSkillExecute，afterEvent 指向原版结果分发事件。
                        //   原版接管后自动执行：改名 + 敏感词/资历判定 + 成功/失败结果窗。
                        //   本 MOD 不写任何改名/结果逻辑。
                        //
                        // 额外（MOD 需求）：跳过原版的"确认消耗"弹窗，直接进入下一步。
                        //   SkipConfirm=true 让 HandleDisplayEvent_ConfirmProfessionSkillExecute
                        //   直接 StartShowSkillAnim，不弹 UI_ProfessionSkillConfirm 框。
                        //   配合后端 ProfessionSkillPatch 包裹 OnSkillExecuted，免扣时间/历练/资源。
                        ArgBox.Set("SkipConfirm", true);
                        // ★ MainInteractionHeadEvent：原版结果窗（成功/失败）退出时读此键决定跳回目标
                        //   （?? fb38f657 原版人物互动主菜单）。注入 MOD 首页 GUID，让结果窗结束回 MOD 首页。
                        ArgBox.Set("MainInteractionHeadEvent", GhostwritingEventGuid);
                        ModSettings.LogDebug("NameInput 确认：接入原版事件链（跳过确认框+免消耗）" + OriginalResultEventGuid);
                        EventHelper.ConfirmSkillExecute(OriginalResultEventGuid, ArgBox);
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
                        // 取消：清理输入监听器，带标记回首页（绕过幂等券）
                        ArgBox.Set("MI_FromNameInput", true);
                        EventHelper.RemoveEventInListenWithActionName(Guid.ToString(), "InputActionComplete");
                        ArgBox.Remove<EventInputRequestData>(KeyInputRequestData);
                        ArgBox.Remove<string>(KeyInputResult);
                        return GhostwritingEventGuid;
                    }
                }
            };
        }

        public override bool OnCheckEventCondition() => true;

        /// <summary>装配输入面板。照抄原版 TaiwuEvent_4347b51a 的 OnEventEnter，
        /// 仅 DataKey/FullName 取值方式对齐 MOD 自有的 ArgBox 键。</summary>
        public override void OnEventEnter()
        {
            try
            {
                ArgBox.Remove<string>(KeyInputResult);
                var req = new EventInputRequestData
                {
                    DataKey = KeyInputResult,
                    InputDataType = 3,
                    FullName = ArgBox.GetCharacter().GetFullName(),
                    NumberRange = new int[] { 1, EventHelper.GetNameLengthConfig()[1] }
                };
                ArgBox.Set(KeyInputRequestData, req);
                EventHelper.AddEventInListenWithActionName(Guid.ToString(), ArgBox, "InputActionComplete");
                ModSettings.LogDebug("NameInput OnEventEnter：已装配输入面板");
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] NameInput OnEventEnter 异常: {ex.Message}");
            }
        }

        public override void OnEventExit()
        {
            // 照抄原版：退出时注销输入监听器
            try
            {
                EventHelper.RemoveEventInListenWithActionName(Guid.ToString(), "InputActionComplete");
            }
            catch { }
        }

        /// <summary>事件正文由语言文件提供（"请输入新的名字"）。</summary>
        public override string GetReplacedContentString() => "";
    }
}
