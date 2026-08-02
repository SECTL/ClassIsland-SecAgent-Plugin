using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;

namespace ClassIsland.SecAgent.Plugin;

[SettingsPageInfo("classisland.secagent.settings", "SecAgent HTTP")]
public partial class SecAgentSettingsPage : SettingsPageBase
{
    private SecAgentController Controller => IAppHost.GetService<SecAgentController>();

    public SecAgentSettingsPage()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void StartButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            Controller.Start();
            MessageText.Text = "HTTP 服务已启动。请同时启用 SecAgent ClassIsland 连接插件。";
        }
        catch (Exception ex)
        {
            MessageText.Text = $"启动失败：{ex.Message}";
        }
        finally
        {
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var status = Controller.GetStatus();
        ServerStatusText.Text = status.ServerRunning ? "✓ 正在运行" : "✗ 未运行";
    }
}
