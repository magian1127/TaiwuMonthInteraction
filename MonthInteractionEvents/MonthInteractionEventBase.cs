using System;
using System.Collections.Generic;
using System.Reflection;
using Config.EventConfig;
using GameData.Common;
using GameData.Domains;
using GameData.Domains.Character;
using GameData.Domains.Map;
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.Enum;
using GameData.Utilities;

namespace MonthInteractionEvents
{
    /// <summary>
    /// 月中互动事件基类 —— 三种互动（促织决斗/代笔改名/看诊施药）各一个子类，共享触发逻辑。
    ///
    /// 共享逻辑：概率检查、位置去重、选地块 NPC、计数检查、轮转顺序。
    /// 子类实现：自身的触发条件（CanTrigger）、执行（ExecuteInteraction）、日志标识。
    ///
    /// ★ 轮转顺序（月初打乱 + 顺位轮转 + 轮空让位）：
    ///   原问题：游戏按 `_headEventList` 固定顺序遍历所有 head 事件的 CheckCondition，
    ///   多个事件都满足前置时总是先入队顺序最前的（即促使"总是先弹促织"）。
    ///
    ///   解法：月初对参与轮转的事件打乱生成一个先后队列 `_turnOrder` + 一个指针 `_turnPointer`。
    ///   每次移动时，只有"指针指向的事件"有机会参与本轮触发竞争：
    ///     - 轮到的事件通过自己的前置（NPC筛选/CanTrigger/计数）就占位入队 → 触发后指针前移一位
    ///     - 轮到的事件前置失败（如看诊没病人 / 达月限） → **轮空让位**：指针前移，本次移动不再触发，
    ///       下次移动从新指针位置开始（即跳过当前事件、优先让下一位）
    ///   不是当前指针指向的事件：CheckCondition 直接 return false（不占位，不影响别人）。
    ///
    ///   这保证：① 任意互动都有均等机会被触发；② 不会因为某事件前置失败浪费整月机会（自动让位）；
    ///   ③ 触发后顺位下移，自然形成"三种互动轮流坐庄"。
    ///
    /// ★ 去重（保留原有逻辑）：一次移动内同一地块只允许一个互动触发。
    ///   游戏对每个事件会调 OnCheckEventCondition 两次（入队 + 显示前二次验证），区分：
    ///     - 本地块 + 本 Guid + 幂等券未用 → 放行二次验证，标记券已用
    ///     - 本地块 + 本 Guid + 幂等券已用 → 不再放行
    ///     - 本地块 + 别 Guid → return false
    ///     - 新地块 → fallthrough 走触发流程
    /// </summary>
    public abstract class MonthInteractionEventBase : TaiwuEventItem
    {
        // ───────── 共享常量 ─────────

        // 触发成功率改为运行时可调设置（默认 10%），见 ModSettings.TriggerChancePercent。
        protected const int NpcSearchRange = 2;            // NPC 搜索范围（地块）

        // ArgBox key
        protected const string KeyTargetCharId = "MI_TargetCharId";

        /// <summary>"取消回退"标记的 ArgBox 键名。子事件 Cancel 选项返回本事件 GUID 时，
        /// 应在 ArgBox 里 Set(此键, true)。CheckConditionInner 见此标记直接放行，绕过幂等券。
        /// 子类设为非空字符串才启用回退支持。</summary>
        protected virtual string CallbackReturnMark => "";

        // ───────── 共享状态 ─────────

        protected static readonly Random Rng = new();

        /// <summary>本次移动已触发互动的地块 + 触发者 Guid + 幂等券已用标志。
        /// 游戏对同一事件会调 OnCheckEventCondition 两次（入队 + 显示前二次验证）：
        ///   - 同一地块 + 同一 Guid + 幂等券未用：放行二次验证，标记券已用
        ///   - 同一地块 + 同一 Guid + 幂等券已用：已用过，不再放行（防止同事件反复入队）
        ///   - 同一地块 + 不同 Guid：别的 event 已触发，return false（保证只弹一个）
        ///   - 新地块：重置，重新开始</summary>
        private static Location _triggeredLocation;
        private static string? _triggeredGuid;
        private static bool _idempotentUsed;

