# Ability.Sdk 项目规格

Sdk 为文件技能提供接入门面和调用能力。模块组织见 [architecture.md](architecture.md)，共享契约见 [core.md](core.md)。业务侧只需引用本项目；入口与角色的使用方式引用[全局接入方案](../../../Docs/技能接入协议与SDK方案.md)第三节。

## 一、公开接入 API

| 门面 | API 与输入输出 |
| --- | --- |
| `AbilityEntry` | 一元 `RunAsync<TInput,TOutput>` 接收启动参数、处理委托、两端 `JsonTypeInfo<T>` 及可选输出类型名，返回进程退出码 `Task<int>`<br/>流式 `RunAsync<TInput,TOutput>` 接收流式委托 `Func<TInput, ExecutionContextMdl, IAsyncEnumerable<TOutput>>`，返回 `Task<int>`<br/>`MeasureResultBytes`、`MeasureResultItemBytes`、`MeasurePayloadBytes` 提供自查尺寸接口 |
| `AbilityEnvironment` | `Current` 访问当前执行环境上下文模型；`GetTaskTempPath()` 获取宿主注入的专属任务目录；一元与流式 `RunAsync` 在派生环境中执行委托并自动恢复外层环境 |
| `AbilityRuntime` | `LeaseAsync`、字符串与结构化 `RequestParamAsync`、`RequestInputAsync`、`RequestSourceRepairAsync`、`RegisterOperationAsync` 与 `ReportProgressAsync` |
| `AbilityCall` | 一元 `InvokeAsync<TInput,TOutput>` 和流式 `InvokeStreamAsync<TInput,TOutput>`，接收 Launcher、输入、两端类型元数据及取消、时间、可选进度条件 |
| `AbilityLogging` | 创建 `ILogger`，提供可接入标准日志接口的 Factory／Provider |
| 敏感资料入口 | `SensitiveDataStore.ReadSnapshotAsync` 读取快照；`SensitiveDataStore.ReplaceAsync` 受控写入；`SensitiveDataStore.ReplaceStandaloneAsync` 离线管理 |

入口处理委托签名：
- 一元：`Func<TInput, ExecutionContextMdl, Task<TOutput>>`
- 流式：`Func<TInput, ExecutionContextMdl, IAsyncEnumerable<TOutput>>`

业务类型始终由业务程序集定义，Sdk 通过调用方传入的 `JsonTypeInfo<T>` 完成编解码，输入输出类型名默认采用对应类型的简称。业务输入由业务模型承载；业务输出说明修正为符合序列化与 IPC 契约的数据结果，适用模型与 DTO 均可作为 `TOutput` 和流元素。

业务流程形参仍只能是角色，调用方通过签名即可明确流程直接需要的能力；Launcher 处理委托接收 `TInput` 和 `ExecutionContextMdl` 属于 SDK 接入契约，不能混淆两者职责。流程与 Launcher 均允许直接读取已取得模型或 DTO 的公开数据，无需为属性读取设立冗余角色；模型受控修改仍由所属适配器承担。角色提供数据优先采用只读属性；功能性查询、范围读取、独立快照及外部 I/O 等方法继续保留。

DTO 的归属、命名、从属结构、直接子流程返回类型引用与资源职责引用[全局架构规约](../../../Docs/文件切片与角色协作架构规约.md)第三、四节：DTO 与生产它的流程同文件共址，命名为业务名称加 `Dto` 后缀，从属结构在内部嵌套并使用局部语义名称，允许引用直接调用的子流程返回的 DTO 类型；DTO 仅承载纯数据，禁止承担释放职责，禁止实现 `IDisposable` 或 `IAsyncDisposable`；DTO 引用模型不改变被引用模型的资源所有权。协议消息体、配置、运行状态或内部辅助类型属于基础设施，按职责组织在各自模块，不机械套用业务 DTO 的文件命名与共址规则。

区分进程内具名元组与 IPC 输出：进程内若干独立结果可由业务流程组成具名元组传递；Launcher 入口可按内聚语义将结果组装为 DTO；直接交付其他结果类型必须有对应源生成元数据和序列化证据，不得承诺任意元组、任意类型或任意字段配置都能完整往返。

业务处理方法优先在内部完成 I/O 并释放自有局部资源，向入口返回承载数据、绝对路径或 URI 的模型或 DTO。进程内确需返回可释放模型时，返回类型公开表达释放能力，方法注明所有权移交条件；IPC 仅传递数据及资源引用，本地句柄继续由原进程管理。释放业务模型的责任属于业务流程或 Launcher，Sdk 按数据契约编解码，并管理自己取得的枚举器和基础设施资源。

公开 API 的 XML 注释须说明以下所有权约定，创建或准备失败时清理尚未移交的资源：

| 对象 | 使用与释放责任 |
| --- | --- |
| 外部传入的执行上下文、时间来源与进度接收端 | Sdk 借用；原所有者管理其寿命。Sdk 创建的关联取消源由 Sdk 释放 |
| `AbilityLease`、`ManagedOperation` | 调用方用 `using` 或 `await using` 管理；Sdk 按下节契约追踪活动对象、释放通知及会话收尾 |
| `SensitiveDataSnapshot` | 成功返回后由接收方释放；返回前失败由资料组件清理自有缓冲 |
| Sdk 创建的连接、调用会话及枚举器 | Sdk 管理；释放条件按实际使用者退出与调用收尾规定执行 |

