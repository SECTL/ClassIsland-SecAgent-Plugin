# ClassIsland SecAgent 联动 Plugin

ClassIsland 侧插件，为 SecAgent 连接插件提供普通 HTTP JSON 服务。它不实现 MCP，也不修改 SecAgent 工作区配置。

插件启动后监听 `127.0.0.1:18789`，提供 `/health`、`/tools` 和 `/tools/{name}` 接口，提供 ClassIsland 版本、档案、主配置、组件目录和主界面组件配置工具。组件目录会结合运行时组件服务返回名称、类型、专属 Settings 字段和通用高级设置；组件配置位于 `Config/ComponentLayouts`，写入后会尝试调用 ClassIsland 组件服务刷新，并自动生成 `.bak` 备份。

其中 `get_classisland_schedule` 是默认展示的只读快捷工具，省略日期时查询今天，也可以传入 `YYYY-MM-DD` 查询指定日期的课程、课间和科目安排。

## IslandCaller 随机点名

插件内置 4 个 IslandCaller 联动工具（需要先在 ClassIsland 中安装并启用 [IslandCaller](https://github.com/HickoryTrail/IslandCaller) 随机点名插件）：

- `call_island_caller`（默认展示）：触发 IslandCaller 随机点名，可选 `count`（1-20，默认 1）与 `profile_id`（指定名单），返回本次被点到的学生名单。IslandCaller 未安装/未就绪时返回说明而不会出错。
- `list_island_caller_profiles`（隐藏）：列出 IslandCaller 已配置名单的 GUID、名称、是否为默认/当前名单与成员数。
- `read_island_caller_roster`（隐藏）：读取指定名单的原始 CSV 与结构化成员。
- `write_island_caller_roster`（隐藏）：覆盖写入指定名单的成员；若写入的是当前激活名单会自动热重载，立即生效。

实现不依赖 IslandCaller 程序集：两个插件运行在同一个 ClassIsland 进程中，通过 DI 容器反射解析 IslandCaller 服务，并在 Avalonia UI 线程上触发点名窗口。

SecAgent 端请安装 `ClassIsland-SecAgent-Connector`；连接插件会自动重试并在服务可用时注册工具和 Skill。
