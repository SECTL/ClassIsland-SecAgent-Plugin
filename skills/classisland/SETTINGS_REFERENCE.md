# ClassIsland Settings 参考

本文档对应 ClassIsland `Settings` 模型。主配置文件是应用根目录的 `Settings.json`，不是 `Config` 目录中的插件配置。字段名以 JSON 中实际出现的名称为准；`SettingsOverlays` 在 JSON 中兼容使用旧名称 `SettingsOverlay`。

## 调用规则

1. 先调用 `classisland__read_classisland_main_config` 获取当前值，不要凭字段名猜值的类型。
2. 只提交用户要求的字段，例如“主界面透明度 50%”应调用：

   ```json
   {"patch":{"Opacity":0.5}}
   ```

3. `Opacity` 是“不透明度”，取值范围通常为 `0.0` 到 `1.0`：`0.5` 表示 50% 不透明，而不是传 `50`。
4. 布尔值必须是 `true`/`false`，数字不要加引号；日期使用当前 JSON 中已有的 ISO 日期格式；颜色、枚举、字典和集合必须先读取现值再修改。
5. `SettingsOverlay` 是 CI 内部的设置叠层数据，不是普通用户设置。不要修改或删除它；更新主配置时必须保留整个对象。
6. 更新工具会校验 JSON 和 `Settings` 类型，写入失败时不要改用文件系统或反复重试；先检查字段名、包装层级和类型。
7. 更新成功返回 `applied_runtime: true` 时，插件已经通过运行中的 `SettingsService` 修改并保存，效果等同于手动在设置页修改；只有启动时读取的设置才需要重启 CI。

## 常用设置

| 字段 | JSON 类型 | 含义与取值 |
|---|---|---|
| `SelectedProfile` | string | 当前使用的档案文件名，例如 `Default.json`。|
| `IsMainWindowVisible` | boolean | 是否显示 CI 主界面。|
| `Opacity` | number | 主界面背景不透明度，通常 `0`–`1`。|
| `Scale` | number | 主界面缩放比例，`1.0` 为原始大小。|
| `Theme` | integer | 主题选择索引；不要猜索引含义，先读取并参考当前 UI。|
| `PrimaryColor` / `SecondaryColor` | color | 主题主色/辅色，保持 CI 当前 JSON 颜色格式。|
| `BackgroundColor` | color | 自定义背景色。|
| `IsCustomBackgroundColorEnabled` | boolean | 是否启用自定义背景色。|
| `MainWindowFont` | string | 主界面字体标识。|
| `MainWindowFontWeight2` | integer | 主界面字体粗细。|
| `MainWindowSecondaryFontSize` | number | 次要文字字号。|
| `MainWindowBodyFontSize` | number | 正文文字字号。|
| `MainWindowEmphasizedFontSize` | number | 强调文字字号。|
| `MainWindowLargeFontSize` | number | 大号文字字号。|
| `WindowDockingLocation` | integer | 窗口停靠位置枚举索引；必须参考现有值或 UI。|
| `WindowDockingOffsetX` / `WindowDockingOffsetY` | integer | 窗口停靠偏移量。|
| `WindowDockingMonitorIndex` | integer | 使用的显示器索引。|
| `WindowLayer` | integer | 窗口层级枚举。|
| `IsMouseClickingEnabled` | boolean | 是否允许鼠标点击穿透/交互，具体表现取决于平台。|
| `HideOnClass` | boolean | 上课时是否隐藏主界面。|
| `HideOnFullscreen` | boolean | 全屏窗口时是否隐藏。|
| `HideOnMaxWindow` | boolean | 最大化窗口时是否隐藏。|
| `HideMode` | integer | 隐藏规则模式枚举。|
| `HideRules` | object | 隐藏规则对象；只能对读取到的规则做局部修改。|
| `ShowDate` | boolean | 是否显示日期。|
| `SingleWeekStartTime` | string/date-time | 单周循环起始时间。|
| `MultiWeekRotationOffset` | array<number> | 多周循环偏移数组；不要只改一个元素后覆盖整个数组，除非用户明确要求。|
| `MultiWeekRotationMaxCycle` | integer | 多周循环最大周数。|
| `ClassPrepareNotifySeconds` | integer | 上课前提醒秒数。|
| `IsClassPrepareNotificationEnabled` | boolean | 是否启用上课前提醒。|
| `IsClassChangingNotificationEnabled` | boolean | 是否启用换课提醒。|
| `IsClassOffNotificationEnabled` | boolean | 是否启用下课提醒。|
| `ShowExtraInfoOnTimePoint` | boolean | 时间点是否显示额外信息。|
| `ExtraInfoType` | integer | 额外信息类型枚举。|
| `IsCountdownEnabled` | boolean | 是否显示倒计时。|
| `CountdownSeconds` | integer | 倒计时提醒阈值秒数。|
| `ScheduleSpacing` | number | 课表/时间轴间距。|
| `ShowCurrentLessonOnlyOnClass` | boolean | 上课时是否只显示当前课程。|
| `DefaultOnClassTimePointMinutes` | integer | 默认上课时间点分钟数。|
| `DefaultBreakingTimePointMinutes` | integer | 默认下课/课间时间点分钟数。|

