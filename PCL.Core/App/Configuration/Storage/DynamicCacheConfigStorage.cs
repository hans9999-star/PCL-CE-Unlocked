using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace PCL.Core.App.Configuration.Storage;

public class DynamicCacheConfigStorage : ConfigStorage
{
    private readonly Dictionary<object, ConfigStorage> _cache = [];
    private ConfigStorage? _nullContextCache;

    /// <summary>
    /// 存取仓库工厂。在没有匹配的上下文实例时将被调用，以创建新的上下文实例。
    /// </summary>
    public required Func<object?, ConfigStorage> StorageFactory { get; init; }

    protected override bool OnAccess<TKey, TValue>(StorageAction action, ref TKey key, [NotNullWhen(true)] ref TValue value, object? context)
    {
        ConfigStorage? storage;
        if (context is null) storage = _nullContextCache;
        else _cache.TryGetValue(context, out storage);
        if (storage is null)
        {
            // 分支说明：StorageFactory 在 context 为 null 时可能直接抛
            // ArgumentNullException（例如 CustomInfo 这类需要上下文参数的配置项）。
            // 此时不能把异常抛给 ConfigStorage.Access —— 那里的 catch 会调用
            // Lifecycle.ForceShutdown(-2) 强制关闭启动器，导致只要有一个配置项
            // 需要参数，任何「遍历所有配置项」的代码都会把启动器打死。
            // 这里把「缺上下文」与「工厂真出错」区分开，返回 false 表示本次无值，
            // 让调用方按默认值处理。
            if (context is null)
            {
                try
                {
                    storage = StorageFactory(null);
                }
                catch (ArgumentNullException)
                {
                    // 该配置项必须有上下文参数，本次访问无值
                    return false;
                }
                _nullContextCache = storage;
            }
            else
            {
                try
                {
                    storage = StorageFactory(context);
                    _cache[context] = storage;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to invoke storage factory", ex);
                }
            }
        }
        return storage.Access(action, ref key, ref value, context);
    }

    protected override void OnStop()
    {
        foreach (var item in _cache.Values) item.Stop();
        _cache.Clear();
    }

    public bool InvalidateCache(object context)
    {
        var result = _cache.TryGetValue(context, out var center);
        if (result)
        {
            center?.Stop();
            _cache.Remove(context);
        }
        return result;
    }
}
