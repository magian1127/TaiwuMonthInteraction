using System.Collections.Generic;
using Config.EventConfig;

namespace MonthInteractionEvents
{
    /// <summary>
        /// 月中互动事件包 —— 游戏扫描 Config.lua 的 EventPackages 字段后自动实例化并注册。
        ///
        /// 含三个事件，各有独立 GUID 和语言文件文本：
        ///   - CricketBattleEvent（促织决斗）：太吾促织≥3 时可触发
        ///   - GhostwritingEvent（代笔改名）：太吾装备才俊技能 + 地块有未成年 NPC 时可触发
        ///   - MedicineEvent（看诊施药）：太吾装备了大夫技能时可触发
        ///
        /// 三个触发事件共享互斥锁（基类 _triggeredLocation）：一次移动只触发一个。
        /// 触发顺序由基类轮转队列管理（月初打乱 + 顺位轮转 + 轮空让位），
        /// OnlyEventGuids 注册所有参与轮转的 head 事件 Guid（不含子事件如 GhostwritingNameInputEvent）。
    /// </summary>
    public class MonthInteractionEventPackage : EventPackage
    {
        public MonthInteractionEventPackage()
        {
            NameSpace = "MonthInteraction";
            Author = "Magian";
            Group = "Default";
            EventList = new List<TaiwuEventItem>
            {
                new CricketBattleEvent(),
                new GhostwritingEvent(),
                new GhostwritingNameInputEvent(),
                new MedicineEvent(),
                new MedicineSelectEvent()
            };
            foreach (var eventItem in EventList)
            {
                eventItem.Package = this;
            }
            // ★ 收集参与轮转的 head 事件 Guid（IsHeadEvent=true 的）
            // 基类的轮转队列在月初会按此列表打乱，决定每次移动哪个事件优先触发。
            // GhostwritingNameInputEvent / MedicineSelectEvent 是子事件（IsHeadEvent=false），不参与轮转。
            MonthInteractionEventBase.AllEventGuids.Clear();
            MonthInteractionEventBase.AllEventTags.Clear();
            foreach (var eventItem in EventList)
            {
                if (eventItem is MonthInteractionEventBase headEvent && headEvent.IsHeadEvent)
                {
                    MonthInteractionEventBase.AllEventGuids.Add(headEvent.Guid.ToString());
                    MonthInteractionEventBase.AllEventTags.Add(headEvent.EventTag);
                }
            }
        }
    }
}