业务方法借用已有可释放模型或返回原实例时，沿用原所有权约定。进程内流式生成并移交的独立可释放模型由接收方逐项释放；业务处理方法将其转换为数据结果（模型或 DTO）后再交给 IPC。

`AbilityEntry` 提供的自查计量接口直接复用 Core 的底层实现：
- `MeasureResultBytes<TModel>(TModel model, JsonTypeInfo<TModel> jsonTypeInfo, string? typeName = null)` 测量一元结果完整信封字节数。
- `MeasureResultItemBytes<TModel>(TModel model, JsonTypeInfo<TModel> jsonTypeInfo)` 测量流元素完整信封字节数。
- `MeasurePayloadBytes<TModel>(TModel model, JsonTypeInfo<TModel> jsonTypeInfo)` 测量业务载荷（模型或 DTO）原始 UTF-8 JSON 载荷字节数。

## 二、能力组件的调用契约

### 1. 资源租约与句柄管理
租约 API 返回实现 `IDisposable` 的 `AbilityLease` 句柄，内部通过 `LeaseManager` 统一追踪活动租约与释放调度。宿主拒绝租约时抛出带脱敏信息的 `LeaseDeniedException`。
调用方在完成业务 I/O 后通过 `using` 释放租约。容量约束覆盖等待和执行的总数量（总在途上限 256）：采用原子预留与登记，超限时原子回退、记录失败状态并直接返回拒绝（返回 void，不抛未捕获异常崩溃应用）；所有已接收释放请求必须先取得可追踪的所有权，再开始执行；普通释放、迟到批准释放与 `DisposeAsync` 收尾释放共用登记与结果追踪；收尾释放失败真实计入失败状态，杜绝静默吞掉后清空记录并误报成功；任务本身在退出块中负责按序记录失败、移出追踪、归还计数并在归零时通知等待源；排空接口采用统一的状态等待机制（`_idleTcs`），直接等待在途归零通知，消除 `Task.WhenAny` 与任务集合等待遗留的未胜出信号及忙循环，可靠响应期限超时和取消，仅当所有在途释放任务排空且无历史失败时才报告成功；排空与并发登记明确裁决规则：排空期间在途计数未达上限时允许正常并发登记并统一等待归零，超限则原子拒绝；会话与句柄释放使用有限超时取消，杜绝使用 `CancellationToken.None` 无限等待。
申请遇到本地取消与宿主批准竞速时，已取得租约同步释放；迟到到达的 `LeaseGranted` 答复由单一处理器拦截，登记并观察其释放任务，纳入会话收尾。

租约管理受管访问权限，本地客户端或连接池由其所有者管理。单次访问及依赖租约的清理完成后释放租约，可复用对象在自身使用期结束时释放。宿主在同一任务治理会话内签发的 `LeaseId` 唯一且不复用；释放只结束该编号对应的授权，不使编号可用于新的授权。资源引用或资源名称应放在申请体中，不能用作可重复分配的 `LeaseId`。进程退出后的新执行重新申请租约，并由宿主签发新编号。

### 2. 参数补充与来源恢复
补参描述模型 `ParamRequestMdl`（置于 `Ability.Model` 命名空间，声明为具体非泛型 partial 类）包含 `Key`、`Hint` 和可选 `Schema`、`UiDescriptor`。
- 文本补参重载返回 `Task<string>`；
- 结构化补参重载接收 `JsonTypeInfo<T>` 并返回 `Task<T>`（返回模型或 DTO 等业务数据）。
两端各自复用 Core 的 `JsonSchemaValidator` 进行校验。异常体系包括 `AbilityRequestFaultException`、`AbilitySchemaViolationException` 与 `AbilityDisconnectedException`，统一实现 `Ability.Core.ISafeFaultDescription`。
上报故障时保留 `AbilityRequestFaultException.Kind` 和发生点 `Site`，导出的错误信息仅使用经审查的 `SafeDescription`，缺省使用固定安全摘要，严禁直接导出原始 `Message`。
敏感补参支持列为待实施需求，当前仅支持普通文本与结构化补参。
来源恢复接口 `RequestSourceRepairAsync` 返回 `SourceRepairOutcome`（`Reread`、`RestartRequired`、`Denied`、`Cancelled`）。Sdk 校验答复与来源能力一致；单次失败不升级为会话损坏。

