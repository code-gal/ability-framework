# Ability.Hosting 项目规格

Hosting 为宿主应用提供任务执行与治理接入。模块组织见 [architecture.md](architecture.md)，公共通信机制见 [core.md](core.md)。进程、调用树、临时资源和观测语义引用[全局接入方案](../../../Docs/技能接入协议与SDK方案.md)第六、九、十一节。

## 一、宿主 API 与运行配置

宿主任务入口为 `AbilityHost.RunAsync`，接收 `AbilityInvocation`、`HostRuntimeOptions` 和可选 `CancellationToken`，返回 `Task<AbilityRunResult>`。

需要释放借用依赖的提供方在调用前创建 `HostRunLifetime`，使用带该句柄的重载。既有重载由 Hosting 创建句柄，成功返回时通过结果的 `Lifetime` 提供观察入口。

**单次运行对象**：`AbilityHost` 只保留公开入口、接入检查与句柄绑定，把一次调用交给一个具体的单次运行对象。该对象持有本次创建的回调派发、治理协调、权威登记表、启动准备、任务专属目录、命名管道、IPC 连接、执行协调器、执行与关联取消源，以及本层的自然退出观察任务和收尾问题列表，并按阶段组织流程：准备、启动与握手、输入下发、执行、必要停止与处置、返回快照、后台收尾。每个阶段的失败路径由该对象裁决；准备失败与正常收尾共用同一套所有权规则：每个资源由创建者释放，直到被明确移交，移交后由接收方负责。

前台预算结束后，同一个对象继续持有原组件与自有资源，等组件实际退出与资源实际释放结束后发布最终报告。该对象不复制登记表、租约表或组件内部状态，不建立跨组件的“所有任务”集合，不提供任意服务查询；各组件继续持有自己的锁、任务、取消与释放。`HostRunLifetime` 仍是公开观察句柄，不兼任运行管理对象。

```csharp
public static class AbilityHost
{
    public static Task<AbilityRunResult> RunAsync(
        AbilityInvocation invocation,
        HostRuntimeOptions? options = null,
        CancellationToken cancellationToken = default);

    public static Task<AbilityRunResult> RunAsync(
        AbilityInvocation invocation,
        HostRuntimeOptions? options,
        CancellationToken cancellationToken,
        HostRunLifetime lifetime);
}
```

### 1. 核心模型契约

| 类型 | 关键字段与契约 |
| --- | --- |
| `AbilityInvocation` | `string LauncherPath`：Launcher 可执行文件或程序集路径<br/>`string TaskId`：任务全局唯一标识<br/>`string InputTypeName`：输入载荷类型名<br/>`string InputJson`：输入载荷的 UTF-8 JSON 文本<br/>`string? OutputTypeName`：预期输出类型名（可选）<br/>`IReadOnlyList<string>? Arguments`：附加进程命令行参数（可选）<br/>`IReadOnlyDictionary<string, string>? EnvironmentVariables`：环境变量覆盖（可选）<br/>`string? WorkingDirectory`：进程工作目录（可选） |
| `HostRuntimeOptions` | `TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5)`：管道连接超时<br/>`TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5)`：v3 握手超时（涵盖 Hello 发送与 HelloAccepted 接收，同时用于初始 `TaskInput` 下发预算）<br/>`TimeSpan BuildTimeout = TimeSpan.FromSeconds(30)`：子调用 Launcher 构建等待预算，由 `HostChildCalls` 使用<br/>`TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(1)`：优雅取消后强杀等待宽限<br/>`TimeSpan? ExecutionTimeout = null`：整任务最大执行期限（超时触发优雅取消）<br/>`TimeSpan? RequestTimeout = null`：治理决策（租约、补参、来源恢复）等待超时<br/>`TimeSpan PolicyUpdateTimeout = TimeSpan.FromSeconds(5)`：策略动态更新确认等待超时<br/>`TimeSpan TraceExportTimeout = TimeSpan.FromSeconds(5)`：追踪导出的有限排空预算，由 `HostTraceCoordinator` 使用<br/>`int MaxPendingPolicyUpdates = 64`：未决策略更新表容量上限<br/>`int ResultCreditWindow = 4`：流式结果初始信用额度窗口<br/>`int MaxPendingControlFrames = 64`：控制出站队列容量<br/>`string? TaskTempRootDirectory = null`：临时目录根（缺省系统临时目录）<br/>`Func<string, CancellationToken, ValueTask>? TaskTempConsumer`：技能退出后、删除临时目录前的内容转存消费回调<br/>`TimeSpan TaskTempConsumerTimeout = TimeSpan.FromSeconds(30)`：转存消费超时<br/>`Func<StreamResultItem, CancellationToken, ValueTask>? ResultItemCallback`：流式结果项逐项消费回调<br/>`Action<HostProgress>? ProgressCallback`：应用层业务进度汇报回调<br/>`Action<ProgressBody>? OperationProgressCallback`：受管操作细粒度阶段进度通知回调<br/>`Action<OperationSnapshot>? OperationSnapshotCallback`：受管操作生命周期状态快照回调<br/>`ChannelReader<RunPolicyUpdate>? PolicyUpdates = null`：动态运行策略更新通道读取端<br/>`RunPolicyBody? PolicyOverride = null`：宿主权威运行策略覆盖（优先于技能建议策略）<br/>`Func<long, LeaseRequestBody, CancellationToken, ValueTask<LeaseDecision?>>? LeaseDecisionCallback`：租约仲裁决策回调<br/>`Func<long, ParamRequestBody, CancellationToken, ValueTask<ParamDecision?>>? ParamDecisionCallback`：补参决策回调（支持文本或结构化载荷）<br/>`Func<long, SourceRepairBody, CancellationToken, ValueTask<SourceRepairDecision?>>? SourceRepairCallback`：数据源恢复决策回调<br/>`Action<string>? DiagnosticCallback`：诊断日志回调<br/>`TimeProvider TimeProvider = TimeProvider.System`：时间提供者（参与本地计时与超时计算） |
| `LeaseDecision` | `bool Granted`：是否批准租约<br/>`string? LeaseId`：批准时的唯一租约标识（缺省自动合成；同一任务治理会话内不可重复签发，释放后仍不可复用）<br/>`string? Reason`：拒绝时的原因说明 |
| `ParamDecision` | `string? Value`：文本补参值（与 PayloadType/Payload 互斥）<br/>`string? PayloadType`：结构化补参类型名<br/>`JsonElement? Payload`：结构化补参原始 JSON 元素 |
| `SourceRepairDecision` | `string Outcome`：恢复裁决动作（`Reread`、`RestartRequired`、`Denied`、`Cancelled`） |
| `RunPolicyUpdate` | `string OperationKey`：目标受管操作键<br/>`long Version`：更新策略目标版本号<br/>`RunPolicyBody Policy`：目标策略内容<br/>`string Source = "HostUpdate"`：策略更新发起来源 |
| `OperationSnapshot` | 记录操作键、父操作键、名称、用途、状态、累计尝试次数、策略版本与来源、策略内容、时间戳（登记、开始、结束、耗时）、停止原因与最新进度快照 |
| `StreamResultItem` | `string PayloadType`：结果项载荷类型名<br/>`JsonElement Payload`：结果项原始 JSON 元素 |
| `HostProgress` | `string OperationKey`：操作标识<br/>`long Sequence`：递增进度序号<br/>`double? Percent`：百分比进度<br/>`string? Message`：进度描述文本<br/>`JsonElement? Data`：附加结构化数据 |
| `AbilityRunResult` | `bool Success`：结果交付条件是否满足，判定见第 3 小节<br/>`string? BusinessOutcome`：已接受的技能终态<br/>`int? ProcessExitCode`：确认的根进程退出码，未启动或退出未知时为 null<br/>`bool ProcessExitConfirmed`：整树进程退出是否确认<br/>`bool GovernanceResourcesReclaimed`：租约与治理归属是否完成结算<br/>`bool DiagnosticsCompleted`：诊断工作是否实际结束<br/>`bool DeferredWorkCompleted`：其余受管工作是否实际结束<br/>`bool CleanupCompleted`：资源释放与目录处置是否满足第 3 小节条件<br/>`HostRunLifetime Lifetime`：本次调用的使用结束观察句柄<br/>`IReadOnlyList<HostCleanupIssue> Issues`：返回时已知的收尾问题<br/>`string Outcome`：终态标识（`SubmitResult`、`ResultClose`、`Fault`、`Cancelled`、`ConnectTimeout`、`HandshakeTimeout`、`ExecutionTimeout`、`ProcessExitBeforeConnect`、`ProcessCrash`、`Disconnected`、`ProtocolError`、`ConsumerFault` 等）<br/>`string? OutputTypeName`：一元输出类型名（若调用方指定则核验严格匹配）<br/>`JsonElement? OutputJson`：一元输出 JSON 元素<br/>`int StreamItemCount`：流式任务接收到的元素总数（即便失败或取消亦可靠保留）<br/>`string? FaultKind`：技能提交的错误种类或异常分类<br/>`string? FaultMessage`：技能提交的错误详情<br/>`TaskTempReport? TaskTemp`：专属临时目录生命周期报告<br/>`string Diagnostics`：收集的 stderr 文本及审计诊断<br/>`IReadOnlyList<string> ObservedMessageTypes`：按序观察到的通信消息列表（上限 128 条）<br/>`bool ObservedMessagesTruncated`：观察消息是否发生截断<br/>`IReadOnlyList<OperationSnapshot> Operations`：所有登记受管操作的最终状态快照集合（由协调器权威保留并可靠返回，不受中间通知丢弃影响） |
| `HostRunLifetime` | 调用方创建的一次性观察句柄；公开只读 `Task<HostCleanupReport> Completion`，由 Hosting 在实际使用结束后完成 |
| `HostCleanupReport` | 最终的 `ProcessExitConfirmed`、`GovernanceResourcesReclaimed`、`DiagnosticsCompleted`、`DeferredWorkCompleted`、`CleanupCompleted`、`TaskTemp` 与 `Issues`，字段类型与结果契约相同 |
| `HostCleanupIssue` | `string Stage`：发生阶段<br/>`string Kind`：错误种类<br/>`string Message`：脱敏说明；原始调用异常仍遵循原抛出契约 |

