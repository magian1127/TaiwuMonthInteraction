return {
    Description = [[月中互动 —— 太吾移动到新地块时，依据装备的志向技能有概率触发路人主动发起互动：
- 促织决斗：无前置条件, 人物会拿出指定物品。
- 代笔改名：装备才俊技能, 人物会要求太吾帮他孩子改名, 触发不消耗时间、历练等。
- 看诊施药：装备大夫技能, 触发不消耗时间、历练等。

设置中可以更改触发率(30%)和每种互动每月最大触发次数(2)。

源码
https://github.com/magian1127/TaiwuMonthInteraction
]],
	Cover = "Cover.png",
    Author = "Magian",
    FileId = 3762534926,
    Source = 1,
    GameVersion = "1.0.44.0",
    Version = "0.2.0.0",
    Title = "月中互动",
    BackendPlugins = {
        "MonthInteractionBackend.dll",
    },
    -- ★ 事件包独立 DLL：游戏从 Events\EventLib\ 加载，注册 EventPackage 子类到事件系统
    -- 触发类型 TaiwuBlockChanged（太吾移动到新地块时触发）
    EventPackages = {
        "MonthInteractionEvents.dll",
    },
    TagList = {
        "Modifications",
        "Extensions",
    },
    -- 可调设置（游戏设置面板显示为滑块，后端读入后反射推送给事件包 DLL）
    DefaultSettings = {
        {
            SettingType = "Toggle",
            Key = "DebugMode",
            DisplayName = "调试模式",
            Description = "输出详细日志到GameData日志用于排查问题（轮转/概率/计数等运行时明细）",
            DefaultValue = false,
        },
        {
            SettingType = "Slider",
            Key = "TriggerChancePercent",
            DisplayName = "触发成功率",
            Description = "太吾移动到新地块时触发月中互动的概率（%）",
            MinValue = 0,
            MaxValue = 100,
            StepSize = 1,
            DefaultValue = 30,
        },
        {
            SettingType = "Slider",
            Key = "MaxPerEventPerMonth",
            DisplayName = "单事件每月最大数量",
            Description = "每种互动（促织决斗/代笔改名/看诊施药）每月最多触发的次数",
            MinValue = 0,
            MaxValue = 10,
            StepSize = 1,
            DefaultValue = 2,
        },
    },
	WorkshopCover = "Cover.png",
}
