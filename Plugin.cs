using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClassIsland.SecAgent.Plugin;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    private SecAgentController? _controller;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        _controller = new SecAgentController();
        services.AddSingleton(_controller);
        services.AddSettingsPage<SecAgentSettingsPage>();
        AppBase.Current.AppStarted += (_, _) => _ = _controller.InitializeAsync();
        AppBase.Current.AppStopping += (_, _) => _controller.Dispose();
    }
}

public sealed class SecAgentController : IDisposable
{
    public const string ServerName = "classisland";
    public const string ServerUrl = "http://127.0.0.1:18789/mcp";
    private SecAgentBridge? _bridge;

    public SecAgentRegistrationStatus GetStatus()
    {
        var workspace = SecAgentWorkspace.TryFind();
        if (workspace is null) return new(null, false, false, false);
        return new(workspace.Root, File.Exists(workspace.SkillFilePath), workspace.ConfigHasEnabledServer, _bridge?.IsRunning == true);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var workspace = SecAgentWorkspace.TryFind();
            if (workspace is null)
            {
                await CommonTaskDialogs.ShowDialog(
                    "SecAgent 未找到工作目录",
                    "未找到 ~/SecAgentWorkspace/secagent.yaml，因此不会修改任何文件。\n\n" +
                    "如需注册，请先初始化 SecAgent，或设置 SECAGENT_WORKSPACE 环境变量。");
                return;
            }

            if (!workspace.NeedsRegistration)
            {
                StartBridge();
                return;
            }

            var result = await new TaskDialog
            {
                Title = "连接 SecAgent",
                Content = $"ClassIsland-SecAgent 插件希望向以下 SecAgent 工作目录注册一个 Skill 和 MCP 服务：\n\n" +
                          $"{workspace.Root}\n\n" +
                          "注册内容：\n" +
                          "• Skill：ClassIsland 操作、档案与主配置工具说明\n" +
                          $"• MCP：{ServerName}（{ServerUrl}，仅本机可访问）\n\n" +
                          "是否同意写入配置并启用该服务？",
                XamlRoot = AppBase.Current.GetRootWindow(),
                Buttons =
                {
                    new TaskDialogButton("取消", false),
                    new TaskDialogButton("同意并注册", true) { IsDefault = true }
                }
            }.ShowAsync();
            if (Equals(result, true)) await RegisterAsync();
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("SecAgent 注册失败", ex.Message);
        }
    }

    public async Task RegisterAsync()
    {
        var workspace = SecAgentWorkspace.TryFind();
        if (workspace is null) throw new InvalidOperationException("未找到 SecAgent 工作目录：~/SecAgentWorkspace/secagent.yaml");
        workspace.Register();
        StartBridge();
        await Task.CompletedTask;
    }

    private void StartBridge()
    {
        _bridge ??= new SecAgentBridge();
        _bridge.Start();
    }

    public void Dispose() => _bridge?.Dispose();
}

public sealed record SecAgentRegistrationStatus(string? Workspace, bool SkillRegistered, bool McpRegistered, bool ServerRunning)
{
    public bool WorkspaceFound => Workspace is not null;
}

internal sealed class SecAgentWorkspace
{
    private const string SkillDirectoryName = "classisland";
    private const string McpFileName = "classisland-server.json";
    private readonly string _configPath;

    private SecAgentWorkspace(string root)
    {
        Root = root;
        _configPath = Path.Combine(root, "secagent.yaml");
    }

    public string Root { get; }
    public string SkillFilePath => Path.Combine(Root, "skills", SkillDirectoryName, "SKILL.md");
    private string SettingsReferencePath => Path.Combine(Root, "skills", SkillDirectoryName, "SETTINGS_REFERENCE.md");
    private string McpPath => Path.Combine(Root, "mcp", McpFileName);
    public bool ConfigHasEnabledServer => HasEnabledServer();
    public bool NeedsRegistration => !File.Exists(SkillFilePath) || !File.Exists(SettingsReferencePath) || !File.Exists(McpPath) || !ConfigHasEnabledServer || !SkillFilesAreCurrent();

    public static SecAgentWorkspace? TryFind()
    {
        var configured = Environment.GetEnvironmentVariable("SECAGENT_WORKSPACE");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SecAgentWorkspace")
            : Path.GetFullPath(configured);
        return File.Exists(Path.Combine(root, "secagent.yaml")) ? new SecAgentWorkspace(root) : null;
    }