宿主业务载荷保持为 JSON，Hosting 无需知道文件技能的具体强类型模型。

Hosting 借用 `HostRuntimeOptions` 中的时间来源、回调及策略更新通道，提供方按下述完成信号管理这些对象及其依赖资源的寿命。Hosting 释放自己创建的计时器、取消源、连接和进程管理资源。结果只交付数据、绝对路径或 URI，本地句柄由所属进程管理；后续访问按资源引用取得连接和授权。

### 2. 借用依赖的使用结束信号

`HostRunLifetime` 由调用方创建，公开只读的 `Task<HostCleanupReport> Completion`。Hosting 先独占绑定句柄，再执行本次调用；句柄只绑定一次，重复绑定在借用依赖前拒绝。返回结果的 `Lifetime` 即本次绑定句柄。绑定成功后，正常返回、取消和启动准备抛错等路径均由 Hosting 负责发布完成报告。准备或业务异常仍按原调用契约返回或抛出，清理错误另行记录。

完成报告只有一个发布者，发布责任在运行对象建立时从入口移交。调用前预检由入口发布（校验失败时全为 true）；运行对象建立后由它发布：正常、取消与准备失败路径由前台在有限预算内发布，或登记实际清理结束后的迟到发布延续。准备成功后、执行保护块之前的失败，以及执行或前台收尾的未预期失败，都不直接发布失败报告：运行对象先建立有限预算，再把可能同步阻塞的前台收尾安排成本次运行持有的真实任务；前台只在该预算内等待，未被等待完成的部分由同一个后台收尾任务继续等待，最终报告在组件实际退出、连接关闭与自有资源释放之后发布，前台保留原异常。安排或调度收尾本身失败也不改变调用方收到的原异常。最终问题快照由后台收尾任务在前台收尾实际退出之后取得，既不复制仍在修改的问题列表，也不遗漏该任务期间记录的迟到释放错误。前台结果快照（含受管操作耗时等取自借用依赖的事实）必须在允许后台收尾发布完成信号之前构造完毕：后台收尾一旦启动，前台不再读取借用依赖，避免提供方据完成信号释放依赖后前台仍读取它。快照构造失败按执行期未预期异常处理，仍由既有失败收尾安排清理并保留原异常，前台快照本身不因迟到事实改写。入口不再依据 `Completion` 是否已完成抢先发布，以免后台仍在运行时提前完成信号并覆盖真实报告；只有运行对象本身未能建立、即本次调用尚未创建任何资源使用时，入口才以失败报告结束等待。`Completion` 只表达实际使用结束，不兼作调度失败通知：连后台收尾任务都无法调度时不发布报告，信号保持待完成，如实表达本次使用尚未确认结束。

收尾步骤各自记录问题并继续后续步骤，因此单个组件释放失败不会跳过其余清理；执行保护块之外的未预期异常进入上述失败收尾安排，不越过收尾直接完成信号。前台收尾在停止整树之前，先对确认从未进入根启动流程的调用记录完成未启动回收与启动完成通知，使整树停止不必等待不会发生的启动结果；真正处于启动流程中的记录保持原状态，继续等待真实启动结果与进程回收。判断依据是运行对象自己的启动流程事实，不是进程句柄是否为空。

`RunAsync` 沿用各组件的有限等待。返回或抛错时，尚在执行的工作继续由 Hosting 持有。`Completion` 在外部工作接纳已关闭、全部已接纳使用者及其收尾派生工作实际退出、工作登记已封闭、Hosting 自有资源的释放已执行后完成。覆盖前台协调流程的依赖使用、进程退出观察与回收、连接循环、计时器与取消回调、策略通道读取、治理决策、结果与目录消费、进度与快照派发、stderr 采集、Trace 导出、诊断派发以及迟到延续；收尾期间产生的工作也纳入同一追踪。调用取消触发执行停止，完成信号继续按实际使用结束发布。

同步回调在返回前完成本次资源使用，异步回调的返回任务须覆盖其派生使用。Hosting 观察工作者和清理异常，待实际使用结束后以成功完成的任务交付报告，其中 `Issues` 逐项记录阶段、错误种类及脱敏说明；调用失败、清理失败与完成信号发布分别表达。仍有使用者运行时，信号保持待完成。

提供方在 `Completion` 完成后释放借用对象及回调捕获的资源，即使报告包含清理错误，此时本次调用对它们的使用已经结束。提供方可对观察等待使用自己的期限或取消信号；停止等待后继续持有资源，或安排自己管理的延续在信号完成后释放。共享给多次调用的依赖，须等相关句柄全部完成，并满足提供方自己的使用规则后释放。

### 3. 调用结果与各类收尾事实

`AbilityRunResult` 是前台返回时的不可变快照；`HostCleanupReport` 是 `Lifetime.Completion` 完成时的最终事实。后者包含下表的收尾字段、最终 `TaskTemp` 及 `Issues`，用于观察迟到变化。完成信号说明本次借用已经结束，报告各字段说明实际达成的处置。

