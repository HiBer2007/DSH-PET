# reference — 开发期参考资料（不参与运行）

这个文件夹里的东西**不会被程序读取**，纯粹是逆向 OpenCode GO 控制台接口时留下的抓包存档，
方便以后接口改版时对照字段结构。可以随时整包删除，不影响任何功能。

| 文件 | 说明 |
| --- | --- |
| `opencode.ai_Archive [26-10-01 17-04-42].har` | 控制台页面主文档 + 静态资源抓包 |
| `opencode.ai_console_api_go_status_Archive [26-10-01 17-04-57].har` | `/console/api/go/status` 响应结构（当前额度、重置时间字段的出处） |
| `opencode.ai_console_api_request-logs_Archive [26-10-01 17-10-38].har` | `/console/api/request-logs` 响应结构（按次计费的 token/费用字段） |

> ⚠️ **这些 `.har` 里含有当时的登录 Cookie（`auth=`、`session=`）**。虽然早已过期，
> 但也别直接外发或提交到公开仓库。要删就整个文件删掉，不要只删一行。
>
> 解析逻辑的回归测试不依赖它们，用的是 `tests/DshPet.Core.Tests/Fixtures/` 里脱敏后的样本。
