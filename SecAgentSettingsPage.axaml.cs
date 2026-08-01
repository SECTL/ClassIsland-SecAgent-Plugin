using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;

namespace ClassIsland.SecAgent.Plugin;

[SettingsPageInfo("classisland.secagent.settings", "SecAgent")]
public partial class SecAgentSettingsPage : SettingsPageBase
{
    private SecAgentController Controller => IAppHost.GetService<SecAgentController>();

    public SecAgentSettingsPage()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private async void RegisterButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RegisterButton.IsEnabled = false;
        MessageText.Text = "正在注册…";
        try
        {
            await Controller.RegisterAsync();
            MessageText.Text = "注册完成。SecAgent 下次发现工具时会加载新的 MCP 配置。";
        }
        catch (Exception ex)
        {
            MessageText.Text = $"注册失败：{ex.Message}";
        }
        finally
        {
            RegisterButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var status = Controller.GetStatus();
        WorkspaceText.Text = status.WorkspaceFound ? status.Workspace : "未找到 ~/SecAgentWorkspace/secagent.yaml";
        SkillStatusText.Text = status.SkillRegistered ? "✓ 已注册" : "✗ 未注册";
        McpStatusText.Text = status.McpRegistered ? "✓ 已启用" : "✗ 未注册或未启用";
        ServerStatusText.Text = status.ServerRunning ? "✓ 正在运行" : "✗ 未运行";
    }
}
