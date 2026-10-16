# 技术选型与Penpot原型 — 验证办法

> 公开版本说明：本机环境信息已脱敏，示例路径与标识不用于定位原始私有产物。

本次验证对象是选型文档、Penpot 原生设计结构与交互关系，未包含桌面应用。所有仓库行使用演示数据，不向真实仓库执行 fetch、commit、push、pull 或 tracking 配置变更。

## 测试准备

公开副本中的 file ID / page ID 为占位值；执行设计脚本前，先替换为自己实例的目标 ID。

通过用户指定实例的 MCP 连接到 RepoTracker，先核对 file ID `00000000-0000-4000-8000-000000000002` 和 page ID `00000000-0000-4000-8000-000000000003`。认证信息只保留在当前连接中，不写入项目或日志。调用过 `high_level_overview` 后按 API 文档操作。

图层结构采用 [penpot-audit.js](../design/penpot-audit.js) 经 `execute_code` 只读检查，结果存为 [penpot-audit.json](../design/evidence/penpot-audit.json)。若后续 MCP storage 已重置，先从 JSON 中记录的 board ID 恢复 board 映射，不能盲目重新运行生成脚本；生成脚本依赖本轮 storage，不是通用导入器。

## H-01：需求与选型对应

检查 README 与概述，确认 Windows 任务栏点击展开、手动添加、自建来源、只查看状态的范围一致；核查候选方案能力与生命周期的官方依据。执行 `dotnet --list-sdks`、`dotnet --list-runtimes`、`git --version` 只确认本机现有环境。

预期：文档区分推荐与批准、环境已安装与产品已实现、性能预算与实测值。fetch 的元数据写入边界不能描述为严格只读。失败时先回到 README 的当前需求及概述的来源链接，不依赖过往设想。

结果：通过。本机有 SDK 10.0.201、WindowsDesktop Runtime 10.0.5、Git 2.51.0.windows.1；未进行产品构建。开发前应锁定届时受支持的补丁版本，不能把本机旧补丁直接当发布基线。任务栏修订已核对 ShowInTaskbar、WindowState 与用户主动固定的官方说明，文档区分窗口最小化、保持展开和固定快捷方式。`Get-CimInstance Win32_OperatingSystem` 返回访问拒绝；改用 `Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'` 读取版本，确认 Windows 10 22H2 / build 19045.6466。真实窗口行为尚未测试。

## H-02：文件、图层与布局

在目标页遍历本轮画板，使用 `penpotUtils.analyzeDescendants` 与 `isContainedIn` 检查形状是否超出其画板；文本另外比较 textBounds 与文本框，容差 2px。检查顶层画板矩形不互相覆盖，统计原生文字和形状，核对库资产。

预期：不存在图层越界、文本溢出、画板互相覆盖；所有指定画板都在正确文件和页面。若失败，先判断文本框和容器谁应调整，不用缩小整屏文字掩盖问题。

结果：手动分组修订后 40 个画板、4,074 个后代图层、1,010 个文本图层；几何和文本越界均为 0，画板重叠为 0。库内有 6 个状态组件、30 个颜色资产；最终统计见证据 JSON。任务栏修订时发现的两处装饰图标文本换行已修复并复验。

## H-03：交互目标与状态结果

读取每个图层的 interactions，确保 navigate-to 的目标在本轮画板中。审查 29 条关键目标关系：原有添加、移除、来源保存、远程关闭与任务栏路径，以及 H-06 的分组维护与图钉行为。

预期：所有目标存在，关键关系全为 true；默认 6 行、添加后 7 行、移除后 5 行、首次添加后 1 行；新来源在结果列表可见，主动关闭远程与离线失败分别展示。失败时检查证据中的 routeChecks、stateChecks、brokenTargets，再核对按钮及其父画板。

结果：293 个交互，29/29 关键关系通过，失效目标为 0，原有添加及移除示例的仓库数量 6/7/5/1 正确；main 演示从任务栏收起场景开始，并展开分组面板。这是 MCP 读取原生交互图的结构验证，不冒称已经通过浏览器逐一点击或真实业务端到端测试。

## H-04：视觉导出

使用 MCP `export_shape` 导出 PNG，逐张检查中文、字重、列对齐、状态色、控件遮挡和留白。首版检查主面板、设置、设计说明、来源新增结果、首次添加结果和浅色对照；修订后检查任务栏展开与收起场景。导出保存在 [evidence](../design/evidence/) 下，首版 6 张保留为历史，当前入口见 taskbar-open.png 与 taskbar-closed.png。

