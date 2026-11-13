using System.Runtime.CompilerServices;

// 仅允许框架测试构造内部协调器与登记状态，不向运行时调用方公开测试接口。
[assembly: InternalsVisibleTo("Ability.Framework.Tests")]
