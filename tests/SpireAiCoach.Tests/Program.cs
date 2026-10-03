using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length > 0 && args[0] == "--isolation-child")
    return await WorkerIsolationTests.Child(args);

var tests = new List<(string, Func<Task>)>();
void Test(string name, Action test) => tests.Add((name, () => { test(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> test) => tests.Add((name, test));
AsyncTest("worker isolation prevents inherited save locks and preserves environment and arguments", WorkerIsolationTests.Run);
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject(Action action, string category)
{
    try { action(); } catch (CoachException ex) { Check(ex.Category == category, $"Expected {category}, got {ex.Category}"); return; }
    throw new Exception("Expected rejection");
}
async Task RejectAsync(Func<Task> action, string category)
{
    try { await action(); } catch (CoachException ex) { Check(ex.Category == category, $"Expected {category}, got {ex.Category}"); return; }
    throw new Exception("Expected rejection");
}
CardInfo Card(string id, bool playable = true) => new(id, "STRIKE", "sts2", "打击", "造成 6 点伤害。", 1, false, 0,
    "Attack", "AnyEnemy", 0, playable, ["enemy-1", "enemy-2"], [], new Dictionary<string, decimal>(), new Dictionary<string, IReadOnlyDictionary<string, decimal>?>());
PileInfo Pile(params CardInfo[] cards) => new(cards.Length, cards);
CreatureInfo Enemy(string id) => new(id, "同名敌人", 20, 20, 0, [], [new("Attack", "攻击", 6, 1)], "ATTACK", []);
CombatSnapshot Snapshot() => new("combat-a", 1, true, "Play", 1, 0,
    new("me", "铁甲战士", 50, 80, 0, 3, 3, 0, [], [], [new("potion-1", "药水", "效果", "AnyEnemy", true, ["enemy-1"])], [], []),
    Pile(Card("card-1"), Card("card-2")), Pile(Card("card-3")), Pile(), Pile(), Pile(),
    [Enemy("enemy-1"), Enemy("enemy-2")], [], true, ["card-3"], []);
JsonObject Step(string action, string? card = null, string? potion = null, string? target = null, string condition = "") =>
    new() { ["action"] = action, ["card_id"] = card, ["potion_id"] = potion, ["target_id"] = target, ["condition"] = condition, ["reason"] = "战术理由" };
JsonObject Reply(CombatSnapshot state) => new()
{
    ["snapshot_id"] = state.Fingerprint(), ["summary"] = "集中攻击一个敌人。",
    ["steps"] = new JsonArray(Step("play_card", "card-1", target: "enemy-1"), Step("end_turn")),
    ["uncertainties"] = new JsonArray()
};
JsonObject First(JsonObject reply) => reply["steps"]![0]!.AsObject();
var state = Snapshot();
Test("pile totals preserve duplicates and explicit counts", () =>
{
    Check(state.Hand.Count == 2 && state.Hand.CountsByCard.Single().Count == 2);
});
Test("Windows key persistence encrypts and deletes on opt-out", () =>
{
    if (!OperatingSystem.IsWindows()) return;
    var directory = Path.GetFullPath(Path.Combine("work", "settings-test-" + Guid.NewGuid().ToString("N")));
    var store = new SettingsStore(directory);
    var settings = new CoachSettings { Model = "test-model", RememberKey = true };
    try
    {
        store.Save(settings, "nonsecret-test-key");
        Check(store.Load().Key == "nonsecret-test-key");
        Check(!File.ReadAllText(Path.Combine(directory, "config.json")).Contains("nonsecret-test-key"));
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi"))).Contains("nonsecret-test-key"));
        store.Save(settings with { RememberKey = false }, "session-key");
        Check(store.Load().Key == "" && !File.Exists(Path.Combine(directory, "api-key.dpapi")));
    }
    finally
    {
        foreach (var file in new[] { "api-key.dpapi", "config.json", "api-key.dpapi.tmp", "config.json.tmp" })
            File.Delete(Path.Combine(directory, file));
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
});
Test("valid plan maps distinct copies and targets", () =>
{
    var json = Reply(state);
    json["steps"]!.AsArray().Insert(1, Step("play_card", "card-2", target: "enemy-2"));
    var advice = AdviceContract.Parse(json.ToJsonString(), state);
    Check(advice.Steps.Count == 3);
    Check(AdviceFormatter.Format(advice, state).Contains("enemy-2"));
});
Test("reassess is a legitimate entire plan", () =>
{
    var json = Reply(state); json["steps"] = new JsonArray(Step("reassess"));
    Check(AdviceContract.Parse(json.ToJsonString(), state).Steps.Single().Action == "reassess");
});
Test("potion action supported", () =>
{
    var json = Reply(state); json["steps"]![0] = Step("use_potion", potion: "potion-1", target: "enemy-1");
    Check(AdviceContract.Parse(json.ToJsonString(), state).Steps[0].PotionId == "potion-1");
});
Test("extra fields are projected away recursively", () =>
{
    var json = Reply(state); json["ignored"] = new JsonObject { ["anything"] = true };
    First(json)["extra"] = new JsonArray(1, 2);
    var projected = Wire.Serialize(AdviceContract.Parse(json.ToJsonString(), state));
    Check(!projected.Contains("ignored") && !projected.Contains("extra"));
});
Test("fenced JSON normalized", () => Check(AdviceContract.Parse("```json\n" + Reply(state).ToJsonString() + "\n```", state).Steps.Count == 2));
Test("inactive fields normalized without affecting plan", () =>
{
    var json = Reply(state); First(json)["potion_id"] = new JsonObject { ["unused"] = true };
    Check(AdviceContract.Parse(json.ToJsonString(), state).Steps[0].PotionId == null);
});
Test("missing consumed field rejected", () =>
{
    var json = Reply(state); First(json).Remove("reason"); Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "schema");
});
Test("wrong type rejected", () =>
{
    var json = Reply(state); First(json)["card_id"] = 1; Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "schema");
});
Test("unknown action rejected", () =>
{
    var json = Reply(state); First(json)["action"] = "auto_play"; Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "contract");
});
Test("unknown card rejected", () =>
{
    var json = Reply(state); First(json)["card_id"] = "absent"; Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "identity");
});
Test("unknown target rejected", () =>
{
    var json = Reply(state); First(json)["target_id"] = "absent"; Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "identity");
});
Test("missing attack target rejected", () =>
{
    var json = Reply(state); First(json)["target_id"] = null; Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "illegal_first_action");
});
Test("nonhand first card rejected", () =>
{
    var json = Reply(state); First(json)["card_id"] = "card-3"; Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "illegal_first_action");
});
Test("unplayable first card rejected", () =>
{
    var blocked = state with { Hand = Pile(Card("card-1", false), Card("card-2")) };
    Reject(() => AdviceContract.Parse(Reply(blocked).ToJsonString(), blocked), "illegal_first_action");
});
Test("changed snapshot rejected", () => Reject(() => AdviceContract.Parse(Reply(state).ToJsonString(), state with { Round = 2 }), "stale_snapshot"));
Test("same visible state after a combat event still invalidates advice", () =>
    Reject(() => AdviceContract.Parse(Reply(state).ToJsonString(), state with { ObservationRevision = 1 }), "stale_snapshot"));
