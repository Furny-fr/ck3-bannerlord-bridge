// Owns the Bannerlord process and the file transport to the CK3Bridge module (PROTOCOL §3, §8.3).
//  * Launch (§3.1): bin/Win64_Shipping_Client/Bannerlord.exe /singleplayer _MODULES_*...*_MODULES_ with that folder as working dir;
//    once with the fallback module list if the first list never reaches a heartbeat (or the module reports an error while booting).
//  * Activation: the module stays passive unless the bridge started its Bannerlord: the launch sets CK3XBL_HOST (the bridge's pid)
//    in the game's environment and leaves a short-lived launch lease (bannerlord_launch.json) for a game a store launcher restarts.
//    A player who ticks CK3 Bridge in the Bannerlord launcher gets their own game, untouched.
//  * Adopt: a Bannerlord that is already running is taken over only when it proves it runs an active CK3Bridge: a fresh heartbeat
//    naming its pid (a passive module never writes one). Any other Bannerlord is the player's own game: the bridge never quits,
//    closes or kills it, does not start a second copy next to it, and reports the problem (Problem) until that game exits.
//  * Module check: no launch while <Bannerlord>/Modules/CK3Bridge is missing (Problem says so; tried again on the next start).
//    When neither module list boots, BootFailed tells the player once and nothing is relaunched until the next start.
//  * Safe-mode question after an unclean exit: answered No automatically while booting, only in bridge processes.
//  * Health: process exit => Lost(Exited); heartbeat older than HeartbeatTimeout (longer while booting/loading/held: the initial
//    spawn of a large battle runs after 'go' while the module is still held) => Lost(Unresponsive),
//    and StatusChanged is raised again when the heartbeat recovers. Bannerlord is killed only when its window has been hung for
//    longer than HungKill; otherwise the bridge waits or closes it gracefully.
//  * Files: request.json / command.txt are written atomically. The module takes command.txt whole (read, then delete), so a command
//    is never written over one it has not taken yet (SendCommandAsync). What a process had not taken dies with it.
using System.Diagnostics;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Json;
using CK3xBL.Core.Native;
using CK3xBL.Protocol;

namespace CK3xBL.Core.Bannerlord;

public sealed class BannerlordLinkOptions
{
    public string GameDir = "";
    public string SharedDir = "";
    public List<string> Modules = new() { "Native", "SandBoxCore", "CustomBattle", "CK3Bridge" };
    public List<string> FallbackModules = new() { "Native", "SandBoxCore", "CustomBattle", "SandBox", "StoryMode", "CK3Bridge" };
    public bool KeepRunning = true;
    public TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    public TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);
    public TimeSpan LoadingHeartbeatTimeout = TimeSpan.FromSeconds(60);
    public TimeSpan BootTimeout = TimeSpan.FromSeconds(180);
    public TimeSpan HungKill = TimeSpan.FromSeconds(30);
    public TimeSpan RelaunchDelay = TimeSpan.FromSeconds(3);
    public TimeSpan QuitTimeout = TimeSpan.FromSeconds(10);
    public TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);

    public string BinDir => Path.Combine(GameDir, "bin", "Win64_Shipping_Client");
    public string ExePath => Path.Combine(BinDir, "Bannerlord.exe");
}

public sealed class BannerlordLink : IBannerlordLink, IDisposable
{
    private const string GameProcessName = "Bannerlord";
    private const string WindowClass = "TWApplication";
    private const string BridgeModule = "CK3Bridge";
    private const string ForeignGameProblem =
        "Bannerlord is already running, but not for CK3 x Bannerlord (your own game?). Save and close it; the bridge then starts its own Bannerlord.";
    private const string ModuleMissingProblem =
        "The CK3Bridge module is not installed in Bannerlord (Modules\\CK3Bridge). Run the CK3 x Bannerlord installer again.";

    /// <summary>Set in the game's environment at launch (value: the bridge's pid); the module stays passive without it (or a lease).</summary>
    public const string HostVariable = "CK3XBL_HOST";
    /// <summary>Launch lease in the shared folder: {"pid": bridge pid, "t": unix ms}. The module accepts a fresh one when its
    /// environment lacks <see cref="HostVariable"/> (a store launcher restarted the game). Removed at the first heartbeat.</summary>
    public const string LaunchLeaseFile = "bannerlord_launch.json";