## 通知、语音和天气

| 字段 | JSON 类型 | 含义 |
|---|---|---|
| `IsNotificationEnabled` | boolean | 总通知开关。|
| `NotificationProvidersEnableStates` | object | 通知提供者启用状态字典。|
| `NotificationProvidersPriority` | array<string> | 通知提供者优先级顺序。|
| `NotificationProvidersSettings` | object | 各通知提供者的扩展设置。|
| `NotificationProvidersNotifySettings` | object | 各通知提供者通知策略。|
| `NotificationChannelsNotifySettings` | object | 各通知频道通知策略。|
| `IsSpeechEnabled` | boolean | 是否启用语音播报。|
| `SpeechVolume` / `NotificationSoundVolume` | number | 语音/通知音量，通常为 `0`–`1`。|
| `SpeechSource` | integer | 语音来源枚举。|
| `SelectedSpeechProvider` | string | 当前语音提供者标识。|
| `EdgeTtsVoiceName` | string | Edge TTS 声音名称。|
| `IsNotificationEffectEnabled` | boolean | 是否启用通知特效。|
| `IsNotificationSoundEnabled` | boolean | 是否启用通知声音。|
| `NotificationSoundPath` | string | 通知声音文件路径。|
| `IsNotificationTopmostEnabled` | boolean | 通知是否置顶。|
| `NotificationEffectRenderingScale` | number | 通知特效渲染缩放。|
| `AllowNotificationSpeech` / `AllowNotificationEffect` / `AllowNotificationSound` / `AllowNotificationTopmost` | boolean | 对应通知能力的总许可开关。|
| `CityId` / `CityName` | string | 天气城市标识和显示名称。|
| `WeatherLongitude` / `WeatherLatitude` | number | 天气位置经纬度。|
| `WeatherLocationSource` | integer | 天气位置来源枚举。|
| `AutoRefreshWeatherLocation` | boolean | 是否自动刷新天气位置。|
| `ExcludedWeatherAlerts` | array<string> | 忽略的天气预警类型。|

## 更新、自动化、插件和调试

