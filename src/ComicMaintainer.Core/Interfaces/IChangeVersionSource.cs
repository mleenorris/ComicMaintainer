namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Implemented by stores whose contents feed derived, cached views (the series
/// library snapshot, combinable-folder groups, ...). <see cref="ChangeVersion"/>
/// is a monotonically increasing counter bumped on every mutation that can
/// change what those views would compute, so consumers can cache a result and
/// cheaply detect when it has gone stale.
/// </summary>
/// <remarks>
/// This is deliberately a separate, opt-in interface rather than a member of
/// the store interfaces: consumers only cache when the source implements it, so
/// test doubles (which don't track changes) always see fresh results.
/// </remarks>
public interface IChangeVersionSource
{
    long ChangeVersion { get; }
}