    private readonly BannerlordLinkOptions _o;
    private readonly IJournal _journal;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly object _startLock = new();   // one start decision (adopt / launch / wait for a foreign game) at a time

    private Process? _process;
    private int _generation;                      // bumped per launched or adopted process; a command belongs to one process
    private int _foreignPid;                      // a Bannerlord without CK3Bridge: nothing is launched until it exits
    private string? _problem;
    private bool _moduleMissing;                  // Modules/CK3Bridge was missing at the last launch attempt
    private volatile bool _bootFailureReported;   // BootFailed raised; reset by the next heartbeat
    private DateTime _launchedAt;
    private volatile bool _usingFallback;
    private volatile bool _bootedOnce;            // this process sent at least one heartbeat
    private volatile bool _lostReported;
    private volatile bool _stopping;
    private DateTime? _hungSince;
    private DateTime? _relaunchAt;
    private BannerlordStatus? _status;
    private string? _lastStatusKey;
    private DateTime _resultStamp;
    private readonly HashSet<string> _requestIds = new(StringComparer.Ordinal);   // battles requested by this bridge run (under _gate)
    private Task? _watch;

    public BannerlordLink(BannerlordLinkOptions options, IJournal journal)
    {
        _o = options;
        _journal = journal;
        Directory.CreateDirectory(_o.SharedDir);
    }

    public string RequestPath => Path.Combine(_o.SharedDir, ProtocolInfo.RequestFile);
    public string ResultPath => Path.Combine(_o.SharedDir, ProtocolInfo.ResultFile);
    public string StatusPath => Path.Combine(_o.SharedDir, ProtocolInfo.StatusFile);
    public string CommandPath => Path.Combine(_o.SharedDir, ProtocolInfo.CommandFile);
    public string LaunchLeasePath => Path.Combine(_o.SharedDir, LaunchLeaseFile);