        /// <summary>本次移动的概率掷骰结果（跨事件共享：一次移动只掷一次概率）。
        /// 设计：概率是"本次移动是否发起互动"的前置门槛——没过则所有事件都不判断、直接结束；
        /// 过了才逐个检查事件条件，第一个满足的触发。
        /// 游戏依次调用三个事件实例（每个又调两次=入队+二次验证），所以概率结果必须跨事件共享，
        /// 否则会被多次调用稀释成不可预测的概率。新地块自动重置（重新掷）。</summary>
        private static Location _chanceCheckedLocation;
        private static bool _chancePassed;

        // ───────── 轮转顺序 ─────────

        /// <summary>轮转队列：事件 Guid 字符串按月初打乱后的顺序。
        /// 每次移动只有 _turnOrder[_turnPointer] 指向的事件有机会参与触发竞争。</summary>
        private static List<string>? _turnOrder;
        private static int _turnPointer;

        /// <summary>上次打乱时的月份（用于检测跨月重新打乱）。</summary>
        private static int _turnShuffleMonth = -1;

        /// <summary>确保轮转队列已为本月初始化（月初重排）。由 CheckConditionInner 调用。
        /// 子类在静态构造里把自己 Guid 加入 _allEventGuids（见 MonthInteractionEventPackage）。</summary>
        private static void EnsureTurnOrderShuffled()
        {
            if (_turnOrder != null && _turnOrder.Count > 0)
            {
                int currMonth = DomainManager.World.GetCurrDate() / 30;
                if (currMonth == _turnShuffleMonth) return;  // 同月不重排
            }
            // 月初（或首次）：重建队列并打乱
            var guids = new List<string>(AllEventGuids);
            if (guids.Count == 0) { _turnOrder = new List<string>(); return; }
            // Fisher-Yates 打乱
            for (int i = guids.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                (guids[i], guids[j]) = (guids[j], guids[i]);
            }
            _turnOrder = guids;
            _turnPointer = 0;
            _turnShuffleMonth = DomainManager.World.GetCurrDate() / 30;
            ModSettings.LogDebug($"轮转队列本月已打乱：[{string.Join(", ", guids)}]，指针=0");
        }

        /// <summary>所有参与轮转的 head 事件 Guid（由 MonthInteractionEventPackage 构造时填充）。
        /// 不含非 head 事件（如 GhostwritingNameInputEvent）；不含非本基类的子类事件。</summary>
        internal static readonly List<string> AllEventGuids = new();

        /// <summary>所有参与轮转的 head 事件 tag（EventTag，由 MonthInteractionEventPackage 构造时填充）。
        /// 供 InteractionCounter 的事件池在轮重置时重新填充（池 = 打乱的全部 tag）。</summary>
        internal static readonly List<string> AllEventTags = new();

        // ───────── 反射缓存（所有子类共享）─────────

        protected static readonly MethodInfo? GetRandomCharMethod =
            typeof(CharacterDomain).GetMethod("GetRandomCharacterInRange",
                BindingFlags.Instance | BindingFlags.NonPublic);

        // ───────── 子类实现 ─────────

        /// <summary>本互动的日志标识（如 "CricketBattle"）。</summary>
        protected internal abstract string EventTag { get; }

        /// <summary>太吾是否满足本互动的前置条件（如促织≥3、装备了某技能）。</summary>
        protected abstract bool CanTrigger(int taiwuCharId);

        /// <summary>玩家接受后执行的互动（调原版 API 弹出对应 UI）。</summary>
        protected abstract void ExecuteInteraction(int targetId, EventArgBox argBox);

