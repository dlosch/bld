using bld.Infrastructure;
using bld.Models;
using System.Runtime.CompilerServices;

namespace bld.Services;

/// <summary>
/// Provides MSBuild initialization logic that must be called before any Microsoft.Build.* types are loaded
/// </summary>
internal static class MSBuildInitializer {
    private static bool _isInitialized = false;
    private static readonly object _lockObject = new object();

    /// <summary>
    /// Initializes MSBuild. This must be called before any Microsoft.Build.* types are loaded.
    /// </summary>
    /// <param name="console">Console output for logging</param>
    /// <param name="options">Cleaning options</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Initialize(IConsoleOutput console, CleaningOptions options) {
        lock (_lockObject) {
            if (!_isInitialized) {
                // This must be called before any other MSBuild Type is loaded.
                // JIT might change that behavior
                MSBuildService.RegisterMSBuildDefaults(console, options);
                _isInitialized = true;
            }
        }
    }

    /// <summary>
    /// Gets whether MSBuild has been initialized
    /// </summary>
    public static bool IsInitialized {
        get {
            lock (_lockObject) {
                return _isInitialized;
            }
        }
    }
}