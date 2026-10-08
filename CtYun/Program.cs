using CtYun;
using CtYun.Models;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 就绪后存活不足该秒数即被断开，视为服务端拒绝当前连接票据
const int ShortLivedSeconds = 5;
// 连续被拒次数达到该值，判定票据已失效并重建整段会话
const int RejectStreakLimit = 2;
// 连续建连失败次数达到该值，判定票据或主机信息已失效并重建整段会话
const int ConnectFailureLimit = 3;
// 会话级失败的最大退避间隔
const int MaxRetryDelaySeconds = 1800;
// 需要人工介入（如设备未绑定）时的重试间隔
const int ManualRetryDelayMinutes = 30;

using var globalCts = new CancellationTokenSource();

Utility.WriteLine(ConsoleColor.Green, $"版本：v {Assembly.GetEntryAssembly()?.GetName().Version}");

AppConfig accountConfig;
try
{
    accountConfig = LoadAccountsConfig();
}
catch (Exception ex)
{
    // 数据目录不可写、配置非法等启动期异常不应让进程带栈崩溃
    Utility.WriteLine(ConsoleColor.Red, $"读取账号配置失败：{ex.Message}");
    Environment.ExitCode = 1;
    return;
}

if (accountConfig?.Accounts is not { Count: > 0 })
{
    Utility.WriteLine(ConsoleColor.Red, "未读取到账号配置。请配置 accounts.json，或设置 APP_USER/APP_PASSWORD，或使用交互输入模式。");
    Environment.ExitCode = 1;
    return;
}

var runtimeConfig = BuildRuntimeConfig(accountConfig);

Utility.WriteLine(ConsoleColor.DarkGray, runtimeConfig.RestartIntervalMinutes > 0
    ? $"会话轮换已启用：每 {runtimeConfig.RestartIntervalMinutes} 分钟（另加 0~{runtimeConfig.RestartJitterSeconds} 秒抖动）重建一次登录与保活会话。"
    : "会话轮换已关闭（restartIntervalMinutes=0），行为与旧版本一致。");

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    try
    {
        if (!globalCts.IsCancellationRequested)
        {
            globalCts.Cancel();
        }
    }
    catch (ObjectDisposedException)
    {
        // 进程正在退出，无需再取消
    }
};

var sessionTasks = runtimeConfig.Accounts
    .Where(account => account != null)
    .Select(account => RunAccountLoopAsync(account, runtimeConfig, globalCts.Token))
    .ToList();

try
{
    await Task.WhenAll(sessionTasks);
    Utility.WriteLine(ConsoleColor.Yellow, "程序已停止。");
}
catch (OperationCanceledException)
{
    Utility.WriteLine(ConsoleColor.Yellow, "程序已停止。");
}
catch (Exception ex)
{
    // 异常退出必须带非零退出码，否则容器/守护进程无法识别失败
    Utility.WriteLine(ConsoleColor.Red, $"程序发生未处理异常，即将退出：{ex}");
    Environment.ExitCode = 1;
}