        /// <summary>在地块范围内选一个目标 NPC。默认实现：随机选一个。
        /// 子类可覆写以加筛选条件（如只选未成年的）。</summary>
        protected virtual int SelectTargetNpc(EventScriptRuntime scriptRuntime, in Location location)
        {
            if (GetRandomCharMethod == null) return -1;
            return (int)GetRandomCharMethod.Invoke(
                DomainManager.Character,
                new object[] { scriptRuntime.Context, location, NpcSearchRange, false })!;
        }

        // ───────── 共享构造 ─────────

        protected MonthInteractionEventBase()
        {
            IsHeadEvent = true;
            TriggerType = Config.EventTriggerType.DefKey.TaiwuBlockChanged;  // = 0
            EventType = EEventType.ModEvent;
            EventGroup = "MonthInteraction";
            ForceSingle = false;
            EventSortingOrder = 500;
            MainRoleKey = "RoleTaiwu";
            TargetRoleKey = "";
            EventBackground = "";
            MaskControl = EventMaskControl.NoChange;
            MaskTweenTime = 0f;

            var acceptOption = new TaiwuEventOption
            {
                OptionKey = "Accept",
                Behavior = 0,
                OnOptionAvailableCheck = () => true,
                OnOptionSelect = () =>
                {
                    try
                    {
                        int targetId = -1;
                        ArgBox.Get(KeyTargetCharId, ref targetId);
                        ModSettings.LogDebug($"玩家接受：{EventTag}，目标 NPC {targetId}");
                        ExecuteInteraction(targetId, ArgBox);
                    }
                    catch (Exception ex)
                    {
                        AdaptableLog.Info($"[MonthInteraction] {EventTag} 接受异常: {ex.Message}");
                    }
                    return null;
                }
            };

            var declineOption = new TaiwuEventOption
            {
                OptionKey = "Decline",
                Behavior = 0,
                OnOptionAvailableCheck = () => true,
                OnOptionSelect = () =>
                {
                    try { OnDecline(); }
                    catch (Exception ex) { AdaptableLog.Info($"[MonthInteraction] {EventTag} OnDecline 异常: {ex.Message}"); }
                    return null;
                }
            };

            EventOptions = new[] { acceptOption, declineOption };
        }

        // ───────── 核心触发逻辑 ─────────

        public override bool OnCheckEventCondition()
        {
            bool result = false;
            try
            {
                result = CheckConditionInner();
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] {EventTag} OnCheckEventCondition 异常: {ex.Message}");
            }
            return result;
        }

