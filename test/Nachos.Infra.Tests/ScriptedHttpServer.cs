using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nachos.Infra.Tests;

/// <summary>
/// A 127.0.0.1-only HTTP listener that answers the n-th request with the n-th scripted response (the last one
/// repeats), so a hook's polling can be exercised without any network. Counts requests.
/// </summary>
internal sealed class ScriptedHttpServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly (HttpStatusCode Status, string Body)[] responses;
    private readonly Task loop;
    private int requests;

    public ScriptedHttpServer(params (HttpStatusCode Status, string Body)[] responses)
    {
        this.responses = responses;
        Uri = new Uri($"http://127.0.0.1:{FreePort()}/");
        listener.Prefixes.Add(Uri.ToString());
        listener.Start();
        loop = Task.Run(ServeAsync);
    }

    public Uri Uri { get; }

    public int Requests => Volatile.Read(ref requests);

    public void Dispose()
    {
        listener.Close();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends with an exception when the listener closes under it.
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var n = Interlocked.Increment(ref requests);
            var (status, body) = responses[Math.Min(n, responses.Length) - 1];
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = body.StartsWith('{') ? "application/json" : "text/plain";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }
}