| 字段或报告 | 确认条件 |
| --- | --- |
| `BusinessOutcome` | 已接受的技能终态：`SubmitResult`、`ResultClose` 或 `SubmitFault`；尚未接受终态时为 null。`Outcome` 保留宿主裁决的正常、故障或停止原因 |
| `ProcessExitConfirmed` | 权威调用树内所有已接纳启动均已结束，实际启动的根、子及后代进程全部确认退出；强杀请求、停止答复与根进程退出各自只提供对应事实。确未启动进程的路径在启动登记收口后按无进程确认处理 |
| `GovernanceResourcesReclaimed` | 治理接纳已关闭，所属调用的活动租约与治理归属已按退出事实完成结算；进行中的决策工作仍计入 `DeferredWorkCompleted` |
| `TaskTemp` | `TaskTempReport` 分别记录目录状态、消费、删除及保留原因，具体契约见第三节第 3 小节。按策略保留属于已达成的目录处置 |
| `DiagnosticsCompleted` | stderr 采集、Trace 导出、诊断派发及其回调和延续已实际结束。导出或回调错误写入 `Issues`；完成事实与诊断发送是否成功分别报告 |
| `DeferredWorkCompleted` | 其余已接纳工作及其派生清理均实际退出，包括回调、策略读取、计时与取消任务、连接循环、进程观察与回收、准备资源释放。迟到异常写入 `Issues` |
| `CleanupCompleted` | `ProcessExitConfirmed`、`GovernanceResourcesReclaimed`、`DiagnosticsCompleted`、`DeferredWorkCompleted` 均为 true，Hosting 自有资源释放成功，目录处置符合既定策略。目录消费、删除或归属持久化失败时为 false；按策略保留可为 true |

完成与失败由组件提供的完成事实、权威退出记录和运行对象自身释放步骤的结果判断，不从收尾问题列表的阶段字符串推导。问题列表只说明具体错误阶段与原因：真实释放失败使对应的清理字段不成立，诊断发送或通知类回调的异常只写入问题列表，不改变资源释放的判定。

尚未创建专属目录时 `TaskTemp = null`，该项按无目录待处置处理。调用失败时仍可完成全部收尾；诊断导出或通知回调抛错后，工作者的实际退出可令对应完成字段为 true，错误继续保留在 `Issues`。Hosting 自有资源释放失败则保持 `CleanupCompleted = false`。

`Success` 表示本次调用达到结果交付条件：宿主裁决为正常、接受正常业务终态、根进程实际退出码为 0、`ProcessExitConfirmed` 与 `GovernanceResourcesReclaimed` 为 true，且约定的结果消费成功。配置 `TaskTempConsumer` 时，其消费属于结果交付；按策略保留目录时，目录消费与删除均按该策略免于执行。目录删除和归属持久化错误、诊断发送错误及通知类回调的迟到异常，通过对应报告表达；其事实与已达成的结果交付分别判定。

目录消费失败或超时在原正常路径上判为 `ConsumerFault`，`Success = false`，并保留 `BusinessOutcome` 和已接收结果。已有业务故障或停止原因时沿用原 `Outcome`、`FaultKind`、`FaultMessage`，目录错误记录在 `TaskTemp` 与 `Issues`。业务一元结果与流元素始终保留实际接收事实。

根 Activity 在前台结果裁决时按 `Success` 设置为 Ok 或 Error 并结束；抛出准备异常时为 Error。导出及后台收尾按各自完成事实记录诊断，迟到事实进入最终报告，根 Activity 和前台快照保持原裁决。

| 前台返回场景（其余条件均已满足） | 公开事实及根 Activity |
| --- | --- |
| 业务正常、整树退出，按策略保留目录 | `Success = true`，退出与治理确认均为 true；全部后台工作结束后 `CleanupCompleted = true`。`TaskTemp` 为 `pending` / `RetainedByPolicy`，消费和删除均为 `NotRequested`；根 Activity 为 Ok |
| 目录消费失败或超时 | `Success = false`，`CleanupCompleted = false`，退出与治理确认保持真实值；`TaskTemp` 为 `pending` / `ConsumerFailed`，消费为 `Failed` 或 `TimedOut`，删除为 `NotRequested`；根 Activity 为 Error。尚在执行的消费回调继续计入迟到工作 |
| 目录消费成功，删除失败 | `Success = true`，退出与治理确认均为 true，`CleanupCompleted = false`；`TaskTemp` 为 `DeleteFailed`，消费为 `Succeeded`，删除为 `Failed`；根 Activity 为 Ok，删除错误另行报告 |
| 进程尚未确认退出 | `Success = false`，`ProcessExitConfirmed = false`，`CleanupCompleted = false`；相关记录与租约继续保留，目录为 `AwaitingProcessExit` 或 `OwnershipUnconfirmed`，消费与删除为 `NotRequested`；根 Activity 为 Error |
| 正常结果已交付，诊断或通知类回调仍在后台收尾 | `Success = true`，退出与治理确认均为 true；对应的 `DiagnosticsCompleted` 或 `DeferredWorkCompleted` 为 false，`CleanupCompleted = false`，`Completion` 待完成；根 Activity 为 Ok。必要结果消费仍待完成时，按消费期限及故障规则裁决 |

## 二、结果流与治理协调