/// <summary>账号级长驻循环：每轮启动一段"会话"，到期或被拒时整段重建。</summary>
async Task RunAccountLoopAsync(AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken ct)
{
    var label = AccountLabel(account);
    var sessionIndex = 0;
    var rejectStreak = 0;
    var failureStreak = 0;

    while (!ct.IsCancellationRequested)
    {
        var lifetime = runtimeConfig.NextSessionLifetime();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (lifetime > TimeSpan.Zero)
        {
            sessionCts.CancelAfter(lifetime);
        }

        sessionIndex++;
        var startedAt = DateTime.UtcNow;
        Utility.WriteLine(ConsoleColor.Cyan, $"[{label}] 开始登录（第 {sessionIndex} 段会话）。");

        SessionResult result;
        try
        {
            result = await RunSessionAsync(account, runtimeConfig, sessionCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            break;
        }
        catch (OperationCanceledException)
        {
            result = new SessionResult(SessionOutcome.Rotation, "会话周期到达", DateTime.UtcNow - startedAt);
        }
        catch (Exception ex)
        {
            result = new SessionResult(SessionOutcome.Failed, ex.Message, DateTime.UtcNow - startedAt);
        }

        if (ct.IsCancellationRequested)
        {
            break;
        }

        switch (result.Outcome)
        {
            case SessionOutcome.Rotation:
                if (!result.KeepAliveStarted)
                {
                    // 会话寿命内还没进入保活就被轮换：多半是登录/取票耗时过长，按失败退避，避免无限快循环
                    failureStreak++;
                    rejectStreak = 0;
                    var preludeWait = NextFailureWait(failureStreak);
                    Utility.WriteLine(ConsoleColor.Red, $"[{label}] 第 {sessionIndex} 段会话在 {Format(result.Elapsed)} 内未能进入保活（连续第 {failureStreak} 次）。{Format(preludeWait)}后重试。");
                    if (!await DelayAsync(preludeWait, ct))
                    {
                        return;
                    }
                    break;
                }

                rejectStreak = 0;
                failureStreak = 0;
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] === 第 {sessionIndex} 段会话运行 {Format(result.Elapsed)}，定时重建登录与保活会话 ===");
                if (!await DelayAsync(runtimeConfig.SessionCooldown, ct))
                {
                    return;
                }
                break;

            case SessionOutcome.Completed:
                if (!result.KeepAliveStarted)
                {
                    failureStreak++;
                    rejectStreak = 0;
                    var idleWait = NextFailureWait(failureStreak);
                    Utility.WriteLine(ConsoleColor.Red, $"[{label}] 保活任务未进入保活阶段即结束（连续第 {failureStreak} 次）：{result.Reason}。{Format(idleWait)}后重试。");
                    if (!await DelayAsync(idleWait, ct))
                    {
                        return;
                    }
                    break;
                }

                rejectStreak = 0;
                failureStreak = 0;
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活任务已结束（{Format(result.Elapsed)}），立即重建会话。");
                if (!await DelayAsync(runtimeConfig.SessionCooldown, ct))
                {
                    return;
                }
                break;

            case SessionOutcome.Rejected:
                rejectStreak++;
                failureStreak = 0;
                var rejectWait = NextRejectWait(runtimeConfig, rejectStreak);
                Utility.WriteLine(ConsoleColor.Red, $"[{label}] 会话已失效：{result.Reason}。{Format(rejectWait)}后重建登录与保活会话（连续第 {rejectStreak} 次）。");
                if (!await DelayAsync(rejectWait, ct))
                {
                    return;
                }
                break;

            case SessionOutcome.Failed:
                failureStreak++;
                rejectStreak = 0;
                var wait = NextFailureWait(failureStreak);
                Utility.WriteLine(ConsoleColor.Red, $"[{label}] 会话建立失败（连续第 {failureStreak} 次）：{result.Reason}。{Format(wait)}后重试。");
                if (!await DelayAsync(wait, ct))
                {
                    return;
                }
                break;

            case SessionOutcome.NeedsManualIntervention:
                rejectStreak = 0;
                failureStreak = 0;
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] {result.Reason}。{ManualRetryDelayMinutes} 分钟后重试。");
                if (!await DelayAsync(TimeSpan.FromMinutes(ManualRetryDelayMinutes), ct))
                {
                    return;
                }
                break;
        }
    }

    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 账号任务已停止。");
}

