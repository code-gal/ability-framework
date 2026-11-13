using Ability.Core;
using Ability.Sdk;

namespace Ability.Model;

/// <summary>
/// 一次受管操作建议或采用的运行策略模型。
/// 遵循文件切片架构规约使用显式私有字段声明；公开 API 统一采用 <see cref="MaxRetries"/> 表达重试次数上限。
/// </summary>
public sealed partial class RunPolicyMdl
{
    /// <summary>
    /// 单次尝试的业务等待期限私有字段。
    /// </summary>
    private TimeSpan? _deadline;

    /// <summary>
    /// 期限计时锚点参考私有字段。
    /// </summary>
    private DeadlineReference _deadlineReference = DeadlineReference.FromStart;

    /// <summary>
    /// 期限到达后的处理动作私有字段。
    /// </summary>
    private DeadlineAction _deadlineAction = DeadlineAction.Cancel;

    /// <summary>
    /// 操作重试次数上限私有字段。
    /// </summary>
    private int _maxRetries;

    /// <summary>
    /// 进度汇报间隔私有字段。
    /// </summary>
    private TimeSpan _progressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 单次尝试的业务等待期限；为空表示不设业务超时，仍受系统有限兜底保护。
    /// </summary>
    public TimeSpan? Deadline
    {
        get => _deadline;
        init => _deadline = value;
    }

    /// <summary>
    /// 期限计时锚点参考（自尝试开始或自策略更新生效），默认为 <see cref="DeadlineReference.FromStart"/>。
    /// </summary>
    public DeadlineReference DeadlineReference
    {
        get => _deadlineReference;
        init => _deadlineReference = value;
    }

    /// <summary>
    /// 期限到达后的处理动作，默认为 <see cref="DeadlineAction.Cancel"/>（请求停止）。
    /// </summary>
    public DeadlineAction DeadlineAction
    {
        get => _deadlineAction;
        init => _deadlineAction = value;
    }

    /// <summary>
    /// 操作重试次数上限。默认为 0，表示不进行重试（即至多允许 1 次尝试）。
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        init => _maxRetries = value;
    }

    /// <summary>
    /// 连续两次进度汇报之间的最小限频时间间隔，默认为 250 毫秒。
    /// </summary>
    public TimeSpan ProgressInterval
    {
        get => _progressInterval;
        init => _progressInterval = value;
    }

    /// <summary>
    /// 校验策略模型参数的数值合法性；非法时抛出 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    public void Validate()
    {
        if (Deadline is { } deadline && deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Deadline), "业务期限若设置必须大于零。");
        }

        if (!Enum.IsDefined(DeadlineReference))
        {
            throw new ArgumentOutOfRangeException(nameof(DeadlineReference), "未定义的期限锚点。");
        }

        if (!Enum.IsDefined(DeadlineAction))
        {
            throw new ArgumentOutOfRangeException(nameof(DeadlineAction), "未定义的期限动作。");
        }

        if (MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "重试上限不能小于零。");
        }

        if (ProgressInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProgressInterval), "进度汇报间隔不能为负。");
        }
    }

    /// <summary>
    /// 将本模型转换为线协议与 Core 共享契约 <see cref="RunPolicyBody"/>。
    /// 依据规格换算尝试上限：MaxAttempts = checked(MaxRetries + 1)；
    /// 动作映射为旧 v3 兼容的 "Notify" 与 "Stop"。
    /// </summary>
    /// <returns>构建的运行策略契约体。</returns>
    public RunPolicyBody ToBody()
    {
        Validate();
        var maxAttempts = checked(MaxRetries + 1);
        var actionText = DeadlineAction == DeadlineAction.Remind ? "Notify" : "Stop";
        return new RunPolicyBody(
            Deadline,
            DeadlineReference.ToString(),
            actionText,
            maxAttempts,
            ProgressInterval);
    }

    /// <summary>
    /// 从 Core 共享契约 <see cref="RunPolicyBody"/> 构建模型。
    /// 依据规格换算重试上限：MaxRetries = checked(body.MaxAttempts - 1)；严格核验 MaxAttempts >= 1。
    /// </summary>
    /// <param name="body">共享契约策略体。</param>
    /// <returns>构建的运行策略模型。</returns>
    public static RunPolicyMdl FromBody(RunPolicyBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!RunPolicyBody.Validate(body, out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(body), error);
        }

        var reference = ParseReference(body.DeadlineReference);
        var action = ParseAction(body.DeadlineAction);
        var retries = checked(body.MaxAttempts - 1);

        var model = new RunPolicyMdl
        {
            Deadline = body.Deadline,
            DeadlineReference = reference,
            DeadlineAction = action,
            MaxRetries = retries,
            ProgressInterval = body.ProgressInterval
        };

        model.Validate();
        return model;
    }

    /// <summary>
    /// 校验共享契约策略体的数值与枚举合法性，杜绝非法值掩盖。
    /// 直接复用 Core 的权威规则校验。
    /// </summary>
    /// <param name="body">待校验的运行策略契约体。</param>
    public static void ValidateBody(RunPolicyBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!RunPolicyBody.Validate(body, out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(body), error);
        }
    }

    /// <summary>
    /// 解析字符串格式的期限计时锚点；非法格式直接抛出异常，杜绝默认值掩盖。
    /// </summary>
    /// <param name="text">锚点文本。</param>
    /// <returns>解析出的锚点枚举值。</returns>
    private static DeadlineReference ParseReference(string? text)
    {
        if (string.Equals(text, "FromUpdate", StringComparison.OrdinalIgnoreCase))
        {
            return DeadlineReference.FromUpdate;
        }

        if (string.Equals(text, "FromStart", StringComparison.OrdinalIgnoreCase))
        {
            return DeadlineReference.FromStart;
        }

        throw new ArgumentException($"未知的期限计时锚点：'{text}'，仅支持 'FromStart' 与 'FromUpdate'。", nameof(text));
    }

    /// <summary>
    /// 解析字符串格式的期限处理动作；兼容旧 v3 的 Notify/Stop 与新 Remind/Cancel，非法格式直接抛出异常。
    /// </summary>
    /// <param name="text">动作文本。</param>
    /// <returns>解析出的动作枚举值。</returns>
    private static DeadlineAction ParseAction(string? text)
    {
        if (string.Equals(text, "Remind", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "Notify", StringComparison.OrdinalIgnoreCase))
        {
            return DeadlineAction.Remind;
        }

        if (string.Equals(text, "Cancel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "Stop", StringComparison.OrdinalIgnoreCase))
        {
            return DeadlineAction.Cancel;
        }

        throw new ArgumentException($"未知的期限处理动作：'{text}'，仅支持 'Remind'/'Notify' 与 'Cancel'/'Stop'。", nameof(text));
    }
}