Test("same-named card instances affect fingerprint", () => Check(state.Fingerprint() != (state with { Hand = Pile(Card("card-9"), Card("card-2")) }).Fingerprint()));
Test("empty steps rejected", () =>
{
    var json = Reply(state); json["steps"] = new JsonArray(); Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "schema");
});
Test("overflow rejected without silent truncation", () =>
{
    var json = Reply(state); json["steps"] = new JsonArray(Enumerable.Range(0, 65).Select(_ => (JsonNode)Step("reassess")).ToArray());
    Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "schema");
});
Test("terminal action must be last", () =>
{
    var json = Reply(state); json["steps"]!.AsArray().Add(Step("play_card", "card-2", target: "enemy-1"));
    Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "contract");
});
Test("duplicate instance requires reuse condition", () =>
{
    var json = Reply(state); json["steps"]!.AsArray().Insert(1, Step("play_card", "card-1", target: "enemy-1"));
    Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "condition");
    json["steps"]![1]!["condition"] = "如果此卡已返回手牌且仍可打出";
    Check(AdviceContract.Parse(json.ToJsonString(), state).Steps.Count == 3);
});
Test("future known draw needs condition", () =>
{
    var json = Reply(state); json["steps"]!.AsArray().Insert(1, Step("play_card", "card-3", target: "enemy-1"));
    Reject(() => AdviceContract.Parse(json.ToJsonString(), state), "condition");
    json["steps"]![1]!["condition"] = "抽到该卡且费用足够后";
    Check(AdviceContract.Parse(json.ToJsonString(), state).Steps.Count == 3);
});
Test("prompt protocol inventory is complete", () =>
{
    foreach (var field in AdviceContract.RootFields.Concat(AdviceContract.StepFields).Concat(AdviceContract.Actions))
        Check(PromptBuilder.SystemPrompt.Contains(field), field);
    Check(!PromptBuilder.UserPrompt(state).Contains("all_cards"));
});
Test("URL normalization", () =>
{
    Check(new CoachSettings { BaseUrl = "https://example.org" }.Endpoint().AbsoluteUri == "https://example.org/v1/chat/completions");
    Check(new CoachSettings { BaseUrl = "https://example.org/custom/v1/" }.Endpoint().AbsoluteUri == "https://example.org/custom/v1/chat/completions");
    Check(new CoachSettings { BaseUrl = "http://localhost:11434/v1/chat/completions" }.Endpoint().AbsoluteUri == "http://localhost:11434/v1/chat/completions");
});
Test("unsafe or credential-bearing URL rejected", () =>
{
    foreach (var url in new[] { "http://example.org/v1", "https://u:key@example.org/v1", "https://example.org/v1?key=secret", "file:///etc/passwd" })
        Reject(() => new CoachSettings { BaseUrl = url }.Endpoint(), "configuration");
});
string Envelope(string content, string finish = "stop") => JsonSerializer.Serialize(new
{
    id = "fake-request", model = "test-model", choices = new[] { new { message = new { content }, finish_reason = finish } },
    usage = new { total_tokens = 123 }
});
CoachSettings Settings() => new() { BaseUrl = "https://example.org/v1", Model = "test-model" };
AsyncTest("HTTP request contains frozen state without token cap or secret in prompt", async () =>
{
    string? requestBody = null;
    using var client = new CoachClient(new FakeHandler(async (req, _) =>
    {
        Check(req.Headers.Authorization?.Parameter == "dummy-key");
        requestBody = await req.Content!.ReadAsStringAsync();
        Check(!requestBody.Contains("dummy-key") && !requestBody.Contains("max_tokens") && !requestBody.Contains("max_completion_tokens"));
        Check(requestBody.Contains(state.Fingerprint()));
        return new(HttpStatusCode.OK) { Content = new StringContent(Envelope(Reply(state).ToJsonString())) };
    }));
    var trace = new CallDiagnostics(); string? preview = null;
    var result = await client.AnalyzeAsync(Settings(), "dummy-key", state, diagnostics: trace, onContent: text => preview = text);
    Check(result.RequestId == "fake-request" && result.Advice.Steps.Count == 2 && requestBody != null);
    Check(trace.ResponseFormat == "json" && !trace.StreamCompleted && preview == Reply(state).ToJsonString());
});
AsyncTest("provider authentication body never surfaced and no retries", async () =>
{
    int calls = 0;
    using var client = new CoachClient(new FakeHandler((_, _) =>
    {
        calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-echo") });
    }));
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "dummy-key", state), "authentication"); Check(calls == 1);
});
AsyncTest("truncated output rejected before parsing", async () =>
{
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Envelope(Reply(state).ToJsonString(), "length")) })));
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state), "truncated");
});
AsyncTest("malformed provider response classified", async () =>
{
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<html>error</html>") })));
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state), "provider_json");
});
AsyncTest("explicit cancellation propagates", async () =>
{
    using var client = new CoachClient(new FakeHandler(async (_, token) => { await Task.Delay(10000, token); return new(HttpStatusCode.OK); }));
    using var cancel = new CancellationTokenSource(40);
    try { await client.AnalyzeAsync(Settings(), "", state, cancel.Token); } catch (OperationCanceledException) { return; }
    throw new Exception("Cancellation not propagated");
});
AsyncTest("nonplayer phase never sends", async () =>
{
    using var client = new CoachClient(new FakeHandler((_, _) => throw new Exception("Unexpected HTTP call")));
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state with { CanAdvise = false }), "phase");
});