/// <summary>一段会话：登录 -> 获取云电脑 -> connect 取票据 -> 保活，直到轮换、被拒或失败。</summary>
async Task<SessionResult> RunSessionAsync(AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken sessionToken)
{
    var label = AccountLabel(account);
    var startedAt = DateTime.UtcNow;

    // 每段会话都重新建立 HttpClient，避免长跑期间句柄累积
    using var api = new CtYunApi(account.DeviceCode);

    var loginOutcome = await PerformLoginSequence(api, account);
    if (loginOutcome != LoginOutcome.Success)
    {
        var elapsed = DateTime.UtcNow - startedAt;
        if (loginOutcome == LoginOutcome.NeedsManualIntervention)
        {
            return new SessionResult(SessionOutcome.NeedsManualIntervention, "当前设备未绑定，且运行环境无法输入短信验证码", elapsed);
        }

        // 账号或密码被拒时重试没有意义：降频等待人工处理，避免持续触发验证码识别
        if (api.CredentialsRejected)
        {
            return new SessionResult(SessionOutcome.NeedsManualIntervention, "账号或密码错误，已降频等待人工处理", elapsed);
        }

        return new SessionResult(SessionOutcome.Failed, "登录失败", elapsed);
    }

    var desktopList = await api.GetLlientListAsync();
    if (desktopList == null || desktopList.Count == 0)
    {
        return new SessionResult(SessionOutcome.Failed, "未获取到云电脑列表", DateTime.UtcNow - startedAt);
    }

    var activeDesktops = new List<Desktop>();
    foreach (var desktop in desktopList)
    {
        if (sessionToken.IsCancellationRequested)
        {
            return new SessionResult(SessionOutcome.Rotation, "会话周期到达", DateTime.UtcNow - startedAt);
        }

        if (desktop.UseStatusText != "运行中")
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] [{desktop.UseStatusText}] 电脑未开机，正在开机，请在2分钟后重新运行软件。");
        }

        var connectResult = await api.ConnectAsync(desktop.DesktopId);
        if (connectResult.Success && connectResult.Data?.DesktopInfo != null)
        {
            desktop.DesktopInfo = connectResult.Data.DesktopInfo;
            activeDesktops.Add(desktop);
        }
        else
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}] Connect Error: [{desktop.DesktopId}] {connectResult.Msg}");
        }
    }

    if (activeDesktops.Count == 0)
    {
        return new SessionResult(SessionOutcome.Failed, "没有可保活的云电脑", DateTime.UtcNow - startedAt);
    }

    using var scopeCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
    var sessionState = new SessionState(scopeCts);

    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活任务启动：{activeDesktops.Count} 台云电脑，每 {runtimeConfig.KeepAliveSeconds} 秒强制重连一次。");

    var workers = activeDesktops
        .Select(desktop => KeepAliveWorkerAsync(api, account, desktop, runtimeConfig, scopeCts.Token, sessionState))
        .ToList();

    try
    {
        await Task.WhenAll(workers);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        // worker 在 try 之外抛出的异常（例如连接地址异常）会让整段会话失去保活能力，
        // 必须按失败处理，否则会退化成每轮冷却时间重新登录一次。
        sessionState.RequestSessionFailure(ex.Message);
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 保活任务异常终止：{ex.Message}");
    }

    var totalElapsed = DateTime.UtcNow - startedAt;

    const bool keepAliveStarted = true; // 已进入保活阶段才可能走到这里

    // 票据失效：重建整段会话（重新登录 + 重新获取票据）
    if (sessionState.RebuildRequested)
    {
        return new SessionResult(SessionOutcome.Rejected, sessionState.RebuildReason, totalElapsed, keepAliveStarted);
    }

    // 会话失去保活能力但不是票据问题：按失败退避，避免高频重新登录
    if (sessionState.Failed)
    {
        return new SessionResult(SessionOutcome.Failed, sessionState.RebuildReason, totalElapsed, keepAliveStarted);
    }

    if (sessionToken.IsCancellationRequested)
    {
        return new SessionResult(SessionOutcome.Rotation, "会话周期到达", totalElapsed, keepAliveStarted);
    }

    return new SessionResult(SessionOutcome.Completed, "保活任务已结束", totalElapsed, keepAliveStarted);
}

