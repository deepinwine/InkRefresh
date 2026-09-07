# InkRefresh · 大上墨水屏刷新助手（Windows）

绿色单文件 Windows 小工具：为 **大上(DASUNG)墨水屏显示器**（Paperlike 全系列）提供**定时自动刷新 / 清残影**。

- 单个 `InkRefresh.exe`（56KB），双击即用，**无需安装、无需运行库**（Windows 10/11 自带 .NET Framework 4.8）
- 不写注册表，拷 U 盘也能用
- 功能设计参考 macOS 上的 InkControl（闭源、无 Windows 版），本工具为独立实现的免费替代

## 功能

| 功能 | 说明 |
|---|---|
| 自动刷新 | 按设定间隔自动清残影；托盘图标实时显示"下次刷新时间(还有 N 秒)"；三个开关（启动即刷新/最小化启动/启用热键）默认全部开启 |
| 自定义间隔 | 界面里改秒数即时生效（1 秒～24 小时），自动保存 |
| 保存配置 | 改动即时自动保存 + 显式[保存配置]按钮；隐藏窗口/退出时兜底保存，重启后原样生效 |
| 立即刷新热键 | 默认 `Ctrl+Alt+R`，任何程序下按了就立刻刷新（可改/可关） |
| 暂停/继续 | 界面按钮或托盘菜单 |
| 开机自启 | 托盘菜单一键开关（"启动"文件夹快捷方式，不写注册表） |
| 刷新方式 | ① 模拟官方驱动快捷键（推荐，默认 `Alt+E`，可改成驱动里的任意组合）② 全屏黑屏重绘（无需驱动）③ 两者 |

## 快速开始

1. 先手动按一次刷新快捷键，确认屏幕闪刷 → 官方驱动在运行。本工具默认模拟 **`Alt+E`**；大上 HD 系列老驱动说明书默认为 `Alt+C`，以你驱动里实际设置的为准，在设置里改成一致即可。
   新机型（253/103/13K/Color）用 **Paperlike Client**，快捷键为自定义，需与本工具设置一致。
2. 双击 `InkRefresh.exe`，立即按设定开始自动刷新（托盘出现图标）。
3. 在设置窗口改"刷新间隔（秒）"即可。

> 点窗口 X = 最小化到托盘；退出请用托盘右键菜单。

## 下载

到 [Releases](../../releases) 下载 zip 解压即可，无需安装。

## 从源码构建

```bash
# 任意平台（Windows/macOS/Linux）安装 .NET SDK 后：
dotnet publish InkRefresh/InkRefresh.csproj -c Release -o dist

# 运行测试
dotnet run --project InkRefresh.Tests -c Release
```

- `InkRefresh/` — 主程序（C# WinForms，net48，零 NuGet 依赖）
- `InkRefresh.Tests/` — 核心逻辑单元测试（快捷键解析 / 配置读写容错）

## 常见问题

**启动后没反应？** ① 手动按快捷键确认官方驱动在运行；② Paperlike Client 用户把客户端里的快捷键填到"驱动刷新快捷键"（两边一致）；③ 右键 exe 以管理员身份运行；④ 改用"全屏黑屏重绘"方式。

**开机自启失效？** 删除/移动旧版本文件夹不会清理"启动"文件夹里的旧快捷方式。v1.1.2 起：自启状态会校验快捷方式是否指向当前 exe（失效的旧快捷方式自动视为未开启），重新勾选即重建并强制恢复"已启用"状态；仍不行请查看任务管理器→启动应用。

**设置/复选框记不住？** 若程序放在不可写目录（如 Program Files）或直接在压缩包里运行，`settings.ini` 写入会失败。v1.1.3 起：exe 目录不可写时自动改存 `%APPDATA%\InkRefresh\settings.ini`（旧值自动迁移）；[保存配置]按钮保存后会重读磁盘校验，失败会弹窗说明原因，实际使用的配置文件路径记录在 `InkRefresh.log`。

**杀毒/SmartScreen 提示？** exe 未做数字签名，放行即可。

**刷新瞬间在打字？** 相当于帮你按了一下驱动快捷键；把两边快捷键都改成冷门组合（如 `Ctrl+Alt+F9`）可避免误触发。

**新机型自带 Auto-Clear？** 253/103/13K 的 Paperlike Client 自带定时清残影；本工具适合老机型与需要更灵活控制的场景。

## 参考

- 大上官网 / 驱动下载：<https://www.dasung.com>
- InkControl（macOS 原版，闭源）：<https://github.com/simpleapples/InkControl>
- Paperlike HD 系列说明书（Alt+C 飞刷清残影）：<https://www.scribd.com/document/442972276>

## License

[MIT](LICENSE)
