using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using ClassIsland.Shared;

namespace ClassIsland.SecAgent.Plugin;

/// <summary>
/// IslandCaller 随机点名工具（反射桥接）。
/// IslandCaller 与 SecAgent 联动插件运行在同一个 ClassIsland 进程中，并把服务注册进
/// ClassIsland 的应用 DI 容器。本类按类型名解析 IslandCaller 的服务并驱动随机点名，
/// 不引用 IslandCaller 程序集：IslandCaller 未安装 / 未就绪 / 调用失败时一律返回结构化
/// 结果（ok=false + message），不向上抛异常，便于 SecAgent 前置规则渲染友好文案。
/// </summary>
internal static class IslandCallerTools
{
    // IslandCaller 类型全名（运行期扫描以 IslandCaller 开头的已加载程序集解析，避免编译期依赖）。
    private const string IslandCallerAssemblyPrefix = "IslandCaller";
    private const string CallerServiceTypeName = "IslandCaller.Services.IslandCallerService.IslandCallerService";
    private const string HistoryServiceTypeName = "IslandCaller.Services.HistoryService";
    private const string ProfileServiceTypeName = "IslandCaller.Services.ProfileService";
    private const string ProfileRuntimeServiceTypeName = "IslandCaller.Services.ProfileRuntimeService";
    private const string StatusServiceTypeName = "IslandCaller.Services.Status";
    private const string SettingsTypeName = "IslandCaller.Models.Settings";

    private static readonly string ProfileDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IslandCaller", "Profile");

    private static string RosterFilePath(Guid id) => Path.Combine(ProfileDirectoryPath, id.ToString() + ".csv");

    // ---------------- 反射基础设施 ----------------

