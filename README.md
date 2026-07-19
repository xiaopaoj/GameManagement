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
- 扫描路径直属 ZIP、RAR 文件和直属游戏文件夹；文件夹内部压缩包延迟到“准备游玩”阶段递归识别，避免机械硬盘扫描时读取大量文件头尾。
- 文件扫描显示候选统计进度、百分比和当前路径，扫描结束前使用阻塞遮罩禁用其他界面操作并阻止关闭主窗口。
- 隐藏已添加候选项目。
- 候选项目多选批量添加，自动生成游戏名称和“初始版本”。
- 候选项目支持按名称或路径、来源类型、磁盘和添加状态筛选。
- 添加游戏和新增版本时记录来源文件数量、总体积、修改时间和 SHA-256 指纹，目录来源生成聚合指纹。
- 游戏详情支持修改显示名称和备注，并显示原始路径有效状态。
- 版本管理支持添加用户命名版本、编辑版本名称与备注、查看来源、切换当前版本及根据文件名、大小、修改时间和 SHA-256 结果重新定位来源。
- 多游戏盘配置及标准工作目录创建。
- 游戏盘支持默认项、启用状态和最低保留空间设置，并显示路径状态、磁盘类型、总容量及剩余空间。
- 游戏库、任务中心、打开目录和启动入口。
- 持久化任务中心记录扫描、来源指纹和准备游玩任务的状态、进度、当前路径、错误及临时目录。
- 扫描、来源指纹和准备游玩支持取消；软件异常退出后将未完成任务恢复为“已中断”。
- 失败、取消和中断任务保留临时目录，支持查看及一次确认安全清理；成功准备任务自动清理临时目录。
- 最终游戏目录先在 `GameTemp` 暂存，完成基线后再提交到 `Games`，防止失败时留下半成品目录。
- 支持通过“查看详情”按钮或双击游戏列表进入游戏详情。
- 已为需求定义的准备游玩、归档、特殊归档、备份、存档、版本、重新定位和删除原始文件建立可发现入口；未完成的后端流程会显示明确的开发状态说明。
- 主导航已包含游戏库、文件扫描、设置、任务中心、存档管理、备份管理和操作历史。
- 自包含单文件 EXE 发布配置。
- 基础自动化测试，包含扫描边界、取消、任务序列化与中断恢复、临时目录安全边界及来源指纹变更回归测试。
- “准备游玩”基础流程：空间估算、游戏盘选择、复制来源、两级压缩文件选择、ZIP/RAR 头尾区域识别、混合文件伪装前缀剥离、规范化临时压缩文件、最大文件 ZIP/RAR 依次尝试、DPAPI 密码保存、递归确定首个有效 EXE 所属游戏目录、展示目录直属 EXE/index.html 和 SHA-256 文件基线。

## 仍需实现

- 分卷完整性专项检查和缺失分卷提示。
- 主游戏 EXE 图标提取及候选评分优化。
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
