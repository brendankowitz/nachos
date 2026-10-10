using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class BodyBoundaryTests
{
    [Theory]
    [InlineData("FF")]
    [InlineData("C0AF")]
    [InlineData("EDA080")]
    [InlineData("F4908080")]
    [InlineData("E282")]
    [InlineData("80")]
    public async Task PreliminaryAuthorization_RejectsRawInvalidUtf8WithLocated422(string hex)
    {
        using var host = new AuthHost();
        _ = host.Http;
        using var stream = new MemoryStream([.. "{\"id\":\"A\",\"future\":\""u8, .. Convert.FromHexString(hex), .. "\"}"u8]);
        var result = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces";
            context.Request.Headers.Authorization = "Bearer " + AuthHost.Key("workspace");
            context.Request.Body = stream;
        });
        result.Response.StatusCode.ShouldBe(422);
        using var reader = new StreamReader(result.Response.Body);
        AdmissionReplayTests.AssertDepthError(await reader.ReadToEndAsync());
        (await host.Store.Workspaces.GetAsync("A", default)).ShouldBeNull();
    }

    [Theory]
    [InlineData("none", 401)]
    [InlineData("peer", 401)]
    [InlineData("other", 422)]
    [InlineData("workspace", 422)]
    public async Task MalformedBody_PreservesAuthorizationPrecedence(string identity, int status)
    {
        using var host = new AuthHost();
        await host.Send("POST", "/v3/workspaces", "{", identity == "none" ? null : AuthHost.Key(identity), status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyReadFailure_PreservesOriginalExceptionIdentity(bool unrelatedCancellation)
    {
        using var host = new AuthHost();
        _ = host.Http;
        Exception failure = unrelatedCancellation ? new OperationCanceledException("synthetic") : new IOException("synthetic");
        using var body = new FragmentedBody([], failure);
        var result = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces";
            context.Request.Headers.Authorization = "Bearer " + AuthHost.Key("workspace");
            context.Request.Body = body;
        });
        result.Response.StatusCode.ShouldBe(500);
        result.Features.Get<IExceptionHandlerFeature>().ShouldNotBeNull().Error.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task OneByteNonseekableBody_IsCopiedAndRewound()
    {
        using var host = new AuthHost();
        _ = host.Http;
        var bytes = Encoding.UTF8.GetBytes("\uFEFF{\"id\":\"A\",\"metadata\":{\"name\":\"caf\\u00e9\"}}");
        using var body = new FragmentedBody(bytes);
        var result = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces";
            context.Request.Headers.Authorization = "Bearer " + AuthHost.Key("workspace");
            context.Request.Body = body;
        });
        result.Response.StatusCode.ShouldBe(200);
        body.ReadCount.ShouldBe(bytes.Length + 1);
        (await host.Store.Workspaces.GetAsync("A", default)).ShouldNotBeNull().Metadata["name"]!.GetValue<string>()
            .ShouldBe("caf\u00e9");
    }

    [Theory]
    [InlineData(null, "", 200)]
    [InlineData(5L, "", 200)]
    [InlineData(0L, " ", 422)]
    [InlineData(0L, "{", 422)]
    public async Task OptionalListBody_UsesActualBytesNotContentLength(long? length, string text, int status)
    {
        using var host = new AuthHost();
        await host.Seed();
        using var body = new FragmentedBody(Encoding.UTF8.GetBytes(text));
        var result = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/v3/workspaces/A/sessions/list";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = length;
            context.Request.Headers.Authorization = "Bearer " + AuthHost.Key("workspace");
            context.Request.Body = body;
        });
        result.Response.StatusCode.ShouldBe(status);
    }

    private sealed class FragmentedBody(byte[] bytes, Exception? failure = null) : Stream
    {
        private int _position;
        internal int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) return ValueTask.FromException<int>(failure);
            ReadCount++;
            if (_position == bytes.Length) return ValueTask.FromResult(0);
            buffer.Span[0] = bytes[_position++];
            return ValueTask.FromResult(1);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