### 3. 受管操作、运行策略与限频进度
操作登记 API `RegisterOperationAsync` 返回实现 `IAsyncDisposable` 的 `ManagedOperation`，调用方用 `await using` 管理操作。登记期间发布的 `pendingTcs` 仅供并发策略更新等待操作就绪；登记任务失败时，即使当时没有策略更新等待者，也必须观察该辅助完成源的异常，同时保持登记调用方收到原异常。
- 公开策略 `RunPolicyMdl`（置于 `Ability.Model` 命名空间，声明为具体非泛型 partial 类）使用 `MaxRetries`（重试上限，默认 0）；线协议转换为 `MaxAttempts = checked(MaxRetries + 1)`，严格校验数值范围（`MaxAttempts >= 1`）。
- 策略动作支持公开 `DeadlineAction.Remind` 与 `DeadlineAction.Cancel`，线协议与旧 v3 兼容双向映射（`Remind` 对应 `Notify`，`Cancel` 对应 `Stop`）；非法动作或锚点明确失败，禁止默认值掩盖。两端策略合法性校验统一复用 Core 的权威规则（`RunPolicyBody.Validate`）。
- 动态策略更新回复 `PolicyApplied.Version` 始终指向原请求的目标生效版本；仅在裁决为 `Applied` 时更新生效策略，而在 `Rejected`、`Ended`、`Stopped`、`Expired` 或 `Stale` 拒绝裁决时同样以原请求目标版本回复，确保宿主端准确对账。
- `ExecuteAsync<T>` 接收本次尝试的 `ManagedOperationContext`（包含局部有效令牌、时间、进度、尝试序号和到期状态）。单次尝试超期由局部令牌生效，期限到达后立即取消当前尝试，通知写出不作为取消的前提。
- 操作状态通知（`OperationStarted`、`AttemptEnded`、`OperationUnresponsive`、`OperationEnd`）的写出均设置有限期限，杜绝因对端未读取导致无限阻塞。
- 尝试结束与进度发送严格遵循共同的顺序裁决排他门控：将门控等待、进度写出及尝试结束写出均纳入有限等待；`SendAttemptEndedAsync` 进入时原子核验通道运行状态，已关闭时拒绝进入；未取得门控时严禁直接执行发送委托，超时必须抛出明确异常并按操作停止/无响应规则处理；单次尝试结束（`AttemptEnded`）发送前清空待发阶段缓存，并在排他门控下等待可能在途的进度帧写出完毕再写出 `AttemptEnded`，严格保证旧尝试进度绝不越过该尝试的 `AttemptEnded`，也绝不进入下一次尝试；跳过过期进度后继续泵送后续有效阶段；观察者派发限制实际在途任务数（非阻塞尝试，满则丢弃中间帧），防止慢观察者导致无界并发任务积压；通道关闭时原子停止接收新的发送者和观察者，统一追踪发送泵、发送者与观察者实际物理运行，待全部退出后方可释放同步对象；若超时未退出则将释放延后至后台安全任务，杜绝慢观察者在退出释放后向门控信号量归还名额引发未处理异常。
- 会话与受管操作建立统一的取消执行所有权与取消源寿命管理，覆盖正常取消、期限到达、宿主 Cancel、底层断连与释放：
  - 入站分发泵各阶段（握手阶段、任务输入阶段、运行阶段收到 Cancel/Shutdown，以及通道断连与分发异常退出）一律通过 `TriggerBusinessCancellation()` 统一异步隔离触发业务取消，杜绝外部注册的回调同步阻塞消息分发泵；
  - 受管操作将尝试启动登记、尝试关闭、取消任务登记与最终释放纳入共同裁决互斥门控锁：
    - **启动登记与释放互斥裁决**：`ExecuteAsync` 启动尝试时的结束状态检查、重试次数递增、活跃尝试状态发布与向会话登记退出任务，与 `DisposeAsync` 标记操作结束及读取活跃尝试状态在同一互斥锁内原子竞争。若释放胜出，`ExecuteAsync` 明确失败并抛出 `ObjectDisposedException`，不执行业务委托亦不发布活跃尝试；若启动胜出，`DisposeAsync` 必定取得完整的活跃尝试状态与局部取消源，派发取消并在宽限期内收尾覆盖该尝试；
    - **期限监听完整覆盖与业务任务物理隔离**：期限监听（`WatchDeadlineAsync`）必须在业务委托执行前启动，完整覆盖委托的全部执行（涵盖返回 `Task` 前的同步计算、阻塞及 I/O 阶段）；业务委托统一由独立任务异步执行并解包，保持 `AsyncLocal` 环境继承与异常传播语义，并在终态和收尾中纳入物理退出追踪；期限监听因策略版本变化而重新计算时，必须取消并观察本轮未胜出的有限延迟，再进入下一轮；父级令牌取消须按正常取消语义向外传播，该等待取消不得触发或同步执行业务尝试取消；
    - **物理退出任务全局追踪**：启动成功的尝试，其物理退出任务（汇聚业务委托与已登记取消执行）必定在同一锁内完成会话登记，绝不错过会话最终释放取消源所等待的集合；
    - **初始化失败按登记步骤回滚**：初始化失败时根据本次实际完成的登记步骤执行回滚。发布前失败（如取消源创建异常、释放胜出或达到尝试上限）仅清理本次分配的局部取消源与退出源，保持原尝试计数和操作状态完全不变；登记过程中失败（如会话退出追踪委托抛错）在互斥门控下仅撤销本次已完成的状态更新（已发布状态则闭合尝试、清空取消源并触发完成源；已递增计数则递减回滚），对应退出源正常终结，杜绝半开脏状态与累计尝试数破坏；
    - 尝试结束退出时在同一互斥锁内原子置位尝试关闭并清空活跃取消源，此后拒绝登记新的取消任务；取消执行严格由尝试取消统一持有器启动并复用，确保资源释放必须且仅包含已登记的使用者；
    - 互斥锁内仅进行纯内存状态转移与瞬时登记，业务委托执行、协议信封写出与异步等待严格置于锁外；
  - 区分有限收尾等待预算与实际物理退出：受管操作为每次尝试保留明确的最终退出任务（汇聚业务委托与全部已登记取消执行），由会话在全局生命周期中持续追踪（涵盖已注销的操作）；会话取消源（`_businessCts` 与 `_pumpCts`）和尝试取消源（`attemptCancellation` 与 `deadlineStop`）脱钩于调用方的有限等待预算，必须等待实际取消任务及全部相关使用者物理退出后才执行释放；
  - 释放前必须显式观察所有聚合任务的异常（`Task.WhenAll(...).ContinueWith(static (t, state) => { _ = t.Exception; ... })`），彻底消除未观察聚合任务异常与在异步取消仍在运行时的提前释放问题。