| 字段 | JSON 类型 | 含义 |
|---|---|---|
| `IsAutomationEnabled` | boolean | 是否启用自动化。|
| `CurrentAutomationConfig` | string | 当前自动化配置名。|
| `IsAutomationWarningVisible` | boolean | 是否显示自动化警告。|
| `UpdateMode` | integer | 更新模式枚举。|
| `SelectedChannel` / `SelectedUpdateChannelV2` | string | 更新渠道。|
| `SelectedUpdateMirrorV2` | string | 更新镜像。|
| `AutoInstallUpdateNextStartup` | boolean | 下次启动是否自动安装更新。|
| `IsPluginMarketWarningVisible` | boolean | 是否显示插件市场警告。|
| `OfficialIndexMirrors` | object<string,string> | 官方插件索引镜像字典。|
| `OfficialSelectedMirror` | string | 当前官方镜像。|
| `IgnoreSslForPluginMirrors` | boolean | 是否忽略插件镜像 SSL 校验；不建议开启。|
| `IsAutoBackupEnabled` | boolean | 是否启用自动备份。|
| `AutoBackupLimit` | integer | 自动备份数量上限。|
| `AutoBackupIntervalDays` | integer | 自动备份间隔天数。|
| `IsDebugEnabled` / `IsDebugOptionsEnabled` / `IsMainWindowDebugEnabled` | boolean | 调试开关；除非用户明确要求，不要修改。|
| `DebugAnimationScale` / `DebugTimeSpeed` / `DebugTimeOffsetSeconds` | number | 调试动画、时间速度和时间偏移。|
| `IsDebugConsoleEnabled` | boolean | 是否启用调试控制台。|
| `IsCriticalSafeMode` | boolean | 是否启用关键安全模式。|
| `CriticalSafeModeMethod` | integer | 安全模式策略枚举。|

## 完整字段目录（当前模型）

以下字段均为 `Settings` 的公开可序列化属性。未在上面展开的复杂字段，必须先读取后按原结构局部更新：

