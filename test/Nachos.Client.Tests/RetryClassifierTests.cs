using Shouldly;

namespace Nachos.Client.Tests;

public sealed class RetryClassifierTests
{
    private const string W = "/v3/workspaces/{workspace_id}";
    private const string P = W + "/peers/{peer_id}";
    private const string S = W + "/sessions/{session_id}";
    private const string M = S + "/messages";
    private const string C = W + "/conclusions";
    private const string H = W + "/webhooks";

    public static TheoryData<string, string> ManifestRoutes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, path) in WireManifest.Routes)
        {
            data.Add(method, path);
        }

        return data;
    }

    [Fact]
    public void Manifest_IsLoaded()
    {
        WireManifest.Routes.Count.ShouldBe(55);
    }

    /// <summary>
    /// Every manifest route+method is classified (retryable / never / retryable-with-key). A route added to the
    /// manifest whose shape the classifier does not recognise fails here instead of silently never retrying.
    /// </summary>
    [Theory]
    [MemberData(nameof(ManifestRoutes))]
    public void EveryManifestRoute_IsClassified(string method, string path)
    {
        RetryClassifier.Classify(new HttpMethod(method), path).ShouldNotBeNull($"{method} {path} has no retry classification");
    }

    [Theory]
    // Reads: GET and the read-only POSTs.
    [InlineData("GET", M + "/{message_id}", RetryCategory.Retryable)]
    [InlineData("GET", S + "/peers", RetryCategory.Retryable)]
    [InlineData("GET", S + "/peers/{peer_id}/config", RetryCategory.Retryable)]
    [InlineData("POST", "/v3/workspaces/list", RetryCategory.Retryable)]
    [InlineData("POST", W + "/sessions/list", RetryCategory.Retryable)]
    [InlineData("POST", M + "/list", RetryCategory.Retryable)]
    [InlineData("POST", W + "/search", RetryCategory.Retryable)]
    [InlineData("POST", P + "/search", RetryCategory.Retryable)]
    [InlineData("POST", C + "/query", RetryCategory.Retryable)]
    [InlineData("POST", P + "/representation", RetryCategory.Retryable)]
    [InlineData("POST", P + "/sessions", RetryCategory.Retryable)]
    // Idempotent writes: PUT, get-or-create POST keyed by id, DELETE, membership adds.
    [InlineData("PUT", W, RetryCategory.Retryable)]
    [InlineData("PUT", M + "/{message_id}", RetryCategory.Retryable)]
    [InlineData("PUT", S + "/peers", RetryCategory.Retryable)]
    [InlineData("POST", "/v3/workspaces", RetryCategory.Retryable)]
    [InlineData("POST", W + "/peers", RetryCategory.Retryable)]
    [InlineData("POST", W + "/sessions", RetryCategory.Retryable)]
    [InlineData("POST", W + "/scopes", RetryCategory.Retryable)]
    [InlineData("POST", S + "/peers", RetryCategory.Retryable)]
    [InlineData("DELETE", S, RetryCategory.Retryable)]
    [InlineData("DELETE", S + "/peers", RetryCategory.Retryable)]
    [InlineData("DELETE", C + "/{conclusion_id}", RetryCategory.Retryable)]
    // Never: H/test, every chat call, and non-idempotent POSTs that accept no Idempotency-Key.
    [InlineData("GET", H + "/test", RetryCategory.Never)]
    [InlineData("POST", W + "/chat", RetryCategory.Never)]
    [InlineData("POST", P + "/chat", RetryCategory.Never)]
    [InlineData("POST", "/v3/keys", RetryCategory.Never)]
    [InlineData("POST", W + "/schedule_dream", RetryCategory.Never)]
    [InlineData("POST", H, RetryCategory.Never)]
    [InlineData("POST", "/v3/admin/grants", RetryCategory.Never)]
    // Non-idempotent mutations: retryable only with an Idempotency-Key.
    [InlineData("POST", M, RetryCategory.RequiresIdempotencyKey)]
    [InlineData("POST", M + "/", RetryCategory.RequiresIdempotencyKey)]
    [InlineData("POST", M + "/upload", RetryCategory.RequiresIdempotencyKey)]
    [InlineData("POST", C, RetryCategory.RequiresIdempotencyKey)]
    [InlineData("POST", S + "/clone", RetryCategory.RequiresIdempotencyKey)]
    public void Classify_NamedCases(string method, string template, RetryCategory expected)
    {
        RetryClassifier.Classify(new HttpMethod(method), template).ShouldBe(expected);
    }

    [Theory]
    [InlineData("GET", M + "/{message_id}", false, true)]
    [InlineData("POST", W + "/peers/list", false, true)]
    [InlineData("PUT", S, false, true)]
    [InlineData("DELETE", W, false, true)]
    [InlineData("POST", W + "/sessions", false, true)]
    [InlineData("GET", H + "/test", false, false)]
    [InlineData("GET", H + "/test", true, false)]
    [InlineData("POST", W + "/chat", true, false)]
    [InlineData("POST", P + "/chat", false, false)]
    [InlineData("POST", M, false, false)]
    [InlineData("POST", M, true, true)]
    [InlineData("POST", M + "/upload", false, false)]
    [InlineData("POST", M + "/upload", true, true)]
    [InlineData("POST", C, false, false)]
    [InlineData("POST", C, true, true)]
    [InlineData("POST", S + "/clone", false, false)]
    [InlineData("POST", S + "/clone", true, true)]
    [InlineData("POST", "/v3/keys", true, false)]
    public void IsRetryable_FollowsSpec16(string method, string template, bool hasKey, bool expected)
    {
        RetryClassifier.IsRetryable(new HttpMethod(method), template, hasKey).ShouldBe(expected);
    }

    [Theory]
    [InlineData("POST", "/v3/unknown")]
    [InlineData("POST", W + "/something_new")]
    [InlineData("PATCH", W)]
    [InlineData("HEAD", W)]
    public void UnknownRoute_IsUnclassified_AndNeverRetried(string method, string template)
    {
        RetryClassifier.Classify(new HttpMethod(method), template).ShouldBeNull();
        RetryClassifier.IsRetryable(new HttpMethod(method), template, hasIdempotencyKey: true).ShouldBeFalse();
    }
}
