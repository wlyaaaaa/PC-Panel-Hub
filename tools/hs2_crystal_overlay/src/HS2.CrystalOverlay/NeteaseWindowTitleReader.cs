using System.Text;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class NeteaseWindowTitleReader
{
    internal IReadOnlyList<NeteaseTrackMetadata> ReadTracks(
        IReadOnlySet<int> processIds,
        NeteasePlayingList catalog)
    {
        var matches = new Dictionary<string, NeteaseTrackMetadata>(
            StringComparer.Ordinal);
        string? matchedIdentity = null;
        var conflictingIdentity = false;
        NativeMethods.WindowEnumProc callback = (window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(
                window,
                out var processId);
            if (!processIds.Contains((int)processId))
            {
                return true;
            }

            var length = NativeMethods.GetWindowTextLength(window);
            if (length is <= 0 or > 4096)
            {
                return true;
            }

            var title = new StringBuilder(length + 1);
            if (NativeMethods.GetWindowText(
                    window,
                    title,
                    title.Capacity) <= 0)
            {
                return true;
            }

            var candidates = catalog.FindAllByWindowTitle(
                title.ToString());
            if (candidates.Count == 0)
            {
                return true;
            }

            var identity = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{candidates[0].Title}\0{candidates[0].Artist}");
            if (matchedIdentity is not null &&
                !string.Equals(
                    matchedIdentity,
                    identity,
                    StringComparison.OrdinalIgnoreCase))
            {
                conflictingIdentity = true;
                return false;
            }

            matchedIdentity = identity;
            foreach (var track in candidates)
            {
                matches[track.Id] = track;
            }

            return true;
        };

        _ = NativeMethods.EnumWindows(callback, 0);
        return conflictingIdentity
            ? []
            : matches.Values.ToArray();
    }
}