1. **结果消费与信用归还（控制消息解耦、异步取消隔离与有界出站）**：流式任务中，Hosting 在 `Hello` 握手协商 `ResultCreditWindow`（范围严格为 `[1, 65536]`）。宿主创建通信连接时，入站接收通道容量必须对齐 `ResultCreditWindow + MaxPendingControlFrames + 64`，确保合法流元素生产绝不因容量瓶颈被误判为协议违规。主接收循环严禁同步等待结果项消费回调或无时限出站写出，必须将结果消费与控制消息分发解耦：`ResultItem` 交付给有界消费工作队列（容量与信用窗口对齐），由后台专属消费工作单元按序执行 `ResultItemCallback`（施加 `DefaultCallbackTimeout`）；单项回调的超时、父级取消或消费失败均统一接入后台受管理的异步取消执行任务，取消源不配置自动定时取消，外部定时器仅负责调度触发取消任务，杜绝定时器线程直接触发业务回调造成阻塞或未捕获异常；消费回调执行的取消触发与收尾退出由同步门（`cancelGate`）统一闭环管理：收尾阶段在门内标记取消登记关闭（`cancelClosed = true`）并捕获已排期的取消任务快照，迟到的定时器回调在门内直接丢弃，彻底消除定时器线程在取消源释放后并发调用 `cbCts.Cancel()` 导致的 `ObjectDisposedException`；最终通过 `Task.WhenAll` 等待回调与取消任务完全物理退出后才安全释放 `cbCts`；主循环严格核验消费任务状态，若 `resultConsumerTask.IsCanceled` 或 `IsFaulted`，立即判定为消费故障中止流，严禁误判为成功结束；仅在消费成功后记录消费数并向技能发送 `ResultCredit(grant: 1)`（施加有限出站超时）；消费回调失败时记录 `ConsumerFault` 并中止结果流。收到 `ResultClose` 结束通知后，消费工作单元排空已入队项后以本地实际接收的累计计数为真值，向技能端回发 `ResultClosed(itemCount)` 完成对账。主循环所有出站写出（含 `OperationAccepted`、`ResultCredit`、`ResultClosed`）均施加明确有限超时。ResultClose 开始时创建一个由 `TimeProvider` 驱动的 30 秒关闭等待，并在整个排空阶段复用；主循环退出时取消该等待、观察其退出，再释放对应取消源，未开始流关闭时不创建占位等待或计时器。外部诊断回调（`DiagnosticCallback`）通过有界异步隔离派发，且异常信息经安全脱敏，杜绝同步执行阻塞主消息循环或跨进程外泄敏感异常堆栈。无论成功、失败或取消，结果模型始终保留实际已接收的流元素数。
2. **整任务超时、取消与协同取消通信（停止原因分类与结果保留）**：握手超时覆盖 `Hello` 发送与 `HelloAccepted` 接收全阶段；协调器精确区分调用方取消、整任务执行超时（`ExecutionTimeout`）、受管操作无响应（`OperationUnresponsive`）与消费回调自身取消，保留已接收局部结果；需要停止技能任务时，宿主在进入 `CancelGracePeriod` 宽限期前限时（1 秒）向技能发送一次 `Cancel` 协同取消通知，保持通信直至进程退出确认；宽限期超时后通过 `ProcessGuard` 强制终止技能及其子进程树。主循环或执行出现异常或取消时，`ExecuteAsync` 内部捕获取消异常保留已接收到的局部结果，不裸丢异常，外层执行方法必须可靠保留已判定并捕获的 `Outcome`、`FaultKind`、`FaultMessage`、已接收流元素计数及最终操作快照，禁止被兜底异常重置为空白状态。终态操作快照派发必须在进程状态流转至实际物理退出之后统一由宿主收口进行，快照通道派发完成后方可关闭通道；未确认物理退出的操作保留未终结状态。
3. **临时目录消费**：整树进程完全退出且目录归属可信后，若未配置 `TaskTempConsumer`，宿主按策略保留 `pending` 目录供事后查验；若配置了消费回调，宿主按有限期限等待消费。消费成功并确认文件使用结束后再删除目录。消费失败或超时保留 `pending`，删除失败记录实际状态与错误；迟到回调继续由 Hosting 追踪。各类处置按第一节第 3 小节及 `TaskTempReport` 分别报告。
4. **进度与快照回调隔离派发与最终状态保证**：宿主收到 `Progress` 通知后，通过有界隔离通道与后台工作单元串行派发 `ProgressCallback` 与 `OperationProgressCallback`。`OperationSnapshotCallback` 同样通过隔离派发进行，避免在主消息循环中同步执行外部代码导致通信分发挂死。若收到的进度通知操作键为空（`string.Empty`），不查找操作表，直接派发全局进度并写入宿主通道；非空时核验操作所有者并更新快照。分别管理普通进度通道、操作快照/进度派发通道、策略更新任务的生命周期；策略更新任务持有独立取消令牌源，在收尾时主动取消并观察退出。派发通道的单次回调等待期限为 2 秒（`HostCallbackDispatcher` 内部 `DefaultCallbackTimeout`），流结果消费回调的单次等待期限为 30 秒（`HostStreamConsumer` 内部 `DefaultCallbackTimeout`）；两处同名常量表示不同业务期限，不得据同名统一配置，超时后的回调继续纳入实际退出追踪。即使仅配置操作快照回调也必须纳入独立收尾；收尾时先在进程确认物理退出后生成并派发最终操作状态，再结束派发通道；快照派发通道采用 `DropOldest` 丢弃中间高频突发快照，但最新最终状态在协调器中始终权威保留且可通过操作快照查询及 `AbilityRunResult.Operations` 可靠获取；为慢回调保留有限等待排空，并在回调超时发生时如实记录诊断报告并继续观察其实际后台任务的迟到异常。
   - **操作进度准入**：非空 `OperationKey` 的进度通知须先通过调用归属核验，再由已登记的 `HostOperation.ApplyProgress` 采纳；只有采纳的帧才更新快照并派发到 `OperationProgressCallback` 与 `ProgressCallback`。操作不存在、尝试未运行、操作已终态或序号重复/倒退的帧不派发外部进度回调。子调用通过 `ChildMessage` 转发的被拒帧仍须回发 `ChildMessageReply` 结束代理等待。空 `OperationKey` 表示会话级进度，不查操作表，保留派发到两个现有进度回调及宿主进度通道的行为。
