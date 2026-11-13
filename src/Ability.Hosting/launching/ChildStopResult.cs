// <copyright file="ChildStopResult.cs" company="MProject">
// Copyright (c) MProject. All rights reserved.
// </copyright>

namespace Ability.Hosting;

/// <summary>
/// 子调用树停止与租约回收真实执行结果。
/// </summary>
/// <param name="InvocationKey">被停止子树的根调用键。</param>
/// <param name="ExitCode">根调用退出码（若未确认退出为 null）。</param>
/// <param name="CleanedLeaseCount">已回收的租约数量。</param>
/// <param name="Outcome">回收终态裁决（"Reaped" 或 "Unresponsive"）。</param>
/// <param name="ExitedNaturally">是否自然退出。</param>
internal readonly record struct ChildStopResult(
    string InvocationKey,
    int? ExitCode,
    int CleanedLeaseCount,
    string Outcome,
    bool ExitedNaturally);
