# ClassIsland HTTP Plugin

ClassIsland 侧插件，为 SecAgent 连接插件提供普通 HTTP JSON 服务。它不实现 MCP，也不修改 SecAgent 工作区配置。

插件启动后监听 `127.0.0.1:18789`，提供 `/health`、`/tools` 和 `/tools/{name}` 接口，提供 ClassIsland 版本、档案、主配置、组件目录和主界面组件配置工具。组件目录会结合运行时组件服务返回名称、类型、专属 Settings 字段和通用高级设置；组件配置位于 `Config/ComponentLayouts`，写入后会尝试调用 ClassIsland 组件服务刷新，并自动生成 `.bak` 备份。

SecAgent 端请安装 `ClassIsland-SecAgent-Connector`；连接插件会自动重试并在服务可用时注册工具和 Skill。
