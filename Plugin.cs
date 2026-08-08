using System.Net;
using System.Net.Http.Json;
using System.Globalization;
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
using ClassIsland.Core.Models.Components;
using ClassIsland.Shared;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.SecAgent.Plugin;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    private HttpApiServer? _server;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        _server = new HttpApiServer();
        services.AddSingleton(_server);
        var controller = new SecAgentController(_server);
        services.AddSingleton(controller);
        services.AddSettingsPage<SecAgentSettingsPage>();
        try { _server.Start(); }
        catch { /* 端口冲突由设置页状态和 SecAgent 连接插件报告。 */ }
        _ = controller.EnsureConnectorInstalledAsync();
        AppBase.Current.AppStopping += (_, _) => _server.Dispose();
    }
}

public sealed class SecAgentController
{
    // SecAgent 测试：允许通过环境变量 CLASSISLAND_CONNECTOR_URL 覆盖服务端口，使测试实例与正式版可并存。
    public static readonly string ServerUrl = Environment.GetEnvironmentVariable("CLASSISLAND_CONNECTOR_URL") ?? "http://127.0.0.1:18789";
    public const string SecAgentServerUrl = "http://127.0.0.1:42189";
    private const string ConnectorId = "classisland-connector";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HttpApiServer _server;
    private int _installing;
    private string _connectorStatus = "正在检查 SecAgent…";

    public SecAgentController(HttpApiServer server) => _server = server;
    public SecAgentRegistrationStatus GetStatus() => new(ServerUrl, _server.IsRunning, _connectorStatus);
    public void Start() => _server.Start();

    public async Task EnsureConnectorInstalledAsync()
    {
        if (Interlocked.Exchange(ref _installing, 1) == 1) return;
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var response = await Client.PostAsJsonAsync(SecAgentServerUrl + "/plugins/install", new { pluginId = ConnectorId });
                    var payload = await response.Content.ReadFromJsonAsync<JsonObject>() ?? new JsonObject();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException(payload["error"]?["message"]?.GetValue<string>() ?? ("HTTP " + (int)response.StatusCode));
                    _connectorStatus = ("已自动安装 SecAgent 联动插件 " + (payload["version"]?.GetValue<string>() ?? "")).Trim();
                    return;
                }
                catch (Exception ex)
                {
                    _connectorStatus = attempt == 3 ? "等待 SecAgent：" + ex.Message : "正在等待 SecAgent…";
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally { Volatile.Write(ref _installing, 0); }
    }
}

public sealed record SecAgentRegistrationStatus(string ServerUrl, bool ServerRunning, string ConnectorStatus)
{
    public bool ServiceAvailable => ServerRunning;
}

public sealed class HttpApiServer : IDisposable
{
    // SecAgent 测试：允许通过环境变量 CLASSISLAND_CONNECTOR_URL 覆盖服务端口，使测试实例与正式版可并存。
    public static readonly string ServerUrl = Environment.GetEnvironmentVariable("CLASSISLAND_CONNECTOR_URL") ?? "http://127.0.0.1:18789";
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _listener.Prefixes.Add(ServerUrl + "/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = ListenAsync(_cts.Token);
    }

