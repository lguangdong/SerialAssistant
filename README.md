# SerialAssistant · 串口调试助手

一个基于 **.NET 8 + WPF** 的 Windows 桌面串口调试工具，采用 MVVM 架构，支持串口收发、HEX/ASCII 双模式、定时发送、快捷指令与收发日志记录。

界面与注释均为简体中文，开箱即用。

---

## 功能特性

| 分类 | 能力 |
| --- | --- |
| 端口管理 | 自动枚举系统串口，WMI 获取设备友好名（如 `COM3-USB-SERIAL CH340`），支持手动刷新并保留上次选择 |
| 参数配置 | 端口、波特率（可手输非标准值）、数据位、校验位、停止位、流控；内置 6 组常用参数预设一键套用 |
| 连接管理 | 打开/关闭串口；连接状态三态显示（未连接 / 已连接 / 设备已断开），带状态指示灯 |
| 接收区 | HEX / ASCII 显示切换、时间戳开关、自动滚动、一键清空、收发字节统计 |
| 发送区 | HEX / ASCII 发送切换、`Ctrl+Enter` 快速发送、清空 |
| 定时发送 | 勾选后按设定间隔（最小 10ms）在后台线程循环发送，可运行时修改间隔 |
| 快捷指令 | 常用指令列表，支持「＋」保存当前发送框内容、「－」删除、双击立即发送 |
| 日志记录 | 勾选后实时把收发内容写入日志文件，路径显示在界面上 |
| 性能保护 | UI 批量刷新（20fps）、单次消费上限背压保护、显示区字符上限裁剪 |

内置参数预设：`115200 8N1`、`9600 8N1`、`19200 8N1`、`9600 7E1 (PLC)`、`2400 8E1 (电表)`、`9600 8N2 (Modbus)`。

内置快捷指令：`AT`、`01 03 00 00 00 0A`、`01 04 00 00 00 02`、`AA 55`。

---

## 环境要求

- **操作系统**：Windows 10 / 11（依赖 `System.IO.Ports` 与 WMI）
- **运行时**：.NET 8 Desktop Runtime
- **开发**：Visual Studio 2022（17.8+）或 `dotnet` CLI 8.0+

---

## 快速开始

### 1. 克隆并打开

```powershell
git clone <仓库地址>
cd SerialAssistant
```

### 2. 命令行运行

```powershell
dotnet run --project SerialAssistant/SerialAssistant.csproj
```

### 3. 生成可执行文件（发布）

`PublishSingleFile` **必须**配合 `-r win-x64`，否则会报 `NETSDK1097`。

#### 方案 A：自包含单文件（推荐 · 免装运行时）

产物为单个 exe，拷到任何 Windows 10/11 机器双击即可运行。

```powershell
dotnet publish SerialAssistant/SerialAssistant.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o ./publish-single
```

> 实测体积约 **155 MB**（自包含会打进整个 .NET 运行时）。

#### 方案 B：依赖式单文件（体积最小）

产物约 **0.7 MB**，但目标机需预先安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet publish SerialAssistant/SerialAssistant.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -p:DebugType=none `
  -o ./publish-fd
```

#### 体积与产物对照

| 模式 | 关键参数 | 产物 | 实测大小 |
| --- | --- | --- | --- |
| 自包含单文件 | `--self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` | `SerialAssistant.exe` ×1 | 154.72 MB |
| 自包含（不加单文件） | `--self-contained true` | exe + 5 个 WPF 原生 dll | 146.88 MB + 约 7.8 MB |
| 依赖式单文件 | `--self-contained false -p:PublishSingleFile=true` | `SerialAssistant.exe` ×1 | 0.73 MB |

#### 两个注意点

1. **WPF 的原生库默认不会打进单文件**，会以 `D3DCompiler_47_cor3.dll`、`PenImc_cor3.dll`、`PresentationNative_cor3.dll`、`vcruntime140_cor3.dll`、`wpfgfx_cor3.dll` 形式留在 exe 外面。必须显式加 `-p:IncludeNativeLibrariesForSelfExtract=true` 才是真正的单文件（代价是首次启动会向临时目录自解压，稍慢）。
2. **WPF 不支持裁剪**：加 `PublishTrimmed=true` 会直接构建失败（裁剪会破坏 XAML 的反射路径），因此自包含体积无法靠裁剪压小，约 155 MB 属正常值。

