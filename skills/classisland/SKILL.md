---
name: classisland
description: 执行ClassIsland操作，如换课、修改课表、修改CI设置（ClassIsland简称CI）
---

# ClassIsland 操作

ClassIsland（简称 CI）插件提供一个本机 MCP 服务。MCP 工具全部设置为隐藏，不能依赖工具列表发现；执行 CI 操作前必须阅读本文档，并按下面的名称和参数调用工具。

MCP 服务地址：`http://127.0.0.1:18789/mcp`

## 工具调用约定

服务名是 `classisland`。工具名称如下：

- `classisland__get_classisland_version_status`：查询 CI 当前版本和运行状态。
- `classisland__list_classisland_profiles`：列出 CI 的档案文件。
- `classisland__read_classisland_profile`：按路径读取档案片段或字段。
- `classisland__write_classisland_profile`：对档案执行差量更新。
- `classisland__read_classisland_main_config`：读取 CI 的主配置 `Settings.json`。
- `classisland__update_classisland_main_config`：按属性名更新主配置中的字段。

这些工具虽然隐藏，但仍然可以通过 MCP `tools/call` 调用。不要因为 `tools/list` 中看不到它们就改用猜测的文件名或自行访问文件系统。

修改主配置前必须同时阅读本 Skill 目录下的 [`SETTINGS_REFERENCE.md`](SETTINGS_REFERENCE.md)。该文件说明字段的数据类型、单位、枚举注意事项和常用字段含义。

## 查询版本状态

调用 `classisland__get_classisland_version_status`，参数必须是 `{}`。
返回字段包括 `appVersion`、`appVersionLong`、`buildType`、`appSubChannel`、`platform`、`operatingSystem` 和 `isDevelopmentBuild`。

## 操作档案

先调用 `classisland__list_classisland_profiles`，参数 `{}`，从返回的 `profiles` 数组中选择 `name`。档案名只能使用文件名，不要传路径；当前档案由 `current` 标记。

读取档案时必须提供 `path`，使用点号分隔的字段路径；例如只读取课表：

```json
{"profile_name":"Default.json","path":"ClassPlans"}
```

读取嵌套字段：

```json
{
  "profile_name": "Default.json",
  "path": "ClassPlans.字典键"
}
```

只有明确需要完整档案时才使用空路径 `"path":""`。写入使用根对象差量：

```json
{"profile_name":"Default.json","patch":{"ClassPlans":{"字典键":{"Name":"新名称"}}}}
```

对象会递归合并，数组会整体替换。也可以只替换某个字段：

```json
{"profile_name":"Default.json","path":"ClassPlans.字典键.Name","value":"新名称"}
```

写入前先读取将要修改的路径，只修改用户明确要求的字段。插件会校验 JSON、创建 `.bak` 备份，并以原子方式替换文件；不要删除未知字段、字典键或数组元素。写入文件后，CI 通常需要切换档案或重启后重新加载；插件不会伪造运行时已刷新状态。

## 操作主配置

调用 `classisland__read_classisland_main_config`，参数 `{}`，返回 `Settings.json`。主配置通常比档案小；如果只需要一个设置，优先从返回对象中读取对应属性，不要为了修改一个字段反复读取或提交整份配置。

先读取 `SETTINGS_REFERENCE.md`，再使用属性名到新值的映射更新主配置：

```json
{
  "patch": {
    "IsMainWindowVisible": true,
    "Scale": 1.0
  }
}
```

`patch` 只能更新 ClassIsland `Settings` 类型已有的公开属性，未知属性或无法解析为对应类型的值会被拒绝。数组、对象等复杂值会整体替换；`SettingsOverlay` 必须原样保留。更新会先按 CI 的 `Settings` 类型校验，然后通过运行中的 `SettingsService.Settings` 属性更新并调用 CI 的保存逻辑，效果等同于在设置页修改；返回值中的 `applied_runtime` 为 `true` 表示已立即应用。若 CI 运行时服务不可用，才会回退到创建 `Settings.json.bak` 并原子写入。部分只在启动时读取的设置仍可能需要重启 CI。参数格式一次正确后直接调用，不要在工具返回错误后猜测另一个包装层级。

所有档案和主配置写操作都属于真实状态变更。只有在用户明确要求后才调用写入工具；修改课表前先读取当前档案并说明将修改的字段。