5. **治理决策调度与资源跟踪**：
   - **统一请求生命周期与工作者精确登记**：治理协调器（`HostGovernanceCoordinator`）将任务关闭、批准登记和租约清空纳入同一套互斥锁同步裁决，杜绝“检查未关闭后清理完成、后台回调再新增租约”的窗口。三类审批（租约、补参、来源恢复）共用统一的请求生命周期管理逻辑：未决请求容量上限 256（超限回复 `ServerBusy`），并发执行容量（上限 64）受限控制；请求记录（`GovernancePendingRequest`）将初始化、使用者登记、结束裁决与最终释放纳入内部共同互斥裁决，消除初始化期间未决字典暴露与关闭/超时竞态：初始化期间建立超时与回调任务前调用 `TryRegisterWorker()`，若期间协调器关闭或提前超时置位已完成，则拒绝登记新工作者；决策任务的 `Task.Run` 坚决不传入 `sessionToken` 调度取消令牌，由任务内部局部响应取消并确保在 `finally` 块中严格执行 `OnWorkerExit()`；超时任务通过 `RequestTimeoutCts.Cancel()` 立即打断槽位排队等待，并在异步隔离任务中触发回调取消；异步取消任务自身作为真实工作者纳入生命周期追踪；当且仅当初始化完成、请求已裁决结束且实际使用者全部物理退出后，方可释放双取消源，彻底杜绝释放后继续登记或使用资源；关闭时原子中止未决请求，等待使用者退出后安全释放资源；回调返回 null 时保留有效等待直至超时触发；未配置回调时恢复各类请求的原有明确缺省答复（租约与补参保持有效等待至超时，来源恢复直接回复 Denied）；严格基于原子裁决返回结果确定裁决所有权与答复交付责任：当且仅当当前任务赢得裁决时才承担答复信封构造与交付；若超时任务或关闭流程已赢得裁决并发送 `RequestTimeout` 等答复，迟到的审批回调异常仅记录脱敏诊断与工作者归还，绝不追加答复、绝不改写裁决、严禁误将未赢得裁决的迟到异常视作交付失败而关闭底层连接；一旦当前任务成功取得裁决所有权（覆盖正常答复、`DecisionFault` 与 `RequestTimeout` 全部路径），构造、故障格式化、包装、发送和降级完整纳入异常保护，若交付管道发生任何逃逸异常或必要答复最终无法送达，可靠执行受管关闭以终结调用端挂起等待；治理诊断接入已有受管有界派发机制（隔离诊断失败与阻塞），严禁在必要处置前无时限同步等待 stderr 写入，诊断输出抛错或阻塞不得阻止故障降级、关闭连接或工作者归还。
   - **租约管理与双表原子性**：收到 `LeaseRequest` 时调度决策，批准回复 `LeaseGranted`，拒绝回复 `LeaseDenied`；未配置回调时保持有效等待。`LeaseId` 是授权实例标识，在同一任务治理协调器寿命内最多签发一次；已经签发的编号即使释放、撤销或子树退出也不得复用。协调器在 `_stateLock` 下保留已签发编号；重复编号明确回复 `LeaseDenied` 并保留既有状态。批准租约时先向权威调用登记表（`InvocationRegistry`）登记归属，再记录已签发编号并写入治理活动表；归属登记失败时拒绝且不占用该编号。自动生成的 GUID 编号路径保持不变。收到单条 `LeaseRelease` 时核验调用归属并仅移除指定租约，禁止批量清空所属调用的全部活动租约；迟到或重复释放不能命中不同授权。子树停止结果（`StopAsync` / `StopAllAsync`）在进程退出确认后立即交付治理撤销租约，严禁依赖答复信封发送结果；未确认完全退出的子树保留记录与租约。
   - **补参交付校验、有限发送与失败断连**：收到 `ParamRequest` 先用 Core 的 `JsonSchemaValidator` 解析 Schema，若 Schema 不支持立即回发 `RequestFault("SchemaNotSupported", ...)` 并保留解析原因，禁止按无 Schema 处理；未配置回调时保持有效等待；决策回调返回后，依据原请求严格核对答复形态（结构化申请严格匹配 `PayloadType` 并核验 `Value`/`Payload` 互斥；纯文本申请禁止携带 `PayloadType` 或 `Payload` 并要求 `Value` 存在）；构造最终 `ParamValue` 信封后，统一调用 Core 集中导出的 `IpcFrame.ValidateJsonElementBounded` 与 `IpcFrame.ValidateStringPayloadBounded` 校验（包含前置低成本无复制 Token 扫描识别超限大 Token，以及有界工作空间编码校验，超限及早拒绝并回复 `PayloadTooLarge`，杜绝无界大内存与大解转义数组分配）；答复交付发送施加独立有限预算（2 秒），覆盖等待出站名额与物理写出，杜绝审批结束后答复发送无限等待；将答复构造、逐层包装、本地编码拒绝、有限发送及失败处置纳入明确的交付生命周期流程，当前任务取得裁决所有权后，答复构造或包装失败不能阻止该裁决结果的失败处置；严格区分本地构造/编码/超限错误与传输受阻：本地错误（涵盖包装失败、`PayloadTooLargeException`、`JsonException`、`ArgumentException`、`NotSupportedException`）统一纳入本地发送拒绝分类，尽量尝试回发具有正确内外层协议结构的小型故障答复（内层小型 `RequestFault` 并由外层信封包装器包装为 `ChildMessageReply`，本地超限准确回复 `ResponsePayloadTooLarge`，编码或非法参数回复相应故障码，兜底发送预算 1 秒），杜绝单次本地超限答复误判为连接失效而破坏整个技能会话；若连小型故障帧亦无法包装或写出，或传输超时、管道损坏，则判定为传输受阻并调用 `connection.CloseAsync` 终止连接，持有并安全观察关闭任务，确保技能端挂起等待及时终结。诊断日志通过非阻塞受管派发隔离失败与阻塞，杜绝因 stderr 写入抛错或阻塞妨碍故障降级与受管关闭。
   - **数据源恢复**：收到 `SourceRepair` 派发 `SourceRepairCallback`；未配置回调时直接回复 `Denied`；决策动作必须为 `Reread`、`RestartRequired`、`Denied`、`Cancelled` 之一，否则回发 `InvalidSourceRepairOutcome`；核验来源是否支持就地热更新（不支持且回发 `Reread` 时回发 `InvalidSourceRepairOutcome`）；通过后返回 `SourceRepairResult`。
   - **受管操作、期限与所有权强校验**：收到 `OperationRegister` 核验操作键唯一性及父操作归属，优先应用 `PolicyOverride` 覆盖策略（两端在初始登记、覆盖策略与动态更新路径统一复用 Core 的 `RunPolicyBody.Validate` 校验）建立 `HostOperation` 并回复 `OperationAccepted`。
     - 收到 `OperationExpired`、`OperationStarted`、`AttemptEnded`、`OperationEnd`、`OperationState`、`Progress`、`PolicyApplied` 时，严格基于 `InvocationRegistry` 核验操作所有者必须严格等于当前消息发起调用（`messageOriginKey == opOwnerKey`），祖先或后代中继关系不赋予改写操作权，所有权不符时记录审计诊断并安全丢弃；
     - 收到 `OperationExpired` 依据已知生效或待确认版本、来源和动作语义严格核验；匹配已知生效版本或待确认版本且动作为 `Stop`/`Cancel` 时更新快照中的 `StopReason = "Deadline"`，`Notify`/`Remind` 动作不修改停止原因；来源或动作不匹配时记录审计诊断；收到旧版本乱序通知明确忽略并记录诊断；收到未知操作或未知版本通知安全丢弃并记录诊断；
     - 收到 `OperationUnresponsive` 核验操作归属，更新状态为 `"Unresponsive"`，保留其实际故障原因（不归类为 `ExecutionTimeout`）；若属于子调用，仅联动停止该子调用子树，严禁直接取消整个根任务；若属于根调用交由进程护栏处理，按取消/无响应收尾；
     - 合法操作通知不得落入未知必选通知分支。
   - **动态策略路由与单次裁决**：动态策略更新下发时，依据受管操作登记的所属调用键进行路由：若属于根调用，直接通过根连接发送；若属于子调用，通过 `ProtocolMessages.CreateChildControl` 包装下发给直接父调用并由子路由中继转发。未决更新表容量受限（上限 64）并设置可配置确认超时（`PolicyUpdateTimeout`），由单次原子裁决保证确认与超时互斥；确认成功后采用目标策略更新 `HostOperation`。
   - **动态策略发送泵故障分类**：策略更新通道由独立工作循环持续读取，每条更新分别处理。未知或已结束操作、非法策略及未决容量拒绝产生脱敏诊断并继续后续更新。登记后若本地登记、构造或编码拒绝，按单次裁决移除该请求、取消其确认计时器并记录失败结果，不影响会话；实际发送超时或传输失败时先终结本条未决记录，再诊断并调用 `TriggerFaultDisconnect`。会话或策略泵正常取消只清理未决记录，不报告传输故障。通道异常关闭须诊断并触发受管断连；正常完成通道只结束工作循环。每次发送仍核验受管操作及其来源调用归属，并保持目标版本和 v3 策略转换语义。
