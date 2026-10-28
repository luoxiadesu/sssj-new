using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal static class HousePoseLimits
{
    // UI_NCafeEditor.OnRotateClick advances by 90 degrees. Do not require a
    // normalized angle: whole turns remain equivalent. NCafeItem.OnRotate swaps
    // footprint axes, and SetPos clamps to parent InnerSize minus that footprint.
    internal static bool Valid(HouseItemRow parent, uint x, uint y, uint rotation, HouseItemRow? item = null)
    {
        if (rotation % 90 != 0) return false;
        if (parent.InnerSize.Count != 2 || parent.InnerSize[0] == 0 || parent.InnerSize[1] == 0)
            return true;
        if (item?.Size.Count != 2 || item.Size[0] == 0 || item.Size[1] == 0)
            return x <= parent.InnerSize[0] && y <= parent.InnerSize[1];
        bool swap = rotation % 180 != 0;
        uint width = item.Size[swap ? 1 : 0], height = item.Size[swap ? 0 : 1];
        // Widen before adding: wrapped uint coordinates must not pass this check.
        return (ulong)x + width <= parent.InnerSize[0] && (ulong)y + height <= parent.InnerSize[1];
    }
}
