using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class NodeSandboxPreloadTests : IDisposable
{
    // Stands in for an AppContainer: lstat and stat fail with EPERM on every ancestor of the roots, realpath always does.
    private const string Harness = """
        'use strict';
        const fs = require('fs');
        const path = require('path');
        const [preloadSource, base, order] = process.argv.slice(2);

        const granted = path.join(base, 'granted');
        const outside = path.join(base, 'outside');
        const deep = path.join(base, 'deep');
        const rootFile = path.join(base, 'root.txt');
        fs.mkdirSync(granted, { recursive: true });
        fs.mkdirSync(outside, { recursive: true });
        fs.mkdirSync(path.join(deep, 'spelled'), { recursive: true });
        fs.writeFileSync(rootFile, 'x');
        fs.writeFileSync(path.join(outside, 'secret.txt'), 'x');
        fs.writeFileSync(path.join(granted, 'plain.txt'), 'x');
        fs.symlinkSync(outside, path.join(granted, 'link'), 'junction');
        fs.symlinkSync(path.join(granted, 'loop-b'), path.join(granted, 'loop-a'), 'junction');
        fs.symlinkSync(path.join(granted, 'loop-a'), path.join(granted, 'loop-b'), 'junction');
        const preload = path.join(granted, 'node-sandbox.cjs');
        fs.copyFileSync(preloadSource, preload);

        const real = { lstatSync: fs.lstatSync, statSync: fs.statSync, lstat: fs.lstat, stat: fs.stat, pLstat: fs.promises.lstat, pStat: fs.promises.stat };
        // A container denies the folder whatever its spelling, so the stub matches file ids, never strings.
        const idOf = (stat, p) => { try { const s = stat.call(fs, p, { bigint: true }); return `${s.dev}:${s.ino}`; } catch { return null; } };
        const ancestors = new Set([idOf(real.statSync, deep)]);
        for (let d = path.resolve(base); ; d = path.dirname(d)) {
          ancestors.add(idOf(real.statSync, d));
          if (path.dirname(d) === d) break;
        }
        // A share that does not exist stands in for a server whose every folder is denied.
        const offline = (p) => /pia-unc-host/i.test(String(p));
        const isAncestor = (stat, p) => {
          if (offline(p)) return true;
          const id = idOf(stat, p);
          return id !== null && ancestors.has(id);
        };
        const eperm = (syscall, p) =>
          Object.assign(new Error(`EPERM: operation not permitted, ${syscall} '${p}'`), { code: 'EPERM', syscall, path: String(p) });

        for (const name of ['lstatSync', 'statSync']) {
          fs[name] = function (p, o) { if (isAncestor(real[name], p)) throw eperm(name, p); return real[name].call(fs, p, o); };
        }
        for (const name of ['lstat', 'stat']) {
          fs[name] = function (p, o, cb) {
            if (typeof o === 'function') { cb = o; o = undefined; }
            if (isAncestor(real[name + 'Sync'], p)) return process.nextTick(cb, eperm(name, p));
            return real[name].call(fs, p, o, cb);
          };
        }
        fs.promises.lstat = async (p, o) => { if (isAncestor(real.lstatSync, p)) throw eperm('lstat', p); return real.pLstat(p, o); };
        fs.promises.stat = async (p, o) => { if (isAncestor(real.statSync, p)) throw eperm('stat', p); return real.pStat(p, o); };
        const deniedSync = (p) => { throw eperm('lstat', p); };
        const deniedCb = (p, o, cb) => process.nextTick(typeof o === 'function' ? o : cb, eperm('lstat', p));
        fs.realpathSync = function (p) { return deniedSync(p); };
        fs.realpathSync.native = function (p) { return deniedSync(p); };
        fs.realpath = function (p, o, cb) { deniedCb(p, o, cb); };
        fs.realpath.native = function (p, o, cb) { deniedCb(p, o, cb); };
        fs.promises.realpath = async (p) => deniedSync(p);

        process.env.PIA_SANDBOX_ROOTS = [
          rootFile,
          granted,
          '\\\\.\\' + path.join(deep, 'spelled').toUpperCase() + '\\',
          '\\\\?\\UNC\\pia-unc-host\\share\\users\\granted',
        ].join('|');
        require(preload);

        const code = (fn) => { try { fn(); return 'none'; } catch (e) { return e.code; } };
        const attempt = async (fn) => { try { return await fn(); } catch (e) { return e.code; } };
        const viaCb = (start) => new Promise((resolve, reject) => start((e, r) => (e ? reject(e) : resolve(r))));
        const callsOf = (start) => new Promise((resolve) => {
          let calls = 0;
          const swallow = () => {};
          process.on('uncaughtException', swallow);
          start(() => { calls++; throw new Error('the callback failed'); });
          setTimeout(() => { process.off('uncaughtException', swallow); resolve(calls); }, 200);
        });

        (async () => {
          const r = {};
          const plain = () => fs.statSync(base);
          const big = () => fs.statSync(base, { bigint: true });
          const [plainStats, bigStats] = order === 'bigint-first' ? [big(), plain()].reverse() : [plain(), big()];
          r.plainIno = typeof plainStats.ino;
          r.bigintIno = typeof bigStats.ino;
          r.ancestorIsDirectory = [
            fs.lstatSync(base).isDirectory(),
            fs.statSync(path.dirname(base)).isDirectory(),
            (await viaCb((cb) => fs.lstat(base, cb))).isDirectory(),
            (await viaCb((cb) => fs.stat(base, { bigint: true }, cb))).isDirectory(),
            (await fs.promises.stat(base)).isDirectory(),
          ];
          const viaLink = path.join(granted, 'link', 'secret.txt');
          r.expected = path.join(outside, 'secret.txt');
          r.realpaths = [
            fs.realpathSync(viaLink),
            fs.realpathSync.native(viaLink),
            await viaCb((cb) => fs.realpath(viaLink, cb)),
            await viaCb((cb) => fs.realpath.native(viaLink, {}, cb)),
            await fs.promises.realpath(viaLink),
          ];
          r.buffers = [
            fs.realpathSync(viaLink, 'buffer'),
            fs.realpathSync.native(viaLink, { encoding: 'buffer' }),
            await viaCb((cb) => fs.realpath(viaLink, 'buffer', cb)),
            await fs.promises.realpath(viaLink, { encoding: 'buffer' }),
          ].map((b) => (Buffer.isBuffer(b) ? b.toString() : null));
          r.hexMatches = fs.realpathSync(viaLink, { encoding: 'hex' }) === Buffer.from(r.expected).toString('hex');
          r.missingCode = code(() => fs.realpathSync(path.join(granted, 'missing.txt')));
          r.loopCode = code(() => fs.realpathSync(path.join(granted, 'loop-a', 'x')));
          const prefixed = '\\\\?\\';
          r.spellings = {
            prefixed: await attempt(() => fs.statSync(prefixed + base).isDirectory()),
            device: await attempt(() => fs.lstatSync('\\\\.\\' + base).isDirectory()),
            recasedWithTrailingSeparator: await attempt(async () => (await viaCb((cb) => fs.stat(base.toUpperCase() + '\\', cb))).isDirectory()),
            forwardSlashes: await attempt(async () => (await fs.promises.lstat(base.replace(/\\/g, '/') + '/')).isDirectory()),
            underARootSpelledWithAPrefix: await attempt(() => fs.statSync(deep).isDirectory()),
            uncUnderAPrefixedRoot: await attempt(() => fs.statSync('\\\\pia-unc-host\\share\\users').isDirectory()),
            uncPrefixedAndRecased: await attempt(() => fs.lstatSync('\\\\?\\UNC\\PIA-UNC-HOST\\Share\\Users\\').isDirectory()),
          };
          r.uncCodes = [
            code(() => fs.statSync('\\\\?\\UNC\\pia-unc-host\\share\\other')),
            code(() => fs.statSync('\\\\pia-unc-host\\share\\users\\granted\\')),
          ];
          r.prefixedRealpaths = [
            await attempt(() => fs.realpathSync(prefixed + viaLink)),
            await attempt(() => fs.realpathSync.native(prefixed + viaLink)),
            await attempt(() => viaCb((cb) => fs.realpath(prefixed + viaLink, cb))),
            await attempt(() => viaCb((cb) => fs.realpath.native(prefixed + viaLink, cb))),
            await attempt(() => fs.promises.realpath(prefixed + viaLink)),
          ];
          r.plainFile = path.join(granted, 'plain.txt');
          r.prefixedPlainRealpath = await attempt(() => fs.realpathSync(prefixed + r.plainFile));
          r.recasedRealpath = await attempt(() => fs.realpathSync(viaLink.toUpperCase()));
          r.recasedFolderRealpath = await attempt(() => fs.realpathSync(path.join(granted, 'LINK') + '\\'));
          r.outside = outside;
          r.realpathCallbackCalls = await callsOf((cb) => fs.realpath(viaLink, cb));
          r.statCallbackCalls = await callsOf((cb) => fs.stat(base, cb));
          process.stdout.write(JSON.stringify(r));
        })().catch((e) => {
          process.stdout.write(JSON.stringify({ error: String((e && e.stack) || e) }));
        });
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-preload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // A recursive delete throws "invalid parameter" on these junctions, so they go first.
        var granted = Path.Combine(_root, "case", "granted");
        if (Directory.Exists(granted))
        {
            foreach (var link in Directory.EnumerateDirectories(granted)
                .Where(folder => File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)).ToList())
                Directory.Delete(link);
        }

        TempPath.Remove(_root);
    }

    [Fact]
    public void Content_LoadsFromTheEmbeddedResource()
    {
        var content = NodeSandboxPreload.Content;

        Assert.StartsWith("'use strict';", content, StringComparison.Ordinal);
        Assert.EndsWith("require('module').syncBuiltinESMExports();", content.TrimEnd(), StringComparison.Ordinal);
        Assert.Same(content, NodeSandboxPreload.Content);
    }

    [Fact]
    public void Content_SplitsTheRootsOnAPipeOnly()
    {
        var content = NodeSandboxPreload.Content;

        Assert.Contains("(process.env.PIA_SANDBOX_ROOTS || '').split('|')", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".split(';')", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_NeverRunsTheRealpathCallbackInsideItsOwnTry()
    {
        Assert.DoesNotContain("try { return cb(null, realFallback(p)); }", NodeSandboxPreload.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_AnswersAncestorsWithItsOwnFolder_NeverTheFirstRoot()
    {
        var content = NodeSandboxPreload.Content;

        Assert.DoesNotContain("roots[0]", content, StringComparison.Ordinal);
        Assert.Contains("orig.statSync(__dirname", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FileName_IsTheNameNodeOptionsRequires()
    {
        Assert.Equal("node-sandbox.cjs", NodeSandboxPreload.FileName);
    }

    [Theory]
    [InlineData("plain-first")]
    [InlineData("bigint-first")]
    public async Task AnAncestor_ReadsAsAFolder_WithTheRequestedInoType_WhenTheFirstRootIsAFile(string order)
    {
        var result = await RunHarnessAsync(order);

        Assert.All(result.GetProperty("ancestorIsDirectory").EnumerateArray(), item => Assert.True(item.GetBoolean()));
        Assert.Equal("number", result.GetProperty("plainIno").GetString());
        Assert.Equal("bigint", result.GetProperty("bigintIno").GetString());
    }

    [Fact]
    public async Task Realpath_FollowsAJunctionInAGrantedRoot_ToItsTarget()
    {
        var result = await RunHarnessAsync("plain-first");

        var expected = result.GetProperty("expected").GetString();
        Assert.All(result.GetProperty("realpaths").EnumerateArray(),
            item => Assert.Equal(expected, item.GetString(), ignoreCase: true));
        Assert.Equal("ENOENT", result.GetProperty("missingCode").GetString());
        Assert.Equal("ELOOP", result.GetProperty("loopCode").GetString());
    }

    [Fact]
    public async Task Realpath_HonoursTheEncodingOption()
    {
        var result = await RunHarnessAsync("plain-first");

        var expected = result.GetProperty("expected").GetString();
        Assert.All(result.GetProperty("buffers").EnumerateArray(),
            item => Assert.Equal(expected, item.GetString(), ignoreCase: true));
        Assert.True(result.GetProperty("hexMatches").GetBoolean());
    }

    [Fact]
    public async Task AnAncestor_ReadsAsAFolder_WhateverTheSpellingOfItOrOfTheRoot()
    {
        var result = await RunHarnessAsync("plain-first");

        Assert.All(result.GetProperty("spellings").EnumerateObject(),
            spelling => Assert.True(spelling.Value.ValueKind == JsonValueKind.True, $"{spelling.Name} gave {spelling.Value}"));
        Assert.All(result.GetProperty("uncCodes").EnumerateArray(), item => Assert.Equal("EPERM", item.GetString()));
    }

    [Fact]
    public async Task Realpath_ResolvesAPrefixedOrRecasedPath_ToItsPlainSpelling()
    {
        var result = await RunHarnessAsync("plain-first");

        var expected = result.GetProperty("expected").GetString();
        Assert.All(result.GetProperty("prefixedRealpaths").EnumerateArray(),
            item => Assert.Equal(expected, item.GetString(), ignoreCase: true));
        Assert.Equal(result.GetProperty("plainFile").GetString(), result.GetProperty("prefixedPlainRealpath").GetString(), ignoreCase: true);
        Assert.Equal(expected, result.GetProperty("recasedRealpath").GetString(), ignoreCase: true);
        Assert.Equal(result.GetProperty("outside").GetString(), result.GetProperty("recasedFolderRealpath").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task AThrowingSuccessCallback_RunsExactlyOnce()
    {
        var result = await RunHarnessAsync("plain-first");

        Assert.Equal(1, result.GetProperty("realpathCallbackCalls").GetInt32());
        Assert.Equal(1, result.GetProperty("statCallbackCalls").GetInt32());
    }

    private async Task<JsonElement> RunHarnessAsync(string order)
    {
        var node = NodeOnPath();
        if (node is null)
            Assert.Skip("No node.exe on PATH.");

        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var preload = Path.Combine(_root, "preload.cjs");
        var harness = Path.Combine(_root, "harness.js");
        await File.WriteAllTextAsync(preload, NodeSandboxPreload.Content, ct);
        await File.WriteAllTextAsync(harness, Harness, ct);

        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Production loads the preload with --preserve-symlinks too; without it the loader's own realpath hits the stub.
        foreach (var argument in new[] { "--preserve-symlinks", harness, preload, Path.Combine(_root, "case"), order })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await stdout;
        var errors = await stderr;
        Assert.True(process.ExitCode == 0, $"node exited {process.ExitCode}: {errors}");
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement.Clone();
        if (result.TryGetProperty("error", out var error))
            Assert.Fail(error.GetString());
        return result;
    }

    private static string? NodeOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), "node.exe"))
            .FirstOrDefault(File.Exists);
}