6. **子调用控制转发与内外编号隔离**：
   - 宿主收到 `ChildMessage` 包装消息时，严格基于权威登记表逐层核验相邻包装层的父子归属（`current.ParentKey == parent.InvocationKey`），禁止跨分支越权欺骗；若目标调用未在活跃表中命中，回退查询历史权威记录（`TryGetHistorical`），祖先链核验（`IsDescendantOf`）支持跨越已回收历史节点的遍历；若属于历史已确认退出或回收的节点，必须按协议语义分类处理：迟到答复（`PolicyApplied`、`RequestFault` 等双向答复帧）与通知由宿主回发外层确认信封（`AckChildNotificationAsync`）幂等吸收以结束外层代理等待，绝不向答复帧回发 `RequestFault`；迟到双向新请求回发带内层原始编号的小型 `RequestFault("ChildAlreadyExited")` 答复终结等待；历史 `ChildStop` 按祖先归属与权威退出记录幂等返回真实 5 参数 `ChildReaped` 答复，绝不被通用历史分支吞掉；严禁抛出 `ProtocolException` 或误关闭根会话。
   - 允许转发的内层消息类型严格限定为治理、状态通知与组合控制请求（`ChildStart`、`ChildStop`、`LeaseRequest`、`LeaseRelease`、`ParamRequest`、`SourceRepair`、`OperationRegister`、`OperationStarted`、`AttemptEnded`、`OperationExpired`、`OperationUnresponsive`、`OperationEnd`、`Progress`、`PolicyApplied`、`RequestFault` 等，其中嵌套 `ChildStart` 为正常组合控制请求，禁止错误加入禁止转发清单）；严禁一元或流式结果帧（`SubmitResult`、`ResultItem`、`ResultOpen`、`ResultClose`、`SubmitFault`）混入控制转发。
   - **内外请求编号映射与代理回复闭环**：宿主调度标识在任务内全局唯一（由递增/递减代理请求号隔离不同子连接的内层 `id`），建立 `_proxyRoutes` 映射原连接、原内层 `requestId` 与外层 `outerRequestId`。内层通知（如 `Progress`、`LeaseRelease`、操作生命周期通知等）在宿主处理完毕后，若外层包装携带请求号，宿主必须向外层发送 `ChildMessageReply` 闭环结束外层代理请求，释放 Sdk 端转发配额；治理请求审批完成后回发包装答复时，严格使用客户端原始请求编号（`replyReqId = originalRequestId ?? requestId`），宿主内部 `dispatchId` 仅用于内部登记与调度，严禁泄露至客户端内层响应信封，逐项覆盖正常、拒绝、超时、构造答复失败、回调取消、回调异常、容量满载与关闭分支，确保内层请求号及外层代理请求号分别正确，覆盖根调用与多层子调用，防止误匹配其他未决请求。
   - **主消息循环解耦与退出接线**：主消息循环收到 `ChildStart` 与 `ChildStop` 时，严禁同步 `await` 阻塞分发，必须派发至有界后台任务池（总容量 64，保留专用控制名额 16，关闭后拒绝）异步执行，并在完成后回发答复；`HostChildCalls` 构造时接入 `processExited` 回调，子进程自然退出时调度 `ReapExitedTreeAsync` 异步回收子树并由宿主持有该退出任务并联动撤销租约；PID 登记失败时严禁吞掉异常，必须阻断启动并回收进程。同一调用的停止工作合并共享单一任务，关闭后所有工作必须纳入收尾，绝不通过无界 `Task.Run` 逃逸。
7. **入站消息分组与执行状态归属**：`HostExecutionCoordinator` 是本次执行状态的唯一所有者。主循环按结果协议、子调用、治理与受管操作、观测四组处理入站帧，分组只用「未处理（交给下一组）」「已处理（接收下一帧）」「结束主循环」三种处置表达；未识别帧在四组均未处理后由原协议规则统一判定（带请求编号回发 `UnsupportedRequest`，可选通知忽略，未识别的必选通知判为协议违规）。结果、故障、输出与流关闭事实由主循环按帧顺序写入，异常分类只在终态仍为 `Unknown` 时补判，收尾步骤只释放工作者、不改写执行状态，后台工作者只能读取这些事实。工作者准备与消息循环同处一个 `try` 保护内；`finally` 保留流关闭等待的结束、消费与策略取消源在实际使用者退出后释放、内部后台任务排空与完全物理退出登记；收尾之后仍按未定终态补判断连与取消分类，再构造结果快照，不把终态判定全部提前到收尾之前。分组只组织处理顺序，不建立消息处理框架、服务查询、通用上下文对象或第二套运行状态。

## 三、权威调用登记、进程护栏与目录标记

消费方提前退出或取消结果流时，Hosting 按既有协同取消、宽限期和护栏规则停止生产进程，并等待退出及子树回收确认。调用结果按第一节第 3 小节分别保留业务终态、实际退出及各类清理事实；尚未确认退出的记录及租约继续由 Hosting 管理。生产端枚举器释放失败或超时按故障处理，Sdk 的正常终态仍须满足业务枚举器和会话必要收尾条件。

### 1. 权威调用登记表与生命周期状态机（InvocationRegistry）
- `InvocationRegistry` 维护任务内唯一的父子调用、实际进程和资源归属记录，并维护历史权威记录索引。
- 状态机严格单调流转：`Registered` → `Starting` → `Running` →（可插入 `StopRequested`）→ `Exited` → `Reaped`。
- **并发锁与共同裁决**：登记、子树遍历、状态转换与租约归属查找共用同步互斥锁；启动登记与任务关闭在锁内原子裁决；无进程启动失败（如满载拒绝）在锁内合法、幂等地推进至 `Reaped` 终态并触发 `CompleteLaunch()`。
- **调用键全历史唯一性**：调用键唯一性检查必须覆盖活动记录与历史记录（`_records` 与 `_historicalRecords`）；同锁内原子校验，多表更新严禁残留半登记状态；拒绝调用的请求不得进入活动树。
- **启动生命周期闭环**：所有调用记录（含根调用与子调用）启动流程均须置位 `CompleteLaunch()`（无论启动成功、失败、PID 登记失败、满载拒绝或取消），使 `WaitForLaunchCompletionAsync` 必定结束，杜绝收尾等待死锁；建启失败由护栏在回收前推进至终态，外部宿主不得重复标记启动失败。
- **权限与归属严格校验及历史权威回退**：`ChildStop` 请求核验来源调用权属，只能停止其所属子树范围内的目标节点，禁止跨分支或停止根节点；若目标节点在活动表中未命中，必须回退查询权威历史记录（`TryGetHistorical`），祖先链核验（`IsDescendantOf`）支持遍历历史父级；确认为当前来源后代的已退出/已回收节点时，幂等回发 `ChildReaped` 确认答复（携带其真实退出码与 `"Reaped"` / `"Unresponsive"` 状态），严禁报协议损坏。
- **子树停止与租约结算**：`RequestStopSubtree` 原子取得子树并置停止标记；`TryRemoveReapedTree` 仅在整棵子树均达到 `Reaped` 终态时才从活动表移除，整树已移除时幂等返回；未确认退出的进程保留记录与租约。
- **历史记录与退出事实归属**：从历史权威记录中获取操作所属调用的真实退出事实（`ExitCode` 与 `ExitedNaturally`）；严格区分已移除子调用与不存在的调用；治理操作登记与调用归属登记必须协同原子化，归属登记必须检查唯一性，严禁静默覆盖已有归属，失败时仅回滚本次新增登记；收尾遇到缺失所有者或历史记录不存在的操作明确保留未知事实并记录诊断，严禁调用进程退出收口方法伪造终态。

### 2. 启动准备、进程树护栏与子调用管理（LauncherPreparation, ProcessGuard & HostChildCalls）

启动准备成功返回后，任务协调者接管准备资源并在进程退出后释放；返回前失败由准备组件清理。公开接入及准备契约须注明这些移交条件，借用的宿主依赖继续由原所有者管理。