        private bool CheckConditionInner()
        {
            // 0. 回退短路：子事件（如 NameInputEvent）的 Cancel 选项返回本事件 GUID 时，
            //    游戏的 ExecuteEventOptionInternal 会调 CheckCondition 验证，但此时走幂等券会返回 false
            //    导致事件关闭而非回退。用 ArgBox 标记识别这种场景，直接放行。
            //    首次触发时 ArgBox 是新的，没有此标记；回退时 ArgBox 从子事件传来，有标记。
            //
            //    ★ 踩坑：游戏在 Option 返回 GUID 时把子事件的 ArgBox 【原样】（同引用）赋给本事件，
            //    不克隆。这个 ArgBox 缺少本事件首次触发流程步骤 8 里塞的 KeyTargetCharId
            //    （短路跳过了那段）。若不在此恢复，再次接受时 ExecuteInteraction 拿到的 targetId
            //    是默认 0 → 改名/互动对象错乱。交子类从子事件带回的键恢复。
            string returnMark = CallbackReturnMark;
            if (!string.IsNullOrEmpty(returnMark) && ArgBox.GetBool(returnMark))
            {
                int restoredTarget = -1;
                if (TryRestoreTargetOnCallback(ref restoredTarget) && restoredTarget >= 0)
                    ArgBox.Set(KeyTargetCharId, restoredTarget);
                return true;
            }

            // 1. 拿太吾对象和位置
            var taiwu = DomainManager.Taiwu.GetTaiwu();
            if (taiwu == null || taiwu.GetId() < 0)
                return false;

            var scriptRuntime = DomainManager.TaiwuEvent.ScriptRuntime;
            if (scriptRuntime?.Context == null)
                return false;

            Location location = taiwu.GetLocation();
            if (!location.IsValid())
                return false;

            // 2. 去重检查（区分"本事件"和"别的 event"）：
            //   同一地块已记录触发者时——
            //     若是本事件 + 幂等券未用：放行二次验证，标记券已用（一券一次性）
            //     若是本事件 + 幂等券已用：不再放行（防止同事件反复入队）
            //     若是别的 event：return false（保证"一次移动只弹一个"）
            //   地块变化时自然 fallthrough 重新走触发流程。
            if (location.Equals(_triggeredLocation))
            {
                if (_triggeredGuid == Guid.ToString())
                {
                    if (_idempotentUsed)
                    {
                        // 幂等券已用：事件已入队并完成二次验证，不再放行后续重复调用
                        return false;
                    }
                    // 本事件之前已通过初筛，放行二次验证（一次性券）
                    _idempotentUsed = true;
                    return true;
                }
                // 本地块已被别的 event 占了
                ModSettings.LogDebug($"{EventTag} 去重拦截（本地块已由 {_triggeredGuid} 触发）");
                return false;
            }

            // 3. ★ 本月无机会短路：事件池空且无剩余轮 → 本月不会再触发任何事件，连概率都不掷。
            //   O(1) 检查，避免月上限用满后每次移动白掷概率 + 白跑条件检查。
            if (!InteractionCounter.HasAnyChanceThisMonth())
                return false;

            // 4. ★ 概率门槛（一次移动只掷一次，结果跨事件共享）。
            //   语义：本次移动是否发起互动——与具体哪个事件无关。
            //   没过 → 本次移动所有事件都不判断，直接结束；
            //   过了 → 进入步骤5，检查本事件条件。
            //   结果按地块缓存（_chancePassed），三个事件实例 + 二次验证都复用，新地块自动重掷。
            if (!_chanceCheckedLocation.Equals(location))
            {
                _chanceCheckedLocation = location;
                _chancePassed = Rng.Next(100) < ModSettings.TriggerChancePercent;
                ModSettings.LogDebug($"概率掷骰（本次移动）：{(_chancePassed ? "过" : "未过")}（{ModSettings.TriggerChancePercent}%）");
            }
            if (!_chancePassed)
                return false;

            // 5. 概率已过，检查本事件自身条件。
            //   轮换队列：本事件还在队列里（有剩余次数）→ 挪到队尾让位，让下一次排前面的优先。
            //   不在队列（本月次数用完）→ return false，瞬间退出（不选 NPC、不做后续计算）。
            //   占位机制（步骤7）保证"一次移动只触发第一个满足条件的事件"。
            if (!InteractionCounter.TryRotateToBack(EventTag))
            {
                ModSettings.LogDebug($"{EventTag} 不满足（本月次数已用完）");
                return false;
            }

            int targetId = SelectTargetNpc(scriptRuntime, location);
            if (targetId < 0)
            {
                ModSettings.LogDebug($"{EventTag} 不满足（NPC 筛选失败）");
                return false;
            }

            if (!CanTrigger(taiwu.GetId()))
            {
                ModSettings.LogDebug($"{EventTag} 不满足（CanTrigger 失败）");
                return false;
            }

            // 6. 计数消耗（扣减剩余次数 + 记 NPC；归零从队列移除）
            if (!InteractionCounter.TryConsumeCount(targetId, EventTag))
            {
                ModSettings.LogDebug($"{EventTag} 不满足（计数超限）");
                return false;
            }

            // 7. 占位（本事件成为本次移动的触发者；同地块后续调用凭 Guid 幂等返回 true）
            string myGuid = Guid.ToString();
            _triggeredLocation = location;
            _triggeredGuid = myGuid;
            _idempotentUsed = false;  // 新地块/新触发者，幂等券重置

            // 8. 塞 ArgBox
            ArgBox.Set(KeyTargetCharId, targetId);

            // 9. 子类钩子：基于已选目标塞额外 ArgBox 数据（如查父母塞 dialogCharId）
            OnTargetSelected(targetId);

            ModSettings.LogDebug($"触发：{EventTag}，目标 NPC {targetId}，Guid={myGuid}");
            return true;
        }

