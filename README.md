# ClassIsland SecAgent 联动 Plugin

ClassIsland 侧插件，为 SecAgent 连接插件提供普通 HTTP JSON 服务。它不实现 MCP，也不修改 SecAgent 工作区配置。

插件启动后监听 `127.0.0.1:18789`，提供 `/health`、`/tools` 和 `/tools/{name}` 接口，提供 ClassIsland 版本、档案、主配置、组件目录和主界面组件配置工具。组件目录会结合运行时组件服务返回名称、类型、专属 Settings 字段和通用高级设置；组件配置位于 `Config/ComponentLayouts`，写入后会尝试调用 ClassIsland 组件服务刷新，并自动生成 `.bak` 备份。

其中 `get_classisland_schedule` 是默认展示的只读快捷工具，省略日期时查询今天，也可以传入 `YYYY-MM-DD` 查询指定日期的课程、课间和科目安排。

SecAgent 端请安装 `ClassIsland-SecAgent-Connector`；连接插件会自动重试并在服务可用时注册工具和 Skill。

## 宿主兼容性

插件清单声明 `apiVersion: 2.0.0.0`，因此兼容 **ClassIsland 2.0.0.0 及以上**宿主：

- ClassIsland 从 2.0.0.0 开始仅拒绝 `apiVersion` 低于 `2.0.0.0` 的插件（`ClassIsland/Services/PluginService.cs`：`apiVersion < new Version(2,0,0,0)`），2.0.0.0 及之后的 2.0.x / 2.1.x 宿主均可加载本插件。
- 本插件用到的宿主 API（`AppBase`、`IAppHost`、`IProfileService`、`IComponentsService`、`IExactTimeService`、`CommonDirectories`、`AddSettingsPage<T>` 等）在 ClassIsland `2.0.0.0` 标签中均已存在，SDK 构建提交 `27e93bb`（`1.7.106.2`）也是 `2.0.0.0` 的祖先提交，不存在使用后加入 API 的问题。
- 更早的 1.x 宿主使用旧版插件体系，不支持本插件。