Test("Self recipient equivalent normalized only with verified targeting semantics", () =>
{
    var defend = Card("card-1") with { TargetType = "Self", RequiresTargetSelection = false, LegalTargetsNow = [] };
    var sample = state with { Hand = Pile(defend) };
    var json = Reply(sample); First(json)["target_id"] = "me";
    var advice = AdviceContract.Parse(json.ToJsonString(), sample);
    Check(advice.Steps[0].TargetId == null);
    First(json)["target_id"] = "enemy-1";
    Reject(() => AdviceContract.Parse(json.ToJsonString(), sample), "illegal_first_action");
    var unknown = sample with { Hand = Pile(defend with { RequiresTargetSelection = null }) };
    json = Reply(unknown); First(json)["target_id"] = "me";
    Reject(() => AdviceContract.Parse(json.ToJsonString(), unknown), "illegal_first_action");
});
Test("star sentinel becomes zero spend and X cost remains explicit", () =>
{
    Check(CardInfo.NormalizeStarCost(-1) == 0 && CardInfo.NormalizeStarCost(3) == 3);
    var card = Card("star-card") with { StarCost = CardInfo.NormalizeStarCost(-1), StarCostX = true };
    using var json = JsonDocument.Parse(Wire.Serialize(card));
    Check(json.RootElement.GetProperty("star_cost").GetInt32() == 0 && json.RootElement.GetProperty("star_cost_x").GetBoolean());
});
AsyncTest("invalid first action keeps exact frozen input original output and reason", async () =>
{
    var json = Reply(state); First(json)["target_id"] = null;
    var content = json.ToJsonString(); var trace = new CallDiagnostics();
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Envelope(content)) })));
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "dummy-key", state, diagnostics: trace), "illegal_first_action");
    Check(trace.AssistantContent == content && trace.ResponseBody == Envelope(content));
    Check(trace.SnapshotId == state.Fingerprint() && trace.RequestBody!.Contains(state.Fingerprint()));
    Check(trace.Outcome == "illegal_first_action" && trace.ErrorDetail!.Contains("card-1") && trace.ErrorDetail.Contains("target=null"));
    Check(trace.FinishReason == "stop" && trace.ProviderRequestId == "fake-request");
});
Test("diagnostics redact configured key including escaped echoes without corrupting JSON", () =>
{
    const string key = "secret-quote-\"-slash-\\-中文";
    var trace = new CallDiagnostics { AssistantContent = key, ResponseBody = Envelope(key), RequestBody = "safe" };
    var text = trace.RedactedJson(key);
    using var doc = JsonDocument.Parse(text);
    Check(doc.RootElement.GetProperty("assistant_content").GetString() == "[REDACTED]");
    Check(!doc.RootElement.GetProperty("response_body").GetString()!.Contains("secret-quote"));
});
AsyncTest("truncated response keeps partial content and HTTP auth never captures echo", async () =>
{
    var trace = new CallDiagnostics();
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Envelope("partial", "length")) })));
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state, diagnostics: trace), "truncated");
    Check(trace.AssistantContent == "partial" && trace.FinishReason == "length" && trace.Outcome == "truncated");
    using var auth = new CoachClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("Authorization: secret-echo") })));
    trace = new();
    await RejectAsync(() => auth.AnalyzeAsync(Settings(), "secret-echo", state, diagnostics: trace), "authentication");
    Check(trace.ResponseBody == null && trace.HttpStatus == 401 && !trace.RedactedJson("secret-echo").Contains("secret-echo"));
});
Test("diagnostic retention keeps twenty owned records and leaves unrelated files", () =>
{
    var directory = Path.GetFullPath(Path.Combine("work", "diagnostics-test-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(directory);
    var unrelated = Path.Combine(directory, "call-user.json"); File.WriteAllText(unrelated, "user");
    try
    {
        var store = new DiagnosticStore(directory);
        for (int i = 0; i < 22; i++) { var trace = new CallDiagnostics(); store.Save(trace, trace.RedactedJson("")); }
        Check(Directory.GetFiles(directory).Length == 21 && File.ReadAllText(unrelated) == "user");
    }
    finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
});

Test("old combat setting loads without losing connection settings", () =>
{
    var directory = Path.GetFullPath(Path.Combine("work", "migration-test-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "config.json");
    File.WriteAllText(path, """{"base_url":"https://example.org/v1","model":"test-model","guidance_scope":"combat","reveal_draw_order":false,"timeout_seconds":90}""");
    try
    {
        var loaded = new SettingsStore(directory).Load();
        loaded.Settings.Validate();
        Check(loaded.Settings.Model == "test-model" && loaded.Settings.BaseUrl == "https://example.org/v1");
        Check(!loaded.Settings.RevealDrawOrder && loaded.Settings.TimeoutSeconds == 90);
        Check(!Wire.Serialize(loaded.Settings).Contains("guidance_scope"));
    }
    finally { File.Delete(path); Directory.Delete(directory); }
});
Test("unused future output cannot enter current turn display", () =>
{
    var json = Reply(state);
    json["future_turns"] = new JsonArray(new JsonObject { ["plan"] = "unused-future-plan" });
    json["horizon_note"] = "unused-horizon";
    json["guidance_scope"] = "combat";
    var advice = AdviceContract.Parse(json.ToJsonString(), state);
    Check(advice.Steps.Count == 2 && !AdviceFormatter.Format(advice, state).Contains("unused"));
    Check(!Wire.Serialize(advice).Contains("future_turns"));
});
AsyncTest("current turn HTTP input preserves powers relic descriptions amounts and usage", async () =>
{
    var sample = state with { Player = state.Player with
    {
        Powers = [new("POWER.TEST", "test-mod", "测试状态", "回合结束触发效果。", 3)],
        Relics = [new("RELIC.TEST", "test-mod", "测试遗物", "失去生命时触发效果。", 2,
            new Dictionary<string, decimal> { ["Amount"] = 2 }, true, 1)]
    }};
    using var client = new CoachClient(new FakeHandler(async (req, _) =>
    {
        using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
        using var context = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        using var prompt = JsonDocument.Parse(body.RootElement.GetProperty("messages")[2].GetProperty("content").GetString()!);
        var root = prompt.RootElement;
        Check(!root.TryGetProperty("max_rounds", out var unusedRounds) && !root.TryGetProperty("guidance_scope", out var unusedScope));
        var player = root.GetProperty("snapshot").GetProperty("player");
        var power = player.GetProperty("powers")[0];
        var relic = context.RootElement.GetProperty("context").GetProperty("player").GetProperty("relics")[0];
        Check(!player.TryGetProperty("relics", out var duplicateRelics));
        Check(power.GetProperty("amount").GetDecimal() == 3 && power.GetProperty("description").GetString() == "回合结束触发效果。");
        Check(relic.GetProperty("description").GetString() == "失去生命时触发效果。" && relic.GetProperty("used_up").GetBoolean());
        Check(relic.GetProperty("variables").GetProperty("Amount").GetInt32() == 2);
        return new(HttpStatusCode.OK) { Content = new StringContent(Envelope(Reply(sample).ToJsonString())) };
    }));
    var trace = new CallDiagnostics();
    var result = await client.AnalyzeAsync(Settings(), "", sample, diagnostics: trace);
    Check(result.Advice.Steps.Count == 2 && trace.GuidanceScope == "current_turn");
});

string Chunk(string? content, string? finish = null, int index = 0) => Wire.Serialize(new
{
    id = "stream-request", model = "stream-model",
    choices = new[] { new { index, delta = new { content }, finish_reason = finish } }
});
string Event(string json, string ending = "\n") => "data: " + json + ending + ending;
HttpResponseMessage StreamResponse(Stream stream) => new(HttpStatusCode.OK)
{
    Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } }
};
CoachClient StreamClient(string text, int chunkSize = 7) => new(new FakeHandler((_, _) =>
    Task.FromResult(StreamResponse(new ScriptedStream([Encoding.UTF8.GetBytes(text)], chunkSize: chunkSize)))));
AsyncTest("SSE displays content before completion and requests streaming once", async () =>
{
    var reply = Reply(state).ToJsonString(); var middle = reply.Length / 2;
    var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = Event(Chunk(reply[..middle]));
    var rest = Event(Chunk(reply[middle..])) + Event(Chunk(null, "stop")) + Event("[DONE]");
    int calls = 0;
    using var client = new CoachClient(new FakeHandler(async (req, _) =>
    {
        calls++;
        using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("stream").GetBoolean());
        Check(req.Headers.Accept.Any(v => v.MediaType == "text/event-stream"));
        return StreamResponse(new ScriptedStream([Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(rest)],
            (part, token) => part == 1 ? release.Task.WaitAsync(token) : Task.CompletedTask));
    }));
    var trace = new CallDiagnostics(); string? preview = null;
    var pending = client.AnalyzeAsync(Settings(), "", state, diagnostics: trace,
        onContent: text => { preview = text; received.TrySetResult(); });
    try
    {
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!pending.IsCompleted && preview == reply[..middle]);
    }
    finally { release.TrySetResult(); }
    var result = await pending;
    Check(calls == 1 && result.Advice.Steps.Count == 2 && preview == reply);
    Check(trace.ResponseFormat == "sse" && trace.StreamCompleted && trace.ResponseBody == first + rest);
    Check(trace.AssistantContent == reply && result.Model == "stream-model" && result.RequestId == "stream-request");
});
AsyncTest("SSE handles Chinese byte splits BOM comments multiline events CR LF and usage", async () =>
{
    var reply = Wire.Serialize(Reply(state));
    var chunk = Chunk(reply).Replace(",\"choices\"", ",\ndata: \"choices\"", StringComparison.Ordinal);
    var usage = """{"choices":[],"usage":{"total_tokens":87}}""";
    var wire = "\uFEFF: heartbeat\r\n\r\nid: ignored\r\ndata: " + chunk.Replace("\n", "\r\n") +
        "\r\n\r\n" + Event(Chunk(null, "stop"), "\r") + Event(usage) + Event("[DONE]");
    using var client = StreamClient(wire, 1);
    var trace = new CallDiagnostics();
    var result = await client.AnalyzeAsync(Settings(), "", state, diagnostics: trace);
    Check(trace.AssistantContent == reply && result.Usage!.Contains("87") && result.Advice.Summary.Contains("集中"));
});
AsyncTest("SSE clean framed stop without DONE is accepted but unfinished responses are not", async () =>
{
    var reply = Reply(state).ToJsonString();
    using var clean = StreamClient(Event(Chunk(reply)) + Event(Chunk(null, "stop")));
    Check((await clean.AnalyzeAsync(Settings(), "", state)).Advice.Steps.Count == 2);
    foreach (var tail in new[] { "", Event("[DONE]"), "data: " + Chunk(null, "stop"), Event(Chunk(null, "stop")) + "data: {" })
    {
        using var client = StreamClient(Event(Chunk(reply)) + tail);
        var trace = new CallDiagnostics();
        await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state, diagnostics: trace), "stream_incomplete");
        Check(!trace.StreamCompleted && trace.AssistantContent == reply && trace.ResponseBody!.Contains("data:"));
    }
});
AsyncTest("SSE length malformed event provider error and unsupported finish stay distinct", async () =>
{
    foreach (var (tail, category) in new[]
    {
        (Event(Chunk(null, "length")), "truncated"), (Event("{"), "provider_json"),
        (Event("{\"error\":{\"message\":\"remote secret\"}}"), "provider_error"),
        ("event: error\ndata: failure\n\n", "provider_error"),
        (Event(Chunk(null, "tool_calls")), "finish_reason"),
        (Event("{\"choices\":[{\"index\":\"bad\",\"delta\":{}}]}"), "provider_schema")
    })
    {
        using var client = StreamClient(Event(Chunk("partial")) + tail);
        var trace = new CallDiagnostics();
        await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state, diagnostics: trace), category);
        Check(trace.AssistantContent == "partial" && trace.Outcome == category && !trace.StreamCompleted);
    }
});
AsyncTest("SSE cannot merge other choices or append content after finish", async () =>
{
    var reply = Reply(state).ToJsonString();
    using var client = StreamClient(Event(Chunk("ignore", index: 1)) + Event(Chunk(reply)) + Event(Chunk(null, "stop")) + Event("[DONE]"));
    var trace = new CallDiagnostics();
    await client.AnalyzeAsync(Settings(), "", state, diagnostics: trace);
    Check(trace.AssistantContent == reply);
    using var invalid = StreamClient(Event(Chunk(reply, "stop")) + Event(Chunk("more")) + Event("[DONE]"));
    await RejectAsync(() => invalid.AnalyzeAsync(Settings(), "", state), "provider_schema");
});
AsyncTest("SSE transport failure retains partial raw events and assistant content", async () =>
{
    var first = Event(Chunk("partial"));
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(StreamResponse(
        new ScriptedStream([Encoding.UTF8.GetBytes(first)], (part, _) => part == 1 ? Task.FromException(new IOException("disconnect")) : Task.CompletedTask)))));
    var trace = new CallDiagnostics();
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state, diagnostics: trace), "transport");
    Check(trace.AssistantContent == "partial" && trace.ResponseBody == first && !trace.StreamCompleted);
});
AsyncTest("SSE cancellation during body read retains partial content", async () =>
{
    using var cancel = new CancellationTokenSource();
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(StreamResponse(
        new ScriptedStream([Encoding.UTF8.GetBytes(Event(Chunk("partial")))],
            (part, token) => part == 1 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask)))));
    var trace = new CallDiagnostics();
    try { await client.AnalyzeAsync(Settings(), "", state, cancel.Token, trace, _ => cancel.Cancel()); }
    catch (OperationCanceledException)
    {
        Check(trace.Outcome == "cancelled" && trace.AssistantContent == "partial" && trace.ResponseBody != null); return;
    }
    throw new Exception("Expected cancellation");
});
AsyncTest("SSE overall timeout also applies after response headers", async () =>
{
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(StreamResponse(
        new ScriptedStream([Encoding.UTF8.GetBytes(Event(Chunk("partial")))],
            (part, token) => part == 1 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask)))));
    var trace = new CallDiagnostics();
    await RejectAsync(() => client.AnalyzeAsync(Settings() with { TimeoutSeconds = 10 }, "", state, diagnostics: trace), "timeout");
    Check(trace.AssistantContent == "partial" && trace.Outcome == "timeout" && !trace.StreamCompleted);
});
AsyncTest("SSE response limit applies without content length or any newline", async () =>
{
    using var client = StreamClient("data: " + new string('x', 2 * 1024 * 1024), 8192);
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state), "response_size");
});
AsyncTest("SSE completed content still goes through snapshot and action validation", async () =>
{
    var json = Reply(state); First(json)["target_id"] = null;
    using var client = StreamClient(Event(Chunk(json.ToJsonString())) + Event(Chunk(null, "stop")) + Event("[DONE]"));
    var trace = new CallDiagnostics();
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state, diagnostics: trace), "illegal_first_action");
    Check(trace.StreamCompleted && trace.Outcome == "illegal_first_action" && trace.AssistantContent == json.ToJsonString());
});
Test("stream preview and diagnostics redact secrets split across provider chunks", () =>
{
    var key = "test-secret-key";
    Check(!SecretRedactor.Redact("reply test-sec", key, true).Contains("test-sec"));
    Check(SecretRedactor.Redact("reply " + key + " done", key, true) == "reply [REDACTED] done");
    var trace = new CallDiagnostics { ResponseFormat = "sse", AssistantContent = "reply " + key + " done",
        ResponseBody = Event(Chunk("reply test-")) + Event(Chunk("secret-key done")) };
    using var doc = JsonDocument.Parse(trace.RedactedJson(key));
    Check(doc.RootElement.GetProperty("response_body").GetString()!.StartsWith("[OMITTED:"));
    Check(doc.RootElement.GetProperty("assistant_content").GetString() == "reply [REDACTED] done");
});
AsyncTest("invalid stream UTF-8 is classified and captured", async () =>
{
    using var client = new CoachClient(new FakeHandler((_, _) => Task.FromResult(StreamResponse(
        new ScriptedStream([new byte[] { 0xff, 0xff, 0x0a }])))));
    var trace = new CallDiagnostics();
    await RejectAsync(() => client.AnalyzeAsync(Settings(), "", state, diagnostics: trace), "provider_encoding");
    Check(trace.ResponseBody != null && trace.Outcome == "provider_encoding" && !trace.StreamCompleted);
});

