# Ability Framework

[English](README.md) | 简体中文

Ability Framework 为独立进程中的技能调用与组合提供 .NET 基础设施，包括技能侧 SDK、宿主侧执行库和共享 IPC 契约。宿主应用负责提供输入、授权决策、用户交互和结果消费，技能负责实现业务操作。

一个技能由可复用的源文件和称为 **Launcher** 的进程入口组成。文件级组合组织同进程代码，Launcher 调用组合跨进程工作。框架通过强类型技能处理方法和基于 JSON 的宿主接口连接这两层执行方式。

## 架构

### 业务代码组织

业务架构将对象状态、能力契约和执行顺序分别表达。

| 要素 | 组织约定 | 职责 |
| --- | --- | --- |
| 模型 | `Model/<Name>Mdl/` | 表达对象及其状态。模型主体与投影共同组成一个分部类型。 |
| 角色 | `Role/I<Name>.cs` | 定义特定用途所需的数据和操作，不绑定具体模型。 |
| 投影 | `<Model>.As<Role>.cs` | 定义工厂和实现角色的私有适配器。工厂将适配器绑定到模型，以角色接口返回。 |
| 流程 | `Module/<Name>Mod.cs` | 接收角色，执行业务判断和局部计算，调用子流程，明确返回结果。 |
| DTO | 与生产它的流程位于同一文件 | 在无须使用适用模型时，承载流程专用的纯数据结果。 |
| 入口 | `Launcher/<Name>Lch/` | 准备输入、装配角色、调用流程，并提供可序列化的输出。 |
| 类库 | `Library/<Name>Lib/` | 在不适合源码级组合时，提供独立编译的程序集。 |

模型公开可读数据和初始化属性，适配器执行受控状态修改。投影只创建角色实例，不执行实际业务 I/O。角色方法实现外部访问，包括认证与服务协议；流程决定需要执行哪些操作，以及它们的先后关系。

例如，文本模型提供读取与编辑投影。去空白流程接收这两个角色，读取内容，计算去除空白后的文本，通过编辑操作取得更新后的模型。流程再为统计字数的子流程创建读取投影，最终向调用方返回模型与统计结果。文件文本模型也可以实现相同的读取契约，供只需要该角色的流程使用。

每个源文件自行声明直接依赖。新 Launcher 可以选择已有模型、投影、角色和流程来组合，无须复用整个应用。业务流程采用无状态静态类型，单次执行状态保存在局部变量和模型中。网络与文件 I/O 由角色适配器承担。这些组织规则面向业务技能，框架内部则使用适合基础设施职责的契约与组件。

### 运行时组件

| 类库 | 职责 |
| --- | --- |
| [Ability.Core](src/Ability.Core) | 消息契约、序列化与大小计量、帧编解码、连接协调、请求匹配、结果流状态及 Schema 校验。 |
| [Ability.Sdk](src/Ability.Sdk) | Launcher 入口、本地执行环境、资源租约、补参、操作策略、子调用、敏感资料访问及诊断，同时提供文件式构建预设。 |
| [Ability.Hosting](src/Ability.Hosting) | 调用 API、进程启动与护栏、权威调用树登记、治理回调、任务目录、结果消费及遥测导出。 |

编译依赖方向为 `Ability.Hosting → Ability.Core` 和 `Ability.Sdk → Ability.Core`，Hosting 与 Sdk 互不引用。宿主应用引用 Hosting，Launcher 引用 Sdk。宿主以 JSON 处理业务载荷，无须引用技能的业务程序集。

宿主维护权威的进程与资源归属记录，每条连接独立管理请求编号与流式额度，SDK 管理本地执行状态。组件之间的具体关系见[架构规格](spec/architecture.md)。

## 调用生命周期

**任务**以 `TaskId` 标识，代表一个业务目标。一次**调用**执行一个 Launcher；任务可以包含根调用和多层子调用。**操作**是调用内登记后接受策略与进度管理的一项工作，一个操作可以包含多次尝试。

正常调用依次执行以下步骤：

1. 宿主接收 `AbilityInvocation`，校验输入，创建任务目录和命名管道，以 `--ipc <pipeName>` 启动 Launcher。
2. SDK 连接管道，两端通过 `Hello` 和 `HelloAccepted` 协商协议主版本 3 及执行参数。
3. 宿主发送 `TaskInput`，交付业务 JSON、任务标识、任务目录路径和 W3C 追踪上下文。
4. SDK 使用调用方提供的 `JsonTypeInfo<TInput>` 解码输入，建立本地执行环境，再调用入口处理方法。
5. 入口装配角色并调用业务流程。适配器按需申请租约或补充输入，宿主通过应用回调取得决策。
6. SDK 使用输出元数据提交一元结果或结果流。宿主消费输出，并协调进程退出、治理结算与目录处置。

