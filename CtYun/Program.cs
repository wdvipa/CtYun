using CtYun;
using CtYun.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var globalCts = new CancellationTokenSource();

Utility.WriteLine(ConsoleColor.Green, $"版本：v {Assembly.GetEntryAssembly()?.GetName().Version}");
Utility.WriteLine(ConsoleColor.DarkCyan, $"运行平台：{GetRuntimeDescription()}");

var accounts = ResolveAccounts();
if (accounts.Count == 0)
{
    Utility.WriteLine(ConsoleColor.Red, "未找到可用账号配置。请使用环境变量或 accounts.json 提供账号信息。");
    return;
}

Console.CancelKeyPress += (s, e) => { e.Cancel = true; globalCts.Cancel(); };

var tasks = accounts.Select(account => RunAccountAsync(account, globalCts.Token)).ToArray();

try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
    Utility.WriteLine(ConsoleColor.Yellow, "程序已停止。");
}

async Task RunAccountAsync(AccountCredential account, CancellationToken globalToken)
{
    var accountName = GetAccountDisplayName(account);
    Utility.WriteLine(ConsoleColor.Cyan, $"[{accountName}] 开始处理账号");

    var cyApi = new CtYunApi(account.DeviceCode);
    if (!await PerformLoginSequence(cyApi, account, accountName))
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{accountName}] 登录流程失败");
        return;
    }

    var desktopList = await cyApi.GetLlientListAsync();
    if (desktopList == null || desktopList.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{accountName}] 未获取到可用桌面");
        return;
    }

    var activeDesktops = new List<Desktop>();
    foreach (var d in desktopList)
    {
        if (d.UseStatusText != "运行中")
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{accountName}] [{d.DesktopCode}] [{d.UseStatusText}] 电脑未开机，正在开机，请在2分钟后重新运行软件");
        }

        var connectResult = await cyApi.ConnectAsync(d.DesktopId);
        if (connectResult.Success && connectResult.Data.DesktopInfo != null)
        {
            d.DesktopInfo = connectResult.Data.DesktopInfo;
            activeDesktops.Add(d);
        }
        else
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{accountName}] Connect Error: [{d.DesktopId}] {connectResult.Msg}");
        }
    }

    if (activeDesktops.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{accountName}] 没有可保活的运行中桌面");
        return;
    }

    Utility.WriteLine(ConsoleColor.Yellow, $"[{accountName}] 保活任务启动：共 {activeDesktops.Count} 台设备，每 60 秒强制重连一次。");

    var keepAliveTasks = activeDesktops.Select(d => KeepAliveWorkerWithForcedReset(accountName, cyApi, d, globalToken));
    await Task.WhenAll(keepAliveTasks);
}

async Task KeepAliveWorkerWithForcedReset(string accountName, CtYunApi cyApi, Desktop desktop, CancellationToken globalToken)
{
    var initialPayload = Convert.FromBase64String("UkVEUQIAAAACAAAAGgAAAAAAAAABAAEAAAABAAAAEgAAAAkAAAAECAAA");
    var uri = new Uri($"wss://{desktop.DesktopInfo.ClinkLvsOutHost}/clinkProxy/{desktop.DesktopId}/MAIN");

    while (!globalToken.IsCancellationRequested)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        sessionCts.CancelAfter(TimeSpan.FromMinutes(60));

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://pc.ctyun.cn");
        client.Options.AddSubProtocol("binary");

        try
        {
            Utility.WriteLine(ConsoleColor.Cyan, $"[{accountName}] [{desktop.DesktopCode}] === 新周期开始，尝试连接 ===");
            await client.ConnectAsync(uri, sessionCts.Token);

            var connectMessage = new ConnecMessage
            {
                type = 1,
                ssl = 1,
                host = desktop.DesktopInfo.ClinkLvsOutHost.Split(":")[0],
                port = desktop.DesktopInfo.ClinkLvsOutHost.Split(":")[1],
                ca = desktop.DesktopInfo.CaCert,
                cert = desktop.DesktopInfo.ClientCert,
                key = desktop.DesktopInfo.ClientKey,
                servername = desktop.DesktopInfo.Host + ":" + desktop.DesktopInfo.Port,
                oqs = 0
            };
            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(connectMessage, AppJsonSerializerContext.Default.ConnecMessage);
            await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, sessionCts.Token);

            await Task.Delay(500, sessionCts.Token);
            await client.SendAsync(initialPayload, WebSocketMessageType.Binary, true, sessionCts.Token);

            Utility.WriteLine(ConsoleColor.Green, $"[{accountName}] [{desktop.DesktopCode}] 连接已就绪，保持 60 秒...");

            try
            {
                await ReceiveLoop(accountName, cyApi, client, desktop, sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                Utility.WriteLine(ConsoleColor.Yellow, $"[{accountName}] [{desktop.DesktopCode}] 60秒时间到，准备重连...");
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{accountName}] [{desktop.DesktopCode}] 异常: {ex.Message}");
            await Task.Delay(5000, globalToken);
        }
        finally
        {
            if (client.State == WebSocketState.Open)
            {
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Timeout Reset", CancellationToken.None);
            }
        }
    }
}