AsyncTest("different combat states reuse byte-identical system prefix and distinct user snapshots", async () =>
{
    var messages = new List<JsonElement>();
    var changed = state with { Round = 2, Player = state.Player with { Energy = 1 } };
    int call = 0;
    using var client = new CoachClient(new FakeHandler(async (req, token) =>
    {
        using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(token));
        messages.Add(body.RootElement.GetProperty("messages").Clone());
        return new(HttpStatusCode.OK) { Content = new StringContent(Envelope(Reply(call++ == 0 ? state : changed).ToJsonString())) };
    }));
    var first = new CallDiagnostics(); var second = new CallDiagnostics();
    await client.AnalyzeAsync(Settings(), "", state, diagnostics: first);
    await client.AnalyzeAsync(Settings(), "", changed, diagnostics: second);
    Check(messages[0][0].GetProperty("role").GetString() == "system" && messages[0][1].GetProperty("role").GetString() == "user");
    var system = messages[0][0].GetProperty("content").GetString()!;
    Check(system == messages[1][0].GetProperty("content").GetString());
    Check(messages[0].GetArrayLength() == 3);
    Check(messages[0][1].GetProperty("content").GetString() == messages[1][1].GetProperty("content").GetString());
    Check(messages[0][2].GetProperty("content").GetString() != messages[1][2].GetProperty("content").GetString());
    Check(!system.Contains(state.Fingerprint()) && !system.Contains(changed.Fingerprint()));
    Check(first.SystemPromptHash == second.SystemPromptHash && first.SystemPromptHash == Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(system))));
});
AsyncTest("stream usage option is optional and reported cached tokens survive diagnostics", async () =>
{
    foreach (bool enabled in new[] { true, false })
    {
        using var client = new CoachClient(new FakeHandler(async (req, token) =>
        {
            using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(token));
            Check(body.RootElement.TryGetProperty("stream_options", out var options) == enabled);
            if (enabled) Check(options.GetProperty("include_usage").GetBoolean());
            var usage = """{"choices":[],"usage":{"prompt_tokens":4096,"completion_tokens":500,"prompt_tokens_details":{"cached_tokens":2048}}}""";
            var wire = Event(Chunk(Reply(state).ToJsonString(), "stop")) + Event(usage) + Event("[DONE]");
            return StreamResponse(new ScriptedStream([Encoding.UTF8.GetBytes(wire)]));
        }));
        var trace = new CallDiagnostics();
        await client.AnalyzeAsync(Settings() with { IncludeStreamUsage = enabled }, "", state, diagnostics: trace);
        Check(trace.TokenUsage.InputTokens == 4096 && trace.TokenUsage.CachedInputTokens == 2048 && trace.TokenUsage.OutputTokens == 500);
        using var diagnostic = JsonDocument.Parse(trace.RedactedJson(""));
        Check(diagnostic.RootElement.GetProperty("token_usage").GetProperty("cached_input_tokens").GetInt64() == 2048);
    }
});
Test("cache telemetry distinguishes missing zero invalid and nonzero usage", () =>
{
    Check(TokenUsage.Read(null).CachedInputTokens == null);
    Check(TokenUsage.Read("{}").Display().Contains("未返回"));
    Check(TokenUsage.Read("""{"prompt_tokens_details":{"cached_tokens":0}}""").CachedInputTokens == 0);
    Check(TokenUsage.Read("""{"prompt_tokens_details":{"cached_tokens":512}}""").CachedInputTokens == 512);
    foreach (var json in new[] { "null", "[]", "bad-json", "{\"prompt_tokens_details\":{\"cached_tokens\":-1}}", "{\"prompt_tokens_details\":{\"cached_tokens\":\"0\"}}" })
        Check(TokenUsage.Read(json).CachedInputTokens == null);
});

