using Microsoft.Build.Locator;

namespace Kata.Roslyn.Workspace;

public static class MsBuildHost
{
    private static readonly object _gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (_gate)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            _registered = true;
        }
    }
}