- `LauncherPreparation`：分析 `LauncherPath`，对于 `.dll` 自动组装 `dotnet <dll>`，对独立可执行文件直接启动；追加 `--ipc <pipeName>` 参数并传入工作目录与环境变量。（注：源码目录直接运行 Launcher 方案列入阶段 2.5 实施，本阶段支持独立可执行文件与 `.dll` 二进制准备）。
- `ProcessGuard` 与根调用保护：
  - 启动进程并注入环境变量（`AbilityInvocation.EnvironmentVariables`），异步排空 stdout 与 stderr，stderr 限制缓冲容量（缺省 64KB），防止管道缓冲区死锁。
  - 同步诊断回调与输出排空严格解耦：通过有界独立通道异步派发诊断行，隔离外部回调阻塞与异常，防止挂死输出流排空。
  - 跨平台通过 `Process.Kill(entireProcessTree: true)` 回收整棵子进程树，防止遗留孤儿进程；强杀后的退出等待施加有限时限（3 秒超时等待），两个输出流任务（`StandardOutputTask`、`StandardErrorTask`）排空均施加有限时限（3 秒超时）；超时先返回已收集快照（`GetCurrentText()`），未结束的排空任务继续追踪，其实际退出与资源释放计入 `Completion`。
  - 退出结果明确返回退出是否已确认（`Confirmed`，根据 `Process.HasExited`）、是否实施强杀（`Killed`）、实际退出码（`ExitCode`，未确认退出时为 null）与诊断文本（`Stderr`）；强杀等待超时且进程仍存活时明确标记 `Confirmed = false`，保留未完成清理状态与后续处置责任，宿主不得提前按死亡进程注销资源或回收租约；自然退出必须基于 `Confirmed && !Killed` 准确判定。
  - **根进程启动起建立完整回收保护**：宿主配置校验（`HostRuntimeOptions.Validate`）严格对齐 .NET 11 `CancellationTokenSource(TimeSpan, TimeProvider)` 的实际范围限制 `ExecutionTimeout` 时间上限（毫秒数 `<= (uint.MaxValue - 1)`，约 49.7 天，杜绝计时器溢出异常）；根进程一旦启动成功，其后的全部初始化（CTS 创建、命名管道连接、握手交互与输入下发）必须全部包裹在整树回收的 `try/finally` 块之内，任何初始化异常均必定触发 `ProcessGuard.StopAsync` 与整树物理回收，彻底杜绝孤儿进程遗留。
  - **初始 TaskInput 下发施加独立有限预算**：下发 `TaskInput` 必须使用独立有限时限（对齐 `options.HandshakeTimeout`，默认 5 秒）并链接全局取消令牌；若对端未及时接收超时，明确归类为 `TaskInputSendTimeout`，下发故障归类为 `TaskInputSendFault`，立即取消发送并触发统一整树回收与进程强杀，杜绝无界挂起。
- `HostExecutionCoordinator`：
  - 后台任务槽容量接纳与子调用登记共同原子裁决。总容量 64，保留 16 个专用控制名额供通知确认、`ChildStop` 与无响应治理调度。
  - **有界接纳与工作不丢弃**：所有停止工作、合并答复等待者与通知确认均须有界接纳。合并请求的答复等待者同样纳入总容量和收尾追踪；预留名额耗尽时严禁仅写日志，必须确保必要工作继续由受管所有者执行（通知确认由消息循环在有限时限内直接写出；无响应停止在满载时入队暂存并在工作槽释放时调度），或明确向发起方回发 `ServerBusy` 等失败答复终结等待。
  - **必要答复交付与受管故障断连**：通知确认或请求答复无法交付时，明确终结相关等待。区分本地编码/包装拒绝与底层传输失败（超时或连接损坏）；本地包装失败尝试发送具有正确内外层协议结构的小型故障帧（内层为小型 `RequestFault`，外层保持正确路由包装），若仍无法发送或发生传输超时/损坏，严禁仅记录日志并维持循环正常运行，必须进入受管故障断连流程（取消协调器生命周期并主动触发连接关闭，持有并观察关闭任务），终结主消息循环并使终态分类为 `Disconnected`。
  - **停止任务生命周期与容量追踪完整性**：停止任务将停止、撤销租约、结果发布与答复写出完整纳入最外层 `try/finally` 块；在答复交付完成前绝不提前归还后台容量或从追踪集合中移除；共享完成源（`stopTcs`）无论是否有合并等待者均挂接异常观察延续，杜绝未观察异常。
  - **无响应停止排队池有界、去重与受限紧急回收责任**：排队暂存池具有容量上限（32），按调用键合并去重；区分“关闭新接纳”与“继续调度已接纳工作”；后台池与排队池双满载时严禁仅写日志丢弃工作，必须由受限紧急停止通道（上限 16）由受管所有者立即执行异步强杀与租约注销；若极端情况下紧急通道亦饱和，则触发受管故障断连促使宿主退出收尾，由 `AbilityHost.StopAllAsync` 统一整树物理回收，确保无响应子进程必定有所有者执行回收，完整保持子调用无响应隔离语义；收尾时在容量许可内调度排队项，若仍超出总容量，则清空暂存池并明确移交宿主 `StopAllAsync` 统一整树回收，严禁无界缓存与收尾死循环。
  - **合并停止真实结果共享与历史消息收敛**：针对同一调用树的并发 `ChildStop` 共享同一个底层停止任务；停止任务必须返回包含真实退出码、实际回收状态（"Reaped" 或 "Unresponsive"）与自然退出标志的真实执行结果（`ChildStopResult`）；每个合并等待者用自身的请求号包装同一份真实结果，杜绝默认伪造回收状态；共享任务异常时所有等待者均显式接收失败答复，并观察共享完成源异常。对于已移入历史表的子调用消息，严禁一律作为通知返回空确认：必须严格保留禁止转发结果帧检查；合法迟到通知执行幂等确认，需要内层答复的双向请求返回带正确请求编号的小型故障答复并包装外层路由；历史 `ChildStop` 答复必须如实返回权威退出事实（保留真实 `ExitCode`、`ExitedNaturally` 与清理租约数 0），绝不重新授予已退出调用任何资源，亦不伪造自然退出。
  - 调用方主动取消（`callerToken`）与任务执行超时（`executionCts`）解耦传入；无论是流消费任务退出、入站读取取消还是循环退出，均通过统一裁决逻辑分类终态原因（超时统一为 `ExecutionTimeout`），杜绝返回 `Unknown` 破坏真实分类；完整保留操作无响应根因。
- `HostChildCalls`：
  - 处理 `ChildStart`：核验调用键、TaskId 与父调用接纳能力，向注册表登记；以 `BuildTimeout` 和 `TimeProvider` 建立与宿主取消及记录停止令牌相连的启动预算，在 Launcher 准备前、同步准备后及 `Process.Start` 前检查取消，并将令牌传给 `ProcessGuard.StartAsync` 在原生启动前、进程绑定后检查。`LauncherPreparation.Prepare` 与 `Process.Start` 是同步操作，预算仅在协作检查点生效，不承诺中断系统调用；若启动调用已返回进程，必须先将进程绑定权威记录，再按停止流程物理回收，之后才能完成启动收口或返回失败。将 PID 登记入任务目录（登记失败立即回收进程，绝不吞掉异常继续返回成功）；挂接退出观察任务并触发 `processExited` 回调。
  - 处理 `ChildStop`：等待子树建启收口，使用接纳时持有的权威记录生成响应，防止并发移除导致查询抛错；并发停止各层进程，反向记录进程回收，汇总清理关联租约（仅收集确认处于 `Reaped` 状态的调用租约），删除构建产物并回发真实的 `ChildReaped`。
  - 区分有限停止尝试与最终物理退出：仅当调用记录成功推进至 `Reaped` 时才永久缓存回收结果；若强杀未确认退出，不缓存结果，允许迟到自然退出继续完成回收与租约清退。
  - 收尾时提供 `StopAllAsync` 整树停止并返回可撤销租约；主消息循环识别子进程自然退出并调度 `ReapExitedTreeAsync`；宿主在整树所有记录均确认 `Reaped` 后方可执行整树资源回收。同一调用的停止回收结果由固定所有者复用，防止并发停止造成租约泄漏。