async Task<LoginOutcome> PerformLoginSequence(CtYunApi api, AccountConfig account)
{
    if (!await api.LoginAsync(account.User, account.Password))
    {
        return LoginOutcome.Failed;
    }

    if (api.LoginInfo.BondedDevice)
    {
        return LoginOutcome.Success;
    }

    var label = AccountLabel(account);

    // 非交互环境下不重复发送短信验证码，避免长时间运行时刷短信
    if (!CanReadFromConsole())
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 当前设备未绑定，且当前为非交互环境，已跳过短信验证码发送。请使用 -it 交互模式运行一次完成设备绑定。");
        return LoginOutcome.NeedsManualIntervention;
    }

    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 当前设备未绑定，正在发送短信验证码。");
    if (!await api.GetSmsCodeAsync(account.User))
    {
        return LoginOutcome.Failed;
    }

    var verificationCode = ReadVerificationCode(account);
    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 未获取到短信验证码。");
        return LoginOutcome.Failed;
    }

    if (!await api.BindingDeviceAsync(verificationCode.Trim()))
    {
        return LoginOutcome.Failed;
    }

    return LoginOutcome.Success;
}

string ReadVerificationCode(AccountConfig account)
{
    var label = AccountLabel(account);
    if (!CanReadFromConsole())
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 当前账号需要短信验证码，请使用 -it 交互模式重新运行并输入验证码。");
        return "";
    }

    Console.Write($"[{label}] 短信验证码: ");
    return Console.ReadLine();
}

