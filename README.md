# ClassIsland SecAgent

本项目以 GNU General Public License v3.0（GPL-3.0）发布，详见 [LICENSE](LICENSE)。

ClassIsland 插件，为 SecAgent 提供当前 ClassIsland 版本状态 MCP 工具和对应 Skill。

## 行为

插件启动后默认查找 `~/SecAgentWorkspace`，也支持通过 `SECAGENT_WORKSPACE` 指定工作目录。
当 Skill、MCP 描述文件或 `secagent.yaml` 中的启用配置缺失时，会弹窗请求用户同意；只有用户同意后才会写入文件并启动本机 HTTP MCP 服务。

MCP 服务只绑定 `127.0.0.1:18789`。所有 MCP 工具默认隐藏，由 Skill 文档说明调用契约。

## 开发

```bash
dotnet build
```

也可以使用 pnpm 编译并启动本机 ClassIsland：

```bash
pnpm dev
```

启动脚本会读取 `.env` 中的 `CLASSISLAND_EXECUTABLE`，并使用 `-epp` 将当前插件的构建输出目录加载到 ClassIsland。

调试运行需要在启动配置中提供 ClassIsland 本体路径，模板已生成 `Properties/launchSettings.json`。
