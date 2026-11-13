# Ability.Sdk 构建预设

文件式入口只需声明程序包：

```csharp
#!/usr/bin/env dotnet
#:package Ability.Sdk@1.0.0
```

NuGet 自动导入包内的构建预设。过渡期需要 `.lib` 写法时，由开发环境在工程根目录的 Directory.Build.props 配置：

```xml
<PropertyGroup Condition="'$(FileBasedProgram)' == 'true'">
  <FileBasedProgramsItemMapping>$(FileBasedProgramsItemMapping);.lib=AbilityVirtualLib</FileBasedProgramsItemMapping>
</PropertyGroup>
```

这属于环境规则，入口无需声明另一个 SDK。独立物理文件使用 AppContext.BaseDirectory，内嵌资源使用所属程序集 API；资源读取不依赖 SDK 工具类。

需要把通用切片独立编译时，调用方声明 `#:include Shared.cs.lib`，磁盘文件仍是 Shared.cs。预设用 dotnet 文件式构建为它创建独立程序集，再交给标准程序集引用解析。切片不写 OutputType；跨程序集使用的成员仍须公开。不同目录的同名文件使用不同程序集身份与输出目录，同一文件的重复声明只构建一次。

支持 include 元数据的 .NET SDK 可使用 `#:include Shared.cs As=Library`。当前 11.0.100 尚不能直接解析该语法；预设已提供对 Compile 项 As=Library 元数据的同一处理路径。

文件式程序仅追加 CS2002 静默，普通 .csproj 保持原诊断。预设不修改入口的输出类型、发布方式或程序集身份。

默认配置、模板和内置数据编为 EmbeddedResource，随所属 DLL 分发，通过所属程序集读取。必须传递独立物理内容时使用声明完整的 Library 与原生 `#:ref`，由 .NET 的项目引用机制处理内容复制。部署后可修改的配置由 Launcher 在 AppContext.BaseDirectory 读取并覆盖内嵌默认值，类库不自行搜索外部配置。