/// <summary>
/// 单台云电脑的保活循环。只负责"连接 -> 保活 -> 到期重连"，
/// 严格不向上一层抛异常；一旦判断票据失效，则通过 SessionState 请求重建整段会话。
/// </summary>
async Task KeepAliveWorkerAsync(
    CtYunApi api,
    AccountConfig account,
    Desktop desktop,
    RuntimeConfig runtimeConfig,
    CancellationToken sessionToken,
    SessionState sessionState)
{
    var label = AccountLabel(account);
    var desktopCode = desktop.DesktopCode ?? desktop.DesktopId;

    var connectFailures = 0;
    var rejectStreak = 0;

    try
    {
        await KeepAliveLoopAsync();
    }
    catch (OperationCanceledException)
    {
        // 会话取消：正常收工
    }
    catch (Exception ex)
    {
        // 本方法承诺不抛异常：任何意外都不能带走整段会话或进程
        Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktopCode}] 保活任务异常结束：{ex.Message}");
        sessionState.RequestSessionFailure($"[{desktopCode}] 保活任务异常结束：{ex.Message}");
    }
    return;

    async Task KeepAliveLoopAsync()
    {
        var initialPayload = Convert.FromBase64String("UkVEUQIAAAACAAAAGgAAAAAAAAABAAEAAAABAAAAEgAAAAkAAAAECAAA");
        var uri = new Uri($"wss://{desktop.DesktopInfo.ClinkLvsOutHost}/clinkProxy/{desktop.DesktopId}/MAIN");

        while (!sessionToken.IsCancellationRequested)
        {
            using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            cycleCts.CancelAfter(TimeSpan.FromSeconds(runtimeConfig.KeepAliveSeconds));

            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Origin", "https://pc.ctyun.cn");
            client.Options.AddSubProtocol("binary");

            var ready = false;
            var cycleCompleted = false;
            var readyAt = DateTime.UtcNow;
            string lastError = null;

            try
            {
                Utility.WriteLine(ConsoleColor.Cyan, $"[{label}][{desktopCode}] === 新周期开始，尝试连接 ===");
                await client.ConnectAsync(uri, cycleCts.Token);
                var hostParts = desktop.DesktopInfo.ClinkLvsOutHost.Split(':', 2);
                var connectMessage = new ConnecMessage
                {
                    type = 1,
                    ssl = 1,
                    host = hostParts[0],
                    port = hostParts.Length > 1 ? hostParts[1] : "443",
                    ca = desktop.DesktopInfo.CaCert,
                    cert = desktop.DesktopInfo.ClientCert,
                    key = desktop.DesktopInfo.ClientKey,
                    servername = desktop.DesktopInfo.Host + ":" + desktop.DesktopInfo.Port,
                    oqs = 0
                };

                var msgBytes = JsonSerializer.SerializeToUtf8Bytes(connectMessage, AppJsonSerializerContext.Default.ConnecMessage);
                await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, cycleCts.Token);

                await Task.Delay(500, cycleCts.Token);
                await client.SendAsync(initialPayload, WebSocketMessageType.Binary, true, cycleCts.Token);

                ready = true;
                readyAt = DateTime.UtcNow;
                connectFailures = 0;
                Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktopCode}] 连接已就绪，保持 {runtimeConfig.KeepAliveSeconds} 秒...");

                await ReceiveLoop(api, client, account, desktop, cycleCts.Token);
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // 本周期正常到期，视为一次健康连接
                cycleCompleted = true;
                rejectStreak = 0;
            }
            catch (Exception ex)
            {
                if (!ready)
                {
                    rejectStreak = 0;
                    connectFailures++;
                    Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktopCode}] 建连失败（第 {connectFailures}/{ConnectFailureLimit} 次）：{ex.Message}");
                }
                else
                {
                    // 就绪后的断开统一在下面按存活时长分类，这里只记录异常信息
                    lastError = ex.Message;
                }
            }
            finally
            {
                await CloseQuietlyAsync(client, label, desktopCode);
            }

            if (cycleCompleted && !ready)
            {
                connectFailures++;
                Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktopCode}] 连接在 {runtimeConfig.KeepAliveSeconds} 秒内未就绪（第 {connectFailures}/{ConnectFailureLimit} 次）。");
            }

            // 就绪后在周期内结束（对端主动关闭、被断开或异常断开）：
            // 只要不是本周期正常到期，就说明服务端不接受当前连接，计入 rejectStreak。
            if (ready && !cycleCompleted && !sessionToken.IsCancellationRequested)
            {
                var lived = DateTime.UtcNow - readyAt;
                rejectStreak++;
                var detail = lastError ?? "对端主动关闭连接";
                if (lived < TimeSpan.FromSeconds(ShortLivedSeconds))
                {
                    Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktopCode}] 就绪仅 {lived.TotalSeconds:F1} 秒即被服务端断开（连续第 {rejectStreak}/{RejectStreakLimit} 次）：{detail}");
                }
                else
                {
                    Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktopCode}] 运行 {Format(lived)} 后被服务端断开（连续第 {rejectStreak}/{RejectStreakLimit} 次，未跑满 {runtimeConfig.KeepAliveSeconds} 秒周期）：{detail}");
                }
            }

            if (sessionToken.IsCancellationRequested)
            {
                break;
            }

            // 票据失效判定：重建整段会话（重新登录并重新 connect 取新票据）
            if (rejectStreak >= RejectStreakLimit)
            {
                sessionState.RequestSessionRebuild($"[{desktopCode}] 连续 {rejectStreak} 次在保活周期内被服务端断开，连接票据可能已过期");
                return;
            }

            if (connectFailures >= ConnectFailureLimit)
            {
                sessionState.RequestSessionRebuild($"[{desktopCode}] 连续 {connectFailures} 次建连失败，连接票据或主机信息可能已过期");
                return;
            }

            // 失败后的短退避，避免以秒级频率空转重连
            if (connectFailures > 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, connectFailures - 1), 60));
                if (!await DelayAsync(delay, sessionToken))
                {
                    return;
                }
            }
            else if (rejectStreak > 0)
            {
                if (!await DelayAsync(TimeSpan.FromSeconds(3), sessionToken))
                {
                    return;
                }
            }
        }
    }
}

