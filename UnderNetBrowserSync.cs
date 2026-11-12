using System;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Placeholder for multiplayer synchronization.
    ///
    /// SteamNetworkLib is installed as a UserLib (SteamNetworkLib-IL2Cpp.dll)
    /// but is an IL2CppInterop-generated assembly that cannot be directly
    /// referenced like a regular managed DLL from a net6.0 mod project.
    /// Integrating SteamNetworkLib requires either:
    ///   1. Using Il2CppInterop.Runtime to access types via reflection/IL2CPP
    ///      type metadata, or
    ///   2. A managed SteamNetworkLib.dll build that can be directly referenced.
    ///
    /// Until then, this class provides the sync API surface but operates in
    /// single-player (local-only) mode. All sync methods are no-ops.
    ///
    /// To enable multiplayer sync, replace this implementation with one that
    /// uses SteamNetworkClient + HostSyncVar (see SteamNetworkLib docs:
    /// https://ifbars.github.io/SteamNetworkLib/docs/data-synchronization.html)
    /// </summary>
    public class UnderNetBrowserSync
    {
        public bool IsAvailable => false;
        public bool IsHost => false;

        public UnderNetBrowserSync()
        {
            // Single-player mode: no network client.
        }

        public void Initialize()
        {
            // No-op in single-player mode.
        }

        public void Shutdown()
        {
            // No-op.
        }

        public void SetRemoteUrl(string? url)
        {
            // No-op.
        }

        public void SetRemotePlaybackState(bool isPlaying)
        {
            // No-op.
        }

        public void SetRemoteTime(double timeSeconds)
        {
            // No-op.
        }

        public void SetRemoteDuration(double durationSeconds)
        {
            // No-op.
        }

        public IDisposable SuppressSyncEvents()
        {
            return _emptyDisposable;
        }

        private static readonly IDisposable _emptyDisposable = new EmptyDisposable();

        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