    private static Type? FindIslandCallerType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name;
            if (string.IsNullOrEmpty(assemblyName) || !assemblyName.StartsWith(IslandCallerAssemblyPrefix, StringComparison.Ordinal))
                continue;
            if (assembly.GetType(fullName, throwOnError: false) is { } type)
                return type;
        }
        return null;
    }

    /// <summary>按类型名解析 IslandCaller 在 ClassIsland DI 容器中注册的单例服务。</summary>
    private static object? ResolveIslandCallerService(string fullTypeName)
    {
        var type = FindIslandCallerType(fullTypeName);
        if (type is null) return null;
        try
        {
            return IAppHost.Host?.Services.GetService(type);
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadProperty(object? instance, string propertyName)
    {
        if (instance is null) return null;
        try
        {
            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadStringProperty(object? instance, string propertyName) => ReadProperty(instance, propertyName) as string;

    private static bool ReadBoolProperty(object? instance, string propertyName) => ReadProperty(instance, propertyName) is true;

    private static Guid? ReadGuidProperty(object? instance, string propertyName)
    {
        try
        {
            return ReadProperty(instance, propertyName) is Guid value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>IslandCaller Settings.Instance（静态属性，IslandCaller 启动加载后可用）。</summary>
    private static object? GetSettingsInstance()
    {
        var settingsType = FindIslandCallerType(SettingsTypeName);
        if (settingsType is null) return null;
        try
        {
            return settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetProfileSetting() => ReadProperty(GetSettingsInstance(), "Profile");

    private static System.Collections.IDictionary? GetProfileList() => ReadProperty(GetProfileSetting(), "ProfileList") as System.Collections.IDictionary;

    private static string? GetProfileName(System.Collections.IDictionary? profileList, Guid id)
    {
        if (profileList is null) return null;
        foreach (System.Collections.DictionaryEntry entry in profileList)
        {
            if (entry.Key is Guid key && key == id) return entry.Value?.ToString();
        }
        return null;
    }

    private static bool ProfileExists(System.Collections.IDictionary? profileList, Guid id) => GetProfileName(profileList, id) is not null;

    // ---------------- 状态与结果构建 ----------------

    private static JsonObject NotInstalledResult()
    {
        return new JsonObject
        {
            ["ok"] = false,
            ["installed"] = false,
            ["message"] = "未检测到 IslandCaller 插件。请在 ClassIsland 中安装并启用 IslandCaller（随机点名）后重试，并在 IslandCaller 设置页配置名单。"
        };
    }

    private static JsonObject FailureResult(string message)
    {
        return new JsonObject { ["ok"] = false, ["message"] = message };
    }

    private static bool TryGetReadyStatus(out object? statusService, out string? notReadyMessage)
    {
        notReadyMessage = null;
        statusService = ResolveIslandCallerService(StatusServiceTypeName);
        if (statusService is null) return false;
        if (!ReadBoolProperty(statusService, "IsPluginReady"))
        {
            notReadyMessage = "IslandCaller 已安装但尚未就绪，暂不能点名。请检查 IslandCaller 设置页是否完成初始化、是否正处于禁止点名的时段（例如课间禁点、上一点名尚未结束），稍后再试。";
            return false;
        }
        return true;
    }

    // ---------------- 工具：call_island_caller ----------------

    internal static JsonObject CallIslandCaller(JsonElement arguments)
    {
        var count = 1;
        if (arguments.TryGetProperty("count", out var countElement) && countElement.ValueKind == JsonValueKind.Number)
        {
            try
            {
                count = countElement.GetInt32();
            }
            catch (FormatException)
            {
                return FailureResult("count 必须是 1-20 之间的整数。");
            }
        }
        count = Math.Clamp(count, 1, 20);

        var profileIdText = OptionalString(arguments, "profile_id");
        Guid? profileId = null;
        if (!string.IsNullOrWhiteSpace(profileIdText))
        {
            if (!Guid.TryParse(profileIdText, out var parsedId)) return FailureResult("profile_id 必须是 IslandCaller 名单的 GUID（来自 list_island_caller_profiles）。");
            profileId = parsedId;
        }

        try
        {
            // 1. IslandCaller 是否已安装（类型与 DI 服务均可解析）
            var callerService = ResolveIslandCallerService(CallerServiceTypeName);
            var historyService = ResolveIslandCallerService(HistoryServiceTypeName);
            var statusService = ResolveIslandCallerService(StatusServiceTypeName);
            if (callerService is null || historyService is null || statusService is null) return NotInstalledResult();

            // 2. 点名就绪门控（与 IslandCaller Status.IsPluginReady 一致）
            if (!ReadBoolProperty(statusService, "IsPluginReady"))
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["installed"] = true,
                    ["ready"] = false,
                    ["message"] = "IslandCaller 已安装但尚未就绪，暂不能点名。请检查 IslandCaller 设置页是否完成初始化、是否正处于禁止点名的时段（例如课间禁点、上一点名尚未结束），稍后再试。"
                };
            }

            // 3. 可选：先切换到指定名单（IslandCaller 的 ProfileRuntimeService.EnsureLoaded）
            string? profileName = null;
            if (profileId is { } targetId)
            {
                var profileList = GetProfileList();
                profileName = GetProfileName(profileList, targetId);
                if (!ProfileExists(profileList, targetId))
                    return FailureResult($"IslandCaller 名单不存在：{targetId}。请先通过 list_island_caller_profiles 获取可用的名单 ID。");
                if (!EnsureProfileLoaded(targetId, out var ensureError))
                    return FailureResult($"切换到 IslandCaller 名单 {profileName}（{targetId}）失败：{ensureError}");
            }

            // 4. 记录点名前后的历史，通过“头部新增条数”对齐确定本次被点学生。
            // 每次成功抽取 IslandCaller 都会 HistoryService.Add → top20List.Insert(0, name)，
            // 即新点到的学生总是以新记录出现在历史头部；历史最多保留 20 条（超出时从尾部淘汰）。
            // 因此不能用 before/after 的 Count 差值判断抽了几人（历史打满 20 条后 Count 不再变化）。
            var top20Field = historyService.GetType().GetField("top20List", BindingFlags.Instance | BindingFlags.NonPublic);
            if (top20Field is null) throw new InvalidOperationException("当前 IslandCaller 版本不兼容：无法读取点名历史。");
            // 先复制点名前的历史（top20List 始终是同一个 List 对象，Add 就地修改，不能保存“引用快照”）。
            var beforeList = SnapshotList(top20Field.GetValue(historyService) as System.Collections.IList);

            var showMethod = callerService.GetType().GetMethod("ShowRandomStudent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (showMethod is null) throw new InvalidOperationException("当前 IslandCaller 版本不兼容：找不到随机点名入口。");

            RunOnUiThread(() => showMethod.Invoke(callerService, new object[] { count }));

            var afterList = SnapshotList(top20Field.GetValue(historyService) as System.Collections.IList);
            // 求出本次点名新增的历史头部条数：新记录逐条压在旧记录之前，去掉头部新增记录后，
            // 剩余部分应仍与点名前的历史按原顺序对齐（历史超 20 条时从尾部淘汰，因此可能被截短）。
            var newHeadCount = CountNewHeadEntries(beforeList, afterList, count);
            var drawnNames = new List<string>();
            for (var i = 0; i < newHeadCount && i < afterList.Count; i++)
            {
                if (afterList[i] is string name) drawnNames.Add(name);
            }
            drawnNames.Reverse();
            drawnNames.RemoveAll(name => string.IsNullOrEmpty(name) || name.StartsWith("Error", StringComparison.Ordinal));

            var students = new JsonArray();
            foreach (var name in drawnNames) students.Add(name);
            var namesJoined = string.Join("、", drawnNames);

            var result = new JsonObject
            {
                ["ok"] = true,
                ["installed"] = true,
                ["ready"] = true,
                ["triggered"] = drawnNames.Count > 0,
                ["count"] = drawnNames.Count,
                ["students"] = students
            };
            if (!string.IsNullOrWhiteSpace(profileName)) result["profile"] = profileName;
            if (profileId is { } usedId) result["profile_id"] = usedId.ToString();
            result["message"] = drawnNames.Count > 0
                ? $"已随机点名 {drawnNames.Count} 人：{namesJoined}。"
                : "IslandCaller 点名已触发，但没有返回学生。可能是当前名单为空、权重均为 0 或正处于禁止点名的时段，请在 IslandCaller 设置页检查名单与时段设置。";
            return result;
        }
        catch (TargetInvocationException ex)
        {
            return FailureResult("IslandCaller 点名失败：" + ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return FailureResult("IslandCaller 点名失败：" + ex.Message);
        }
    }

    /// <summary>切换 IslandCaller 到指定名单（确保名单加载，供随后点名使用）。</summary>
    private static bool EnsureProfileLoaded(Guid profileId, out string? error)
    {
        error = null;
        try
        {
            var runtimeService = ResolveIslandCallerService(ProfileRuntimeServiceTypeName);
            if (runtimeService is null)
            {
                error = "IslandCaller 名单运行时服务不可用。";
                return false;
            }
            var method = runtimeService.GetType().GetMethod("EnsureLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method is null)
            {
                error = "当前 IslandCaller 版本不兼容：找不到名单加载入口。";
                return false;
            }
            var ok = method.Invoke(runtimeService, new object[] { profileId }) is true;
            if (!ok) error = "IslandCaller 未能加载该名单。";
            return ok;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    // ---------------- 工具：list_island_caller_profiles ----------------

    internal static JsonObject ListIslandCallerProfiles()
    {
        try
        {
            var settingsInstance = GetSettingsInstance();
            var profileService = ResolveIslandCallerService(ProfileServiceTypeName);
            var statusService = ResolveIslandCallerService(StatusServiceTypeName);
            if (settingsInstance is null || profileService is null || statusService is null) return NotInstalledResult();

            var profileSetting = GetProfileSetting();
            var profileList = GetProfileList();
            var defaultProfileId = ReadGuidProperty(profileSetting, "DefaultProfile") ?? Guid.Empty;
            var activeProfileId = ReadGuidProperty(profileService, "ActiveProfileId") ?? Guid.Empty;

            var getMembersMethod = profileService.GetType().GetMethod("GetMembers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var profiles = new JsonArray();
            if (profileList is not null)
            {
                foreach (System.Collections.DictionaryEntry entry in profileList)
                {
                    if (entry.Key is not Guid id) continue;
                    var name = entry.Value?.ToString() ?? string.Empty;
                    var item = new JsonObject
                    {
                        ["id"] = id.ToString(),
                        ["name"] = name,
                        ["is_default"] = id == defaultProfileId,
                        ["is_active"] = id == activeProfileId
                    };
                    try
                    {
                        var members = getMembersMethod?.Invoke(profileService, new object[] { id });
                        item["member_count"] = (members as System.Collections.ICollection)?.Count ?? 0;
                    }
                    catch (Exception ex)
                    {
                        item["member_count"] = null;
                        item["load_error"] = ex.GetBaseException().Message;
                    }
                    item["roster_file_exists"] = File.Exists(RosterFilePath(id));
                    profiles.Add(item);
                }
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["installed"] = true,
                ["ready"] = ReadBoolProperty(statusService, "IsPluginReady"),
                ["default_profile_id"] = defaultProfileId == Guid.Empty ? null : defaultProfileId.ToString(),
                ["active_profile_id"] = activeProfileId == Guid.Empty ? null : activeProfileId.ToString(),
                ["profiles"] = profiles
            };
        }
        catch (TargetInvocationException ex)
        {
            return FailureResult("读取 IslandCaller 名单失败：" + (ex.InnerException?.Message ?? ex.Message));
        }
        catch (Exception ex)
        {
            return FailureResult("读取 IslandCaller 名单失败：" + ex.Message);
        }
    }

    // ---------------- 工具：read_island_caller_roster ----------------

    internal static JsonObject ReadIslandCallerRoster(JsonElement arguments)
    {
        var profileIdText = OptionalString(arguments, "profile_id");
        if (string.IsNullOrWhiteSpace(profileIdText)) return FailureResult("缺少 profile_id 参数（IslandCaller 名单 GUID）。");
        if (!Guid.TryParse(profileIdText, out var profileId)) return FailureResult("profile_id 必须是 IslandCaller 名单的 GUID（来自 list_island_caller_profiles）。");

        try
        {
            var settingsInstance = GetSettingsInstance();
            var statusService = ResolveIslandCallerService(StatusServiceTypeName);
            if (settingsInstance is null || statusService is null) return NotInstalledResult();

            var profileName = GetProfileName(GetProfileList(), profileId);
            if (profileName is null) return FailureResult($"IslandCaller 名单不存在：{profileId}。请先通过 list_island_caller_profiles 获取可用的名单 ID。");

            var filePath = RosterFilePath(profileId);
            if (!File.Exists(filePath)) return FailureResult($"IslandCaller 名单文件不存在：{filePath}。请在 IslandCaller 设置页先创建该名单。");

            var members = ParseRosterFile(filePath, out var csvText, out var parseWarnings);
            var membersJson = new JsonArray();
            foreach (var member in members)
            {
                membersJson.Add(new JsonObject
                {
                    ["id"] = member.Id,
                    ["name"] = member.Name,
                    ["gender"] = member.Gender,
                    ["manual_weight"] = member.ManualWeight
                });
            }
            var result = new JsonObject
            {
                ["ok"] = true,
                ["installed"] = true,
                ["profile_id"] = profileId.ToString(),
                ["profile"] = profileName,
                ["roster_file"] = filePath,
                ["member_count"] = members.Count,
                ["members"] = membersJson,
                ["csv"] = csvText
            };
            if (parseWarnings is not null) result["warnings"] = parseWarnings;
            return result;
        }
        catch (Exception ex)
        {
            return FailureResult("读取 IslandCaller 名单失败：" + ex.Message);
        }
    }

    // ---------------- 工具：write_island_caller_roster ----------------

    internal static JsonObject WriteIslandCallerRoster(JsonElement arguments)
    {
        var profileIdText = OptionalString(arguments, "profile_id");
        if (string.IsNullOrWhiteSpace(profileIdText)) return FailureResult("缺少 profile_id 参数（IslandCaller 名单 GUID）。");
        if (!Guid.TryParse(profileIdText, out var profileId)) return FailureResult("profile_id 必须是 IslandCaller 名单的 GUID（来自 list_island_caller_profiles）。");
        if (!arguments.TryGetProperty("members", out var membersElement) || membersElement.ValueKind != JsonValueKind.Array)
            return FailureResult("缺少 members 数组参数（要写入名单的学生）。");
        if (membersElement.GetArrayLength() == 0) return FailureResult("members 不能为空，至少需要一名学生。");

        try
        {
            var settingsInstance = GetSettingsInstance();
            var profileService = ResolveIslandCallerService(ProfileServiceTypeName);
            var statusService = ResolveIslandCallerService(StatusServiceTypeName);
            if (settingsInstance is null || profileService is null || statusService is null) return NotInstalledResult();

            var profileList = GetProfileList();
            var profileName = GetProfileName(profileList, profileId);
            if (profileName is null) return FailureResult($"IslandCaller 名单不存在：{profileId}。请先通过 list_island_caller_profiles 获取可用的名单 ID。");

            var normalized = new List<(string Name, int Gender, double ManualWeight)>();
            foreach (var memberElement in membersElement.EnumerateArray())
            {
                var name = memberElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrEmpty(name)) return FailureResult("members 中每个学生都必须有非空 name。");
                if (name.Contains(',') || name.Contains('\n') || name.Contains('\r'))
                    return FailureResult($"学生姓名“{name}”包含逗号或换行，无法写入 IslandCaller CSV 名单。");
                if (normalized.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal)))
                    return FailureResult($"学生姓名重复：{name}。名单中的姓名应保持唯一。");

                var gender = 0;
                if (memberElement.TryGetProperty("gender", out var genderElement))
                {
                    if (genderElement.ValueKind == JsonValueKind.Number) gender = genderElement.GetInt32();
                    else if (genderElement.ValueKind == JsonValueKind.String && int.TryParse(genderElement.GetString(), out var parsedGender)) gender = parsedGender;
                    if (gender is not (0 or 1)) return FailureResult($"性别字段只支持 0（男）或 1（女），收到：{gender}。");
                }

                var manualWeight = 1.0;
                if (memberElement.TryGetProperty("manual_weight", out var weightElement))
                {
                    if (weightElement.ValueKind == JsonValueKind.Number) manualWeight = weightElement.GetDouble();
                    else if (weightElement.ValueKind == JsonValueKind.String && double.TryParse(weightElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedWeight)) manualWeight = parsedWeight;
                    if (!(manualWeight > 0)) return FailureResult($"manual_weight 必须是大于 0 的数值，收到：{manualWeight}。");
                }

                normalized.Add((name, gender, manualWeight));
            }

            // ID 按顺序重新编号（IslandCaller 排序与选择都不依赖原始 ID）
            var writer = new StringBuilder();
            writer.Append("id,name,gender,manualweight");
            writer.Append('\n');
            for (var i = 0; i < normalized.Count; i++)
            {
                var (name, gender, manualWeight) = normalized[i];
                writer.Append(i + 1);
                writer.Append(',');
                writer.Append(name);
                writer.Append(',');
                writer.Append(gender);
                writer.Append(',');
                writer.Append(manualWeight.ToString("0.###", CultureInfo.InvariantCulture));
                writer.Append('\n');
            }

            Directory.CreateDirectory(ProfileDirectoryPath);
            File.WriteAllText(RosterFilePath(profileId), writer.ToString(), new UTF8Encoding(false));

            // 若写入的是当前激活名单，立即热重载，使 IslandCaller 后续点名使用新名单
            var reloaded = false;
            var activeProfileId = ReadGuidProperty(profileService, "ActiveProfileId");
            if (activeProfileId == profileId)
            {
                var runtimeService = ResolveIslandCallerService(ProfileRuntimeServiceTypeName);
                if (runtimeService is not null)
                {
                    try
                    {
                        var reloadMethod = runtimeService.GetType().GetMethod("Reload", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (reloadMethod is not null)
                        {
                            reloadMethod.Invoke(runtimeService, new object[] { profileId });
                            reloaded = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        reloaded = false;
                        return FailureResult($"名单已写入文件，但热重载失败：{ex.GetBaseException().Message}。请重启 IslandCaller 或切换一次名单后生效。");
                    }
                }
            }

            var result = new JsonObject
            {
                ["ok"] = true,
                ["installed"] = true,
                ["written"] = true,
                ["profile_id"] = profileId.ToString(),
                ["profile"] = profileName,
                ["member_count"] = normalized.Count,
                ["reloaded"] = reloaded,
                ["message"] = reloaded
                    ? $"已写入并重载 IslandCaller 名单“{profileName}”，共 {normalized.Count} 人，后续点名立即生效。"
                    : $"已写入 IslandCaller 名单“{profileName}”，共 {normalized.Count} 人。该名单当前未激活，切换到此名单后生效。"
            };
            return result;
        }
        catch (Exception ex)
        {
            return FailureResult("写入 IslandCaller 名单失败：" + ex.Message);
        }
    }

    // ---------------- 工具：CSV 名单读写 ----------------

    private sealed record Member(int Id, string Name, int Gender, double ManualWeight);

    /// <summary>解析 IslandCaller 名单 CSV（格式：id,name,gender,manualweight），跳过格式错误行。</summary>
    private static List<Member> ParseRosterFile(string filePath, out string csvText, out string? warnings)
    {
        csvText = File.ReadAllText(filePath);
        var lines = csvText.Split('\n');
        var members = new List<Member>();
        var problems = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('\uFEFF')) line = line[1..].Trim();
            if (i == 0)
            {
                if (!string.Equals(line, "id,name,gender,manualweight", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"IslandCaller 名单 CSV 标题格式错误：{filePath}。必须为 id,name,gender,manualweight。");
                }
                continue;
            }
            var parts = line.Split(',');
            if (parts.Length != 4 || !int.TryParse(parts[0], out var id) || string.IsNullOrWhiteSpace(parts[1]) || !int.TryParse(parts[2], out var gender) || !double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var manualWeight))
            {
                problems.Add($"第 {i + 1} 行格式错误已跳过：{line}");
                continue;
            }
            members.Add(new Member(id, parts[1], gender, manualWeight));
        }
        members.Sort((a, b) => a.Id.CompareTo(b.Id));
        warnings = problems.Count > 0 ? string.Join("；", problems) : null;
        return members;
    }

    // ---------------- 工具：UI 线程调度 ----------------

    /// <summary>在 Avalonia UI 线程上执行操作；IslandCaller 点名窗口必须由 UI 线程创建。</summary>
    private static void RunOnUiThread(Action action)
    {
        Dispatcher? dispatcher;
        try
        {
            dispatcher = Dispatcher.UIThread;
        }
        catch (Exception)
        {
            dispatcher = null;
        }
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        try
        {
            dispatcher.Invoke(action);
        }
        catch (Exception)
        {
            // UI 线程调度不可用（例如宿主尚未进入主循环）时，退回当前线程调用。
            action();
        }
    }

    private static string? OptionalString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    // ---------------- 历史头部新增条数判定 ----------------

    /// <summary>把历史列表（IList）复制为独立快照，避免持有同一对象的引用而被后续就地修改污染。</summary>
    private static List<object?> SnapshotList(System.Collections.IList? list)
    {
        var snapshot = new List<object?>();
        if (list is null) return snapshot;
        foreach (var item in list) snapshot.Add(item);
        return snapshot;
    }

    /// <summary>
    /// 计算本次点名在历史头部新增的条数。IslandCaller 每次成功抽取都会把学生插到
    /// top20List 头部，因此点名后列表形如 [新增...][原历史...]；把 after 头部去掉 k 条后，
    /// 剩余部分应能按原顺序与 before 对齐（原历史超 20 条时会被从尾部淘汰而截短）。
    /// 从大到小尝试 k，取第一个完全对齐的值，即为实际被点到的人数。
    /// </summary>
    private static int CountNewHeadEntries(List<object?> before, List<object?> after, int requestedCount)
    {
        if (after.Count == 0 || requestedCount <= 0) return 0;
        // 点名没有产生任何新增记录（例如调用时插件刚好未就绪而提前返回）。
        if (SequencesEqual(before, after)) return 0;
        var maxK = Math.Min(requestedCount, after.Count);
        for (var k = maxK; k >= 0; k--)
        {
            var tailLength = after.Count - k;
            // 尾部（去新增后剩下的原历史）不能比点名前的历史更长。
            if (tailLength > before.Count) continue;
            var aligned = true;
            for (var i = 0; i < tailLength; i++)
            {
                if (!Equals(after[k + i], before[i]))
                {
                    aligned = false;
                    break;
                }
            }
            if (aligned) return k;
        }
        return 0;
    }

    private static bool SequencesEqual(List<object?> left, List<object?> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i], right[i])) return false;
        }
        return true;
    }
}
