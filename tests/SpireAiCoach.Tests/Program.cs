using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

var tests = new List<(string, Func<Task>)>();
void Test(string name, Action test) => tests.Add((name, () => { test(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> test) => tests.Add((name, test));
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
    var result = await client.AnalyzeAsync(Settings(), "dummy-key", state);
    Check(result.RequestId == "fake-request" && result.Advice.Steps.Count == 2 && requestBody != null);
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
        using var prompt = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var root = prompt.RootElement;
        Check(!root.TryGetProperty("max_rounds", out var unusedRounds) && !root.TryGetProperty("guidance_scope", out var unusedScope));
        var player = root.GetProperty("snapshot").GetProperty("player");
        var power = player.GetProperty("powers")[0]; var relic = player.GetProperty("relics")[0];
        Check(power.GetProperty("amount").GetDecimal() == 3 && power.GetProperty("description").GetString() == "回合结束触发效果。");
        Check(relic.GetProperty("description").GetString() == "失去生命时触发效果。" && relic.GetProperty("used_up").GetBoolean());
        Check(relic.GetProperty("variables").GetProperty("Amount").GetInt32() == 2);
        return new(HttpStatusCode.OK) { Content = new StringContent(Envelope(Reply(sample).ToJsonString())) };
    }));
    var trace = new CallDiagnostics();
    var result = await client.AnalyzeAsync(Settings(), "", sample, diagnostics: trace);
    Check(result.Advice.Steps.Count == 2 && trace.GuidanceScope == "current_turn");
});

int failures = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
