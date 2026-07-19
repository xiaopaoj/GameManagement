# 游戏管理软件

面向 Windows 的单机游戏原始压缩文件、游戏盘和存档统一管理工具。

当前版本：`1.0.0`

> 项目目前处于开发阶段，现有可执行文件仅包含基础游戏库、扫描路径和游戏盘管理功能，并非完整正式版本。

## 项目目标

- 管理分散在多个目录中的 ZIP、RAR 游戏原始文件。
- 将当前游玩的游戏准备到用户选择的固态游戏盘。
- 支持两级解压、独立密码保存、游戏目录和主游戏 EXE 识别。
- 对游戏目录和用户确认的 Windows 系统存档目录进行存档管理。
- 支持普通归档、特殊归档、版本切换和每日计划备份。
- 最终发布为无需额外安装运行时的单文件 Windows EXE。

完整需求参见：[游戏管理软件需求与实施计划_v1.0.0.md](./游戏管理软件需求与实施计划_v1.0.0.md)。

## 当前已实现

- `.NET 8 + WPF` 中文桌面应用工程。
- EXE 所在目录的 `data`、`logs` 自动创建与写入权限检查。
- JSON 数据持久化和原子替换写入。
- 多扫描路径管理。
- ZIP、RAR 和目录候选扫描。
- 隐藏已添加候选项目。
- 游戏及首个用户命名版本添加。
- 多游戏盘配置及标准工作目录创建。
- 游戏库、任务中心、打开目录和启动入口。
- 自包含单文件 EXE 发布配置。
- 基础自动化测试。

## 仍需实现

- 两级解压向导和分卷文件选择。
- DPAPI 解压密码管理。
- 游戏目录、主游戏 EXE 和图标识别。
- 文件基线及增量 Hash 比较。
- 正常与异常存档快照。
- 游戏运行和系统存档目录监控。
- 普通归档、特殊归档和版本切换。
- 手动备份、每日计划备份和备份查看。
- 完整删除确认与回收站处理。

## 项目结构

```text
GameManagement/
├─ src/GameManagement.App/       # WPF 应用源码
├─ tests/GameManagement.Tests/   # 自动化测试
├─ tools/                        # 本地开发辅助脚本
├─ GameManagement.sln
├─ global.json
└─ 游戏管理软件需求与实施计划_v1.0.0.md
```

`build`、`bin`、`obj`、本地 SDK、运行数据库和日志不会提交到 Git。

## 开发环境

- Windows 10 或 Windows 11
- .NET 8 SDK
- Visual Studio 2022、JetBrains Rider 或 Visual Studio Code（可选）

如果系统未安装 .NET 8 SDK，可以将 SDK 安装到项目本地目录：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dotnet-install.ps1 -Version 8.0.100 -InstallDir .\tools\dotnet -NoPath
```

## 构建与测试

使用项目本地 SDK：

```powershell
.\tools\dotnet\dotnet.exe restore GameManagement.sln
.\tools\dotnet\dotnet.exe build GameManagement.sln -c Debug
.\tools\dotnet\dotnet.exe test GameManagement.sln -c Debug
```

如果已经全局安装符合 `global.json` 要求的 SDK，可以将命令中的 `.\tools\dotnet\dotnet.exe` 替换为 `dotnet`。

## 发布单文件 EXE

```powershell
.\tools\dotnet\dotnet.exe publish .\src\GameManagement.App\GameManagement.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\build\release
```

发布产物：

```text
build\release\GameManagement.exe
```

## 运行数据

程序运行后在 EXE 所在目录创建：

```text
data/
logs/
```

这些目录包含用户配置、游戏记录、任务状态和运行日志，不应提交到 Git。

## 编码约定

- 文档、代码注释、接口错误提示、提交信息、PR 描述和最终说明默认使用中文。
- 中文文件统一使用 UTF-8 编码。
- 代码标识符、协议字段、第三方库要求或既有英文约定除外。