Test("split prompt reconstructs the complete current snapshot without duplicate relics", () =>
{
    var relic = new EffectInfo("RELIC.TEST", "test-mod", "测试遗物", "含引号\"、换行\n与反斜杠\\的描述", 2,
        new Dictionary<string, decimal> { ["counter"] = 3 }, false, 2);
    var original = state with { Player = state.Player with { Relics = [relic, relic] } };
    var context = JsonNode.Parse(PromptBuilder.ContextPrompt(original))!;
    var prompt = JsonNode.Parse(PromptBuilder.UserPrompt(original))!;
    var snapshot = prompt["snapshot"]!.DeepClone();
    Check(snapshot["player"]!["relics"] == null);
    foreach (var item in context["context"]!["player"]!.AsObject())
        snapshot["player"]![item.Key] = item.Value!.DeepClone();
    Check(JsonNode.DeepEquals(snapshot, JsonSerializer.SerializeToNode(original, Wire.Json)));
    Check(prompt["snapshot_id"]!.GetValue<string>() == original.Fingerprint());
    Check(PromptBuilder.InputPreview(original).Contains("测试遗物"));
});
Test("relic context refreshes on every observed change and never carries old combat data", () =>
{
    var relic = new EffectInfo("RELIC.TEST", "test-mod", "测试遗物", "当前效果", 2,
        new Dictionary<string, decimal> { ["counter"] = 3 }, false, 1);
    var sample = state with { Player = state.Player with { Relics = [relic] } };
    var prefix = PromptBuilder.ContextPrompt(sample);
    Check(prefix == PromptBuilder.ContextPrompt(sample with { CombatId = "new-combat", Round = 4, ObservationRevision = 9 }));
    foreach (var changed in new[] { relic with { UsedUp = true }, relic with { Amount = 4 },
        relic with { StackCount = 2 }, relic with { Description = "变化后的效果" },
        relic with { Variables = new Dictionary<string, decimal> { ["counter"] = 4 } } })
    {
        var current = sample with { Player = sample.Player with { Relics = [changed] } };
        Check(PromptBuilder.ContextPrompt(current) != prefix);
        Check(JsonNode.Parse(PromptBuilder.ContextPrompt(current))!["context"]!["player"]!["relics"]![0]!["description"]!.GetValue<string>() == changed.Description);
    }
    Check(PromptBuilder.ContextPrompt(state) != prefix); // Removing the relic restores the empty current set.
});
AsyncTest("HTTP content has one JSON object encoding and readable diagnostics preserve text and redaction", async () =>
{
    var key = "test-secret-key";
    var description = "描述含\"引号\"、路径 C:\\cards\\test 和换行\n下一行 " + key;
    var sample = state with { Player = state.Player with { Relics = [new("R", "mod", "遗物", description, 1)] } };
    using var client = new CoachClient(new FakeHandler(async (req, _) =>
    {
        using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
        var messages = body.RootElement.GetProperty("messages");
        using var context = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Check(context.RootElement.ValueKind == JsonValueKind.Object); // Not a quoted JSON string.
        Check(context.RootElement.GetProperty("context").GetProperty("player").GetProperty("relics")[0].GetProperty("description").GetString() == description);
        return new(HttpStatusCode.OK) { Content = new StringContent(Envelope(Reply(sample).ToJsonString())) };
    }));
    var trace = new CallDiagnostics();
    await client.AnalyzeAsync(Settings(), key, sample, diagnostics: trace);
    var diagnostic = trace.RedactedJson(key);
    var display = DiagnosticDisplay.Format(diagnostic);
    using var saved = JsonDocument.Parse(diagnostic);
    using var request = JsonDocument.Parse(saved.RootElement.GetProperty("request_body").GetString()!);
    foreach (var message in request.RootElement.GetProperty("messages").EnumerateArray())
        Check(display.Contains(message.GetProperty("content").GetString()!));
    Check(display.Contains(trace.AssistantContent!) && !display.Contains(key));
    Check(display.Contains("[REDACTED]") && display.Contains("[system]") && display.Contains("[user]"));
    Check(trace.RequestBody!.Contains(key)); // Display redaction does not alter the frozen wire evidence.
    Check(DiagnosticDisplay.Format(new CallDiagnostics().RedactedJson("")).Contains("尚未生成请求"));
});