- 会话收尾（`AbilitySession.DisposeAsync`）保证连接关闭的 `try/finally` 覆盖取消触发与受管操作释放本身；调用受管操作释放时采用异步隔离，杜绝因操作释放内同步触发取消阻塞而绕过收尾预算；受管操作在释放内部异步隔离派发取消通知，且在 `finally` 块中完成本地注销，确保取消回调抛错或阻塞不中断释放推进；移除固定延迟一秒释放取消源的硬编码逻辑，按取消任务与实际使用者的物理退出决定释放时机；收尾明确区分有限整体等待预算（3 秒，覆盖租约、受管操作、进度通道与入站分发泵的排空）与有限强制关闭预算（1 秒，覆盖连接与管道底层释放）；超时后在 `finally` 中强制关闭底层通信与连接，持有并安全观察连接和管道释放任务的迟到异常。
- 操作登记与宿主立即下发的 `PolicyUpdate` 采用待决占位协作，消除 `OperationAccepted` 与动态更新之间的竞态；处理更新时异步分发，不阻塞入站分发泵以保持 `Cancel` 可即时响应。策略应用与确认内容采用同一次裁决结果，杜绝二次状态读取造成的版本不一致。
- 进度通道统一支持会话级与操作级表示：会话级进度使用空操作键（`string.Empty`），构造函数允许空操作键，仅拒绝 `null`；公开 `ReportProgress` 严格遵守操作和尝试状态，终态或未开始时安全忽略；入口 `ExecutionContextMdl` 接入环境级进度汇报通道（32 阶段有界合并、自增序号与单一发送队列），对于非空但不存在的操作键安全丢弃并记录诊断。

### 4. 敏感资料存储与授权
`SensitiveDataStore` 提供基于 AES-256-GCM 信封的敏感数据保护（敏感来源引用模型 `SensitiveSourceRefMdl` 置于 `Ability.Model` 命名空间，声明为具体非泛型 partial 类）：
- **快照读取**：`ReadSnapshotAsync` 先后申请资料文件本身与密钥提供者（`ISensitiveKeyProvider`）的 `Read` 租约，在授权下读取并返回实现 `IDisposable` 的 `SensitiveDataSnapshot`。成功返回后调用方用 `using` 管理快照，返回前失败由资料组件清理尚未移交的自有缓冲。快照持有字符缓冲，调用方 `Dispose` 时安全清零；清零保证仅覆盖当前实现直接持有并清零的内部明文字节数组与快照内部字符数组；
- **受控持久化**：技能在当前会话内通过 `ReplaceAsync` 更新时，必须先成功取得目标文件 `Write` 租约与密钥提供者 `Read` 租约；加密失败时明文缓冲必定执行 `CryptographicOperations.ZeroMemory` 安全清零，随后通过临时文件与强刷盘原子替换目标文件；
- **脱机管理更新**：针对无会话的部署与管理工具，提供独立的 `ReplaceStandaloneAsync`；仅允许在当前未处于活跃会话时调用，若在会话内调用抛出 `InvalidOperationException` 阻止绕过租约机制；
- 远程及复杂数据源扩展列入后续需求。

### 5. 子调用组件与控制转发
Sdk 通过 `AbilityCall` 提供一元（`InvokeAsync<TInput, TOutput>`）与流式（`InvokeStreamAsync<TInput, TOutput>`）子调用能力（输出支持模型与 DTO 等纯数据结果）：
- **前置 TaskInput 帧准备**：一元与流式调用均在向宿主申请启动子进程前调用 Core 的 `IpcFrame.EncodeTaskInputFrame<TInput>`，用调用方 `JsonTypeInfo<TInput>` 编码业务模型，包含任务 ID、临时目录、类型名及当前 W3C 追踪字段，并取得已通过最终通用编码核验的完整帧。握手成功后直接写出并刷新同一帧字节，不重新序列化业务模型；输入超限或准备失败时不会启动子进程。首遍准备表示受独立 16 MiB 输出与 128 MiB 工作区限制，该限制约束准备空间，不代表线上业务载荷上限。超过中间表示上限的自定义原始 JSON 即使最终默认编码载荷可低于 1 MiB，也会被拒绝。
- **子连接与流状态机（初始额度同步登记）**：`ChildSession` 接管子管道连接，维护结果帧状态机（`ResultOpen`、`ResultItem`、`ResultClose`、`SubmitResult`、`SubmitFault`）；初始信用额度完全由握手协商确立（缺省 4），父端接收计数器与握手授权同步初始化，使子端启动即具备合法发送额度，严禁在收到 `ResultOpen` 后重复额外发送初始额度；此后每消费一条流元素补发 1 点额度；流式枚举使用容量为 1 的通道及消费确认，将背压严格向上游子进程传递。结果帧校验在本类内持有 `_streamStateLock`，直接读取开流、关闭、一元终态与剩余额度字段，并在校验通过后更新状态或额度。开流后收到 `SubmitFault` 会记录终态并拒绝后续结果帧；完整一元及故障终态接纳按 D16 保持在子会话内。
- **子控制路由与安全转发（深层寻址封装、同锁裁决与请求通知解耦）**：会话维护直接子会话表与递归展开的后代路由表；向深层后代（非直接子节点）转发控制消息时必须保持 `ChildMessage` 寻址封装，以供下游逐级解包路由；写出超时（2 秒）必须取消底层写任务；登记、后代路由与关闭同锁裁决；路由关闭后拒绝登记新子会话，子调用发起端核验拒绝并立即阻断启动；写出过程追踪活跃生产者并在所有生产者物理退出后方可释放同步门；转发循环严格区分通知类消息与请求类消息：无 ID 通知类消息在循环内按序中继，带 ID 请求类消息派发至有界待决请求任务池（容量 32）异步等待宿主答复并写回子管道，杜绝审批等待阻塞后续通知；`ChildSession.IsForwardedControl` 完整识别 `OperationUnresponsive` 与针对策略拒绝的 `RequestFault`；进度与 `AttemptEnded` 保持有序写出；`ChildRouter.DisposeAsync` 接入会话统一收尾预算，排空失败如实向上传播；会话收尾严格遵循先停止子调用路由、再释放活动租约的生命周期顺序。
- **停止答复核验与失败报告**：`ChildSession` 按原始 `ChildMessage` 与实际 `ChildMessageReply` 逐层匹配调用键和请求 `Id`，末层再匹配 `ChildStop` 与答复 `Id`；只有末层答复为调用键匹配且 `Outcome` 为 `Reaped` 的 `ChildReaped` 时，才删除对应后代路由。`RequestFault`、未确认回收和空代理确认均保留路由。单个子会话的停止执行者负责记录一次失败并重新抛出原异常；`ChildRouter` 保留会话级 `HasFailedStops` 摘要并汇总释放时的停止异常，`AbilityCall` 不重复记录同一停止失败。
- **生命周期与异常收尾**：覆盖构建超时、连接超时、提前退出、取消、断连与父调用结束等收尾路径；通过 `ChildStop` 请求宿主确认回收；区分退出码 130（主动取消）与退出码 1（故障）；流式枚举提前退出触发取消并释放资源。

