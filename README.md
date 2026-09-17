# 作业提醒 · HomeworkReminder

用 Avalonia 写的 Windows 桌面待办应用：自动读取**长江雨课堂**的作业与公告，
以仿 Microsoft To Do 的 Fluent 界面呈现。

![界面](docs/screenshots/app-main.png)

## 功能与特性

* **作业清单**：按课程归集作业，显示截止时间与剩余天数，逾期自动标红

* **真实完成状态**：完成进度直接来自雨课堂的作答记录，而不是「截止时间过了没有」

* **公告未读**：公告按已读/未读分组，支持单条已读与全部已读

* **两种登录方式**：应用内嵌官方登录页微信扫码；没有 WebView2 的机器可改用粘贴 Cookie 登录

* **Fluent 半透明界面**：亚克力磨砂背景，支持自定义壁纸与透明度滑块

* **深色模式**：浅色 / 深色 / 跟随系统，即时切换

* **托盘常驻**：关闭时可选择「最小化运行」，收到新作业随时点开

* **隐私安全**：登录态文件用 Windows DPAPI 加密存放，只有本机当前用户能解开

* **学期可配置**：编辑数据目录下的 `settings.json` 即可切换学期与学校，不用改代码

## 快速开始

双击运行 `HomeworkReminder.Desktop.exe`，首次启动会显示登录页：

1. **扫码登录**（默认）——应用内嵌雨课堂官方登录页，微信扫码即可；
2. **粘贴 Cookie**——缺少 WebView2 运行时时点「改用 Cookie 登录」，从浏览器
   F12 → Network 复制任意 `v2/api/web` 请求的 `cookie` 请求头粘贴进去。

登录成功后自动同步，之后每次启动都会刷新作业列表。

## 数据存放

| 位置                                               | 内容                                                                                                         |
| ------------------------------------------------ | ---------------------------------------------------------------------------------------------------------- |
| `%LOCALAPPDATA%\HomeworkReminder\`               | `session.json`（DPAPI 加密的登录态）、`local-state.json`（本地勾选/已读）、`settings.json`（壁纸/透明度/主题等设置）、`startup.log`（启动日志） |
| exe 旁边的 `HomeworkReminder.Desktop.exe.WebView2\` | 登录页浏览器内核的缓存（WebView2 默认行为）                                                                                 |

数据目录可用环境变量 `HOMEWORKREMINDER_DATA_DIR` 覆盖。

## 打包发布

```powershell
.\tools\publish.ps1 -Mode trim   -NoPdb   # 裁剪单文件：40.8 MB，双击即用（推荐分发）
.\tools\publish.ps1 -Mode single -NoPdb   # 完整单文件：约 100 MB，兼容性最好
.\tools\publish.ps1 -Mode fdd             # 框架依赖：体积最小，目标机需装 .NET 10 运行时
.\tools\publish.ps1 -Mode sc              # 自包含多文件
```

产物在 `publish\<模式>\` 下。**分发前请删除产物目录里的 `.WebView2` 文件夹**
（里面是本机登录页的 cookie 缓存），只发 exe 文件本身。

### 发布产物自检

```powershell
.\HomeworkReminder.Desktop.exe --selfcheck out.png   # 窗口/绑定自检 + 离屏渲染
.\HomeworkReminder.Desktop.exe --jsoncheck           # 序列化路径自检（裁剪版必过项）
```

两者退出码为 0 即表示产物健康。

## 项目结构

```
HomeworkReminder/                 共享项目：模型、服务、视图、ViewModel
  Models/                         TodoItem 等数据模型
  Services/
    YktApiClient.cs               雨课堂接口客户端（唯一的网络出入口）
    SyncService.cs                课程/日志/进度三源合并 → 待办
    SessionStore.cs               登录态持久化（DPAPI 加密）
    AppJsonContext.cs             源生成 JSON 序列化上下文
    YktLoginService.cs            WebView 扫码登录
  Views/                          MainWindow / TodoShellView / LoginView
  Styles/TodoTheme.axaml          仿 To Do 的 Fluent 控件样式（含深色主题）
HomeworkReminder.Desktop/         桌面宿主（启动、托盘、自检入口）
tools/                            构建、发布、冒烟测试脚本
docs/                             接口实测文档与调研记录
```

## 开发

```powershell
.\tools\run.ps1                              # 构建并启动（数据目录指向 .appdata）
dotnet run --project tools\SyncSmoke         # 无界面跑一遍真实同步，打印待办结果
```

> 本仓库默认离线还原配置（`NuGet.Config` 指向仓库内 `packages/`）。在可联网的
> 机器上可删除 `NuGet.Config`、`packages/`、`.nuget-packages/`，直接 `dotnet restore`。

## 已知限制

* 雨课堂没有「学生标记作业完成」的接口，应用内的勾选只影响本地视图

* 全部接口来自雨课堂网页端自身，非公开契约，前端改版可能导致失效；
  代码对每个调用做了容错（单课程失败不中断整次同步，并汇总为界面警告条）

* Android / iOS / Browser 端沿用模板但未经验证