子调用使用相同的 Launcher 契约。`AbilityCall` 请求宿主管理启动，再通过父子管道交换输入和结果。子结果返回直接父调用，由父业务决定如何组合；治理请求则向上转发至宿主，宿主核验调用归属后沿对应路径返回决策。

当前运行范围是每个技能进程执行一次调用。重启会创建新进程并从输入重新执行，不恢复旧进程的内存现场。

## 协议与结果传输

通信使用全双工命名管道。每帧由四字节小端长度前缀和 UTF-8 JSON 信封组成。请求与答复通过连接内编号关联；控制消息和结果共用连接，但分别使用有界调度容量。

| 项目 | 契约 |
| --- | --- |
| 协议版本 | 主版本 3，握手时拒绝不兼容的主版本。 |
| 帧大小 | JSON 信封不超过 16 MiB，不含四字节长度前缀。 |
| 业务载荷 | 每份输入、一元结果或流元素的 UTF-8 JSON 序列化大小不超过 1 MiB。 |
| 一元结果 | `SubmitResult` 携带输出类型名与 JSON 载荷。 |
| 结果流 | 依次发送 `ResultOpen`、有序的 `ResultItem` 和 `ResultClose`，通过 `ResultClosed` 确认接收项数。 |
| 背压 | 初始信用额度在握手时协商；生产元素前取得额度，消费成功后补充额度。 |
| 大载荷与二进制 | 使用文件绝对路径或 URI，不内联二进制或超限 JSON。 |

额度耗尽时，SDK 暂停结果流生产，但继续接收控制消息。取消、消费失败或提前结束枚举会启动生产端停止与清理。发生故障时仍保留实际已接收的元素数量。

新生成的中间文件应写入 `AbilityEnvironment.GetTaskTempPath()` 返回的任务目录。宿主删除目录前必须消费或持久化需要的产物。任务目录内的路径是临时引用，不是长期保存位置。

线协议、校验规则与 Schema 关键字支持范围见 [Core 规格](spec/core.md)。

## 公开接入 API

| API | 用途 |
| --- | --- |
| `AbilityHost.RunAsync` | 根据 `AbilityInvocation` 和 `HostRuntimeOptions` 启动并管理 Launcher，返回 `AbilityRunResult`。 |
| `AbilityEntry.RunAsync` / `RunStreamAsync` | 将 Launcher 处理方法接入 IPC，返回进程退出码。 |
| `AbilityEnvironment.Current` / `RunAsync` | 读取执行条件，或在派生环境中执行指定工作。 |
| `AbilityRuntime` | 申请租约、补参、来源恢复、受管操作及报告进度。 |
| `AbilityCall.InvokeAsync` / `InvokeStreamAsync` | 使用输入输出类型元数据调用另一个 Launcher。 |
| `AbilityEntry.MeasurePayloadBytes` / `MeasureResultBytes` / `MeasureResultItemBytes` | 提交前测量业务载荷或完整结果信封的大小。 |
| `AbilityLogging` | 将技能日志接入 `ILogger` 与结构化诊断。 |

一元处理委托的契约为 `Func<TInput, ExecutionContextMdl, Task<TOutput>>`，流式处理方法返回 `IAsyncEnumerable<TOutput>`。Launcher 为输入和输出提供源生成的 `JsonTypeInfo<T>`。输出可以使用满足序列化契约的模型或 DTO，进程内资源句柄不跨进程传输。

`HostRuntimeOptions` 通过租约、补参、来源恢复、结果项、进度、操作快照和诊断回调连接应用策略与界面。宿主应用决定怎样答复这些请求，Hosting 提供执行机制，不限定用户界面的实现方式。

具体 API 契约与所有权规则见 [Sdk 规格](spec/sdk.md)和 [Hosting 规格](spec/hosting.md)。

## 执行与资源管理

### 执行条件

`ExecutionContextMdl` 保存 `CancellationToken`、`TimeProvider` 和 `IProgress<RunProgress>`。`AbilityEnvironment` 将这些条件限定在当前异步执行范围内。控制流程可以派生环境来管理超时、重试或进度汇总，退出作用域后恢复调用方环境。流式作用域在推进和释放枚举器期间采用生产侧环境。

租户、身份、工作单元等业务上下文通过显式角色参数传递，与执行条件和追踪上下文分别管理。建立执行环境不等于建立 IPC 会话，也不授予资源访问权限。

### 治理能力

适配器访问宿主治理策略覆盖的资源前，先取得 `AbilityLease`，再执行实际 I/O 并释放租约。租约记录授权与归属，不是操作系统沙箱；业务实现仍须遵守访问契约。