    private const string VersionTool = "get_classisland_version_status";
    private const string ListProfilesTool = "list_classisland_profiles";
    private const string ReadProfileTool = "read_classisland_profile";
    private const string GetScheduleTool = "get_classisland_schedule";
    private const string WriteProfileTool = "write_classisland_profile";
    private const string CreateProfileFromTimetableTool = "create_classisland_profile_from_timetable";
    private const string ReadMainConfigTool = "read_classisland_main_config";
    private const string ListMainSettingsTool = "list_classisland_settings";
    private const string UpdateMainConfigTool = "update_classisland_main_config";
    private const string ListComponentConfigsTool = "list_classisland_component_configs";
    private const string ListComponentsTool = "list_classisland_components";
    private const string ReadComponentConfigTool = "read_classisland_component_config";
    private const string WriteComponentConfigTool = "write_classisland_component_config";
    private const string UpdateComponentTool = "update_classisland_component";
    private const string SwapClassesTool = "swap_classisland_classes";
    private const string ChangeClassTool = "change_classisland_class";
    private const string ScheduleDayAsTool = "schedule_classisland_day_as";
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
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                await WriteJsonAsync(context, 200, new JsonObject { ["apiVersion"] = 1, ["name"] = "classisland", ["version"] = AppBase.AppVersion, ["status"] = "ok" }, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/tools")
            {
                await WriteJsonAsync(context, 200, new JsonObject { ["apiVersion"] = 1, ["tools"] = Tools() }, cancellationToken);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path.StartsWith("/tools/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["/tools/".Length..]);
                using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken);
                var result = CallTool(name, document.RootElement);
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["result"] = result }, cancellationToken);
                return;
            }

            await WriteJsonAsync(context, 404, new JsonObject { ["ok"] = false, ["error"] = "Not found" }, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["message"] = ex.Message } }, cancellationToken);
        }
        finally { context.Response.Close(); }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, JsonNode body, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private static JsonArray Tools() => new(
        Tool(CreateProfileFromTimetableTool, "Create a new ClassIsland profile from semantic timetable rows; the server generates and links all GUIDs.", CreateProfileFromTimetableSchema()),
        Tool(VersionTool, "获取当前 ClassIsland 的版本和运行状态。", EmptySchema()),
        Tool(ListProfilesTool, "列出 ClassIsland 档案。", EmptySchema()),
        Tool(ReadProfileTool, "按路径读取 ClassIsland 档案片段。", ProfileReadSchema()),
        Tool(GetScheduleTool, "获取今天或指定日期的课程安排；这是面向日常查询的快捷只读工具。", ScheduleSchema(), hidden: false),
        Tool(WriteProfileTool, "对 ClassIsland 档案执行差量更新。", ProfileWriteSchema()),
        Tool(ReadMainConfigTool, "读取 ClassIsland 主配置。", EmptySchema()),
        Tool(ListMainSettingsTool, "列出 ClassIsland 可持久化主设置的类型、当前值和枚举选项。", MainSettingsListSchema()),
        Tool(UpdateMainConfigTool, "按属性更新 ClassIsland 主配置。参数格式：{\"patch\":{\"字段名\":新值}}，例如 {\"patch\":{\"TimeOffsetSeconds\":-5}} 或 {\"patch\":{\"Scale\":1.2}}。", ObjectSchema(("patch", "object"))),
        Tool(ListComponentConfigsTool, "列出 ClassIsland 主界面组件配置。", EmptySchema()),
        Tool(ListComponentsTool, "列出 ClassIsland 主界面的组件、名称、类型和通用高级设置。", ComponentListSchema()),
        Tool(ReadComponentConfigTool, "读取 ClassIsland 主界面组件配置。", ComponentConfigReadSchema()),
        Tool(WriteComponentConfigTool, "修改 ClassIsland 主界面组件配置。", ComponentConfigWriteSchema()),
        Tool(SwapClassesTool, "交换两节课的科目（支持跨天）。temporary=true 时写入临时层课表并标记临时换课，不影响原课表。", SwapClassesSchema(), hidden: false),
        Tool(ChangeClassTool, "把某一天某一节课临时改成指定科目（占课）。temporary=true 时写入临时层课表。", ChangeClassSchema(), hidden: false),
        Tool(ScheduleDayAsTool, "把指定日期设为使用另一天/另一星期几的课表（调休），通过预定课表生效。", ScheduleDayAsSchema(), hidden: false),
        Tool(UpdateComponentTool, "按组件 ID 修改 ClassIsland 组件的通用高级设置或专属设置。", ComponentUpdateSchema()));

    private static JsonObject Tool(string name, string description, JsonObject schema, bool hidden = true) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["hidden"] = hidden
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

    private static JsonObject SwapClassesSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("date_a", "class_a", "date_b", "class_b"),
        ["properties"] = new JsonObject
        {
            ["date_a"] = new JsonObject { ["type"] = "string", ["description"] = "第一节所在日期，格式 YYYY-MM-DD" },
            ["class_a"] = new JsonObject { ["type"] = "integer", ["description"] = "第一节次序号，1=第1节" },
            ["date_b"] = new JsonObject { ["type"] = "string", ["description"] = "第二节所在日期，格式 YYYY-MM-DD" },
            ["class_b"] = new JsonObject { ["type"] = "integer", ["description"] = "第二节次序号，1=第1节" },
            ["temporary"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否临时换课（写入临时层并标记，不影响原课表）。默认 true" }
        }
    };

    private static JsonObject ChangeClassSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("date", "class", "subject_name"),
        ["properties"] = new JsonObject
        {
            ["date"] = new JsonObject { ["type"] = "string", ["description"] = "日期，格式 YYYY-MM-DD" },
            ["class"] = new JsonObject { ["type"] = "integer", ["description"] = "节次序号，1=第1节" },
            ["subject_name"] = new JsonObject { ["type"] = "string", ["description"] = "科目名称，例如 物理、自习" },
            ["temporary"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否临时占课（写入临时层并标记）。默认 true" }
        }
    };

    private static JsonObject ScheduleDayAsSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("date"),
        ["properties"] = new JsonObject
        {
            ["date"] = new JsonObject { ["type"] = "string", ["description"] = "目标日期，格式 YYYY-MM-DD" },
            ["source_date"] = new JsonObject { ["type"] = "string", ["description"] = "源日期，格式 YYYY-MM-DD；与 source_weekday 二选一" },
            ["source_weekday"] = new JsonObject { ["type"] = "integer", ["description"] = "源星期几：0=周日，1=周一，…，6=周六；与 source_date 二选一" }
        }
    };

    private static JsonObject ScheduleSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["date"] = new JsonObject { ["type"] = "string", ["pattern"] = "^\\d{4}-\\d{2}-\\d{2}$", ["description"] = "可选，格式 YYYY-MM-DD；省略时查询今天。" }
        },
        ["additionalProperties"] = false
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

    private static JsonObject CreateProfileFromTimetableSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["profile_name"] = new JsonObject { ["type"] = "string", ["description"] = "新档案文件名，必须是当前目录下的 .json 文件名。" },
            ["display_name"] = new JsonObject { ["type"] = "string", ["description"] = "档案显示名称；省略时使用文件名。" },
            ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "目标文件已存在时是否覆盖；默认 false。" },
            ["days"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "weekday 使用 CI 的 DayOfWeek 数字：周日 0、周一 1、...、周六 6。",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["weekday"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 6 },
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["rows"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["start"] = new JsonObject { ["type"] = "string", ["description"] = "例如 07:30 或 07:30:00。" },
                                    ["end"] = new JsonObject { ["type"] = "string", ["description"] = "例如 07:50 或 07:50:00。" },
                                    ["subject"] = new JsonObject { ["type"] = "string", ["description"] = "上课时填写科目名称；留空表示课间/活动。" },
                                    ["label"] = new JsonObject { ["type"] = "string", ["description"] = "课间或活动显示名称；上课时可省略。" },
                                    ["type"] = new JsonObject { ["type"] = "string", ["description"] = "可选：lesson/class、break/rest/activity、line。省略时按 subject 是否为空推断。" }
                                },
                                ["required"] = new JsonArray("start", "end"),
                                ["additionalProperties"] = false
                            }
                        }
                    },
                    ["required"] = new JsonArray("weekday", "rows"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("profile_name", "days"),
        ["additionalProperties"] = false
    };

    private static JsonObject ComponentConfigReadSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["config_name"] = new JsonObject { ["type"] = "string", ["description"] = "组件配置名，例如 Default.json" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "点号分隔路径；空字符串返回完整配置。" }
        },
        ["required"] = new JsonArray("config_name", "path"), ["additionalProperties"] = false
    };

    private static JsonObject ComponentConfigWriteSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["config_name"] = new JsonObject { ["type"] = "string", ["description"] = "组件配置名，例如 Default.json" },
            ["patch"] = new JsonObject { ["type"] = "object", ["description"] = "根对象的递归增量更新" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要更新的点号路径" },
            ["value"] = new JsonObject { ["description"] = "path 对应的新值，可以是任意 JSON 值" }
        },
        ["required"] = new JsonArray("config_name"), ["additionalProperties"] = false
    };

    private static JsonObject ComponentListSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["config_name"] = new JsonObject { ["type"] = "string", ["description"] = "可选；默认使用当前激活的组件配置" }
        },
        ["additionalProperties"] = false
    };

    private static JsonObject ComponentUpdateSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["config_name"] = new JsonObject { ["type"] = "string", ["description"] = "可选；默认使用当前激活的组件配置" },
            ["component_id"] = new JsonObject { ["type"] = "string", ["description"] = "组件的 UUID" },
            ["common_patch"] = new JsonObject { ["type"] = "object", ["description"] = "通用高级设置，例如 HideOnRule、Opacity、MarginLeft、IsFixedWidthEnabled 等" },
            ["settings_patch"] = new JsonObject { ["type"] = "object", ["description"] = "组件专属设置，例如课表组件的 CountdownSeconds 等" }
        },
        ["required"] = new JsonArray("component_id"), ["additionalProperties"] = false
    };

    private static JsonObject MainSettingsListSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["include_values"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否返回每个设置的当前值，默认 true。" }
        },
        ["additionalProperties"] = false
    };

    private static JsonNode CallTool(string name, JsonElement arguments)
    {
        return name switch
        {
            VersionTool => VersionStatus(),
            ListProfilesTool => ListProfiles(),
            ReadProfileTool => ReadProfile(arguments),
            GetScheduleTool => GetSchedule(arguments),
            WriteProfileTool => WriteProfile(arguments),
            CreateProfileFromTimetableTool => CreateProfileFromTimetable(arguments),
            ReadMainConfigTool => ReadMainConfig(),
            ListMainSettingsTool => ListMainSettings(arguments),
            UpdateMainConfigTool => UpdateMainConfig(arguments),
            ListComponentConfigsTool => ListComponentConfigs(),
            ListComponentsTool => ListComponents(),
            ReadComponentConfigTool => ReadComponentConfig(arguments),
            WriteComponentConfigTool => WriteComponentConfig(arguments),
            UpdateComponentTool => UpdateComponent(arguments),
            SwapClassesTool => SwapClasses(arguments),
            ChangeClassTool => ChangeClass(arguments),
            ScheduleDayAsTool => ScheduleDayAs(arguments),
            _ => throw new ArgumentException($"未知工具：{name}")
        };
    }

    private static (JsonObject Profile, string Path) LoadProfileRoot()
    {
        var profileService = IAppHost.Host?.Services.GetService<IProfileService>() ?? throw new InvalidOperationException("ClassIsland 当前没有可用的档案服务。");
        var profilePath = ResolveProfilePath(profileService.CurrentProfilePath);
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath)) throw new FileNotFoundException("ClassIsland 当前档案不存在。", profilePath);
        var profile = JsonNode.Parse(File.ReadAllText(profilePath)) as JsonObject ?? throw new InvalidDataException("ClassIsland 当前档案不是有效 JSON 对象。");
        return (profile, profilePath);
    }

    private static DateTime ParseDateArg(JsonElement element, string property)
    {
        var text = OptionalString(element, property);
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ArgumentException($"{property} 必须是 YYYY-MM-DD 格式，例如 2026-08-05。");
        return date;
    }

    private static int? OptionalInt(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && int.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static bool? OptionalBool(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)) return value.GetBoolean();
        return null;
    }

    /// <summary>按日期选出当天生效的课表 key：先查预定（OrderedSchedules），再按星期规则匹配。与 get_classisland_schedule 的选表逻辑一致。</summary>
    private static string? FindSchedulePlanId(JsonObject profile, DateTime targetDate)
    {
        var plans = profile["ClassPlans"] as JsonObject ?? new JsonObject();
        var scheduledPlanId = FindOrderedSchedulePlanId(profile, targetDate);
        if (scheduledPlanId is not null && plans[scheduledPlanId] is JsonObject) return scheduledPlanId;
        var weekday = (int)targetDate.DayOfWeek;
        var candidates = plans
            .Where(item => item.Value is JsonObject plan && IsPlanEnabled(plan) && IsPlanForWeekday(plan, weekday))
            .ToList();
        var selectedGroupId = NodeString(profile["SelectedClassPlanGroupId"]);
        var groupMatch = candidates.FirstOrDefault(item => string.IsNullOrWhiteSpace(selectedGroupId) || string.Equals(NodeString((item.Value as JsonObject)?["AssociatedGroup"]), selectedGroupId, StringComparison.OrdinalIgnoreCase));
        var selected = groupMatch.Value is not null ? groupMatch : candidates.FirstOrDefault();
        return selected.Key;
    }

    private static string? FindPlanIdByWeekday(JsonObject profile, int weekday)
    {
        var plans = profile["ClassPlans"] as JsonObject ?? new JsonObject();
        var match = plans.FirstOrDefault(item => item.Value is JsonObject plan && IsPlanEnabled(plan) && (NodeInt((plan["TimeRule"] as JsonObject)?["WeekDay"]) ?? NodeInt((plan["TimeRule"] as JsonObject)?["Weekday"])) == weekday);
        return match.Key;
    }

    /// <summary>创建/复用指定日期的可写课表。temporary=true 时返回临时层课表（从基础课表复制并标记），不影响原课表。</summary>
    private static JsonObject? GetTargetClassPlanJson(JsonObject profile, DateTime date, bool temporary, out Guid? targetGuid)
    {
        targetGuid = null;
        var basePlanId = FindSchedulePlanId(profile, date);
        if (basePlanId is null || !Guid.TryParse(basePlanId, out var baseGuid) || profile["ClassPlans"]?[basePlanId] is not JsonObject basePlan) return null;
        if (!temporary || (NodeBool(basePlan["IsOverlay"]) ?? false))
        {
            targetGuid = baseGuid;
            return basePlan;
        }
        var orderedId = FindOrderedSchedulePlanId(profile, date);
        if (orderedId is not null && profile["ClassPlans"]?[orderedId] is JsonObject overlayPlan && (NodeBool(overlayPlan["IsOverlay"]) ?? false))
        {
            targetGuid = baseGuid;
            return basePlan;
        }
        var newId = CreateTempClassPlanJson(profile, baseGuid, date);
        targetGuid = baseGuid;
        if (newId is null) return null;
        return profile["ClassPlans"]?[newId.Value.ToString()] as JsonObject;
    }

    /// <summary>复制基础课表为临时层：IsOverlay=true、OverlaySourceId、OverlaySetupTime、追加名称，并写入当天 OrderedSchedules。</summary>
    private static Guid? CreateTempClassPlanJson(JsonObject profile, Guid sourceId, DateTime date)
    {
        if (profile["ClassPlans"] is not JsonObject classPlans || classPlans[sourceId.ToString()] is not JsonObject source) return null;
        var existingOrdered = FindOrderedSchedulePlanId(profile, date);
        if (existingOrdered is not null && classPlans[existingOrdered] is JsonObject ep && (NodeBool(ep["IsOverlay"]) ?? false)) return null;
        var copy = (JsonObject)source.DeepClone();
        var newId = Guid.NewGuid();
        copy["IsOverlay"] = true;
        copy["OverlaySourceId"] = sourceId;
        copy["OverlaySetupTime"] = date;
        copy["Name"] = $"{NodeString(copy["Name"])}（临时层）";
        classPlans[newId.ToString()] = copy;
        var ordered = profile["OrderedSchedules"] as JsonObject ?? (JsonObject)(profile["OrderedSchedules"] = new JsonObject());
        ordered[date.ToString("yyyy-MM-dd'T'00:00:00")] = new JsonObject { ["ClassPlanId"] = newId.ToString(), ["IsActive"] = false };
        profile["IsOverlayClassPlanEnabled"] = true;
        profile["OverlayClassPlanId"] = newId;
        return newId;
    }

    private static string SubjectName(JsonObject profile, JsonObject classInfo)
    {
        var id = NodeString(classInfo["SubjectId"]);
        return NodeString((profile["Subjects"] as JsonObject)?[id]?["Name"]);
    }

    private static JsonObject SwapClasses(JsonElement arguments)
    {
        var dateA = ParseDateArg(arguments, "date_a");
        var dateB = ParseDateArg(arguments, "date_b");
        var classA = OptionalInt(arguments, "class_a") ?? throw new ArgumentException("class_a 必须是节次序号（1=第1节）。");
        var classB = OptionalInt(arguments, "class_b") ?? throw new ArgumentException("class_b 必须是节次序号（1=第1节）。");
        var temporary = OptionalBool(arguments, "temporary") ?? true;
        var (profile, path) = LoadProfileRoot();
        var planA = GetTargetClassPlanJson(profile, dateA, temporary, out _);
        var planB = GetTargetClassPlanJson(profile, dateB, temporary, out _);
        if (planA is null || planB is null) throw new ArgumentException($"找不到 {dateA:yyyy-MM-dd} 或 {dateB:yyyy-MM-dd} 的课表。");
        var classesA = planA["Classes"] as JsonArray;
        var classesB = planB["Classes"] as JsonArray;
        if (classesA is null || classesB is null || classesA.Count < classA || classesB.Count < classB)
            throw new ArgumentException($"节次超出范围：{dateA:yyyy-MM-dd} 有 {classesA?.Count ?? 0} 节，{dateB:yyyy-MM-dd} 有 {classesB?.Count ?? 0} 节。");
        var infoA = classesA[classA - 1] as JsonObject ?? throw new ArgumentException($"第 {classA} 节不是有效课程项。");
        var infoB = classesB[classB - 1] as JsonObject ?? throw new ArgumentException($"第 {classB} 节不是有效课程项。");
        var subjectA = infoA["SubjectId"]?.DeepClone();
        var subjectB = infoB["SubjectId"]?.DeepClone();
        infoA["SubjectId"] = subjectB;
        infoB["SubjectId"] = subjectA;
        if (temporary) { infoA["IsChangedClass"] = true; infoB["IsChangedClass"] = true; }
        WriteJsonAtomically(path, profile);
        return new JsonObject
        {
            ["ok"] = true,
            ["temporary"] = temporary,
            ["swapped"] = new JsonObject
            {
                [$"{dateA:yyyy-MM-dd} 第{classA}节"] = SubjectName(profile, infoA),
                [$"{dateB:yyyy-MM-dd} 第{classB}节"] = SubjectName(profile, infoB)
            },
            ["message"] = $"已交换 {dateA:yyyy-MM-dd} 第{classA}节 与 {dateB:yyyy-MM-dd} 第{classB}节。"
        };
    }

    private static JsonObject ChangeClass(JsonElement arguments)
    {
        var date = ParseDateArg(arguments, "date");
        var classIndex = OptionalInt(arguments, "class") ?? throw new ArgumentException("class 必须是节次序号（1=第1节）。");
        var subjectName = OptionalString(arguments, "subject_name");
        if (string.IsNullOrWhiteSpace(subjectName)) throw new ArgumentException("subject_name 不能为空。");
        var temporary = OptionalBool(arguments, "temporary") ?? true;
        var (profile, path) = LoadProfileRoot();
        var subjects = profile["Subjects"] as JsonObject ?? (JsonObject)(profile["Subjects"] = new JsonObject());
        var subjectId = FindOrCreateSubject(subjects, subjectName, new JsonArray());
        var targetPlan = GetTargetClassPlanJson(profile, date, temporary, out _);
        if (targetPlan is null) throw new ArgumentException($"找不到 {date:yyyy-MM-dd} 的课表。");
        var classes = targetPlan["Classes"] as JsonArray;
        if (classes is null) throw new ArgumentException($"节次超出范围：{date:yyyy-MM-dd} 没有课程列表。");
        if (classes.Count < classIndex) throw new ArgumentException($"节次超出范围：{date:yyyy-MM-dd} 只有 {classes.Count} 节。");
        var classInfo = classes[classIndex - 1] as JsonObject ?? throw new ArgumentException($"第 {classIndex} 节不是有效课程项。");
        classInfo["SubjectId"] = subjectId;
        if (temporary) classInfo["IsChangedClass"] = true;
        WriteJsonAtomically(path, profile);
        return new JsonObject { ["ok"] = true, ["date"] = date.ToString("yyyy-MM-dd"), ["class"] = classIndex, ["subject"] = subjectName, ["temporary"] = temporary };
    }

    private static JsonObject ScheduleDayAs(JsonElement arguments)
    {
        var date = ParseDateArg(arguments, "date");
        var sourceDateText = OptionalString(arguments, "source_date");
        var sourceWeekday = OptionalInt(arguments, "source_weekday");
        var (profile, path) = LoadProfileRoot();
        string? sourcePlanId;
        if (!string.IsNullOrWhiteSpace(sourceDateText))
        {
            if (!DateTime.TryParseExact(sourceDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sourceDate))
                throw new ArgumentException("source_date 必须是 YYYY-MM-DD 格式。");
            sourcePlanId = FindSchedulePlanId(profile, sourceDate);
            if (sourcePlanId is null) throw new ArgumentException($"找不到 {sourceDateText} 使用的课表。");
        }
        else if (sourceWeekday is { } wd)
        {
            if (wd < 0 || wd > 6) throw new ArgumentException("source_weekday 必须是 0（周日）到 6（周六）。");
            sourcePlanId = FindPlanIdByWeekday(profile, wd);
            if (sourcePlanId is null) throw new ArgumentException($"没有星期 {WeekdayName(wd)} 的课表。");
        }
        else throw new ArgumentException("必须提供 source_date 或 source_weekday 之一。");
        var ordered = profile["OrderedSchedules"] as JsonObject ?? (JsonObject)(profile["OrderedSchedules"] = new JsonObject());
        ordered[date.ToString("yyyy-MM-dd'T'00:00:00")] = new JsonObject { ["ClassPlanId"] = sourcePlanId, ["IsActive"] = false };
        WriteJsonAtomically(path, profile);
        return new JsonObject
        {
            ["ok"] = true,
            ["date"] = date.ToString("yyyy-MM-dd"),
            ["class_plan_id"] = sourcePlanId,
            ["class_plan_name"] = NodeString((profile["ClassPlans"] as JsonObject)?[sourcePlanId]?["Name"]),
            ["message"] = $"已把 {date:yyyy-MM-dd}（{WeekdayName((int)date.DayOfWeek)}）的课表设为使用源课表。"
        };
    }

    private static DateTime GetCurrentCiTime()
    {
        try
        {
            return IAppHost.GetService<IExactTimeService>().GetCurrentLocalDateTime();
        }
        catch
        {
            return DateTime.Now;
        }
    }

    private static string? ResolveProfilePath(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return null;
        if (Path.IsPathRooted(profilePath)) return profilePath;
        var candidate = Path.Combine(CommonDirectories.AppRootFolderPath, "Profiles", profilePath);
        return File.Exists(candidate) ? candidate : profilePath;
    }

    private static JsonObject VersionStatus() => new()
    {
        ["appVersion"] = AppBase.AppVersion, ["appVersionLong"] = AppBase.AppVersionLong,
        ["buildType"] = AppBase.Current.BuildType, ["appSubChannel"] = AppBase.Current.AppSubChannel,
        ["platform"] = AppBase.Current.Platform, ["operatingSystem"] = AppBase.Current.OperatingSystem,
        // 返回 CI 内部时钟（可能被 SecAgent 模拟时间覆盖）
        ["localDateTime"] = GetCurrentCiTime().ToString("O"), ["localDate"] = GetCurrentCiTime().ToString("yyyy-MM-dd"),
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

    private static JsonObject GetSchedule(JsonElement arguments)
    {
        var dateText = OptionalString(arguments, "date");
        DateTime targetDate;
        // 优先使用 CI 内部时钟（支持 SecAgent 模拟时间），而非真实系统时间。
        if (string.IsNullOrWhiteSpace(dateText)) targetDate = GetCurrentCiTime().Date;
        else if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out targetDate))
            throw new ArgumentException("date 必须是 YYYY-MM-DD 格式，例如 2026-08-03。");

        var profileService = IAppHost.Host?.Services.GetService<IProfileService>() ?? throw new InvalidOperationException("ClassIsland 当前没有可用的档案服务。");
        var profilePath = ResolveProfilePath(profileService.CurrentProfilePath);
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath)) throw new FileNotFoundException("ClassIsland 当前档案不存在。", profilePath);
        var profile = JsonNode.Parse(File.ReadAllText(profilePath)) as JsonObject ?? throw new InvalidDataException("ClassIsland 当前档案不是有效 JSON 对象。");
        var plans = profile["ClassPlans"] as JsonObject ?? new JsonObject();
        var scheduledPlanId = FindOrderedSchedulePlanId(profile, targetDate);
        var selectedPlanId = scheduledPlanId;
        var selectionSource = scheduledPlanId is null ? "weekday_rule" : "ordered_schedule";

        if (selectedPlanId is null || plans[selectedPlanId] is not JsonObject)
        {
            var weekday = (int)targetDate.DayOfWeek;
            var candidates = plans
                .Where(item => item.Value is JsonObject plan && IsPlanEnabled(plan) && IsPlanForWeekday(plan, weekday))
                .ToList();
            var selectedGroupId = NodeString(profile["SelectedClassPlanGroupId"]);
            var groupMatch = candidates.FirstOrDefault(item => string.IsNullOrWhiteSpace(selectedGroupId) || string.Equals(NodeString((item.Value as JsonObject)?["AssociatedGroup"]), selectedGroupId, StringComparison.OrdinalIgnoreCase));
            var selected = groupMatch.Value is not null ? groupMatch : candidates.FirstOrDefault();
            selectedPlanId = selected.Key;
        }

        var result = new JsonObject
        {
            ["date"] = targetDate.ToString("yyyy-MM-dd"),
            ["weekday"] = WeekdayName((int)targetDate.DayOfWeek),
            ["profile_name"] = Path.GetFileName(profilePath),
            ["profile_display_name"] = NodeString(profile["Name"]),
            // 下面两个字段返回 CI 内部当前时刻（支持模拟时间），方便模型判断“当前”对应哪个时间段。
            ["now"] = GetCurrentCiTime().ToString("O"),
            ["now_local"] = GetCurrentCiTime().ToString("yyyy-MM-dd HH:mm:ss")
        };

        if (selectedPlanId is null || plans[selectedPlanId] is not JsonObject plan)
        {
            result["has_schedule"] = false;
            result["entries"] = new JsonArray();
            result["message"] = $"{targetDate:yyyy-MM-dd} 没有找到匹配的课表。";
            result["available_plans"] = new JsonArray(plans.Select(item => (JsonNode)new JsonObject { ["id"] = item.Key, ["name"] = NodeString((item.Value as JsonObject)?["Name"]) }).ToArray());
            return result;
        }

        var layouts = profile["TimeLayouts"] as JsonObject ?? new JsonObject();
        var subjects = profile["Subjects"] as JsonObject ?? new JsonObject();
        var layoutId = NodeString(plan["TimeLayoutId"]);
        var layout = layouts[layoutId] as JsonObject;
        var timePoints = layout?["Layouts"] as JsonArray ?? new JsonArray();
        var classes = plan["Classes"] as JsonArray ?? new JsonArray();
        var entries = new JsonArray();
        var classIndex = 0;
        foreach (var rawPoint in timePoints)
        {
            if (rawPoint is not JsonObject point) continue;
            var timeType = NodeInt(point["TimeType"]) ?? 0;
            var entry = new JsonObject
            {
                ["type"] = timeType switch { 0 => "lesson", 1 => "break", 2 => "line", 3 => "action", _ => "other" },
                ["start"] = NodeString(point["StartTime"]),
                ["end"] = NodeString(point["EndTime"])
            };
            if (timeType == 0)
            {
                entry["period"] = classIndex + 1;
                var classInfo = classIndex < classes.Count ? classes[classIndex] as JsonObject : null;
                var subjectId = NodeString(classInfo?["SubjectId"]);
                var subject = subjects[subjectId] as JsonObject;
                entry["subject"] = NodeString(subject?["Name"]);
                entry["teacher"] = NodeString(subject?["TeacherName"]);
                entry["subject_id"] = subjectId;
                classIndex++;
            }
            else if (timeType == 1)
            {
                entry["label"] = NodeString(point["BreakName"]);
            }
            entries.Add(entry);
        }

        result["has_schedule"] = true;
        result["selection_source"] = selectionSource;
        result["plan"] = new JsonObject { ["id"] = selectedPlanId, ["name"] = NodeString(plan["Name"]), ["time_layout_id"] = layoutId, ["time_layout_name"] = NodeString(layout?["Name"]) };
        result["lesson_count"] = classIndex;
        result["entries"] = entries;
        return result;
    }

    private static string? FindOrderedSchedulePlanId(JsonObject profile, DateTime targetDate)
    {
        if (profile["OrderedSchedules"] is not JsonObject schedules) return null;
        foreach (var item in schedules)
        {
            if (!ScheduleKeyMatches(item.Key, targetDate) || item.Value is not JsonObject schedule) continue;
            var planId = NodeString(schedule["ClassPlanId"]);
            if (!string.IsNullOrWhiteSpace(planId)) return planId;
        }
        return null;
    }

    private static bool ScheduleKeyMatches(string key, DateTime targetDate)
    {
        var date = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (key.StartsWith(date, StringComparison.OrdinalIgnoreCase)) return true;
        return DateTime.TryParse(key, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) && parsed.Date == targetDate.Date;
    }

    private static bool IsPlanForWeekday(JsonObject plan, int weekday)
    {
        var rule = plan["TimeRule"] as JsonObject;
        return (NodeInt(rule?["WeekDay"]) ?? NodeInt(rule?["Weekday"])) == weekday;
    }

    private static bool IsPlanEnabled(JsonObject plan)
        => NodeBool(plan["IsEnabled"]) ?? true;

    private static string NodeString(JsonNode? node)
    {
        try { return node?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static int? NodeInt(JsonNode? node)
    {
        try { return node?.GetValue<int>(); }
        catch { }
        if (int.TryParse(NodeString(node), out var value)) return value;
        return null;
    }

    private static bool? NodeBool(JsonNode? node)
    {
        try { return node?.GetValue<bool>(); }
        catch { }
        if (bool.TryParse(NodeString(node), out var value)) return value;
        return null;
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
        ValidateProfileCandidate(node);
        ValidateProfileReferences(node);
        WriteJsonAtomically(path, node);
        var runtimeReloaded = TryReloadCurrentProfile(name, out var runtimeReloadError);
        var result = new JsonObject
        {
            ["profile_name"] = name,
            ["written"] = true,
            ["backup"] = path + ".bak",
            ["runtime_reloaded"] = runtimeReloaded
        };
        if (runtimeReloadError is not null) result["runtime_reload_error"] = runtimeReloadError;
        return result;
    }

    private static JsonObject CreateProfileFromTimetable(JsonElement arguments)
    {
        var name = SafeFileName(arguments, "profile_name");
        var displayName = arguments.TryGetProperty("display_name", out var displayElement) && displayElement.ValueKind == JsonValueKind.String
            ? displayElement.GetString()?.Trim()
            : null;
        displayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(name) : displayName;
        var overwrite = arguments.TryGetProperty("overwrite", out var overwriteElement) && overwriteElement.ValueKind == JsonValueKind.True;
        if (!arguments.TryGetProperty("days", out var daysElement) || daysElement.ValueKind != JsonValueKind.Array || daysElement.GetArrayLength() == 0)
            throw new ArgumentException("days 必须是至少包含一天的数组。");

        var profilesPath = Path.Combine(CommonDirectories.AppRootFolderPath, "Profiles");
        var targetPath = Path.Combine(profilesPath, name);
        if (File.Exists(targetPath) && !overwrite) throw new IOException($"档案已存在：{name}；如需覆盖请明确传入 overwrite:true。");

        var days = new List<TimetableDay>();
        var weekdays = new HashSet<int>();
        foreach (var dayElement in daysElement.EnumerateArray())
        {
            if (dayElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("days 中的每一项必须是对象。");
            if (!dayElement.TryGetProperty("weekday", out var weekdayElement) || !weekdayElement.TryGetInt32(out var weekday) || weekday is < 0 or > 6)
                throw new ArgumentException("weekday 必须是 0 到 6 的整数：周日 0、周一 1、...、周六 6。");
            if (!weekdays.Add(weekday)) throw new ArgumentException($"weekday {weekday} 重复；每个星期只能有一个课表。");
            if (!dayElement.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array || rowsElement.GetArrayLength() == 0)
                throw new ArgumentException($"weekday {weekday} 的 rows 不能为空。");

            var dayName = dayElement.TryGetProperty("name", out var dayNameElement) && dayNameElement.ValueKind == JsonValueKind.String
                ? dayNameElement.GetString()?.Trim()
                : null;
            dayName = string.IsNullOrWhiteSpace(dayName) ? WeekdayName(weekday) : dayName;
            var rows = new List<TimetableRow>();
            foreach (var rowElement in rowsElement.EnumerateArray())
            {
                if (rowElement.ValueKind != JsonValueKind.Object) throw new ArgumentException($"weekday {weekday} 的 rows 中存在非对象项。");
                var start = ParseTime(rowElement, "start");
                var end = ParseTime(rowElement, "end");
                if (end <= start) throw new ArgumentException($"weekday {weekday} 存在结束时间不晚于开始时间的行：{start:hh\\:mm\\:ss}-{end:hh\\:mm\\:ss}。");
                var subject = OptionalString(rowElement, "subject");
                var label = OptionalString(rowElement, "label");
                var kind = OptionalString(rowElement, "type").ToLowerInvariant();
                var timeType = kind switch
                {
                    "line" or "separator" or "分割线" => 2,
                    "lesson" or "class" or "上课" => 0,
                    "break" or "rest" or "activity" or "课间" or "休息" or "活动" => 1,
                    "" => string.IsNullOrWhiteSpace(subject) ? 1 : 0,
                    _ => throw new ArgumentException($"不支持的 timetable row type：{kind}；请使用 lesson、break 或 line。")
                };
                if (timeType == 0 && string.IsNullOrWhiteSpace(subject)) throw new ArgumentException($"weekday {weekday} 有上课行缺少 subject。");
                if (timeType == 1 && string.IsNullOrWhiteSpace(label)) label = "课间";
                rows.Add(new TimetableRow(start, end, timeType, subject, label));
            }
            days.Add(new TimetableDay(weekday, dayName!, rows));
        }

        var templatePath = ResolveProfilePath(IAppHost.Host?.Services.GetService<IProfileService>()?.CurrentProfilePath);
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath)) templatePath = Path.Combine(profilesPath, "Default.json");
        var root = File.Exists(templatePath)
            ? JsonNode.Parse(File.ReadAllText(templatePath)) as JsonObject ?? throw new InvalidDataException("当前 CI 档案不是有效 JSON 对象。")
            : new JsonObject();

        var defaultGroupId = "acaf4ef0-e261-4262-b941-34ea93cb4369";
        var globalGroupId = "00000000-0000-0000-0000-000000000000";
        var groups = root["ClassPlanGroups"] as JsonObject ?? new JsonObject();
        root["ClassPlanGroups"] = groups;
        groups[defaultGroupId] ??= new JsonObject { ["Name"] = "默认", ["IsGlobal"] = false };
        groups[globalGroupId] ??= new JsonObject { ["Name"] = "全局课表群", ["IsGlobal"] = true };
        var subjects = root["Subjects"] as JsonObject ?? new JsonObject();
        root["Subjects"] = subjects;

        var layouts = new JsonObject();
        var plans = new JsonObject();
        var layoutBySignature = new Dictionary<string, (string Id, string Name)>();
        var createdLayoutInfo = new JsonArray();
        var createdPlanInfo = new JsonArray();
        var createdSubjectInfo = new JsonArray();
        foreach (var day in days.OrderBy(x => x.Weekday))
        {
            var signature = string.Join("|", day.Rows.Select(x => $"{x.Start:c};{x.End:c};{x.TimeType}"));
            if (!layoutBySignature.TryGetValue(signature, out var layoutInfo))
            {
                layoutInfo = (Guid.NewGuid().ToString(), $"{day.Name}时间表");
                layoutBySignature[signature] = layoutInfo;
                var layoutItems = new JsonArray();
                foreach (var row in day.Rows)
                {
                    var item = new JsonObject
                    {
                        ["StartTime"] = row.Start.ToString(@"hh\:mm\:ss"),
                        ["EndTime"] = row.End.ToString(@"hh\:mm\:ss"),
                        ["TimeType"] = row.TimeType
                    };
                    if (row.TimeType == 0) item["DefaultClassId"] = Guid.Empty.ToString();
                    if (row.TimeType == 1) item["BreakName"] = row.Label;
                    layoutItems.Add(item);
                }
                layouts[layoutInfo.Id] = new JsonObject { ["Name"] = layoutInfo.Name, ["Layouts"] = layoutItems };
                createdLayoutInfo.Add(new JsonObject { ["id"] = layoutInfo.Id, ["name"] = layoutInfo.Name, ["weekday"] = day.Weekday });
            }

            var classes = new JsonArray();
            foreach (var row in day.Rows.Where(x => x.TimeType == 0))
            {
                var subjectId = FindOrCreateSubject(subjects, row.Subject, createdSubjectInfo);
                classes.Add(new JsonObject { ["SubjectId"] = subjectId });
            }
            var planId = Guid.NewGuid().ToString();
            plans[planId] = new JsonObject
            {
                ["Name"] = $"{day.Name}课表",
                ["TimeLayoutId"] = layoutInfo.Id,
                ["AssociatedGroup"] = defaultGroupId,
                ["IsEnabled"] = true,
                ["TimeRule"] = new JsonObject { ["WeekDay"] = day.Weekday, ["WeekCountDiv"] = 0, ["WeekCountDivTotal"] = 2 },
                ["Classes"] = classes
            };
            createdPlanInfo.Add(new JsonObject { ["id"] = planId, ["name"] = $"{day.Name}课表", ["weekday"] = day.Weekday, ["time_layout_id"] = layoutInfo.Id, ["class_count"] = classes.Count });
        }

        root["Name"] = displayName;
        root["TimeLayouts"] = layouts;
        root["ClassPlans"] = plans;
        root["OrderedSchedules"] = new JsonObject();
        root["Id"] = Guid.NewGuid().ToString();
        root["SelectedClassPlanGroupId"] = defaultGroupId;
        root["IsOverlayClassPlanEnabled"] = false;
        root["OverlayClassPlanId"] = null;
        root["TempClassPlanId"] = null;
        root["IsTempClassPlanGroupEnabled"] = false;
        root["TempClassPlanGroupId"] = null;
        root["IsActive"] = false;
        ValidateProfileCandidate(root);
        ValidateProfileReferences(root);
        WriteJsonAtomically(targetPath, root);
        var runtimeReloaded = TryReloadCurrentProfile(name, out var runtimeReloadError);

        var result = new JsonObject
        {
            ["profile_name"] = name,
            ["display_name"] = displayName,
            ["written"] = true,
            ["overwritten"] = overwrite,
            ["runtime_reloaded"] = runtimeReloaded,
            ["activated"] = runtimeReloaded,
            ["time_layouts"] = createdLayoutInfo,
            ["class_plans"] = createdPlanInfo,
            ["subjects_created"] = createdSubjectInfo,
            ["next_step"] = runtimeReloaded ? "档案已重新加载。" : "档案已创建，但不是当前运行档案；请在 CI 的档案选择器中切换或重启 CI 后选择它。"
        };
        if (runtimeReloadError is not null) result["runtime_reload_error"] = runtimeReloadError;
        return result;
    }

    private static string OptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    private static TimeSpan ParseTime(JsonElement element, string property)
    {
        var text = OptionalString(element, property);
        if (!TimeSpan.TryParse(text, out var value) || value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
            throw new ArgumentException($"{property} 必须是有效的当天时间，例如 07:30 或 07:30:00：{text}");
        return value;
    }

    private static string WeekdayName(int weekday) => weekday switch
    {
        0 => "周日", 1 => "周一", 2 => "周二", 3 => "周三", 4 => "周四", 5 => "周五", 6 => "周六", _ => $"星期{weekday}"
    };

    private static string FindOrCreateSubject(JsonObject subjects, string subjectName, JsonArray createdSubjectInfo)
    {
        var existing = subjects.FirstOrDefault(x => x.Value is JsonObject subject && string.Equals(subject["Name"]?.GetValue<string>(), subjectName, StringComparison.OrdinalIgnoreCase));
        if (existing.Key is not null) return existing.Key;
        var id = Guid.NewGuid().ToString();
        subjects[id] = new JsonObject { ["Name"] = subjectName, ["Initial"] = subjectName[..Math.Min(1, subjectName.Length)], ["TeacherName"] = "", ["IsOutDoor"] = false };
        createdSubjectInfo.Add(new JsonObject { ["id"] = id, ["name"] = subjectName });
        return id;
    }

    private sealed record TimetableDay(int Weekday, string Name, List<TimetableRow> Rows);
    private sealed record TimetableRow(TimeSpan Start, TimeSpan End, int TimeType, string Subject, string Label);

    private static void ValidateProfileCandidate(JsonNode node)
    {
        if (node is not JsonObject profile) throw new InvalidDataException("档案根节点必须是 JSON 对象。");

        var timeLayoutCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (profile["TimeLayouts"] is JsonObject timeLayouts)
        {
            foreach (var item in timeLayouts)
            {
                if (item.Value is not JsonObject layout) throw new InvalidDataException($"时间表 {item.Key} 必须是对象。");
                if (layout["Layouts"] is not JsonArray layouts) throw new InvalidDataException($"时间表 {item.Key} 缺少 Layouts 数组。");
                var classCount = 0;
                foreach (var rawPoint in layouts)
                {
                    if (rawPoint is not JsonObject point) throw new InvalidDataException($"时间表 {item.Key} 的时间点必须是对象。");
                    var timeType = point["TimeType"]?.GetValue<int>() ?? throw new InvalidDataException($"时间表 {item.Key} 的时间点缺少 TimeType。");
                    if (timeType == 0) classCount++;
                    if (point.ContainsKey("DefaultClassId") && point["DefaultClassId"] is null)
                        throw new InvalidDataException($"时间表 {item.Key} 的 DefaultClassId 不能是 null；请使用空 GUID 字符串或省略该字段。");
                }
                timeLayoutCounts[item.Key] = classCount;
            }
        }

        if (profile["ClassPlans"] is JsonObject classPlans)
        {
            foreach (var item in classPlans)
            {
                if (item.Value is not JsonObject plan) throw new InvalidDataException($"课表 {item.Key} 必须是对象。");
                var timeLayoutId = plan["TimeLayoutId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(timeLayoutId)) throw new InvalidDataException($"课表 {item.Key} 缺少 TimeLayoutId。");
                if (!Guid.TryParse(timeLayoutId, out _)) throw new InvalidDataException($"课表 {item.Key} 的 TimeLayoutId 不是有效 GUID。");
                if (plan["Classes"] is not JsonArray classes) throw new InvalidDataException($"课表 {item.Key} 缺少 Classes 数组。");
                var matchingLayout = timeLayoutCounts.FirstOrDefault(x => string.Equals(x.Key, timeLayoutId, StringComparison.OrdinalIgnoreCase));
                if (matchingLayout.Key is not null && classes.Count != matchingLayout.Value)
                    throw new InvalidDataException($"课表 {item.Key} 的 Classes 数量为 {classes.Count}，但对应时间表只有 {matchingLayout.Value} 个上课时间点；课间不应放入 Classes。");
                foreach (var rawClass in classes)
                {
                    if (rawClass is not JsonObject classInfo) throw new InvalidDataException($"课表 {item.Key} 的课程项必须是对象。");
                    var subjectId = classInfo["SubjectId"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(subjectId) && !Guid.TryParse(subjectId, out _)) throw new InvalidDataException($"课表 {item.Key} 的 SubjectId 不是有效 GUID。");
                }
            }
        }

        if (profile["OrderedSchedules"] is JsonObject schedules)
        {
            foreach (var item in schedules)
            {
                if (item.Value is not JsonObject schedule) throw new InvalidDataException($"预定课表 {item.Key} 必须是对象，格式应为 {{\"ClassPlanId\":\"课表 GUID\"}}。");
                var classPlanId = schedule["ClassPlanId"]?.GetValue<string>();
                if (!Guid.TryParse(classPlanId, out _)) throw new InvalidDataException($"预定课表 {item.Key} 的 ClassPlanId 不是有效 GUID。");
            }
        }
    }

    private static void ValidateProfileReferences(JsonNode node)
    {
        if (node is not JsonObject profile) throw new InvalidDataException("档案根节点必须是 JSON 对象。");
        var layouts = profile["TimeLayouts"] as JsonObject ?? new JsonObject();
        var subjects = profile["Subjects"] as JsonObject ?? new JsonObject();
        var groups = profile["ClassPlanGroups"] as JsonObject ?? new JsonObject();
        var plans = profile["ClassPlans"] as JsonObject ?? new JsonObject();
        foreach (var item in plans)
        {
            if (item.Value is not JsonObject plan) continue;
            var layoutId = plan["TimeLayoutId"]?.GetValue<string>();
            if (layoutId is null || !layouts.ContainsKey(layoutId)) throw new InvalidDataException($"课表 {item.Key} 引用的 TimeLayoutId 不存在：{layoutId}");
            var groupId = plan["AssociatedGroup"]?.GetValue<string>();
            if (groupId is not null && !groups.ContainsKey(groupId)) throw new InvalidDataException($"课表 {item.Key} 引用的 AssociatedGroup 不存在：{groupId}");
            if (plan["Classes"] is JsonArray classes)
                foreach (var rawClass in classes)
                {
                    var subjectId = (rawClass as JsonObject)?["SubjectId"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(subjectId) && !subjects.ContainsKey(subjectId)) throw new InvalidDataException($"课表 {item.Key} 引用的 SubjectId 不存在：{subjectId}");
                }
        }
        if (profile["OrderedSchedules"] is JsonObject schedules)
            foreach (var schedule in schedules)
            {
                var planId = (schedule.Value as JsonObject)?["ClassPlanId"]?.GetValue<string>();
                if (planId is null || !plans.ContainsKey(planId)) throw new InvalidDataException($"预定课表 {schedule.Key} 引用的 ClassPlanId 不存在：{planId}");
            }
    }

    private static bool TryReloadCurrentProfile(string profileName, out string? error)
    {
        error = null;
        try
        {
            var service = IAppHost.Host?.Services.GetService<IProfileService>();
            if (service is null || !string.Equals(Path.GetFileName(service.CurrentProfilePath), profileName, StringComparison.OrdinalIgnoreCase)) return false;
            var method = service.GetType().GetMethod("LoadProfileAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method?.Invoke(service, null) is not Task task) throw new InvalidOperationException("ClassIsland 档案服务不支持重新加载当前档案。");
            task.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
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

    private static JsonObject ListMainSettings(JsonElement arguments)
    {
        var assembly = AppBase.Current.GetType().Assembly;
        var settingsType = assembly.GetType("ClassIsland.Models.Settings") ?? throw new InvalidOperationException("无法找到 ClassIsland Settings 类型。");
        var serviceType = assembly.GetType("ClassIsland.Services.SettingsService");
        var service = serviceType is null ? null : IAppHost.Host?.Services.GetService(serviceType);
        var runtimeSettings = serviceType?.GetProperty("Settings")?.GetValue(service);
        var includeValues = !arguments.TryGetProperty("include_values", out var includeElement) || includeElement.ValueKind != JsonValueKind.False;
        var properties = new JsonArray();
        foreach (var property in settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(x => x.GetIndexParameters().Length == 0 && x.CanRead)
                     .OrderBy(x => x.Name))
        {
            if (property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null) continue;
            var item = new JsonObject
            {
                ["name"] = property.Name,
                ["json_name"] = property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name ?? property.Name,
                ["type"] = property.PropertyType.FullName ?? property.PropertyType.Name,
                ["writable"] = property.CanWrite
            };
            if (property.PropertyType.IsEnum)
            {
                var enumValues = new JsonArray();
                foreach (var enumName in property.PropertyType.GetEnumNames()) enumValues.Add(enumName);
                item["enum"] = enumValues;
            }
            if (includeValues && runtimeSettings is not null)
            {
                try { item["current"] = JsonSerializer.SerializeToNode(property.GetValue(runtimeSettings), property.PropertyType); }
                catch { }
            }
            properties.Add(item);
        }
        return new JsonObject { ["settings_type"] = settingsType.FullName, ["settings"] = properties };
    }

    private static string ComponentConfigDirectoryPath => Path.Combine(CommonDirectories.AppRootFolderPath, "Config", "ComponentLayouts");

    private static JsonObject ListComponentConfigs()
    {
        var configs = Directory.Exists(ComponentConfigDirectoryPath)
            ? Directory.EnumerateFiles(ComponentConfigDirectoryPath, "*.json").OrderBy(x => x).Select(x => (JsonNode)new JsonObject { ["name"] = Path.GetFileName(x) }).ToArray()
            : Array.Empty<JsonNode>();
        return new JsonObject
        {
            ["config_directory"] = "Config/ComponentLayouts",
            ["active_config"] = ReadCurrentComponentConfig(),
            ["configs"] = new JsonArray(configs)
        };
    }

    private static JsonObject ListComponents(JsonElement arguments = default)
    {
        var configName = ResolveComponentConfigName(arguments);
        var configPath = Path.Combine(ComponentConfigDirectoryPath, configName);
        if (!File.Exists(configPath)) throw new FileNotFoundException("组件配置不存在。", configName);
        var root = JsonNode.Parse(File.ReadAllText(configPath)) ?? throw new InvalidDataException("组件配置不是有效 JSON。");
        var runtimeById = ReadRuntimeComponentMetadata().Where(x => x["component_id"] is not null).ToDictionary(x => x["component_id"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        var components = new JsonArray();
        foreach (var (path, node) in EnumerateComponentNodes(root, ""))
        {
            var id = node["Id"]?.GetValue<string>() ?? "";
            var runtime = runtimeById.TryGetValue(id, out var matched) ? matched : null;
            var common = new JsonObject();
            foreach (var property in node.AsObject())
            {
                if (property.Key is "Id" or "NameCache" or "Settings" or "Children") continue;
                common[property.Key] = property.Value?.DeepClone();
            }
            var item = new JsonObject
            {
                ["path"] = path,
                ["component_id"] = id,
                ["name"] = runtime?["name"]?.DeepClone() ?? node["NameCache"]?.DeepClone() ?? "",
                ["type"] = runtime?["type"]?.DeepClone() ?? node["Settings"]?.GetType().Name ?? "Unknown",
                ["runtime_type"] = runtime?["runtime_type"]?.DeepClone(),
                ["settings_type"] = runtime?["settings_type"]?.DeepClone(),
                ["common_settings"] = common,
                ["settings"] = node["Settings"]?.DeepClone()
            };
            if (runtime?["settings_fields"] is { } fields) item["settings_fields"] = fields.DeepClone();
            components.Add(item);
        }
        return new JsonObject { ["config_name"] = configName, ["active_config"] = ReadCurrentComponentConfig(), ["components"] = components };
    }

    private static JsonObject UpdateComponent(JsonElement arguments)
    {
        var configName = ResolveComponentConfigName(arguments);
        if (!arguments.TryGetProperty("component_id", out var idElement) || idElement.ValueKind != JsonValueKind.String) throw new ArgumentException("component_id 必须是字符串。");
        var id = idElement.GetString()!;
        var commonPatch = arguments.TryGetProperty("common_patch", out var commonElement) ? commonElement : default;
        var settingsPatch = arguments.TryGetProperty("settings_patch", out var settingsElement) ? settingsElement : default;
        if ((commonPatch.ValueKind != JsonValueKind.Object) && (settingsPatch.ValueKind != JsonValueKind.Object)) throw new ArgumentException("请提供 common_patch 或 settings_patch。");

        var configPath = Path.Combine(ComponentConfigDirectoryPath, configName);
        if (!File.Exists(configPath)) throw new FileNotFoundException("组件配置不存在。", configName);
        var root = JsonNode.Parse(File.ReadAllText(configPath)) ?? throw new InvalidDataException("组件配置不是有效 JSON。");
        var match = EnumerateComponentNodes(root, "").FirstOrDefault(x => string.Equals(x.Node["Id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
        if (match.Node is null) throw new KeyNotFoundException($"找不到组件 ID：{id}");

        var updated = new JsonArray();
        if (commonPatch.ValueKind == JsonValueKind.Object)
        {
            var patch = JsonNode.Parse(commonPatch.GetRawText())!.AsObject();
            foreach (var property in patch)
            {
                if (property.Key is "Id" or "NameCache" or "Settings" or "Children") throw new ArgumentException($"不能通过 common_patch 修改组件结构字段：{property.Key}");
                var actual = match.Node.AsObject().FirstOrDefault(x => string.Equals(x.Key, property.Key, StringComparison.OrdinalIgnoreCase));
                if (actual.Key is null) throw new KeyNotFoundException($"组件通用设置不存在：{property.Key}");
                match.Node[actual.Key] = property.Value?.DeepClone();
                updated.Add($"{match.Path}.{actual.Key}");
            }
        }
        if (settingsPatch.ValueKind == JsonValueKind.Object)
        {
            if (match.Node["Settings"] is not JsonObject settings) throw new InvalidOperationException("该组件没有专属 Settings；请使用 common_patch 修改通用高级设置。");
            var patch = JsonNode.Parse(settingsPatch.GetRawText())!.AsObject();
            MergeObject(settings, patch);
            foreach (var property in patch.Select(x => x.Key)) updated.Add($"{match.Path}.Settings.{property}");
        }
        WriteJsonAtomically(configPath, root);
        var refreshed = TryRefreshComponentConfigs(out var refreshError);
        var result = new JsonObject
        {
            ["config_name"] = configName,
            ["component_id"] = id,
            ["path"] = match.Path,
            ["written"] = true,
            ["updated_paths"] = updated,
            ["backup"] = configPath + ".bak",
            ["runtime_refreshed"] = refreshed
        };
        if (refreshError is not null) result["runtime_refresh_error"] = refreshError;
        return result;
    }

    private static string ResolveComponentConfigName(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("config_name", out var value) && value.ValueKind == JsonValueKind.String) return SafeJsonFileName(value.GetString()!);
        var active = ReadCurrentComponentConfig();
        return string.IsNullOrWhiteSpace(active) ? "Default.json" : SafeJsonFileName(active.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? active : active + ".json");
    }

    private static IEnumerable<(string Path, JsonObject Node)> EnumerateComponentNodes(JsonNode root, string path)
    {
        if (root is not JsonObject obj) yield break;
        if (obj["Id"] is JsonValue) yield return (path, obj);
        if (obj["Lines"] is JsonArray lines)
            for (var index = 0; index < lines.Count; index++)
                if (lines[index] is { } line) foreach (var item in EnumerateComponentNodes(line, $"Lines.{index}")) yield return item;
        if (obj["Children"] is JsonArray children)
            for (var index = 0; index < children.Count; index++)
                if (children[index] is { } child) foreach (var item in EnumerateComponentNodes(child, $"{path}.Children.{index}".TrimStart('.'))) yield return item;
    }

    private static List<JsonObject> ReadRuntimeComponentMetadata()
    {
        var output = new List<JsonObject>();
        try
        {
            var service = IAppHost.Host?.Services.GetService<IComponentsService>();
            if (service is null) return output;
            for (var lineIndex = 0; lineIndex < service.CurrentComponents.Lines.Count; lineIndex++)
            {
                var line = service.CurrentComponents.Lines[lineIndex];
                for (var childIndex = 0; childIndex < line.Children.Count; childIndex++)
                    AppendRuntimeComponentMetadata(line.Children[childIndex], $"Lines.{lineIndex}.Children.{childIndex}", output);
            }
        }
        catch { }
        return output;
    }

    private static void AppendRuntimeComponentMetadata(ComponentSettings settings, string path, ICollection<JsonObject> output)
    {
        var info = settings.AssociatedComponentInfo;
        var fields = new JsonArray();
        if (info.SettingsType is not null)
        {
            foreach (var property in info.SettingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(x => x.CanRead && x.GetIndexParameters().Length == 0)
                         .OrderBy(x => x.Name))
            {
                var field = new JsonObject
                {
                    ["name"] = property.Name,
                    ["type"] = property.PropertyType.FullName ?? property.PropertyType.Name
                };
                if (property.PropertyType.IsEnum)
                {
                    var enumValues = new JsonArray();
                    foreach (var enumName in property.PropertyType.GetEnumNames()) enumValues.Add(enumName);
                    field["enum"] = enumValues;
                }
                try
                {
                    var value = settings.Settings?.GetType().GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(settings.Settings);
                    field["current"] = JsonSerializer.SerializeToNode(value, property.PropertyType);
                }
                catch { }
                fields.Add(field);
            }
        }

        output.Add(new JsonObject
        {
            ["path"] = path,
            ["component_id"] = settings.Id,
            ["name"] = info.Name,
            ["description"] = info.Description,
            ["type"] = info.ComponentType?.FullName,
            ["runtime_type"] = info.ComponentType?.AssemblyQualifiedName,
            ["settings_type"] = info.SettingsType?.FullName,
            ["source"] = info.ComponentType?.Assembly.GetName().Name,
            ["is_container"] = info.IsComponentContainer,
            ["settings_fields"] = fields
        });

        if (settings.Children is not null)
            for (var index = 0; index < settings.Children.Count; index++)
                AppendRuntimeComponentMetadata(settings.Children[index], $"{path}.Children.{index}", output);
    }

    private static object? GetPropertyValue(object? instance, string name) => instance?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(instance);
    private static string? GetStringProperty(object? instance, string name) => GetPropertyValue(instance, name)?.ToString();

    private static JsonObject ReadComponentConfig(JsonElement arguments)
    {
        var name = SafeJsonFileName(arguments, "config_name");
        var pathExpression = arguments.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()!
            : throw new ArgumentException("path 必须是字符串；使用空字符串才请求完整组件配置。");
        var path = Path.Combine(ComponentConfigDirectoryPath, name);
        if (!File.Exists(path)) throw new FileNotFoundException("组件配置不存在。", name);
        var config = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("组件配置不是有效 JSON。");
        return new JsonObject { ["config_name"] = name, ["path"] = pathExpression, ["active_config"] = ReadCurrentComponentConfig(), ["value"] = SelectPath(config, pathExpression) };
    }

    private static JsonObject WriteComponentConfig(JsonElement arguments)
    {
        var name = SafeJsonFileName(arguments, "config_name");
        var pathExpression = arguments.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String ? pathElement.GetString() : null;
        var hasPatch = arguments.TryGetProperty("patch", out var patchElement);
        var hasValue = arguments.TryGetProperty("value", out var valueElement);
        if (string.IsNullOrWhiteSpace(pathExpression) && (!hasPatch || patchElement.ValueKind != JsonValueKind.Object)) throw new ArgumentException("请提供 patch 对象，或同时提供非空 path 和 value。");
        if (!string.IsNullOrWhiteSpace(pathExpression) && !hasValue) throw new ArgumentException("使用 path 时必须提供 value。");

        var path = Path.Combine(ComponentConfigDirectoryPath, name);
        if (!File.Exists(path)) throw new FileNotFoundException("组件配置不存在。", name);
        var config = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("组件配置不是有效 JSON。");
        if (string.IsNullOrWhiteSpace(pathExpression)) MergeObject(config, JsonNode.Parse(patchElement.GetRawText())!.AsObject());
        else SetPath(config, pathExpression!, JsonNode.Parse(valueElement.GetRawText()));
        WriteJsonAtomically(path, config);

        var refreshed = TryRefreshComponentConfigs(out var refreshError);
        var result = new JsonObject
        {
            ["config_name"] = name,
            ["written"] = true,
            ["backup"] = path + ".bak",
            ["runtime_refreshed"] = refreshed
        };
        if (refreshError is not null) result["runtime_refresh_error"] = refreshError;
        return result;
    }

    private static string? ReadCurrentComponentConfig()
    {
        try
        {
            if (!File.Exists(MainConfigPath)) return null;
            var settings = JsonNode.Parse(File.ReadAllText(MainConfigPath)) as JsonObject;
            return settings?["CurrentComponentConfig"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private static bool TryRefreshComponentConfigs(out string? error)
    {
        try
        {
            var service = IAppHost.Host?.Services.GetService<IComponentsService>();
            if (service is null) { error = "ClassIsland 组件服务当前不可用；配置已写入磁盘，重启后会加载。"; return false; }
            service.RefreshConfigs();
            error = null;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static JsonObject UpdateMainConfig(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("patch 必须是 JSON 对象。正确格式：{\"patch\":{\"字段名\":新值}}，例如 {\"patch\":{\"Scale\":1.2}}。");
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
        saveMethod?.Invoke(service, new object[] { "SecAgent HTTP API 更新主配置" });
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

    private static string SafeJsonFileName(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{property} 必须是字符串。");
        return SafeJsonFileName(value.GetString()!);
    }

    private static string SafeJsonFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("组件配置名必须是当前目录下的 .json 文件名。");
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

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }
}