    public void Register()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SkillFilePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(McpPath)!);
        File.WriteAllText(SkillFilePath, LoadSkillContent(), new UTF8Encoding(false));
        File.WriteAllText(SettingsReferencePath, LoadSkillReference(), new UTF8Encoding(false));
        File.WriteAllText(McpPath, McpJson, new UTF8Encoding(false));
        UpdateConfig();
    }

    private bool SkillFilesAreCurrent()
    {
        try
        {
            return string.Equals(File.ReadAllText(SkillFilePath, Encoding.UTF8), LoadSkillContent(), StringComparison.Ordinal) &&
                   string.Equals(File.ReadAllText(SettingsReferencePath, Encoding.UTF8), LoadSkillReference(), StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    private static string LoadSkillContent()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        var candidates = new[]
        {
            assemblyDirectory is null ? null : Path.Combine(assemblyDirectory, "skills", "classisland", "SKILL.md"),
            Path.Combine(AppContext.BaseDirectory, "skills", "classisland", "SKILL.md")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) throw new FileNotFoundException("插件内置 Skill 文件不存在。请确保发布插件时保留 skills/classisland/SKILL.md。", string.Join("; ", candidates));
        var content = File.ReadAllText(path, Encoding.UTF8);
        if (!content.StartsWith("---\nname: classisland\ndescription: 执行ClassIsland操作，如换课、修改课表、修改CI设置（ClassIsland简称CI）\n---", StringComparison.Ordinal))
            throw new InvalidDataException("插件内置 SKILL.md 的 frontmatter 不符合约定。\n" + path);
        return content;
    }

    private static string LoadSkillReference()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        var candidates = new[]
        {
            assemblyDirectory is null ? null : Path.Combine(assemblyDirectory, "skills", "classisland", "SETTINGS_REFERENCE.md"),
            Path.Combine(AppContext.BaseDirectory, "skills", "classisland", "SETTINGS_REFERENCE.md")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) throw new FileNotFoundException("插件内置 ClassIsland 设置参考文件不存在。", string.Join("; ", candidates));
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private bool HasEnabledServer()
    {
        try
        {
            var root = LoadRoot(File.ReadAllText(_configPath));
            var servers = FindMapping(root, "mcp", "servers");
            var classIsland = FindMapping(servers, "classisland");
            return Scalar(classIsland, "url") == SecAgentBridge.ServerUrl &&
                   string.Equals(Scalar(classIsland, "enabled"), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (YamlException)
        {
            return false;
        }
    }

    private void UpdateConfig()
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(File.ReadAllText(_configPath))) yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("SecAgent 配置必须是一个 YAML 对象。");

        var mcp = GetOrCreateMapping(root, "mcp");
        var servers = GetOrCreateMapping(mcp, "servers");
        var classIsland = GetOrCreateMapping(servers, "classisland");
        SetScalar(classIsland, "transport", "http");
        SetScalar(classIsland, "url", SecAgentBridge.ServerUrl);
        SetScalar(classIsland, "enabled", "true");

        var serialized = Serialize(yaml);
        _ = LoadRoot(serialized); // 重新解析序列化结果，确认写入内容仍是合法 YAML。

        var backupPath = _configPath + ".bak";
        File.Copy(_configPath, backupPath, true);
        var tempPath = _configPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, serialized, new UTF8Encoding(false));
            File.Move(tempPath, _configPath, true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static YamlMappingNode LoadRoot(string content)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(content);
        yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new YamlException("SecAgent 配置必须是一个 YAML 对象。");
        return root;
    }

    private static YamlMappingNode FindMapping(YamlMappingNode parent, params string[] path)
    {
        var current = parent;
        foreach (var key in path)
        {
            if (!current.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlMappingNode mapping)
                throw new YamlException($"找不到 YAML 对象节点：{key}");
            current = mapping;
        }
        return current;
    }

    private static YamlMappingNode GetOrCreateMapping(YamlMappingNode parent, string key)
    {
        var keyNode = new YamlScalarNode(key);
        if (parent.Children.TryGetValue(keyNode, out var node))
        {
            if (node is YamlMappingNode mapping) return mapping;
            throw new YamlException($"YAML 节点不是对象：{key}");
        }
        var created = new YamlMappingNode();
        parent.Add(keyNode, created);
        return created;
    }

    private static string? Scalar(YamlMappingNode parent, string key)
    {
        return parent.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;
    }

    private static void SetScalar(YamlMappingNode parent, string key, string value)
    {
        parent.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);
    }

    private static string Serialize(YamlStream yaml)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(writer, false);
        return writer.ToString();
    }

    private const string McpJson = """
{
  "name": "classisland",
  "transport": "http",
  "url": "http://127.0.0.1:18789/mcp",
  "tools": [
    {
      "name": "get_classisland_version_status",
      "hidden": true
    },
    {
      "name": "list_classisland_profiles",
      "hidden": true
    },
    {
      "name": "read_classisland_profile",
      "hidden": true
    },
    {
      "name": "write_classisland_profile",
      "hidden": true
    },
    {
      "name": "read_classisland_main_config",
      "hidden": true
    },
    {
      "name": "update_classisland_main_config",
      "hidden": true
    }
  ]
}
""";
}