Test("local no-damage stopping requires known rewards and opt-in continuation wins", () =>
{
    var safe = new LocalCandidate([], 50, 0, 0, 100, 80, true, false, true, StartingHp: 50);
    Check(LocalSearchPolicy.CanStop(safe, false));
    Check(!LocalSearchPolicy.CanStop(safe, true));
    Check(!LocalSearchPolicy.CanStop(safe with { RewardCoverageKnown = false }, false));
    Check(LocalSearchPolicy.CanStop(safe with { HpLost = 3 }, false), "Recovered damage still meets the net no-loss objective");
    Check(!LocalSearchPolicy.CanStop(safe with { Hp = 49 }, false));
    Check(!LocalSearchPolicy.CanStop(safe with { Actions = [new(-1, "potion", null, "", "", "", PotionSlot: 0)] }, false));
    Check(!LocalSearchPolicy.CanStop(safe with { Dead = true }, false));
    Check(!LocalSearchPolicy.CanStop(safe with { Won = false }, false));
});
Test("local ranking prioritizes combat victory over healthy unfinished horizons", () =>
{
    var prior = new LocalCandidate([], 50, 0, 0, 100, 80, true, false, false);
    Check(LocalSearchPolicy.Better(prior, prior with { Dead = true, EnemyHp = 0 }));
    Check(!LocalSearchPolicy.Better(prior with { Hp = 49, EnemyHp = 0 }, prior));
    Check(LocalSearchPolicy.Better(prior with { MaxHp = 81 }, prior));
    Check(LocalSearchPolicy.Better(prior with { Gold = 110 }, prior));
    Check(LocalSearchPolicy.Better(prior with { Hp = 5 }, prior with { Won = false, Hp = 80 }));
    Check(!LocalSearchPolicy.Better(prior with { Won = false, Hp = 80 }, prior));
});
Test("local protocol preserves instance position and pre-state identity", () =>
{
    var action = new LocalAction(3, "CARD.STRIKE", 42, "Strike", "Slime", "native-before");
    var candidate = new LocalCandidate([action], 49, 1, 12, 99, 80, false, false, false);
    var result = new LocalSearchResult("job", "snapshot", "partial", "limited", 7, 1, 120, candidate);
    var copy = JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(result))!;
    Check(copy.Best!.Actions.Single() == action && copy.Id == "job" && copy.SnapshotId == "snapshot");
    Check(LocalSearchPolicy.Format(copy).Contains("第 4 张"));
    Check(LocalSearchPolicy.Format(copy).Contains("42"));
    Check(LocalSearchPolicy.Format(copy).Contains("尚未找到获胜路线"));
});
Test("local choices preserve ordered offers and do not merge same-name options", () =>
{
    var first = new LocalCardChoice("offer-1", 0, "CARD.SAME", "Same");
    var action = new LocalAction(-1, "POTION.ATTACK_POTION", 0, "Attack potion", "self", "root", PotionSlot: 0, Choices: [first]);
    var copy = JsonSerializer.Deserialize<LocalAction>(JsonSerializer.Serialize(action))!;
    Check(copy.Choices!.Single() == first && copy.PotionSlot == 0);
    var frontier = new LocalFrontier(8);
    frontier.Add([action], 0);
    frontier.Add([copy], 0);
    frontier.Add([action with { Choices = [first with { Index = 1 }] }], 0);
    frontier.Add([action with { Choices = [first with { OfferHash = "offer-2" }] }], 0);
    frontier.Add([action with { Choices = [new("offer-1", -1, "", "skip")] }], 0);
    Check(frontier.Count == 4 && frontier.Duplicates == 1);
    Check(LocalSearchPolicy.Describe(action).Contains("选择第 1 张"));
});
Test("local search explores choice siblings independently before closing the parent action", () =>
{
    var tree = new LocalSearchTree(1729);
    var potion = new LocalAction(-1, "POTION", 0, "Potion", "Self", "root", PotionSlot: 0);
    var options = Enumerable.Range(0, 3).Select(i => new LocalAction(i, "choice:SAME", null, "Same", "", "offer")).ToArray();
    var seen = new HashSet<int>();
    for (int i = 0; i < 3; i++)
    {
        Check(!tree.Exhausted);
        var trial = tree.Begin();
        tree.Select(trial, [potion]);
        var choice = tree.Select(trial, options);
        Check(seen.Add(choice.HandIndex));
        tree.Complete(trial, new([], 80, 0, 10, 0, 80, false, false, false), 20, closeExactPrefix: true);
    }
    Check(tree.Exhausted && tree.CompletedTrials == 3);
});
Test("combat plans preserve end turn boundaries and never invent a final end turn", () =>
{
    var end = new LocalAction(-1, "", null, "", "", "h1", Round: 3, EndTurn: true);
    var card = new LocalAction(0, "STRIKE", 5, "Strike", "Enemy", "h2", Round: 4);
    var best = new LocalCandidate([end, card], 45, 5, 0, 100, 80, true, false, false, Rounds: 2);
    var result = new LocalSearchResult("j", "s", "done", "ok", 1, 0, 20, best, Victories: 1, Workers: 4);
    var copy = JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(result))!;
    Check(copy.Best!.Actions.SequenceEqual(best.Actions));
    var text = LocalSearchPolicy.Format(copy);
    Check(text.Contains("第 3 回合") && text.Contains("第 4 回合") && text.Contains("已找到获胜路线"));
    Check(!text.Contains("3. 结束回合") && text.Contains("4 路并发"));
});
Test("frontier deduplicates exact prefixes without merging distinct card instances or rounds", () =>
{
    var frontier = new LocalFrontier(3);
    var a = new LocalAction(0, "SAME", 1, "Same", "Enemy", "hash", Round: 1);
    frontier.Add([a], 1); frontier.Add([a], 2);
    frontier.Add([a with { HandIndex = 1 }], 3);
    frontier.Add([a with { Round = 2 }], 4);
    Check(frontier.Count == 3 && frontier.Duplicates == 1);
    frontier.MarkVisited([a]);
    Check(frontier.Count == 2);
    frontier.Add([a], 100); Check(frontier.Count == 2 && frontier.Duplicates == 2);
    Check(frontier.TryTake(out var next) && next[0].Round == 2);
    frontier.Add([a with { BeforeHash = "other" }], 5);
    frontier.Add([a with { TargetId = 2 }], 6);
    frontier.Add([a with { TargetId = 3 }], 7);
    Check(frontier.Count == 3 && frontier.BudgetPruned == 1);
});
Test("automatic local concurrency reserves CPU and memory and manual selection is bounded", () =>
{
    const ulong gib = 1024UL * 1024 * 1024;
    Check(LocalSearchPolicy.WorkerCount(16, 9 * gib, 0) == 4);
    Check(LocalSearchPolicy.WorkerCount(32, 32 * gib, 0) == 8);
    Check(LocalSearchPolicy.WorkerCount(2, gib, 0) == 1);
    Check(LocalSearchPolicy.WorkerCount(16, 9 * gib, 8) == 8);
    Check(LocalSearchPolicy.WorkerCount(16, 9 * gib, 100) == 16);
});
Test("local concurrency persists without AI configuration and preserves existing settings and key", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "spire-local-settings-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new SpireAiCoach.Mod.SettingsStore(directory);
        store.SaveLocalWorkers(6);
        Check(store.Load().Settings.LocalWorkers == 6 && store.Load().Settings.Model == "");
        store.Save(Settings() with { RememberKey = true }, "synthetic-test-secret");
        var key = File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi"));
        var before = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "config.json")))!;
        store.SaveLocalWorkers(8);
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "config.json")))!;
        before["local_workers"] = 8;
        Check(JsonNode.DeepEquals(before, after));
        Check(key.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi"))));
        Check(store.Load().Key == "synthetic-test-secret" && store.Load().Settings.LocalWorkers == 8);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});