补参支持文本和类型化 JSON，可以附带 Schema 与 UI 描述。两端使用 Core 支持的 Schema 子集校验结构化答复，不承诺完整支持 JSON Schema。来源恢复请求区分刷新后重读与重启进程等处理方式。

受管操作登记期限、到期动作和重试上限等建议策略，由宿主选择实际策略，并可发送带版本号的动态更新。SDK 应用局部取消条件，跟踪尝试与进度；重试是否安全由业务逻辑判断。非幂等外部操作仍需使用业务幂等键或核实外部状态。

### 完成与收尾

框架分别记录结果交付、进程退出确认、治理资源回收和清理完成。`AbilityRunResult` 是前台返回时的不可变快照，`Success` 表示结果交付条件成立，不代表所有清理均成功。

`HostRunLifetime.Completion` 在全部受管工作和依赖使用结束后提供最终收尾报告。应用为回调提供可释放依赖时，应在调用前创建该观察句柄，并将依赖保留到完成信号发布。完成信号说明使用已结束，报告仍会保留实际清理失败。

宿主先请求协同取消，技能在宽限期内未退出时再由进程护栏处理。租约依据真实进程退出事实回收；任务目录处置须等待进程退出与产物安全消费。当前宿主未配置 `TaskTempConsumer` 时按策略保留目录，不因调用成功返回就自动删除。

## 可观测性

技能使用 .NET 原生 `ActivitySource` / `ActivityListener` 追踪和 `ILogger` 日志接入。SDK 将结构化日志与 Span 记录写入有界 stderr 通道，技能侧不需要引入 `OpenTelemetry.*` 包或 OTLP 导出器。

宿主直接读取各技能进程的 stderr，使用 W3C 追踪上下文关联记录，并可向配置的接收端通过 OTLP/HTTP 导出追踪和日志。进度与操作状态走 IPC，与 stderr 诊断分开。标准输出不承担业务结果传输。

诊断处理采用有界缓冲和有限排空期限，导出错误与业务结果分别报告。具体诊断及完成语义见 [Hosting 规格](spec/hosting.md)。

## 开发与接入

框架目标为 `net11.0`。[global.json](global.json) 选择 SDK 11.0.100，并允许滚动至最新补丁版本。[Directory.Build.props](Directory.Build.props) 启用可空引用类型、隐式 using 和警告视为错误。Core 与 Sdk 开启裁剪及 Native AOT 兼容性分析，实际发布支持另行验证。

当前基于源码的使用步骤如下：

1. 打开 [Ability.Framework.slnx](Ability.Framework.slnx)，构建解决方案。
2. 运行 [Ability.MinimalHost](test/auxiliary/Ability.MinimalHost)，通过 [Ability.MinimalLauncher](test/auxiliary/Ability.MinimalLauncher) 观察一元与流式调用。宿主自动查找已构建的 Launcher，也可通过 `--launcher` 指定路径。
3. 开发技能时引用 Sdk，定义输入输出的序列化元数据，通过 `AbilityEntry` 接入处理方法。
4. 开发宿主时引用 Hosting，提供 Launcher 路径与输入 JSON，配置决策和消费回调，并检查调用结果及其生命周期报告。

最小程序展示通信接入，不是完整的模型—角色—流程架构示例。当前宿主接收已构建的 `.exe` 和 `.dll` Launcher，不按需编译 `.cs` 入口。

SDK 还提供文件式构建预设，处理重复源码诊断和隔离类库编译，并保留入口自身的构建身份。指令支持及发布行为取决于实际工具链，完成范围见[构建预设验证记录](spec/verification/sdk-build-presets.md)。

| 位置 | 内容 |
| --- | --- |
| [src/](src/) | Core、Sdk 与 Hosting 生产类库。 |
| [test/](test/) | MSTest 用例、辅助进程、静态检查工具及[工程脚本](test/script/README.md)。 |
| `tool/` | 预留给参考宿主与开发集成。 |
| [spec/](spec/spec.md) | 技术契约、架构、实施计划及验证记录。 |

## 项目状态与规格索引

项目正在分阶段实施与整理。源码审查和定向检查见[第二阶段验收记录](spec/verification/phase-2.md)，统一端到端验证与用户验收尚未完成。跨平台运行与 Native AOT 发布以各自验证结果为准。

驻留多轮会话、完整的外部操作恢复账本和确定性重放属于后续工作。NuGet 分发、Aspire 集成和发布示例见[首版方案](spec/first-release.md)，本文不将它们列为已完成产物。

[总规格](spec/spec.md)定义术语与不变量，[架构规格](spec/architecture.md)说明组件关系，[Core](spec/core.md)、[Sdk](spec/sdk.md)与 [Hosting](spec/hosting.md) 规格定义技术契约。[实施计划](spec/plan.md)与[验收规格](spec/acceptance.md)分别跟踪开发安排和验证要求。