`InvokeStreamAsync` 的消费方提前结束枚举时，枚举器释放须停止已发起的子调用，并通过既有回收协议确认子树退出与资源结算。取消执行须异步隔离且有所有者；不能把子调用未确认回收的失败仅记日志，让枚举器释放表现为成功。保留主异常，同时如实传播或报告收尾失败。正常枚举结束同样须完成子调用收尾。有限预算内未确认回收时，向调用方反映收尾失败，未完成的停止任务继续由子调用组件或所属会话追踪，最终由 Hosting 按进程退出事实回收。子进程中的业务资源由子进程清理，父端管理自己的枚举器、连接及停止请求。

### 6. 资源分发与读取

默认配置、模板及内置数据等随版本发布的不可变资产，以 `EmbeddedResource` 编入所属程序集。由使用资源的类库通过 `typeof(T).Assembly.GetManifestResourceStream(...)` 读取，调用方释放返回的流。资源项在所属目录声明，避免把源码、构建文件或 `*.run.json` 纳入资源通配符。Launcher 无需知道类库的资源目录，也不导入下游类库的 props 或 targets。框架不另造内嵌资源查找器。

必须独立分发的文件由所属切片声明伴生内容及 `CopyToOutputDirectory`，输出相对路径随内容项传递。支持带元数据的 `#:include` 时可直接声明；输出与发布行为按实际工具链验证。纯文本本地化可使用 `.resx`，不在其中存放图片或二进制对象。

独立物理文件由使用者按其输出相对路径结合 `AppContext.BaseDirectory` 读取。Framework 不提供资源路径工具，不规定额外的 owner 标识或目录布局。

部署后可修改的配置由 Launcher 管理：以应用目录中的外部文件覆盖程序集内嵌默认值，再把取得的数据或角色交给业务代码。底层类库不自行探寻外部配置；用户设置、凭据及热更新数据继续由稳定外部数据源提供。本节不涉及外部服务和网关租约契约。

### 7. 结构化诊断与脫敏
Sdk 提供 `AbilityDiagnostics` 输出格式化 stderr 诊断：
- 诊断行与换行符原子写入，杜绝多线程并发写入时日志行交错撕裂；
- 后台输出工作者单读消费，生产者仅向有界通道写入，丢弃由通道溢出策略处理，绝不在生产者线程读取通道以维护单读契约；
- URL 脱敏全面覆盖标准 URI 与非标准/畸形 URL（解析失败时通过正则剥离 `userInfo` 密码凭据）。

## 三、执行环境与会话协调

### 1. 执行环境独立性与模型
- `ExecutionContextMdl`：承载 `CancellationToken`、`TimeProvider` 与 `IProgress<RunProgress>`，遵循文件切片规约使用显式私有字段，主体与投影置于 `Ability.Model` 命名空间。模型声明为 `sealed partial` 类，保留数据字段和公开 `init` 属性。在同模块内通过 `ExecutionContextMdl.AsExecutionContext.cs` 投影文件提供公开静态工厂 `ExecutionContextMdl.AsExecutionContext(ExecutionContextMdl model)`，每次创建轻量适配器，不复制模型，不取得时间源或进度对象所有权；同时保留 `model.AsExecutionContext()` 实例方法委托至静态工厂作为兼容入口。投影文件中包含唯一的私有嵌套适配器 `ExecutionContextAdapter`。并发修改上下文时，各分支准备独立模型。
- `AbilityEnvironment`：
  - 通过 `AsyncLocal` 维护环境模型与任务临时目录绝对路径；未建立环境时读取抛出 `InvalidOperationException`。
  - `RunAsync<TResult>` 在指定环境下执行一元委托，在正常、异常或取消退出时通过 `finally` 恢复调用方环境。
  - `RunAsync<TItem>` 在每次 `MoveNextAsync` 推进及 `DisposeAsync` 释放期间采用生产侧环境，返回消费方时通过 `finally` 恢复消费方环境；流元素的消费发生在消费方原环境中。