预期：文字可读、图标与行对齐、点击目标对应可见控件。若字体替代、缺字或渲染异常，先检查 Penpot 可用字体和字体变体，修改后重新导出涉及画板。

结果：首版 6 张及修订后的任务栏展开、收起场景均已检查，中文与列对齐正常，修正后未见遮挡或裁切。任务栏入口可见、面板位于任务栏上方。主观风格、实际 Windows DPI 和辅助功能体验仍需后续评审与实现验收。

## H-05：仓库文档与敏感信息

执行 `node --check` 检查五个设计脚本的语法；用 Node 一次性内联检查 Markdown 相对链接是否指向现有文件，不额外创建验证批处理。运行 `git diff --check` 与 `git status --short --branch`；对新增工作区做密钥模式检查，仅输出命中计数。

预期：相对链接无断链、脚本无语法错误、无私密连接参数、保留已有 README 修改和 `.agents/`。Git 在此环境读取用户全局 ignore 时可能输出权限警告；需如实记录，但不修改用户配置来消除警告。

已运行的语法与差异命令：

```powershell
node --check 'devSchedule/技术选型与Penpot原型/design/penpot-prototype.js'
node --check 'devSchedule/技术选型与Penpot原型/design/penpot-refinements.js'
node --check 'devSchedule/技术选型与Penpot原型/design/penpot-taskbar.js'
node --check 'devSchedule/技术选型与Penpot原型/design/penpot-groups.js'
node --check 'devSchedule/技术选型与Penpot原型/design/penpot-audit.js'
git diff --check
git status --short --branch
```

任务栏修订后，上述四个语法检查与 Git 命令退出码均为 0。Node 内联检查覆盖根 README 与工作区的 6 个 Markdown 文件、29 个相对链接，断链 0；敏感参数命中 0；结构审计失败 0；PNG 证据 8 个，工作区约 577 KiB。预存的 README 修改和 `.agents/` 保留，本轮按用户反馈修改 README 的入口说明并标明当前选型建议，更新 devSchedule。Git 仍提示全局 ignore 读取权限不足，未改变全局配置；未提交或推送。

## H-06：手动分组增量

以用户已认可的任务栏入口为前提，使用 [penpot-groups.js](../design/penpot-groups.js) 添加分组场景。通过 MCP 审查新建、折叠、移动、重命名、删除与保持展开的目标关系；读取每个 `Repo group / <id>` 容器的仓库图层，比较变更前后的数量、唯一性和归属。

预期：初始工作 3 / 个人 2 / 未分组 1；新建工具组为 0，原有 6 个仓库保留；折叠只隐藏工作组的 3 行，标题仍显示数量 3；repotracker 移到个人后为 2 / 3 / 1 且无重复；重命名不改变归属；删除个人组后为工作 3 / 未分组 3，原有仓库集合不变。任务栏展开进入分组面板，设置、收起、图钉仍有正确目标。

复用 H-02 的几何与文本边界检查、H-04 的 MCP PNG 导出和 H-05 的脚本及文档检查。若失败，先检查 groupingChecks、routeChecks、容器层级和新目标画板；不使用删除仓库行的方式掩盖归属错误。

这里只验证预设原型。自由输入、重名校验、配置原子保存、重启恢复、所有行的操作、添加向导的目标分组选择及超长内容滚动均需要桌面实现后验收；未通过原型操作真实仓库。

结果：29 条关键目标关系全部通过，分组的 7 项状态检查全部为 true，成员列表保存在最新结构 JSON 的 groupingChecks 中。分组主面板通过 MCP 导出并视觉检查，文字、分组标题与数量清晰，保留原有状态列及快捷操作。后续任务栏、移动选择与删除确认的 PNG 导出遇到 25–45 秒超时，已尝试原始回执与事件流读取；未把这些画板标为逐张视觉检查通过。画板内容已保存，结构边界与交互验证通过，不影响直接在 Penpot 中评审。

本轮仓库复验：五个 `node --check`、`git diff --check`、`git status --short --branch` 均退出 0；Node 内联检查覆盖 6 个 Markdown 文件、36 个相对链接，断链 0、敏感参数命中 0、审计失败 0。现有 PNG 共 9 张，分组本轮新增 groups-main.png，工作区约 694 KiB。Git 仍有全局 ignore 读取权限警告。README 的已有改动及 `.agents/` 保留；未提交或推送，未运行产品构建。