```text
SelectedProfile, IsMainWindowVisible, IsWelcomeWindowShowed, SettingsOverlay,
WeatherLongitude, WeatherLatitude, WeatherLocationSource, AutoRefreshWeatherLocation,
NoTLSWeatherRequests, SingleWeekStartTime, MultiWeekRotationOffset, MultiWeekRotationMaxCycle,
ClassPrepareNotifySeconds, IsClassPrepareNotificationEnabled, MiniInfoProviderSettings,
ShowDate, HideOnClass, IsClassChangingNotificationEnabled, IsClassOffNotificationEnabled,
HideMode, HideRules, HideOnFullscreen, ExcludedFullscreenWindow, HideOnMaxWindow,
IsReportingEnabled, IsSentryEnabled, TaskBarIconClickBehavior, ShowExtraInfoOnTimePoint,
ExtraInfoType, IsCountdownEnabled, CountdownSeconds, ExtraInfo4ShowSecondsSeconds,
ScheduleSpacing, ShowCurrentLessonOnlyOnClass, IsNonExactCountdownEnabled,
DefaultOnClassTimePointMinutes, DefaultBreakingTimePointMinutes, IsSplashEnabled,
SplashCustomText, SplashCustomLogoSource, ExactTimeServer, IsExactTimeEnabled,
TimeOffsetSeconds, IsTimeAutoAdjustEnabled, TimeAutoAdjustSeconds, LastTimeAdjustDateTime,
IsWaitForTransientDisabled, AnimationLevel, IsCriticalSafeMode, ShowDetailedStatusOnSplash,
CriticalSafeModeMethod, AutoDisableCorruptPlugins, CorruptPluginsDisabledLastSession,
ReduceProgressAccuracy, AppLastStartedTime, IsRefreshingToastEnabled,
RefreshingToastThresholdDays, ShowRefreshingToastOnNextStart, MaxRefreshingToastCounts,
LeftRefreshingToastCounts, RefreshingToastIsOnboardingGuide, OnboardingToastTitle,
OnboardingToastBody, RefreshingScopes, Theme, PrimaryColor, SecondaryColor, ColorSource,
WallpaperColorPlatte, SelectedPlatteIndex, IsWallpaperAutoUpdateEnabled,
WallpaperAutoUpdateIntervalSeconds, WallpaperClassName, IsFallbackModeEnabled,
UseExperimentColorPickingMethod, TargetLightValue, IsCustomBackgroundColorEnabled,
BackgroundColor, Opacity, Scale, MainWindowFont, MainWindowFontWeight2, RadiusX, RadiusY,
MainWindowSecondaryFontSize, MainWindowBodyFontSize, MainWindowEmphasizedFontSize,
MainWindowLargeFontSize, IsCustomForegroundColorEnabled, CustomForegroundColor,
MainWindowLineVerticalMargin, IsIslandSeperated, CurrentComponentConfig,
IsNotificationEnabled, NotificationProvidersEnableStates, NotificationProvidersPriority,
NotificationProvidersSettings, NotificationProvidersNotifySettings,
NotificationChannelsNotifySettings, IsSystemSpeechSystemExist, IsNetworkConnect,
IsSpeechEnabled, SpeechVolume, SpeechSource, SelectedSpeechProvider, EdgeTtsVoiceName,
IsNotificationEffectEnabled, IsNotificationSoundEnabled, NotificationSoundPath,
IsNotificationTopmostEnabled, NotificationEffectRenderingScale,
IsNotificationEffectRenderingScaleAutoSet, AllowNotificationSpeech, AllowNotificationEffect,
AllowNotificationSound, AllowNotificationTopmost, NotificationSoundVolume,
NotificationSpeechCustomSmgTokenSource, GptSoVitsSpeechSettings,
NotificationUseStandaloneEffectUiThread, IsAutomationEnabled, CurrentAutomationConfig,
IsAutomationWarningVisible, UpdateMode, SelectedChannel, LastCheckUpdateTime,
LastUpdateStatus, AutoInstallUpdateNextStartup, UpdateArtifactHash, SelectedUpdateMirrorV2,
SelectedUpdateChannelV2, SelectedUpdateChannelV3, DebugSubChannelOverride,
DebugPublicKeyOverride, DebugPhainonRootUrlOverride, WindowDockingLocation,
IsIgnoreWorkAreaEnabled, WindowDockingOffsetX, WindowDockingOffsetY, WindowDockingMonitorIndex,
WindowTopmostRecheckMode, IsScreenRecordingModeEnabled, WindowLayer, IsMouseClickingEnabled,
UseRawInput, IsMouseInFadingEnabled, IsMouseInFadingReversed, TouchInFadingDurationMs,
IsCompatibleWindowTransparentEnabled, IsErrorLoadingRawInput, LastWeatherInfo, CityId,
CityName, ExcludedWeatherAlerts, WeatherIconId, ExpIsExcelImportEnabled,
ExpAllowEditingActivatedTimeLayout, DiagnosticFirstLaunchTime, DiagnosticStartupCount,
DiagnosticCrashCount, DiagnosticLastCrashTime, DiagnosticMemoryKillCount,
DiagnosticLastMemoryKillTime, IsAutoBackupEnabled, LastAutoBackupTime, BackupFilesSize,
AutoBackupLimit, AutoBackupIntervalDays, OfficialIndexMirrors, OfficialSelectedMirror,
PluginIndexes, IgnoreSslForPluginMirrors, LastRefreshPluginSourceTime,
IsPluginMarketWarningVisible, IsPluginsAutoUpdateEnabled, IsPluginsUpdateNotificationEnabled,
IsRollingComponentWarningVisible, IsThemeWarningVisible, IsThemeSeparateInfoVisible,
IsDebugEnabled, IsDebugOptionsEnabled, IsMainWindowDebugEnabled, DebugAnimationScale,
DebugTimeSpeed, DebugTimeOffsetSeconds, TimeLayoutEditorIndex, IsDebugConsoleEnabled,
DebugGitHubAuthKey, ContributorsCache, LastAppVersion, ShowComponentsMigrateTip,
IsMigratedFromv1_4, IsProfileEditorClassInfoSubjectAutoMoveNextEnabled, IsSwapMode,
ShowEchoCaveWhenSettingsPageLoading, SettingsPagesCachePolicy, TrustedProfileIds,
ShowSellingAnnouncement, HasEditModeTutorialShown, ClassPlanEditModeIndex
```