async Task ReceiveLoop(string accountName, CtYunApi cyApi, ClientWebSocket ws, Desktop desktop, CancellationToken ct)
{
    var buffer = new byte[8192];
    var encryptor = new Encryption();

    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        if (result.MessageType == WebSocketMessageType.Close) break;

        if (result.Count <= 0)
        {
            continue;
        }

        var data = buffer.AsSpan(0, result.Count).ToArray();
        var hex = BitConverter.ToString(data).Replace("-", "");
        if (hex.StartsWith("52454451", StringComparison.OrdinalIgnoreCase))
        {
            Utility.WriteLine(ConsoleColor.Green, $"[{accountName}] [{desktop.DesktopCode}] -> 收到保活校验");
            var response = encryptor.Execute(data);
            await ws.SendAsync(response, WebSocketMessageType.Binary, true, ct);
            Utility.WriteLine(ConsoleColor.DarkGreen, $"[{accountName}] [{desktop.DesktopCode}] -> 发送保活响应成功");
            continue;
        }

        try
        {
            var infos = SendInfo.FromBuffer(data);
            foreach (var info in infos)
            {
                if (info.Type == 103)
                {
                    var byUserName = new SendInfo
                    {
                        Type = 118,
                        Data = Encoding.UTF8.GetBytes("{\"type\":1,\"userName\":\"" + cyApi.LoginInfo.UserName + "\",\"userInfo\":\"\",\"userId\":" + cyApi.LoginInfo.UserId + "}")
                    }.ToBuffer(true);
                    await ws.SendAsync(byUserName, WebSocketMessageType.Binary, true, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{accountName}] [{desktop.DesktopCode}] 消息处理错误: {ex.Message}");
        }
    }
}

#region 辅助工具
static List<AccountCredential> ResolveAccounts()
{
    var accounts = LoadAccountsFromEnvironment();
    if (accounts.Count > 0)
    {
        return NormalizeAccounts(accounts);
    }

    accounts = LoadAccountsFromConfigFile();
    if (accounts.Count > 0)
    {
        return NormalizeAccounts(accounts);
    }

    var interactiveAccount = ResolveInteractiveAccount();
    return interactiveAccount == null ? [] : [interactiveAccount];
}

static List<AccountCredential> LoadAccountsFromEnvironment()
{
    var users = SplitMultiValue(Environment.GetEnvironmentVariable("APP_USERS"));
    var passwords = SplitMultiValue(Environment.GetEnvironmentVariable("APP_PASSWORDS"));
    var deviceCodes = SplitMultiValue(Environment.GetEnvironmentVariable("DEVICECODES"));
    var names = SplitMultiValue(Environment.GetEnvironmentVariable("APP_NAMES"));

    if (users.Count > 0 && passwords.Count > 0)
    {
        var accounts = new List<AccountCredential>();
        for (var i = 0; i < users.Count; i++)
        {
            accounts.Add(new AccountCredential
            {
                Name = i < names.Count ? names[i] : null,
                User = users[i],
                Password = i < passwords.Count ? passwords[i] : passwords[^1],
                DeviceCode = i < deviceCodes.Count ? deviceCodes[i] : null
            });
        }
        return accounts;
    }

    if (HasEnvironmentCredentials())
    {
        return [new AccountCredential
        {
            Name = Environment.GetEnvironmentVariable("APP_NAME"),
            User = Environment.GetEnvironmentVariable("APP_USER"),
            Password = Environment.GetEnvironmentVariable("APP_PASSWORD"),
            DeviceCode = Environment.GetEnvironmentVariable("DEVICECODE")
        }];
    }

    return [];
}

static List<AccountCredential> LoadAccountsFromConfigFile()
{
    var path = GetAccountsConfigPath();
    if (!File.Exists(path))
    {
        return [];
    }

    try
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.MultiAccountConfig);
        return config?.Accounts ?? [];
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.Red, $"读取账号配置文件失败: {ex.Message}");
        return [];
    }
}