控制流程显式接收 `IExecutionContext`（置于 `Ability.Role` 命名空间），派生取消源须链接父级停止信号，并在子调用及收尾完成后由创建者释放。沿用的时间源和进度接收端继续按原共享与所有权约定使用。执行上下文只保存取消令牌和借用引用，取消源的释放责任仍属于其创建者。

租户、身份和工作单元等业务上下文通过强类型业务角色传递。Sdk 的隐式环境承载执行条件与任务关联，追踪上下文由原生 Activity 传播。

### 2. 调用会话（AbilitySession）全流程协作
`AbilityEntry.RunAsync` 在内部创建 `AbilitySession` 协调单次执行生命周期：
1. **参数解析与连接建立**：完整扫描命令行参数，严格核验恰好包含一个 `--ipc <pipeName>` 选项；缺少参数、缺少参数值或存在重复 `--ipc` 选项均立即明确拒绝；建立全双工 `NamedPipeClientStream` 并由 Core 的 `IpcConnection` 接管。
2. **协议握手**：等待接收宿主 `Hello` 帧；核对协议主版本为 3；`resultCreditWindow` 必须在 `[1, 65536]` 范围内（严禁零额度窗口）；`cleanupGracePeriod`、`connectionTimeout` 与 `buildTimeout` 必须大于零；回发 `HelloAccepted`，并保存宿主指定的各项协商参数。阶段后拒绝重复 `Hello`。
3. **输入接收与环境初始化**：接收 `TaskInput`；核验任务临时目录有效性；通过调用方 `JsonTypeInfo<TInput>` 反序列化业务输入模型；在当前异步上下文中注入任务临时路径与执行环境。阶段后拒绝重复 `TaskInput`。
4. **业务执行与结果提交**：
   - **一元模式**：执行处理委托，取得数据结果；按第 6 步完成必要收尾后，通过 `SubmitResult` 发送给宿主。
   - **流式模式**：先发送 `ResultOpen`；随后逐项枚举流元素；生产前先获取信用额度（初始额度由握手窗口协商），未产出项时归还；每发一项 `ResultItem` 消耗 1 点额度；枚举结束后先完成下述枚举器释放及第 6 步必要收尾，再发送无体 `ResultClose` 并等待宿主回发 `ResultClosed(itemCount)` 完成对账。在未开流或未关闭时收到 `ResultClosed` 判为协议错误。
5. **协议校验与分发规则**：单入站分发泵严格按会话阶段分发消息。未知请求主动回发 `UnsupportedRequest`；未知通知仅在 `optionalNotification=true` 时忽略，未知的必需通知直接报告协议错误；非法或解析失败的 `ResultCredit` 立即报告协议故障。统一会话关闭、在途登记、操作发布与尝试退出登记的所有权门控（`_operationsGate`），关闭后禁止新增登记，收尾前已接受的登记全部纳入收尾，迟到批准立即拒绝并清理。
6. **终态通信中断与实际退出原因解耦、租约释放前提与生命周期状态等待**：明确区分退出原因，统一正常、故障和取消路径的收尾时序：
   - **终态通信中断与实际退出原因严格解耦**：终态消息（`SubmitResult`、`ResultClose`）发送与确认等待（`ResultClosed`）使用独立明确的有限时限预算（发送 5 秒、确认等待 30 秒），仅响应调用方主动取消或终态中断信号（`_terminalInterruptCts`，在宿主主动 Cancel/Shutdown、底层断连或协议错误时触发）；严格分别保存“终态通信需要中断”和“实际退出原因”：调用方取消或宿主明确发送 Cancel/Shutdown 遵循退出码 130 规范语义；意外断连与协议损坏按故障退出（退出码 1），保留真实退出原因并关闭连接，严禁将断连误报为 130 主动取消；
   - **租约释放前提与统一收尾预算**：业务枚举器清理与必要收尾（`EnsureTeardownAsync`）共用 3 秒整体预算，从开始枚举器清理或停止后收拢在途枚举时计起，后续收尾使用剩余时间；一元模式从必要收尾开始计时；必须以“业务在途操作及子调用已实际停止”作为收尾释放活动租约的前提；若在途操作在 3 秒预算内未能完全停止，或子调用路由器报告停止失败（`HasFailedStops` 或捕获到停止异常），立即判定收尾失败并标记 `success = false`，保留租约授权，严禁向宿主发送释放通知，交由宿主在确认进程退出后回收；仅当在途操作与子调用确认完全退出且预算未耗尽时，才允许将剩余活动租约入队释放并在同一剩余预算内排空；最外层 `RunCoreAsync` 的 `finally` 块对子路由器释放施加 1 秒有限时限，杜绝无限卡死；
   - **同锁裁决登记所有权与状态等待生命周期**：会话在 `_operationsGate` 互斥锁内单次原子裁决中取得操作登记过程的所有权并递增活跃使用者计数，再在锁外执行网络请求，登记过程及其派生清理全部退出后才归还所有权；生命周期收尾等待采用基于状态归零通知的等待机制（`_zeroUsersTcs`），直接监听在途使用者归零，彻底消除等待已完成任务引发的 `Task.WhenAny` 忙循环与后台延续任务线程饥饿；所有实际使用者物理退出后安全释放会话取消源；
   - **终态通信与连接断开**：必要收尾完成后按规范结束通信：外部取消或宿主取消按规范退出码 130 退出，严禁伪报正常终态；通信损坏或业务异常按退出码 1 退出，尝试发送 `SubmitFault`；捕获未处理异常准备发送 `SubmitFault` 时，诊断日志输出完全接入非阻塞的 `AbilityDiagnostics.Log` 有界受管机制，严禁在发送前执行任何同步标准错误流写出，杜绝因标准错误管道满载阻塞而阻断 `SubmitFault` 的交付；收尾成功且正常完成时发送 `SubmitResult` 或 `ResultClose`；终态发送完毕后方可关闭底层通信连接与分发泵（`CloseAsync`）。收尾失败如实反映真实结果，严格保持有限时限与物理退出追踪。
