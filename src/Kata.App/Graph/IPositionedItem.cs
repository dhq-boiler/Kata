using System.Windows;

namespace Kata.App.Graph;

// Marker for VMs that VirtualizingNodifyCanvas can query without realizing
// their container. Location is in graph space; Size is the pre-computed
// bounding box used for viewport intersection.
public interface IPositionedItem
{
    Point Location { get; }
    Size Size { get; }
}
