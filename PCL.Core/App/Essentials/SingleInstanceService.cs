using PCL.Core.App.IoC;

namespace PCL.Core.App.Essentials;

/// <summary>
///     单实例服务。<br/>
///     分支说明：本分支已移除「只能开一个启动器」的限制，允许同时运行多个实例，
///     因此这里不再注册生命周期服务，也不做任何单例锁检查。<br/>
///     原实现依赖 <c>Context</c> 等由生命周期注入的成员，取消挂载后无法保留，
///     故仅保留类型骨架以维持引用兼容；如需恢复多开限制，把
///     <c>LifecycleService</c> / <c>LifecycleScope</c> 特性与锁文件逻辑加回即可。
/// </summary>
public sealed partial class SingleInstanceService
{
}