async Task ReceiveLoop(CtYunApi api, ClientWebSocket ws, AccountConfig account, Desktop desktop, CancellationToken ct)
{
    var buffer = new byte[8192];
    var encryptor = new Encryption();
    var label = AccountLabel(account);

    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        if (result.MessageType == WebSocketMessageType.Close) break;

        if (result.Count == 0)
        {
            continue;
        }

        var data = buffer.AsSpan(0, result.Count).ToArray();
        var hex = BitConverter.ToString(data).Replace("-", "");
        if (hex.StartsWith("52454451", StringComparison.OrdinalIgnoreCase))
        {
            Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] -> 收到保活校验");
            var response = encryptor.Execute(data);
            await ws.SendAsync(response, WebSocketMessageType.Binary, true, ct);
            Utility.WriteLine(ConsoleColor.DarkGreen, $"[{label}][{desktop.DesktopCode}] -> 发送保活响应成功");
            continue;
        }

        try
        {
            var infos = SendInfo.FromBuffer(data);
            foreach (var info in infos)
            {
                if (info.Type == 103)
                {
                    var payload = Encoding.UTF8.GetBytes("{\"type\":1,\"userName\":\"" + api.LoginInfo.UserName + "\",\"userInfo\":\"\",\"userId\":" + api.LoginInfo.UserId + "}");
                    var byUserName = new SendInfo { Type = 118, Data = payload }.ToBuffer(true);
                    await ws.SendAsync(byUserName, WebSocketMessageType.Binary, true, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.DarkYellow, $"[{label}][{desktop.DesktopCode}] 消息解析失败: {ex.Message}");
        }
    }
}

/// <summary>优雅关闭连接。异常一律吞掉：这里是历史上唯一能把整个进程带崩的位置。</summary>
async Task CloseQuietlyAsync(ClientWebSocket client, string label, string desktopCode)
{
    try
    {
        if (client.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Timeout Reset", closeCts.Token);
        }
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.DarkYellow, $"[{label}][{desktopCode}] 关闭连接异常（已忽略，不影响进程）：{ex.Message}");
    }
    finally
    {
        try
        {
            if (client.State != WebSocketState.Closed)
            {
                client.Abort();
            }
        }
        catch
        {
            // 忽略：连接已处于不可用状态
        }
    }
}

/// <summary>读取账号配置（含文件与交互输入），可能因数据目录不可写抛出异常。</summary>
AppConfig LoadAccountsConfig()
{
    var dataDir = GetDataDir();
    Directory.CreateDirectory(dataDir);

    var config = LoadAccountsFromFile(dataDir) ?? LoadAccountsFromEnvironment();
    if (config?.Accounts is not { Count: > 0 })
    {
        config = LoadAccountsFromConsole(dataDir);
    }

    foreach (var account in config.Accounts.Where(a => a != null))
    {
        account.Name = FirstNotEmpty(account.Name, account.User);
        account.DeviceCode = ResolveDeviceCode(account, dataDir);
    }

    return config;
}

/// <summary>把配置归一化为运行期配置，并对关键参数做边界约束。</summary>
RuntimeConfig BuildRuntimeConfig(AppConfig config)
{
    var dataDir = GetDataDir();
    var keepAliveSeconds = Math.Max(10, config.KeepAliveSeconds);
    var restartIntervalMinutes = Math.Max(0, config.RestartIntervalMinutes);
    var restartJitterSeconds = Math.Max(0, config.RestartJitterSeconds);
    var sessionCooldownSeconds = Math.Max(0, config.SessionCooldownSeconds);

    var overrideValue = Environment.GetEnvironmentVariable("CTYUN_RESTART_INTERVAL_MINUTES");
    if (int.TryParse(overrideValue, out var overrideMinutes))
    {
        restartIntervalMinutes = Math.Max(0, overrideMinutes);
        Utility.WriteLine(ConsoleColor.Yellow, $"环境变量 CTYUN_RESTART_INTERVAL_MINUTES={overrideMinutes} 已覆盖会话轮换周期。");
    }

    if (restartIntervalMinutes > 0)
    {
        var minimumMinutes = (int)Math.Ceiling(keepAliveSeconds * 2 / 60.0);
        if (restartIntervalMinutes < minimumMinutes)
        {
            Utility.WriteLine(ConsoleColor.Yellow, $"restartIntervalMinutes={restartIntervalMinutes} 小于两个保活周期，已自动调整为 {minimumMinutes} 分钟。");
            restartIntervalMinutes = minimumMinutes;
        }

        if (restartJitterSeconds >= restartIntervalMinutes * 60)
        {
            restartJitterSeconds = restartIntervalMinutes * 30;
        }
    }

    return new RuntimeConfig(
        config.Accounts,
        keepAliveSeconds,
        restartIntervalMinutes,
        restartJitterSeconds,
        sessionCooldownSeconds,
        dataDir);
}