### 4. Visual Studio

用 VS 打开 `SerialAssistant.sln`，按 `F5` 启动即可。

若要图形化发布：右键 `SerialAssistant` 项目 → **发布** → 目标选「文件夹」→ 部署模式选「独立」→ 勾选「生成单个文件」→ 发布。

---

## 使用说明

1. **选择端口**：下拉框选择目标串口（点击「刷新」可重新扫描）。
2. **配置参数**：可直接选常用预设，或手动设置波特率等参数。参数仅在未连接时可编辑。
3. **打开串口**：点击「打开串口」，状态栏指示灯变绿即连接成功。
4. **接收数据**：接收区实时显示数据；可切换 HEX 显示、时间戳、自动滚动。
5. **发送数据**：在发送框输入内容，点击「发送」或 `Ctrl+Enter`；勾选「HEX 发送」时按十六进制解析。
6. **定时发送**：勾选「定时发送」，设置间隔（毫秒，最小 10ms）。
7. **快捷指令**：点击「＋」把发送框内容存为快捷指令，双击列表项立即发送。
8. **记录日志**：勾选「记录日志」，日志写入 `我的文档\SerialAssistantLogs\SerialLog_时间戳.txt`。

### HEX 输入格式

发送框勾选「HEX 发送」时，支持多种写法（大小写、有无分隔符均可）：

```
01 03 AB
0103AB
01,03,AB
01-03-AB
0x01 0x03
```

---

## 项目结构

```
SerialAssistant/
├── SerialAssistant.sln
└── SerialAssistant/
    ├── App.xaml                      # 应用入口，StartupUri 指向 MainWindow
    ├── App.xaml.cs
    ├── MainWindow.xaml               # 主界面布局（串口设置 / 接收区 / 发送区 / 状态栏）
    ├── MainWindow.xaml.cs            # 少量视图逻辑（自动滚动、快捷指令双击）
    ├── AssemblyInfo.cs
    ├── SerialAssistant.csproj        # net8.0-windows + WPF
    ├── ViewModels/
    │   └── MainViewModel.cs          # 唯一 ViewModel，全部交互逻辑与状态
    └── Services/
        ├── Interface/
        │   └── ISerialPortService.cs # 串口服务抽象
        └── SerialAssistant/
            ├── SerialPortService.cs  # 串口打开/关闭/读写，后台读线程
            ├── SerialPortUtil.cs     # 串口枚举（WMI + GetPortNames 合并）
            ├── SerialPortInfo.cs     # 端口信息模型
            ├── SerialSetting.cs      # 串口参数模型
            ├── HexUtil.cs            # ASCII / HEX 转换
            └── LogWriter.cs          # 日志写入（批量 Flush）
```

---

## 技术栈

- **框架**：.NET 8（`net8.0-windows`）+ WPF
- **MVVM**：CommunityToolkit.Mvvm 8.4.2（源生成器 `[ObservableProperty]` / `[RelayCommand]`）
- **串口**：System.IO.Ports 10.0.11
- **设备枚举**：System.Management 8.0.0（WMI）

架构细节见 [docs/技术架构文档.md](docs/技术架构文档.md)。

---

## 已知限制与待办

- **退出未清理**：`MainViewModel.Dispose()` 目前无调用方，程序退出时串口与日志未显式释放，可在 `MainWindow.Closing` 中挂接。
- **写超时处理**：`Write` 超时（默认 2000ms）目前仅提示发送失败并停止定时发送，后续建议按「设备异常」走强制断开流程。
- **发送静默成功**：`SerialPortService.Send` 在端口未打开时返回 `0` 而不抛异常，调用方无法区分「发送成功」与「未发送」。
- **编码范围**：`HexUtil` 的 ASCII 转换仅覆盖可打印 ASCII（0x20~0x7E），0x80 以上会变成 `?`，暂不支持中文与多字节编码。
- **快捷指令未持久化**：快捷指令与参数预设为运行时内存数据，重启后恢复默认。

---

## 许可

本项目仅用于学习与调试用途。
