using CommunityToolkit.Mvvm.ComponentModel;

namespace HomeworkReminder.ViewModels;

/// <summary>
/// 所有 ViewModel 的基类。
/// 派生自 ObservableObject（而非 ObservableValidator）：本应用不做表单校验，
/// 因此也不需要处理 Avalonia 与 CommunityToolkit 的校验插件重复问题
/// （Avalonia 12 已把 BindingPlugins 改为 internal）。
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}