AppConfig LoadAccountsFromEnvironment()
{
    var user = Environment.GetEnvironmentVariable("APP_USER");
    var password = Environment.GetEnvironmentVariable("APP_PASSWORD");
    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
    {
        return null;
    }

    return new AppConfig
    {
        Accounts =
        [
            new AccountConfig
            {
                Name = Environment.GetEnvironmentVariable("APP_NAME"),
                User = user,
                Password = password,
                DeviceCode = Environment.GetEnvironmentVariable("DEVICECODE")
            }
        ]
    };
}

AppConfig LoadAccountsFromFile(string dataDir)
{
    var configPath = Environment.GetEnvironmentVariable("CTYUN_CONFIG");
    if (string.IsNullOrWhiteSpace(configPath))
    {
        configPath = Path.Combine(dataDir, "accounts.json");
    }

    if (!File.Exists(configPath))
    {
        return null;
    }

    try
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppConfig);
        Utility.WriteLine(ConsoleColor.Green, $"已读取配置文件：{configPath}");
        return config;
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.Red, $"读取配置文件失败：{ex.Message}");
        return null;
    }
}

AppConfig LoadAccountsFromConsole(string dataDir)
{
    if (!CanReadFromConsole())
    {
        return new AppConfig();
    }

    var accounts = new List<AccountConfig>();
    while (true)
    {
        Console.Write("账号: ");
        var user = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(user))
        {
            break;
        }

        Console.Write("密码: ");
        var password = ReadPassword();
        accounts.Add(new AccountConfig { Name = user, User = user, Password = password });

        Console.Write("继续添加账号? (y/N): ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }
    }

    if (accounts.Count > 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"交互输入模式已读取 {accounts.Count} 个账号。设备码会保存到 {Path.Combine(dataDir, "devices")}。");
    }

    return new AppConfig { Accounts = accounts };
}

string ResolveDeviceCode(AccountConfig account, string dataDir)
{
    if (!string.IsNullOrWhiteSpace(account.DeviceCode))
    {
        return account.DeviceCode.Trim();
    }

    var devicesDir = Path.Combine(dataDir, "devices");
    Directory.CreateDirectory(devicesDir);
    var deviceCodePath = Path.Combine(devicesDir, SafeName(account.Name ?? account.User) + ".txt");
    if (!File.Exists(deviceCodePath))
    {
        File.WriteAllText(deviceCodePath, "web_" + GenerateRandomString(32));
    }

    return File.ReadAllText(deviceCodePath).Trim();
}

string GetDataDir()
{
    var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(dataDir))
    {
        return dataDir;
    }

    return IsRunningInContainer() ? "/app/data" : AppContext.BaseDirectory;
}

/// <summary>会话被拒后的等待：以冷却时间为起点指数退避，避免失效时高频重建登录。</summary>
static TimeSpan NextRejectWait(RuntimeConfig runtimeConfig, int rejectStreak)
{
    var baseSeconds = Math.Max(runtimeConfig.SessionCooldown.TotalSeconds, 5);
    var seconds = baseSeconds * Math.Pow(2, Math.Min(rejectStreak - 1, 6));
    return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelaySeconds));
}

static TimeSpan NextFailureWait(int failureStreak)
{
    var seconds = 30 * Math.Pow(2, Math.Min(failureStreak - 1, 6));
    return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelaySeconds));
}

static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
{
    if (delay <= TimeSpan.Zero)
    {
        return !ct.IsCancellationRequested;
    }

    try
    {
        await Task.Delay(delay, ct);
        return !ct.IsCancellationRequested;
    }
    catch (Exception)
    {
        // 取消（含 token 源已释放）一律视为"应当停止"，绝不让异常逃出调用点
        return false;
    }
}

