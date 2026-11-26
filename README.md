# PCLH-Ai-chatPlus-Plugin

挂在 [PCL Harness](https://github.com/Badqf/PCL-Harness) 启动页右侧卡片里的 AI 对话助手，基于插件 **API v10**。

和纯聊天插件的区别：它能**真的操作启动器**——装原版 / 加载器、下载并安装模组、装整合包（新建实例、带封面和默认名字）、启动游戏、改参数、开关模组、读游戏日志。所有改动都通过宿主公开 API 完成，不改启动器本体。

当前版本：**0.5.3**

## 安装

**要求：支持插件 API v10 的 PCL Harness（3.10.x 起）。**

方式一（推荐）：到 [Releases](https://github.com/yitianzhuangzhu/PCLH-Ai-chatPlus-Plugin/releases/latest) 下载 `aiplus-0.5.3.pclplugin`，启动器 →「插件」页 →「导入插件…」选中它，启用时确认一次权限弹窗。仓库里的 [`dist/`](dist) 与 Release 附件是同一个文件。

方式二（手动）：把 `main` 文件夹改名为 `aiplus`，放进 `<启动器目录>\PCLHarness\Plugins\`，重启启动器（或插件页「重新扫描」）。

装好后到「启动」页右侧卡片点「配置」填接口、密钥、模型：

| 服务 | 接口地址 | 模型名 |
| --- | --- | --- |
| DeepSeek | `https://api.deepseek.com` | `deepseek-chat` |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` 等 |
| 本地 Ollama | `http://127.0.0.1:11434/v1` | `qwen2.5` 等（无需密钥） |

## 功能

- **流式对话**：SSE 逐字渲染，`停止` 随时中断；代码块识别（等宽 + 深色底 + 语言标签）。
- **多会话**：新建 / 切换 / 改名 / 删除 / 导出 Markdown，每个会话独立文件，标题由第一句话自动生成。
- **会话数量上限**：默认保留 50 个会话（插件设置页可改，5–500）。满了以后新建会先清空会话、再清最久没动过的；带草稿的会话最后才动，清理时会告诉你是谁被清了。状态行快满时才显示「（上限 50）」，会话面板另有「清理空会话」按钮手动减负。
- **输入草稿保留**：草稿按会话保存，切到别的页面再回来、切会话、甚至重开启动器，没发出去的字都还在（含光标位置）。
- **agent.md 长期约定**：`data\agent.md` 是一份你自己写的 Markdown 规矩，每次请求都会追加到系统提示词后面。改完保存立即生效（按修改时间热加载），插件升级不会覆盖；首次运行自动生成带说明的模板。对话里 `/agent` 查看、`/agent <一句话>` 追加、`/agent reset` 重建；也可以直接跟 AI 说「记住，以后装光影前先问我」，它会用 `remember_convention` 自己写进去，跨会话生效。
- **上下文可视化**：状态行显示 `上下文 ≈ N / 64.0k (x%) · 上次 ↑输入 ↓输出`，底部有用量条。
- **工具调用**：30 个工具，模型自己决定调用；单次提问最多 12 轮工具往返，耗尽时强制收尾作答而不是硬掐断。
- **真下载、真安装**：内容文件由宿主直接写进实例并做 SHA-1 校验，任务进启器「任务管理」；整合包交给宿主安装流程，和用户把包拖进启动器完全一样。
- **CurseForge 免 Key**：走启动器同款国内镜像；配了 Key 时优先官方接口，失败自动回落到镜像。

## 工具清单

| 分类 | 工具 | 说明 |
| --- | --- | --- |
| 实例 | `list_instances` | 列出所有可启动实例 |
| | `instance_info` | 当前实例名称 / 版本 / 加载器 / 目录 / 能否装 mod |
| | `select_instance` | 切换当前实例（不启动） |
| | `launch_instance` | 启动游戏 |
| 参数 | `append_jvm_args` | 追加 JVM 参数 |
| | `append_game_args` | 追加游戏参数 |
| | `get_pending_args` | 查看已追加的参数 |
| 插件 | `list_plugins` | 列出插件及启用状态 |
| | `set_plugin_enabled` | 启用 / 禁用插件（会重载，可能打断对话） |
| 内容 | `list_mods` | 列 mods / 资源包 / 光影 |
| | `set_mod_enabled` | 启用 / 禁用（改名 `.disabled`，可逆） |
| | `read_log` | 读游戏日志（最多 200 行） |
| | `global_setting` | 读启动器全局设置某一项 |
| | `write_note` | 写笔记到插件 `notes\` |
| | `open_url` | 用浏览器打开 http/https 链接 |
| Modrinth | `search_modrinth` | 搜索模组 / 资源包 / 光影 / 数据包 |
| | `download_modrinth` | 下载并装进当前实例（或整合包新建实例） |
| CurseForge | `search_curseforge` | 搜索（走国内镜像，免 Key） |
| | `check_curseforge` | 检查镜像 / 官方两条通路状态 |
| | `download_curseforge` | 下载并安装，支持数字 id / slug / 文件页链接 |
| 下载 | `download_mods_batch` | 一次最多 20 个，`source` 可选 modrinth / curseforge |
| | `download_file` | 任意 http(s) 直链，可指定装进哪个目录 |
| | `install_download` | 把已下载的文件装进实例；整合包传 `modpack` |
| | `list_downloads` | 列出 `data\downloads` |
| 装游戏 | `install_vanilla` | 安装原版（可选 Forge / NeoForge / OptiFine / Fabric 组合） |
| | `list_loaders` | 列出可装的加载器版本 |
| | `install_loader` | 安装加载器（自动带原版） |
| | `open_download_page` | 打开启动器下载页 |
| 约定 | `read_conventions` | 读 `agent.md` 里的长期约定（不凭印象答） |
| | `remember_convention` | 把一条规矩写进 `agent.md`，跨会话生效 |

权限声明见 `main/plugin.json`：`instance.read/select/install/write`、`modpack.install`、`mods.read/write`、`launch`、`args`、`plugins`、`logs.read`、`settings.global`，以及 `net.hosts` 白名单（未列出的站点会在运行时弹窗请用户授权）。

## 目录结构

```
main/AiPlus.vb          插件全部源码（单文件，约 5.5k 行）
main/plugin.json        插件清单（id / 版本 / 权限 / 网络白名单）
build-plugin.ps1        本地编译入口（可选部署、打包）
dist/                   已打包好的 .pclplugin
```

插件运行时数据都在 `<启动器目录>\PCLHarness\Plugins\aiplus\data\`：

| 路径 | 内容 |
| --- | --- |
| `agent.md` | 你写的长期约定（追加进 system 提示词，升级不覆盖） |
| `sessions\*.txt` | 会话记录（标题 + Base64 正文） |
| `drafts\<会话id>.txt` | 输入草稿 |
| `downloads\` | 下载缓存 |
| `notes\` `exports\` | 笔记与导出 |

API Key 用 Windows DPAPI 加密存在插件设置里；CurseForge Key 还支持 `PCL_CURSEFORGE_API_KEY` / `CURSEFORGE_API_KEY` 环境变量和 `PCL\Secrets.json` 兜底。

## 自行编译

需要 Windows + PowerShell，以及下面四样东西（都不入库，见 `.gitignore`）：

1. **PCL-Harness.exe** —— 从 [PCL Harness](https://github.com/Badqf/PCL-Harness) 取，放在仓库根目录。
2. **插件 SDK** —— 解压到根目录，文件夹名保持 `PCL-Harness-Plugin-SDK-*`（`build-plugin.ps1` 自动找）。
3. **Roslyn 编译器** —— `Microsoft.Net.Compilers.Toolset` 4.14.0，`vbc.exe` 落在 `.tools\roslyn\tasks\net472\vbc.exe`。
4. **net48 参考程序集** —— `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3，解到 `.tools\refasm\build\.NETFramework\v4.8`。

```powershell
# 编译
powershell -ExecutionPolicy Bypass -File .\build-plugin.ps1 -Project main

# 编译 + 打包到 dist\
powershell -ExecutionPolicy Bypass -File .\build-plugin.ps1 -Project main -Pack

# 编译 + 部署到正在运行的启动器插件目录
powershell -ExecutionPolicy Bypass -File .\build-plugin.ps1 -Project main -Deploy
```

脚本会检查上面四个路径，缺哪个会直接报出来。宿主内部库（`PCLCS.dll`、`Newtonsoft.Json.dll`、`MeloongCore*.dll`）是启动器 exe 里的嵌入资源，需要先用 `.tools\extract` 从 exe 导出到 `.tools\libs-new\`，脚本检测到就会自动加进引用。

## 许可

[GPL-3.0](LICENSE)。插件通过宿主公开 API 工作，不分发启动器本体；PCL Harness 本体与插件 SDK 遵循其各自的许可。
