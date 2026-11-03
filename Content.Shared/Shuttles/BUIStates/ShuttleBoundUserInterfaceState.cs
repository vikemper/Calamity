using Content.Shared.Shuttles.UI.MapObjects;
using Content.Shared._Mono.FireControl;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.BUIStates;

[Serializable, NetSerializable]
public sealed class ShuttleBoundUserInterfaceState : BoundUserInterfaceState
{
    public NavInterfaceState NavState;
    public ShuttleMapInterfaceState MapState;
    public DockingInterfaceState DockState;
    public bool FireControlConsole;
    public bool FireControlConnected;
    public FireControllableEntry[] FireControllables = [];

    public ShuttleBoundUserInterfaceState(
        NavInterfaceState navState,
        ShuttleMapInterfaceState mapState,
        DockingInterfaceState dockState,
        bool fireControlConsole = false,
        bool fireControlConnected = false,
        FireControllableEntry[]? fireControllables = null)
    {
        NavState = navState;
        MapState = mapState;
        DockState = dockState;
        FireControlConsole = fireControlConsole;
        FireControlConnected = fireControlConnected;
        FireControllables = fireControllables ?? [];
    }
}
