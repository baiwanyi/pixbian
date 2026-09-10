# 长期记忆

> 只收规范、稳定事实与可复用判别式；代码定义、具体数值、一次性排障流水进当日日志。
> 界面 / 控件 / 布局 / 渲染 / 缩略图 / 视频播放 / UI 线程的已验证事实在 `MEMORY-WinUI.md`，涉及这些领域时先读它。
> 2026-09-10 十分：拆出界面与渲染分册，恢复本文可完整注入。

## 项目与开发环境
- Pixbian：WinUI 3 本地相册浏览器。WASDK 2.4.0 元包（WinUI 实为 2.3.6）+ `net10.0-windows10.0.26100.0`（最低 17763，LangVersion 13）。测试基线 356。
- `dotnet` 用 `C:\Program Files\dotnet\dotnet.exe`；缩进 4 空格；文件首部 3–8 行中文模块说明；构建必带 `-warnaserror`（普通构建只报警告会掩盖 CI 红，改测试/WebServer 后须 `-c Release -warnaserror` 复验）。
- 硬件：C SSD；D 机械盘（媒体库 `D:\Downloads\*`，余量长期偏低，查「慢/卡」先看余量）。
- 一键脚本用「显式 build + Start-Process」两段式（OneDrive 工作区产物落盘可能被锁）；构建前确认应用未运行（MSB3026）；加 RID 后产物落 `win-x64\` → 「改动没生效」先看 `(Get-Process Pixbian).Path`。
- 终端：含中文命令会语法错误/乱码 → 中文 commit 用 `git commit -F <UTF-8 文件>`；含中文 `.ps1` 须 UTF-8 with BOM，改后须校验 BOM 与 Parser 语法；诊断脚本放 `C:\Temp\`。
- PowerShell 陷阱：`@($null).Count -eq 1`（判空须过滤 `$null`）；`New-Object T (a,b)` 被当数组参数 → 用 `[T]::new()`；函数返回 `byte[]` 会被流展开成 `object[]` → 调用处须 `[byte[]](...)` 强转，否则重载解析失败**静默不写任何字节**。
- 工具事实：WAL 库用 `SqliteOpenMode.ReadWrite` 可与运行中应用并发读；`search_content` 的 glob 不支持 `!` 取反；查 MSBuild 属性 `dotnet msbuild x.csproj -getProperty:名`；读中文 md 行数用 `-Encoding UTF8` + `.Count`（`Measure-Object -Line` 会少算）。
- Python 3.14 + Pillow 12（无 numpy / ImageMagick / SVG 光栅化）；Pillow 12 无 `ImageChops.divide` → 含 alpha 缩放走「预乘 → Lanczos → 反预乘」；验证 ICO 用 Win32 `LoadImage(IMAGE_ICON)`，不用 `System.Drawing.Icon`。
- **MVVM Toolkit 8.4.0 的 `[ObservableProperty]` partial property 形式仅 `LangVersion=preview` 下生成实现** → `13.0`/`14.0` 报 CS9248；项目保留字段版 + `NoWarn;MVVMTK0045`。诊断：`-p:EmitCompilerGeneratedFiles=true`。
- 按行号批量改多区间必须降序；机械重排优先整文件重写；NuGet 审计用 `WarningsNotAsErrors` 豁免 NU19xx。
- 稀疏包文件关联：显示名是**关联级**属性 → 每个扩展名要有各自名称（「JPG 文件」）就必须各写一条 `uap:FileTypeAssociation`；`Name` 仅允许字母数字与句点且须全局唯一（用 `pixbian.image.jpg` 形式）；Logo 可跨关联复用同一图标。清单改动后用 `MakeAppx pack /nv` 试打包即可校验 schema，无需签名或管理员。

## 分层与协作规范
- Core 最底层、零项目引用、纯 `net10.0`；Data/Imaging/Media/WebServer 单向引用 Core，UI 引用全部；跨层数据走 `Pixbian.Core.Models`。
- Core 禁用 WIC / `Windows.Graphics.Imaging`（绑 windows TFM 会破坏 Core.Tests）→ Core 定抽象 + UI 注入实现；FFmpegInteropX 只被 UI 引用 → 复用解码策略的工厂只能在 UI 层。
- 页面需要主窗口时经 `App.Services` 按需解析，不要注入（窗口持有页面 → 循环依赖）。查看三条平行链路：双击图片 → ImageViewerWindow、双击视频 → 主窗口播放态、幻灯片 → SlideShowWindow；轻量预览下图片且主窗口未建时直开查看器窗口（关闭走 `Environment.Exit(0)`），视频仍须走主窗口。
- 敏感信息禁止硬编码；API 响应 DTO 白名单；日志脱敏（WinRT 异常记 HResult）；禁用 `Trace.WriteLine`（一律 `AppLog`）。
- 改动 > 5 文件须先确认是否 commit（Conventional Commits，中文内容）；> 2 文件的大改先取得用户确认方案；每次修改记当日 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷）。
- **【强制】功能是否正常一律由用户手工验证**，AI 不得用自启 + 截屏 + UIA 脚本代替人工验收（仅可用于崩溃取证）。
- **【强制】会话中途磁盘文件可能被用户改动**：读到与先前不一致的内容须复核再动手。

## 通用工程方法论
- 性能定位顺序：先测真实数据规模 → 再测单点耗时 → 最后改代码；**优化前先证伪前提**。
- 后台任务让出比例比绝对时长更关键（批次 2.5s 时节流 ≥1.5s）并设批次数上限；常驻任务须节流 + 排他；排他优先 `Interlocked.CompareExchange`（持 CTS/`SemaphoreSlim` 字段触发 CA1001）。
- **查 API 是否存在一律读包内二进制**：WinRT 投影 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 读 `Microsoft.WinUI.dll` 配套 `.xml` 的 `T:`/`P:`/`M:` 索引；主题键与模板默认值读 `Themes/generic.xaml`；winmd 不可 `Assembly.LoadFrom`。
- 「慢」/「冻结」判别：①单核 100% + 日志停滞 = 布局死循环（托管栈空、无崩溃日志）；②CPU 高 + 日志增长 = 业务慢；③CPU 增量 0 + 全线程 Wait = 渲染停摆。多嫌疑用叠加减法逐轮排除。
- 概率性缺陷被性能优化引爆是常态：不要回滚优化，去找被掩盖的根因；**把「为什么不做」的实测证据写进配置注释**（而非只写报告）。
- 巨型文件拆分（partial，已实测四例）：提成员签名行号 → 划职责分区 → 分段精读 → 各 partial 整文件写入 → 主文件重写 → 构建迭代修 using。纯搬运零行为变更，以 `-warnaserror` + 全量测试为护栏；类声明与基类列表只留主文件。
- 死代码审计判据：编译器恒 0 警告，须人工核对引用计数——整类查实例化 + DI 注册 + 订阅；方法查调用点；字段查「只写不读」；`x:Name` 无人用 ≠ 控件未用。
- XamlCompiler 会缓存旧类型元数据：改 VM 属性类型报 CS1503 时 `dotnet clean` 即解；批量核对「注释与代码是否一致」应写只读脚本扫 XAML 注释，比人工比对可靠。

## 索引 / 取数 / 已定决策
- **音乐库独立于图库**：曲目落 `music_tracks`，不进 `media_items`；不能用常驻监控 → 设置变更时重扫 + 启动时从库恢复（机械盘全量扫盘须 `Task.Run`）。
- 索引两阶段：扫描只写文件属性，宽高时长由后台分批回填；失败必须落「已失败」否则反复捞取；写回只覆盖尺寸/时长列，用户数据（收藏/评分/分类）用 `COALESCE` 保护。
- 展示的大小/日期/时长全部来自索引库，不实时读文件系统；库里 `taken_utc` 是文件系统时间而非 EXIF 拍摄时间。
- 分页：非随机排序走**键集游标**（排序值 + id 双键；OFFSET 深翻慢 151×），随机排序走固定 `random_rank` 游标；删除收缩集合后游标取已加载末条，天然不回退。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机用固定序列保证分页稳定，不用 SQL `RANDOM()`。
- 删除走回收站（`IRecycleBinService` → `Microsoft.VisualBasic.FileIO.FileSystem`）并同步清索引；「从索引移除」仅删记录。搜索保留 `LIKE '%词%'` 子串语义（FTS5 因中文 2 字词受 trigram ≥3 限制而放弃，勿重复评估）。
- 局域网共享不内置 TLS（配反向代理）；可绑定指定网卡，非法地址显式抛异常而非静默回退。
- 用户数据备份：JSON 数据包以 `path` 为条目业务键、分类/分组以 `name` 互引；导入策略「合并，导入文件为准」；仅备份扫描源与音乐目录（不含界面偏好与 Web 密码哈希）；OneDrive 同步走「启动时检查 + 补齐错过周期」，根目录探测顺序 `OneDrive` → `OneDriveConsumer` → `OneDriveCommercial`。
- 已推翻旧定论：「滑动窗口触发条件苛刻」不成立；埋点改「异步化 + 默认关闭」；查看器 `PreviousImage` 已正确回收。

## 验证与入口排查
- 「点了没反应」先查入口是否存在（跳转常是「按 Tag 查导航项 → 找不到静默 return」）；`git log -S '<Tag>'` 为空 = 功能从未接入。验证「设置即时生效」类功能必须走真实 UI 路径。
- 验证 UI 用截屏 + UIA 枚举 ListItem（Name + 坐标）；WinUI `TextBlock` 不把 Text 暴露为 UIA Name，按钮可用 `InvokePattern`。HEIC/AVIF 依赖 WIC 编解码器扩展。
