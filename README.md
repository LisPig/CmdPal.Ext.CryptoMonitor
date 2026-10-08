# Crypto Monitor · PowerToys 命令面板加密行情扩展

> 币价常驻在你的命令面板 Dock 上：一眼看涨跌，点开看走势。

**中文** · [English](./README.en.md)

`CmdPal.Ext.CryptoMonitor` 是 [PowerToys 命令面板](https://learn.microsoft.com/windows/powertoys/command-palette/overview)（Command Palette，CmdPal）的扩展。它在命令面板里提供加密行情监控页、币种搜索、走势图和价格提醒，并把每个监控币做成 Dock 上常驻的价格按钮。

后台常驻约 30 MB 内存，只在需要时网络请求，价格源全部直连（不依赖代理）。

---

## 功能

- **Dock 价格条** —— 每个监控币一个常驻按钮，显示现价和 24h 涨跌，点一下打开它的走势图
- **监控列表** —— 现价、24h 涨跌、市值排名、24h 最高/最低、24h 成交额；数据超过 2 分钟会标注「N 分钟前的数据」
- **走势图** —— 1 小时 / 24 小时 / 7 天 / 30 天四个周期，本地渲染 PNG（无第三方图表服务、无外部依赖）
- **币种搜索** —— 输入 `btc` / `eth` / 中文名以外的符号或全名即可；CoinGecko 搜不到时回落到本地 2.2 万条币种目录，**离线也能搜**
- **价格提醒** —— 高于 / 低于某价（按显示货币）触发系统通知，价格回落后自动重新布防
- **多行情源容错** —— Binance Vision / Gate.io / CoinEx 依次兜底，全部直连；CoinGecko 负责市值、排名、Logo、搜索补充
- **人民币显示** —— 行情按 USD 获取，CNY 显示时自动换算（无汇率时回落 CoinGecko 原生 CNY 报价）
- **本地缓存** —— 上次价格、币种目录、K 线、Logo 全部落盘，重启后立刻显示上次行情而不是空列表
- **中文界面**，`Win` 级轻量：无账号、无遥测、无后台服务

## 安装

### 方式一：下载 Release（.msix）

1. 从 [Releases](../../releases) 下载 `CmdPal.Ext.CryptoMonitor_x.y.z.msix` 和 `dev.cer`
2. 信任这个自签名开发证书（只需要一次）：

   ```powershell
   Import-Certificate -FilePath .\dev.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
   ```

3. 安装包：

   ```powershell
   Add-AppxPackage .\CmdPal.Ext.CryptoMonitor_0.1.0.0.msix
   ```

> 需要 Windows 11 + PowerToys（命令面板已启用）。包是 framework-dependent 的，需要 **.NET 10 运行环境**（`winget install Microsoft.DotNet.Runtime.10`）。

### 方式二：从源码构建

需要 .NET 10 SDK、PowerToys（命令面板）、开发者模式（`elevate.ps1` 可代为开启）。

```powershell
git clone https://github.com/LisPig/CmdPal.Ext.CryptoMonitor.git
cd CmdPal.Ext.CryptoMonitor
powershell -ExecutionPolicy Bypass -File build-deploy.ps1
```

脚本会 publish → 生成 AppxManifest → 打包 → 用本地自签名证书签名 → loose 注册安装，最后在命令面板里执行一次 **Reload**（或重启 PowerToys）即可看到扩展。

## 使用

- **打开**：命令面板里搜 `Crypto Monitor`（或直接输入币种符号，会走「搜索加密货币行情」的 fallback 项）
- **Dock**：命令面板 Dock 设置里添加 Crypto Monitor 的价格条，监控币就会常驻在屏幕边缘
- **设置**：命令面板 → Crypto Monitor 的「设置」，可配置
  - 显示货币（USD / CNY）
  - 监控列表（逗号分隔，如 `btc,eth,sol,doge`；非主流币可以直接写 CoinGecko id，如 `zcash`）
  - 刷新间隔（15–300 秒）
  - Dock 显示币种数（1–10）
  - 价格提醒（`btc>100000; eth<2500`，分号分隔）与提醒开关
  - 数据源（自动 / 仅 CoinGecko）与 CoinGecko 元数据开关
  - CoinGecko Demo API Key（可选，提高额度）

## 数据与隐私

扩展不发送任何用户数据，只请求公开行情接口。所有本地文件：

| 位置 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\CryptoMonitor\prefs.ini` | 设置（监控列表、提醒等） |
| `%LOCALAPPDATA%\CryptoMonitor\quotes.json` | 上次行情（重启后秒显） |
| `%LOCALAPPDATA%\CryptoMonitor\coins.tsv` | CoinGecko 币种目录快照（约 2.2 万条，每 12 小时刷新） |
| `%LOCALAPPDATA%\CryptoMonitor\charts\` `history\` | 走势图 PNG 与 K 线缓存 |
| `%ProgramData%\CryptoMonitor\icons\` | 币种 Logo 缓存（宿主需要能直接读，故放这里） |
| `%TEMP%\CryptoMonitor-cmdpal.log` | 运行日志（1 MB 轮转） |

## 实现要点

- **扩展形态**：`IExtension` + `CommandProvider`（`CryptoMonitorCommandsProvider`），通过 `windows.comServer` + `com.microsoft.commandpalette` appExtension 注册，宿主以 `-RegisterProcessAsComServer` 拉起独立进程
- **图表**：`Services/ChartRenderer.cs` 手写光栅化（3 倍超采样 + 5×7 点阵字体）与 PNG 编码（zlib），零第三方图形依赖；大缓冲复用、渲染串行化，避免 LOH 碎片
- **内存**：后台常驻约 30 MB。命令项只创建一次（工具箱的 `WeakEventListener` 实际强引用 item，重复创建会永久泄漏）、图表预取轮转 + 8 MB 级复用缓冲、`System.GC.ConserveMemory` + 空闲时 `EmptyWorkingSet`
- **网络**：`Services/Network.cs` 自行读取 WinINET 代理设置，交易所走直连、CoinGecko 走系统代理，代理变化时自动重建客户端

## 项目结构

```
CmdPal.Ext.CryptoMonitor/
├─ CryptoMonitorExtension.cs       # IExtension 入口
├─ CryptoMonitorCommandsProvider.cs# 命令提供者（顶层命令 / fallback / Dock）
├─ Pages/                          # 监控列表、详情、走势图、自定义提醒
├─ Dock/PriceDockItem.cs           # Dock 上的价格按钮
├─ Services/                       # 行情、币种目录、图表渲染、缓存、网络
├─ Settings/CryptoMonitorSettings.cs
└─ Package.appxmanifest
```

命名说明：项目/程序集/命名空间为 `CmdPal.Ext.CryptoMonitor`（对齐官方 `Microsoft.CmdPal.Ext.*` 的写法），包标识为 `LisPig.CmdPal.Ext.CryptoMonitor`（`厂商.产品` 约定，保证唯一）；命令面板里显示的名字是 **Crypto Monitor**。

## 许可

MIT。项目基于 PowerToys 命令面板扩展模板搭建（模板部分版权归 Microsoft，MIT 许可）。
