namespace Ability.Hosting;

/// <summary>
/// 任务内统一的权威调用登记表。
/// 集中维护调用键唯一性、树形父子调用拓扑、生命周期状态迁移与资源租约归属。
/// </summary>
internal sealed class InvocationRegistry
{
    /// <summary>
    /// 登记表全局读写同步互斥锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 当前活跃的调用记录字典（以 invocationKey 为键）。
    /// </summary>
    private readonly Dictionary<string, InvocationRecord> _records = new(StringComparer.Ordinal);

    /// <summary>
    /// 受管操作键到所属调用键的反向映射表。
    /// </summary>
    private readonly Dictionary<string, string> _operationToInvocation = new(StringComparer.Ordinal);

    /// <summary>
    /// 本次任务历史上登记过的全部调用列表（包含已回收的记录），用于收尾时汇总诊断。
    /// </summary>
    private readonly List<InvocationRecord> _all = [];

    /// <summary>
    /// 本次任务历史上登记过的全部调用字典（包含已从活跃表移除的记录），以 invocationKey 索引。
    /// </summary>
    private readonly Dictionary<string, InvocationRecord> _historicalRecords = new(StringComparer.Ordinal);

    /// <summary>
    /// 权威登记一条新调用。
    /// </summary>
    /// <param name="taskId">所属任务标识。</param>
    /// <param name="parentKey">父调用键；根调用为 null。</param>
    /// <param name="pipeName">调用键（同时作为 IPC 管道名）。</param>
    /// <returns>新建的调用权威记录。</returns>
    /// <exception cref="InvalidOperationException">调用键重复，或父调用不存在/已不可接纳子调用。</exception>
    public InvocationRecord Register(string taskId, string? parentKey, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        lock (_gate)
        {
            if (_historicalRecords.ContainsKey(pipeName) || _records.ContainsKey(pipeName))
            {
                throw new InvalidOperationException($"调用键已在历史或当前登记表中存在：{pipeName}。");
            }

            if (parentKey is not null
                && (!_records.TryGetValue(parentKey, out var parent) || !parent.CanAcceptChild))
            {
                throw new InvalidOperationException($"子调用的父调用不存在或已停止接纳新子调用：{parentKey}。");
            }

            var record = new InvocationRecord(taskId, parentKey, pipeName);
            _records.Add(pipeName, record);
            _historicalRecords.Add(pipeName, record);
            _all.Add(record);
            return record;
        }
    }

    /// <summary>
    /// 获取本次任务登记过的全部历史记录快照（包含已回收的），供事后汇总与 OTLP 导出。
    /// </summary>
    public IReadOnlyList<InvocationRecord> AllRecords
    {
        get
        {
            lock (_gate)
            {
                return _all.ToArray();
            }
        }
    }