Test("potion actions preserve slots targets and action kind without aliasing card prefixes", () =>
{
    var potion = new LocalAction(-1, "POTION.FIRE_POTION", 7, "火焰药水", "敌人", "before", Round: 2, PotionSlot: 2);
    var copy = JsonSerializer.Deserialize<LocalAction>(JsonSerializer.Serialize(potion))!;
    Check(copy == potion);
    Check(LocalSearchPolicy.Describe(copy).Contains("药水槽 3") && !LocalSearchPolicy.Describe(copy).Contains("手牌"));
    Check(LocalFrontier.Key([potion]) != LocalFrontier.Key([potion with { PotionSlot = 1 }]));
    Check(LocalFrontier.Key([potion]) != LocalFrontier.Key([potion with { PotionSlot = null }]));
    var won = new LocalCandidate([], 60, 0, 0, 100, 80, true, false, false);
    Check(LocalSearchPolicy.Better(won, won with { Actions = [potion] }));
    Check(LocalSearchPolicy.Better(won with { Hp = 70, Actions = [potion] }, won));
    var result = new LocalSearchResult("id", "snap", "done", "", 1, 0, 1000, won);
    Check(LocalSearchPolicy.Format(result).Contains("未纳入主动使用药水"));
    Check(LocalSearchPolicy.Format(result with { IncludePotions = true }).Contains("已纳入主动使用药水"));
});
Test("local potion preference persists independently of AI connection and encrypted secret", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "spire-potion-settings-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new SpireAiCoach.Mod.SettingsStore(directory);
        store.SaveLocalOptions(4, true);
        Check(store.Load().Settings.LocalIncludePotions);
        store.SaveLocalWorkers(2); Check(store.Load().Settings.LocalIncludePotions);
        store.Save(Settings() with { RememberKey = true, LocalIncludePotions = true }, "synthetic-secret");
        var key = File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi"));
        store.SaveLocalOptions(4, false);
        Check(!store.Load().Settings.LocalIncludePotions && store.Load().Settings.Model == Settings().Model);
        Check(key.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "api-key.dpapi"))));
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Test("progress isolates job snapshot worker and monotonic ordering", () =>
{
    var book = new LocalProgressBook("job", "snapshot");
    var p = new LocalProgress("job", "snapshot", 0, 4, 3, 2, 1, 32, 1, 2000, 60, "试走", null, []);
    Check(book.Accept(p));
    Check(!book.Accept(p with { Id = "old-job", Sequence = 9 }));
    Check(!book.Accept(p with { SnapshotId = "old-snapshot", Sequence = 9 }));
    Check(!book.Accept(p with { Sequence = 2 }));
    Check(!book.Accept(p with { Worker = 4 }));
    Check(book.Accept(p with { Worker = 1, Sequence = 1 }));
    Check(book.Latest.Count == 2 && book.Latest[0].Sequence == 3);
    Check(LocalProgressBook.BudgetUsed(p with { ElapsedMs = 90000 }) == 100);
    Check(LocalProgressBook.BudgetUsed(p with { ElapsedMs = -1 }) == 0);
});
Test("progress reports measured damage block energy powers and hand changes", () =>
{
    var before = new LocalSimState(1, 80, 80, 0, 3, "覆甲 2", ["打击"], 2,
        [new(4, "敌人", 40, 40, 0, "", "攻击")]);
    var after = before with { Block = 12, Energy = 2, Hp = 77, Potions = 1, Hand = [],
        Enemies = [new(4, "敌人", 20, 40, 0, "易伤 2", "攻击")] };
    var change = LocalProgressBook.Changes(before, after);
    Check(change.Contains("80→77") && change.Contains("40→20") && change.Contains("格挡 0→12") && change.Contains("药水 2→1"));
    Check(change.Contains("易伤 2") && change.Contains("手牌"));
    Check(LocalProgressBook.StateText(after).Contains("生命 77/80"));
});
AsyncTest("frequent local telemetry replacement stays readable during concurrent reads on Windows", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "spire-wire-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "progress.json");
    try
    {
        LocalWire.Write(path, new[] { 0, 0 });
        var writer = Task.Run(() => { for (int i = 1; i < 500; i++) LocalWire.Write(path, new[] { i, i }); });
        int reads = 0;
        while (!writer.IsCompleted || reads < 100)
        {
            var pair = LocalWire.Read<int[]>(path);
            Check(pair.Length == 2 && pair[0] == pair[1]); reads++;
            await Task.Yield();
        }
        await writer;
    }
    finally { Directory.Delete(directory, true); }
});

SearchAlgorithmTests.Register(Test);
SearchWorkTests.Register(Test);
OptimizationTests.Register(Test);
TimelineTests.Register(Test);
var filter = args.Length == 2 && args[0] == "--filter" ? args[1] : null;
if (args.Length != 0 && filter == null) { Console.Error.WriteLine("Usage: [--filter substring]"); return 2; }
var selected = tests.Where(t => filter == null || t.Item1.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
if (selected.Length == 0) { Console.Error.WriteLine("No tests matched"); return 2; }
int failures = 0;
foreach (var (name, test) in selected)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{selected.Length - failures}/{selected.Length} passed");
return failures == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

sealed class ScriptedStream(IReadOnlyList<byte[]> parts, Func<int, CancellationToken, Task>? beforePart = null, int chunkSize = 7) : Stream
{
    private int _part, _offset;
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_offset == 0 && beforePart != null) await beforePart(_part, token);
        if (_part == parts.Count) return 0;
        int count = Math.Min(Math.Min(buffer.Length, chunkSize), parts[_part].Length - _offset);
        parts[_part].AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == parts[_part].Length) { _offset = 0; _part++; }
        return count;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
