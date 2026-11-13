# Ability Framework

English | [简体中文](README.zh-CN.md)

Ability Framework provides .NET infrastructure for invoking and composing skills in independent processes. It supplies a skill-side SDK, a host-side execution library, and shared IPC contracts. Host applications provide input, authorization decisions, interaction, and result consumption; skills implement business operations.

A skill consists of reusable source files and a process entry point called a **Launcher**. File-level composition organizes code within a process. Launcher invocation composes work across processes. The framework connects these two execution levels through typed skill handlers and a JSON-based host interface.

## Architecture

### Business code organization

The business architecture separates object state, capability contracts, and execution order.

| Element | Convention | Responsibility |
| --- | --- | --- |
| Model | `Model/<Name>Mdl/` | Represents an object and its state. The model declaration and its projections form one partial type. |
| Role | `Role/I<Name>.cs` | Defines the data and operations required for a particular use, independently of a concrete model. |
| Projection | `<Model>.As<Role>.cs` | Defines a factory and a private adapter implementing the role. The factory binds the adapter to a model and returns the role interface. |
| Flow | `Module/<Name>Mod.cs` | Receives roles, performs business decisions and local computation, invokes subflows, and explicitly returns results. |
| DTO | Declared in its producing flow file | Carries a flow-specific data result when no suitable model is needed. |
| Launcher | `Launcher/<Name>Lch/` | Prepares input, assembles roles, invokes a flow, and exposes serializable output. |
| Library | `Library/<Name>Lib/` | Provides an independently compiled assembly when source-level composition is not appropriate. |

Models expose readable data and initialization properties; adapters perform controlled state changes. A projection creates a role instance without executing business I/O. Role methods implement external access, including authentication and service protocols. Flows determine which operations are required and how they are ordered.

For example, a text model provides reading and editing projections. A trimming flow receives those roles, reads the content, computes the trimmed text, and obtains the updated model from the editing operation. It creates a reading projection for a word-counting subflow, then returns the model and statistics to its caller. A file-backed text model can implement the same reading contract for flows that only require that role.

Each source file declares its direct dependencies. This allows a new Launcher to select existing models, projections, roles, and flows rather than reuse an entire application. Business flows are stateless static types; per-execution state remains in local variables and models. Network and file I/O stays in role adapters. These organization rules apply to business skills; framework internals use infrastructure-specific contracts and components.

### Runtime components

| Library | Responsibility |
| --- | --- |
| [Ability.Core](src/Ability.Core) | Message contracts, serialization and size measurement, frame encoding, connection coordination, request matching, result-stream state, and Schema validation. |
| [Ability.Sdk](src/Ability.Sdk) | Launcher entry points, local execution environments, resource leases, parameter requests, operation policies, child calls, sensitive-data access, and diagnostics. Includes file-based build presets. |
| [Ability.Hosting](src/Ability.Hosting) | Invocation API, process startup and supervision, authoritative call-tree records, governance callbacks, task directories, result consumption, and telemetry export. |

The compile-time dependency directions are `Ability.Hosting → Ability.Core` and `Ability.Sdk → Ability.Core`. Hosting and Sdk do not reference each other. A host application references Hosting; a Launcher references Sdk. The host handles business payloads as JSON and does not need the skill's business assembly.

The host maintains authoritative process and resource ownership. Each connection owns its request identifiers and stream credits. The SDK owns local execution state. Detailed component relationships are documented in [Architecture](spec/architecture.md).

## Invocation lifecycle

A **task**, identified by `TaskId`, represents one business objective. An **invocation** executes one Launcher. A task can contain a root invocation and a tree of child invocations. An **operation** is work registered for policy and progress management within an invocation; each operation can have multiple attempts.

A normal invocation proceeds as follows:

1. The host receives an `AbilityInvocation`, validates its input, creates a task directory and named pipe, and starts the Launcher with `--ipc <pipeName>`.
2. The SDK connects to the pipe. Both sides negotiate protocol major version 3 and execution parameters through `Hello` and `HelloAccepted`.
3. The host sends `TaskInput` containing business JSON, task identity, the task-directory path, and W3C trace context.
4. The SDK decodes the input with the supplied `JsonTypeInfo<TInput>`, establishes a local execution environment, and invokes the handler.
5. The handler assembles roles and calls business flows. Adapters request leases or additional input when required. The host obtains decisions through application callbacks.
6. The SDK sends a single result or a result stream using the supplied output metadata. The host consumes the output and coordinates process exit, governance settlement, and directory disposition.