internal sealed class SecAgentBridge : IDisposable
{
    public const string ServerUrl = "http://127.0.0.1:18789/mcp";
    private const string VersionTool = "get_classisland_version_status";
    private const string ListProfilesTool = "list_classisland_profiles";
    private const string ReadProfileTool = "read_classisland_profile";
    private const string WriteProfileTool = "write_classisland_profile";
    private const string ReadMainConfigTool = "read_classisland_main_config";
    private const string UpdateMainConfigTool = "update_classisland_main_config";
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _listener.Prefixes.Add("http://127.0.0.1:18789/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = ListenAsync(_cts.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(context, cancellationToken);
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            var id = root.TryGetProperty("id", out var idElement) ? JsonNode.Parse(idElement.GetRawText()) : null;
            JsonNode response = method switch
            {
                "tools/list" => ToolsList(id),
                "tools/call" => ToolsCall(root, id),
                "initialize" => Initialize(id),
                _ => Error(id, -32601, $"不支持的方法：{method}")
            };
            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
        catch (Exception ex)
        {
            var bytes = Encoding.UTF8.GetBytes(Error(null, -32603, ex.Message).ToJsonString());
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
        finally { context.Response.Close(); }
    }

    private static JsonNode ToolsList(JsonNode? id) => RpcResult(id, new JsonObject
    {
        ["tools"] = new JsonArray(
            Tool(VersionTool, "获取当前 ClassIsland 的版本和运行状态。", EmptySchema()),
            Tool(ListProfilesTool, "列出 ClassIsland 档案。", EmptySchema()),
            Tool(ReadProfileTool, "按路径读取 ClassIsland 档案片段。", ProfileReadSchema()),
            Tool(WriteProfileTool, "对 ClassIsland 档案执行差量更新。", ProfileWriteSchema()),
            Tool(ReadMainConfigTool, "读取 ClassIsland 主配置。", EmptySchema()),
            Tool(UpdateMainConfigTool, "按属性更新 ClassIsland 主配置。", ObjectSchema(("patch", "object"))))
    });

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["hidden"] = true
    };