    public BannerlordStatus? Status { get { lock (_gate) return _status; } }
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }
    public int ProcessId { get { lock (_gate) return _process is { HasExited: false } p ? p.Id : 0; } }
    public string? Problem { get { lock (_gate) return _problem ?? (_moduleMissing ? ModuleMissingProblem : null); } }
    /// <summary>The last launch attempt found no CK3Bridge module in Bannerlord's Modules folder.</summary>
    public bool ModuleMissing { get { lock (_gate) return _moduleMissing; } }
    public nint ForegroundBeforeLaunch { get; private set; }

    public nint MainWindow
    {
        get
        {
            int pid = ProcessId;
            if (pid == 0) return 0;
            var hwnd = (nint)(Status?.Hwnd ?? 0);
            if (hwnd != 0 && Win32.IsWindow(hwnd) && Win32.ProcessIdOf(hwnd) == pid) return hwnd;
            var windows = Win32.TopLevelWindows(pid, WindowClass);
            return windows.Count > 0 ? windows[0] : 0;
        }
    }

    public event Action<BannerlordStatus>? StatusChanged;
    public event Action<BannerlordLoss, string>? Lost;
    public event Action<BattleResult>? ResultUpdated;
    /// <summary>Raised when <see cref="Problem"/> changes (null = resolved).</summary>
    public event Action<string?>? ProblemChanged;
    /// <summary>Raised once when Bannerlord could not boot the module with either module list (text for the player).</summary>
    public event Action<string>? BootFailed;

    public Task StartAsync(CancellationToken ct)
    {
        _stopping = false;
        EnsureStarted();
        _watch ??= Task.Run(WatchLoop);
        return Task.CompletedTask;
    }

    /// <summary>Bridge start, before anything else: removes request.json, command.txt and half-written copies left by an earlier run,
    /// which the module would otherwise take as new work, and a launch lease that would activate a game this run did not start.</summary>
    public void ResetTransport()
    {
        foreach (var path in new[] { RequestPath, CommandPath, RequestPath + ".tmp", CommandPath + ".tmp", LaunchLeasePath })
            if (File.Exists(path) && TryDelete(path)) _journal.Info("bannerlord", $"removed {Path.GetFileName(path)} left by an earlier run");
    }

    // ------------------------------------------------------------------------------------------------------------- process

    /// <summary>Adopts a running Bannerlord that runs CK3Bridge, or launches one, unless the player's own Bannerlord is running.</summary>
    private void EnsureStarted()
    {
        lock (_startLock)
        {
            if (IsRunning) return;
            var running = Process.GetProcessesByName(GameProcessName);
            Process? adopted = null;
            try
            {
                var now = DateTime.UtcNow;
                var status = ProtocolJson.TryRead<BannerlordStatus>(StatusPath);
                adopted = running.FirstOrDefault(p => ProvesBridge(p.Id, status, now));
                if (adopted is not null) Adopt(adopted);
                else if (running.Length > 0) SetForeign(running[0].Id);
            }
            finally
            {
                foreach (var p in running) if (!ReferenceEquals(p, adopted)) p.Dispose();
            }
            if (running.Length > 0) return;
            SetForeign(0);
            Launch(_usingFallback);
        }
    }

    /// <summary>A Bannerlord process belongs to the bridge when a fresh heartbeat names it: only an active CK3Bridge writes one, and it
    /// is active only in a game the bridge started. CK3Bridge in the command line proves nothing (the player may tick the module).</summary>
    internal bool ProvesBridge(int pid, BannerlordStatus? status, DateTime now) =>
        status is not null && status.Pid == pid && now - HeartbeatTime(status) <= HeartbeatLimit(status);

    private void Adopt(Process p)
    {
        lock (_gate)
        {
            _process = p;
            _generation++;
            _launchedAt = DateTime.UtcNow;
            _bootedOnce = false;
            _lostReported = false;
            _hungSince = null;
            _status = null;
            _lastStatusKey = null;
        }
        SetForeign(0);
        _journal.Info("bannerlord", $"adopted running Bannerlord pid {p.Id} (runs CK3Bridge)");
    }

    private void SetForeign(int pid)
    {
        string? problem;
        lock (_gate)
        {
            if (_foreignPid == pid) return;
            _foreignPid = pid;
            _problem = pid == 0 ? null : ForeignGameProblem;
            problem = _problem ?? (_moduleMissing ? ModuleMissingProblem : null);
        }
        if (pid != 0) _journal.Warn("bannerlord", $"pid {pid} is not running an active {BridgeModule} (no heartbeat): left alone, and no second Bannerlord is started next to it");
        else _journal.Info("bannerlord", "no Bannerlord without the bridge module is in the way any more");
        ProblemChanged?.Invoke(problem);
    }

    private void Launch(bool fallback)
    {
        lock (_startLock)   // StartAsync (tailer or UI thread), the watcher's relaunch and RestartAsync may race
        {
            if (IsRunning) return;
            lock (_gate) _relaunchAt = null;
            if (!File.Exists(_o.ExePath))
            {
                _journal.Error("bannerlord", $"Bannerlord.exe not found at {_o.ExePath}; set the Bannerlord folder (tray: Set game folders)");
                return;
            }
            if (!SetModuleMissing(!GameInstall.HasBannerlordModule(_o.GameDir))) return;
            var modules = fallback ? _o.FallbackModules : _o.Modules;
            var psi = new ProcessStartInfo(_o.ExePath, $"/singleplayer _MODULES_*{string.Join("*", modules)}*_MODULES_")
            {
                WorkingDirectory = _o.BinDir,
                UseShellExecute = false,
            };
            psi.Environment[HostVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            WriteLaunchLease();
            ForegroundBeforeLaunch = Win32.GetForegroundWindow();
            try
            {
                var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                lock (_gate)
                {
                    _process = p;
                    _generation++;
                    _launchedAt = DateTime.UtcNow;
                    _usingFallback = fallback;
                    _bootedOnce = false;
                    _lostReported = false;
                    _hungSince = null;
                    _status = null;
                    _lastStatusKey = null;
                }
                _journal.Info("bannerlord", $"launched pid {p.Id}: {psi.Arguments}");
            }
            catch (Exception e)
            {
                _journal.Error("bannerlord", "launch failed", e);
                if (File.Exists(LaunchLeasePath)) TryDelete(LaunchLeasePath);
            }
        }
    }

    /// <summary>Records whether the module is installed; returns true when a launch may go ahead. Raises ProblemChanged on a change.</summary>
    private bool SetModuleMissing(bool missing)
    {
        string? problem;
        lock (_gate)
        {
            if (_moduleMissing == missing) return !missing;
            _moduleMissing = missing;
            problem = _problem ?? (missing ? ModuleMissingProblem : null);
        }
        if (missing) _journal.Error("bannerlord", $"CK3Bridge module not found in {GameInstall.BannerlordModuleDir(_o.GameDir)}; Bannerlord is not started");
        else _journal.Info("bannerlord", "CK3Bridge module found");
        ProblemChanged?.Invoke(problem);
        return !missing;
    }

    private void WriteLaunchLease()
    {
        try
        {
            AtomicFile.WriteAllText(LaunchLeasePath,
                $"{{\"pid\":{Environment.ProcessId},\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}", withBom: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Warn("bannerlord", $"could not write {LaunchLeaseFile}: {e.Message}");
        }
    }

    /// <summary>Bannerlord could not boot the module: tell the player once (until a heartbeat arrives again).</summary>
    private void ReportBootFailure(string why)
    {
        if (_bootFailureReported) return;
        _bootFailureReported = true;
        string version = GameInstall.BannerlordVersion(_o.GameDir) is { } v ? $"Bannerlord {v}" : "This Bannerlord version";
        string text = $"Bannerlord could not start the CK3 bridge module ({why}). {version} may not be supported " +
                      $"(this release was built for {BridgeVersion.BannerlordTarget}).";
        _journal.Error("bannerlord", text);
        BootFailed?.Invoke(text);
    }

    // ------------------------------------------------------------------------------------------------------------- watcher

    private async Task WatchLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception e) { _journal.Error("bannerlord", "watcher tick failed", e); }
            try { await Task.Delay(_o.PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        Process? p;
        DateTime? relaunchAt;
        int foreign;
        lock (_gate) { p = _process; relaunchAt = _relaunchAt; foreign = _foreignPid; }

        if (p is null)
        {
            if (_stopping) return;
            if (foreign != 0 ? ForeignGameGone(foreign, now) : relaunchAt is { } at && now >= at) EnsureStarted();
            return;
        }

        if (p.HasExited)
        {
            HandleExit(p);
            return;
        }

        if (!_bootedOnce) AnswerSafeModeQuestion(p.Id);
        ReadStatus(p.Id, now);
        WatchResult();
        CheckHealth(p, now);
    }

    /// <summary>The player's own Bannerlord exited (or turned out to run CK3Bridge after all): time to decide again.</summary>
    private bool ForeignGameGone(int pid, DateTime now)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        return ProvesBridge(pid, ProtocolJson.TryRead<BannerlordStatus>(StatusPath), now);
    }

    private void HandleExit(Process p)
    {
        int code;
        try { code = p.ExitCode; } catch (InvalidOperationException) { code = int.MinValue; }
        lock (_gate)
        {
            // A restart may already have replaced the process this tick looked at.
            if (!ReferenceEquals(_process, p)) { p.Dispose(); return; }
            _process = null;
            _status = null;
            _lastStatusKey = null;
        }
        p.Dispose();
        // What the dead process had not taken yet must not reach the next one (it would hold a battle nobody asks for).
        foreach (var path in new[] { RequestPath, CommandPath })
            if (File.Exists(path) && TryDelete(path)) _journal.Info("bannerlord", $"{Path.GetFileName(path)} not taken before the exit; removed");

        bool requested = _stopping;
        bool neverBooted = !_bootedOnce;
        if (requested) _journal.Info("bannerlord", "exited (requested)");
        else _journal.Warn("bannerlord", $"exited unexpectedly (code {code}){(neverBooted ? " before its first heartbeat" : "")}");
        Lost?.Invoke(BannerlordLoss.Exited, requested ? "Bannerlord was closed" : $"Bannerlord exited (code {code})");
        if (requested) return;
        if (neverBooted && _usingFallback)
        {
            // Both module lists failed: relaunching in a loop would only repeat it. The next battle or "Start Bannerlord" tries again.
            ReportBootFailure($"it exited with code {code} before the module answered");
            return;
        }
        if (!_o.KeepRunning) return;
        if (neverBooted)
        {
            _journal.Info("bannerlord", "relaunching with the fallback module list");
            _usingFallback = true;
        }
        lock (_gate) _relaunchAt = DateTime.UtcNow + _o.RelaunchDelay;
    }

    private void AnswerSafeModeQuestion(int pid)
    {
        // Our process, or one started from a bridge command line (e.g. handed over by the one we launched); never the player's game.
        var pids = new List<int> { pid };
        foreach (var other in Process.GetProcessesByName(GameProcessName))
        {
            if (other.Id != pid && Win32.CommandLineOf(other.Id)?.Contains(BridgeModule, StringComparison.OrdinalIgnoreCase) == true)
                pids.Add(other.Id);
            other.Dispose();
        }
        if (SafeModeDialog.TryAnswerNo(pids) is { } what) _journal.Info("bannerlord", what);
    }

    private void ReadStatus(int pid, DateTime now)
    {
        var s = ProtocolJson.TryRead<BannerlordStatus>(StatusPath);
        if (s is null || string.IsNullOrEmpty(s.Status)) return;
        if (s.Pid != pid && !AdoptReportedProcess(s, now)) return;   // stale file of an earlier process

        bool changed;
        lock (_gate)
        {
            _status = s;
            string key = s.Status + "|" + s.BattleId;
            changed = key != _lastStatusKey;
            _lastStatusKey = key;
        }
        if (!_bootedOnce)
        {
            _bootedOnce = true;
            _bootFailureReported = false;
            _journal.Info("bannerlord", $"first heartbeat after {(now - _launchedAt).TotalSeconds:0.0} s: {s.Status}");
            if (File.Exists(LaunchLeasePath)) TryDelete(LaunchLeasePath);   // the module has read it (or did not need it)
        }
        if (changed)
        {
            _journal.Info("bannerlord", $"status {s.Status}{(s.BattleId is null ? "" : " " + s.BattleId)}{(s.Error is null ? "" : " error: " + s.Error)}");
            if (s.Status == BannerlordStates.Error && s.BattleId is null && !_stopping)
            {
                if (!_usingFallback)
                {
                    // The module could not start with the lean module list (PROTOCOL §3.1): restart with the fallback list.
                    _journal.Warn("bannerlord", "module reported an error while idle; restarting with the fallback module list");
                    _usingFallback = true;
                    _ = RestartAsync();
                }
                else ReportBootFailure(s.Error ?? "the module reported an error");
            }
            StatusChanged?.Invoke(s);
        }
    }

    /// <summary>The heartbeat names a different, live Bannerlord process (e.g. the exe we started handed over to another process):
    /// follow that one.</summary>
    private bool AdoptReportedProcess(BannerlordStatus s, DateTime now)
    {
        if (s.Pid <= 0 || now - HeartbeatTime(s) > _o.HeartbeatTimeout) return false;
        Process reported;
        try { reported = Process.GetProcessById(s.Pid); }
        catch (ArgumentException) { return false; }
        if (!reported.ProcessName.StartsWith(GameProcessName, StringComparison.OrdinalIgnoreCase))
        {
            reported.Dispose();
            return false;
        }
        lock (_gate)
        {
            _journal.Info("bannerlord", $"following pid {s.Pid} named in the heartbeat (was {_process?.Id})");
            _process?.Dispose();
            _process = reported;
        }
        return true;
    }

    /// <summary>Reports a new or rewritten result.json. A result for a battle this bridge run never requested (a file an earlier run or
    /// a test left behind) is skipped quietly: it is no battle of this session (live test, start-up line
    /// "result.json: t1_knights finished").</summary>
    internal void WatchResult()
    {
        var stamp = File.Exists(ResultPath) ? File.GetLastWriteTimeUtc(ResultPath) : default;
        if (stamp == _resultStamp) return;
        if (ProtocolJson.TryRead<BattleResult>(ResultPath) is not { Id: not null } result) return;   // retried next tick if half-written
        _resultStamp = stamp;
        if (!IsRequestedThisRun(result.Id)) return;
        _journal.Info("bannerlord", $"result.json: {result.Id} {result.Status} (winner {result.Winner ?? "-"})");
        ResultUpdated?.Invoke(result);
    }

    private static DateTime HeartbeatTime(BannerlordStatus s) => DateTimeOffset.FromUnixTimeMilliseconds(s.T).UtcDateTime;

    internal TimeSpan HeartbeatLimit(BannerlordStatus s) =>
        s.Status is BannerlordStates.Booting or BannerlordStates.Loading or BannerlordStates.Held ? _o.LoadingHeartbeatTimeout : _o.HeartbeatTimeout;

    private void CheckHealth(Process p, DateTime now)
    {
        var s = Status;
        TimeSpan age;
        string why;
        if (!_bootedOnce)
        {
            age = now - _launchedAt;
            if (age <= _o.BootTimeout) { _hungSince = null; return; }
            why = $"no heartbeat {age.TotalSeconds:0} s after launch";
            ReportBootFailure($"no answer {age.TotalSeconds:0} s after launch");
        }
        else
        {
            age = now - HeartbeatTime(s!);
            if (age <= HeartbeatLimit(s!))
            {
                if (_lostReported)
                {
                    _lostReported = false;
                    _journal.Info("bannerlord", "heartbeat recovered");
                    StatusChanged?.Invoke(s!);   // the module may have moved on while silent: let the orchestrator look again
                }
                _hungSince = null;
                return;
            }
            why = $"heartbeat {age.TotalSeconds:0.0} s old (status {s!.Status})";
        }

        if (!_lostReported)
        {
            _lostReported = true;
            _journal.Warn("bannerlord", why);
            Lost?.Invoke(BannerlordLoss.Unresponsive, why);
        }

        var hwnd = MainWindow;
        if (hwnd != 0 && Win32.IsHungAppWindow(hwnd))
        {
            _hungSince ??= now;
            if (now - _hungSince > _o.HungKill)
            {
                _journal.Error("bannerlord", $"window hung for {(now - _hungSince.Value).TotalSeconds:0} s; killing pid {p.Id}");
                try { p.Kill(); } catch (Exception e) { _journal.Error("bannerlord", "kill failed", e); }
                _hungSince = null;
            }
        }
        else _hungSince = null;
    }

    private async Task RestartAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping = false;
        EnsureStarted();
    }

    // ------------------------------------------------------------------------------------------------------------- files

    /// <summary>Whether this bridge run wrote a request with this id.</summary>
    internal bool IsRequestedThisRun(string id)
    {
        lock (_gate) return _requestIds.Contains(id);
    }

    public Task WriteRequestAsync(BattleRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Id))
            lock (_gate) _requestIds.Add(request.Id);
        DeleteResult();
        ProtocolJson.WriteAtomic(RequestPath, request);
        _journal.Info("bannerlord", $"request {request.Id} written (preload {request.Presentation?.Preload})");
        return Task.CompletedTask;
    }

    public Task WithdrawRequestAsync(string battleId, CancellationToken ct)
    {
        if (ProtocolJson.TryRead<BattleRequest>(RequestPath)?.Id == battleId && TryDelete(RequestPath))
            _journal.Info("bannerlord", $"request {battleId} withdrawn before Bannerlord took it");
        return Task.CompletedTask;
    }

    /// <summary>Writes one command and waits until the module has taken it, so commands and request writes reach the module in the
    /// order they were issued. A command the module has not taken is never overwritten (it takes command.txt whole: read, then
    /// delete; replacing or appending to the file could lose a command or run one twice). There is deliberately no time limit while
    /// the process lives: the module takes the file at its next poll or right after a synchronous load step, and a main thread that
    /// stays blocked shows as a hung window, which is killed after HungKill (its exit ends the wait). Only 'quit' replaces a pending
    /// command after QuitTimeout. Returns false when the command was dropped (Bannerlord not running, exited, or being closed).</summary>
    public async Task<bool> SendCommandAsync(string command, CancellationToken ct)
    {
        int generation;
        lock (_gate)
        {
            if (_process is not { HasExited: false })
            {
                _journal.Warn("bannerlord", $"command '{command}' dropped: Bannerlord is not running");
                return false;
            }
            generation = _generation;
        }
        await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool quit = command == BannerlordCommands.Quit;
            var deadline = quit ? DateTime.UtcNow + _o.QuitTimeout : DateTime.MaxValue;
            // Normally absent: every earlier command was taken before its send returned.
            var pending = await WaitTakenAsync(generation, quit, deadline, ct).ConfigureAwait(false);
            if (pending is CommandWait.Gone or CommandWait.Stopping) return Dropped(command, pending);
            if (pending == CommandWait.TimedOut) _journal.Warn("bannerlord", "a pending command was not taken; 'quit' replaces it");

            AtomicFile.WriteAllText(CommandPath, command, withBom: false);
            _journal.Info("bannerlord", $"command: {command}");
            var taken = await WaitTakenAsync(generation, quit, deadline, ct).ConfigureAwait(false);
            return taken == CommandWait.Taken || Dropped(command, taken);
        }
        finally { _commandGate.Release(); }
    }

    private enum CommandWait { Taken, Gone, Stopping, TimedOut }

    private async Task<CommandWait> WaitTakenAsync(int generation, bool quit, DateTime deadline, CancellationToken ct)
    {
        while (File.Exists(CommandPath))
        {
            lock (_gate)
                if (_generation != generation || _process is not { HasExited: false }) return CommandWait.Gone;
            if (_stopping && !quit) return CommandWait.Stopping;
            if (DateTime.UtcNow >= deadline) return CommandWait.TimedOut;
            await Task.Delay(15, ct).ConfigureAwait(false);
        }
        return CommandWait.Taken;
    }

    private bool Dropped(string command, CommandWait why)
    {
        _journal.Warn("bannerlord", $"command '{command}' " + why switch
        {
            CommandWait.Gone => "dropped: Bannerlord exited",
            CommandWait.Stopping => "dropped: Bannerlord is being closed",
            _ => "was not taken in time",
        });
        return false;
    }

    public BattleResult? ReadResult(string battleId)
    {
        var r = ProtocolJson.TryRead<BattleResult>(ResultPath);
        return r is not null && r.Id == battleId ? r : null;
    }

    public void DeleteResult() => TryDelete(ResultPath);

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Warn("bannerlord", $"could not delete {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    /// <summary>Tests only: treats <paramref name="process"/> as the Bannerlord in use (no watcher; nothing is launched or stopped).</summary>
    internal void AttachForTests(Process process)
    {
        lock (_gate)
        {
            _process = process;
            _generation++;
        }
    }

    // ------------------------------------------------------------------------------------------------------------- shutdown

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        Process? p;
        lock (_gate) { p = _process; _relaunchAt = null; }
        if (p is null || p.HasExited) return;

        _journal.Info("bannerlord", "quitting");
        await SendCommandAsync(BannerlordCommands.Quit, ct).ConfigureAwait(false);
        if (await WaitExitAsync(p, _o.QuitTimeout, ct).ConfigureAwait(false)) return;

        _journal.Info("bannerlord", "no exit after 'quit'; closing the main window");
        try { p.CloseMainWindow(); } catch (InvalidOperationException) { return; }
        if (await WaitExitAsync(p, _o.CloseTimeout, ct).ConfigureAwait(false)) return;

        var hwnd = MainWindow;
        if (hwnd != 0 && Win32.IsHungAppWindow(hwnd))
        {
            _journal.Error("bannerlord", "window is hung; killing");
            try { p.Kill(); } catch (Exception e) { _journal.Error("bannerlord", "kill failed", e); }
        }
        else _journal.Warn("bannerlord", "Bannerlord is still closing; leaving it to finish on its own");
    }

    private static async Task<bool> WaitExitAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return p.HasExited; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _watch?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        lock (_gate) _process?.Dispose();
        _cts.Dispose();
    }
}