static AccountCredential ResolveInteractiveAccount()
{
    var deviceCodeFile = GetDeviceCodeFilePath();
    if (!File.Exists(deviceCodeFile)) File.WriteAllText(deviceCodeFile, "web_" + GenerateRandomString(32));
    var code = File.ReadAllText(deviceCodeFile).Trim();
    Console.Write("账号: "); var u = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(u)) return null;
    Console.Write("密码: "); var p = ReadPassword();
    return new AccountCredential
    {
        Name = u,
        User = u,
        Password = p,
        DeviceCode = code
    };
}

static List<AccountCredential> NormalizeAccounts(List<AccountCredential> accounts)
{
    var result = new List<AccountCredential>();
    for (var i = 0; i < accounts.Count; i++)
    {
        var account = accounts[i];
        if (string.IsNullOrWhiteSpace(account.User) || string.IsNullOrWhiteSpace(account.Password))
        {
            continue;
        }

        account.User = account.User.Trim();
        account.Password = account.Password.Trim();
        account.Name = string.IsNullOrWhiteSpace(account.Name) ? account.User : account.Name.Trim();
        account.DeviceCode = string.IsNullOrWhiteSpace(account.DeviceCode)
            ? "web_" + GenerateRandomString(32)
            : account.DeviceCode.Trim();
        result.Add(account);
    }
    return result;
}

static List<string> SplitMultiValue(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return [];
    }

    return value.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

static async Task<bool> PerformLoginSequence(CtYunApi api, AccountCredential account, string accountName)
{
    if (!await api.LoginAsync(account.User, account.Password)) return false;
    if (!api.LoginInfo.BondedDevice)
    {
        await api.GetSmsCodeAsync(account.User);
        Console.Write($"[{accountName}] 短信验证码: ");
        if (!await api.BindingDeviceAsync(Console.ReadLine())) return false;
    }
    return true;
}

static string GetAccountDisplayName(AccountCredential account)
{
    return string.IsNullOrWhiteSpace(account.Name) ? account.User : account.Name;
}

static string GetAccountsConfigPath()
{
    if (IsAndroid())
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, "accounts.json");
        }
    }

    return "accounts.json";
}

static string GenerateRandomString(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
}

static bool IsAndroid() => OperatingSystem.IsAndroid();

static bool HasEnvironmentCredentials()
{
    return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_USER"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APP_PASSWORD"));
}

static string GetDeviceCodeFilePath()
{
    if (IsAndroid())
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, "DeviceCode.txt");
        }
    }

    return "DeviceCode.txt";
}

static string GetRuntimeDescription()
{
    if (OperatingSystem.IsAndroid()) return "Android";
    if (OperatingSystem.IsLinux()) return "Linux";
    if (OperatingSystem.IsWindows()) return "Windows";
    if (OperatingSystem.IsMacOS()) return "macOS";
    return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
}

static string ReadPassword()
{
    StringBuilder sb = new StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0) { sb.Remove(sb.Length - 1, 1); Console.Write("\b \b"); }
        else if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write("*"); }
    }
    Console.WriteLine();
    return sb.ToString();
}
#endregion