        /// <summary>目标 NPC 选定后的钩子，子类可覆写往 ArgBox 塞额外数据。
        /// 默认空实现。targetId 是 KeyTargetCharId 对应的 NPC。</summary>
        protected virtual void OnTargetSelected(int targetId) { }

        /// <summary>取消回退放行时的钩子：子类从子事件 Cancel 带回的 ArgBox 键恢复首次触发时
        /// 选定的目标 id。基类在 CallbackReturnMark 短路放行前调用，返回 true 且 targetId>=0
        /// 时写回 KeyTargetCharId，避免再次接受时 ExecuteInteraction 拿到默认 0。
        /// 默认 false（不恢复）。仅支持取消回退的子类（如 Ghostwriting）需要覆写。</summary>
        protected virtual bool TryRestoreTargetOnCallback(ref int targetId) => false;

        /// <summary>玩家拒绝时调用的钩子，默认空实现。子类可覆写做清理（如恢复其他 MOD 的状态）。</summary>
        protected virtual void OnDecline() { }

        /// <summary>轮转指针前移一位（环形）。触发成功或轮空让位时调用。</summary>
        private static void AdvanceTurnPointer()
        {
            if (_turnOrder == null || _turnOrder.Count == 0) return;
            _turnPointer = (_turnPointer + 1) % _turnOrder.Count;
        }

        /// <summary>读档/进新世界时由 InteractionCounter.Reset() 调用，清掉轮转队列静态状态。
        /// 下次 CheckCondition 时 EnsureTurnOrderShuffled 会按当前月份重建。</summary>
        internal static void ResetTurnState()
        {
            _turnOrder = null;
            _turnPointer = 0;
            _turnShuffleMonth = -1;
        }

        public override void OnEventEnter() { }
        public override void OnEventExit() { }

        /// <summary>事件正文由语言文件提供（每个子类一个 GUID + 一段文本）。</summary>
        public override string GetReplacedContentString() => "";

        // ───────── 共享工具 ─────────

        /// <summary>技能 DefKey 缓存。</summary>
        private static int? _literatiSkillId;
        private static int? _doctorSkillId;

        /// <summary>检查太吾是否装备了指定志向技能。</summary>
        protected static bool IsSkillEquipped(string skillDefKeyName)
        {
            try
            {
                int skillId;
                if (skillDefKeyName == "LiteratiSkill0")
                {
                    _literatiSkillId ??= Convert.ToInt32(typeof(Config.ProfessionSkill.DefKey)
                        .GetField("LiteratiSkill0")?.GetValue(null));
                    skillId = _literatiSkillId.Value;
                }
                else if (skillDefKeyName == "DoctorSkill0")
                {
                    _doctorSkillId ??= Convert.ToInt32(typeof(Config.ProfessionSkill.DefKey)
                        .GetField("DoctorSkill0")?.GetValue(null));
                    skillId = _doctorSkillId.Value;
                }
                else
                {
                    return false;
                }

                var slots = DomainManager.Extra.GetTaiwuProfessionSkillSlots();
                return slots != null && slots.IsEquipped(skillId);
            }
            catch (Exception ex)
            {
                AdaptableLog.Info($"[MonthInteraction] IsSkillEquipped({skillDefKeyName}) 异常: {ex.Message}");
                return false;
            }
        }
    }
}