static string Format(TimeSpan value) => value.TotalMinutes >= 1
    ? $"{value.TotalMinutes:F1} 分钟"
    : $"{value.TotalSeconds:F1} 秒";

static string GenerateRandomString(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
}

static string ReadPassword()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }

        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Remove(sb.Length - 1, 1);
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write("*");
        }
    }
}

static string AccountLabel(AccountConfig account) => account.Name ?? account.User;

static string SafeName(string value)
{
    var source = string.IsNullOrWhiteSpace(value) ? "default" : value;
    var builder = new StringBuilder(source.Length);
    foreach (var ch in source)
    {
        builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
    }
    return builder.ToString();
}

static string FirstNotEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static bool CanReadFromConsole() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

static bool IsRunningInContainer() => File.Exists("/.dockerenv");

enum SessionOutcome
{
    /// <summary>定时轮换到期，属于正常路径。</summary>
    Rotation,

    /// <summary>保活任务自行结束（非轮换、非失败）。</summary>
    Completed,

    /// <summary>连接票据失效，需要重建整段会话。</summary>
    Rejected,

    /// <summary>登录/取列表/建连等失败，延迟重试。</summary>
    Failed,

    /// <summary>需要人工介入，例如设备未绑定。</summary>
    NeedsManualIntervention
}

enum LoginOutcome
{
    Success,
    Failed,
    NeedsManualIntervention
}

/// <summary>一段会话的结果。KeepAliveStarted 表示本段会话是否真正进入过保活阶段。</summary>
record SessionResult(SessionOutcome Outcome, string Reason, TimeSpan Elapsed, bool KeepAliveStarted = false);

/// <summary>在会话内共享状态：让同一会话下的所有保活任务一起收工，并标记会话是否需要重建或已失败。</summary>
sealed class SessionState
{
    private readonly CancellationTokenSource _scope;
    private int _rebuildRequested;
    private int _failed;
    private string _reason;

    public SessionState(CancellationTokenSource scope) => _scope = scope;

    public bool RebuildRequested => Volatile.Read(ref _rebuildRequested) != 0;

    public bool Failed => Volatile.Read(ref _failed) != 0;

    public string RebuildReason => _reason;

    /// <summary>连接票据失效：需要重建整段会话（重新登录并重新获取票据）。</summary>
    public void RequestSessionRebuild(string reason)
    {
        if (Interlocked.Exchange(ref _rebuildRequested, 1) != 0)
        {
            return;
        }

        _reason = reason;
        Utility.WriteLine(ConsoleColor.Yellow, $"会话需要重建：{reason}");
        CancelScope();
    }

    /// <summary>会话已失去保活能力（非票据问题）：按失败退避，避免高频重新登录。</summary>
    public void RequestSessionFailure(string reason)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        _reason = reason;
        CancelScope();
    }

    private void CancelScope()
    {
        try
        {
            _scope.Cancel();
        }
        catch (Exception)
        {
            // 会话已结束或联动回调抛出聚合异常：都不影响调用方的收工语义
        }
    }
}

record RuntimeConfig(
    List<AccountConfig> Accounts,
    int KeepAliveSeconds,
    int RestartIntervalMinutes,
    int RestartJitterSeconds,
    int SessionCooldownSeconds,
    string DataDir)
{
    public TimeSpan SessionCooldown => TimeSpan.FromSeconds(SessionCooldownSeconds);

    /// <summary>本次会话应当运行多久；轮换关闭时表示不限制。</summary>
    public TimeSpan NextSessionLifetime()
    {
        if (RestartIntervalMinutes <= 0)
        {
            return Timeout.InfiniteTimeSpan;
        }

        var seconds = RestartIntervalMinutes * 60.0;
        if (RestartJitterSeconds > 0)
        {
            seconds += Random.Shared.Next(0, RestartJitterSeconds + 1);
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
