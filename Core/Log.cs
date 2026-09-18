using System;
using System.Collections.Generic;
using AliveSensor.Config;
using StardewModdingAPI;
using StardewValley;

namespace AliveSensor.Core;

/// <summary>
/// Logging with categories. Info/Warn/Error always go to the SMAPI console.
/// Debug lines only appear when Debug mode is on; Verbose lines (high-frequency sampling) need Debug + Verbose.
/// Every debug line carries the in-game clock and a [bg] marker when written off the main thread.
/// </summary>
internal sealed class Log
{
    private readonly IMonitor _monitor;
    private readonly Func<ModConfig> _config;
    private readonly int _mainThreadId;
    private readonly HashSet<string> _onceKeys = new();
    private readonly object _onceLock = new();

    public Log(IMonitor monitor, Func<ModConfig> config)
    {
        _monitor = monitor;
        _config = config;
        _mainThreadId = Environment.CurrentManagedThreadId;
    }

    public bool DebugEnabled => _config().Debug.Enabled;

    public bool VerboseEnabled => _config().Debug.Enabled && _config().Debug.Verbose;

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public void Info(string message) => _monitor.Log(message, LogLevel.Info);

    public void Warn(string message) => _monitor.Log(message, LogLevel.Warn);

    public void Error(string message, Exception? ex = null)
    {
        if (ex is null)
            _monitor.Log(message, LogLevel.Error);
        else
            _monitor.Log($"{message}: {(DebugEnabled ? ex.ToString() : ex.Message)}", LogLevel.Error);
    }

    /// <summary>Warn only the first time a given key is seen this session.</summary>
    public void WarnOnce(string key, string message)
    {
        lock (_onceLock)
        {
            if (!_onceKeys.Add(key))
                return;
        }
        Warn(message);
    }

    public void Debug(string category, string message)
    {
        if (DebugEnabled)
            _monitor.Log(Format(category, message), LogLevel.Debug);
    }

    public void Verbose(string category, string message)
    {
        if (VerboseEnabled)
            _monitor.Log(Format(category, message), LogLevel.Trace);
    }

    private string Format(string category, string message)
    {
        string thread = IsMainThread ? "" : " [bg]";
        return $"[{category}]{thread} {Clock()}{message}";
    }

    /// <summary>In-game clock prefix, e.g. "Y1 spring 3 14:30 | ". Empty before a save is loaded.</summary>
    private static string Clock()
    {
        if (!Context.IsWorldReady)
            return "";
        int time = Game1.timeOfDay;
        return $"Y{Game1.year} {Game1.currentSeason} {Game1.dayOfMonth} {time / 100:00}:{time % 100:00} | ";
    }
}