    /// <summary>
    /// 按调用键查找活动记录；若不存在则抛出异常。
    /// </summary>
    /// <param name="key">调用键。</param>
    /// <returns>活动记录。</returns>
    /// <exception cref="KeyNotFoundException">调用键不存在时抛出。</exception>
    public InvocationRecord Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            return _records.TryGetValue(key, out var record)
                ? record
                : throw new KeyNotFoundException($"未在权威表中找到活动调用键：{key}。");
        }
    }

    /// <summary>
    /// 按调用键尝试查找活动记录。
    /// </summary>
    /// <param name="key">调用键。</param>
    /// <param name="record">若存在则输出记录。</param>
    /// <returns>命中返回 true；否则返回 false。</returns>
    public bool TryGet(string key, out InvocationRecord? record)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            record = null;
            return false;
        }

        lock (_gate)
        {
            return _records.TryGetValue(key, out record);
        }
    }

    /// <summary>
    /// 按调用键尝试查找历史登记过的记录（包含已从活跃表移除的记录）。
    /// </summary>
    /// <param name="key">调用键。</param>
    /// <param name="record">若存在则输出历史记录。</param>
    /// <returns>命中返回 true；否则返回 false。</returns>
    public bool TryGetHistorical(string key, out InvocationRecord? record)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            record = null;
            return false;
        }

        lock (_gate)
        {
            return _historicalRecords.TryGetValue(key, out record);
        }
    }

    /// <summary>
    /// 深度优先收集以 rootKey 为根的整棵调用子树（包含根节点本身）。
    /// </summary>
    /// <param name="rootKey">子树根调用键。</param>
    /// <returns>子树包含的调用记录列表。</returns>
    public IReadOnlyList<InvocationRecord> GetSubtree(string rootKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);

        lock (_gate)
        {
            var result = new List<InvocationRecord>();
            CollectUnderLock(rootKey, result);
            return result;
        }
    }

    /// <summary>
    /// 在同一原子锁保护下，关闭新子调用接纳并获取已置停止标记的完整子树。
    /// 杜绝在获取子树和置停止标记之间有并发新子调用登记。
    /// </summary>
    /// <param name="rootKey">子树根调用键。</param>
    /// <returns>已标记停止的子树调用记录列表。</returns>
    public IReadOnlyList<InvocationRecord> RequestStopSubtree(string rootKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);

        lock (_gate)
        {
            var subtree = new List<InvocationRecord>();
            CollectUnderLock(rootKey, subtree);
            foreach (var record in subtree)
            {
                record.RequestStop();
            }

            return subtree;
        }
    }

    /// <summary>
    /// 登记租约至指定调用。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <param name="leaseId">租约标识。</param>
    public void TrackLease(string invocationKey, string leaseId)
        => Get(invocationKey).TrackLease(leaseId);

    /// <summary>
    /// 全局释放指定租约。
    /// </summary>
    /// <param name="leaseId">租约标识。</param>
    public void ReleaseLease(string leaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        lock (_gate)
        {
            foreach (var record in _records.Values)
            {
                if (record.ReleaseLease(leaseId))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 尝试从指定调用中释放指定的单条租约，严格核验归属。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <param name="leaseId">租约标识。</param>
    /// <returns>核验成功且成功释放返回 true；若调用不存在或不拥有该租约返回 false。</returns>
    public bool TryReleaseLease(string invocationKey, string leaseId)
    {
        if (string.IsNullOrWhiteSpace(invocationKey) || string.IsNullOrWhiteSpace(leaseId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_records.TryGetValue(invocationKey, out var record))
            {
                return false;
            }

            return record.ReleaseLease(leaseId);
        }
    }

    /// <summary>
    /// 尝试向指定调用登记租约；若调用不存在或已处于退出终态则返回 false。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <param name="leaseId">租约标识。</param>
    /// <returns>登记成功返回 true；若调用不存在或已终止返回 false。</returns>
    public bool TryTrackLease(string invocationKey, string leaseId)
    {
        if (string.IsNullOrWhiteSpace(invocationKey) || string.IsNullOrWhiteSpace(leaseId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_records.TryGetValue(invocationKey, out var record)
                || record.State is InvocationState.Exited or InvocationState.Reaped
                || record.StopRequested)
            {
                return false;
            }

            record.TrackLease(leaseId);
            return true;
        }
    }

    /// <summary>
    /// 尝试登记指定调用创建的受管操作键，严格核验调用状态与归属。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <param name="operationKey">操作键。</param>
    /// <returns>若调用存在且未终止并成功记录归属返回 true；若调用不存在或已处于退出终态返回 false。</returns>
    public bool TryTrackOperation(string invocationKey, string operationKey)
    {
        if (string.IsNullOrWhiteSpace(invocationKey) || string.IsNullOrWhiteSpace(operationKey))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_records.TryGetValue(invocationKey, out var record)
                || record.State is InvocationState.Exited or InvocationState.Reaped
                || record.StopRequested)
            {
                return false;
            }

            if (_operationToInvocation.ContainsKey(operationKey))
            {
                // 该受管操作键已有归属记录，拒绝重复登记以防覆盖并导致回滚删除原有归属
                return false;
            }

            record.TrackOperation(operationKey);
            _operationToInvocation[operationKey] = invocationKey;
            return true;
        }
    }

    /// <summary>
    /// 回滚撤销指定调用与受管操作键的归属绑定。
    /// </summary>
    /// <param name="invocationKey">所属调用键。</param>
    /// <param name="operationKey">操作键。</param>
    public void UntrackOperation(string invocationKey, string operationKey)
    {
        if (string.IsNullOrWhiteSpace(invocationKey) || string.IsNullOrWhiteSpace(operationKey))
        {
            return;
        }

        lock (_gate)
        {
            if (_records.TryGetValue(invocationKey, out var record))
            {
                record.UntrackOperation(operationKey);
            }

            if (_operationToInvocation.TryGetValue(operationKey, out var mapped)
                && string.Equals(mapped, invocationKey, StringComparison.Ordinal))
            {
                _operationToInvocation.Remove(operationKey);
            }
        }
    }

    /// <summary>
    /// 登记指定调用创建的受管操作键。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <param name="operationKey">操作键。</param>
    public void TrackOperation(string invocationKey, string operationKey)
    {
        if (!TryTrackOperation(invocationKey, operationKey))
        {
            throw new KeyNotFoundException($"未在权威表中找到可登记操作的活动调用键：{invocationKey}。");
        }
    }

    /// <summary>
    /// 获取受管操作所属的调用键。
    /// </summary>
    /// <param name="operationKey">操作键。</param>
    /// <returns>若存在返回所属调用键，否则返回 null。</returns>
    public string? GetOperationOwner(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return null;
        }

        lock (_gate)
        {
            return _operationToInvocation.GetValueOrDefault(operationKey);
        }
    }

    /// <summary>
    /// 取走指定调用的全部租约号。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <returns>租约列表。</returns>
    public IReadOnlyList<string> TakeLeaseIds(string invocationKey)
        => Get(invocationKey).TakeLeaseIds();

    /// <summary>
    /// 获取指定调用的全部租约号只读副本。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <returns>租约列表。</returns>
    public IReadOnlyList<string> GetLeaseIds(string invocationKey)
        => Get(invocationKey).GetLeaseIds();

    /// <summary>
    /// 将指定调用的活动租约标记为已清理。
    /// </summary>
    /// <param name="invocationKey">调用键。</param>
    /// <returns>累计已清理租约列表。</returns>
    public IReadOnlyList<string> CleanLeaseIds(string invocationKey)
        => Get(invocationKey).CleanLeaseIds();

    /// <summary>
    /// 尝试在整棵子树全部推进到 Reaped 终态后，从活跃表中安全、幂等地移除整树记录。
    /// 若子树中仍存在未完成回收的节点，或子树已被其他并发路径移除，则安全返回 false，不抛出异常。
    /// </summary>
    /// <param name="rootKey">子树根调用键。</param>
    /// <returns>若成功完成移除返回 true；否则返回 false。</returns>
    public bool TryRemoveReapedTree(string rootKey)
    {
        if (string.IsNullOrWhiteSpace(rootKey))
        {
            return false;
        }

        lock (_gate)
        {
            var subtree = new List<InvocationRecord>();
            CollectUnderLock(rootKey, subtree);
            if (subtree.Count == 0 || subtree.Any(record => record.State != InvocationState.Reaped))
            {
                return false;
            }

            foreach (var record in subtree)
            {
                _records.Remove(record.InvocationKey);
            }

            return true;
        }
    }

    /// <summary>
    /// 在整棵子树全部推进到 Reaped 终态后，从活跃表中安全移除整树记录。
    /// </summary>
    /// <param name="rootKey">子树根调用键。</param>
    /// <exception cref="InvalidOperationException">子树中仍存在未完成回收的节点时抛出。</exception>
    public void RemoveReapedTree(string rootKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);

        lock (_gate)
        {
            var subtree = new List<InvocationRecord>();
            CollectUnderLock(rootKey, subtree);

            if (subtree.Any(record => record.State != InvocationState.Reaped))
            {
                throw new InvalidOperationException($"调用子树中仍有尚未回收完毕的记录：{rootKey}。");
            }

            foreach (var record in subtree)
            {
                _records.Remove(record.InvocationKey);
            }
        }
    }

    /// <summary>
    /// 在已持有互斥锁的情况下递归收集子树节点。
    /// </summary>
    private void CollectUnderLock(string parentKey, List<InvocationRecord> target)
    {
        if (!_records.TryGetValue(parentKey, out var parent))
        {
            return;
        }

        target.Add(parent);
        var children = _records.Values.Where(item => item.ParentKey == parentKey).ToArray();
        foreach (var child in children)
        {
            CollectUnderLock(child.InvocationKey, target);
        }
    }

    /// <summary>
    /// 检查登记表中除可选指定的排除项外，所有活动记录是否均已确认进入 Reaped 终态。
    /// </summary>
    /// <param name="excludeKey">可选的排除键（如根调用键）。</param>
    /// <returns>全部已物理确认回收返回 true；若仍有存活或未确认记录返回 false。</returns>
    public bool AllInvocationsReaped(string? excludeKey = null)
    {
        lock (_gate)
        {
            foreach (var record in _records.Values)
            {
                if (excludeKey is not null && string.Equals(record.InvocationKey, excludeKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (record.State != InvocationState.Reaped)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
