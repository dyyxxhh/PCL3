using PCL.Core.App;
using PCL.Core.ViewModel;

namespace PCL;

/// <summary>工具页面左侧导航栏的视图模型。</summary>
public class PageToolsLeftViewModel : ViewModelBase
{
    public bool IsHiddenPage(bool isGameLinkChecked, bool isTestChecked)
    {
        return isGameLinkChecked || isTestChecked && Config.Preference.Hide.ToolsTest;
    }
}
