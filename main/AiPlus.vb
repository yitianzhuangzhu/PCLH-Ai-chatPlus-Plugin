''' <summary>
''' PCL Harness 插件：AI 助手 Pro（API v10）。
''' 在「启动」页右侧挂一张对话卡片：流式输出、代码块识别、上下文用量可视化、
''' 多会话（新建 / 切换 / 改名 / 删除 / 导出，输入草稿按会话保留，切页不丢）、
''' 以及真正能把内容下载下来（任意直链、Modrinth / CurseForge 搜索与下载，
''' 并一键装进当前实例的 mods / 资源包 / 光影目录，整合包交给宿主安装）。
''' </summary>
Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Web.Script.Serialization
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

''' <summary>一条对话消息。</summary>
Public Class ChatMessage
    Public Role As String
    Public Content As String
End Class

''' <summary>一个会话的元信息（列表里显示用的那份）。</summary>
Public Class ChatSession
    Public Id As String = ""
    Public Title As String = ""
    Public Count As Integer = 0
    Public UpdatedAt As Date = Date.MinValue
End Class

Public Class AiPlusPlugin
    Implements PCL.IPclPlugin
    Implements PCL.IPclPluginUi
    Implements PCL.IPclPluginUi2
    Implements PCL.IPclPluginModpack

    ' JSON / 转义用到的字符（放常量里，避免到处写字面量）
    Private Shared ReadOnly ChBack As Char = Convert.ToChar(92)    ' \
    Private Shared ReadOnly ChQuote As Char = Convert.ToChar(34)   ' "
    Private Shared ReadOnly ChN As Char = Convert.ToChar(110)      ' n
    Private Shared ReadOnly ChT As Char = Convert.ToChar(116)      ' t
    Private Shared ReadOnly ChR As Char = Convert.ToChar(114)      ' r
    Private Shared ReadOnly ChB As Char = Convert.ToChar(98)       ' b
    Private Shared ReadOnly ChF As Char = Convert.ToChar(102)      ' f
    Private Shared ReadOnly ChU As Char = Convert.ToChar(117)      ' u

    ''' <summary>代码块围栏。</summary>
    Private Const Fence As String = "```"

    ' ==================== 状态 ====================

    Private Host As PCL.PluginHost
    Private ReadOnly Messages As New List(Of ChatMessage)
    Private ReadOnly SyncRoot As New Object()

    ''' <summary>
    ''' API v9：宿主在整合包安装结束时回调的结果。InstallModpack 的任务在后台跑，
    ''' 我们在工作线程上同步等它的 State，真实实例名与失败原因从这个回调里取。
    ''' </summary>
    Private LastPackResult As PCL.PluginModpackResult

    ' ==================== 多会话状态 ====================

    ''' <summary>当前会话 id（文件名去掉扩展名），例如 20261122-204512-a1b2。</summary>
    Private SessionId As String = ""
    ''' <summary>会话标题（写进会话文件首行）。</summary>
    Private SessionTitle As String = "新对话"
    Private ReadOnly Sessions As New List(Of ChatSession)
    ''' <summary>改名模式：下一句回车当成新标题，而不是发给模型。</summary>
    Private RenameMode As Boolean = False
    ''' <summary>
    ''' 会话数量上限（插件设置页可改）。会话文件是纯文本、每条消息都存着，
    ''' 不设上限会一直堆下去；超出时先清空会话、再清最久没动过的。
    ''' </summary>
    Private SessionCap As Integer = 50

    ' ==================== 输入草稿状态 ====================
    ' 卡片是「每次进启动页就 Factory 重建一次」的，输入框里的字跟着卡片一起没了。
    ' 草稿按会话 id 存在内存 + data\drafts\ 里，切页、切会话、重载插件都能拿回来。

    ''' <summary>会话 id → 未发送的输入草稿（内存副本）。</summary>
    Private ReadOnly DraftCache As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    ''' <summary>会话 id → 光标位置，切回来能接着打。</summary>
    Private ReadOnly DraftCaret As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
    ''' <summary>改了还没落盘的草稿 id。</summary>
    Private ReadOnly DraftDirty As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private DraftTimer As System.Windows.Threading.DispatcherTimer
    ''' <summary>还原草稿 / 改名占位期间屏蔽写回，避免把别人的字串进当前会话。</summary>
    Private SuppressDraft As Boolean
    ''' <summary>改名时被标题临时占用前的那份草稿，改完 / 取消要还回输入框。</summary>
    Private RenameDraft As String = Nothing
    Private RenameCaret As Integer = 0

    Private BaseUrl As String = "https://api.deepseek.com"
    Private ApiKey As String = ""
    Private ModelName As String = "deepseek-chat"
    Private CtxLimit As Integer = 64000
    Private SysPrompt As String = ""
    ''' <summary>CurseForge API Key（加密存储）。为空时按环境变量 / PCL\Secrets.json 兜底。</summary>
    Private CfKey As String = ""

    Private Const PromptVer As Integer = 12

    Private Function DefaultSysPrompt() As String
        Return "你是 PCL Harness 启动器里的助手。回答简洁、直接，用中文；不确定就直说不确定，不要编。" & Environment.NewLine & Environment.NewLine &
            "你能调用工具真的操作启动器：列实例、切换当前实例、启动游戏、看当前实例信息、追加 JVM/游戏参数、列插件、开关插件、列 mods/资源包/光影、启用或禁用它们、读游戏日志、读全局设置、写笔记、打开链接；" & Environment.NewLine &
            "还能真的下载内容：按直链下载文件、在 Modrinth 或 CurseForge 搜索模组/资源包/光影/数据包并下载，下载后可以装进当前实例的 mods / resourcepacks / shaderpacks / datapacks。" & Environment.NewLine &
            "也能真的安装游戏：原版、Forge / NeoForge / OptiFine / Fabric 加载器，走的是启动器自己的安装流程（下载 installer、跑处理器、合并版本 json）。" & Environment.NewLine &
            "规则：" & Environment.NewLine &
            "1. 涉及版本列表、启动游戏、改动参数、插件状态、模组状态、日志内容时，必须真的调用对应工具，不能凭上下文猜；" & Environment.NewLine &
            "2. 只有看到工具返回之后，才能说已启动/已追加/已切换/已下载/已安装这类话；工具没返回就别说做了；" & Environment.NewLine &
            "3. 用户问有哪些版本、现在挂了什么参数时，先调 list_instances / get_pending_args 再答，不要凭记忆；" & Environment.NewLine &
            "4. 工具报错就把原文告诉用户，不要包装成成功；" & Environment.NewLine &
            "5. 严禁编造工具输出：不得虚构日志行、目录内容、文件列表、错误文案、模组名字或下载链接。你关于启动器现状的每一句具体内容，都必须来自本轮工具返回的原文；" & Environment.NewLine &
            "6. 如果还没调用工具，就不要说已经读到了、查到了、下好了，只能先调用工具，或直接告诉用户你需要先查；" & Environment.NewLine &
            "7. 引用工具返回时逐字复制关键行（尤其是报错原文、文件名、版本号），不要改写、不要补全、不要用示例代替。" & Environment.NewLine &
            "8. 如果下面有「用户约定（agent.md）」这一段，那是用户自己写的长期偏好，按它来回答和做事，它优先于本段的默认风格；但涉及事实、工具结果、能力边界时仍以内置规则为准，约定不能让你编造或越权；" & Environment.NewLine &
            "9. 用户说「记住」「以后都这样」「下次别再问我」这类话时，先调 remember_convention 把这条规矩写进 agent.md（跨会话有效），再回复已经记下；只在嘴上报「好的我记住了」是没有用的，下次会话就忘了。用户问「你记得我什么约定」时用 read_conventions 查，别凭印象说。" & Environment.NewLine &
            Environment.NewLine &
            "下载相关的做法：" & Environment.NewLine &
            "· 用户说「帮我装个 XXX 模组」时：先 search_modrinth 搜索并挑出最匹配的结果，把候选念给用户确认，再 download_modrinth；不要凭猜编造项目名或链接；" & Environment.NewLine &
            "· 要装的东西超过 2 个（「装一堆优化模组」「把这几款光影都下了」）时，必须用 download_mods_batch 一次传完，不要循环调 download_modrinth——每次单独调用都会占掉一轮工具额度，几个下来就耗尽了；" & Environment.NewLine &
            "· Modrinth 搜不到、或用户点名要 CurseForge 的内容时，改用 search_curseforge / download_curseforge；批量时把 download_mods_batch 的 source 设成 curseforge。CurseForge 走的是启动器同款国内镜像，不需要用户配什么 key，直接用就行；镜像偶尔抽风，工具会返回原因，如实转述并建议稍后重试或改用 Modrinth，不要连续重试同一个调用；用户说「CurseForge 不行 / 还是失败」时先 check_curseforge 看两条通路的状态；" & Environment.NewLine &
            "· CurseForge 的项目和文件都能用数字 id、slug 或 curseforge.com 链接传入；用户贴的是文件页链接（带 /files/数字）时，那个文件就是他要的，别自己换成最新版；" & Environment.NewLine &
            "· 整合包要用 project_type=modpack 下载：下载完会交给启动器安装（和用户把包拖进启动器一样），它新建的是一个新实例，不是装进当前实例；实例名默认取项目名、封面自动用项目图标，所以版本管理页里和用户自己下载的整合包看起来一样；用户点名要别的名字时传 instance_name；这种安装很慢（可能几百个 mod），工具会一直等到装完才返回，不要在等待期间说「应该好了」；装完提醒用户去启动页刷新版本列表、选中新实例再启动。已经在 data\downloads 里的整合包文件用 install_download 传 folder=modpack 也能装（补上 project / source 才有封面）；" & Environment.NewLine &
            "· 模组 / 资源包 / 光影 / 数据包装进当前实例时，文件是由启动器直接下载进实例目录并校验的，不再经过 data\downloads；任务会出现在「任务管理」里，所以返回「已下载并装进…」就是真的落地了，不用再去 list_mods 验证；" & Environment.NewLine &
            "· 搜索时能确定加载器（fabric/forge/neoforge/quilt）和游戏版本就用参数限定，不填则由服务端返回全部；装错了实例会起不来；" & Environment.NewLine &
            "· 下载默认装进「当前实例」，装之前先用 instance_info 确认当前实例和它是否可装模组；" & Environment.NewLine &
            "· 下载失败（主机被拒、超时、体积超限）要如实转述，不要重试第三次以上，也不要换一个自己编的链接再试；" & Environment.NewLine &
            "· 不要下载 .exe / .jar 形式的「安装器」再尝试运行——你没有执行程序的能力。" & Environment.NewLine &
            Environment.NewLine &
            "装游戏版本 / 加载器的做法：" & Environment.NewLine &
            "· 用户说「装个 1.20.1 原版」→ install_vanilla；说「装 Forge / NeoForge / OptiFine / Fabric」→ install_loader（加载器安装会自动带原版，不用先装原版）；" & Environment.NewLine &
            "· 用户明确问「有哪些版本可选」时才 list_loaders；只为「确认一下」而反复查同一个东西是浪费轮次，别做；" & Environment.NewLine &
            "· 安装是后台任务：工具返回「已开始」就立刻把关键信息（版本名、目标目录、去右下角任务栏看进度）告诉用户，然后结束这一轮。不要紧接着调 list_instances 去验证——装完之前那个版本一定不在列表里；" & Environment.NewLine &
            "· 只有在用户后来问「装好了吗」时，才 list_instances 查一次；查到就报查到，没查到就说任务栏可能还在跑或失败了、让他看日志，不要连查好几遍。" & Environment.NewLine &
            Environment.NewLine &
            "装游戏和装内容必须分两步（最容易出错的地方）：" & Environment.NewLine &
            "· 顺序固定：装游戏本体 / 加载器 / 整合包 → 等它真的装完 → 切到那个实例 → 才能下载 mod / 资源包 / 光影 / 数据包；" & Environment.NewLine &
            "· 后台安装刚发起（工具返回「已开始」「进度看任务栏」）时，绝对不能接着调任何下载工具。插件会直接拒绝并让你等——那不是故障，是防止把 mod 下进旧实例；" & Environment.NewLine &
            "· 用户说「做个 XX 整合包」「装个 1.20.1 加几个优化 mod」时，先把方案说清楚（版本、加载器、要哪些 mod），确认后按上面的顺序一步步做，不要一口气把所有工具都发出去；" & Environment.NewLine &
            "· 整合包本身就带 mods，装完不要再往里补 mod，除非用户明确说要加；" & Environment.NewLine &
            "· 内容装进的是「当前选中的实例」，不是刚装好的那个。装完先 select_instance 切过去，或者提醒用户回「启动」页刷新并选中新版本，再下内容。" & Environment.NewLine &
            Environment.NewLine &
            "查资料与诊断的做法：" & Environment.NewLine &
            "· 用户用中文问模组 / 物品 / 方块 / 机制 / 教程时，先 search_mcmod（MC百科）：中文名、俗称和适用版本对得最准，返回标题 + 链接 + 摘要；要看正文、前置、支持的版本就 fetch_url 抓返回的条目页；" & Environment.NewLine &
            "· 要英文原名、下载文件、更新日志、项目页细节时再用 search_modrinth / search_curseforge；两边配合：MC百科查资料，Modrinth / CurseForge 拿下载；" & Environment.NewLine &
            "· 别凭训练记忆断言模组功能或兼容性，尤其版本号和「最新」这类会过时的信息；" & Environment.NewLine &
            "· 用户说「崩了 / 起不来 / 报错」时，先 diagnose_crash 把崩溃报告和日志拿全再判断；报告里没有的结论不要编，也不知道就说不知道，并指出下一步查哪里；" & Environment.NewLine &
            "· 问「我装了哪些 mod / 有没有冲突 / 缺什么前置」时用 mods_scan（读 jar 里的真实 modid 和依赖声明），不要拿文件名猜版本；" & Environment.NewLine &
            "· 要确认画质、内存、某个 mod 的配置时，用 read_instance_file 读 options.txt / config 下的原文，照着实际值给建议；" & Environment.NewLine &
            "· fetch_url 只能抓白名单里的站（MC百科、Modrinth、CurseForge、GitHub、Mojang、镜像），名单外的会弹窗问用户；被拒绝就换站或直接说明拿不到，不要反复抓同一个地址；" & Environment.NewLine &
            "· 联网有三条路，别用错：① 服务商自带联网（GLM 的 web_search、Kimi 的 $web_search、Qwen 的 enable_search）由服务端检索，你直接答并给出来源链接即可，不要在答完后又说「我无法联网」；② 有 web_search 工具时优先调它，一次拿到标题 + 链接 + 摘要；③ 已知具体网址才用 fetch_url 抓正文。" & Environment.NewLine &
            "· 搜不到就说搜不到，不要用训练记忆假装是搜到的结果；训练记忆里的版本号、模组发布时间经常过时，涉及「最新 / 现在」的问题一律以工具结果为准。" & Environment.NewLine &
            "· 联网结果和训练记忆要分清楚：说「我查到的」时必须有对应工具返回，否则就说「我印象里」，并且提醒可能过时。" & Environment.NewLine &
            Environment.NewLine &
            "能力边界（很重要）：" & Environment.NewLine &
            "· 你没有任何执行代码或命令行的手段，也不能读任意路径的文件：能读的只有当前实例目录里的文本文件（read_instance_file）、宿主提供的 logs / crash-reports，以及插件自己的 data 目录；" & Environment.NewLine &
            "· 你自己能直接写的只有本插件的 data 目录（notes、downloads、agent.md）；模组 / 资源包这类内容是交给启动器写进当前实例的（宿主负责下载、校验、进度），你不能直接编辑实例里的文件内容，也不能改配置或删文件——read_instance_file 只给读，读到什么就照着说什么；" & Environment.NewLine &
            "· 改动类操作（启动游戏、切实例、追加参数、开关插件、启用禁用模组、下载并安装内容）只在用户明确要求时做，做完把工具原文回给用户；" & Environment.NewLine &
            "· 开关插件会让启动器重载插件、可能打断本次对话，操作前先说清后果；不能操作本插件自己；" & Environment.NewLine &
            "· 启用/禁用模组只是把文件名改成 .disabled，可逆；不要改动文件内容；" & Environment.NewLine &
            "· 权限是显式声明的：没有声明的能力会直接抛 UnauthorizedAccessException，遇到就如实转述；连未知站点会在运行时弹窗请用户授权，被拒绝就停下；" & Environment.NewLine &
            "· 用户要求做这些以外的事（改配置文件、删文件、跑脚本、装新软件），直接说做不到，并给出用户自己操作的步骤。"
    End Function

    Private Client As HttpClient
    Private Cts As CancellationTokenSource
    Private Busy As Boolean
    Private CurTarget As ChatMessage
    Private CurBubble As StackPanel
    Private ReadOnly Pending As New StringBuilder()
    Private LastFlush As DateTime = DateTime.MinValue

    ' 用量
    Private LastPromptToks As Integer = 0
    Private LastCompletionToks As Integer = 0

    ' ==================== UI 引用 ====================

    Private MsgPanel As StackPanel
    Private Scroller As PCL.MyScrollViewer
    Private InputBox As TextBox
    Private SendBtn As PCL.MyButton
    Private StopBtn As PCL.MyButton
    Private StatusText As TextBlock
    Private UsageText As TextBlock
    Private CtxGrid As Grid
    Private ConfPanel As StackPanel
    Private SessionPanel As StackPanel
    Private SessionList As StackPanel
    Private CfgBase As TextBox
    Private CfgKey As TextBox
    Private CfgModel As TextBox
    Private CfgCf As TextBox
    ' 工具调用：本轮请求里已收到的 tool_calls（按 index 累积参数片段）
    Private ToolCalls As New List(Of ToolCallReq)
    ' 本次提问内，「工具+参数」的失败次数（见 ExecTool 的重复调用保护），每次发送时清空
    Private ReadOnly FailSeen As New Dictionary(Of String, Integer)
    ' 工具轮次上限。装加载器这种任务天然要多轮（查版本 → 装 → 查实例 → 汇报），
    ' 4 轮太紧，会被“轮次已达上限”硬掐断；这里放宽到 12，并在耗尽时强制收尾作答。
    Private Const MaxToolRounds As Integer = 12
    ' 上一轮实际执行了多少次工具（状态行展示，用于识别“没查就编”）
    Private LastToolCount As Integer = 0

    ''' <summary>粗略判断这句话是不是在要求操作启动器。是的话强制模型第一轮必须调工具。</summary>
    Private Function LooksLikeAction(T As String) As Boolean
        If String.IsNullOrWhiteSpace(T) Then Return False
        Dim Ks() As String = {"读", "日志", "列", "查", "看", "启动", "开", "跑", "追加", "加", "改", "设", "写", "记", "保存", "打开",
                              "插件", "实例", "版本", "mod", "参数", "jvm", "cpu", "内存", "启动器", "链接", "网址", "记录"}
        For Each K In Ks
            If T.IndexOf(K, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
        Next
        Return False
    End Function

    Private Class ToolCallReq
        Public Id As String
        Public Name As String
        ''' <summary>function（本地执行）或 builtin_function（Kimi 的 $web_search，客户端只回传参数、由服务端检索）。</summary>
        Public Kind As String = "function"
        Public Args As New StringBuilder()
    End Class

    ' ==================== 模型自带联网 ====================
    ''' <summary>是否让模型侧自带的联网搜索（GLM / Kimi / DeepSeek / Qwen）去查资料。</summary>
    Private WebSearchOn As Boolean = True
    ''' <summary>这次请求是否真的把服务端联网工具塞进去了（失败后据此决定要不要降级重试）。</summary>
    Private ServerSearchInjected As Boolean = False
    ''' <summary>本进程里服务端联网工具被接口拒过一次就不再贴，免得每轮都白失败一遍。</summary>
    Private ServerSearchBroken As Boolean = False
    ''' <summary>识别联网方式所依赖的三项配置；变了才重新给被拒过的接口一次机会。</summary>
    Private SearchSig As String = ""

    ' ==================== 安装顺序 ====================
    ''' <summary>已经在后台跑、还没落地的安装（加载器 / 整合包）的目标实例名。空 = 现在没有安装任务。</summary>
    Private InstallPending As String = ""
    ''' <summary>上面那个安装是什么时候发起的，用来做过期兜底。</summary>
    Private InstallPendingAt As DateTime = DateTime.MinValue
    ''' <summary>最近一次装完的实例名。下载内容时如果当前实例不是它，要提醒一句别装错地方。</summary>
    Private LastInstalled As String = ""

    ' ==================== 生命周期 ====================

    Public Sub OnLoad(Host As PCL.PluginHost) Implements PCL.IPclPlugin.OnLoad
        Me.Host = Host
        LoadSettings()
        RebuildClient()
        InitSessions()
        Host.LogInfo($"AI 助手 Pro 已加载：模型 {ModelName}，接口 {BaseUrl}，会话 {Sessions.Count} 个，当前「{SessionTitle}」{Messages.Count} 条")
    End Sub

    Public Sub OnUnload() Implements PCL.IPclPlugin.OnUnload
        Try
            If Cts IsNot Nothing Then Cts.Cancel()
        Catch ex As Exception
        End Try
        ' 卸载 / 重载插件前把没发出去的草稿落盘，下次打开还在
        Try
            If DraftTimer IsNot Nothing Then DraftTimer.Stop()
            If InputBox Is Nothing Then
                FlushDrafts()
            ElseIf InputBox.Dispatcher.CheckAccess() Then
                SaveDraftNow()
            Else
                InputBox.Dispatcher.Invoke(New Action(Sub() SaveDraftNow()))
            End If
        Catch ex As Exception
        End Try
        SaveSession()
        If Host IsNot Nothing Then Host.LogInfo("AI 助手 Pro 已卸载")
    End Sub

    ''' <summary>
    ''' API v9：整合包安装结束（成功 / 失败 / 取消都会来）。只记录，等待方自己读。
    ''' </summary>
    Public Sub OnModpackInstalled(Result As PCL.PluginModpackResult) Implements PCL.IPclPluginModpack.OnModpackInstalled
        Try
            LastPackResult = Result
        Catch ex As Exception
        End Try
    End Sub

    ' ==================== 配置 ====================

    Private Function GetStr(Key As String, Def As String) As String
        Try
            Dim V = Host.GetSetting(Key, CObj(Def))
            If V Is Nothing Then Return Def
            Dim S = V.ToString()
            If String.IsNullOrWhiteSpace(S) Then Return Def
            Return S
        Catch ex As Exception
            Return Def
        End Try
    End Function

    Private Function GetInt(Key As String, Def As Integer) As Integer
        Try
            Dim V = Host.GetSetting(Key, CObj(Def))
            If V Is Nothing Then Return Def
            Dim N As Integer
            If Integer.TryParse(V.ToString(), N) Then
                If N > 0 Then Return N
            End If
            Return Def
        Catch ex As Exception
            Return Def
        End Try
    End Function

    Private Function GetBool(Key As String, Def As Boolean) As Boolean
        Try
            Dim V = Host.GetSetting(Key, CObj(Def))
            If V Is Nothing Then Return Def
            If TypeOf V Is Boolean Then Return CBool(V)
            Dim S = V.ToString().Trim().ToLowerInvariant()
            If S = "true" OrElse S = "1" OrElse S = "yes" OrElse S = "on" Then Return True
            If S = "false" OrElse S = "0" OrElse S = "no" OrElse S = "off" Then Return False
            Return Def
        Catch ex As Exception
            Return Def
        End Try
    End Function

    Private Sub LoadSettings()
        BaseUrl = GetStr("apiBase", "https://api.deepseek.com")
        ApiKey = UnprotectSecret(GetStr("apiKey", ""))
        ' 老版本把密钥明文写在 plugins.settings.json 里：读到明文就立刻改写成密文
        Dim StoredKey As String = GetStr("apiKey", "")
        If StoredKey.Length > 0 AndAlso Not StoredKey.StartsWith(SecretPrefix, StringComparison.Ordinal) Then
            Try
                Host.SetSetting("apiKey", ProtectSecret(ApiKey))
                Host.LogInfo("检测到明文密钥，已改为 Windows 加密存储")
            Catch ex As Exception
            End Try
        End If
        ModelName = GetStr("model", "deepseek-chat")
        CtxLimit = GetInt("contextLimit", 64000)
        WebSearchOn = GetBool("webSearch", True)
        ' 改了接口 / 模型 / 开关，就给被拒过的接口重新试一次的机会
        Dim Sig As String = If(BaseUrl, "") & "|" & If(ModelName, "") & "|" & WebSearchOn.ToString()
        If Not String.Equals(Sig, SearchSig, StringComparison.Ordinal) Then
            SearchSig = Sig
            ServerSearchBroken = False
        End If
        SessionCap = Math.Max(5, Math.Min(500, GetInt("maxSessions", 50)))
        CfKey = UnprotectSecret(GetStr("cfApiKey", ""))
        Dim StoredCf As String = GetStr("cfApiKey", "")
        If StoredCf.Length > 0 AndAlso Not StoredCf.StartsWith(SecretPrefix, StringComparison.Ordinal) Then
            Try
                Host.SetSetting("cfApiKey", ProtectSecret(CfKey))
            Catch ex As Exception
            End Try
        End If
        ' 提示词是随版本升级的：老设置里的旧 prompt 会盖掉新默认值，所以按 promptVer 强制刷新
        If GetInt("promptVer", 0) < PromptVer Then
            SysPrompt = DefaultSysPrompt()
            Try
                Host.SetSetting("systemPrompt", SysPrompt)
                Host.SetSetting("promptVer", PromptVer)
            Catch ex As Exception
            End Try
        Else
            SysPrompt = GetStr("systemPrompt", DefaultSysPrompt())
        End If
    End Sub

    Private Sub SaveSettings()
        Try
            Host.SetSetting("apiBase", BaseUrl)
            Host.SetSetting("apiKey", ProtectSecret(ApiKey))
            Host.SetSetting("model", ModelName)
            Host.SetSetting("cfApiKey", ProtectSecret(CfKey))
            Host.SetSetting("systemPrompt", SysPrompt)
            Host.SetSetting("contextLimit", CtxLimit)
            RebuildClient()
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("保存设置失败：" & Redact(ex.Message))
        End Try
    End Sub

    ' ==================== 密钥保护 ====================

    ''' <summary>密文前缀。带这个前缀的值是用 Windows DPAPI 加密过的。</summary>
    Private Const SecretPrefix As String = "dpapi:"

    ''' <summary>用一个固定串做口令，避免密钥位数太短时被 DPAPI 的熵检查拦下。</summary>
    Private Shared ReadOnly SecretEntropy As Byte() = Encoding.UTF8.GetBytes("PCLHarness.AiChat.v1")

    ''' <summary>加密成 "dpapi:Base64"，只能被当前 Windows 用户在同一台机器上解回来。</summary>
    Private Function ProtectSecret(Plain As String) As String
        If String.IsNullOrEmpty(Plain) Then Return ""
        If Plain.StartsWith(SecretPrefix, StringComparison.Ordinal) Then Return Plain
        Try
            Dim Raw As Byte() = Encoding.UTF8.GetBytes(Plain)
            Dim Enc As Byte() = Security.Cryptography.ProtectedData.Protect(
                Raw, SecretEntropy, Security.Cryptography.DataProtectionScope.CurrentUser)
            Return SecretPrefix & Convert.ToBase64String(Enc)
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("密钥加密失败，本次仅在内存中保留：" & ex.Message)
            Return ""
        End Try
    End Function

    ''' <summary>解密；不是密文（老版本的明文）原样返回，解密失败返回空串并提示重填。</summary>
    Private Function UnprotectSecret(Stored As String) As String
        If String.IsNullOrEmpty(Stored) Then Return ""
        If Not Stored.StartsWith(SecretPrefix, StringComparison.Ordinal) Then Return Stored
        Try
            Dim Enc As Byte() = Convert.FromBase64String(Stored.Substring(SecretPrefix.Length))
            Dim Raw As Byte() = Security.Cryptography.ProtectedData.Unprotect(
                Enc, SecretEntropy, Security.Cryptography.DataProtectionScope.CurrentUser)
            Return Encoding.UTF8.GetString(Raw)
        Catch ex As Exception
            If Host IsNot Nothing Then
                Host.LogWarn("密钥解密失败（换了 Windows 账户或换了机器？），请在「配置」里重新填写")
            End If
            Return ""
        End Try
    End Function

    ''' <summary>密钥的展示形式：sk-1234…abcd；太短就全是星号。</summary>
    Private Function MaskSecret(Value As String) As String
        If String.IsNullOrEmpty(Value) Then Return "（未配置）"
        If Value.Length <= 8 Then Return "********"
        Return Value.Substring(0, 4) & "…" & Value.Substring(Value.Length - 4)
    End Function

    ''' <summary>把文本里出现的当前密钥换成掩码——写日志、写历史、报错给用户看之前都要过一遍。</summary>
    Private Function Redact(Text As String) As String
        If String.IsNullOrEmpty(Text) Then Return Text
        If String.IsNullOrEmpty(ApiKey) OrElse ApiKey.Length < 6 Then Return Text
        Dim Out As String = Text.Replace(ApiKey, MaskSecret(ApiKey))
        ' 兜底：万一上游把整个 Authorization 头回显出来
        Dim Bearer As String = "Bearer " & ApiKey
        If Out.IndexOf(Bearer, StringComparison.OrdinalIgnoreCase) >= 0 Then
            Out = Out.Replace(Bearer, "Bearer " & MaskSecret(ApiKey))
        End If
        Return Out
    End Function

    ''' <summary>按当前端点重建 HttpClient：本地地址不走系统代理（避免代理把 127.0.0.1 劫走）。</summary>
    Private Sub RebuildClient()
        Try
            If Client IsNot Nothing Then Client.Dispose()
        Catch ex As Exception
        End Try
        Dim Handler As New HttpClientHandler()
        If IsLocalEndpoint() Then
            Handler.UseProxy = False
            Handler.Proxy = Nothing
        End If
        Client = New HttpClient(Handler)
        Client.Timeout = Timeout.InfiniteTimeSpan
    End Sub

    ''' <summary>把「连不上」的异常翻成人话，带上端点和该查什么。</summary>
    Private Function ConnectErrorText(Ex As Exception) As String
        Dim Ep As String = ApiEndpoint()
        Dim Inner As String = If(Ex.InnerException IsNot Nothing, Ex.InnerException.Message, Ex.Message)
        Dim Hint As String
        If IsLocalEndpoint() Then
            Hint = "本地服务没在监听：先启动 Ollama（托盘图标，或命令行 ollama serve），再用 ollama list 确认有 " & ModelName & "。 "
        Else
            Hint = "检查接口地址 / 网络 / 代理。 "
        End If
        Return $"连不上 {Ep}：{Inner}。{Hint}"
    End Function
    Private Function IsLocalEndpoint() As Boolean
        Dim B = If(BaseUrl, "").ToLowerInvariant()
        Return B.Contains("127.0.0.1") OrElse B.Contains("localhost")
    End Function

    Private Function ApiEndpoint() As String
        Dim B = If(BaseUrl, "").Trim().TrimEnd(Convert.ToChar("/"))
        If B.Length = 0 Then B = "https://api.deepseek.com"
        If B.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) Then Return B
        Return B & "/chat/completions"
    End Function

    ' ==================== 模型自带联网 ====================
    ' 各家在 OpenAI 兼容接口上开联网的方式完全不同，必须分开处理：
    '   GLM     ：tools 里加 {"type":"web_search","web_search":{"enable":true}}
    '   Kimi    ：tools 里加 {"type":"builtin_function","function":{"name":"$web_search"}}，
    '             模型返回 tool_calls 后客户端把 arguments 原样回传，检索由服务端完成
    '   Qwen    ：顶层参数 enable_search:true（不在 tools 里）
    '   DeepSeek：服务端工具 deepseek_web_search / deepseek_read_url 明确不允许和自定义 function 混用，
    '             而我们整套工具都是 function，所以只能走它独立的 Search 接口 → 本地 web_search 工具
    '   其它（OpenAI、中转站、Ollama）：不加任何东西，保持原样

    ''' <summary>按接口地址 / 模型名认这家自带哪种服务端联网能力。</summary>
    Private Function SearchProvider() As String
        ' 本地模型（Ollama / LM Studio）没有任何服务端联网能力，别把 qwen 那套参数发过去
        If IsLocalEndpoint() Then Return "none"
        Dim B = If(BaseUrl, "").Trim().ToLowerInvariant()
        Dim M = If(ModelName, "").Trim().ToLowerInvariant()
        If B.Contains("bigmodel.cn") OrElse B.Contains("open.bigmodel") OrElse B.Contains("api.z.ai") Then Return "glm"
        If B.Contains("moonshot") OrElse B.Contains("kimi.com") OrElse B.Contains("api.kimi") Then Return "kimi"
        If B.Contains("dashscope") OrElse B.Contains("aliyuncs") Then Return "qwen"
        If B.Contains("deepseek") Then Return "deepseek"
        ' 地址是企业网关 / 中转站时，靠模型名兜底
        If M.StartsWith("glm") OrElse M.StartsWith("chatglm") Then Return "glm"
        If M.StartsWith("kimi") OrElse M.StartsWith("moonshot") Then Return "kimi"
        If M.StartsWith("qwen") OrElse M.StartsWith("qwq") Then Return "qwen"
        If M.StartsWith("deepseek") Then Return "deepseek"
        Return "none"
    End Function

    ''' <summary>要追加进 tools 数组的服务端联网工具；Nothing 表示这家不适用。</summary>
    Private Function ServerSearchToolJson() As String
        If Not WebSearchOn OrElse ServerSearchBroken Then Return Nothing
        Select Case SearchProvider()
            Case "glm"
                Return "{""type"":""web_search"",""web_search"":{""enable"":true,""search_result"":true}}"
            Case "kimi"
                Return "{""type"":""builtin_function"",""function"":{""name"":""$web_search""}}"
            Case Else
                ' DeepSeek 的服务端工具不能与 function tools 共存，走本地 web_search 工具
                Return Nothing
        End Select
    End Function

    ''' <summary>顶层参数形式的联网开关（Qwen 的 enable_search，带前导逗号）。</summary>
    Private Function ServerSearchTopJson() As String
        If Not WebSearchOn OrElse ServerSearchBroken Then Return Nothing
        If SearchProvider() = "qwen" Then Return "," & JsonStr("enable_search") & ":true"
        Return Nothing
    End Function

    ''' <summary>DeepSeek 官方 Search 接口地址；非官方站点返回空串（中转站一般不转发这个路径）。</summary>
    Private Function SearchEndpoint() As String
        Dim B = If(BaseUrl, "").Trim().TrimEnd(Convert.ToChar("/"))
        If B.Length = 0 Then B = "https://api.deepseek.com"
        Dim L = B.ToLowerInvariant()
        If Not (L.Contains("api.deepseek.com") OrElse L.StartsWith("https://deepseek.com")) Then Return ""
        If L.EndsWith("/tools/search") Then Return B
        If L.EndsWith("/v1") Then Return B & "/tools/search"
        Return B & "/v1/tools/search"
    End Function

    ''' <summary>本地 web_search 工具：只在 DeepSeek 官方站给（靠服务商搜索接口，不是抓网页）。</summary>
    Private Function LocalSearchToolJson() As String
        If Not WebSearchOn Then Return Nothing
        If SearchProvider() <> "deepseek" OrElse SearchEndpoint().Length = 0 Then Return Nothing
        Return ToolDef2("web_search",
                        "联网搜索（DeepSeek 官方搜索接口，按次计费）。返回若干条结果的标题、链接、摘要和发布时间。" &
                        "问「最近 / 最新 / 现在怎么样」「有没有人做过」「某个模组最近更新了什么」这类训练数据覆盖不到的问题时用它；" &
                        "已知具体网址要看正文，用 fetch_url。",
                        "{""type"":""object"",""properties"":{" &
                        """query"":{""type"":""string"",""description"":""搜索词，1~128 字，可用 site: / after: / lang: 等语法""}," &
                        """count"":{""type"":""integer"",""description"":""返回条数，默认 6，最多 10""}},""required"":[""query""]}")
    End Function

    ' ==================== 多会话存储 ====================
    ' 一个会话一个文件：data\sessions\<id>.txt
    '   首行  #meta<TAB>b64(标题)
    '   之后  role<TAB>b64:正文      （正文可能含换行/制表符，base64 保证一行装下）
    ' 目录本身就是索引：标题在文件首行，时间取文件修改时间，条数数非空行。

    Private Function SessionsDir() As String
        If Host Is Nothing Then Return Nothing
        Return Path.Combine(Host.DataFolder, "sessions")
    End Function

    ''' <summary>会话 id 只允许字母数字和短横线，杜绝路径穿越。</summary>
    Private Function SafeId(Sid As String) As Boolean
        If String.IsNullOrWhiteSpace(Sid) Then Return False
        If Sid.Length > 40 Then Return False
        For Each C In Sid
            If Not (Char.IsLetterOrDigit(C) OrElse C = Convert.ToChar(45)) Then Return False
        Next
        Return True
    End Function

    Private Function SessionPath(Sid As String) As String
        Dim D = SessionsDir()
        If D Is Nothing OrElse Not SafeId(Sid) Then Return Nothing
        Return Path.Combine(D, Sid & ".txt")
    End Function

    Private Function NewSessionId() As String
        Dim R As New Random()
        Return Date.Now.ToString("yyyyMMdd-HHmmss") & "-" & R.Next(4096, 65536).ToString("x")
    End Function

    Private Function EncodeBody(B As String) As String
        Return "b64:" & Convert.ToBase64String(Encoding.UTF8.GetBytes(If(B, "")))
    End Function

    ''' <summary>新格式是 b64: 前缀；老格式（\n 转义）还能读，读不了就返回 Nothing 丢掉残片。</summary>
    Private Function DecodeBody(Raw As String) As String
        If Raw.StartsWith("b64:") Then
            Try
                Return Encoding.UTF8.GetString(Convert.FromBase64String(Raw.Substring(4)))
            Catch ex As Exception
                Return Nothing
            End Try
        End If
        Return Raw.Replace("\n", Environment.NewLine)
    End Function

    Private Function DecodePlain(B64 As String) As String
        Try
            Return Encoding.UTF8.GetString(Convert.FromBase64String(B64))
        Catch ex As Exception
            Return ""
        End Try
    End Function

    Private Const MetaPrefix As String = "#meta"

    ''' <summary>标题统一清洗：去掉换行、截断到 40 字。</summary>
    Private Function CleanTitle(T As String) As String
        Dim S As String = If(T, "").Replace(Convert.ToChar(13), Convert.ToChar(32)).Replace(Convert.ToChar(10), Convert.ToChar(32)).Trim()
        While S.Contains("  ")
            S = S.Replace("  ", " ")
        End While
        If S.Length > 40 Then S = S.Substring(0, 40) & "…"
        Return S
    End Function

    ''' <summary>扫一遍会话目录，填 Sessions（按修改时间倒序）。</summary>
    Private Sub RefreshSessionList()
        Sessions.Clear()
        Dim D = SessionsDir()
        If D Is Nothing OrElse Not Directory.Exists(D) Then Return
        Try
            For Each F In Directory.GetFiles(D, "*.txt")
                Dim Sid As String = Path.GetFileNameWithoutExtension(F)
                If Not SafeId(Sid) Then Continue For
                Dim Meta As New ChatSession With {.Id = Sid, .Title = "（无标题）"}
                Try
                    Meta.UpdatedAt = File.GetLastWriteTime(F)
                    Dim Lines = File.ReadAllLines(F, Encoding.UTF8)
                    If Lines.Length > 0 AndAlso Lines(0).StartsWith(MetaPrefix & Convert.ToChar(9), StringComparison.Ordinal) Then
                        Dim T = DecodePlain(Lines(0).Substring(MetaPrefix.Length + 1))
                        If Not String.IsNullOrWhiteSpace(T) Then Meta.Title = T
                    End If
                    Dim N As Integer = 0
                    For Each Line In Lines
                        If String.IsNullOrWhiteSpace(Line) Then Continue For
                        If Line.StartsWith(MetaPrefix & Convert.ToChar(9), StringComparison.Ordinal) Then Continue For
                        N += 1
                    Next
                    Meta.Count = N
                Catch ex As Exception
                End Try
                Sessions.Add(Meta)
            Next
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("扫描会话目录失败：" & ex.Message)
        End Try
        Sessions.Sort(New Comparison(Of ChatSession)(Function(A, B) B.UpdatedAt.CompareTo(A.UpdatedAt)))
    End Sub

    ''' <summary>读一个会话进 Messages。</summary>
    Private Sub LoadSession(Sid As String)
        Messages.Clear()
        SessionId = If(SafeId(Sid), Sid, "")
        SessionTitle = "新对话"
        Dim P = SessionPath(Sid)
        If P Is Nothing OrElse Not File.Exists(P) Then Return
        Try
            For Each Line In File.ReadAllLines(P, Encoding.UTF8)
                If String.IsNullOrWhiteSpace(Line) Then Continue For
                If Line.StartsWith(MetaPrefix & Convert.ToChar(9), StringComparison.Ordinal) Then
                    Dim T = DecodePlain(Line.Substring(MetaPrefix.Length + 1))
                    If Not String.IsNullOrWhiteSpace(T) Then SessionTitle = T
                    Continue For
                End If
                Dim Tab As Integer = Line.IndexOf(Convert.ToChar(9))
                If Tab <= 0 Then Continue For
                Dim Role As String = Line.Substring(0, Tab)
                Dim Body As String = DecodeBody(Line.Substring(Tab + 1))
                If Body Is Nothing Then Continue For
                ' 旧格式把代码块拆成多行，会留下只有围栏的残片（如「```python」），丢掉。
                Dim Trimmed As String = Body.Trim()
                If Trimmed.StartsWith(Fence) AndAlso Trimmed.IndexOf(Fence, 3) < 0 Then Continue For
                Messages.Add(New ChatMessage With {.Role = Role, .Content = Body})
            Next
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("读取会话失败：" & ex.Message)
        End Try
    End Sub

    ''' <summary>把当前会话写回磁盘。</summary>
    Private Sub SaveSession()
        If Host Is Nothing Then Return
        If Not SafeId(SessionId) Then Return
        Try
            Dim D = SessionsDir()
            If Not Directory.Exists(D) Then Directory.CreateDirectory(D)
            Dim Sb As New StringBuilder()
            Sb.Append(MetaPrefix).Append(Convert.ToChar(9))
            Sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(If(SessionTitle, "新对话"))))
            Sb.Append(Environment.NewLine)
            For Each M In Messages
                If String.IsNullOrWhiteSpace(M.Content) Then Continue For
                Sb.Append(M.Role).Append(Convert.ToChar(9))
                Sb.Append(EncodeBody(Redact(M.Content)))
                Sb.Append(Environment.NewLine)
            Next
            File.WriteAllText(SessionPath(SessionId), Sb.ToString(), New UTF8Encoding(False))
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("保存会话失败：" & ex.Message)
        End Try
    End Sub

    ''' <summary>新建会话；当前会话还是空的就复用，避免留一堆空文件。</summary>
    Private Sub NewSession()
        SaveDraftNow()
        If SafeId(SessionId) AndAlso Messages.Count = 0 Then
            SessionTitle = "新对话"
        Else
            SaveSession()
            ' 会话数量上限：先腾位置再新建，否则会话文件会一直堆下去
            Dim Removed As List(Of String) = MakeSessionRoom(SessionId, True)
            SessionId = NewSessionId()
            SessionTitle = "新对话"
            Messages.Clear()
            If Removed.Count > 0 AndAlso Host IsNot Nothing Then
                Host.Hint("会话最多 " & SessionCap & " 个，已清理最早的 " & Removed.Count & " 个：" &
                          Shorten(String.Join("、", Removed.ToArray()), 20))
            End If
        End If
        SaveSession()
        RenameMode = False
        Try
            Host.SetSetting("lastSession", SessionId)
        Catch ex As Exception
        End Try
        RefreshSessionList()
        RenderAll()
        RenderSessions()
        ApplyDraft()
        UpdateStatus()
    End Sub

    Private Sub SwitchSession(Sid As String)
        If Not SafeId(Sid) Then Return
        If String.Equals(Sid, SessionId, StringComparison.OrdinalIgnoreCase) Then Return
        If Busy Then
            Host.Hint("正在回答，先点「停止」再切换会话")
            Return
        End If
        SaveDraftNow()
        SaveSession()
        LoadSession(Sid)
        RenameMode = False
        Try
            Host.SetSetting("lastSession", SessionId)
        Catch ex As Exception
        End Try
        RefreshSessionList()
        RenderAll()
        RenderSessions()
        ApplyDraft()
        UpdateStatus()
        If Host IsNot Nothing Then Host.LogInfo("切换会话：" & SessionTitle & "（" & Messages.Count & " 条）")
    End Sub

    Private Sub DeleteSession(Sid As String)
        If Not SafeId(Sid) Then Return
        If String.Equals(Sid, SessionId, StringComparison.OrdinalIgnoreCase) AndAlso Busy Then
            Host.Hint("正在回答，先点「停止」再删除会话")
            Return
        End If
        SaveDraftNow()
        Dim Name As String = Sid
        For Each S In Sessions
            If String.Equals(S.Id, Sid, StringComparison.OrdinalIgnoreCase) Then Name = S.Title : Exit For
        Next
        Dim SidPath = SessionPath(Sid)
        If SidPath IsNot Nothing AndAlso File.Exists(SidPath) Then
            If Not RemoveSessionFile(Sid) Then
                Host.Hint("删除会话失败，看日志")
                Return
            End If
        Else
            ' 文件本来就不在了，只把残留的草稿清掉
            DropDraft(Sid)
        End If
        If String.Equals(Sid, SessionId, StringComparison.OrdinalIgnoreCase) Then
            SessionId = ""
            Messages.Clear()
            RefreshSessionList()
            If Sessions.Count > 0 Then
                LoadSession(Sessions(0).Id)
            Else
                SessionId = NewSessionId()
                SessionTitle = "新对话"
                SaveSession()
            End If
            Try
                Host.SetSetting("lastSession", SessionId)
            Catch ex As Exception
            End Try
        End If
        RefreshSessionList()
        RenderAll()
        RenderSessions()
        ApplyDraft()
        UpdateStatus()
        If Host IsNot Nothing Then Host.Hint("已删除会话「" & Name & "」")
    End Sub

    Private Sub RenameSession(Sid As String, Title As String)
        Dim T As String = CleanTitle(Title)
        If T.Length = 0 OrElse Not SafeId(Sid) Then Return
        If String.Equals(Sid, SessionId, StringComparison.OrdinalIgnoreCase) Then
            SessionTitle = T
            SaveSession()
        Else
            Try
                Dim P = SessionPath(Sid)
                If P Is Nothing OrElse Not File.Exists(P) Then Return
                Dim Lines As New List(Of String)(File.ReadAllLines(P, Encoding.UTF8))
                Dim Head As String = MetaPrefix & Convert.ToChar(9) & Convert.ToBase64String(Encoding.UTF8.GetBytes(T))
                If Lines.Count > 0 AndAlso Lines(0).StartsWith(MetaPrefix & Convert.ToChar(9), StringComparison.Ordinal) Then
                    Lines(0) = Head
                Else
                    Lines.Insert(0, Head)
                End If
                File.WriteAllLines(P, Lines.ToArray(), New UTF8Encoding(False))
            Catch ex As Exception
                Host.Hint("改名失败：" & ex.Message)
                Return
            End Try
        End If
        RefreshSessionList()
        RenderSessions()
        UpdateStatus()
    End Sub

    ''' <summary>会话第一句话决定标题（只起一次）。</summary>
    Private Sub MaybeAutoTitle(Text As String)
        If Messages.Count <> 1 Then Return
        If SessionTitle <> "新对话" AndAlso SessionTitle.Length > 0 Then Return
        SessionTitle = CleanTitle(Text)
        If SessionTitle.Length > 18 Then SessionTitle = SessionTitle.Substring(0, 18) & "…"
    End Sub

    ''' <summary>把旧版 aichat 的 data\chat.txt 导成一个会话（只导一次）。</summary>
    Private Sub MigrateLegacy()
        Try
            Dim Old As String = Path.Combine(Host.DataFolder, "chat.txt")
            If Not File.Exists(Old) Then Return
            If Sessions.Count > 0 Then Return
            Dim D = SessionsDir()
            If Not Directory.Exists(D) Then Directory.CreateDirectory(D)
            Dim Sb As New StringBuilder()
            Sb.Append(MetaPrefix).Append(Convert.ToChar(9))
            Sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes("旧会话（导入）"))).Append(Environment.NewLine)
            For Each Line In File.ReadAllLines(Old, Encoding.UTF8)
                If String.IsNullOrWhiteSpace(Line) Then Continue For
                Sb.Append(Line).Append(Environment.NewLine)
            Next
            Dim Sid As String = NewSessionId()
            File.WriteAllText(Path.Combine(D, Sid & ".txt"), Sb.ToString(), New UTF8Encoding(False))
            SessionId = Sid
            If Host IsNot Nothing Then Host.LogInfo("已把旧版 chat.txt 导入为一个会话")
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("导入旧聊天记录失败：" & ex.Message)
        End Try
    End Sub

    ''' <summary>启动时决定用哪个会话：优先上次的，其次最近一个，都没有就新建。</summary>
    Private Sub InitSessions()
        Dim D = SessionsDir()
        If D Is Nothing Then Return
        Try
            If Not Directory.Exists(D) Then Directory.CreateDirectory(D)
        Catch ex As Exception
        End Try
        RefreshSessionList()
        MigrateLegacy()
        RefreshSessionList()

        Dim Want As String = GetStr("lastSession", "")
        Dim Hit As ChatSession = Nothing
        If SafeId(Want) Then
            For Each S In Sessions
                If String.Equals(S.Id, Want, StringComparison.OrdinalIgnoreCase) Then Hit = S : Exit For
            Next
        End If
        If Hit Is Nothing AndAlso Sessions.Count > 0 Then Hit = Sessions(0)
        If Hit IsNot Nothing Then
            LoadSession(Hit.Id)
        Else
            SessionId = NewSessionId()
            SessionTitle = "新对话"
            SaveSession()
        End If
        Try
            Host.SetSetting("lastSession", SessionId)
        Catch ex As Exception
        End Try
        ' 升级或调低上限后，老数据可能已经超了：启动时补压一次（保留当前会话）
        If Sessions.Count > SessionCap Then
            Dim Removed As List(Of String) = MakeSessionRoom(SessionId, False)
            If Removed.Count > 0 AndAlso Host IsNot Nothing Then
                Host.LogInfo("会话超过上限 " & SessionCap & "，已清理 " & Removed.Count & " 个")
            End If
        End If
        RefreshSessionList()
    End Sub

    ''' <summary>导出当前会话为 Markdown，落在 data\exports\。</summary>
    Private Function ExportSession() As String
        If Host Is Nothing Then Return "插件未就绪。"
        SaveSession()
        Try
            Dim Dir As String = Path.Combine(Host.DataFolder, "exports")
            If Not Directory.Exists(Dir) Then Directory.CreateDirectory(Dir)
            Dim Sb As New StringBuilder()
            Sb.Append("# ").Append(If(SessionTitle, "会话")).Append(Environment.NewLine).Append(Environment.NewLine)
            For Each M In Messages
                If String.IsNullOrWhiteSpace(M.Content) Then Continue For
                Sb.Append("## ").Append(If(M.Role = "user", "我", "AI")).Append(Environment.NewLine).Append(Environment.NewLine)
                Sb.Append(M.Content).Append(Environment.NewLine).Append(Environment.NewLine)
            Next
            Dim Name As String = SessionId & ".md"
            File.WriteAllText(Path.Combine(Dir, Name), Sb.ToString(), New UTF8Encoding(False))
            Return "已导出到 data\exports\" & Name
        Catch ex As Exception
            Return "导出失败：" & ex.Message
        End Try
    End Function

    ' ==================== 会话数量上限 ====================
    ' 会话文件是「一条消息一行」的纯文本，聊得多了会一直堆：这里给个上限，超出时自动减负，
    ' 顺序是「没草稿的优先 → 空会话优先 → 最久没动的先删」，尽量别清掉还能接着聊的内容。

    ''' <summary>某个会话是否存着还没发出去的草稿（清理时优先避开）。</summary>
    Private Function HasDraft(Sid As String) As Boolean
        If Not SafeId(Sid) Then Return False
        Dim T As String = ""
        If DraftCache.TryGetValue(Sid, T) Then Return Not String.IsNullOrWhiteSpace(T)
        Try
            Dim P = DraftPath(Sid)
            Return P IsNot Nothing AndAlso File.Exists(P)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ''' <summary>删掉一个会话文件（连带它的草稿）；失败只记日志，返回是否成功。</summary>
    Private Function RemoveSessionFile(Sid As String) As Boolean
        If Not SafeId(Sid) Then Return False
        Dim P = SessionPath(Sid)
        If P Is Nothing Then Return False
        Try
            If File.Exists(P) Then File.Delete(P)
            DropDraft(Sid)
            Return True
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("删除会话失败（" & Sid & "）：" & ex.Message)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 给新会话腾位置并返回被删掉的标题。LeaveSlot=True 时多留一个位置（马上要新建），
    ''' False 时只压到上限（启动时补一次清理用）。
    ''' </summary>
    Private Function MakeSessionRoom(KeepId As String, LeaveSlot As Boolean) As List(Of String)
        Dim Removed As New List(Of String)
        RefreshSessionList()
        If Sessions.Count < SessionCap Then Return Removed

        Dim Pool As New List(Of ChatSession)
        For Each S In Sessions
            If String.Equals(S.Id, KeepId, StringComparison.OrdinalIgnoreCase) Then Continue For
            Pool.Add(S)
        Next
        If Pool.Count = 0 Then Return Removed

        Pool.Sort(New Comparison(Of ChatSession)(
            Function(A, B)
                Dim DA As Boolean = HasDraft(A.Id), DB As Boolean = HasDraft(B.Id)
                If DA <> DB Then Return If(DA, 1, -1)
                Dim EA As Boolean = (A.Count = 0), EB As Boolean = (B.Count = 0)
                If EA <> EB Then Return If(EA, -1, 1)
                Return A.UpdatedAt.CompareTo(B.UpdatedAt)
            End Function))

        Dim Need As Integer = Sessions.Count - SessionCap + If(LeaveSlot, 1, 0)
        For Each S In Pool
            If Need <= 0 Then Exit For
            If RemoveSessionFile(S.Id) Then
                Removed.Add(If(String.IsNullOrWhiteSpace(S.Title), S.Id, S.Title))
                Need -= 1
            End If
        Next
        RefreshSessionList()
        Return Removed
    End Function

    ''' <summary>手动减负：清掉所有空会话（当前会话、带草稿的都不动）。</summary>
    Private Sub CleanEmptySessions()
        If Host Is Nothing Then Return
        SaveDraftNow()
        RefreshSessionList()
        Dim N As Integer = 0
        For Each S In Sessions
            If String.Equals(S.Id, SessionId, StringComparison.OrdinalIgnoreCase) Then Continue For
            If S.Count > 0 OrElse HasDraft(S.Id) Then Continue For
            If RemoveSessionFile(S.Id) Then N += 1
        Next
        RefreshSessionList()
        RenderSessions()
        UpdateStatus()
        Host.Hint(If(N > 0, "已清理 " & N & " 个空会话", "没有可清理的空会话"))
    End Sub

    ' ==================== Agent 约定（agent.md） ====================

    ''' <summary>agent.md 参与提示词的字符上限，防止一份大文件把上下文一次吃光。</summary>
    Private Const AgentDocMax As Integer = 12000
    ''' <summary>单条约定的字符上限。</summary>
    Private Const AgentLineMax As Integer = 300
    Private AgentDocCache As String = ""
    Private AgentStamp As String = ""

    Private Function AgentDocPath() As String
        If Host Is Nothing Then Return Nothing
        Return Path.Combine(Host.DataFolder, "agent.md")
    End Function

    ''' <summary>第一次用的时候给一份带说明的骨架，直接编辑就能用。</summary>
    Private Function AgentTemplate() As String
        Return "<!--" & vbCrLf &
               "  这个文件会在每次对话时原样追加到系统提示词后面，用来放「我这边自己的规矩」。" & vbCrLf &
               "  改完保存就生效，不用重启启动器；插件升级不会覆盖它。" & vbCrLf &
               "  删掉整个文件 = 没有额外约定（内置规则仍然有效）。" & vbCrLf &
               "  想快速加一条也可以在对话框里说 /agent <一句话>，或者让 AI 用 remember_convention 记。" & vbCrLf &
               "-->" & vbCrLf &
               vbCrLf &
               "# Agent 约定" & vbCrLf &
               vbCrLf &
               "- 回答用中文，直接给结论，别客套。" & vbCrLf &
               "- 装模组 / 光影之前先把候选念给我确认，别自己替我挑。" & vbCrLf &
               "- 动版本之前先说清当前选中的是哪个实例。" & vbCrLf &
               "- 装游戏本体 / 加载器 / 整合包时，必须等它真的装完、切到那个实例之后，再下载 mod；不许边装边下。" & vbCrLf &
               "- 我问模组、物品、方块、玩法这类游戏内容时，先去 MC百科（mcmod.cn）查，别凭记忆答。" & vbCrLf
    End Function

    Private Sub EnsureAgentFile(P As String)
        Try
            If File.Exists(P) Then Return
            Dim D = Path.GetDirectoryName(P)
            If Not String.IsNullOrEmpty(D) AndAlso Not Directory.Exists(D) Then Directory.CreateDirectory(D)
            File.WriteAllText(P, AgentTemplate(), New UTF8Encoding(False))
            If Host IsNot Nothing Then Host.LogInfo("已生成 agent.md 模板：" & P)
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("生成 agent.md 失败：" & ex.Message)
        End Try
    End Sub

    ''' <summary>读当前约定；按修改时间 + 大小做缓存，编辑保存后下一轮自动生效。</summary>
    Private Function AgentDoc() As String
        Dim P = AgentDocPath()
        If P Is Nothing Then Return ""
        Try
            If Not File.Exists(P) Then
                EnsureAgentFile(P)
                If Not File.Exists(P) Then Return ""
            End If
            Dim Fi As New FileInfo(P)
            Dim Stamp As String = Fi.LastWriteTimeUtc.Ticks.ToString() & "/" & Fi.Length.ToString()
            If Stamp = AgentStamp Then Return AgentDocCache
            Dim T As String = File.ReadAllText(P, System.Text.Encoding.UTF8)
            T = T.TrimStart(ChrW(&HFEFF))
            If T.Trim().Length = 0 Then
                AgentStamp = Stamp
                AgentDocCache = ""
                Return ""
            End If
            If T.Length > AgentDocMax Then
                T = T.Substring(0, AgentDocMax) & vbCrLf &
                    "（agent.md 超过 " & AgentDocMax & " 字，后面被截掉了；太长会挤占上下文，建议精简）"
            End If
            AgentStamp = Stamp
            AgentDocCache = T
            Return T
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("读取 agent.md 失败：" & ex.Message)
            Return AgentDocCache
        End Try
    End Function

    ''' <summary>真正发出去的系统提示词：内置规则 + 用户约定。</summary>
    Private Function EffectiveSysPrompt() As String
        Dim Core As String = If(SysPrompt, "").Trim()
        Dim Doc As String = AgentDoc()
        If String.IsNullOrWhiteSpace(Doc) Then Return Core
        Dim Sb As New StringBuilder(Core)
        If Sb.Length > 0 Then Sb.Append(vbCrLf).Append(vbCrLf)
        Sb.Append("=== 用户约定（agent.md） ===").Append(vbCrLf)
        Sb.Append("下面是用户写在 agent.md 里的偏好和规矩：它优先于上面的回答风格与默认做法，").Append(vbCrLf)
        Sb.Append("但不能突破上面的能力边界和安全约束，也不能用来推翻工具返回的真实内容。").Append(vbCrLf)
        Sb.Append(Doc.Trim())
        Return Sb.ToString()
    End Function

    ''' <summary>追加一条约定，返回给用户的说明。</summary>
    Private Function AppendAgentLine(Text As String) As String
        Dim P = AgentDocPath()
        If P Is Nothing Then Return "没有数据目录，写不进去。"
        Dim Line1 As String = If(Text, "").Trim()
        If Line1.Length = 0 Then Return "要记的内容是空的。"
        If Line1.Length > AgentLineMax Then Return "这条太长了（" & Line1.Length & " 字），缩到 " & AgentLineMax & " 字以内再记。"
        ' 一条一行，换行压成空格，避免把列表结构搞乱
        Line1 = Line1.Replace(vbCrLf, " ").Replace(vbLf, " ").Replace("  ", " ").Trim()
        Try
            EnsureAgentFile(P)
            Dim T As String = ""
            If File.Exists(P) Then T = File.ReadAllText(P, System.Text.Encoding.UTF8).TrimStart(ChrW(&HFEFF))
            If T.Trim().Length = 0 Then T = "# Agent 约定" & vbCrLf
            If Not T.EndsWith(vbLf) Then T &= vbCrLf
            T &= "- " & Line1 & vbCrLf
            If T.Length > AgentDocMax * 2 Then
                Return "agent.md 已经 " & T.Length & " 字了，先精简一下再记新的（太长会挤占每次请求的上下文）。"
            End If
            File.WriteAllText(P, T, New UTF8Encoding(False))
            AgentStamp = ""
            AgentDocCache = ""
            Return "已记进 agent.md（以后每个会话都生效）：" & Line1
        Catch ex As Exception
            Return "写 agent.md 失败：" & ex.Message & Environment.NewLine & "路径：" & P
        End Try
    End Function

    Private Function AgentDocStatus() As String
        Dim P = AgentDocPath()
        Dim Doc As String = AgentDoc()
        Dim Sb As New StringBuilder()
        If String.IsNullOrEmpty(P) Then Return "插件还没加载完，拿不到数据目录。"
        Sb.Append("agent.md：").Append(P).Append(vbCrLf)
        If String.IsNullOrWhiteSpace(Doc) Then
            Sb.Append("现在是空的，没有额外约定（内置规则照旧生效）。直接编辑这个文件，或者 /agent <一句话> 追加一条。")
            Return Sb.ToString()
        End If
        Sb.Append("已生效 ").Append(Doc.Length).Append(" 字，每次请求都会跟在系统提示词后面。").Append(vbCrLf)
        Sb.Append("----").Append(vbCrLf)
        Sb.Append(If(Doc.Length > 600, Doc.Substring(0, 600) & Environment.NewLine & "……（后面省略）", Doc)).Append(vbCrLf)
        Sb.Append("----").Append(vbCrLf)
        Sb.Append("改完保存立即生效，不用重启；/agent <一句话> 追加，/agent reset 重建模板。")
        Return Sb.ToString()
    End Function

    Private Function ResetAgentDoc() As String
        Dim P = AgentDocPath()
        If P Is Nothing Then Return "没有数据目录。"
        Try
            File.WriteAllText(P, AgentTemplate(), New UTF8Encoding(False))
            AgentStamp = ""
            AgentDocCache = ""
            Return "agent.md 已重置成模板：" & P
        Catch ex As Exception
            Return "重置失败：" & ex.Message
        End Try
    End Function

    ' ==================== 输入草稿 ====================

    Private Function DraftsDir() As String
        If Host Is Nothing Then Return Nothing
        Return Path.Combine(Host.DataFolder, "drafts")
    End Function

    ''' <summary>当前会话的草稿键；还没分配会话时用占位键，不至于丢字。</summary>
    Private Function DraftKey() As String
        Dim K As String = If(SessionId, "").Trim()
        If Not SafeId(K) Then K = "pending"
        Return K
    End Function

    Private Function DraftPath(K As String) As String
        If Not SafeId(K) Then Return Nothing
        Dim D = DraftsDir()
        If D Is Nothing Then Return Nothing
        Return Path.Combine(D, K & ".txt")
    End Function

    ''' <summary>取草稿：先内存，再盘上。</summary>
    Private Function GetDraft(K As String) As String
        Dim T As String = Nothing
        If DraftCache.TryGetValue(K, T) Then Return If(T, "")
        T = ""
        Try
            Dim P = DraftPath(K)
            If P IsNot Nothing AndAlso File.Exists(P) Then T = File.ReadAllText(P, Encoding.UTF8)
        Catch ex As Exception
        End Try
        DraftCache(K) = T
        Return T
    End Function

    ''' <summary>写 / 删一个草稿文件，都是尽力而为，失败只记日志。</summary>
    Private Sub WriteDraft(K As String, T As String)
        Dim P = DraftPath(K)
        If P Is Nothing Then Return
        Try
            If String.IsNullOrEmpty(T) Then
                If File.Exists(P) Then File.Delete(P)
                Return
            End If
            Dim D = Path.GetDirectoryName(P)
            If Not String.IsNullOrEmpty(D) AndAlso Not Directory.Exists(D) Then Directory.CreateDirectory(D)
            File.WriteAllText(P, T, New UTF8Encoding(False))
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("保存草稿失败：" & ex.Message)
        End Try
    End Sub

    Private Sub EnsureDraftTimer()
        If DraftTimer IsNot Nothing Then Return
        Try
            DraftTimer = New System.Windows.Threading.DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(700)}
            AddHandler DraftTimer.Tick, Sub(sender, e) FlushDrafts()
        Catch ex As Exception
        End Try
    End Sub

    ''' <summary>把攒下的脏草稿落盘（每个键都写文件太浪费，停手 0.7 秒再写）。</summary>
    Private Sub FlushDrafts()
        If DraftTimer IsNot Nothing Then DraftTimer.Stop()
        If DraftDirty.Count = 0 Then Return
        For Each K In DraftDirty.ToArray()
            Dim T As String = ""
            If DraftCache.TryGetValue(K, T) Then WriteDraft(K, T)
            DraftDirty.Remove(K)
        Next
    End Sub

    ''' <summary>输入框内容变化：先记内存，稍后落盘。</summary>
    Private Sub DraftChanged()
        If SuppressDraft OrElse InputBox Is Nothing Then Return
        Dim K As String = DraftKey()
        DraftCache(K) = If(InputBox.Text, "")
        DraftCaret(K) = InputBox.CaretIndex
        DraftDirty.Add(K)
        EnsureDraftTimer()
        If DraftTimer IsNot Nothing Then
            DraftTimer.Stop()
            DraftTimer.Start()
        End If
    End Sub

    ''' <summary>立刻把输入框里的内容存成草稿（切页、切会话、重载前调）。</summary>
    Private Sub SaveDraftNow()
        If SuppressDraft OrElse InputBox Is Nothing Then Return
        Dim K As String = DraftKey()
        DraftCache(K) = If(InputBox.Text, "")
        DraftCaret(K) = InputBox.CaretIndex
        DraftDirty.Add(K)
        FlushDrafts()
    End Sub

    ''' <summary>把当前会话的草稿铺回输入框（卡片刚建好 / 刚切完会话时调）。</summary>
    Private Sub ApplyDraft()
        If InputBox Is Nothing Then Return
        Dim K As String = DraftKey()
        Dim T As String = GetDraft(K)
        SuppressDraft = True
        Try
            InputBox.Text = T
            Dim C As Integer = T.Length
            Dim S As Integer = 0
            If DraftCaret.TryGetValue(K, S) Then C = Math.Max(0, Math.Min(S, T.Length))
            Try
                InputBox.CaretIndex = C
                InputBox.ScrollToEnd()
            Catch ex As Exception
            End Try
        Finally
            SuppressDraft = False
        End Try
    End Sub

    ''' <summary>会话被删掉时把它的草稿一起清掉，别留孤儿文件。</summary>
    Private Sub DropDraft(Sid As String)
        If Not SafeId(Sid) Then Return
        DraftCache.Remove(Sid)
        DraftCaret.Remove(Sid)
        DraftDirty.Remove(Sid)
        WriteDraft(Sid, "")
    End Sub

    ''' <summary>改名结束 / 取消：把被标题顶掉的那份草稿还回输入框。</summary>
    Private Sub RestoreRenameDraft()
        Dim T As String = If(RenameDraft, "")
        Dim C As Integer = RenameCaret
        RenameDraft = Nothing
        If InputBox Is Nothing Then Return
        SuppressDraft = True
        Try
            InputBox.Text = T
            Try
                InputBox.CaretIndex = Math.Max(0, Math.Min(C, T.Length))
            Catch ex As Exception
            End Try
        Finally
            SuppressDraft = False
        End Try
    End Sub

    ' ==================== 接口实现 ====================

    Public Function GetPages() As IEnumerable(Of PCL.PluginPageInfo) Implements PCL.IPclPluginUi.GetPages
        Return Nothing
    End Function

    Public Function GetNavItems() As IEnumerable(Of PCL.PluginNavItem) Implements PCL.IPclPluginUi.GetNavItems
        Return Nothing
    End Function

    Public Function GetSettings() As IEnumerable(Of PCL.PluginSetting) Implements PCL.IPclPluginUi.GetSettings
        Return New List(Of PCL.PluginSetting) From {
            New PCL.PluginSetting With {
                .Key = "apiBase",
                .Title = "接口地址",
                .Description = "OpenAI 兼容的 base，例如 https://api.deepseek.com、https://api.openai.com/v1，" &
                               "或本地 Ollama 的 http://127.0.0.1:11434/v1（本地不需要密钥）。",
                .SettingType = PCL.PluginSettingType.Text,
                .DefaultValue = "https://api.deepseek.com"
            },
            New PCL.PluginSetting With {
                .Key = "model",
                .Title = "模型",
                .Description = "例如 deepseek-chat、gpt-4o-mini、qwen2.5。",
                .SettingType = PCL.PluginSettingType.Text,
                .DefaultValue = "deepseek-chat"
            },
            New PCL.PluginSetting With {
                .Key = "systemPrompt",
                .Title = "预设提示词",
                .Description = "内置 system 消息：工具用法、能力边界、回答风格都在这里，插件升级时会被新版覆盖。" &
                               "想让 AI 长期记住你的习惯，改 data 目录下的 agent.md 更合适（升级不覆盖、保存即生效，对话里发 /agent 可查看或追加）。",
                .SettingType = PCL.PluginSettingType.Text,
                .DefaultValue = "你是 PCL Harness 启动器里的助手。回答简洁、直接，用中文；不确定就直说不确定，不要编。"
            },
            New PCL.PluginSetting With {
                .Key = "contextLimit",
                .Title = "上下文上限（token）",
                .Description = "只用来画用量条。deepseek-chat 是 64000，Ollama 的 qwen2.5 一般是 32000。",
                .SettingType = PCL.PluginSettingType.Number,
                .DefaultValue = 64000,
                .MinValue = 1000,
                .MaxValue = 1000000
            },
            New PCL.PluginSetting With {
                .Key = "maxSessions",
                .Title = "会话数量上限",
                .Description = "最多保留多少个对话，默认 50。超出时新建会话会先清空会话、再清最久没动过的；" &
                               "带草稿的会话最后才动，也可以在「会话」面板点「清理空会话」自己减负。",
                .SettingType = PCL.PluginSettingType.Number,
                .DefaultValue = 50,
                .MinValue = 5,
                .MaxValue = 500
            },
            New PCL.PluginSetting With {
                .Key = "webSearch",
                .Title = "模型自带联网搜索",
                .Description = "开启后，按接口地址自动选用服务商自己的联网能力：GLM 用 web_search、Kimi 用 $web_search、" &
                               "DeepSeek 用官方 Search 接口（5 元 / 千次，和模型共用同一个 Key）、Qwen 用 enable_search。" &
                               "搜索结果由服务商在服务端检索，不经过本插件；接口不认这个参数时会自动降级成普通模式。",
                .SettingType = PCL.PluginSettingType.Boolean,
                .DefaultValue = True
            }
        }
    End Function

    Public Function GetHomeCards() As IEnumerable(Of PCL.PluginCardInfo) Implements PCL.IPclPluginUi2.GetHomeCards
        Dim Card As New PCL.PluginCardInfo With {
            .Id = "aiplus.home.card",
            .Title = "AI 助手 Pro",
            .Description = "多会话 · 下载内容 · 管实例",
            .Factory = Function() BuildCard()
        }
        Return New List(Of PCL.PluginCardInfo) From {Card}
    End Function

    Public Function GetSettingsCards() As IEnumerable(Of PCL.PluginCardInfo) Implements PCL.IPclPluginUi2.GetSettingsCards
        Return Nothing
    End Function

    Public Function GetStatusWidgets() As IEnumerable(Of PCL.PluginStatusWidget) Implements PCL.IPclPluginUi2.GetStatusWidgets
        ' 3.6.4 起宿主不再渲染状态栏组件
        Return Nothing
    End Function

    ' ==================== 卡片 UI ====================

    Private Function BuildCard() As Object
        LoadSettings()

        Dim Root As New StackPanel With {.Margin = New Thickness(0, 2, 0, 0)}

        ' ---- 顶部状态行 ----
        Dim TopRow As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .Margin = New Thickness(0, 0, 0, 4)
        }
        Dim StatusCol As New StackPanel With {.Width = 238, .Margin = New Thickness(0, 0, 8, 0)}
        StatusText = New TextBlock With {
            .FontSize = 11,
            .TextWrapping = TextWrapping.Wrap
        }
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        StatusCol.Children.Add(StatusText)

        UsageText = New TextBlock With {
            .FontSize = 10,
            .Margin = New Thickness(0, 1, 0, 0),
            .TextWrapping = TextWrapping.Wrap
        }
        UsageText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        StatusCol.Children.Add(UsageText)
        TopRow.Children.Add(StatusCol)

        Dim SessBtn As New PCL.MyButton With {.Text = "会话", .Height = 24, .MinWidth = 48, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler SessBtn.Click,
            Sub(sender, e)
                If SessionPanel Is Nothing Then Return
                SessionPanel.Visibility = If(SessionPanel.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
                If SessionPanel.Visibility = Visibility.Visible Then RenderSessions()
            End Sub
        TopRow.Children.Add(SessBtn)

        Dim CfgBtn As New PCL.MyButton With {.Text = "配置", .Height = 24, .MinWidth = 48, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler CfgBtn.Click,
            Sub(sender, e)
                If ConfPanel IsNot Nothing Then
                    ConfPanel.Visibility = If(ConfPanel.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
                End If
            End Sub
        TopRow.Children.Add(CfgBtn)

        Dim ClearBtn As New PCL.MyButton With {.Text = "清空", .Height = 24, .MinWidth = 48}
        AddHandler ClearBtn.Click,
            Sub(sender, e)
                If Busy Then
                    Host.Hint("正在回答，先点「停止」")
                    Return
                End If
                Messages.Clear()
                SaveSession()
                RefreshSessionList()
                RenderAll()
                RenderSessions()
                UpdateStatus()
            End Sub
        TopRow.Children.Add(ClearBtn)
        Root.Children.Add(TopRow)

        ' ---- 会话面板（默认折叠） ----
        SessionPanel = New StackPanel With {.Visibility = Visibility.Collapsed, .Margin = New Thickness(0, 0, 0, 8)}
        Dim SessBar As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim NewBtn As New PCL.MyButton With {.Text = "＋ 新对话", .Height = 24, .MinWidth = 78, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler NewBtn.Click, Sub(sender, e) NewSession()
        SessBar.Children.Add(NewBtn)

        Dim RenBtn As New PCL.MyButton With {.Text = "改名", .Height = 24, .MinWidth = 56, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler RenBtn.Click, Sub(sender, e) BeginRename()
        SessBar.Children.Add(RenBtn)

        Dim ExpBtn As New PCL.MyButton With {.Text = "导出", .Height = 24, .MinWidth = 56}
        AddHandler ExpBtn.Click, Sub(sender, e) Host.Hint(ExportSession())
        SessBar.Children.Add(ExpBtn)

        ' 会话多了以后手动减负：只清空的，当前会话和带草稿的不动
        Dim PruneBtn As New PCL.MyButton With {.Text = "清理空会话", .Height = 24, .MinWidth = 88, .Margin = New Thickness(6, 0, 0, 0)}
        AddHandler PruneBtn.Click, Sub(sender, e) CleanEmptySessions()
        SessBar.Children.Add(PruneBtn)
        SessionPanel.Children.Add(SessBar)

        SessionList = New StackPanel()
        Dim SessScroll As New PCL.MyScrollViewer With {
            .Height = 120,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .Padding = New Thickness(0, 0, 4, 0),
            .Content = SessionList
        }
        HookWheel(SessScroll)
        SessionPanel.Children.Add(SessScroll)
        Root.Children.Add(SessionPanel)

        ' ---- 上下文用量条 ----
        CtxGrid = New Grid With {.Height = 3, .Margin = New Thickness(0, 0, 0, 8)}
        CtxGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(0, GridUnitType.Star)})
        CtxGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Dim CtxBg As New Border With {
            .CornerRadius = New CornerRadius(2),
            .Background = New SolidColorBrush(Color.FromArgb(26, 255, 255, 255))
        }
        Grid.SetColumnSpan(CtxBg, 2)
        CtxGrid.Children.Add(CtxBg)
        Dim CtxFill As New Border With {
            .CornerRadius = New CornerRadius(2),
            .HorizontalAlignment = HorizontalAlignment.Stretch
        }
        CtxFill.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
        Grid.SetColumn(CtxFill, 0)
        CtxGrid.Children.Add(CtxFill)
        Root.Children.Add(CtxGrid)

        ' ---- 配置面板 ----
        ConfPanel = New StackPanel With {.Visibility = Visibility.Collapsed, .Margin = New Thickness(0, 0, 0, 8)}
        CfgBase = New TextBox()
        CfgKey = New TextBox()
        CfgModel = New TextBox()
        CfgCf = New TextBox()
        ConfPanel.Children.Add(MakeFieldRow("接口", CfgBase, BaseUrl))
        ConfPanel.Children.Add(MakeFieldRow("密钥", CfgKey, ApiKey))
        ConfPanel.Children.Add(MakeFieldRow("模型", CfgModel, ModelName))
        ConfPanel.Children.Add(MakeFieldRow("CF Key（可选）", CfgCf, If(CfKey, "")))

        Dim QuickRow As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 2, 0, 6)}
        Dim DeepSeekBtn As New PCL.MyButton With {.Text = "用 DeepSeek", .Height = 24, .MinWidth = 96, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler DeepSeekBtn.Click,
            Sub(sender, e)
                CfgBase.Text = "https://api.deepseek.com"
                CfgModel.Text = "deepseek-chat"
            End Sub
        QuickRow.Children.Add(DeepSeekBtn)

        Dim OllamaBtn As New PCL.MyButton With {.Text = "用本地 Ollama", .Height = 24, .MinWidth = 104, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler OllamaBtn.Click,
            Sub(sender, e)
                CfgBase.Text = "http://127.0.0.1:11434/v1"
                CfgModel.Text = "qwen2.5"
                CfgKey.Text = ""
            End Sub
        QuickRow.Children.Add(OllamaBtn)

        Dim SaveBtn As New PCL.MyButton With {.Text = "保存", .Height = 24, .MinWidth = 56}
        AddHandler SaveBtn.Click,
            Sub(sender, e)
                BaseUrl = If(CfgBase.Text, "").Trim()
                ApiKey = If(CfgKey.Text, "").Trim()
                ModelName = If(CfgModel.Text, "").Trim()
                If ModelName.Length = 0 Then ModelName = "deepseek-chat"
                CfKey = If(CfgCf.Text, "").Trim()
                SaveSettings()
                UpdateStatus()
                Host.Hint("AI 助手配置已保存")
            End Sub
        QuickRow.Children.Add(SaveBtn)
        ConfPanel.Children.Add(QuickRow)
        Root.Children.Add(ConfPanel)

        ' ---- 对话区 ----
        MsgPanel = New StackPanel()
        Scroller = New PCL.MyScrollViewer With {
            .Height = 258,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .Padding = New Thickness(0, 0, 4, 0),
            .Content = MsgPanel
        }
        HookWheel(Scroller)
        Root.Children.Add(Scroller)

        ' ---- 输入区 ----
        InputBox = New TextBox With {
            .Height = 54,
            .AcceptsReturn = True,
            .TextWrapping = TextWrapping.Wrap,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .Margin = New Thickness(0, 8, 0, 6)
        }
        StyleInput(InputBox, 54.0)

        ' 用 handledEventsToo:=True 注册（VB 的 AddHandler 语句没这个参数，只能调方法）：
        ' AcceptsReturn 的 TextBox 会先在类处理器里把回车吃掉。
        InputBox.AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent, New System.Windows.Input.KeyEventHandler(AddressOf InputBox_KeyDown), True)
        InputBox.AddHandler(System.Windows.Input.Keyboard.KeyDownEvent, New System.Windows.Input.KeyEventHandler(AddressOf InputBox_KeyDown), True)
        ' 跟踪输入法组字：组字中的回车是「上屏」，不能当发送
        InputBox.AddHandler(System.Windows.Input.TextCompositionManager.TextInputStartEvent, New System.Windows.Input.TextCompositionEventHandler(AddressOf InputImeStart), True)
        InputBox.AddHandler(System.Windows.Input.TextCompositionManager.TextInputUpdateEvent, New System.Windows.Input.TextCompositionEventHandler(AddressOf InputImeStart), True)
        InputBox.AddHandler(System.Windows.Input.TextCompositionManager.TextInputEvent, New System.Windows.Input.TextCompositionEventHandler(AddressOf InputImeDone), True)
        ' 草稿：边打边记（内存），离开输入框 / 卡片被重建时落盘
        AddHandler InputBox.TextChanged, Sub(sender, e) DraftChanged()
        AddHandler InputBox.LostFocus,
            Sub(sender, e)
                Try
                    SaveDraftNow()
                Catch ex As Exception
                End Try
            End Sub
        Root.Children.Add(InputBox)

        Dim BtnRow As New StackPanel With {.Orientation = Orientation.Horizontal}
        SendBtn = New PCL.MyButton With {.Text = "发送", .Height = 28, .MinWidth = 76, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler SendBtn.Click, Sub(sender, e) Send()
        BtnRow.Children.Add(SendBtn)

        StopBtn = New PCL.MyButton With {.Text = "停止", .Height = 28, .MinWidth = 76, .Margin = New Thickness(0, 0, 10, 0), .IsEnabled = False}
        AddHandler StopBtn.Click,
            Sub(sender, e)
                Try
                    If Cts IsNot Nothing Then Cts.Cancel()
                Catch ex As Exception
                End Try
            End Sub
        BtnRow.Children.Add(StopBtn)

        Dim Tip As New TextBlock With {
            .Text = "Enter 发送 · /help 看指令",
            .FontSize = 11,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Tip.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        BtnRow.Children.Add(Tip)
        Root.Children.Add(BtnRow)

        RenderAll()
        RenderSessions()
        ' 卡片重建（切页回来 / 插件重载）时把没发出去的草稿铺回去
        ApplyDraft()
        ' 重建等于改名流程被打断：别让下一句回车把草稿当成新标题
        If RenameMode Then RenameMode = False
        RenameDraft = Nothing
        UpdateStatus()
        Return Root
    End Function

    ''' <summary>往上找最近的外层 ScrollViewer（就是宿主页面的滚动器）。</summary>
    Private Function FindOuterScroller(D As DependencyObject) As ScrollViewer
        Try
            Dim C = VisualTreeHelper.GetParent(D)
            While C IsNot Nothing
                Dim S = TryCast(C, ScrollViewer)
                If S IsNot Nothing Then Return S
                C = VisualTreeHelper.GetParent(C)
            End While
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' 让卡片内的滚动区自己吃滚轮。宿主的页面滚动器会带着动画把整页滚走，而且不理会
    ''' Handled，所以改为「鼠标停在这块区域时把页面钉住」：进区记下外层偏移，滚轮只滚自己，
    ''' 外层一旦被动画带走就拉回来；鼠标离开即解锁。
    ''' </summary>
    Private Sub HookWheel(Sv As PCL.MyScrollViewer)
        If Sv Is Nothing Then Return
        ' 建卡时还没挂进可视化树，找不到外层滚动器；等 Loaded 再挂。
        AddHandler Sv.Loaded,
            Sub(sender, e)
                HookWheelNow(Sv)
            End Sub
    End Sub

    Private Sub HookWheelNow(Sv As PCL.MyScrollViewer)
        If Sv Is Nothing Then Return
        Dim Outer As ScrollViewer = FindOuterScroller(Sv)
        ' PCL 自己的 PerformVerticalOffsetDelta 是带动画的平滑滚动，直接调它比自己改 Offset 手感好。
        Dim DoScroll As Action(Of Double) =
            Sub(Delta As Double)
                Try
                    Sv.PerformVerticalOffsetDelta(Delta)
                Catch exS As Exception
                    Try
                        Sv.ScrollToVerticalOffset(Sv.VerticalOffset + Delta)
                    Catch ex2 As Exception
                    End Try
                End Try
            End Sub
        Dim StepOf As Func(Of Integer, Double) =
            Function(WheelDelta As Integer)
                Dim M1 As Double = 1.0
                Try
                    M1 = CDbl(Sv.DeltaMult)
                Catch ex1 As Exception
                End Try
                Return -WheelDelta * M1 / 3.0
            End Function

        If Outer Is Nothing Then
            Sv.AddHandler(System.Windows.UIElement.PreviewMouseWheelEvent,
                New System.Windows.Input.MouseWheelEventHandler(
                    Sub(sender, e)
                        DoScroll(StepOf(e.Delta))
                        e.Handled = True
                    End Sub), True)
            Return
        End If

        Dim Pinned As Double = -1
        AddHandler Sv.MouseEnter, Sub(sender, e) Pinned = Outer.VerticalOffset
        AddHandler Sv.MouseLeave, Sub(sender, e)
                                      Pinned = -1
                                  End Sub
        AddHandler Outer.ScrollChanged,
            Sub(sender, e3)
                If Pinned >= 0 AndAlso Sv.IsMouseOver Then
                    If Math.Abs(Outer.VerticalOffset - Pinned) > 0.5 Then
                        Outer.ScrollToVerticalOffset(Pinned)
                    End If
                End If
            End Sub
        Sv.AddHandler(System.Windows.UIElement.PreviewMouseWheelEvent,
            New System.Windows.Input.MouseWheelEventHandler(
                Sub(sender, e)
                    If Pinned < 0 Then Pinned = Outer.VerticalOffset
                    DoScroll(StepOf(e.Delta))
                    e.Handled = True
                End Sub), True)
    End Sub

    ''' <summary>普通 TextBox 是系统白底，统一贴到卡片配色。</summary>
    Private Sub StyleInput(Box As TextBox, H As Double)
        If Box Is Nothing Then Return
        Box.Height = H
        Box.Background = New SolidColorBrush(Color.FromArgb(18, 255, 255, 255))
        Box.BorderBrush = New SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        Box.BorderThickness = New Thickness(1)
        Box.Padding = New Thickness(8, 4, 8, 4)
        Box.FontSize = 13
        Box.VerticalContentAlignment = If(H > 40, VerticalAlignment.Top, VerticalAlignment.Center)
        Box.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1")
        Box.SetResourceReference(TextBox.CaretBrushProperty, "ColorBrush1")
    End Sub
    Private Function MakeFieldRow(Label As String, Box As TextBox, Initial As String) As StackPanel
        Dim Row As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim Lb As New TextBlock With {
            .Text = Label,
            .FontSize = 12,
            .Width = 34,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Lb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        Row.Children.Add(Lb)
        Box.Text = If(Initial, "")
        Box.Width = 250
        StyleInput(Box, 26.0)
        Row.Children.Add(Box)
        Return Row
    End Function

    ''' <summary>输入法正在组字（拼音还没上屏）。</summary>
    Private ImeComposing As Boolean

    Private Sub InputImeStart(sender As Object, e As System.Windows.Input.TextCompositionEventArgs)
        ImeComposing = True
    End Sub

    Private Sub InputImeDone(sender As Object, e As System.Windows.Input.TextCompositionEventArgs)
        ImeComposing = False
    End Sub

    Private Sub InputBox_KeyDown(sender As Object, e As System.Windows.Input.KeyEventArgs)
        ' 中文输入法开着时，回车会以 ImeProcessed 形式过来，真实按键要看 ImeProcessedKey；
        ' 小键盘回车是 Key.Enter，主键盘是 Key.Return，两个都得认。
        Dim K = If(e.Key = System.Windows.Input.Key.ImeProcessed, e.ImeProcessedKey, e.Key)
        ' 组字中的回车要留给输入法上屏
        If e.Key = System.Windows.Input.Key.ImeProcessed AndAlso ImeComposing Then Return
        ' Esc 取消改名
        If RenameMode AndAlso K = System.Windows.Input.Key.Escape Then
            RenameMode = False
            RestoreRenameDraft()
            e.Handled = True
            Host.Hint("已取消改名")
            Return
        End If
        If (K = System.Windows.Input.Key.Return OrElse K = System.Windows.Input.Key.Enter) AndAlso
           (System.Windows.Input.Keyboard.Modifiers And System.Windows.Input.ModifierKeys.Shift) = 0 Then
            e.Handled = True
            Send()
        End If
    End Sub

    ' ==================== 状态 / 用量 ====================

    ''' <summary>粗略估 token：中文约 0.6 token/字，英文约 0.3。</summary>
    Private Function EstimateTokens() As Integer
        Dim Chars As Integer = 0
        For Each M In Messages
            Chars += If(M.Content, "").Length
        Next
        Chars += If(EffectiveSysPrompt(), "").Length
        Return CInt(Chars * 0.55)
    End Function

    Private Function FmtTok(N As Integer) As String
        If N >= 1000 Then Return (N / 1000.0).ToString("0.0") & "k"
        Return N.ToString()
    End Function

    ''' <summary>状态行里的会话数：快到上限时才把上限显示出来，平时不占地方。</summary>
    Private Function SessionCountText() As String
        If Sessions.Count <= 1 Then Return ""
        Dim T As String = " · 共 " & Sessions.Count & " 个会话"
        If Sessions.Count >= SessionCap - 5 Then T &= "（上限 " & SessionCap & "）"
        Return T
    End Function

    Private Sub UpdateStatus()
        If StatusText Is Nothing Then Return
        Dim KeyState As String = "未配置密钥"
        If ApiKey.Length > 0 Then KeyState = "已配置密钥"
        If IsLocalEndpoint() Then KeyState = "本地接口"
        StatusText.Text = Shorten(If(SessionTitle, "新对话"), 16) & " · " & Messages.Count & " 条" &
                          SessionCountText() &
                          If(LastToolCount > 0, " · 工具 " & LastToolCount, "")

        If UsageText IsNot Nothing Then
            Dim Used As Integer = EstimateTokens()
            Dim Pct As Double = 0
            If CtxLimit > 0 Then Pct = Math.Min(1.0, Used / CtxLimit)
            UsageText.Text = $"{ModelName} · {KeyState} · 上下文 ≈ {FmtTok(Used)} / {FmtTok(CtxLimit)} ({CInt(Pct * 100)}%)" &
                             If(LastPromptToks > 0, $" · 上次 ↑{LastPromptToks} ↓{LastCompletionToks}", "")
        End If

        If CtxGrid IsNot Nothing AndAlso CtxGrid.ColumnDefinitions.Count = 2 Then
            Dim Used As Integer = EstimateTokens()
            Dim Pct As Double = 0
            If CtxLimit > 0 Then Pct = Math.Min(1.0, Used / CtxLimit)
            If Pct < 0.0001 Then Pct = 0.0001
            CtxGrid.ColumnDefinitions(0).Width = New GridLength(Pct, GridUnitType.Star)
            CtxGrid.ColumnDefinitions(1).Width = New GridLength(1.0 - Pct, GridUnitType.Star)
        End If
    End Sub

    ' ==================== 渲染 ====================

    Private Sub RenderAll()
        If MsgPanel Is Nothing Then Return
        MsgPanel.Children.Clear()
        If Messages.Count = 0 Then
            AddTransient("当前会话「" & SessionTitle & "」还没有内容。" & Environment.NewLine &
                         "第一次用先点「配置」填接口和密钥；「会话」里可以新建 / 切换 / 改名 / 导出。" & Environment.NewLine &
                         "我能真的操作启动器，也能真的下东西：/instances 列版本、/launch 启动、/download 看已下载……输入 /help 看全部指令。")
            Return
        End If
        For Each M In Messages
            If String.IsNullOrWhiteSpace(M.Content) Then Continue For
            AddBubble(M.Role, M.Content)
        Next
        ScrollToEnd()
    End Sub

    ''' <summary>重画会话列表（每次增删改都要调）。</summary>
    Private Sub RenderSessions()
        If SessionList Is Nothing Then Return
        SessionList.Children.Clear()
        If Sessions.Count = 0 Then
            Dim T As New TextBlock With {.Text = "还没有会话。", .FontSize = 12, .Margin = New Thickness(0, 2, 0, 2)}
            T.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
            SessionList.Children.Add(T)
            Return
        End If
        For Each S In Sessions
            SessionList.Children.Add(SessionRow(S))
        Next
    End Sub

    Private Function SessionRow(S As ChatSession) As FrameworkElement
        Dim Row As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim IsCur As Boolean = String.Equals(S.Id, SessionId, StringComparison.OrdinalIgnoreCase)
        Dim Label As String = If(IsCur, "● ", "○ ") & S.Title & "（" & S.Count & " 条）"
        Dim B As New PCL.MyButton With {
            .Text = Shorten(Label, 40),
            .Height = 24,
            .MinWidth = 208,
            .Margin = New Thickness(0, 0, 6, 0)
        }
        Dim Sid As String = S.Id
        AddHandler B.Click, Sub(sender, e) SwitchSession(Sid)
        Row.Children.Add(B)
        If Not IsCur Then
            Dim Del As New PCL.MyButton With {.Text = "删", .Height = 24, .MinWidth = 36}
            AddHandler Del.Click, Sub(sender, e) DeleteSession(Sid)
            Row.Children.Add(Del)
        End If
        Return Row
    End Function

    ''' <summary>改名：把当前标题塞进输入框，下一次回车当成新标题。</summary>
    Private Sub BeginRename()
        If Busy Then
            Host.Hint("正在回答，先点「停止」")
            Return
        End If
        If InputBox Is Nothing Then Return
        RenameMode = True
        ' 输入框要被标题临时占用：先把草稿存起来，改完 / 取消再还回去
        RenameDraft = If(InputBox.Text, "")
        RenameCaret = InputBox.CaretIndex
        SuppressDraft = True
        Try
            InputBox.Text = SessionTitle
        Finally
            SuppressDraft = False
        End Try
        InputBox.Focus()
        Try
            InputBox.CaretIndex = InputBox.Text.Length
        Catch ex As Exception
        End Try
        Host.Hint("改名模式：输入新标题后回车（Esc 取消）")
    End Sub

    ''' <summary>把消息渲染成气泡，返回气泡内容面板（流式时反复重填）。</summary>
    Private Function AddBubble(Role As String, Text As String) As StackPanel
        If MsgPanel Is Nothing Then Return Nothing
        Dim IsUser As Boolean = (Role = "user")
        Dim Brd As New Border With {
            .CornerRadius = New CornerRadius(6),
            .Padding = New Thickness(10, 7, 10, 7),
            .Margin = New Thickness(0, 0, 0, 8),
            .MaxWidth = 468,
            .HorizontalAlignment = If(IsUser, HorizontalAlignment.Right, HorizontalAlignment.Left),
            .Background = If(IsUser,
                New SolidColorBrush(Color.FromArgb(30, 96, 140, 255)),
                New SolidColorBrush(Color.FromArgb(22, 128, 128, 128)))
        }
        Dim Sp As New StackPanel()
        FillBubble(Sp, Text)
        Brd.Child = Sp
        MsgPanel.Children.Add(Brd)
        Return Sp
    End Function

    ''' <summary>只出现在界面、不写进历史的提示气泡（实例列表、指令回显等）。</summary>
    Private Function AddTransient(Text As String) As StackPanel
        If MsgPanel Is Nothing Then Return Nothing
        Dim Brd As New Border With {
            .CornerRadius = New CornerRadius(6),
            .Padding = New Thickness(10, 7, 10, 7),
            .Margin = New Thickness(0, 0, 0, 8),
            .MaxWidth = 468,
            .HorizontalAlignment = HorizontalAlignment.Left,
            .Background = New SolidColorBrush(Color.FromArgb(18, 128, 128, 128))
        }
        Dim Sp As New StackPanel()
        FillBubble(Sp, Text, True)
        Brd.Child = Sp
        MsgPanel.Children.Add(Brd)
        Return Sp
    End Function

    ''' <summary>按 ``` 围栏切分文本：普通段落用正文字体，代码段落用等宽块。</summary>
    Private Sub FillBubble(Sp As StackPanel, Text As String, Optional Faded As Boolean = False)
        If Sp Is Nothing Then Return
        Sp.Children.Clear()
        Dim T As String = If(Text, "")
        Dim I As Integer = 0
        Do While True
            Dim A As Integer = T.IndexOf(Fence, I, StringComparison.Ordinal)
            If A < 0 Then
                If I < T.Length Then AddPlain(Sp, T.Substring(I), Faded)
                Exit Do
            End If
            If A > I Then AddPlain(Sp, T.Substring(I, A - I), Faded)
            Dim B As Integer = T.IndexOf(Fence, A + Fence.Length, StringComparison.Ordinal)
            If B < 0 Then
                ' 还没收口（流式输出中）：整段当代码，后面继续追加
                AddCode(Sp, T.Substring(A + Fence.Length))
                Exit Do
            End If
            AddCode(Sp, T.Substring(A + Fence.Length, B - (A + Fence.Length)))
            I = B + Fence.Length
        Loop
        If Sp.Children.Count = 0 Then AddPlain(Sp, "", Faded)
    End Sub

    Private Sub AddPlain(Sp As StackPanel, Text As String, Faded As Boolean)
        Dim Clean As String = If(Text, "").TrimStart(Convert.ToChar(10))
        If Clean.Length = 0 Then Return
        Dim Tb As New TextBlock With {
            .Text = Clean.TrimEnd(Convert.ToChar(10)),
            .TextWrapping = TextWrapping.Wrap,
            .FontSize = 13,
            .LineHeight = 19
        }
        Tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        If Faded Then Tb.Opacity = 0.75
        Sp.Children.Add(Tb)
    End Sub

    ''' <summary>代码块：深色底 + 等宽字体，首行若是语言标记就当标签显示。</summary>
    Private Sub AddCode(Sp As StackPanel, Body As String)
        Dim Raw As String = If(Body, "").TrimStart(Convert.ToChar(10))
        Dim Lang As String = ""
        Dim Code As String = Raw
        Dim FirstNl As Integer = Raw.IndexOf(Convert.ToChar(10))
        If FirstNl > 0 AndAlso FirstNl <= 20 Then
            Dim Head As String = Raw.Substring(0, FirstNl).Trim()
            If Head.Length > 0 AndAlso Head.IndexOf(Convert.ToChar(32)) < 0 Then Lang = Head
            If Lang.Length > 0 Then Code = Raw.Substring(FirstNl + 1)
        End If
        Code = Code.TrimEnd(Convert.ToChar(10))
        ' 只有语言标记、没有正文的残片（旧版历史被撕碎后留下的）直接不画。
        If Code.Trim().Length = 0 AndAlso Lang.Length > 0 Then Return

        Dim Box As New StackPanel()
        If Lang.Length > 0 Then
            Dim Tag As New TextBlock With {
                .Text = Lang,
                .FontSize = 10,
                .Margin = New Thickness(0, 0, 0, 3),
                .Opacity = 0.6
            }
            Tag.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
            Box.Children.Add(Tag)
        End If
        Dim Tb As New TextBlock With {
            .Text = Code,
            .TextWrapping = TextWrapping.Wrap,
            .FontFamily = New FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
            .FontSize = 12,
            .LineHeight = 17
        }
        Tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Box.Children.Add(Tb)

        Dim Brd As New Border With {
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(9, 7, 9, 7),
            .Margin = New Thickness(0, 3, 0, 3),
            .Background = New SolidColorBrush(Color.FromArgb(56, 0, 0, 0)),
            .BorderBrush = New SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            .BorderThickness = New Thickness(1),
            .Child = Box
        }
        Sp.Children.Add(Brd)
    End Sub

    Private Sub ScrollToEnd()
        If Scroller Is Nothing Then Return
        Try
            If MsgPanel IsNot Nothing Then MsgPanel.UpdateLayout()
            Scroller.UpdateLayout()
            Scroller.ScrollToVerticalOffset(Scroller.ScrollableHeight)
            ' 首屏渲染时布局还没算完，ScrollableHeight 会偏小；再补一帧。
            Scroller.Dispatcher.BeginInvoke(
                New Action(Sub()
                              Try
                                  If MsgPanel IsNot Nothing Then MsgPanel.UpdateLayout()
                                  Scroller.UpdateLayout()
                                  Scroller.ScrollToVerticalOffset(Scroller.ScrollableHeight)
                              Catch ex2 As Exception
                              End Try
                          End Sub),
                System.Windows.Threading.DispatcherPriority.ContextIdle)
        Catch ex As Exception
        End Try
    End Sub

    ' ==================== 发送与流式接收 ====================

    Private Sub Send()
        If Busy Then Return
        If Host Is Nothing Then Return
        LoadSettings()
        If InputBox Is Nothing Then Return

        Dim Text As String = If(InputBox.Text, "").Trim()

        ' 改名模式：这一句是标题，不是提问
        If RenameMode Then
            RenameMode = False
            ' 输入框里现在是标题，草稿还压着，处理完再还原
            If Text.Length = 0 Then
                RestoreRenameDraft()
                Host.Hint("标题为空，已取消改名")
                Return
            End If
            RenameSession(SessionId, Text)
            RestoreRenameDraft()
            Host.Hint("会话已改名为「" & SessionTitle & "」")
            UpdateStatus()
            Return
        End If

        If Text.Length = 0 Then Return

        ' 斜杠指令走本地，不发请求
        If Text.StartsWith("/") Then
            InputBox.Text = ""
            HandleCommand(Text)
            Return
        End If

        If ApiKey.Length = 0 AndAlso Not IsLocalEndpoint() Then
            UpdateStatus()
            If ConfPanel IsNot Nothing Then ConfPanel.Visibility = Visibility.Visible
            Host.Hint("先在「配置」里填 API Key，或用本地 Ollama")
            Return
        End If

        InputBox.Text = ""

        Dim WasEmpty As Boolean = (Messages.Count = 0)
        Messages.Add(New ChatMessage With {.Role = "user", .Content = Text})
        MaybeAutoTitle(Text)
        If WasEmpty AndAlso MsgPanel IsNot Nothing Then MsgPanel.Children.Clear()
        AddBubble("user", Text)

        Dim Target As New ChatMessage With {.Role = "assistant", .Content = ""}
        Messages.Add(Target)
        CurTarget = Target
        CurBubble = AddBubble("assistant", "")
        ScrollToEnd()

        Dim Extra As New List(Of String)
        Dim ForceTool As Boolean = LooksLikeAction(Text)
        Dim ToolsUsed As Integer = 0
        FailSeen.Clear()
        Busy = True
        SetBusyUi(True)
        UpdateStatus()

        Cts = New CancellationTokenSource()
        Dim Tok As CancellationToken = Cts.Token

        Task.Run(Async Function() As Task
                     Try
                         Dim Rounds As Integer = 0
                         Do
                             Tok.ThrowIfCancellationRequested()
                             ToolCalls.Clear()
                             Dim Choice As String = "auto"
                             If ForceTool AndAlso Rounds = 0 Then Choice = "required"
                             Dim Body As String = BuildRequestJson(Extra, Choice)
                             Dim SearchRejected As Boolean = False
                             Try
                                 Await StreamChatAsync(Body, Target, Tok).ConfigureAwait(False)
                             Catch ex As OperationCanceledException
                                 Throw
                             Catch ex As Exception
                                 ' 这家接口 / 中转站不认服务端联网工具：先不往上抛，摘掉工具重发一次，
                                 ' 免得整个对话因为一个可选的联网开关直接失败
                                 If ServerSearchInjected AndAlso Not ServerSearchBroken AndAlso LooksLikeSearchReject(ex.Message) Then
                                     ServerSearchBroken = True
                                     SearchRejected = True
                                 Else
                                     Throw
                                 End If
                             End Try
                             If SearchRejected Then
                                 If Host IsNot Nothing Then Host.Hint("这个接口不认模型自带联网，已切回普通模式")
                                 Body = BuildRequestJson(Extra, Choice)
                                 Await StreamChatAsync(Body, Target, Tok).ConfigureAwait(False)
                             End If
                             If ToolCalls.Count = 0 Then Exit Do

                             Rounds += 1
                             If Rounds > MaxToolRounds Then
                                 ' 轮次耗尽：不再执行工具，但必须让模型把已有结果讲完，
                                 ' 否则用户只会看到一句「已达上限」，等于白装。
                                 ToolCalls.Clear()
                                 AppendPiece(Target, Environment.NewLine & Environment.NewLine &
                                             "（工具调用已用满 " & MaxToolRounds & " 轮，下面基于已有结果总结）")
                                 Dim WrapExtra As New List(Of String)(Extra)
                                 Dim WrapSb As New StringBuilder()
                                 WrapSb.Append("{").Append(JsonStr("role")).Append(":").Append(JsonStr("user")).Append(",")
                                 WrapSb.Append(JsonStr("content")).Append(":").Append(
                                     JsonStr("（系统提示：工具调用次数已达上限，不要再调用任何工具。请只依据上面已经拿到的工具返回结果，把当前进展说清楚：做了什么、结果如何、还差什么、用户下一步该做什么。不要补充任何没拿到的信息。）")).Append("}")
                                 WrapExtra.Add(WrapSb.ToString())
                                 Try
                                     Dim WrapBody As String = BuildRequestJson(WrapExtra, "none")
                                     Await StreamChatAsync(WrapBody, Target, Tok).ConfigureAwait(False)
                                 Catch ex As OperationCanceledException
                                     Throw
                                 Catch ex As Exception
                                     AppendPiece(Target, Environment.NewLine & "⚠ 总结失败：" & Redact(ex.Message))
                                 End Try
                                 Exit Do
                             End If

                             Extra.Add(ToolCallsMessageJson())
                             For Each Tc In ToolCalls
                                 Dim R As String
                                 If String.Equals(Tc.Name, "$web_search", StringComparison.OrdinalIgnoreCase) Then
                                     ' Kimi 的 builtin_function：客户端只把参数原样回传，检索由 Moonshot 服务端完成。
                                     ' 这里千万不能当成普通工具去执行——本地没有、也不该有搜索实现。
                                     R = Tc.Args.ToString()
                                     ToolsUsed += 1
                                     If Host IsNot Nothing Then Host.LogInfo("AI 服务端联网 → " & Shorten(R, 120))
                                 Else
                                     R = ExecTool(Tc.Name, Tc.Args.ToString())
                                     ToolsUsed += 1
                                     If Host IsNot Nothing Then Host.LogInfo("AI 工具 " & Tc.Name & " → " & Shorten(R, 140))
                                 End If
                                 Extra.Add(ToolResultMessageJson(Tc.Id, R))
                             Next
                             FlushUi()
                         Loop
                         If ToolsUsed = 0 AndAlso ForceTool Then
                             AppendPiece(Target, Environment.NewLine & Environment.NewLine &
                                         "⚠ 这一轮没有调用任何工具，上面的内容不是工具返回的真实结果，请让我重试或换个问法。")
                         End If
                         LastToolCount = ToolsUsed
                     Catch ex As OperationCanceledException
                         AppendPiece(Target, Environment.NewLine & "（已停止）")
                     Catch ex As Exception
                         If Host IsNot Nothing Then Host.LogWarn("AI 助手请求失败：" & Redact(ex.Message) & " | " & Redact(ex.ToString()))
                         AppendPiece(Target, "⚠ " & Redact(ex.Message))
                     Finally
                         FlushUi()
                         Busy = False
                         SaveSession()
                         RefreshSessionList()
                         If Host IsNot Nothing Then
                             Host.RunInUi(
                                 Sub()
                                     SetBusyUi(False)
                                     RenderSessions()
                                     UpdateStatus()
                                     ScrollToEnd()
                                 End Sub)
                         End If
                     End Try
                 End Function)
    End Sub

    Private Sub SetBusyUi(Active As Boolean)
        If SendBtn IsNot Nothing Then SendBtn.IsEnabled = Not Active
        If StopBtn IsNot Nothing Then StopBtn.IsEnabled = Active
    End Sub

    Private Async Function StreamChatAsync(Body As String, Target As ChatMessage, Tok As CancellationToken) As Task
        Dim Req As New HttpRequestMessage(HttpMethod.Post, ApiEndpoint())
        Req.Content = New StringContent(Body, Encoding.UTF8, "application/json")
        If ApiKey.Length > 0 Then
            Req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & ApiKey)
        End If
        Req.Headers.TryAddWithoutValidation("Accept", "text/event-stream")

        Dim Resp As HttpResponseMessage
        Try
            Resp = Await Client.SendAsync(Req, HttpCompletionOption.ResponseHeadersRead, Tok).ConfigureAwait(False)
        Catch ex As OperationCanceledException
            Throw
        Catch ex As Exception
            Throw New Exception(Redact(ConnectErrorText(ex)))
        End Try
        If Not Resp.IsSuccessStatusCode Then
            Dim ErrBody As String = ""
            Try
                ErrBody = Await Resp.Content.ReadAsStringAsync().ConfigureAwait(False)
            Catch ex As Exception
            End Try
            Dim Tip As String = ""
            If CInt(Resp.StatusCode) = 404 Then
                Tip = "（404：多半是模型名不存在。用 ollama list 看已装模型，或在「配置」里改成已装的那个。）"
            ElseIf CInt(Resp.StatusCode) = 401 OrElse CInt(Resp.StatusCode) = 403 Then
                Tip = "（鉴权失败：检查 API Key 是否填对。）"
            End If
            Throw New Exception($"HTTP {CInt(Resp.StatusCode)}：{Shorten(ErrBody, 240)}{Tip}")
        End If

        Using St As Stream = Await Resp.Content.ReadAsStreamAsync().ConfigureAwait(False)
            Using Rd As New StreamReader(St, Encoding.UTF8)
                Do
                    Tok.ThrowIfCancellationRequested()
                    Dim Line As String = Await Rd.ReadLineAsync().ConfigureAwait(False)
                    If Line Is Nothing Then Exit Do
                    If Not Line.StartsWith("data:", StringComparison.Ordinal) Then Continue Do
                    Dim Data As String = Line.Substring(5).Trim()
                    If Data.Length = 0 Then Continue Do
                    If Data = "[DONE]" Then Exit Do
                    ReadUsage(Data)
                    ReadToolDeltas(Data)
                    Dim Piece As String = ExtractContent(Data)
                    If Piece IsNot Nothing AndAlso Piece.Length > 0 Then AppendPiece(Target, Piece)
                Loop
            End Using
        End Using
        FlushUi()
    End Function

    ''' <summary>从 SSE 块里读 usage（DeepSeek 在最后一帧返回 prompt/completion tokens）。</summary>
    Private Sub ReadUsage(Json As String)
        Try
            Dim P As Integer = Json.IndexOf("""prompt_tokens"":", StringComparison.Ordinal)
            If P < 0 Then Return
            Dim C As Integer = Json.IndexOf("""completion_tokens"":", StringComparison.Ordinal)
            Dim PT As Integer = ReadNumberAfter(Json, P + 16)
            Dim CT As Integer = 0
            If C >= 0 Then CT = ReadNumberAfter(Json, C + 20)
            If PT > 0 Then
                LastPromptToks = PT
                LastCompletionToks = CT
            End If
        Catch ex As Exception
        End Try
    End Sub

    Private Function ReadNumberAfter(S As String, Index As Integer) As Integer
        Dim I As Integer = Index
        While I < S.Length AndAlso (S(I) = Convert.ToChar(32) OrElse S(I) = Convert.ToChar(9))
            I += 1
        End While
        Dim Start As Integer = I
        While I < S.Length AndAlso Char.IsDigit(S(I))
            I += 1
        End While
        If I = Start Then Return 0
        Dim N As Integer
        If Integer.TryParse(S.Substring(Start, I - Start), N) Then Return N
        Return 0
    End Function

    Private Sub AppendPiece(Target As ChatMessage, Piece As String)
        SyncLock SyncRoot
            Target.Content = Target.Content & Piece
            Pending.Append(Piece)
        End SyncLock
        If (DateTime.UtcNow - LastFlush).TotalMilliseconds >= 90 Then FlushUi()
    End Sub

    Private Sub FlushUi()
        Dim Full As String
        SyncLock SyncRoot
            If Pending.Length = 0 Then
                LastFlush = DateTime.UtcNow
                Return
            End If
            Pending.Clear()
            Full = If(CurTarget Is Nothing, "", CurTarget.Content)
            LastFlush = DateTime.UtcNow
        End SyncLock
        If Host Is Nothing Then Return
        Host.RunInUi(
            Sub()
                If CurBubble IsNot Nothing Then FillBubble(CurBubble, Full)
                ScrollToEnd()
            End Sub)
    End Sub

    ' ==================== 斜杠指令 ====================

    Private Sub HandleCommand(Text As String)
        Dim Parts As String() = Text.Substring(1).Split(New Char() {Convert.ToChar(32)}, 2)
        Dim Cmd As String = Parts(0).ToLowerInvariant()
        Dim Arg As String = If(Parts.Length > 1, Parts(1).Trim(), "")

        Select Case Cmd
            Case "help", "帮助", "?"
                AddTransient("可用指令：" & Environment.NewLine &
                             "/instances  列出启动器里的游戏实例" & Environment.NewLine &
                             "/launch <名字>  启动指定实例" & Environment.NewLine &
                             "/jvm <参数>  给下次启动追加 JVM 参数" & Environment.NewLine &
                             "/args <参数>  给下次启动追加游戏参数" & Environment.NewLine &
                             "/args  （不带参数）  查看当前追加了什么" & Environment.NewLine &
                             "/new  新建一个会话" & Environment.NewLine &
                             "/sessions  列出所有会话" & Environment.NewLine &
                             "/use <编号>  切换到某个会话" & Environment.NewLine &
                             "/rename <标题>  给当前会话改名" & Environment.NewLine &
                             "/export  把当前会话导出成 Markdown" & Environment.NewLine &
                             "/downloads  看已经下载的文件" & Environment.NewLine &
                             "/clear  清空当前会话" & Environment.NewLine &
                             "输入框草稿按会话保留：切页、切会话、重开启动器都还在。" & Environment.NewLine &
                             "会话最多保留 " & SessionCap & " 个（插件设置里可改）：新建时若已满，先清空的、再清最久没动的。" & Environment.NewLine &
                             "/agent  看 agent.md 里写了什么长期约定；/agent <一句话> 追加一条；/agent reset 重建模板。" & Environment.NewLine &
                             "agent.md 在插件 data 目录，直接编辑保存就生效，插件升级不会覆盖它。" & Environment.NewLine &
                             "其余内容都直接发给 AI。")
                ScrollToEnd()

            Case "instances", "实例", "list"
                AddTransient(InstanceListText())
                ScrollToEnd()

            Case "launch", "启动"
                If Arg.Length = 0 Then
                    AddTransient("用法：/launch <实例名片段>，例如 /launch 1.8.9；不带名字则启动当前实例。")
                    AddTransient(SelectAndLaunch(""))
                Else
                    AddTransient(SelectAndLaunch(Arg))
                End If
                ScrollToEnd()

            Case "jvm"
                If Arg.Length = 0 Then
                    AddTransient("当前追加的 JVM 参数：" & ListOrNone(Host.ExtraJvmArgs))
                Else
                    Host.AppendJvmArgs(Arg)
                    AddTransient("已追加 JVM 参数：" & Arg & Environment.NewLine &
                                 "现在共 " & Host.ExtraJvmArgs.Count & " 条，下次启动生效。")
                End If
                ScrollToEnd()

            Case "args", "gameargs"
                If Arg.Length = 0 Then
                    AddTransient("JVM 参数：" & ListOrNone(Host.ExtraJvmArgs) & Environment.NewLine &
                                 "游戏参数：" & ListOrNone(Host.ExtraGameArgs))
                Else
                    Host.AppendGameArgs(Arg)
                    AddTransient("已追加游戏参数：" & Arg & Environment.NewLine &
                                 "现在共 " & Host.ExtraGameArgs.Count & " 条，下次启动生效。")
                End If
                ScrollToEnd()

            Case "new", "新会话"
                NewSession()
                AddTransient("已新建会话「" & SessionTitle & "」。")
                ScrollToEnd()

            Case "sessions", "会话"
                AddTransient(SessionListText())
                ScrollToEnd()

            Case "use", "switch"
                Dim Idx As Integer
                If Arg.Length = 0 OrElse Not Integer.TryParse(Arg, Idx) Then
                    AddTransient("用法：/use <编号>，编号来自 /sessions。")
                ElseIf Idx < 1 OrElse Idx > Sessions.Count Then
                    AddTransient("编号超出范围，用 /sessions 看当前列表。")
                Else
                    SwitchSession(Sessions(Idx - 1).Id)
                    AddTransient("已切到会话「" & SessionTitle & "」。")
                End If
                ScrollToEnd()

            Case "rename", "改名"
                If Arg.Length = 0 Then
                    BeginRename()
                Else
                    RenameSession(SessionId, Arg)
                    AddTransient("会话已改名为「" & SessionTitle & "」。")
                End If
                ScrollToEnd()

            Case "export", "导出"
                AddTransient(ExportSession())
                ScrollToEnd()

            Case "downloads", "下载"
                AddTransient(ToolListDownloads())
                ScrollToEnd()

            Case "clear", "清空"
                Messages.Clear()
                SaveSession()
                RefreshSessionList()
                RenderAll()
                RenderSessions()
                UpdateStatus()

            Case "agent", "约定", "md"
                If Arg.Length = 0 Then
                    AddTransient(AgentDocStatus())
                ElseIf Arg.ToLowerInvariant() = "reset" OrElse Arg = "重置" Then
                    AddTransient(ResetAgentDoc())
                ElseIf Arg.ToLowerInvariant() = "open" OrElse Arg = "打开" Then
                    AddTransient("agent.md 路径：" & Environment.NewLine & AgentDocPath() & Environment.NewLine &
                                 "在资源管理器地址栏粘这个路径就能打开（插件不能直接调起编辑器）。")
                Else
                    AddTransient(AppendAgentLine(Arg))
                End If
                ScrollToEnd()

            Case Else
                AddTransient("不认识的指令「/" & Cmd & "」，输入 /help 看列表。")
                ScrollToEnd()
        End Select
    End Sub

    Private Function ListOrNone(L As List(Of String)) As String
        If L Is Nothing OrElse L.Count = 0 Then Return "（无）"
        Return String.Join(" | ", L.ToArray())
    End Function

    ''' <summary>会话列表的纯文本版（给 /sessions 和 /use 的编号）。</summary>
    Private Function SessionListText() As String
        If Sessions.Count = 0 Then Return "还没有会话。"
        Dim Sb As New StringBuilder()
        Sb.Append("共 ").Append(Sessions.Count).Append(" / ").Append(SessionCap).Append(" 个会话（● 是当前）：")
        Dim I As Integer = 0
        For Each S In Sessions
            I += 1
            Sb.Append(Environment.NewLine).Append(I).Append(". ")
            If String.Equals(S.Id, SessionId, StringComparison.OrdinalIgnoreCase) Then Sb.Append("● ")
            Sb.Append(S.Title).Append("（").Append(S.Count).Append(" 条）")
        Next
        Sb.Append(Environment.NewLine).Append("切换：/use <编号>；新建：/new；改名：/rename <标题>；导出：/export")
        Return Sb.ToString()
    End Function

    ' ==================== 启动器实例 ====================

    ''' <summary>启动器当前的 MC 根目录：优先问宿主（McFolderSelected.Path），其次读 Setup.ini。</summary>
    ' ==================== 实例与内容（API v7.3） ====================

    ' ==================== 安装顺序（先装本体，再下内容） ====================

    ''' <summary>把一个刚发起、还在后台跑的安装记下来；落地之前不许下载任何内容。</summary>
    Private Sub MarkInstallPending(Name As String)
        If String.IsNullOrWhiteSpace(Name) Then Return
        InstallPending = Name.Trim()
        InstallPendingAt = DateTime.Now
        If Host IsNot Nothing Then Host.LogInfo("安装任务开始，暂停内容下载：" & InstallPending)
    End Sub

    ''' <summary>安装是否已经落地：版本 json 出现，或者实例列表里已经有这个名字。</summary>
    Private Function InstallLanded(Name As String) As Boolean
        If String.IsNullOrWhiteSpace(Name) Then Return True
        Try
            Dim McDir As String = PclMcDir()
            If McDir.Length > 0 Then
                If File.Exists(McDir & "versions\" & Name & "\" & Name & ".json") Then Return True
            End If
        Catch ex As Exception
        End Try
        For Each E In AllInstances()
            If String.Equals(E.Name, Name, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    ''' <summary>
    ''' 下载内容前的守门员：后台安装没落地就返回一段拒绝文案，落地了返回空串。
    ''' 堵的就是「游戏本体还没装好，mod 先下进旧实例」这个坑。
    ''' </summary>
    Private Function InstallGuard() As String
        If InstallPending.Length = 0 Then Return ""
        If (DateTime.Now - InstallPendingAt).TotalMinutes > 45 Then
            ' 兜底：任务可能失败了、或者用户把启动器关了，别让下载一直被卡住
            If Host IsNot Nothing Then Host.LogWarn("安装任务超过 45 分钟没落地，解除下载限制：" & InstallPending)
            InstallPending = ""
            Return ""
        End If
        If Not InstallLanded(InstallPending) Then
            Return "先别下内容：实例「" & InstallPending & "」还在安装（启动器右下角任务栏在跑），版本文件还没落地。" &
                   Environment.NewLine &
                   "这时候下载会装进当前选中的旧实例，等于装错地方，还可能让旧实例起不来。" & Environment.NewLine &
                   "正确顺序：等任务栏走完 → 用户回「启动」页刷新版本列表并选中 " & InstallPending &
                   "（或者你先 select_instance）→ 再下载内容。" & Environment.NewLine &
                   "不要跳过这一步，也不要反复查进度（装完之前查不到）。"
        End If
        Dim Done As String = InstallPending
        InstallPending = ""
        LastInstalled = Done
        If Host IsNot Nothing Then Host.LogInfo("安装已落地，恢复内容下载：" & Done)
        Return ""
    End Function

    ''' <summary>
    ''' 刚装完的那个实例和当前选中的不一致时给一句提醒（只提醒一次，不拦截）。
    ''' 内容是装进「当前实例」的，模型很容易在装完 1.20.1 之后把 mod 下到旧实例里。
    ''' </summary>
    Private Function InstanceMismatchHint() As String
        If LastInstalled.Length = 0 Then Return ""
        Dim Done As String = LastInstalled
        LastInstalled = ""
        Dim Cur As String = ""
        Try
            Dim I = Host.InstanceInfo
            If I IsNot Nothing AndAlso I.Available Then Cur = I.Name
        Catch ex As Exception
        End Try
        If Cur.Length = 0 Then
            Return "（提示：现在没有选中的实例，文件可能无处可装；刚装好的是「" & Done & "」，先 select_instance 切过去）"
        End If
        If Not String.Equals(Cur, Done, StringComparison.OrdinalIgnoreCase) Then
            Return "（提示：刚装好的是「" & Done & "」，当前选中的是「" & Cur & "」，这次内容会装进「" & Cur & "」。要装进新实例就先 select_instance）"
        End If
        Return ""
    End Function

    Private Function AllInstances() As List(Of PCL.PluginInstanceEntry)
        Try
            If Host Is Nothing Then Return New List(Of PCL.PluginInstanceEntry)
            Dim L = Host.ListInstances()
            If L IsNot Nothing Then Return L
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("ListInstances 失败：" & ex.Message)
        End Try
        Return New List(Of PCL.PluginInstanceEntry)
    End Function

    Private Function FindInstEntry(Key As String) As PCL.PluginInstanceEntry
        Dim K As String = If(Key, "").Trim().ToLowerInvariant()
        If K.Length = 0 Then Return Nothing
        Dim All = AllInstances()
        For Each E In All
            If E.Name.ToLowerInvariant() = K Then Return E
        Next
        For Each E In All
            If E.Name.ToLowerInvariant().Contains(K) Then Return E
        Next
        Return Nothing
    End Function

    ''' <summary>切换实例（instance.select）+ 启动（launch）；名字留空表示启动当前实例。</summary>
    Private Function SelectAndLaunch(Name As String) As String
        Dim Want As String = If(Name, "").Trim()
        If Want.Length > 0 Then
            Dim Cur As PCL.PluginInstanceEntry = Nothing
            Try
                Cur = Host.SelectedInstance()
            Catch ex As Exception
            End Try
            If Cur Is Nothing OrElse Not String.Equals(Cur.Name, Want, StringComparison.OrdinalIgnoreCase) Then
                Dim Hit = FindInstEntry(Want)
                If Hit Is Nothing Then Return "没找到匹配「" & Want & "」的实例。可用的是：" & InstanceListText()
                If Not Host.SelectInstance(Hit.Name) Then Return "切换到「" & Hit.Name & "」失败（宿主返回 False）。"
                Want = Hit.Name
            End If
        End If
        Dim OkL As Boolean = Host.LaunchGame()
        Dim M As String = If(OkL, "已交给启动器开始启动", "启动器返回 False（可能有其它任务在跑，或实例配置不完整）")
        Host.LogInfo("AI 工具启动：" & If(Want.Length > 0, Want, "当前实例") & " → " & M)
        Return If(Want.Length > 0, Want & " → ", "") & M
    End Function

    ''' <summary>只切换当前实例，不启动。</summary>
    Private Function SelectOnly(Name As String) As String
        Dim Hit = FindInstEntry(Name)
        If Hit Is Nothing Then Return "没找到匹配「" & Name & "」的实例。可用的是：" & InstanceListText()
        If Not Host.SelectInstance(Hit.Name) Then Return "切换到「" & Hit.Name & "」失败（宿主返回 False）。"
        Host.LogInfo("AI 工具 select_instance → " & Hit.Name)
        Return "已把当前实例切到「" & Hit.Name & "」（不会自动启动；要启动请再调 launch_instance）。"
    End Function

    ' ==================== JSON ====================

    Private Function JsonStr(S As String) As String
        Dim Sb As New StringBuilder()
        Sb.Append(ChQuote)
        For Each C In If(S, "")
            Dim Code As Integer = AscW(C)
            If Code = 34 Then
                Sb.Append(ChBack).Append(ChQuote)
            ElseIf Code = 92 Then
                Sb.Append(ChBack).Append(ChBack)
            ElseIf Code = 10 Then
                Sb.Append(ChBack).Append(ChN)
            ElseIf Code = 13 Then
                Sb.Append(ChBack).Append(ChR)
            ElseIf Code = 9 Then
                Sb.Append(ChBack).Append(ChT)
            ElseIf Code < 32 Then
                Sb.Append(ChBack).Append(ChU).Append(Code.ToString("x4"))
            Else
                Sb.Append(C)
            End If
        Next
        Sb.Append(ChQuote)
        Return Sb.ToString()
    End Function

    Private Function BuildRequestJson(Optional Extra As List(Of String) = Nothing, Optional ToolChoice As String = "auto") As String
        Dim Sb As New StringBuilder()
        Sb.Append("{").Append(JsonStr("model")).Append(":").Append(JsonStr(ModelName))
        Sb.Append(",").Append(JsonStr("stream")).Append(":true")
        Sb.Append(",").Append(JsonStr("stream_options")).Append(":{").Append(JsonStr("include_usage")).Append(":true}")
        Sb.Append(",").Append(JsonStr("temperature")).Append(":0.3")
        Sb.Append(",").Append(JsonStr("tools")).Append(":").Append(ToolsJson())
        Sb.Append(",").Append(JsonStr("tool_choice")).Append(":").Append(JsonStr(ToolChoice))
        Dim TopTxt As String = ServerSearchTopJson()
        If TopTxt IsNot Nothing Then Sb.Append(TopTxt)
        ' 记下这次带没带服务端联网工具：接口不认时要靠它决定降级重试
        ServerSearchInjected = (ServerSearchToolJson() IsNot Nothing) OrElse (ServerSearchTopJson() IsNot Nothing)
        Sb.Append(",").Append(JsonStr("messages")).Append(":[")

        Dim Src As New List(Of ChatMessage)
        For Each M In Messages
            If M.Role = "assistant" AndAlso String.IsNullOrWhiteSpace(M.Content) Then Continue For
            Src.Add(M)
        Next

        Dim First As Boolean = True
        Dim Sys As String = EffectiveSysPrompt()
        If Not String.IsNullOrWhiteSpace(Sys) Then
            Sb.Append("{").Append(JsonStr("role")).Append(":").Append(JsonStr("system"))
            Sb.Append(",").Append(JsonStr("content")).Append(":").Append(JsonStr(Sys)).Append("}")
            First = False
        End If

        Dim StartIdx As Integer = Math.Max(0, Src.Count - 20)
        For I As Integer = StartIdx To Src.Count - 1
            If Not First Then Sb.Append(",")
            Sb.Append("{").Append(JsonStr("role")).Append(":").Append(JsonStr(Src(I).Role))
            Sb.Append(",").Append(JsonStr("content")).Append(":").Append(JsonStr(Src(I).Content)).Append("}")
            First = False
        Next

        If Extra IsNot Nothing Then
            For Each Raw In Extra
                Sb.Append(",").Append(Raw)
            Next
        End If

        Sb.Append("]}")
        Return Sb.ToString()
    End Function

    ''' <summary>
    ''' 接口拒绝「服务端联网工具」时的典型报错：4xx 且 body 里提到 tool / unknown / unsupported / invalid。
    ''' 宁可只在这种情况降级——正常 429、超时不该把联网功能永久关掉。
    ''' </summary>
    Private Function LooksLikeSearchReject(Msg As String) As Boolean
        If String.IsNullOrEmpty(Msg) Then Return False
        If Msg.IndexOf("HTTP 400", StringComparison.OrdinalIgnoreCase) < 0 AndAlso
           Msg.IndexOf("HTTP 422", StringComparison.OrdinalIgnoreCase) < 0 Then Return False
        Dim L As String = Msg.ToLowerInvariant()
        Return L.Contains("tool") OrElse L.Contains("unknown") OrElse L.Contains("unsupported") OrElse
               L.Contains("invalid") OrElse L.Contains("web_search") OrElse L.Contains("not support")
    End Function

    ''' <summary>给模型的工具清单（OpenAI 兼容 function calling）。</summary>
    Private Function ToolsJson() As String
        Dim S As String = "[" &
            ToolDef("list_instances", "列出启动器里所有可启动的游戏实例（版本）。需要知道有哪些版本时先调这个。", "{}") & "," &
            ToolDef("instance_info", "查看启动器当前选中的游戏实例信息：名称、版本、加载器、目录、是否可装模组（只读）。", "{}") & "," &
            ToolDef2("launch_instance", "启动游戏。传 name 会先把当前实例切过去（instance.select）再启动；不传就启动当前实例。会真的把游戏跑起来，用户没明确要求启动时不要调。",
                     "{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""实例名（可省略，可用片段），省略表示启动当前实例""}}}") & "," &
            ToolDef2("select_instance", "只切换当前选中的游戏实例（会影响之后「启动游戏」用哪个版本），不会启动。",
                     "{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""实例名或片段""}},""required"":[""name""]}") & "," &
            ToolDef2("append_jvm_args", "给下一次启动追加 JVM 参数（本次会话有效）。",
                     "{""type"":""object"",""properties"":{""args"":{""type"":""string"",""description"":""要追加的参数，如 -Xmx4G -XX:+UseG1GC""}},""required"":[""args""]}") & "," &
            ToolDef2("append_game_args", "给下一次启动追加游戏参数（本次会话有效）。",
                     "{""type"":""object"",""properties"":{""args"":{""type"":""string"",""description"":""要追加的游戏参数""}},""required"":[""args""]}") & "," &
            ToolDef("get_pending_args", "查看当前已追加的 JVM 参数和游戏参数。", "{}") & "," &
            ToolDef("list_plugins", "列出启动器里已安装的插件及启用状态（只读）。", "{}") & "," &
            ToolDef2("set_plugin_enabled", "启用或禁用某个插件。注意：启动器会重载插件，包括本插件自己，聊天可能被打断；不能操作本插件。",
                     "{""type"":""object"",""properties"":{""id"":{""type"":""string"",""description"":""插件 id，先用 list_plugins 查""},""enabled"":{""type"":""boolean"",""description"":""true 启用 / false 禁用""}},""required"":[""id"",""enabled""]}") & "," &
            ToolDef2("list_mods", "列出当前实例（或指定实例）的 mods / resourcepacks / shaderpacks 内容，含启用状态（只读）。",
                     "{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""mods"",""resourcepacks"",""shaderpacks""],""description"":""默认 mods""},""instance"":{""type"":""string"",""description"":""实例名，默认当前实例""}}}") & "," &
            ToolDef2("set_mod_enabled", "启用或禁用某个模组/资源包/光影（只是把文件名改成 .disabled，可逆）。用户没明确要求时不要调。",
                     "{""type"":""object"",""properties"":{""file"":{""type"":""string"",""description"":""FileName，来自 list_mods，不要带 .disabled""},""enabled"":{""type"":""boolean"",""description"":""true 启用 / false 禁用""},""kind"":{""type"":""string"",""enum"":[""mods"",""resourcepacks"",""shaderpacks""],""description"":""默认 mods""},""instance"":{""type"":""string"",""description"":""实例名，默认当前实例""}},""required"":[""file"",""enabled""]}") & "," &
            ToolDef2("read_log", "读当前实例的游戏日志（只读，最多 200 行）。排查游戏崩溃/模组报错时用这个；启动器自己的日志不对模型开放。",
                     "{""type"":""object"",""properties"":{""lines"":{""type"":""integer"",""description"":""要读多少行，默认 60，上限 200""}}}") & "," &
            ToolDef2("global_setting", "读取启动器全局设置里某一项的值（只读，如 UiLanguage、UiTheme 之类，键名不确定就先问用户）。",
                     "{""type"":""object"",""properties"":{""key"":{""type"":""string"",""description"":""设置项键名""}},""required"":[""key""]}") & "," &
            ToolDef2("write_note", "把一段文本存成笔记文件。只能写进本插件的 notes 目录，文件名限字母数字下划线连字符，扩展名 .md 或 .txt，内容上限 64 KB。",
                     "{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""文件名，如 今天的问题.md""},""text"":{""type"":""string"",""description"":""正文""}},""required"":[""name"",""text""]}") & "," &
            ToolDef2("open_url", "用系统浏览器打开一个 http/https 链接（不能是 file: 或其他协议）。",
                     "{""type"":""object"",""properties"":{""url"":{""type"":""string"",""description"":""完整网址""}},""required"":[""url""]}") & "," &
            ToolDef2("search_modrinth", "在 Modrinth 上搜索模组/资源包/光影/数据包。返回标题、slug、下载量与简介；下载时把 slug 传给 download_modrinth。",
                     "{""type"":""object"",""properties"":{" &
                     """query"":{""type"":""string"",""description"":""搜索关键词，例如 sodium、iris、fabric api""}," &
                     """project_type"":{""type"":""string"",""enum"":[""mod"",""resourcepack"",""shader"",""datapack"",""modpack""],""description"":""默认 mod""}," &
                     """loader"":{""type"":""string"",""description"":""fabric / forge / neoforge / quilt，可省略""}," &
                     """game_version"":{""type"":""string"",""description"":""例如 1.20.1，可省略""}," &
                     """limit"":{""type"":""integer"",""description"":""返回条数，默认 5，最多 10""}}," &
                     """required"":[""query""]}") & "," &
            ToolDef2("download_modrinth", "从 Modrinth 下载一个项目（会自动挑最新的、匹配加载器/游戏版本的版本文件），默认由启动器直接下载进当前实例的对应目录并做 SHA-1 校验；install=false 时只存到 data\downloads。装之前先用 instance_info 确认实例。",
                     "{""type"":""object"",""properties"":{" &
                     """project"":{""type"":""string"",""description"":""search_modrinth 返回的 slug 或 project id""}," &
                     """project_type"":{""type"":""string"",""enum"":[""mod"",""resourcepack"",""shader"",""datapack"",""modpack""],""description"":""决定装进哪个目录，默认 mod""}," &
                     """loader"":{""type"":""string"",""description"":""fabric / forge / neoforge / quilt，可省略""}," &
                     """game_version"":{""type"":""string"",""description"":""例如 1.20.1，可省略""}," &
                     """instance_name"":{""type"":""string"",""description"":""仅整合包用：新建实例的名字，留空则用项目名""}," &
                     """install"":{""type"":""boolean"",""description"":""是否装进当前实例，默认 true；false 则只下载""}}," &
                     """required"":[""project""]}") & "," &
            ToolDef("check_curseforge", "检查 CurseForge 通道（国内镜像 / 官方接口）现在通不通。用户说「CurseForge 不行」「搜不到东西」时先跑这个再下结论。", "{}") & "," &
            ToolDef2("search_curseforge",
                     "在 CurseForge 上搜索模组/资源包/光影/数据包。返回名称、数字 id、slug、下载量与简介。" &
                     "走启动器同款国内镜像，不需要 API Key。modrinth 搜不到、或用户明确要 CurseForge 的东西时用它。",
                     "{""type"":""object"",""properties"":{" &
                     """query"":{""type"":""string"",""description"":""搜索关键词""}," &
                     """project_type"":{""type"":""string"",""enum"":[""mod"",""resourcepack"",""shader"",""datapack"",""modpack""],""description"":""默认 mod""}," &
                     """loader"":{""type"":""string"",""description"":""forge / fabric / neoforge / quilt，可省略""}," &
                     """game_version"":{""type"":""string"",""description"":""例如 1.20.1，可省略""}," &
                     """limit"":{""type"":""integer"",""description"":""返回条数，默认 5，最多 10""}}," &
                     """required"":[""query""]}") & "," &
            ToolDef2("download_curseforge",
                     "从 CurseForge 下载一个项目并（默认）装进当前实例——由启动器直接写进实例目录并做 SHA-1 校验。自动挑最新的、匹配加载器/游戏版本的正式版文件。" &
                     "project 可以是 search_curseforge 返回的数字 id、slug、项目链接；给带 /files/12345 的文件页链接时会精确下那个文件，也可以用 file_id 指定。",
                     "{""type"":""object"",""properties"":{" &
                     """project"":{""type"":""string"",""description"":""数字 id / slug / curseforge.com 项目或文件链接""}," &
                     """file_id"":{""type"":""string"",""description"":""可选。指定 CurseForge 文件 id，留空取最新正式版""}," &
                     """project_type"":{""type"":""string"",""enum"":[""mod"",""resourcepack"",""shader"",""datapack"",""modpack""],""description"":""决定装进哪个目录，默认 mod""}," &
                     """loader"":{""type"":""string"",""description"":""forge / fabric / neoforge / quilt，可省略""}," &
                     """game_version"":{""type"":""string"",""description"":""例如 1.20.1，可省略""}," &
                     """instance_name"":{""type"":""string"",""description"":""仅整合包用：新建实例的名字，留空则用项目名""}," &
                     """install"":{""type"":""boolean"",""description"":""是否装进当前实例，默认 true；false 则只下载""}}," &
                     """required"":[""project""]}") & "," &
            ToolDef2("download_mods_batch",
                     "一次批量下载并安装多个项目（模组/资源包/光影/数据包），最多 20 个。" &
                     "用户要装一堆东西时优先用它，不要一个一个调 download_modrinth——每次单独调用都会占掉一轮工具调用额度。" &
                     "已知 slug 就放 projects；只知道名字/关键词就放 queries（自动取搜索结果第一条，可能不是最想要的，所以要跟用户说明）。" &
                     "source 默认 modrinth，用户要 CurseForge 的内容时传 curseforge（走国内镜像，不需要 key）。" &
                     "逐条处理，单个失败不影响其他项，最后返回每一条的结果。下载是同步的，文件多会比较慢。",
                     "{""type"":""object"",""properties"":{" &
                     """source"":{""type"":""string"",""enum"":[""modrinth"",""curseforge""],""description"":""内容来源，默认 modrinth""}," &
                     """projects"":{""type"":""array"",""items"":{""type"":""string""},""description"":""已知的 slug 或 project id 列表""}," &
                     """queries"":{""type"":""array"",""items"":{""type"":""string""},""description"":""关键词列表，插件内部搜一次取第一条""}," &
                     """project_type"":{""type"":""string"",""enum"":[""mod"",""resourcepack"",""shader"",""datapack"",""modpack""],""description"":""这批东西的类型，默认 mod""}," &
                     """loader"":{""type"":""string"",""description"":""fabric / forge / neoforge / quilt，可省略""}," &
                     """game_version"":{""type"":""string"",""description"":""例如 1.20.1，可省略""}," &
                     """install"":{""type"":""boolean"",""description"":""是否装进当前实例，默认 true""}}," &
                     """required"":[]}") & "," &
            ToolDef2("download_file", "按直链下载任意文件（http/https）。install 留空时存到本插件的 data\downloads；填 mods / resourcepacks / shaderpacks / datapacks 时由启动器直接下载进当前实例并校验（不需要再复制）；传 modpack 则先下载再交给启动器安装整合包（会新建实例）。",
                     "{""type"":""object"",""properties"":{" &
                     """url"":{""type"":""string"",""description"":""http/https 直链，必须以 .jar/.zip 等文件结尾或是下载接口""}," &
                     """filename"":{""type"":""string"",""description"":""保存的文件名，可省略（从地址推断）""}," &
                     """install"":{""type"":""string"",""enum"":["""",""mods"",""resourcepacks"",""shaderpacks"",""datapacks"",""modpack""],""description"":""留空只下载；整合包传 modpack""}}," &
                     """required"":[""url""]}") & "," &
            ToolDef2("install_download", "把 data\downloads 里已经下好的文件装进当前实例的指定目录；如果文件是整合包（.mrpack，或 zip 里带 manifest.json / modrinth.index.json 等），传 folder=modpack 就会交给启动器安装（API v9 的宿主安装流程），新建一个实例——和用户把整合包拖进启动器完全一样；工具会先报出识别到的格式、包名、MC 版本、加载器和文件数。",
                     "{""type"":""object"",""properties"":{" &
                     """file"":{""type"":""string"",""description"":""list_downloads 里的文件名""}," &
                     """folder"":{""type"":""string"",""enum"":[""mods"",""resourcepacks"",""shaderpacks"",""datapacks"",""modpack""],""description"":""默认 mods；整合包用 modpack""}," &
                     """instance_name"":{""type"":""string"",""description"":""整合包新建实例的名字，可省略（默认用项目名）""}," &
                     """project"":{""type"":""string"",""description"":""可选。整合包对应的项目 slug / id，填了才能补上封面和默认名字""}," &
                     """source"":{""type"":""string"",""enum"":[""modrinth"",""curseforge""],""description"":""project 属于哪个站，默认 modrinth""}}," &
                     """required"":[""file""]}") & "," &
            ToolDef("list_downloads", "列出 data\downloads 里已经下载的文件（名字、大小、时间）。", "{}") & "," &
            ToolDef2("install_vanilla", "安装原版 Minecraft：取官方版本清单 → 写入 versions\<版本>\<版本>.json → 下载客户端 jar。缺失的 libraries 与 assets 不用这里下，启动器每次启动前会自动补全。用户说「装个原版 / 下载 1.20.1 / 安装最新正式版」时用这个；装完要提醒用户回启动页刷新版本列表再点启动。",
                     "{""type"":""object"",""properties"":{" &
                     """version"":{""type"":""string"",""description"":""版本号，例如 1.20.1、1.21.8；传 latest 表示最新正式版""}}," &
                     """required"":[""version""]}") & "," &
            ToolDef2("list_loaders",
                     "列出某个 Minecraft 版本能装哪些 Mod 加载器版本（Forge / NeoForge / OptiFine / Fabric）。" &
                     "用户问「能装哪些 Forge」「OptiFine 哪个版本」「Fabric 最新是几」时用它；不查也能直接装（默认取最新）。",
                     "{""type"":""object"",""properties"":{" &
                     """mc_version"":{""type"":""string"",""description"":""要装加载器的 Minecraft 版本，例如 1.20.1""}," &
                     """loader"":{""type"":""string"",""enum"":[""all"",""forge"",""neoforge"",""optifine"",""fabric""],""description"":""留空或 all 表示全部列出""}}," &
                     """required"":[""mc_version""]}") & "," &
            ToolDef2("install_loader",
                     "安装 Mod 加载器（Forge / NeoForge / OptiFine / Fabric），走启动器自己的安装流程：" &
                     "下载 installer、执行处理器、合并版本 json、补全支持库，进度显示在启动器右下角任务栏，和用户手动下载完全一样。" &
                     "会自动带上原版。OptiFine 同时给 optifine_version 和 forge 时，OptiFine 会作为 mod 装进 mods 目录。" &
                     "这是异步操作：调用后立刻返回「已开始」，要告诉用户去右下角看进度，不要声称已经装完。",
                     "{""type"":""object"",""properties"":{" &
                     """mc_version"":{""type"":""string"",""description"":""原版版本，例如 1.20.1""}," &
                     """loader"":{""type"":""string"",""enum"":[""forge"",""neoforge"",""optifine"",""fabric""],""description"":""要装的加载器""}," &
                     """loader_version"":{""type"":""string"",""description"":""加载器版本号，留空取最新；OptiFine 这里填 1.20.1_HD_U_I6 这种文件名片段""}," &
                     """optifine_version"":{""type"":""string"",""description"":""可选。装 Forge 时顺带装 OptiFine，填 OptiFine 的版本片段""}," &
                     """instance_name"":{""type"":""string"",""description"":""可选。新版本文件夹名，留空自动生成""}}," &
                     """required"":[""mc_version"",""loader""]}") & "," &
            ToolDef("open_download_page", "把启动器切到「下载」页面（用户可以在里面自己挑版本、整合包、资源包）。install_vanilla 失败、或用户想手动选内容时用。", "{}") & "," &
            ToolDef("read_conventions", "读用户写在 agent.md 里的长期约定（偏好、规矩）。用户问「你记得我什么约定」「我的规矩是什么」时用，不要凭印象答。", "{}") & "," &
            ToolDef2("remember_convention",
                     "把一条长期约定写进 agent.md（跨会话、跨重启都有效）。只在用户明确要求「记住 / 以后都这样 / 下次别再问」时调用；" &
                     "写完把这条复述给用户。写的是对以后行为的要求，不要拿它存普通笔记（存笔记用 write_note）。",
                     "{""type"":""object"",""properties"":{""text"":{""type"":""string"",""description"":""要记住的一条规矩，一句话，例如：装光影前先把候选念给我确认""}},""required"":[""text""]}") & "," &
            ToolDef2("search_mcmod",
                     "在 MC百科（mcmod.cn）搜模组 / 物品 / 方块 / 教程，返回标题、链接和摘要。" &
                     "用户用中文问「这个模组是干嘛的 / 什么版本能用 / 有什么前置 / 这个物品怎么获得」时优先用它：中文资料和适用版本比 Modrinth 全，也更容易对上国内玩家的叫法。" &
                     "它只给资料，没有下载地址；要下载还是走 search_modrinth / search_curseforge。" &
                     "需要正文细节时拿返回的链接调 fetch_url 抓条目页。",
                     "{""type"":""object"",""properties"":{" &
                     """query"":{""type"":""string"",""description"":""关键词，中文或英文都行，例如 机械动力 / jei / 暮色森林""}," &
                     """limit"":{""type"":""integer"",""description"":""最多返回几条，默认 8，上限 20""}}," &
                     """required"":[""query""]}") & "," &
            ToolDef2("fetch_url",
                     "抓一个网页或 JSON 接口的正文来读（不是给用户开浏览器）。适合查模组说明、MC wiki、GitHub 的 README / issue、版本清单。" &
                     "返回的是去标签后的纯文本，默认只给前 6000 字。白名单外的站会弹窗问用户要不要放行，被拒绝就换站，不要反复试。",
                     "{""type"":""object"",""properties"":{" &
                     """url"":{""type"":""string"",""description"":""http/https 完整地址""}," &
                     """max_chars"":{""type"":""integer"",""description"":""最多返回多少字，默认 6000，最大 15000""}},""required"":[""url""]}") & "," &
            ToolDef2("mods_scan",
                     "扫描当前实例 mods 目录里每个 jar 的元数据：真实 modid、名称、版本、加载器、声明的前置依赖；" &
                     "并检查「缺前置」和「同一个 modid 装了多份」。用户问装了哪些 mod、有没有冲突、缺什么前置、为什么崩的时候用这个，比 list_mods 详细得多。",
                     "{""type"":""object"",""properties"":{},""required"":[]}") & "," &
            ToolDef2("diagnose_crash",
                     "一次性收集诊断材料：实例信息、最新崩溃报告（前 160 行）、日志尾部 120 行、mods 数量与清单。" &
                     "用户说「崩了 / 起不来 / 报错」时先调这个，再基于原文分析，不要凭空猜原因。",
                     "{""type"":""object"",""properties"":{},""required"":[]}") & "," &
            ToolDef2("read_instance_file",
                     "读当前实例目录里的文本文件（options.txt、config\*.toml、saves 里的 level.dat 不行，但 .snbt/.json 可以）。" &
                     "path 留空则返回实例目录概览（有哪些文件、config 里几项、有几个存档）。只能读，不能写，不能越出实例目录。",
                     "{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""相对实例目录的路径，如 options.txt / config/xxx-common.toml；留空看概览""}}}")

        ' 服务端联网工具（GLM / Kimi）与本地搜索工具（DeepSeek）都拼在最后
        Dim Tail As New StringBuilder()
        Dim Srv As String = ServerSearchToolJson()
        If Srv IsNot Nothing Then Tail.Append(",").Append(Srv)
        Dim Loc As String = LocalSearchToolJson()
        If Loc IsNot Nothing Then Tail.Append(",").Append(Loc)
        If Tail.Length > 0 Then S = S & Tail.ToString()
        Return S & "]"
    End Function

    Private Function ToolDef(Name As String, Desc As String, Params As String) As String
        Return ToolDef2(Name, Desc, Params)
    End Function

    Private Function ToolDef2(Name As String, Desc As String, Params As String) As String
        Return "{" & JsonStr("type") & ":" & JsonStr("function") & "," & JsonStr("function") & ":{" &
               JsonStr("name") & ":" & JsonStr(Name) & "," &
               JsonStr("description") & ":" & JsonStr(Desc) & "," &
               JsonStr("parameters") & ":" & Params & "}}"
    End Function

    ''' <summary>从一段 SSE JSON 里抠出 delta.content（没有就返回 Nothing）。</summary>
    Private Function ExtractContent(Json As String) As String
        Return JsonStringField(Json, "content")
    End Function

    ''' <summary>从一个 JSON 片段里取某个键的字符串值；没有该键就返回 Nothing。</summary>
    Private Function JsonStringField(Json As String, KeyName As String) As String
        Dim Key As String = """" & KeyName & """:"
        Dim I As Integer = Json.IndexOf(Key, StringComparison.Ordinal)
        If I < 0 Then Return Nothing
        I += Key.Length
        While I < Json.Length AndAlso (Json(I) = Convert.ToChar(32) OrElse Json(I) = Convert.ToChar(9))
            I += 1
        End While
        If I >= Json.Length OrElse Json(I) <> ChQuote Then Return Nothing
        I += 1

        Dim Sb As New StringBuilder()
        While I < Json.Length
            Dim C As Char = Json(I)
            If C = ChQuote Then Exit While
            If C = ChBack AndAlso I + 1 < Json.Length Then
                I += 1
                Dim N As Char = Json(I)
                If N = ChN Then
                    Sb.Append(Convert.ToChar(10))
                ElseIf N = ChT Then
                    Sb.Append(Convert.ToChar(9))
                ElseIf N = ChR Then
                    Sb.Append(Convert.ToChar(13))
                ElseIf N = ChB Then
                    Sb.Append(Convert.ToChar(8))
                ElseIf N = ChF Then
                    Sb.Append(Convert.ToChar(12))
                ElseIf N = ChU Then
                    If I + 4 < Json.Length Then
                        Dim Hex As String = Json.Substring(I + 1, 4)
                        Dim Code As Integer
                        If Integer.TryParse(Hex, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, Code) Then
                            Sb.Append(ChrW(Code))
                        End If
                        I += 4
                    End If
                Else
                    Sb.Append(N)
                End If
            Else
                Sb.Append(C)
            End If
            I += 1
        End While
        Return Sb.ToString()
    End Function

    Private Function Shorten(S As String, Max As Integer) As String
        If String.IsNullOrEmpty(S) Then Return ""
        Dim T = S.Replace(Environment.NewLine, " ")
        If T.Length <= Max Then Return T
        Return T.Substring(0, Max) & "…"
    End Function

    ' ==================== 工具调用 ====================

    ''' <summary>
    ''' 流式下的 tool_calls 是「按 index 分片」推送的：name 只在第一片出现，arguments 分多片拼。
    ''' 这里按 "index": 切段，把 name/arguments/id 累积到 ToolCalls 对应槽位。
    ''' </summary>
    Private Sub ReadToolDeltas(Json As String)
        Dim I As Integer = Json.IndexOf("""tool_calls"":", StringComparison.Ordinal)
        If I < 0 Then Return
        Dim P As Integer = Json.IndexOf("""index"":", I, StringComparison.Ordinal)
        While P >= 0
            Dim Slot As Integer = ReadNumberAfter(Json, P + 8)
            Dim NextP As Integer = Json.IndexOf("""index"":", P + 8, StringComparison.Ordinal)
            Dim Seg As String = If(NextP < 0, Json.Substring(P), Json.Substring(P, NextP - P))

            While ToolCalls.Count <= Slot
                ToolCalls.Add(New ToolCallReq())
            End While
            Dim Tc As ToolCallReq = ToolCalls(Slot)

            Dim Id As String = JsonStringField(Seg, "id")
            If Id IsNot Nothing AndAlso Id.Length > 0 Then Tc.Id = Id
            Dim Nm As String = JsonStringField(Seg, "name")
            If Nm IsNot Nothing AndAlso Nm.Length > 0 Then Tc.Name = Nm
            Dim Kd As String = JsonStringField(Seg, "type")
            If Kd IsNot Nothing AndAlso Kd.Length > 0 Then Tc.Kind = Kd
            Dim Aa As String = JsonStringField(Seg, "arguments")
            If Aa IsNot Nothing Then Tc.Args.Append(Aa)

            P = NextP
        End While
    End Sub

    ''' <summary>拼一条 assistant 消息（带 tool_calls）给下一轮请求用。</summary>
    Private Function ToolCallsMessageJson() As String
        Dim Sb As New StringBuilder()
        Sb.Append("{").Append(JsonStr("role")).Append(":").Append(JsonStr("assistant"))
        Sb.Append(",").Append(JsonStr("content")).Append(":null")
        Sb.Append(",").Append(JsonStr("tool_calls")).Append(":[")
        Dim First As Boolean = True
        For Each Tc In ToolCalls
            If Not First Then Sb.Append(",")
            Sb.Append("{").Append(JsonStr("id")).Append(":").Append(JsonStr(Tc.Id))
            Sb.Append(",").Append(JsonStr("type")).Append(":").Append(JsonStr(If(String.IsNullOrEmpty(Tc.Kind), "function", Tc.Kind)))
            Sb.Append(",").Append(JsonStr("function")).Append(":{")
            Sb.Append(JsonStr("name")).Append(":").Append(JsonStr(Tc.Name))
            Sb.Append(",").Append(JsonStr("arguments")).Append(":").Append(JsonStr(Tc.Args.ToString()))
            Sb.Append("}}")
            First = False
        Next
        Sb.Append("]}")
        Return Sb.ToString()
    End Function

    Private Function ToolResultMessageJson(Id As String, Result As String) As String
        Return "{" & JsonStr("role") & ":" & JsonStr("tool") & "," &
               JsonStr("tool_call_id") & ":" & JsonStr(Id) & "," &
               JsonStr("content") & ":" & JsonStr(Result) & "}"
    End Function

    ''' <summary>执行一个工具，返回给模型的文本结果（永远不抛，失败也回一条说明）。</summary>
    ''' <summary>
    ''' 工具执行入口。带一层「同参数失败重试」保护：模型偶尔会对必败的调用反复重试
    ''' （实测有一轮连试 10 次同样的 search_modrinth），把工具轮次白白烧光。
    ''' 同一个工具 + 完全相同的参数失败两次后，第三次直接拦下并提示换参数。
    ''' </summary>
    Private Function ExecTool(Name As String, ArgsJson As String) As String
        Dim Key As String = If(Name, "").Trim().ToLowerInvariant() & "|" & If(ArgsJson, "").Trim()
        Dim N As Integer = 0
        If FailSeen.ContainsKey(Key) Then N = FailSeen(Key)
        If N >= 2 Then
            Return "（已拦截）这个工具用完全相同的参数已经失败 " & N & " 次，再试结果不会变。" &
                   "请换参数（例如去掉 loader / game_version 限制、换个关键词），或者直接告诉用户现在做不了以及原因。"
        End If
        Dim R As String = ExecToolCore(Name, ArgsJson)
        If LooksLikeToolError(R) Then
            FailSeen(Key) = N + 1
        Else
            FailSeen.Remove(Key)
        End If
        Return R
    End Function

    ''' <summary>工具返回文本里出现这些词就认为是失败（用于上面的重复调用拦截，不追求精确）。</summary>
    Private Shared ReadOnly ErrorMarks As String() = {
        "失败", "错误", "拒绝", "不可用", "缺少", "没有搜到", "超时", "未找到", "无法", "不存在", "已达上限"
    }

    Private Function LooksLikeToolError(R As String) As Boolean
        If String.IsNullOrEmpty(R) Then Return False
        For Each K In ErrorMarks
            If R.IndexOf(K, StringComparison.Ordinal) >= 0 Then Return True
        Next
        Return False
    End Function

    Private Function ExecToolCore(Name As String, ArgsJson As String) As String
        Dim Op As String = If(Name, "").Trim().ToLowerInvariant()

        ' 把内容下载和游戏安装强制拆成前后两步：后台安装没落地就先不下东西
        Dim TouchesInstance As Boolean =
            Op = "download_modrinth" OrElse Op = "download_curseforge" OrElse Op = "install_download"
        If Op = "download_mods_batch" Then
            Dim InsFlag As Boolean? = JsonBoolField(ArgsJson, "install")
            TouchesInstance = Not (InsFlag.HasValue AndAlso InsFlag.Value = False)
        ElseIf Op = "download_file" Then
            TouchesInstance = If(JsonStringField(ArgsJson, "install"), "").Trim().Length > 0
        End If

        If TouchesInstance Then
            Dim G As String = InstallGuard()
            If G.Length > 0 Then Return G
        End If

        Dim R As String = ExecToolCoreInner(Op, ArgsJson)

        If TouchesInstance Then
            Dim H As String = InstanceMismatchHint()
            If H.Length > 0 Then R = H & Environment.NewLine & R
        End If
        Return R
    End Function

    Private Function ExecToolCoreInner(Name As String, ArgsJson As String) As String
        Try
            Select Case If(Name, "").Trim().ToLowerInvariant()
                Case "list_instances"
                    Return InstanceListText()
                Case "instance_info"
                    Dim I = Host.InstanceInfo
                    If I Is Nothing OrElse Not I.Available Then Return "当前没有选中的游戏实例。"
                    Dim Sb0 As New StringBuilder()
                    Sb0.Append("名称：").Append(I.Name).Append("；版本：").Append(I.VersionId)
                    If Not String.IsNullOrWhiteSpace(I.InheritName) Then Sb0.Append("；继承自：").Append(I.InheritName)
                    Sb0.Append("；可装模组：").Append(If(I.Modable, "是", "否"))
                    If Not String.IsNullOrWhiteSpace(I.PathIndie) Then Sb0.Append("；目录：").Append(I.PathIndie)
                    Return Sb0.ToString()
                Case "global_setting"
                    Dim Gk As String = If(JsonStringField(ArgsJson, "key"), "").Trim()
                    If Gk.Length = 0 Then Return "缺少 key 参数。"
                    Dim Gv As String = Host.GetGlobalSetting(Gk)
                    If Gv Is Nothing Then Return "没有名为「" & Gk & "」的全局设置。"
                    If Gv.Trim().Length = 0 Then Return Gk & " = （空，未设置）"
                    Return Gk & " = " & Gv
                Case "launch_instance"
                    Dim Nm As String = If(JsonStringField(ArgsJson, "name"), "").Trim()
                    Return SelectAndLaunch(Nm)
                Case "select_instance"
                    Dim Nm2 As String = If(JsonStringField(ArgsJson, "name"), "").Trim()
                    If Nm2.Length = 0 Then Return "缺少 name 参数。"
                    Return SelectOnly(Nm2)
                Case "append_jvm_args"
                    Dim A As String = JsonStringField(ArgsJson, "args")
                    If String.IsNullOrWhiteSpace(A) Then Return "缺少 args 参数。"
                    Host.AppendJvmArgs(A.Trim())
                    Return "已追加 JVM 参数：" & A.Trim()
                Case "append_game_args"
                    Dim A As String = JsonStringField(ArgsJson, "args")
                    If String.IsNullOrWhiteSpace(A) Then Return "缺少 args 参数。"
                    Host.AppendGameArgs(A.Trim())
                    Return "已追加游戏参数：" & A.Trim()
                Case "get_pending_args"
                    Dim J As String = JoinList(Host.ExtraJvmArgs)
                    Dim G As String = JoinList(Host.ExtraGameArgs)
                    Return "JVM 参数：" & J & "；游戏参数：" & G
                Case "list_plugins"
                    Return PluginListText()
                Case "set_plugin_enabled"
                    Return ToolSetPluginEnabled(ArgsJson)
                Case "list_mods"
                    Return ModsListText(ArgsJson)
                Case "set_mod_enabled"
                    Return ToolSetMod(ArgsJson)
                Case "read_log"
                    Return ToolReadLog(ArgsJson)
                Case "write_note"
                    Return ToolWriteNote(ArgsJson)
                Case "open_url"
                    Return ToolOpenUrl(ArgsJson)
                Case "search_modrinth"
                    Return ToolSearchModrinth(ArgsJson)
                Case "download_modrinth"
                    Return ToolDownloadModrinth(ArgsJson)
                Case "search_curseforge"
                    Return ToolSearchCurseForge(ArgsJson)
                Case "check_curseforge"
                    Return ToolCheckCurseForge()
                Case "download_curseforge"
                    Return ToolDownloadCurseForge(ArgsJson)
                Case "download_mods_batch"
                    Return ToolDownloadModsBatch(ArgsJson)
                Case "download_file"
                    Return ToolDownloadFile(ArgsJson)
                Case "install_download"
                    Return ToolInstallDownload(ArgsJson)
                Case "list_downloads"
                    Return ToolListDownloads()
                Case "install_vanilla"
                    Return ToolInstallVanilla(ArgsJson)
                Case "list_loaders"
                    Return ToolListLoaders(ArgsJson)
                Case "install_loader"
                    Return ToolInstallLoader(ArgsJson)
                Case "open_download_page"
                    Host.Navigate(1)
                    Return "已切到「下载」页面。如果没看到这个页签，说明提供下载页的插件（download-page）没有启用。"
                Case "read_conventions"
                    Dim Doc As String = AgentDoc()
                    If String.IsNullOrWhiteSpace(Doc) Then Return "用户没有写 agent.md 约定（文件为空或不存在），按默认规则做事就行。"
                    Return "agent.md 里的约定：" & Environment.NewLine & Doc
                Case "remember_convention"
                    Return AppendAgentLine(JsonStringField(ArgsJson, "text"))
                Case "web_search"
                    Return ToolWebSearch(ArgsJson)
                Case "fetch_url"
                    Return ToolFetchUrl(ArgsJson)
                Case "search_mcmod"
                    Return ToolSearchMcmod(ArgsJson)
                Case "mods_scan"
                    Return ToolModsScan(ArgsJson)
                Case "diagnose_crash"
                    Return ToolDiagnose(ArgsJson)
                Case "read_instance_file"
                    Return ToolReadInstanceFile(ArgsJson)
                Case Else
                    Return "未知工具：" & Name
            End Select
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "工具执行失败：" & T.Message
        End Try
    End Function

    Private Function JoinList(L As IEnumerable(Of String)) As String
        If L Is Nothing Then Return "（无）"
        Dim Parts As New List(Of String)
        For Each S In L
            If Not String.IsNullOrWhiteSpace(S) Then Parts.Add(S)
        Next
        If Parts.Count = 0 Then Return "（无）"
        Return String.Join(" ", Parts) & $"（{Parts.Count} 条）"
    End Function

    ''' <summary>笔记目录：只落在本插件自己的 data 文件夹里，是唯一允许写的目标。</summary>
    Private Function NotesDir() As String
        Return Path.Combine(Host.DataFolder, "notes")
    End Function

    ''' <summary>严格白名单：字母数字下划线中划线 + 单一扩展名 .md/.txt，不允许任何路径分隔符或多余点。</summary>
    Private Function SafeNoteName(Nm As String) As Boolean
        If String.IsNullOrWhiteSpace(Nm) Then Return False
        Nm = Nm.Trim()
        If Nm.Length < 5 OrElse Nm.Length > 48 Then Return False
        Dim Low As String = Nm.ToLowerInvariant()
        Dim Base As String
        If Low.EndsWith(".md") Then
            Base = Nm.Substring(0, Nm.Length - 3)
        ElseIf Low.EndsWith(".txt") Then
            Base = Nm.Substring(0, Nm.Length - 4)
        Else
            Return False
        End If
        If Base.Length = 0 Then Return False
        For Each C In Base
            Dim Ok As Boolean = Char.IsLetterOrDigit(C) OrElse C = Convert.ToChar(95) OrElse C = Convert.ToChar(45) OrElse AscW(C) > 127
            If Not Ok Then Return False
        Next
        If Nm.IndexOf(Convert.ToChar(92)) >= 0 OrElse Nm.IndexOf("/"c) >= 0 Then Return False
        Return True
    End Function

    Private Function JsonBoolField(Json As String, KeyName As String) As Boolean?
        Dim K As String = """" & KeyName & """:"
        Dim I As Integer = If(Json, "").IndexOf(K, StringComparison.Ordinal)
        If I < 0 Then Return Nothing
        Dim Rest As String = Json.Substring(I + K.Length).TrimStart()
        If Rest.StartsWith("true", StringComparison.OrdinalIgnoreCase) Then Return True
        If Rest.StartsWith("false", StringComparison.OrdinalIgnoreCase) Then Return False
        Return Nothing
    End Function

    Private Function JsonIntField(Json As String, KeyName As String, DefaultValue As Integer) As Integer
        Dim K As String = """" & KeyName & """:"
        Dim I As Integer = If(Json, "").IndexOf(K, StringComparison.Ordinal)
        If I < 0 Then Return DefaultValue
        Dim Rest As String = Json.Substring(I + K.Length).TrimStart()
        Dim N As Integer = 0
        Dim Got As Boolean = False
        For Each C In Rest
            If Char.IsDigit(C) Then
                N = N * 10 + (AscW(C) - AscW("0"c))
                Got = True
            Else
                Exit For
            End If
        Next
        If Got Then Return N
        Return DefaultValue
    End Function

    Private Function PluginListText() As String
        Try
            Dim All = Host.Plugins.List()
            If All Is Nothing OrElse All.Count = 0 Then Return "没有装任何插件。"
            Dim Sb As New StringBuilder()
            Sb.Append("共 ").Append(All.Count).Append(" 个插件：")
            For Each P In All
                Sb.Append(Environment.NewLine)
                Sb.Append(P.Id).Append(" | ").Append(P.Name).Append(" v").Append(P.Version)
                Sb.Append(If(P.Enabled, " | 已启用", " | 已禁用"))
                Sb.Append(If(P.Loaded, " | 已加载", " | 未加载"))
            Next
            Return Sb.ToString()
        Catch ex As Exception
            Return "读插件列表失败：" & Redact(ex.Message)
        End Try
    End Function

    ''' <summary>开关插件。防护：拒绝操作自己、拒绝不存在的 id，且必须真的在列表里查到。</summary>
    Private Function ToolSetPluginEnabled(ArgsJson As String) As String
        Dim Id As String = If(JsonStringField(ArgsJson, "id"), "").Trim()
        Dim En As Boolean? = JsonBoolField(ArgsJson, "enabled")
        If Id.Length = 0 Then Return "缺少 id 参数，先用 list_plugins 查。"
        If Not En.HasValue Then Return "缺少 enabled 参数（true/false）。"
        If Id.Equals("aiplus", StringComparison.OrdinalIgnoreCase) Then Return "拒绝：不能开关本插件自己（会把对话打断）。"
        Dim Target As Object = Nothing
        Try
            Target = Host.Plugins.Find(Id)
        Catch ex As Exception
        End Try
        If Target Is Nothing Then Return "没有 id 为「" & Id & "」的插件。现有：" & PluginListText()
        ' 官方文档：SetEnabled 会读写磁盘 + Refresh，必须在后台线程调用（这里本来就在 worker 线程上）
        Try
            Dim Ok As Boolean = Host.Plugins.SetEnabled(Id, En.Value)
            If Not Ok Then Return "启动器拒绝了这次变更：" & Id
        Catch ex2 As Exception
            Dim T2 As Exception = If(ex2.InnerException IsNot Nothing, ex2.InnerException, ex2)
            Return "变更失败：" & T2.Message
        End Try
        ' 复核：宿主可能返回成功但实际没变（reload 可能正在发生）
        Try
            Dim Re = Host.Plugins.Find(Id)
            If Re Is Nothing Then Return "已" & If(En.Value, "启用", "禁用") & " " & Id & "（复核跳过：插件列表里暂时找不到它，正在重载）。"
            If Re.Enabled <> En.Value Then
                Return "已调用宿主，但复核结果不一致：" & Id & " 现在仍是" & If(Re.Enabled, "启用", "禁用") & " 状态。"
            End If
            Return "已" & If(En.Value, "启用", "禁用") & " " & Id & "（列表复核通过；启动器会重载插件，本对话可能被打断）"
        Catch ex3 As Exception
            Dim T3 As Exception = If(ex3.InnerException IsNot Nothing, ex3.InnerException, ex3)
            Return "已" & If(En.Value, "启用", "禁用") & " " & Id & "（复核失败：" & T3.Message & "）"
        End Try
    End Function

    ''' <summary>按 kind 取内容列表（mods / resourcepacks / shaderpacks）。</summary>
    Private Function ContentsOf(Kind As String, Opt As String) As List(Of PCL.PluginContentEntry)
        Select Case Kind
            Case "mods"
                Return Host.ListMods(Opt)
            Case "resourcepacks", "packs"
                Return Host.ListResourcePacks(Opt)
            Case "shaderpacks", "shaders"
                Return Host.ListShaderPacks(Opt)
        End Select
        Return New List(Of PCL.PluginContentEntry)
    End Function

    ''' <summary>列 mods / resourcepacks / shaderpacks（mods.read，走宿主 v7.3 接口）。</summary>
    Private Function ModsListText(ArgsJson As String) As String
        Dim Kind As String = If(JsonStringField(ArgsJson, "kind"), "mods").Trim().ToLowerInvariant()
        Dim InstN As String = If(JsonStringField(ArgsJson, "instance"), "").Trim()
        Dim Opt As String = If(InstN.Length = 0, Nothing, InstN)
        Dim Items As List(Of PCL.PluginContentEntry) = Nothing
        Select Case Kind
            Case "mods"
                Items = Host.ListMods(Opt)
            Case "resourcepacks", "packs"
                Kind = "resourcepacks"
                Items = Host.ListResourcePacks(Opt)
            Case "shaderpacks", "shaders"
                Kind = "shaderpacks"
                Items = Host.ListShaderPacks(Opt)
            Case Else
                Return "kind 只能是 mods / resourcepacks / shaderpacks。"
        End Select
        If Items Is Nothing Then Items = New List(Of PCL.PluginContentEntry)
        If Items.Count = 0 Then Return "当前实例的 " & Kind & " 目录是空的（或没有选中实例）。"
        Dim Sb As New StringBuilder()
        Sb.Append(Kind).Append("：共 ").Append(Items.Count).Append(" 项")
        Dim N As Integer = 0
        For Each It In Items
            If N >= 200 Then
                Sb.Append(Environment.NewLine).Append("…（还有 ").Append(Items.Count - N).Append(" 项）")
                Exit For
            End If
            Sb.Append(Environment.NewLine).Append(If(It.Enabled, "[启用] ", "[禁用] ")).Append(It.FileName)
            If Not String.IsNullOrWhiteSpace(It.Name) AndAlso It.Name <> It.FileName Then
                Sb.Append("（").Append(It.Name).Append("）")
            End If
            N += 1
        Next
        If Host IsNot Nothing Then Host.LogInfo($"AI 工具 list {Kind} → {Items.Count} 项")
        Return Sb.ToString()
    End Function

    ''' <summary>启用/禁用 mods 等文件（mods.write；只改文件名后缀 .disabled，可逆）。</summary>
    Private Function ToolSetMod(ArgsJson As String) As String
        Dim Fn As String = If(JsonStringField(ArgsJson, "file"), "").Trim()
        If Fn.Length = 0 Then Return "缺少 file 参数（用 list_mods / list_resourcepacks 返回的 FileName，不要带 .disabled）。"
        Dim En As Boolean? = JsonBoolField(ArgsJson, "enabled")
        If En Is Nothing Then Return "缺少 enabled 参数（true 启用 / false 禁用）。"
        Dim Kind As String = If(JsonStringField(ArgsJson, "kind"), "mods").Trim().ToLowerInvariant()
        Dim InstN As String = If(JsonStringField(ArgsJson, "instance"), "").Trim()
        Dim Opt As String = If(InstN.Length = 0, Nothing, InstN)
        Dim Ok As Boolean
        Select Case Kind
            Case "mods"
                Ok = Host.SetModEnabled(Fn, En.Value, Opt)
            Case "resourcepacks", "packs"
                Ok = Host.SetResourcePackEnabled(Fn, En.Value, Opt)
            Case "shaderpacks", "shaders"
                Ok = Host.SetShaderPackEnabled(Fn, En.Value, Opt)
            Case Else
                Return "kind 只能是 mods / resourcepacks / shaderpacks。"
        End Select
        If Not Ok Then Return "宿主返回 False（可能文件不存在，或已有同名禁用副本，接口不会覆盖）。"
        Host.LogInfo("AI 工具 set_mod_enabled：" & Fn & " → " & If(En.Value, "启用", "禁用"))
        ' 复核：宿主对不存在的文件也会返回 True，必须回读列表确认
        Dim After As List(Of PCL.PluginContentEntry) = ContentsOf(Kind, Opt)
        Dim HitR As PCL.PluginContentEntry = Nothing
        For Each It In After
            If String.Equals(It.FileName, Fn, StringComparison.OrdinalIgnoreCase) Then
                HitR = It
                Exit For
            End If
        Next
        If HitR Is Nothing Then
            Return "宿主返回成功，但 " & Kind & " 列表里没有「" & Fn & "」——文件名可能不对（用 list_mods 确认真实 FileName，不要带 .disabled）。"
        End If
        If HitR.Enabled <> En.Value Then
            Return "已调用宿主，但复核结果不一致：现在 " & Fn & " 是" & If(HitR.Enabled, "启用", "禁用") & " 状态。"
        End If
        Return "已" & If(En.Value, "启用", "禁用") & "：" & Fn & "（列表复核通过）"
    End Function

    ''' <summary>
    ''' 只读游戏日志（logs.read）。启动器自己的日志是留给开发者排查的，不对模型开放。
    ''' 走宿主 ListLogs/ReadLog，只能读当前实例的固定路径，不接受任意路径。
    ''' </summary>
    Private Function ToolReadLog(ArgsJson As String) As String
        Dim N As Integer = JsonIntField(ArgsJson, "lines", 60)
        If N < 10 Then N = 10
        If N > 200 Then N = 200
        Return GameLogText(N)
    End Function

    ''' <summary>读当前实例的游戏日志（宿主 API，固定路径，不接受任意路径）。</summary>
    Private Function GameLogText(N As Integer) As String
        Try
            Dim L = Host.ListLogs()
            If L Is Nothing OrElse L.Count = 0 Then Return "宿主没返回任何游戏日志（当前可能没有选中实例，或这个实例还没启动过）。"
            Dim Target = Nothing
            For Each F In L
                If Target Is Nothing OrElse F.ModifiedAt > Target.ModifiedAt Then Target = F
            Next
            If Target Is Nothing Then Return "宿主没返回任何游戏日志。"
            Dim Txt As String = Host.ReadLog(Target.Path, N)
            If String.IsNullOrWhiteSpace(Txt) Then Return "游戏日志是空的：" & Target.Name
            Host.LogInfo($"AI 工具 read_log（game）→ {Target.Name}，{N} 行")
            Return "文件：" & Target.Name & "（游戏日志，最多 " & N & " 行）" & Environment.NewLine & Txt
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "读游戏日志失败：" & T.Message
        End Try
    End Function

    ' ==================== 联网抓取 / 实例诊断 ====================

    ''' <summary>当前选中且目录可用的实例；没有就返回 Nothing。</summary>
    Private Function TryInstance() As PCL.PluginInstanceSnapshot
        Try
            Dim I = Host.InstanceInfo
            If I IsNot Nothing AndAlso I.Available AndAlso Not String.IsNullOrWhiteSpace(I.PathIndie) AndAlso Directory.Exists(I.PathIndie) Then Return I
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    ''' <summary>把 HTML 粗加工成可读文本：去脚本样式、块级标签换行、去标签、解实体、压空行。</summary>
    Private Function HtmlToText(Html As String) As String
        Dim S As String = If(Html, "")
        Try
            S = Regex.Replace(S, "<!--.*?-->", "", RegexOptions.Singleline)
            S = Regex.Replace(S, "<script\b.*?</script>", "", RegexOptions.Singleline Or RegexOptions.IgnoreCase)
            S = Regex.Replace(S, "<style\b.*?</style>", "", RegexOptions.Singleline Or RegexOptions.IgnoreCase)
            S = Regex.Replace(S, "<(br|/p|/div|/li|/tr|/h[1-6]|/section|/article|/table|/blockquote|/title|/head|/ul|/ol|/pre|/dd|/dt|/header|/footer|/nav|/form)\b[^>]*>", vbLf, RegexOptions.IgnoreCase)
            S = Regex.Replace(S, "<td\b[^>]*>", " | ", RegexOptions.IgnoreCase)
            S = Regex.Replace(S, "<[^>]+>", "")
            S = WebUtility.HtmlDecode(S)
            S = S.Replace(ChrW(&HA0), " "c)
            S = Regex.Replace(S, "[ \t]+", " ")
            S = Regex.Replace(S, " *\r?\n *", vbCrLf)
            S = Regex.Replace(S, "(?:\r?\n){3,}", vbCrLf & vbCrLf)
        Catch ex As Exception
            Return S
        End Try
        Return S.Trim()
    End Function

    ''' <summary>行内文本清洗：去标签、解实体、压空白（标题 / 摘要用）。</summary>
    Private Function CleanInline(S As String) As String
        Dim T As String = Regex.Replace(If(S, ""), "<[^>]+>", "")
        T = WebUtility.HtmlDecode(T)
        T = T.Replace(ChrW(&HA0), " "c)
        T = Regex.Replace(T, "\s+", " ")
        Return T.Trim()
    End Function

    ''' <summary>
    ''' MC百科（mcmod.cn）搜索。中文模组 / 物品 / 教程最全的一家，只给资料不给下载。
    ''' 走 search.mcmod.cn 的结果页，按 .result-item 抓标题 + 链接 + 摘要。
    ''' </summary>
    Private Function ToolSearchMcmod(ArgsJson As String) As String
        Dim Q As String = If(JsonStringField(ArgsJson, "query"), "").Trim()
        If Q.Length = 0 Then Return "缺少 query 参数，例如 jei / 机械动力 / 暮色森林。"
        Dim Limit As Integer = JsonIntField(ArgsJson, "limit", 8)
        If Limit < 1 Then Limit = 1
        If Limit > 20 Then Limit = 20

        Dim Enc As String = Uri.EscapeDataString(Q)
        Dim U As String = "https://search.mcmod.cn/s?key=" & Enc & "&site=0&filter=0"
        Dim Raw As String = ""
        Try
            Raw = Host.HttpGetString(U)
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            If Host IsNot Nothing Then Host.LogWarn("search_mcmod 失败（" & Q & "）：" & T.Message)
            Return "MC百科搜索失败：" & T.Message & Environment.NewLine &
                   "search.mcmod.cn 已经在插件白名单里，正常不会弹授权；失败多半是站点超时或临时拦截，稍后再试。" & Environment.NewLine &
                   "实在要查也可以直接 fetch_url 打开 https://search.mcmod.cn/s?key=" & Enc
        End Try
        If Raw Is Nothing Then Raw = ""
        If Raw.Trim().Length = 0 Then Return "MC百科返回了空响应，稍后再试。"
        Return ParseMcmodResults(Raw, Q, Limit, Enc)
    End Function

    ''' <summary>把 search.mcmod.cn 的结果页解析成「标题 + 链接 + 摘要」的纯文本（拆出来是为了能脱离宿主单测）。</summary>
    Private Function ParseMcmodResults(Raw As String, Q As String, Limit As Integer, Enc As String) As String
        Dim Blocks = Regex.Matches(Raw, "<div class=""result-item"">(.*?)<div class=""foot"">", RegexOptions.Singleline)
        If Blocks.Count = 0 Then
            Return "MC百科没解析出结果（页面版式可能变了，或者关键词没命中）。" & Environment.NewLine &
                   "换个更短的关键词再试，或直接 fetch_url 打开 https://search.mcmod.cn/s?key=" & Enc
        End If

        Dim Sb As New StringBuilder()
        Sb.Append("MC百科「").Append(Q).Append("」搜到 ").Append(Blocks.Count).Append(" 条，列前 ")
        Dim Show As Integer = Math.Min(Limit, Blocks.Count)
        Sb.Append(Show).Append(" 条：")
        Dim N As Integer = 0
        For Each B As Match In Blocks
            If N >= Limit Then Exit For
            Dim Blk As String = B.Groups(1).Value

            Dim Link As String = ""
            Dim Title As String = ""
            For Each A As Match In Regex.Matches(Blk, "<a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)
                Dim H As String = A.Groups(1).Value.Trim()
                ' 跳过分类图标和「来自」这类辅助链接，第一个真正的条目链接就是标题
                If H.Length = 0 Then Continue For
                If H.IndexOf("/class/category/", StringComparison.Ordinal) >= 0 Then Continue For
                If H.IndexOf("/category/", StringComparison.Ordinal) >= 0 Then Continue For
                If H.StartsWith("#", StringComparison.Ordinal) Then Continue For
                If H.IndexOf("mcmod.cn", StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
                Link = H
                Title = CleanInline(A.Groups(2).Value)
                Exit For
            Next
            If Link.Length = 0 OrElse Title.Length = 0 Then Continue For
            If Link.StartsWith("//", StringComparison.Ordinal) Then Link = "https:" & Link

            Dim Sum As String = ""
            Dim Bd As Match = Regex.Match(Blk, "<div class=""body"">(.*?)</div>", RegexOptions.Singleline)
            If Bd.Success Then
                Sum = HtmlToText(Bd.Groups(1).Value)
                ' 摘要里带 MC百科自己的 BBCode 标记：[h1=概述]、[mark:title_menu] 之类
                Sum = Regex.Replace(Sum, "\[[A-Za-z0-9_]+[:=][^\]]{0,80}\]", "")
                Sum = Regex.Replace(Sum, "\s+", " ").Trim()
                If Sum.Length > 180 Then Sum = Sum.Substring(0, 180) & "…"
            End If

            N += 1
            Sb.Append(Environment.NewLine).Append(N).Append(". ").Append(Title)
            Sb.Append(Environment.NewLine).Append("   ").Append(Link)
            If Sum.Length > 0 Then Sb.Append(Environment.NewLine).Append("   ").Append(Sum)
        Next

        If N = 0 Then
            Return "MC百科页面解析到 0 条可用结果（版式可能变了）。可以 fetch_url 打开 https://search.mcmod.cn/s?key=" & Enc & " 直接看。"
        End If

        Sb.Append(Environment.NewLine).Append(Environment.NewLine)
        Sb.Append("说明：MC百科是资料站，没有下载地址；要看正文、适用版本、前置和教程，拿上面的链接调 fetch_url。")
        Sb.Append("要下载还是用 search_modrinth / search_curseforge。")
        Return Sb.ToString()
    End Function

    ''' <summary>GET 一个网址并返回可读正文。走 Host.HttpGetString：白名单内直连，名单外在 net.dynamic 下向用户申请。</summary>
    Private Function ToolFetchUrl(ArgsJson As String) As String
        Dim U As String = If(JsonStringField(ArgsJson, "url"), "").Trim()
        If U.Length = 0 Then Return "缺少 url 参数。"
        If Not U.StartsWith("http://", StringComparison.OrdinalIgnoreCase) AndAlso
           Not U.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            Return "只支持 http / https 链接：" & U
        End If
        Dim Max As Integer = JsonIntField(ArgsJson, "max_chars", 6000)
        If Max < 500 Then Max = 500
        If Max > 15000 Then Max = 15000

        Dim Raw As String
        Try
            Raw = Host.HttpGetString(U)
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            If Host IsNot Nothing Then Host.LogWarn("fetch_url 失败（" & U & "）：" & T.Message)
            Return "抓取失败：" & T.Message & Environment.NewLine &
                   "常见原因：这个站不在插件白名单、并且用户拒绝了授权弹窗；或者站点超时 / 需要登录。可以换 Modrinth、CurseForge、GitHub、Mojang 这类已放行的站。" & Environment.NewLine &
                   "（插件白名单：mcmod.cn、search.mcmod.cn、api.modrinth.com、cdn.modrinth.com、api.curseforge.com、*.forgecdn.net、mod.mcimirror.top、github.com、raw.githubusercontent.com、objects.githubusercontent.com、piston-meta/piston-data/launchermeta.mojang.com、bmclapi2.bangbang93.com）"
        End Try
        If Raw Is Nothing Then Raw = ""
        If Raw.Trim().Length = 0 Then Return "抓到了空响应：" & U

        Dim Trimmed As String = Raw.TrimStart()
        Dim IsJsonTxt As Boolean = Trimmed.StartsWith("{") OrElse Trimmed.StartsWith("[")
        Dim Text As String = If(IsJsonTxt, Raw, HtmlToText(Raw))
        Dim Kind As String = If(IsJsonTxt, "JSON", "网页正文")

        Dim Total As Integer = Text.Length
        Dim Head As String = "已抓取 " & U & Environment.NewLine &
                             "类型：" & Kind & "；" & Total & " 字" &
                             If(Total > Max, "（只给前 " & Max & " 字，需要更多就加 max_chars）", "") & Environment.NewLine &
                             "----------------------------------------" & Environment.NewLine
        If Total > Max Then Text = Text.Substring(0, Max) & Environment.NewLine & "……（后面还有 " & (Total - Max) & " 字）"
        Return Head & Text
    End Function

    ' ---- 搜索 ----

    ''' <summary>
    ''' DeepSeek 官方 Search 接口（/v1/tools/search）：这是搜索，不是抓网页。
    ''' 5 元 / 千次成功请求，与模型共用同一个 Key；返回标题、链接、长摘要、发布时间。
    ''' （DeepSeek 的服务端联网工具不许和自定义 function 混用，所以只能走这个独立接口。）
    ''' </summary>
    Private Function ToolWebSearch(ArgsJson As String) As String
        Dim Q As String = If(JsonStringField(ArgsJson, "query"), "").Trim()
        If Q.Length = 0 Then Return "缺少 query 参数。"
        If Q.Length > 128 Then Q = Q.Substring(0, 128)
        Dim N As Integer = JsonIntField(ArgsJson, "count", 6)
        If N < 1 Then N = 1
        If N > 10 Then N = 10

        Dim U As String = SearchEndpoint()
        If U.Length = 0 Then
            Return "当前接口地址不是 DeepSeek 官方站点，没有可用的搜索接口（中转站一般不会转发 /v1/tools/search）。" &
                   "已知网址就用 fetch_url 抓正文；要搜索请把「接口地址」改成 https://api.deepseek.com。"
        End If
        If ApiKey.Length = 0 Then Return "没有配置 API Key，调不了搜索接口。"

        Dim Body As String = "{" & JsonStr("query") & ":" & JsonStr(Q) & "," &
                             JsonStr("count") & ":" & N.ToString() & "," &
                             JsonStr("need_content") & ":false}"
        Dim Resp As String = ""
        Try
            Dim Req As New HttpRequestMessage(HttpMethod.Post, U)
            Req.Content = New StringContent(Body, Encoding.UTF8, "application/json")
            Req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & ApiKey)
            Using Tcs As New CancellationTokenSource(TimeSpan.FromSeconds(25))
                Dim Rs = Client.SendAsync(Req, Tcs.Token).GetAwaiter().GetResult()
                Resp = Rs.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                If Not Rs.IsSuccessStatusCode Then
                    Dim Tip As String = ""
                    Select Case CInt(Rs.StatusCode)
                        Case 401, 403
                            Tip = "（鉴权失败：检查 API Key。）"
                        Case 402
                            Tip = "（账号余额不足。）"
                        Case 429
                            Tip = "（QPS 超限，等几秒再试。）"
                        Case 404
                            Tip = "（这个地址没有搜索接口，可能配的是中转站。）"
                    End Select
                    Return "搜索失败：HTTP " & CInt(Rs.StatusCode) & " " & Shorten(Resp, 200) & Tip
                End If
            End Using
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "搜索请求失败：" & T.Message & Environment.NewLine &
                   "（只有 api.deepseek.com 官方站开放 /v1/tools/search；中转站请改用 fetch_url。）"
        End Try

        Dim D As Dictionary(Of String, Object) = JsonObjectOf(Resp)
        If D Is Nothing Then Return "搜索返回了无法解析的内容：" & Shorten(Resp, 220)
        Dim Arr As Object() = JArr(D, "results")
        If Arr Is Nothing OrElse Arr.Length = 0 Then
            Return "没搜到结果：" & Q & "。可以换关键词，或去掉过于严格的 site: / after: / lang: 限制。"
        End If

        Dim Sb As New StringBuilder()
        Sb.Append("搜索「").Append(Q).Append("」共 ").Append(Arr.Length).Append(" 条（按相关性排序）：").Append(Environment.NewLine)
        Dim Idx As Integer = 0
        For Each Item In Arr
            Dim R As Dictionary(Of String, Object) = TryCast(Item, Dictionary(Of String, Object))
            If R Is Nothing Then Continue For
            Idx += 1
            Dim Ttl As String = DictStr(R, "title")
            Dim Lnk As String = DictStr(R, "url")
            Dim Sum As String = DictStr(R, "summary")
            If Sum.Length = 0 Then Sum = DictStr(R, "snippet")
            Dim Dt As String = DictStr(R, "date")
            Dim Sn As String = DictStr(R, "site_name")

            Sb.Append(Idx.ToString()).Append(". ").Append(If(Ttl.Length > 0, Ttl, Lnk))
            If Sn.Length > 0 Then Sb.Append("（").Append(Sn).Append("）")
            If Dt.Length >= 10 Then Sb.Append(" ").Append(Dt.Substring(0, 10))
            Sb.Append(Environment.NewLine).Append("   ").Append(Lnk).Append(Environment.NewLine)
            If Sum.Length > 0 Then
                If Sum.Length > 420 Then Sum = Sum.Substring(0, 420) & "…"
                Sb.Append("   ").Append(Sum).Append(Environment.NewLine)
            End If
        Next
        Sb.Append("（以上是搜索结果；回答时把链接带上，需要某条正文再用 fetch_url 抓它的 url。）")
        Return Sb.ToString()
    End Function

    Private Function DictStr(D As Dictionary(Of String, Object), K As String) As String
        If D Is Nothing Then Return ""
        Dim V As Object = Nothing
        If Not D.TryGetValue(K, V) OrElse V Is Nothing Then Return ""
        Return Convert.ToString(V)
    End Function

    ' ---- mods 目录的 jar 元数据 ----

    Private Class JarMeta
        Public FileName As String = ""
        Public ModId As String = ""
        Public ModName As String = ""
        Public Version As String = ""
        Public Loader As String = ""
        Public Enabled As Boolean = True
        Public Deps As New List(Of String)
    End Class

    ''' <summary>加载器自身的东西不算「缺前置」。</summary>
    Private Function IsBaseDep(Id As String) As Boolean
        Select Case If(Id, "").Trim().ToLowerInvariant()
            Case "minecraft", "java", "fabricloader", "forge", "neoforge", "minecraft_forge"
                Return True
        End Select
        Return False
    End Function

    Private Sub ParseTomlMeta(Txt As String, ByVal M As JarMeta)
        Dim Section As String = ""
        Dim GotId As Boolean = False
        Dim PendingId As String = ""
        Dim PendingMandatory As Boolean = True

        Dim Flush As Action =
            Sub()
                If PendingId.Length > 0 AndAlso PendingMandatory AndAlso Not IsBaseDep(PendingId) Then
                    If Not M.Deps.Contains(PendingId) Then M.Deps.Add(PendingId)
                End If
                PendingId = ""
                PendingMandatory = True
            End Sub

        For Each Line0 In Txt.Replace(vbCrLf, vbLf).Split(CChar(vbLf))
            Dim L As String = Line0.Trim()
            If L.Length = 0 OrElse L.StartsWith("#") Then Continue For
            If L.StartsWith("[[") Then
                Flush()
                Dim Sec As String = L.TrimStart("["c).TrimEnd("]"c).Trim()
                If Sec.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase) Then
                    Section = "dep"
                ElseIf Sec.Equals("mods", StringComparison.OrdinalIgnoreCase) Then
                    Section = "mods"
                Else
                    Section = Sec
                End If
                Continue For
            End If
            Dim Eq As Integer = L.IndexOf("="c)
            If Eq <= 0 Then Continue For
            Dim Key As String = L.Substring(0, Eq).Trim()
            Dim Val As String = L.Substring(Eq + 1).Trim().Trim(""""c).Trim()
            If Section = "mods" Then
                If Key = "modId" AndAlso Not GotId Then M.ModId = Val : GotId = True
                If Key = "version" AndAlso M.Version.Length = 0 Then M.Version = Val
                If Key = "displayName" AndAlso M.ModName.Length = 0 Then M.ModName = Val
            ElseIf Section = "dep" Then
                If Key = "modId" Then
                    Flush()
                    PendingId = Val
                ElseIf Key = "mandatory" Then
                    PendingMandatory = Val.Equals("true", StringComparison.OrdinalIgnoreCase)
                End If
            End If
        Next
        Flush()
    End Sub

    Private Sub ParseMcmodInfo(Txt As String, ByVal M As JarMeta)
        Dim Arr = JsonArrayOf(Txt)
        If Arr Is Nothing OrElse Arr.Length = 0 Then Return
        Dim D = TryCast(Arr(0), Dictionary(Of String, Object))
        If D Is Nothing Then Return
        M.ModId = JStr(D, "modid")
        M.ModName = JStr(D, "name")
        M.Version = JStr(D, "version")
    End Sub

    ''' <summary>从 jar 里读 fabric.mod.json / mods.toml / mcmod.info，拿真实的 modid、版本和依赖。</summary>
    Private Function ReadJarMeta(P As String) As JarMeta
        Dim M As New JarMeta With {.FileName = Path.GetFileName(P)}
        M.Enabled = Not M.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
        Try
            Using Z As ZipArchive = ZipFile.OpenRead(P)
                Dim FE = Z.GetEntry("fabric.mod.json")
                If FE IsNot Nothing Then
                    Using R As New StreamReader(FE.Open(), Encoding.UTF8)
                        Dim D = JsonObjectOf(R.ReadToEnd())
                        If D IsNot Nothing Then
                            M.ModId = JStr(D, "id")
                            M.Version = JStr(D, "version")
                            M.ModName = JStr(D, "name")
                            M.Loader = "fabric"
                            Dim Dep As Dictionary(Of String, Object) = JSub(D, "depends")
                            If Dep IsNot Nothing Then
                                For Each K In Dep.Keys
                                    Dim Ks As String = Convert.ToString(K).Trim().ToLowerInvariant()
                                    If Ks.Length > 0 AndAlso Not IsBaseDep(Ks) AndAlso Not M.Deps.Contains(Ks) Then M.Deps.Add(Ks)
                                Next
                            End If
                        End If
                    End Using
                End If

                If String.IsNullOrWhiteSpace(M.ModId) Then
                    Dim NE = Z.GetEntry("META-INF/neoforge.mods.toml")
                    Dim IsNeo As Boolean = NE IsNot Nothing
                    If NE Is Nothing Then NE = Z.GetEntry("META-INF/mods.toml")
                    If NE IsNot Nothing Then
                        Using R As New StreamReader(NE.Open(), Encoding.UTF8)
                            ParseTomlMeta(R.ReadToEnd(), M)
                        End Using
                        M.Loader = If(IsNeo, "neoforge", "forge")
                    End If
                End If

                If String.IsNullOrWhiteSpace(M.ModId) Then
                    Dim ME0 = Z.GetEntry("mcmod.info")
                    If ME0 IsNot Nothing Then
                        Using R As New StreamReader(ME0.Open(), Encoding.UTF8)
                            ParseMcmodInfo(R.ReadToEnd(), M)
                        End Using
                        If String.IsNullOrWhiteSpace(M.Loader) Then M.Loader = "forge"
                    End If
                End If
            End Using
        Catch ex As Exception
            ' 坏 jar / 加密 / 非 zip：留空，不打断整轮扫描
        End Try
        Return M
    End Function

    ''' <summary>扫当前实例的 mods 目录，列出真实 modid / 版本 / 依赖，并检查缺前置与重复 modid。</summary>
    Private Function ToolModsScan(ArgsJson As String) As String
        Dim Inst = TryInstance()
        If Inst Is Nothing Then Return "当前没有选中可用的实例（先用 list_instances / select_instance）。"
        Dim Dir As String = Path.Combine(Inst.PathIndie, "mods")
        If Not Directory.Exists(Dir) Then Return "实例「" & Inst.Name & "」没有 mods 目录：" & Dir

        Dim All As String() = Directory.GetFiles(Dir, "*.jar*")
        If All.Length = 0 Then Return "mods 目录是空的：" & Dir
        Array.Sort(All, StringComparer.OrdinalIgnoreCase)

        Dim Limit As Integer = If(All.Length > 250, 250, All.Length)
        Dim Metas As New List(Of JarMeta)
        For I As Integer = 0 To Limit - 1
            Metas.Add(ReadJarMeta(All(I)))
        Next

        Dim Sb As New StringBuilder()
        Dim Enabled As Integer = 0
        For Each M In Metas
            If M.Enabled Then Enabled += 1
        Next
        Sb.Append("实例「").Append(Inst.Name).Append("」的 mods：").Append(All.Length).Append(" 个文件（启用 ").Append(Enabled).Append(" / 禁用 ").Append(All.Length - Enabled).Append("）")
        If All.Length > Limit Then Sb.Append("，只解析了前 ").Append(Limit).Append(" 个")
        Sb.Append(Environment.NewLine).Append("----------------------------------------").Append(Environment.NewLine)

        Dim ById As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
        Dim NoMeta As New List(Of String)

        For Each M In Metas
            If String.IsNullOrWhiteSpace(M.ModId) Then
                NoMeta.Add(M.FileName)
            Else
                If Not ById.ContainsKey(M.ModId) Then ById(M.ModId) = New List(Of String)
                ById(M.ModId).Add(M.FileName)
            End If
        Next

        Dim Idx As Integer = 0
        For Each M In Metas
            Idx += 1
            Sb.Append(Idx.ToString().PadLeft(3)).Append(". ")
            If Not M.Enabled Then Sb.Append("[禁用] ")
            Sb.Append(M.FileName)
            If M.ModId.Length > 0 Then
                Sb.Append(Environment.NewLine).Append("      id=").Append(M.ModId)
                If M.ModName.Length > 0 AndAlso Not String.Equals(M.ModName, M.ModId, StringComparison.OrdinalIgnoreCase) Then Sb.Append("  名称=").Append(M.ModName)
                If M.Version.Length > 0 Then Sb.Append("  版本=").Append(M.Version)
                If M.Loader.Length > 0 Then Sb.Append("  ").Append(M.Loader)
                If M.Deps.Count > 0 Then Sb.Append(Environment.NewLine).Append("      需要：").Append(String.Join("、", M.Deps.ToArray()))
            Else
                Sb.Append("（读不到 modid：可能是资源包、坏包或非标准 jar）")
            End If
            Sb.Append(Environment.NewLine)
            If Sb.Length > 10000 Then
                Sb.Append("……（列表太长，先截断；剩下 ").Append(Metas.Count - Idx).Append(" 个）")
                Exit For
            End If
        Next

        ' 依赖检查：只比对本实例的 jar，所以「缺」就是真的没装
        Dim Missing As New List(Of String)
        For Each M In Metas
            For Each D In M.Deps
                If Not ById.ContainsKey(D) Then
                    Dim Line As String = D & "  ← " & If(M.ModName.Length > 0, M.ModName, M.ModId) & "（" & M.FileName & "）"
                    If Not Missing.Contains(Line) Then Missing.Add(Line)
                End If
            Next
        Next
        Dim Dups As New List(Of String)
        For Each K In ById.Keys
            If ById(K).Count > 1 Then Dups.Add(K & "：" & String.Join(" / ", ById(K).ToArray()))
        Next

        If Missing.Count > 0 OrElse Dups.Count > 0 Then
            Sb.Append(Environment.NewLine).Append("---- 检查结果 ----").Append(Environment.NewLine)
            If Missing.Count > 0 Then
                Sb.Append("可能缺前置（这些 modid 在 mods 里没找到）：").Append(Environment.NewLine)
                Dim Cnt As Integer = 0
                For Each L In Missing
                    Sb.Append("· ").Append(L).Append(Environment.NewLine)
                    Cnt += 1
                    If Cnt >= 25 Then
                        Sb.Append("· ……还有 ").Append(Missing.Count - Cnt).Append(" 条").Append(Environment.NewLine)
                        Exit For
                    End If
                Next
                Sb.Append("（前置也可能是被某个 mod 内置了 / 用别的 modid 提供，别当成绝对结论；可以再 search_modrinth 确认。）").Append(Environment.NewLine)
            End If
            If Dups.Count > 0 Then
                Sb.Append("重复 modid（同一个 mod 装了多个版本，通常会崩）：").Append(Environment.NewLine)
                For Each L In Dups
                    Sb.Append("· ").Append(L).Append(Environment.NewLine)
                Next
            End If
        End If

        If NoMeta.Count > 0 Then
            Sb.Append(Environment.NewLine).Append("读不到 modid 的文件 ").Append(NoMeta.Count).Append(" 个：").Append(String.Join("、", NoMeta.ToArray())).Append(Environment.NewLine)
        End If

        Host.LogInfo("AI 工具 mods_scan → " & Inst.Name & "，" & Metas.Count & " 个 jar")
        Return Sb.ToString()
    End Function

    ''' <summary>一次性把诊断要用的东西凑齐：实例信息 + 最新崩溃报告 + 日志尾部 + mods 概况。</summary>
    Private Function ToolDiagnose(ArgsJson As String) As String
        Dim Sb As New StringBuilder()
        Dim Inst = TryInstance()
        If Inst IsNot Nothing Then
            Sb.Append("== 实例 ==").Append(Environment.NewLine)
            Sb.Append("名称：").Append(Inst.Name).Append("；版本：").Append(Inst.VersionId)
            If Not String.IsNullOrWhiteSpace(Inst.InheritName) Then Sb.Append("；继承自：").Append(Inst.InheritName)
            Sb.Append("；可装模组：").Append(If(Inst.Modable, "是", "否"))
            Sb.Append(Environment.NewLine).Append("目录：").Append(Inst.PathIndie).Append(Environment.NewLine)

            Dim ModsDir As String = Path.Combine(Inst.PathIndie, "mods")
            If Directory.Exists(ModsDir) Then
                Dim Jars = Directory.GetFiles(ModsDir, "*.jar")
                Dim Disabled = Directory.GetFiles(ModsDir, "*.jar.disabled")
                Sb.Append("mods：" & Jars.Length & " 个启用，").Append(Disabled.Length).Append(" 个禁用").Append(Environment.NewLine)
                If Jars.Length > 0 AndAlso Jars.Length <= 60 Then
                    Sb.Append("已装：")
                    For Each F In Jars
                        Sb.Append(Path.GetFileName(F)).Append("；")
                    Next
                    Sb.Append(Environment.NewLine)
                End If
            Else
                Sb.Append("mods：没有 mods 目录").Append(Environment.NewLine)
            End If
        Else
            Sb.Append("== 实例 ==").Append(Environment.NewLine).Append("当前没有选中可用的实例。").Append(Environment.NewLine)
        End If

        Dim CrashPath As String = ""
        Dim LogPath As String = ""
        Try
            Dim L = Host.ListLogs()
            If L IsNot Nothing Then
                For Each F In L
                    Dim P As String = If(F.Path, "")
                    If P.StartsWith("crash-reports", StringComparison.OrdinalIgnoreCase) AndAlso CrashPath.Length = 0 Then CrashPath = P
                    If P.StartsWith("logs", StringComparison.OrdinalIgnoreCase) AndAlso P.EndsWith("latest.log", StringComparison.OrdinalIgnoreCase) Then LogPath = P
                Next
                If LogPath.Length = 0 Then
                    For Each F In L
                        If If(F.Path, "").StartsWith("logs", StringComparison.OrdinalIgnoreCase) Then LogPath = F.Path
                    Next
                End If
            End If
        Catch ex As Exception
        End Try

        If CrashPath.Length > 0 Then
            Dim T As String = ""
            Try
                T = Host.ReadLog(CrashPath, 2000)
            Catch ex As Exception
            End Try
            If Not String.IsNullOrWhiteSpace(T) Then
                Dim Lines As String() = T.Replace(vbCrLf, vbLf).Split(CChar(vbLf))
                Dim TakeN As Integer = If(Lines.Length > 160, 160, Lines.Length)
                Sb.Append(Environment.NewLine).Append("== 崩溃报告（最新的 ").Append(CrashPath).Append("，共 ").Append(Lines.Length).Append(" 行，给前 ").Append(TakeN).Append(" 行）==").Append(Environment.NewLine)
                For I As Integer = 0 To TakeN - 1
                    Sb.Append(Lines(I)).Append(Environment.NewLine)
                Next
                If Lines.Length > TakeN Then Sb.Append("……（后面主要是环境信息与 mod 列表，需要的话用 read_instance_file 读全文）").Append(Environment.NewLine)
            End If
        End If

        If LogPath.Length > 0 Then
            Dim T As String = ""
            Try
                T = Host.ReadLog(LogPath, 120)
            Catch ex As Exception
            End Try
            If Not String.IsNullOrWhiteSpace(T) Then
                Sb.Append(Environment.NewLine).Append("== 日志尾部（").Append(LogPath).Append("，后 120 行）==").Append(Environment.NewLine).Append(T).Append(Environment.NewLine)
            End If
        End If

        If CrashPath.Length = 0 AndAlso LogPath.Length = 0 Then
            Sb.Append(Environment.NewLine).Append("没有找到日志或崩溃报告（这个实例可能还没启动过）。").Append(Environment.NewLine)
        End If

        If Sb.Length > 14000 Then
            Sb.Length = 14000
            Sb.Append(Environment.NewLine).Append("……（诊断内容过长，已截断）")
        End If
        Host.LogInfo("AI 工具 diagnose_crash → " & If(Inst IsNot Nothing, Inst.Name, "无实例"))
        Return Sb.ToString()
    End Function

    ''' <summary>读当前实例目录里的文本文件；path 为空时给目录概览。只读、限大小、禁越界。</summary>
    Private Function ToolReadInstanceFile(ArgsJson As String) As String
        Dim Inst = TryInstance()
        If Inst Is Nothing Then Return "当前没有选中可用的实例。"
        Dim Root As String = Path.GetFullPath(Inst.PathIndie)
        Dim Rel As String = If(JsonStringField(ArgsJson, "path"), "").Trim().Replace("/", "\").TrimStart("\"c)

        If Rel.Length = 0 Then
            Dim Sb As New StringBuilder()
            Sb.Append("实例「").Append(Inst.Name).Append("」目录：").Append(Root).Append(Environment.NewLine)
            Try
                Dim Files = Directory.GetFiles(Root)
                If Files.Length > 0 Then
                    Sb.Append("顶层文件：")
                    Dim C As Integer = 0
                    For Each F In Files
                        Sb.Append(Path.GetFileName(F)).Append(" (").Append(FmtSize(New FileInfo(F).Length)).Append(")；")
                        C += 1
                        If C >= 15 Then Sb.Append("……") : Exit For
                    Next
                    Sb.Append(Environment.NewLine)
                End If
                For Each D In New String() {"mods", "config", "saves", "resourcepacks", "shaderpacks", "logs", "crash-reports", "datapacks", "defaultconfigs"}
                    Dim P As String = Path.Combine(Root, D)
                    If Directory.Exists(P) Then
                        Dim N As Integer = 0
                        Try
                            N = Directory.GetFileSystemEntries(P).Length
                        Catch ex As Exception
                        End Try
                        Sb.Append("· ").Append(D).Append("\ （").Append(N).Append(" 项）").Append(Environment.NewLine)
                    End If
                Next
                Sb.Append("用法：read_instance_file path=""options.txt"" 或 path=""config\xxx.toml""。")
            Catch ex As Exception
                Sb.Append("读目录失败：").Append(ex.Message)
            End Try
            Return Sb.ToString()
        End If

        If Rel.Contains("..") Then Return "拒绝：路径里不能有 ..（只能读当前实例目录里的文件）。"
        Dim Full As String
        Try
            Full = Path.GetFullPath(Path.Combine(Root, Rel))
        Catch ex As Exception
            Return "路径不合法：" & Rel
        End Try
        If Not Full.StartsWith(Root.TrimEnd("\"c) & "\", StringComparison.OrdinalIgnoreCase) Then Return "拒绝：只能读当前实例目录里的文件。"
        If Not File.Exists(Full) Then Return "文件不存在：" & Rel & "（先不带 path 调一次，看看目录里有什么）"

        Dim Ext As String = Path.GetExtension(Full).ToLowerInvariant()
        Select Case Ext
            Case ".txt", ".json", ".json5", ".toml", ".cfg", ".conf", ".properties", ".yaml", ".yml", ".md", ".log", ".mcmeta", ".snbt"
            Case Else
                Return "只读文本类文件（.txt/.json/.toml/.cfg/.properties/.yaml/.log/.snbt 等），" & Ext & " 不支持。"
        End Select

        Dim Fi As New FileInfo(Full)
        If Fi.Length > 512 * 1024 Then Return "文件太大（" & FmtSize(Fi.Length) & "），超过 512 KB 上限。"
        Try
            Dim T As String = File.ReadAllText(Full, Encoding.UTF8)
            If T.Length > 8000 Then T = T.Substring(0, 8000) & Environment.NewLine & "……（还有 " & (T.Length - 8000) & " 字）"
            Return "文件：" & Rel & "（" & FmtSize(Fi.Length) & "）" & Environment.NewLine & "----------------------------------------" & Environment.NewLine & T
        Catch ex As Exception
            Return "读文件失败：" & ex.Message
        End Try
    End Function

    ''' <summary>唯一允许的写操作：只能写 notes 目录，白名单文件名，64 KB 上限。</summary>
    Private Function ToolWriteNote(ArgsJson As String) As String
        Dim Nm As String = If(JsonStringField(ArgsJson, "name"), "").Trim()
        Dim Tx As String = If(JsonStringField(ArgsJson, "text"), "")
        If Not SafeNoteName(Nm) Then
            Return "拒绝：文件名只能用字母/数字/下划线/连字符/中文，扩展名必须是 .md 或 .txt，长度 5–48。"
        End If
        If Tx.Length > 65536 Then Return "拒绝：内容超过 64 KB 上限（当前 " & Tx.Length & " 字符）。"
        Try
            Dim Dir As String = NotesDir()
            Dim DirFull As String = Path.GetFullPath(Dir)
            Dim Full As String = Path.GetFullPath(Path.Combine(DirFull, Nm))
            If Not Full.StartsWith(DirFull, StringComparison.OrdinalIgnoreCase) Then
                Return "拒绝：目标路径不在 notes 目录内。"
            End If
            Directory.CreateDirectory(DirFull)
            File.WriteAllText(Full, Tx, New UTF8Encoding(False))
            If Host IsNot Nothing Then Host.LogInfo("AI 写入笔记：" & Nm & "（" & Tx.Length & " 字符）")
            Return "已写入 notes\" & Nm & "（" & Tx.Length & " 字符）"
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "写笔记失败：" & T.Message
        End Try
    End Function

    ''' <summary>只允许 http/https，避免 file: 或自定义协议被用来拉起本地程序。</summary>
    Private Function ToolOpenUrl(ArgsJson As String) As String
        Dim U As String = If(JsonStringField(ArgsJson, "url"), "").Trim()
        If U.Length = 0 Then Return "缺少 url 参数。"
        If U.Length > 500 Then Return "拒绝：URL 太长。"
        For Each C In U
            If Char.IsWhiteSpace(C) Then Return "拒绝：URL 里不能有空格或换行。"
        Next
        Dim Low As String = U.ToLowerInvariant()
        If Not (Low.StartsWith("https://") OrElse Low.StartsWith("http://")) Then
            Return "拒绝：只允许 http/https 链接。"
        End If
        If Low.Contains("file:") OrElse Low.StartsWith("javascript:") OrElse Low.StartsWith("data:") Then
            Return "拒绝：不允许该协议。"
        End If
        Host.OpenUrl(U)
        Return "已用系统浏览器打开：" & U
    End Function

    ' ==================== 下载内容 ====================
    ' 所有产物只落在 data\downloads（插件自己的 data 目录，宿主永远放行）；
    ' 要装进实例就走 Host.InstallContent（instance.install 权限 + 固定的四个目录）。

    Private Const ModrinthApi As String = "https://api.modrinth.com/v2"

    ' ---------- CurseForge ----------
    ' 官方 API 必须带 x-api-key，而宿主的 HttpGetString 不支持自定义请求头，所以这一路用插件自己的
    ' HttpClient，目标主机硬编码（不接受外部传入地址，等价于自己遵守 net.hosts 白名单）。
    '
    ' 关键：启动器自己并不直连 CurseForge —— ModDownload.vb 把 api.curseforge.com 整体重写成
    ' mod.mcimirror.top/curseforge，forgecdn 也换到同一台镜像上。该镜像不需要 key，
    ' 所以这里也优先走镜像，用户不用申请 API Key；配了 key 才额外试官方接口。
    ' 映射参照 ModDownload.vb:1326-1332。

    Private Const CfOfficialApi As String = "https://api.curseforge.com/v1"
    Private Const CfMirrorApi As String = "https://mod.mcimirror.top/curseforge/v1"
    Private Const CfMirrorHost As String = "mod.mcimirror.top"
    Private Const CurseForgeGameId As Integer = 432 'Minecraft
    Private CfClient As HttpClient

    ''' <summary>把 CurseForge / 其 CDN 的地址换成国内镜像的等价地址（与启动器自己的做法一致）。</summary>
    Private Function CfMirrorUrl(U As String) As String
        Dim S As String = If(U, "")
        If S.Length = 0 Then Return S
        S = S.Replace("api.curseforge.com", CfMirrorHost & "/curseforge")
        S = S.Replace("mediafilez.forgecdn.net", CfMirrorHost)
        S = S.Replace("edge.forgecdn.net", CfMirrorHost)
        S = S.Replace("media.forgecdn.net", CfMirrorHost)
        Return S
    End Function

    ''' <summary>可用的 CurseForge API Key；一点都没有时返回空串。Source 回填来源（排查用）。</summary>
    Private Function CurseForgeKey() As String
        Dim Src As String = ""
        Return CurseForgeKeyEx(Src)
    End Function

    Private Function CurseForgeKeyEx(ByRef Source As String) As String
        Source = ""
        Dim K As String = If(CfKey, "").Trim()
        If K.Length > 0 Then
            Source = "插件设置"
            Return K
        End If
        For Each EnvName As String In New String() {"PCL_CURSEFORGE_API_KEY", "CURSEFORGE_API_KEY"}
            Try
                Dim V As String = Environment.GetEnvironmentVariable(EnvName)
                If Not String.IsNullOrWhiteSpace(V) Then
                    Source = "环境变量 " & EnvName
                    Return V.Trim()
                End If
            Catch ex As Exception
            End Try
        Next
        '启动器自己也在读这个文件，用户配过就顺手复用，不用配两遍
        Try
            Dim Candidates As New List(Of String)
            Dim Ef As String = ExeFolder()
            If Ef.Length > 0 Then
                Candidates.Add(Path.Combine(Ef, "PCL\Secrets.json"))
                Candidates.Add(Path.Combine(Ef, "Secrets.json"))
            End If
            Try
                Dim Entry As String = Reflection.Assembly.GetEntryAssembly().Location
                Dim Ed As String = Path.GetDirectoryName(Entry)
                If Ed.Length > 0 Then
                    Candidates.Add(Path.Combine(Ed, "PCL\Secrets.json"))
                    Candidates.Add(Path.Combine(Ed, "Secrets.json"))
                End If
            Catch ex As Exception
            End Try
            For Each Cand In Candidates
                If Not File.Exists(Cand) Then Continue For
                Dim D = JsonObjectOf(File.ReadAllText(Cand))
                Dim V As String = JStr(D, "CurseForgeApiKey").Trim()
                If V.Length > 0 Then
                    Source = Cand
                    Return V
                End If
            Next
        Catch ex As Exception
        End Try
        Return ""
    End Function

    ''' <summary>镜像和官方都不可用时给用户看的说明（同时是工具返回值）。</summary>
    Private Function CfDownText(Detail As String) As String
        Return "这次没能连上 CurseForge。" & Environment.NewLine &
               "插件是走启动器同款国内镜像（mod.mcimirror.top/curseforge）访问的，不需要 API Key；" & Environment.NewLine &
               "镜像偶尔会抽风，过几分钟再试通常就好。也可以先用 Modrinth（search_modrinth / download_modrinth），热门模组两边都有。" & Environment.NewLine &
               "如果一直失败，可以看启动器日志里有没有 mod.mcimirror.top 的报错。" & Environment.NewLine &
               "详细原因：" & Detail
    End Function

    ''' <summary>
    ''' 发一个 CurseForge GET 请求。优先走国内镜像（不需要 key）；配了 key 时先试官方接口。
    ''' 成功返回正文，失败返回 Nothing 并把原因写进 Err。
    ''' </summary>
    Private Function CfGet(PathAndQuery As String, ByRef Err As String) As String
        Err = ""
        If Not PathAndQuery.StartsWith("/") Then PathAndQuery = "/" & PathAndQuery

        Dim Problems As New List(Of String)
        Dim K As String = CurseForgeKey()

        '配了 key 就先用官方接口：数据最新，也不依赖镜像
        If K.Length > 0 Then
            Dim E1 As String = ""
            Dim B1 As String = CfHttpGet(CfOfficialApi & PathAndQuery, K, E1)
            If B1 IsNot Nothing Then Return B1
            Problems.Add("官方接口 " & E1)
            '404 是项目真的不存在，换镜像也一样，没必要再试
            If E1.StartsWith("404", StringComparison.Ordinal) Then
                Err = CfDownText(String.Join("；", Problems.ToArray()))
                Return Nothing
            End If
        End If

        Dim E2 As String = ""
        Dim B2 As String = CfHttpGet(CfMirrorApi & PathAndQuery, "", E2)
        If B2 IsNot Nothing Then Return B2
        Problems.Add("镜像 " & E2)

        Err = CfDownText(String.Join("；", Problems.ToArray()))
        Return Nothing
    End Function

    ''' <summary>实际发请求的那一层。地址只能来自本文件里的两个常量前缀。</summary>
    Private Function CfHttpGet(FullUrl As String, Key As String, ByRef Err As String) As String
        Err = ""
        If CfClient Is Nothing Then
            CfClient = New HttpClient()
            CfClient.Timeout = TimeSpan.FromSeconds(30)
        End If
        Try
            Dim Req As New HttpRequestMessage(HttpMethod.Get, FullUrl)
            If Key.Length > 0 Then Req.Headers.Add("x-api-key", Key)
            Req.Headers.Add("Accept", "application/json")
            Req.Headers.Add("User-Agent", "PCLHarness-AiPlus")
            Dim Resp As HttpResponseMessage = CfClient.SendAsync(Req).GetAwaiter().GetResult()
            Dim Code As Integer = CInt(Resp.StatusCode)
            Dim Body As String = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            If Code = 403 Then
                Err = "403：Key 无效或没有权限"
                Return Nothing
            End If
            If Code = 404 Then
                Err = "404：项目或文件不存在"
                Return Nothing
            End If
            If Code = 429 Then
                Err = "429：请求太频繁，稍等再试"
                Return Nothing
            End If
            If Code < 200 OrElse Code >= 300 Then
                Err = "HTTP " & Code & "：" & Shorten(Body, 140)
                Return Nothing
            End If
            Return Body
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Err = "连不上 " & HostNameOf(FullUrl) & "（" & T.Message & "）"
            Return Nothing
        End Try
    End Function

    Private Function CfClassId(PType As String) As String
        Select Case PType
            Case "modpack"
                Return "4471"
            Case "datapack"
                Return "6945"
            Case "shader"
                Return "6552"
            Case "resourcepack"
                Return "12"
        End Select
        Return "6"
    End Function

    ''' <summary>加载器 → CurseForge 的 modLoaderType 数值。</summary>
    Private Function CfLoaderType(Loader As String) As String
        Select Case If(Loader, "").Trim().ToLowerInvariant()
            Case "forge"
                Return "1"
            Case "fabric"
                Return "4"
            Case "quilt"
                Return "5"
            Case "neoforge"
                Return "6"
        End Select
        Return ""
    End Function

    Private Function AllDigits(S As String) As Boolean
        If String.IsNullOrEmpty(S) Then Return False
        For Each C In S
            If Not Char.IsDigit(C) Then Return False
        Next
        Return True
    End Function

    ''' <summary>
    ''' 把 project 参数归一成 CurseForge 的项目 id（数字字符串）。
    ''' 支持：纯数字 id、slug、curseforge.com 项目链接、带 /files/12345 的文件链接（顺带解析出 fileId）。
    ''' </summary>
    Private Function CfResolveModId(Raw As String, ByRef PinnedFileId As String, ByRef Err As String) As String
        PinnedFileId = ""
        Err = ""
        Dim S As String = If(Raw, "").Trim()
        If S.Length = 0 Then
            Err = "缺少 project 参数（CurseForge 项目 id、slug 或链接）。"
            Return ""
        End If
        '链接：先剥掉 /files/<id> 那一段，剩下的最后一段就是 slug（或数字 id）
        If S.Contains("/") OrElse S.Contains("://") Then
            Dim Mk As String = "/files/"
            Dim Fi As Integer = S.IndexOf(Mk, StringComparison.OrdinalIgnoreCase)
            If Fi > 0 Then
                Dim Tail As String = S.Substring(Fi + Mk.Length)
                Dim Q0 As Integer = Tail.IndexOf("?"c)
                If Q0 >= 0 Then Tail = Tail.Substring(0, Q0)
                Tail = Tail.TrimEnd("/"c)
                If AllDigits(Tail) Then PinnedFileId = Tail
                S = S.Substring(0, Fi)
            End If
            S = S.TrimEnd("/"c)
            Dim Slash As Integer = S.LastIndexOf("/"c)
            If Slash >= 0 Then S = S.Substring(Slash + 1)
            Dim Q As Integer = S.IndexOf("?"c)
            If Q >= 0 Then S = S.Substring(0, Q)
        End If
        S = Uri.UnescapeDataString(S.Trim())
        If S.Length = 0 Then
            Err = "没能从「" & Raw & "」里解析出项目 slug，请直接给 slug 或项目 id。"
            Return ""
        End If
        If AllDigits(S) Then Return S

        'slug → 用 search + slug 精确过滤
        Dim Body As String = CfGet("/mods/search?gameId=" & CurseForgeGameId.ToString() & "&slug=" & Uri.EscapeDataString(S), Err)
        If Body Is Nothing Then Return ""
        Dim D = JsonObjectOf(Body)
        Dim Arr = JArr(D, "data")
        If Arr IsNot Nothing AndAlso Arr.Length > 0 Then
            Dim First = TryCast(Arr(0), Dictionary(Of String, Object))
            Dim Id As String = JStr(First, "id").Trim()
            If Id.Length > 0 Then Return Id
        End If
        '个别情况 slug 搜不到，退回关键词搜一次
        Dim Err2 As String = ""
        Dim Body2 As String = CfGet("/mods/search?gameId=" & CurseForgeGameId.ToString() & "&pageSize=5&searchFilter=" & Uri.EscapeDataString(S), Err2)
        If Body2 IsNot Nothing Then
            Dim Arr2 = JArr(JsonObjectOf(Body2), "data")
            If Arr2 IsNot Nothing AndAlso Arr2.Length > 0 Then
                Dim First2 = TryCast(Arr2(0), Dictionary(Of String, Object))
                Dim Id2 As String = JStr(First2, "id").Trim()
                If Id2.Length > 0 Then Return Id2
            End If
        End If
        Err = "CurseForge 上找不到「" & S & "」。先 search_curseforge 搜一下，拿返回里的数字 id 或 slug。"
        Return ""
    End Function

    ''' <summary>按 fileId 拼 CurseForge CDN 直链：4593545 → files/4593/545/文件名。</summary>
    Private Function CfCdnUrl(FileId As String, FileName As String) As String
        Dim S As String = If(FileId, "").Trim()
        Dim N As String = If(FileName, "").Trim()
        If N.Length = 0 OrElse Not AllDigits(S) Then Return ""
        Dim A As String, B As String
        If S.Length > 3 Then
            A = S.Substring(0, S.Length - 3)
            B = S.Substring(S.Length - 3)
        Else
            A = "0"
            B = S
        End If
        Return "https://mediafilez.forgecdn.net/files/" & A & "/" & B & "/" & N.Replace(" ", "%20")
    End Function

    ''' <summary>在项目的文件列表里挑一个：优先 pinned fileId，其次最新正式版，再退到最新任意版。</summary>
    Private Function CfPickFile(ModId As String, Loader As String, Gv As String, Pinned As String, ByRef Err As String) As Dictionary(Of String, Object)
        Err = ""
        '用户给了具体文件 id（贴的是文件页链接）时，直接按 id 查，省一次列表请求
        If Pinned.Length > 0 Then
            Dim Ep As String = ""
            Dim BodyP As String = CfGet("/mods/" & ModId & "/files/" & Pinned, Ep)
            If BodyP IsNot Nothing Then
                Dim RootP = JsonObjectOf(BodyP)
                Dim Vp As Object = Nothing
                If RootP IsNot Nothing AndAlso RootP.TryGetValue("data", Vp) AndAlso Vp IsNot Nothing Then
                    Dim One = TryCast(Vp, Dictionary(Of String, Object))
                    If One IsNot Nothing Then Return One
                End If
            End If
        End If

        Dim Tried As New List(Of String)
        Dim Qs As New List(Of String)
        Qs.Add("pageSize=50")
        If Gv.Length > 0 Then Qs.Add("gameVersion=" & Uri.EscapeDataString(Gv))
        Dim Lt As String = CfLoaderType(Loader)
        If Lt.Length > 0 Then Qs.Add("modLoaderType=" & Lt)
        Tried.Add("/mods/" & ModId & "/files?" & String.Join("&", Qs.ToArray()))
        '过滤后可能是空的（版本号写法不一致、加载器标错等），再不带过滤拉一次自己筛
        If Gv.Length > 0 OrElse Lt.Length > 0 Then Tried.Add("/mods/" & ModId & "/files?pageSize=50")

        Dim Files As New List(Of Dictionary(Of String, Object))
        For Each PathQ In Tried
            Dim E As String = ""
            Dim Body As String = CfGet(PathQ, E)
            If Body Is Nothing Then
                If Files.Count = 0 Then Err = E
                Continue For
            End If
            Dim Arr = JArr(JsonObjectOf(Body), "data")
            If Arr Is Nothing Then Continue For
            For Each X In Arr
                Dim Fd = TryCast(X, Dictionary(Of String, Object))
                If Fd IsNot Nothing Then Files.Add(Fd)
            Next
            If Files.Count > 0 Then Exit For
        Next
        If Files.Count = 0 Then
            If Err.Length = 0 Then Err = "「" & ModId & "」没有任何可下载的文件。"
            Return Nothing
        End If

        If Pinned.Length > 0 Then
            '上面按 id 没查到（比如作者隐藏了文件），才退到列表里找
            For Each Fd In Files
                If JStr(Fd, "id").Trim() = Pinned Then Return Fd
            Next
        End If

        '只要匹配加载器/版本的文件
        Dim Pool As New List(Of Dictionary(Of String, Object))
        For Each Fd In Files
            If CfFileMatches(Fd, Loader, Gv) Then Pool.Add(Fd)
        Next
        If Pool.Count = 0 Then Pool = Files

        'files 接口默认按上传时间倒序返回，所以「第一个正式版」就是最新的正式版。
        '不用 fileDate 字符串比较：它可能被反序列化成 DateTime 再 ToString，格式随区域设置变，比不准。
        Dim Best As Dictionary(Of String, Object) = Nothing
        For Each Fd In Pool
            If JStr(Fd, "releaseType").Trim() = "1" Then
                Best = Fd
                Exit For
            End If
        Next
        If Best Is Nothing Then Best = Pool(0)
        Return Best
    End Function

    ''' <summary>文件的 gameVersions 里是否同时包含游戏版本和加载器名。</summary>
    Private Function CfFileMatches(Fd As Dictionary(Of String, Object), Loader As String, Gv As String) As Boolean
        Dim Gs = JArr(Fd, "gameVersions")
        If Gs Is Nothing Then Return True
        Dim HasGv As Boolean = (Gv.Length = 0)
        Dim HasLd As Boolean = (Loader.Length = 0)
        For Each G In Gs
            Dim S As String = If(G Is Nothing, "", G.ToString()).ToLowerInvariant()
            If Gv.Length > 0 AndAlso S = Gv.ToLowerInvariant() Then HasGv = True
            If Loader.Length > 0 AndAlso S = Loader.ToLowerInvariant() Then HasLd = True
        Next
        Return HasGv AndAlso HasLd
    End Function

    ''' <summary>自检：CurseForge 通道现在到底通不通、走的是哪条路。</summary>
    Private Function ToolCheckCurseForge() As String
        Dim Sb As New StringBuilder()
        Dim Q As String = "/mods/search?gameId=" & CurseForgeGameId.ToString() & "&pageSize=1"

        Dim E1 As String = ""
        Dim B1 As String = CfHttpGet(CfMirrorApi & Q, "", E1)
        Sb.Append("国内镜像 mod.mcimirror.top（不需要 key）：").Append(If(B1 IsNot Nothing, "可用", "不可用 —— " & E1))

        Dim Src As String = ""
        Dim K As String = CurseForgeKeyEx(Src)
        If K.Length = 0 Then
            Sb.Append(Environment.NewLine).Append("官方接口 api.curseforge.com：没配 API Key，跳过（有镜像就够了，不用配）")
        Else
            Dim Masked As String
            If K.Length <= 8 Then
                Masked = "****"
            Else
                Masked = K.Substring(0, 4) & "****" & K.Substring(K.Length - 4)
            End If
            Dim E2 As String = ""
            Dim B2 As String = CfHttpGet(CfOfficialApi & Q, K, E2)
            Sb.Append(Environment.NewLine).Append("官方接口（Key 来自 ").Append(Src).Append("，值 ").Append(Masked).Append("）：").Append(If(B2 IsNot Nothing, "可用", "不可用 —— " & E2))
        End If

        If B1 Is Nothing AndAlso K.Length = 0 Then
            Sb.Append(Environment.NewLine).Append("两条路都不通，暂时用 Modrinth 替代。")
        End If
        Return Sb.ToString()
    End Function

    Private Function ToolSearchCurseForge(ArgsJson As String) As String
        Dim Q As String = If(JsonStringField(ArgsJson, "query"), "").Trim()
        If Q.Length = 0 Then Return "缺少 query 参数。"
        Dim PType As String = NormProjectType(JsonStringField(ArgsJson, "project_type"))
        Dim Loader As String = If(JsonStringField(ArgsJson, "loader"), "").Trim().ToLowerInvariant()
        Dim Gv As String = If(JsonStringField(ArgsJson, "game_version"), "").Trim()
        Dim Lim As Integer = JsonIntField(ArgsJson, "limit", 5)
        If Lim < 1 Then Lim = 1
        If Lim > 10 Then Lim = 10

        Dim Qs As New List(Of String)
        Qs.Add("gameId=" & CurseForgeGameId.ToString())
        Qs.Add("classId=" & CfClassId(PType))
        Qs.Add("pageSize=" & Lim.ToString())
        Qs.Add("sortField=2") '下载量
        Qs.Add("sortOrder=desc")
        Qs.Add("searchFilter=" & Uri.EscapeDataString(Q))
        If Gv.Length > 0 Then Qs.Add("gameVersion=" & Uri.EscapeDataString(Gv))
        Dim Lt As String = CfLoaderType(Loader)
        If Lt.Length > 0 Then Qs.Add("modLoaderType=" & Lt)

        Dim Err As String = ""
        Dim Body As String = CfGet("/mods/search?" & String.Join("&", Qs.ToArray()), Err)
        If Body Is Nothing Then Return Err
        Dim Arr = JArr(JsonObjectOf(Body), "data")
        If Arr Is Nothing OrElse Arr.Length = 0 Then
            Return "CurseForge 上没有搜到「" & Q & "」的结果（可以去掉 loader / game_version 再试）。"
        End If

        Dim Sb As New StringBuilder()
        Sb.Append("CurseForge 搜索「").Append(Q).Append("」（类型 ").Append(PType)
        If Loader.Length > 0 Then Sb.Append("，加载器 ").Append(Loader)
        If Gv.Length > 0 Then Sb.Append("，版本 ").Append(Gv)
        Sb.Append("）：")
        Dim I As Integer = 0
        For Each X In Arr
            Dim Md = TryCast(X, Dictionary(Of String, Object))
            If Md Is Nothing Then Continue For
            I += 1
            Sb.Append(Environment.NewLine).Append(I).Append(". ").Append(JStr(Md, "name"))
            Sb.Append(" [id ").Append(JStr(Md, "id")).Append("]")
            Dim Slug As String = JStr(Md, "slug").Trim()
            If Slug.Length > 0 Then Sb.Append(" /").Append(Slug)
            Sb.Append(" ↓").Append(JStr(Md, "downloadCount"))
            Dim Desc As String = Shorten(JStr(Md, "summary"), 110)
            If Desc.Length > 0 Then Sb.Append(Environment.NewLine).Append("   ").Append(Desc)
        Next
        If I = 0 Then Return "没有搜到可用的结果。"
        Sb.Append(Environment.NewLine).Append("（要下载就调 download_curseforge，project 传方括号里的数字 id 或 /slug；批量就用 download_mods_batch 并把 source 设为 curseforge）")
        If Host IsNot Nothing Then Host.LogInfo("AI 工具 search_curseforge → " & Q & "，" & I & " 条")
        Return Sb.ToString()
    End Function

    Private Function ToolDownloadCurseForge(ArgsJson As String) As String
        Dim PType As String = NormProjectType(JsonStringField(ArgsJson, "project_type"))
        Dim Loader As String = If(JsonStringField(ArgsJson, "loader"), "").Trim().ToLowerInvariant()
        Dim Gv As String = If(JsonStringField(ArgsJson, "game_version"), "").Trim()
        Dim DoInstall As Boolean = True
        Dim Iv As Boolean? = JsonBoolField(ArgsJson, "install")
        If Iv.HasValue Then DoInstall = Iv.Value
        Return DownloadOneCurseForge(JsonStringField(ArgsJson, "project"), PType, Loader, Gv, DoInstall, If(JsonStringField(ArgsJson, "file_id"), "").Trim(),
                                      If(JsonStringField(ArgsJson, "instance_name"), "").Trim())
    End Function

    ''' <summary>下载并（可选）安装一个 CurseForge 项目，成功时返回文本以「已下载」开头（批量流程靠它判成败）。</summary>
    Private Function DownloadOneCurseForge(PidRaw As String, PType As String, Loader As String, Gv As String, DoInstall As Boolean, PinnedFile As String, Optional InstNameWant As String = "") As String
        Dim Pinned As String = If(PinnedFile, "").Trim()
        Dim Err As String = ""
        Dim ModId As String = CfResolveModId(PidRaw, Pinned, Err)
        If ModId.Length = 0 Then Return Err

        Dim Pick As Dictionary(Of String, Object) = CfPickFile(ModId, Loader, Gv, Pinned, Err)
        If Pick Is Nothing Then Return Err

        Dim FileId As String = JStr(Pick, "id").Trim()
        Dim Fn As String = JStr(Pick, "fileName").Trim()
        Dim Dl As String = JStr(Pick, "downloadUrl").Trim()
        If Dl.Length = 0 Then
            '作者关掉了 API 直链时 downloadUrl 是 null，退回 CDN 规则拼出来
            Dl = CfCdnUrl(FileId, Fn)
            If Dl.Length = 0 Then Return "CurseForge 没有给出这个文件的下载地址，也没法按 id 拼出 CDN 链接。"
        End If

        '下载地址依次试：原始 CDN → 镜像同路径 → 按 fileId 拼出来的 CDN → 拼出来的镜像地址。
        '国内直连 forgecdn 有时很慢或被挡，镜像那条通常最快。
        Dim Saved As String = ""
        Dim Tried As New List(Of String)
        Tried.Add(Dl)
        Dim Mu As String = CfMirrorUrl(Dl)
        If Mu <> Dl Then Tried.Add(Mu)
        Dim Cu As String = CfCdnUrl(FileId, Fn)
        If Cu.Length > 0 Then
            If Not Tried.Contains(Cu) Then Tried.Add(Cu)
            Dim Mc As String = CfMirrorUrl(Cu)
            If Mc <> Cu AndAlso Not Tried.Contains(Mc) Then Tried.Add(Mc)
        End If

        'CurseForge 的 hashes 里 algo=1 是 SHA-1，fileLength 是字节数；有就交给宿主校验
        Dim Sha1 As String = ""
        Dim Hs = JArr(Pick, "hashes")
        If Hs IsNot Nothing Then
            For Each H In Hs
                Dim Hd = TryCast(H, Dictionary(Of String, Object))
                If Hd Is Nothing Then Continue For
                If JStr(Hd, "algo").Trim() = "1" Then
                    Sha1 = JStr(Hd, "value").Trim()
                    Exit For
                End If
            Next
        End If
        Dim FSize As Long = JLng(Pick, "fileLength")

        '整合包是新建实例：名字用项目名、封面用项目图标（与在启动器里下载整合包时一致）
        Dim InstName As String = ""
        Dim Logo As String = ""
        If PType = "modpack" Then
            Dim T As String = ""
            Dim I As String = ""
            ModpackMeta("curseforge", ModId, T, I)
            InstName = CleanInstanceName(If(If(InstNameWant, "").Trim().Length > 0, InstNameWant, T))
            Logo = SaveRemoteImage(I, "curseforge-" & ModId)
        End If

        Dim InstFolder As String = FolderForType(PType)
        Dim InInst As Boolean = False
        Dim InstErr As String = ""
        If PType <> "modpack" AndAlso DoInstall AndAlso InstFolder.Length > 0 Then
            'API v10：挨个地址让宿主直接下进实例（宿主自己会重写镜像、重试、校验）
            For Each U In Tried
                Dim Rel As String = ""
                Dim Ie As String = FetchIntoInstance(U, Fn, InstFolder, Sha1, FSize, Rel)
                If Ie.Length = 0 Then
                    InInst = True
                    Saved = Rel
                    Exit For
                End If
                InstErr = Ie
            Next
        End If

        If Not InInst Then
            Dim Fe As String = ""
            For Each U In Tried
                Dim E As String = FetchTo(U, Fn, Saved)
                If E.Length = 0 Then
                    Fe = ""
                    Exit For
                End If
                Fe = E
            Next
            If Fe.Length > 0 Then
                If InstErr.Length > 0 Then Return InstErr & Environment.NewLine & "退回 data\downloads 也失败了：" & Fe
                Return Fe & Environment.NewLine & "（已依次试过 " & Tried.Count & " 个下载地址）"
            End If
        End If

        Dim Sb As New StringBuilder()
        If InInst Then
            Sb.Append("已下载并装进「").Append(InstanceLabel()).Append("」的 ").Append(InstFolder).Append("：").Append(Path.GetFileName(Saved))
            If FSize > 0 Then Sb.Append("（").Append(FmtSize(FSize)).Append("）")
        Else
            Sb.Append("已下载：").Append(Path.GetFileName(Saved))
            Try
                Sb.Append("（").Append(FmtSize(New FileInfo(Saved).Length)).Append("）")
            Catch ex As Exception
            End Try
        End If
        Sb.Append(Environment.NewLine).Append("项目：").Append(JStr(Pick, "modId").Trim())
        Sb.Append(" · 文件：").Append(If(JStr(Pick, "displayName").Trim(), Fn))
        If JStr(Pick, "fileDate").Trim().Length > 0 Then Sb.Append(" · 发布 ").Append(Shorten(JStr(Pick, "fileDate"), 10))
        AppendInstallResult(Sb, Saved, PType, DoInstall, InstName, Logo, InInst)
        Return Sb.ToString()
    End Function

    ''' <summary>
    ''' 版本名清洗成启动器一定接受的样子（避免安装时弹「输入版本名称」）。
    ''' 规则对齐 PCL 的 ValidateFolderName：非法字符、首尾空格、尾部点、8.3 短名、保留名、长度 100。
    ''' </summary>
    Private Function CleanInstanceName(Want As String) As String
        Dim S As String = If(Want, "").Trim()
        If S.Length = 0 Then Return ""
        Dim Bad As New StringBuilder()
        Bad.Append(Path.GetInvalidFileNameChars())
        Bad.Append("!;%")
        For Each C As Char In Bad.ToString().ToCharArray()
            S = S.Replace(C.ToString(), "")
        Next
        S = System.Text.RegularExpressions.Regex.Replace(S, ".{2,}~\d", "")
        S = S.Trim().TrimEnd("."c)
        If S.Length > 60 Then S = S.Substring(0, 60).Trim().TrimEnd("."c)
        For Each R As String In New String() {"CON", "PRN", "AUX", "CLOCK$", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"}
            If String.Equals(S, R, StringComparison.OrdinalIgnoreCase) Then S &= "_pack"
        Next
        Return S.Trim()
    End Function

    ''' <summary>把远程图片落到 data\logos 并返回本地路径（失败返回空串）。启动器的 Logo 参数要的是本地文件。</summary>
    Private Function SaveRemoteImage(Url As String, Key As String) As String
        Dim U As String = If(Url, "").Trim()
        If U.Length = 0 Then Return ""
        If Not (U.StartsWith("https://", StringComparison.OrdinalIgnoreCase) OrElse U.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) Then Return ""
        If Host Is Nothing Then Return ""
        Dim Dir As String = Path.Combine(Host.DataFolder, "logos")
        Try
            If Not Directory.Exists(Dir) Then Directory.CreateDirectory(Dir)
        Catch ex As Exception
            Return ""
        End Try
        '扩展名只用来看「能不能当图标」。PCL 加载时是按文件头识别 WebP 的（MyBitmap），
        '而且 ModpackInstall 会统一复制成 PCL\Logo.png，所以 webp 内容存成 .png 也能正常显示。
        Dim Ext As String = Path.GetExtension(FileNameFromUrl(U))
        If Ext Is Nothing Then Ext = ""
        Ext = Ext.ToLowerInvariant()
        If Ext <> ".png" AndAlso Ext <> ".jpg" AndAlso Ext <> ".jpeg" AndAlso Ext <> ".gif" AndAlso Ext <> ".webp" Then Ext = ".png"
        If Ext = ".webp" Then Ext = ".png"
        Dim Name As String = SafeFileName(If(Key, ""), "")
        If Name.Length = 0 Then Name = "pack"
        Dim Full As String = Path.Combine(Dir, Name & Ext)
        Try
            If File.Exists(Full) AndAlso New FileInfo(Full).Length > 1024 Then Return Full
        Catch ex As Exception
        End Try
        Try
            If Host.HttpDownloadResult(U, Full) = PCL.PluginHost.PluginDownloadResult.Ok Then Return Full
        Catch ex As Exception
        End Try
        'CDN 直连不行时走镜像（CurseForge 的 logo 在 media.forgecdn.net）
        Dim Alt As String = CfMirrorUrl(U)
        If Alt.Length > 0 AndAlso Alt <> U Then
            Try
                If Host.HttpDownloadResult(Alt, Full) = PCL.PluginHost.PluginDownloadResult.Ok Then Return Full
            Catch ex As Exception
            End Try
        End If
        Return ""
    End Function

    ''' <summary>取项目的显示名与图标地址（整合包用来当实例名和封面）。取不到就给空串，不报错。</summary>
    Private Sub ModpackMeta(Source As String, Id As String, ByRef Title As String, ByRef Icon As String)
        Title = ""
        Icon = ""
        If Id Is Nothing OrElse Id.Trim().Length = 0 Then Return
        If Source = "curseforge" Then
            Dim Err As String = ""
            Dim Body As String = CfGet("/mods/" & Uri.EscapeDataString(Id.Trim()), Err)
            If Body Is Nothing Then Return
            Dim Root As Object = JsonObjectOf(Body)
            If Root Is Nothing Then Return
            Dim D = TryCast(Root("data"), Dictionary(Of String, Object))
            If D Is Nothing Then Return
            Title = JStr(D, "name").Trim()
            If D.ContainsKey("logo") Then
                Dim Lg = TryCast(D("logo"), Dictionary(Of String, Object))
                If Lg IsNot Nothing Then
                    '优先原图，缩略图只有 256px，做封面会糊
                    Icon = JStr(Lg, "url").Trim()
                    If Icon.Length = 0 Then Icon = JStr(Lg, "thumbnailUrl").Trim()
                End If
            End If
        Else
            Dim Raw As String = ""
            Try
                Raw = Host.HttpGetString(ModrinthApi & "/project/" & Uri.EscapeDataString(Id.Trim()))
            Catch ex As Exception
                Return
            End Try
            Dim D = JsonObjectOf(Raw)
            If D Is Nothing Then Return
            Title = JStr(D, "title").Trim()
            Icon = JStr(D, "icon_url").Trim()
        End If
    End Sub

    ''' <summary>
    ''' API v9：把整合包交给宿主安装（等价于用户在启动器里点「安装整合包」）。
    ''' 会新建一个实例；同步等到装完才返回。返回 0=成功，1=取消，2=失败，3=已丢到后台（仅 UI 线程调用时）。
    ''' </summary>
    Private Function InstallModpackFile(File As String, InstName As String, LogoPath As String, ByRef Detail As String) As Integer
        Detail = ""
        If String.IsNullOrWhiteSpace(File) OrElse Not System.IO.File.Exists(File) Then
            Detail = "找不到整合包文件：" & If(File, "")
            Return 2
        End If
        Dim Ext As String = Path.GetExtension(File)
        If Ext Is Nothing Then Ext = ""
        Ext = Ext.TrimStart("."c).ToLowerInvariant()
        If Ext <> "zip" AndAlso Ext <> "mrpack" Then
            Detail = "整合包只能是 zip / mrpack（PCL 打不开 rar）：" & Path.GetFileName(File)
            Return 2
        End If
        If Host Is Nothing Then
            Detail = "插件还没连上启动器。"
            Return 2
        End If

        '先让宿主认一遍：格式、包内名字、能不能静默安装，这里一次拿全（只读，几十毫秒）
        Dim Info As PCL.PluginModpackInfo = Nothing
        Try
            Info = Host.InspectModpack(File)
        Catch ex As Exception
            Info = Nothing
        End Try
        If Info Is Nothing Then
            Detail = "启动器认不出这个整合包的格式：" & Path.GetFileName(File)
            Return 2
        End If
        If Not Info.CanInstallSilently Then
            Detail = "这个包（" & Info.Format & "）不能静默安装：它需要手动选一个空目录，请在「下载 → 安装整合包」里装。"
            Return 2
        End If

        '版本名：调用方给的（项目名）优先，其次包内声明的；清洗到启动器一定接受
        Dim Name As String = CleanInstanceName(If(If(InstName, "").Trim().Length > 0, InstName, Info.Name))
        Dim Lg As String = ""
        If LogoPath IsNot Nothing AndAlso System.IO.File.Exists(LogoPath) Then Lg = LogoPath
        Dim Opts As New PCL.PluginModpackOptions With {.InstanceName = Name, .Logo = Lg}

        'UI 线程上同步等会卡住安装流程，只能丢后台
        Dim OnUi As Boolean = False
        Try
            Dim Disp As System.Windows.Threading.Dispatcher = System.Windows.Application.Current.Dispatcher
            OnUi = Disp IsNot Nothing AndAlso Disp.CheckAccess()
        Catch ex As Exception
        End Try
        If OnUi Then
            Dim Th As New Threading.Thread(
                Sub()
                    Try
                        Host.InstallModpack(File, Opts)
                    Catch ex As Exception
                    End Try
                End Sub)
            Th.IsBackground = True
            Th.Start()
            Detail = "已在后台开始安装，进度看「任务管理」；装完会在版本列表里多一个实例。"
            ' UI 线程上只能丢后台，工具立刻返回 —— 这里必须记一笔，否则模型会马上去下 mod
            MarkInstallPending(Name)
            Return 3
        End If

        LastPackResult = Nothing
        Dim Task As PCL.ModPlugin.PluginTaskHandle = Nothing
        Try
            Task = Host.InstallModpack(File, Opts)
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Detail = "启动器没有开始安装：" & T.Message
            Return 2
        End Try
        If Task Is Nothing Then
            Detail = "启动器没有返回安装任务。"
            Return 2
        End If

        '进度同时写在「任务管理」里，这里只更新状态栏，不用轮询太密
        Dim LastPct As Integer = -1
        Do While Task.State = PCL.ModBase.LoadState.Loading OrElse Task.State = PCL.ModBase.LoadState.Waiting
            Dim Pct As Integer = CInt(Task.Progress * 100)
            If Pct <> LastPct Then
                LastPct = Pct
                Try
                    SetBusyNote("安装整合包 " & Pct & "%（大包要几分钟）")
                Catch ex As Exception
                End Try
            End If
            Threading.Thread.Sleep(300)
        Loop
        Try
            SetBusyNote("")
        Catch ex As Exception
        End Try

        Dim Res As PCL.PluginModpackResult = LastPackResult
        Select Case Task.State
            Case PCL.ModBase.LoadState.Finished
                If Res IsNot Nothing AndAlso Res.InstanceName IsNot Nothing AndAlso Res.InstanceName.Length > 0 Then
                    Detail = Res.InstanceName
                Else
                    Detail = Name
                End If
                ' 新实例装好了，但当前选中的还是旧的；后面要往里加 mod 得先切过去
                LastInstalled = Detail
                Return 0
            Case PCL.ModBase.LoadState.Canceled
                Detail = "安装被取消。"
                Return 1
            Case Else
                If Res IsNot Nothing AndAlso Res.Message IsNot Nothing AndAlso Res.Message.Length > 0 Then
                    Detail = "安装失败：" & Res.Message
                Else
                    Detail = "安装失败：启动器没给出原因，去「任务管理」或日志里看。"
                End If
                Return 2
        End Select
    End Function

    ''' <summary>把已下载的文件装进当前实例，并把结果追加到 Sb（Modrinth / CurseForge 下载共用）。</summary>
    Private Sub AppendInstallResult(Sb As StringBuilder, Saved As String, PType As String, DoInstall As Boolean,
                                    Optional InstName As String = "", Optional LogoPath As String = "",
                                    Optional AlreadyInInstance As Boolean = False)
        Dim Folder As String = FolderForType(PType)
        If Not DoInstall Then
            Sb.Append(Environment.NewLine).Append("按你的要求只下载，没有装进实例。可用 install_download 再装。")
            Return
        End If
        If AlreadyInInstance Then
            'API v10 由宿主直接下进实例，文件已在目标目录，不需要再复制
            Sb.Append(Environment.NewLine).Append("文件由启动器直接下载到实例内并完成校验，不需要再复制；进度可在「任务管理」里回看。")
            Return
        End If
        If PType = "modpack" Then
            Sb.Append(Environment.NewLine).Append("开始交给启动器安装整合包（会新建一个实例，大包可能要好几分钟）……")
            Dim Detail As String = ""
            Dim Rc As Integer = InstallModpackFile(Saved, InstName, LogoPath, Detail)
            Sb.Append(Environment.NewLine)
            Select Case Rc
                Case 0
                    Sb.Append("整合包已装好")
                    If Detail.Length > 0 Then Sb.Append("，实例名「").Append(Detail).Append("」")
                    Sb.Append("；去启动页刷新一下版本列表，选中它就能启动。")
                    If LogoPath IsNot Nothing AndAlso LogoPath.Length > 0 Then Sb.Append("封面图已设好。")
                Case 1
                    Sb.Append("整合包安装被取消。").Append(If(Detail.Length > 0, Detail, ""))
                Case 3
                    Sb.Append(Detail)
                Case Else
                    Sb.Append("整合包安装没完成：").Append(If(Detail.Length > 0, Detail, "原因未知")).Append(Environment.NewLine)
                    Sb.Append("文件还在 data\downloads，可以稍后重试，或者直接在启动器里把「").Append(Path.GetFileName(Saved)).Append("」拖进窗口安装。")
            End Select
            If Rc = 0 AndAlso Host IsNot Nothing Then Host.LogInfo("AI 安装整合包完成：" & Path.GetFileName(Saved))
            Return
        End If
        If Folder.Length = 0 Then
            Sb.Append(Environment.NewLine).Append("这个类型没有对应的安装目录，文件已在 data\downloads。")
            Return
        End If
        Dim Inst As PCL.PluginInstanceSnapshot = Nothing
        Try
            Inst = Host.InstanceInfo
        Catch ex As Exception
        End Try
        If Inst Is Nothing OrElse Not Inst.Available Then
            Sb.Append(Environment.NewLine).Append("当前没有选中的实例，所以没有安装。用 select_instance 选一个再 install_download。")
            Return
        End If
        If Folder = "mods" AndAlso Not Inst.Modable Then
            Sb.Append(Environment.NewLine).Append("注意：当前实例「").Append(Inst.Name).Append("」标记为不可装模组（原版或整合包锁定）。")
        End If
        If Host.InstallContent(Saved, Folder) Then
            Sb.Append(Environment.NewLine).Append("已装进「").Append(Inst.Name).Append("」的 ").Append(Folder).Append(" 目录。")
            If Host IsNot Nothing Then Host.LogInfo("AI 下载并安装：" & Path.GetFileName(Saved) & " → " & Inst.Name & "\" & Folder)
        Else
            Sb.Append(Environment.NewLine).Append("安装失败：宿主返回 False（实例目录不可写，或实例配置不完整）。文件仍在 data\downloads。")
        End If
    End Sub


    Private Function DownloadsDir() As String
        If Host Is Nothing Then Return Nothing
        Return Path.Combine(Host.DataFolder, "downloads")
    End Function

    Private Function FmtSize(N As Long) As String
        If N >= 1073741824L Then Return (N / 1073741824.0).ToString("0.0") & " GB"
        If N >= 1048576L Then Return (N / 1048576.0).ToString("0.0") & " MB"
        If N >= 1024L Then Return (N / 1024.0).ToString("0") & " KB"
        Return N.ToString() & " B"
    End Function

    Private Function ValidFolder(F As String) As Boolean
        Return F = "mods" OrElse F = "resourcepacks" OrElse F = "shaderpacks" OrElse F = "datapacks"
    End Function

    ''' <summary>文件名洗成安全单段：无路径分隔符、无 Windows 非法字符、长度受限。</summary>
    Private Function SafeFileName(Want As String, Url As String) As String
        Dim N As String = If(Want, "").Trim()
        If N.Length = 0 Then N = FileNameFromUrl(Url)
        Dim Slash As Integer = Math.Max(N.LastIndexOf("\"c), N.LastIndexOf("/"c))
        If Slash >= 0 Then N = N.Substring(Slash + 1)
        For Each Bad As String In New String() {"\", "/", ":", "*", "?", """", "<", ">", "|"}
            N = N.Replace(Bad, "")
        Next
        N = N.Trim().TrimEnd("."c)
        If N.Length > 100 Then
            Dim Ext As String = ""
            Dim Dot As Integer = N.LastIndexOf("."c)
            If Dot > 0 AndAlso N.Length - Dot <= 12 Then Ext = N.Substring(Dot)
            N = N.Substring(0, Math.Min(100 - Ext.Length, N.Length)) & Ext
        End If
        If N = "." OrElse N = ".." Then Return ""
        Return N
    End Function

    Private Function FileNameFromUrl(Url As String) As String
        Try
            Dim U As String = If(Url, "")
            Dim Q As Integer = U.IndexOf("?"c)
            If Q >= 0 Then U = U.Substring(0, Q)
            Dim H As Integer = U.IndexOf("#"c)
            If H >= 0 Then U = U.Substring(0, H)
            Dim Slash As Integer = U.LastIndexOf("/"c)
            If Slash < 0 OrElse Slash = U.Length - 1 Then Return ""
            Return Uri.UnescapeDataString(U.Substring(Slash + 1))
        Catch ex As Exception
            Return ""
        End Try
    End Function

    Private Function HostNameOf(Url As String) As String
        Try
            Return New Uri(Url).Host
        Catch ex As Exception
            Return Url
        End Try
    End Function

    ''' <summary>下载到 data\downloads。成功返回空串，失败返回给用户看的原因。</summary>
    Private Function FetchTo(Url As String, WantName As String, ByRef SavedPath As String) As String
        SavedPath = ""
        Dim U As String = If(Url, "").Trim()
        If U.Length = 0 Then Return "缺少下载地址。"
        If U.Length > 800 Then Return "下载地址太长（超过 800 字符）。"
        If Not (U.StartsWith("https://", StringComparison.OrdinalIgnoreCase) OrElse U.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) Then
            Return "只允许 http/https 直链，拒绝：" & Shorten(U, 60)
        End If
        Dim Name As String = SafeFileName(WantName, U)
        If Name.Length = 0 Then Return "无法从地址推断出文件名，请用 filename 参数显式指定。"
        Dim Dir As String = DownloadsDir()
        Try
            If Not Directory.Exists(Dir) Then Directory.CreateDirectory(Dir)
        Catch ex As Exception
            Return "创建下载目录失败：" & ex.Message
        End Try
        Dim Full As String = Path.Combine(Dir, Name)
        Dim R As PCL.PluginHost.PluginDownloadResult
        Try
            R = Host.HttpDownloadResult(U, Full)
        Catch ex As Exception
            Return "下载失败：" & ex.Message
        End Try
        If R = PCL.PluginHost.PluginDownloadResult.Ok Then
            SavedPath = Full
            Return ""
        End If
        Select Case R
            Case PCL.PluginHost.PluginDownloadResult.NotPermitted
                Return "插件没有 net 权限。"
            Case PCL.PluginHost.PluginDownloadResult.HostDenied
                Return "宿主拒绝了主机 " & HostNameOf(U) & "（不在 net.hosts 白名单里，也没有拿到运行时授权）。"
            Case PCL.PluginHost.PluginDownloadResult.PathDenied
                Return "宿主拒绝了写入路径：" & Full
            Case PCL.PluginHost.PluginDownloadResult.TooLarge
                Return "文件超过 512 MB 上限，已中止。"
            Case Else
                Return "下载失败（网络错误、404 或磁盘写入失败）：" & Shorten(U, 90)
        End Select
    End Function

    ''' <summary>
    ''' API v10：让宿主把文件直接下进当前实例（镜像重写、重试、SHA-1 校验、任务管理进度都由启动器负责）。
    ''' 成功返回空串，失败返回给人看的原因；成功时 RelativePath 是相对实例根的落点。
    ''' </summary>
    Private Function FetchIntoInstance(Url As String, WantName As String, Folder As String, Sha1 As String, Size As Long, ByRef RelativePath As String) As String
        RelativePath = ""
        Dim U As String = If(Url, "").Trim()
        If U.Length = 0 Then Return "缺少下载地址。"
        If Folder.Length = 0 Then Return "没有指定安装目录。"
        If Host Is Nothing Then Return "插件还没连上启动器。"
        Dim Name As String = SafeFileName(WantName, U)
        If Name.Length = 0 Then Return "无法从地址推断出文件名。"

        Dim Inst As PCL.PluginInstanceSnapshot = Nothing
        Try
            Inst = Host.InstanceInfo
        Catch ex As Exception
        End Try
        If Inst Is Nothing OrElse Not Inst.Available Then
            Return "当前没有选中的实例，没法直接装进去（先用 select_instance 选一个）。"
        End If

        Dim Rel As String = Folder & "\" & Name
        Dim Req As New PCL.PluginDownloadRequest With {
            .Url = U,
            .RelativePath = Rel,
            .Sha1 = If(Sha1, "").Trim(),
            .Size = If(Size > 0, Size, 0L)
        }
        Dim Files As New List(Of PCL.PluginDownloadRequest)
        Files.Add(Req)
        '0 = 不额外设限（仍是宿主 16 GiB 硬上限）；单个整合包不会走到这里
        Dim Opts As New PCL.PluginDownloadOptions With {.Overwrite = True, .TotalLimitBytes = 0}

        Dim Task As PCL.ModPlugin.PluginTaskHandle = Nothing
        Try
            Task = Host.DownloadFiles(Files, Opts)
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "启动器没有开始下载：" & T.Message
        End Try
        If Task Is Nothing Then Return "启动器没有返回下载任务。"
        Try
            Task.Wait()
        Catch ex As Exception
            '单文件成败以 LastDownloadResults 为准
        End Try

        Dim Hit As PCL.PluginDownloadItemResult = Nothing
        Try
            For Each R In Host.LastDownloadResults
                If String.Equals(If(R.RelativePath, ""), Rel, StringComparison.OrdinalIgnoreCase) Then
                    Hit = R
                    Exit For
                End If
            Next
            If Hit Is Nothing Then
                For Each R In Host.LastDownloadResults
                    Dim Rp As String = If(R.RelativePath, "")
                    If Rp.EndsWith("\" & Name, StringComparison.OrdinalIgnoreCase) OrElse Rp.EndsWith("/" & Name, StringComparison.OrdinalIgnoreCase) Then
                        Hit = R
                        Exit For
                    End If
                Next
            End If
        Catch ex As Exception
        End Try
        If Hit Is Nothing Then Return "下载任务结束了，但没拿到结果（去「任务管理」看看）。"

        If Hit.Status = "Ok" OrElse Hit.Status = "Skipped" Then
            RelativePath = If(If(Hit.RelativePath, "").Length > 0, Hit.RelativePath, Rel)
            Return ""
        End If
        Select Case Hit.Status
            Case "Denied"
                Return "宿主拒绝下载：" & If(If(Hit.Message, "").Length > 0, Hit.Message, "主机不在 net.hosts 白名单，或路径越出实例目录。")
            Case "TooLarge"
                Return "文件超过下载上限：" & If(Hit.Message, "")
            Case "HashMismatch"
                Return "下载完成但 SHA-1 校验不符，文件已丢弃：" & If(Hit.Message, "")
            Case "Canceled"
                Return "下载被取消。"
            Case Else
                Return "下载失败（" & Hit.Status & "）：" & If(Hit.Message, "")
        End Select
    End Function

    ''' <summary>取字典里的整数（JSON 数字反序列化后可能是 Integer/Long/Decimal/Double）。</summary>
    Private Function JLng(D As Dictionary(Of String, Object), K As String) As Long
        If D Is Nothing Then Return 0
        Dim V As Object = Nothing
        If Not D.TryGetValue(K, V) OrElse V Is Nothing Then Return 0
        Try
            Return CLng(V)
        Catch ex As Exception
            Return 0
        End Try
    End Function

    ' ---------- 极简 JSON 读取（System.Web.Extensions 的 JavaScriptSerializer） ----------

    Private Function JsonObjectOf(Text As String) As Dictionary(Of String, Object)
        Try
            Dim Ser As New JavaScriptSerializer()
            Ser.MaxJsonLength = 8 * 1024 * 1024
            Return TryCast(Ser.DeserializeObject(Text), Dictionary(Of String, Object))
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    Private Function JsonArrayOf(Text As String) As Object()
        Try
            Dim Ser As New JavaScriptSerializer()
            Ser.MaxJsonLength = 8 * 1024 * 1024
            Return TryCast(Ser.DeserializeObject(Text), Object())
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    Private Function JStr(D As Dictionary(Of String, Object), K As String) As String
        If D Is Nothing Then Return ""
        Dim V As Object = Nothing
        If Not D.TryGetValue(K, V) OrElse V Is Nothing Then Return ""
        Return V.ToString()
    End Function

    Private Function JBool(D As Dictionary(Of String, Object), K As String) As Boolean
        If D Is Nothing Then Return False
        Dim V As Object = Nothing
        If Not D.TryGetValue(K, V) OrElse V Is Nothing Then Return False
        Try
            Return CBool(V)
        Catch ex As Exception
            Return False
        End Try
    End Function

    Private Function JArr(D As Dictionary(Of String, Object), K As String) As Object()
        If D Is Nothing Then Return Nothing
        Dim V As Object = Nothing
        If Not D.TryGetValue(K, V) OrElse V Is Nothing Then Return Nothing
        Return TryCast(V, Object())
    End Function

    Private Function NormProjectType(T As String) As String
        Select Case If(T, "").Trim().ToLowerInvariant()
            Case "", "mod", "mods"
                Return "mod"
            Case "resourcepack", "resourcepacks", "resource_pack", "packs"
                Return "resourcepack"
            Case "shader", "shaders", "shaderpack", "shaderpacks"
                Return "shader"
            Case "datapack", "datapacks"
                Return "datapack"
            Case "modpack", "modpacks"
                Return "modpack"
        End Select
        Return "mod"
    End Function

    Private Function FolderForType(PType As String) As String
        Select Case PType
            Case "mod"
                Return "mods"
            Case "resourcepack"
                Return "resourcepacks"
            Case "shader"
                Return "shaderpacks"
            Case "datapack"
                Return "datapacks"
        End Select
        Return ""
    End Function

    ''' <summary>当前选中实例的显示名；没有就返回「当前实例」。</summary>
    Private Function InstanceLabel() As String
        Try
            Dim I = Host.InstanceInfo
            If I IsNot Nothing AndAlso I.Available AndAlso Not String.IsNullOrEmpty(I.Name) Then Return I.Name
        Catch ex As Exception
        End Try
        Return "当前实例"
    End Function

    Private Function ToolSearchModrinth(ArgsJson As String) As String
        Dim Q As String = If(JsonStringField(ArgsJson, "query"), "").Trim()
        If Q.Length = 0 Then Return "缺少 query 参数。"
        Dim PType As String = NormProjectType(JsonStringField(ArgsJson, "project_type"))
        Dim Loader As String = If(JsonStringField(ArgsJson, "loader"), "").Trim().ToLowerInvariant()
        Dim Gv As String = If(JsonStringField(ArgsJson, "game_version"), "").Trim()
        Dim Lim As Integer = JsonIntField(ArgsJson, "limit", 5)
        If Lim < 1 Then Lim = 1
        If Lim > 10 Then Lim = 10

        Dim Url As String = SearchUrl(Q, PType, Loader, Gv, Lim)

        Dim Raw As String
        Try
            Raw = Host.HttpGetString(Url)
        Catch ex As Exception
            Return "请求 Modrinth 失败：" & ex.Message
        End Try
        Dim D = JsonObjectOf(Raw)
        If D Is Nothing Then Return "Modrinth 返回了无法解析的响应：" & Shorten(Raw, 200)
        Dim Hits = JArr(D, "hits")
        If Hits Is Nothing OrElse Hits.Length = 0 Then
            Return "没有搜到「" & Q & "」的结果（可以去掉 loader / game_version 再试）。"
        End If
        Dim Sb As New StringBuilder()
        Sb.Append("Modrinth 搜索「").Append(Q).Append("」（类型 ").Append(PType)
        If Loader.Length > 0 Then Sb.Append("，加载器 ").Append(Loader)
        If Gv.Length > 0 Then Sb.Append("，版本 ").Append(Gv)
        Sb.Append("）：")
        Dim I As Integer = 0
        For Each H In Hits
            Dim Hd = TryCast(H, Dictionary(Of String, Object))
            If Hd Is Nothing Then Continue For
            I += 1
            Sb.Append(Environment.NewLine).Append(I).Append(". ").Append(JStr(Hd, "title"))
            Sb.Append(" [").Append(JStr(Hd, "slug")).Append("]")
            Sb.Append(" ↓").Append(JStr(Hd, "downloads"))
            Dim Desc As String = Shorten(JStr(Hd, "description"), 110)
            If Desc.Length > 0 Then Sb.Append(Environment.NewLine).Append("   ").Append(Desc)
        Next
        If I = 0 Then Return "没有搜到可用的结果。"
        Sb.Append(Environment.NewLine).Append("（要下载就调 download_modrinth，project 传方括号里的 slug）")
        If Host IsNot Nothing Then Host.LogInfo("AI 工具 search_modrinth → " & Q & "，" & I & " 条")
        Return Sb.ToString()
    End Function

    Private Function ToolDownloadModrinth(ArgsJson As String) As String
        Dim PType As String = NormProjectType(JsonStringField(ArgsJson, "project_type"))
        Dim Loader As String = If(JsonStringField(ArgsJson, "loader"), "").Trim().ToLowerInvariant()
        Dim Gv As String = If(JsonStringField(ArgsJson, "game_version"), "").Trim()
        Dim DoInstall As Boolean = True
        Dim Iv As Boolean? = JsonBoolField(ArgsJson, "install")
        If Iv.HasValue Then DoInstall = Iv.Value
        Return DownloadOneModrinth(JsonStringField(ArgsJson, "project"), PType, Loader, Gv, DoInstall,
                                   If(JsonStringField(ArgsJson, "instance_name"), "").Trim())
    End Function

    ''' <summary>下载并（可选）安装一个 Modrinth 项目，成功时返回文本以「已下载」开头（批量流程靠它判成败）。</summary>
    Private Function DownloadOneModrinth(PidRaw As String, PType As String, Loader As String, Gv As String, DoInstall As Boolean, Optional InstNameWant As String = "") As String
        Dim Pid As String = If(PidRaw, "").Trim()
        If Pid.Length = 0 Then Return "缺少 project 参数（search_modrinth 返回的 slug 或 project id）。"

        Dim Url As String = ModrinthApi & "/project/" & Uri.EscapeDataString(Pid) & "/version"
        Dim Qs As New List(Of String)
        If Loader.Length > 0 Then Qs.Add("loaders=" & Uri.EscapeDataString("[""" & Loader & """]"))
        If Gv.Length > 0 Then Qs.Add("game_versions=" & Uri.EscapeDataString("[""" & Gv & """]"))
        If Qs.Count > 0 Then Url &= "?" & String.Join("&", Qs.ToArray())

        Dim Raw As String
        Try
            Raw = Host.HttpGetString(Url)
        Catch ex As Exception
            Return "请求 Modrinth 版本列表失败：" & ex.Message
        End Try
        Dim Arr = JsonArrayOf(Raw)
        If Arr Is Nothing Then Return "Modrinth 返回了无法解析的版本列表：" & Shorten(Raw, 200)
        If Arr.Length = 0 Then
            Return "「" & Pid & "」没有匹配的版本文件（检查 loader / game_version，或该项目不支持这个组合）。"
        End If

        Dim Pick As Dictionary(Of String, Object) = Nothing
        For Each V In Arr
            Dim Vd = TryCast(V, Dictionary(Of String, Object))
            If Vd Is Nothing Then Continue For
            Dim Fs = JArr(Vd, "files")
            If Fs IsNot Nothing AndAlso Fs.Length > 0 Then
                Pick = Vd
                Exit For
            End If
        Next
        If Pick Is Nothing Then Return "该项目的所有版本都没有可下载的文件。"

        Dim Target As Dictionary(Of String, Object) = Nothing
        Dim Files = JArr(Pick, "files")
        For Each F In Files
            Dim Fd = TryCast(F, Dictionary(Of String, Object))
            If Fd Is Nothing Then Continue For
            If JBool(Fd, "primary") Then
                Target = Fd
                Exit For
            End If
        Next
        If Target Is Nothing Then Target = TryCast(Files(0), Dictionary(Of String, Object))
        If Target Is Nothing Then Return "没有解析出可下载的文件。"

        Dim Dl As String = JStr(Target, "url")
        Dim Fn As String = JStr(Target, "filename")
        If Dl.Length = 0 Then Return "该版本的文件缺少下载地址。"

        'Modrinth 给了 sha1 和大小，交给宿主校验；没给就只校验能否落地
        Dim Sha1 As String = ""
        If Target.ContainsKey("hashes") Then
            Dim Hd = TryCast(Target("hashes"), Dictionary(Of String, Object))
            If Hd IsNot Nothing Then Sha1 = JStr(Hd, "sha1").Trim()
        End If
        Dim FSize As Long = JLng(Target, "size")

        '整合包是新建实例：名字用项目名、封面用项目图标（与在启动器里下载整合包时一致）
        Dim InstName As String = ""
        Dim Logo As String = ""
        If PType = "modpack" Then
            Dim T As String = ""
            Dim I As String = ""
            ModpackMeta("modrinth", Pid, T, I)
            InstName = CleanInstanceName(If(If(InstNameWant, "").Trim().Length > 0, InstNameWant, T))
            Logo = SaveRemoteImage(I, "modrinth-" & Pid)
        End If

        Dim Saved As String = ""
        Dim InInst As Boolean = False
        Dim InstFolder As String = FolderForType(PType)
        If PType <> "modpack" AndAlso DoInstall AndAlso InstFolder.Length > 0 Then
            'API v10：直接让宿主下进实例，省掉「先下到 data\downloads 再复制」这一步
            Dim Rel As String = ""
            Dim Ie As String = FetchIntoInstance(Dl, Fn, InstFolder, Sha1, FSize, Rel)
            If Ie.Length = 0 Then
                InInst = True
                Saved = Rel
            Else
                '写实例失败（没权限 / 没选中实例 / 宿主拒绝）就退回缓存，再用 install_download 的路径装
                Dim E2 As String = FetchTo(Dl, Fn, Saved)
                If E2.Length > 0 Then Return Ie & Environment.NewLine & "退回 data\downloads 也失败了：" & E2
            End If
        Else
            Dim Err As String = FetchTo(Dl, Fn, Saved)
            If Err.Length > 0 Then Return Err
        End If

        Dim Sb As New StringBuilder()
        If InInst Then
            Sb.Append("已下载并装进「").Append(InstanceLabel()).Append("」的 ").Append(InstFolder).Append("：").Append(Path.GetFileName(Saved))
            If FSize > 0 Then Sb.Append("（").Append(FmtSize(FSize)).Append("）")
        Else
            Sb.Append("已下载：").Append(Path.GetFileName(Saved))
            Try
                Sb.Append("（").Append(FmtSize(New FileInfo(Saved).Length)).Append("）")
            Catch ex As Exception
            End Try
        End If
        Sb.Append(Environment.NewLine).Append("项目：").Append(Pid)
        Sb.Append(" · 版本：").Append(JStr(Pick, "version_number"))
        If JStr(Pick, "name").Length > 0 Then Sb.Append("（").Append(JStr(Pick, "name")).Append("）")

        AppendInstallResult(Sb, Saved, PType, DoInstall, InstName, Logo, InInst)
        Return Sb.ToString()
    End Function

    ''' <summary>
    ''' 拼 Modrinth 搜索 URL。facets 是「二维」结构：外层数组装若干组，每组是字符串数组，
    ''' 正确形态只有两层：[["project_type:mod"],["categories:fabric"]]。
    ''' 每组多包一层（[[["a"]],[["b"]]]）或整体少包一层都会 400。
    ''' </summary>
    Private Function SearchUrl(Q As String, PType As String, Loader As String, Gv As String, Lim As Integer) As String
        Dim Facets As New List(Of String)
        Facets.Add("[""project_type:" & PType & """]")
        If Loader.Length > 0 Then Facets.Add("[""categories:" & Loader & """]")
        If Gv.Length > 0 Then Facets.Add("[""versions:" & Gv & """]")
        Dim Url As String = ModrinthApi & "/search?query=" & Uri.EscapeDataString(Q) & "&limit=" & Lim.ToString()
        Url &= "&facets=" & Uri.EscapeDataString("[" & String.Join(",", Facets.ToArray()) & "]")
        Return Url
    End Function

    ''' <summary>按关键词搜一次 Modrinth，取第一条的 slug。出错时返回错误文本，Slug 为空串。</summary>
    Private Function SearchTopSlug(Q As String, PType As String, Loader As String, Gv As String, ByRef Slug As String) As String
        Slug = ""
        Dim Url As String = SearchUrl(Q, PType, Loader, Gv, 1)
        Dim Raw As String
        Try
            Raw = Host.HttpGetString(Url)
        Catch ex As Exception
            Return "请求 Modrinth 失败：" & ex.Message
        End Try
        Dim D = JsonObjectOf(Raw)
        If D Is Nothing Then Return "Modrinth 返回了无法解析的响应"
        Dim Hits = JArr(D, "hits")
        If Hits Is Nothing OrElse Hits.Length = 0 Then Return "没有搜到结果"
        Dim Hd = TryCast(Hits(0), Dictionary(Of String, Object))
        If Hd Is Nothing Then Return "搜索结果格式异常"
        Slug = JStr(Hd, "slug").Trim()
        If Slug.Length = 0 Then Slug = JStr(Hd, "project_id").Trim()
        If Slug.Length = 0 Then Return "搜索结果缺少 slug"
        Return ""
    End Function

    ''' <summary>批量下载：把「装一堆东西」压缩成一轮工具调用，避免逐个调用把工具轮次吃完。</summary>
    Private Function ToolDownloadModsBatch(ArgsJson As String) As String
        Dim D = JsonObjectOf(ArgsJson)
        If D Is Nothing Then Return "参数不是合法 JSON。"

        Dim PType As String = NormProjectType(JsonStringField(ArgsJson, "project_type"))
        Dim Loader As String = If(JsonStringField(ArgsJson, "loader"), "").Trim().ToLowerInvariant()
        Dim Gv As String = If(JsonStringField(ArgsJson, "game_version"), "").Trim()
        Dim DoInstall As Boolean = True
        Dim Iv As Boolean? = JsonBoolField(ArgsJson, "install")
        If Iv.HasValue Then DoInstall = Iv.Value
        Dim Source As String = If(JsonStringField(ArgsJson, "source"), "modrinth").Trim().ToLowerInvariant()
        If Source <> "curseforge" Then Source = "modrinth"

        Dim Items As New List(Of String)
        Dim NeedSearch As New List(Of Boolean)
        Dim PA = JArr(D, "projects")
        If PA IsNot Nothing Then
            For Each X In PA
                Dim S As String = If(X Is Nothing, "", X.ToString()).Trim()
                If S.Length > 0 Then
                    Items.Add(S)
                    NeedSearch.Add(False)
                End If
            Next
        End If
        Dim QA = JArr(D, "queries")
        If QA IsNot Nothing Then
            For Each X In QA
                Dim S As String = If(X Is Nothing, "", X.ToString()).Trim()
                If S.Length > 0 Then
                    Items.Add(S)
                    NeedSearch.Add(True)
                End If
            Next
        End If
        If Items.Count = 0 Then Return "缺少要下载的内容：projects（slug 列表）或 queries（关键词列表）至少给一个。"
        If Items.Count > 20 Then Return "一次最多处理 20 项，当前 " & Items.Count & " 项，分两次调用。"

        Dim OkCount As Integer = 0
        Dim FailCount As Integer = 0
        Dim UsedSearch As Boolean = False
        Dim Sb As New StringBuilder()

        For I As Integer = 0 To Items.Count - 1
            Dim Want As String = Items(I)
            Dim Head As String = "[" & (I + 1) & "/" & Items.Count & "] "
            Dim Label As String = Want

            If NeedSearch(I) AndAlso Source <> "curseforge" Then
                UsedSearch = True
                Dim Slug As String = ""
                Dim SE As String = SearchTopSlug(Want, PType, Loader, Gv, Slug)
                If SE.Length > 0 Then
                    FailCount += 1
                    Sb.Append(Environment.NewLine).Append(Head).Append("「").Append(Want).Append("」搜索失败：").Append(SE)
                    Continue For
                End If
                Want = Slug
            End If

            If Host IsNot Nothing Then Host.LogInfo("AI 批量下载 " & (I + 1) & "/" & Items.Count & "：" & Want)
            SetBusyNote("批量下载 " & (I + 1) & "/" & Items.Count & " · " & Label)

            'CurseForge 那边关键词由 DownloadOneCurseForge 自己去搜（它支持 slug / id / 关键词 / 链接）
            Dim R As String
            If Source = "curseforge" Then
                R = DownloadOneCurseForge(Want, PType, Loader, Gv, DoInstall, "")
            Else
                R = DownloadOneModrinth(Want, PType, Loader, Gv, DoInstall)
            End If
            Dim Good As Boolean = R.StartsWith("已下载", StringComparison.Ordinal)
            If Good Then OkCount += 1 Else FailCount += 1
            Sb.Append(Environment.NewLine).Append(Head).Append(Label)
            If Label <> Want Then Sb.Append(" → ").Append(Want)
            Sb.Append("：").Append(R.Replace(Environment.NewLine, " / "))
        Next

        Dim Sum As New StringBuilder()
        Sum.Append("批量处理完成（来源 ").Append(If(Source = "curseforge", "CurseForge", "Modrinth")).Append("）：成功 ").Append(OkCount).Append(" 个，失败 ").Append(FailCount).Append(" 个（共 ").Append(Items.Count).Append(" 个）。")
        If UsedSearch Then
            Sum.Append(Environment.NewLine).Append("注意：关键词是按搜索结果第一条取的，可能不是你想要的模组；不对的话用 slug 重下。")
        End If
        If FailCount > 0 Then
            Sum.Append(Environment.NewLine).Append("失败项的原因见下面每一条，不要重试第三次以上，也不要自己编造替代链接。")
        End If
        Sum.Append(Sb.ToString())
        Return Sum.ToString()
    End Function

    ''' <summary>在状态行上临时显示一句提示（后台线程调用，切回 UI 线程）。</summary>
    Private Sub SetBusyNote(S As String)
        If Host Is Nothing Then Return
        Try
            Host.RunInUi(Sub()
                             If StatusText IsNot Nothing Then StatusText.Text = S
                         End Sub)
        Catch ex As Exception
        End Try
    End Sub

    Private Function ToolDownloadFile(ArgsJson As String) As String
        Dim U As String = If(JsonStringField(ArgsJson, "url"), "").Trim()
        Dim Nm As String = If(JsonStringField(ArgsJson, "filename"), "").Trim()
        Dim Folder As String = If(JsonStringField(ArgsJson, "install"), "").Trim().ToLowerInvariant()

        'API v10：已知要装进哪个目录时，直接让宿主下进实例（省掉复制，还有镜像与校验）
        Dim DirectErr As String = ""
        If ValidFolder(Folder) Then
            Dim Rel As String = ""
            DirectErr = FetchIntoInstance(U, Nm, Folder, "", 0, Rel)
            If DirectErr.Length = 0 Then
                Return "已下载并装进「" & InstanceLabel() & "」的 " & Folder & "：" & Path.GetFileName(Rel) & Environment.NewLine &
                       "文件由启动器直接写入实例并完成校验，进度可在「任务管理」里回看。"
            End If
            '失败就退回「先缓存再安装」的老路，至少还能拿到文件
        End If

        Dim Saved As String = ""
        Dim Err As String = FetchTo(U, Nm, Saved)
        If Err.Length > 0 Then
            If DirectErr.Length > 0 Then Return DirectErr & Environment.NewLine & "退回 data\downloads 也失败了：" & Err
            Return Err
        End If
        Dim Sb As New StringBuilder()
        Sb.Append("已下载：data\downloads\").Append(Path.GetFileName(Saved))
        Try
            Sb.Append("（").Append(FmtSize(New FileInfo(Saved).Length)).Append("）")
        Catch ex As Exception
        End Try
        If Folder.Length = 0 OrElse Folder = "none" Then Return Sb.ToString()
        If Folder = "modpack" OrElse Folder = "modpacks" Then
            Dim Detail As String = ""
            Dim Rc As Integer = InstallModpackFile(Saved, "", "", Detail)
            Sb.Append(Environment.NewLine)
            Select Case Rc
                Case 0
                    Sb.Append("整合包已装好")
                    If Detail.Length > 0 Then Sb.Append("，实例名「").Append(Detail).Append("」")
                    Sb.Append("（版本列表可能要刷新一下），选中它就能启动。")
                Case 1
                    Sb.Append("整合包安装被取消。").Append(If(Detail.Length > 0, Detail, ""))
                Case 3
                    Sb.Append(Detail)
                Case Else
                    Sb.Append("整合包安装没完成：").Append(If(Detail.Length > 0, Detail, "原因未知"))
            End Select
            Return Sb.ToString()
        End If
        If Not ValidFolder(Folder) Then
            Return Sb.ToString() & Environment.NewLine & "install 只能是 mods / resourcepacks / shaderpacks / datapacks / modpack，已跳过安装。"
        End If
        If Host.InstallContent(Saved, Folder) Then
            Sb.Append(Environment.NewLine).Append("已装进当前实例的 ").Append(Folder).Append(" 目录。")
        Else
            Sb.Append(Environment.NewLine).Append("装进 ").Append(Folder).Append(" 失败：宿主返回 False（多半是没选中实例）。")
        End If
        Return Sb.ToString()
    End Function

    ''' <summary>看压缩包像不像整合包（.mrpack 一律算；zip 看关键文件）。读不了就当不是。</summary>
    Private Function LooksLikeModpack(File As String) As Boolean
        Dim Ext As String = Path.GetExtension(If(File, ""))
        If Ext Is Nothing Then Ext = ""
        Ext = Ext.TrimStart("."c).ToLowerInvariant()
        If Ext = "mrpack" Then Return True
        If Ext <> "zip" Then Return False
        Try
            Using Z As System.IO.Compression.ZipArchive = System.IO.Compression.ZipFile.OpenRead(File)
                For Each E In Z.Entries
                    Dim N As String = If(E.FullName, "").Replace("\"c, "/"c).TrimStart("/"c)
                    Dim Seg As String() = N.Split("/"c)
                    '只看根目录和一级目录，和 PCL 自己的判断保持一致
                    If Seg.Length > 2 Then Continue For
                    Dim Base As String = Seg(Seg.Length - 1)
                    If Base = "modrinth.index.json" OrElse Base = "mcbbs.packmeta" OrElse
                       Base = "mmc-pack.json" OrElse Base = "modpack.json" OrElse
                       Base = "manifest.json" OrElse Base = "modpack.zip" OrElse Base = "modpack.mrpack" Then
                        Return True
                    End If
                Next
            End Using
        Catch ex As Exception
            Return False
        End Try
        Return False
    End Function

    Private Function ToolInstallDownload(ArgsJson As String) As String
        Dim Fn As String = If(JsonStringField(ArgsJson, "file"), "").Trim()
        Dim Folder As String = If(JsonStringField(ArgsJson, "folder"), "").Trim().ToLowerInvariant()
        Dim InstName As String = If(JsonStringField(ArgsJson, "instance_name"), "").Trim()
        Dim Explicit As Boolean = (Folder.Length > 0)
        If Folder.Length = 0 Then Folder = "mods"
        Dim Name As String = SafeFileName(Fn, "")
        If Name.Length = 0 Then Return "缺少 file 参数（list_downloads 里的文件名）。"
        Dim D = DownloadsDir()
        If D Is Nothing Then Return "插件未就绪。"
        Dim Full As String = Path.Combine(D, Name)
        If Not File.Exists(Full) Then Return "data\downloads 里没有「" & Name & "」，先用 list_downloads 看有哪些文件。"

        '整合包：交给启动器自己的安装流程（等于用户拖进窗口），会新建实例，不是往当前实例里塞文件
        Dim IsPack As Boolean = (Folder = "modpack" OrElse Folder = "modpacks") OrElse
                                (Not Explicit AndAlso (LooksLikeModpack(Full) OrElse
                                                       Path.GetExtension(Full).TrimStart("."c).ToLowerInvariant() = "mrpack"))
        If IsPack Then
            'API v9：先让宿主认一遍，把格式 / 名字 / 加载器 / 文件数报给用户
            Dim PackInfo As PCL.PluginModpackInfo = Nothing
            Try
                PackInfo = Host.InspectModpack(Full)
            Catch ex As Exception
            End Try
            Dim Desc As String = ""
            If PackInfo IsNot Nothing Then
                Desc = "识别为 " & PackInfo.Format
                If PackInfo.Name.Length > 0 Then Desc &= " · " & PackInfo.Name
                If PackInfo.Version.Length > 0 Then Desc &= " " & PackInfo.Version
                If PackInfo.MinecraftVersion.Length > 0 Then Desc &= " · MC " & PackInfo.MinecraftVersion
                If PackInfo.Loader.Length > 0 Then Desc &= " · " & PackInfo.Loader & " " & PackInfo.LoaderVersion
                If PackInfo.FileCount > 0 Then Desc &= " · " & PackInfo.FileCount & " 个文件"
                If PackInfo.OptionalCount > 0 Then Desc &= "（" & PackInfo.OptionalCount & " 个可选，静默安装会跳过）"
                If InstName.Length = 0 AndAlso PackInfo.Name.Length > 0 Then InstName = PackInfo.Name
                Desc &= Environment.NewLine
            End If
            '能告诉我是哪个项目的话，顺手把默认版本名和封面补上（和启动器里下载整合包一致）
            Dim Proj As String = If(JsonStringField(ArgsJson, "project"), "").Trim()
            Dim Src As String = If(JsonStringField(ArgsJson, "source"), "").Trim().ToLowerInvariant()
            If Src <> "curseforge" Then Src = "modrinth"
            Dim Logo As String = ""
            If Proj.Length > 0 Then
                Dim T As String = ""
                Dim I As String = ""
                ModpackMeta(Src, Proj, T, I)
                If InstName.Length = 0 Then InstName = T
                Logo = SaveRemoteImage(I, Src & "-" & Proj)
            End If
            Dim Detail As String = ""
            Dim Rc As Integer = InstallModpackFile(Full, InstName, Logo, Detail)
            Select Case Rc
                Case 0
                    If Host IsNot Nothing Then Host.LogInfo("AI 安装整合包完成：" & Name)
                    Dim Tip As String = Desc & "整合包已装好：" & Name & Environment.NewLine
                    If Detail.Length > 0 Then Tip &= "实例名「" & Detail & "」"
                    If Logo.Length > 0 Then Tip &= "，封面已设好"
                    Tip &= "。启动器里多了一个新实例（版本列表可能要刷新一下），选中它就能启动。"
                    Return Tip
                Case 1
                    Return Desc & "整合包安装被取消。" & If(Detail.Length > 0, Environment.NewLine & Detail, "")
                Case 3
                    Return Desc & Detail
                Case Else
                    Return Desc & "整合包安装没完成：" & If(Detail.Length > 0, Detail, "原因未知") & Environment.NewLine &
                           "文件还在 data\downloads，可以重试，或者直接在启动器里把「" & Name & "」拖进窗口安装。"
            End Select
        End If

        If Not ValidFolder(Folder) Then Return "folder 只能是 mods / resourcepacks / shaderpacks / datapacks；整个整合包请用 modpack。"
        If Host.InstallContent(Full, Folder) Then
            If Host IsNot Nothing Then Host.LogInfo("AI 安装下载文件：" & Name & " → " & Folder)
            Return "已装进当前实例的 " & Folder & "：" & Name
        End If
        Return "安装失败：宿主返回 False（可能没有选中实例，或实例目录不可写）。"
    End Function

    Private Function ToolListDownloads() As String
        Dim D = DownloadsDir()
        If D Is Nothing Then Return "插件未就绪。"
        If Not Directory.Exists(D) Then Return "还没有下载过任何文件（目录 data\downloads 不存在）。"
        Dim Files = Directory.GetFiles(D)
        If Files Is Nothing OrElse Files.Length = 0 Then Return "data\downloads 是空的。"
        Array.Sort(Files)
        Dim Sb As New StringBuilder()
        Sb.Append("data\downloads 共 ").Append(Files.Length).Append(" 个文件：")
        Dim N As Integer = 0
        For Each F In Files
            If N >= 60 Then
                Sb.Append(Environment.NewLine).Append("…（还有 ").Append(Files.Length - N).Append(" 个）")
                Exit For
            End If
            Dim Fi As New FileInfo(F)
            Sb.Append(Environment.NewLine).Append(Fi.Name)
            Sb.Append(" | ").Append(FmtSize(Fi.Length))
            Sb.Append(" | ").Append(Fi.LastWriteTime.ToString("MM-dd HH:mm"))
            N += 1
        Next
        Return Sb.ToString()
    End Function

    ' ==================== 安装原版游戏 ====================
    ' 只做两件事：写 versions\<id>\<id>.json + 下 <id>.jar。
    ' libraries 与 assets 不在这里下 —— 启动器每次启动都会跑 DlClientFix，自动补全缺失的支持库和资源文件。

    Private Const MojangManifestUrl As String = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
    Private Const BmclManifestUrl As String = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"

    ''' <summary>从 data 目录反推启动器 exe 所在目录：data → 插件 id → Plugins → PCLHarness → exe 目录。</summary>
    Private Function ExeFolder() As String
        Try
            Dim D As New DirectoryInfo(Host.DataFolder.TrimEnd("\"c))
            For i As Integer = 1 To 4
                If D.Parent Is Nothing Then Return ""
                D = D.Parent
            Next
            Return D.FullName
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ''' <summary>游戏根目录（.minecraft）：优先全局设置里的游戏文件夹，其次 exe 同级的 .minecraft。</summary>
    Private Function McRoot() As String
        Dim Raw As String = ""
        Try
            Raw = If(Host.GetGlobalSetting("LaunchFolderSelect"), "").Trim()
        Catch ex As Exception
        End Try
        Dim Exe As String = ExeFolder()
        If Raw.Length > 0 Then
            ' 设置里存的是 $" 代指 exe 目录，形如 "$.minecraft\"；$ 后面可能没有分隔符，要先补一个
            If Raw.StartsWith("$") AndAlso Exe.Length > 0 Then
                Dim Tail As String = Raw.Substring(1)
                If Tail.StartsWith("\") Then Tail = Tail.Substring(1)
                Raw = Exe.TrimEnd("\"c) & "\" & Tail
            End If
            If Directory.Exists(Raw) Then Return Raw.TrimEnd("\"c)
        End If
        If Exe.Length > 0 Then
            Dim Cand As String = Path.Combine(Exe, ".minecraft")
            If Directory.Exists(Cand) Then Return Cand
        End If
        Return ""
    End Function

    Private Function JSub(D As Dictionary(Of String, Object), K As String) As Dictionary(Of String, Object)
        If D Is Nothing Then Return Nothing
        Dim V As Object = Nothing
        If Not D.TryGetValue(K, V) OrElse V Is Nothing Then Return Nothing
        Return TryCast(V, Dictionary(Of String, Object))
    End Function

    ''' <summary>先官方后镜像；都失败时 ErrText 里带上两边的原因。</summary>
    Private Function FetchVersionManifest(ByRef FromBmcl As Boolean, ByRef ErrText As String) As String
        ErrText = ""
        Try
            Dim S As String = Host.HttpGetString(MojangManifestUrl)
            If S IsNot Nothing AndAlso S.IndexOf("""versions""", StringComparison.Ordinal) >= 0 Then
                FromBmcl = False
                Return S
            End If
            ErrText = "官方清单内容异常"
        Catch ex As Exception
            ErrText = "官方：" & ex.Message
        End Try
        Try
            Dim S As String = Host.HttpGetString(BmclManifestUrl)
            If S IsNot Nothing AndAlso S.IndexOf("""versions""", StringComparison.Ordinal) >= 0 Then
                FromBmcl = True
                Return S
            End If
            ErrText &= "；镜像清单内容异常"
        Catch ex As Exception
            ErrText &= "；镜像：" & ex.Message
        End Try
        Return ""
    End Function

    Private Function DownloadFailText(R As PCL.PluginHost.PluginDownloadResult, Url As String) As String
        Select Case R
            Case PCL.PluginHost.PluginDownloadResult.NotPermitted
                Return "插件没有 net 权限"
            Case PCL.PluginHost.PluginDownloadResult.HostDenied
                Return "宿主拒绝了主机 " & HostNameOf(Url)
            Case PCL.PluginHost.PluginDownloadResult.PathDenied
                Return "宿主拒绝了写入路径（files 白名单没覆盖这个 versions 目录）"
            Case PCL.PluginHost.PluginDownloadResult.TooLarge
                Return "文件超过 512 MB 上限"
            Case Else
                Return "网络错误、404 或磁盘写入失败"
        End Select
    End Function

    Private Function ToolInstallVanilla(ArgsJson As String) As String
        Dim Want As String = If(JsonStringField(ArgsJson, "version"), "").Trim()
        If Want.Length = 0 Then Return "缺少 version 参数，例如 1.20.1；要最新正式版就传 latest。"

        Dim Root As String = McRoot()
        If Root.Length = 0 Then
            Return "找不到游戏目录（.minecraft）。请先在启动器「设置 → 启动」里确认游戏文件夹，再让我装。"
        End If

        Dim FromBmcl As Boolean = False
        Dim ErrText As String = ""
        Dim Raw As String = FetchVersionManifest(FromBmcl, ErrText)
        If Raw.Length = 0 Then Return "获取版本清单失败：" & ErrText

        Dim M = JsonObjectOf(Raw)
        If M Is Nothing Then Return "版本清单不是预期 JSON，无法解析。"

        ' latest 换成具体版本号
        Dim Low As String = Want.ToLowerInvariant()
        If Low = "latest" OrElse Low = "最新" OrElse Low = "最新正式版" OrElse Low = "latest_release" Then
            Dim Rel As String = JStr(JSub(M, "latest"), "release")
            If Rel.Length = 0 Then Return "版本清单里没有 latest.release 字段。"
            Low = Rel.ToLowerInvariant()
        End If

        Dim Id As String = ""
        Dim DetailUrl As String = ""
        Dim PrefId As String = ""
        Dim PrefUrl As String = ""
        Dim Items = JArr(M, "versions")
        If Items Is Nothing Then Return "版本清单里没有 versions 列表。"
        For Each Item In Items
            Dim O = TryCast(Item, Dictionary(Of String, Object))
            If O Is Nothing Then Continue For
            Dim Vid As String = JStr(O, "id").Trim()
            Dim Vurl As String = JStr(O, "url").Trim()
            If Vid.Length = 0 Then Continue For
            If Vid.ToLowerInvariant() = Low Then
                Id = Vid
                DetailUrl = Vurl
                Exit For
            End If
            If PrefId.Length = 0 AndAlso Vid.ToLowerInvariant().StartsWith(Low, StringComparison.Ordinal) Then
                PrefId = Vid
                PrefUrl = Vurl
            End If
        Next
        If Id.Length = 0 Then
            Id = PrefId
            DetailUrl = PrefUrl
        End If
        If Id.Length = 0 Then Return "版本清单里找不到「" & Want & "」。可以传 latest，或换个确切的版本号。"
        If FromBmcl Then DetailUrl = "https://bmclapi2.bangbang93.com/version/" & Id & "/json"
        If DetailUrl.Length = 0 Then Return "版本清单里 " & Id & " 没有 json 地址。"

        Dim Detail As String = ""
        Try
            Detail = Host.HttpGetString(DetailUrl)
        Catch ex As Exception
            Return "下载 " & Id & " 的版本详情失败：" & ex.Message
        End Try
        Dim D = JsonObjectOf(Detail)
        If D Is Nothing Then Return Id & " 的版本详情不是预期 JSON。"

        Dim Client = JSub(JSub(D, "downloads"), "client")
        Dim JarUrl As String = JStr(Client, "url")
        Dim JarSize As Long = 0
        Long.TryParse(JStr(Client, "size"), JarSize)

        Dim VerDir As String = Path.Combine(Path.Combine(Root, "versions"), Id)
        Dim JsonPath As String = Path.Combine(VerDir, Id & ".json")
        Dim JarPath As String = Path.Combine(VerDir, Id & ".jar")
        Try
            Host.WriteTextFile(JsonPath, Detail)
        Catch ex As Exception
            Return "写入版本文件失败：" & ex.Message & Environment.NewLine &
                   "要写的路径：" & JsonPath & Environment.NewLine &
                   "（plugin.json 的 files.write 只覆盖 <启动器目录>\..\.minecraft\versions\*；游戏文件夹在别处的话要把这个路径加进白名单）"
        End Try

        Dim Sb As New StringBuilder()
        Sb.Append("已安装原版 ").Append(Id).Append("：").Append(JsonPath)

        If JarUrl.Length = 0 Then
            Sb.Append(Environment.NewLine).Append("这个版本的详情里没有客户端下载地址，jar 需要另外补。")
        Else
            Dim R As PCL.PluginHost.PluginDownloadResult = PCL.PluginHost.PluginDownloadResult.NotPermitted
            Dim LastErr As Exception = Nothing
            Try
                R = Host.HttpDownloadResult(JarUrl, JarPath)
            Catch ex As Exception
                LastErr = ex
            End Try
            If LastErr IsNot Nothing Then
                Sb.Append(Environment.NewLine).Append("客户端 jar 下载出错：").Append(LastErr.Message)
            ElseIf R = PCL.PluginHost.PluginDownloadResult.Ok Then
                Sb.Append(Environment.NewLine).Append("客户端 jar 已下载")
                If JarSize > 0 Then Sb.Append("（").Append(FmtSize(JarSize)).Append("）")
                Sb.Append("：").Append(JarPath)
            Else
                Sb.Append(Environment.NewLine).Append("客户端 jar 下载失败：").Append(DownloadFailText(R, JarUrl))
            End If
        End If

        Sb.Append(Environment.NewLine)
        Sb.Append("支持库和资源文件（libraries / assets）不用现在下：启动器每次启动前会自动补全缺失的文件。")
        Sb.Append(Environment.NewLine)
        Sb.Append("接下来请用户回「启动」页刷新版本列表（或重启启动器），选中 ").Append(Id).Append(" 后点启动。")
        ' 版本已经落地，但当前选中的还是旧实例；后面下载内容时要提醒先切过去
        LastInstalled = Id
        Return Sb.ToString()
    End Function

    ' ==================== Mod 加载器安装（复用启动器自己的安装流程） ====================
    ' 关键点：不自己解 installer、不自己跑 processors、不自己合并 json。
    ' 直接调宿主内部的 PCL.ModDownloadLib.McInstallStart，PCL 会把整套安装挂到右下角任务栏，
    ' 进度条、补 library、写安装清单全部和用户手动安装一模一样。

    Private Const BmclApiBase As String = "https://bmclapi2.bangbang93.com"

    ''' <summary>一个可安装的加载器版本候选。</summary>
    Private Class LoaderPick
        ''' <summary>传给 PCL 的版本串（Forge/NeoForge/Fabric 是版本号，OptiFine 是文件名）。</summary>
        Public Value As String = ""
        ''' <summary>给用户看的名字。</summary>
        Public Label As String = ""
        ''' <summary>备注（是否测试版、兼容的 Forge 等）。</summary>
        Public Note As String = ""
        ''' <summary>排序用。</summary>
        Public VerObj As System.Version = Nothing
        ''' <summary>NeoForge 的 API 名，如 20.4.30-beta / 1.20.1-47.1.99。</summary>
        Public ApiName As String = ""
        ''' <summary>Forge installer 的 hash。</summary>
        Public Hash As String = ""
        ''' <summary>OptiFine 的原始字段，构造 DlOptiFineListEntry 用。</summary>
        Public Raw As Dictionary(Of String, Object) = Nothing
    End Class

    ''' <summary>启动器当前选中的游戏目录（带尾斜杠）；拿不到就退回插件自己推的路径。</summary>
    Private Function PclMcDir() As String
        Dim D As String = ""
        Try
            D = PCL.ModMinecraft.McFolderSelected
        Catch ex As Exception
        End Try
        If String.IsNullOrWhiteSpace(D) Then D = McRoot()
        If String.IsNullOrWhiteSpace(D) Then Return ""
        D = D.Trim()
        If Not D.EndsWith("\") AndAlso Not D.EndsWith("/") Then D &= "\"
        Return D
    End Function

    Private Function ForgePicks(McVer As String, ByRef Err As String) As List(Of LoaderPick)
        Dim Out As New List(Of LoaderPick)
        Err = ""
        Dim Raw As String = ""
        Try
            Raw = Host.HttpGetString(BmclApiBase & "/forge/minecraft/" & McVer.Replace("-", "_"))
        Catch ex As Exception
            Err = "请求 BMCLAPI 失败：" & ex.Message
            Return Out
        End Try
        Dim Arr = JsonArrayOf(Raw)
        If Arr Is Nothing Then
            Err = "BMCLAPI 返回的不是 JSON 数组"
            Return Out
        End If
        For Each It In Arr
            Dim O = TryCast(It, Dictionary(Of String, Object))
            If O Is Nothing Then Continue For
            Dim V As String = JStr(O, "version").Trim()
            If V.Length = 0 Then Continue For
            Dim P As New LoaderPick With {.Value = V, .Label = V}
            Try
                P.VerObj = New System.Version(V)
            Catch ex As Exception
            End Try
            Dim Files = JArr(O, "files")
            If Files IsNot Nothing Then
                For Each F In Files
                    Dim FO = TryCast(F, Dictionary(Of String, Object))
                    If FO Is Nothing Then Continue For
                    If JStr(FO, "category") = "installer" AndAlso JStr(FO, "format") = "jar" Then
                        P.Hash = JStr(FO, "hash")
                        Exit For
                    End If
                Next
            End If
            Out.Add(P)
        Next
        Out.Sort(Function(A As LoaderPick, B As LoaderPick) If(A.VerObj Is Nothing OrElse B.VerObj Is Nothing, 0, B.VerObj.CompareTo(A.VerObj)))
        If Out.Count = 0 Then Err = "BMCLAPI 上没有 " & McVer & " 的 Forge"
        Return Out
    End Function

    Private Function NeoForgePicks(McVer As String, ByRef Err As String) As List(Of LoaderPick)
        Dim Out As New List(Of LoaderPick)
        Err = ""
        Dim Raw As String = ""
        Try
            Raw = Host.HttpGetString(BmclApiBase & "/neoforge/list/" & McVer)
        Catch ex As Exception
            Err = "请求 BMCLAPI 失败：" & ex.Message
            Return Out
        End Try
        Dim Arr = JsonArrayOf(Raw)
        If Arr Is Nothing Then
            Err = "BMCLAPI 返回的不是 JSON 数组"
            Return Out
        End If
        For Each It In Arr
            Dim O = TryCast(It, Dictionary(Of String, Object))
            If O Is Nothing Then Continue For
            Dim Api As String = JStr(O, "rawVersion").Trim()
            Dim Plain As String = JStr(O, "version").Trim()
            If Api.Length = 0 Then Api = Plain
            If Api.Length = 0 Then Continue For
            Dim P As New LoaderPick With {.Value = Api, .Label = Plain, .ApiName = Api}
            If Api.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
               Api.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0 Then P.Note = "测试版"
            Try
                P.VerObj = New System.Version(Plain)
            Catch ex As Exception
            End Try
            Out.Add(P)
        Next
        ' 新的在前；解析不出数字版本号的（beta/alpha）沉到最后
        Out.Sort(Function(A As LoaderPick, B As LoaderPick)
                     If A.VerObj Is Nothing AndAlso B.VerObj Is Nothing Then Return 0
                     If A.VerObj Is Nothing Then Return 1
                     If B.VerObj Is Nothing Then Return -1
                     Return B.VerObj.CompareTo(A.VerObj)
                 End Function)
        If Out.Count = 0 Then Err = "BMCLAPI 上没有 " & McVer & " 的 NeoForge"
        Return Out
    End Function

    ''' <summary>OptiFine 的显示名 / 实例名构造规则，与 PCL 内部保持一致。</summary>
    Private Function OptiFineDisplay(Raw As Dictionary(Of String, Object)) As String
        Dim Mcv As String = JStr(Raw, "mcversion")
        Dim Typ As String = JStr(Raw, "type")
        Dim Patch As String = JStr(Raw, "patch")
        Return (Mcv & Typ.Replace("HD_U", "").Replace("_", " ") & " " & Patch).Replace(".0 ", " ")
    End Function

    Private Function OptiFineInstance(Raw As Dictionary(Of String, Object)) As String
        Dim Mcv As String = JStr(Raw, "mcversion")
        Dim Typ As String = JStr(Raw, "type")
        Dim Patch As String = JStr(Raw, "patch")
        Return Mcv & "-OptiFine_" & (Typ & " " & Patch).Replace(".0 ", " ").Replace(" ", "_").Replace(Mcv & "_", "")
    End Function

    Private Function OptiFinePicks(McVer As String, ByRef Err As String) As List(Of LoaderPick)
        Dim Out As New List(Of LoaderPick)
        Err = ""
        Dim Raw As String = ""
        Try
            Raw = Host.HttpGetString(BmclApiBase & "/optifine/versionList")
        Catch ex As Exception
            Err = "请求 BMCLAPI 失败：" & ex.Message
            Return Out
        End Try
        Dim Arr = JsonArrayOf(Raw)
        If Arr Is Nothing Then
            Err = "BMCLAPI 返回的不是 JSON 数组"
            Return Out
        End If
        For Each It In Arr
            Dim O = TryCast(It, Dictionary(Of String, Object))
            If O Is Nothing Then Continue For
            If JStr(O, "mcversion").Trim() <> McVer Then Continue For
            Dim FileName As String = JStr(O, "filename").Trim()
            If FileName.Length = 0 Then Continue For
            Dim P As New LoaderPick With {
                .Value = FileName,
                .Label = OptiFineDisplay(O),
                .ApiName = OptiFineInstance(O),
                .Raw = O
            }
            Dim ForgeReq As String = JStr(O, "forge").Replace("Forge ", "").Replace("#", "").Trim()
            If ForgeReq.IndexOf("N/A", StringComparison.OrdinalIgnoreCase) >= 0 Then ForgeReq = ""
            If ForgeReq.Length > 0 Then P.Note = "需 Forge " & ForgeReq
            Out.Add(P)
        Next
        ' 正式版排在测试版前面，同类里按 patch 降序（I6 > I5），这样 Out(0) 就是默认要装的版本
        Out.Sort(Function(A As LoaderPick, B As LoaderPick)
                     Dim Ap As Boolean = A.Value.IndexOf("pre", StringComparison.OrdinalIgnoreCase) >= 0
                     Dim Bp As Boolean = B.Value.IndexOf("pre", StringComparison.OrdinalIgnoreCase) >= 0
                     If Ap <> Bp Then Return If(Ap, 1, -1)
                     Return String.CompareOrdinal(B.Value, A.Value)
                 End Function)
        If Out.Count = 0 Then Err = "BMCLAPI 上没有 " & McVer & " 的 OptiFine"
        Return Out
    End Function

    Private Function FabricPicks(McVer As String, ByRef Err As String) As List(Of LoaderPick)
        Dim Out As New List(Of LoaderPick)
        Dim Unstable As New List(Of LoaderPick)
        Err = ""
        Dim Raw As String = ""
        Try
            Raw = Host.HttpGetString(BmclApiBase & "/fabric-meta/v2/versions/loader/" & McVer)
        Catch ex As Exception
            Err = "请求 BMCLAPI 失败：" & ex.Message
            Return Out
        End Try
        Dim Arr = JsonArrayOf(Raw)
        If Arr Is Nothing Then
            Err = "BMCLAPI 返回的不是 JSON 数组"
            Return Out
        End If
        For Each It In Arr
            Dim O = TryCast(It, Dictionary(Of String, Object))
            If O Is Nothing Then Continue For
            Dim LO = TryCast(O("loader"), Dictionary(Of String, Object))
            If LO Is Nothing Then Continue For
            Dim V As String = JStr(LO, "version").Trim()
            If V.Length = 0 Then Continue For
            Dim P As New LoaderPick With {.Value = V, .Label = V}
            If JBool(LO, "stable") Then
                Out.Add(P)
            Else
                P.Note = "不稳定"
                Unstable.Add(P)
            End If
        Next
        ' Fabric meta 本身是新到旧，稳定版整体提到前面就够用
        Out.AddRange(Unstable)
        If Out.Count = 0 Then Err = "BMCLAPI 上没有 " & McVer & " 的 Fabric"
        Return Out
    End Function

    Private Function LoaderPicks(McVer As String, Loader As String, ByRef Err As String) As List(Of LoaderPick)
        Err = ""
        Select Case Loader
            Case "forge"
                Return ForgePicks(McVer, Err)
            Case "neoforge"
                Return NeoForgePicks(McVer, Err)
            Case "optifine"
                Return OptiFinePicks(McVer, Err)
            Case "fabric"
                Return FabricPicks(McVer, Err)
            Case Else
                Err = "不认识的加载器：" & Loader & "（支持 forge / neoforge / optifine / fabric）"
                Return New List(Of LoaderPick)
        End Select
    End Function

    Private Function ToolListLoaders(ArgsJson As String) As String
        Dim McVer As String = If(JsonStringField(ArgsJson, "mc_version"), "").Trim()
        Dim Loader As String = If(JsonStringField(ArgsJson, "loader"), "all").Trim().ToLowerInvariant()
        If McVer.Length = 0 Then Return "缺少 mc_version，例如 1.20.1。"

        Dim Names As New List(Of String)
        If Loader.Length = 0 OrElse Loader = "all" Then
            Names.AddRange(New String() {"forge", "neoforge", "optifine", "fabric"})
        Else
            Names.Add(Loader)
        End If

        Dim Sb As New StringBuilder()
        Sb.Append("Minecraft ").Append(McVer).Append(" 可装的加载器：").Append(Environment.NewLine)
        For Each L In Names
            Dim Err As String = ""
            Dim Picks = LoaderPicks(McVer, L, Err)
            If Picks.Count = 0 Then
                Sb.Append("· ").Append(L).Append("：").Append(If(Err.Length > 0, Err, "没有可用版本")).Append(Environment.NewLine)
                Continue For
            End If
            Sb.Append("· ").Append(L).Append("（").Append(Picks.Count).Append(" 个，新的在前）：")
            Dim N As Integer = 0
            For Each P In Picks
                If N >= 12 Then
                    Sb.Append(" …共 ").Append(Picks.Count).Append(" 个")
                    Exit For
                End If
                If N > 0 Then Sb.Append("、")
                Sb.Append(P.Label)
                If P.Note.Length > 0 Then Sb.Append("(").Append(P.Note).Append(")")
                N += 1
            Next
            Sb.Append(Environment.NewLine)
        Next
        Sb.Append("装的时候把想要的版本号传给 install_loader 的 loader_version；不传就取最新。")
        Return Sb.ToString()
    End Function

    ''' <summary>把 Forge 版本串变回 DlForgeVersionEntry；失败返回 Nothing（让 PCL 自己查）。</summary>
    Private Function BuildForgeEntry(McVer As String, Pick As LoaderPick) As PCL.DlForgeVersionEntry
        Try
            Dim E As New PCL.DlForgeVersionEntry(Pick.Value, Nothing, McVer)
            E.Category = "installer"
            E.Hash = Pick.Hash
            E.IsRecommended = False
            Return E
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    ''' <summary>把 OptiFine 的候选变回 JObject，供 McComponents 使用。</summary>
    Private Function BuildOptiFineJson(Pick As LoaderPick) As Newtonsoft.Json.Linq.JObject
        If Pick Is Nothing OrElse Pick.Raw Is Nothing Then Return Nothing
        Dim O As New Newtonsoft.Json.Linq.JObject()
        O("DisplayName") = OptiFineDisplay(Pick.Raw)
        O("FileName") = Pick.Value
        O("InstanceName") = OptiFineInstance(Pick.Raw)
        O("IsPreview") = (Pick.Value.IndexOf("pre", StringComparison.OrdinalIgnoreCase) >= 0)
        O("Inherit") = JStr(Pick.Raw, "mcversion")
        O("ReleaseTime") = ""
        Dim ForgeReq As String = JStr(Pick.Raw, "forge").Replace("Forge ", "").Replace("#", "").Trim()
        If ForgeReq.IndexOf("N/A", StringComparison.OrdinalIgnoreCase) >= 0 Then
            O("RequiredForgeVersion") = Nothing
        Else
            O("RequiredForgeVersion") = ForgeReq
        End If
        Return O
    End Function

    ''' <summary>在 UI 线程调宿主安装入口；返回是否成功开始。</summary>
    Private Function StartPclInstall(Req As PCL.ModDownloadLib.McInstallRequest, ByRef ErrText As String) As Boolean
        ErrText = ""
        Dim Disp As System.Windows.Threading.Dispatcher = Nothing
        Try
            Disp = System.Windows.Application.Current.Dispatcher
        Catch ex As Exception
        End Try
        Try
            If Disp Is Nothing OrElse Disp.CheckAccess() Then Return PCL.ModDownloadLib.McInstallStart(Req)
            Dim Box As Object() = New Object() {False}
            Dim LocalErr As String = ""
            Disp.Invoke(Sub()
                            Try
                                Box(0) = PCL.ModDownloadLib.McInstallStart(Req)
                            Catch ex As Exception
                                LocalErr = ex.Message
                            End Try
                        End Sub)
            If LocalErr.Length > 0 Then ErrText = LocalErr
            Return CBool(Box(0))
        Catch ex As Exception
            ErrText = ex.Message
            Return False
        End Try
    End Function

    Private Function ToolInstallLoader(ArgsJson As String) As String
        Dim McVer As String = If(JsonStringField(ArgsJson, "mc_version"), "").Trim()
        Dim Loader As String = If(JsonStringField(ArgsJson, "loader"), "").Trim().ToLowerInvariant()
        Dim WantVer As String = If(JsonStringField(ArgsJson, "loader_version"), "").Trim()
        Dim OptiWant As String = If(JsonStringField(ArgsJson, "optifine_version"), "").Trim()
        Dim InstName As String = If(JsonStringField(ArgsJson, "instance_name"), "").Trim()

        If McVer.Length = 0 Then Return "缺少 mc_version，例如 1.20.1。"
        If Loader.Length = 0 Then Return "缺少 loader：forge / neoforge / optifine / fabric。"
        If Loader <> "forge" AndAlso Loader <> "neoforge" AndAlso Loader <> "optifine" AndAlso Loader <> "fabric" Then
            Return "不认识的加载器：" & Loader & "（支持 forge / neoforge / optifine / fabric）"
        End If

        Dim McDir As String = PclMcDir()
        If McDir.Length = 0 Then
            Return "拿不到游戏目录。请先在启动器里选好游戏文件夹（设置 → 启动 → 游戏文件夹），再让我装。"
        End If

        ' ---- 选版本 ----
        Dim Err As String = ""
        Dim Picks = LoaderPicks(McVer, Loader, Err)
        If Picks.Count = 0 Then Return "取 " & Loader & " 版本列表失败：" & If(Err.Length > 0, Err, "没有可用版本")

        Dim Pick As LoaderPick = Nothing
        Dim ByFile As Boolean = (Loader = "optifine")
        If WantVer.Length > 0 Then
            For Each P In Picks
                Dim Hit As Boolean = False
                If ByFile Then
                    Hit = P.Value.Equals(WantVer, StringComparison.OrdinalIgnoreCase) OrElse
                          P.Value.IndexOf(WantVer, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                          P.Label.IndexOf(WantVer, StringComparison.OrdinalIgnoreCase) >= 0
                Else
                    Hit = P.Value.Equals(WantVer, StringComparison.OrdinalIgnoreCase) OrElse
                          P.Label.Equals(WantVer, StringComparison.OrdinalIgnoreCase)
                End If
                If Hit Then
                    Pick = P
                    Exit For
                End If
            Next
            If Pick Is Nothing Then
                Dim Sb2 As New StringBuilder()
                Sb2.Append("没有 ").Append(Loader).Append(" ").Append(WantVer).Append("（").Append(McVer).Append("）。可选：")
                Dim N2 As Integer = 0
                For Each P In Picks
                    If N2 >= 10 Then Exit For
                    If N2 > 0 Then Sb2.Append("、")
                    Sb2.Append(P.Label)
                    N2 += 1
                Next
                Return Sb2.ToString()
            End If
        Else
            Pick = Picks(0)
        End If

        ' ---- 顺带装 OptiFine（只对 forge/neoforge/fabric 有意义）----
        Dim OfPick As LoaderPick = Nothing
        Dim OfNote As String = ""
        If OptiWant.Length > 0 AndAlso Loader <> "optifine" Then
            Dim OfErr As String = ""
            Dim OfPicks = OptiFinePicks(McVer, OfErr)
            If OfPicks IsNot Nothing Then
                For Each P In OfPicks
                    If P.Value.IndexOf(OptiWant, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                       P.Label.IndexOf(OptiWant, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                       P.ApiName.IndexOf(OptiWant, StringComparison.OrdinalIgnoreCase) >= 0 Then
                        OfPick = P
                        Exit For
                    End If
                Next
            End If
            If OfPick Is Nothing Then
                ' 指定的版本没对上（模型常把「最新」或只写 patch 号的片段传进来）。
                ' 与其整体失败，不如用最新的那个，并在返回里明说换成了什么。
                If OfPicks IsNot Nothing AndAlso OfPicks.Count > 0 Then
                    OfPick = OfPicks(0)
                    OfNote = "注意：没找到 OptiFine「" & OptiWant & "」，已改用最新的 " & OfPick.Label & "。"
                Else
                    Return "找到了 " & Loader & " " & Pick.Label & "，但没找到 OptiFine「" & OptiWant & "」（" & McVer & "）。" &
                           If(OfErr.Length > 0, "原因：" & OfErr, "先用 list_loaders 看可选版本。")
                End If
            End If
        End If

        ' ---- 实例名 ----
        If InstName.Length = 0 Then
            Select Case Loader
                Case "forge"
                    InstName = McVer & "-forge-" & Pick.Label
                Case "neoforge"
                    InstName = McVer & "-neoforge-" & Pick.Label
                Case "fabric"
                    InstName = McVer & "-fabric-" & Pick.Label
                Case Else
                    InstName = Pick.ApiName
            End Select
            If OfPick IsNot Nothing Then InstName &= "-OptiFine"
        End If

        Dim Bad As String = ""
        For Each C As Char In Path.GetInvalidFileNameChars()
            If InstName.IndexOf(C) >= 0 Then Bad = C.ToString() : Exit For
        Next
        If Bad.Length > 0 Then Return "实例名里有非法字符「" & Bad & "」，换一个。"
        If InstName.Length > 80 Then Return "实例名太长（" & InstName.Length & " 字），换短一点。"

        Dim VerFolder As String = McDir & "versions\" & InstName & "\"
        If File.Exists(VerFolder & InstName & ".json") Then
            Return "版本 " & InstName & " 已经存在（" & VerFolder & InstName & ".json）。" &
                   "换个 instance_name，或者先在启动器里删掉旧版本。"
        End If

        ' ---- 组装请求 ----
        Dim ForgeEntry As PCL.DlForgeVersionEntry = Nothing
        Dim NeoEntry As PCL.DlNeoForgeListEntry = Nothing
        Dim ForgeVer As String = Nothing
        Dim NeoVer As String = Nothing
        Dim FabVer As String = Nothing

        Try
            Select Case Loader
                Case "forge"
                    ForgeVer = Pick.Value
                    ForgeEntry = BuildForgeEntry(McVer, Pick)
                Case "neoforge"
                    NeoVer = Pick.Value
                    NeoEntry = New PCL.DlNeoForgeListEntry(Pick.ApiName)
                Case "fabric"
                    FabVer = Pick.Value
            End Select
        Catch ex As Exception
            Return "构造 " & Loader & " 安装信息失败：" & ex.Message
        End Try

        Dim OfJson As Newtonsoft.Json.Linq.JObject = Nothing
        If Loader = "optifine" Then
            OfJson = BuildOptiFineJson(Pick)
        ElseIf OfPick IsNot Nothing Then
            OfJson = BuildOptiFineJson(OfPick)
        End If

        Dim Comp As PCLCS.McComponents
        Try
            Comp = New PCLCS.McComponents(McVer, OfJson, ForgeVer, NeoVer, FabVer, Nothing, Nothing, Nothing)
        Catch ex As Exception
            Return "构造安装组件失败：" & ex.Message
        End Try

        Dim Req As New PCL.ModDownloadLib.McInstallRequest With {
            .NewInstanceName = InstName,
            .InstanceFolder = VerFolder,
            .Components = Comp,
            .MinecraftJson = Nothing,
            .ForgeEntry = ForgeEntry,
            .NeoForgeEntry = NeoEntry,
            .LiteLoaderEntry = Nothing,
            .FabricApi = Nothing,
            .OptiFabric = Nothing
        }

        Dim InstallErr As String = ""
        Dim Started As Boolean = StartPclInstall(Req, InstallErr)
        If Not Started Then
            Return "启动安装失败：" & If(InstallErr.Length > 0, InstallErr, "宿主返回 False") & Environment.NewLine &
                   "常见原因：版本目录已存在、临时目录不可写、或下载源连不上。" & Environment.NewLine &
                   "详细原因看启动器日志：" & If(EcFolderOrEmpty().Length > 0, EcFolderOrEmpty() & "\PCLHarness\Log1.txt", "PCLHarness\Log1.txt")
        End If
        ' 后台任务已经开跑：从现在到它落地之前，下载内容的工具会被拦住
        MarkInstallPending(InstName)

        Dim Sb As New StringBuilder()
        Sb.Append("已开始安装 ").Append(InstName).Append("：").Append(Environment.NewLine)
        Select Case Loader
            Case "forge"
                Sb.Append("· Forge ").Append(Pick.Label)
            Case "neoforge"
                Sb.Append("· NeoForge ").Append(Pick.Label)
            Case "fabric"
                Sb.Append("· Fabric ").Append(Pick.Label)
            Case Else
                Sb.Append("· OptiFine ").Append(Pick.Label)
        End Select
        If OfPick IsNot Nothing Then Sb.Append(" + OptiFine ").Append(OfPick.Label)
        Sb.Append(Environment.NewLine)
        If OfNote.Length > 0 Then Sb.Append(OfNote).Append(Environment.NewLine)
        Sb.Append("· 目标目录：").Append(VerFolder).Append(Environment.NewLine)
        Sb.Append("安装由启动器自己执行：下载 installer、跑处理器、合并版本 json、补支持库，").
           Append("进度条在启动器右下角的任务栏里，点开能看到每一步。").Append(Environment.NewLine)
        Sb.Append("这是后台任务，现在还没装完。等任务栏走完，让用户回「启动」页刷新版本列表，选中 ").Append(InstName).Append(" 再点启动。").Append(Environment.NewLine)
        Sb.Append("注意：安装完成前这个版本不会出现在版本列表里，不要反复调用工具查询进度——查也查不到，只会白白占掉工具轮次。")
        If OfJson IsNot Nothing AndAlso Loader <> "optifine" Then
            Sb.Append(Environment.NewLine).Append("OptiFine 会作为 mod 放进 mods 目录（因为同时装了 ").Append(Loader).Append("）。")
        End If
        Return Sb.ToString()
    End Function

    Private Function EcFolderOrEmpty() As String
        Try
            Return ExeFolder()
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ''' <summary>实例列表的纯文本版（给模型 / 给 /instances）。</summary>
    Private Function InstanceListText() As String
        Dim All = AllInstances()
        If All.Count = 0 Then Return "没读到游戏实例。先在「版本选择」里添加或下载一个版本。"
        Dim Sb As New StringBuilder()
        Sb.Append("共 ").Append(All.Count).Append(" 个实例：")
        Dim N As Integer = 0
        For Each E In All
            If N >= 200 Then
                Sb.Append("…（还有 ").Append(All.Count - N).Append(" 个）")
                Exit For
            End If
            If N > 0 Then Sb.Append("；")
            If E.IsSelected Then Sb.Append("[当前] ")
            Sb.Append(E.Name)
            N += 1
        Next
        Return Sb.ToString()
    End Function
End Class