Child calls use the same Launcher contract. `AbilityCall` requests host-managed startup, then exchanges input and results over a parent–child pipe. Child results return to the direct parent for business composition. Governance requests are routed upstream to the host, which validates call ownership and returns the decision along the corresponding route.

The current runtime scope is one invocation per skill process. Restarting creates a new process and executes from input; it does not restore the previous in-memory execution state.

## Protocol and result transport

Communication uses full-duplex named pipes. Each frame contains a four-byte little-endian length prefix followed by a UTF-8 JSON envelope. Requests and replies use connection-local identifiers; control messages and results share the connection but have separate bounded scheduling capacity.

| Property | Contract |
| --- | --- |
| Protocol version | Major version 3; incompatible major versions are rejected during handshake. |
| Frame limit | JSON envelope up to 16 MiB, excluding the four-byte prefix. |
| Business payload limit | Up to 1 MiB of serialized UTF-8 JSON per input, single result, or stream item. |
| Single result | `SubmitResult` carries the output type name and JSON payload. |
| Result stream | `ResultOpen`, ordered `ResultItem` messages, then `ResultClose`; `ResultClosed` acknowledges the received item count. |
| Backpressure | Initial credits are negotiated during handshake. Producing an item requires a credit; successful consumption replenishes credits. |
| Large and binary data | Absolute file paths or URIs instead of inline binary data or oversized JSON. |

The SDK pauses stream production when credits are exhausted without blocking control-message reception. Cancellation, failed consumption, and early enumeration exit initiate producer shutdown and cleanup. Faults retain the number of items already received.

New intermediate files belong in the directory returned by `AbilityEnvironment.GetTaskTempPath()`. The host must consume or persist required artifacts before deleting that directory. A path into a task directory is a temporary reference, not a durable output location.

Wire contracts, validation rules, and supported Schema keywords are defined in [Core](spec/core.md).

## Public integration APIs

| API | Use |
| --- | --- |
| `AbilityHost.RunAsync` | Starts and manages a Launcher from an `AbilityInvocation` and `HostRuntimeOptions`; returns `AbilityRunResult`. |
| `AbilityEntry.RunAsync` / `RunStreamAsync` | Connects a Launcher handler to IPC and returns the process exit code. |
| `AbilityEnvironment.Current` / `RunAsync` | Reads execution conditions or scopes execution to a derived environment. |
| `AbilityRuntime` | Requests leases, parameters, source repair, managed operations, and progress reporting. |
| `AbilityCall.InvokeAsync` / `InvokeStreamAsync` | Invokes another Launcher with typed input and output metadata. |
| `AbilityEntry.MeasurePayloadBytes` / `MeasureResultBytes` / `MeasureResultItemBytes` | Measures business payloads or complete result envelopes before submission. |
| `AbilityLogging` | Integrates skill logging with `ILogger` and structured diagnostics. |

The single-result handler contract is `Func<TInput, ExecutionContextMdl, Task<TOutput>>`. A streaming handler returns `IAsyncEnumerable<TOutput>`. Launchers provide source-generated `JsonTypeInfo<T>` for both input and output. The output can be a suitable model or DTO that meets the serialization contract; process-local handles are not transferred.

`HostRuntimeOptions` connects execution to the application's policies and UI through lease, parameter, source-repair, result-item, progress, operation-snapshot, and diagnostic callbacks. The host application decides how these requests are answered. Hosting supplies the execution mechanism rather than a particular user interface.

API contracts and ownership rules are documented in [Sdk](spec/sdk.md) and [Hosting](spec/hosting.md).

## Execution and resource management

### Execution conditions

`ExecutionContextMdl` holds a `CancellationToken`, a `TimeProvider`, and an `IProgress<RunProgress>`. `AbilityEnvironment` scopes these conditions to the current asynchronous execution. A control flow can derive an environment for timeout, retry, or progress aggregation; leaving the scope restores the caller's environment. Streaming scopes apply the producer's environment during enumeration and disposal.

Business context such as tenant, identity, and work-unit information travels through explicit role parameters. Execution conditions and trace context are managed separately. Establishing an environment does not create an IPC session or grant resource access.

### Governance

Adapters acquire an `AbilityLease` before accessing resources covered by the host's governance policy, then perform the actual I/O and release the lease. A lease records authorization and ownership; it is not an operating-system sandbox. Business code remains responsible for following the access contract.

Parameter requests support text and typed JSON, with optional Schema and UI descriptors. The two endpoints validate structured replies using Core's supported Schema subset; this is not a claim of complete JSON Schema support. Source-repair requests distinguish rereading a refreshed source from restarting the process.