7. **子调用（AbilityCall）、子会话（ChildSession）与子路由（ChildRouter）协同**：
   - **单请求序号源与子请求池满载处理**：同一连接上的请求编号统一由底层的 `IpcConnection.NextRequestId()` 分配，SDK 不维护独立自增序号，彻底杜绝组合调用下子调用与治理请求发生编号冲突；`ChildSession` 转发循环严格解析并区分消息语义：单向通知（进度、状态、到期、释放、无响应等）与双向答复（`PolicyApplied`、`RequestFault` 等）直接按序中继写出，不阻塞等待答复；所有等待答复的双向请求（治理申请 `LeaseRequest`、`ParamRequest`、`SourceRepair`、`OperationRegister` 以及嵌套子调用 `ChildStart`、`ChildStop`）统一交由有界待决请求池（容量 32）异步调度等待宿主答复，严禁在主转发循环中同步阻塞。门控满载时施加 2 秒独立发送期限回发 `ServerBusy` 的 `RequestFault` 拒绝，发送失败立即显式终结该子会话。关闭、取得门控名额与任务登记共同裁决；门控对象必须在转发循环与全部在途请求任务完全物理退出后方可释放，超时未退出由后台所有者延续收尾；并发 `DisposeAsync` 共享同一个收尾任务。
   - **子路由生命周期全路径收尾与状态通知**：`ChildRouter` 释放采用单例共享任务模式；无论生产者等待是否超时或被取消，均可靠触发控制转发循环取消，并持有全部子调用停止任务；`ChildSession` 的停止执行者记录停止异常，`ChildRouter` 汇总释放时的停止任务异常并向外暴露会话级 `HasFailedStops`，释放失败时抛出 `AggregateException`；活跃生产者归零采用异步通知机制；同步对象在所有在途任务确认完全退出后释放并吞掉异常。
   - **子调用启动状态精细划分与停止失败持久记录**：`AbilityCall` 严格区分尚未请求启动（`!StartRequested`）、已确认未启动、已确认回收和退出未确认四种状态；本地准备失败或向已关闭路由器注册失败时仅清理本地资源，不向宿主发送 `ChildStop`；启动失败或其他停止路径中，若退出未确认（`child.StartRequested` 为 true 但 `StopAndConfirmAsync` 抛错、超时或未确认回收），严禁移除仍可能存活调用的后代路由。停止执行者统一向 `ChildRouter` 报告一次失败，并保留原异常；`AbilityCall` 可把失败附加到主异常供调用方诊断，但不重复设置会话失败状态。即使业务代码捕获启动异常后继续正常返回，`HasFailedStops` 仍阻止会话提前释放活动租约。多层控制答复必须逐层匹配原请求的路由键，仅收到对应调用的 `ChildReaped` 且 `Outcome == "Reaped"` 后才注销该后代路由。
   - **停止尝试任务复用**：同一停止尝试仍在途时，所有等待者共享一个任务；只有确认 `ChildReaped` 成功回收的任务会永久缓存。失败、超时或未确认回收的尝试结束后，后续显式停止请求可以创建新尝试。失败事实继续保留在 `ChildRouter.HasFailedStops`，重试不会解除租约保护，也不会自动触发无限重试。任务完成源及持有实例先在锁内发布，实际异步 I/O 在锁外启动；仅当完成的失败任务仍是当前缓存实例时才清除，以保证并发等待者观察同一次原异常且不会覆盖后继尝试。

### 3. 业务枚举器的释放与完成条件

Sdk 消费业务结果流时负责释放取得的 `IAsyncEnumerator<TOutput>`。`await foreach` 或显式 `await using` 提供正常、异常与提前退出时的释放语义；有限等待和未完成清理的管理按下述规则执行。业务迭代器在内部用 `using` 或 `await using` 管理枚举期间的局部可释放模型；资源在各次 `yield return` 之间保持有效，枚举器释放执行其内部清理。

正常结束、枚举或发送异常、取消以及提前退出均须安排枚举器释放，并等待实际清理完成。正常 `ResultClose` 的发送以业务枚举器释放和会话必要收尾均成功为前提。枚举器创建或首次推进期间失败时，由已取得资源的所有者清理；返回了结果流但尚未开始枚举时，生产者在迭代器内部取得本次枚举资源。