    private static JsonObject EmptySchema() => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false
    };

    private static JsonObject ObjectSchema(params (string Name, string Type)[] fields)
    {
        var properties = new JsonObject();
        foreach (var (name, type) in fields) properties[name] = new JsonObject { ["type"] = type };
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(fields.Select(x => (JsonNode)x.Name).ToArray()), ["additionalProperties"] = false };
    }

    private static JsonObject ProfileReadSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["profile_name"] = new JsonObject { ["type"] = "string" }, ["path"] = new JsonObject { ["type"] = "string", ["description"] = "点号分隔路径，例如 ClassPlans 或 ClassPlans.字典键；空字符串返回完整 JSON。" } },
        ["required"] = new JsonArray("profile_name", "path"), ["additionalProperties"] = false
    };

    private static JsonObject ProfileWriteSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["profile_name"] = new JsonObject { ["type"] = "string" },
            ["patch"] = new JsonObject { ["type"] = "object", ["description"] = "根对象的递归差量；对象递归合并，数组整体替换。" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "指定字段路径时，用 value 替换该字段。" },
            ["value"] = new JsonObject { ["description"] = "path 指定字段的新值。" }
        },
        ["required"] = new JsonArray("profile_name"), ["additionalProperties"] = false
    };

    private static JsonNode ToolsCall(JsonElement root, JsonNode? id)
    {
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        var name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var n) ? n.GetString() : null;
        var arguments = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var a) ? a : default;
        try
        {
            var result = name switch
            {
                VersionTool => VersionStatus(),
                ListProfilesTool => ListProfiles(),
                ReadProfileTool => ReadProfile(arguments),
                WriteProfileTool => WriteProfile(arguments),
                ReadMainConfigTool => ReadMainConfig(),
                UpdateMainConfigTool => UpdateMainConfig(arguments),
                _ => throw new ArgumentException($"未知工具：{name}")
            };
            return RpcResult(id, new JsonObject { ["structuredContent"] = result, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.ToJsonString() }) });
        }
        catch (Exception ex) { return Error(id, -32602, ex.Message); }
    }

    private static JsonObject VersionStatus() => new()
    {
        ["appVersion"] = AppBase.AppVersion, ["appVersionLong"] = AppBase.AppVersionLong,
        ["buildType"] = AppBase.Current.BuildType, ["appSubChannel"] = AppBase.Current.AppSubChannel,
        ["platform"] = AppBase.Current.Platform, ["operatingSystem"] = AppBase.Current.OperatingSystem,
        ["isDevelopmentBuild"] = AppBase.Current.IsDevelopmentBuild
    };

    private static JsonObject ListProfiles()
    {
        var profilePath = Path.Combine(CommonDirectories.AppRootFolderPath, "Profiles");
        var profiles = Directory.Exists(profilePath)
            ? Directory.EnumerateFiles(profilePath, "*.json").OrderBy(x => x).Select(x => new JsonObject { ["name"] = Path.GetFileName(x) }).ToArray()
            : Array.Empty<JsonObject>();
        return new JsonObject { ["profiles"] = new JsonArray(profiles) };
    }

    private static JsonObject ReadProfile(JsonElement arguments)
    {
        var name = SafeFileName(arguments, "profile_name");
        if (!arguments.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String) throw new ArgumentException("path 必须是字符串；使用空字符串才请求完整档案。");
        var pathExpression = pathElement.GetString()!;
        var path = Path.Combine(CommonDirectories.AppRootFolderPath, "Profiles", name);
        if (!File.Exists(path)) throw new FileNotFoundException("档案不存在。", name);
        var profile = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("档案不是有效 JSON。");
        return new JsonObject { ["profile_name"] = name, ["path"] = pathExpression, ["value"] = SelectPath(profile, pathExpression) };
    }

    private static JsonObject WriteProfile(JsonElement arguments)
    {
        var name = SafeFileName(arguments, "profile_name");
        var pathExpression = arguments.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var hasPatch = arguments.TryGetProperty("patch", out var patchElement);
        var hasValue = arguments.TryGetProperty("value", out var valueElement);
        if (string.IsNullOrWhiteSpace(pathExpression) && (!hasPatch || patchElement.ValueKind != JsonValueKind.Object)) throw new ArgumentException("请提供 patch 对象，或同时提供非空 path 和 value。");
        if (!string.IsNullOrWhiteSpace(pathExpression) && !hasValue) throw new ArgumentException("使用 path 时必须提供 value。");
        var path = Path.Combine(CommonDirectories.AppRootFolderPath, "Profiles", name);
        var node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("档案不是有效 JSON。");
        if (!string.IsNullOrWhiteSpace(pathExpression)) SetPath(node, pathExpression!, JsonNode.Parse(valueElement.GetRawText()));
        else MergeObject(node, JsonNode.Parse(patchElement.GetRawText())!.AsObject());
        WriteJsonAtomically(path, node);
        return new JsonObject { ["profile_name"] = name, ["written"] = true, ["backup"] = path + ".bak" };
    }

    private static JsonNode SelectPath(JsonNode root, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return root;
        var current = root;
        foreach (var segment in expression.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current switch
            {
                JsonObject obj when obj[segment] is { } child => child,
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count && array[index] is { } child => child,
                _ => throw new KeyNotFoundException($"档案路径不存在：{expression}")
            };
        }
        return current.DeepClone();
    }

    private static void SetPath(JsonNode root, string expression, JsonNode? value)
    {
        var parts = expression.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("path 不能为空。");
        var parent = root;
        foreach (var segment in parts[..^1])
        {
            parent = parent switch
            {
                JsonObject obj when obj[segment] is { } child => child,
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count && array[index] is { } child => child,
                _ => throw new KeyNotFoundException($"档案路径不存在：{expression}")
            };
        }
        var last = parts[^1];
        if (parent is JsonObject parentObject) parentObject[last] = value;
        else if (parent is JsonArray array && int.TryParse(last, out var index) && index >= 0 && index < array.Count) array[index] = value;
        else throw new KeyNotFoundException($"档案路径不存在：{expression}");
    }

    private static void MergeObject(JsonNode target, JsonObject patch)
    {
        if (target is not JsonObject targetObject) throw new InvalidDataException("档案根节点必须是 JSON 对象。");
        foreach (var item in patch)
        {
            if (item.Value is JsonObject childPatch && targetObject[item.Key] is JsonObject childTarget) MergeObject(childTarget, childPatch);
            else targetObject[item.Key] = item.Value?.DeepClone();
        }
    }

    private static string MainConfigPath => Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json");

    private static JsonObject ReadMainConfig()
    {
        if (!File.Exists(MainConfigPath)) throw new FileNotFoundException("ClassIsland 主配置不存在。", MainConfigPath);
        return new JsonObject { ["config_file"] = "Settings.json", ["settings"] = JsonNode.Parse(File.ReadAllText(MainConfigPath)) ?? throw new InvalidDataException("主配置不是有效 JSON。") };
    }

    private static JsonObject UpdateMainConfig(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("patch 必须是 JSON 对象。");
        if (!File.Exists(MainConfigPath)) throw new FileNotFoundException("ClassIsland 主配置不存在。", MainConfigPath);
        var settingsNode = (JsonNode.Parse(File.ReadAllText(MainConfigPath)) as JsonObject) ?? throw new InvalidDataException("主配置不是 JSON 对象。");
        foreach (var item in patch.EnumerateObject())
        {
            var existing = settingsNode.FirstOrDefault(x => string.Equals(x.Key, item.Name, StringComparison.OrdinalIgnoreCase));
            if (existing.Key is null) throw new ArgumentException($"主配置不存在属性：{item.Name}");
            settingsNode[existing.Key] = JsonNode.Parse(item.Value.GetRawText());
        }
        ValidateMainConfig(settingsNode);
        var appliedRuntime = TryApplyRuntimeSettings(settingsNode, patch);
        if (!appliedRuntime) WriteJsonAtomically(MainConfigPath, settingsNode);
        return new JsonObject { ["written"] = true, ["applied_runtime"] = appliedRuntime, ["config_file"] = "Settings.json", ["updated_properties"] = new JsonArray(patch.EnumerateObject().Select(x => (JsonNode)x.Name).ToArray()) };
    }

    private static bool TryApplyRuntimeSettings(JsonObject settings, JsonElement patch)
    {
        var assembly = AppBase.Current.GetType().Assembly;
        var serviceType = assembly.GetType("ClassIsland.Services.SettingsService");
        var settingsType = assembly.GetType("ClassIsland.Models.Settings");
        var service = serviceType is null ? null : IAppHost.Host?.Services.GetService(serviceType);
        var runtimeSettings = serviceType?.GetProperty("Settings")?.GetValue(service);
        if (serviceType is null || settingsType is null || service is null || runtimeSettings is null) return false;

        foreach (var item in patch.EnumerateObject())
        {
            var property = settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => string.Equals(x.Name, item.Name, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(x.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name, item.Name, StringComparison.OrdinalIgnoreCase));
            if (property is null || !property.CanWrite) throw new ArgumentException($"Settings 属性不可写：{item.Name}");
            var value = JsonSerializer.Deserialize(item.Value.GetRawText(), property.PropertyType);
            property.SetValue(runtimeSettings, value);
        }

        var saveMethod = serviceType.GetMethod("SaveSettings", new[] { typeof(string) });
        saveMethod?.Invoke(service, new object[] { "SecAgent MCP 更新主配置" });
        return true;
    }

    private static void ValidateMainConfig(JsonObject settings)
    {
        var settingsType = AppBase.Current.GetType().Assembly.GetType("ClassIsland.Models.Settings");
        if (settingsType is null) throw new InvalidOperationException("无法找到 ClassIsland Settings 类型，拒绝写入主配置。");
        var properties = settingsType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .SelectMany(x => new[] { x.Name, x.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name })
            .Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        foreach (var property in settings.Select(x => x.Key))
            if (!properties.Contains(property)) throw new ArgumentException($"主配置包含未知 Settings 属性：{property}");
        _ = JsonSerializer.Deserialize(settings.ToJsonString(), settingsType) ?? throw new InvalidDataException("更新后的主配置无法解析为 ClassIsland Settings。");
    }

    private static string SafeFileName(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{property} 必须是字符串。");
        var name = value.GetString()!;
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("档案名必须是当前目录下的 .json 文件名。");
        return name;
    }

    private static void WriteJsonAtomically(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static JsonNode Initialize(JsonNode? id) => RpcResult(id, new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "classisland", ["version"] = AppBase.AppVersion } });
    private static JsonNode RpcResult(JsonNode? id, JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    private static JsonNode Error(JsonNode? id, int code, string message) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public void Dispose() { _cts?.Cancel(); _listener.Stop(); _listener.Close(); _cts?.Dispose(); _cts = null; }
}