### 3. 任务专属临时目录、结果消费与遗留清理（TaskTempDirectory）
- 在任务启动前于根目录下创建隔离的专属临时目录（路径附加随机后缀）。
- 写入 `task.owner.json` 标记文件，版本为 2，记录宿主 PID、启动时间与状态：
  - 初始状态为 `active`。
  - 技能进程启动后调用 `RecordProcessId` 登记 PID；若标记持久化失败，将内存与磁盘标记转为 `unconfirmed` 并抛出 `TaskTempOwnershipException`，强制回收该进程并保护目录不被自动删除。
  - 技能进程退出后，转为 `pending`。
  - `TaskTempConsumer` 执行成功后，转为 `releasing` 并删除目录。
  - 删除失败时保留实际目录及 `releasing` 状态，报告删除错误；标记写入失败时分别报告内存状态与磁盘持久化错误。
  - 整树退出且归属可信后，未配置消费回调、消费失败或超时，以及按故障策略保留时，目录保持 `pending`。标记持久化失败时保留实际状态并按归属保护规则处理，原有 `unconfirmed` 状态继续保护性保留。
- 标记写入异常明确捕获并反映在报告的 `MarkerPersisted` 与 `MarkerError` 中，内存状态与持久化状态分别表达，杜绝空 catch。
- 并发调用 `FinalizeAsync` 共享并等待同一个收尾异步任务。
- 恢复阶段 2.3 的安全取消与生命周期范式：消费回调与异步取消任务由同步门控统一裁决，双退出后安全释放 CTS，显式观察迟到异常。
- `CleanupLeftovers(options)` 遗留目录自动清理（向宿主公开）：
  - 扫描临时目录根下的所有子目录；
  - 严格反序列化校验标记有效性：包括协议版本为 2、宿主 PID 为正整数、状态字符串合法且持久化状态为 `releasing`，以及 `processIds` 数组属性必须存在且包含非负正整数（缺失 `processIds` 属性严禁当成空列表接受，必须拒绝清理并保留目录）；
  - 仅当记录的宿主进程与所有技能进程均已确认退出时，才执行删除；
  - 状态为 `active`、`pending`、`unconfirmed`、标记缺失、`processIds` 缺失或进程仍存活的目录均严格保留并输出审计诊断。
- 生成 `TaskTempReport` 随 `AbilityRunResult` 返回。目录收尾异常时，报告取 `TaskTempDirectory.Report` 的实际快照并把异常记入问题列表，不构造新的持久化或处置状态，也不伪造持久化结果。

`TaskTempReport` 须公开下列事实。返回报告与最终收尾报告各自保存当时快照；迟到消费完成沿用本次已裁决的失败处置，目录保持保留，后续处置由显式消费或遗留清理决定。

| 字段 | 含义与取值 |
| --- | --- |
| `string State` | 内存中的目录标记状态：`active`、`pending`、`releasing` 或 `unconfirmed`；删除成功另由 `DeletionStatus` 确认 |
| `string ConsumptionStatus` | `NotRequested`、`Pending`、`Succeeded`、`Failed` 或 `TimedOut`；超时裁决后仍在执行的回调计入迟到工作 |
| `string DeletionStatus` | `NotRequested`、`Pending`、`Succeeded` 或 `Failed`；`Succeeded` 表示目录实际删除 |
| `string Disposition` | `RetainedByPolicy`、`Deleted`、`ConsumerFailed`、`DeleteFailed`、`AwaitingProcessExit` 或 `OwnershipUnconfirmed`，说明本次处置 |
| `string? RetentionReason` | 保留原因；按策略保留、消费失败和安全条件待确认分别说明 |
| `bool MarkerPersisted`、`string? MarkerError` | 内存状态是否已可靠持久化及对应错误；目录归属未知时采取保护性保留 |

`RetainedByPolicy` 与 `Deleted` 表示目录处置已满足策略；前者保留 `pending` 供明确的后续接管，并按既有遗留清理规则处理。`ConsumerFailed`、`DeleteFailed` 和归属持久化错误使 `CleanupCompleted = false`。退出或归属仍待确认时，消费与删除保持 `NotRequested`，原目录、记录和租约保留至相应安全条件成立。

## 四、诊断与组合接入

- **宿主根活动与 W3C 追踪上下文透传**：`AbilityHost.RunAsync` 启动宿主根 Activity（`Ability.Run`），将其实际 `traceparent` 与 `tracestate` 写入 `TaskInput` 信封透传至技能进程；根 Activity 按第一节第 3 小节的 `Success` 判定结束，策略保留目录与诊断后台排空分别按其实际事实报告；任务收尾时（包含启动失败与早退分支）统一将该根活动传入 `HostTraceCoordinator.CollectAndExportAsync` 完成全链路追踪导出。导出与派发实际结束纳入 `Lifetime.Completion`。
- **全链路诊断隔离与派发收尾**：宿主全链路诊断输出（包括进程护栏、生命周期异常、收尾超时与 Trace 导出）统一通过 `HostCallbackDispatcher` 异步有界通道隔离派发，杜绝外部慢回调卡死消息循环或收尾；派发器创建成功后即建立全程受管的生命周期保护（实现 `IAsyncDisposable`），在整个宿主启动准备、执行与收尾全过程中受管；无论是正常完成、启动准备失败（包括 Launcher 不存在、专属临时目录创建失败、命名管道创建失败等），均由唯一收尾责任可靠执行 `CompleteAndDrainAsync` 排空收尾，具备严格幂等性以消除重复排空与重复释放；释放过程隔离收尾异常，确保原始准备异常或业务异常完整向外抛出而不被清理错误覆盖；派发器收尾时先排空进度与快照通道，最后关闭并排空诊断通道；收尾时必须先确认生产者不会再增加：先关闭快照与进度通道并等待派发循环物理退出，再汇聚集合中剩余的回调延续任务；调用方保持有限等待（1 秒），未完成时交由后台展开持有任务等待全部派发循环与剩余回调延续完全物理退出后再关闭诊断通道，严禁使用单次早期静态快照遗漏后来登记的生产者；异步延续必须展开、持有并观察异常；诊断工作者的取消令牌源（CTS）释放严格以工作者实际物理退出为前提，杜绝提前释放导致 `ObjectDisposedException`。每次快照/操作进度与进度回调的迟到延续各自持有两个取消源（单次回调期限源与关联工作者令牌的关联源），延续分别尝试释放两者，前一个释放抛错不得跳过后一个；释放异常记录 `DispatcherDisposal` 问题并置位释放失败事实，由运行对象自有资源判定读取。回调自身抛出的异常仍只记入问题列表，不置位释放失败事实；两类事实分别表达。
- **stderr 解析与有界收集**：`HostTraceCollector` 收集 stderr 诊断，结构化记录上限 4096 条，普通文本上限 256KB；单行超长截断，畸形行降级为普通诊断；兼容接受 SDK 标识为 `AbilityFramework` 与 `SkillIpcMvp` 的记录；排空等待施加明确有限超时；严格校验 TraceId（32位小写十六进制且非全零）与 SpanId（16位小写十六进制且非全零）格式长度，保留并传递 `tracestate`。
- **OTLP/HTTP 导出**：`HostTraceExport` 使用纯 BCL `ProtoWriter` 编码 protobuf，直接通过 `HttpClient` POST 导出，无外部 OTel 库依赖；未闭合 span 自动补齐为中断状态（Error，固定中断说明）；`Activity.Kind` 按标准 OTLP 映射；日志按 category 分组为 ScopeLogs；导出故障通过诊断报告，不影响业务结果；诊断回调异常脱敏，禁止裸 `ex.Message`。
- **参考宿主与组合扩展**：参考宿主、开发工具与 Aspire AppHost 放在 `tool`，通过公开 API 接入。它们提供决策和展示，保持与生产宿主相同的框架调用方式。文件技能的多入口组合通过 Sdk 与 Hosting 通信，项目引用仍遵循总规格。

相关验收见 [阶段二验收](verification/phase-2.md)。