Managed operations register suggested policies for deadlines, expiry actions, and retry limits. The host selects the effective policy and can send versioned updates. The SDK applies local cancellation and tracks attempts and progress; business logic determines whether retrying is safe. Non-idempotent external operations still require business idempotency keys or external-state verification.

### Completion and cleanup

The framework distinguishes result delivery, confirmed process exit, governance-resource reclamation, and completed cleanup. `AbilityRunResult` is an immutable snapshot at foreground return. Its `Success` field describes result-delivery conditions, not every cleanup outcome.

`HostRunLifetime.Completion` provides the final cleanup report after all tracked work and dependency use have ended. Applications that supply disposable callback dependencies should create the lifetime handle before invocation and retain those dependencies until completion. Completion indicates that use has ended; cleanup failures remain visible in the report.

The host requests cooperative cancellation, then uses process supervision if a skill does not exit within its grace period. Leases are reclaimed according to confirmed process-exit facts. Task-directory disposal waits for process exit and safe artifact consumption. Without a `TaskTempConsumer`, the current host retains the directory by policy; it does not delete it automatically on successful return.

## Observability

Skills use native .NET `ActivitySource` / `ActivityListener` tracing and `ILogger` integration. The SDK writes structured log and span records to bounded stderr output; skill-side telemetry does not require `OpenTelemetry.*` packages or an OTLP exporter.

The host reads each skill process's stderr directly, correlates records using W3C trace context, and optionally exports traces and logs over OTLP/HTTP to a configured receiver. Progress and operation state travel through IPC, separately from stderr diagnostics. Standard output is not the business-result channel.

Diagnostic processing uses bounded buffers and finite draining periods. Export failures are reported separately from business results. Detailed diagnostic and completion semantics are covered in [Hosting](spec/hosting.md).

## Development and integration

The framework targets `net11.0`. [global.json](global.json) selects SDK 11.0.100 with latest-patch roll-forward. [Directory.Build.props](Directory.Build.props) enables nullable reference types, implicit usings, and warnings as errors. Core and Sdk enable trimming and Native AOT compatibility analysis; publishing support requires separate verification.

For the current source-based workflow:

1. Open [Ability.Framework.slnx](Ability.Framework.slnx) and build the solution.
2. Run [Ability.MinimalHost](test/auxiliary/Ability.MinimalHost) to exercise single-result and streaming calls against [Ability.MinimalLauncher](test/auxiliary/Ability.MinimalLauncher). The host locates the built Launcher or accepts a path through `--launcher`.
3. For a skill, reference Sdk, define input and output serialization metadata, and connect a handler through `AbilityEntry`.
4. For a host, reference Hosting, provide the Launcher path and input JSON, configure decision and consumption callbacks, and inspect both the invocation result and its lifetime report.

The minimal programs demonstrate transport integration, not the full model–role–flow architecture. The current host accepts built `.exe` and `.dll` Launchers; it does not compile `.cs` entry points on demand.

The SDK also includes file-based build presets for duplicate-source diagnostics and isolated library compilation. These presets preserve entry-point build identity. Directive support and publishing behavior depend on the actual toolchain; see the [build-preset verification record](spec/verification/sdk-build-presets.md).

| Location | Contents |
| --- | --- |
| [src/](src/) | Core, Sdk, and Hosting production libraries. |
| [test/](test/) | MSTest cases, helper processes, static-check tooling, and [engineering scripts](test/script/README.md). |
| `tool/` | Reserved for reference hosts and development integrations. |
| [spec/](spec/spec.md) | Technical contracts, architecture, implementation plans, and verification records. |

## Project status and specifications

The project is in staged implementation and refinement. Source reviews and targeted checks are recorded in the [phase-two verification record](spec/verification/phase-2.md); unified end-to-end validation and user acceptance remain pending. Cross-platform execution and Native AOT publishing are subject to their own verification results.

Resident multi-round sessions, a complete external-operation recovery ledger, and deterministic replay remain future work. NuGet distribution, Aspire integration, and release examples are described in the [first-release proposal](spec/first-release.md), not presented here as completed deliverables.

Use the [main specification](spec/spec.md) for terminology and invariants, [Architecture](spec/architecture.md) for component relationships, and the [Core](spec/core.md), [Sdk](spec/sdk.md), and [Hosting](spec/hosting.md) specifications for technical contracts. The [implementation plan](spec/plan.md) and [acceptance specification](spec/acceptance.md) track implementation and validation separately.
