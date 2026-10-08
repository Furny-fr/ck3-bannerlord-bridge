// Leases on CK3xBL.Ux.InputGuard (low-level keyboard/mouse hooks) for the core's delivery and quiet-input checks. The hooks are
// installed on the first lease and removed with the last one, so they exist only around transitions.
using CK3xBL.Core.Delivery;
using UxInputGuard = CK3xBL.Ux.InputGuard;

namespace CK3xBL;

internal sealed class UxInputGuardSource : IInputGuardSource, IDisposable
{
    private readonly object _gate = new();
    private UxInputGuard? _guard;
    private int _leases;

    public IInputGuard Acquire()
    {
        lock (_gate)
        {
            _guard ??= new UxInputGuard();
            _leases++;
            return new Lease(this, _guard);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases > 0 || _guard is null) return;
            _guard.Dispose();
            _guard = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _guard?.Dispose();
            _guard = null;
            _leases = 0;
        }
    }

    private sealed class Lease(UxInputGuardSource owner, UxInputGuard guard) : IInputGuard
    {
        private UxInputGuard.InputBlock? _block;
        private int _disposed;

        public long PhysicalIdleMs => (long)guard.PhysicalIdle.TotalMilliseconds;

        /// <summary>True while physical input is really blocked: a block ends by itself at its cap (4 s), or without working hooks.</summary>
        public bool Blocking
        {
            get => _block is { IsActive: true };
            set
            {
                if (value)
                {
                    if (_block is { IsActive: true }) return;
                    _block?.Dispose();                     // capped: start a fresh block
                    _block = guard.BlockPhysicalInput();
                }
                else
                {
                    _block?.Dispose();
                    _block = null;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Blocking = false;
            owner.Release();
        }
    }
}
