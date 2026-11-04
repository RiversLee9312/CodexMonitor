using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ToolProgramTests
{
    [TestMethod]
    [DataRow("text(await tools.exec_command({cmd:'echo hello'}));")]
    [DataRow("text((await tools.exec_command({cmd: `echo hello`})).output);")]
    [DataRow("text((await tools['exec_command']({['cmd']:'echo hello'}))['output']);")]
    [DataRow("text((await tools?.exec_command?.({cmd:'echo hello'}))?.output);")]
    [DataRow("const cmd='echo hello'; text(await tools.exec_command({cmd}));")]
    [DataRow("let args={cmd:'echo hello'}; const r=await tools.exec_command(args); text(r.output);")]
    [DataRow("const run=tools.exec_command; text(await run({cmd:'echo hello'}));")]
    [DataRow("const {exec_command: run}=tools; text(await run({cmd:'echo hello'}));")]
    [DataRow("const t=tools; const name='exec_'+'command'; text(await t[name]({cmd:'echo hello'}));")]
    [DataRow("const prefix='echo'; text(await tools.exec_command({cmd:`${prefix} hello`}));")]
    [DataRow("const base={cwd:'D:/repo'}; text(await tools.exec_command({...base,cmd:'echo hello'}));")]
    [DataRow("const [args]=[{cmd:'echo hello'}]; text(JSON.stringify(await tools.exec_command(args)));")]
    [DataRow("const {cmd: command}={cmd:'echo hello'}; text(await tools.exec_command({cmd: command}));")]
    [DataRow("await (async()=>{const r=await tools.exec_command({cmd:'echo hello'});text(r.content[0].text)})();")]
    [DataRow("async function run(args) { return await tools.exec_command(args); } text(await run({cmd:'echo hello'}));")]
    [DataRow("const run=async ({cmd})=>tools.exec_command({cmd}); text(await run({cmd:'echo hello'}));")]
    [DataRow("await tools.exec_command({cmd:'echo hello'}).then(result=>text(result.output));")]
    [DataRow("const args=JSON.parse('{\"cmd\":\"echo hello\"}'); text(await tools.exec_command(args));")]
    [DataRow("// @exec: {\"yield_time_ms\": 1000}\n/* comment */ text(await tools.exec_command({cmd:'echo hello',}));")]
    [DataRow("true && text(await tools.exec_command({cmd:'echo hello'}));")]
    [DataRow("let cmd='echo'; cmd+=' hello'; text(await tools.exec_command({cmd}));")]
    [DataRow("text((await tools.exec_command({\"cmd\":\"echo hello\"})).output ?? '');")]
    [DataRow("text((await tools.exec_command({cmd:'echo hello'})).output || '');")]
    [DataRow("let cmd='old'; {cmd='echo hello';} text(await tools.exec_command({cmd}));")]
    [DataRow("const cmd='echo hello'; {const cmd='shadow';text(cmd);} text(await tools.exec_command({cmd}));")]
    [DataRow("text(await tools.exec_command({cmd:'echo hello',yield_time_ms:+1000,max_output_tokens:-1}));")]
    public void ExpressionVariantsShareTheCommandAdapterAndKeepOriginalSource(string code)
    {
        var tool = ToolActivity.FromCall("functions.exec", code, "Hello", ToolActivityState.Completed);
        Assert.IsTrue(tool.Program?.Complete, code); Assert.HasCount(1, tool.Program!.Calls);
        Assert.AreEqual("ToolCommand", tool.CategoryKey); Assert.AreEqual("echo hello", tool.DecodedInput);
        Assert.AreEqual("echo hello", tool.Summary); Assert.AreEqual(code, tool.Input); Assert.AreEqual("Hello", tool.Output);
    }

    [TestMethod]
    [DataRow("text(await Promise.all([tools.exec_command({cmd:'first'}),tools.exec_command({cmd:'second'})]));")]
    [DataRow("const results=await Promise.allSettled([tools.exec_command({cmd:'first'}),tools.exec_command({cmd:'second'})]); for(const r of results) text(r.value);")]
    [DataRow("await Promise.all(['first','second'].map(async cmd=>text(await tools.exec_command({cmd}))));")]
    [DataRow("['first','second'].forEach(async cmd=>text(await tools.exec_command({cmd}))); ")]
    [DataRow("for(const cmd of ['first','second']) { text(await tools.exec_command({cmd})); }")]
    [DataRow("const run=async cmd=>{const r=await tools.exec_command({cmd}); text(r.output);}; await run('first'); await run('second');")]
    [DataRow("const run=tools.exec_command; const args=[{cmd:'first'}]; text(await run(...args)); text(await run({cmd:'second'}));")]
    public void BatchesKeepEveryCallInOrderWithoutExecutingCode(string code)
    {
        var program = ToolProgram.Parse(code);
        Assert.IsTrue(program.Complete, code); Assert.HasCount(2, program.Calls);
        CollectionAssert.AreEqual(new[] { "first", "second" }, program.Calls.Select(call => ToolActivity.FromCall(call.Name, call.Input).DecodedInput).ToArray());
        Assert.AreEqual("ToolCommand", ToolActivity.FromCall("exec", code).CategoryKey);
    }

    [TestMethod]
    public void AllSemanticToolsUseTheSameAdaptersAndFreeFormPatchesAreDecoded()
    {
        var sleep = ToolActivity.FromCall("exec", "text(await tools.clock__sleep({duration_ms:30000}));");
        // Flat MCP/harness names and namespace-qualified names are both retained.
        var wait = ToolActivity.FromCall("exec", "text(await tools.clock.sleep({duration_ms:30000}));");
        Assert.AreEqual("ToolWaiting", wait.CategoryKey); Assert.AreEqual(30000L, wait.WaitDurationMs);
        var patch = ToolActivity.FromCall("exec", "const patch=`*** Begin Patch\n*** Add File: New.cs\n+hello\n*** End Patch`; text(await tools.apply_patch(patch));");
        Assert.AreEqual("ToolFileChanges", patch.CategoryKey); Assert.AreEqual("New.cs", patch.Summary);
        Assert.AreEqual(1, patch.DisplayParts()[0].AddedLines);
        var mcp = ToolActivity.FromCall("exec", "const {mcp__example__search: search}=tools; text(await search({query:'WinUI'}));");
        Assert.AreEqual("ToolMcp", mcp.CategoryKey); Assert.IsFalse(mcp.DisplayInput.Contains("text("));
    }

    [TestMethod]
    public void LocalResponseAndServerDynamicCallsShareTheProgramProjectionAndDoNotCacheStaleClones()
    {
        using var local = System.Text.Json.JsonDocument.Parse("""{"type":"custom_tool_call","name":"exec","namespace":"functions","input":"const r=await tools.exec_command({cmd:'echo hello'}); text(r.output);"}""");
        using var server = System.Text.Json.JsonDocument.Parse("""{"id":"server","type":"dynamicToolCall","namespace":"functions","tool":"exec","arguments":{"code":"const r=await tools.exec_command({cmd:'echo hello'}); text(r.output);"}}""");
        var a = TranscriptItemAdapter.ParseResponse(local.RootElement, "local", default)!.Tool!;
        var b = CodexClient.ParseItem(server.RootElement).Tool!;
        Assert.AreEqual(a.DecodedInput, b.DecodedInput); Assert.AreEqual(a.CategoryKey, b.CategoryKey);
        Assert.AreEqual("echo hello", a.DecodedInput);
        Assert.AreEqual("echo changed", (b with { Input = "text(await tools.exec_command({cmd:'echo changed'}));" }).DecodedInput);
    }

    [TestMethod]
    [DataRow("text('tools.exec_command({cmd:\"fake\"})');")]
    [DataRow("// tools.exec_command({cmd:'fake'})\ntext('done');")]
    [DataRow("const unused=()=>tools.exec_command({cmd:'fake'}); text('done');")]
    [DataRow("if(false) text(await tools.exec_command({cmd:'fake'}));")]
    [DataRow("false && text(await tools.exec_command({cmd:'fake'}));")]
    [DataRow("const options={get cmd(){return tools.exec_command({cmd:'fake'})}}; text(options);")]
    [DataRow("function* unused(){yield tools.exec_command({cmd:'fake'});} unused();")]
    [DataRow("if(true==='true') text(await tools.exec_command({cmd:'fake'}));")]
    [DataRow("text(await tools.exec_command({cmd: 'unfinished' ")]
    public void StringsCommentsUnusedFunctionsAndUnreachedBranchesDoNotBecomeExecutedTools(string code)
    { Assert.IsEmpty(ToolProgram.Parse(code).Calls); }

    [TestMethod]
    [DataRow("if(runtimeFlag) text(await tools.exec_command({cmd:'maybe'}));")]
    [DataRow("const args={cmd:runtimeCommand}; text(await tools.exec_command(args));")]
    [DataRow("let cmd='old'; if(runtimeFlag) cmd='new'; text(await tools.exec_command({cmd}));")]
    [DataRow("while(true) text(await tools.exec_command({cmd:'loop'}));")]
    [DataRow("const run=()=>run(); run(); text(await tools.exec_command({cmd:'later'}));")]
    [DataRow("let cmd='old';function change(){cmd='new'};change();text(await tools.exec_command({cmd}));")]
    [DataRow("if(1==true) text(await tools.exec_command({cmd:'coercion'}));")]
    public void RuntimeDependentOrUnboundedProgramsKeepTheFallback(string code)
    { Assert.IsFalse(ToolProgram.Parse(code).Complete); }
}
