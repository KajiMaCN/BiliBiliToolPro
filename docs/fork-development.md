# Fork 开发分支

这个分支整理了账号登录、任务执行与面板交互的改进，使用仓库中的默认配置即可构建。运行时账号与通知设置通过面板或环境变量提供。

## 主要改动

- 账号管理统一扫码和账号密码登录入口，支持验证码及手机端布局。
- 粉丝牌支持主播排除、置顶、分页、缓存与实时进度，观看任务按单房间依次执行。
- 今日任务按账号切换，补做展示步骤、进度与原因，直播观看与视频分享读取平台确认进度。
- 任务配置使用时间选择器，保存按钮展示状态，离开未保存页面时提供提醒。
- Cookie 检查与每日最终通知集中设置，按任务选择失败提醒和最终汇总时间。
- 保留青龙兼容、账号隔离、通知发送与任务结果判定修复。

## 构建

使用 .NET 10 SDK：

```bash
HUSKY=0 dotnet build Ray.BiliBiliTool.sln --configuration Release
HUSKY=0 dotnet test Ray.BiliBiliTool.sln -m:1 --configuration Release --no-build --filter "Category!=External"
node --test test/Ray.BiliBiliTool.Web.ComponentTests/JavaScript/password-login.test.mjs
docker build -t bilitool-web:fork .
```

通知依赖源码及许可证位于 `vendor/Ray.Serilog.Sinks`。项目直接引用该目录中的两个项目，其他依赖从 NuGet.org 还原，构建不需要额外的本地包源。

## 通知依赖

`Ray.Serilog.Sinks.Batched` 源自 [Ray.Serilog.Sinks](https://github.com/RayWangQvQ/Ray.Serilog.Sinks)，保留原程序集身份，包含队列、显式刷新和退出排空修复。`Ray.Serilog.Sinks.Compatibility` 承载 HTTP、Telegram 与企业微信的发送修复。两个项目与业务任务分别维护。

这些修复尚未发布为 NuGet 正式版本。此 fork 使用带许可证的源码引用来保持可构建性，后续可在对应版本发布后切换为包引用。

## 验证结果

2026-10-07 完整构建与 Web、Console 发布通过，1,542 项 .NET 离线回归、12 项验证码脚本测试及 192 项青龙兼容性模拟用例通过。仓库 Dockerfile 构建成功，隔离实例的登录页、认证跳转与三个前端脚本均通过检查。
