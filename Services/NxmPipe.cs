using System.IO.Pipes;
using System.Text;

namespace StardewToolkit.Services;

/// <summary>
/// nxm:// 链接跨实例传递：浏览器拉起的「第二个实例」用命名管道把链接转发给已开的主实例后退出。
/// 管道名按进程位数固定，单机自用足够；主实例 Shown 时开始监听，进程退出自然回收。
/// </summary>
public static class NxmPipe
{
    public const string PipeName = "StardewToolkit-nxm";

    /// <summary>主实例开始监听；收到链接回调一次。永远循环，进程退出时随之结束。</summary>
    public static void StartListening(Action<string> onLink) => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try { onLink(line.Trim()); }
                    catch { /* 回调里出错别拖垮管道监听线程 */ }
                }
            }
            catch
            {
                // 一次失败等一小会再补建管道，别让一次竞态毁掉整个监听
                await Task.Delay(500);
            }
        }
    });

    /// <summary>第二实例把链接发给主实例；发不出去（主实例不在了）返回 false。</summary>
    public static bool TrySend(string link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // 主实例在忙 UI 时可能接不上，给个短超时，超时就宁可让用户重点一次
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(link);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
