using System.Diagnostics;
using System.Text.RegularExpressions;
using ComicMaintainer.Tests.Helpers;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Runs the container entrypoint with bash, with the commands it uses to manage users, ownership and modes (and
/// gosu) replaced by stubs that record their arguments, to check what it does to the comic library. The library
/// belongs to the user: other containers share it through its group and some libraries are kept private, so the
/// entrypoint must only check that PUID can write the top folders, and change the library only when
/// FIX_LIBRARY_PERMISSIONS opts in, and then never to a fixed mode such as 755.
/// </summary>
public sealed class EntrypointLibraryPermissionsTests : IDisposable
{
    private static readonly string[] StubbedCommands =
        ["getent", "groupadd", "groupmod", "useradd", "usermod", "id", "mkdir", "chown", "chmod", "find", "realpath"];

    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "entrypoint-" + Guid.NewGuid().ToString("N"));
    private readonly string _stubs;
    private readonly string _log;
    private readonly string _library;
    private readonly string _duplicates;
    private readonly string _config;

    public EntrypointLibraryPermissionsTests()
    {
        _stubs = Directory.CreateDirectory(Path.Join(_root, "bin")).FullName;
        _log = Path.Join(_root, "calls.log");
        _library = Directory.CreateDirectory(Path.Join(_root, "watched")).FullName;
        _duplicates = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
        _config = Directory.CreateDirectory(Path.Join(_root, "config")).FullName;
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [BashFact]
    public void ByDefault_LeavesTheLibraryContentsAlone()
    {
        var run = RunEntrypoint();

        AssertSucceeded(run);
        var library = new[] { Posix(_library), Posix(_duplicates) };
        Assert.DoesNotContain(run.Calls, call => call.Command is "chown" or "chmod" or "find" && call.Arguments.Any(library.Contains));
        // Only the config directories are handed to PUID, and /app is not: the app user must not rewrite its binaries.
        Assert.Equal(
            ["/Config", Posix(_config)],
            run.Calls.Where(call => call is { Command: "chown", Arguments: ["-R", ..] }).Select(call => call.Arguments[^1]));
        Assert.DoesNotContain(run.Calls, call => call.Arguments.Contains("/app"));
        var start = run.Calls[^1];
        Assert.Equal("gosu", start.Command);
        Assert.Equal(["comicmaintainer", "dotnet", "ComicMaintainer.WebApi.dll"], start.Arguments);
    }

    [BashFact]
    public void ChecksThatTheAppUserCanWriteEachLibraryFolder()
    {
        var run = RunEntrypoint(unwritable: _duplicates);

        AssertSucceeded(run);
        var checkedFolders = run.Calls.Where(call => call is { Command: "gosu", Arguments: [_, "sh", ..] }).Select(call => call.Arguments[^1]);
        Assert.Equal([Posix(_library), Posix(_duplicates)], checkedFolders);
        Assert.Contains($"cannot write to {Posix(_duplicates)}", run.Output);
        Assert.DoesNotContain($"cannot write to {Posix(_library)}", run.Output);
        Assert.Contains("FIX_LIBRARY_PERMISSIONS=true", run.Output);
        // A folder that holds the user's files or has another owner is not taken over.
        Assert.DoesNotContain(run.Calls, call => call.Command == "chown" && call.Arguments.Contains(Posix(_duplicates)));
    }

    [BashFact]
    public void AnEmptyRootOwnedLibraryFolder_IsGivenToPuidWithoutRecursing()
    {
        var run = RunEntrypoint(unwritable: _duplicates, ("EMPTY_ROOT_OWNED_DIR", Posix(_duplicates)));

        AssertSucceeded(run);
        var claim = Assert.Single(run.Calls, call => call.Command == "chown" && call.Arguments.Contains(Posix(_duplicates)));
        Assert.Equal(["1234:5678", "--", Posix(_duplicates)], claim.Arguments);
        var check = Assert.Single(run.Calls, call => call is { Command: "find" } && call.Arguments[0] == Posix(_duplicates));
        Assert.Equal([Posix(_duplicates), "-maxdepth", "0", "-type", "d", "-uid", "0", "-empty"], check.Arguments);
        Assert.DoesNotContain("cannot write to", run.Output);
    }

    [BashFact]
    public void FixLibraryPermissions_GivesTheLibraryToPuidWithOwnerAndGroupAccessOnly()
    {
        var run = RunEntrypoint(("FIX_LIBRARY_PERMISSIONS", "true"));

        AssertSucceeded(run);
        foreach (var folder in new[] { _library, _duplicates })
        {
            var walk = Assert.Single(run.Calls, call => call is { Command: "find" } && call.Arguments[0] == Posix(folder));
            var arguments = string.Join(' ', walk.Arguments);
            // Ownership comes before the tests that exclude secret files, which only keep their mode.
            Assert.Matches(
                @"-exec chown 1234:5678 \{} \+ ! \( -type f \( -name jwt-secret\.key -o -name jwt-secret\.key\.\*\.tmp \) \) ! -path /run/secrets/jwt ! -path /run/secrets/jwt -exec chmod u\+rwX,g\+rwX \{} \+$",
                arguments);
            Assert.Contains($"-samefile {Posix(_config)} -prune -o", arguments);
            Assert.Contains("! -type l", arguments);
        }

        Assert.DoesNotContain(run.Calls, call => call.Command == "chmod" && call.Arguments.Any(argument => Regex.IsMatch(argument, "^[0-7]{3,4}$") && argument != "600"));
    }

    /// <summary>
    /// The container smoke test's layout, on a real find: a private password file that belongs to another user, the
    /// generated JWT key, and the config folder, all inside the duplicates folder. The app runs as PUID, so the file
    /// must be given to it or the app cannot read it and stops at startup.
    /// </summary>
    [BashFact]
    public void FixLibraryPermissions_OnARealWalk_GivesSecretFilesToPuidAndLeavesTheirModeAlone()
    {
        var comic = TouchFile(_library, "series", "issue.cbz");
        var passwordFile = TouchFile(_duplicates, ".admin-password");
        var jwtKey = TouchFile(_duplicates, "jwt-secret.key");
        var duplicate = TouchFile(_duplicates, "issue.cbz");
        var nestedConfig = Directory.CreateDirectory(Path.Join(_duplicates, "config")).FullName;
        var database = TouchFile(nestedConfig, "comicmaintainer.db");

        var run = RunEntrypoint(
            realFind: true,
            ("FIX_LIBRARY_PERMISSIONS", "true"),
            ("ADMIN_PASSWORD_FILE", Posix(passwordFile)),
            ("CONFIG_DIR", Posix(nestedConfig)));

        AssertSucceeded(run);
        // -exec ... + batches paths, so one walk can make several calls.
        var givenToPuid = Paths(run, "chown", "1234:5678");
        var opened = Paths(run, "chmod", "u+rwX,g+rwX");
        Assert.Contains(Posix(passwordFile), givenToPuid);
        Assert.Contains(Posix(jwtKey), givenToPuid);
        Assert.DoesNotContain(Posix(passwordFile), opened);
        Assert.DoesNotContain(Posix(jwtKey), opened);
        foreach (var path in new[] { _library, comic, _duplicates, duplicate })
        {
            Assert.Contains(Posix(path), givenToPuid);
            Assert.Contains(Posix(path), opened);
        }

        // The config folder keeps its own owner handling and private modes even inside the duplicates folder.
        Assert.DoesNotContain(Posix(nestedConfig), givenToPuid);
        Assert.DoesNotContain(Posix(database), givenToPuid);
        Assert.DoesNotContain(Posix(database), opened);
    }

    [BashFact]
    public void FixLibraryPermissions_RejectsAnUnknownValueAndLeavesTheLibraryAlone()
    {
        var run = RunEntrypoint(("FIX_LIBRARY_PERMISSIONS", "sometimes"));

        AssertSucceeded(run);
        Assert.Contains("FIX_LIBRARY_PERMISSIONS must be true or false", run.Output);
        Assert.DoesNotContain(run.Calls, call => call.Command == "find" && call.Arguments[0] == Posix(_library));
    }

    [BashFact]
    public void AMissingLibraryFolder_IsCreatedForPuidWithoutTouchingAnythingElse()
    {
        var missing = Path.Join(_root, "new-library");

        var run = RunEntrypoint(("WATCHED_DIR", Posix(missing)));

        AssertSucceeded(run);
        Assert.Contains(run.Calls, call => call is { Command: "mkdir" } && call.Arguments.SequenceEqual(["-p", "--", Posix(missing)]));
        Assert.Contains(run.Calls, call => call is { Command: "chown" } && call.Arguments.SequenceEqual(["1234:5678", "--", Posix(missing)]));
    }

    [Fact]
    public void Script_NeverSetsAFixedModeOnTheLibrary()
    {
        var script = File.ReadAllText(Path.Join(RepositoryRoot, "entrypoint.dotnet.sh"));

        // A fixed mode clears group write access other containers rely on, adds the execute bit to every comic and
        // makes private libraries world-readable. Only the generated JWT key is given a fixed (private) mode.
        Assert.Equal(["600"], Regex.Matches(script, @"\bchmod\s+(?:-R\s+)?([0-7]{3,4})\b").Select(match => match.Groups[1].Value).Distinct());
        Assert.DoesNotMatch(@"chown\s+-R[^\n]*(/app|/watched_dir|/duplicates|library_dir)", script);
    }

    private sealed record Call(string Command, string[] Arguments);

    private sealed record Run(int ExitCode, string Output, IReadOnlyList<Call> Calls);

    private static void AssertSucceeded(Run run) =>
        Assert.True(run.ExitCode == 0, $"The entrypoint exited with {run.ExitCode}:{Environment.NewLine}{run.Output}");

    private static string[] Paths(Run run, string command, string firstArgument) =>
        run.Calls.Where(call => call.Command == command && call.Arguments[0] == firstArgument).SelectMany(call => call.Arguments.Skip(1)).ToArray();

    private static string TouchFile(string folder, params string[] relativeParts)
    {
        var path = Path.Join([folder, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        return path;
    }

    private Run RunEntrypoint(params (string Name, string Value)[] environment) => RunEntrypoint(unwritable: null, environment);

    private Run RunEntrypoint(bool realFind, params (string Name, string Value)[] environment) =>
        RunEntrypoint(unwritable: null, realFind, environment);

    private Run RunEntrypoint(string? unwritable, params (string Name, string Value)[] environment) =>
        RunEntrypoint(unwritable, realFind: false, environment);

    private Run RunEntrypoint(string? unwritable, bool realFind, params (string Name, string Value)[] environment)
    {
        WriteStubs(realFind);
        // The checkout may have CRLF line endings on Windows, which bash does not accept.
        var script = Path.Join(_root, "entrypoint.sh");
        File.WriteAllText(script, File.ReadAllText(Path.Join(RepositoryRoot, "entrypoint.dotnet.sh")).Replace("\r\n", "\n"));

        var start = new ProcessStartInfo(TestCapabilities.BashPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // The stubs go first on PATH from inside bash: Git for Windows' bash puts its own /usr/bin, which has the real
        // mkdir, chown and find, ahead of the PATH it is started with. Its PATH holds /c/... paths, so the stub folder
        // is converted with cygpath where there is one.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            """stubs=$1; shift; if command -v cygpath > /dev/null 2>&1; then stubs=$(cygpath -u "$stubs"); fi; PATH="$stubs:$PATH" exec bash "$@" """);
        start.ArgumentList.Add("bash");
        start.ArgumentList.Add(Posix(_stubs));
        start.ArgumentList.Add(Posix(script));
        start.Environment["STUB_LOG"] = Posix(_log);
        start.Environment["UNWRITABLE_DIR"] = unwritable is null ? "" : Posix(unwritable);
        start.Environment["PUID"] = "1234";
        start.Environment["PGID"] = "5678";
        start.Environment["WATCHED_DIR"] = Posix(_library);
        start.Environment["DUPLICATE_DIR"] = Posix(_duplicates);
        start.Environment["CONFIG_DIR"] = Posix(_config);
        start.Environment["JWT_SECRET_FILE"] = "/run/secrets/jwt";
        start.Environment.Remove("FIX_LIBRARY_PERMISSIONS");
        start.Environment.Remove("ADMIN_PASSWORD_FILE");
        start.Environment.Remove("EMPTY_ROOT_OWNED_DIR");
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // Generous: Git for Windows starts processes slowly, and each stub is one. Only a hang should fail here.
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "The entrypoint did not finish within five minutes.");
        process.WaitForExit();

        var calls = File.Exists(_log)
            ? File.ReadAllLines(_log).Select(ParseCall).ToList()
            : [];
        return new Run(process.ExitCode, output.Result + error.Result, calls);
    }

    /// <summary>
    /// Each stub records its name and arguments, one call per line with every argument in brackets. realpath prints
    /// the path it was given, and <c>realpath -e</c> fails for a missing one, as the real command does. The find stub
    /// walks nothing, and reports only EMPTY_ROOT_OWNED_DIR when asked whether a single folder is empty and root-owned.
    /// gosu runs nothing: the write check it is asked for fails for UNWRITABLE_DIR until that folder has been given to
    /// PUID, and the app it is finally asked to start is not started.
    /// </summary>
    private void WriteStubs(bool realFind)
    {
        const string record = """
            #!/bin/sh
            { printf '%s' "$(basename "$0")"; for argument in "$@"; do printf ' [%s]' "$argument"; done; printf '\n'; } >> "$STUB_LOG"
            """;
        foreach (var command in StubbedCommands.Where(command => !realFind || command != "find"))
        {
            var body = command switch
            {
                "realpath" => """
                    for argument in "$@"; do last=$argument; done
                    if [ "$1" = -e ] && [ ! -e "$last" ]; then exit 1; fi
                    printf '%s\n' "$last"

                    """,
                "getent" or "id" => "exit 2\n",
                "find" => """
                    if [ "$2" = -maxdepth ] && [ "$1" = "$EMPTY_ROOT_OWNED_DIR" ]; then printf '%s\n' "$1"; fi
                    exit 0

                    """,
                _ => "exit 0\n",
            };
            WriteStub(command, record + "\n" + body);
        }

        WriteStub("gosu", record + "\n" + """
            if [ "$2" = sh ]; then
                for argument in "$@"; do last=$argument; done
                if [ "$last" != "$UNWRITABLE_DIR" ]; then exit 0; fi
                grep -qF "chown [1234:5678] [--] [$last]" "$STUB_LOG"
                exit $?
            fi
            exit 0

            """);
    }

    private void WriteStub(string command, string content)
    {
        var path = Path.Join(_stubs, command);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static Call ParseCall(string line)
    {
        var name = line.Split(' ', 2)[0];
        var arguments = Regex.Matches(line, @" \[(.*?)\](?= \[|$)").Select(match => match.Groups[1].Value).ToArray();
        return new Call(name, arguments);
    }

    /// <summary>The path as bash sees it: Git for Windows' bash takes C:/... paths, so only the separators change.</summary>
    private static string Posix(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static string RepositoryRoot
    {
        get
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current != null; i++)
            {
                if (File.Exists(Path.Join(current, "entrypoint.dotnet.sh")))
                {
                    return current;
                }
                current = Path.GetDirectoryName(current);
            }
            throw new DirectoryNotFoundException("Could not locate the repository root relative to test binaries.");
        }
    }
}