同一枚举器的推进和释放按顺序执行，正在执行的 MoveNextAsync 与 DisposeAsync 不得并发。停止时先请求正在推进的枚举退出，再按顺序释放枚举器；实际仍在运行的推进由会话持续追踪。枚举器释放、停止后的在途推进及必要收尾共享一次 3 秒预算，后续步骤使用剩余时间，不重新开始计时。覆盖业务委托或 DisposeAsync 调用本身的同步阻塞部分。超时仅结束前台等待；实际推进、清理和取消任务继续由会话持有，观察迟到异常，实际使用者退出后才释放依赖及 CTS。正常 ResultClose 只能在清理和必要收尾成功后发送。清理失败时保留已有业务异常或真实停止原因；原正常路径收尾失败则转为故障。未确认业务和子调用停止时，禁止提前释放活动租约。剩余租约按既有规则保留至 Hosting 确认进程退出后回收，失控进程交由护栏处理。

## 四、失败与诊断

补参失败分别表示定义不受支持、实例不满足、校验未完成、答复形态错误和解码失败。Sdk 保留上游 `RequestFault` 的错误码，并补充本地请求发生的位置；业务方可按异常类型与错误码处理，单项请求失败不自动损坏整个会话。

结构化诊断与追踪系统设计：
- **结构化输出**：`AbilityDiagnostics` 将记录写至 stderr，前缀为 `DiagnosticConstants.Marker`（`skill-diag:`），版本为 1，SDK 标识为 `AbilityFramework`。队列有界（容量 1024），配置为 `BoundedChannelFullMode.DropWrite`，满载时保留旧项并淘汰新写入项，通过通道 `itemDropped` 回调原子累加满载淘汰数，精准区分满载淘汰与通道关闭后的写入失败，消除 TryWrite 误判与单读契约冲突；有限排空（默认 2 秒）。
- **异常安全说明**：非显式实现 `ISafeFaultDescription` 的异常仅导出类型名，原文不跨进程外泄；`AbilityLogging` 在调用 formatter 前用 `SafeLogException` 替身替换异常对象，确保第三方 formatter 无法获取敏感信息。
- **原生 Activity 采集**：`AbilityTracing` 监听自身及 `System.Net.Http` 来源，透传 W3C TraceParent 与 TraceState（写出 `tracestate` 字段），分别写出 `span_start` 与 `span_end`。
- **URL 脱敏**：标签白名单中 `url.path` 自动去除查询串、哈希片段及 `user:pass@` 认证信息。

## 五、文件式程序构建预设

开发者统一通过 `#:package Ability.Sdk@版本` 接入。该 NuGet 包包含运行时 API，以及由 NuGet 自动导入的 `buildTransitive/Ability.Sdk.props` 和 `Ability.Sdk.targets`，不提供自定义 MSBuild SDK 入口。

过渡期 `.lib` 扩展名映射属于开发环境规则，在工程根目录的 Directory.Build.props 中声明 `FileBasedProgramsItemMapping` 的 `.lib=AbilityVirtualLib`。CLI 在读取伪扩展名指令前取得这个映射；包内 Target 接收生成的类库项并执行编译。映射不放在包内，不要求开发者再选一个 SDK。

构建与发布身份仍由 Launcher 或独立类库声明；预设不改写入口的 `OutputType`、`PublishAot` 或程序集名称。Framework 自身的 `.csproj` 工程继续使用原有工程属性，不套用文件式入口规则。

预设只在 `FileBasedProgram == true` 时把 `CS2002` 追加到已有 `NoWarn`。保留使用者声明的其他诊断设置；转换为普通 `.csproj` 后不追加该项。合法的菱形包含继续声明直接依赖，不以删除依赖或排除源码消除重复警告。

类库路由按全局方案提供两种声明入口：过渡期用 `FileBasedProgramsItemMapping` 识别 `Lib.cs.lib`，从该项还原物理文件 `Lib.cs`；标准形式识别 `Compile` 项的 `As=Library` 元数据。两者交给同一隔离编译步骤：调用既有 dotnet 文件式构建，只对被选择的切片传入 `OutputType=Library`，随后把 DLL 交给正常程序集引用解析。源文件不改名、不添加该属性，也不作为 XML ProjectReference。隔离编译保留本次 Configuration 和 TargetFramework；按完整源码路径生成程序集身份并区分中间输出，去除同一次构建中的重复入口。该身份由选择隔离的调用方构建机制赋予，不覆盖入口身份；需要自己声明程序集身份的独立 Library 使用原生 `#:ref`。标准指令语法须由实际 .NET SDK 支持；预设负责消费已生成的项元数据，不另造指令解析器。

隔离编译的增量检查由 dotnet 文件式构建负责，覆盖源码包含和构建配置。外层 Target 不仅按入口文件时间跳过整个构建；入口未改时，被包含的源码仍可能变化。没有类库项时不启动外部构建。构建进程非零退出即中断当前 Target，不再重复检查其成功输出。

伴生内容保留 `CopyToOutputDirectory`、发布和目标路径元数据，通过 .NET 原生项目引用传递。需要独立物理资产时使用声明完整的 Library 与 `#:ref`；上述隔离切片采用程序集引用，不把它冒称为原生项目引用的内容传递通道。切片内嵌资源随 DLL 分发。入口不导入类库构建文件或补写拷贝步骤，Framework 不另造资产拷贝器。构建预设的实际完成范围与工具链证据记录在 [专项记录](verification/sdk-build-presets.md)，不以声明目标代替实施或验收。
